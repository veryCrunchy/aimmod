using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AimMod.Desktop.Trainers;

namespace AimMod.Desktop.Hub;

public sealed record HubTrainingSetup(string Mode, string Engine, int Bpm, int Seconds, int Pattern,
    int NoteSpeed, int AimStyle, int AimSpacing, int CircleSize, int ApproachRate, int Sliders, int SliderBeats,
    int PathStyle, bool Randomized, string MusicSource, string ConfigurationHash);
public sealed record HubTrainingSession(string Id, DateTimeOffset CompletedAt, string Visibility,
    HubTrainingSetup Setup, int Notes, int Hits, int Within25, int Extras, int RepeatedKeys,
    double? Accuracy, double? MeanMs, double? SpreadMs, double? DriftMs, double? ResponseMs,
    double PlayedSeconds, double? PeakNps, double? JumpDistance, double? AimVelocity, int? LongestChain)
{
    private static readonly JsonSerializerOptions comparisonJson = createComparisonJson();

    private static JsonSerializerOptions createComparisonJson()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type != typeof(TrainerSettings)) return;
            // Keep the exact pre-guided JSON property order and shape for ordinary runs.
            // A compact exercise adds only its non-default scale to the cohort identity.
            var movement = info.Properties.Single(p => p.Name == nameof(TrainerSettings.MovementScale));
            movement.ShouldSerialize = (_, value) => value is double scale && scale != 1;
        });
        return new JsonSerializerOptions { TypeInfoResolver = resolver };
    }

    public static HubTrainingSession FromResult(TrainerResult result, bool publicly)
    {
        if (result.Assisted) throw new ArgumentException("Assisted results require a separate training contract.", nameof(result));
        TrainerSettings s = result.Settings;
        // Hash the comparison settings locally: keys, offset, source paths and device configuration never leave the app.
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(s.ComparisonKey(), comparisonJson)))).ToLowerInvariant();
        var setup = new HubTrainingSetup(s.Kind.ToString().ToLowerInvariant(), result.Engine, s.Bpm, s.Seconds,
            (int)s.Pattern, (int)s.NoteSpeed, (int)s.AimStyle, s.AimSpacing, s.CircleSize, s.ApproachRate,
            (int)s.Sliders, s.SliderBeats, (int)s.PathStyle, s.RandomizePatterns,
            s.Music == "song" ? "installed" : s.Music == "cues" ? "cues" : "aimmod", hash);
        return new(result.Id.ToString(), result.CompletedAt, publicly ? "public" : "private", setup,
            result.Notes, result.Hits, result.Within25, result.Extras, result.RepeatedKeys,
            result.Accuracy, result.MeanMs, result.SpreadMs, result.DriftMs, result.ResponseMs,
            result.PlayedSeconds ?? s.Seconds, result.Demand?.PeakNps, result.Demand?.JumpDistance,
            result.Demand?.AimVelocity, result.Demand?.LongestChain);
    }
}

/// <summary>Future sessions only, persistent retries, and a session-start account/consent snapshot.</summary>
public sealed class HubTrainingSyncService(string path, HttpClient client, Uri baseUri,
    IHubCredentialStore credentials, IHubSharingPreferenceStore preferences, Func<long?> osuAccount)
{
    private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim flushGate = new(1, 1);
    private string status = "Training sync is ready.";
    public string Status => preferences.Load().TrainingSyncEnabled ? status : "Training sync is off.";
    public sealed record Pending(string Scope, Guid Generation, HubTrainingSession Session, int Attempts = 0, DateTimeOffset NextAttempt = default);
    private static string digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private string? scope(HubCredential? credential, long? account) => credential is null || account is null || account <= 0 ? null
        : digest($"{baseUri.AbsoluteUri}|{credential.AccountLabel}|{credential.LinkedAt:O}|{account}");

    public Action<TrainerResult>? BeginSession()
    {
        HubSharingPreferences settings = preferences.Load();
        string? owner = scope(credentials.Load(), osuAccount());
        if (!settings.TrainingSyncEnabled || owner is null || settings.TrainingSyncGeneration == Guid.Empty) return null;
        return result =>
        {
            if (!result.Assisted)
                _ = enqueueAsync(owner, settings.TrainingSyncGeneration, HubTrainingSession.FromResult(result, settings.TrainingPublicSharing));
        };
    }

    private async Task enqueueAsync(string owner, Guid generation, HubTrainingSession session)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var settings = preferences.Load();
            if (!settings.TrainingSyncEnabled || generation != settings.TrainingSyncGeneration || owner != scope(credentials.Load(), osuAccount())) return;
            var items = load();
            items.RemoveAll(item => item.Generation != generation);
            if (items.Any(item => item.Scope == owner && item.Session.Id == session.Id)) return;
            if (items.Count >= 1000) { status = "Training uploads are full. Your session is saved in local history."; return; }
            items.Add(new(owner, generation, session)); save(items);
            status = $"{items.Count} training sessions waiting to sync.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { status = "Training could not be queued. Your result remains in local history."; }
        finally { gate.Release(); }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try { do { await FlushAsync(cancellationToken).ConfigureAwait(false); } while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (!await flushGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            Pending? item; HubCredential? credential;
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var settings = preferences.Load(); credential = credentials.Load(); string? owner = scope(credential, osuAccount());
                var items = load();
                // Revoking sync discards unsent consent generations; re-enabling cannot unexpectedly publish them.
                int removed = items.RemoveAll(pending => !settings.TrainingSyncEnabled || pending.Generation != settings.TrainingSyncGeneration);
                if (removed > 0) save(items);
                if (!settings.TrainingSyncEnabled) return;
                if (owner is null) { status = "Link AimMod Hub and select your osu! account to sync training."; return; }
                item = items.FirstOrDefault(pending => pending.Scope == owner && pending.NextAttempt <= DateTimeOffset.UtcNow);
                if (item is null)
                {
                    status = items.Any(pending => pending.Scope == owner) ? "Training is saved locally. Waiting to retry sync."
                        : items.Count > 0 ? "Sessions from an earlier account link remain local." : "Training sync is up to date.";
                    return;
                }
            }
            finally { gate.Release(); }
            // Never hold the file gate across a network request: the next completed result must persist immediately.
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "api/osu/v1/training/sessions"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential!.UploadToken);
            request.Content = JsonContent.Create(new { schemaVersion = 1, sessions = new[] { item.Session } }, options: json);
            bool accepted = false;
            try
            {
                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var ack = await response.Content.ReadFromJsonAsync<Acknowledgement>(json, cancellationToken).ConfigureAwait(false);
                    accepted = ack?.Accepted == 1;
                }
                status = accepted ? "Training session synced." : response.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? "Training sync is not available on the server yet. Sessions are saved locally."
                    : "Training sync could not finish. It will retry automatically.";
            }
            catch (Exception error) when (error is HttpRequestException or JsonException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            { status = "Training is saved locally. Sync will retry when the connection is available."; }
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var items = load();
                int index = items.FindIndex(pending => pending.Scope == item.Scope && pending.Generation == item.Generation && pending.Session.Id == item.Session.Id);
                if (index < 0) return;
                if (accepted) items.RemoveAt(index);
                else items[index] = item with { Attempts = Math.Min(item.Attempts + 1, 12), NextAttempt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(3600, 15 * Math.Pow(2, item.Attempts))) };
                save(items);
            }
            finally { gate.Release(); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { status = "Training sync could not read its queue. Local practice history is still available."; }
        finally { flushGate.Release(); }
    }

    private List<Pending> load() => File.Exists(path) ? JsonSerializer.Deserialize<List<Pending>>(File.ReadAllText(path), json) ?? [] : [];
    private void save(List<Pending> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(items, json)); File.Move(temporary, path, true);
    }
    private sealed record Acknowledgement(int Accepted);
}

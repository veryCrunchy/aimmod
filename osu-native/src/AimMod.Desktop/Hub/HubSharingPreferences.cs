using System.Text.Json;

namespace AimMod.Desktop.Hub;

public sealed record HubSharingPreferences(
    OsuHubVisibility Visibility = OsuHubVisibility.Public,
    bool UploadReplayFile = false,
    bool UploadAnalysis = false,
    bool AutomaticSharingEnabled = false,
    double MinimumPp = 0,
    double MinimumAccuracy = 95,
    Guid AutomaticSharingGeneration = default)
{
    public static HubSharingPreferences Default { get; } = new();

    public HubSharingPreferences Normalised() => this with
    {
        Visibility = Enum.IsDefined(Visibility) ? Visibility : OsuHubVisibility.Private,
        MinimumPp = double.IsFinite(MinimumPp) ? Math.Clamp(MinimumPp, 0, 5000) : 0,
        MinimumAccuracy = double.IsFinite(MinimumAccuracy) ? Math.Clamp(MinimumAccuracy, 0, 100) : 100,
        AutomaticSharingEnabled = AutomaticSharingEnabled && double.IsFinite(MinimumPp) && double.IsFinite(MinimumAccuracy),
    };
}

public interface IHubSharingPreferenceStore
{
    HubSharingPreferences Load();
    Task SaveAsync(HubSharingPreferences preferences, CancellationToken cancellationToken = default);
    Task UpdateAsync(Func<HubSharingPreferences, HubSharingPreferences> update, CancellationToken cancellationToken = default) =>
        SaveAsync(update(Load()), cancellationToken);
}

public sealed class FileHubSharingPreferenceStore : IHubSharingPreferenceStore
{
    private const int current_version = 1;
    private static readonly JsonSerializerOptions json_options = new(JsonSerializerDefaults.Web);
    private readonly string path;
    private readonly SemaphoreSlim saveGate = new(1, 1);

    public FileHubSharingPreferenceStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("The Hub sharing preference path must be absolute.", nameof(path));
        this.path = path;
    }

    public HubSharingPreferences Load()
    {
        try
        {
            if (!File.Exists(path))
                return HubSharingPreferences.Default;
            using FileStream stream = File.OpenRead(path);
            PreferenceDocument? document = JsonSerializer.Deserialize<PreferenceDocument>(stream, json_options);
            return document?.Version == current_version && document.Preferences is not null
                ? document.Preferences.Normalised()
                : HubSharingPreferences.Default;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return HubSharingPreferences.Default;
        }
    }

    public Task SaveAsync(HubSharingPreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        return UpdateAsync(_ => preferences, cancellationToken);
    }

    public async Task UpdateAsync(Func<HubSharingPreferences, HubSharingPreferences> update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HubSharingPreferences previous = Load();
            HubSharingPreferences preferences = update(previous).Normalised();
            preferences = preferences with
            {
                AutomaticSharingGeneration = preferences.AutomaticSharingEnabled
                    ? previous.AutomaticSharingEnabled && previous.AutomaticSharingGeneration != Guid.Empty
                        ? previous.AutomaticSharingGeneration : Guid.NewGuid()
                    : Guid.Empty,
            };
            await saveCoreAsync(preferences, cancellationToken).ConfigureAwait(false);
        }
        finally { saveGate.Release(); }
    }

    private async Task saveCoreAsync(HubSharingPreferences preferences, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, new PreferenceDocument(current_version, preferences.Normalised()), json_options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed record PreferenceDocument(int Version, HubSharingPreferences Preferences);
}

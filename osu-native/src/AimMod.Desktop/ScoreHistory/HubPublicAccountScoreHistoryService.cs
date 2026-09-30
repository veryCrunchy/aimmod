using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AimMod.Osu.Runtime;

namespace AimMod.Desktop.ScoreHistory;

public sealed class HubPublicAccountScoreHistoryService : IAccountScoreHistoryService
{
    private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient client;
    private readonly Uri hubBaseUri;
    private int? resolvedUserId;
    private readonly string? username;
    private readonly TimeSpan timeout;
    private readonly SemaphoreSlim gate = new(1, 1);
    private OnlineAccountScoreHistoryResult? cached;
    private DateTimeOffset expiresAt;

    public HubPublicAccountScoreHistoryService(HttpClient client, Uri hubBaseUri, string? username, TimeSpan? timeout = null, int? userId = null)
    {
        this.client = client;
        this.username = username?.Trim();
        this.timeout = timeout ?? TimeSpan.FromSeconds(10);
        this.hubBaseUri = hubBaseUri;
        resolvedUserId = userId is > 0 ? userId : null;
    }

    public async Task<OnlineAccountScoreHistoryResult> FetchAccountAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (resolvedUserId is null && (string.IsNullOrWhiteSpace(username) || Encoding.UTF8.GetByteCount(username) > 128
            || username.Any(char.IsControl) || username.Contains('/') || username.Contains('\\')))
            return unavailable(OsuBestScoresFetchStatus.SessionUnavailable);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        bool entered = false;
        try
        {
            await gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            if (cached is not null && DateTimeOffset.UtcNow < expiresAt)
                return cached with
                {
                    BestCoverage = cached.BestCoverage with { IsFromCache = true },
                    RecentCoverage = cached.RecentCoverage with { IsFromCache = true },
                };

            var result = await fetch(deadline.Token).ConfigureAwait(false);
            cached = result;
            expiresAt = DateTimeOffset.UtcNow.Add(result.Profile is null ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(5));
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return failed(OsuBestScoresFetchStatus.NetworkError);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException)
        {
            return failed(error is JsonException or InvalidDataException ? OsuBestScoresFetchStatus.InvalidResponse : OsuBestScoresFetchStatus.NetworkError);
        }
        finally
        {
            if (entered)
                gate.Release();
        }

        OnlineAccountScoreHistoryResult failed(OsuBestScoresFetchStatus status)
        {
            var result = unavailable(status);
            if (entered)
            {
                cached = result;
                expiresAt = DateTimeOffset.UtcNow.AddSeconds(30);
            }
            return result;
        }
    }

    public async Task<OnlineBeatmapScoreHistoryResult> FetchBeatmapAsync(int beatmapId, CancellationToken cancellationToken = default)
    {
        if (beatmapId <= 0)
            return new(beatmapId, [], coverage(OsuBestScoresFetchStatus.InvalidResponse, "public account window", null));
        var account = await FetchAccountAsync(cancellationToken).ConfigureAwait(false);
        // The Hub exposes a merged window, not an exhaustive per-beatmap endpoint.
        var combined = account.BestCoverage.IsSuccess ? account.BestCoverage : account.RecentCoverage;
        return new(beatmapId, account.Scores.Where(score => score.OnlineBeatmapId == beatmapId).ToArray(),
            combined with { Scope = "public account window", IsExhaustive = false });
    }

    private async Task<OnlineAccountScoreHistoryResult> fetch(CancellationToken cancellationToken)
    {
        if (resolvedUserId is null)
        {
            using var lookup = new HttpRequestMessage(HttpMethod.Post,
                new Uri(hubBaseUri.AbsoluteUri.TrimEnd('/') + "/aimmod.osu.v1.OsuService/GetOfficialUserProfile"))
            {
                Content = JsonContent.Create(new { identifier = username, lookupKey = "OFFICIAL_USER_LOOKUP_KEY_USERNAME", ruleset = "RULESET_OSU" }),
            };
            lookup.Headers.Add("Connect-Protocol-Version", "1");
            using var lookupResponse = await client.SendAsync(lookup, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!lookupResponse.IsSuccessStatusCode)
                return unavailable(lookupResponse.StatusCode == HttpStatusCode.NotFound
                    ? OsuBestScoresFetchStatus.SessionUnavailable : OsuBestScoresFetchStatus.ServerError);
            var identity = await readPayload<LookupPayload>(lookupResponse, cancellationToken).ConfigureAwait(false);
            if (identity?.Profile is not { UserId: > 0 } resolved || string.IsNullOrWhiteSpace(resolved.Username))
                return unavailable(OsuBestScoresFetchStatus.InvalidResponse);
            resolvedUserId = resolved.UserId;
        }
        var endpoint = new Uri(hubBaseUri.AbsoluteUri.TrimEnd('/') + "/api/osu/v1/profile-scores/"
            + resolvedUserId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "?mode=osu&limit=100");
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return unavailable(response.StatusCode == HttpStatusCode.NotFound
                ? OsuBestScoresFetchStatus.SessionUnavailable : OsuBestScoresFetchStatus.ServerError);

        var payload = await readPayload<Payload>(response, cancellationToken).ConfigureAwait(false);
        var profile = payload?.Profile;
        // IDs survive renames; the numeric route also supports players without a Hub account.
        if (profile is null || profile.OsuUserId != resolvedUserId || string.IsNullOrWhiteSpace(profile.OsuUsername)
            || payload!.Items is null || payload.Coverage?.Best is null || payload.Coverage.Recent is null)
            return unavailable(OsuBestScoresFetchStatus.InvalidResponse);

        DateTimeOffset fetchedAt = DateTimeOffset.UtcNow;
        var publicProfile = new OsuProfile(profile.OsuUserId, profile.OsuUsername!, profile.CountryCode,
            Uri.TryCreate(profile.AvatarUrl, UriKind.Absolute, out var avatar) && avatar.Scheme == "https" ? avatar : null,
            new OsuProfileStatistics(profile.GlobalRank, null, profile.PerformancePoints, null,
                profile.PlayCount, profile.PlayTimeSeconds, 0, 0, 0, 0));
        var scores = payload.Items.Where(item => item is not null && item.OsuUserId == profile.OsuUserId
                && item.Ruleset == "osu" && item.OnlineScoreId > 0 && item.BeatmapId > 0 && item.BeatmapSetId > 0
                && item.Source is "official" or "merged" && item.Accuracy is >= 0 and <= 1
                && item.PlayedAt > DateTimeOffset.UnixEpoch)
            .DistinctBy(item => item.OnlineScoreId).Take(100)
            .Select(item => new ScoreHistoryEntry($"osu:{item.OnlineScoreId}", item.OnlineScoreId, item.BeatmapId,
                item.BeatmapSetId, null, null, item.Title ?? "", item.Artist ?? "", item.Difficulty ?? "", item.PlayedAt,
                item.StarRating, item.Accuracy, item.PerformancePoints, item.TotalScore, item.MaxCombo, item.CountMiss,
                item.Mods?.Where(mod => !string.IsNullOrWhiteSpace(mod)).ToArray() ?? [], ScoreHistoryProvenance.OnlinePublic,
                false, item.Passed, item.Bpm, (int)Math.Clamp(item.LengthMs / 1000, 0, int.MaxValue),
                LegacyScore: item.PpCalculation?.Lazer == false))
            .OrderByDescending(item => item.PlayedAt).ToArray();
        return new(publicProfile, scores, coverage(mapStatus(payload.Coverage.Best.Status), "public best score window", fetchedAt),
            coverage(mapStatus(payload.Coverage.Recent.Status), "public recent score window", fetchedAt));
    }

    private static async Task<T?> readPayload<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        const int maximumBytes = 4 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException("Public score response is too large.");
        using var buffer = new MemoryStream();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maximumBytes)
                throw new InvalidDataException("Public score response is too large.");
            buffer.Write(chunk, 0, read);
        }
        return JsonSerializer.Deserialize<T>(buffer.ToArray(), json);
    }

    private sealed record LookupPayload(LookupProfile? Profile);
    private sealed record LookupProfile(
        [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] int UserId,
        string? Username);

    private static OsuBestScoresFetchStatus mapStatus(string? status) => status switch
    {
        "available" or "page_limit" => OsuBestScoresFetchStatus.Success,
        "not_configured" => OsuBestScoresFetchStatus.SessionUnavailable,
        "authentication_failed" => OsuBestScoresFetchStatus.Unauthorized,
        "invalid_response" => OsuBestScoresFetchStatus.InvalidResponse,
        _ => OsuBestScoresFetchStatus.ServerError,
    };

    private static OnlineScoreCoverage coverage(OsuBestScoresFetchStatus status, string scope, DateTimeOffset? fetchedAt) =>
        new(status, false, fetchedAt, scope, 100, false);

    private static OnlineAccountScoreHistoryResult unavailable(OsuBestScoresFetchStatus status) => new(null, [],
        coverage(status, "public best score window", null), coverage(status, "public recent score window", null));

    private sealed record Payload(Profile? Profile, Item[]? Items, Coverage? Coverage);
    private sealed record Coverage(Window? Best, Window? Recent);
    private sealed record Window(string? Status);
    private sealed record Profile(int OsuUserId, string? OsuUsername, string? CountryCode, string? AvatarUrl,
        int? GlobalRank, double? PerformancePoints, int PlayCount, int PlayTimeSeconds);
    private sealed record Item(long OnlineScoreId, int OsuUserId, int BeatmapId, int BeatmapSetId, string? Ruleset,
        string? Source, string? Title, string? Artist, string? Difficulty, DateTimeOffset PlayedAt, double StarRating,
        double Accuracy, double? PerformancePoints, long TotalScore, int MaxCombo, int CountMiss, string[]? Mods,
        bool? Passed, double Bpm, long LengthMs, PpInput? PpCalculation);
    private sealed record PpInput(bool? Lazer);
}

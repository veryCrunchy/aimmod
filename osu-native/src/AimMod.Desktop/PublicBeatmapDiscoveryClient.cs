using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AimMod.Osu.Runtime;

namespace AimMod.Desktop;

/// <summary>Uses the Hub's public catalog when a local lazer session is unavailable.</summary>
public sealed class PublicBeatmapDiscoveryClient : IOfficialBeatmapDiscoveryClient, IOfficialBeatmapDifficultyClient, IDisposable
{
    private const string provider = "PROVIDER_OSU_OFFICIAL";
    private const string cursor_prefix = "hub:";
    private readonly HttpClient http;
    private readonly Uri hub;
    private readonly Func<IOfficialBeatmapDiscoveryClient?> authenticated;
    private readonly OfficialBeatmapDiscoveryClient publicFiles = new();

    public PublicBeatmapDiscoveryClient(HttpClient http, Uri hub, Func<IOfficialBeatmapDiscoveryClient?> authenticated)
    {
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        this.hub = hub ?? throw new ArgumentNullException(nameof(hub));
        this.authenticated = authenticated ?? throw new ArgumentNullException(nameof(authenticated));
    }

    public async Task<OfficialBeatmapSearchResult> SearchAsync(OfficialBeatmapSearchQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var q = query.Normalised();
        bool hubCursor = q.Cursor?.StartsWith(cursor_prefix, StringComparison.Ordinal) == true;
        if (!hubCursor && authenticated() is { } official)
        {
            var result = await official.SearchAsync(q, cancellationToken).ConfigureAwait(false);
            if (!needsPublicCatalog(result.Status)) return result;
        }

        // Public cursors remain on this provider even if lazer logs in mid-scan.
        string? token = hubCursor ? q.Cursor![cursor_prefix.Length..] : null;
        var body = new
        {
            query = q.SearchText,
            providers = new[] { provider },
            filters = new
            {
                ruleset = "RULESET_OSU",
                status = q.Category == OfficialBeatmapCategory.Any ? "any"
                    : q.Category == OfficialBeatmapCategory.Leaderboard ? "leaderboard"
                    : q.Category.ToString().ToLowerInvariant(),
                stars = new { minimum = q.MinimumStars, maximum = q.MaximumStars },
            },
            sort = q.Sort switch
            {
                OfficialBeatmapSort.Artist => "artist_asc",
                OfficialBeatmapSort.Title => "title_asc",
                OfficialBeatmapSort.Rating => "favourites_desc",
                _ => q.Sort.ToString().ToLowerInvariant() + "_desc",
            },
            pageTokens = token is null ? [] : new[] { new { provider, pageToken = token } },
        };
        return await requestAsync("SearchBeatmapItems", body, null, q, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OfficialBeatmapSearchResult> GetSetAsync(int beatmapSetId, CancellationToken cancellationToken = default)
    {
        if (beatmapSetId <= 0) throw new ArgumentOutOfRangeException(nameof(beatmapSetId));
        if (authenticated() is { } official)
        {
            var result = await official.GetSetAsync(beatmapSetId, cancellationToken).ConfigureAwait(false);
            if (!needsPublicCatalog(result.Status)) return result;
        }
        return await requestAsync("GetBeatmapItem", new { provider, sourceId = beatmapSetId.ToString(CultureInfo.InvariantCulture) },
            beatmapSetId, new(), cancellationToken).ConfigureAwait(false);
    }

    public Task<OfficialBeatmapDownloadResult> DownloadAsync(int beatmapSetId, string destinationDirectory, bool noVideo = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Full archives still belong to the user's client; public PP calculation only needs .osu files.
        return authenticated()?.DownloadAsync(beatmapSetId, destinationDirectory, noVideo, cancellationToken)
            ?? Task.FromResult(new OfficialBeatmapDownloadResult(OfficialBeatmapRequestStatus.SessionUnavailable));
    }

    public Task<OfficialBeatmapDifficultyDownloadResult> DownloadDifficultyAsync(int beatmapId, string destinationDirectory,
        CancellationToken cancellationToken = default) => publicFiles.DownloadDifficultyAsync(beatmapId, destinationDirectory, cancellationToken);

    private async Task<OfficialBeatmapSearchResult> requestAsync(string method, object body, int? expectedSet,
        OfficialBeatmapSearchQuery query, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                new Uri(hub.AbsoluteUri.TrimEnd('/') + "/aimmod.osu.v1.OsuService/" + method))
            { Content = JsonContent.Create(body) };
            request.Headers.Add("Connect-Protocol-Version", "1");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return OfficialBeatmapSearchResult.Empty(response.StatusCode == HttpStatusCode.TooManyRequests
                    ? OfficialBeatmapRequestStatus.RateLimited : OfficialBeatmapRequestStatus.ServerError);
            await response.Content.LoadIntoBufferAsync(8 * 1024 * 1024).WaitAsync(deadline.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false));
            JsonElement root = document.RootElement;
            JsonElement[] items;
            string? next = null;
            if (expectedSet is not null)
            {
                if (!root.TryGetProperty("item", out var item)) return invalid();
                items = [item];
            }
            else
            {
                if (!root.TryGetProperty("providers", out var providers) || providers.ValueKind != JsonValueKind.Array)
                    return invalid();
                if (!providers.EnumerateArray().Any(p => isOfficial(p) && boolean(p, "available")))
                    return OfficialBeatmapSearchResult.Empty(OfficialBeatmapRequestStatus.ServerError);
                items = array(root, "items");
                next = array(root, "nextPageTokens").Where(isOfficial).Select(p => str(p, "pageToken")).FirstOrDefault();
            }
            var sets = items.Where(isOfficial).Select(parseSet).Where(s => s is not null).Cast<OfficialBeatmapSet>()
                .Select(s => s with { Difficulties = s.Difficulties.Where(d =>
                    (query.MinimumStars is null || d.StarRating >= query.MinimumStars)
                    && (query.MaximumStars is null || d.StarRating <= query.MaximumStars)).ToArray() })
                .Where(s => s.Difficulties.Count > 0).ToArray();
            if (expectedSet is not null && (sets.Length != 1 || sets[0].BeatmapSetId != expectedSet)) return invalid();
            // Keep the full provider page: trimming it to a UI limit would lose maps when advancing its cursor.
            return new(OfficialBeatmapRequestStatus.Success, sets, sets.Length, !string.IsNullOrEmpty(next),
                string.IsNullOrEmpty(next) ? null : cursor_prefix + next);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or OperationCanceledException
            or InvalidOperationException or FormatException)
        {
            return OfficialBeatmapSearchResult.Empty(error is HttpRequestException or OperationCanceledException
                ? OfficialBeatmapRequestStatus.NetworkError : OfficialBeatmapRequestStatus.InvalidResponse);
        }
    }

    private static OfficialBeatmapSet? parseSet(JsonElement item)
    {
        if (!int.TryParse(str(item, "sourceId"), out int id) || id <= 0) return null;
        var difficulties = array(item, "difficulties").Where(d => str(d, "ruleset") is "RULESET_OSU" or "1")
            .Select(d => new OfficialBeatmapDifficulty(integer(d, "beatmapId"), str(d, "name"), "osu", number(d, "stars"),
                number(d, "bpm"), integer(d, "lengthSeconds"), (float)number(d, "circleSize"), (float)number(d, "approachRate"),
                (float)number(d, "overallDifficulty"), (float)number(d, "drainRate"), 0, 0, null))
            .Where(d => d.BeatmapId > 0 && double.IsFinite(d.StarRating) && d.StarRating > 0).ToArray();
        return new(id, str(item, "title"), "", str(item, "artist"), "", str(item, "creator"), "", str(item, "status"),
            null, DateTimeOffset.TryParse(str(item, "updatedAtIso"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var updated) ? updated : null,
            integer(item, "playCount"), integer(item, "favouriteCount"), false, false,
            url(item, "coverUrl"), url(item, "coverUrl"), url(item, "coverUrl"), url(item, "previewUrl"), difficulties);
    }

    private static bool needsPublicCatalog(OfficialBeatmapRequestStatus status) => status is
        OfficialBeatmapRequestStatus.SessionUnavailable or OfficialBeatmapRequestStatus.SignedOut or OfficialBeatmapRequestStatus.TokenExpired
        or OfficialBeatmapRequestStatus.Unauthorized or OfficialBeatmapRequestStatus.SessionChanged;
    private static bool isOfficial(JsonElement value) => str(value, "provider") is provider or "1";
    private static string str(JsonElement value, string name) => value.TryGetProperty(name, out var property)
        ? property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : property.ToString() : "";
    private static int integer(JsonElement value, string name) => int.TryParse(str(value, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;
    private static double number(JsonElement value, string name) => double.TryParse(str(value, name), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed) ? parsed : 0;
    private static bool boolean(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
    private static JsonElement[] array(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array ? property.EnumerateArray().ToArray() : [];
    private static Uri? url(JsonElement value, string name) => Uri.TryCreate(str(value, name), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    private static OfficialBeatmapSearchResult invalid() => OfficialBeatmapSearchResult.Empty(OfficialBeatmapRequestStatus.InvalidResponse);
    public void Dispose() => publicFiles.Dispose();
}

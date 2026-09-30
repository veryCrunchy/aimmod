using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AimMod.Osu.Runtime;

public sealed class OfficialBeatmapDiscoveryClient : IOfficialBeatmapDiscoveryClient, IOfficialBeatmapDifficultyClient, IDisposable
{
    private const int maximum_search_response_bytes = 8 * 1024 * 1024;
    private const long maximum_archive_bytes = 512L * 1024 * 1024;
    private const long maximum_difficulty_bytes = 16L * 1024 * 1024;
    private const int maximum_redirects = 3;
    private const int maximum_cached_difficulties = 32;
    private const int maximum_cached_difficulty_bytes = 2 * 1024 * 1024;
    private const string search_key = "search";
    private const string set_key = "set";
    private const string download_key = "download";
    private const string difficulty_key = "difficulty";
    private static readonly TimeSpan default_download_stall_timeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan difficulty_cache_lifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan stale_partial_age = TimeSpan.FromHours(1);
    private static readonly Uri search_endpoint = new("https://osu.ppy.sh/api/v2/beatmapsets/search", UriKind.Absolute);
    private static readonly JsonSerializerOptions json_options = new(JsonSerializerDefaults.Web);

    private readonly LazerSessionMonitor? session;
    private readonly HttpClient httpClient;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan downloadStallTimeout;
    private readonly ConcurrentDictionary<string, DateTimeOffset> cooldowns = new();
    private readonly ConcurrentDictionary<int, CachedDifficulty> difficultyCache = new();
    private readonly ConcurrentDictionary<string, bool> sweptDirectories = new(StringComparer.OrdinalIgnoreCase);

    // Individual .osu files are public and do not require a lazer installation.
    public OfficialBeatmapDiscoveryClient()
        : this(OfficialOsuApiClient.CreateProductionHandler())
    {
    }

    public OfficialBeatmapDiscoveryClient(LazerSessionMonitor session)
        : this(session, OfficialOsuApiClient.CreateProductionHandler())
    {
    }

    internal OfficialBeatmapDiscoveryClient(
        LazerSessionMonitor session,
        HttpMessageHandler handler,
        TimeProvider? timeProvider = null,
        TimeSpan? downloadStallTimeout = null)
        : this(handler, timeProvider, downloadStallTimeout)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
    }

    internal OfficialBeatmapDiscoveryClient(
        HttpMessageHandler handler,
        TimeProvider? timeProvider = null,
        TimeSpan? downloadStallTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.downloadStallTimeout = downloadStallTimeout ?? default_download_stall_timeout;
        httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
    }

    public async Task<OfficialBeatmapSearchResult> SearchAsync(
        OfficialBeatmapSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        OfficialBeatmapSearchQuery normalised = query.Normalised();
        if (session is null)
            return OfficialBeatmapSearchResult.Empty(OfficialBeatmapRequestStatus.SessionUnavailable);
        LazerSessionState startingState = session.Current;
        using LazerAccessTokenLease? lease = session.TryLeaseAccessToken();

        if (lease is null || !lease.TryGetAccessToken(out string accessToken))
            return OfficialBeatmapSearchResult.Empty(withoutToken(startingState.Status));

        if (activeCooldown(search_key) is { } searchCooldown)
            return OfficialBeatmapSearchResult.Empty(OfficialBeatmapRequestStatus.RateLimited) with { RetryAfter = searchCooldown };

        Uri searchUri = buildSearchUri(normalised);
        try
        {
            using HttpResponseMessage response = await HttpRequestPolicy.SendWithSingleRetryAsync(
                httpClient,
                () => createJsonRequest(searchUri, accessToken),
                cancellationToken).ConfigureAwait(false);

            OfficialBeatmapRequestStatus? sessionFailure = await validateSessionAsync(startingState, lease, cancellationToken).ConfigureAwait(false);
            if (sessionFailure is not null)
                return OfficialBeatmapSearchResult.Empty(sessionFailure.Value);

            OfficialBeatmapRequestStatus? responseFailure = classifyFailure(response, search_key);
            if (responseFailure is not null)
                return OfficialBeatmapSearchResult.Empty(responseFailure.Value) with { RetryAfter = retryAfter(responseFailure.Value, search_key) };

            SearchResponse? payload = await readJsonPayloadAsync<SearchResponse>(response, maximum_search_response_bytes, cancellationToken).ConfigureAwait(false);
            if (payload?.BeatmapSets is null || payload.Total < 0)
                return OfficialBeatmapSearchResult.Empty(OfficialBeatmapRequestStatus.InvalidResponse);

            OfficialBeatmapSet[] sets = payload.BeatmapSets
                                                   .Select(parseSet)
                                                   .Where(set => set is not null)
                                                   .Cast<OfficialBeatmapSet>()
                                                   .Select(set => filterDifficulties(set, normalised.MinimumStars, normalised.MaximumStars))
                                                   .Where(set => set.Difficulties.Count > 0)
                                                   .Take(normalised.Limit)
                                                   .ToArray();

            return new OfficialBeatmapSearchResult(
                OfficialBeatmapRequestStatus.Success,
                sets,
                payload.Total,
                payload.BeatmapSets.Count > sets.Length || payload.Total > sets.Length,
                string.IsNullOrEmpty(payload.CursorString) ? null : payload.CursorString);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            return OfficialBeatmapSearchResult.Empty(
                exception is HttpRequestException or TaskCanceledException
                    ? OfficialBeatmapRequestStatus.NetworkError
                    : OfficialBeatmapRequestStatus.InvalidResponse);
        }
    }

    public async Task<OfficialBeatmapSearchResult> GetSetAsync(int beatmapSetId, CancellationToken cancellationToken = default)
    {
        if (beatmapSetId <= 0)
            throw new ArgumentOutOfRangeException(nameof(beatmapSetId));
        if (session is null)
            return OfficialBeatmapSearchResult.Empty(OfficialBeatmapRequestStatus.SessionUnavailable);
        LazerSessionState startingState = session.Current;
        using LazerAccessTokenLease? lease = session.TryLeaseAccessToken();
        if (lease is null || !lease.TryGetAccessToken(out string accessToken))
            return OfficialBeatmapSearchResult.Empty(withoutToken(startingState.Status));
        if (activeCooldown(set_key) is { } setCooldown)
            return OfficialBeatmapSearchResult.Empty(OfficialBeatmapRequestStatus.RateLimited) with { RetryAfter = setCooldown };
        Uri setUri = new($"https://osu.ppy.sh/api/v2/beatmapsets/{beatmapSetId}");
        try
        {
            using HttpResponseMessage response = await HttpRequestPolicy.SendWithSingleRetryAsync(
                httpClient,
                () => createJsonRequest(setUri, accessToken),
                cancellationToken).ConfigureAwait(false);
            var failure = await validateSessionAsync(startingState, lease, cancellationToken).ConfigureAwait(false) ?? classifyFailure(response, set_key);
            if (failure is not null)
                return OfficialBeatmapSearchResult.Empty(failure.Value) with { RetryAfter = retryAfter(failure.Value, set_key) };
            var payload = await readJsonPayloadAsync<SearchBeatmapSet>(response, maximum_search_response_bytes, cancellationToken).ConfigureAwait(false);
            var set = payload is null ? null : parseSet(payload);
            return set?.BeatmapSetId == beatmapSetId
                ? new OfficialBeatmapSearchResult(OfficialBeatmapRequestStatus.Success, [set], 1, false)
                : OfficialBeatmapSearchResult.Empty(OfficialBeatmapRequestStatus.InvalidResponse);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            return OfficialBeatmapSearchResult.Empty(error is HttpRequestException or TaskCanceledException
                ? OfficialBeatmapRequestStatus.NetworkError : OfficialBeatmapRequestStatus.InvalidResponse);
        }
    }

    public async Task<OfficialBeatmapDownloadResult> DownloadAsync(
        int beatmapSetId,
        string destinationDirectory,
        bool noVideo = false,
        CancellationToken cancellationToken = default)
    {
        if (beatmapSetId <= 0)
            throw new ArgumentOutOfRangeException(nameof(beatmapSetId));
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        if (!Path.IsPathFullyQualified(destinationDirectory))
            throw new ArgumentException("The beatmap download directory must be absolute.", nameof(destinationDirectory));

        if (session is null)
            return new OfficialBeatmapDownloadResult(OfficialBeatmapRequestStatus.SessionUnavailable);

        LazerSessionState startingState = session.Current;
        using LazerAccessTokenLease? lease = session.TryLeaseAccessToken();
        if (lease is null || !lease.TryGetAccessToken(out string accessToken))
            return new OfficialBeatmapDownloadResult(withoutToken(startingState.Status));

        if (activeCooldown(download_key) is { } downloadCooldown)
            return new OfficialBeatmapDownloadResult(OfficialBeatmapRequestStatus.RateLimited, RetryAfter: downloadCooldown);

        Directory.CreateDirectory(destinationDirectory);
        sweepStalePartialFiles(destinationDirectory);
        string archivePath = Path.Combine(destinationDirectory, $"aimmod-{beatmapSetId}-{Guid.NewGuid():N}.osz");
        string partialPath = partialPathFor(archivePath);

        try
        {
            Uri uri = new($"https://osu.ppy.sh/api/v2/beatmapsets/{beatmapSetId}/download{(noVideo ? "?noVideo=1" : string.Empty)}");
            using HttpResponseMessage response = await sendDownloadRequestAsync(uri, accessToken, cancellationToken).ConfigureAwait(false);

            OfficialBeatmapRequestStatus? sessionFailure = await validateSessionAsync(startingState, lease, cancellationToken).ConfigureAwait(false);
            if (sessionFailure is not null)
                return new OfficialBeatmapDownloadResult(sessionFailure.Value);

            OfficialBeatmapRequestStatus? responseFailure = classifyFailure(response, download_key);
            if (responseFailure is not null)
                return new OfficialBeatmapDownloadResult(responseFailure.Value, RetryAfter: retryAfter(responseFailure.Value, download_key));
            if (response.Content.Headers.ContentLength is > maximum_archive_bytes)
                return new OfficialBeatmapDownloadResult(OfficialBeatmapRequestStatus.InvalidResponse);

            long bytesWritten = await copyBoundedAsync(response.Content, partialPath, cancellationToken).ConfigureAwait(false);
            if (bytesWritten <= 0 || !isBeatmapArchive(partialPath))
            {
                deleteIfPresent(partialPath);
                return new OfficialBeatmapDownloadResult(OfficialBeatmapRequestStatus.InvalidResponse);
            }

            if (!lease.TryGetAccessToken(out _))
            {
                deleteIfPresent(partialPath);
                return new OfficialBeatmapDownloadResult(OfficialBeatmapRequestStatus.SessionChanged);
            }

            File.Move(partialPath, archivePath);
            return new OfficialBeatmapDownloadResult(OfficialBeatmapRequestStatus.Success, archivePath, bytesWritten);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            deleteIfPresent(partialPath);
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            deleteIfPresent(partialPath);
            return new OfficialBeatmapDownloadResult(
                exception is HttpRequestException or TaskCanceledException
                    ? OfficialBeatmapRequestStatus.NetworkError
                    : OfficialBeatmapRequestStatus.InvalidResponse);
        }
    }

    public async Task<OfficialBeatmapDifficultyDownloadResult> DownloadDifficultyAsync(
        int beatmapId,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        if (beatmapId <= 0)
            throw new ArgumentOutOfRangeException(nameof(beatmapId));
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        if (!Path.IsPathFullyQualified(destinationDirectory))
            throw new ArgumentException("The beatmap difficulty download directory must be absolute.", nameof(destinationDirectory));

        Directory.CreateDirectory(destinationDirectory);
        sweepStalePartialFiles(destinationDirectory);
        string beatmapPath = Path.Combine(destinationDirectory, $"aimmod-{beatmapId}-{Guid.NewGuid():N}.osu");
        string partialPath = partialPathFor(beatmapPath);
        try
        {
            if (tryGetCachedDifficulty(beatmapId) is { } cachedContent)
            {
                await File.WriteAllBytesAsync(beatmapPath, cachedContent, cancellationToken).ConfigureAwait(false);
                return new OfficialBeatmapDifficultyDownloadResult(
                    OfficialBeatmapRequestStatus.Success,
                    beatmapId,
                    beatmapPath,
                    cachedContent.Length);
            }

            if (activeCooldown(difficulty_key) is { } difficultyCooldown)
                return new OfficialBeatmapDifficultyDownloadResult(OfficialBeatmapRequestStatus.RateLimited, beatmapId, RetryAfter: difficultyCooldown);

            Uri difficultyUri = new($"https://osu.ppy.sh/osu/{beatmapId}");
            using HttpResponseMessage response = await HttpRequestPolicy.SendWithSingleRetryAsync(
                httpClient,
                () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, difficultyUri);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
                    return request;
                },
                cancellationToken).ConfigureAwait(false);

            OfficialBeatmapRequestStatus? failure = classifyFailure(response, difficulty_key);
            if (failure is not null)
                return new OfficialBeatmapDifficultyDownloadResult(failure.Value, beatmapId, RetryAfter: retryAfter(failure.Value, difficulty_key));
            if (response.Content.Headers.ContentLength is > maximum_difficulty_bytes)
                return new OfficialBeatmapDifficultyDownloadResult(OfficialBeatmapRequestStatus.InvalidResponse, beatmapId);

            long bytesWritten = await copyBoundedAsync(
                response.Content,
                partialPath,
                maximum_difficulty_bytes,
                "The beatmap difficulty exceeds AimMod's download limit.",
                cancellationToken).ConfigureAwait(false);
            if (bytesWritten <= 0 || !isExpectedDifficulty(partialPath, beatmapId))
            {
                deleteIfPresent(partialPath);
                return new OfficialBeatmapDifficultyDownloadResult(OfficialBeatmapRequestStatus.InvalidResponse, beatmapId);
            }

            if (bytesWritten <= maximum_cached_difficulty_bytes)
                await cacheDifficultyAsync(beatmapId, partialPath, cancellationToken).ConfigureAwait(false);

            File.Move(partialPath, beatmapPath);
            return new OfficialBeatmapDifficultyDownloadResult(
                OfficialBeatmapRequestStatus.Success,
                beatmapId,
                beatmapPath,
                bytesWritten);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            deleteIfPresent(partialPath);
            deleteIfPresent(beatmapPath);
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            deleteIfPresent(partialPath);
            deleteIfPresent(beatmapPath);
            return new OfficialBeatmapDifficultyDownloadResult(
                exception is HttpRequestException or TaskCanceledException
                    ? OfficialBeatmapRequestStatus.NetworkError
                    : OfficialBeatmapRequestStatus.InvalidResponse,
                beatmapId);
        }
    }

    public void Dispose() => httpClient.Dispose();

    private async Task<HttpResponseMessage> sendDownloadRequestAsync(Uri initialUri, string accessToken, CancellationToken cancellationToken)
    {
        Uri current = initialUri;
        for (int redirect = 0; redirect <= maximum_redirects; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            if (string.Equals(current.Host, "osu.ppy.sh", StringComparison.OrdinalIgnoreCase))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            try
            {
                if ((int)response.StatusCode is < 300 or >= 400)
                    return response;

                Uri? redirectUri = response.Headers.Location;
                if (redirectUri is not null && !redirectUri.IsAbsoluteUri)
                    redirectUri = new Uri(current, redirectUri);
                if (redirect == maximum_redirects || !isTrustedDownloadUri(redirectUri))
                    return response;

                response.Dispose();
                current = redirectUri!;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }

        throw new InvalidOperationException("The redirect limit was not enforced.");
    }

    private async Task<OfficialBeatmapRequestStatus?> validateSessionAsync(
        LazerSessionState startingState,
        LazerAccessTokenLease lease,
        CancellationToken cancellationToken)
    {
        try
        {
            await session!.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return OfficialBeatmapRequestStatus.SessionUnavailable;
        }

        return session.Current.Revision != startingState.Revision || !lease.TryGetAccessToken(out _)
            ? OfficialBeatmapRequestStatus.SessionChanged
            : null;
    }

    private OfficialBeatmapRequestStatus? classifyFailure(HttpResponseMessage response, string endpoint)
    {
        if (HttpRequestPolicy.IsRateLimited(response.StatusCode))
        {
            registerRateLimit(endpoint, response);
            return OfficialBeatmapRequestStatus.RateLimited;
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return OfficialBeatmapRequestStatus.Unauthorized;
        if ((int)response.StatusCode is >= 300 and < 400)
            return OfficialBeatmapRequestStatus.InvalidResponse;
        if (!response.IsSuccessStatusCode)
            return OfficialBeatmapRequestStatus.ServerError;
        return null;
    }

    private DateTimeOffset? activeCooldown(string endpoint) =>
        cooldowns.TryGetValue(endpoint, out DateTimeOffset until) && until > timeProvider.GetUtcNow() ? until : null;

    private DateTimeOffset? retryAfter(OfficialBeatmapRequestStatus status, string endpoint) =>
        status == OfficialBeatmapRequestStatus.RateLimited && cooldowns.TryGetValue(endpoint, out DateTimeOffset until) ? until : null;

    private void registerRateLimit(string endpoint, HttpResponseMessage response)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset until = now + HttpRequestPolicy.ClampCooldown(HttpRequestPolicy.ParseRetryAfter(response.Headers.RetryAfter, now));
        cooldowns.AddOrUpdate(endpoint, until, (_, existing) => existing > until ? existing : until);
    }

    private byte[]? tryGetCachedDifficulty(int beatmapId)
    {
        if (difficultyCache.TryGetValue(beatmapId, out CachedDifficulty? entry))
        {
            if (timeProvider.GetUtcNow() - entry.CachedAt < difficulty_cache_lifetime)
                return entry.Content;

            difficultyCache.TryRemove(beatmapId, out _);
        }

        return null;
    }

    private async Task cacheDifficultyAsync(int beatmapId, string verifiedPath, CancellationToken cancellationToken)
    {
        byte[] content = await File.ReadAllBytesAsync(verifiedPath, cancellationToken).ConfigureAwait(false);
        difficultyCache[beatmapId] = new CachedDifficulty(content, timeProvider.GetUtcNow());
        if (difficultyCache.Count <= maximum_cached_difficulties)
            return;

        foreach (KeyValuePair<int, CachedDifficulty> oldest in difficultyCache.OrderBy(pair => pair.Value.CachedAt).Take(difficultyCache.Count - maximum_cached_difficulties))
            difficultyCache.TryRemove(oldest.Key, out _);
    }

    private static string partialPathFor(string finalPath) =>
        Path.Combine(Path.GetDirectoryName(finalPath)!, $".{Path.GetFileName(finalPath)}.partial");

    private void sweepStalePartialFiles(string directory)
    {
        if (!sweptDirectories.TryAdd(directory, true))
            return;

        try
        {
            DateTime threshold = timeProvider.GetUtcNow().UtcDateTime - stale_partial_age;
            foreach (string path in Directory.EnumerateFiles(directory, ".aimmod-*.partial"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < threshold)
                        File.Delete(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record CachedDifficulty(byte[] Content, DateTimeOffset CachedAt);

    private static Uri buildSearchUri(OfficialBeatmapSearchQuery query)
    {
        var terms = new List<string> { query.SearchText };
        if (query.MinimumStars is { } minimum) terms.Add($"stars>={minimum.ToString("R", CultureInfo.InvariantCulture)}");
        if (query.MaximumStars is { } maximum) terms.Add($"stars<={maximum.ToString("R", CultureInfo.InvariantCulture)}");
        var parameters = new Dictionary<string, string>
        {
            ["q"] = string.Join(' ', terms.Where(term => term.Length > 0)),
            ["m"] = "0",
            ["s"] = query.Category.ToString().ToLowerInvariant(),
            ["sort"] = $"{(query.Sort == OfficialBeatmapSort.Relevance && query.SearchText.Length == 0 ? OfficialBeatmapSort.Ranked : query.Sort).ToString().ToLowerInvariant()}_desc",
            ["nsfw"] = query.IncludeExplicitContent ? "true" : "false",
        };
        if (!string.IsNullOrEmpty(query.Cursor)) parameters["cursor_string"] = query.Cursor;
        string encoded = string.Join("&", parameters.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new UriBuilder(search_endpoint) { Query = encoded }.Uri;
    }

    private static HttpRequestMessage createJsonRequest(Uri uri, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static async Task<T?> readJsonPayloadAsync<T>(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using PooledBody? body = await PooledBody.ReadAsync(response.Content, maximumBytes, cancellationToken).ConfigureAwait(false);
        return body is { } content ? JsonSerializer.Deserialize<T>(content.Span, json_options) : default;
    }

    private static OfficialBeatmapSet? parseSet(SearchBeatmapSet payload)
    {
        if (payload.Id <= 0 || string.IsNullOrWhiteSpace(payload.Title) || string.IsNullOrWhiteSpace(payload.Artist) ||
            string.IsNullOrWhiteSpace(payload.Creator) || payload.Beatmaps is null)
            return null;

        OfficialBeatmapDifficulty[] difficulties = payload.Beatmaps
                                                         .Where(beatmap => beatmap.Id > 0 && beatmap.ModeInt is >= 0 and <= 3 && beatmap.DifficultyRating >= 0)
                                                         .Select(beatmap => new OfficialBeatmapDifficulty(
                                                             beatmap.Id,
                                                             beatmap.Version ?? string.Empty,
                                                             rulesetShortName(beatmap.ModeInt),
                                                             beatmap.DifficultyRating,
                                                             beatmap.Bpm,
                                                             Math.Max(0, beatmap.TotalLength),
                                                             beatmap.CircleSize,
                                                             beatmap.ApproachRate,
                                                             beatmap.OverallDifficulty,
                                                             beatmap.DrainRate,
                                                             Math.Max(0, beatmap.PlayCount),
                                                             Math.Max(0, beatmap.PassCount),
                                                             beatmap.MaximumCombo))
                                                         .ToArray();

        return new OfficialBeatmapSet(
            payload.Id,
            payload.Title,
            payload.TitleUnicode ?? payload.Title,
            payload.Artist,
            payload.ArtistUnicode ?? payload.Artist,
            payload.Creator,
            payload.Source ?? string.Empty,
            payload.Status ?? string.Empty,
            payload.RankedDate,
            payload.LastUpdated,
            Math.Max(0, payload.PlayCount),
            Math.Max(0, payload.FavouriteCount),
            payload.Nsfw,
            payload.Availability?.DownloadDisabled ?? false,
            parseAssetUrl(payload.Covers?.Cover2x ?? payload.Covers?.Cover),
            parseAssetUrl(payload.Covers?.Card2x ?? payload.Covers?.Card),
            parseAssetUrl(payload.Covers?.List2x ?? payload.Covers?.List),
            parseAssetUrl(payload.PreviewUrl),
            difficulties);
    }

    private static OfficialBeatmapSet filterDifficulties(OfficialBeatmapSet set, double? minimumStars, double? maximumStars) => set with
    {
        Difficulties = set.Difficulties
                          .Where(difficulty => difficulty.RulesetShortName == "osu")
                          .Where(difficulty => minimumStars is null || difficulty.StarRating >= minimumStars)
                          .Where(difficulty => maximumStars is null || difficulty.StarRating <= maximumStars)
                          .OrderBy(difficulty => difficulty.StarRating)
                          .ToArray(),
    };

    private static Uri? parseAssetUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (value.StartsWith("//", StringComparison.Ordinal))
            value = "https:" + value;
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    }

    private static string rulesetShortName(int rulesetId) => rulesetId switch
    {
        0 => "osu",
        1 => "taiko",
        2 => "fruits",
        3 => "mania",
        _ => string.Empty,
    };

    private static bool isTrustedDownloadUri(Uri? uri) => uri is { IsAbsoluteUri: true } &&
        uri.Scheme == Uri.UriSchemeHttps &&
        (string.Equals(uri.Host, "osu.ppy.sh", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".ppy.sh", StringComparison.OrdinalIgnoreCase));

    private Task<long> copyBoundedAsync(HttpContent content, string path, CancellationToken cancellationToken) =>
        copyBoundedAsync(content, path, maximum_archive_bytes, "The beatmap archive exceeds AimMod's download limit.", cancellationToken);

    private async Task<long> copyBoundedAsync(
        HttpContent content,
        string path,
        long maximumBytes,
        string limitMessage,
        CancellationToken cancellationToken)
    {
        await using Stream input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        byte[] buffer = new byte[81920];
        long total = 0;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        while (true)
        {
            stall.CancelAfter(downloadStallTimeout);
            int read;
            try
            {
                read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new HttpRequestException("The beatmap download stalled.");
            }

            if (read == 0)
                break;
            total += read;
            if (total > maximumBytes)
                throw new IOException(limitMessage);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return total;
    }

    private static bool isExpectedDifficulty(string path, int beatmapId)
    {
        try
        {
            using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
            string? firstLine = reader.ReadLine();
            if (firstLine is null || !firstLine.TrimStart('\uFEFF').StartsWith("osu file format v", StringComparison.Ordinal))
                return false;

            while (reader.ReadLine() is { } line)
            {
                if (!line.StartsWith("BeatmapID:", StringComparison.OrdinalIgnoreCase))
                    continue;
                return int.TryParse(line.AsSpan("BeatmapID:".Length).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
                       && parsed == beatmapId;
            }
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool isBeatmapArchive(string path)
    {
        try
        {
            Span<byte> signature = stackalloc byte[4];
            using FileStream stream = File.OpenRead(path);
            if (stream.Read(signature) != signature.Length ||
                signature[0] != (byte)'P' || signature[1] != (byte)'K' ||
                signature[2] is not (3 or 5 or 7) || signature[3] is not (4 or 6 or 8))
                return false;

            stream.Position = 0;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            return archive.Entries.Any(entry =>
                string.Equals(Path.GetExtension(entry.FullName), ".osu", StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static void deleteIfPresent(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static OfficialBeatmapRequestStatus withoutToken(LazerSessionStatus status) => status switch
    {
        LazerSessionStatus.SignedOut => OfficialBeatmapRequestStatus.SignedOut,
        LazerSessionStatus.Remembered => OfficialBeatmapRequestStatus.TokenExpired,
        _ => OfficialBeatmapRequestStatus.SessionUnavailable,
    };

    private sealed class SearchResponse
    {
        [JsonPropertyName("cursor_string")]
        public string? CursorString { get; init; }
        [JsonPropertyName("beatmapsets")]
        public List<SearchBeatmapSet>? BeatmapSets { get; init; }

        public int Total { get; init; }
    }

    private sealed class SearchBeatmapSet
    {
        public int Id { get; init; }
        public string? Title { get; init; }

        [JsonPropertyName("title_unicode")]
        public string? TitleUnicode { get; init; }

        public string? Artist { get; init; }

        [JsonPropertyName("artist_unicode")]
        public string? ArtistUnicode { get; init; }

        public string? Creator { get; init; }
        public string? Source { get; init; }
        public string? Status { get; init; }

        [JsonPropertyName("ranked_date")]
        public DateTimeOffset? RankedDate { get; init; }

        [JsonPropertyName("last_updated")]
        public DateTimeOffset? LastUpdated { get; init; }

        [JsonPropertyName("play_count")]
        public int PlayCount { get; init; }

        [JsonPropertyName("favourite_count")]
        public int FavouriteCount { get; init; }

        public bool Nsfw { get; init; }

        [JsonPropertyName("preview_url")]
        public string? PreviewUrl { get; init; }

        public SearchCovers? Covers { get; init; }
        public SearchAvailability? Availability { get; init; }
        public List<SearchBeatmap>? Beatmaps { get; init; }
    }

    private sealed class SearchCovers
    {
        public string? Cover { get; init; }

        [JsonPropertyName("cover@2x")]
        public string? Cover2x { get; init; }

        public string? Card { get; init; }

        [JsonPropertyName("card@2x")]
        public string? Card2x { get; init; }

        public string? List { get; init; }

        [JsonPropertyName("list@2x")]
        public string? List2x { get; init; }
    }

    private sealed class SearchAvailability
    {
        [JsonPropertyName("download_disabled")]
        public bool DownloadDisabled { get; init; }
    }

    private sealed class SearchBeatmap
    {
        public int Id { get; init; }
        public string? Version { get; init; }

        [JsonPropertyName("mode_int")]
        public int ModeInt { get; init; }

        [JsonPropertyName("difficulty_rating")]
        public double DifficultyRating { get; init; }

        public double Bpm { get; init; }

        [JsonPropertyName("total_length")]
        public int TotalLength { get; init; }

        [JsonPropertyName("cs")]
        public float CircleSize { get; init; }

        [JsonPropertyName("ar")]
        public float ApproachRate { get; init; }

        [JsonPropertyName("accuracy")]
        public float OverallDifficulty { get; init; }

        [JsonPropertyName("drain")]
        public float DrainRate { get; init; }

        [JsonPropertyName("playcount")]
        public int PlayCount { get; init; }

        [JsonPropertyName("passcount")]
        public int PassCount { get; init; }

        [JsonPropertyName("max_combo")]
        public int? MaximumCombo { get; init; }
    }
}

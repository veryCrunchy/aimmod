using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AimMod.Osu.Runtime;

public sealed class OfficialOsuApiClient : IDisposable
{
    private const int maximum_response_bytes = 1024 * 1024;
    private const int maximum_scores_response_bytes = 8 * 1024 * 1024;
    private const int scores_page_size = 100;
    private const int maximum_score_pages = 20;
    private const int score_cache_schema_version = 2;
    private const string osu_api_version = "20220705";
    private static readonly TimeSpan score_cache_lifetime = TimeSpan.FromMinutes(15);
    private static readonly Uri profile_endpoint = new("https://osu.ppy.sh/api/v2/me/osu", UriKind.Absolute);
    private static readonly JsonSerializerOptions json_options = new(JsonSerializerDefaults.Web);

    private readonly LazerSessionMonitor session;
    private readonly HttpClient httpClient;
    private readonly string scoreCacheDirectory;
    private readonly TimeProvider timeProvider;

    public OfficialOsuApiClient(LazerSessionMonitor session)
        : this(session, CreateProductionHandler(), getDefaultCacheDirectory(), TimeProvider.System)
    {
    }

    internal OfficialOsuApiClient(LazerSessionMonitor session, HttpMessageHandler handler)
        : this(session, handler, Path.Combine(Path.GetTempPath(), $"aimmod-osu-api-{Guid.NewGuid():N}"), TimeProvider.System)
    {
    }

    internal OfficialOsuApiClient(
        LazerSessionMonitor session,
        HttpMessageHandler handler,
        string scoreCacheDirectory,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentException.ThrowIfNullOrWhiteSpace(scoreCacheDirectory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.session = session;
        this.scoreCacheDirectory = Path.GetFullPath(scoreCacheDirectory);
        this.timeProvider = timeProvider;
        httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    public async Task<OsuBestScoresFetchResult> FetchBestScoresAsync(
        OsuProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.UserId <= 0 || string.IsNullOrWhiteSpace(profile.Username))
            throw new ArgumentException("A valid osu! profile is required.", nameof(profile));

        try
        {
            await session.RefreshAsync(cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            return new OsuBestScoresFetchResult(OsuBestScoresFetchStatus.SessionUnavailable);
        }

        LazerSessionState startingState = session.Current;
        using LazerAccessTokenLease? lease = session.TryLeaseAccessToken();

        if (lease is null || !lease.TryGetAccessToken(out string accessToken))
            return withoutScoreToken(startingState.Status);
        if (!string.Equals(startingState.Username, profile.Username, StringComparison.OrdinalIgnoreCase))
        {
            accessToken = string.Empty;
            return new OsuBestScoresFetchResult(OsuBestScoresFetchStatus.SessionChanged);
        }

        OsuBestScoresCacheDocument? cached = await readScoreCacheAsync(profile.UserId, cancellationToken);
        if (cached is not null)
        {
            if (!isSameSession(startingState, lease, profile))
            {
                accessToken = string.Empty;
                return new OsuBestScoresFetchResult(OsuBestScoresFetchStatus.SessionChanged);
            }

            accessToken = string.Empty;
            return new OsuBestScoresFetchResult(
                OsuBestScoresFetchStatus.Success,
                cached.Scores,
                IsFromCache: true,
                cached.FetchedAt);
        }

        var scores = new List<OsuBestScore>();

        try
        {
            for (int page = 0; page < maximum_score_pages; page++)
            {
                int offset = page * scores_page_size;
                Uri endpoint = new($"https://osu.ppy.sh/api/v2/users/{profile.UserId}/scores/best?mode=osu&limit={scores_page_size}&offset={offset}");
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                request.Headers.TryAddWithoutValidation("x-api-version", osu_api_version);

                using HttpResponseMessage response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                OsuBestScoresFetchStatus? sessionStatus = await validateScoreSessionAsync(startingState, lease, profile, cancellationToken);
                if (sessionStatus is not null)
                    return new OsuBestScoresFetchResult(sessionStatus.Value);

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return new OsuBestScoresFetchResult(OsuBestScoresFetchStatus.Unauthorized);
                if ((int)response.StatusCode is >= 300 and < 400)
                    return new OsuBestScoresFetchResult(OsuBestScoresFetchStatus.InvalidResponse);
                if (!response.IsSuccessStatusCode)
                    return new OsuBestScoresFetchResult(OsuBestScoresFetchStatus.ServerError);

                List<BestScoreResponse>? payload = await readPayloadAsync<List<BestScoreResponse>>(
                    response,
                    maximum_scores_response_bytes,
                    cancellationToken);
                if (payload is null || payload.Count > scores_page_size)
                    return new OsuBestScoresFetchResult(OsuBestScoresFetchStatus.InvalidResponse);

                sessionStatus = await validateScoreSessionAsync(startingState, lease, profile, cancellationToken);
                if (sessionStatus is not null)
                    return new OsuBestScoresFetchResult(sessionStatus.Value);

                foreach (BestScoreResponse item in payload)
                {
                    OsuBestScore? score = parseBestScore(item, profile);
                    if (score is null)
                        return new OsuBestScoresFetchResult(OsuBestScoresFetchStatus.InvalidResponse);
                    scores.Add(score);
                }

                if (payload.Count < scores_page_size)
                    break;

                if (page == maximum_score_pages - 1)
                    return new OsuBestScoresFetchResult(OsuBestScoresFetchStatus.InvalidResponse);
            }

            if (!isSameSession(startingState, lease, profile))
                return new OsuBestScoresFetchResult(OsuBestScoresFetchStatus.SessionChanged);

            DateTimeOffset fetchedAt = timeProvider.GetUtcNow();
            var document = new OsuBestScoresCacheDocument(
                score_cache_schema_version,
                osu_api_version,
                profile.UserId,
                fetchedAt,
                fetchedAt + score_cache_lifetime,
                scores);
            await tryWriteScoreCacheAsync(document, cancellationToken);

            return new OsuBestScoresFetchResult(OsuBestScoresFetchStatus.Success, scores, FetchedAt: fetchedAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            return new OsuBestScoresFetchResult(
                exception is HttpRequestException or TaskCanceledException
                    ? OsuBestScoresFetchStatus.NetworkError
                    : OsuBestScoresFetchStatus.InvalidResponse);
        }
        finally
        {
            accessToken = string.Empty;
        }
    }

    public async Task<OsuUserBeatmapScoresFetchResult> FetchUserBeatmapScoresAsync(
        OsuProfile profile,
        int beatmapId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.UserId <= 0 || string.IsNullOrWhiteSpace(profile.Username))
            throw new ArgumentException("A valid osu! profile is required.", nameof(profile));
        if (beatmapId <= 0)
            throw new ArgumentOutOfRangeException(nameof(beatmapId));

        try
        {
            await session.RefreshAsync(cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            return new OsuUserBeatmapScoresFetchResult(OsuBestScoresFetchStatus.SessionUnavailable);
        }

        LazerSessionState startingState = session.Current;
        using LazerAccessTokenLease? lease = session.TryLeaseAccessToken();
        if (lease is null || !lease.TryGetAccessToken(out string accessToken))
            return new OsuUserBeatmapScoresFetchResult(withoutScoreToken(startingState.Status).Status);
        if (!string.Equals(startingState.Username, profile.Username, StringComparison.OrdinalIgnoreCase))
        {
            accessToken = string.Empty;
            return new OsuUserBeatmapScoresFetchResult(OsuBestScoresFetchStatus.SessionChanged);
        }

        OsuUserBeatmapScoresCacheDocument? cached = await readBeatmapScoreCacheAsync(profile.UserId, beatmapId, cancellationToken);
        if (cached is not null)
        {
            if (!isSameSession(startingState, lease, profile))
            {
                accessToken = string.Empty;
                return new OsuUserBeatmapScoresFetchResult(OsuBestScoresFetchStatus.SessionChanged);
            }
            accessToken = string.Empty;
            return new OsuUserBeatmapScoresFetchResult(
                OsuBestScoresFetchStatus.Success,
                cached.Scores,
                true,
                cached.FetchedAt);
        }

        try
        {
            Uri endpoint = new($"https://osu.ppy.sh/api/v2/beatmaps/{beatmapId}/scores/users/{profile.UserId}/all?ruleset=osu");
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("x-api-version", osu_api_version);

            using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            OsuBestScoresFetchStatus? sessionStatus = await validateScoreSessionAsync(startingState, lease, profile, cancellationToken);
            if (sessionStatus is not null)
                return new OsuUserBeatmapScoresFetchResult(sessionStatus.Value);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new OsuUserBeatmapScoresFetchResult(OsuBestScoresFetchStatus.Unauthorized);
            if ((int)response.StatusCode is >= 300 and < 400)
                return new OsuUserBeatmapScoresFetchResult(OsuBestScoresFetchStatus.InvalidResponse);
            if (!response.IsSuccessStatusCode)
                return new OsuUserBeatmapScoresFetchResult(OsuBestScoresFetchStatus.ServerError);

            UserBeatmapScoresResponse? payload = await readPayloadAsync<UserBeatmapScoresResponse>(response, maximum_scores_response_bytes, cancellationToken);
            if (payload?.Scores is null)
                return new OsuUserBeatmapScoresFetchResult(OsuBestScoresFetchStatus.InvalidResponse);

            var scores = new List<OsuUserBeatmapScore>(payload.Scores.Count);
            foreach (BestScoreResponse item in payload.Scores)
            {
                OsuUserBeatmapScore? score = parseUserBeatmapScore(item, profile.UserId);
                if (score is null)
                    return new OsuUserBeatmapScoresFetchResult(OsuBestScoresFetchStatus.InvalidResponse);
                scores.Add(score);
            }

            DateTimeOffset fetchedAt = timeProvider.GetUtcNow();
            var document = new OsuUserBeatmapScoresCacheDocument(
                score_cache_schema_version,
                osu_api_version,
                profile.UserId,
                beatmapId,
                fetchedAt,
                fetchedAt + score_cache_lifetime,
                scores);
            await tryWriteBeatmapScoreCacheAsync(document, cancellationToken);
            return new OsuUserBeatmapScoresFetchResult(OsuBestScoresFetchStatus.Success, scores, false, fetchedAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            return new OsuUserBeatmapScoresFetchResult(
                exception is HttpRequestException or TaskCanceledException
                    ? OsuBestScoresFetchStatus.NetworkError
                    : OsuBestScoresFetchStatus.InvalidResponse);
        }
        finally
        {
            accessToken = string.Empty;
        }
    }

    public async Task<OsuBestScoresFetchResult> FetchRecentScoresAsync(
        OsuProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.UserId <= 0 || string.IsNullOrWhiteSpace(profile.Username))
            throw new ArgumentException("A valid osu! profile is required.", nameof(profile));

        try
        {
            await session.RefreshAsync(cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            return new OsuBestScoresFetchResult(OsuBestScoresFetchStatus.SessionUnavailable);
        }

        LazerSessionState startingState = session.Current;
        using LazerAccessTokenLease? lease = session.TryLeaseAccessToken();
        if (lease is null || !lease.TryGetAccessToken(out string accessToken))
            return withoutScoreToken(startingState.Status);
        if (!string.Equals(startingState.Username, profile.Username, StringComparison.OrdinalIgnoreCase))
        {
            accessToken = string.Empty;
            return new OsuBestScoresFetchResult(OsuBestScoresFetchStatus.SessionChanged);
        }

        OsuBestScoresCacheDocument? cached = await readScoreCacheAsync(profile.UserId, cancellationToken, "recent");
        if (cached is not null)
        {
            if (!isSameSession(startingState, lease, profile))
            {
                accessToken = string.Empty;
                return new OsuBestScoresFetchResult(OsuBestScoresFetchStatus.SessionChanged);
            }
            accessToken = string.Empty;
            return new OsuBestScoresFetchResult(OsuBestScoresFetchStatus.Success, cached.Scores, true, cached.FetchedAt);
        }

        try
        {
            Uri endpoint = new($"https://osu.ppy.sh/api/v2/users/{profile.UserId}/scores/recent?mode=osu&include_fails=1&limit={scores_page_size}&offset=0");
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("x-api-version", osu_api_version);
            using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            OsuBestScoresFetchStatus? sessionStatus = await validateScoreSessionAsync(startingState, lease, profile, cancellationToken);
            if (sessionStatus is not null)
                return new OsuBestScoresFetchResult(sessionStatus.Value);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new OsuBestScoresFetchResult(OsuBestScoresFetchStatus.Unauthorized);
            if ((int)response.StatusCode is >= 300 and < 400)
                return new OsuBestScoresFetchResult(OsuBestScoresFetchStatus.InvalidResponse);
            if (!response.IsSuccessStatusCode)
                return new OsuBestScoresFetchResult(OsuBestScoresFetchStatus.ServerError);

            List<BestScoreResponse>? payload = await readPayloadAsync<List<BestScoreResponse>>(response, maximum_scores_response_bytes, cancellationToken);
            if (payload is null || payload.Count > scores_page_size)
                return new OsuBestScoresFetchResult(OsuBestScoresFetchStatus.InvalidResponse);
            var scores = new List<OsuBestScore>(payload.Count);
            foreach (BestScoreResponse item in payload)
            {
                OsuBestScore? score = parseBestScore(item, profile);
                if (score is null)
                    return new OsuBestScoresFetchResult(OsuBestScoresFetchStatus.InvalidResponse);
                scores.Add(score);
            }

            DateTimeOffset fetchedAt = timeProvider.GetUtcNow();
            var document = new OsuBestScoresCacheDocument(score_cache_schema_version, osu_api_version, profile.UserId,
                fetchedAt, fetchedAt + score_cache_lifetime, scores);
            await tryWriteScoreCacheAsync(document, cancellationToken, "recent");
            return new OsuBestScoresFetchResult(OsuBestScoresFetchStatus.Success, scores, false, fetchedAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            return new OsuBestScoresFetchResult(
                exception is HttpRequestException or TaskCanceledException
                    ? OsuBestScoresFetchStatus.NetworkError
                    : OsuBestScoresFetchStatus.InvalidResponse);
        }
        finally
        {
            accessToken = string.Empty;
        }
    }

    public async Task<OsuProfileFetchResult> FetchCurrentProfileAsync(CancellationToken cancellationToken = default)
    {
        LazerSessionState startingState = session.Current;
        using LazerAccessTokenLease? lease = session.TryLeaseAccessToken();

        if (lease is null || !lease.TryGetAccessToken(out string accessToken))
            return withoutToken(startingState.Status);

        using var request = new HttpRequestMessage(HttpMethod.Get, profile_endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        try
        {
            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            try
            {
                await session.RefreshAsync(cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                return new OsuProfileFetchResult(OsuProfileFetchStatus.SessionUnavailable);
            }
            if (session.Current.Revision != startingState.Revision || !lease.TryGetAccessToken(out _))
                return new OsuProfileFetchResult(OsuProfileFetchStatus.SessionChanged);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new OsuProfileFetchResult(OsuProfileFetchStatus.Unauthorized);
            if ((int)response.StatusCode is >= 300 and < 400)
                return new OsuProfileFetchResult(OsuProfileFetchStatus.InvalidResponse);
            if (!response.IsSuccessStatusCode)
                return new OsuProfileFetchResult(OsuProfileFetchStatus.ServerError);

            ProfileResponse? payload = await readPayloadAsync<ProfileResponse>(response, maximum_response_bytes, cancellationToken);
            if (payload is null || payload.Id <= 0 || string.IsNullOrWhiteSpace(payload.Username) ||
                !string.Equals(payload.Username, startingState.Username, StringComparison.OrdinalIgnoreCase))
            {
                return new OsuProfileFetchResult(OsuProfileFetchStatus.InvalidResponse);
            }

            var profile = new OsuProfile(
                payload.Id,
                payload.Username,
                payload.CountryCode,
                parseAvatarUrl(payload.AvatarUrl),
                payload.Statistics is null
                    ? null
                    : new OsuProfileStatistics(
                        payload.Statistics.GlobalRank,
                        payload.Statistics.CountryRank,
                        payload.Statistics.PerformancePoints,
                        payload.Statistics.HitAccuracy,
                        payload.Statistics.PlayCount,
                        payload.Statistics.PlayTime,
                        payload.Statistics.RankedScore,
                        payload.Statistics.TotalScore,
                        payload.Statistics.TotalHits,
                        payload.Statistics.MaximumCombo));

            return new OsuProfileFetchResult(OsuProfileFetchStatus.Success, profile);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            return new OsuProfileFetchResult(
                exception is HttpRequestException or TaskCanceledException
                    ? OsuProfileFetchStatus.NetworkError
                    : OsuProfileFetchStatus.InvalidResponse);
        }
    }

    public void Dispose() => httpClient.Dispose();

    internal static HttpClientHandler CreateProductionHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.Brotli | DecompressionMethods.Deflate | DecompressionMethods.GZip,
    };

    private static OsuProfileFetchResult withoutToken(LazerSessionStatus status) => new(status switch
    {
        LazerSessionStatus.SignedOut => OsuProfileFetchStatus.SignedOut,
        LazerSessionStatus.Remembered => OsuProfileFetchStatus.TokenExpired,
        _ => OsuProfileFetchStatus.SessionUnavailable,
    });

    private static OsuBestScoresFetchResult withoutScoreToken(LazerSessionStatus status) => new(status switch
    {
        LazerSessionStatus.SignedOut => OsuBestScoresFetchStatus.SignedOut,
        LazerSessionStatus.Remembered => OsuBestScoresFetchStatus.TokenExpired,
        _ => OsuBestScoresFetchStatus.SessionUnavailable,
    });

    private static async Task<T?> readPayloadAsync<T>(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maximumBytes)
            return default;

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        byte[] buffer = new byte[maximumBytes + 1];
        int bytesRead = 0;

        while (bytesRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(bytesRead, buffer.Length - bytesRead), cancellationToken);
            if (read == 0)
                break;
            bytesRead += read;
        }

        if (bytesRead > maximumBytes)
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, bytesRead), json_options);
        }
        finally
        {
            Array.Clear(buffer, 0, bytesRead);
        }
    }

    private async Task<OsuBestScoresFetchStatus?> validateScoreSessionAsync(
        LazerSessionState startingState,
        LazerAccessTokenLease lease,
        OsuProfile profile,
        CancellationToken cancellationToken)
    {
        try
        {
            await session.RefreshAsync(cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            return OsuBestScoresFetchStatus.SessionUnavailable;
        }

        return isSameSession(startingState, lease, profile) ? null : OsuBestScoresFetchStatus.SessionChanged;
    }

    private bool isSameSession(LazerSessionState startingState, LazerAccessTokenLease lease, OsuProfile profile) =>
        session.Current.Revision == startingState.Revision &&
        string.Equals(session.Current.Username, profile.Username, StringComparison.OrdinalIgnoreCase) &&
        lease.TryGetAccessToken(out _);

    private async Task<OsuBestScoresCacheDocument?> readScoreCacheAsync(int userId, CancellationToken cancellationToken, string category = "best")
    {
        string path = getScoreCachePath(userId, category);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > maximum_scores_response_bytes)
                return null;

            OsuBestScoresCacheDocument? document = await JsonSerializer.DeserializeAsync<OsuBestScoresCacheDocument>(
                stream,
                json_options,
                cancellationToken);
            if (document is null || document.SchemaVersion != score_cache_schema_version ||
                document.ApiVersion != osu_api_version || document.UserId != userId ||
                document.ExpiresAt <= timeProvider.GetUtcNow() || document.FetchedAt > document.ExpiresAt ||
                document.Scores is null ||
                document.Scores.Count > scores_page_size * maximum_score_pages ||
                document.Scores.Any(score => score is null || score.UserId != userId))
            {
                return null;
            }

            return document;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or IOException or
                                           UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private async Task writeScoreCacheAsync(OsuBestScoresCacheDocument document, CancellationToken cancellationToken, string category = "best")
    {
        Directory.CreateDirectory(scoreCacheDirectory);
        string destination = getScoreCachePath(document.UserId, category);
        string temporary = Path.Combine(scoreCacheDirectory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document, json_options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task tryWriteScoreCacheAsync(OsuBestScoresCacheDocument document, CancellationToken cancellationToken, string category = "best")
    {
        try
        {
            await writeScoreCacheAsync(document, cancellationToken, category);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A cache failure must not discard authoritative PP already returned by osu!.
        }
    }

    private string getScoreCachePath(int userId, string category = "best") =>
        Path.Combine(scoreCacheDirectory, $"{category}-scores-{osu_api_version}-user-{userId}.json");

    private async Task<OsuUserBeatmapScoresCacheDocument?> readBeatmapScoreCacheAsync(int userId, int beatmapId, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(getBeatmapScoreCachePath(userId, beatmapId), FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > maximum_scores_response_bytes)
                return null;
            OsuUserBeatmapScoresCacheDocument? document = await JsonSerializer.DeserializeAsync<OsuUserBeatmapScoresCacheDocument>(stream, json_options, cancellationToken);
            return document is not null && document.SchemaVersion == score_cache_schema_version &&
                   document.ApiVersion == osu_api_version && document.UserId == userId && document.BeatmapId == beatmapId &&
                   document.ExpiresAt > timeProvider.GetUtcNow() && document.FetchedAt <= document.ExpiresAt &&
                   document.Scores is not null && document.Scores.All(score => score is not null && score.UserId == userId)
                ? document
                : null;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private async Task tryWriteBeatmapScoreCacheAsync(OsuUserBeatmapScoresCacheDocument document, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(scoreCacheDirectory);
        string destination = getBeatmapScoreCachePath(document.UserId, document.BeatmapId);
        string temporary = Path.Combine(scoreCacheDirectory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document, json_options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            File.Move(temporary, destination, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private string getBeatmapScoreCachePath(int userId, int beatmapId) =>
        Path.Combine(scoreCacheDirectory, $"beatmap-scores-{osu_api_version}-user-{userId}-beatmap-{beatmapId}.json");

    private static string getDefaultCacheDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AimMod",
        "cache",
        "osu-api");

    private static OsuBestScore? parseBestScore(BestScoreResponse item, OsuProfile profile)
    {
        if (item.Id <= 0 || item.UserId != profile.UserId || item.Beatmap is null || item.Beatmapset is null ||
            item.Beatmap.Id <= 0 || item.Beatmapset.Id <= 0 || string.IsNullOrWhiteSpace(item.Beatmap.Version) ||
            string.IsNullOrWhiteSpace(item.Beatmapset.Title) || string.IsNullOrWhiteSpace(item.Beatmapset.Artist) ||
            string.IsNullOrWhiteSpace(item.Beatmapset.Creator) || item.Accuracy is < 0 or > 1 ||
            item.MaxCombo < 0 || item.Statistics is null || item.Statistics.HasNegativeValue ||
            item.Beatmap.DifficultyRating < 0)
        {
            return null;
        }

        if (item.Mods.ValueKind != JsonValueKind.Array)
            return null;

        string[] mods = parseMods(item.Mods);
        if (mods.Length != item.Mods.GetArrayLength())
            return null;

        return new OsuBestScore(
            item.Id,
            item.UserId,
            profile.Username,
            item.Pp,
            item.Accuracy,
            item.Score,
            item.MaxCombo,
            new OsuScoreStatistics(
                item.Statistics.CountMiss,
                item.Statistics.Count300,
                item.Statistics.Count100,
                item.Statistics.Count50),
            mods,
            item.Mods.GetRawText(),
            item.EndedAt,
            item.CreatedAt,
            new OsuScoreBeatmap(
                item.Beatmap.Id,
                item.Beatmap.Checksum,
                item.Beatmap.Version,
                item.Beatmap.DifficultyRating,
                item.Beatmap.MaxCombo,
                item.Beatmap.Bpm,
                item.Beatmap.TotalLength),
            new OsuScoreBeatmapSet(
                item.Beatmapset.Id,
                item.Beatmapset.Title,
                item.Beatmapset.TitleUnicode,
                item.Beatmapset.Artist,
                item.Beatmapset.ArtistUnicode,
                item.Beatmapset.Creator,
                item.Beatmapset.Source,
                item.Beatmapset.Status,
                parseHttpsUri(item.Beatmapset.Covers?.Cover)));
    }

    private static OsuUserBeatmapScore? parseUserBeatmapScore(BestScoreResponse item, int userId)
    {
        if (item.Id <= 0 || item.UserId != userId || item.Accuracy is < 0 or > 1 || item.MaxCombo < 0 ||
            item.Statistics is null || item.Statistics.HasNegativeValue || item.Mods.ValueKind != JsonValueKind.Array)
            return null;
        string[] mods = parseMods(item.Mods);
        if (mods.Length != item.Mods.GetArrayLength())
            return null;
        return new OsuUserBeatmapScore(
            item.Id,
            item.UserId,
            item.Pp,
            item.Accuracy,
            item.Score,
            item.MaxCombo,
            new OsuScoreStatistics(item.Statistics.CountMiss, item.Statistics.Count300, item.Statistics.Count100, item.Statistics.Count50),
            mods,
            item.Mods.GetRawText(),
            item.EndedAt,
            item.CreatedAt);
    }

    private static string[] parseMods(JsonElement mods)
    {
        if (mods.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<string>();
        foreach (JsonElement mod in mods.EnumerateArray())
        {
            string? acronym = mod.ValueKind switch
            {
                JsonValueKind.String => mod.GetString(),
                JsonValueKind.Object when mod.TryGetProperty("acronym", out JsonElement value) && value.ValueKind == JsonValueKind.String => value.GetString(),
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(acronym))
                return [];
            result.Add(acronym);
        }

        return result.ToArray();
    }

    private static Uri? parseAvatarUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            return null;

        return uri;
    }

    private static Uri? parseHttpsUri(string? value) => parseAvatarUrl(value);

    private sealed class BestScoreResponse
    {
        public long Id { get; init; }

        [JsonPropertyName("user_id")]
        public int UserId { get; init; }

        public double? Pp { get; init; }
        public double Accuracy { get; init; }
        public long Score { get; init; }

        [JsonPropertyName("max_combo")]
        public int MaxCombo { get; init; }

        public ScoreStatisticsResponse? Statistics { get; init; }
        public JsonElement Mods { get; init; }

        [JsonPropertyName("ended_at")]
        public DateTimeOffset? EndedAt { get; init; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset? CreatedAt { get; init; }

        public ScoreBeatmapResponse? Beatmap { get; init; }
        public ScoreBeatmapSetResponse? Beatmapset { get; init; }
    }

    private sealed class UserBeatmapScoresResponse
    {
        public List<BestScoreResponse>? Scores { get; init; }
    }

    private sealed class ScoreStatisticsResponse
    {
        [JsonPropertyName("count_miss")]
        public int CountMiss { get; init; }

        [JsonPropertyName("count_300")]
        public int Count300 { get; init; }

        [JsonPropertyName("count_100")]
        public int Count100 { get; init; }

        [JsonPropertyName("count_50")]
        public int Count50 { get; init; }

        [JsonIgnore]
        public bool HasNegativeValue => CountMiss < 0 || Count300 < 0 || Count100 < 0 || Count50 < 0;
    }

    private sealed class ScoreBeatmapResponse
    {
        public int Id { get; init; }
        public string? Checksum { get; init; }
        public string? Version { get; init; }

        [JsonPropertyName("difficulty_rating")]
        public double DifficultyRating { get; init; }

        [JsonPropertyName("max_combo")]
        public int? MaxCombo { get; init; }

        public double Bpm { get; init; }

        [JsonPropertyName("total_length")]
        public int TotalLength { get; init; }
    }

    private sealed class ScoreBeatmapSetResponse
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
        public ScoreCoversResponse? Covers { get; init; }
    }

    private sealed class ScoreCoversResponse
    {
        public string? Cover { get; init; }
    }

    private sealed class ProfileResponse
    {
        public int Id { get; init; }
        public string Username { get; init; } = string.Empty;

        [JsonPropertyName("country_code")]
        public string? CountryCode { get; init; }

        [JsonPropertyName("avatar_url")]
        public string? AvatarUrl { get; init; }

        public StatisticsResponse? Statistics { get; init; }
    }

    private sealed class StatisticsResponse
    {
        [JsonPropertyName("global_rank")]
        public int? GlobalRank { get; init; }

        [JsonPropertyName("country_rank")]
        public int? CountryRank { get; init; }

        [JsonPropertyName("pp")]
        public double? PerformancePoints { get; init; }

        [JsonPropertyName("hit_accuracy")]
        public double? HitAccuracy { get; init; }

        [JsonPropertyName("play_count")]
        public int PlayCount { get; init; }

        [JsonPropertyName("play_time")]
        public int PlayTime { get; init; }

        [JsonPropertyName("ranked_score")]
        public long RankedScore { get; init; }

        [JsonPropertyName("total_score")]
        public long TotalScore { get; init; }

        [JsonPropertyName("total_hits")]
        public long TotalHits { get; init; }

        [JsonPropertyName("maximum_combo")]
        public int MaximumCombo { get; init; }
    }
}

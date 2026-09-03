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
    private static readonly Uri search_endpoint = new("https://osu.ppy.sh/api/v2/beatmapsets/search", UriKind.Absolute);
    private static readonly JsonSerializerOptions json_options = new(JsonSerializerDefaults.Web);

    private readonly LazerSessionMonitor session;
    private readonly HttpClient httpClient;

    public OfficialBeatmapDiscoveryClient(LazerSessionMonitor session)
        : this(session, OfficialOsuApiClient.CreateProductionHandler())
    {
    }

    internal OfficialBeatmapDiscoveryClient(LazerSessionMonitor session, HttpMessageHandler handler)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentNullException.ThrowIfNull(handler);
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
        LazerSessionState startingState = session.Current;
        using LazerAccessTokenLease? lease = session.TryLeaseAccessToken();

        if (lease is null || !lease.TryGetAccessToken(out string accessToken))
            return OfficialBeatmapSearchResult.Empty(withoutToken(startingState.Status));

        using var request = new HttpRequestMessage(HttpMethod.Get, buildSearchUri(normalised));
        addJsonHeaders(request, accessToken);

        try
        {
            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            OfficialBeatmapRequestStatus? sessionFailure = await validateSessionAsync(startingState, lease, cancellationToken).ConfigureAwait(false);
            if (sessionFailure is not null)
                return OfficialBeatmapSearchResult.Empty(sessionFailure.Value);

            OfficialBeatmapRequestStatus? responseFailure = classifyFailure(response);
            if (responseFailure is not null)
                return OfficialBeatmapSearchResult.Empty(responseFailure.Value);

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
                payload.BeatmapSets.Count > sets.Length || payload.Total > sets.Length);
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

        LazerSessionState startingState = session.Current;
        using LazerAccessTokenLease? lease = session.TryLeaseAccessToken();
        if (lease is null || !lease.TryGetAccessToken(out string accessToken))
            return new OfficialBeatmapDownloadResult(withoutToken(startingState.Status));

        Directory.CreateDirectory(destinationDirectory);
        string archivePath = Path.Combine(destinationDirectory, $"aimmod-{beatmapSetId}-{Guid.NewGuid():N}.osz");

        try
        {
            Uri uri = new($"https://osu.ppy.sh/api/v2/beatmapsets/{beatmapSetId}/download{(noVideo ? "?noVideo=1" : string.Empty)}");
            using HttpResponseMessage response = await sendDownloadRequestAsync(uri, accessToken, cancellationToken).ConfigureAwait(false);

            OfficialBeatmapRequestStatus? sessionFailure = await validateSessionAsync(startingState, lease, cancellationToken).ConfigureAwait(false);
            if (sessionFailure is not null)
                return new OfficialBeatmapDownloadResult(sessionFailure.Value);

            OfficialBeatmapRequestStatus? responseFailure = classifyFailure(response);
            if (responseFailure is not null)
                return new OfficialBeatmapDownloadResult(responseFailure.Value);
            if (response.Content.Headers.ContentLength is > maximum_archive_bytes)
                return new OfficialBeatmapDownloadResult(OfficialBeatmapRequestStatus.InvalidResponse);

            long bytesWritten = await copyBoundedAsync(response.Content, archivePath, cancellationToken).ConfigureAwait(false);
            if (bytesWritten <= 0 || !isBeatmapArchive(archivePath))
            {
                deleteIfPresent(archivePath);
                return new OfficialBeatmapDownloadResult(OfficialBeatmapRequestStatus.InvalidResponse);
            }

            if (!lease.TryGetAccessToken(out _))
            {
                deleteIfPresent(archivePath);
                return new OfficialBeatmapDownloadResult(OfficialBeatmapRequestStatus.SessionChanged);
            }

            return new OfficialBeatmapDownloadResult(OfficialBeatmapRequestStatus.Success, archivePath, bytesWritten);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            deleteIfPresent(archivePath);
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            deleteIfPresent(archivePath);
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
        string beatmapPath = Path.Combine(destinationDirectory, $"aimmod-{beatmapId}-{Guid.NewGuid():N}.osu");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://osu.ppy.sh/osu/{beatmapId}"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            OfficialBeatmapRequestStatus? failure = classifyFailure(response);
            if (failure is not null)
                return new OfficialBeatmapDifficultyDownloadResult(failure.Value, beatmapId);
            if (response.Content.Headers.ContentLength is > maximum_difficulty_bytes)
                return new OfficialBeatmapDifficultyDownloadResult(OfficialBeatmapRequestStatus.InvalidResponse, beatmapId);

            long bytesWritten = await copyBoundedAsync(
                response.Content,
                beatmapPath,
                maximum_difficulty_bytes,
                "The beatmap difficulty exceeds AimMod's download limit.",
                cancellationToken).ConfigureAwait(false);
            if (bytesWritten <= 0 || !isExpectedDifficulty(beatmapPath, beatmapId))
            {
                deleteIfPresent(beatmapPath);
                return new OfficialBeatmapDifficultyDownloadResult(OfficialBeatmapRequestStatus.InvalidResponse, beatmapId);
            }

            return new OfficialBeatmapDifficultyDownloadResult(
                OfficialBeatmapRequestStatus.Success,
                beatmapId,
                beatmapPath,
                bytesWritten);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            deleteIfPresent(beatmapPath);
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
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

        throw new InvalidOperationException("The redirect limit was not enforced.");
    }

    private async Task<OfficialBeatmapRequestStatus?> validateSessionAsync(
        LazerSessionState startingState,
        LazerAccessTokenLease lease,
        CancellationToken cancellationToken)
    {
        try
        {
            await session.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return OfficialBeatmapRequestStatus.SessionUnavailable;
        }

        return session.Current.Revision != startingState.Revision || !lease.TryGetAccessToken(out _)
            ? OfficialBeatmapRequestStatus.SessionChanged
            : null;
    }

    private static OfficialBeatmapRequestStatus? classifyFailure(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return OfficialBeatmapRequestStatus.Unauthorized;
        if ((int)response.StatusCode is >= 300 and < 400)
            return OfficialBeatmapRequestStatus.InvalidResponse;
        if (!response.IsSuccessStatusCode)
            return OfficialBeatmapRequestStatus.ServerError;
        return null;
    }

    private static Uri buildSearchUri(OfficialBeatmapSearchQuery query)
    {
        var parameters = new Dictionary<string, string>
        {
            ["q"] = query.SearchText,
            ["m"] = "0",
            ["s"] = query.Category.ToString().ToLowerInvariant(),
            ["sort"] = $"{(query.Sort == OfficialBeatmapSort.Relevance && query.SearchText.Length == 0 ? OfficialBeatmapSort.Ranked : query.Sort).ToString().ToLowerInvariant()}_desc",
            ["nsfw"] = query.IncludeExplicitContent ? "true" : "false",
        };
        string encoded = string.Join("&", parameters.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new UriBuilder(search_endpoint) { Query = encoded }.Uri;
    }

    private static void addJsonHeaders(HttpRequestMessage request, string accessToken)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    private static async Task<T?> readJsonPayloadAsync<T>(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maximumBytes)
            return default;

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] buffer = new byte[maximumBytes + 1];
        int bytesRead = 0;
        while (bytesRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(bytesRead, buffer.Length - bytesRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            bytesRead += read;
        }

        if (bytesRead > maximumBytes)
            return default;
        return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, bytesRead), json_options);
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

    private static Task<long> copyBoundedAsync(HttpContent content, string path, CancellationToken cancellationToken) =>
        copyBoundedAsync(content, path, maximum_archive_bytes, "The beatmap archive exceeds AimMod's download limit.", cancellationToken);

    private static async Task<long> copyBoundedAsync(
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
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
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

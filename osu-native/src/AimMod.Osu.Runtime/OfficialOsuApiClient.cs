using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AimMod.Osu.Runtime;

public sealed class OfficialOsuApiClient : IDisposable
{
    private const int maximum_response_bytes = 1024 * 1024;
    private static readonly Uri profile_endpoint = new("https://osu.ppy.sh/api/v2/me/osu", UriKind.Absolute);
    private static readonly JsonSerializerOptions json_options = new(JsonSerializerDefaults.Web);

    private readonly LazerSessionMonitor session;
    private readonly HttpClient httpClient;

    public OfficialOsuApiClient(LazerSessionMonitor session)
        : this(session, CreateProductionHandler())
    {
    }

    internal OfficialOsuApiClient(LazerSessionMonitor session, HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(handler);

        this.session = session;
        httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
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

            ProfileResponse? payload = await readPayloadAsync(response, cancellationToken);
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

    private static async Task<ProfileResponse?> readPayloadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maximum_response_bytes)
            return null;

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        byte[] buffer = new byte[maximum_response_bytes + 1];
        int bytesRead = 0;

        while (bytesRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(bytesRead, buffer.Length - bytesRead), cancellationToken);
            if (read == 0)
                break;
            bytesRead += read;
        }

        if (bytesRead > maximum_response_bytes)
            return null;

        try
        {
            return JsonSerializer.Deserialize<ProfileResponse>(buffer.AsSpan(0, bytesRead), json_options);
        }
        finally
        {
            Array.Clear(buffer, 0, bytesRead);
        }
    }

    private static Uri? parseAvatarUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            return null;

        return uri;
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

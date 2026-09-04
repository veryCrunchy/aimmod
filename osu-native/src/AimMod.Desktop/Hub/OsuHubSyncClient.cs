using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace AimMod.Desktop.Hub;

public sealed record OsuHubUploadResult(
    string ShareId,
    Uri ShareUri,
    string Visibility,
    bool MetadataCreated,
    bool ReplayUploaded,
    bool FromLocalCache);

public interface IOsuHubUploader
{
    Task<OsuHubUploadResult> UploadAsync(
        OsuHubSyncRequest request,
        string? replayPath = null,
        CancellationToken cancellationToken = default);
}

public sealed class OsuHubSyncClient : IOsuHubUploader
{
    public static readonly Uri DefaultBaseUri = new("https://aimmod.app/");
    public static readonly TimeSpan LocalDeduplicationFreshness = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions json_options = new(JsonSerializerDefaults.Web);

    private readonly HttpClient client;
    private readonly Uri baseUri;
    private readonly IHubCredentialStore credentials;
    private readonly IOsuHubSyncCache cache;

    public OsuHubSyncClient(HttpClient client, IHubCredentialStore credentials, IOsuHubSyncCache cache, Uri? baseUri = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        Uri configured = baseUri ?? DefaultBaseUri;
        if (!configured.IsAbsoluteUri || (configured.Scheme != Uri.UriSchemeHttps && configured.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("Hub URL must be an absolute HTTP(S) URL.", nameof(baseUri));
        this.baseUri = new Uri(configured.ToString().TrimEnd('/') + "/", UriKind.Absolute);
    }

    public async Task<OsuHubUploadResult> UploadAsync(
        OsuHubSyncRequest request,
        string? replayPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        HubCredential credential = credentials.Load()
                                   ?? throw new InvalidOperationException("AimMod is not linked to an AimMod Hub account.");

        OsuHubSyncCacheEntry? cached = cache.Find(request.ContentHash);
        bool replayAlreadyUploaded = request.Replay is null
                                     || !request.Replay.UploadFile
                                     || cached is { ReplayUploaded: true }
                                     && string.Equals(cached.ReplaySha256, request.Replay.Sha256, StringComparison.OrdinalIgnoreCase);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (cached is not null
            && cached.SyncedAt <= now
            && now - cached.SyncedAt <= LocalDeduplicationFreshness
            && string.Equals(cached.Visibility, request.Visibility, StringComparison.Ordinal)
            && replayAlreadyUploaded)
        {
            return resultFromCache(cached);
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "api/osu/v1/sync"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.UploadToken);
        message.Headers.TryAddWithoutValidation("Idempotency-Key", request.ClientUploadId);
        message.Content = JsonContent.Create(request, options: json_options);
        using HttpResponseMessage response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await ensureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        OsuHubSyncResponse payload = await response.Content.ReadFromJsonAsync<OsuHubSyncResponse>(json_options, cancellationToken).ConfigureAwait(false)
                                     ?? throw new InvalidDataException("Hub returned an empty osu sync response.");

        bool replayUploaded = cached?.ReplayUploaded == true
                              && request.Replay is not null
                              && string.Equals(cached.ReplaySha256, request.Replay.Sha256, StringComparison.OrdinalIgnoreCase);
        if (payload.ReplayUploadRequired)
        {
            if (request.Replay is null || !request.Replay.UploadFile)
                throw new InvalidDataException("Hub requested replay bytes that were not enabled in the upload contract.");
            if (string.IsNullOrWhiteSpace(replayPath) || !File.Exists(replayPath))
                throw new FileNotFoundException("The replay file selected for Hub upload is unavailable.", replayPath);
            replayUploaded = await uploadReplayAsync(payload.ShareId, request.Replay, replayPath, credential.UploadToken, cancellationToken).ConfigureAwait(false);
        }

        var entry = new OsuHubSyncCacheEntry(
            request.ContentHash,
            payload.Visibility,
            payload.ShareId,
            request.Replay?.Sha256 ?? "",
            replayUploaded,
            DateTimeOffset.UtcNow);
        await cache.SaveAsync(entry, cancellationToken).ConfigureAwait(false);
        return new OsuHubUploadResult(
            payload.ShareId,
            new Uri(baseUri, "osu/replays/" + payload.ShareId),
            payload.Visibility,
            payload.Created,
            replayUploaded,
            false);
    }

    private async Task<bool> uploadReplayAsync(
        string shareId,
        OsuHubReplay replay,
        string replayPath,
        string token,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(replayPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > 64L * 1024 * 1024)
            throw new InvalidDataException("Replay exceeds the Hub 64 MiB upload limit.");
        byte[] actualHash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        string actualHashText = Convert.ToHexString(actualHash).ToLowerInvariant();
        if (!string.Equals(actualHashText, replay.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Replay changed after its upload metadata was created.");
        stream.Position = 0;

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, $"api/osu/v1/replays/{Uri.EscapeDataString(shareId)}/file"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Headers.TryAddWithoutValidation("X-Content-SHA256", replay.Sha256);
        message.Content = new StreamContent(stream);
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-osu-replay");
        message.Content.Headers.ContentLength = stream.Length;
        using HttpResponseMessage response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await ensureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private OsuHubUploadResult resultFromCache(OsuHubSyncCacheEntry entry) => new(
        entry.ShareId,
        new Uri(baseUri, "osu/replays/" + entry.ShareId),
        entry.Visibility,
        false,
        entry.ReplayUploaded,
        true);

    private static async Task ensureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException($"Hub request failed ({(int)response.StatusCode}): {body[..Math.Min(body.Length, 512)]}", null, response.StatusCode);
    }
}

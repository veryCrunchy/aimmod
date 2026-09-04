using System.Collections.Concurrent;
using System.Net;

namespace AimMod.Desktop.Skins.Online;

public sealed record SkinHttpFetchOptions(
    IReadOnlyCollection<string> AllowedHosts,
    IReadOnlyCollection<string> AllowedContentTypes,
    long MaximumBytes,
    TimeSpan Timeout,
    int MaximumRedirects = 5);

public sealed record SkinHttpPayload(byte[] Bytes, Uri FinalUri, string ContentType);

public sealed record SkinHttpFile(string Path, long Length, Uri FinalUri, string ContentType);

public sealed class SkinHttpException : Exception
{
    public SkinHttpException(string code, string message, Uri? redirectUri = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        RedirectUri = redirectUri;
    }

    public string Code { get; }
    public Uri? RedirectUri { get; }
}

public interface ISkinHttpTransport
{
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken);
}

public interface ISecureSkinHttpClient
{
    Task<SkinHttpPayload> GetBytesAsync(Uri uri, SkinHttpFetchOptions options, CancellationToken cancellationToken = default);
    Task<SkinHttpFile> DownloadAsync(Uri uri, string destinationPath, SkinHttpFetchOptions options, CancellationToken cancellationToken = default);
}

public sealed class SecureSkinHttpClient : ISecureSkinHttpClient, IDisposable
{
    private static readonly TimeSpan minimum_request_interval = TimeSpan.FromMilliseconds(750);

    private readonly ISkinHttpTransport transport;
    private readonly IDisposable? ownedTransport;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> hostLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> lastRequests = new(StringComparer.OrdinalIgnoreCase);

    public SecureSkinHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseCookies = false,
        };
        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AimMod/0.1 (+https://github.com/veryCrunchy/aimmod)");
        var httpTransport = new HttpClientSkinTransport(client);
        transport = httpTransport;
        ownedTransport = httpTransport;
    }

    internal SecureSkinHttpClient(ISkinHttpTransport transport)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public async Task<SkinHttpPayload> GetBytesAsync(
        Uri uri,
        SkinHttpFetchOptions options,
        CancellationToken cancellationToken = default)
    {
        await using var memory = new MemoryStream();
        SkinHttpTransfer transfer = await fetchAsync(uri, memory, options, cancellationToken).ConfigureAwait(false);
        return new SkinHttpPayload(memory.ToArray(), transfer.FinalUri, transfer.ContentType);
    }

    public async Task<SkinHttpFile> DownloadAsync(
        Uri uri,
        string destinationPath,
        SkinHttpFetchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (!Path.IsPathFullyQualified(destinationPath))
            throw new ArgumentException("The download destination must be absolute.", nameof(destinationPath));

        string fullPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        try
        {
            await using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous);
            SkinHttpTransfer transfer = await fetchAsync(uri, stream, options, cancellationToken).ConfigureAwait(false);
            return new SkinHttpFile(fullPath, transfer.Length, transfer.FinalUri, transfer.ContentType);
        }
        catch
        {
            try
            {
                File.Delete(fullPath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
            throw;
        }
    }

    private async Task<SkinHttpTransfer> fetchAsync(
        Uri initialUri,
        Stream destination,
        SkinHttpFetchOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initialUri);
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));

        var allowedHosts = new HashSet<string>(options.AllowedHosts.Select(normalizeHost), StringComparer.OrdinalIgnoreCase);
        Uri current = validateUri(initialUri, allowedHosts, "url_rejected");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);

        try
        {
            for (int redirect = 0; redirect <= options.MaximumRedirects; redirect++)
            {
                await waitForRateLimit(current.Host, timeout.Token).ConfigureAwait(false);
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using HttpResponseMessage response = await transport.SendAsync(request, timeout.Token).ConfigureAwait(false);
                Uri responseUri = response.RequestMessage?.RequestUri ?? current;
                validateUri(responseUri, allowedHosts, "response_host_rejected");

                if (isRedirect(response.StatusCode))
                {
                    if (redirect == options.MaximumRedirects)
                        throw new SkinHttpException("too_many_redirects", "The skin download redirected too many times.");
                    Uri next = resolveRedirect(responseUri, response.Headers.Location);
                    try
                    {
                        current = validateUri(next, allowedHosts, "redirect_host_rejected");
                    }
                    catch (SkinHttpException error) when (error.Code == "redirect_host_rejected")
                    {
                        throw new SkinHttpException(error.Code, error.Message, next, error);
                    }
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    throw new SkinHttpException("http_error", $"The remote server returned HTTP {(int)response.StatusCode}.");

                string contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? string.Empty;
                if (!options.AllowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
                    throw new SkinHttpException("unexpected_content_type", $"The server returned unsupported content type '{contentType}'.");
                if (response.Content.Headers.ContentLength is long declared && declared > options.MaximumBytes)
                    throw new SkinHttpException("payload_too_large", "The skin download exceeds the configured size limit.");

                await using Stream source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                byte[] buffer = new byte[81_920];
                long total = 0;
                while (true)
                {
                    int read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    total += read;
                    if (total > options.MaximumBytes)
                        throw new SkinHttpException("payload_too_large", "The skin download exceeds the configured size limit.");
                    await destination.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                }
                return new SkinHttpTransfer(total, responseUri, contentType);
            }
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SkinHttpException("timeout", "The skin request timed out.", innerException: error);
        }
        catch (HttpRequestException error)
        {
            throw new SkinHttpException("network_error", "The skin request failed.", innerException: error);
        }

        throw new SkinHttpException("too_many_redirects", "The skin download redirected too many times.");
    }

    private async Task waitForRateLimit(string host, CancellationToken cancellationToken)
    {
        SemaphoreSlim gate = hostLocks.GetOrAdd(host, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (lastRequests.TryGetValue(host, out DateTimeOffset last))
            {
                TimeSpan remaining = minimum_request_interval - (DateTimeOffset.UtcNow - last);
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
            }
            lastRequests[host] = DateTimeOffset.UtcNow;
        }
        finally
        {
            gate.Release();
        }
    }

    private static Uri validateUri(Uri uri, IReadOnlySet<string> allowedHosts, string code)
    {
        if (!uri.IsAbsoluteUri
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || uri.Port != 443
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !allowedHosts.Contains(normalizeHost(uri.Host)))
            throw new SkinHttpException(code, $"The host '{uri.Host}' is not approved for this skin request.");
        return uri;
    }

    private static string normalizeHost(string host) => host.Trim().TrimEnd('.').ToLowerInvariant();

    private static bool isRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static Uri resolveRedirect(Uri current, Uri? location)
    {
        if (location is null)
            throw new SkinHttpException("invalid_redirect", "The server returned a redirect without a destination.");
        return location.IsAbsoluteUri ? location : new Uri(current, location);
    }

    public void Dispose() => ownedTransport?.Dispose();

    private sealed record SkinHttpTransfer(long Length, Uri FinalUri, string ContentType);

    private sealed class HttpClientSkinTransport(HttpClient client) : ISkinHttpTransport, IDisposable
    {
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        public void Dispose() => client.Dispose();
    }
}

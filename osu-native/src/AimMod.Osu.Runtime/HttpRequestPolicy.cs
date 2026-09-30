using System.Buffers;
using System.Net;
using System.Net.Http.Headers;

namespace AimMod.Osu.Runtime;

/// <summary>
/// Shared HTTP behaviour for the official osu! clients: Retry-After parsing, the cooldown
/// applied after a 429, and the single jittered retry for transient server errors.
/// </summary>
internal static class HttpRequestPolicy
{
    public static readonly TimeSpan DefaultRateLimitCooldown = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MinimumRateLimitCooldown = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaximumRateLimitCooldown = TimeSpan.FromMinutes(10);

    public static bool IsRateLimited(HttpStatusCode statusCode) => statusCode == HttpStatusCode.TooManyRequests;

    public static bool IsTransientServerError(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    public static TimeSpan? ParseRetryAfter(RetryConditionHeaderValue? header, DateTimeOffset now)
    {
        if (header is null)
            return null;
        if (header.Delta is { } delta)
            return delta;
        if (header.Date is { } date)
            return date - now;
        return null;
    }

    public static TimeSpan ClampCooldown(TimeSpan? requested)
    {
        TimeSpan value = requested ?? DefaultRateLimitCooldown;
        if (value < MinimumRateLimitCooldown)
            return MinimumRateLimitCooldown;
        return value > MaximumRateLimitCooldown ? MaximumRateLimitCooldown : value;
    }

    public static TimeSpan RetryJitter() => TimeSpan.FromMilliseconds(Random.Shared.Next(150, 450));

    /// <summary>
    /// Sends a request and, if the server answers with a transient 5xx, sends one fresh copy
    /// after a short random delay. The original response is disposed before the retry.
    /// </summary>
    public static async Task<HttpResponseMessage> SendWithSingleRetryAsync(
        HttpClient client,
        Func<HttpRequestMessage> createRequest,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await client.SendAsync(
            createRequest(),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!IsTransientServerError(response.StatusCode))
            return response;

        response.Dispose();
        await Task.Delay(RetryJitter(), cancellationToken).ConfigureAwait(false);
        return await client.SendAsync(
            createRequest(),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// A response body read into a pooled buffer no larger than the body itself needs.
/// </summary>
internal readonly struct PooledBody(byte[] buffer, int length) : IDisposable
{
    public ReadOnlySpan<byte> Span => buffer.AsSpan(0, length);

    public void Dispose()
    {
        Array.Clear(buffer, 0, length);
        ArrayPool<byte>.Shared.Return(buffer);
    }

    public static readonly TimeSpan DefaultBodyTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Reads at most <paramref name="maximumBytes"/> bytes. Returns null when the body is larger.
    /// HttpClient.Timeout does not cover a body read after ResponseHeadersRead, so a body that
    /// does not finish within <paramref name="timeout"/> fails with <see cref="HttpRequestException"/>.
    /// </summary>
    public static async ValueTask<PooledBody?> ReadAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        using var bodyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bodyTimeout.CancelAfter(timeout ?? DefaultBodyTimeout);
        try
        {
            return await readAsync(content, maximumBytes, bodyTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && bodyTimeout.IsCancellationRequested)
        {
            throw new HttpRequestException("The response body did not arrive in time.", exception);
        }
    }

    private static async ValueTask<PooledBody?> readAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        long? declared = content.Headers.ContentLength;
        if (declared > maximumBytes)
            return null;

        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        int capacity = declared is { } known ? (int)Math.Min(known + 1, maximumBytes + 1) : Math.Min(16 * 1024, maximumBytes + 1);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, capacity));
        int length = 0;
        try
        {
            while (true)
            {
                if (length == buffer.Length)
                {
                    if (length > maximumBytes)
                    {
                        Array.Clear(buffer, 0, length);
                        ArrayPool<byte>.Shared.Return(buffer);
                        return null;
                    }

                    byte[] larger = ArrayPool<byte>.Shared.Rent((int)Math.Min((long)buffer.Length * 2, maximumBytes + 1L));
                    buffer.AsSpan(0, length).CopyTo(larger);
                    Array.Clear(buffer, 0, length);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }

                int limit = (int)Math.Min(buffer.Length, maximumBytes + 1L);
                if (length >= limit)
                {
                    Array.Clear(buffer, 0, length);
                    ArrayPool<byte>.Shared.Return(buffer);
                    return null;
                }

                int read = await stream.ReadAsync(buffer.AsMemory(length, limit - length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                length += read;
            }

            if (length > maximumBytes)
            {
                Array.Clear(buffer, 0, length);
                ArrayPool<byte>.Shared.Return(buffer);
                return null;
            }

            return new PooledBody(buffer, length);
        }
        catch
        {
            Array.Clear(buffer, 0, length);
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }
}

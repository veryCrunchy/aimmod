using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

namespace AimMod.InGame;

/// <summary>Status of a request sent as the linked account. Status 0: no response (network failure or timeout).</summary>
readonly record struct HubSendResult(int Status, TimeSpan? RetryAfter)
{
    public bool Ok => Status is >= 200 and < 300;
    /// <summary>The Hub refused the device's upload token (revoked or expired).</summary>
    public bool Unauthorized => Status is 401 or 403;
}

// Live activity and run uploads act as the linked account with the device's
// upload token, like tournaments. Their failures never change the history
// download state or its Retry-After: each caller keeps its own backoff.
sealed partial class Hub
{
    /// <summary>Changes whenever a different credential is linked or the account is unlinked.</summary>
    internal string AccountFingerprint
    {
        get
        {
            var current = account;
            return current is null ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(current.Handle + "\n" + current.Token)))[..16];
        }
    }

    /// <summary>Sends one request as the linked account; null when no account is linked.</summary>
    internal async Task<HubSendResult?> SendAsAccount(HttpMethod method, string path, object? json, CancellationToken token, bool connect = false)
    {
        var current = account;
        if (current is null) return null;
        using var request = new HttpRequestMessage(method, Origin + path);
        // A string is sent as already-serialized JSON.
        if (json is string text) request.Content = new StringContent(text, Encoding.UTF8, "application/json");
        else if (json is not null) request.Content = JsonContent.Create(json, json.GetType());
        if (connect) request.Headers.Add("Connect-Protocol-Version", "1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", current.Token);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            var retry = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - clock.GetUtcNow());
            // Drain a bounded amount so the connection can be reused; the body is never logged.
            using (var stream = await response.Content.ReadAsStreamAsync(token))
            {
                var buffer = new byte[4096]; var total = 0; int read;
                while (total < 65536 && (read = await stream.ReadAsync(buffer, token)) > 0) total += read;
            }
            return new((int)response.StatusCode, retry);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || ex is OperationCanceledException && !token.IsCancellationRequested)
        { return new(0, null); }
    }
}

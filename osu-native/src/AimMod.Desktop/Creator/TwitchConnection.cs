using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using AimMod.Desktop.Hub;

namespace AimMod.Desktop.Creator;

public sealed record TwitchAccount(string Id, string Login);
public sealed record TwitchDeviceCode(string Code, string UserCode, Uri VerificationUri, DateTimeOffset ExpiresAt, int IntervalSeconds)
{
    public override string ToString() => "Twitch device authorization";
}

public sealed record TwitchCredential(string ClientId, string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, TwitchAccount? Account)
{
    public override string ToString() => "Twitch credentials (private)";
}

public interface ITwitchCredentialStore
{
    Task<TwitchCredential?> LoadAsync(CancellationToken token);
    Task SaveAsync(TwitchCredential value, CancellationToken token);
    Task ClearAsync(CancellationToken token);
}

public sealed class TwitchCredentialStore(string path, IHubSecretProtector? protector = null) : ITwitchCredentialStore
{
    private readonly IHubSecretProtector protector = protector ?? PlatformHubSecretProtector.Instance;
    public async Task<TwitchCredential?> LoadAsync(CancellationToken token)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("The Twitch connection could not be read.");
        byte[] payload = this.protector.Unprotect(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false));
        try { return JsonSerializer.Deserialize<TwitchCredential>(payload); }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }
    public async Task SaveAsync(TwitchCredential value, CancellationToken token)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value);
        byte[] encrypted;
        try { encrypted = this.protector.Protect(payload); }
        finally { CryptographicOperations.ZeroMemory(payload); }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Unix permissions are applied when the file is created, before any
            // token bytes are written. Windows uses per-user data protection.
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temp, options))
                await stream.WriteAsync(encrypted, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temp, path, true);
        }
        finally { CryptographicOperations.ZeroMemory(encrypted); if (File.Exists(temp)) File.Delete(temp); }
    }
    public Task ClearAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); File.Delete(path); return Task.CompletedTask; }
}

public sealed class TwitchConnection : IDisposable
{
    private readonly HttpClient http;
    private readonly string clientId;
    private readonly ITwitchCredentialStore store;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private TwitchCredential? credential;
    private bool loaded;
    private DateTimeOffset validatedAt;
    public bool IsConfigured => clientId.Length > 0;

    public TwitchConnection(string clientId, ITwitchCredentialStore store, HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        this.clientId = clientId.Trim(); this.store = store; this.clock = clock ?? TimeProvider.System;
        http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<TwitchAccount?> SavedAccountAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { await load(token).ConfigureAwait(false); return credential?.Account; }
        finally { gate.Release(); }
    }

    public async Task<TwitchDeviceCode> BeginAsync(CancellationToken token)
    {
        if (!IsConfigured) throw new InvalidOperationException("Twitch connections are not configured in this build. You can still add a VOD manually.");
        using var request = form("device", new() { ["client_id"] = clientId, ["scopes"] = "" });
        var reply = await SendAsync(http, request, token).ConfigureAwait(false);
        ensure(reply.Status);
        JsonElement body = reply.Body;
        string code = required(body, "device_code"), userCode = required(body, "user_code");
        if (!Uri.TryCreate(required(body, "verification_uri"), UriKind.Absolute, out Uri? uri)
            || uri.Scheme != "https" || uri.Host is not ("www.twitch.tv" or "twitch.tv")
            || uri.AbsolutePath != "/activate" || !uri.IsDefaultPort || uri.UserInfo.Length > 0)
            throw new InvalidDataException("Twitch returned an invalid sign-in address.");
        int expires = body.GetProperty("expires_in").GetInt32(), interval = body.GetProperty("interval").GetInt32();
        if (expires is < 1 or > 3600 || interval is < 1 or > 120) throw new InvalidDataException("Twitch returned an invalid sign-in window.");
        return new(code, userCode, uri, clock.GetUtcNow().AddSeconds(expires), interval);
    }

    public async Task<TwitchAccount> CompleteAsync(TwitchDeviceCode device, CancellationToken token)
    {
        int interval = device.IntervalSeconds;
        while (clock.GetUtcNow() < device.ExpiresAt)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), clock, token).ConfigureAwait(false);
            if (clock.GetUtcNow() >= device.ExpiresAt) break;
            using var request = form("token", new() { ["client_id"] = clientId, ["scopes"] = "",
                ["device_code"] = device.Code, ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code" });
            var reply = await SendAsync(http, request, token).ConfigureAwait(false);
            string error = optional(reply.Body, "message");
            if (error.Length == 0) error = optional(reply.Body, "error");
            if (reply.Status == HttpStatusCode.BadRequest && error == "authorization_pending") continue;
            if (error == "slow_down") { interval = Math.Min(120, interval + 5); continue; }
            if ((int)reply.Status >= 400) throw new InvalidOperationException("Twitch sign-in was declined or expired. Connect again to get a new code.");
            var candidate = parseCredential(reply.Body, null);
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                TwitchAccount account = await validate(candidate.AccessToken, token).ConfigureAwait(false);
                candidate = candidate with { Account = account };
                await store.SaveAsync(candidate, token).ConfigureAwait(false);
                credential = candidate; loaded = true; validatedAt = clock.GetUtcNow();
                return account;
            }
            finally { gate.Release(); }
        }
        throw new InvalidOperationException("The Twitch code expired. Connect again to get a new code.");
    }

    public async Task<string> AccessTokenAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await load(token).ConfigureAwait(false);
            if (credential is null) throw new InvalidOperationException("Connect Twitch to find past broadcasts.");
            if (credential.ExpiresAt <= clock.GetUtcNow().AddMinutes(1)) await refresh(token).ConfigureAwait(false);
            if (clock.GetUtcNow() - validatedAt >= TimeSpan.FromMinutes(55))
            {
                TwitchAccount account;
                try { account = await validate(credential!.AccessToken, token).ConfigureAwait(false); }
                catch (TwitchSessionExpiredException) { await refresh(token).ConfigureAwait(false); account = await validate(credential!.AccessToken, token).ConfigureAwait(false); }
                if (credential!.Account is { } previous && previous.Id != account.Id)
                    throw new InvalidOperationException("The connected Twitch account changed. Disconnect and connect it again.");
                credential = credential with { Account = account }; validatedAt = clock.GetUtcNow();
            }
            return credential!.AccessToken;
        }
        finally { gate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { await store.ClearAsync(token).ConfigureAwait(false); credential = null; loaded = true; validatedAt = default; }
        finally { gate.Release(); }
    }

    public void AddHeaders(HttpRequestMessage request, string token)
    {
        if (request.RequestUri?.Host != "api.twitch.tv" || request.RequestUri.Scheme != "https") throw new ArgumentException("Invalid Twitch endpoint.");
        request.Headers.Add("Client-Id", clientId); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task load(CancellationToken token)
    {
        if (loaded) return;
        credential = await store.LoadAsync(token).ConfigureAwait(false);
        if (credential?.ClientId != clientId) credential = null;
        loaded = true;
    }
    private async Task refresh(CancellationToken token)
    {
        if (string.IsNullOrEmpty(credential?.RefreshToken)) throw new TwitchSessionExpiredException();
        using var request = form("token", new() { ["client_id"] = clientId, ["refresh_token"] = credential.RefreshToken, ["grant_type"] = "refresh_token" });
        var reply = await SendAsync(http, request, token).ConfigureAwait(false);
        if (reply.Status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized) throw new TwitchSessionExpiredException();
        ensure(reply.Status);
        TwitchCredential next = parseCredential(reply.Body, credential.Account);
        // Device-flow refresh tokens are single-use. Persist the rotated pair
        // before any subsequent request or UI work.
        await store.SaveAsync(next, CancellationToken.None).ConfigureAwait(false);
        credential = next; validatedAt = default;
    }
    private TwitchCredential parseCredential(JsonElement body, TwitchAccount? account)
    {
        int seconds = body.GetProperty("expires_in").GetInt32();
        if (seconds is < 1 or > 60 * 24 * 3600) throw new InvalidDataException("Twitch returned an invalid connection expiry.");
        return new(clientId, required(body, "access_token"), required(body, "refresh_token"), clock.GetUtcNow().AddSeconds(seconds), account);
    }
    private async Task<TwitchAccount> validate(string accessToken, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate");
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", accessToken);
        var reply = await SendAsync(http, request, token).ConfigureAwait(false);
        if (reply.Status == HttpStatusCode.Unauthorized) throw new TwitchSessionExpiredException();
        ensure(reply.Status);
        if (required(reply.Body, "client_id") != clientId) throw new TwitchSessionExpiredException();
        return new(required(reply.Body, "user_id"), required(reply.Body, "login"));
    }
    private static HttpRequestMessage form(string endpoint, Dictionary<string, string> fields) => new(HttpMethod.Post, "https://id.twitch.tv/oauth2/" + endpoint)
    { Content = new FormUrlEncodedContent(fields) };
    private static string optional(JsonElement body, string key) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    internal static string required(JsonElement body, string key)
    {
        string value = optional(body, key);
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192) throw new InvalidDataException("Twitch returned incomplete information.");
        return value;
    }
    internal static void ensure(HttpStatusCode status)
    {
        if (status == HttpStatusCode.Unauthorized) throw new TwitchSessionExpiredException();
        if (status == HttpStatusCode.TooManyRequests) throw new InvalidOperationException("Twitch is limiting requests. Try again in a minute.");
        if ((int)status is < 200 or >= 300) throw new HttpRequestException("Twitch could not complete the request.");
    }
    internal static async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken token)
    {
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        const int maxBytes = 4 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("Twitch returned too much information.");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] block = new byte[16384]; int read;
        while ((read = await input.ReadAsync(block, token).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + read > maxBytes) throw new InvalidDataException("Twitch returned too much information.");
            buffer.Write(block, 0, read);
        }
        if (buffer.Length == 0) return (response.StatusCode, default);
        using var document = JsonDocument.Parse(buffer.ToArray());
        return (response.StatusCode, document.RootElement.Clone());
    }
    public void Dispose() => http.Dispose();
}

public sealed class TwitchSessionExpiredException() : InvalidOperationException("Your Twitch connection expired. Disconnect and connect Twitch again.");

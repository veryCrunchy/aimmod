using System.Net.Http.Json;
using System.Text.Json;

namespace AimMod.Desktop.Hub;

public sealed record HubDeviceLinkSession(
    string DeviceCode,
    string UserCode,
    Uri VerificationUri,
    Uri VerificationUriComplete,
    TimeSpan ExpiresIn,
    TimeSpan PollInterval);

public sealed record HubLinkedAccount(string Username, string DisplayName);

public enum HubDeviceLinkStatus
{
    Pending,
    Approved,
    Expired,
}

public sealed record HubDeviceLinkPollResult(HubDeviceLinkStatus Status, HubLinkedAccount? Account = null);

public sealed class HubDeviceLinkClient
{
    private static readonly JsonSerializerOptions json_options = new(JsonSerializerDefaults.Web);
    private readonly HttpClient client;
    private readonly Uri baseUri;
    private readonly IHubCredentialStore credentialStore;

    public HubDeviceLinkClient(HttpClient client, Uri baseUri, IHubCredentialStore credentialStore)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.baseUri = normalizeBaseUri(baseUri);
        this.credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
    }

    public async Task<HubDeviceLinkSession> BeginAsync(string deviceLabel, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri(baseUri, "auth/device/start"),
            new DeviceStartRequest(string.IsNullOrWhiteSpace(deviceLabel) ? "AimMod osu" : deviceLabel.Trim()),
            json_options,
            cancellationToken).ConfigureAwait(false);
        await ensureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        DeviceStartResponse payload = await response.Content.ReadFromJsonAsync<DeviceStartResponse>(json_options, cancellationToken).ConfigureAwait(false)
                                      ?? throw new InvalidDataException("Hub returned an empty device-link response.");
        return new HubDeviceLinkSession(
            payload.DeviceCode,
            payload.UserCode,
            new Uri(payload.VerificationUri, UriKind.Absolute),
            new Uri(payload.VerificationUriComplete, UriKind.Absolute),
            TimeSpan.FromSeconds(Math.Max(1, payload.ExpiresIn)),
            TimeSpan.FromSeconds(Math.Max(1, payload.Interval)));
    }

    public async Task<HubDeviceLinkPollResult> PollAsync(string deviceCode, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri(baseUri, "auth/device/poll"),
            new DevicePollRequest(deviceCode),
            json_options,
            cancellationToken).ConfigureAwait(false);
        await ensureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        DevicePollResponse payload = await response.Content.ReadFromJsonAsync<DevicePollResponse>(json_options, cancellationToken).ConfigureAwait(false)
                                     ?? throw new InvalidDataException("Hub returned an empty device-link status.");
        if (string.Equals(payload.Status, "approved", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(payload.UploadToken) || payload.User is null)
                throw new InvalidDataException("Approved Hub link did not include credentials.");
            string accountLabel = string.IsNullOrWhiteSpace(payload.User.DisplayName)
                ? payload.User.Username
                : payload.User.DisplayName;
            await credentialStore.SaveAsync(new HubCredential(payload.UploadToken, accountLabel, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            return new HubDeviceLinkPollResult(HubDeviceLinkStatus.Approved, new HubLinkedAccount(payload.User.Username, payload.User.DisplayName));
        }
        if (string.Equals(payload.Status, "expired", StringComparison.OrdinalIgnoreCase))
            return new HubDeviceLinkPollResult(HubDeviceLinkStatus.Expired);
        return new HubDeviceLinkPollResult(HubDeviceLinkStatus.Pending);
    }

    public async Task<HubDeviceLinkPollResult> WaitForApprovalAsync(
        HubDeviceLinkSession session,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + session.ExpiresIn;
        while (DateTimeOffset.UtcNow < deadline)
        {
            HubDeviceLinkPollResult result = await PollAsync(session.DeviceCode, cancellationToken).ConfigureAwait(false);
            if (result.Status != HubDeviceLinkStatus.Pending)
                return result;
            await Task.Delay(session.PollInterval, cancellationToken).ConfigureAwait(false);
        }
        return new HubDeviceLinkPollResult(HubDeviceLinkStatus.Expired);
    }

    private static Uri normalizeBaseUri(Uri value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.IsAbsoluteUri || (value.Scheme != Uri.UriSchemeHttps && value.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("Hub URL must be an absolute HTTP(S) URL.", nameof(value));
        return new Uri(value.ToString().TrimEnd('/') + "/", UriKind.Absolute);
    }

    private static async Task ensureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException($"Hub request failed ({(int)response.StatusCode}): {body[..Math.Min(body.Length, 512)]}", null, response.StatusCode);
    }

    private sealed record DeviceStartRequest(string Label);
    private sealed record DevicePollRequest(string DeviceCode);
    private sealed record DeviceStartResponse(string DeviceCode, string UserCode, string VerificationUri, string VerificationUriComplete, int ExpiresIn, int Interval);
    private sealed record DeviceLinkUser(string Username, string DisplayName);
    private sealed record DevicePollResponse(string Status, DeviceLinkUser? User, string? UploadToken);
}

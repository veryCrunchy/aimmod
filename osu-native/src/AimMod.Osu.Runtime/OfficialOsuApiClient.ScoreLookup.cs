using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AimMod.Osu.Runtime;

public sealed record OsuScoreAddress(long Id, string? LegacyRuleset = null)
{
    public static bool TryParse(string text, out OsuScoreAddress? address)
    {
        address = null;
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? uri) || uri.Scheme != "https"
            || uri.Host != "osu.ppy.sh" || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort) return false;
        string[] parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length is not (2 or 3) || parts[0] != "scores"
            || !long.TryParse(parts[^1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out long id) || id <= 0) return false;
        string? ruleset = parts.Length == 3 ? parts[1] : null;
        if (ruleset is not (null or "osu" or "taiko" or "fruits" or "mania")) return false;
        address = new(id, ruleset);
        return true;
    }

    public string ApiPath => LegacyRuleset is null ? $"scores/{Id}" : $"scores/{LegacyRuleset}/{Id}";
}

public sealed partial class OfficialOsuApiClient
{
    // Uses the existing read-only API lease; failure must never change or clear
    // the user's osu! session. The URL cannot direct credentials to another host.
    public async Task<JsonElement> FetchScoreAsync(OsuScoreAddress address, CancellationToken token = default)
    {
        if (address.Id <= 0 || address.LegacyRuleset is not (null or "osu" or "taiko" or "fruits" or "mania"))
            throw new ArgumentException("Enter an osu! score link.");
        await session.RefreshAsync(token).ConfigureAwait(false);
        LazerSessionState starting = session.Current;
        using LazerAccessTokenLease? lease = session.TryLeaseAccessToken();
        if (lease is null || !lease.TryGetAccessToken(out string accessToken))
            throw new InvalidOperationException("Connect your osu! online session to look up a score link. Local plays are available without it.");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://osu.ppy.sh/api/v2/" + address.ApiPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("x-api-version", osu_api_version);
        using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("This score is no longer available on osu!.");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("Your osu! online session cannot open this score. Reconnect it in Settings.");
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("osu! is limiting requests. Wait a moment before trying again.");
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("osu! could not return this score. Try again shortly.");
        JsonElement payload = await readPayloadAsync<JsonElement>(response, maximum_response_bytes, token).ConfigureAwait(false);
        await session.RefreshAsync(token).ConfigureAwait(false);
        if (session.Current.Revision != starting.Revision || !lease.TryGetAccessToken(out _))
            throw new InvalidOperationException("Your osu! session changed. Look up the score again.");
        return payload;
    }
}

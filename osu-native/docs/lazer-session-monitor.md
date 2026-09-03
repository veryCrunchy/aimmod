# Lazer session monitor

`LazerSessionMonitor` follows the account fields in a caller-supplied lazer `game.ini`. It does not locate or open a user's installation on its own.

Create and observe it from the native application composition layer:

```csharp
await using LazerSessionMonitor session = await LazerSessionMonitor.CreateAsync(gameIniPath, cancellationToken);

LazerSessionState initial = session.Current;
session.StateChanged += state => updateAccountDisplay(state);
```

`LazerSessionState` is safe for UI binding and serialization. It contains only:

- `Status`
- `Username`
- `Revision`

The statuses have narrow meanings:

- `Unavailable`: the file is absent, unreadable, oversized, malformed, or contains an impossible credential combination.
- `SignedOut`: no usable stored token exists. The username may remain because lazer can retain it after logout.
- `Remembered`: a stored access token expired and lazer retained a refresh credential. AimMod does not use that refresh credential.
- `SignedIn`: the file contains an access token with more than 30 seconds remaining, a username, and `SavePassword` is not false. This matches lazer's own token-validity window.

The watcher handles ordinary writes, deletion, creation, and atomic file replacement. `RefreshAsync()` provides an explicit refresh path. The monitor also rereads the file every five seconds. This recovers from a watcher overflow, a data root that appeared after startup, or a missed atomic replacement. It also bounds how long a missed logout or account swap can remain visible. A token timer enters `Remembered` at the start of lazer's 30-second refresh window instead of sending a token that lazer already considers stale.

## Secret boundary

The access-token lease is internal to `AimMod.Osu.Runtime`. `OfficialOsuApiClient` lives in that assembly and uses the lease only for the duration of one request:

```csharp
using LazerAccessTokenLease? lease = session.TryLeaseAccessToken();
if (lease is null || !lease.TryGetAccessToken(out string accessToken))
    return ApiResult.SignedOut;

// Add accessToken to the official API request, then let the lease dispose.
```

Account changes, logout, the 30-second expiry safety window, malformed replacement files, and monitor disposal revoke existing leases. The monitor never stores the refresh credential, refreshes a token, writes `game.ini`, starts a network request, or logs credential values. Standard public models have no token member.

AimMod deliberately leaves refresh-token use to lazer. Lazer refreshes through its own OAuth client identity, replaces its in-memory token with the response, and writes the resulting access token, expiry, and refresh token back to `game.ini`. Reusing that refresh credential in AimMod would require impersonating lazer's OAuth client and could race token replacement in the running game. AimMod waits for lazer to publish the replacement, then leases only the new access token. If lazer is closed with an expired access token, AimMod can still show the remembered username, but it cannot fetch `/me` until lazer refreshes the session or the user connects AimMod through a separate OAuth flow.

The reader opens the file with read, write, and delete sharing so it does not block lazer's atomic saves. It accepts at most 1 MiB and rejects invalid UTF-8 or malformed relevant values.

## Official profile API

`OfficialOsuApiClient` consumes the monitor's internal lease and exposes one operation:

```csharp
using var osuApi = new OfficialOsuApiClient(session);
OsuProfileFetchResult result = await osuApi.FetchCurrentProfileAsync(cancellationToken);
```

The client always sends `GET https://osu.ppy.sh/api/v2/me/osu`. Callers cannot supply another origin, path, or mode. Its production `HttpClientHandler` disables redirects. The response is limited to 1 MiB and maps to the token-free `OsuProfile` and `OsuProfileStatistics` records.

Before accepting a response, the client refreshes `game.ini` and checks that the original token lease and session revision remain current. It discards a response if the user logged out, changed accounts, or lazer replaced the token during the request. A stable HTTP 401 or 403 becomes `Unauthorized`; an account change during that response becomes `SessionChanged`.

Network, status, and parsing failures return a closed `OsuProfileFetchStatus` value. The client does not return exception text, HTTP response bodies, authorization headers, or tokens through its public result.

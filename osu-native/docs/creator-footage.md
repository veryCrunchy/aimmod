# Score-to-footage lookup

Creator tools are **off by default**. Enable **Settings > General > Creator tools**, then choose **Set up accounts & find footage** or **Replays > Find footage**. With the setting off, the replay workspace has no footage controls and does not start Twitch or preview requests.

In **Your accounts**, check the osu! account connected through AimMod's existing client settings, connect your own Twitch account, then choose **Link my accounts & import broadcasts**. The link records both account IDs. Automatic imports only use a link matching the currently selected osu! account and signed-in Twitch account. Test-instance data and credentials are stored separately and never used as normal defaults.

Choose **Your online scores** or **Local plays** to find an attempt. Normal online history uses the current osu! account's best/recent feed, including the existing public score feed for stable accounts. Matching uses timestamps and recording metadata, without scanning broadcasts, OCR or transcription. Optional timestamp previews load the selected moment through Twitch's embedded player, capture one frame and close the player. No full video file is saved.

## Isolated creator instance

Launch `AimMod --creator-test instance.json` with a private configuration containing `osuUserId` (a numeric public osu! account ID) and `twitchChannel` (a channel login). Keep real configurations and datasets outside the repository.

The instance reads the public best/recent score window through the Hub's official osu! API integration on each launch. It does not claim complete play history, impersonate the selected account, discover a local osu! installation, or import local credentials. A configuration-specific storage name and IPC pipe isolate its data from the normal app. Protocol registration and updater bootstrap are skipped.

After Twitch sign-in, the instance imports available archive metadata automatically, with bounded pagination. Existing manual timeline corrections are preserved. Only scores whose timestamps fall within returned broadcasts can be matched; expired archives and scores outside the API window are not invented.

## Timestamp previews

Matched cards show the osu! score identity, statistics, play time and score link alongside the broadcast. Twitch previews use an isolated headless Edge or Chrome context, without existing browser cookies or extensions. The embedded player's position is checked before capture. The displayed frame can be within 1.5 seconds of the selected time and reports its captured timestamp. A changed time can be checked with **Preview selected time** or Enter in the timestamp field.

Frame images are cached by video ID and timestamp for seven days, capped at 100 images and 32 MB. Playback uses a short streamed segment rather than downloading the full broadcast. Unavailable, restricted or unplayable moments show an error instead of an unrelated channel thumbnail. YouTube frame previews are not yet supported.

## Local recordings

Choose **Your accounts > Add local recording** or **Recordings & VODs > Add local recording**. Twitch sign-in is not required. Paste the full video path, identify the osu! player, and enter the recording start time with timezone and its duration. File creation dates are not assumed to be recording times, since copying or remuxing can change them. If a score is selected, the recording can instead be aligned to that score's known video timestamp. Paused or edited recordings need a separate entry for each continuous part.

Local videos use the same score matching, confirmed moments and timeline correction as VODs. Opening a match seeks VLC, mpv or ffplay when one is installed in a standard location or on PATH. Otherwise AimMod opens the default video player and copies the timestamp, clearly asking the user to seek manually. Missing or moved recordings can be repaired with **Adjust recording**. Removing an index entry never deletes its source video.

Local timestamp previews use an already installed FFmpeg, including AimMod's existing private tool installation. They seek and decode one frame with a 30-second deadline, without copying the recording or uploading it. Previews share the 100-image, 32 MB, seven-day cache. Local cache keys include the file path, size and modification time so replacing a recording invalidates its frames. Neither local paths nor score identities are included in CSV exports.

## Twitch configuration

Official builds include AimMod's public Twitch Client ID. Custom builds can register a Twitch application with the **Public** client type for device authorization and override its Client ID using one of these options:

- Build property: `-p:AimModTwitchClientId=PUBLIC_CLIENT_ID`
- Build environment: `AIMMOD_TWITCH_CLIENT_ID`
- Runtime environment override: `AIMMOD_TWITCH_CLIENT_ID`

The build embeds only the public Client ID. Do not put a client secret or an access token in build properties, release assets, application resources or repository files. With no Client ID configured, manual VOD and recording entries remain usable; Twitch connection requests explain that the build is not configured.

The app requests a device code with no additional scopes. Public user and archive metadata do not require email, chat or channel-management access. The player completes authorization on Twitch. Sign-in cancellation, denial and expiry do not affect their osu! or AimMod Hub sessions.

Access tokens are validated on first use and at most 55 minutes after the previous validation while the feature is in use. Expiring tokens are refreshed under a single connection lock, and rotated refresh tokens are persisted before further work. Credentials use a separate local file, Windows per-user data protection, and owner-only creation permissions on Unix. They are excluded from the footage library and exports.

## Lookup and caching

The player explicitly selects the Twitch channel associated with the score's osu! player. This association is saved locally; equal usernames are not assumed.

The client resolves that channel through Get Users and requests Get Videos with `type=archive`, `sort=time`, and `first=100`. Uploaded videos and highlights are excluded because their creation times do not identify the original play's wall-clock time. Candidates overlap the saved score timestamp according to `created_at` plus `duration`. These are estimates, not confirmed playback positions.

Pagination stops at the end of the available archive or after covering the possible duration window before the score. A search is bounded to 20 pages. Repeated cursors and the page limit produce a partial result rather than a claim that the archive was exhausted. Responses are capped at 4 MiB. Metadata caching is in memory only, with at most 32 video pages and 64 channel lookups retained for ten minutes. Plays newer than a cached page cause that page to refresh, so a recent play can find a growing or newly created VOD.

Discovered VODs become local footage entries with stable IDs. Repeated discovery preserves existing manually aligned entries and confirmed markers. Each entry covers one continuous segment; interrupted or edited recordings need separate segments. A failed, cancelled, rate-limited or malformed request does not overwrite saved footage.

## Playback and limits

Web VOD links open near the score timestamp, with 30 seconds of lead-in for an estimate. The player can save the exact observed timestamp after checking the video. A saved score time may correspond to a play's start or completion, depending on its source; the app never subtracts a full map duration to invent the start of a failed run.

Twitch archives can expire or disappear between a metadata check and opening their URL. Users may retain or add a local recording reference. YouTube videos can be added manually; automatic YouTube archive discovery and OBS synchronization are separate integrations.

Links to scores already loaded for the user's account work without an additional osu! session. Other online osu! score-link lookups require the existing official osu! API session. Local score lookup does not require that session.

No separate streamer binary is required. A later creator preset can expose this workspace by default while retaining the same gameplay, account and storage code.

## Provider references

- [Twitch device authorization](https://dev.twitch.tv/docs/authentication/getting-tokens-oauth/#device-code-grant-flow)
- [Twitch token validation](https://dev.twitch.tv/docs/authentication/validate-tokens/)
- [Twitch Get Videos](https://dev.twitch.tv/docs/api/reference/#get-videos)
- [osu! API](https://osu.ppy.sh/docs/index.html)
- [VLC playback options](https://docs.videolan.me/vlc-user/en/support/faq/faqwindows.html)
- [mpv start-time option](https://mpv.io/manual/stable/#options-start)

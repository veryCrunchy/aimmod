# AimMod for osu! native shell

AimMod owns a native window through a custom `OsuGameBase` subclass. A headless worker process owns isolated runtime jobs and communicates over redirected standard input and output. It runs through the same AimMod executable in an internal `--worker` mode, so the release contains one apphost and one self-contained .NET runtime.

It is not a full osu client. The desktop references `ppy.osu.Game` for `OsuGameBase` and the standard ruleset package, but it does not derive from `OsuGame`, include `osu.Desktop`, or load osu's client screens. The worker boundary is intentionally small so a later `AimMod.Osu.Runtime.Adapter` can compile only the lazer services that AimMod needs.

## Projects

- `AimMod.Desktop` is the native window and AimMod navigation.
- `AimMod.Osu.Runtime.Contracts` contains versioned messages and capability names. It has no ppy dependencies.
- `AimMod.Osu.Runtime` supervises the worker, implements the protocol client, and provides read-only lazer session and playback-preference monitors. Session handling is described in [docs/lazer-session-monitor.md](docs/lazer-session-monitor.md).
- `AimMod.Osu.Worker` is a separate worker assembly loaded by AimMod's headless process mode. It runs bounded osu!standard replay analysis with the official ruleset and no audio or window. For selected library assets, it opens the detected lazer Realm read-only only long enough for Realm to write a private consistent snapshot; queries run against that snapshot.
- `AimMod.Osu.Runtime.Tests` checks protocol parsing, routing, and failure behavior.
- `AimMod.Desktop.Tests` checks native replay launch parsing, official player wiring, grouped and virtualised local-library queries, replay indexing, analysis staging, and insight presentation without opening a window.
- `AimMod.Osu.Worker.Tests` checks staging boundaries, replay-analysis limits, timeout behavior, and worker errors.

The desktop never starts a web server. It also does not start the worker until a feature asks for an osu capability.

Installed lazer skin discovery and native replay-player selection are described in [docs/native-skin-management.md](docs/native-skin-management.md).

The internal worker can be tested without opening a window:

```sh
printf '%s\n' '{"id":"11111111-1111-1111-1111-111111111111","protocolVersion":1,"command":"hello"}' | AimMod --worker
```

## Build

```sh
dotnet restore AimMod.Native.sln
dotnet build AimMod.Native.sln --configuration Release --no-restore
dotnet test tests/AimMod.Osu.Runtime.Tests/AimMod.Osu.Runtime.Tests.csproj --configuration Release --no-build
```

Build the separate, self-contained Linux download without launching the GUI:

```sh
./scripts/build-linux-release.sh
```

The release script verifies checked-in dependency pins and rejects React, Tauri, Node, web frontend, and KovaaK payloads. See [docs/linux-packaging.md](docs/linux-packaging.md) for the package layout and reproducibility controls.

The local proof of concept pins `ppy.osu.Game` and `ppy.osu.Game.Rulesets.Osu` to `2026.730.0`, the newest matching pair published on NuGet during development.

Run the native wiring probe without opening a window:

```sh
dotnet run --project src/AimMod.Desktop/AimMod.Desktop.csproj --configuration Release --no-build -- --probe
```

Open an osu!standard replay in AimMod's native window:

```sh
AimMod --beatmap /absolute/path/to/set.osz --replay /absolute/path/to/play.osr
```

This route uses the official `ReplayPlayer`, gameplay clock, beatmap audio, hitsounds, and configured skin inside the AimMod process. It does not start the full osu client or a second replay window.

The native route imports the selected beatmap into AimMod's own storage, resolves the replay's embedded beatmap hash, and pushes the official `ReplayPlayer` onto AimMod's `OsuScreenStack`. Rendering, music, hitsounds, replay timing, input, and the configured skin all stay in the same process. AimMod does not open the full osu client or a second replay window.

While playback starts, AimMod copies only the selected `.osu` and `.osr` into an isolated temporary directory and asks its muted headless worker for exact object judgements. The native replay overlay then shows the hit distribution, exact miss timestamps and object numbers, and a concrete next-play prompt. Staged files are removed after analysis.

For an extracted `.osu`, keep its audio, samples, images, and storyboard in the same directory. A raw `.osu` hash file copied from lazer's internal file store is not a playable bundle because its referenced assets are stored separately.

## Runtime boundary

Messages use one JSON object per line. Standard output is protocol-only. Diagnostics belong on standard error. The first request must be `hello`, and both processes reject unsupported protocol versions.

Replay analysis accepts only files that AimMod copied into an isolated staging directory. Its result includes exact object judgements and timing offsets, with fixed input, output, and wall-clock limits. This path does not open the live osu database, start an audio device, or create a window.

The protocol exposes `library.catalog.search` for bounded, grouped beatmap-set and replay queries over the detected lazer library. The native Beatmaps and Replays screens switch to this source after discovery, while keeping AimMod's inherited Realm source as an offline fallback. Each query runs outside the UI thread, supports text/ruleset/star filters, sorting, and paging, and discards stale results without abandoning the worker's private snapshot cleanup.

`library.resolve-assets` handles caller-selected lazer assets. A request supplies an empty AimMod-owned staging directory, up to 512 beatmap MD5 or SHA-256 hashes, and up to 512 score IDs. While its private snapshot lease is active, the worker copies each selected beatmap, audio, background, or replay file into staging and verifies its SHA-256 hash. The result reports staged files, missing stored files, and selectors it could not resolve. The native library uses this path for real artwork, beatmap audio, and replay playback without exposing lazer's hashed file-store layout to the UI.

## Local library

The native Beatmaps and Replays routes are backed by the official detached beatmap store and local-score Realm query. Beatmap difficulties stay grouped under their set. Searches and filters run offline against detached snapshots, results are paged in batches of 60, and the native list keeps only a small drawable pool alive. Replay metadata follows the active user exposed by the current ppy API session and invalidates when the user or score table changes.

AimMod also detects conventional osu!lazer data roots on Linux, Windows, and macOS, including `storage.ini` redirects and an explicit `AIMMOD_OSU_LAZER_DATA_DIR` override. It watches the detected `game.ini` for login, logout, token rotation, and account swaps, with a five-second read-only reconciliation pass in case a file event is missed. A fixed-origin official API client uses a short-lived internal token lease to fetch `/api/v2/me/osu`; tokens never enter public models or UI text. AimMod does not copy or refresh lazer's refresh credential.

AimMod separately follows an allowlisted set of playback preferences from lazer's `game.ini` and `framework.ini`: beatmap skins, beatmap colours, beatmap hitsounds, global audio offset, positional hitsound level, and music, effects, and master volume. The monitor ignores account and token keys and never writes either file. This keeps native replay playback aligned with the active lazer setup while preserving AimMod's independent application storage.

External library assets use a separate read-only boundary. For each `library.resolve-assets` request, the worker asks Realm to make a consistent private database snapshot outside the lazer data root and opens that snapshot read-only. It resolves only the requested beatmap hashes and score IDs, maps their file hashes directly into lazer's file-store layout without scanning it, and copies verified files to the supplied staging directory before deleting the private snapshot. The command never writes to the lazer database or file store.

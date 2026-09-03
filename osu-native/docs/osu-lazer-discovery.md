# osu!lazer data-root discovery

`OsuLazerDiscoveryService` finds candidate osu!lazer data roots without opening Realm or reading profile data. The service is platform-explicit and receives both environment values and filesystem operations through injected values, which keeps it deterministic and fully testable offline.

## API

```csharp
var environment = new OsuDiscoveryEnvironment(
    HomeDirectory: home,
    XdgDataHome: xdgDataHome,
    AppData: appData,
    ExplicitDataRoot: aimmodOsuDataRoot);

var result = new OsuLazerDiscoveryService(new PhysicalOsuDiscoveryFileSystem())
    .Discover(platform, environment);

IReadOnlyList<OsuLazerDataRoot> ready = result.CompleteDataRoots;
```

The app host owns environment-variable access. It should pass `AIMMOD_OSU_LAZER_DATA_DIR` as `ExplicitDataRoot`; the discovery service itself does not access process state.

Candidate order is:

1. Explicit override.
2. A `FullPath` redirect from a candidate's `storage.ini`.
3. Conventional platform locations.

Linux checks `$XDG_DATA_HOME/osu`, `~/.local/share/osu`, and the Flatpak data directory. Windows checks `%APPDATA%\osu`. macOS checks `~/Library/Application Support/osu`.

## Validation and safety

A complete root must contain all of:

- a non-empty `client.realm` file;
- a `files` directory;
- a non-empty `game.ini` file.

Partial roots are returned with individual marker flags and user-presentable problems, but only complete roots appear in `CompleteDataRoots`. Paths are canonicalised before deduplication. Marker links are accepted only when their final target remains inside the canonical data root.

`storage.ini` is limited to 64 KiB, must be a regular non-symlink file, and may contain only one absolute `FullPath`. Relative redirects, malformed duplicates, oversized files, and unreadable paths are reported in `RejectedCandidates`. Canonically equivalent roots are merged, with source provenance preserved.

The physical adapter performs only bounded metadata/configuration reads. Tests use an in-memory synthetic filesystem and do not inspect live user directories.

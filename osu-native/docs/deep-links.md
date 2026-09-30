# Website deep links

Supported launch arguments (one URI, no additional options):

- `aimmod-osu://beatmapsets/{id}`: decimal integer 1 through 2147483647 (Int32.MaxValue), without leading zeros.
- `aimmod-osu://skins/osuskins/{sourceId}`: exactly seven ASCII alphanumeric characters, case preserved (for example `3sXe0RR`).
- `aimmod-osu://skins/osuck/{sourceId}`: 1-80 ASCII letters, digits, hyphens or underscores, case preserved. Numeric IDs and provider slugs are accepted.

Queries, fragments, trailing slashes, escaped characters, traversal, credentials, ports and additional arguments are rejected. The website must cap beatmap IDs at Int32.MaxValue even when its backend represents IDs as uint64 strings.

The online Beatmaps tab loads `/api/v2/beatmapsets/{id}` using the existing lazer session and displays the set and all difficulty details. Session failures retain the selected ID for retry. The online Skins tab resolves exact provider metadata. Links never prepare a skin preview archive, download a beatmap archive, save, import or apply content; those actions require the existing explicit UI controls.

## Startup and existing instances

The framework host's `aimmod-native-shell` named pipe uses a `string[]` IPC channel so wire types remain independent of AimMod assembly versions. The receiver is attached before IPC binding, validates again, queues accepted links, and acknowledges delivery. The game drains the queue on its update thread after loading and raises its window. A secondary link launch exits only after acknowledgement. If an older instance has no receiver or acknowledgement fails after 15 seconds, the new process retains its launch link and opens a review window.

## Protocol registration

Normal packaged executable launches refresh registration for the current user. Running through `dotnet`, worker/probe modes and tests do not register the protocol. Windows Velopack install/update callbacks also refresh `HKCU\Software\Classes\aimmod-osu`, with both the executable and `%1` quoted. Registration follows the running executable when a portable folder moves; launch AimMod once from its new location. Velopack refreshes registration after replacing the installed application.

Linux creates `$XDG_DATA_HOME/applications/aimmod-osu.desktop` (default `~/.local/share/applications`) and selects it using `xdg-mime`. Its `Exec` passes one `%u` argument. For AppImage releases the persistent `APPIMAGE` path is registered, never the temporary mount. For tarball releases the extracted executable is registered. Launch once after extraction or moving the package. A desktop session with `xdg-mime` is required for browser dispatch. Registration failure does not prevent app startup.

## Verification

Focused desktop tests cover parsing, numeric overflow, launch-mode conflicts, pre-load routing, queued startup delivery, a real framework named-pipe round trip, and registration quoting. Runtime tests check exact-set endpoint identity and ensure the metadata operation makes only one request. On release platforms, also launch each URI from a browser with AimMod closed, while it is loading, and while it is already open; repeat after installer/AppImage updates and portable relocation. Confirm that no archive operation occurs until its UI button is selected.

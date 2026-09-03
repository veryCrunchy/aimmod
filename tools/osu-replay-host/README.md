# Official osu! replay host

This helper runs the official `ppy.osu.Game` `ReplayPlayer` in an osu!framework desktop host. The same process owns rendering, the beatmap track, replay timing, and hitsounds. AimMod does not schedule one browser `Audio` object per replay sample.

The first host intentionally uses a native top-level window. osu!framework's desktop host creates and owns an SDL window and graphics swapchain. Its public API does not accept an existing GTK/WebKit/Tauri child handle. Headless mode uses `DummyRenderer`, so it cannot render gameplay. `GameHost.TakeScreenshotAsync()` can read pixels back from a real renderer, but that is CPU readback rather than a shared GPU texture.

## Run

```sh
osu-replay-host --beatmap /path/to/set.osz --replay /path/to/play.osr
```

The recommended input is a caller-staged `.osz`. An extracted `.osu` also works if its audio, samples, images, and storyboard remain in the same directory. The host imports the bundle into its own isolated lazer database. The replay's embedded beatmap MD5 selects the exact difficulty before the host pushes the official `ReplayPlayer`.

Do not pass a raw `.osu` hash file from lazer's internal `files/` store. Its referenced audio and samples live at other hashes, so that file alone is not a playable bundle. AimMod must export or stage an `.osz`, or reconstruct an extracted beatmap directory from Realm metadata and the file store. The host overrides lazer's consume-after-import hook and never deletes the supplied bundle.

`osu-replay-host --probe` is non-graphical and reports compiled capabilities as one JSON line.

## JSON-lines IPC

stdin accepts one command per line:

```json
{"id":"1","type":"play"}
{"id":"2","type":"pause"}
{"id":"3","type":"seek","timeMs":45000}
{"id":"4","type":"setPlaybackRate","rate":1.25}
{"id":"5","type":"setVolume","master":0.8,"music":0.7,"effects":1.0}
{"id":"6","type":"getState"}
{"id":"7","type":"close"}
```

stdout emits one JSON object per line. Events are `hello`, `ready`, `state`, `ended`, `ack`, `error`, and `fatal`. Periodic `state` events are capped at four per second. Callers must treat stdout as protocol data and stderr as diagnostics.

The process exits when stdin closes. It uses a distinct `aimmod-osu-replay-host` storage name and does not open or modify the user's active osu!lazer database. This also means the first host uses its isolated lazer defaults. It does not automatically inherit the active client's selected skin, skin files, offsets, audio device, or other settings. Those need an explicit snapshot/import contract in a later integration.

## Embedding boundary

A true child surface needs framework work, not a Tauri-side flag:

- expose or add an `IWindow` implementation backed by a caller-supplied native handle;
- create the graphics surface and swapchain against that handle;
- forward resize, focus, keyboard, mouse, tablet, and DPI events into osu!framework;
- provide per-platform implementations. Wayland does not provide the X11-style cross-process reparenting used by `XReparentWindow`.

The practical next integration is a managed native replay window positioned with AimMod, or a new framework texture-export path. Reparenting the current SDL window through reflection would depend on internal types and would not be portable.

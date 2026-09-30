# In-game replays

Replays use the actual Unreal main viewport, an owned camera, and inert target
actors. The AimMod workspace closes after scene activation succeeds; a native
replay HUD provides play/pause, seeking, speed and exit controls.

## Components

- `ReplayCapture.lua` observes run events and records camera, target state,
  player inputs and map identity. It never invokes gameplay input or scoring.
- `ReplayCatalog.cs` validates completed private `.amreplay` files.
- `NativeReplayPlayback.cs` interpolates recorded state and exchanges bounded
  frame/control files with the game. It never writes score history.
- `browser.js` lists native recordings; `native-browser.js` hands playback to
  the main game view. There is no sprite, canvas or movement-archive viewer.
- `ReplayMainScene.lua`, `ReplayMainBridge.lua` and `ReplayHUD.lua` own the main
  camera, visual actors, controls, state restoration and runtime checks.
- `ReplaySafetyProbe.lua` provides optional aggregate observers for score,
  damage, completion and upload events without changing their arguments/results.
- `ReplayMapTransition.lua` contains the separately tested map-transition state
  machine. Its live adapter and geometry identity checks remain under validation.

## Score boundary and current validation

Playback requires verified world/controller ownership, matching map identity,
and a paused game. Active scenarios require an existing menu pause and frozen
challenge state and timers. It never starts a challenge, possesses
bots, injects recorded weapon inputs, or suppresses legitimate queued uploads.
Only owned visual actors are moved or destroyed. Camera, visibility, pause and
input state are restored on exit. See `MAIN-VIEWPORT.md` for the detailed contract.

Main-view activation and camera movement still require visual verification.
Mock tests do not prove live rendering or complete score-path coverage.
A fresh completed native recording has been observed with camera/target frames
and map metadata. Historical incomplete movement and image recordings and their
viewer/decoder have been removed; they are not a fallback playback path.

## Tests

Recording boundaries follow the native challenge state and elapsed timer.
Repeated analytics start, restart, or replay notifications cannot split an
advancing attempt. A real timer reset or transition out of the active challenge
ends that recording; the next advancing attempt receives a new shared history
and replay ID. Paused game time does not create additional frames or files.
Completion is associated with the recording independently of analytics hook
ordering. Private lifecycle diagnostics include the boundary cause and the
number of start notifications observed.

Run the native service with `--self-test`. Run `node --test` on the `.test.cjs`
files in this directory. Lua capture, telemetry, main-scene, main-bridge, safety
and map-transition tests run with a compatible Lua interpreter from this
working directory. Fixtures are synthetic and do not upload scores.

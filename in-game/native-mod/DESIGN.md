# AimModCore (native UE4SS mod)

AimModCore is a small C++ UE4SS mod that observes KovaaK's challenge runs
read-only and produces the run journal, live telemetry and replay files that
the AimMod native service and the in-game UI already consume. It replaces the
Lua telemetry/replay capture (which stopped seeing events on KovaaK's 3.9.11,
where the challenge lifecycle, shot and score functions are called natively)
and the older `ue4ss-mod/` bridge.

## Goals and hard limits

- Read-only. The mod never calls a scoring, upload, input, possession or
  challenge-control function and never writes game memory. It only calls
  reflected getters (`IsInChallenge`, `GetChallengeTimeElapsed`,
  `Get_*_ValueElse`, `GetLastScore`, camera/actor getters) and reads
  reflected properties. The allow-list of callable functions lives in one
  table (`mod/src/GameBindings.cpp`); nothing else is ever invoked.
- Stable across game updates. Every class, function and property is resolved
  by name at startup/world change and its signature (parameter names, types,
  sizes) is verified before first use. A missing or changed item disables only
  the features that need it; the log prints one compatibility summary.
- Minimal per-frame cost. Hot paths compare cached pointers; there are no
  string operations, allocations or file I/O on the game thread during a run
  except the replay frame formatter, which appends to a reused buffer.
- Permanent install. UE4SS (`dwmapi.dll` proxy) loads the mod on every
  launch; the mod starts and supervises the native service.

## Components

```
core/     engine-independent library (unit tested, no UE4SS headers)
  Lifecycle      attempt state machine (poll samples + optional hook events)
  Formats        journal line, live-overlay JSON, replay JSON lines, escaping
  ReplayV2       replay format 2 encoder/decoder (ReplayWriter: format 1)
  Settings       native-settings.tsv parser, core-active handshake line
  Supervisor     service restart back-off policy
mod/      UE4SS glue (built against a local RE-UE4SS checkout)
  GameBindings   name resolution, signature checks, typed getter calls
  Observer       game-thread scheduler, hooks, lifecycle driver, live values
  ReplaySampler  per-frame input + 60 Hz state capture (format 2)
  Output         writer thread: journal append, atomic JSON files, heartbeat,
                 shared-memory live channel
  ServiceHost    launches and supervises AimMod.InGame.exe
```

### Threads

- Game thread: the UE4SS `EngineTick` post-callback drives a fixed-rate
  scheduler (lifecycle + live values at 20 Hz, replay samples at 60 Hz,
  world re-binding at 1 Hz). If `HookEngineTick` is disabled the scheduler is
  driven from the `ProcessEvent` callback on the game thread instead.
  UFunction hooks (kill credit, input recording) also run here.
- Writer thread: owns all file I/O. The game thread hands it small immutable
  jobs through a bounded queue; the journal is never dropped, live snapshots
  are coalesced (latest wins).
- Supervisor thread: starts/restarts the service with back-off.

## Event sources

1. Polling (primary, verified on 3.9.11). `ScenarioManager` is resolved once
   per world (`FindAllOf`, non-default object; receivers are the ones sharing
   its outer, the game instance) and held as a weak pointer. Each poll reads
   `IsInChallenge`, `IsScenarioLoading`, `GetChallengeQueueTimeRemaining`,
   `GetChallengeTimeElapsed`, `GetChallengeTimeRemaining`, the current
   scenario (`GetCurrentScenario` -> `IsActive`/`IsInChallenge`, name read
   once per scenario object), `StatsManager:GetLastScore` and
   `GetLastChallengeTimeRemaining`.
2. `ProcessEvent` observer (UE4SS `RegisterProcessEventPreCallback`, read
   only). Filters against a small array of cached `UFunction*` resolved once
   per world: the framework broadcasts (`ScenarioBroadcastReceiver:Send_*`,
   `PerformanceIndicatorsBroadcastReceiver:Send_*`) and the Blueprint
   handlers bound to the receiver's `Receive_*` delegates. On 3.9.11 these
   are mostly called natively, so this source is an accelerator: lifecycle
   never depends on it.
3. UFunction hooks (UE4SS `RegisterHook`, post, read only):
   `ScenarioManager:NotifyPlayerKillCredit` (fires on 3.9.11) and the
   `MetaInputRecordingComponent:Record*` input entry points (replay inputs;
   coverage is whatever the build actually calls).

### Lifecycle state machine (core/Lifecycle)

States: `Idle -> Running -> Ending -> Idle`.

- Start: the challenge is running (in challenge, scenario active, not
  loading, queue timer zero) and the elapsed timer advanced between two
  polls, after having been observed not running (armed). A start hook only
  arms the machine early; the timer still confirms the attempt.
- Restart: while running, the elapsed timer drops back to <= 1 s and a
  following poll advances again: the current attempt is cancelled
  (`restart`) and a new one starts.
- End: running -> not running (while not paused) enters `Ending` with a 5 s
  deadline and starts watching `FPSAimTrainer\stats` for the CSV the game
  writes when (and only when) a challenge completes (matched by scenario and
  its `Challenge Start` time within 5 s). That record completes the attempt
  and supplies the final score, hits, misses, kills and damage. Without it,
  only a run whose timer ran out (<= 0.5 s remaining at the last running
  poll) completes at the deadline; anything else is cancelled (`quit`).
  `GetLastScore`/`GetLastChallengeTimeRemaining` are never completion
  evidence: they reset (to 0) when a scenario is left (live test #2: a quit
  after 2 s was journalled as a completion by that rule).
- Final score: the stats record, else a `GetLastScore` that changed during
  the attempt (a 0 never replaces a non-zero live score; live test #2 read 0
  at completion for a 10533 run), else the last
  `PerformanceIndicatorsStateReceiver:Get_Score_ValueElse` value with
  `Result == HasValue`. A run without any is not journalled. Scores are
  never computed.

### Values (ValueElse / ValueOr contract)

`*_ValueElse(OutValue, Result)`: trust `OutValue` only when
`Result == EValueElseResult::HasValue (0)`; `Else (1)` means no value (never
carry a previous value through). `*_ValueOr(ValueIfNull)` is used only where
an explicit sentinel is acceptable. Live metrics prefer the performance
receiver (score, shots, hits, kills, damage) and fall back to the local
character's weapon session counters / kill count / damage (read-only
properties), matching the Lua observer.

## Output contract

All under `%LOCALAPPDATA%\AimMod\KovaaksNative\`:

- `completed.tsv`: one appended line per completed attempt,
  `run\t<id>\t<scenario>\t<score>\t<accuracy>\t<duration>\t<kills>\t<damage>\t<UTC ISO-8601>`,
  fields `%`-escaped (`%25`, `%09`, `%0D`, `%0A`); read by `NativeRuns.cs`.
- `live-overlay.json`: `{"version":1,"active":..,"paused":..,...}` exactly
  as `Telemetry.lua` wrote it; replaced atomically; rewritten on change and
  at least once per second while the game runs; read by
  `LiveOverlayState.cs` (2 s freshness) and the Lua HUD.
- `replays/<id>.amreplay`: replay format 2 (below), written once when an
  attempt completes (`.partial` then rename). The replay id equals the journal
  id. `ReplayCatalog.cs` reads format 2 and the older JSON-lines format 1
  (`ReplayCapture.lua`), so existing libraries keep working.
- `replay-status.json`: capture status for the workspace.
- `core-scene.json` (new): current scenario/map/challenge/pause state for
  the replay start gate, rewritten at least once a second.
- `core-active.tsv` (new): handshake `AIMMOD_CORE_1\t<version>\t<unix
  seconds>\t<capabilities>` refreshed every second and removed on shutdown.
  The Lua mod steps aside for each capability listed (`telemetry`,
  `replay`) while the stamp is at most 3 s old, and reads the live snapshot
  from `live-overlay.json` for its HUD. When the stamp goes stale (crash,
  uninstall) Lua resumes on its own.
- Shared memory `Local\AimMod.KovaaksNative.Live` (new, optional): a
  seqlock-protected copy of the live-overlay body for readers that want it
  without file polling. The file remains the compatibility path.

## Replay format 2

Goal: 1:1 playback from the smallest record. Source of truth is what the
game consumed; everything derivable or interpolable is left out.

Capture (game thread): every engine frame while recording stores its game
time and the inputs the game consumed in it (the `MetaInputRecordingComponent`
entry points: look axes as per-frame deltas, movement axes as changes, button
edges). At 60 Hz it samples camera location/rotation/FOV, targets (location,
capsule, health, profile, rotation) and the measured stats. Memory is
bounded (about 15 minutes at 400 fps); the capture is encoded by the writer
thread when the attempt completes; interrupted attempts are dropped.

Encoding (`core/ReplayV2`):

- Look axis values on current builds are mouse counts times a constant
  (0.07); the encoder detects the quantum and stores integer counts, else raw
  floats. Rotation per unit (yaw, pitch) is fitted by least squares over the
  sample intervals (live test #1: 0.114588 deg/unit, the game's own
  sensitivity increment 0.114586) and stored in the file.
- View rotation is stored only as keyframes: the start, every button edge,
  kill or registered hit, at most every 0.5 s, and whenever replaying the
  deltas through the constants drifts more than 0.02 deg from the sampled
  rotation. Without an input stream it degrades to dense keyframes.
- Camera location/FOV and target tracks are piecewise-linear keyframes
  within tolerance (0.05 cm camera, 0.5 cm targets, 0.5 deg target rotation,
  spans at most 2 s); gaps (hidden or dead targets) start new segments.
  Target motion is scenario-random, so it is recorded, not simulated.
- Stats are change records; `seconds` only when it departs from the clock.
- Integers are LEB128 varints, deltas zig-zag coded, streams columnar; the
  body is compressed with the Windows Compression API (LZMS; readable from
  .NET through cabinet.dll, no third-party code).

File: `"AMRPLAY2"`, u32 version (2), u32 header length, header JSON (the
format 1 header fields plus `reason`, `frames`, `inputEvents`, `score`,
`duration` and an `encoding` summary), u32 algorithm, u32 body size,
compressed body: frames (time in 50 us units), inputs (frame deltas, action
ids, values), yaw/pitch per unit, rotation keyframes (1e-4 deg), camera
track, targets (profile, points, health changes, rotation points), stats
records, registered hits. The catalog lists files from the header alone.

Decoding (`ReplayFormat2.cs`): rotation is rebuilt at every recorded engine
frame (keyframe plus the deltas consumed since); `Motion` answers the exact
pose at any playback instant; a 60 Hz frame list is also produced for the
browser, HUD and older consumers.

Playback: renderer protocol 6 adds a 0.4 s window of 120 Hz camera samples,
per-target velocities and the wall-clock publication instant (`clock`) to
each published frame. AimModCore's presenter (`mod/src/Presenter`) reads the
frame file at 60 Hz and, in the engine tick pre-callback (before the world
and camera update of that frame), sets the replay camera and target proxies
to the pose at the exact playback instant: publication time plus elapsed
wall time times speed; no smoothing or clock correction is needed because
both sides share the wall clock. It moves only the actors the Lua scene
lists in `replay-proxies.tsv` (its own camera actor and inert proxies), only
while that camera is the player's view target and the game is paused, and
logs its apply rate, frame interval and playback-step error every 5 s. Its
heartbeat (`replay-presenter.tsv`) tells the Lua renderer to leave the view
and target locations alone; if the count stops advancing Lua falls back to
its own render-rate loop. Protocol 5 renderers and services keep working.

Replay start: the service checks `core-scene.json` (scenario, map, challenge
and pause state published by AimModCore) before loading. A replay plays in
the world it was recorded in, from the pause menu; a start that cannot be
honoured yet stays pending with a reason and message
(`GET /native-replay` -> `start`) and begins by itself once the game is
ready (cancel with `{"action":"cancel"}`; 10 minute limit). When the
reason is another scenario and AimModCore advertises `load`, the service
asks it to load the replay's scenario (game command below) and shows the
progress, or the refusal, in the same message.

Measured on live test #1 (60 s tracking run, 378 fps, 43821 inputs):
format 1 4.04 MB/min, format 2 54.6 KB/min (74x smaller; XPRESS 61.5,
uncompressed 179). Keyframes 2.05/s; drift before snapping max 0.0020 deg;
decoded orientation vs every recorded sample max 0.0019 deg (rms 0.0003);
playback sampled at the format 1 frame times differs by at most 0.008 deg and
0.83 cm for targets; score, shots and hits identical; the view ray is on the
target at 363 of 431 hit samples in both the recording and the
reconstruction (100% agreement). `aimmod_replay_tool convert` and the
service's `--compare-replays` reproduce these numbers.

## Replay sharing, run vs run, clips, spectating

All of these use the replay viewer (paused world, Lua scene, native
presenter); none touch a running challenge.

- Received replays: `POST <prefix>/replays/import` (header `X-AimMod-UI: 1`,
  body = the format 2 file, up to 8 MB) decodes the file completely, then adds
  it as `replays/<id>.amreplay` (`{"id","scenario"}`; 422 `{"error"}` for
  `unsupported-format`, `invalid-replay`, `replay-exists`). Format 1 is not
  accepted from other players.
- Run vs run: `POST <prefix>/native-replay`
  `{"action":"load","id":"<run>","compareId":"<other run>"}` (same scenario;
  422 otherwise). Protocol 6 frames then carry `ghost\t<x y z pitch yaw roll fov>`
  (the other run's camera at the same timeline second) and, while playing,
  `ghostmotion\t<t>\t<7 values>` rows. The Lua scene places a small inert
  sphere where the other run aims (800 cm along its view); the presenter
  moves it every engine frame. Status reports `mode:"compare"` and
  `compareId`. Pending starts keep the comparison.
- Clips: the clip key (default F8; `clip-settings.tsv`:
  `AIMMOD_CLIPS_1` / `key\tF9` / `before\t8` / `after\t2`; F1-F12, Insert,
  Home, End, PageUp, PageDown, Pause, ScrollLock; 0-60 s) marks a moment of a
  recorded run, read-only and only while KovaaK's has focus. When the run
  completes each mark becomes its own format 2 replay
  `<id>-clipN.amreplay` (the slice, re-based to 0, header `clipOf`,
  `clipStart`); marks are listed in the full replay's header (`marks`).
  Clips are ordinary library entries: shareable with the import above.
  Video export is not implemented (needs an in-game capture path).
- Spectating: the bridge writes the watched player's view to
  `spectate-pose.tsv` in pose format 1 and the workspace starts
  `{"action":"spectate","scenario","mapName","mapScale","label"}` (409 with
  `error`/`message` when the stream is missing or the start gate refuses:
  the spectator must be in the same scenario, in the pause menu). The view is
  shown 120 ms behind the newest pose; it ends when the stream stops for 2 s.
  Follow the leader: send the same spectate request again with the new
  `"stream"` (same scenario and map): the view switches in place (no stop,
  no new 2 s window); until poses of that stream arrive the current view
  continues, then it cuts to the new player (fresh 120 ms buffer, no
  blend). Another scenario/map starts a new view. With `stream` set, poses
  of other streams are ignored; without it, a stream id change or a clock
  jumping back by more than 1 s also starts a fresh buffer.
  AimModCore writes the local player's view in the same format to
  `self-pose.tsv` (60 Hz samples, rewritten at 30 Hz) only while
  `self-pose.request` was touched within the last 5 s.

Pose format 1 (UTF-8, LF):

```
AIMMOD_POSE_1\t<sequence>[\t<stream id>]     (stream id: [A-Za-z0-9_-]{1,64}, e.g. the watched player)
meta\t<%-escaped scenario>\t<%-escaped map name>\t<map scale>
pose\t<unix ms>\t<x>\t<y>\t<z>\t<pitch>\t<yaw>\t<roll>\t<fov>     (1-64 rows, increasing ms)
target\t<id>\t<x>\t<y>\t<z>\t<capsule radius>\t<capsule half height> (optional, latest positions)
tag\t<target id>\t<stream id>                           (optional: that target is this player's avatar)
self\t<unix ms>\t<x>\t<y>\t<z>\t<radius>\t<half height>\t<crouched 0|1>   (optional: the sender's own body)
fire\t<unix ms>\t<shots fired total>\t<fired since previous publication 0|1>  (optional: the sender's weapon)
```

Pose format compatibility: readers ignore row types they do not know
(the service's reader does since this change; the multiplayer bridge
already did), and every row added later must be optional. Header and the
`meta`/`pose`/`target` row shapes do not change. Deploy the service with
this reader before a mod that writes `tag`/`self`/`fire` rows (an older
service rejects unknown rows in spectate-pose.tsv).

Avatars (tracking duel): the bridge writes `avatars.tsv` in the output
folder - `AIMMOD_AVATARS_1`, then `<actor name>\t<stream id>` per drawn
avatar (actor name as `UObject::GetName`, e.g. `BP_AvatarHull_C_3`; stream
id `[A-Za-z0-9_-]{1,64}`, up to 64 rows). AimModCore reads it once a second
while the pose stream is requested and adds a `tag` row for each target
whose actor name matches. `self` is the sender's character location and
capsule (no eye-height estimate needed); `fire` comes from the local
weapons' session shot counters (`fired` = the total advanced since the
previous publication, about every 33 ms).

PB ghost while practising is not implemented yet: freeplay exposes no
attempt clock the ghost could follow, and the mod would need to place
visuals in a live (unpaused) world. Startup now logs which timers advance in
freeplay (`freeplay timer probe`) to pick a sync source; until then run vs
run against the best replay covers the comparison.

## Game commands

The user approved AimMod changing game state where a feature needs it
(replay scenario load, multiplayer starts); ranked play stays untouched.
AimModCore performs these on the game thread, one at a time.

Transport: the service writes `core-command.tsv` (atomically) and AimModCore
answers in `core-command-result.tsv`. HTTP (workspace capability URL,
header `X-AimMod-UI: 1`): `POST <prefix>/game-command` with JSON
`{"action","scenario","mode","timeScale","targetSize","targetSpeed","mapScale","weapon"}`
returns `{"sequence":n}` (409 `{"error":"unsupported"}` without the
capability, 400 `{"error":code}` when malformed); `GET <prefix>/game-command`
returns `{"capabilities":[...],"result":{sequence,state,code,message}}`.
Capabilities come from `core-active.tsv`: `load` (scenario load) and `start`
(start with a play type), `capture` (thumbnails).

```
AIMMOD_CORE_COMMAND_1
seq	<increasing integer; a request left from an earlier session is ignored>
action	load-scenario | start-scenario | reset-overrides
scenario	<exact scenario name>             (load/start)
mode	freeplay | challenge                   (start; default freeplay)
timeScale	<0.1..4>                          (start, freeplay only)
targetSize	<0.1..10>                        (start, freeplay only)
targetSpeed	<0.1..10>                       (start, freeplay only)
mapScale	<0.1..10>                          (start, freeplay only)
weapon	<weapon profile name>                (start, freeplay only)
seed	<0..4294967295>                          (start; freeplay, or challenge of "AimMod Match - " scenarios)
width	<64..3840> / height	<64..2160>       (capture-thumbnail)
out	<name>.png                              (capture-thumbnail; plain file name)
view1..view4	<x>,<y>,<z>,<pitch>,<yaw>,<fov> (capture-thumbnail; 1-4, in order)
```

`capture-thumbnail` (capability `capture`, also `refresh-scenarios` and
`action` list above): refused during a challenge; loads the scenario in
freeplay if it is not the current one (with the same new-file rescan as
load/start), waits until the world is loaded plus 2 s for streaming, spawns
an inert CameraActor of its own, hides the player pawn (its first-person
weapon), makes the camera the view target, and for each view places the
camera, waits 0.75 s and runs `HighResShot <w>x<h>` (scene only: no HUD,
crosshair or UMG/AimMod overlays). The PNG the game writes to
`Saved\Screenshots\WindowsNoEditor` is moved to
`%LOCALAPPDATA%\AimMod\KovaaksNative\thumbnails\<out>` (several views:
`<stem>-1.png`, `<stem>-2.png`, ...). If HighResShot writes nothing in 4 s
it falls back once to `shot` (window resolution). Afterwards the view
target, pawn visibility and camera are restored. The final result is
`done captured {"files":[...]}` (message is that JSON); errors:
`screenshot-unavailable`, `capture-failed`, `timeout`, `challenge-active`,
`busy`, `unknown-scenario`, `invalid-thumbnail`. HTTP: `POST game-command`
with `{"action":"capture-thumbnail","scenario","width","height","out",
"views":[{"x","y","z","pitch","yaw","fov"}]}`.

Result: `AIMMOD_CORE_RESULT_1	<seq>	<accepted|done|error|notice>	<code>	<%-escaped message>`.
Codes: `loading`/`starting` (accepted), `loaded`/`already-loaded`/`started`/`reset`
(done), `challenge-active`, `busy`, `game-unavailable`, `unsupported`,
`unknown-scenario`, `start-failed`, `timeout`, `mode-mismatch`,
`override-failed`, `invalid-*`, `overrides-freeplay-only` (error),
`challenge-cancelled` (notice, sequence 0).

Entry points (verified in the 3.9.11 dumps; resolved at startup, a missing
one disables the capability and the start gate falls back to "load it in
KovaaK's"): the scenario browser's own path `Start_Scenario(InOuter,
InScenarioName, InFromWorkshop, InPlayOnLoad)` + `BlueprintAsyncActionBase:Activate`
(load: InPlayOnLoad false; start: true after
`ScenarioManager:SetPersistentPlayType`), `GetLocalScenarioHash` /
`GetOnlineScenarioHash` to check the scenario exists, and
`PlayCurrentScenario(Challenge, Start)` once if a challenge start did not
begin by itself. Overrides: `GameplayStatics:SetGlobalTimeDilation`,
`MetaGameState:SetMapScale`, `AdaptiveDifficultySystem:Import_OverrideProfile`
(fixed target size/speed multipliers: min = max, no adjustment) and
`WeaponHandler:SetWeaponProfileByString`.

Rules:
- Every field is validated (names: 1-256 bytes, no control characters;
  numbers in range; unknown fields rejected), again in the mod.
- Refused while a challenge is running (leaving it would cancel a ranked
  attempt) and while a scenario loads.
- Ranked safety: KovaaK's submits leaderboard scores only for challenge
  runs (the stats CSV and upload path are challenge-only). Overrides are
  accepted for freeplay only, are reset before any scenario load/start and
  whenever the scenario changes, and if a challenge begins while any
  override is still active AimModCore cancels it (`CancelChallenge`) before
  it can finish or submit, and says so (`challenge-cancelled`).
- Every request and outcome is logged (`game command <seq>: ...`).
- Target size/speed and time scale semantics (adaptive override profile,
  global time dilation) are the first live-verified items of test #4.

## Match play (damage modes)

Deathmatch, vampiric 1v1 and instagib are host-authoritative: each player
reports their shots, the host decides hits, damage and deaths, and every
player's AimModCore applies the host's verdict to its own character.
Capabilities `shots` and `match-play` (the latter drops when the character
bindings are missing).

`self-shots.tsv` (written while `self-shots.request` was touched within the
last 5 s; deleted when the request lapses; atomic replace on every new shot):

```
AIMMOD_SHOTS_1	<publish seq>	<session>
shot	<unix ms>	<shot seq>	<ox>	<oy>	<oz>	<dx>	<dy>	<dz>	<slot>	<target>	<headshot 0/1>	<gameHit 0/1>
tag	<target id>	<stream id>
```

- Shots are detected by polling every weapon's `ShotsFiredThisSession` each
  frame (up to 8 per weapon per frame); `slot` is the weapon's index in
  `WeaponHandler:GetWeapons`. A new character or weapon set only resets the
  baseline. `gameHit` is set for as many shots as `ShotsHitThisSession`
  advanced in the same frame (the game's own verdict, for cross-checks).
- The ray is the camera at the frame the counter advanced (origin in cm,
  unit direction from pitch/yaw). `target` is the nearest visible
  character capsule the ray meets, by self-pose target id (the same ids as
  `self-pose.tsv`), 0 for none; `headshot` = the hit point is in the top
  fifth of the capsule. No world occlusion test: the host checks line of
  sight if the mode needs it. `tag` rows map hit avatars to stream ids
  (`avatars.tsv`).
- The window holds the last 32 shots, at most 3 s old. `shot seq` is
  monotonic within `session` (a new session restarts it); readers dedupe by
  it.

`play-state.tsv` (written by the service, atomic replace; AimModCore polls it
every 15 ms while in use and treats a file not rewritten for 5 s as gone, so
the service rewrites it at least every 2 s):

```
AIMMOD_PLAYSTATE_1	<state seq>
match	<scenario name>
health	<current>	<max>
alive	<0/1>
respawnAt	<unix ms, 0 = none>
protected	<0/1>
hit	<hit seq>	<attacker member id>	<damage>	<headshot 0/1>	<dx>	<dy>	<dz>
```

Any malformed or unknown row rejects the whole file. Applied only when the
current scenario starts with `AimMod Match - `, equals `match`, the game is
in freeplay and not loading; never in a challenge or any other scenario
(leaving the gate releases spawn protection and logs the reason). On each new
`state seq`, through the character's own functions (`MetaCharacter`):

- `protected` -> `OverrideInvulnerable` (`ResetInvulnerable` on release).
- A new `hit seq` -> `HandleDamage(amount, null attacker, origin, 0)` for the
  game's hit effect, with `origin` 1 m back along the hit direction, no
  knockback, and the amount capped below the current health so it is never
  lethal by itself. A hit already present when the gate opens is not replayed.
- `alive` 1 -> 0 -> `OnCharacterKilled`, and `SetRespawnTimer` to
  `respawnAt` when bound; 0 -> 1 -> `Respawn(true)` unless the native timer
  already brought the character back. Where it respawns is the game's choice.
- While alive, health follows `health` (`SetHealth`, checked every 250 ms, so
  local regeneration or damage is undone).

If `HandleDamage`, `SetHealth`, `Respawn`, `OnCharacterKilled`,
`OverrideInvulnerable`, `ResetInvulnerable` or `GetCurrentHealth` is missing on
the character class, match play is disabled for the session and the missing
names are logged.

## Match seeds (shared randomness)

Findings (3.9.11 dumps and imports):
- No reflected `FRandomStream` member or seed property on the scenario,
  spawner (`CSpawnVolume`, `CSpawnVolumeManager`, `CSpawnTargetNPC`) or bot
  classes; spawning and bot movement are native and not reflected (only
  `Scenario:SpawnBots`). `BotBrainComponent` has `Get/SetRandomStream`
  (action choice of the newer bot brain), the only stream in the API.
- Blueprint randomness is marginal (KovLib, FPSCharacter, sky, pitch
  modifier: `RandomInteger`/`RandomFloatInRange`, the global RNG).
- The game imports `rand`/`srand` from the UCRT (api-ms-win-crt-utility), as
  AimModCore does: `FMath::Rand/FRand/RandRange/RandInit` draw from the CRT
  per-thread state of the game thread, which AimModCore can seed from its
  game-thread callbacks. `FMath::SRand` (its own global) is not reachable.

Strategy (b), prototype: `start-scenario` with `seed` seeds the game
thread's CRT RNG with `SeedFor(seed, 0)` when the scenario has started
(and again at each challenge attempt start), and with `SeedFor(seed, n)`
in the pre-hook of the n-th target death/kill (`NotifyCharacterDeath`,
`NotifyPlayerKillCredit`; one event per frame) - right before the game
picks the respawn. Spawns drawn synchronously at a kill are then the same
for every player whatever happened before. Limits: draws made every tick
(continuous bot movement) depend on frame count, so movement can still
diverge between players with different frame rates; a respawn delay lets
per-tick draws run between the reseed and the spawn; `FRandomStream`
users (bot brain) are not affected. Seeding never runs outside the seeded
scenario (it stops on any scenario change or new request) and never in
ranked play (freeplay, or challenge only for generated `AimMod Match - `
scenarios; refused otherwise as `seed-not-allowed`).

Verification: run the scenario twice with the same seed and different
play, then `AimMod.InGame.exe --compare-spawns <rootA> <idA> <rootB> <idB>`
lists both runs' target appearances (new target, reappearance after an
absence, or a jump over 150 cm in one frame) and reports how many match
within 5 cm and where they first differ. If spawns match but movement
does not, the next step is (c): drive spawns from a pre-generated sequence.

## Native service

`Mods\AimModCore\service\AimMod.InGame.exe` is started with
`--exit-with-game` if its singleton mutex (`Local\AimMod.KovaaksNative.History`)
is not held. If it exits while the game runs it is restarted with back-off
(2 s, doubling to 60 s; reset after 5 minutes of uptime). The mod never
kills it; the service exits by itself when the game closes.

## Compatibility summary

At startup and on every world change the mod logs one block:

```
[AimModCore] compatibility (KovaaK's build ..., UE4SS ...):
  lifecycle: poll=ok hooks=2/9 ...
  score: stats-last-score=ok indicator-score=ok
  replay: camera=ok targets=ok inputs=0/20 hooks
  disabled: <feature>: <missing item>
```

## Install

`install/Install-AimModCore.ps1` locates the game through the Steam library
folders, verifies the UE4SS release zip by SHA-256, installs
`dwmapi.dll` + `ue4ss\` + the stable settings + `Mods\AimModCore` (and the
service and Lua UI mod when given), enables them in `mods.txt`, and writes
`ue4ss\aimmod-install.json` with the SHA-256 of every file it placed.
`-Repair` re-applies the manifest after a game update (Steam verification can
remove the proxy); `Uninstall-AimModCore.ps1` removes exactly the files in
the manifest. Nothing is downloaded at runtime.

```
install\Build-AimModPackage.ps1                  # -> out\package (mod, self-contained service, Lua UI, settings)
install\Install-AimModCore.ps1 -Package out\package -Ue4ssZip <UE4SS_v3.0.1-1152-ge3ba1016.zip>
install\Install-AimModCore.ps1 -Repair           # after a game update; uses the cached package
install\Uninstall-AimModCore.ps1                 # restores backed-up files; keeps run data
```

The installer refuses to run while this game install is running, backs up
any file it replaces (`<name>.aimmod-backup`) and restores those on
uninstall; installer-created mod lists and folders are removed.

## Building

```
git clone https://github.com/UE4SS-RE/RE-UE4SS external/RE-UE4SS   # ignored
git -C external/RE-UE4SS checkout e3ba1016                          # game's UE4SS
git -C external/RE-UE4SS submodule update --init --recursive        # UEPseudo needs Epic-linked GitHub access
cmake -S in-game/native-mod -B in-game/native-mod/build -G "Visual Studio 17 2022" -A x64
cmake --build in-game/native-mod/build --config Game__Shipping__Win64 --target AimModCore aimmod_core_tests
```

Core-only (no UE4SS source): configure with `-DAIMMOD_BUILD_MOD=OFF` and
build `Release`. The test runner is `aimmod_core_tests.exe`.

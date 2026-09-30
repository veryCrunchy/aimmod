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
  ReplayWriter   bounded replay buffer/file lifecycle (.partial -> .amreplay)
  Settings       native-settings.tsv parser, core-active handshake line
  Supervisor     service restart back-off policy
mod/      UE4SS glue (built against a local RE-UE4SS checkout)
  GameBindings   name resolution, signature checks, typed getter calls
  Observer       game-thread scheduler, hooks, lifecycle driver, live values
  ReplaySampler  60 Hz camera/target/input sampling into ReplayWriter
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
- End: running -> not running (while not paused) enters `Ending` with a 3 s
  deadline. The attempt completes when `StatsManager:GetLastScore` or
  `GetLastChallengeTimeRemaining` changes from the value captured at start,
  or a completion hook fires, or the deadline passes after the timer ran out
  (<= 0.5 s remaining at the last running poll). Otherwise it is cancelled
  (`quit`). Scenario changes cancel.
- Final score: `StatsManager:GetLastScore` (the value the end screen shows),
  else the last `PerformanceIndicatorsStateReceiver:Get_Score_ValueElse`
  value with `Result == HasValue`. A run without either is not journalled.
  Scores are never computed.

### Values (ValueElse / ValueOr contract)

`*_ValueElse(OutValue, Result)`: trust `OutValue` only when
`Result == EValueElseResult::HasValue (0)`; `Else (1)` means no value (never
carry a previous value through). `*_ValueOr(ValueIfNull)` is used only where
an explicit sentinel is acceptable. Live metrics prefer the performance
receiver (score, shots, hits, kills, damage) and fall back to the local
character's weapon session counters / kill count / damage (read-only
properties), matching the Lua observer.

## Output contract (unchanged formats)

All under `%LOCALAPPDATA%\AimMod\KovaaksNative\`:

- `completed.tsv`: one appended line per completed attempt,
  `run\t<id>\t<scenario>\t<score>\t<accuracy>\t<duration>\t<kills>\t<damage>\t<UTC ISO-8601>`,
  fields `%`-escaped (`%25`, `%09`, `%0D`, `%0A`); read by `NativeRuns.cs`.
- `live-overlay.json`: `{"version":1,"active":..,"paused":..,...}` exactly
  as `Telemetry.lua` wrote it; replaced atomically; rewritten on change and
  at least once per second while the game runs; read by
  `LiveOverlayState.cs` (2 s freshness) and the Lua HUD.
- `replays/<id>.partial` -> `replays/<id>.amreplay`: JSON lines (`header`,
  `frame`, `input`, `end`) identical to `ReplayCapture.lua`, bounded to 64 MB
  / 36000 frames / 128 actors; published only when completed with >1 frame;
  read by `ReplayCatalog.cs`. The replay id equals the journal id.
- `replay-status.json`: capture status for the workspace.
- `core-active.tsv` (new): handshake `AIMMOD_CORE_1\t<version>\t<unix
  seconds>\t<capabilities>` refreshed every second and removed on shutdown.
  The Lua mod steps aside for each capability listed (`telemetry`,
  `replay`) while the stamp is at most 3 s old, and reads the live snapshot
  from `live-overlay.json` for its HUD. When the stamp goes stale (crash,
  uninstall) Lua resumes on its own.
- Shared memory `Local\AimMod.KovaaksNative.Live` (new, optional): a
  seqlock-protected copy of the live-overlay body for readers that want it
  without file polling. The file remains the compatibility path.

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

# AimMod inside KovaaK's

The UE4SS module adds an AimMod entry to the pause header and replaces its logo
with the current AimMod asset. The workspace uses an owned Gameface view in the
renderer shipped for KovaaK's newer stats UI. It uses neither Tauri nor osu code.

Start the .NET 8 worker in `in-game/native-service` before loading the mod.
It creates private runtime files below `%LOCALAPPDATA%/AimMod/KovaaksNative`.
Existing desktop history is opened read-only; `--history` and `--output`
override its standard paths. Never put runtime data in source control.

## Components

- Menu.lua: pause-menu container, header entry, branding and reload cleanup.
- Workspace.lua: private loopback UI and visibility notifications.
- Telemetry.lua: post-observed metrics and completed-run journal.
- ReplayCapture.lua: bounded camera, target-state and input-event recording.
- Native worker: local/Hub history, encrypted account linking, coaching, replay
  loading, snapshots and embedded UI resources.

The local server binds only a dynamic IPv4 loopback port. Routes require a
random per-process capability path and expose no general file access. Account
and pending-link credentials are protected with Windows DPAPI.

## Runtime

API baseline: UE4SS v3.0.1-1125-g527a483b, KovaaK's 3.9.8, Unreal 4.26.
Enable AimModNativeUI in the selected mod list without replacing other entries.
Enable the engine game-thread scheduler. Prefer file-based Lua reload: Ctrl+R
also restarts a KovaaK's scenario. Ctrl+F8 or the header entry toggles AimMod.

No callback replaces scores, submits scores, or injects gameplay input.
Hub previews fill gaps without overwriting richer local records. Hub currently
caps scenario history without pagination; coverage can be incomplete.

## Replays and limits

Replays reconstruct recorded state, not screen recordings. See
[replay documentation](../replay/README.md) for capture and fidelity boundaries.
The experimental Unreal viewport adapter stays separate until live isolation,
rendering and teardown are verified. Exact scene replays, full original AimMod
feature parity and automatic runtime installation remain incomplete.

## Checks

Run `dotnet run --project in-game/native-service -- --self-test` from the repo
root and `node --test in-game/ui/compat.test.cjs in-game/replay/*.test.cjs`.
Verify layout in the game renderer: this build lacks clientWidth/clientHeight,
standard table layout and full Intl number/date options. Automated checks do
not establish successful live recording or rendering.

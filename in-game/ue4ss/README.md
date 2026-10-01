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
- DiscordPresence.lua: hands KovaaK's own Discord presence to the worker and back.
- Native worker: local/Hub history, encrypted account linking, coaching, replay
  loading, snapshots, Discord presence and embedded UI resources.

The local server binds only a dynamic IPv4 loopback port. Routes require a
random per-process capability path and expose no general file access. Account
and pending-link credentials are protected with Windows DPAPI.

## Runtime

API baseline: UE4SS v3.0.1-1125-g527a483b, KovaaK's 3.9.8, Unreal 4.26.
Enable AimModNativeUI in the selected mod list without replacing other entries.
Also observed on KovaaK's 3.9.11 (Steam build 25635011) with UE4SS
v3.0.1-1152. The mod requires the engine game-thread scheduler and disables
itself with a log line when a required UE4SS global is missing. Prefer
file-based Lua reload: Ctrl+R also restarts a KovaaK's scenario. The header
entry toggles AimMod; the mod registers no key binds.

Challenge lifecycle is observed from every available source: the legacy
AnalyticsManager routes (3.9.8 and earlier) and the framework
ScenarioBroadcastReceiver broadcasts (3.9.11). The bound sources are logged
once at startup. Each observer registers independently, so a missing event
degrades only its feature. Broadcast completion has no parameters; the final
score is the game's own score broadcast or indicator value, never computed.
The mod does not use the UE4SS LoadMap, InitGameState or BeginPlay/EndPlay
hooks, which fail on 3.9.11.

No callback replaces scores, submits scores, or injects gameplay input.
Hub previews fill gaps without overwriting richer local records. Hub currently
caps scenario history without pagination; coverage can be incomplete.

## Discord presence

KovaaK's 3.9.11 publishes its own presence from a background thread in the
game (its own client for the local `discord-ipc-N` pipe, no Discord DLL). The
thread runs while the game's "Discord Rich Presence" setting is on: turning it
off clears the activity and closes the connection, turning it on reconnects.
The setting is exposed as the static reflected functions
`MetaGameUserSettings:GetDiscordRichPresence` / `SetDiscordRichPresence`.

AimMod replaces that presence without hooking the game:

1. The worker, while its Discord presence is on, refreshes
   `discord-takeover.tsv` every 2 s.
2. DiscordPresence.lua sees a fresh request, records `discord-restore.tsv`,
   turns KovaaK's switch off and acknowledges `released` in
   `discord-game.tsv` (refreshed every second).
3. Only while it reads `released` does the worker connect with the AimMod
   application and publish. It sends at most five updates per 20 s and
   reconnects with back-off.
4. When the worker turns its presence off or stops, it closes its connection
   first and then withdraws the request. When the request is withdrawn or more
   than 6 s old, the mod turns KovaaK's switch back on and deletes the restore
   file. A leftover restore file (crash) is applied on the next launch.

If KovaaK's own presence is already off, the mod reports `off` and nothing is
published. While AimMod holds the switch, turning it on in KovaaK's menu is
turned off again within a second; use AimMod's Settings > Discord to go back to
KovaaK's presence. Builds without the switch report `unavailable` and keep
their own presence. The switch is the game's own persisted user setting, so a
game crash while held leaves it off until the mod next starts; uninstalling
AimMod at that point needs the option turned back on in KovaaK's settings.

The worker logs each handoff state change, Discord connect and READY (the
Discord user is never logged), every SET_ACTIVITY with Discord's result or
error, dropped buttons and reconnect delays with a `[Discord]` prefix. While
the AimMod panel is shown the UI reports its page, so the presence reads for
example "In AimMod · Statistics" or "Browsing replays". To check the pipeline
without the game, run the worker with `--discord-test` (optionally
`--discord-test-seconds N`): it publishes a sample activity, prints Discord's
replies with the user redacted, then clears it.

In a multiplayer lobby the presence shows "In lobby · 2/4 · <mode>" with a
Discord party (`party.size`), and in a match the mode, scenario, round and the
lead over the best other player ("Round 2/4 · Leading by 1,200"). `party.id` is
a hash of AimMod's random lobby id. While the lobby has room, isn't invite only
and isn't mid-match (unless late join is on), the presence carries
`secrets.join`, so Discord offers Join / Ask to Join; Discord does not allow
buttons alongside a secret, so the Hub button is left out then. The secret is a
one-way hash of the Steam lobby token. A player who joins from Discord receives
it as `ACTIVITY_JOIN` on their own AimMod's Discord connection, which matches it
against the joinable AimMod lobbies of their Steam friends and joins through
Steam, so Discord joins reach the same people as Steam's "Join Game" and the
lobby's privacy still applies. Ask-to-join requests (`ACTIVITY_JOIN_REQUEST`)
are accepted while a join is offered and declined otherwise; the asking user is
never logged. A `steam://joinlobby` button was not used because it would publish
the Steam lobby and host ids. Joining from Discord needs the joining player's
AimMod running with its Discord presence on. Settings > Discord can hide the
lobby and match details and turn the join off.

## Replays and limits

Replays reconstruct recorded state, not screen recordings. See
[replay documentation](../replay/README.md) for capture and fidelity boundaries.
The experimental Unreal viewport adapter stays separate until live isolation,
rendering and teardown are verified. Exact scene replays, full original AimMod
feature parity and automatic runtime installation remain incomplete.

## Checks

Run `dotnet run --project in-game/native-service -- --self-test` from the repo
root and `node --test in-game/ui/compat.test.cjs in-game/replay/*.test.cjs`.
Run each `in-game/replay/*.test.lua` from that directory with a Lua 5.3 or
5.4 interpreter.
Run each `in-game/replay/*.test.lua` from that directory with a Lua 5.3 or
5.4 interpreter.
Verify layout in the game renderer: this build lacks clientWidth/clientHeight,
standard table layout and full Intl number/date options. Automated checks do
not establish successful live recording or rendering.

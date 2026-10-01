# Multiplayer and PvP for AimMod on KovaaK's

Status: research, plus a read-only feasibility probe (`in-game/steam-probe`).
Nothing here ships in AimModCore yet.

Target: KovaaK's 3.9.11 (Steam AppID 824270, UE4). Game paths below are
relative to the game's install folder, written `<game>`.

## Summary

- **Steam networking works for us, and we don't need the game's help.** The game
  ships the Steamworks SDK 1.47 `steam_api64.dll`, and SteamAPI is initialised
  in-process under AppID 824270. Every interface we need resolves by version
  string through `SteamInternal_FindOrCreateUserInterface`, including ones newer
  than the game's SDK: `SteamNetworkingSockets012`, `SteamNetworkingMessages002`,
  `SteamNetworkingUtils004`, `SteamFriends018` and `SteamUser023`. The installed
  Steam client provides them. The relay network (SDR) serves a config for
  824270. The probe measured 25 valid relays and 36 points of presence.
- **The game ships unused Steam multiplayer code.** It includes a Duels mode
  behind `IsDuelsEnabled`, built on UE replication, Steam lobbies (the UWorks
  plugin and OnlineSubsystemSteam), and legacy `ISteamNetworking` P2P. The
  developer confirmed the game uses Steamworks only for the Workshop.
  - Use `ISteamNetworkingSockets` P2P on a dedicated virtual port.
  - Namespace lobby keys `aimmod.*` and never set UE's session keys.
  - AimMod takes over Steam invites. That can be rich-presence invites, or
    friends-only lobby invites once the kit confirms the game's leftover
    handlers stay inert.
- **Recommended design:** a thin Steam bridge in C++ inside the game process. It
  only moves bytes and never touches gameplay. The native service owns lobby,
  match and verification state, and the Gameface UI shows it. Transport is SDR
  P2P with relay-only routing, so peers never learn each other's IP. The
  fallback is a Hub WebSocket relay that uses the same message schema.
- **The developer has given permission.** The KovaaK's developer allows
  decompiling and modifying the game, provided we don't touch the ranked
  leaderboard system in an unfair way. AimMod PvP results therefore stay
  completely separate from KovaaK's ranked leaderboards. PvP stays opt-in, with
  a switch that forces Hub-only transport.

## 1. Steam access from our mod

### 1.1 What the game ships

| Item | Finding |
| --- | --- |
| DLLs | `FPSAimTrainer\Binaries\Win64\steam_api64.dll` and `Engine\Binaries\ThirdParty\Steamworks\Steamv147\Win64\steam_api64.dll`, byte-identical (SHA-256 `728D0D3A…C9C944`, file version 5.53.33.78) |
| `steam_appid.txt` | `824270` |
| SDK generation | 1.47. The DLL has `ISteamTV`, `ISteamRemotePlay` and `ISteamParties`, and the old flat API that takes an instance pointer (`SteamAPI_ISteamUser_GetSteamID(intptr_t)`, 813 `SteamAPI_*` exports). |
| Missing from the DLL | versioned accessors (`SteamAPI_SteamMatchmaking_v009` …), `SteamAPI_ManualDispatch_*`, and all flat `ISteamNetworkingSockets/Messages/Utils` functions |
| Present | `SteamAPI_GetHSteamUser/Pipe`, `SteamInternal_FindOrCreateUserInterface`, `SteamInternal_ContextInit`, `SteamAPI_RegisterCallback/UnregisterCallback/RegisterCallResult`, flat `ISteamMatchmaking_*` (38), `ISteamFriends_*` (73, incl. `SetRichPresence`, `ActivateGameOverlayInviteDialog`), `ISteamUser_*` (incl. `GetAuthSessionTicket`), `ISteamUtils_*` (incl. `IsAPICallCompleted`, `GetAPICallResult`), `ISteamApps_GetLaunchCommandLine/GetLaunchQueryParam`, `ISteamNetworking_*` (legacy P2P) |
| Interface strings in the DLL | `SteamClient017`, `SteamClient020`, `SteamUser020`, `SteamUtils009`, `SteamGameServer013`, `SteamController007`, `SteamInput001` |
| Interface strings in the game exe | `SteamUser020`, `SteamFriends017`, `SteamMatchMaking009`, `SteamMatchMakingServers002`, `SteamNetworking006`, `SteamUtils009`, `STEAMAPPS_INTERFACE_VERSION008`, `STEAMUSERSTATS_INTERFACE_VERSION011`, `STEAMUGC_INTERFACE_VERSION014`, `STEAMREMOTESTORAGE_INTERFACE_VERSION014`, … No `SteamNetworkingSockets*`, `SteamNetworkingMessages*` or `SteamNetworkingUtils*` string anywhere, so **the game itself doesn't use the new networking stack** |

### 1.2 Probe results (standalone harness, stage 1)

The harness loads the game's `steam_api64.dll` into its own process and inits
Steam under 824270. It runs the same checks the in-game mod runs:

```
steam_api64.dll: loaded, file version 5.53.33.78, IsSteamRunning=1
flat api: versioned accessors=no manual dispatch=no networking-sockets flat=no
SteamAPI: initialised (user handle 1, pipe 1)
SteamUser: SteamUser023=yes SteamUser021=yes SteamUser020=yes
SteamFriends: SteamFriends018=yes SteamFriends017=yes
SteamMatchMaking: SteamMatchMaking009=yes
SteamUtils: SteamUtils010=yes SteamUtils009=yes
SteamNetworking (legacy P2P): SteamNetworking006=yes
SteamNetworkingSockets: SteamNetworkingSockets012=yes SteamNetworkingSockets009=yes SteamNetworkingSockets008=yes
SteamNetworkingMessages: SteamNetworkingMessages002=yes
SteamNetworkingUtils: SteamNetworkingUtils004=yes SteamNetworkingUtils003=yes
user: logged on=1 steamid=...NNNN (type=individual universe=public)
app: AppID=824270
networking utils: vtable check ok
relay (initial): avail=Waiting config=Attempting anyRelay=Waiting pinging=1 msg="Attempt #1 to fetch config (only-if-cached) from https://api.steampowered.com/ISteamApps/GetSDRConfig/v1?appid=824270"
p2p identity cert: Attempting "Requesting cert"
relay: warm-up requested (InitRelayNetworkAccess; contacts Valve relays only)
relay (after warm-up): avail=Current config=Current anyRelay=Current pinging=0 msg="OK.  Relays: 25 valid, 10 great, 11 good+, 18 ok+, 6 ignored"
relay: 36 points of presence known
callbacks: PersonaStateChange x2, first dispatch on game thread   (harness: its own pump thread)
```

Notes:

- Creating the `SteamNetworkingSockets` interface is enough to make the Steam
  client start fetching the SDR config and requesting a P2P certificate. No
  player traffic is involved.
- The in-game mod runs the same checks against the game's own SteamAPI
  instance (section 5). See 1.2.1 for its results.
- `steam_api64.dll` prints the full SteamID to the harness's stdout. Don't paste
  harness output into public issues without redacting it.

### 1.2.1 In-game results (stage 1, KovaaK's 3.9.11)

The mod ran inside the game against the game's own SteamAPI instance:

- `steam_api64.dll` 5.53.33.78 was loaded, and SteamAPI had been initialised
  by the game (user handle 1, pipe 1).
- Every interface resolved: `SteamUser023`, `SteamFriends018`,
  `SteamMatchMaking009`, `SteamUtils010`, `SteamNetworking006`,
  `SteamNetworkingSockets012`, `SteamNetworkingMessages002` and
  `SteamNetworkingUtils004`. The vtable check was OK.
- Logged on as an individual in the public universe, AppID 824270.
- At start-up (no warm-up) the relay was `avail=Waiting config=Attempting`, and
  the P2P cert was `Requesting cert`.
- Callbacks: `PersonaStateChange` x5 and `SteamRelayNetworkStatus` x4, with
  the **first dispatch on the game thread**. No lobby or rich-presence join
  requests.

So in 3.9.11 the game pumps `SteamAPI_RunCallbacks` on the game thread. The
bridge still treats handlers as "any thread", because an update could move
the pump.

### 1.3 How to resolve interfaces: flat exports and version strings, not OnlineSubsystemSteam

Use `SteamAPI_GetHSteamUser()` and
`SteamInternal_FindOrCreateUserInterface(hUser, "<Interface><NNN>")`:

- It's what Valve's own inline accessors do. It forwards to the Steam client,
  which keeps every published interface version working. So we pin the
  versions we compile against, not the ones the game was built with.
- For interfaces that have a flat export in the 1.47 DLL (matchmaking, friends,
  user, utils, apps), call the flat function with the interface pointer. It's
  ABI-stable, and a `CSteamID` travels as a `uint64` in a register.
- For newer interfaces (`SteamNetworkingSockets012`, `SteamNetworkingMessages002`,
  `SteamNetworkingUtils004`, `SteamFriends018`), call through the vtable.
  Declare it in the exact order of the public Steamworks header. See
  `steam-probe/src/SteamAbi.hpp`. It needs no SDK source and matches the
  Steamworks headers, where `InitRelayNetworkAccess` is an inline helper, not a
  vtable slot. Before relying on the rest, the probe calls one harmless slot
  (`GetLocalTimestamp`) as a layout check.
- Never call `SteamAPI_Init`, `SteamAPI_Shutdown` or `SteamAPI_RunCallbacks`
  in-process. The game owns them. The mod only checks that
  `GetHSteamUser()`/`GetHSteamPipe()` are non-zero, and it never loads
  `steam_api64.dll` itself.

Why not OnlineSubsystemSteam or UWorks:

- `FOnlineSubsystemSteam` and its session, identity and friends interfaces are
  plain C++ objects with no reflection. From UE4SS we'd need the shipping
  binary's C++ ABI, which breaks on every game build.
- UWorks exposes lobby nodes as UObjects, which reflection can reach. But their
  delegates are the ones the game's Duels UI binds, so reusing them would put
  our traffic into the game's handlers. The OSS also ties lobbies to UE
  sessions (`OWNINGID`, `SESSIONFLAGS`, `P2PADDR`, `BUILDID`), which is exactly
  what we want to stay out of.
- Both are pinned to the 1.47 interfaces and have no `ISteamNetworkingSockets`
  at all.

### 1.4 Threading and callbacks

- **Who pumps.** The game has OnlineSubsystemSteam
  (`OnlineAsyncTaskThreadSteam %s` is in the exe), and in UE4 its async task
  thread calls `SteamAPI_RunCallbacks`. UWorks may pump too. So callbacks can
  arrive on a thread that isn't the game thread. The in-game probe measured
  the game thread on 3.9.11 (see 1.2.1). Handlers are still written for "any
  thread".
- **Listening doesn't steal.** `steam_api` broadcasts each callback to every
  registered `CCallbackBase`. Our listeners (`SteamAPI_RegisterCallback`) sit
  next to the game's without consuming anything. The probe does exactly this
  for `PersonaStateChange`, `SteamRelayNetworkStatus`,
  `GameLobbyJoinRequested` and `GameRichPresenceJoinRequested`.
- **Call results.** Poll our own `SteamAPICall_t` handles with
  `ISteamUtils::IsAPICallCompleted` and `GetAPICallResult` on our worker
  thread. The game has no `CCallResult` registered for handles it didn't
  create, so nothing is taken from it. Stage 2 does this for `CreateLobby`.
- **Never manual dispatch.** `SteamAPI_ManualDispatch_*` doesn't exist in 1.47.
  It's also process-global, so turning it on would break the game's own
  dispatch. We must never call `SteamAPI_RunCallbacks` ourselves either,
  because that would run the game's handlers on our thread.
- **Rules for the bridge.** Callback handlers are lock-free and only enqueue
  (SteamID, event, small payload). One AimMod network thread does all Steam
  calls: `ReceiveMessagesOnPollGroup`, `SendMessageToConnection`, lobby calls
  and call-result polling. The game thread is never involved. Listeners are
  registered once and unregistered in the mod's destructor. The objects are
  never freed while registered.

## 2. The game's own networking

Evidence, from strings in `FPSAimTrainer-Win64-Shipping.exe`, the pak index and
the 3.9.11 reflection dump:

- **Duels mode:** `UDuels`, `IsDuelsEnabled`, `GetDuelsLobbyUEMapName`, the
  `DuelsMain` map, `DuelsChallengeTimeLimiterServer`,
  `DuelsChallengeTimeLimiterClient` and `DuelsChallengeTimeLimiterInterface`
  (reflected classes in `/Script/GameSkillsTrainer`), and the UI assets
  `DuelsGameplayTimeout`, `DuelsPostChallengeReturnToLobby` and
  `DuelsPostConfirmQuit`. Having server and client classes means Duels uses UE
  replication with a listen server.
- **Steam plumbing:** `OnlineSubsystemSteam` and its async tasks (create, join
  and find lobbies, invite accepted, rich presence), `OnlineSubsystemUWorks`
  with `UIpNetDriverUWorks`, `USteamNetDriver` and `USteamNetConnection`
  (legacy `ISteamNetworking006` P2P via `SteamSockets`), `bUseSteamNetworking`,
  and `bAllowP2PPacketRelay`.
- **UWorks lobby nodes and delegates:** `CreateLobby`, `JoinLobby`,
  `RequestLobbyList`, `SetLobbyData`, `LobbyEnter`, `LobbyChatMsg`,
  `LobbyDataUpdate`, `LobbyInvite`, `GameLobbyJoinRequested`, `LobbyKicked`.
- **UE online framework:** Lobby and Party beacons (`ALobbyBeaconHost/Client`,
  `APartyBeacon*`), `USocialParty`, and the slash commands `InviteToParty` and
  `JoinParty`.
- **Session keys and launch handling:** `+connect` and `+connect_lobby` (OSS
  join-string parsing). `OWNINGID`, `P2PADDR`, `SESSIONFLAGS` and `BUILDID`
  (OSS lobby keys). `USteamEventManager::HandleNewLaunchQueryParameters` and
  `GetLaunchQueryParam`.
- **Other:** `UDiscordRichPresenceManager` (the game's own Discord RPC), the
  Anybrain SDK and Xsolla.

We don't know whether Duels is live for players. `IsDuelsEnabled` looks like a
server-side or config flag. Either way, we have to assume the game's handlers
are registered and that other KovaaK's clients may search for lobbies under
824270.

### Avoiding collisions

| Surface | What the game uses | What AimMod does |
| --- | --- | --- |
| Transport | legacy `ISteamNetworking` P2P (UE net driver), UE ports and channels | `ISteamNetworkingSockets012` P2P (`ConnectP2P` / `CreateListenSocketP2P`) on one dedicated virtual port, e.g. `0x414D`. This is a separate namespace from legacy P2P channels. The game registers no `SteamNetConnectionStatusChangedCallback_t` (1221) handler because it has no sockets code. We open the listen socket only while in a match, and accept only SteamIDs the match expects. The first message is a handshake (`AMP1`, protocol version, match id); on any mismatch we close the connection. Avoid `ISteamNetworkingMessages`: its channels are closer to the legacy model and it's easier to misroute. |
| Lobby metadata | `OWNINGID`, `SESSIONFLAGS`, `P2PADDR`, `BUILDID` + UE session settings | Keys are only `aimmod.proto`, `aimmod.match`, `aimmod.scenario`, `aimmod.state`. We never set UE keys, so the OSS session search treats our lobbies as invalid and skips them. |
| Lobby visibility | UWorks and OSS `RequestLobbyList` | Only `FriendsOnly` (the friends flow) or `Private` lobbies. Never `Public`, and never `Invisible` (Invisible lobbies **are** returned by searches). Call `SetLobbyJoinable(false)` once the match is full. Public matchmaking goes through the Hub, not Steam lobby search. |
| Invites | OSS and UWorks register `GameLobbyJoinRequested_t`, `LobbyInvite_t` and `GameRichPresenceJoinRequested_t` handlers, and the OSS parses `+connect_lobby`, `+connect` and `SteamConnectIP=`. The developer confirmed the game uses Steamworks only for the Workshop, so nothing acts on these, but the handlers are still compiled in. | AimMod owns invite handling (see "Steam invites" in section 4): either rich presence `connect=-aimmodjoin=aimmod:<ver>:<room>` with `InviteUserToGame`, or a friends-only lobby with `InviteUserToLobby`. Before the lobby path ships, the kit's test 3 must confirm that the game's handlers stay inert. |
| Rich presence | the game may set keys through UWorks `SetRichPresence` | Set only `connect` (and maybe `status`), and only while in an AimMod match. Clear only the keys we set. `steam_display` needs localization tokens that the developer uploads, so we can't set custom display text. |
| Lobby chat | UWorks `LobbyChatMsg` delegate | Not used. Ready state lives in lobby member data (`aimmod.ready`). |

## 3. Risks

### Steamworks and the Steam Subscriber Agreement

Allowed, or at least not in question:

- Using Steam in a game you own. The Steam client serves our requests under
  824270 like any other client code in the process, and the SDR config is
  served for 824270 (measured).
- Showing SteamIDs to lobby or match peers. That's inherent in Steam lobbies
  and P2P.

- **Developer permission.** The KovaaK's developer has given permission to
  decompile and modify the game freely, provided we don't touch the ranked
  leaderboard system in an unfair way. That covers our use of the game's
  AppID for lobbies, relays, rich presence and invites.

Constraints that follow from the permission:

- AimMod PvP results are kept completely separate from KovaaK's ranked
  leaderboards. We never submit, alter, block or reorder ranked scores, and a
  PvP result never becomes a KovaaK's leaderboard entry.
- A run inside a PvP match is still an ordinary KovaaK's run. Whatever the game
  itself submits for it is untouched.

Remaining grey areas:

- Valve's Steamworks terms are between Valve and the developer. The
  developer's permission is what makes our use of their AppID acceptable.
  Valve could still act on abuse, for example flooding lobbies.
- Our lobbies count toward the game's usage. If our lobbies were ever
  misfiltered, they'd appear to the game's Duels code.
- The Hub can't verify Steam auth tickets for 824270. `ISteamUserAuth/AuthenticateUserTicket`
  and encrypted app tickets need the AppID owner's publisher key. The Hub
  verifies players through its own account linking. Inside a match, SDR P2P
  connections are certificate-authenticated by Steam, so the remote SteamID of
  a `ConnectP2P` connection can be trusted.

Actions: ship PvP opt-in and off by default. Provide a switch that forces
Hub-only transport. Keep AimMod PvP results separate from KovaaK's ranked
leaderboards, and never touch KovaaK's ranked submission.

### Anybrain

- `anybrainSDK.dll` exports `AnybrainStartSDK/StartMatch/RawInputCallback/OnViewAngleUpdate/OnHit/OnKill/…`
  and imports `HID`, `SETUPAPI`, `IPHLPAPI` and `bcrypt`. It is behavioural,
  analysing input and view angles, and fingerprints hardware. We found no
  static imports of process or module enumeration, but it could resolve those
  at run time, so we can't rule it out.
- The game's log strings show the SDK is started with a Steam auth ticket,
  and a match only starts for ranked (workshop leaderboard) scenarios. Examples:
  `ranked scenario start; scheduling StartMatch…` and
  `scenario start not ranked (freeplay); no match started.`
- The game forwards raw mouse blocks to it (`AnybrainRawInputCallback`).
- Our position: the network bridge never touches input, gameplay objects or
  timing. It adds one worker thread and a little socket traffic through the
  Steam client, whose sockets live in `steamclient64.dll`. A PvP run is still
  a normal run, so on a ranked scenario Anybrain sees it as usual. Don't ship
  anything that injects input or auto-starts runs.
- `GetAuthSessionTicket` calls are independent per caller. We must never call
  `CancelAuthTicket` on handles we didn't create.

### KovaaK's updates

- Interface versions are pinned by string and served by the Steam client, so
  a game update doesn't break them. If the game moves to SDK 1.48 or later,
  the flat names we use (`SteamAPI_ISteamUser_GetSteamID`, …) keep the same
  name and ABI. `SteamInternal_FindOrCreateUserInterface` and
  `SteamAPI_GetHSteamUser` still exist. At start-up the bridge logs a
  compatibility line, as AimModCore does, and turns itself off if any export
  is missing.
- If KovaaK's ships Duels on `ISteamNetworkingSockets`, virtual ports could
  collide. The unusual port plus the handshake contain that risk. Re-run the
  probe after each game update.
- If KovaaK's ships its own PvP, revisit this whole design.
- The in-process vtable declarations are the fragile part. They're pinned to
  specific interface versions, which Valve keeps binary-compatible, and they're
  checked once at start-up.

### Privacy

- Relay-only: on every P2P connection, set
  `k_ESteamNetworkingConfig_P2P_Transport_ICE_Enable` to `Disable`. Traffic
  then goes only over SDR, and peers never learn each other's IP. The Steam
  default follows the user's "Steam Networking" preference, which can allow
  direct (ICE) connections and expose IPs.
- SteamIDs are shared only with match peers and, with consent, the Hub.
  Friend lists are never uploaded. Logs redact SteamIDs and lobby IDs to their
  last 4 digits (see `probe::RedactSteamId`).
- The fallback Hub relay sees client IPs, as any server does. Peers still
  don't.

## 4. Architecture proposal

```
 KovaaK's process                                   AimMod native service (.NET 8)
 ┌──────────────────────────────────────────┐       ┌─────────────────────────────────┐
 │ AimModCore (existing, read-only)         │ files │ run journal, live overlay       │
 │   lifecycle, score, shots, camera ───────┼──────►│                                 │
 │                                          │       │ Match engine (new)              │
 │ AimModNet (new, thin Steam bridge)       │ pipe  │   lobby / ready / countdown     │
 │   own worker thread, no game objects     │◄─────►│   live score + ghost streams    │
 │   lobbies, rich presence, SDR P2P        │       │   verification, results → Hub   │
 │   passive callback listeners (enqueue)   │       │ Transport: SteamBridge | HubWS  │
 └──────────────────────────────────────────┘       └──────────────┬──────────────────┘
            ▲ Steam client (SDR relays)                            │ loopback HTTP
            ▼                                                      ▼
      other AimMod players                                 Gameface in-game UI
```

### Where the code lives

- **C++ `AimModNet`** is a small module. It could be a separate UE4SS mod so a
  fault disables only PvP, or a module in AimModCore. It only makes Steam
  calls: lobby create, join, leave, data and member data; rich presence; the
  overlay invite; `ConnectP2P` and the listen socket; send and receive; relay
  status. It knows nothing about matches. It talks to the service over a named
  pipe with length-prefixed messages (`lobby.*`, `peer.connected`,
  `peer.message`, `send`). It sends live score from AimModCore's observer
  directly, so it doesn't add latency through files.
- **The service** owns the match state machine, clock sync, verification and
  Hub calls. Transport sits behind an interface (`SteamBridgeTransport`,
  `HubRelayTransport`) that carries the same message schema.
- **The UI** is lobby, ready check, countdown, live scoreboard and results.
  All of it is rendered from service state over the existing loopback server.
- **An out-of-process alternative** is feasible. The harness shows that a
  second process can init Steam under 824270 alongside the game. That would
  take Steam networking out of the game process entirely. The costs: two
  processes register for the same app, overlay join requests go to the game
  process, and the account shows the game "launched" twice. Keep this as a plan
  B if in-process Steam calls become a concern.

### Discovery and invites

1. **Friends.** Steam invites handled by AimMod; see "Steam invites" below.
2. **Hub.** The Hub hands out a room code. Players join by code or through
   Hub matchmaking (by scenario and skill band). The Hub exchanges the players'
   SteamIDs, which were linked with consent. Peers then `ConnectP2P` directly,
   with no Steam lobby needed. Hub matchmaking is where public queues belong.
3. **Discord.** Join secret = Hub room code, which the
   `feat/kovaaks-discord-rpc` work can carry. The service resolves it through
   the Hub and then continues as in 2.

### Steam invites

The KovaaK's developer confirmed that the game uses Steamworks only for the
Workshop and nothing multiplayer, so AimMod can take over Steam invites. Two
mechanisms are viable. Both are in the test kit.

**A. Rich-presence game invite (preferred first step).**

- The host sets rich presence `connect=-aimmodjoin=aimmod:<version>:<room>`
  and `status="In an AimMod match (needs AimMod to join)"`.
- It then invites through `InviteUserToGame(friend, <same string>)` (flat
  export in 1.47) or `ActivateGameOverlayInviteDialogConnectString`
  (`SteamFriends018`, vtable). Friends also get "Join Game" on the host in the
  friends list.
- **Friend running KovaaK's:** `GameRichPresenceJoinRequested_t` (337) fires
  with the string, and AimModCore's listener handles it. The game's own OSS
  handler also receives it. It only acts on `SteamConnectIP=` and otherwise
  logs "Failed to parse connection URL" and returns, so it ignores our string.
- **Friend not running KovaaK's:** Steam starts the game with the connect
  string appended to its command line.
- **Why the `-` prefix.** UE's `StartGameInstance` treats a first command-line
  token that doesn't start with `-` as the startup map URL. `aimmod:…` alone
  could make UE try to "browse" to it before falling back to the default map.
  A `-aimmodjoin=` switch is ignored by UE and by the OSS, which only looks for
  `+connect` and `+connect_lobby`.
- **Custom status text.** `steam_display` only works with localization tokens
  that the developer uploads for AppID 824270. Without them, friends see the
  normal "Playing KovaaK's". The `status` key shows up in Steam's "view game
  info". Custom `steam_display` text needs the developer to add tokens.

**B. Friends-only lobby invite.**

- The host creates a `FriendsOnly` lobby with only `aimmod.*` keys and calls
  `InviteUserToLobby` or opens the overlay invite dialog.
- **Friend running:** `GameLobbyJoinRequested_t` (333) fires. AimModCore joins
  by lobby ID.
- **Friend not running:** Steam starts the game with `+connect_lobby <id>`.
  UE's OSS also parses that at start-up and runs its own
  find-lobby-for-invite task. With our keys missing, the task should fail
  quietly. Test 3 in the kit checks that empirically, both with the game
  running and with it launched by the invite: the menu loads normally, and
  there's no travel or Duels map. If it doesn't stay inert, AimModCore must
  consume the request first. The fallback is to use only variant A.
- Lobby membership is also a natural place for ready state and member data.

**Friends without AimMod:**

- **Variant A:** the game launches normally or ignores the invite, and the
  status text tells them AimMod is required.
- **Variant B:** the same, provided test 3 shows the OSS stays inert.

**How AimModCore consumes the launch case:**

- At `on_unreal_init`, read `GetCommandLineW()` once. Don't use
  `ISteamApps::GetLaunchCommandLine`, which only covers `steam://run` URLs.
- Parse `-aimmodjoin=<value>` (variant A) or `+connect_lobby <id>`
  (variant B).
- Validate the `aimmod:<version>:<room>` format and version. Hand it to the
  service as a pending join, which the UI confirms after the menu has loaded.
  If it's already running, a 337/333 event goes through the same path.
- Never act on it before SteamAPI is initialised.
- Log only a redacted room or lobby ID.

### Match flow

1. **Scenario.** The host picks a scenario. The lobby or room carries the
   scenario name and a content hash of the scenario file. Each member reports
   `aimmod.scenarioOk` once its local copy matches. A mismatch blocks the ready
   check.
2. **Ready check.** Every member sets `aimmod.ready=1`. The host or Hub
   confirms.
3. **Clock sync.** Each peer runs 4–8 ping exchanges with the host and uses
   the minimum-RTT sample for its offset. This is NTP-style and accurate to a
   few ms over SDR.
4. **Countdown.** The host announces `start_at = host_time + 5 s`. Each client
   shows a synchronised countdown, and each player then starts the scenario
   themselves. The mod stays read-only for gameplay: it never starts runs or
   sends input. AimModCore's lifecycle start event gives each player's real
   start time. Races compare whole runs, and ghosts are aligned on run-relative
   time, so a start skew of a few hundred ms is harmless. A late start (more
   than 3 s) is flagged.
5. **Live.**
   - Score frames at 10 Hz: run time, score, shots, hits, kills, time left.
     About 32 bytes each.
   - An optional ghost crosshair: yaw and pitch at 30 Hz, quantized to 16 bits
     each, batched every 100 ms.
   - Score and ghost frames are unreliable-no-delay. Events (start, end, quit,
     results) are reliable.
6. **Results.** Each client sends its final score (`StatsManager` last score,
   exactly as AimModCore journals it), a run summary, and the hash of its
   replay file. Every peer shows a provisional result, and it becomes final
   once it is verified.

**Bandwidth per peer:**

| Stream | Rate |
| --- | --- |
| Score | about 0.5 KB/s with SDR overhead |
| Ghost crosshair | about 0.3 KB/s |
| Full camera replay stream at 60 Hz (optional, spectating) | about 2 KB/s |

A 1v1 match with ghosts stays under 1 KB/s each way. That's far below any
limit and needs no physics or state sync.

### Anti-tamper

Peers are untrusted, because anyone can patch a mod. Checks, in increasing
strength:

1. **Mutual checks,** which cost nothing and run on every client:
   - The score stream only rises where the scenario allows it.
   - `hits <= shots`, and the kill count agrees with the score.
   - The final score equals the last live frame (within one frame).
   - The run duration matches the scenario's time limit.
   - The start time matches the countdown.

   Any failure marks the result "disputed".
2. **Replay exchange.** After the match, peers swap replay hashes, and on
   request the replay itself (reliable, compressed). Each client replays the
   opponent's camera and hit timeline against its score.
3. **Hub-verified.** Each client uploads the run and replay to the Hub through
   the existing authenticated account, tagged with the match id. The Hub
   applies the same checks with server-side scenario rules and publishes the
   official result. Only Hub-verified results count for any rating.

The SteamID of a Steam P2P connection is authenticated by Steam, so a peer
can't impersonate another player. It can only lie about its own run, which is
what checks 1–3 catch.

### Fallback transport: Hub WebSocket relay

- The service opens `wss` to the Hub for the room and sends the same message
  frames. The Hub fans them out and can verify results inline.
- Use it when Steam isn't initialised, when the Steam path is switched off
  (policy), or when SDR is unavailable.
- Latency is higher, but score racing and ghosts tolerate 100–200 ms easily.

## 5. The probe (`in-game/steam-probe`)

The probe is a separate UE4SS C++ mod, `AimModSteamProbe`, plus a standalone
harness. It isn't part of AimModCore.

**Stage 1 (always, read-only):**

- It waits up to 3 minutes for the game to load and init `steam_api64.dll`. It
  never loads or inits Steam itself.
- It logs:
  - the DLL version and flat-API shape
  - whether SteamAPI is initialised
  - availability of each interface version
  - logged-on state and the redacted SteamID (last 4 digits)
  - the AppID
  - relay and P2P-certificate status
- Optional `relay_warmup=1` calls `InitRelayNetworkAccess`, which contacts
  Valve relays only.
- Passive callback listeners, registered from the engine tick (game thread),
  show which thread dispatches Steam callbacks.
- It creates no lobbies and sends nothing.

**Stage 2 (off twice over):**

- It's compiled only with `-DAIMMOD_PROBE_STAGE2=ON`, and even then runs only
  with `stage2_loopback=1` (`mod/config.stage2.txt`).
- It first waits up to 30 s for the relay network and the P2P certificate. It
  logs each one's state and how many ms it took.
- It creates one Private, non-joinable, single-slot lobby. Private lobbies are
  invite-only and never listed. "Invisible" would be listed in searches.
- It sets only `aimmod.proto` and reads it back, loops a lobby chat message
  back to itself, and leaves. Each step is logged with its timing.
- It then times five reliable round trips through an in-memory
  `CreateSocketPair` (no network).
- It never sends an invite, never sets rich presence, never writes the game's
  UE session keys and never touches Duels.
- Scope guards always leave the lobby and close the socket pair, including on
  failure. If `CreateLobby` hasn't completed when the probe gives up, the guard
  keeps polling for 30 s and leaves a lobby that appears late.
- Nothing reaches another player.

Expected log lines, in order: `readiness: relay=…`, `readiness: p2p cert=…`,
`stage 2 lobby: private lobby … created in N ms`, `SetLobbyJoinable(false)=1`,
`SetLobbyData(aimmod.proto)=1, read back ok`,
`chat loopback send=1 received after N ms`, `LeaveLobby issued`,
`stage 2 sockets: in-memory pair round trips 5/5 …`, and
`stage 2: done (relay+cert=ready lobby=ok sockets=ok)`.

**In-game result (KovaaK's 3.9.11): PASS.**

| Check | Result |
| --- | --- |
| Relay after warm-up | `Current`: 24 valid relays (10 great, 11 good+), 36 points of presence |
| P2P certificate | ready |
| Private lobby create | 287 ms |
| `SetLobbyJoinable(false)` | returned 1 |
| `aimmod.proto` | set and read back |
| Lobby chat loopback to self | 242 ms, then the lobby was left |
| In-memory socket pair | 5/5 round trips, about 1 µs each |
| Callback dispatch | game thread |

The default read-only config was restored in the game afterwards.

**Stage 3 (prepared, not run):**

- This is the two-account relay-only P2P test. It's compiled only with
  `-DAIMMOD_PROBE_STAGE3=ON`, and runs only with `stage3_role` set (see
  "Stage 3 test" below).

### Build

Requirements: Visual Studio 2022 (MSVC x64) and CMake 3.22 or later. The mod
also needs the same local RE-UE4SS checkout as AimModCore, at
`external/RE-UE4SS` (git-ignored, pinned to `e3ba1016`; see
`in-game/native-mod/DESIGN.md`).

```
# harness only (no UE4SS needed)
cmake -S in-game/steam-probe -B in-game/steam-probe/build-harness -G "Visual Studio 17 2022" -A x64
cmake --build in-game/steam-probe/build-harness --config Release

# UE4SS mod
cmake -S in-game/steam-probe -B in-game/steam-probe/build-mod -G "Visual Studio 17 2022" -A x64 -DAIMMOD_PROBE_BUILD_MOD=ON
cmake --build in-game/steam-probe/build-mod --config Game__Shipping__Win64 --target AimModSteamProbe
```

Output: `build-mod/Game__Shipping__Win64/main.dll`. Add
`-DAIMMOD_PROBE_STAGE2=ON` only for a build where stage 2 has been approved,
and `-DAIMMOD_PROBE_STAGE3=ON` only for the two-account test.

### Deploy (manual; the game must be closed)

1. Create `<game>\FPSAimTrainer\Binaries\Win64\ue4ss\Mods\AimModSteamProbe\dlls\`.
2. Copy `main.dll` into `dlls\`, and `in-game/steam-probe/mod/config.txt` to
   `Mods\AimModSteamProbe\config.txt`.
3. Append the line `AimModSteamProbe : 1` to `ue4ss\Mods\mods.txt`. If
   `ue4ss\Mods\mods.json` exists (AimMod installs have both), also add
   `{"mod_name": "AimModSteamProbe", "mod_enabled": true}` to its `mods`
   array.
4. Start the game from Steam and wait about 40 s on the main menu.
5. Read the `[AimModSteamProbe]` lines in `ue4ss\UE4SS.log`. Check the SteamID
   is redacted before sharing them.
6. Remove it: delete the folder and the `mods.txt` and `mods.json` entries.

To upgrade an existing install to the stage 2 build, close the game and
replace just two files in `Mods\AimModSteamProbe`:

- `dlls\main.dll`: the build configured with `-DAIMMOD_PROBE_STAGE2=ON`
- `config.txt`: a copy of `mod/config.stage2.txt`

The build stages both under `build-mod/deploy/AimModSteamProbe/`.

### Harness

The harness inits Steam itself, so the account shows KovaaK's as running
while it runs:

```
aimmod_steam_probe_harness --steam-api <copy of the game's steam_api64.dll> [--relay-warmup] [--observe-seconds 10]
```

### Stage 3 test: relay-only P2P between two accounts

This needs two Steam accounts that both own KovaaK's, on two machines. Two
machines on the same network are fine, because ICE is off and traffic goes
through Valve's relays either way.

**What it checks:**

- **Transport:** `ISteamNetworkingSockets` P2P on AimMod's virtual port
  `0x414D` (16717). `P2P_Transport_ICE_Enable` is set to `Disable` on both the
  listen socket and the outgoing connection, so the connection is relay-only.
  The remote address is never logged.
- **Accepting:** the listener sees incoming connections through a passive
  `SteamNetConnectionStatusChangedCallback_t` (1221) listener. It registers
  that listener on the game thread and only enqueues events. It accepts only
  the configured peer arriving on its own listen socket, and closes anything
  else with reason 5002. No lobby, invite or rich presence is involved.
- **After connecting:** it checks that Steam authenticated and encrypted the
  connection, that it is relayed, and that the remote SteamID is the
  configured peer. It logs the relay's data-centre code.
- **Handshake:** each frame is 28 bytes:
  - the magic `AMP1`
  - protocol version 1
  - the message type (Hello, HelloAck, Ping, Pong, Bye)
  - the 64-bit FNV-1a hash of `stage3_match`
  - a sequence number and a timestamp

  A mismatch in the magic, version or match hash closes the connection. Hello
  and HelloAck are reliable.
- **Measurement:** the connector sends 20 unreliable pings 100 ms apart, then
  logs RTT min/median/max and a reliable Bye.
- **Cleanup:** a session object always closes the connection and the listen
  socket, and unregisters the listener.

**Setup, on each machine:**

1. Build with `-DAIMMOD_PROBE_STAGE3=ON`, as the mod or with the harness only.
2. In the local `config.txt`, set:
   - `stage3_role`: `listen` on machine A, `connect` on machine B
   - `stage3_peer`: the **other** account's SteamID64
   - `stage3_match`: the same token on both

   These values stay in the local config and never go in the repository.
3. Start A first, then B. Each side runs stage 3 after stage 1, or after
   stage 2 if that's enabled. `stage3_seconds` (default 120) bounds the
   listener's wait.
4. Pass criteria:
   - Both sides log `connected … relayed=1 authenticated+encrypted=1 peer-match=1`
     and `handshake ok`.
   - B logs at least 15/20 pongs with its RTTs.
   - A logs `bye=1`.
   - Neither UE4SS.log nor the game log shows any `FOnlineAsyncEvent*` line or
     Duels activity at the same time.

### Test kit (harness, no UE4SS needed)

The easiest way to run stage 3 with a friend is the standalone harness. Build
it as a static-runtime, harness-only build with stage 3:

```
cmake -S in-game/steam-probe -B in-game/steam-probe/build-kit -G "Visual Studio 17 2022" -A x64 -DAIMMOD_PROBE_STAGE3=ON -DAIMMOD_PROBE_STATIC_RUNTIME=ON
cmake --build in-game/steam-probe/build-kit --config Release
```

The kit contains:

- the exe, renamed `aimmod-steam-test.exe`
- `steam_appid.txt` (`824270`)
- `.cmd` scripts that prompt for the peer ID and the code
- a README

It doesn't bundle `steam_api64.dll`. The harness loads the copy inside the
local KovaaK's install, which it finds through Steam's `libraryfolders.vdf`,
and only loads it, never copies or modifies it.

**Requirements and behaviour:**

- **Steam init:** `SteamAPI_Init` works with Steam running. The AppID comes
  from `SteamAppId` (set by the harness) or `steam_appid.txt` in the working
  directory. The init only succeeds for accounts that own KovaaK's. The P2P
  cert and relay access are per app as well. So the friend must own KovaaK's
  and have it installed, but doesn't need UE4SS or AimMod.
- **The game must be closed** while the harness runs. The harness registers
  as the same app, so Steam could route a P2P connection or invite to either
  process. Running both at once worked for stage 1, but isn't reliable for
  these tests.
- **Extra flags:**
  - `--print-steamid` prints the account's SteamID64. This is local console
    only; logs stay redacted.
  - The connector retries every 3 s until the listener is up.
  - The listener waits up to 300 s.
- **Result:** a short PASS/FAIL block covering RTT min/median/max, relayed,
  encrypted/authenticated, the peer check, the handshake and the relay POP,
  with no SteamIDs or addresses. It's also saved as `aimmod-p2p-result.txt`.
  The exit code is 0 on pass.
- **Invite test:** `--invite-test send|receive`, with
  `--invite-variant rp|lobby` on the sender. It covers the two invite
  mechanisms in section 4 and saves `aimmod-invite-result.txt`. The receiver
  also reads the sender's `connect` rich presence to check it's visible to
  friends.

```
aimmod-steam-test.exe --stage3-role listen  --stage3-peer <friend's SteamID64> --stage3-match <code>
aimmod-steam-test.exe --stage3-role connect --stage3-peer <user's SteamID64>   --stage3-match <code>
aimmod-steam-test.exe --invite-test receive --stage3-peer <sender's SteamID64> --stage3-match <code>
aimmod-steam-test.exe --invite-test send --invite-variant rp|lobby --stage3-peer <receiver's SteamID64> --stage3-match <code>
```

## 6. AimModSteam: the production Steam bridge (`in-game/steam-bridge`)

`AimModSteam` grew out of the probe. It's a separate UE4SS C++ mod that gives
the native service Steam lobbies, invites, friends and relay-only P2P, so
players never exchange SteamIDs. It lives outside AimModCore for now and will
be merged into it later. The stage-3 kit remains the fallback test tool.

### What it does

- **Lobbies.**
  - Privacy is `friends` (Steam friends-only) or `invite` (Steam private).
    There are no Public lobbies: public matchmaking goes through the Hub.
  - Lobby data uses only `aimmod.*` keys. The bridge owns `aimmod.v` (`1`),
    `aimmod.bridge`, `aimmod.token` and `aimmod.privacy`. The service may set
    up to 24 other `aimmod.*` keys, such as mode, room code or scenario hash,
    with values up to 256 bytes.
  - The bridge never sets UE session keys (`OWNINGID`, `SESSIONFLAGS`,
    `P2PADDR`, `BUILDID`).
  - It joins only lobbies with `aimmod.v=1`, and leaves any other lobby
    straight away.
- **Members.** Lobby members, owner and data are polled every 250 ms, with no
  lobby callbacks. Each member carries:
  - a persona name and initials;
  - avatars on request (32×32 RGBA from `GetSmallFriendAvatar` and
    `GetImageRGBA`);
  - P2P connected state and RTT.
- **Host actions.**
  - **Kick:** an AMP1 `Kick` frame to that member. Their bridge leaves the
    lobby, and the host refuses their reconnects for the life of the lobby.
  - **Host transfer:** `SetLobbyOwner`. If the host leaves, Steam promotes
    another member. Every bridge follows the new owner: the host listens and
    clients reconnect.
- **Invites without IDs.**
  - "Invite friends" opens `ActivateGameOverlayInviteDialog(lobby)`.
  - A per-friend Invite uses `InviteUserToLobby`. The friends list includes
    persona state, "playing KovaaK's" (`GetFriendGamePlayed`, AppID 824270),
    "has AimMod" (rich presence `aimmod=1`, which every AimModSteam sets), and
    the friend's joinable AimMod lobby when there is one.
- **Join requests.** These four sources each become a `join.requested`
  event. The UI confirms, then the service sends `lobby.join`:
  - `GameLobbyJoinRequested_t` (333), when a Steam invite is accepted while
    the game runs;
  - `GameRichPresenceJoinRequested_t` (337), from "Join Game" or an
    `aimmod:` connect string;
  - `+connect_lobby <id>` on the command line;
  - `-aimmodjoin=aimmod:1:<id>` on the command line.

  The command line is read once at start-up. A pending launch join is
  re-sent with every `ready` until it's joined or dismissed.
- **Rich presence.**
  - `aimmod=1` is set always.
  - `status` is set by the service.
  - In a joinable friends lobby, the bridge sets
    `connect=-aimmodjoin=aimmod:1:<lobby>` and
    `steam_player_group`/`_size`, so friends get "Join Game" in the Steam
    friends list.
  - The keys are cleared on leave, and everything on shutdown.
- **Transport.**
  - `ISteamNetworkingSockets` P2P on virtual port `0x414D`, with ICE disabled,
    so traffic is relay-only (SDR) and no IPs are shared.
  - Star topology: the lobby owner listens, and every client connects to the
    owner. Only Steam-authenticated, encrypted connections whose identity
    matches are used.
  - **Handshake:** the client sends `Hello(lobby, token)`. The host checks
    the lobby, the `aimmod.token`, that the sender is a lobby member, and that
    it isn't banned. It then sends `Welcome` or `Reject(code)`.
  - **After the handshake:** `Data` frames carry the service's opaque frames
    (1–16 KiB) as reliable or unreliable no-delay messages. `Ping`/`Pong`
    every 2 s produce `p2p.ping` RTT events. `Bye` and `Kick` close the
    connection.
- **Safety.**
  - Every pipe command is size-limited (64 KiB) and strictly parsed: depth 6,
    no duplicate keys, unknown fields rejected. Every P2P frame is strictly
    decoded (exact sizes, version byte, payload ≤ 16 KiB).
  - Logs redact SteamIDs and lobby IDs to their last 4 digits, and contain no
    names or IPs.
  - The bridge leaves the lobby, closes sockets and clears rich presence on
    shutdown. If the game already shut Steam down, it skips the cleanup.
  - It never touches gameplay, ranked or leaderboard code.

### Threads

- One bridge worker thread makes every Steam call and owns all bridge state.
  The Steam calls the bridge uses are thread-safe, so it doesn't need the game
  thread.
- The game thread is used only to register and unregister the three passive
  callbacks: 333, 337 and 1221 (`SteamNetConnectionStatusChangedCallback_t`).
  The engine-tick job queue does this, as in the probe.
- Callback handlers and the pipe thread only enqueue work and wake the worker.

### The pipe contract (version 1)

**Transport.**

- Named pipe `\\.\pipe\aimmod-steam-v1`. AimModSteam is the server; the
  native service is the only client.
- Access is the current user (and SYSTEM) only, and remote clients are
  rejected.
- Each frame is a `uint32` little-endian length, then that many bytes of UTF-8
  JSON (1–65536).
- The service reconnects whenever the pipe drops. After connecting, it sends
  `hello` and rebuilds its state from `ready` and `lobby.updated`.

**Common fields.**

- Every command is `{"v":1,"cmd":"<name>","id":<int, optional>, ...}`.
- Every event is `{"v":1,"ev":"<name>", ...}`.
- Peer ids and lobby ids are SteamID64s as decimal **strings**. The service
  uses a peer id as the member id (`IMultiplayerTransport.LocalPeer` is
  `ready.self.peer`).
- A command with an `id` always gets exactly one
  `{"ev":"result","id":n,"ok":bool,"code"?,"message"?}`. For `lobby.create`
  and `lobby.join`, that result arrives once Steam finishes.
- A command without an `id` only reports failures, with `"id":null`. Use
  this for high-rate `p2p.send`.

| Command | Fields | Notes |
| --- | --- | --- |
| `hello` | — | Replies `ready`, plus `lobby.updated` if in a lobby, plus any pending `join.requested`. |
| `lobby.create` | `privacy` `friends`\|`invite`, `maxMembers` 2–16, `data` {aimmod.*: string} | Fails `busy` if already in a lobby. |
| `lobby.join` | `lobby` | Fails `not-aimmod` for foreign lobbies, and `steam` with the enter response code. |
| `lobby.leave` | — | Always ok. |
| `lobby.setData` | `data` {aimmod.*: string\|null} | Host only. `null` deletes the key. The bridge's own keys are read-only. |
| `lobby.setJoinable` | `joinable` bool | Host only. It also removes or restores the friends-list "Join Game". |
| `lobby.invite` | `friend`? | With `friend`, calls `InviteUserToLobby`. Without it, opens the Steam overlay invite dialog. |
| `lobby.kick` | `peer` | Host only. |
| `lobby.transfer` | `peer` | Host only; calls `SetLobbyOwner`. |
| `join.dismiss` | — | Drops the pending join request. |
| `friends.list` | — | Replies `friends`. |
| `avatar.get` | `peer` | Replies `avatar`, after up to 5 s while Steam loads it. |
| `presence.set` | `status` ≤ 64 bytes | The rich presence `status` key. |
| `p2p.send` | `peer`, `reliable` (default true), `data` base64 (1–16384 bytes) | Fails `not-connected` without a ready connection. A client can only reach the host. |
| `p2p.close` | `peer` | Graceful close (Bye). |

| Event | Fields |
| --- | --- |
| `ready` | `contract` 1, `wire` 1, `bridge`, `steam` true, `appId`, `self` {peer, name, initials}, `relay` (`Current`, `Attempting`, …) |
| `lobby.updated` | `lobby`, `owner`, `isHost`, `privacy`, `joinable`, `maxMembers`, `members` [{peer, name, initials, host, self, connected, rtt?}], `data` {aimmod.* except the token} |
| `member.joined` / `member.left` | `member` {…as above} / `peer` |
| `lobby.left` | `lobby`, `reason`: `left`, `kicked`, `closed` or `shutdown` |
| `join.requested` | `source`: `steam-invite`, `rich-presence`, `launch-aimmodjoin` or `launch-connect-lobby`; plus `lobby`, `compatible`, `from`, `fromName` (`from` is null for launches) |
| `invite.received` | `from`, `fromName`, `lobby`: a Steam lobby invite for KovaaK's arrived (`LobbyInvite_t` 503) and the user has **not** accepted it in Steam. Show an in-game prompt; on yes send `lobby.join {lobby}`. |
| `p2p.connected` / `p2p.disconnected` | `peer`, `host` (true when that peer is our host) / `peer`, `reason` |
| `p2p.message` | `peer`, `reliable`, `data` base64 (one service frame) |
| `p2p.ping` | `peer`, `rtt` ms |
| `friends` | `friends` [{peer, name, initials, state, playing, aimmod, lobby?}] |
| `avatar` | `peer`, then either `w`, `h`, `rgba` (base64, w·h·4 bytes) or `missing` true |
| `error` | `code`, `message`: for example `rejected` (a host refused us) or `p2p` |

### Contract additions: Workshop downloads and bulk transfers (still version 1)

These additions are backward compatible. `ready` now also carries
`features: ["lobby","p2p","ugc","xfer"]`, `maxChunk` (32768) and `xferWindow`
(4), so the service can feature-detect.

**Workshop (ISteamUGC).**

- The bridge uses `STEAMUGC_INTERFACE_VERSION014`, the version the game's 1.47
  flat exports wrap and the one the game itself uses.
- It only reads item state, subscribes and requests downloads. It never
  publishes, edits, votes on or unsubscribes from items.
- Item ids are Workshop `PublishedFileId`s as decimal strings.

| Command | Fields | Result / events |
| --- | --- | --- |
| `ugc.state` | `item` | `ugc.state` {item, state (EItemState bits), subscribed, installed, downloading, needsUpdate, downloaded, total, folder?} |
| `ugc.subscribe` | `item` | `result` once Steam answers (`SubscribeItem` call result 1313), then `ugc.state` |
| `ugc.download` | `item`, `highPriority` (default true) | `result` (`DownloadItem` accepted). Then `ugc.progress` {item, downloaded, total}, polled every 500 ms and sent only on change. Ends with `ugc.installed` {item, folder} or `ugc.error` {item, result, message}. At most 16 downloads at once, each with a 15-minute limit. |

`ItemInstalled_t` (3405) and `DownloadItemResult_t` (3406) are passive
listeners, used only for items the bridge was asked to download. The game
receives the same broadcasts, as it does for its own Workshop use.
`ugc.installed.folder` is the local install folder from
`GetItemInstallInfo`. The service copies or links what it needs from there.

**Bulk transfers (host-to-joiner file streaming).** These are chunked JSON
messages: no second pipe frame type, so the pipe stays one simple framing.

- **Chunk size:** a chunk is up to `maxChunk` = 32 KiB of raw bytes, sent as
  base64. That's about 43.7 KB, inside one 64 KiB pipe frame and far below
  Steam's 512 KiB reliable-message limit.
- **On the wire:** P2P frames are AMP1 `Chunk` (u32 transfer, u32 index,
  bytes), `ChunkAck` (u32 transfer, u32 index) and `Cancel` (u32 transfer,
  u16 reason).
- **Priority:** after the handshake, each side configures two lanes on its
  connection with `ConfigureConnectionLanes`: lane 0 at priority 0 for
  `p2p.send` match traffic, and lane 1 at priority 1 for chunks, sent with
  `SendMessages`. So match frames always overtake queued file data. If
  lanes can't be configured, chunks fall back to lane 0, and the window
  below still bounds the queue.
- **Flow control:** credit-based. A transfer may have at most `xferWindow`
  (4) unacknowledged chunks, so 128 KiB in flight. The receiving bridge sends
  `ChunkAck` only once the chunk has been handed to its service over the
  pipe, so a slow receiver slows the sender.
- **Limits:** at most 4 transfers per peer in each direction. Transfer ids are
  chosen by the sender's service: 1..2^31-1 and unique per peer while open.

| Command | Fields | Notes |
| --- | --- | --- |
| `xfer.chunk` | `peer`, `transfer`, `index` (0..2^31-1), `data` base64 (1–32768 bytes) | Fails `window` when 4 chunks are unacknowledged: wait for `xfer.ack`. Fails `busy` with 4 open transfers to that peer, `not-connected` without a ready link. Resending an unacknowledged index is allowed. |
| `xfer.cancel` | `peer`, `transfer`, `reason` `cancel`\|`complete`\|`error` (default `cancel`) | Sends `Cancel` and drops the state. The sender uses `complete` after the receiver has confirmed the file, typically through a service message. |

| Event | Fields |
| --- | --- |
| `xfer.chunk` | `peer`, `transfer`, `index`, `data` (receiver side; ordered per transfer, because chunks are reliable on one lane) |
| `xfer.ack` | `peer`, `transfer`, `index`, `credit` (free window slots) |
| `xfer.end` | `peer`, `transfer`, `reason` (`complete`, `cancel`, `error` or `disconnected`), `by` (`peer`, or `local` for a disconnect) |

**What the service owns:**

- The offer and accept messages (file name, size, SHA-256, chunk count),
  carried as normal `p2p.send` service frames.
- Writing the chunks to a temporary file, checking the hash, and installing it
  into `Saved\SaveGames\Scenarios` or `Maps`.
- Retry policy: resend unacknowledged chunks after a reconnect, or restart
  the transfer.

**Testing so far:** the unit tests cover encoding of all three frames, the
size bounds, and a chunk's base64 fitting in a pipe frame. The Workshop
commands and a live transfer still need the game (for UGC) and two
accounts (for P2P).
### Invites: received vs accepted

- **`invite.received`** means "someone invited you". It comes from
  `LobbyInvite_t` (503), which fires when a friend sends a KovaaK's lobby
  invite (`InviteUserToLobby` or the overlay dialog). It's filtered to
  KovaaK's game ids and to lobbies other than the current one. The user hasn't
  accepted anything in Steam yet, so the UI asks before joining.
- There is no receive-side notification for `InviteUserToGame`
  (rich-presence game invites). Steam only reports those when the user
  accepts, as 337. AimModSteam invites with `InviteUserToLobby`, so an
  in-game prompt is always possible.
- **`join.requested`** is always user-initiated in Steam, so the UI may join
  without asking again. Its sources:
  - `steam-invite` (333): the user clicked Join or Accept on a lobby invite in
    Steam, or "Join Game" on a friend in a lobby.
  - `rich-presence` (337): the user clicked "Join Game" or accepted a game
    invite.
  - `launch-connect-lobby` / `launch-aimmodjoin`: Steam launched the game
    because the user accepted one of those while it was closed.

  The only thing to check first is `compatible: true`, which means the same
  join-string version.
### Contract additions: rejoin, friend status, spectate (still version 1)

**Rejoin after a crash or disconnect.**

- On every lobby entry and host change, AimModSteam writes
  `steam-last-lobby.json` (lobby id, host id, time) into the user's own
  `%LOCALAPPDATA%\AimMod\KovaaksNative`. It never leaves the machine and is
  never logged in full.
- Leaving on purpose or being kicked clears it. A closed lobby, a shutdown or a
  crash keeps it for 3 hours.
- `ready.lastLobby` is `{lobby, host, hostName, ageSeconds}` or `null`. It's
  only filled while not in a lobby.
- `lobby.rejoin {}` joins it, and the handshake runs again. It fails with
  `no-last-lobby`, `busy`, or the usual join errors when the lobby is gone.

**Friend status.**

- Every AimModSteam sets rich presence:
  - `aimmod_state` = `lobby`, `playing` or `idle`;
  - `aimmod_scenario` = the current scenario from AimModCore's
    `core-scene.json`, empty when hidden;
  - `aimmod_lobby` = `<members>/<max>/<j|->` while in a lobby, where `j`
    means joinable from the friends list.
- `presence.privacy {hideScenario}`, or `hide_scenario=1` in `config.txt`,
  keeps the scenario out of rich presence.
- `friends` entries for friends playing KovaaK's now also carry `aimmodState`,
  `scenario`, `lobbySize`, `lobbyMax` and `lobbyJoinable` when present.

**Spectate feed.**

- `spectate.start {peer, rate 1..60 (default 60)}` and `spectate.stop {}`.
  Switching targets stops the old stream.
- Events: `spectate.frame {peer, seq, t, x, y, z, pitch, yaw, roll, fov,
  fired}` at up to the requested rate, and `spectate.ended {peer, reason}`
  when the target leaves.
- **Routing (star topology).** A client spectator sends `SpectateSub(target,
  rate)` to the host. The host keeps per-target subscribers and asks the
  target for the highest requested rate. The target sends 49-byte `Camera`
  frames (57 bytes: origin, seq, sender unix ms, camera, flags), unreliable, only while subscribed. The host relays them only to
  that target's subscribers, or emits them itself when the host is the
  spectator.
- **Bandwidth:** at most 60 × 49 B ≈ 3 KB/s per watched player, one stream
  per target whatever the number of spectators. The target sends no camera
  frames at all when nobody watches.
- **AimModCore pose files (pose format 1, `native-mod/DESIGN.md` "Spectating").**
  - **Watched player:** while a spectator subscribes, the bridge rewrites
    `%LOCALAPPDATA%\AimMod\KovaaksNative\self-pose.request` every 2 s, so
    AimModCore keeps publishing `self-pose.tsv`. It reads that file (only
    when it's less than 1.5 s old) and sends each new `pose` row as a
    `Camera` frame with the row's unix ms. It also sends a reliable
    `CameraMeta` (scenario, map, scale) every second. Without an AimModCore
    feed, it falls back to its own camera sample.
  - **Spectator:** the bridge maps the sender's times onto local time, using
    the lowest observed latency, and keeps the newest 64 rows. It writes
    `spectate-pose.tsv` in pose format 1 at up to 60 Hz, as a temp file plus
    an atomic rename. The `meta` line comes from `CameraMeta`. AimModCore's
    presenter shows it 120 ms behind the newest row and stops after 2 s
    without one.
  - `src/PoseFile.{hpp,cpp}` parses and writes the format strictly: the
    escaping matches AimModCore's, times must increase, fov must be valid,
    and at most 64 rows. It's unit tested against an AimModCore-style sample.
  - `target` rows aren't forwarded. `fired` stays false until AimModCore adds
    it to the format.
**Pose frame: remote crouch and height.**

- `Pose` now carries the sender's crouch flag (bit 0) and capsule half-height
  (flag bit 1 plus a f32), and the receiver uses only those.
- Frames without the half-height still decode, and use the default 88.
- Remote transforms are computed in `src/GhostMath.hpp` from the received
  samples alone. Local camera, capsule or crouch can't reach them, and unit
  tests check that.
- The sender reads the possessed pawn (`Controller.K2_GetPawn`, falling back
  to `MyCharacter`). It sends the actor location with Z, plus velocity.
### Mapping to the service's `IMultiplayerTransport`

This is the lobby UI agent's model on `feat/kovaaks-multiplayer-ui`
(`native-service/Multiplayer/Protocol.cs`). A `SteamTransport` implements it
over the pipe:

| `IMultiplayerTransport` | Bridge |
| --- | --- |
| `Kind` / `Available` | `"steam"` / `ready.steam` and a connected pipe |
| `LocalPeer` | `ready.self.peer` |
| `Advertise(lobby)` | `lobby.create` the first time (privacy `friends` or `invite`; `public` stays on the Hub, which is also the only place codes resolve). After that, `lobby.setData` with `aimmod.code`, `aimmod.mode`, `aimmod.scenario`, `aimmod.scenario_hash`, `aimmod.players`, … and `lobby.setJoinable(false)` while a match runs without late join. |
| `Withdraw()` | `lobby.leave` |
| `Resolve(code)` | Not available over Steam: codes resolve through the Hub. Steam joins come from `join.requested` or a friend's `lobby`. |
| `Send(peer, frame, reliable)` | `p2p.send` with `data` = base64(frame). One service envelope (≤ 16 KiB) per message. |
| `Close(peer)` | `p2p.close` |
| `Drain()` | `p2p.connected` → `connected`, `p2p.disconnected` → `disconnected`, `p2p.message` → `message`. Also map `member.left` and `lobby.left` to disconnects. |
| `Invite(lobby)` | `lobby.invite` (overlay). The friends list uses `lobby.invite {friend}`. |

The host's LobbyCore stays the authority. Steam lobby membership only gates who
may connect. The service's `hello`/`welcome` protocol runs inside `Data`
frames on top of the AMP1 handshake. `lobby.kick` and `lobby.transfer` should
follow LobbyCore's `kick` and `transfer` so that Steam and the service agree.

### Build

```
cmake -S in-game/steam-bridge -B in-game/steam-bridge/build-core -G "Visual Studio 17 2022" -A x64
cmake --build in-game/steam-bridge/build-core --config Release && in-game/steam-bridge/build-core/Release/aimmod_steam_tests.exe
cmake -S in-game/steam-bridge -B in-game/steam-bridge/build-mod -G "Visual Studio 17 2022" -A x64 -DAIMMOD_STEAM_BUILD_MOD=ON
cmake --build in-game/steam-bridge/build-mod --config Game__Shipping__Win64 --target AimModSteam
```

Output: `build-mod/Game__Shipping__Win64/main.dll`. Like AimModCore, it links
`UE4SS.dll` and needs the RE-UE4SS checkout at `external/RE-UE4SS`, pinned to
`e3ba1016`.

### Install

**Now:** `in-game/steam-bridge/install/Install-AimModSteam.ps1 -Dll <main.dll>`
adds `ue4ss\Mods\AimModSteam\dlls\main.dll` to an existing AimMod install and
enables it in `mods.txt` and `mods.json`. `-Remove` reverses that. The game
must be closed.

**For a friend:** `in-game/steam-bridge/install/New-AimModFriendBundle.ps1`
builds one zip from four inputs:

- the AimModCore package (`Build-AimModPackage.ps1` output)
- `native-mod/install`
- the verified UE4SS zip
- AimModSteam's `main.dll`

The zip contains `Install-AimMod.cmd`. That runs `Install-AimModCore.ps1` and
then `Install-AimModSteam.ps1`, so the friend gets UE4SS, AimModCore with its
service, AimModNativeUI and AimModSteam with one double-click, with KovaaK's
closed.

**To merge into AimModCore's installer** (`in-game/native-mod/install/`):

- `Build-AimModPackage.ps1`: after the AimModCore build, build
  `in-game/steam-bridge` (`-DAIMMOD_STEAM_BUILD_MOD=ON`, target `AimModSteam`
  plus `aimmod_steam_tests`). Run the tests, then copy its `main.dll` to
  `$Output\AimModSteam\dlls\main.dll`.
- `Install-AimModCore.ps1`:
  - After the AimModCore `Add-Tree`, add:

    ```
    $steam = Test-Path -LiteralPath (Join-Path $Package 'AimModSteam\dlls\main.dll')
    if ($steam) { Add-Tree (Join-Path $Package 'AimModSteam') 'ue4ss\Mods\AimModSteam' }
    ```
  - Add `if ($steam) { $ours += 'AimModSteam' }` next to the `$ours` list.
  - Record `steam = $steam` in the manifest.
  - The uninstaller already removes every file the manifest lists.

### Manual check

`in-game/steam-bridge/tools/Test-AimModSteamPipe.ps1` connects to the pipe
while the service isn't using it. It sends `hello` and `friends.list`, and
prints every event with ids redacted.

- `-Create` makes a friends-only lobby and opens the invite overlay.
- `-Join <lobby>` joins a lobby.

### Ghost demo (`ghost_demo=1`)

The smallest two-player test of the transport and the in-world experience
lives inside AimModSteam, so it doesn't depend on AimModCore or the service
UI. Turn it on with `ghost_demo=1` in `Mods\AimModSteam\config.txt`
(`mod/config.ghost-demo.txt`).

**Joining.**

- With no service connected to the pipe, AimModSteam auto-joins on any Steam
  invite, "Join Game" or invite launch.
- The host creates the lobby with `tools/Test-AimModSteamPipe.ps1 -Create`
  (optionally `-InviteName <part of a friend's name>`).

**Poses.**

- On each engine tick, the mod reads the local player's position,
  `MetaPlayerController.MyCharacter` → `K2_GetActorLocation`. It reads the view
  rotation from `PlayerCameraManager.GetCameraRotation` and the velocity from
  `GetVelocity`.
- The bridge sends a `Pose` AMP1 frame at 30 Hz, unreliable. It carries origin,
  seq, x/y/z, yaw/pitch, velocity, flags, and the scenario name from
  AimModCore's `core-scene.json`.
- The host relays client poses to the other clients (star topology). A client
  may only send its own pose, and clients accept relays only from the host.
- Frames are strictly sized, and the values must be finite.

**Ghosts.**

- For each remote member on the **same scenario**, the mod spawns three
  `StaticMeshActor`s with engine basic shapes, sized from the local capsule:
  a cylinder body, a sphere head and a cube visor that shows yaw.
- Each actor is Movable, with no collision, no shadow and no gameplay
  meaning.
- They're moved 100 ms behind the newest sample. Positions are interpolated,
  yaw follows the shortest path, and there's up to 100 ms of extrapolation
  from velocity.
- A peer on another scenario isn't shown, and the log says
  `peer ...1234 on "<scenario>"`.
- Ghosts are removed when a peer leaves or disconnects, after 3 s without
  poses, on lobby leave, and when the world changes (they're respawned in
  the new world).
- Nothing reads or writes scoring or ranked state.

**Log lines** (in `UE4SS.log`):

- `created lobby` / `joined lobby`
- `ghost demo: auto-joining lobby`
- `member joined`
- `p2p connected`
- `ping ...: N ms` (every 10 s)
- `ghost demo: receiving poses from …`
- `ghost demo: local scenario is "…"`

**Local checks.**

- The unit tests cover the Pose encoding: round-trip, truncation, NaN and
  long scene names.
- `tools/BridgeHarness.cpp` runs the bridge outside the game under its own
  SteamAPI. It exercised the pipe end to end: hello, friends, an unknown
  command, a refused non-`aimmod.*` key, creating a private lobby,
  `setData` + `lobby.updated`, and leave.
- Two-instance P2P can't run on one account (same SteamID), so the friend
  test is the P2P test.
### Player avatars (real characters in place of the shapes)

With `avatars=1`, the default in `config.ghost-demo.txt`, each remote player
on the same scenario becomes a real KovaaK's character. That gives the skin,
hitboxes, team colours and health bar. This is what the game-modes research
recommends (`in-game/docs/game-modes.md` §3.1).

**Spawning.**

- `ATheMetaAIController::Spawn(WorldContext, Profile, Team, Lives=0)` is
  called on the class default object.
- The bot profile is `avatar_profile` from config if set. Otherwise it's the
  profile of a bot the scenario already has (`TheMetaAIController.MyProfileName`),
  on that bot's team.
- Our own controllers are remembered, so they're never used as a template.

**Kept inert, re-applied every second** (the game may reset bots at challenge
start):

- `SetUseWeapons(false)` and `StopAiming()`.
- The controller's actor tick is turned off, so there are no AI decisions,
  aiming or movement input.
- `OverrideInvulnerable(true)`, so it never dies and never scores kills.
- The `CharacterMovement` mode is set from `avatar_move_mode` (default
  Flying, so gravity and AI input don't fight the pose).

**Driving, every frame from the interpolated pose stream:**

- `UpdateClientLocAndRot(location, yaw-only rotation, bPlayAnim=true)`, or
  `K2_SetActorLocationAndRotation` with `avatar_drive=teleport`.
- The movement component's `Velocity` is set from the stream, so the
  animation blueprint can blend run, walk and jump.
- `StartCrouching`/`StartUncrouch` follow the crouch flag, which is pose
  flags bit 0, read from the sender's `MetaCharacter.IsCrouching`.
- Pitch isn't applied: the body only yaws. Head or aim pitch needs an
  aim-offset input that the dump doesn't expose. The pitch is still in the
  pose for later.

**Appearance.**

- The default is whatever character profile the bot profile names. In the
  map-port scenarios that's Meso/McCree.
- Per-player choices: if lobby data holds `aimmod.char.<peer SteamID>` = a
  character profile name, the avatar calls `LoadCharacterProfile(name)`.
  Generated match scenarios need to ship one character profile per offered
  model and skin, for example `AimMod Meso McCree`. The host sets these keys.

**Name tags.** Not done: there's no text-render path we can drive without a
widget. The character's `Infobar` widget shows the profile's display name.

**Lifecycle.**

- A bot is spawned when the peer is on the same scenario, and removed with
  `RemoveSelf()` on leave, scenario mismatch or timeout.
- A world change or scenario reset destroys it; it's respawned within 2 s.
- After 3 failed spawns for a peer, that peer falls back to the shapes. So
  do all peers when the spawn bindings are missing.

**Offline check.** `avatar_test=1` spawns one avatar that circles the local
player at 4 m and crouches 3 s out of every 10, with no network. Use it to
check spawning, the inert AI, movement and the animations before a session
with a friend. Watch for the `avatars:` lines in `UE4SS.log`.

**Not yet confirmed in game:**

- whether `UpdateClientLocAndRot` plus `Velocity` animates, or whether
  `avatar_drive=teleport` or another `avatar_move_mode` works better;
- whether spawned bots count toward the scenario's own bot logic.

The scenario is freeplay. The avatar is invulnerable, and scenarios with
`ScorePerHit`/`ScorePerDamage` above 0 would still score hits on it, so
generated match scenarios keep those at 0.
## 7. Next steps

1. Wire `SteamTransport` in the service to the pipe contract, in place of the
   lobby simulation (lobby UI agent).
2. Two-player test with AimModSteam on both machines:
   - an invite through the overlay;
   - "Join Game" from the friends list;
   - an invite with the game closed (the `+connect_lobby` launch);
   - kick, host transfer, and host leave and migration;
   - live score frames.
3. Run the kit's test 3 to confirm that the game's leftover OSS handlers stay
   inert for lobby invites. AimModSteam doesn't depend on them.
4. Merge AimModSteam into AimModCore and its installer.
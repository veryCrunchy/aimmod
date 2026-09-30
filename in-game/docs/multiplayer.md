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

## 6. Next steps

1. Run the kit's tests 1–3 with a friend and record the results here.
2. Pick the invite variant from the results. Implement launch-argument and
   337/333 handling in AimModCore as described in "Steam invites".
3. Build `AimModNet`, the service match engine and the Hub room and relay
   endpoints behind a feature flag. AimMod PvP results must stay separate
   from KovaaK's ranked leaderboards.

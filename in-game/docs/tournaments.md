# Tournaments for AimMod on KovaaK's

Bracket events played inside KovaaK's through AimMod's own multiplayer:
registration, check-in, seeding, brackets, best-of-N series with picks and
bans, a shared seed per game, results verified with replays, disputes and
organiser overrides, spectating and casting.

Everything that needs a server lives on **AimMod Hub**
(`verycrunchy/aimmod-hub`, branch `feat/tournaments`): the data, the bracket
engine, seeds, seeding stats, result verification, disputes and
permissions. Its `docs/tournaments.md` documents the API in detail. The
in-game service only talks to the Hub API (as the existing `Hub*.cs`
clients do) and runs the lobby. In developer mode a simulated Hub stands in,
so the whole flow can be tried alone.

## 1. Policy

- **AimMod PvP and tournaments never touch KovaaK's ranked leaderboards.**
  Every tournament game runs in freeplay (the lobby forces it; a seeded
  `start-scenario` is refused by AimModCore outside freeplay or generated
  `AimMod Match - ` scenarios). Anybrain never starts for these runs.
- **Results are verified by AimMod**: the lobby host's client validates each
  game, both players upload their replays to the Hub, the Hub checks them,
  and the other player confirms. Disputes go to the organisers with the
  replays attached.
- No separate server: the Hub and Steam (lobbies, relay, invites) are all it
  needs.

## 2. Formats

Implemented on the Hub (`api/internal/tournament/bracket`), with exhaustive
tests:

| Format | Notes |
|---|---|
| Single elimination | Standard seeding (1 v 16, 8 v 9, ...). Byes go to the top seeds. Optional bronze match. |
| Double elimination | Winners bracket of k rounds, losers bracket of 2(k-1) rounds with reversed drop-ins, grand final with optional reset. |
| Round robin | Circle method, once or twice per pair; wins, head-to-head (two-way ties only), game difference, games won. |
| Swiss | Round 1 top half vs bottom half; later rounds by record without rematches; one bye per player at most; Buchholz. |

Every match is a **best-of-N series** (1 to 9). Rounds can be longer (a
best-of-5 final). The ruleset has a **scenario pool** and an optional
**veto**: bans and picks by the higher or lower seed; the scenario left over
is the decider. Games are played in pick order, then the decider. Without a
veto the games follow the pool in order. A tied game is replayed on the same
scenario with a new seed.

Game modes: tournaments start with **score races** (same scenario, same
seed, higher score wins the game). Head-to-head modes (tracking duel,
deathmatch; `docs/game-modes.md`) slot in through the same result record
once the lobby can play them as a series.

## 3. Seeding

- **Manual**: the organiser orders the field (until the first match starts).
- **Random**: drawn from a recorded seed, so the draw can be audited.
- **Benchmark rank**: overall rank on a KovaaK's benchmark, through the Hub's
  benchmark client and the player's linked Steam account.
- **Scenario PB**: best score on one scenario from the player's Hub runs.

Unrated players are seeded last, by registration time.

## 4. Lifecycle

1. **Draft** (organiser only).
2. **Registration**: open or invite-only (by Hub handle), with a cap. Solo
   entrants now; teams later (the entrant model already separates the
   entrant from the user).
3. **Check-in**: an optional window. AimMod shows a check-in notice in game.
   Players who don't check in are left out when the organiser starts.
4. **Bracket generation** at the start; seeds can still change until a match
   starts.
5. **Scheduling**: *ready when online* (default; the match opens as soon as
   both players are known) or *scheduled* (first-round start time). Each
   match has a deadline: a player who is ready wins by **no-show** when the
   other isn't; nobody ready flags the match for the organisers.
6. **Results**: reported game by game, confirmed by the opponent, accepted
   after the confirmation window (15 minutes) unless something needs review.
7. **Disputes and overrides**: uphold, overturn or replay a match; set any
   result (later matches that depended on it are reset); disqualify.
8. **Completion and standings**: places by elimination stage, or table
   tie-breaks for round robin and Swiss.

There's no background job on the Hub: every read applies what time has
decided (check-in opening, no-shows, accepted results).

## 5. Match flow in game

```
Hub: match opens -> both "ready" -> host chosen -> veto -> game 1 (seed s1) ...
         ^                                            |
   in-game notice                         host client: locked lobby (scenario, length, s1)
   "Your tournament match is ready"       opponent client: joins by lobby id + match token
                                          both: freeplay start with seed s1, scores live
                                          host: validates, reports game 1 (result record)
                                          both: upload own replay
                                                ... next game, next seed ...
                                          opponent: confirm or dispute
```

1. **The match appears in AimMod.** The service polls `ListMyMatches` (every
   10 s when a match or check-in is open, 3 s during a match, 30 s
   otherwise).
2. **Both players get a notice** through the out-of-panel notice layer
   (`notify.html`): "Your tournament match is ready" with Ready and Later. The
   notice waits while a lobby game is running.
3. **Host.** When both are ready, the Hub picks the host: the higher seed,
   unless only the other client can host (the Steam bridge is connected).
4. **The lobby is created by the host's client** (`HostTournamentGame`):
   score race, two players, invite-only, the game's scenario and length, the
   ruleset's countdown and spectators, auto-start, and a **tournament lock**
   (`LobbySettings.Tournament`: tournament, match, label, game, seed, the two
   players).
5. **The opponent joins automatically**, friends or not, through the
   bridge's tournament lobbies (`multiplayer.md`, "tournament lobbies"): the
   host creates the Steam lobby with `privacy:"tournament"`, the Hub's
   per-match **join token** and the opponent's SteamID64 as the only entrant.
   It's an Invisible lobby (joinable by id, not shown to friends). The host
   reports the lobby id to the Hub (`LiveMatch.lobby_token`), and the
   opponent's client joins by that id with the token
   (`lobby.join {lobby, token}`). The token never goes into lobby data; the
   bridge checks it in the P2P handshake and locks the lobby once the
   entrant is in. The Hub gives the token and the lobby id only to the
   match's two players. No Steam invite is involved.
6. **Settings are locked**: `LobbyRules.Apply` refuses every change, also from
   the host; anyone who isn't one of the two players joins as a spectator.
   Between games only the tournament moves the lobby on (`LockTournament`:
   next game, scenario, seed).
7. **The series plays.** Picks and bans happen on the Tournaments page (or on
   the Hub website); then each game: auto-ready when the content is there,
   countdown, both start in freeplay with the game's seed, live scores.
8. **Results**: when the lobby game is final, the host's client builds the
   result record, validates it (both finished, real scores, the lobby's seed
   is the Hub's) and reports it (`ReportGame`). Both clients upload their own
   replay of the game. The Hub answers with the next game and seed.
9. **Confirmation**: after the deciding game the opponent gets "Confirm the
   result: 2–1" with Confirm and Dispute.
10. **Disputes** go to the organisers with both replays.

### Same seed for everyone

- The Hub draws a fresh **32-bit seed per game** from a cryptographic source
  (AimModCore's `start-scenario` takes `seed` 0..4294967295) and stores it.
- It reveals a game's seed to the players and staff only when the game
  starts (so nobody can practise the exact spawn sequence in advance), and to
  everyone after the match.
- The lobby carries it in the tournament lock, so every player's client
  starts the run with it (`IGameControl.Start(scenario, mode, seed)`).
- Results echo the seed (`GameResult.seed`, `ReportGame.seed`); a mismatch
  flags the game and blocks automatic acceptance.
- The replay verifier compares the target spawn sequence with the seed's
  expected sequence once that's possible: AimModCore's seeding reseeds the
  game thread's RNG at the start and at each kill, so spawns match between
  players (`native-mod/DESIGN.md`, "Match seeds"). The Hub checks a `seed`
  field in the replay header today; the spawn-sequence comparison is part of
  the replay verifier worker (section 7).

### Result record

`ReportGame.result` adopts the record proposed in `docs/game-modes.md`
section 8.4, with three changes: each player also carries an `entrant_id`
(the hashed `PlayerKey` only means something to the local history), the
game's `seed` is echoed, and times are Unix milliseconds. Players, scores,
places, the host's key and the winner come from the lobby; mode-specific
fields (frags, deaths, claims, rejected, track percent) are kept for the
head-to-head modes.

## 6. Organiser and host overview

**In game (MVP, built).** The Tournaments page shows the lobby's players
together while the match runs: name, live score, accuracy, time left, ping,
connection and status, plus the game of the series and the spectator count.
The host's client pushes the same snapshot to the Hub every 3 seconds
(`ReportLiveState`).

**On the Hub (MVP, built).** `/tournaments/<event>/overview` shows every
running match of the event at once as player cards (score, accuracy, time
left, ping, connection), refreshed every 3 seconds; the match page shows the
live state of one match.

**One large live view.** Spectating already exists: watching a lobby member
replays their camera and targets in the spectator's own world
(`spectate-pose.tsv`), and follow-the-leader switches to whoever leads. A
caster or organiser joins the match lobby as a spectator (tournament lobbies
allow spectators when the ruleset does) and uses click-to-switch or
follow-the-leader. Watching from outside the lobby (an organiser overseeing
several matches) uses the lobby-less spectate request with the player's
permission.

**Small live views per player (later phase).** Researched in the 3.9.11
dumps:

- The engine pieces are all there and reachable: `SceneCapture2D`,
  `SceneCaptureComponent2D` (`CaptureScene`, `bCaptureEveryFrame`,
  `TextureTarget`, `FOVAngle`), `KismetRenderingLibrary.CreateRenderTarget2D`,
  and UMG `Image.SetBrushResourceObject` to show a render target.
- The game already renders a scene capture into a render target in
  shipping builds: the character skin preview
  (`CharacterSkinPreviewSceneCaptureComponent2D`,
  `CharacterSkinPreviewRenderTarget2D`).
- `SceneCaptureComponent.ShowOnlyActors` / `HiddenActors` /
  `PrimitiveRenderMode` exist, so each capture can draw only its player's
  puppet targets on top of the map.
- **Cost**: each capture is another render of the scene (CPU draw thread and
  GPU), roughly proportional to its pixel count and the scene's draw calls.
  KovaaK's scenes are light, but eight captures every frame would double or
  triple the frame cost on mid-range PCs. Practical budget: small targets
  (320x180), show flags trimmed (no post-processing, shadows, particles),
  `bCaptureEveryFrame` off and `CaptureScene` called round-robin, one or two
  per frame (each view at 10 to 15 frames per second).
- **The hard part is content, not rendering**: each view needs that player's
  targets. The pose stream already carries the watched player's camera and
  targets; small views need one stream per player at once and a puppet target
  set per player, hidden from every other capture. That's a bridge change
  (several spectate streams) and an AimModCore change (puppet sets per
  source), on top of the capture actors and a UMG overlay (Gameface can't
  show engine render targets directly).
- Verdict: feasible but heavy. Planned after the MVP as phase 3 below, behind
  a developer flag first, with a frame-time guard that drops the small views
  when the game falls below its target frame rate.

**OBS (built).** The OBS overlay host serves a tournament overlay at
`<obs address>/<token>/tournament` (bracket, the current match's names and
series score, live scores), read-only and without account data. Casters add
it as a browser source next to the existing HUD overlay. The Hub overview
page works as a browser source too.

## 7. Anti-cheat and fairness

- **Host validation** (built): both players finished, finite scores, the
  lobby's seed is the game's; the host's client reports `host_validated`.
  The lobby's existing checks still apply: same content hashes, same AimMod
  version, scores clamped by the host, disputed lines.
- **The host isn't trusted alone**: the opponent confirms, and both players
  upload their own replays.
- **Hub replay checks** (built): format 2 header and framing, scenario, input
  present, completed run, final score within 0.5%, length, frame rate,
  recording time, header seed. Suspicious or rejected replays block automatic
  acceptance.
- **Replay verifier worker** (later): the body is LZMS-compressed through the
  Windows Compression API, so deep checks run in a Windows worker using the
  service's own `ReplayFormat2` decoder: the target spawn sequence against the
  seed (`--compare-spawns`), view-speed and input plausibility, and hit
  validation for head-to-head modes (game-modes.md 5.4).
- **Seeds** are revealed only when the game starts.
- **Disputes** stop the match; organisers decide with both replays.

## 8. Data model and API

On the Hub (`proto/aimmod/tournament/v1/tournament.proto`), service
`TournamentService`:

- Tournaments: `ListTournaments`, `GetTournament`, `CreateTournament`,
  `UpdateTournament`, `AdvanceTournament` (open registration, open check-in,
  start, cancel).
- Entrants: `Register`, `Withdraw`, `CheckIn`, `InviteEntrants`, `SetSeeds`,
  `Reseed`, `Disqualify`.
- Matches: `ListMyMatches`, `GetMatch`, `MarkReady`, `SubmitVeto`,
  `ReportGame` (with `GameResult`), `ConfirmResult`, `OpenDispute`,
  `ResolveDispute`, `SetMatchResult`.
- Staff and casting: `SetStaff`, `ReportLiveState`, `GetOverview`.
- Replays: `POST /api/tournaments/v1/replays` and
  `GET /api/tournaments/v1/replays/<tournament>/<replay>`.

Authz: the in-game client uses its device upload token; the website its
session. Per tournament: organiser, admin and caster staff; the Hub
administrator can act on everything. Who may create tournaments is a Hub
setting (`AIMMOD_HUB_TOURNAMENT_CREATORS`: `admin` by default, `verified` or
`signed-in`).

## 9. In-game implementation

| File | What |
|---|---|
| `native-service/Tournaments/TournamentModel.cs` | What the service keeps from the Hub's JSON; the result record. |
| `native-service/HubTournaments.cs` | Hub client: authenticated tournament RPCs and replay upload; `HubTournamentSource`. |
| `native-service/Tournaments/SimulatedTournamentHub.cs` | `ITournamentHub` and the developer simulation (8-player bracket, simulated opponents). |
| `native-service/Tournaments/TournamentService.cs` | Polling, notices, the series driver (host, join, report, upload, live state), `/tournaments` and the OBS state. |
| `native-service/Multiplayer/MultiplayerService.Tournament.cs` | Locked tournament lobby: host a game, join by token, lobby state for the overview. |
| `native-service/Multiplayer/LobbyModel.cs`, `LobbyCore.cs` | `TournamentLock`, refused changes, players-only, `LockTournament`. |
| `native-service/Multiplayer/GameControl.cs` | Seeded `start-scenario`. |
| `ui/tournaments.js`, `ui/tournaments.css` | Tournaments page: matches, check-in, veto, games, confirm or dispute, host overview, canvas bracket (Roboto). |
| `ui/tournament-overlay.html`, `ui/tournament-overlay.js` | OBS tournament overlay. |
| `ui/developer.js` | "Simulated tournament" on the Developer page. |

Developer mode: turn it on under Settings, then "Simulate a tournament" on
the Developer or Tournaments page. You're the top seed of an 8-player
bracket; the opponent readies, bans and confirms by itself, and the lobby
runs with a simulated opponent. "Advance the bracket" finishes the other
matches; "Opponent reports" makes the opponent report, so you get the
confirm notice.

Tests: `AimMod.InGame --self-test-multiplayer` (tournament rules, the Hub
client against a fake transport, the seeded start command, and a whole
simulated series through the real lobby service) and
`node --test in-game/ui/tournaments.test.cjs`.

## 10. Phases

1. **MVP (this branch and the Hub's `feat/tournaments`)**: formats, seeding,
   lifecycle, series with veto and seeds, locked lobby, notices, Tournaments
   page with bracket, host overview, Hub pages, replay header checks, OBS
   overlay, developer simulation.
2. **Hardening**: the Windows replay verifier worker (spawn sequence against
   the seed, plausibility); a two-account test of joining a tournament lobby
   by id; Discord and email calls for check-in and
   matches; per-match scheduling by organisers.
3. **Casting**: small live views per player (scene captures, section 6), a
   caster's multi-match dashboard, OBS scenes per match.
4. **More**: teams, head-to-head modes as tournament games, ladders and
   seasons.

## 11. Decisions for the user

- **Formats first**: single elimination and double elimination are ready to
  run; round robin and Swiss are built and tested too. Which to promote first
  on the website?
- **Who can organise**: the Hub administrator only (default), any verified
  account, or anyone signed in.
- **Stat-based seeding source**: benchmark rank (which benchmark per
  tournament, chosen by the organiser) or scenario PB from Hub runs. Is one
  of them the default?
- **Prizes**: shown as free text; AimMod handles no money. Keep it that way,
  or no prize field at all?
- **Replays**: required by default for tournaments (blocks automatic
  acceptance without both replays), or optional?

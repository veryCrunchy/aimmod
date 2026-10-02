# Multiplayer game modes for AimMod on KovaaK's

Status: design and feasibility study, a read-only runtime probe
(`in-game/probes/modes-probe`, first live run in section 9.1), and the first
mode under construction: the tracking duel (section 6.3.1). The offline avatar
spike is built (section 9.3). Nothing here is released.

Target: KovaaK's 3.9.11 (UE 4.26, UE4SS `e3ba1016`). Evidence comes from the
reflection dumps of this exact build (UHT and C++ header dumps, the object dump
of a running sandbox scenario with bots) and from strings in the shipping
executable, read without modifying anything. Game paths are written `<game>`.

Builds on:

- `in-game/docs/multiplayer.md` (branch `feat/kovaaks-multiplayer-research`):
  Steam SDR P2P from an in-process bridge, lobbies, invites, host-authority
  star topology. Lobbies and the relay are proven live.
- AimModCore `in-game/native-mod/DESIGN.md`: lifecycle and score observation,
  replay capture, the engine-tick presenter that moves replay proxies, and the
  game-command channel (scenario load/start, freeplay overrides,
  `SetWeaponProfileByString`).
- The lobby service on `feat/kovaaks-multiplayer-ui`
  (`in-game/native-service/Multiplayer/`): lobby model, `aimmod.mp` v1
  protocol, clock sync, and the deterministic match-scenario generator.
- The map-port tool (`feat/kovaaks-map-port`, `in-game/tools/map-port/`), which
  writes complete `.sce` files with character, weapon, bot and movement
  profiles.

### Since the first draft

- **AimModSteam** (`in-game/steam-bridge`, see multiplayer.md §6) now shows
  remote players as real characters, as recommended in 3.1:
  - `ATheMetaAIController::Spawn` bots, AI off, invulnerable;
  - driven from a 30 Hz pose stream with about 100 ms of interpolation;
  - the look comes from `aimmod.char.<id>` (`LoadCharacterProfile`);
  - plus lobbies, P2P, spectating, file transfer and the Workshop.
- **The lobby service** has score race, duel, FFA rounds and practice, generated
  match scenarios, content transfer, replays, history, auto-ready, rematch and
  reconnect. The tracking duel is now a lobby mode there.
- **AimModCore** has game commands (load, start with freeplay overrides,
  refresh scenarios), the replay presenter, and the `self-pose.tsv` feed
  (pose format 1: the local camera at 60 Hz plus drawn target capsules). The
  tracking duel builds on that feed.

Evidence tags used below: **[dump]** found in the 3.9.11 reflection dumps,
**[exe]** found in executable strings, **[live]** already verified in game by
earlier AimMod work or the probe's first run (9.1), **[probe]** still to be
confirmed by the probe's full test script or a spike.

## 1. Summary

- **Remote players should be native KovaaK's bots, driven by AimMod.** Bots
  and the player are the same class: the object dump of a sandbox scenario
  shows the player and every bot as `FPSCharacter_C` (an `AMetaCharacter`),
  with bots possessed by `ATheMetaAIController` [dump]. A bot whose AI is
  switched off and whose pose AimMod sets every frame looks and reacts like a
  player. It gets the humanoid skeletal models and skins (Meso with
  Genji/McCree/Pharah/Tracer, Endo, Ecto, …), mesh hit detection, headshots,
  team colours, health bars, hit sounds, hit markers and damage numbers, all
  without AimMod rendering anything. The inert `StaticMeshActor` proxies of the
  replay viewer stay the right tool for ghosts and spectating, where nothing
  can be hit.
- **Hits: native presentation, AimMod authority.** Avatars are invincible
  locally, so the game never kills a remote player on its own. Hits are read
  from the local weapon (damage and hit counters, `CharactersHit`) and from
  AimMod's own camera ray against the avatar's hull, sent to the host as
  claims, validated there with lag compensation, and applied as damage on the
  victim's machine through the character's reflected damage functions
  (`HandleDamage`, `TakeDamageFunc`, `SetHealth`) [dump]. Local hit
  feedback (markers, sounds, damage counters) should keep working on
  invincible bots, as KovaaK's tracking scenarios score damage on bots that
  never die; the probe confirms it with `InvincibleBots` [probe].
- **Most rules are native profile fields, applied by the host.** Health,
  respawn delays, headshot-only, team damage blocking, health on kill, regen,
  lifesteal, knockback, ammo on kill, multi-slot weapons (8 slots), instagib
  weapons and movement abilities are all profile data [dump]. AimMod generates
  the scenario with them, and the host engine mirrors the rules that must be
  authoritative (lifesteal, kill rewards, armour, economy).
- **Missing natively: pickups, grapple and objectives.** The legacy Reflex
  loader knows only `PlayerSpawn`, `Target`, `JumpPad` and `Teleporter`
  entities (`EMapCreatorLegacyMapEntityType`) [exe]. Nothing named `Pickup`
  exists in any dump or in the executable, so Reflex pickups are ignored.
  There is no grapple, hook or rope ability, only an AI-side bot tether. Bomb
  sites, buy zones and flags don't exist. All of these become AimMod logic:
  host-side zone and radius checks, AimMod-spawned marker actors, and
  `LaunchCharacter` for grapple pull. Each one is feasible with reflected
  engine functions [dump].
- **Ranked stays untouched.** Every PvP mode runs in a generated AimMod match
  scenario (reserved name prefix `AimMod Match - `) in freeplay. AimModCore's
  existing rule applies: any state-changing feature is refused or reset if a
  challenge run begins. Anybrain only starts a match for ranked scenarios
  (multiplayer.md, "Anybrain"), so it never sees a PvP round.
- **Order:** score race and practice together, then tracking duel (avatars and
  local sampling, no damage injection), then deathmatch, vampiric 1v1 and
  instagib (damage, death, respawn), then CS rounds (teams, economy, freeze
  time, bomb), then CTF, pickups, weapon drops and grapple.

## 2. Policy and constraints

- The KovaaK's developer allows modding and decompiling, except unfair changes
  to ranked leaderboards. The user approved AimMod changing game state where a
  feature needs it.
- PvP never runs as a challenge. It uses generated scenarios played in
  freeplay with AimMod scoring. Results go to AimMod (and the Hub), never to
  KovaaK's leaderboards.
- AimModCore's "Play" capabilities (section 4.3) are armed only while **all**
  of these hold:
  - the service reports an active match;
  - the current scenario is the generated match scenario named by the lobby;
  - the play type is freeplay and `IsInChallenge` is false.

  Leaving the scenario, a scenario change, or a challenge start disarms them
  and restores everything they changed (team, invulnerability, input blocks,
  weapon slots). A challenge that starts while armed is cancelled, as game
  commands already do.
- No input injection. AimMod may *read* input through the game
  (`APlayerController::IsInputKeyDown`) and may *block* it (freeze time), but
  never synthesises key presses, mouse motion or shots.
- Peers are untrusted, as in multiplayer.md: the host validates every claim,
  and only Hub-verified results count for any rating.

## 3. Findings from the 3.9.11 dumps

### 3.1 Characters, bots and remote avatars

| Need | What the game has | Evidence |
| --- | --- | --- |
| Humanoid body with skins | `AMetaCharacter` (BP `FPSCharacter_C`) has `CharacterMesh0` (skeletal), per-shape bounding components (`Cyl*`, `Sphere*`, `Cube*`, projectile `P*`), `CharacterOwnedWidgetComponent` health/info bars, `ColorManagerComponent`. Profiles pick `CharacterModel` and `CharacterSkin`. Loaded models: Meso, Endo, Ecto, StylizedEcto, Diver, Medusa, Pill, Pigeon, Pumpkin, JackOLantern (+ AnimeGirl DLC); Meso skins Genji, McCree, Pharah, Tracer. | [dump] object dump subobjects of `FPSCharacter_C_*`, `MetaSkeletalCharacterModelDataAsset` and `MetaSkeletalCharacterSkinDataAsset` instances |
| Bots are the same class as the player | every bot in the sandbox world is `FPSCharacter_C` + `TheMetaAIController` | [dump] |
| Spawn a bot at runtime | `static ATheMetaAIController::Spawn(WorldContext, ProfileName, Team, Lives)` (BlueprintCallable) | [dump] UHT `TheMetaAIController.h` |
| Switch its AI off | `SetUseWeapons(bool)`, `StopAiming()`, `ClearMovementInputs()`, `ClearTarget()`, `RemoveSelf()`; bot profile `NoAiming`, `NoDodging`, `UseWeapons`, `StandStillUntilHurt` | [dump] |
| Move it | `AActor::K2_SetActorLocationAndRotation`/`K2_TeleportTo`; `UCharacterMovementComponent::SetMovementMode`/`DisableMovement`; and `AMetaCharacter::UpdateClientLocAndRot(Location, Rotation, bPlayAnim)`, which reads like a networked-client setter (Duels leftovers) | [dump]; semantics [probe] |
| Recorded path playback on a bot | `UPlayerPlaybackComponent` (`CharacterMovementPath` on every character), `StartPlayback(Profile)`, `IsWorldPositionPlayback`, `SetTime`; feature flag `IsCharacterPlaybacksEnabled` | [dump]; not needed by the design, but a fallback driver [probe] |
| Teams | `Team`, `TeamOverride`, `bOnEnemyTeam`; `SetTeam`, `OverrideTeam`, `ResetTeam`; `UKovFunctionLibrary::TeamCheck` → `SameCharacter/Teammates/Opponents/Invalid`; profile `BlockTeamDamage`, `TeamBodyColor/HeadColor` vs `EnemyBodyColor/…OnHit/OnLookAt`; scenario `PlayerTeam`, `BotTeams`; Freeplay manager `UTeamSelectorWidget` (teams 0/1/2) | [dump] |
| Spawn points | `AMapCreatorSpawnPoint` (`TeamMask`, `Weight`, `PermittedCharacterProfiles`, `Path`), `AMapCreatorSpawnVolume`; `AKovaakMapCreatorRepository::GetSpawnPointsForTeam(TeamId)` | [dump] |

**Recommendation.** Each client's generated match scenario adds one avatar bot
per remote player (`AddedBots`, an `AimMod Avatar` bot profile with
`NoAiming=true`, `NoDodging=true`, `UseWeapons=false`, and a character profile
with the chosen model and skin, `MeshHitDetection=true`). AimModCore claims
those bots by profile name, then every engine frame:

1. keeps the AI inert (`SetUseWeapons(false)`, `StopAiming`, controller tick
   off);
2. puts the movement component in `MOVE_None` (so gravity and AI input don't
   fight the pose);
3. sets location and rotation from the interpolated network pose (section
   5.2), and writes the movement component's `Velocity` so the animation
   blueprint shows running, jumping and crouching (or calls
   `UpdateClientLocAndRot(..., bPlayAnim=true)` if the probe shows it
   animates);
4. sets `Team` relative to the local player (teammates share my team, everyone
   else is the enemy team), so colours and team damage blocking are correct
   from each viewer's perspective;
5. keeps the avatar invulnerable (scenario `InvincibleBots=true` or
   `OverrideInvulnerable(true)`) and mirrors the host's health for the health
   bar (`SetHealth`).

Late joiners or reconnects that need a new avatar mid-round use
`ATheMetaAIController::Spawn` with the same profile name. If a build breaks
bot driving, the fallback is the replay viewer's inert proxy (no native hit
feedback; AimMod's ray test then decides every hit alone).

### 3.2 Hit registration

| Item | What the game has | Evidence |
| --- | --- | --- |
| Shot pipeline | `AWeaponParentActor`: `HitscanShot`, `SingleHitscanTrace(BulletNum)`, `ProjectileShot`, `DamageHitscanTargetNative(Distance, ComponentHit, CharacterTarget, Hit)`, `DamageEnemy(Target, Damage, bHeadshots)`, `ApplyEffects(Target, bWasHeadshot)`, `AddDamageSumNative(bHeadshot, Target, Distance)`, `GetDamage(Headshot, Distance, Charge)`, `GetDamageFalloff(Distance)` | [dump] |
| Hit notifications | `Send_ShotFired(Shooter, DamagePossible)`, `Send_ShotHit(Shooter, Target, DamageDone)`, `Send_ShotMissed(Shooter)`; `ScenarioManager::NotifyDamageDealt(Recipient)`, `NotifyCharacterDeath(Character)`, `NotifyPlayerKillCredit(PlayerCharacter)` | [dump]; `NotifyPlayerKillCredit` fires as a hook [live] |
| Counters (pollable) | weapon: `ShotsFiredThisSession`, `ShotsHitThisSession`, `DamageDoneThisSession`, `DamagePossibleThisSession`, `bAnyHeadshots`, `CharactersHit`, `CurrentAmmo`; character: `DamageDone`, `DamageTaken`, `KillCount`, `DeathCount`, `UniqueAttackers`, `Health` | [dump] |
| Tracking | beam weapons (`FakeShaftBeamUpdate`, `SetBeamColor`), `UTargetTrackingTelemetryAdaptor`; damage counters on bots that never die | [dump]; counters on `InvincibleBots` [probe] |

Caveat: AimModCore found that on 3.9.11 the game calls its lifecycle, shot and
score functions natively, so UFunction hooks on them see nothing [live].
`Send_ShotHit` and `DamageEnemy` are probably native too. The probe counts
which of these hooks fire. The design doesn't depend on hooks: AimModCore
polls the counters above every engine frame (they are plain reflected
properties) and attributes a change to the avatar in `CharactersHit`, with
AimMod's own ray test as the independent check:

- **Claim source 1 (native):** the local weapon's hit counter and damage
  advanced this frame and `CharactersHit` contains avatar *k*, plus
  `bAnyHeadshots` for the zone.
- **Claim source 2 (AimMod ray):** at the shot time, the camera ray (camera
  location and rotation are already sampled by the replay sampler) intersects
  avatar *k*'s hull (capsule for the body, sphere for the head, from the
  avatar's profile bounding box) at its rendered pose.

A hit is claimed when source 1 says so. Source 2 is sent along as evidence,
and a disagreement is logged. For tracking modes, source 2 alone drives the
score (section 6.3), which makes scoring frame-rate independent and
weapon-agnostic.

Lifesteal, health on kill and kill credit don't need the game's own events:
the host computes them from confirmed damage (section 4.1). The native fields
stay at 0 in PvP profiles, so nothing heals twice.

### 3.3 Damage, health, death and respawn on the local player

All on `AMetaCharacter`, all `UFUNCTION(BlueprintCallable)` [dump]:

| Function | Use |
| --- | --- |
| `HandleDamage(Amount, Attacker, KBOrigin, WeaponKB)` | preferred way to apply confirmed damage. `Attacker` is the shooter's avatar pawn, so the death toast and killer camera have a real killer, and native knockback and aim punch apply |
| `TakeDamageFunc(Value, Amount, Attacker, KBOrigin, Ground, Air, bCrit)` and `ITakeDamageInterface::MetaTakeDamage(...)` | alternative entry points, with ground and air knockback factors and the crit flag |
| `HandleHeal(Amount, KBOrigin, WeaponKB)`, `HandleLifesteal(Damage)`, `RegenHealthFromKill()` | apply the host's heals with native feedback |
| `SetHealth(NewHealth)`, `SetCharacterProfileMaxHealth(Max)`, `SetStuffToMax()` | reconciliation to the host's value; round-start refill |
| `Death(Killer)`, `OnCharacterKilled()`, `BeginRespawn()`, `Respawn(bPlayAnim)`, `FinishRespawn()`, `SetRespawnTimer(Time)`, `SetLives(N)`, `RanOutOfLives()` | death and respawn are native once health reaches 0. The host can also force them (round reset) |
| `K2_HealthChangedEvent`, `K2_RespawnEvent` delegates; `HealthSignature(Character, Previous, New)` | change notifications (BP delegates; AimModCore polls `Health` instead) |
| `OverrideInvulnerable(bool)`, `bRespawnInvulnerable`, `ClearRespawnInvulnerability()` | spawn protection; invincible avatars |
| `ShouldTakeDamageFromAttacker(Attacker)`, `ShouldBlockTeamDamage()`, `IsTeamDamageBlocked()` | native team rules; informational |

Open questions for the probe: whether `HandleDamage` on the player kills and
respawns it natively when `PlayerMaxLives=0` (infinite), and whether
`InvinciblePlayer=false` in the scenario header is enough (most stock
scenarios set it true).

### 3.4 Weapons, ammo and loadouts

- `AWeaponHandler` keeps `NativeWeapons` (array), `CurrentWeaponNum_`,
  `SelectableWeapon` (array of bool), and the character profile has 8 weapon
  slots (`WeaponProfileNames=a;b;;;;;;;`, keys `Weapon1Pressed` …
  `Weapon8Pressed` on `AMetaPlayerController`) [dump]. **A multi-slot
  inventory exists.**
- `SetWeaponProfileByString(ProfileName, Slot)`,
  `SetWeaponProfileByProfileNative(Slot, Profile)`, `ChangeWeapon(N, bForce)`,
  `RefillAllAmmo()`, `AWeaponParentActor::SetCurrentAmmo(N)`,
  `RefillAmmoFromKill`, `Reload` [dump]. AimModCore already binds
  `SetWeaponProfileByString` and calls it for slot 0 (primary). A buy sets slot *n* and switches to
  it. Ammo is per weapon (`CurrentAmmo`, profile `MagazineMax`,
  `AmmoPerShot`, `CooldownType`).
- **Instagib** is pure profile data: hitscan (`Type=Hitscan`),
  `DamagePerShot` ≥ max health, `TimeBetweenShots` about 1.0–1.5 s,
  `CooldownType=InfiniteUse`, optional flat knockback
  (`FlatKnockbackHorizontal/Vertical`) for the classic jump-boost. There's no
  stock instagib profile; we ship one in the generated scenario.
- Projectiles are `AMetaProjectile` actors (rocket, plasma, arrow, orb meshes,
  explosives via `ExplosionNative`). Projectile hits on avatars are detected
  natively on the shooter's machine. They're claimed like hitscan hits, with
  the projectile flight time folded into the lag-compensation window (cap
  below).

### 3.5 Abilities and grapple

- `AAbilityHandler` loads up to 4 ability profiles (`AbilityProfileNames=;;;`)
  of the types `EAbilityType = Movement, Weapon, Melee, Sprint, Recall`
  [dump]. The editor's presets (Blink, Dash, Launch, Gravity Boost, Jump, Boop
  Rocket, Offhand, Free Reload, Melee) are parameterisations of these:
  `FMovementAbilityNative` has `MainVelocity`, `MainVelocityCanGoVertical`,
  `MainVelocitySetToMovementKeys`, `UpVelocity`, `NegateGravityForDuration`,
  `LockDirectionForDuration`, `AbilityDuration`, `EndVelocityClampFactor`,
  `Hurtbox*`, `HealthRestore`, `AbilityReloadsCurrentWeapon`.
- **No grapple, hook or rope exists.** The only "tether" is
  `ATheMetaAIController::SetTetherChar`, which keeps a bot near a target
  [dump]. The single `Grapple` string in the object dump is a workshop
  scenario name.
- Grapple options, cheapest first:
  1. **Native "grapple dash"**: a Movement ability with
     `MainVelocityCanGoVertical=true`, high `MainVelocity`,
     `NegateGravityForDuration`, short `AbilityDuration`. It goes where you
     look. No AimMod code, but no rope and no anchor.
  2. **AimMod pull grapple (recommended for Quake modes)**: bind the grapple
     to an ability slot holding a no-op ability, so the game's own key
     binding triggers it. AimModCore sees the press (ability charges, or
     `IsInputKeyDown` on the bound key), traces from the camera
     (`KismetSystemLibrary::LineTraceSingle`) to find an anchor, then each
     frame applies `ACharacter::LaunchCharacter(PullVelocity, false, false)`
     towards the anchor (Q3 CTF style: constant pull speed, release on key up
     or on reaching the anchor). The rope is an `ACableActor` (the
     CableComponent plugin is present) or a stretched static mesh. Movement
     stays client-side, like all movement. The host only checks the
     resulting speed against `grapple.maxSpeed`.
  3. `ApplyFlatKnockback(Origin, H, V)` for one-shot launches (jump pads,
     rocket jumps). It's also reflected.

### 3.6 Pickups and map objects

- Legacy Reflex `.map` entities: the loader's enum
  `EMapCreatorLegacyMapEntityType` has only `Invalid`, `PlayerSpawn`,
  `Target`, `JumpPad` and `Teleporter` [exe]. **`Pickup` entities are not
  loaded**, and no pickup class or `pickupType` string exists in the dumps or
  the executable. The numeric `pickupType` values are Reflex Arena's own; if
  we import them, map-port has to take them from Reflex's definitions and
  sample maps, not from KovaaK's.
- Map-creator JSON objects with runtime actors: `AMapCreatorSpawnPoint`
  (`TeamMask`), `AMapCreatorSpawnVolume`, `AMapCreatorJumpPad` (target
  waypoint), `AMapCreatorTeleporter` (target, `TeleportDelay`),
  `AMapCreatorHurtKill` (`Damage`, `Cooldown`, `bKill`),
  `AMapCreatorWaypoint`, `AMapCreatorTextLabel` (text, colour, size),
  `AMapCreatorWater`, plus props from `KovaakMapCreatorPropTable` [dump].
  Jump pads and teleporters work natively, which suits arena maps.
- **Runtime spawning works**: the replay viewer already spawns
  `StaticMeshActor`s with `BeginDeferredActorSpawnFromClass` and
  `FinishSpawningActor` and sets meshes and materials [live]. Pickup, flag,
  bomb and zone markers use the same path with `/Engine/BasicShapes/*` meshes
  (or prop-table meshes), coloured by material parameters. Text uses a spawned
  `AMapCreatorTextLabel` or the Gameface HUD.
- Overlaps: AimMod doesn't rely on UE overlap events (they need components
  with collision on actors we spawn, and events we'd have to hook). The host
  checks positions instead: player feet and capsule against a sphere or an
  oriented box, every state frame. This is deterministic, cheat-resistant (the
  host decides) and needs no engine callbacks.

### 3.7 Rounds, freeze and timers

- Respawn and teleport: `Respawn(bPlayAnim)`, `AActor::K2_TeleportTo`,
  `TeleportDelayed(Location, Rotation, Delay)`,
  `AMetaGameState::RespawnPlayerAndDestroyProjectiles()` (round reset that also
  clears live projectiles) [dump].
- Freeze time: `AController::SetIgnoreMoveInput(true)` (used by the replay
  scene [live]) stops movement and leaves mouse look, which is how CS freeze
  time behaves. To block shooting during freeze, the candidates are
  `AMetaCharacter::bAbilityBlockingAttack` (read by `IsAttackInputBlocked`),
  `StunMe(Time)`, or the weapon handler's `bWantsFire`. Which one is clean is
  for the probe and a test build to decide [probe].
- The round timer is AimMod's: the generated scenario has a long `Timelimit`
  (map-port uses 600 s). Rounds are service state, shown in the Gameface HUD.
  `ScenarioManager::AddChallengeTime` exists, but we don't need it.
- Cursor for the buy menu: `AMetaPlayerController::K2_SetShowMouseCursor` plus
  Gameface input focus (UI work, section 8.1).

### 3.8 The game's own Duels code

`UDuels` (a game-instance subsystem with only a challenge time limiter),
`UDuelsChallengeTimeLimiterServer/Client`, `IsDuelsEnabled`,
`GetDuelsLobbyUEMapName`, `UpdateClientLocAndRot` [dump]. The developer says
Steamworks isn't used for multiplayer, and nothing in Duels covers rounds,
teams or objectives. We don't use it. We don't flip its feature flag either,
because it would bring in UE replication and the OSS lobby path that
multiplayer.md keeps us away from.

## 4. Mode framework

### 4.1 Architecture

```
 KovaaK's process                                            AimMod native service (.NET 8)
 ┌───────────────────────────────────────────────┐  shm/pipe ┌───────────────────────────────────┐
 │ AimModCore                                    │◄─────────►│ Match engine (existing lobby)     │
 │  Observer, ReplaySampler (existing)           │  commands │  ModeHost: rules engine (authority│
 │  Play (new): avatars, local probes,           │  events   │   on the host, mirror on clients) │
 │   damage/heal/teleport/weapon/freeze/launch,  │  poses    │  RoundController, Economy,        │
 │   markers, input query, grapple               │           │  Objectives, Pickups, Scoring     │
 │                                               │           │  LagComp history, Validation      │
 │ AimModNet (Steam bridge, existing research)   │◄─────────►│ Transport (Steam | Hub WS)        │
 │  fast pose channel in-process ─── peers       │  frames   │                                   │
 └───────────────────────────────────────────────┘           └───────────────┬───────────────────┘
                                                                             │ loopback HTTP
                                                                             ▼
                                                              Gameface UI: lobby, HUD, buy menu
```

- **Host authority, star topology** (multiplayer.md). The host's service runs
  the rules engine. Clients send inputs and claims, never results. Each
  client's service mirrors the host's state for its HUD and drives its own
  game through AimModCore.
- **Fast path stays in the game process.** Pose frames (30–60 Hz, unreliable)
  go AimModCore → AimModNet → peers → AimModNet → AimModCore without visiting
  the service, so they don't pay an IPC hop. AimModNet also hands a copy to
  the service: the host needs the pose history for lag compensation, and
  every client needs it for zone checks shown in the HUD.
- **Rules path goes through the service.** Claims, damage, deaths, round
  state, economy and pickups are reliable `aimmod.mp` envelopes, processed by
  the host's `ModeHost`.
- **Local IPC.** The existing file transport (`core-command.tsv`) is too slow
  for per-frame data. Add a shared-memory channel
  `Local\AimMod.KovaaksNative.Play`, built like the existing live channel
  (seqlock, single writer per direction):
  - mod → service: local pose and state at 60 Hz, hit claims, local events
    (died, respawned, weapon changed, ability used, key pressed);
  - service → mod: a command ring (sequence numbered, acknowledged in the
    other direction) and the avatar snapshot buffer when the service is the
    pose source (Hub relay fallback).

### 4.2 Rules engine in the service

```csharp
interface IGameMode
{
    string Id { get; }                         // "tracking-duel", "dm", "vampiric", "instagib", "cs", "ctf", ...
    ModeSettingsSchema Settings { get; }       // lobby UI + validation + PlayKey
    ScenarioPlan Plan(LobbySettings s, MapMeta map);   // profiles, avatars, weapons for the generator
    void Start(MatchContext m);                // teams, spawns, economy init
    void Tick(MatchContext m, double now);     // host only: timers, zones, pickups, regen
    void OnClaim(MatchContext m, HitClaim c);  // host only: validated hit -> damage/score
    void OnEvent(MatchContext m, PlayerEvent e); // death, respawn, buy, use, pickup touch, leave
    ModeSnapshot Snapshot(MatchContext m);     // what clients mirror and the HUD shows
}
```

- **Shared services** used by modes: `TeamService` (assignment, balance, side
  swap), `RoundController` (phases with deadlines on the host clock),
  `DamageModel` (weapon table → damage by zone and distance, armour,
  lifesteal, friendly-fire policy), `Economy`, `Objectives` (zones, bomb,
  flags), `Pickups`, `SpawnSelector` (team masks, weights, spawn-kill
  avoidance), `ScoreBoard`, `Validator` (section 5.4).
- **The host is the only writer.** `MatchContext` holds the authoritative
  state: players (team, alive, health, armour, money, loadout, score),
  objective state, pickup timers and the round phase. Clients get a
  `ModeSnapshot` at most 4 times a second on change, plus discrete events.
  Snapshots are replace-not-merge, so a missed event self-heals.
- **Deterministic generation.** `ScenarioPlan` feeds the existing
  `MatchScenario` generator. Every member builds byte-identical scenario files
  from hashed inputs, as the lobby already does. New inputs: the mode id, its
  settings, the avatar count and the map metadata hash.
- **Host migration.** The lobby already migrates the host. Modes need
  `MatchContext` to be serialisable, and the host broadcasts a checkpoint at
  every round boundary. On migration the new host resumes from the last
  checkpoint and the round in progress is replayed from its start (or
  declared void, by mode setting).

#### 4.2.1 Generated arenas and the load gate (built)

A live CS match once ran its rounds while KovaaK's still showed the previous
map. `core-scene.json` had the match scenario's name, but its `mapName` was
`kovaim1.map`. Rebuilding that arena offline from the same map port
(`--generate-arena <base.sce> <mode> <out.sce>`) showed:

- The `[Map Data]` bytes, `MapName` and `MapScale` were identical to the base.
- The generator had appended its Character, Bot, Aim, Dodge and Weapon
  profiles after the base's last Weapon Profile, so they were out of the
  grouped order KovaaK's saves scenarios in.

Grouping the sections did **not** fix it. The next live run, with a grouped
deathmatch arena, kept `defaultscenario.map`.

The cause is in KovaaK's map pipeline (3.9.11, read from the game binary;
details in `native-mod/DESIGN.md`, "Map loading"). `AMetaGameState` applies
a scenario's map from three scenario events:

- Initialize: only for a new scenario **with `IsChallenge=true`**;
- play-type change (freeplay/challenge): local scenarios only if the map
  name or scale differs, online and trainer scenarios always;
- leaving or entering the editor: only if the name or scale differs.

Every AimMod arena and map port has `IsChallenge=false`. So loading one
never applies its map; only a later play-type change does. That is why
switching freeplay to challenge and back "fixed" it.

AimModCore now loads the map itself with KovaaK's own apply step. After
every load, start or end-run of an AimMod scenario outside a challenge, and
on an explicit `ensure-map` command, it reads the map the game parsed from
the scenario and calls `SetCurrentMapName` and `SetMapData`. The service
calls ensure-map from one place, `ScenarioLoader.FixWrongMap`
(`native-service/ScenarioLoader.cs`), the load check that the match load gate
(`MultiplayerService.Load.cs`) and replay playback share.

To find which part of an arena KovaaK's trips on:

- `--bisect-arena <base.sce> <mode> <folder>` writes nine "AimMod Probe NN"
  variants and a `variants.tsv` list. Each variant keeps the map untouched:
  - 00: the base file with only its Name changed;
  - 01: the base rewritten by AimMod's writer;
  - 02: the base with a generated-length name;
  - 03: the base plus the header changes;
  - 04: the base plus the unused avatar character profiles;
  - 05: the base plus the hidden bot (its profiles and the bot list);
  - 06: the base plus the weapons and changed profiles;
  - 07: everything but the avatars;
  - 08: the whole arena.
- `--install-probe-variants <folder> [--game <root>]` copies them into
  KovaaK's Scenarios folder and asks AimModCore to refresh the list.
  `--remove` deletes every "AimMod Probe" file from there again.

Changes:

- **Section order.** The generator (version 4) groups sections in KovaaK's
  order: Aim, Bot, Bot Rotation, Character, Dodge, the ability profiles,
  Weapon. The map comes last, exactly as the base had it. This is kept for
  tidiness; it wasn't the cause.
- **Validation.** `MatchScenario.Validate(base, generated)` reports:
  - a changed `[Map Data]` (byte for byte), `MapName` or `MapScale`;
  - sections out of order, and duplicate profiles;
  - a player profile, added bot, bot character or player weapon that the file
    doesn't define.

  The service refuses to write a match scenario with a problem that its base
  doesn't already have. The self-tests generate every mode's arena from a
  CRLF port with a multi-line JSON map and expect no problems.
- **Load gate, all modes.** A player counts as loaded only after
  `core-scene.json` shows the round's scenario, with its `MapName` (with or
  without the extension, any case) at its `MapScale`, `loading:false`, on two
  polls in a row. The countdown, and CS freeze and buy time, start only once
  every present player is loaded. Until then, everyone sees "Waiting for
  everyone to load (n/m)".
- **Retry and abort.** If KovaaK's shows the round's scenario with the wrong
  map or scale, the client sends `ensure-map` right away (AimModCore's `map`
  capability). Without that capability, or when AimModCore answers
  `unsupported`, it loads the scenario again instead; any other wrong scene is
  retried that way after 15 s. If the map is still wrong 15 s after the fix,
  the client reports `loaded {ok:false, reason, attempt}`, with the
  ensure-map error in the reason. An ensure-map answered `done map-ok` or
  `done map-loaded` counts as the map loaded while the scene shows the
  round's scenario, not loading, even if its map name disagrees (logged).

  A reported problem, or 45 s without everyone loaded, fails the load. The
  match never starts on its own after that. Everyone sees "Couldn't load the
  match (n/m)" with each player's reason, and the host gets Retry
  (`retry-load`, a new `LoadAttempt` with nobody loaded) and Abort (`end`).
  Reports from an earlier attempt don't count.

  A player still in a challenge run reports
  `loaded {ok:false, pending:true}`. Everyone sees "Still in a challenge
  run." as their reason, but only the time limit fails the load. Once the run
  ends, their map loads and the match starts by itself, even after a failed
  wait.
- **What players see.** The round box shows the map check ("Checking the
  map", "Your map didn't load" with the reason, "Your map loaded"), never
  "Loaded" while the map is wrong. The loading screen counts who is ready,
  lists each player's reason, and offers the host Retry or End match once the
  load fails. It never says the match will start anyway.
- **Frozen until go-live (all modes).** While the match loads and counts
  down, `round-state.tsv` says `phase freeze`. AimModCore then ignores move
  input, switches jumping off and stops the movement component, while
  looking stays free; claims before go-live are already refused. Combat
  modes place every player on a spawn of their own (their team's side where
  the map says) during the countdown, so everyone starts at an assigned
  spawn. CS keeps its own freeze time.
- **Restart lock (all modes).** While a match runs, KovaaK's restart bind and
  the pause menu's restart button are off (AimModCore, native-mod/DESIGN.md
  "Restart lock"). A press shows "Restart is off during a match". If a
  restart still gets through, the match carries on: host scores stand, a
  score run keeps the score from before the restart, and the player is put
  back where they were.
- **Debug copies.** A match scenario stays in the game's Scenarios folder
  while its lobby needs it. One that failed to load is copied to
  `<output>/match-debug/` before cleanup removes it. Only the last three are
  kept.

### 4.3 What AimModCore's Play module needs

Every entry point is resolved by name and signature-checked like today's
bindings. A missing one disables only the modes that need it, and the
compatibility block lists it.

| Capability | Entry points (3.9.11) | Needed by |
| --- | --- | --- |
| `avatar.claim` / `avatar.spawn` / `avatar.release` | scenario `AddedBots` claimed by bot profile name; `ATheMetaAIController::Spawn(ctx, profile, team, lives)`; `RemoveSelf`; fallback `StaticMeshActor` proxy | all PvP and practice together |
| `avatar.inert` | `SetUseWeapons(false)`, `StopAiming`, `ClearMovementInputs`, controller `SetActorTickEnabled(false)`; `CharMoveComp.SetMovementMode(MOVE_None)` | all |
| `avatar.pose` (per frame, pre-tick) | `K2_SetActorLocationAndRotation(..., bTeleport=true)`; movement component `Velocity` write; `UpdateClientLocAndRot` [probe] | all |
| `avatar.look` | `SetTeam`/`OverrideTeam`, `UpdateVisibility`, `SetActorHiddenInGame`, `OverrideInvulnerable`, `SetHealth` (health bar mirror) | all |
| `local.state` (per frame) | camera pose (existing sampler), `GetVelocity`, crouch/air flags (`bCrouching`, `bLastOnGround`), weapon slot, `CurrentAmmo`, `FireHeld`, weapon counters, `CharactersHit`, `Health`, `Lives` | all |
| `local.damage` / `local.heal` / `local.health` | `HandleDamage`, `TakeDamageFunc`, `HandleHeal`, `SetHealth`, `SetCharacterProfileMaxHealth`, `SetStuffToMax` | DM, vampiric, instagib, CS, CTF |
| `local.respawn` / `local.teleport` | `Respawn`, `K2_TeleportTo`, `TeleportDelayed`, `RespawnPlayerAndDestroyProjectiles`, `SetLives` | DM, CS, CTF |
| `local.weapon` | `SetWeaponProfileByString(name, slot)`, `ChangeWeapon`, `RefillAllAmmo`, `SetCurrentAmmo` | CS shop, drops, pickups, instagib |
| `local.freeze` | `SetIgnoreMoveInput`; attack block [probe]: `bAbilityBlockingAttack` or `StunMe` | CS, round modes |
| `local.velocity` | `LaunchCharacter`, `ApplyFlatKnockback` | grapple, jump pads, knockback rules |
| `local.team` | `SetTeam` on the player character | team modes |
| `input.query` | `APlayerController::IsInputKeyDown(FKey)`, `GetInputKeyTimeDown` for the use, plant, defuse and grapple keys | CS, CTF, grapple |
| `world.markers` | `BeginDeferredActorSpawnFromClass(StaticMeshActor)` + `SetStaticMesh`/`SetMaterial`/`SetActorScale3D`; `AMapCreatorTextLabel`; `ACableActor`; `SpawnEmitterAtLocation` for effects | CS, CTF, pickups, grapple |
| `world.spawns` | `GetSpawnPointsForTeam`, spawn point transforms | all with respawn |
| `world.trace` | `KismetSystemLibrary::LineTraceSingle` | grapple anchor, ray hit evidence |

Hard rules for the module:

- It only acts while armed (section 2).
- It only touches the local player, the avatars it claimed and the actors it
  spawned.
- It keeps an undo record for every change and replays it on disarm.
- Commands are validated twice: by the service and again in the mod, with
  ranges such as health 0–1000, velocity at most 10000 cm/s, teleport only to
  points inside the map bounds, and weapon names from the match scenario only.
- Every command is logged at a bounded rate.

### 4.4 Network messages

The existing `aimmod.mp` v1 envelope (JSON, ≤ 16 KB, reliable or not) carries
the low-rate messages. High-rate pose frames get a compact binary frame on the
same connection, because JSON at 60 Hz × 9 peers is wasteful. Per frame that's
about 40 bytes, so a 10-player match stays well under 30 KB/s at the host.

**Fast frames (binary, unreliable, no-delay):**

| Type | Direction | Body |
| --- | --- | --- |
| `pose` | client → host → all (host relays) | player index (u8), sequence (u16), sender render time in ms on the host clock (u32 wrap), position (3 × f32), velocity (3 × i16 at 1 cm/s), yaw and pitch (2 × u16), flags (u16: crouch, air, firing, ADS, reloading, dead, grapple), weapon slot (u4), animation hint (u4) |
| `aim` | client → host (tracking modes, optional) | batched view samples between poses: dt, yaw, pitch (6 bytes each), used for tracking validation |

**Reliable envelopes (JSON):**

| Type | Direction | Body (fields) | Modes |
| --- | --- | --- | --- |
| `mode.config` | host → all | mode id, settings, map metadata hash, team list, avatar slot ↔ member map | all |
| `mode.state` | host → all | phase, phase end time (host clock), round, side, team scores, per-player alive/health/armour/money/score, objective state; ≤ 4 Hz, on change | all |
| `spawn` | host → one | spawn transform, health, armour, loadout (slot → weapon), invulnerable seconds | all with respawn |
| `hit` | client → host | shot id, shooter render time, weapon slot and profile hash, target member, zone (head or body), shot origin and direction, native evidence (damage delta, headshot flag), ray evidence (hit point, distance) | DM, vampiric, instagib, CS, CTF |
| `track` | client → host, 10 Hz | cumulative on-target time per opponent, samples, firing time, sum of view samples | tracking duel |
| `damage` | host → all | victim, attacker, amount, zone, weapon, victim health and armour after, attacker heal (lifesteal) | damage modes |
| `death` | host → all | victim, killer, weapon, headshot, assist list | damage modes |
| `respawn` | host → all | member, at time | DM, CTF |
| `econ` | host → one / all | balance, delta, reason (kill, round win/loss, plant, defuse, buy, refund) | CS |
| `buy` / `buy.result` | client → host / host → client | item id / accepted, new balance, slot | CS |
| `use` | client → host | action (plant, defuse, pickup-weapon, drop), begin or cancel, client time | CS, CTF, drops |
| `objective` | host → all | bomb state (carried, dropped at, planting, planted at site, defusing, defused, exploded), flag state per team (home, carried by, dropped at, return timer) | CS, CTF |
| `pickup` | host → all | id, type, position, state (available or taken), respawn time | arena, drops |
| `grant` | host → one | weapon or slot, health, armour, ammo, powerup and duration | arena, drops, CS |
| `round.end` | host → all | winner side, reason (elimination, bomb, defuse, time, capture, score), MVP, economy summary | round modes |
| `match.end` | host → all | standings, per-player stats, replay hash request | all |

Unchanged: `hello`, `welcome`, `snapshot`, `command`, `result`, `score`,
`finish`, `ping`, `pong`, `bye`. Unknown types are still dropped, so older
clients fail closed and the lobby's version check keeps them out of matches
they can't play.

## 5. Netcode

### 5.1 Who simulates what

- Each player's own movement, aim and shooting run in their own game,
  unchanged. Feel is identical to single-player KovaaK's.
- Remote players are avatars driven from pose frames. Nothing about them is
  simulated locally beyond animation.
- The host simulates nothing physical. It validates poses (speed bounded by
  the movement profile plus knockback, grapple and jump-pad allowances;
  teleports only when it ordered one), keeps a 1 s pose history per player,
  and runs the rules.

### 5.2 Interpolation and extrapolation

- **Clock.** The existing NTP-style `ClockSync` (minimum RTT of 8 samples)
  gives each client the host clock. Pose times are host-clock milliseconds.
- **Render delay.** Each client renders remote players at
  `t_render = t_host_now − D`, with `D = max(2 × frame interval, jitter
  buffer)`. That gives D ≈ 100 ms at 20 Hz, 50 ms at 30 Hz and 35 ms at 60 Hz,
  plus measured jitter (95th percentile of arrival spread, clamped to 25–150
  ms).
- **Interpolation.** Position uses cubic Hermite between the two bracketing
  frames, with the sent velocities as tangents, which keeps strafe reversals
  sharp. Yaw interpolates on the shortest arc and pitch linearly. Flags switch
  at the frame time.
- **Extrapolation** happens only when frames are late: linear from the last
  velocity for at most 100 ms (60 Hz) or 150 ms (20 Hz), then hold. Snap
  instead of blending when the host flags a teleport or the error is above
  64 cm.
- **Send rate.** 60 Hz for tracking duel (aim fairness), 30 Hz default for
  combat modes, 20 Hz when a link reports high loss. The host relays at the
  same rate and coalesces to the newest frame per player.
- **Presentation tick.** AimModCore applies avatar poses in the engine-tick
  pre-callback (as the replay presenter does), so a pose is applied before
  the world, camera and weapon traces of that frame. The local hit test then
  sees exactly what is drawn.

### 5.3 Lag compensation and hit validation

Clients use favour-the-shooter hit registration, and the host validates by
rewinding:

1. The shooter claims a hit with its render time `t_render` (sent in every
   pose, so the host knows each client's current delay).
2. The host rewinds the victim's pose history to `t_render` (interpolated the
   same way) and checks that the shot ray hits the victim's hull at that pose,
   with a tolerance of hull radius + 8 cm (10 cm for the head). The shot
   origin must be within 32 cm of the shooter's own recorded pose.
3. Caps: rewinds of more than 200 ms (250 ms for projectiles) are rejected;
   the fire rate can't exceed `1 / TimeBetweenShots` × 1.1 (burst and
   multi-pellet rules from the weapon profile); damage is recomputed by the
   host from the weapon table (zone, falloff, headshot multiplier). The
   client's damage value is evidence only.
4. Shots that land after the shooter died on the host (because of latency)
   are honoured only if `t_render` is before the death time. That gives trades
   the way CS does them.

Accepted claims become `damage` events. Rejected claims are counted per
player. Repeated rejections mark the match result "disputed" (as for score
races) and appear in the post-match report.

### 5.4 Anti-cheat and fairness

- **The host can't be trusted either.** Every client validates the host's
  results, and only Hub-verified matches count. After the match each client
  uploads its replay (format 2 already holds camera, inputs and registered
  hits) plus its pose stream. The Hub re-runs the hit validation from both
  sides' recordings and the same weapon table.
- **Cross-checks that cost nothing:** native evidence and ray evidence must
  agree for most hits; a claimed hit with no native hit counter change is
  suspicious (except for tracking ray-only scoring); impossible view speed
  (from `aim` samples); speed over the movement-profile bound.
- **Scenario integrity:** the scenario hash is already checked in the lobby,
  so everyone plays the same profiles. AimModCore reports the effective
  weapon and character profile hashes at round start, and a mismatch blocks
  the round.
- **Anybrain** runs only for ranked scenarios, so PvP rounds never start an
  Anybrain match. AimMod doesn't touch Anybrain and doesn't inject input.
  Note that the game's player controller has its own checks
  (`bCheatsDetected`, `StrictCheck`, `LooseCheck`, `InputLagChecker`) [dump].
  AimMod never calls them, and the probe logs whether anything trips during a
  test round.
- **Privacy:** relay-only SDR, no IPs to peers (multiplayer.md).

## 6. Mode specifications

Settings below are lobby settings (validated and clamped by the host, as
`LobbyRules` does today). Every value is a default.

### 6.1 Score race (exists) and practice together

- **Score race:** unchanged (lobby mode `score-race`). The same scenario as
  published, highest score wins, verified by replay hash. Optional ghost
  crosshairs. No avatars, no state changes.
- **Practice together** (lobby mode `practice`): the generated scenario with
  the host's chosen scenario and map, plus avatars for the other players
  (invincible both ways, no damage). Each player has their own targets, as in
  single-player, and sees others moving around with a live score ticker. It
  needs only `avatar.*` and the pose channel, so it's the first real test of
  avatars.

### 6.2 Deathmatch (FFA and 1v1)

| Setting | Default | Range |
| --- | --- | --- |
| Frag limit | 20 (1v1: 15) | 1–100 |
| Time limit | 10 min | 1–30 |
| Health / armour | 100 / 0 | 1–500 / 0–200 |
| Respawn delay | 2 s (forced, no manual) | 0–10 |
| Spawn protection | 1.5 s or until firing | 0–5 |
| Weapons | scenario weapon in slot 1 | weapon set |
| Health on kill | 0 | 0–100 |
| Self-damage | off | |

Score: +1 per kill, −1 per suicide. Spawns come from the `SpawnSelector`,
which picks the spawn farthest from enemies' current host positions, weighted
by `Weight`.

#### 6.2.1 What's built: deathmatch, vampiric 1v1, instagib (phase 2, service side)

These use the same host-validated claim model as the tracking duel.
`CombatMatch` on the host owns health, deaths, frags, respawns, spawn
protection and lifesteal. Clients only claim hits.

**Modes:**

| Mode | Players | Frag limit | Match length | Respawn | Rules |
| --- | --- | --- | --- | --- | --- |
| `deathmatch` | 2–8 | 20 | 5 min (1–10 min) | 2 s | — |
| `vampiric` | exactly 2 | 10 | 5 min | 2 s | lifesteal 50% (0–200%, steps of 5); +25 hp per kill; 2 hp/s decay that never kills; no overheal |
| `instagib` | 2–8 | 25 | 5 min | 1 s | — |
| `team-deathmatch` | 2–8 | 50 (team total) | 5 min | 2 s | teams 1/2 by join order, alternating; no friendly fire (`teammate`) |

- **Weapons.** Deathmatch and vampiric use the AimMod Combat Rifle (hitscan,
  20 damage, ×2 headshot, 10 shots/s). Instagib uses the AimMod Railgun
  (1000 damage, 1.2 s between shots). Lobby weapon presets don't apply: the
  host validates against the mode's own weapon.
- **Spawn protection** lasts 1.5 s and ends early when you fire.
- **Scoring.** Placement is by frags, then fewer deaths. A player with more
  than 20% of claims rejected (at least 10 claims) is marked disputed.
- **Arena (generated):** a vulnerable player (`InvinciblePlayer=false`) with
  exactly the mode weapon, no native lifesteal, regen or health on kill,
  native respawn delays equal to the mode's, no native score, and the same
  invisible helper bot.

**Claim validation** (`CombatMatch.Claim`; reasons are counted per player, and
every decision goes back to the shooter as `hit-ack`; details in 6.2.2):

1. Order and time:
   - a claim number already decided is answered again with its first
     decision and never applied twice (`repeated`);
   - the shot time is inside the match, at most 1.5 s old and at most 200 ms
     ahead of the host clock (`time`);
   - the shooter is alive, or the host killed it after the shot was fired
     (a trade, within 300 ms) (`shooter-dead`);
   - fire rate: every run of k accepted hits spans k × 90% of
     `TimeBetweenShots`, less 20 ms of frame slack (`fire-rate`).
2. Shooter checks (a claim past the shooter's own track waits up to 300 ms for
   it, then `no-shooter-track`):
   - the ray starts within 32 cm of the shooter's own camera track
     (`origin`);
   - it looks within 3° of the camera's turn between the two samples around
     the shot (`aim`).
3. Victim:
   - the opponent whose own track comes closest to the drawn target (25 cm)
     within the rewind for that shooter and victim: the measured view delay
     + 100 ms, at least 200 ms, at most 500 ms (`target-mismatch`);
   - otherwise (or without a drawn target) the host's own rewound ray test,
     if the drawn target lies within 150 cm of that rewound track (`miss`);
   - a matched victim the host already killed (`victim-dead`).
4. Geometry:
   - the ray must hit that hull, with 4 cm for rounding, 15 cm when the
     game itself counted the hit (`ray-miss`);
   - the victim must not be spawn-protected (`spawn-protected`).
5. Damage is computed by the host:
   - the head zone is AimModCore's: where the ray enters the hull is at least
     60% of the half height above the centre. AimModCore's head flag holds
     within 4 cm of that line, and the game's own headshot (its damage per
     hit shows the multiplier) counts anywhere above 20% of the half height;
   - the weapon's fixed headshot multiplier (CS: ×4, the rifle ×2, the knife
     and railgun ×1), then armour in CS;
   - damage is clamped to the victim's remaining health, so overkill doesn't
     heal;
   - then lifesteal, death, frag, and the respawn timer.

**Data path:**

- Every player streams `track` batches, as in the duel.
- Hits come from a new AimModCore feed. Each becomes a `hit` message (on the
  host clock), including the drawn position of the target the game hit,
  taken from the latest `self-pose.tsv` target rows.
- Health, life and events reach clients in the match snapshot's `combat`
  view (≤ 4 per second; the last 16 events).
- Each client writes its own state for AimModCore to apply.

**Contracts with AimModCore.** AimModCore implemented the first two; their
exact shapes are in `native-mod/DESIGN.md` ("Match play"), and the service
follows them:

- `self-shots.tsv`: the shots AimModCore publishes while
  `self-shots.request` is fresh; the service rewrites that file every 2 s
  during a match, and within 100 ms whenever it took new shots, as
  `<unix ms>\t<last shot seq taken>\t<session>` (the acknowledgement).
  - Header `AIMMOD_SHOTS_1\t<publish seq>\t<session>`. A new session restarts
    the shot sequence.
  - Rows `shot\t<ms>\t<seq>\t<origin x y z>\t<unit direction x y z>\t<slot>\t<target>\t<headshot>\t<gameHit>\t<capsule x y z>\t<radius>\t<half height>\t<game damage per hit>\t<source>`.
    The service turns the direction into pitch and yaw. It claims every shot
    with a target that the game counted as a hit (before the game's counter
    has counted any hit, every shot with a target), with the capsule from
    the row as the drawn target.
  - AimModCore keeps every shot until it is acknowledged (at most 256, 15 s),
    so a slow or stalled poll never loses one.
  - `tag` rows are allowed.
  - The 13- and 11-column row shapes still read.
- `play-state.tsv`: written by the service on change and at least every
  second, because AimModCore drops it after 5 s. Unknown rows reject the
  whole file, so it holds only:

  ```
  AIMMOD_PLAYSTATE_1\t<seq>
  match\t<the match scenario's exact name>
  health\t<current>\t<max>
  alive\t<0/1>
  respawnAt\t<local unix ms, 0 = none>
  protected\t<0/1>
  hit\t<event id>\t<attacker member id>\t<damage>\t<headshot 0/1>\t<dx>\t<dy>\t<dz>   (the shot's direction, from the host)
  ```

  AimModCore applies it only in that scenario, in freeplay:
  - `protected`: invulnerability;
  - a new hit: `HandleDamage`, for the native effect, never lethal by itself;
  - `alive` 1 → 0: `OnCharacterKilled`, and `SetRespawnTimer` to
    `respawnAt`;
  - `alive` 0 → 1: `Respawn`;
  - health follows `SetHealth`.
- **New, requested from AimModCore:** `round-state.tsv`, so `play-state.tsv`
  stays as AimModCore parses it. It carries what doesn't fit there:

  ```
  AIMMOD_ROUND_1\t<seq>
  match\t<scenario>
  spawn\t<id>\t<x>\t<y>\t<z>\t<yaw>                                  (teleport once per id: host-chosen respawns, CS round starts)
  phase\t<freeze|live|planted|end|over>\t<frozen 0/1>\t<buy window 0/1>\t<phase ends, local unix ms>   (CS)
  loadout\t<primary profile or ->\t<pistol profile or ->\t<armour>\t<helmet 0/1>\t<kit 0/1>          (CS)
  ```

  It's rewritten on change and every second.

**Checks:** 32 self-tests cover the rules, every rejection reason, headshots,
death, frag, respawn, spawn protection, the host-rewind fallback, lifesteal,
decay, instagib, the wire formats, a deathmatch ending at its frag limit, and
the arena.

**Since then (phase 2, second part):**

- **Immediate events.** The host pushes every new damage, death and respawn
  to every peer in a reliable `combat` message the moment it decides them.
  Clients apply pushed events on top of the last snapshot
  (`CombatOverlay`), so health, deaths and frags show without waiting for
  the 250 ms snapshot. The snapshot still corrects any gap.
- **Combat HUD.** In the notice layer, on the same top-edge strip as the duel:
  - health (a bar that turns red under 30) or "Back in N";
  - "protected" while spawn protection lasts;
  - frags against the limit;
  - the best opponent, or both team scores in team deathmatch;
  - time left (m:ss);
  - up to three kills from the last 6 s underneath (your kills green, your
    deaths red).
  It comes from `combat` in `multiplayer-notify.json`, takes no input, and
  stays out of the crosshair area.
- **Team deathmatch** (`team-deathmatch`): see the table above. A team win
  places all of its members first and has no single winner, and the combat
  view names `winnerTeam`.
- **Spawn selection.** The host reads the arena's spawn points from the
  generated scenario (`MatchScenario.Spawns`):
  - map-creator JSON `SpawnPoint` objects: location × MapScale, yaw from the
    rotation, TeamMask;
  - Reflex `PlayerSpawn` entities: (a, b, c) → (c, a, b) × MapScale, with
    teamA/teamB flags.

  On respawn it picks the team-allowed spawn farthest from the nearest living
  opponent. The respawn event carries it, and `round-state.tsv` gets a `spawn`
  line that AimModCore teleports to once per event id. Without spawn points,
  the game's own respawn stands. The coordinate conversion follows map-port's
  calibration and needs a live check.
- **Death effects and team colours on avatars** (AimModSteam, requested through
  the coordinator). The service writes `avatar-state.tsv` during combat
  matches:

  ```
  AIMMOD_AVATARS_1\t<sequence>
  match\t<%-escaped match id>
  peer\t<member id = SteamID64>\t<alive 0/1>\t<friend|enemy>\t<health>\t<died at unix ms, 0>\t<respawn at unix ms, 0>[\t<weapon>]
  ```

  In CS the optional `weapon` column is the third-person model of what that
  player holds (6.6.2), or `-`.

  When `alive` drops to 0, the bridge plays the death on that avatar (native
  `Death`/gib if it stays inert, otherwise hide it with
  `SetActorHiddenInGame`). It shows the avatar again on respawn. `friend`
  avatars go on the local player's team (`SetTeam`), so team colours and the
  game's team checks are right. `health` can drive the avatar's health bar
  (`SetHealth` on the invulnerable avatar).

**Still open:** Hub verification of results (8.4); kill effects need the
bridge change above.

#### 6.2.2 Hit registration

A shot the shooter's game showed as a hit should count, and nothing else
should. The pipeline, end to end:

1. **The shot (AimModCore, every frame).** A weapon's `ShotsFiredThisSession`
   advancing is a shot, `ShotsHitThisSession` the game's own hit (the
   hitmarker the shooter saw), `DamageDoneThisSession` the damage per hit.
   Shots counted together in one frame are spread back over that frame. The
   target is the actor `Send_ShotHit` named (when the game calls it through
   reflection), else the drawn capsule the camera ray meets, else, for a game
   hit only, the capsule it passes within 15 cm of (the visible mesh is wider
   than the capsule; the head can stick out above it). The row carries that
   capsule exactly as drawn in that frame.
2. **The feed (service, every 100 ms).** `ShotFeed` claims the game hits
   with a target, numbers the claims per match, counts game hits without a
   target, ray hits the game called misses, gaps in the shot sequence and
   stale shots, and acknowledges the last shot it took.
3. **The claim (client to host).** A reliable `hit`, kept in `ClaimOutbox`
   and sent again every 400 ms until the host's `hit-ack` answers it, for up
   to the claim window plus 1 s.
4. **The decision (host).** `CombatMatch` keeps every player's timestamped
   camera track (60 Hz samples, 10 s) and, from each player's tagged drawn
   targets, where its game drew everyone else. From those it measures each
   shooter's view delay per victim (the median lag at which its drawn rows
   match the victim's track; a remote bot's relay can be 300 ms) and rewinds
   the victim that far (see 6.2.1 for every check). A track is a camera: each one carries its
   hull (	rack field `b`: camera height above the capsule centre, radius, half
   height; bots: their drawn avatar, about 167 cm in CS ports, 64 cm elsewhere),
   and every check turns the camera Z back into that capsule centre and head. The bots' own tracks are
   the host's drawn avatars, so the hull the host validates against is the
   one its game hit-tests. `self-pose.tsv` carries the last 16 camera samples
   and the drawn targets of the previous publications (`seen` rows), so no
   sample is lost between polls.
5. **The confirmation.** Damage, deaths and kills arrive as `combat` events,
   which drive the crosshair hit marker and the HUD. Each claim's decision
   comes back as `hit-ack` (`ok`, `reason`, `detail`), and repeats get the
   first decision.

**Diagnostics** (service log):

- `Hit refused: <player> claim #<n> (shot #<n>) <reason>: <what was
  measured>` on the host, for every refusal: how far the ray passed outside
  the hull, the nearest track and its rewind, the aim error, the fire
  interval.
- `Hit refused by the host: ...` on the shooter's machine, from `hit-ack`.
- Every 10 s of a live match, `Hits: ...`: shots read, game hits, game hits
  without a drawn target, ray-only shots, shots lost before reading, claims,
  and the decisions (accepted, refused by reason, waiting, resent,
  unanswered); on the host, per shooter.

AimModCore logs `match play: shots (...)` every 10 s while shots flow: shots
fired, game hits (on the drawn capsule, just beside it, named by the game,
without a target), ray hits the game counted as misses, `Send_ShotHit` calls,
the last shot, the acknowledged shot and the shots lost. It logs each game
hit without a target near the ray (at most once a second).
### 6.3 Tracking duel

**Decided (user): simultaneous.** Both players track each other at the same
time while trying not to be tracked. There are no alternating attacker and
dodger rounds.

- Each player's score is their own time on target against the other's avatar
  hull.
- One round has both players tracking. The higher time-on-target share takes
  the round, and the match goes to the most rounds won, then the higher total.
- Rounds (default 3, up to 9) and round length (default 10 s, 10–60 s) are
  lobby settings.
- Holding fire is not required. "Require fire" is an optional lobby setting,
  off by default, that counts time on target only while the fire button is
  held. It uses AimModCore's `fire` row.

**Scoring.** Measured for each player from that player's own stream, every
sample weighted by its frame time, from what that player sees:

- `onTarget(t)` is true when the camera ray (at the frame's final view
  rotation) intersects the opponent avatar's hull at its rendered pose. The
  hull is the avatar profile's bounding body (capsule) and head (sphere).
  With "require fire" on, a sample counts only when its publication's `fire`
  row says the weapon fired.
- **Time-on-target:** `TOT = Σ onTarget(t) · dt`. Round score = `TOT /
  roundDuration` in %, shown with one decimal.
- **Head bonus** (optional, default off): a ray through the head sphere counts
  1.5×.
- **Damage-equivalent** (shown for KovaaK's familiarity, not used to decide):
  `TOT × DPS` of the duel weapon (for example 200 DPS), so 10 s fully on
  target = 2000.
- **Hit ticks** from the native beam weapon (`ShotsHit/ShotsFired`) are kept
  as evidence. They depend on `TimeBetweenShots` and frame rate, so they don't
  decide the result.

**Fairness when both move.**

- Both players see each other through the same pipeline (render delay D plus
  one-way latency), so the visual lag of the target is symmetric, and both
  are scored by the same rules in both directions.
- The host scores each direction against the hull the shooter's game drew,
  only where it matches the target's own track within 200 ms (the rewind cap).
  Otherwise it rewinds the target's track itself. Mismatched hulls (> 10%),
  short coverage (< 80%) or a missing target track dispute the round.
- Both players use the same movement profile and avatar hull (the lobby
  forces one character profile for both; skins are cosmetic and use the same
  hit hull), and the arena is symmetric with mirrored spawns.
- An anti-camping option (default off) is a dodger speed floor: below
  `minSpeed` for more than 1 s, the attacker's TOT accrues at 1.25×.

#### 6.3.1 What's built (lobby service, `feat/kovaaks-game-modes`)

**Update: the simultaneous rework is in.**
- `TrackingRound` scores both players at once (`ScoreFor` each direction), so
  every round has two scored placements and a winner.
- The HUD shows two bars, yours and theirs (time on target so far), both
  scores, round wins and the seconds left.
- Drawn targets use AimModCore's `tag` rows (target → the player's stream
  `s-<16 hex>`, the bridge's FNV-1a of `aimmod-spectate:<SteamID64>`), so the
  helper bot and any other bot are ignored without guessing.
- `fire` rows mark samples for "require fire".

The text below describes the first, alternating build; the data path and
validation are unchanged.

The defaults are under user review: alternating rounds, aim-only scoring, and
host-checked hits with a 200 ms rewind cap.

- **Mode `tracking-duel`** (`LobbyModes.Tracking`):
  - exactly two players;
  - `rounds` = attacks each (default 3, max 5), so 6 rounds by default;
  - `timeLimit` = round length (default 10 s, range 10–60 s; never the
    scenario's own length);
  - roles alternate: the first player attacks in odd rounds;
  - the dodger's line shows `dodger`;
  - the match is won on the total time-on-target %, then the best round.
- **Arena scenario.** `MatchScenario` always generates one:
  - invincible players and bots;
  - `ScorePerHit/Damage/Kill=0`, so nothing scores natively;
  - the base scenario's target bots are removed;
  - one invisible, passable, inert helper bot (`AimMod Hidden Bot` with body
    `AimMod Hidden`) is added, because AimModSteam spawns avatars from a bot
    profile the scenario already has. `MainBBHide`/`ProjBBHide` don't hide it
    in 3.9.11: a live TDM showed its BodyBB components and collision cylinder
    visible and colliding (QueryAndPhysics), so it took shots and counted for
    KovaaK's accuracy. AimModSteam therefore parks the scenario's own instance
    every second in AimMod arenas: hidden, collision off, AI and weapons off,
    invulnerable, `MOVE_None`, at (5 km, 5 km, 500 m), outside any map and
    inside the world bounds. Whether KovaaK's runs an arena with no bot at
    all (empty `AddedBots`) is still untested; the spawn of avatars from the
    profile may depend on it;
  - the KovaaK's run lasts 10 s longer than the round, so the host, not the
    game, ends the round.
- **Data path.**
  - During a round, every player's service keeps AimModCore's
    `self-pose.request` fresh.
  - It reads `self-pose.tsv` and sends `track` batches to the host every
    100 ms (reliable).
  - The batches hold camera samples (`t, x, y, z, pitch, yaw`) and "seen"
    rows: the target capsules the game drew, stamped with the newest pose's
    time.
  - All times are on the host clock (`ClockSync`).
  - KovaaK's own score frames are refused in this mode.
- **Host scoring** (`TrackingRound.Compute`):
  - The dodger is the drawn target whose rows best match the dodger's own
    track, so other drawn targets (the helper bot) are ignored.
  - A drawn row counts when it matches the dodger's track within 25 cm at a
    lag of 0–200 ms (the **rewind cap**). The attacker's ray is then scored
    against that drawn capsule: favour the shooter.
  - Other samples use the dodger's own track, rewound by the measured median
    lag, capped at 200 ms. Without drawn rows, the lag is estimated as
    100 ms + half of both round trips.
  - Eye height and capsule size come from the drawn rows when present.
    Otherwise the defaults are a 64 cm eye above the centre and the avatar
    profile's 45 cm × 115 cm bounding box.
  - Gaps over 50 ms count as off target.
  - A round is **disputed** when coverage is under 80%, when more than 10% of
    drawn rows don't match, or when there's no dodger track.
  - The live percentage feeds the match snapshot (`tracking`) a few times a
    second for the HUD.
- **Checks:** 36 self-tests, including geometry, fair tracking at 120 ms lag
  (> 97%, lag measured), blind host rewind, fabricated drawn hulls (disputed),
  a 350 ms lag beyond the cap (capped and disputed), half coverage, alternating
  rounds with a winner on total, a player leaving, and the arena generator.

**Needs from other components** (requested through the coordinator):

1. **AimModSteam: show avatars in service lobbies.** Avatars are drawn only
   with `ghost_demo=1` today (`showRemote = ghost_demo`). The duel needs them
   whenever a lobby match runs on the same scenario.
2. **AimModSteam: spawn from a named bot profile.** It should spawn from the
   profile the service names (for example a lobby key
   `aimmod.avatar_bot=AimMod Hidden Bot`), and work when the scenario's only
   bot is that helper.
3. **AimModSteam: re-apply looks and inert flags on every scenario load.**
   The probe showed that KovaaK's re-profiles surviving bots in place on a
   scenario change, in the same world (9.1). So `LoadCharacterProfile` and
   the inert flags must be re-applied when the scenario changes, not only
   when the world changes.
4. **AimModCore: identify avatars in the `target` rows.** Add `avatar	<peer
   hash>` (or a flag) to the rows for AimModSteam's avatars, so the host
   doesn't have to infer the dodger by matching. The matching stays as the
   check.
5. **AimModCore: optional extra fields.** A `self` row with the actor
   location and capsule removes the eye-height estimate. A `fired` flag per
   pose row enables "fire to score" and spray statistics.
6. **HUD (done).** The out-of-panel notice layer shows a slim strip on the
   top edge, away from the crosshair, during the countdown and the live round:
   - `YOU TRACK` (green) or `YOU DODGE` (amber), and the opponent;
   - the host's round score so far;
   - a bar of the share of the elapsed round on target;
   - the seconds left.

   It comes from `duel` in `multiplayer-notify.json` (`DuelHud`), takes no
   input, and pushes nothing into the crosshair area. The countdown toast says
   who tracks. The lobby panel shows roles and results.

### 6.4 Vampiric 1v1

A deathmatch 1v1 where damage dealt heals the dealer.

- Lifesteal `L` (default 50%, range 0–200%): on every confirmed hit the host
  heals the shooter by `min(damage × L, maxHealth + overheal − health)`.
  Damage is clamped to the victim's remaining health, so overkill doesn't
  heal.
- Overheal (default 0, range 0–100) above max health decays 2 hp/s.
- Optional decay: −2 hp/s for both players (default on), which forces
  engagement.
- Health on kill: default +25.
- The host sends `damage` with the attacker heal. The shooter's client applies
  it with `HandleHeal` (native effect) and then reconciles with `SetHealth`.
  The native `LifeStealPercent` stays 0, so nothing heals twice.

### 6.5 Instagib

- Weapon: an `AimMod Railgun` profile (hitscan, `DamagePerShot` 1000,
  `TimeBetweenShots` 1.2 s, infinite ammo, a bright trail
  (`HitscanVisual`/beam), optional flat knockback for rail jumps). Every hit is
  a kill. Headshots don't matter.
- Settings: frag limit 30, time 10 min, respawn 1 s, optional jetpack or air
  jumps (native profile fields `HasJetpack`, `AirJumpCount`), and an optional
  grapple (section 6.9).
- Validation is simpler: one claim per shot, rewind check, fire rate ≤ 1/1.2
  s.

### 6.6 CS-style competitive

Teams T and CT, up to 5v5 (the lobby limit must rise from 8 to 10; section
8.1).

**Timers** (CS2 values, with a "short" preset):

| Phase | Standard | Short |
| --- | --- | --- |
| Warm-up | until all ready | same |
| Freeze time (move blocked, look allowed, buy open) | 15 s | 8 s |
| Buy time (from round start, in buy zone) | 20 s | 15 s |
| Round time | 1:55 | 1:30 |
| Bomb timer | 40 s | 35 s |
| Plant time | 3.2 s | 3 s |
| Defuse (no kit / kit) | 10 s / 5 s | 7 s / 4 s |
| Round end delay | 7 s | 5 s |
| Halves | MR12: 12 rounds a half, first to 13 | MR6: first to 7 |
| Overtime | MR3, start money $10000 | off |

**Economy:**

| Item | Value |
| --- | --- |
| Start money (each half) | $800 |
| Max money | $16000 |
| Round win: elimination or time (CT) | $3250 |
| Round win: bomb exploded (T) | $3500 |
| Round win: defused (CT) | $3500 |
| Loss bonus | $1400, +$500 per consecutive loss, max $3400; a win steps the counter down by one, as in CS2 |
| T planted but lost | +$800 to every T |
| Kill reward | by weapon class: knife $1500, SMG $600, shotgun $900, pistol and rifle $300, sniper $100 |
| Team kill | −$300 |
| Survivors on a time-out loss (T) | no loss bonus |

**Shop** (each entry is a weapon profile embedded in the generated scenario;
prices are the CS2 reference values and the lobby can switch to a "flat"
preset):

| Key (slot index) | Item (AimMod profile) | Price | Native profile basis |
| --- | --- | --- | --- |
| 2 (1), pistol | Starter pistol (free, T and CT) | $0 | semi-auto hitscan, 26 damage, ×4 head |
| 2 (1) | Heavy pistol | $700 | 53 damage, slow fire |
| 1 (0), primary | SMG | $1200–1500 | fully auto, 0.075 s, falloff |
| 1 (0) | Rifle (T) / Rifle (CT) | $2700 / $2900 | 36 / 33 damage, 0.1 s, spread and recoil per lobby preset |
| 1 (0) | Sniper | $4750 | 115 damage, ADS (`CanAimDownSight`, `ADSFOVScale`), one-shot body |
| 3 (2) | Knife | free | short-range hitscan in weapon slot 3 (6.6.2) |
| armour | Kevlar / Kevlar + helmet | $650 / $1000 | AimMod armour (below) |
| kit | Defuse kit (CT) | $400 | AimMod flag (defuse 5 s) |

- **Armour** has no native equivalent. The host's `DamageModel` applies it:
  armour value 100, body damage × the weapon's armour penetration (default
  0.775), armour absorbs half the reduction; head damage is reduced only with
  a helmet.
- **Buying:** the client sends `buy`. The host checks the phase, the buy zone
  (host position check), the money and the slot. The answer is a `grant`, and
  AimModCore calls `SetWeaponProfileByString(profile, slot)` and
  `ChangeWeapon(slot)`. Weapons persist between rounds for survivors and drop
  on death (section 6.8).
- **Buy menu:** a Gameface panel opened with the buy key (`input.query`;
  default B, configurable), usable in freeze and buy time, with mouse cursor.
  Items are greyed out by price, side and slot.

**Rounds:**

- Round start: everyone gets `RespawnPlayerAndDestroyProjectiles`, then the
  host teleports each player to a side spawn (team masks), `SetStuffToMax`,
  restores the loadout and freezes (`local.freeze`).
- During the round, death is permanent until the next round. Dead players
  spectate (camera on a teammate's avatar) and the avatar is hidden or gibbed.
- Win conditions are checked by the host in `Tick`.
- Halftime swaps teams: everyone's money resets to $800, loadouts are cleared,
  and `SetTeam` is called.

**Plant and defuse:**

- Bomb sites and buy zones come from map metadata (section 8.2). The host tests
  the player's feet position against the zone volume every pose frame.
- One T carries the bomb (assigned at round start). The bomb drops on the
  carrier's death at the death position, and any T walking within 48 cm picks
  it up.
- **Plant:** the carrier is inside a site, on the ground, and holds the use
  key (`IsInputKeyDown`, default E) for the plant time while standing still
  (speed < 30 cm/s). AimModCore locks movement during the hold
  (`SetIgnoreMoveInput`), as CS does. Releasing cancels.
- **Defuse:** a CT within 64 cm of the bomb, alive, holds use for the defuse
  time. Taking damage doesn't cancel it (CS rule). Moving away does.
- **Markers:** the bomb is a spawned mesh with a blinking material parameter,
  sites have floor decals or text labels ("A", "B"), and the HUD shows bomb
  state and timer.

#### 6.6.0 CS maps, bomb and the pose feed (built)

- **Only CS maps.** A scenario can host CS competitive only if its map's
  `.aimmod.json` has the AimMod CS map spec (tools/map-port README, "CS map
  spec"). The spec has bomb sites A and B, at least 5 T and 5 CT spawns,
  and T and CT buy zones. Being a CS port isn't enough.
  - `ScenarioChoice.CsProblem` carries the reason. In CS the host refuses
    other maps and the start is blocked ("cs-map").
  - Switching to CS with an ineligible map picks the first CS map in the
    library.
  - The lobby view has `eligibility` (scenario name to `{ok, reason,
    players}`) for the current mode. The library view has `modes.cs`
    (`{ok, reason}`) per scenario.
- **Rounds on the map.** Each round starts on your side's spawns. You can
  buy only inside your buy zone during buy time; the menu opens only there
  and closes when you leave. You can plant only inside a site. The HUD
  shows the site letter (and callout) you stand in. A compass shows sites A
  and B, plus the bomb when it is dropped (Ts) or planted (everyone).
- **Bomb.** It goes to the last carrier if they're still a T, else to a
  random T. Teammates see who has it.
  - G drops it 70 cm ahead of you. The dropper can't pick it straight back
    up. Any alive T walking over it picks it up; CTs walk over it.
  - It drops where the carrier dies.
  - A refused plant, defuse or drop says why on the HUD ("Not in a bomb
    site", "You don't have the bomb", ...).
  - The dropped and planted bomb is drawn in the world, and the planted
    bomb beeps (6.6.2, 6.6.3).
- **The pose feed was dropped.** AimModCore writes its `self` and `fire`
  rows with a real Unix-millisecond time (about 1.8e12). The service's pose
  reader capped numbers at 1e12, so it threw away every self-pose frame
  with those rows. The time is now read as an integer.
  - Without that feed the host had no track for the local player. That
    explains hits refused as "no-shooter-track", every buy refused with "Go
    back to your buy zone", plants refused with "no-track", and no site
    letters.
- **Arena data on every lobby core.** A lobby created after the match
  scenario was built (a second CS lobby, a new host) used to run without
  the map's spawns and CS data. That meant buying and planting anywhere and
  no team spawns. The service now hands the arena's spawns and CS map data
  to the lobby core every tick.
- **Simulated players.** In developer mode every simulated player is a
  stand-in of its own: AimModSteam's synthetic peers 1 to 16, feature
  `dev-avatar-walkers`.
  - Each walks between its own side's spawns: its CS side, or its TDM team.
  - Each has its own stream, track, team, health, deaths and respawns,
    validated on the host.
  - Floor traces now go by object type (world static and dynamic) rather
    than the Visibility channel. They start at the spawn point itself, and a
    trace that starts inside geometry counts as no floor, so nobody stands
    on a roof.
  - The HUD strip shows how many players are alive on each side.
- **Name tags (AimModCore + notice layer).** Teammates' names show over their
  heads in their team colour, through walls. Enemies get a name only under
  the crosshair and in line of sight. The service writes `world-tags.tsv`;
  AimModCore projects and pushes the tags every frame (native-mod/DESIGN.md,
  "World tags").
  - Not built yet: a team-coloured outline or emissive accent on the
    avatars. The bridge already puts avatars on the right team with
    `SetTeam`.
- **Spawns on the floor.** Ported spawn origins sat 17 to 48 units above the
  floor, so avatars spawned there floated. The map port now puts every
  spawn's feet on the floor below it, and the checks fail any spawn more than
  8 units up. A CS round-start teleport places the capsule centre: the feet
  plus 40 units, times the map scale. A simulated player that hasn't found
  the floor by trace yet stays where it is instead of walking in the air.
- **Leftover bots.** KovaaK's re-uses bots across scenario loads, and a
  direct map load left the previous scenario's bots in AimMod arenas. They
  were alive and took shots, which counted for KovaaK's accuracy. In an
  arena AimModSteam now parks every bot that isn't the helper bot, and
  spawns avatars from the helper profile only.
- **Buy menu.** It is kept as one element while the HUD redraws, so a click
  is never lost between press and release. Escape closes it as well as B.
  The compass hides while it is open so the two don't overlap.

#### 6.6.1 Decided and built (service side)

**User decisions:**
- Team sizes are 3v3, 4v4 or 5v5 only, so the lobby limit is 10 for CS (8
  elsewhere).
- The CS2 economy, armour and keys as tabled above.
- Armour is buyable: kevlar, and kevlar plus helmet, with the CS2 damage and
  headshot rules.
- Default keys are B (buy) and E (use, plant, defuse), checked against
  KovaaK's binds in
  `%LOCALAPPDATA%\FPSAimTrainer\Saved\Config\WindowsNoEditor\Input.ini`.

**Built** (`CsMode.cs`, `MultiplayerService.Cs.cs`, lobby mode `cs`):

- **Teams.** Teams are assigned by join order (team 1 starts T). The start is
  blocked unless there are 6, 8 or 10 players (`cs-teams`). Friendly fire
  (lobby setting `friendlyFire`, on by default in CS, off in team deathmatch):
  CS2 rules, teammates take 33 % of the damage, a team kill takes the kill
  away and costs $300, and the kill feed marks it TK. Bots fill empty slots
  (6.6.4).
- **Round machine.**
  - Phases: freeze (15 s) → live (1:55; buying in freeze time and the first
    20 s) → planted (40 s bomb) → end (7 s) → next round.
  - Win conditions: elimination; bomb explodes (T); defuse (CT); time (CT).
    If every CT dies after the plant, the T win.
  - Each round, AimModCore gets the round phase and whether the player is
    frozen, the loadout, and a side spawn from the metadata (`play-state.tsv`,
    below).
- **Economy (CS2):**
  - start $800, max $16,000;
  - win $3,250 (elimination or time) or $3,500 (bomb or defuse);
  - loss bonus $1,400 + $500 per consecutive loss up to $3,400, stepping
    down by one on a win;
  - Terrorists get +$800 each after a plant, and none if they survive a lost
    time-out;
  - $300 for the planter and the defuser;
  - kill rewards per weapon class (pistol and rifle $300, SMG $600, AWP
    $100).
- **Shop:** Glock-18 / USP-S (starting pistols, $200), Desert Eagle $700,
  MAC-10 $1,050 (T), MP9 $1,250 (CT), AK-47 $2,700 (T), M4A1-S $2,900 (CT),
  AWP $4,750, Kevlar $650, Kevlar + helmet $1,000 ($350 upgrade), defuse kit
  $400 (CT). Every profile is in the arena.
- **Buy validation (host):** buy window, alive, inside your side's buy zone
  (when the map has them), side, money, already owned. Buys are lobby
  commands (`buy {item}`).
- **Damage.** The claim names the weapon slot (`w`: 0 primary, 1 pistol, 2 knife). The
  host uses the bought weapon (damage, ×4 headshot, fire rate) and applies
  armour the CS2 way:
  - the weapon's armour-penetration share goes to health;
  - armour absorbs the rest at 0.5 armour per point;
  - a headshot is reduced only with a helmet.

  Dead players lose their gear.
- **Bomb.**
  - A Terrorist in turn carries it, and it drops where the carrier dies. Any
    Terrorist who walks over it picks it up.
  - Plant: the carrier holds E inside a bomb site, standing still, for 3.2 s.
    Moving more than 40 cm cancels it.
  - Defuse: a CT within 1 m of the bomb holds E for 10 s, or 5 s with a kit.
- **Halves and overtime.** Sides switch after `halfRounds` (default 12,
  6–15): money resets to $800, the loss bonus resets, and gear is gone. The
  first to `halfRounds + 1` wins. A tie goes to overtime (default on): MR3
  halves at $12,500, first to 4 of 6 per block.
- **Objectives.** The host reads `aimmod_<map>_<game>.aimmod.json` (map-port,
  `aimmod.map-objectives` v1, × `map_scale`) from KovaaK's `maps` folder or
  AimMod's own `maps` folder. It uses the bomb sites (unnamed ones become A,
  B, …), the buy zones per side, and the team spawns. Without metadata, buying
  and planting work anywhere.
- **Keys.** The service reads B, E and the digits only while the game window
  has focus and a CS round runs:
  - B toggles the buy menu in the buy window;
  - digits buy from it;
  - holding E sends `use {held}` edges.

  A clash with an Input.ini bind is shown on the HUD.
- **HUD.** The notice layer's top strip, `cs` in `multiplayer-notify.json`,
  shows:
  - health and armour, or "Down";
  - side and score, the round number and the phase;
  - money and kit;
  - planting and defusing progress;
  - the bomb timer or round clock;
  - the numbered buy menu while open;
  - any key clashes.
- **For AimModCore:** CS uses the `phase`, `loadout` and per-round `spawn`
  lines of `round-state.tsv` (6.2.1). Health, life and hits stay in
  `play-state.tsv`.

  While frozen: `SetIgnoreMoveInput(true)`, with looking allowed. The loadout
  goes to slots 0 and 1 (`SetWeaponProfileByString`, "-" empties a slot). At
  round start, a dead player respawns and everyone teleports.
- **Checks:** 32 self-tests, including the armour maths, loss bonus,
  prices, lobby team sizes, arena, key clashes, metadata parsing, a full 3v3
  run (buy rules, plant 3.2 s, kit defuse 5 s, rewards, elimination,
  halftime switch, first to 7), the time-out rule, and lobby buy commands.

**Not yet:**
- weapon drops and pickups between players;
- a full buy panel with clicks (digits work now);
- per-round avatar resets in AimModSteam beyond `avatar-state.tsv`;
- host migration mid-match (a CS match on a new host restarts its round
  state);
- the native round-start respawn and freeze in AimModCore.

#### 6.6.2 Weapons in the hand, slots, knife and bomb (built)

- **Why no weapon showed.** Every generated profile said `WeaponModel=Rifle`,
  which isn't one of KovaaK's viewmodel names, so the first-person view had no
  weapon mesh. The names are the game's own list (`MatchScenario.ViewModels`:
  `KovaaKs Rifle`, `Heavy Surge Rifle`, `Spider`, `Machine Pistol`,
  `Law Bringer`, `Stud Gun`, `Spike`, ..., and `Blank` for none). Third-person
  models are `WeaponDeveloperSettings`' `WeaponMeshViewModels` (`AK47`, `M4`,
  `SMG`, `Pistol`, `Six Shooter`, `Bolt Action Sniper`, ...). The CS player
  profile sets `HideWeapon=false`; avatars keep `HideWeapon=true`. With
  KovaaK's own "Show Weapon" setting off, the first live test still showed no
  arms or gun: that setting hides the whole view model. AimModCore now shows
  the weapon in every AimMod match regardless (in memory only; the setting is
  untouched outside matches).
- **Profiles** (`CsRules`, `CsLook`): every CS item is a hitscan profile with
  the host's damage and fire rate, a viewmodel by class, the CS2 magazine and
  reload, and a view kick (`MaxRecoilUp`/`Horiz`, auto reset). Spread stays 0
  because the host validates hits on the camera ray; a kick moves the camera,
  so it moves hits as it moves the crosshair.

  | Item | Viewmodel | Third person | Mag | Reload |
  | --- | --- | --- | --- | --- |
  | Glock-18 | Stud Gun | Pistol | 20 | 2.27 s |
  | USP-S | Spike | Pistol | 12 | 2.17 s |
  | Desert Eagle | Law Bringer | Six Shooter | 7 | 2.2 s |
  | MAC-10, MP9 | Machine Pistol | SMG | 30 | 2.6 / 2.1 s |
  | AK-47 | KovaaKs Rifle | AK47 | 30 | 2.43 s |
  | M4A1-S | Heavy Surge Rifle | M4 | 20 | 3.07 s |
  | AWP (scopes on RMB unless ADS is off) | Spider | Bolt Action Sniper | 5 | 3.67 s |
  | Knife | Blank + AimMod knife | none | - | - |
  | C4 | Blank + AimMod bomb | none | - | - |

  The combat modes' rifle shows `KovaaKs Rifle`, the railgun `Heal Rifle`.
- **Slots** (CS): 1 primary, 2 pistol, 3 knife, 4 grenades, 5 bomb (the
  carrier's only), as in CS. The arena's player profile has
  `USP-S;Glock-18;Knife;Grenade;C4` until the round's loadout fills the slots
  (`loadout` line, 9 columns: the grenade slot's profile is last). KovaaK's
  own Weapon1 to Weapon5 keys switch natively. AimModCore adds the mouse wheel and Q (the
  last weapon), and draws a bought weapon; dropping or planting the bomb from
  the hand draws the best weapon. Buying a primary replaces the old one (no
  drops yet).
- **Knife:** 40 damage every 0.4 s within 2.1 m (48 Source units at 4.4 cm),
  through the same host-validated claims (slot 2); the host refuses a stab
  beyond `KnifeRangeCm` + 60 cm (`range`). Kill reward $1,500.
- **Knife moves.** KovaaK's has no melee animation for the first-person
  arms (only gun fire, reload and scope montages), so AimModCore moves its
  own knife model: left-mouse slashes alternate right-to-left and
  left-to-right (0.25 s with a quick return); right mouse stabs forward
  (0.38 s). Slashes do 40, 25 for a follow-up within 0.6 s; the stab does
  65 within 1.4 m, once a second, claimed as slot 5 on the camera ray.
- **Knife sounds.** The knife and bomb profiles have no gunshot
  (`ShootSound=None`), and AimModCore empties the user's shot sound names for
  those two weapons in memory. The service plays the knife's own sounds for
  your attacks: a swish for a miss, a thud when the ray meets a player
  within reach (synthesised like the bomb sounds, at the "Bomb and round
  sounds" volume).
- **ADS zoom** (lobby setting `adsZoom`, CS): `off`, `cs` (default: only the
  AWP scopes, as in CS2, FOV 40) or `all` (also rifles FOV 70, SMGs 75,
  pistols 80). KovaaK's own ADS fields (`CanAimDownSight`, `ADSFOVOverride`,
  `ADSZoomSensFactor`) do the zoom; `adsSensitivity` (0.2-2, default 1.0) is
  the zoomed sensitivity ratio. The knife and bomb never zoom.
- **Bomb:** slot 5 holds AimMod's C4 model while you carry it; E or fire with
  it in hand plants. It never hits (the client drops its claims, the host
  refuses slot 4). The dropped or planted bomb is drawn in the world from the
  `bomb` line (everyone sees it), with a light that flashes with the beep.
- **Others' hands:** AimModCore's self-pose `weapon` row names the slot in
  hand; the service sends `hold {slot}` to the host on change; `CsPlayerView`
  has `Holding`; `avatar-state.tsv` carries its third-person model, which
  AimModSteam puts on the avatar's own third-person weapon mesh
  (`GetThirdPersonWeaponMeshComponent_Primary`, KovaaK's `FN_*` meshes).
  KovaaK's has no third-person knife, bomb or grenade, so with one of those in
  hand the avatar shows the gun that player carries: an alive avatar is never
  empty-handed. The service rewrites the file every second even when nothing
  changes, so freeze time never lets it go stale (AimModSteam drops a file
  older than 10 s, which used to leave every avatar unarmed about 10 s into
  each round).
- **Not yet:** the bomb on a remote carrier's back, the knife on avatars,
  weapon drops between players, per-weapon damage falloff.

#### 6.6.3 Bomb and round sounds (built)

- AimMod's own sounds, synthesised by the service (`BombSounds`: sine tones,
  noise and envelopes; nothing recorded or taken from a game);
  `--write-bomb-sounds <folder>` writes them as WAV files.
- The planted bomb beeps at fixed times before the explosion: at 40 s left,
  then every `BeepInterval` (1 s at 40 s, 0.29 s at 10 s, about 7 a second at
  the end), and a rising tone in the last second. Every machine beeps in step,
  and the bomb's light flashes with it.
- The beep comes from the bomb: volume falls with distance (full within 3 m,
  half at about 18 m, never below 6 %) and it pans with the bomb's bearing from
  where you look. The explosion and others' plant and defuse sounds are
  positional too; your own actions, the "bomb planted" alert (three rising
  notes, everyone), defused, the kit click and the Terrorists' bomb drop and
  pickup ticks are 2D.
- Played through Windows audio (winmm, 44.1 kHz stereo, mixed by AimMod), at
  the "Bomb and round sounds" volume in the multiplayer settings (default
  70 %, 0 is off). UE can't play a runtime-built sound without engine code
  AimMod doesn't call (`USoundWaveProcedural`'s queue isn't reflected), so
  KovaaK's master volume doesn't apply.
- Other players' gunfire and footsteps (all shooting modes; `GunAudio.cs`):
  KovaaK's only plays your own gun. Every shot has its own time from the
  shooter's stream: a player's service reads every shot its game fired
  (`self-shots.tsv`, hits and misses) and sends them to the host (`fired`); a
  bot's trigger pulls are the host's own decisions (`BotFire`). The host checks
  each against the shooter's weapon (alive, the round under way, never faster
  than 60 % of the fire interval) and passes them to everyone at once
  (`shots`). Each machine plays the others' shots at their own spacing (a
  per-shooter delay absorbs the network path; a shot over 600 ms late is
  dropped), from where they were fired, by weapon class (pistol, SMG, rifle,
  AWP, shotgun, knife swish), panned by bearing, falling off at half the
  bomb's rate. A bot's own line traces say which players a wall hides it from;
  they hear its shots muffled (low-passed, quieter). Other players' shots
  carry no line of sight, so they fall off by distance only.
- Footsteps come from where this machine's game draws the other players: a
  step every stride (about 3 a second at a run) while they move on the ground,
  quieter walking (30 %) or crouched (20 %), teammates at 60 %, none past
  22 m.
- Both play at the "Gunfire and footsteps" volume (default 70 %, 0 is off), in
  the same mixer as the round sounds (at most 64 voices; the oldest gunfire
  goes first).

#### 6.6.4 Bots (built)

Host-run players for every mode but the tracking duel (not tournament
lobbies). The host adds them in the lobby (**Add bot** at Easy, Normal or
Hard, or **Fill with bots**); they're named `BOT <name>`, always ready, load
at once, survive a host change (the new host runs them on) and leave if the
mode changes to the tracking duel. The shooting modes play the bot brain
below; the score modes (score race, duel, free-for-all rounds, practice)
play `BotScorer`: the host sends a bot's score frames and finish like a
player's game would, from a pace off the host's best on the scenario (else
about 1,000 a minute) times its difficulty (62 / 84 / 102 %) and its own
form, rising with streaks and slumps but never falling.

- **Drawing and moving.** Each bot is one of AimModSteam's walking stand-in
  avatars (synthetic peers 1 to 16). The host's walkers get waypoints (own
  spawns first, then bomb sites, callouts and the other side's spawns) and
  orders through `bot-orders.tsv`: roam, walk to a goal (around walls by way
  of the waypoints), hold, face a point, and stand at the round's spawn.
  The host sends their positions to everyone 10 times a second (`bots`), and
  each client's walkers follow them.
  - Ported maps carry spawns and bomb sites only (no callouts, no nav
    data), so AimModSteam builds a nav grid for the bots
    (`NavGrid.hpp`): floor points 120 cm apart, grown from every spawn,
    waypoint and goal by line traces (no step over the step height, no
    drop over 1.6 steps, nothing across at foot or waist height), about
    1500 traces a tick, so a whole map is covered in a few seconds. A bot
    walks the A* path over it, string-pulled into straight legs over the
    grid alone (no traces). Without a grid (other arenas) a goal is reached
    by a route over the waypoints, checked a few straight walks at a time.
  - On a single grid step the walker trusts the grid's floor (a trace that
    began in a low ceiling, a step read differently) but never walks through
    a wall; a string-pulled leg it can't take is walked again grid step by
    grid step. Stuck (under 40 cm in 1.5 s), it climbs a ladder of
    recoveries, starting over only once well past the spot: hop and back off
    to the last point; take the link out of the grid and plan again; take
    the grid point beyond it out; hop over to the next point.
  - It keeps clear of other bodies (bots, players, the local player): slows
    behind one ahead, steps aside from one alongside, stops beside one
    already standing on its spot. It turns no faster than its turn rate
    (`turn`, by difficulty), faces where it walks, checks the most open
    angles from where it stands (the grid's straight runs) and strafes side
    to side in a fight (`fight`).
  - Orders also say how far to go (`stop`, a fraction of the way: map
    control, post-plant spots) and a detour first (`via`, a split).
  - `aimmod_nav_probe <map.json> <map.aimmod.json>` (steam-bridge tools)
    runs the same grid and walkers offline on a ported map's brushes
    (convex hulls of each brush's vertices), and reports coverage, which
    spawns and sites connect, and how walkers and five-bot squads fare from
    each spawn to each site. Both ported CS maps (de_d2_remake, de_d2_beta):
    every spawn and site connected, every walk and squad arrives.
  - In game, once the grid is complete, AimModSteam logs its coverage: how
    many spawn-to-waypoint pairs connect, and the ones that don't.
  - Bot debug (developer menu): AimModSteam draws each bot's path as small
    spheres and its goal as a cube, and logs every bot's job every second.
  - Logs: the grid's growth, and every 5 s each bot's order, goal,
    distance, path length and progress; the service adds the orders to
    its 30 s bot line.
  - CT bots split over the sites by their place among their own side's
    bots; near its spot a bot faces the enemy spawn's way.
  - Walkers move like the local player: run speed and step height from its
    `CharacterMovement` (a ported map is scaled up, CS runs at 1100 cm/s
    with 79 cm steps).
  - Every tick the avatar ends exactly where its walker is. The game's drive
    sweeps, so a wall or a jump across the map (a round's spawn) used to
    leave it short: bots stood floating in the local spawn while the logic
    had them elsewhere. Anything off by more than 2 cm is placed directly.
  - Feet on the floor: the avatar's own capsule half-height above the
    walker's floor (the mesh bounds sit below the soles and lifted it).
  - `avatar-state.tsv` names bots by their stand-in peer; AimModSteam
    accepts peers 1 to 16 there (their rows used to void the whole file, so
    no avatar showed deaths, teams or weapons).
- **Scoreboard (CS).** Board kind `cs`: each side (yours first) with its
  score and players alive, then its players and bots with money (your side
  only), kills, deaths, K/D and DEAD; the clock is the round's phase clock,
  not the match time limit (a three-hour cap).
- **Looks.** Each bot gets one of the humanoid avatars (Meso skins, Meso,
  Endo, Ecto) from its id, skipping looks other bots already wear while one
  is free, so it's stable for the match and the lobby isn't one bot copied.
- **Spectating while dead (CS).** Down until the round ends, the camera
  follows a living teammate (anyone alive when no teammate is), bots
  included. Click or Space watches the next one, right click the previous;
  the HUD says who. The service writes `spectate-view.tsv`
  (`AIMMOD_VIEW_1\t<unix ms>`, then `view\t<peer>`, rewritten every second);
  AimModSteam puts a chase camera behind the avatar it draws for that peer
  and gives the view back when the file goes or is older than 3 s. No pose
  stream from another machine is involved.
- **Sight.** The host asks its game to trace from each bot's eye to the
  nearest enemies; AimModSteam answers in `bot-sight.tsv`. A bot only targets
  a player its trace says is in sight.
- **Aim** (`BotBrain` and `BotAim`, by difficulty): the crosshair turns onto
  a target at 260 / 450 / 720 degrees a second, settles on it for
  220 / 130 / 60 ms after a reaction time (650 / 380 / 220 ms), then fires
  in bursts. A shot lands by a hit chance (28 / 45 / 62 % at close range on
  a still target, less with distance, a moving target and deeper into a
  spray) times how close the crosshair is to the body. A landed shot goes
  through `CombatMatch.BotHit`: alive, fire rate, round phase, spawn
  protection, friendly fire and armour, like a player's hit. It strafes in
  a fight (25 / 60 / 100 %).
- **Eyes and ears** (`TeamKnowledge`): what any bot of a side sees, and
  running footsteps (22 m) and gunfire (45 m) it hears, the whole side knows
  for 6 s. A bot watches the nearest such spot, hunts it when it has nothing
  else to do, and rotations follow it.
- **CS** (`BotStrategy`, `BotEconomy`). The side buys together: pistol
  rounds, a full buy (rifles, helmets, kits) from $3,700 a head, a half buy
  (SMGs) from $2,000, a force on the last round of a half or at match point,
  else an eco. Terrorists plan the round (the same on every machine): a
  rush, a default (map control part of the way to both sites, then at a
  call 20 to 35 s in, the site together) or a split (half by way of the
  other site's approach); ecos rush. The carrier plants, the nearest
  Terrorist fetches a dropped bomb, and after the plant they hold around it
  facing the way the defence comes back. Counter-Terrorists anchor both
  sites with one playing forward; a sighting or sound near a site pulls all
  but one anchor over; with the bomb down they gather short of it (two of
  them, or 6 s), then retake: the nearest defuses, the others cover (Hard
  bots keep defusing through a fight near the end). Low on health with
  nothing in sight, a bot falls back to its nearest teammate. A bot that
  loses sight of an enemy goes where it last saw them.
- **Tracks.** A bot's track (hits, a dropped bomb) uses a player's
  convention: the floor its game reports under it plus a standing player's
  camera height (this machine's own, once seen). A carrier with no track
  drops the bomb where it was last seen, else at its spawn.
- **Online.** The host runs every bot (brain and walker); its game's bot
  positions go to everyone 10 times a second and every client draws them
  there (`pose` orders, no logic of their own). A client's hit on a bot is
  a claim to the host like any other. A new host keeps the bots and runs
  them on.
- **Never counted.** Bots post no runs and never reach the Hub or KovaaK's
  leaderboards; recent matches mark them as bots, the scoreboard tags them
  BOT.

#### 6.6.5 Grenades (built)

HE, flashbang, smoke, decoy, molotov (T) and incendiary (CT), as in CS. The
host owns all of it (`CsGrenades.cs`): what each player carries, every
throw, the path, when and where it goes off, and what it does. Everything is
AimMod's own: procedural models of engine basic shapes and synthesised sounds.

| Grenade | Price | Side | Carry | What it does |
| --- | --- | --- | --- | --- |
| HE Grenade | $300 | both | 1 | Goes off 1.5 s after the throw: 98 damage at the blast, a bell falloff to nothing at 15.4 m (350 units); half reaches health through armour; no damage through walls |
| Flashbang | $200 | both | 2 | Pops at 1.5 s: blinds everyone (teammates and the thrower too) by how directly they look at it and how far it is, if nothing blocks the line |
| Smoke Grenade | $300 | both | 1 | Pops once it lies still (at least 1 s in): a cloud 12.4 m across and 6 m tall for 18 s (grows in over 1.5 s, thins over the last 2 s) |
| Molotov | $400 | T | 1 | Catches where it first lands on a floor within 2 s (else bursts in the air): a 2.5 m fire for 7 s, 40 damage a second in 0.25 s ticks, armour doesn't help |
| Incendiary Grenade | $600 | CT | 1 | The same fire |
| Decoy Grenade | $50 | both | 1 | Once it lies still: 15 s of fake gunfire that sounds like its owner's weapon, then a small pop |

- **Carrying.** Four grenades in all, one of each but two flashbangs, and
  one fire grenade (`GrenadeRules.CarryProblem`). They stay through rounds
  you survive and are lost when you die or at halftime. Buying is the usual
  `buy {item}` (the buy menu has a Grenades column; refused: `owned`,
  `carry-limit`, `side`, `money`).
- **In the hand.** Slot 4 (key 4) holds one profile, `AimMod CS Grenade`
  (Blank viewmodel, no shot sound, never hits: the client drops its claims,
  the host refuses slot 3). Which grenade is in hand is the service's: key 4
  again with a grenade in hand picks the next one you carry (HE, flash, smoke,
  fire, decoy). The service reads the buttons like the other CS keys: fire or
  right mouse pulls the pin; letting go throws: fire alone a full throw, right
  mouse alone underhand (30 %), both a medium throw (65 %). The client sends
  `throw {kind, strength, o: camera, r: [pitch, yaw]}`; the host checks the
  round is on (not in freeze time), you carry it and the throw starts within
  1.5 m of your own camera track. The throw is CS's: 675 units/s at full
  strength, aimed 10 degrees up at level. The last grenade thrown empties the
  slot and AimModCore draws your best weapon.
- **Flight** (`GrenadePhysics`, the same steps in AimModSteam's
  `GrenadePhysics.hpp`): gravity 0.4 of CS's 800 units/s², 1/64 s steps, a
  bounce off whatever a line trace hits keeps 45 % of the speed, a floor
  landing slower than 120 cm/s slides to a stop (700 cm/s²), at most 8 s and
  24 keys. Between keys the path is exact (a parabola, or an even slow-down),
  so a key list draws the same everywhere.
  - The host asks its own game for the path (`grenade-sim.tsv`: `throw` rows);
    AimModSteam flies it with the map's line traces (world static and dynamic
    objects, no pawns) and answers in `grenade-paths.tsv` (`path` rows, 9
    numbers a key). No answer in 350 ms: the host flies it over a level floor
    under the thrower. Both implementations are checked against the same
    numbers.
  - The host broadcasts the path with the grenade (`CsView.Grenades`, state
    `flying`, `Keys`, `Ends`), and when it went off: every peer draws the same
    bounces, and the pop where the host says.
- **Line of sight.** An HE or flash blast asks the game (`los` rows) whether
  the line from the blast to each player in reach (eye, and the body for HE)
  is clear; the answers come back in `grenade-paths.tsv`, and a blast waits
  300 ms for them (then counts the line as clear). A smoke between the flash
  and you also keeps you from being blinded.
- **Flashes.** The amount (0..1): full when you look within about 53 degrees
  of the pop, less to the side, 0.1 behind you; full within 400 units
  (17.6 m), nothing at 1500 (66 m). The screen stays white for
  2.5 s × amount², then clears over 2.8 s × amount (`CsPlayerView.Flash`):
  about 5 s in all for a flash in your face, 2-3 s from the side or further
  off. The white reaches your screen twice: AimModCore's flash layer (above
  every view, from the `flash` line of `grenades.tsv`, with the frozen
  after-image of the moment of the flash under it for a strong one) and the
  notice page's top layer (`#cs-flash`, played from `cs.flashFx`). A hard
  flash also rings in your ears. A flashed bot sees nothing. The host logs
  what each flash did (`[grenades] flash #…`: who it blinded, by how much,
  and whether the game answered the sight lines) and each player's service
  logs `[grenades] you were flashed`.
- **Smoke.** It blocks sight for bots (the service drops their sight of
  anyone behind it), enemy name tags (no `world-tags.tsv` row for an enemy
  behind it from your camera) and flashes; shots go through it, as in CS. It
  spreads to its full size (about 12 m across, a chokepoint or a doorway and
  its sides) over 1 s. Inside it AimModCore flattens the picture to grey (a
  post-process blended in by how deep the camera is) and the HUD's veil greys
  the view under the HUD.
  A smoke puts out a fire it covers, and a fire grenade landing in a smoke
  never catches (an `extinguished` blast with a hiss).
- **Damage** goes through the host's `CombatMatch.AreaHit`: the CS armour
  model, friendly fire at the lobby's team share, your own grenade can hurt
  and kill you (that takes a kill away), and the kill feed names the grenade
  ($300 kill reward).
- **AimModCore** draws from `grenades.tsv` (service, rewritten on change and
  every second; `CsGrenades.hpp` has the contract): the grenade in hand on the
  first-person camera (drawn back with the pin pulled, gone for a moment after
  a throw), grenades in flight spinning along the host's path, smoke clouds of
  40 overlapping grey puffs (engine spheres, squashed, lighter on top) that
  come out of the canister, spread over a second, drift and billow slowly and
  thin out from the edge (one actor and 40 mesh components per smoke, posed
  at 30 Hz), the smoke canister and decoys lying
  where they stopped, a burning pool with flickering flames, and blasts (an HE
  fireball, the flash's white pop, a fire grenade bursting or put out).
- **Sounds** (`GrenadeSounds`, in `--write-bomb-sounds`): the pin, the throw,
  bounces as the path reaches them, the HE blast, the flash's pop and ringing,
  the smoke's hiss, fire catching and crackling, a fire put out, and the
  decoy's shots by weapon class (pistol, SMG, rifle, sniper, heavy), all
  positional at the "Bomb and round sounds" volume.
- **HUD.** The grenades you carry under your guns, the one in hand marked
  with key 4; the flash's white and the smoke's grey veil; the kill feed's
  grenade names. Teammates' name tags show their grenades as small chips
  (gear `g=he,flash,flash,smoke`).
- **Bots** (`BotGrenades`, after `BotBrain` each tick). Buys in freeze time
  with the money left after guns and armour: Easy a flash; Normal a smoke, a
  flash and an HE; Hard a smoke, a flash, a fire grenade, an HE and a second
  flash. Throws, once per kind a round and one every 2 s: Terrorists heading
  for a site smoke it off towards the defenders' spawn, then flash over it; a
  Terrorist burns a defuse; Counter-Terrorists smoke the planted bomb and burn
  a plant; anyone throws the HE at an enemy they see 7-22 m away. Hooks for
  the bot logic: `IBotGrenadePolicy` (replace the policy:
  `MultiplayerService.BotGrenadePolicy`), `BotGrenades.Buys` and
  `BotGrenades.Plan` (this policy's parts), `GrenadeAim.Lob` and
  `GrenadeAim.Timed` (the velocity that lands a grenade on a point, or has it
  there when it goes off), and `LobbyCore.BotThrow` (any carried grenade at
  any velocity up to a full throw).
- **Checks:** prices, sides and carry limits, the cycle order, buying,
  throws (freeze time, the grenade, the origin), HE falloff, armour and line
  of sight, flash amounts and timings, teammates and the thrower flashed,
  smoke blocking lines, bot sight and flashes, fire damage over time, fire
  put out by smoke and never catching in one, kills and the kill feed, the
  deterministic path (the same twice, bounces off a wall, exact between keys,
  the level-floor numbers both implementations share), the files, sounds,
  name-tag chips, the HUD, and the bots' buys, aim and throws.
- **Not yet:** grenades dropped on death for others to pick up, grenade
  models in others' hands (avatars), the radar, smoke that bends around walls
  (it is an ellipsoid), and fire that spreads over the floor's shape.

### 6.7 Capture the flag

- Two teams. Each base has a flag stand (map metadata `flag` with team). The
  flag is an AimMod-spawned marker that follows its carrier's avatar: each
  frame AimModCore sets its pose to the carrier's back. For the local carrier
  it attaches above the camera's view.
- **Pickup:** an enemy within 64 cm of a home or dropped flag. **Return:** a
  teammate touching their dropped flag, or 30 s auto-return. **Capture:**
  carrier within 96 cm of their own home flag stand while it is home.
  +1 capture, flag returns.
- Settings: capture limit 3, time 15 min, respawn 3 s, weapons (instagib CTF
  or loadout), grapple on or off.
- Each client draws the flags from `objective` state. The host is the only one
  deciding pickups and captures.

### 6.8 Weapon drops and pickups (arena)

- **Drops:** on death, the host creates a `pickup` of type `weapon(profile)` at
  the victim's last position (+ a small upward offset), with a 20 s lifetime.
  The victim's slot is cleared. Touch radius 48 cm. In CS, pickup goes
  through the use key; in arena modes, by touch.
- **Map pickups** (arena modes): types `weapon(profile)`, `health(5, 25, 50,
  mega 100 with decay)`, `armour(5, 50, 100, 150)`, `ammo(weapon)`,
  `powerup(quad: ×3 damage in the host DamageModel; haste: movement speed via
  `MetaSpeedMultiplier` [probe]; regen)`. Respawn times are Quake-like:
  weapons 5 s, health 35 s, mega 60–120 s, armour 25 s, powerups 120 s.
- The host owns pickup state. Clients draw markers (`world.markers`): a mesh
  per type, spinning, hidden while taken, with a respawn countdown.
- Positions come from map metadata (`pickup` entries), converted by map-port
  from Reflex `.map` pickups or Source `item_*` / `weapon_*` entities
  (section 8.2).

### 6.9 Grapple

- **Recommended:** the AimMod pull grapple (section 3.5, option 2).
  - Bound to an ability slot holding a no-op ability profile, so the game's
    own key binding fires it.
  - Range 2500 cm, pull speed 1800 cm/s, a pull that's constant along the
    rope plus gravity, release on key up.
  - Only world geometry can be an anchor. Avatars can't, because they aren't
    collision targets for the trace channel used.
  - The rope is drawn locally. Remote players see the rope from the pose
    flag `grapple`, with the anchor point sent in a reliable `use` event.
- **Fallback:** native "grapple dash" movement ability (option 1). It works in
  any mode without AimMod code and is good enough for movement practice.
- The host validates speed against `maxSpeed + grapple.pullSpeed`.

### 6.10 Other arena modes worth having

All are compositions of the pieces above: team deathmatch (DM + teams +
team-damage policy), elimination rounds (CS rounds without economy or bomb,
fixed loadouts), gun game (a weapon ladder; `grant` next weapon per kill),
"one in the chamber" (1 ammo, `SetCurrentAmmo(1)` per kill, instagib pistol),
and King of the hill (a zone from metadata, score per second while alone in
it).

## 7. Feasibility matrix

Legend: **N** native profile or engine feature, **A** AimMod logic using
reflected functions, **P** needs the probe or a test build to confirm, **B**
blocked.

| Mode | Avatars | Damage and health | Rules | Map needs | Verdict | Key evidence |
| --- | --- | --- | --- | --- | --- | --- |
| Score race | not needed | not needed | A (exists) | none | **ready** | lobby service; AimModCore score journal |
| Practice together | A: **built** in AimModSteam (spawn, AI off, invulnerable, pose, look); animation P | none (invincible) | A (lobby mode exists) | none | **feasible, avatars built; live check pending** | `ATheMetaAIController::Spawn`, `UpdateClientLocAndRot`/`K2_SetActorLocationAndRotation`, `LoadCharacterProfile` |
| Tracking duel | A (bridge avatars) | none | A: **built**, host TOT with 200 ms rewind cap (6.3.1) | arena generated (helper bot, no targets) | **in progress: service done; bridge items 6.3.1/1–3 needed** | `self-pose.tsv` camera + target capsules; `TrackingRound` checks |
| Deathmatch / 1v1 | A | A via N: `HandleDamage`, native death and respawn (P); stock players are invulnerable (9.1), so the generated scenario sets `InvinciblePlayer=false` | A: claims (polled counters), lag comp | spawn points (N, `SpawnableSpawnPoint_C` with TeamMask live) | **feasible** | `HandleDamage`, `Death`, `Respawn`, `GetSpawnPointsForTeam` |
| Vampiric 1v1 | A | A: host lifesteal + `HandleHeal`/`SetHealth`; N field exists but unused | A | spawns | **feasible** | `HandleLifesteal`, `HandleHeal`, `LifeStealPercent` |
| Instagib | A | N weapon profile + A authority | A | arena map | **feasible** | `FWeaponProfileNative.DamagePerShot`; stock Quake-style profiles (`Railgun`, `Rocket Launcher`, `LG`) seen live on bots (9.1) |
| CS competitive | A | A + A armour | A: rounds, economy, bomb | **A metadata** (buy zones, sites) via map-port; N spawns with TeamMask | **feasible, largest** | `SetWeaponProfileByString(slot)`, 8 weapon slots (live: always 8 allocated), `SetIgnoreMoveInput`, `IsInputKeyDown`, `SetTeam`; the CS walk key is a native Sprint ability (live); freeze attack block (P) |
| CTF | A | A | A: flags | A metadata (flag stands) | **feasible** | `StaticMeshActor` markers (live), position checks |
| Weapon drops | A | n/a | A | none | **feasible** | `SetWeaponProfileByString`, markers |
| Pickups (Reflex-style) | n/a | A grants | A | A metadata; Reflex `Pickup` not loaded natively (**B natively**) | **feasible as AimMod** | `EMapCreatorLegacyMapEntityType` has no pickup |
| Grapple | n/a | n/a | A: pull via `LaunchCharacter` | none | **feasible as AimMod**; no native grapple (**B natively**) | `LaunchCharacter`, `ACableActor`, `FMovementAbilityNative` |
| Jump pads, teleporters | n/a | n/a | N | map JSON objects | **native** | `AMapCreatorJumpPad`, `AMapCreatorTeleporter` |
| Native Duels | — | — | — | — | **not used** | `UDuels` is a stub over UE replication |

Nothing is blocked outright. The two native gaps (pickups and grapple) are
filled by AimMod logic. The real risks are in the P items: avatar animation
when the pose is set externally (the offline spike in 9.3 checks it), native
death and respawn after injected damage, and a clean attack block for freeze
time. Hit registration polls weapon counters rather than hooking shot
functions (9.1).

## 8. Changes needed elsewhere

### 8.1 Multiplayer UI and lobby service (`feat/kovaaks-multiplayer-ui`)

- `LobbyModes`: add `tracking-duel`, `dm`, `tdm`, `vampiric`, `instagib`,
  `cs`, `ctf`, `arena` (keep `score-race`, `duel`, `ffa-rounds`, `practice`).
  `duel` today is a 1v1 score race; keep it under that name or rename it to
  `race-1v1` before release so it isn't confused with tracking duel.
- **Mode settings:** a per-mode schema (`IGameMode.Settings`) instead of
  growing `LobbySettings`. Add a `ModeSettings` JSON object validated by the
  mode, and include it in `PlayKey` and `MatchScenario.Key`. Settings include
  timers, frag and capture limits, economy preset, friendly fire, lifesteal %,
  respawn delay, weapon set, pickups and grapple toggles, tracking variant,
  "fire to score".
- **Teams:** team assignment in the lobby (two columns, drag or auto-balance,
  lock teams), side swap at half, team size limits; `MaxPlayerLimit` 8 → 10
  for 5v5.
- **Map picker:** filter by metadata capabilities (`spawns.team`, `bombsites`,
  `buyzones`, `flags`, `pickups`) and show why a map is unavailable for a
  mode.
- **In-match HUD (Gameface):** round timer and phase, team scores, money,
  kill feed, health and armour (AimMod health replaces the native bar where
  armour applies), objective indicators (bomb, flags, zones), Tab scoreboard,
  tracking-duel TOT bar, respawn countdown, spectator view label.
- **Buy menu panel** with cursor handling (`K2_SetShowMouseCursor`, Gameface
  focus) and keybind settings for buy, use and grapple, with a conflict check
  against KovaaK's binds.
- **Protocol:** add the message types in section 4.4; add the binary fast
  channel to `IMultiplayerTransport` (`SendFast(peer, bytes)`) for the
  AimModNet bridge and the Hub relay.
- **MatchScenario generator:** emit the avatar bot and character profiles,
  the shop and mode weapon profiles, the PvP character profile
  (`InvinciblePlayer=false`, lifesteal fields 0, respawn delays per mode,
  `BlockTeamDamage` by friendly-fire setting), and `PlayerTeam`/`BotTeams`.
  Bump `GeneratorVersion`.

### 8.2 Map-port (`feat/kovaaks-map-port`)

- **Keep objective entities instead of dropping them.** Write
  `<map>.aimmod.json` next to the map JSON (and embed it in the scenario
  description or a separate `[AimMod]`-free file, because KovaaK's would
  reject unknown sections):
  ```json
  {"format": 1, "mapScale": 4.0, "units": "kovaaks-world",
   "spawns": [{"team": 1, "pos": [x,y,z], "yaw": 90}],
   "zones": [{"type": "buyzone", "team": 1, "box": {"center": [..], "half": [..], "yaw": 0}},
             {"type": "bombsite", "name": "A", "hull": [[x,y,z], ...]}],
   "flags": [{"team": 1, "pos": [..]}],
   "pickups": [{"type": "health", "amount": 50, "pos": [..], "respawn": 35}],
   "bounds": {"min": [..], "max": [..]}}
  ```
  Coordinates are in final KovaaK's world units (after the axis flip and
  MapScale), so no consumer redoes the transform. Its hash goes into the lobby
  map choice.
- Source entities to keep: `func_buyzone` (brush hull → zone, `TeamNum`),
  `func_bomb_target` and `info_bomb_target` (site name from `targetname`, or
  by position, "A" nearer to the T spawn's right), `info_player_*` (already
  kept), and HL2DM/GMod `item_*` and `weapon_*` for arena pickups. CTF flags
  usually aren't in Source maps; allow a `--flags x,y,z:team` option and
  editor-placed text labels.
- Reflex input: map-port only *writes* Reflex today. Reading Reflex `.map` for
  arena maps (brushes are already a known format; `Pickup` entities with
  `pickupType`) is new work. The `pickupType` table must come from Reflex's
  own definitions and sample maps.
- `--avatars N` for match scenarios: avatar bot profile (`NoAiming`,
  `NoDodging`, `UseWeapons=false`, invincible, Meso with a skin per team) in
  place of the strafing target bots. The lobby generator can also do this,
  so map-port only needs to leave out target bots on request.
- The report lists dropped and kept objective entities.

### 8.4 Hub results (waiting for the tournaments API)

Hub verification waits for the Hub tournament API (`feat/kovaaks-tournaments`
in this repository, `feat/tournaments` in aimmod-hub). At the time of writing,
neither branch had tournament or result code. So this is a proposal to align
with, not a format the Hub accepts yet. Each finished match would produce
one record, built from the host's state and the clients' replay hashes:

```json
{"format": 1, "match": "m-…", "mode": "deathmatch", "settingsKey": "<MatchScenario.Key>", "scenarioHash": "…",
 "startedAt": 0, "endedAt": 0, "host": "<player key>", "players": [
  {"key": "<hashed member id>", "team": 0, "place": 1, "score": 20, "frags": 20, "deaths": 7,
   "claims": 130, "rejected": 2, "trackPercent": null, "disputed": false, "replay": "<replay hash>"}],
 "winner": "<player key>|null", "winnerTeam": null, "rounds": [ … per-round placements … ]}
```

Players are identified by the same hashed key as the local history
(`PlayerKey`). The Hub re-runs the hit validation from the uploaded replays
and pose streams with the same rules (`CombatMatch`, `TrackingRound`) before
it counts a result. The field names and the transport will follow the
tournaments agent's API once it exists.

### 8.3 AimModCore (`in-game/native-mod`)

- The new `Play` module (section 4.3), the shared-memory Play channel,
  disarm and undo, and compatibility-summary lines (`play: avatars=ok
  damage=ok freeze=attack-block-missing …`).
- Presenter generalisation: one pre-tick pose applier for replay proxies and
  match avatars, with a separate interpolation buffer per source.
- Polling of weapon counters and `CharactersHit` at frame rate while armed
  (cheap property reads).

## 9. Live probe

`in-game/probes/modes-probe/AimModModesProbe` is a UE4SS Lua mod that
answers the [probe] items. It is read-only:

- It calls only reflected getters and reads properties.
- Its hooks only count calls; they don't read, change or return arguments.
- It writes only log lines, capped at 6000.
- It prints no player names or Steam IDs.

What it logs:

1. **Inventory:** whether every class and function in section 4.3 exists at
   runtime (`inventory: N present, M missing` and one line per missing item).
2. **Reflection reachability:** counter-only hooks on about 40 shot, damage,
   death, respawn, weapon and ability functions. `hook counts:` lines show
   which ones the game calls through reflection. A count that stays 0 while
   the event clearly happened means it's a native call, and the framework must
   poll instead.
3. **World snapshot** once per loaded map: every character (player or bot,
   class, character and bot profile, team, enemy flag, health and max, lives,
   model and skin, mesh hit detection, invulnerability, team damage blocking,
   lifesteal, health on kill, regen, respawn delays), its weapons (profile,
   ammo, fired, hit, damage) and abilities (name, type, charges); map object
   counts (spawn points with team masks, jump pads, teleporters, hurt/kill
   volumes, waypoints, text labels, water, projectiles, trigger boxes); the
   prop and game-object data-table row names (candidate markers); the loaded
   character models.
4. **Deltas:** every change of health, damage taken or done, kills, deaths,
   lives, team or hidden state per character, every 0.5 s.

### 9.1 First live run (KovaaK's 3.9.11, during normal play)

The probe ran through two scenario loads: a stock scenario on `kovaim1.map`,
then a Quake-style freeplay scenario on `boxerhalflimited.map` (map scale
3.8). That gave 54 log lines. The full test script (shooting, taking damage,
dying) hasn't been run yet.

**What the log shows:**

- **Inventory: 97 of 98 present.** Every class and function the framework
  plans to call exists at runtime. The one miss,
  `/Game/.../FPSCharacter.FPSCharacter_C`, was looked up on the main menu,
  before any scenario had loaded the blueprint. It's in the dump of a running
  scenario. The probe now checks it once per world.
- **Hooks: 43 of 43 bound.** Only `MetaCharacter:SetHealth` fired (3 calls,
  around the scenario load). Every other count stayed at 0. In this window,
  though, nobody fired a shot (`fired=0` on every weapon), took damage or died
  (no delta lines). So the zero counts for shot, damage, death and kill hooks
  **are expected either way and don't by themselves prove native calls**.
  - Together with AimModCore's earlier finding that shot and score functions
    are called natively on 3.9.11, the design keeps polling: weapon counters,
    `CharactersHit`, character health and damage fields, and AimMod's own
    ray.
  - Probe steps 3–6 still have to settle `Send_ShotHit`, `DamageEnemy`,
    `HandleDamage` and `Death`. Even if they fire, polling stays the primary
    source.
  - `SetHealth` reaching a hook means at least some character setters go
    through reflection. A health override from AimMod can therefore be
    observed, and is a real UFunction call.
- **Characters are one class [live].** The player and every bot are
  `FPSCharacter_C`. Bots carry `botProfile` (their `TheMetaAIController`).
  Meso/McCree bots report `meshHits=true`; model-`None` bots use bounding
  boxes.
- **Bots survive scenario changes.** The same bot object was reused across
  the two scenarios: same name; health 0 → 100; invulnerable false → true;
  re-profiled in place. The world (KovaaK's sandbox) didn't change, only the
  map. So anything AimMod sets on a bot must be re-applied on every scenario
  change, not just on world change (see 6.3.1/3).
- **Teams.**
  - On `kovaim1` the player is team 0 and marked enemy, and so is the bot.
  - In the Quake scenario the player is team 1 (`enemy=false`) and the bots
    are team 0 (`enemy=true`).
  - So team 0 behaves as "no team" (everyone hostile), suitable for FFA, and
    `bOnEnemyTeam` is the per-character "hostile to the local player" flag
    the colours use.
- **Players are invulnerable in stock scenarios** (`invuln=true` in both).
  Damage modes must set `InvinciblePlayer=false` in their generated scenarios,
  as planned.
- **Weapons.**
  - `NativeWeapons` always has 8 entries [live], whether a slot is configured
    or not, and `GetWeaponCount` counts the configured ones.
  - Stock profiles already carry multi-weapon loadouts: a bot with `Railgun`,
    `Rocket Launcher` and `LG`, and a player profile with `LG` in slot 3.
    These are useful bases for instagib and arena modes.
- **Abilities.** 4 ability slots are always allocated. The CS player profile's
  walk key is a native Sprint ability (`CS Walk`, type 3).
- **Map objects.**
  - Spawn points are the blueprint `SpawnableSpawnPoint_C` (an
    `AMapCreatorSpawnPoint`), with `TeamMask` 1 and 2 on the Quake map
    [live].
  - Waypoints are present.
  - One `TriggerBox`: the map repository's fall-out respawner.
  - No jump pads or teleporters on these maps.
- **Data tables.** The prop table has 154 rows and the game-object table 8.
  The names printed as `RemoteUnrealParam` because the probe didn't unwrap the
  out-parameter. That's fixed, so the next run lists them.
- **Character models.** All 11 skeletal models are loaded once a scenario
  with skeletal bots is up. None were loaded in the first scenario's snapshot.

### Deploy (manual, game closed; don't use the live install for anything else)

1. Copy `in-game/probes/modes-probe/AimModModesProbe` to
   `<game>\FPSAimTrainer\Binaries\Win64\ue4ss\Mods\AimModModesProbe` (so the
   script ends up in `Mods\AimModModesProbe\Scripts\main.lua`).
2. Append `AimModModesProbe : 1` to `ue4ss\Mods\mods.txt`. If
   `ue4ss\Mods\mods.json` exists, also add
   `{"mod_name": "AimModModesProbe", "mod_enabled": true}` to its `mods`
   array.
3. Start KovaaK's and do the test script below.
4. Collect the `[AimModModesProbe]` lines from `ue4ss\UE4SS.log`.
5. Remove the folder and both list entries.

No build step is needed.

### Test script (about 10 minutes)

1. Wait on the main menu for 20 s (inventory and hook binding).
2. Load a scenario with bots in **freeplay** (any map-port CS scenario works:
   Meso/McCree bots, mesh hits). Wait 5 s for the world snapshot.
3. Shoot a bot until it dies, twice. This checks hit and kill hooks, and the
   bot's health, damage taken and death deltas.
4. Switch weapons with the number keys if the scenario has more than one
   (`ChangeWeapon` reachability); use an ability if it has one.
5. Load a freeplay scenario whose bots shoot back, with `InvinciblePlayer`
   false (make one in the scenario editor: bot profile `UseWeapons=true`).
   Take damage, die and respawn. This checks `HandleDamage`/`TakeDamageFunc`/
   `Death`/`Respawn` reachability and the player's health and lives deltas.
6. Load a tracking scenario with invincible bots and track for 10 s. This
   checks that weapon damage and hit counters advance on invincible bots.
7. Open a map-creator map with jump pads or teleporters if available (map
   object counts).

### 9.2 Questions: answered and open

| Question | Status |
| --- | --- |
| Do the framework's classes and functions exist at runtime? | **Answered:** 97/98; the miss is a not-yet-loaded blueprint (9.1) |
| Are bots and the player the same class, with mesh hits on Meso? | **Answered:** yes (9.1) |
| Do shot and damage functions fire as hooks? | **Open but moot:** no shots in the first run. The design polls regardless. Steps 3 and 5 settle it |
| Do weapon counters and `CharactersHit` advance on invincible bots? | **Open:** step 6. The tracking duel doesn't depend on it (ray scoring) |
| Weapon slots | **Answered:** 8 always allocated; stock multi-weapon loadouts exist |
| Team semantics | **Answered:** team 0 = no team (all hostile); 1/2 teams; `bOnEnemyTeam` drives colours |
| Spawn points with team masks at runtime | **Answered:** `SpawnableSpawnPoint_C`, masks 1 and 2 |
| Do bots persist across scenario changes? | **Answered:** yes, re-profiled in place (new requirement 6.3.1/3) |
| Can an AI-off bot be driven and spawned at runtime? | **Built** in AimModSteam (`Spawn`, inert, pose stream). Animation and look are a live check: the offline spike (9.3) |
| Does `HandleDamage` on the player kill and natively respawn it? | **Open:** step 5 and a phase 2 spike |
| Clean attack block for freeze time? | **Open:** phase 3 spike (`bAbilityBlockingAttack`, `StunMe`, `bWantsFire`) |
| `SetWeaponProfileByString` for slots 1–7 and `ChangeWeapon` | **Open:** phase 2 spike |
| Is `MetaSpeedMultiplier` honoured (haste)? | **Open:** phase 4 |

### 9.3 Offline avatar spike (built on `avatar_test=1`)

This needs no second player and no network.

1. Record a freeplay run on any map with AimMod: it's a normal replay.
2. Run `AimMod.InGame.exe --export-avatar-path <replay id>` (optional:
   `--output <AimMod folder>`). It writes `avatar-test-path.tsv`, the run's
   camera at 30 Hz with its scenario, map and scale, next to AimMod's other
   runtime files.
3. Install AimModSteam's `config.ghost-demo.txt` with `avatar_test=1` (the
   avatar test runs only in ghost-demo mode), and load **the same scenario**
   in freeplay.
4. The test avatar now follows the recorded path, looping. It's a real
   `ATheMetaAIController::Spawn` bot with AI off, invulnerable, driven every
   frame with `UpdateClientLocAndRot` and velocity (or
   `avatar_drive=teleport`).
   - The camera path is lowered by the local player's own eye height,
     measured live, so the bot walks where the recorded player walked.
   - On any other scenario it circles, as before, and the log says why.
5. Check:
   - the walk, run, jump and crouch animations;
   - that the body faces its direction of travel;
   - that bullets register on the Meso mesh, with hit markers and sounds;
   - the `avatars:` lines in `UE4SS.log`.

   For the tracking duel, also check that AimModCore's `self-pose.tsv` lists
   the avatar in its `target` rows.

The path parser and sampler (`steam-bridge/src/AvatarPath.*`) are unit
tested: 131 core checks pass. The mod-side change compiles only against the
private RE-UE4SS checkout, which wasn't available here, so it still needs a
build.

## 10. Phased plan

| Phase | Scope | Depends on | Outcome |
| --- | --- | --- | --- |
| 0 | Run the probe; offline avatar spike (replay path → bot); Play module skeleton with arming, undo and the shared-memory channel | AimModCore, dumps | **Probe run once (9.1). Spike built (9.3), live run pending.** Play module not started |
| 1 | Score race (exists) and practice together; pose channel (binary fast frames over AimModNet); interpolation; tracking duel (alternating, then simultaneous) with host TOT recompute | phase 0, Steam bridge stage 3 | **Pose channel, interpolation and avatars done in AimModSteam. Tracking duel (alternating) done in the service (6.3.1)**; needs the bridge items 6.3.1/1–3 and a two-player test |
| 2 | Deathmatch FFA and 1v1, vampiric 1v1, instagib: hit claims, lag compensation, `damage`/`death`/`respawn`, spawn selection, lifesteal rule, instagib profile; post-match replay verification on the Hub | phase 1 | **Service side built (6.2.1), plus team deathmatch, spawn selection, pushed events and the combat HUD.** Needs AimModCore's `self-shots.tsv` and `play-state.tsv` (with `spawn`), AimModSteam's `avatar-state.tsv` reader, and a two-player test. Hub verification waits for the tournaments API (8.4) |
| 3 | CS rounds: teams, `RoundController`, freeze and buy time, economy, shop and buy menu, armour model, bomb plant and defuse, map-port objective metadata, halftime, 5v5 lobby | phase 2, map-port metadata | CS competitive |
| 4 | CTF (flags, carrier markers, captures), weapon drops, arena pickups (metadata from Source items and later Reflex import), AimMod grapple, gun game and the other compositions | phase 3 | Quake/Xonotic-style arena pack |

Each phase ships behind the existing multiplayer feature flag, off by default.
Each one adds self-test checks in the service, like the lobby's 134: rules
engine unit tests per mode (economy table, round transitions, lifesteal
clamps, plant and defuse timers, TOT scoring) and protocol fuzzing of the new
message types.

## 11. Decisions for the user

1. **Avatars as native bots** (recommended), keeping inert proxies only as the
   fallback and for ghosts. Final once the phase 0 spike confirms pose and
   animation.
2. **Hit authority:** favour-the-shooter claims with host rewind validation,
   capped at 200 ms (recommended), or host-only ray tests (worse feel at
   higher ping).
3. **Tracking duel (decided):** simultaneous. Both players track and dodge
   at once, the higher time-on-target share takes the round, fire isn't
   required ("require fire" is an option, off), host-validated with the
   200 ms rewind cap. Rounds and round length are settings.
4. **CS rules (decided):** 3v3/4v4/5v5 only (lobby limit 10 for CS), the CS2
   economy, and buyable kevlar and kevlar plus helmet with the CS2 damage and
   headshot rules.
5. **Keys (decided):** B buy, E use/plant/defuse, checked against KovaaK's
   `Input.ini` binds. Grapple stays on an ability slot.
6. **Lifesteal source:** host rule with native heal effect (recommended), not
   the native `LifeStealPercent` field (client-side, can't be validated).
7. **Map metadata format:** the `<map>.aimmod.json` sidecar (recommended), and
   whether map-port should read Reflex `.map` files for arena pickups.
8. **Developer asks (optional):** native pickup support, a grapple ability
   type, or guidance on `UpdateClientLocAndRot` and the Duels leftovers.
   None of these block anything.

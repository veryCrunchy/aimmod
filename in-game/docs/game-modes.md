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

**Claim validation** (`CombatMatch.Claim`; reasons are counted per player):

1. Order and time:
   - sequence numbers increase (`repeated`);
   - the shot time is inside the match and at most 1 s old (`time`);
   - the shooter is alive (`shooter-dead`);
   - fire rate is at most 1 / `TimeBetweenShots`, with 10% tolerance
     (`fire-rate`).
2. Shooter checks:
   - the ray starts within 32 cm of the shooter's own camera track
     (`origin`);
   - it looks within 3° of that track's pitch and yaw (`aim`).
3. Victim:
   - the alive opponent whose own track matches the drawn target the game hit
     (25 cm, 0–200 ms back: the rewind cap) (`target-mismatch`);
   - without a drawn target, whoever the ray hits after the capped estimated
     rewind (`miss`).
4. Geometry:
   - the ray must hit that hull (`ray-miss`);
   - the victim must not be spawn-protected (`spawn-protected`).
5. Damage is computed by the host:
   - a headshot is the ray through the top 25 cm sphere of the hull;
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

**Contracts for AimModCore** (requested through the coordinator; until they
exist the mode runs but can't hurt anyone in game):

```
self-shots.tsv   (written while self-pose.request is fresh; latest 64 shots; temp file + rename)
AIMMOD_SHOTS_1\t<file sequence>
shot\t<unix ms>\t<shot seq, increasing>\t<x>\t<y>\t<z>\t<pitch>\t<yaw>\t<weapon slot 0-7>\t<hit target id from self-pose target rows, 0 = miss>\t<headshot 0/1>
```

`self-shots.tsv` comes from polling the local weapon's `ShotsFiredThisSession`
and `ShotsHitThisSession`, plus `CharactersHit` and `bAnyHeadshots`, at frame
rate (the shot hooks don't fire, 9.1). The ray is the camera at that frame.

```
play-state.tsv   (written by the service on change; absolute and idempotent)
AIMMOD_PLAY_1\t<sequence>
match\t<%-escaped match id>
self\t<alive 0/1>\t<health>\t<max health>\t<respawn at unix ms, 0 = none>\t<spawn protected until unix ms>
hit\t<event id>\t<amount>\t<headshot 0/1>\t<attacker member id>      (last damage taken, for the native hit effect)
spawn\t<event id>\t<x>\t<y>\t<z>\t<yaw>      (where the host respawned this player; teleport once per event id)
```

How AimModCore applies `play-state.tsv`:

- Only while armed: an AimMod match scenario in freeplay, never a challenge.
- It reconciles the local character to the file:
  - each new `hit` event: `HandleDamage(amount, attacker avatar)`, for the
    native knockback, aim punch and death camera, then `SetHealth(health)`;
  - `alive=0`: let the native death run, or call `Death`;
  - `alive` back to 1: `Respawn`, then `SetHealth(max)`.

  The host stays the authority: any drift is corrected to the file's
  values.

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
  opponent. The respawn event carries it, and `play-state.tsv` gets a `spawn`
  line that AimModCore teleports to once per event id. Without spawn points,
  the game's own respawn stands. The coordinate conversion follows map-port's
  calibration and needs a live check.
- **Death effects and team colours on avatars** (AimModSteam, requested through
  the coordinator). The service writes `avatar-state.tsv` during combat
  matches:

  ```
  AIMMOD_AVATARS_1\t<sequence>
  match\t<%-escaped match id>
  peer\t<member id = SteamID64>\t<alive 0/1>\t<friend|enemy>\t<health>\t<died at unix ms, 0>\t<respawn at unix ms, 0>
  ```

  When `alive` drops to 0, the bridge plays the death on that avatar (native
  `Death`/gib if it stays inert, otherwise hide it with
  `SetActorHiddenInGame`). It shows the avatar again on respawn. `friend`
  avatars go on the local player's team (`SetTeam`), so team colours and the
  game's team checks are right. `health` can drive the avatar's health bar
  (`SetHealth` on the invulnerable avatar).

**Still open:** Hub verification of results (8.4); kill effects need the
bridge change above.

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
    profile the scenario already has;
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
| 3 | Knife | free | melee ability (native `FMeleeAbilityNative`), not a weapon slot |
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
  blocked unless there are 6, 8 or 10 players (`cs-teams`). Friendly fire is
  off.
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
- **Damage.** The claim names the weapon slot (`w`: 0 primary, 1 pistol). The
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
- **Contract additions to `play-state.tsv` for AimModCore:**

  ```
  round\t<freeze|live|planted|end|over>\t<frozen 0/1>\t<buy window 0/1>\t<phase ends, local unix ms>
  loadout\t<primary profile or ->\t<pistol profile or ->\t<armour>\t<helmet 0/1>\t<kit 0/1>
  spawn\t<id>\t<x>\t<y>\t<z>\t<yaw>          (one per CS round: teleport at the round start)
  ```

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

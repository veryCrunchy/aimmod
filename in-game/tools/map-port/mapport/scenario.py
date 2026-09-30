"""Generate a KovaaK's scenario (.sce) with a Counter-Strike movement profile.

Unit conversion: one Source unit is `map_scale` Unreal units in-game (the map JSON is written in
Source units and the scenario's MapScale multiplies it). Every length/speed below is Source value x
map_scale, so the player moves exactly like CS relative to the ported geometry. KovaaK's own
"Counter-Striker" profile (MaxSpeed 1100, StepUpHeight 75, BB 250) sits at roughly the same x4 scale.
"""
from __future__ import annotations

import math
from dataclasses import dataclass
from typing import List, Tuple

UE_GRAVITY = 980.0  # Unreal default world gravity, cm/s^2; the profile's Gravity is a scale on it
# Default character-model pack: Meso (skins Genji, McCree, Pharah, Tracer), Endo, Ecto, ...
BOT_MODEL = "Meso"
BOT_SKIN = "McCree"


@dataclass
class Movement:
    run_speed: float = 250.0       # knife run speed, u/s
    accelerate: float = 5.2        # sv_accelerate
    friction: float = 4.0          # sv_friction
    stop_speed: float = 75.0       # sv_stopspeed
    air_accelerate: float = 10.0   # sv_airaccelerate
    air_speed_cap: float = 30.0    # Source clamps air wish speed to 30 u/s
    gravity: float = 800.0         # sv_gravity
    jump_height: float = 57.0      # u; CS jump impulse is sqrt(2 * 800 * 57) ~= 302 u/s
    step: float = 18.0             # step-up height
    hull_height: float = 72.0
    hull_radius: float = 16.0
    crouch_height: float = 54.0
    crouch_speed_mult: float = 0.34
    max_velocity: float = 3500.0   # sv_maxvelocity


PRESETS = {
    "cs": Movement(),
    "css": Movement(accelerate=5.0, friction=4.0, stop_speed=75.0, air_accelerate=10.0),
    "csgo": Movement(accelerate=5.5, friction=5.2, stop_speed=80.0, air_accelerate=12.0),
}


def _b(v: bool) -> str:
    return "true" if v else "false"


def _v(x: float, y: float, z: float) -> str:
    return f"X={x:.3f} Y={y:.3f} Z={z:.3f}"


def character_profile(name: str, mv: Movement, s: float, weapon: str, bot: bool = False) -> List[Tuple[str, str]]:
    jump_v = math.sqrt(2 * mv.gravity * mv.jump_height)
    return [
        ("Name", name), ("MaxHealth", "100.0"), ("WeaponProfileNames", f"{weapon};;;;;;;"),
        ("MinRespawnDelay", "1.0"), ("MaxRespawnDelay", "1.0"),
        ("StepUpHeight", f"{mv.step * s:.1f}"), ("CrouchHeightModifier", f"{mv.crouch_height / mv.hull_height:.3f}"),
        ("CrouchAnimationSpeed", "1.0"), ("CameraOffset", _v(0, 0, 0)), ("HeadshotOnly", "false"),
        ("DamageKnockbackFactor", "0.0"),
        ("MaxSpeed", f"{mv.run_speed * s:.1f}"), ("MaxCrouchSpeed", f"{mv.run_speed * mv.crouch_speed_mult * s:.1f}"),
        ("Acceleration", f"{mv.accelerate * mv.run_speed * s:.1f}"),
        ("CrouchingAcceleration", f"{mv.accelerate * mv.run_speed * s:.1f}"),
        ("Friction", f"{mv.friction:.2f}"), ("BrakingFrictionFactor", "1.0"),
        ("JumpVelocityMin", f"{jump_v * s:.1f}"), ("JumpVelocityMax", f"{jump_v * s:.1f}"),
        ("Gravity", f"{mv.gravity * s / UE_GRAVITY:.4f}"), ("AirControl", "1.0"),
        ("CanCrouch", "true"), ("CanPogoJump", "false"), ("CanCrouchInAir", "true"),
        ("CrouchInAirRaisesFeet", "true"), ("CanJumpFromCrouch", "true"),
        ("EnemyBodyColor", _v(0.771, 0.1, 0.1)), ("EnemyBodyColorOnHit", _v(1, 1, 1)),
        ("EnemyBodyColorOnLookAt", _v(1, 1, 1)), ("EnemyHeadColor", _v(1, 0.6, 0.3)),
        ("EnemyHeadColorOnHit", _v(1, 1, 1)), ("EnemyHeadColorOnLookAt", _v(1, 1, 1)),
        ("TeamBodyColor", _v(0.1, 0.3, 0.9)), ("TeamHeadColor", _v(0.3, 0.6, 1.0)),
        ("MainBBType", "Cylindrical"), ("MainBBHeight", f"{mv.hull_height * s:.1f}"),
        ("MainBBRadius", f"{mv.hull_radius * s:.1f}"), ("MainBBHasHead", "true"),
        ("MainBBHeadRadius", f"{6.0 * s:.1f}"), ("MainBBHeadOffset", "0.0"), ("MainBBHide", "false"),
        ("ProjBBType", "Cylindrical"), ("ProjBBHeight", f"{mv.hull_height * s:.1f}"),
        ("ProjBBRadius", f"{mv.hull_radius * s:.1f}"), ("ProjBBHasHead", "true"),
        ("ProjBBHeadRadius", f"{6.0 * s:.1f}"), ("ProjBBHeadOffset", "0.0"), ("ProjBBHide", "true"),
        ("BlockSelfDamage", "true"), ("InvinciblePlayer", "false"), ("InvincibleBots", "false"),
        ("BlockTeamDamage", "true"), ("HasJetpack", "false"), ("JetpackActivationDelay", "0.2"),
        ("JetpackFullFuelTime", "4.0"), ("JetpackFuelIncPerSec", "1.0"), ("JetpackFuelRegensInAir", "false"),
        ("JetpackThrust", "6000.0"), ("JetpackMaxZVelocity", "400.0"), ("JetpackAirControlWithThrust", "0.25"),
        ("AirJumpCount", "0"), ("AirJumpVelocity", "0.0"), ("AbilityProfileNames", ";;;"),
        ("HideWeapon", _b(bot)), ("AerialFriction", "0.0"), ("AerialVerticalTurningFriction", "100000.0"),
        ("AerialVerticalBreakingFriction", "0.0"), ("UseAerialVerticalFriction", "false"),
        ("StrafeSpeedMult", "1.0"), ("BackSpeedMult", "1.0"), ("RespawnInvulnTime", "0.0"),
        ("BlockedSpawnRadius", "0.0"), ("BlockSpawnFOV", "0.0"), ("BlockSpawnDistance", "0.0"),
        ("RespawnAnimationDuration", "0.0"), ("AllowBufferedJumps", "false"), ("BounceOffWalls", "false"),
        ("LeanAngle", "0.0"), ("LeanDisplacement", "0.0"), ("AirJumpExtraControl", "0.0"),
        ("ForwardSpeedBias", "1.0"), ("HealthRegainedonkill", "0.0"), ("HealthRegenPerSec", "0.0"),
        ("HealthRegenDelay", "0.0"), ("JumpSpeedPenaltyDuration", "0.0"), ("JumpSpeedPenaltyPercent", "0.0"),
        ("ThirdPersonCamera", "false"), ("TPSArmLength", "300.0"), ("TPSOffset", _v(0, 150, 150)),
        ("BrakingDeceleration", "0.0"), ("TerminalVelocity", f"{mv.max_velocity * s:.1f}"),
        # Bots use the humanoid "Meso" skeletal model (184 cm mesh fitted to the hull) with a real skin;
        # per-mesh hit detection gives them a head and body like a CS player model.
        ("CharacterModel", BOT_MODEL if bot else "None"), ("CharacterSkin", BOT_SKIN if bot else "Default"),
        ("MeshHitDetection", _b(bot)), ("SpawnOffsetMin", _v(0, 0, 0)), ("SpawnOffsetMax", _v(0, 0, 0)),
        ("InvertBlockedSpawn", "false"), ("ViewBobTime", "0.0"), ("ViewBobAngleAdjustment", "0.0"),
        ("ViewBobCameraZOffset", "0.0"), ("ViewBobAffectsShots", "false"), ("IsFlyer", "false"),
        ("FlightObeysPitch", "false"), ("FlightVelocityUp", "800.0"), ("FlightAccelUp", "800.0"),
        ("FlightVelocityDown", "800.0"), ("FlightAccelDown", "800.0"), ("IsFlyUpOnJumpAndCrouch", "false"),
        ("DisableCharacterCollision", "false"), ("LifeStealPercent", "0.0"), ("AbilityGlobalCooldown", "0.0"),
        ("BlockAbilityOnStartDuration", "0.0"), ("DragCoefficient", "10.0"), ("AmmoRegainedOnKill", "0"),
        # Quake/Source movement: friction and acceleration scale with speed exactly like Source.
        ("ContinuousGroundFriction", f"{mv.friction:.2f}"), ("ContinuousAirFriction", "0.0"),
        ("ScaledGroundAcceleration", f"{mv.accelerate:.2f}"), ("ScaledAirAcceleration", f"{mv.air_accelerate:.2f}"),
        ("MaxAirSpeed", f"{mv.air_speed_cap * s:.1f}"), ("StopSpeed", f"{mv.stop_speed * s:.1f}"),
        ("StopSpeedThreshold", f"{mv.stop_speed * s:.1f}"), ("ClampVelocityToInputSpeed", "false"),
        ("JumpSkipsFriction", "false"), ("EnableQuakeMovement", "true"), ("EnableQuakeJump", "false"),
        ("KtJump", "0.0"), ("MovementPhysicsTickInterval", "0.0"), ("MovementPhysicsTickEnabled", "false"),
        ("TeamGlowUpHead", "0.0"), ("TeamGlowUpBody", "0.0"), ("EnemyGlowUpHead", "0.0"),
        ("EnemyGlowUpBody", "0.0"), ("EnemyGlowUpHeadOnHit", "0.0"), ("EnemyGlowUpBodyOnHit", "0.0"),
        ("EnemyGlowUpHeadOnLookAt", "0.0"), ("EnemyGlowUpBodyOnLookAt", "0.0"),
    ]


def weapon_profile(name: str, s: float) -> List[Tuple[str, str]]:
    """AK-47-like hitscan rifle without spread/recoil (aim practice, not spray practice)."""
    return [
        ("Name", name), ("Type", "Hitscan"), ("ShotsPerClick", "1"), ("DamagePerShot", "36.0"),
        ("KnockbackFactor", "0.0"), ("TimeBetweenShots", "0.1"), ("Pierces", "false"), ("Category", "FullyAuto"),
        ("BurstShotCount", "1"), ("TimeBetweenBursts", "0.5"), ("MaxHitscanRange", "1000000.0"),
        ("HeadshotCapable", "true"), ("HeadshotMultiplier", "4.0"), ("CooldownType", "InfiniteUse"),
        ("MagazineMax", "30"), ("AmmoPerShot", "1"), ("ReloadTimeFromEmpty", "2.5"), ("ReloadTimeFromPartial", "2.5"),
        ("DamageFalloffStartDistance", "100000.0"), ("DamageFalloffStopDistance", "100000.0"),
        ("DamageAtMaxRange", "36.0"), ("DelayBeforeShot", "0.0"), ("VisualLifetime", "0.05"),
        ("BlockedByWorld", "true"), ("CanAimDownSight", "false"), ("ShootSoundCooldown", "0.08"),
        ("HitSoundCooldown", "0.08"), ("ShootSound", "Shot"), ("HitscanVisualOffset", _v(0, 0, -40)),
        ("DecalType", "1"), ("DecalSize", "30.0"), ("CircularSpread", "true"),
        ("SpreadSSA", "0.0,0.0,0.0,0.0"), ("SpreadSCA", "0.0,0.0,0.0,0.0"), ("SpreadMSA", "0.0,0.0,0.0,0.0"),
        ("SpreadMCA", "0.0,0.0,0.0,0.0"), ("SpreadSSH", "0.0,0.0,0.0,0.0"), ("SpreadSCH", "0.0,0.0,0.0,0.0"),
        ("SpreadMSH", "0.0,0.0,0.0,0.0"), ("SpreadMCH", "0.0,0.0,0.0,0.0"),
        ("MaxRecoilUp", "0.0"), ("MinRecoilUp", "0.0"), ("MinRecoilHoriz", "0.0"), ("MaxRecoilHoriz", "0.0"),
        ("WeaponModel", "Rifle"), ("WeaponAnimation", "Primary"), ("WeaponSkin", "Default"),
        ("ADSFOVScaleString", "Quake/Source"), ("FullyAutomatic", "true"),
    ]


def build(name: str, map_json_name: str, map_text: str, map_scale: float, mv: Movement,
          bots: int = 5, description: str = "") -> str:
    s = map_scale
    player, bot_char, weapon = "CS Player", "CS Target", "CS Rifle"
    header = [
        ("Name", name), ("PlayerCharacters", player), ("BotCharacters", "CS Target Bot.bot"),
        ("IsChallenge", "false"), ("Timelimit", "600.0"), ("PlayerProfile", player),
        ("AddedBots", ";".join(["CS Target Bot.bot"] * bots)), ("PlayerMaxLives", "0"),
        ("BotMaxLives", ";".join(["0"] * bots)), ("PlayerTeam", "1"), ("BotTeams", ";".join(["2"] * bots)),
        ("ScoreToWin", "1000.0"), ("ScorePerDamage", "0.0"), ("ScorePerHit", "0.0"), ("ScorePerKill", "1.0"),
        ("MapName", map_json_name), ("MapScale", f"{map_scale}"), ("BlockProjectilePredictors", "true"),
        ("BlockCheats", "false"), ("InvinciblePlayer", "true"), ("InvincibleBots", "false"), ("Timescale", "1.0"),
        ("BlockHealthbars", "false"), ("TimeRefilledByKill", "0.0"), ("LockFOVRange", "false"),
        ("AimTypeTag", "Clicking"), ("AimSubTypeTag", "Dynamic"), ("AimTypeFlicking", "true"),
        ("AimTypeProjectile", "false"), ("AimTypePlayerMovement", "true"), ("DifficultyTag", "2"),
        ("SearchTags", "Map port, Counter-Strike, Movement"),
        ("Description", description or "Ported Source map with Counter-Strike movement."),
        ("GameVersion", "3.9.11"), ("ScenarioVersion", "Initial"),
    ]
    aim = [("Name", "Default"), ("MinReactionTime", "0.3"), ("MaxReactionTime", "0.4"), ("AimingStyle", "Simple")]
    bot = [
        ("Name", "CS Target Bot"), ("DodgeProfileNames", "CS Strafe"), ("DodgeProfileWeights", "1.0"),
        ("DodgeProfileMaxChangeTime", "5.0"), ("DodgeProfileMinChangeTime", "1.0"),
        ("WeaponsProfileNames", ";;;;;;;"), ("WeaponProfileWeights", "1.0;1.0;1.0;1.0;1.0;1.0;1.0;1.0"),
        ("AimingProfileNames", "Default;Default;Default;Default;Default;Default;Default;Default"),
        ("WeaponSwitchTime", "3.0"), ("UseWeapons", "false"), ("CharacterProfile", bot_char),
        ("SeeThroughWalls", "false"), ("NoDodging", "false"), ("StandStillUntilHurt", "false"),
        ("NoAiming", "true"), ("SpawnGroup", "0"), ("UseMinimumRespawnTime", "true"), ("DisableScoring", "false"),
    ]
    dodge = [
        ("Name", "CS Strafe"), ("MaxTargetDistance", f"{2500 * s / 4:.1f}"), ("MinTargetDistance", "0.0"),
        ("ToggleLeftRight", "true"), ("ToggleForwardBack", "true"), ("MinLRTimeChange", "0.2"),
        ("MaxLRTimeChange", "1.0"), ("MinFBTimeChange", "0.2"), ("MaxFBTimeChange", "1.0"),
        ("JumpFrequency", "0.02"), ("CrouchInAirFrequency", "0.0"), ("CrouchOnGroundFrequency", "0.05"),
        ("MinCrouchTime", "0.3"), ("MaxCrouchTime", "0.6"), ("MinJumpTime", "0.3"), ("MaxJumpTime", "0.6"),
        ("BlockedMovementPercent", "0.5"), ("BlockedMovementReactionMin", "0.125"),
        ("BlockedMovementReactionMax", "0.2"),
    ]
    sections = [("", header), ("[Aim Profile]", aim), ("[Bot Profile]", bot),
                ("[Character Profile]", character_profile(player, mv, s, weapon)),
                ("[Character Profile]", character_profile(bot_char, mv, s, "", bot=True)),
                ("[Dodge Profile]", dodge), ("[Weapon Profile]", weapon_profile(weapon, s))]
    out: List[str] = []
    for title, rows in sections:
        if title:
            out += ["", title]
        out += [f"{k}={v}" for k, v in rows]
    out += ["", "[Map Data]", map_text.rstrip("\n")]
    return "\r\n".join(out) + "\r\n"

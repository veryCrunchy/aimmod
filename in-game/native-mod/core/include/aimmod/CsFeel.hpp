#pragma once
// CS weapon feel (in-game/docs/game-modes.md 6.6.6), the engine-free half. AimModCore's CsFeel
// applies it in AimMod CS match scenarios only; the host (CsFeel.cs) mirrors the numbers and the
// spread formula exactly, so it can rebuild every bullet's ray.
//  - Inaccuracy like CS:GO: a static spread, a base cone by stance (scoped snipers their own),
//    a share of the movement cone by speed, a cone while airborne, and a penalty that each shot
//    and each landing adds and that recovers over time.
//  - The bullet's ray: the camera ray turned by a deterministic offset drawn from a per-shot seed
//    (the match salt and the shot number), so the host gets the same ray from the same numbers.
//  - The scope: CS zoom levels (right mouse: level 1, level 2, out), a short scope-in, out after
//    each shot and back after the bolt while the button is held, and the scope's blur while moving.
//  - Speed: every weapon's CS max speed, as a share of the knife's (scoped snipers slower still).
#include <cstdint>
#include <string_view>
#include <vector>

namespace aimmod::cs
{
    // CS units: the CS map ports run 250 u/s at 1100 cm/s (4.4 cm per unit).
    constexpr double UnitCm = 4.4, KnifeSpeed = 250;
    // Speeds (u/s) at or below this share of the weapon's max speed add no movement inaccuracy, and
    // the movement cone is full from FullMoveShare on (CS:GO's accurate range and full-speed point).
    constexpr double AccurateShare = 0.34, FullMoveShare = 0.95;
    // The penalty (fire, landing) left after a weapon's recovery time.
    constexpr double RecoveredShare = 0.1;
    // The penalty never grows beyond this (rad), whatever is sprayed.
    constexpr double MaxPenalty = 0.3;
    // How long scoping in takes until the scoped accuracy is reached (s).
    constexpr double ScopeInSeconds = 0.15;

    // One CS item. Angles in radians (CS:GO's "inaccuracy" numbers / 1000).
    struct WeaponFeel
    {
        const char* id;           // the service's item id
        const char* profile;      // the KovaaK's weapon profile the arena carries
        const char* cls;          // pistol, smg, rifle, sniper, knife, grenade, bomb
        double maxSpeed;          // u/s with it in hand
        double scopedSpeed;       // u/s while scoped (0: no scope)
        double spread;            // static spread
        double stand, crouch;     // base cone (snipers: unscoped)
        double move;              // added at full speed
        double jump;              // added while airborne
        double land;              // penalty added on landing
        double fire;              // penalty added per shot
        double recoverStand, recoverCrouch; // s until RecoveredShare of the penalty is left
        double scopedStand, scopedCrouch;   // base cone while scoped
        int zoomLevels;           // 0 none, 1 or 2
        double zoomFov[2];        // CS FOV of each level (degrees; CS's hip FOV is 90)
        bool Spreads() const { return stand > 0 || spread > 0; }
    };
    const std::vector<WeaponFeel>& Feels();
    const WeaponFeel* FeelById(std::string_view id);
    const WeaponFeel* FeelByProfile(std::string_view profile);

    // How the player moves at a shot.
    struct Motion
    {
        double speed{};  // horizontal, u/s
        bool air{};      // airborne
        bool crouch{};
    };
    // The share of the movement cone at this speed (0..1), against the weapon's current max speed.
    double MoveShare(double speed, double maxSpeed);
    // The base and movement cone (no penalty, no static spread). scopeBlend: 0 unscoped .. 1 fully
    // scoped (a sniper scoping in blends from its unscoped cone).
    double ConeNow(const WeaponFeel& w, const Motion& m, double scopeBlend);
    // The least cone the host accepts for a shot moving at `speed` (u/s), airborne or not: the
    // weapon's best stance (scoped for a sniper), its slowest max speed, no penalty.
    double MinimumCone(const WeaponFeel& w, double speed, bool air);

    // A weapon's live inaccuracy: the cone now plus the penalty its shots and landings left.
    class Accuracy
    {
    public:
        // dt in seconds. scoped: in the scope (a sniper), counting the scope-in.
        void Tick(double dt, const WeaponFeel& w, const Motion& m, double scopeBlend);
        void Shot(const WeaponFeel& w);
        void Reset() { *this = Accuracy{}; }
        double inaccuracy() const { return m_cone + m_penalty; }
        double penalty() const { return m_penalty; }
        // Smoothed for the scope's blur and the crosshair: rises at once, settles over about 0.35 s.
        double settled() const { return m_settled; }

    private:
        double m_cone{}, m_penalty{}, m_settled{};
        bool m_wasAir{}, m_seen{};
    };

    // The match's salt for the seeds: FNV-1a 64 of the match id.
    std::uint64_t Salt(std::string_view matchId);
    // The bullet's offset from the camera ray, in the camera's right and up directions (tangent units),
    // for the shot `shot` of this match: radius inaccuracy * u1 at angle 2 pi u2, plus the static
    // spread * u3 at 2 pi u4, with u1..u4 from SplitMix64 seeded by salt and shot.
    struct Offset
    {
        double right{}, up{};
    };
    Offset SpreadOffset(std::uint64_t salt, std::uint64_t shot, double inaccuracy, double spread);
    // The four uniform numbers for a shot (tests compare them with the host's).
    void SpreadRandoms(std::uint64_t salt, std::uint64_t shot, double out[4]);
    // The bullet's unit direction: forward + right * o.right + up * o.up, from the camera's pitch and
    // yaw (degrees, UE: x forward, y right, z up).
    void SpreadDirection(double pitch, double yaw, const Offset& o, double out[3]);
    // The same as a rotator added to the camera's (degrees): the pitch and yaw of that direction minus
    // the camera's (yaw wrapped to -180..180).
    void SpreadRotator(double pitch, double yaw, const Offset& o, double& addPitch, double& addYaw);
    // The same as a rotator applied in the camera's own frame (degrees).
    void SpreadLocalRotator(const Offset& o, double& localPitch, double& localYaw);

    // CS zoom: the scope level (0 unscoped) of a weapon with zoom levels.
    class Scope
    {
    public:
        // Right mouse: the next level, out after the last one.
        void Press(const WeaponFeel& w, double now);
        // A shot while scoped: out of the scope; back to the same level after `bolt` seconds if the
        // button is held then.
        void Shot(double now, double bolt);
        // Every frame: held is the right mouse button; returns true when the level changed.
        bool Tick(double now, bool held);
        // Switching weapons, reloading, dying: out, nothing pending.
        void Out();
        int level() const { return m_level; }
        double since() const { return m_since; }
        bool resumePending() const { return m_resumeLevel > 0; }
        // 0..1: how far scoping in has got (ScopeInSeconds), 0 while unscoped.
        double Blend(double now) const;

    private:
        int m_level{}, m_resumeLevel{};
        double m_since{}, m_resumeAt{};
        bool m_changed{};
    };
    // The full-zoom FOV of a level, from the game's first level FOV (whatever FOV scaling the game
    // uses): the same ratio of half-angle tangents as CS's levels.
    double LevelFov(double firstLevelFov, const WeaponFeel& w, int level);
    // The zoomed sensitivity of a level: the lobby's (level 1) times the level's zoom against level 1.
    double LevelSensitivity(double firstLevelSensitivity, const WeaponFeel& w, int level);

    // Max speed with the weapon in hand, as a share of the knife's (scoped: the scoped speed).
    double SpeedShare(const WeaponFeel* w, bool scoped);

    // The crosshair's gap: the cone's edge (inaccuracy plus spread) on the screen, as a share of half
    // the screen's width, for the horizontal FOV in degrees.
    double CrosshairGap(double cone, double fovDegrees);
    // The scope's blur (0 sharp .. 1 fully smeared) from the settled inaccuracy above the scoped cone.
    double ScopeBlur(const WeaponFeel& w, double settled, double scopeBlend);
} // namespace aimmod::cs

#include <aimmod/CsFeel.hpp>

#include <algorithm>
#include <cmath>

namespace aimmod::cs
{
    namespace
    {
        constexpr double Pi = 3.14159265358979323846, Deg = Pi / 180.0;
        // Smoothing of the settled inaccuracy on the way down (s).
        constexpr double SettleSeconds = 0.12;
        // Extra inaccuracy (rad) above the scoped cone at which the scope is fully smeared.
        constexpr double FullBlur = 0.03;

        std::uint64_t SplitMix(std::uint64_t& state)
        {
            state += 0x9E3779B97F4A7C15ull;
            std::uint64_t z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ull;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBull;
            return z ^ (z >> 31);
        }
        double Tan(double degrees) { return std::tan(degrees * Deg / 2); }
        double LevelRatio(const WeaponFeel& w, int level)
        {
            if (level <= 1 || w.zoomLevels < 2) return 1;
            return Tan(w.zoomFov[1]) / Tan(w.zoomFov[0]);
        }
    } // namespace

    // CS:GO-like numbers (its weapon scripts' inaccuracy values / 1000), rounded; max speeds in u/s.
    const std::vector<WeaponFeel>& Feels()
    {
        static const std::vector<WeaponFeel> all = {
            //  id        profile                    class     speed scoped spread   stand   crouch  move   jump  land  fire    recover     scoped          zoom
            {"glock", "AimMod CS Glock-18", "pistol", 240, 0, 0.0020, 0.0070, 0.0045, 0.014, 0.16, 0.04, 0.045, 0.33, 0.25, 0, 0, 0, {0, 0}},
            {"usp", "AimMod CS USP-S", "pistol", 240, 0, 0.0015, 0.0050, 0.0035, 0.012, 0.15, 0.04, 0.050, 0.35, 0.27, 0, 0, 0, {0, 0}},
            {"deagle", "AimMod CS Desert Eagle", "pistol", 230, 0, 0.0020, 0.0090, 0.0060, 0.060, 0.26, 0.06, 0.060, 0.80, 0.60, 0, 0, 0, {0, 0}},
            {"mac10", "AimMod CS MAC-10", "smg", 240, 0, 0.0030, 0.0150, 0.0100, 0.030, 0.16, 0.04, 0.008, 0.35, 0.25, 0, 0, 0, {0, 0}},
            {"mp9", "AimMod CS MP9", "smg", 240, 0, 0.0025, 0.0130, 0.0090, 0.026, 0.16, 0.04, 0.007, 0.32, 0.24, 0, 0, 0, {0, 0}},
            {"ak47", "AimMod CS AK-47", "rifle", 215, 0, 0.0006, 0.0064, 0.0048, 0.146, 0.30, 0.06, 0.0078, 0.37, 0.26, 0, 0, 0, {0, 0}},
            {"m4a1s", "AimMod CS M4A1-S", "rifle", 225, 0, 0.0005, 0.0050, 0.0037, 0.120, 0.30, 0.06, 0.0070, 0.35, 0.25, 0, 0, 0, {0, 0}},
            {"awp", "AimMod CS AWP", "sniper", 200, 100, 0.0002, 0.0800, 0.0600, 0.180, 0.45, 0.08, 0.110, 0.25, 0.20, 0.0020, 0.0015, 2, {40, 10}},
            {"knife", "AimMod CS Knife", "knife", 250, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, {0, 0}},
            {"grenade", "AimMod CS Grenade", "grenade", 245, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, {0, 0}},
            {"c4", "AimMod CS C4", "bomb", 250, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, {0, 0}},
        };
        return all;
    }

    const WeaponFeel* FeelById(std::string_view id)
    {
        for (const auto& w : Feels())
            if (id == w.id) return &w;
        return nullptr;
    }

    const WeaponFeel* FeelByProfile(std::string_view profile)
    {
        for (const auto& w : Feels())
            if (profile == w.profile) return &w;
        return nullptr;
    }

    double MoveShare(double speed, double maxSpeed)
    {
        if (!(maxSpeed > 0) || !std::isfinite(speed)) return 0;
        return std::clamp((speed / maxSpeed - AccurateShare) / (FullMoveShare - AccurateShare), 0.0, 1.0);
    }

    double ConeNow(const WeaponFeel& w, const Motion& m, double scopeBlend)
    {
        const double blend = w.zoomLevels > 0 ? std::clamp(scopeBlend, 0.0, 1.0) : 0;
        const double unscoped = m.crouch ? w.crouch : w.stand, scoped = m.crouch ? w.scopedCrouch : w.scopedStand;
        const double base = unscoped + (scoped - unscoped) * blend;
        const double maxNow = blend >= 0.5 && w.scopedSpeed > 0 ? w.scopedSpeed : w.maxSpeed;
        return base + w.move * MoveShare(m.speed, maxNow) + (m.air ? w.jump : 0);
    }

    double MinimumCone(const WeaponFeel& w, double speed, bool air)
    {
        double base = std::min(w.stand, w.crouch);
        if (w.zoomLevels > 0) base = std::min({base, w.scopedStand, w.scopedCrouch});
        return base + w.move * MoveShare(speed, w.maxSpeed) + (air ? w.jump : 0);
    }

    void Accuracy::Tick(double dt, const WeaponFeel& w, const Motion& m, double scopeBlend)
    {
        dt = std::clamp(std::isfinite(dt) ? dt : 0.0, 0.0, 0.5);
        m_cone = ConeNow(w, m, scopeBlend);
        if (m_seen && m_wasAir && !m.air) m_penalty = std::min(MaxPenalty, m_penalty + w.land);
        m_wasAir = m.air;
        m_seen = true;
        const double recover = std::max(0.05, m.crouch ? w.recoverCrouch : w.recoverStand);
        m_penalty *= std::pow(RecoveredShare, dt / recover);
        if (m_penalty < 1e-6) m_penalty = 0;
        const double now = inaccuracy();
        if (now >= m_settled) m_settled = now;
        else m_settled += (now - m_settled) * (1 - std::exp(-dt / SettleSeconds));
    }

    void Accuracy::Shot(const WeaponFeel& w)
    {
        m_penalty = std::min(MaxPenalty, m_penalty + w.fire);
        m_settled = std::max(m_settled, inaccuracy());
    }

    std::uint64_t Salt(std::string_view matchId)
    {
        std::uint64_t h = 14695981039346656037ull;
        for (unsigned char c : matchId)
        {
            h ^= c;
            h *= 1099511628211ull;
        }
        return h;
    }

    void SpreadRandoms(std::uint64_t salt, std::uint64_t shot, double out[4])
    {
        std::uint64_t state = salt ^ (shot * 0xD1B54A32D192ED03ull);
        for (int i = 0; i < 4; ++i) out[i] = static_cast<double>(SplitMix(state) >> 11) * (1.0 / 9007199254740992.0);
    }

    Offset SpreadOffset(std::uint64_t salt, std::uint64_t shot, double inaccuracy, double spread)
    {
        double u[4];
        SpreadRandoms(salt, shot, u);
        const double r1 = std::max(0.0, inaccuracy) * u[0], a1 = 2 * Pi * u[1], r2 = std::max(0.0, spread) * u[2], a2 = 2 * Pi * u[3];
        return {std::cos(a1) * r1 + std::cos(a2) * r2, std::sin(a1) * r1 + std::sin(a2) * r2};
    }

    void SpreadDirection(double pitch, double yaw, const Offset& o, double out[3])
    {
        const double p = pitch * Deg, y = yaw * Deg, cp = std::cos(p), sp = std::sin(p), cy = std::cos(y), sy = std::sin(y);
        const double f[3] = {cp * cy, cp * sy, sp}, r[3] = {-sy, cy, 0}, u[3] = {-sp * cy, -sp * sy, cp};
        double len = 0;
        for (int i = 0; i < 3; ++i)
        {
            out[i] = f[i] + o.right * r[i] + o.up * u[i];
            len += out[i] * out[i];
        }
        len = std::sqrt(len);
        for (int i = 0; i < 3; ++i) out[i] /= len;
    }

    void SpreadRotator(double pitch, double yaw, const Offset& o, double& addPitch, double& addYaw)
    {
        double d[3];
        SpreadDirection(pitch, yaw, o, d);
        addPitch = std::asin(std::clamp(d[2], -1.0, 1.0)) / Deg - pitch;
        addYaw = std::remainder(std::atan2(d[1], d[0]) / Deg - yaw, 360.0);
    }

    void SpreadLocalRotator(const Offset& o, double& localPitch, double& localYaw)
    {
        localYaw = std::atan2(o.right, 1.0) / Deg;
        localPitch = std::atan2(o.up, std::hypot(1.0, o.right)) / Deg;
    }

    void Scope::Press(const WeaponFeel& w, double now)
    {
        if (w.zoomLevels <= 0) return;
        const int before = m_level;
        m_resumeLevel = 0;
        m_level = (m_level + 1) % (w.zoomLevels + 1);
        if (before == 0) m_since = now;
        m_changed = true;
    }

    void Scope::Shot(double now, double bolt)
    {
        if (m_level == 0) return;
        m_resumeLevel = m_level;
        m_resumeAt = now + std::max(0.0, bolt);
        m_level = 0;
        m_changed = true;
    }

    bool Scope::Tick(double now, bool held)
    {
        if (m_resumeLevel > 0 && now >= m_resumeAt)
        {
            if (held)
            {
                m_level = m_resumeLevel;
                m_since = now;
                m_changed = true;
            }
            m_resumeLevel = 0;
        }
        const bool changed = m_changed;
        m_changed = false;
        return changed;
    }

    void Scope::Out()
    {
        if (m_level != 0) m_changed = true;
        m_level = m_resumeLevel = 0;
    }

    double Scope::Blend(double now) const { return m_level > 0 ? std::clamp((now - m_since) / ScopeInSeconds, 0.0, 1.0) : 0; }

    double LevelFov(double firstLevelFov, const WeaponFeel& w, int level)
    {
        const double ratio = LevelRatio(w, level);
        if (ratio == 1) return firstLevelFov;
        return 2 * std::atan(std::tan(firstLevelFov * Deg / 2) * ratio) / Deg;
    }

    double LevelSensitivity(double firstLevelSensitivity, const WeaponFeel& w, int level) { return firstLevelSensitivity * LevelRatio(w, level); }

    double SpeedShare(const WeaponFeel* w, bool scoped)
    {
        if (!w) return 1;
        return (scoped && w->scopedSpeed > 0 ? w->scopedSpeed : w->maxSpeed) / KnifeSpeed;
    }

    double CrosshairGap(double cone, double fovDegrees)
    {
        if (!(fovDegrees > 1 && fovDegrees < 179)) return 0;
        return std::tan(std::clamp(cone, 0.0, 1.2)) / Tan(fovDegrees);
    }

    double ScopeBlur(const WeaponFeel& w, double settled, double scopeBlend)
    {
        if (w.zoomLevels <= 0 || scopeBlend <= 0) return 0;
        const double extra = std::max(0.0, settled - std::min(w.scopedStand, w.scopedCrouch));
        return std::clamp(std::max(extra / FullBlur, 1 - scopeBlend), 0.0, 1.0);
    }
} // namespace aimmod::cs

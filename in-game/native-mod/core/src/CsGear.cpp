#include <aimmod/CsGear.hpp>

#include <algorithm>
#include <cmath>

namespace aimmod::cs
{
    Loadout FromRound(const RoundState::Loadout& line)
    {
        Loadout l;
        l.names = {line.primary, line.pistol, line.knife, line.grenade, line.bomb};
        return l;
    }

    int Best(const Loadout& l)
    {
        for (int slot : {PrimarySlot, PistolSlot, KnifeSlot, GrenadeSlot, BombSlot})
            if (l.Has(slot)) return slot;
        return -1;
    }

    int Cycle(const Loadout& l, int current, int direction)
    {
        if (direction == 0) return current;
        const int step = direction > 0 ? 1 : -1;
        int slot = current < 0 || current >= Slots ? (step > 0 ? -1 : Slots) : current;
        for (int i = 0; i < Slots; ++i)
        {
            slot = (slot + step + Slots) % Slots;
            if (slot != current && l.Has(slot)) return slot;
        }
        return current;
    }

    void Switcher::Observe(int slot)
    {
        if (slot == m_current) return;
        if (m_current >= 0) m_last = m_current;
        m_current = slot;
    }

    int Switcher::Previous(const Loadout& l) const
    {
        if (m_last >= 0 && m_last != m_current && l.Has(m_last)) return m_last;
        for (int slot : {PrimarySlot, PistolSlot, KnifeSlot, GrenadeSlot, BombSlot})
            if (slot != m_current && l.Has(slot)) return slot;
        return -1;
    }

    int AfterLoadout(const Loadout* before, const Loadout& after, int inHand)
    {
        if (!before) return Best(after);
        for (int slot : {PrimarySlot, PistolSlot})
            if (after.Has(slot) && after.names[static_cast<std::size_t>(slot)] != before->names[static_cast<std::size_t>(slot)]) return slot == inHand ? -1 : slot;
        if (!after.Has(inHand)) return Best(after);
        return -1;
    }

    // The same schedule as BombSounds.BeepInterval in the service.
    double BeepInterval(double remaining)
    {
        const double r = std::clamp(remaining, 0.0, 40.0) / 40.0;
        return std::clamp(std::pow(r, 0.9), 0.13, 1.0);
    }

    bool LightOn(double remaining)
    {
        if (remaining > 40.0 || remaining < -0.5) return false;
        if (remaining <= 1.0) return true;
        // The latest beep at or before now: beeps at 40 s left, then every BeepInterval.
        double beep = 40.0;
        for (int i = 0; i < 1000; ++i)
        {
            const double next = beep - BeepInterval(beep);
            if (next < remaining) break;
            beep = next;
        }
        return beep - remaining < 0.1;
    }

    namespace
    {
        constexpr const wchar_t* Cube = L"/Engine/BasicShapes/Cube.Cube";
        constexpr const wchar_t* Sphere = L"/Engine/BasicShapes/Sphere.Sphere";
        constexpr const wchar_t* Cylinder = L"/Engine/BasicShapes/Cylinder.Cylinder";
    } // namespace

    const std::vector<Part>& BombModel()
    {
        static const std::vector<Part> parts = {
            // The block: dark olive, resting on z = 0.
            {Cube, {0, 0, 3.6}, {26, 16, 7.2}, {0, 0, 0}, {0.16, 0.15, 0.07}, false},
            // A light grey keypad on top, and a green display beside it.
            {Cube, {4.5, 1.5, 7.4}, {9, 7.5, 0.6}, {0, 0, 0}, {0.55, 0.55, 0.52}, false},
            {Cube, {-5.5, 2.5, 7.4}, {7, 4, 0.5}, {0, 0, 0}, {0.03, 0.22, 0.05}, false},
            // Two black bands round the block.
            {Cube, {-10, 0, 3.6}, {1.6, 16.6, 7.6}, {0, 0, 0}, {0.02, 0.02, 0.02}, false},
            {Cube, {10.5, 0, 3.6}, {1.6, 16.6, 7.6}, {0, 0, 0}, {0.02, 0.02, 0.02}, false},
            // A wire from the display to the keypad.
            {Cylinder, {-0.5, -4.5, 7.6}, {0.8, 0.8, 9}, {0, 0, 90}, {0.5, 0.05, 0.03}, false},
            // The light.
            {Sphere, {-7, -5, 7.8}, {1.8, 1.8, 1.8}, {0, 0, 0}, {1.0, 0.03, 0.02}, true},
        };
        return parts;
    }

    const std::vector<Part>& KnifeModel()
    {
        static const std::vector<Part> parts = {
            // The blade, narrowing to the tip (a second, shorter piece), steel.
            {Cube, {9, 0, 0.3}, {14, 0.5, 2.8}, {0, 0, 0}, {0.62, 0.64, 0.68}, false},
            {Cube, {17, 0, 0.9}, {4, 0.45, 1.5}, {0, 0, 0}, {0.62, 0.64, 0.68}, false},
            // Guard and handle.
            {Cube, {1.5, 0, 0}, {1.2, 3, 4.4}, {0, 0, 0}, {0.08, 0.08, 0.09}, false},
            {Cube, {-4.5, 0, -0.2}, {10, 2.2, 3}, {0, 0, 0}, {0.03, 0.03, 0.03}, false},
        };
        return parts;
    }

    Hold InHand(int slot)
    {
        // Low on the right, pointing ahead (knife), held out flat in front (bomb), or a grenade in the palm.
        if (slot == BombSlot) return {{34, 9, -22}, {-25, 8, 0}};
        if (slot == GrenadeSlot) return {{30, 13, -15}, {10, -15, 0}};
        return {{30, 15, -14}, {12, -12, -20}};
    }
    double KnifeMoveSeconds(KnifeMove move) { return move == KnifeMove::Stab ? 0.38 : move == KnifeMove::None ? 0.0 : 0.25; }

    KnifeMove NextSlash(KnifeMove previous) { return previous == KnifeMove::SlashRight ? KnifeMove::SlashLeft : KnifeMove::SlashRight; }

    Hold KnifePose(KnifeMove move, double t)
    {
        Hold pose{};
        const double length = KnifeMoveSeconds(move);
        if (move == KnifeMove::None || t <= 0 || t >= length) return pose;
        constexpr double Pi = 3.14159265358979323846;
        const double u = t / length;
        // Out fast (ease out), then back to rest (ease in and out).
        const double peak = move == KnifeMove::Stab ? 0.3 : 0.35;
        const double a = u < peak ? std::sin(u / peak * Pi / 2) : 0.5 + 0.5 * std::cos((u - peak) / (1 - peak) * Pi);
        if (move == KnifeMove::Stab)
        {
            pose.offset[0] = 24 * a, pose.offset[1] = -3 * a, pose.offset[2] = 3 * a;
            pose.rotation[0] = -8 * a;
            return pose;
        }
        const double side = move == KnifeMove::SlashRight ? 1.0 : -1.0;
        pose.offset[0] = 8 * a, pose.offset[1] = -14 * side * a, pose.offset[2] = 4 * a;
        pose.rotation[0] = -15 * a, pose.rotation[1] = -55 * side * a, pose.rotation[2] = 35 * side * a;
        return pose;
    }
} // namespace aimmod::cs

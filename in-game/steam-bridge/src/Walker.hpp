#pragma once
// Developer mode's simulated player: walks the arena like a simple bot, between the map's
// spawn points, feet on the floor. The mod supplies the world through two line traces
// (floor below a point, wall between two points); everything else lives here so it can be
// tested without the game. Positions are capsule centres in world units (cm).

#include "GhostMath.hpp"

#include <array>
#include <cmath>
#include <cstdint>
#include <functional>
#include <optional>
#include <vector>

namespace bridge::ghost
{
    struct Walker
    {
        // Floor height below (x, y), searching down from z; nullopt when there is no floor.
        using Floor = std::function<std::optional<double>(double x, double y, double z)>;
        // True when nothing solid lies between the two points.
        using Clear = std::function<bool(double ax, double ay, double az, double bx, double by, double bz)>;

        static constexpr double Speed = 320;        // cm/s, a walking bot
        static constexpr double StrafeAmplitude = 90, StrafePeriod = 2.6;
        static constexpr double StepUp = 45, StepDown = 70; // larger changes count as a wall or a ledge
        static constexpr double Arrive = 70;         // cm from a spawn counts as there
        static constexpr double WaistAbove = 10;     // wall checks at waist height above the centre
        static constexpr double JumpHeight = 50, JumpTime = 0.55;

        std::vector<std::array<double, 3>> spawns;   // capsule centres (or feet) of the arena's spawns
        double x = 0, y = 0, z = 0, yaw = 0;
        bool placed = false;
        int target = -1, at = -1, blocked = 0;
        double walkedFor = 0, nextCrouch = 6, crouchUntil = -1, nextJump = 14, jumpStart = -1;
        std::uint32_t seed = 0x2468ace1u;

        std::uint32_t Next()
        {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            return seed;
        }
        double Uniform(double lo, double hi) { return lo + (hi - lo) * (Next() % 10000) / 10000.0; }

        // Stand on spawn `index` (its floor), e.g. at the start and after each respawn.
        bool Place(int index, double halfHeight, const Floor& floor)
        {
            if (index < 0 || index >= static_cast<int>(spawns.size())) return false;
            const auto& s = spawns[static_cast<std::size_t>(index)];
            const auto ground = floor(s[0], s[1], s[2] + halfHeight + 50);
            x = s[0];
            y = s[1];
            z = ground ? *ground + halfHeight : s[2];
            at = index;
            target = -1;
            blocked = 0;
            placed = true;
            return true;
        }
        bool PlaceRandom(double halfHeight, const Floor& floor)
        {
            return !spawns.empty() && Place(static_cast<int>(Next() % spawns.size()), halfHeight, floor);
        }

        // The next spawn to walk to: a random other one in clear sight (waist height), else any other.
        void Choose(const Clear& clear)
        {
            const int n = static_cast<int>(spawns.size());
            if (n < 2) { target = -1; return; }
            int fallback = -1;
            for (int tries = 0; tries < 2 * n; ++tries)
            {
                const int i = static_cast<int>(Next() % static_cast<std::uint32_t>(n));
                if (i == at) continue;
                if (fallback < 0) fallback = i;
                const auto& s = spawns[static_cast<std::size_t>(i)];
                if (clear(x, y, z + WaistAbove, s[0], s[1], s[2] + WaistAbove)) { target = i; return; }
            }
            target = fallback;
        }

        // One step of `dt` seconds at time `now` (seconds). Never moves through a wall, off a
        // ledge or up a step higher than StepUp; a blocked way picks another spawn.
        RemoteTransform Step(double now, double dt, double halfHeight, const Floor& floor, const Clear& clear)
        {
            RemoteTransform s;
            s.halfHeight = halfHeight;
            if (!placed && !PlaceRandom(halfHeight, floor)) return s;
            dt = std::clamp(dt, 0.0, 0.1);
            if (target < 0) Choose(clear);
            double vx = 0, vy = 0;
            if (target >= 0)
            {
                const auto& goal = spawns[static_cast<std::size_t>(target)];
                const double dx = goal[0] - x, dy = goal[1] - y, d = std::hypot(dx, dy);
                if (d < Arrive)
                {
                    at = target;
                    target = -1;
                }
                else
                {
                    const double fx = dx / d, fy = dy / d;
                    // Strafe left and right while walking, like a bot dodging.
                    walkedFor += dt;
                    const double w = 2 * 3.14159265358979 / StrafePeriod;
                    const double side = StrafeAmplitude * w * std::cos(walkedFor * w);
                    vx = fx * Speed - fy * side;
                    vy = fy * Speed + fx * side;
                    const double nx = x + vx * dt, ny = y + vy * dt;
                    const auto ground = floor(nx, ny, z + StepUp);
                    const double nz = ground ? *ground + halfHeight : 0;
                    // Look 50 cm ahead (about the body's radius plus a step) for a wall.
                    const double speed = std::max(1.0, std::hypot(vx, vy));
                    const bool wall = !clear(x, y, z + WaistAbove, x + vx / speed * 50, y + vy / speed * 50, z + WaistAbove);
                    if (!ground || wall || nz > z + StepUp || nz < z - StepDown)
                    {
                        vx = vy = 0;
                        if (++blocked > 3)
                        {
                            at = target; // treat as visited and try another way
                            target = -1;
                            blocked = 0;
                        }
                    }
                    else
                    {
                        blocked = 0;
                        x = nx;
                        y = ny;
                        z = nz;
                        yaw = std::atan2(fy, fx) * 180.0 / 3.14159265358979;
                    }
                }
            }
            // Crouch now and then, jump rarely (visual only: the capsule centre stays on the floor).
            if (now >= nextCrouch && crouchUntil < now)
            {
                crouchUntil = now + Uniform(0.8, 1.6);
                nextCrouch = now + Uniform(7, 12);
            }
            if (now >= nextJump && jumpStart < 0 && now >= crouchUntil)
            {
                jumpStart = now;
                nextJump = now + Uniform(12, 20);
            }
            double hop = 0, vz = 0;
            if (jumpStart >= 0)
            {
                const double u = (now - jumpStart) / JumpTime;
                if (u >= 1) jumpStart = -1;
                else
                {
                    hop = std::sin(u * 3.14159265358979) * JumpHeight;
                    vz = std::cos(u * 3.14159265358979) * JumpHeight * 3.14159265358979 / JumpTime;
                }
            }
            s.crouch = now < crouchUntil;
            s.halfHeight = s.crouch ? halfHeight * 0.6 : halfHeight;
            s.x = x;
            s.y = y;
            s.z = z + hop;
            s.vx = vx;
            s.vy = vy;
            s.vz = vz;
            s.yaw = yaw;
            return s;
        }
    };
} // namespace bridge::ghost

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
        bool grounded = false; // the current z came from a floor trace (never shown floating once true)
        double nextChoose = 0;
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
            // From the spawn point itself (inside the room, a little above the floor): a trace started
            // higher can begin inside a low ceiling, and a start-penetrating hit puts the body on the roof.
            const auto ground = floor(s[0], s[1], s[2] + 10);
            x = s[0];
            y = s[1];
            z = ground ? *ground + halfHeight : s[2];
            grounded = ground.has_value();
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

        static constexpr double SegmentProbe = 100; // cm between floor probes along a walk segment
        static constexpr double FootAbove = 30;     // the foot-height line checked for walls, above the floor

        // A straight walk from here to (gx, gy): floor under every probe, no step up over StepUp or down
        // over StepDown between probes, and nothing solid across the line at foot height.
        bool Walkable(double gx, double gy, double halfHeight, const Floor& floor, const Clear& clear) const
        {
            const double d = std::hypot(gx - x, gy - y);
            const int n = std::max(1, static_cast<int>(std::ceil(d / SegmentProbe)));
            double px = x, py = y, feet = z - halfHeight;
            for (int k = 1; k <= n; ++k)
            {
                const double u = static_cast<double>(k) / n, qx = x + (gx - x) * u, qy = y + (gy - y) * u;
                const auto f = floor(qx, qy, feet + halfHeight + StepUp);
                if (!f || *f > feet + StepUp || *f < feet - StepDown) return false;
                if (!clear(px, py, feet + FootAbove, qx, qy, *f + FootAbove)) return false;
                px = qx; py = qy; feet = *f;
            }
            return true;
        }

        // The next spawn to walk to: a random other one reachable in a straight walkable line; none
        // reachable: stay (and try again later).
        void Choose(const Clear& clear, const Floor& floor, double halfHeight)
        {
            const int n = static_cast<int>(spawns.size());
            target = -1;
            if (n < 2 || !grounded) return;
            for (int tries = 0; tries < std::min(2 * n, 24); ++tries)
            {
                const int i = static_cast<int>(Next() % static_cast<std::uint32_t>(n));
                if (i == at) continue;
                const auto& s = spawns[static_cast<std::size_t>(i)];
                if (Walkable(s[0], s[1], halfHeight, floor, clear)) { target = i; return; }
            }
        }

        // One step of `dt` seconds at time `now` (seconds). Never moves through a wall, off a
        // ledge or up a step higher than StepUp; a blocked way picks another spawn.
        RemoteTransform Step(double now, double dt, double halfHeight, const Floor& floor, const Clear& clear)
        {
            RemoteTransform s;
            s.halfHeight = halfHeight;
            if (!placed && !PlaceRandom(halfHeight, floor)) return s;
            dt = std::clamp(dt, 0.0, 0.1);
            // Not on a traced floor yet (placed without one): look again where it stands, and stand still until found.
            if (!grounded)
            {
                if (const auto ground = floor(x, y, z + StepUp); ground)
                {
                    z = *ground + halfHeight;
                    grounded = true;
                }
            }
            if (target < 0 && now >= nextChoose)
            {
                Choose(clear, floor, halfHeight);
                if (target < 0) nextChoose = now + 1.0; // nothing reachable from here: look again in a second
            }
            double vx = 0, vy = 0;
            if (target >= 0 && grounded)
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
            // A crouched body is a shorter capsule standing on the same floor: its centre is lower.
            s.z = z + hop - (s.crouch ? halfHeight - s.halfHeight : 0);
            s.vx = vx;
            s.vy = vy;
            s.vz = vz;
            s.yaw = yaw;
            return s;
        }
    };
} // namespace bridge::ghost

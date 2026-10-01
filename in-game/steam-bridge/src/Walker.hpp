#pragma once
// Developer mode's simulated player and the bots: walk the arena between the map's spawn points
// and waypoints, feet on the floor. The mod supplies the world through two line traces (floor
// below a point, wall between two points); everything else lives here so it can be tested
// without the game. Positions are capsule centres in world units (cm).
//
// Bots walk to goals across a whole map (a CS bomb site from spawn). A straight walk is rarely
// clear there, so a goal is reached by a route over the waypoints: the straight walks between
// them are checked lazily, a few per step, and remembered for the map (LinkCache, shared by every
// walker on it).

#include "GhostMath.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <functional>
#include <limits>
#include <map>
#include <memory>
#include <optional>
#include <vector>

namespace bridge::ghost
{
    // Which straight walks between two points are walkable, for one map (shared by its walkers).
    struct LinkCache
    {
        std::map<std::array<std::int64_t, 4>, bool> links; // rounded (ax, ay, bx, by) -> walkable
        std::map<std::array<std::int64_t, 2>, std::optional<double>> floors; // rounded (x, y) -> floor z at a waypoint
        static std::int64_t Key(double v) { return static_cast<std::int64_t>(std::llround(v / 10.0)); }
    };

    struct Walker
    {
        // Floor height below (x, y), searching down from z; nullopt when there is no floor.
        using Floor = std::function<std::optional<double>(double x, double y, double z)>;
        // True when nothing solid lies between the two points.
        using Clear = std::function<bool(double ax, double ay, double az, double bx, double by, double bz)>;

        // Defaults (a KovaaK's arena); Tune() matches them to the local player's movement.
        static constexpr double Speed = 320;        // cm/s, a walking bot
        static constexpr double StrafeAmplitude = 90, StrafePeriod = 2.6;
        static constexpr double StepUp = 45, StepDown = 70; // larger changes count as a wall or a ledge
        static constexpr double Arrive = 70;         // cm from a spawn counts as there
        static constexpr double WaistAbove = 10;     // wall checks at waist height above the centre
        static constexpr double JumpHeight = 50, JumpTime = 0.55;
        static constexpr int PlanBudget = 6;         // straight-walk checks per planning step
        static constexpr int PlanFanout = 10;        // neighbours tried from each waypoint

        double speed = Speed, stepUp = StepUp, stepDown = StepDown, arrive = Arrive, strafe = StrafeAmplitude;

        // Movement like the player's: run speed (the bots walk a little slower, as with a rifle) and
        // step height (stairs). A ported map is scaled up (CS: 250 u/s x 4.4 = 1100 cm/s, 18 u steps
        // = 79 cm): the defaults would leave a bot creeping and stopped by the first stair.
        void Tune(double runSpeed, double stepHeight)
        {
            const double k = std::clamp(runSpeed > 0 ? runSpeed * 0.85 / Speed : 1.0, 0.6, 4.0);
            speed = Speed * k;
            stepUp = std::clamp(stepHeight > 0 ? stepHeight : StepUp, StepUp, 160.0);
            stepDown = std::max(StepDown, stepUp * 1.6);
            arrive = Arrive * std::clamp(k, 1.0, 2.5);
            strafe = StrafeAmplitude * std::clamp(k, 1.0, 2.5);
        }

        std::vector<std::array<double, 3>> spawns;   // capsule centres (or feet) of the arena's spawns
        // A bot's waypoints are its own spawns first, then other points on the map: only the first
        // `own` are places to stand at (0: all of them).
        int own = 0;
        // Bot orders (BotOrders.hpp): walk to `goal` and stay there; `hold` still; `face` a point.
        std::optional<std::array<double, 3>> goal;
        bool hold = false;
        std::optional<std::array<double, 3>> face;
        static constexpr int GoalTarget = -2;        // `target` when walking straight to the goal
        static constexpr double EyeAbove = 64;       // a standing body's eye above its centre
        double x = 0, y = 0, z = 0, yaw = 0, pitch = 0;
        bool placed = false;
        bool grounded = false; // the current z came from a floor trace (never shown floating once true)
        double nextChoose = 0;
        int target = -1, at = -1, blocked = 0;
        double walkedFor = 0, nextCrouch = 6, crouchUntil = -1, nextJump = 14, jumpStart = -1;
        std::uint32_t seed = 0x2468ace1u;
        // Route to the goal: waypoints still to pass, then the goal (GoalTarget).
        std::vector<int> route;
        std::optional<std::array<double, 3>> routeGoal;
        std::shared_ptr<LinkCache> links;
        // Straight walks from where it stands now (not at a waypoint): only for this spot.
        std::map<int, bool> fromHere;
        double hereX = 1e30, hereY = 1e30;
        int plansPending = 0, plansFound = 0, plansFailed = 0;

        std::uint32_t Next()
        {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            return seed;
        }
        double Uniform(double lo, double hi) { return lo + (hi - lo) * (Next() % 10000) / 10000.0; }

        void ForgetRoute()
        {
            route.clear();
            routeGoal.reset();
            fromHere.clear();
            hereX = hereY = 1e30;
        }
        // The waypoint it stands at (within `arrive`), or -1.
        int NodeAt(double px, double py) const
        {
            for (int i = 0; i < static_cast<int>(spawns.size()); ++i)
                if (std::hypot(spawns[static_cast<std::size_t>(i)][0] - px, spawns[static_cast<std::size_t>(i)][1] - py) < arrive) return i;
            return -1;
        }

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
            ForgetRoute();
            return true;
        }
        bool PlaceRandom(double halfHeight, const Floor& floor)
        {
            const std::size_t n = own > 0 ? std::min(spawns.size(), static_cast<std::size_t>(own)) : spawns.size();
            return n > 0 && Place(static_cast<int>(Next() % n), halfHeight, floor);
        }
        // Stand at a point the service gives (a round's spawn, a respawn), on its floor.
        void PlaceAt(double px, double py, double pz, double facing, double halfHeight, const Floor& floor)
        {
            const auto ground = floor(px, py, pz + 10);
            x = px;
            y = py;
            z = ground ? *ground + halfHeight : pz;
            grounded = ground.has_value();
            yaw = facing;
            at = NodeAt(px, py);
            target = -1;
            blocked = 0;
            placed = true;
            nextChoose = 0;
            ForgetRoute();
        }
        std::array<double, 3> Point(int i) const { return i == GoalTarget && goal ? *goal : spawns[static_cast<std::size_t>(i)]; }

        static constexpr double SegmentProbe = 100; // cm between floor probes along a walk segment
        static constexpr double FootAbove = 30;     // the foot-height line checked for walls, above the floor

        // A straight walk from (fx, fy) with its feet at `feet` to (gx, gy): floor under every probe, no
        // step up over stepUp or down over stepDown between probes, and nothing solid across the line
        // at foot height.
        bool WalkableFrom(double fx, double fy, double feet, double gx, double gy, double halfHeight, const Floor& floor, const Clear& clear) const
        {
            const double d = std::hypot(gx - fx, gy - fy);
            const int n = std::max(1, static_cast<int>(std::ceil(d / SegmentProbe)));
            double px = fx, py = fy;
            for (int k = 1; k <= n; ++k)
            {
                const double u = static_cast<double>(k) / n, qx = fx + (gx - fx) * u, qy = fy + (gy - fy) * u;
                const auto f = floor(qx, qy, feet + halfHeight + stepUp);
                if (!f || *f > feet + stepUp || *f < feet - stepDown) return false;
                if (!clear(px, py, feet + FootAbove, qx, qy, *f + FootAbove)) return false;
                px = qx; py = qy; feet = *f;
            }
            return true;
        }
        bool Walkable(double gx, double gy, double halfHeight, const Floor& floor, const Clear& clear) const
        {
            return WalkableFrom(x, y, z - halfHeight, gx, gy, halfHeight, floor, clear);
        }

        // ---- route planning over the waypoints -------------------------------------------------
        enum class Plan { Found, None, Pending };
        LinkCache& Cache()
        {
            if (!links) links = std::make_shared<LinkCache>();
            return *links;
        }
        // A waypoint's floor (cached for the map).
        std::optional<double> NodeFloor(int i, const Floor& floor)
        {
            const auto& s = spawns[static_cast<std::size_t>(i)];
            const std::array<std::int64_t, 2> key{LinkCache::Key(s[0]), LinkCache::Key(s[1])};
            auto& floors = Cache().floors;
            if (const auto it = floors.find(key); it != floors.end()) return it->second;
            const auto f = floor(s[0], s[1], s[2] + stepUp);
            floors[key] = f;
            return f;
        }

        // A* from where it stands to `g`: nodes are the waypoints plus the goal; an edge is a walkable
        // straight line. Unknown edges are checked as the search reaches them, at most PlanBudget per
        // call; out of budget the search stops (Pending) and resumes next time with what it learnt.
        Plan PlanRoute(const std::array<double, 3>& g, double halfHeight, const Floor& floor, const Clear& clear)
        {
            const int n = static_cast<int>(spawns.size());
            const int start = n, goalNode = n + 1;
            if (std::hypot(x - hereX, y - hereY) > 1) { fromHere.clear(); hereX = x; hereY = y; }
            int budget = PlanBudget;
            const auto pos = [&](int i) -> std::array<double, 2> {
                if (i == start) return {x, y};
                if (i == goalNode) return {g[0], g[1]};
                return {spawns[static_cast<std::size_t>(i)][0], spawns[static_cast<std::size_t>(i)][1]};
            };
            const auto dist = [&](int a, int b) { const auto p = pos(a), q = pos(b); return std::hypot(p[0] - q[0], p[1] - q[1]); };
            // Feasibility of a -> b: true, false, or nullopt (out of budget this call).
            const auto link = [&](int a, int b) -> std::optional<bool> {
                if (a == start && at >= 0) a = at;
                const auto p = pos(a), q = pos(b);
                if (a == start)
                {
                    if (const auto it = fromHere.find(b); it != fromHere.end()) return it->second;
                    if (budget <= 0) return std::nullopt;
                    --budget;
                    const bool ok = Walkable(q[0], q[1], halfHeight, floor, clear);
                    fromHere[b] = ok;
                    return ok;
                }
                const std::array<std::int64_t, 4> key{LinkCache::Key(p[0]), LinkCache::Key(p[1]), LinkCache::Key(q[0]), LinkCache::Key(q[1])};
                auto& cache = Cache().links;
                if (const auto it = cache.find(key); it != cache.end()) return it->second;
                const auto feet = NodeFloor(a, floor);
                if (!feet) { cache[key] = false; return false; }
                if (budget <= 0) return std::nullopt;
                --budget;
                const bool ok = WalkableFrom(p[0], p[1], *feet, q[0], q[1], halfHeight, floor, clear);
                cache[key] = ok;
                return ok;
            };
            const double inf = std::numeric_limits<double>::infinity();
            std::vector<double> cost(static_cast<std::size_t>(n + 2), inf);
            std::vector<int> parent(static_cast<std::size_t>(n + 2), -1);
            std::vector<bool> closed(static_cast<std::size_t>(n + 2), false);
            cost[static_cast<std::size_t>(start)] = 0;
            for (;;)
            {
                int u = -1;
                double best = inf;
                for (int i = 0; i < n + 2; ++i)
                    if (!closed[static_cast<std::size_t>(i)] && cost[static_cast<std::size_t>(i)] < inf)
                    {
                        const double f = cost[static_cast<std::size_t>(i)] + dist(i, goalNode);
                        if (f < best) { best = f; u = i; }
                    }
                if (u < 0) return Plan::None;
                if (u == goalNode)
                {
                    route.clear();
                    for (int v = goalNode; v != start; v = parent[static_cast<std::size_t>(v)])
                        route.insert(route.begin(), v == goalNode ? GoalTarget : v);
                    return Plan::Found;
                }
                closed[static_cast<std::size_t>(u)] = true;
                // The most promising neighbours first (towards the goal), a few of them.
                std::vector<std::pair<double, int>> next;
                for (int v = 0; v < n + 2; ++v)
                    if (v != start && v != u && !closed[static_cast<std::size_t>(v)] && !(u == start && v == at)) next.push_back({dist(u, v) + dist(v, goalNode), v});
                std::sort(next.begin(), next.end());
                int tried = 0;
                for (const auto& [_, v] : next)
                {
                    if (tried >= PlanFanout && v != goalNode) continue;
                    const double c = cost[static_cast<std::size_t>(u)] + dist(u, v);
                    if (c >= cost[static_cast<std::size_t>(v)]) continue;
                    ++tried;
                    const auto ok = link(u, v);
                    if (!ok) return Plan::Pending;
                    if (!*ok) continue;
                    cost[static_cast<std::size_t>(v)] = c;
                    parent[static_cast<std::size_t>(v)] = u;
                }
            }
        }

        // The next point to walk to. With a goal: straight there when it can, else the next waypoint
        // of a route to it; without one (roam), a random other waypoint reachable in a straight line.
        // Returns true while a route is still being worked out (call again soon).
        bool Choose(const Clear& clear, const Floor& floor, double halfHeight)
        {
            const int n = static_cast<int>(spawns.size());
            target = -1;
            if (!grounded) return false;
            if (goal)
            {
                const auto& g = *goal;
                if (std::hypot(g[0] - x, g[1] - y) < arrive) { ForgetRoute(); return false; } // there: stay
                // The goal moved (a chase, the dropped bomb): plan again.
                if (routeGoal && std::hypot((*routeGoal)[0] - g[0], (*routeGoal)[1] - g[1]) > arrive) ForgetRoute();
                if (!route.empty()) { target = route.front(); return false; }
                if (Walkable(g[0], g[1], halfHeight, floor, clear)) { target = GoalTarget; routeGoal = g; return false; }
                switch (PlanRoute(g, halfHeight, floor, clear))
                {
                case Plan::Found:
                    ++plansFound;
                    routeGoal = g;
                    target = route.front();
                    return false;
                case Plan::Pending:
                    ++plansPending;
                    return true;
                case Plan::None:
                    ++plansFailed;
                    routeGoal = g; // planned for this goal: wander, and plan again from where that leads
                    break; // no route known: wander to any reachable point and look again from there
                }
            }
            if (n < 2) return false;
            for (int tries = 0; tries < std::min(2 * n, 24); ++tries)
            {
                const int i = static_cast<int>(Next() % static_cast<std::uint32_t>(n));
                if (i == at) continue;
                const auto& s = spawns[static_cast<std::size_t>(i)];
                if (Walkable(s[0], s[1], halfHeight, floor, clear)) { target = i; return false; }
            }
            return false;
        }

        // One step of `dt` seconds at time `now` (seconds). Never moves through a wall, off a
        // ledge or up a step higher than stepUp; a blocked way picks another spawn.
        RemoteTransform Step(double now, double dt, double halfHeight, const Floor& floor, const Clear& clear)
        {
            RemoteTransform s;
            s.halfHeight = halfHeight;
            if (!placed && !PlaceRandom(halfHeight, floor)) return s;
            dt = std::clamp(dt, 0.0, 0.1);
            // Not on a traced floor yet (placed without one): look again where it stands, and stand still until found.
            if (!grounded)
            {
                if (const auto ground = floor(x, y, z + stepUp); ground)
                {
                    z = *ground + halfHeight;
                    grounded = true;
                }
            }
            // A new goal while walking somewhere else: choose again now.
            if (goal && target != -1 && (!routeGoal || std::hypot((*routeGoal)[0] - (*goal)[0], (*routeGoal)[1] - (*goal)[1]) > arrive))
            {
                target = -1;
                nextChoose = 0;
            }
            if (!goal)
            {
                if (routeGoal) ForgetRoute();
                if (target == GoalTarget) target = -1;
            }
            if (target == -1 && now >= nextChoose && !hold)
            {
                const bool pending = Choose(clear, floor, halfHeight);
                if (target < 0) nextChoose = now + (pending ? 0.15 : 1.0); // nothing reachable from here: look again soon
            }
            double vx = 0, vy = 0;
            if (hold) target = -1;
            if (target != -1 && grounded && !hold)
            {
                const auto dest = Point(target);
                const double dx = dest[0] - x, dy = dest[1] - y, d = std::hypot(dx, dy);
                if (d < arrive)
                {
                    at = target == GoalTarget ? -1 : target;
                    if (!route.empty() && route.front() == target) route.erase(route.begin());
                    // Straight on to the next waypoint of the route, or the goal.
                    target = route.empty() ? -1 : route.front();
                }
                else
                {
                    const double fx = dx / d, fy = dy / d;
                    // Strafe left and right while walking, like a bot dodging (less near a turn).
                    walkedFor += dt;
                    const double w = 2 * 3.14159265358979 / StrafePeriod;
                    const double side = strafe * w * std::cos(walkedFor * w) * std::min(1.0, d / (arrive * 3));
                    vx = fx * speed - fy * side;
                    vy = fy * speed + fx * side;
                    const double nx = x + vx * dt, ny = y + vy * dt;
                    const auto ground = floor(nx, ny, z + stepUp);
                    const double nz = ground ? *ground + halfHeight : 0;
                    // Look about the body's radius plus a step ahead for a wall.
                    const double moving = std::max(1.0, std::hypot(vx, vy));
                    const double ahead = std::max(50.0, arrive * 0.7);
                    const bool wall = !clear(x, y, z + WaistAbove, x + vx / moving * ahead, y + vy / moving * ahead, z + WaistAbove);
                    if (!ground || wall || nz > z + stepUp || nz < z - stepDown)
                    {
                        vx = vy = 0;
                        if (++blocked > 3)
                        {
                            // The straight walk looked clear but the body can't pass: remember it and go another way.
                            if (links && at >= 0 && target >= 0)
                            {
                                const auto& p = spawns[static_cast<std::size_t>(at)];
                                const auto& q = spawns[static_cast<std::size_t>(target)];
                                links->links[{LinkCache::Key(p[0]), LinkCache::Key(p[1]), LinkCache::Key(q[0]), LinkCache::Key(q[1])}] = false;
                            }
                            at = target == GoalTarget ? -1 : target; // treat as visited and try another way
                            if (at < 0) at = NodeAt(x, y);
                            target = -1;
                            blocked = 0;
                            ForgetRoute();
                            nextChoose = now + 0.5;
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
            // Facing a point (an enemy): turn the body and the aim there.
            if (face)
            {
                const auto& f = *face;
                const double dx = f[0] - x, dy = f[1] - y, dz = f[2] - (z + EyeAbove);
                if (std::hypot(dx, dy) > 1)
                {
                    yaw = std::atan2(dy, dx) * 180.0 / 3.14159265358979;
                    pitch = std::atan2(dz, std::hypot(dx, dy)) * 180.0 / 3.14159265358979;
                }
            }
            else pitch = 0;
            if (hold) nextJump = std::max(nextJump, now + 2); // no hops while standing to shoot or plant
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
            s.pitch = pitch;
            return s;
        }
    };
} // namespace bridge::ghost

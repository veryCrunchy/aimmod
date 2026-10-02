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
#include "NavGrid.hpp"

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
        static constexpr double WaistAbove = 0;      // wall checks at the centre: the height the nav grid checks
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
        // The map's nav grid (shared) and the path along it: floor points, then the goal.
        static constexpr int NavTarget = -3;         // `target` while following navPath
        std::shared_ptr<NavGrid> nav;
        std::vector<std::array<double, 3>> navPath;
        std::size_t navIndex = 0;
        bool navReached = false;
        double navPlannedAt = -1, clockNow = 0;
        int navBlocked = 0, stuckRecoveries = 0;
        double NavArrive() const { return nav ? std::min(arrive, nav->spacing * 0.3) : arrive; }
        std::vector<int> navNodes;                   // grid node of each path point (-1: the goal itself)
        std::array<double, 3> legFrom{};             // where the current path leg starts
        bool navEndsShort = false;                   // the path stops short of the goal (stop, via)
        double exactUntil = -1;                      // grid steps only (no string-pulling) until then
        // Orders beyond walk-and-stay: walk only `goalStop` of the way (a default or post-plant spot);
        // first a detour `via` (x, y, z, fraction of the way to it), then the goal.
        double goalStop = 1;
        std::optional<std::array<double, 4>> via;
        bool viaDone = false;
        std::optional<std::array<double, 3>> stoppedFor;
        double routeStop = 1;
        std::optional<std::array<double, 4>> routeVia;
        // Other bodies to keep clear of (x, y), set before each step.
        std::vector<std::array<double, 2>> others;
        static constexpr double Separation = 130, TrustDistance = 60, StrafeRange = 180;
        // Turning: degrees per second (by difficulty); a fight's strafe (0 none .. 1 full).
        double turnRate = 540, fight = 0;
        double wantYaw = 0, lookYaw = 0, nextLook = 0, strafeFlipAt = 0, strafeSign = 1;
        std::optional<std::array<double, 2>> holdAnchor;
        double progressX = 0, progressY = 0, progressAt = 0;
        int stuckLevel = 0;
        double stuckX = 1e30, stuckY = 1e30;

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
            navPath.clear();
            navIndex = 0;
            navReached = false;
            if (target == NavTarget) target = -1;
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
            yaw = wantYaw = lookYaw = facing;
            at = NodeAt(px, py);
            target = -1;
            blocked = 0;
            placed = true;
            nextChoose = 0;
            ForgetRoute();
        }
        std::array<double, 3> Point(int i) const
        {
            if (i == NavTarget) return navPath[std::min(navIndex, navPath.size() - 1)];
            return i == GoalTarget && goal ? *goal : spawns[static_cast<std::size_t>(i)];
        }

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
                // The map's nav grid (NavGrid) when there is one: the way there over the floor.
                if (nav && !nav->nodes.empty())
                {
                    if (!navPath.empty() && navIndex < navPath.size()) { target = NavTarget; return false; }
                    // Stopping short (a default position, a post-plant spot): there already.
                    if (stoppedFor && std::hypot((*stoppedFor)[0] - g[0], (*stoppedFor)[1] - g[1]) < arrive) return false;
                    // A detour first (a split), then the goal; walk `stop` of the way only.
                    const bool viaLeg = via && !viaDone;
                    const std::array<double, 3> dest = viaLeg ? std::array<double, 3>{(*via)[0], (*via)[1], (*via)[2]} : g;
                    const double fraction = viaLeg ? (*via)[3] : goalStop;
                    bool reached = false;
                    auto ids = nav->PathNodes({x, y, z}, dest, reached);
                    if (!ids.empty())
                    {
                        const bool shortened = reached && fraction < 0.999;
                        if (shortened) ids.resize(std::max<std::size_t>(1, static_cast<std::size_t>(std::ceil(ids.size() * std::clamp(fraction, 0.0, 1.0)))));
                        if (clockNow >= exactUntil) ids = nav->Smooth(ids);
                        navNodes = ids;
                        navPath.clear();
                        for (const int id : ids) navPath.push_back({nav->nodes[static_cast<std::size_t>(id)].x, nav->nodes[static_cast<std::size_t>(id)].y, nav->nodes[static_cast<std::size_t>(id)].z});
                        if (reached && !shortened && !viaLeg) { navPath.push_back(g); navNodes.push_back(-1); }
                        navIndex = 0;
                        while (navIndex + 1 < navPath.size() && std::hypot(navPath[navIndex][0] - x, navPath[navIndex][1] - y) < NavArrive()) ++navIndex;
                        legFrom = {x, y, z};
                        navReached = reached;
                        navEndsShort = shortened || viaLeg;
                        navPlannedAt = clockNow;
                        routeGoal = g;
                        reached ? ++plansFound : ++plansPending;
                        target = NavTarget;
                        return false;
                    }
                    if (!nav->Done()) { ++plansPending; return true; } // the grid hasn't reached here yet
                }
                if (std::hypot(g[0] - x, g[1] - y) < 4000 && Walkable(g[0], g[1], halfHeight, floor, clear)) { target = GoalTarget; routeGoal = g; return false; }
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
            // A few tries only: each is a straight walk checked by traces along its whole length.
            for (int tries = 0; tries < std::min(n, 6); ++tries)
            {
                const int i = static_cast<int>(Next() % static_cast<std::uint32_t>(n));
                if (i == at) continue;
                const auto& s = spawns[static_cast<std::size_t>(i)];
                if (Walkable(s[0], s[1], halfHeight, floor, clear)) { target = i; return false; }
            }
            return false;
        }

        // Stuck recovery: no progress for StuckSeconds while walking. Each time it happens again
        // the next step is tried: 1 hop and back off to the last point, 2 take the link it can't pass
        // out of the grid and plan again without shortcuts, 3 the same for the grid point beyond it,
        // 4 hop over to the next point (or onto the nearest grid point). The ladder starts over only
        // once it is well past the spot.
        static constexpr double StuckSeconds = 1.5, StuckDistance = 40;
        void Recover(double now, double halfHeight)
        {
            ++stuckRecoveries;
            const int level = stuckLevel++ % 4;
            if (level == 0)
            {
                jumpStart = now; // a hop over whatever the feet caught on
                if (target == NavTarget && navIndex > 0) --navIndex;
            }
            else if (level == 1 || level == 2)
            {
                // First the link it can't pass, then the grid point beyond it, out of the grid.
                if (nav && target == NavTarget && navIndex > 0 && navIndex < navNodes.size() && navNodes[navIndex - 1] >= 0 && navNodes[navIndex] >= 0 &&
                    nav->Dir(navNodes[navIndex - 1], navNodes[navIndex]) >= 0)
                {
                    if (level == 1) nav->Block(navNodes[navIndex - 1], navNodes[navIndex]);
                    else nav->BlockNode(navNodes[navIndex]);
                }
                exactUntil = now + 10; // grid steps only for a while (no shortcut past the snag)
                ForgetRoute();
                target = -1;
                nextChoose = 0;
            }
            else
            {
                // Hop over to the next point of the path when it is close (the grid says the floor goes on
                // there; whatever the traces disagree about is jumped), else onto the nearest grid point.
                if (target == NavTarget && navIndex < navPath.size() && navIndex < navNodes.size() && navNodes[navIndex] >= 0 && std::hypot(navPath[navIndex][0] - x, navPath[navIndex][1] - y) < (nav ? nav->spacing * 2.5 : 0))
                {
                    x = navPath[navIndex][0];
                    y = navPath[navIndex][1];
                    z = navPath[navIndex][2] + halfHeight;
                    legFrom = navPath[navIndex];
                    ++navIndex;
                    jumpStart = now;
                    return;
                }
                if (nav)
                    if (const int n = nav->Nearest(x, y, z); n >= 0)
                    {
                        const auto& p = nav->nodes[static_cast<std::size_t>(n)];
                        x = p.x;
                        y = p.y;
                        z = p.z + halfHeight;
                        jumpStart = now;
                    }
                ForgetRoute();
                target = -1;
                nextChoose = 0;
            }
        }

        // Distance from (px, py) to the segment a-b.
        static double SegmentDistance(double px, double py, const std::array<double, 3>& a, const std::array<double, 3>& b, double& u)
        {
            const double dx = b[0] - a[0], dy = b[1] - a[1], len = dx * dx + dy * dy;
            u = len > 1e-9 ? std::clamp(((px - a[0]) * dx + (py - a[1]) * dy) / len, 0.0, 1.0) : 1.0;
            return std::hypot(a[0] + dx * u - px, a[1] + dy * u - py);
        }

        static double Approach(double from, double to, double maxStep)
        {
            const double d = WrapAngle(to - from);
            return from + std::clamp(d, -maxStep, maxStep);
        }

        // Moves by (vx, vy) for dt if the floor and walls allow; `trust` (on a grid link) walks on
        // where the step checks are stricter than the grid's. Returns whether it moved.
        bool Move(double vx, double vy, double dt, double halfHeight, const Floor& floor, const Clear& clear, bool trust, double trustZ)
        {
            const double nx = x + vx * dt, ny = y + vy * dt;
            const auto ground = floor(nx, ny, z + stepUp);
            const double nz = ground ? *ground + halfHeight : 0;
            const double moving = std::max(1.0, std::hypot(vx, vy));
            const double ahead = std::max(50.0, std::min(arrive * 0.7, moving * 0.12));
            // The look ahead follows the slope under the feet (stairs, ramps), so the steps ahead
            // don't read as a wall.
            const double stepLen = std::max(1e-6, std::hypot(nx - x, ny - y));
            const double slope = ground ? std::clamp((nz - z) / stepLen, -1.5, 1.5) : 0;
            const bool wall = !clear(x, y, z + WaistAbove, x + vx / moving * ahead, y + vy / moving * ahead, z + WaistAbove + slope * ahead);
            if (ground && !wall && nz <= z + stepUp && nz >= z - stepDown)
            {
                x = nx;
                y = ny;
                z = nz;
                return true;
            }
            // On a grid link only the floor is trusted (a trace that began in a low ceiling, a step the
            // probe read differently); a wall in the way is never walked through.
            if (!trust || wall) return false;
            x = nx;
            y = ny;
            z = ground && nz <= z + stepUp * 1.5 && nz >= z - stepDown * 1.5 ? nz : trustZ;
            return true;
        }

        // One step of `dt` seconds at time `now` (seconds). Never moves through a wall, off a
        // ledge or up a step higher than stepUp (on a grid link it trusts the grid); turns no faster
        // than turnRate, keeps clear of other bodies, looks around while holding and strafes in a fight.
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
            // A new goal (or a new way to it) while walking somewhere else: choose again now.
            const bool planChanged = routeStop != goalStop || routeVia != via;
            if (planChanged)
            {
                routeStop = goalStop;
                routeVia = via;
                viaDone = false;
                stoppedFor.reset();
                ForgetRoute();
                nextChoose = 0;
            }
            if (goal && target != -1 && (!routeGoal || std::hypot((*routeGoal)[0] - (*goal)[0], (*routeGoal)[1] - (*goal)[1]) > arrive))
            {
                ForgetRoute();
                target = -1;
                nextChoose = 0;
            }
            if (goal && stoppedFor && std::hypot((*stoppedFor)[0] - (*goal)[0], (*stoppedFor)[1] - (*goal)[1]) > arrive) { stoppedFor.reset(); viaDone = false; }
            if (!goal)
            {
                if (routeGoal) ForgetRoute();
                if (target == GoalTarget) target = -1;
                stoppedFor.reset();
                viaDone = false;
            }
            clockNow = now;
            // A path that ended short of the goal (the grid was still growing): look again now and then.
            if (target == NavTarget && !navReached && now - navPlannedAt > 2.0)
            {
                navPath.clear();
                target = -1;
                nextChoose = 0;
            }
            if (target == -1 && now >= nextChoose && !hold)
            {
                const bool pending = Choose(clear, floor, halfHeight);
                if (target < 0) nextChoose = now + (pending ? 0.15 : 1.0); // nothing reachable from here: look again soon
            }
            double vx = 0, vy = 0;
            if (hold && target != -1) { target = -1; }
            if (!hold) holdAnchor.reset();
            bool walking = false;
            if (target != -1 && grounded && !hold)
            {
                const auto dest = Point(target);
                const double dx = dest[0] - x, dy = dest[1] - y, d = std::hypot(dx, dy);
                if (target == NavTarget && d < NavArrive())
                {
                    legFrom = navPath[navIndex];
                    ++navIndex;
                    if (navIndex >= navPath.size())
                    {
                        navPath.clear();
                        navNodes.clear();
                        target = -1;
                        at = NodeAt(x, y);
                        if (via && !viaDone) { viaDone = true; nextChoose = 0; }  // the detour done: on to the goal
                        else if (navEndsShort && goal) stoppedFor = *goal;          // as far as it was told to go
                    }
                }
                else if (target != NavTarget && d < arrive)
                {
                    at = target == GoalTarget ? -1 : target;
                    if (!route.empty() && route.front() == target) route.erase(route.begin());
                    // Straight on to the next waypoint of the route, or the goal.
                    target = route.empty() ? -1 : route.front();
                }
                else
                {
                    const double fx = dx / d, fy = dy / d;
                    // Strafe left and right while roaming, like a bot dodging; never along a nav path.
                    walkedFor += dt;
                    const double w = 2 * 3.14159265358979 / StrafePeriod;
                    const double side = target == NavTarget ? 0 : strafe * w * std::cos(walkedFor * w) * std::min(1.0, d / (arrive * 3));
                    vx = fx * speed - fy * side;
                    vy = fy * speed + fx * side;
                    // Keep clear of other bodies: slow down behind one ahead (single file through a
                    // door), step aside from one alongside; never walk into them.
                    double pace = 1;
                    for (const auto& o : others)
                    {
                        const double ox = x - o[0], oy = y - o[1], od = std::hypot(ox, oy);
                        if (od >= Separation || od < 1) continue;
                        const double aheadOf = -(ox * fx + oy * fy) / od; // 1: right in front
                        if (aheadOf > 0.3) pace = std::min(pace, std::clamp((od - Separation * 0.55) / (Separation * 0.45), 0.0, 1.0));
                        const double push = (Separation - od) / Separation * speed * 0.6;
                        const double across = (ox * -fy + oy * fx) / od;
                        const double sign = across >= 0 ? 1 : -1;
                        vx += -fy * sign * push;
                        vy += fx * sign * push;
                    }
                    vx -= fx * speed * (1 - pace);
                    vy -= fy * speed * (1 - pace);
                    // At the goal with someone already standing there: stop alongside instead.
                    if (target == NavTarget && navIndex + 1 >= navPath.size() && d < Separation * 2)
                        for (const auto& o : others)
                            if (std::hypot(o[0] - dest[0], o[1] - dest[1]) < Separation * 0.8 && std::hypot(x - o[0], y - o[1]) < Separation * 1.3)
                            {
                                navPath.clear();
                                navNodes.clear();
                                target = -1;
                                if (goal) stoppedFor = *goal;
                                break;
                            }
                    if (target == -1) { vx = vy = 0; }
                    // On a grid link (near the segment from the last point to this one) the grid's checks hold.
                    // Only a single grid step is trusted: a string-pulled leg is checked like any walk.
                    double u = 1;
                    const bool gridStep = nav && navIndex > 0 && navIndex < navNodes.size() && navNodes[navIndex - 1] >= 0 && navNodes[navIndex] >= 0
                                              ? nav->Dir(navNodes[navIndex - 1], navNodes[navIndex]) >= 0
                                              : std::hypot(dest[0] - legFrom[0], dest[1] - legFrom[1]) <= (nav ? nav->spacing * 1.5 : 0);
                    const bool onLink = target == NavTarget && gridStep && SegmentDistance(x, y, legFrom, dest, u) < TrustDistance;
                    const double linkZ = legFrom[2] + (dest[2] - legFrom[2]) * u + halfHeight;
                    if (Move(vx, vy, dt, halfHeight, floor, clear, onLink, linkZ))
                    {
                        blocked = 0;
                        walking = true;
                        wantYaw = std::atan2(fy, fx) * 180.0 / 3.14159265358979; // face where it walks
                    }
                    else
                    {
                        vx = vy = 0;
                        if (++blocked > 3 && target != NavTarget)
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
                        else if (target == NavTarget)
                        {
                            ++navBlocked;
                            // A shortcut the body can't take: the same way again, grid step by grid step.
                            if (!gridStep && blocked > 2)
                            {
                                exactUntil = now + 10;
                                ForgetRoute();
                                target = -1;
                                nextChoose = 0;
                                blocked = 0;
                            }
                        }
                    }
                }
            }
            // Stuck: walking somewhere without getting anywhere.
            if (target != -1 && !hold)
            {
                // Progress resets the recovery ladder only once well past the spot it got stuck at.
                if (std::hypot(x - progressX, y - progressY) > StuckDistance) { progressX = x; progressY = y; progressAt = now; if (std::hypot(x - stuckX, y - stuckY) > 250) stuckLevel = 0; }
                else if (now - progressAt > StuckSeconds) { stuckX = x; stuckY = y; Recover(now, halfHeight); progressAt = now; progressX = x; progressY = y; }
            }
            else { progressX = x; progressY = y; progressAt = now; }

            // Holding: a fight strafes (ADAD across the line to the enemy, near where it stood); otherwise
            // it checks the open angles around it, a few seconds each.
            if (hold && grounded)
            {
                if (!holdAnchor) holdAnchor = std::array<double, 2>{x, y};
                if (face && fight > 0)
                {
                    const auto& f = *face;
                    const double fdx = f[0] - x, fdy = f[1] - y, fd = std::max(1.0, std::hypot(fdx, fdy));
                    if (now >= strafeFlipAt) { strafeSign = -strafeSign; strafeFlipAt = now + Uniform(0.35, 0.9); }
                    const double sx = -fdy / fd * strafeSign, sy = fdx / fd * strafeSign, sp = speed * 0.55 * std::clamp(fight, 0.0, 1.0);
                    if (std::hypot(x + sx * sp * dt - (*holdAnchor)[0], y + sy * sp * dt - (*holdAnchor)[1]) > StrafeRange || !Move(sx * sp, sy * sp, dt, halfHeight, floor, clear, false, z))
                        strafeSign = -strafeSign;
                    else { vx = sx * sp; vy = sy * sp; }
                }
                else if (!face && now >= nextLook)
                {
                    nextLook = now + Uniform(1.5, 3.5);
                    lookYaw = LookAround();
                }
            }
            // Standing (holding, or there) too close to another body: a step aside.
            if (!walking && grounded)
                for (const auto& o : others)
                {
                    const double ox = x - o[0], oy = y - o[1], od = std::hypot(ox, oy);
                    if (od < Separation * 0.8 && od > 1 && Move(ox / od * 200, oy / od * 200, dt, halfHeight, floor, clear, false, z)) break;
                }
            // Standing still and looking around, also once there.
            if (!hold && !walking && target == -1 && !face && now >= nextLook)
            {
                nextLook = now + Uniform(1.5, 3.5);
                lookYaw = LookAround();
            }
            // Crouch now and then, jump rarely (visual only: the capsule centre stays on the floor).
            if (!hold && now >= nextCrouch && crouchUntil < now)
            {
                crouchUntil = now + Uniform(0.8, 1.6);
                nextCrouch = now + Uniform(7, 12);
            }
            // Facing a point (an enemy): turn the body and the aim there; else where it walks or looks.
            double wantPitch = 0;
            if (face)
            {
                const auto& f = *face;
                const double dx = f[0] - x, dy = f[1] - y, dz = f[2] - (z + EyeAbove);
                if (std::hypot(dx, dy) > 1)
                {
                    wantYaw = std::atan2(dy, dx) * 180.0 / 3.14159265358979;
                    wantPitch = std::atan2(dz, std::hypot(dx, dy)) * 180.0 / 3.14159265358979;
                }
            }
            else if (!walking) wantYaw = lookYaw;
            // Human turning: no faster than turnRate (degrees per second).
            yaw = WrapAngle(Approach(yaw, wantYaw, turnRate * dt));
            pitch = pitch + std::clamp(wantPitch - pitch, -turnRate * dt, turnRate * dt);
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

        // Holding without an enemy: a look along one of the most open ways from here (the grid's
        // straight runs; a corner or a long angle), a little off its line.
        double LookAround()
        {
            static constexpr double Angles[4]{0, 180, 90, -90}; // NavGrid directions +x, -x, +y, -y
            if (nav && !nav->nodes.empty())
                if (const int n = nav->Nearest(x, y, z); n >= 0)
                {
                    int open[4]{}, total = 0;
                    for (int d = 0; d < 4; ++d) total += open[d] = nav->Openness(n, d) * nav->Openness(n, d);
                    if (total > 0)
                    {
                        int pick = static_cast<int>(Next() % static_cast<std::uint32_t>(total));
                        for (int d = 0; d < 4; ++d)
                            if ((pick -= open[d]) < 0) return Angles[d] + Uniform(-25, 25);
                    }
                }
            return yaw + Uniform(-70, 70);
        }
    };
} // namespace bridge::ghost

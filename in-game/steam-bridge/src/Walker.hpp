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
//
// They move like players: the body speeds up and slows down instead of starting and stopping dead,
// curves through the corners of its path (it steers for a point a little ahead on it), slows for
// sharp corners and doorways, and turns with an eased, slightly overshooting flick of the view
// instead of at a constant rate. In a fight they counter-strafe (strafe, a dead stop to shoot, the
// other way) or strafe side to side; holding an angle they can jiggle, wide or crouch peek it.

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
        static constexpr double Pi = 3.14159265358979;

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
        // Narrow passages along the path (a doorway, a gap between props: the floor a cell or two
        // wide): no corner near them is cut, and it slows down going in and out.
        std::vector<bool> navExact;                  // per path point: steer to it exactly
        std::vector<std::array<double, 2>> navNarrow, navNarrowEnds;
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

        // How it walks (the `move` order): run, shift-walk (silent, 52% of the run speed as in CS) or
        // crouch-walk (34%, the body crouched); `preaim` looks round the next corner of its path.
        static constexpr double WalkFactor = 0.52, CrouchFactor = 0.34;
        bool walkGait = false, crouchStance = false, preaim = false;
        double GaitSpeed() const { return speed * (crouchStance ? CrouchFactor : walkGait ? WalkFactor : 1.0); }
        // The body's velocity (cm/s): it reaches full speed in AccelTime, stops in DecelTime, and
        // a counter-strafe stops it dead in CounterTime.
        static constexpr double AccelTime = 0.2, DecelTime = 0.15, CounterTime = 0.08;
        double velX = 0, velY = 0;
        // Eased turning (the `aim` order): the view's angular velocity changes no faster than aimAccel
        // (deg/s^2; 0: AccelPerRate x turnRate) up to turnRate, and overshoots the target by about
        // `overshoot` of the turn (of its last part, on a long one) before it settles.
        static constexpr double AccelPerRate = 6, Overshoot = 0.05;
        double aimAccel = 0, overshoot = Overshoot;
        double yawVel = 0, pitchVel = 0;
        double TurnAccel() const { return aimAccel > 0 ? aimAccel : AccelPerRate * turnRate; }
        // Peeking an angle while holding (the `peek` order): from where it holds (the anchor) out to one
        // side of the point and back quickly (jiggle), out wide and stay (wide), or a step out and down.
        enum class Peek { None, Jiggle, Wide, Crouch };
        Peek peek = Peek::None;
        std::array<double, 3> peekAt{};
        static constexpr double JiggleOut = 60, JiggleFar = 90, WideOut = 250, CrouchOut = 100, PeekTest = 120;
        std::optional<std::array<double, 3>> peekFor; // the point the side was chosen for
        std::array<double, 2> peekSide{};
        double peekFlipAt = 0, peekReach = 0;
        bool peekOut = false, peekCrouched = false;
        // A fight's style: counter-strafing (strafe 0.2-0.45 s, a dead stop, still 0.25-0.6 s to shoot,
        // the other way) or plain side to side.
        bool counterStrafe = false;
        int strafePhase = 0; // counter-strafing: 0 strafing, 1 stopped
        double phaseUntil = 0;
        // Zones to keep out of (x, y, z, radius, cost per grid step: a smoke, a fire), and the ones the
        // current path was planned around. Waypoint routes (no grid) ignore them.
        NavGrid::Avoid avoid, planAvoid;
        double avoidCheckAt = 0;
        // Where it looks this step (a face order, the peeked point, the corner it pre-aims), for the debug overlay.
        std::optional<std::array<double, 3>> lookAt;

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
            navExact.clear();
            navNarrow.clear();
            navNarrowEnds.clear();
            if (target == NavTarget) target = -1;
        }
        void Halt()
        {
            velX = velY = 0;
            yawVel = pitchVel = 0;
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
            Halt();
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
            Halt();
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

        // The narrow passages along a planned grid path (before string-pulling, so every cell it
        // walks is seen), and which points of the walked path lie near one.
        void MarkNarrow(const std::vector<int>& raw)
        {
            navNarrow.clear();
            navNarrowEnds.clear();
            bool inside = false;
            const auto at2 = [&](int id) { const auto& p = nav->nodes[static_cast<std::size_t>(id)]; return std::array<double, 2>{p.x, p.y}; };
            for (std::size_t k = 1; k + 1 < raw.size(); ++k)
            {
                const bool narrow = nav->Narrow(raw[k - 1], raw[k], raw[k + 1]);
                if (narrow) navNarrow.push_back(at2(raw[k]));
                if (narrow != inside) navNarrowEnds.push_back(at2(narrow ? raw[k] : raw[k - 1]));
                inside = narrow;
            }
            if (inside) navNarrowEnds.push_back(at2(raw[raw.size() - 2]));
            navExact.assign(navPath.size(), false);
            const double within = nav->spacing * 1.5;
            for (std::size_t i = 0; i < navPath.size(); ++i)
                navExact[i] = Near(navNarrow, navPath[i][0], navPath[i][1], within);
        }
        bool Exact(std::size_t i) const { return i < navExact.size() && navExact[i]; }
        static bool Near(const std::vector<std::array<double, 2>>& points, double px, double py, double within)
        {
            for (const auto& p : points)
                if (std::hypot(p[0] - px, p[1] - py) < within) return true;
            return false;
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
                    planAvoid = avoid;
                    auto ids = nav->PathNodes({x, y, z}, dest, reached, planAvoid);
                    if (!ids.empty())
                    {
                        const bool shortened = reached && fraction < 0.999;
                        if (shortened) ids.resize(std::max<std::size_t>(1, static_cast<std::size_t>(std::ceil(ids.size() * std::clamp(fraction, 0.0, 1.0)))));
                        const std::vector<int> raw = ids;
                        if (clockNow >= exactUntil) ids = nav->Smooth(ids, planAvoid);
                        navNodes = ids;
                        navPath.clear();
                        for (const int id : ids) navPath.push_back({nav->nodes[static_cast<std::size_t>(id)].x, nav->nodes[static_cast<std::size_t>(id)].y, nav->nodes[static_cast<std::size_t>(id)].z});
                        if (reached && !shortened && !viaLeg) { navPath.push_back(g); navNodes.push_back(-1); }
                        MarkNarrow(raw);
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

        // The zones to keep out of changed enough to plan again (one appeared or went, moved or grew
        // by over half a metre, or costs something else).
        bool AvoidChanged() const
        {
            if (avoid.size() != planAvoid.size()) return true;
            for (std::size_t i = 0; i < avoid.size(); ++i)
            {
                const auto& a = avoid[i];
                const auto& b = planAvoid[i];
                if (std::hypot(std::hypot(a[0] - b[0], a[1] - b[1]), a[2] - b[2]) > 50 || std::fabs(a[3] - b[3]) > 50 || std::fabs(a[4] - b[4]) > 1e-6) return true;
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
                if (target == NavTarget && navIndex > 0)
                {
                    --navIndex;
                    legFrom = navIndex > 0 ? navPath[navIndex - 1] : std::array<double, 3>{x, y, z - halfHeight};
                }
            }
            else if (level == 1 || level == 2)
            {
                // First the link it can't pass, then the grid point beyond it, out of the grid.
                if (nav && target == NavTarget && navIndex > 0 && navIndex < navNodes.size() && navNodes[navIndex - 1] >= 0 && navNodes[navIndex] >= 0)
                {
                    const int a = navNodes[navIndex - 1], b = navNodes[navIndex];
                    if (nav->Adjacent(a, b))
                    {
                        if (level == 1) nav->Block(a, b);
                        else nav->BlockNode(b);
                    }
                }
                exactUntil = now + 10; // grid steps only for a while (no shortcut past the snag)
                ForgetRoute();
                target = -1;
                nextChoose = 0;
            }
            else
            {
                velX = velY = 0;
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

        // One step of eased turning from `angle` toward `goal` (degrees; `wrap`: a yaw, the short way
        // round). The angular velocity `vel` heads for the fastest speed that can still stop in time,
        // a little faster (overshoot), at most `rate`, and changes by no more than accel x dt a step:
        // the view speeds up, flicks across, overshoots a touch and settles. Never a snap.
        static double Ease(double angle, double goal, double& vel, double rate, double accel, double over, double dt, bool wrap)
        {
            if (dt <= 0) return angle;
            const double err = wrap ? WrapAngle(goal - angle) : goal - angle;
            if (std::fabs(err) < 0.05 && std::fabs(vel) <= accel * dt) { vel = 0; return angle; } // settled
            // The fastest it can go and still stop in time, stepping dt at a time (v^2 / 2a + v dt / 2 = err).
            const double h = accel * dt / 2, stop = std::sqrt(h * h + 2 * accel * std::fabs(err)) - h;
            double want = std::min({rate, stop * (1 + over), std::fabs(err) / dt * (1 + over)});
            if (err < 0) want = -want;
            vel += std::clamp(want - vel, -accel * dt, accel * dt);
            const double next = angle + vel * dt;
            return wrap ? WrapAngle(next) : next;
        }

        // The body's velocity toward `want` no faster than a body can change it: speeding up or curving
        // at `up`, slowing down or reversing at `down` (cm/s per second).
        static void Accelerate(double& vx, double& vy, double wx, double wy, double up, double down, double dt)
        {
            const double dvx = wx - vx, dvy = wy - vy, dv = std::hypot(dvx, dvy);
            if (dv < 1e-9 || dt <= 0) return;
            const bool slowing = std::hypot(wx, wy) < std::hypot(vx, vy) - 1e-6 || vx * wx + vy * wy < 0;
            const double step = std::min(dv, (slowing ? down : up) * dt);
            vx += dvx / dv * step;
            vy += dvy / dv * step;
        }

        // Moves by (vx, vy) for dt if the floor and walls allow; `trust` (on a grid link) walks on
        // where the step checks are stricter than the grid's. The wall check looks a little ahead,
        // never further than `reach` (the point it walks to: a wall just past it is no obstacle).
        // Returns whether it moved.
        bool Move(double vx, double vy, double dt, double halfHeight, const Floor& floor, const Clear& clear, bool trust, double trustZ, double reach = 1e9)
        {
            const double nx = x + vx * dt, ny = y + vy * dt;
            const auto ground = floor(nx, ny, z + stepUp);
            const double nz = ground ? *ground + halfHeight : 0;
            const double moving = std::max(1.0, std::hypot(vx, vy));
            const double ahead = std::min(std::max(50.0, std::min(arrive * 0.7, moving * 0.12)), std::max({25.0, moving * dt * 1.5, reach}));
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

        // How far ahead on its path it steers: a grid step, or 0.15 s at its speed.
        double Lookahead() const { return std::max(nav ? nav->spacing : 120.0, std::hypot(velX, velY) * 0.15); }

        // Along the path: on to the next point once there, or once it is round a corner it cuts (nearer
        // the next leg than this one; never at a narrow passage). The end of the path ends the walk.
        void AdvancePath()
        {
            for (int guard = 0; guard < 64 && target == NavTarget && navIndex < navPath.size(); ++guard)
            {
                const auto& end = navPath[navIndex];
                const double dEnd = std::hypot(end[0] - x, end[1] - y);
                bool next = dEnd < NavArrive();
                if (!next && navIndex + 1 < navPath.size() && !Exact(navIndex))
                {
                    double u1 = 0, u2 = 0;
                    const double onThis = SegmentDistance(x, y, legFrom, end, u1), onNext = SegmentDistance(x, y, end, navPath[navIndex + 1], u2);
                    next = onNext < onThis && dEnd < Lookahead() * 1.5;
                }
                if (!next) return;
                legFrom = navPath[navIndex];
                ++navIndex;
                if (navIndex >= navPath.size())
                {
                    navPath.clear();
                    navNodes.clear();
                    navExact.clear();
                    target = -1;
                    at = NodeAt(x, y);
                    if (via && !viaDone) { viaDone = true; nextChoose = 0; }  // the detour done: on to the goal
                    else if (navEndsShort && goal) stoppedFor = *goal;          // as far as it was told to go
                    return;
                }
            }
        }

        // Pure pursuit along the path: the way to steer (hx, hy) is toward the point Lookahead() on
        // from the nearest point of this leg (no further than a narrow passage's point, close to one only
        // a little ahead); `desired` slows down for a sharp corner ahead, a narrow passage and the path's
        // end; `reach` is how far the steering point is.
        void Steer(double cruise, double& hx, double& hy, double& desired, double& reach) const
        {
            const auto& end = navPath[navIndex];
            const double spacing = nav ? nav->spacing : 120.0;
            const bool narrow = Near(navNarrow, x, y, spacing * 1.5);
            const double pace = Near(navNarrowEnds, x, y, spacing * 1.5) ? 0.7 : 1.0;
            double look = narrow ? spacing * 0.25 : Lookahead();
            double u = 0;
            SegmentDistance(x, y, legFrom, end, u);
            double cx = legFrom[0] + (end[0] - legFrom[0]) * u, cy = legFrom[1] + (end[1] - legFrom[1]) * u;
            for (std::size_t i = navIndex; i < navPath.size(); ++i)
            {
                const double l = std::hypot(navPath[i][0] - cx, navPath[i][1] - cy);
                if (l >= look) { cx += (navPath[i][0] - cx) / l * look; cy += (navPath[i][1] - cy) / l * look; break; }
                look -= l;
                cx = navPath[i][0];
                cy = navPath[i][1];
                if (Exact(i)) break;
            }
            double dx = cx - x, dy = cy - y, d = std::hypot(dx, dy);
            if (d < 1)
            {
                dx = end[0] - x;
                dy = end[1] - y;
                d = std::max(1e-6, std::hypot(dx, dy));
            }
            hx = dx / d;
            hy = dy / d;
            reach = d;
            desired = cruise * pace;
            const double brake = speed / DecelTime * 0.5;
            const double dEnd = std::hypot(end[0] - x, end[1] - y);
            if (navIndex + 1 < navPath.size())
            {
                // Into a corner no faster than it can take it: slower the sharper it turns.
                const auto& next = navPath[navIndex + 1];
                const double ax = end[0] - legFrom[0], ay = end[1] - legFrom[1], bx = next[0] - end[0], by = next[1] - end[1];
                const double la = std::hypot(ax, ay), lb = std::hypot(bx, by);
                if (la > 1e-6 && lb > 1e-6)
                {
                    const double turn = std::acos(std::clamp((ax * bx + ay * by) / (la * lb), -1.0, 1.0)) * 180.0 / Pi;
                    const double corner = cruise * std::clamp(1 - turn / 180.0 * 1.1, 0.4, 1.0) * (Exact(navIndex) ? 0.7 : 1.0);
                    desired = std::min(desired, std::sqrt(corner * corner + 2 * brake * dEnd));
                }
            }
            else desired = std::min(desired, std::sqrt(2 * brake * std::max(0.0, dEnd - NavArrive() * 0.5)) + 60); // the end of the path: slow to a stop
        }

        // Holding with the peek order: out to one side of the peeked point and back (jiggle), out wide
        // (wide) or a step out and down (crouch), from where it holds. The side is the one from which
        // the point can be seen (else the more open one); the velocity it wants is (wx, wy).
        void PeekStep(double now, const Clear& clear, double& wx, double& wy)
        {
            const auto& anchor = *holdAnchor;
            if (!peekFor || std::hypot((*peekFor)[0] - peekAt[0], (*peekFor)[1] - peekAt[1]) > 50)
            {
                peekFor = peekAt;
                peekOut = false;
                peekFlipAt = now;
                const double dx = peekAt[0] - anchor[0], dy = peekAt[1] - anchor[1], d = std::max(1.0, std::hypot(dx, dy));
                const double sx = -dy / d, sy = dx / d;
                const double eye = z + EyeAbove;
                const bool left = clear(anchor[0] + sx * PeekTest, anchor[1] + sy * PeekTest, eye, peekAt[0], peekAt[1], peekAt[2]);
                const bool right = clear(anchor[0] - sx * PeekTest, anchor[1] - sy * PeekTest, eye, peekAt[0], peekAt[1], peekAt[2]);
                double sign = (Next() & 1) ? 1 : -1;
                if (left != right) sign = left ? 1 : -1;
                else if (!left && nav && !nav->nodes.empty())
                    if (const int n = nav->Nearest(anchor[0], anchor[1], z); n >= 0)
                    {
                        const int d1 = std::fabs(sx) > std::fabs(sy) ? (sx > 0 ? 0 : 1) : (sy > 0 ? 2 : 3);
                        const int a = nav->Openness(n, d1, 4), b = nav->Openness(n, NavGrid::Opposite[d1], 4);
                        if (a != b) sign = a > b ? 1 : -1;
                    }
                peekSide = {sx * sign, sy * sign};
            }
            double out = 0;
            if (peek == Peek::Jiggle)
            {
                if (now >= peekFlipAt)
                {
                    peekOut = !peekOut;
                    peekFlipAt = now + Uniform(0.2, 0.3);
                    if (peekOut) peekReach = Uniform(JiggleOut, JiggleFar);
                }
                out = peekOut ? peekReach : 0;
            }
            else out = peek == Peek::Wide ? WideOut : CrouchOut;
            const double tx = anchor[0] + peekSide[0] * out - x, ty = anchor[1] + peekSide[1] * out - y, d = std::hypot(tx, ty);
            peekCrouched = peek == Peek::Crouch && d < 30;
            if (d < 2) return;
            const double sp = std::min(speed, std::sqrt(2 * speed / DecelTime * 0.8 * d));
            wx = tx / d * sp;
            wy = ty / d * sp;
        }

        // Holding in a fight: across the line to the enemy, near where it holds. Counter-strafing:
        // strafe, a dead stop (the shooting window), the other way; else plain side to side. The
        // velocity it wants is (wx, wy); `down` is how hard it may stop.
        void FightStep(double now, double& wx, double& wy, double& down)
        {
            const auto& f = *face;
            const auto& anchor = *holdAnchor;
            const double fdx = f[0] - x, fdy = f[1] - y, fd = std::max(1.0, std::hypot(fdx, fdy));
            const double sx = -fdy / fd, sy = fdx / fd;
            // Where it would come to a stop from here: out of range, it turns back (or stops) in time.
            const double v = std::hypot(velX, velY), stopIn = v / (2 * (counterStrafe ? speed / CounterTime : speed / DecelTime));
            const double px = x + velX * stopIn - anchor[0], py = y + velY * stopIn - anchor[1];
            const bool away = (sx * strafeSign) * (x - anchor[0]) + (sy * strafeSign) * (y - anchor[1]) > 0;
            const bool beyond = std::hypot(px, py) > StrafeRange * 0.9 && away;
            if (counterStrafe)
            {
                down = speed / CounterTime;
                if (now >= phaseUntil || (strafePhase == 0 && beyond))
                {
                    if (strafePhase == 0)
                    {
                        strafePhase = 1;
                        phaseUntil = now + CounterTime + Uniform(0.25, 0.6);
                    }
                    else
                    {
                        strafePhase = 0;
                        strafeSign = -strafeSign;
                        // Far out to one side already: back the other way.
                        if (sx * strafeSign * (x - anchor[0]) + sy * strafeSign * (y - anchor[1]) > StrafeRange * 0.4) strafeSign = -strafeSign;
                        phaseUntil = now + Uniform(0.2, 0.45);
                    }
                }
                if (strafePhase == 0)
                {
                    const double sp = GaitSpeed() * (0.6 + 0.4 * std::clamp(fight, 0.0, 1.0));
                    wx = sx * strafeSign * sp;
                    wy = sy * strafeSign * sp;
                }
                return;
            }
            if (now >= strafeFlipAt) { strafeSign = -strafeSign; strafeFlipAt = now + Uniform(0.35, 0.9); }
            else if (beyond) { strafeSign = -strafeSign; strafeFlipAt = now + Uniform(0.35, 0.9); }
            const double sp = speed * 0.55 * std::clamp(fight, 0.0, 1.0);
            wx = sx * strafeSign * sp;
            wy = sy * strafeSign * sp;
        }

        // One step of `dt` seconds at time `now` (seconds). Never moves through a wall, off a
        // ledge or up a step higher than stepUp (on a grid link it trusts the grid); turns with an
        // eased flick no faster than turnRate, keeps clear of other bodies, looks around while holding,
        // peeks and strafes in a fight.
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
            // The zones to keep out of changed (a smoke went up, a fire burnt out): the way there is
            // planned again around them, at most twice a second.
            if (now >= avoidCheckAt && AvoidChanged())
            {
                avoidCheckAt = now + 0.5;
                planAvoid = avoid;
                if (target == NavTarget)
                {
                    ForgetRoute();
                    nextChoose = 0;
                }
            }
            if (target == -1 && now >= nextChoose && !hold)
            {
                const bool pending = Choose(clear, floor, halfHeight);
                if (target < 0) nextChoose = now + (pending ? 0.15 : 1.0); // nothing reachable from here: look again soon
            }
            if (hold && target != -1) { target = -1; }
            if (!hold)
            {
                holdAnchor.reset();
                peekFor.reset();
                strafePhase = 0;
            }
            if (!hold || peek != Peek::Crouch) peekCrouched = false;
            const double up = speed / AccelTime;
            double down = speed / DecelTime;
            double wantX = 0, wantY = 0; // the velocity it wants
            bool walking = false, moved = false;
            if (target != -1 && grounded && !hold)
            {
                if (target == NavTarget) AdvancePath();
                else if (const auto dest = Point(target); std::hypot(dest[0] - x, dest[1] - y) < arrive)
                {
                    at = target == GoalTarget ? -1 : target;
                    if (!route.empty() && route.front() == target) route.erase(route.begin());
                    // Straight on to the next waypoint of the route, or the goal.
                    target = route.empty() ? -1 : route.front();
                }
            }
            if (target != -1 && grounded && !hold)
            {
                const auto dest = Point(target);
                const double dx = dest[0] - x, dy = dest[1] - y, d = std::max(1e-6, std::hypot(dx, dy));
                const double fx = dx / d, fy = dy / d; // straight for the point
                const double cruise = GaitSpeed();
                double hx = fx, hy = fy, desired = cruise, reach = 1e9, side = 0;
                if (target == NavTarget) Steer(cruise, hx, hy, desired, reach);
                else
                {
                    // Strafe left and right while roaming, like a bot dodging; never along a nav path.
                    walkedFor += dt;
                    const double w = 2 * Pi / StrafePeriod;
                    side = strafe * w * std::cos(walkedFor * w) * std::min(1.0, d / (arrive * 3));
                }
                wantX = hx * desired - hy * side;
                wantY = hy * desired + hx * side;
                // Keep clear of other bodies: slow down behind one ahead (single file through a
                // door), step aside from one alongside; never walk into them.
                double pace = 1;
                // Behind someone it starts slowing down early enough to stop in time from its speed.
                const double braking = std::hypot(velX, velY) * DecelTime * 0.5;
                for (const auto& o : others)
                {
                    const double ox = x - o[0], oy = y - o[1], od = std::hypot(ox, oy);
                    if (od >= Separation + braking || od < 1) continue;
                    const double aheadOf = -(ox * hx + oy * hy) / od; // 1: right in front
                    if (aheadOf > 0.3) pace = std::min(pace, std::clamp((od - braking - Separation * 0.55) / (Separation * 0.45), 0.0, 1.0));
                    if (od >= Separation) continue;
                    const double push = (Separation - od) / Separation * speed * 0.6;
                    const double across = (ox * -hy + oy * hx) / od;
                    const double sign = across >= 0 ? 1 : -1;
                    wantX += -hy * sign * push;
                    wantY += hx * sign * push;
                }
                wantX -= hx * desired * (1 - pace);
                wantY -= hy * desired * (1 - pace);
                // At the goal with someone already standing there: stop alongside instead.
                if (target == NavTarget && navIndex + 1 >= navPath.size() && d < Separation * 2)
                    for (const auto& o : others)
                        if (std::hypot(o[0] - dest[0], o[1] - dest[1]) < Separation * 0.8 && std::hypot(x - o[0], y - o[1]) < Separation * 1.3)
                        {
                            navPath.clear();
                            navNodes.clear();
                            navExact.clear();
                            target = -1;
                            if (goal) stoppedFor = *goal;
                            break;
                        }
                if (target != -1)
                {
                    moved = true;
                    Accelerate(velX, velY, wantX, wantY, up, down, dt);
                    // On a grid link (near the segment from the last point to this one) the grid's checks hold.
                    // Only a single grid step is trusted: a string-pulled leg is checked like any walk.
                    double u = 1;
                    const bool gridStep = nav && navIndex > 0 && navIndex < navNodes.size() && navNodes[navIndex - 1] >= 0 && navNodes[navIndex] >= 0
                                              ? nav->Adjacent(navNodes[navIndex - 1], navNodes[navIndex])
                                              : std::hypot(dest[0] - legFrom[0], dest[1] - legFrom[1]) <= (nav ? nav->spacing * 1.5 : 0);
                    const bool onLink = target == NavTarget && gridStep && SegmentDistance(x, y, legFrom, dest, u) < TrustDistance;
                    const double linkZ = legFrom[2] + (dest[2] - legFrom[2]) * u + halfHeight;
                    bool ok = Move(velX, velY, dt, halfHeight, floor, clear, onLink, linkZ, target == NavTarget ? reach : 1e9);
                    if (!ok)
                    {
                        // Steering round the corner is blocked: straight for the point instead (the grid
                        // says that way is clear), at least at half its pace.
                        const double sp = std::max(std::hypot(velX, velY), cruise * 0.5);
                        ok = Move(fx * sp, fy * sp, dt, halfHeight, floor, clear, onLink, linkZ, target == NavTarget ? d : 1e9);
                        if (ok) { velX = fx * sp; velY = fy * sp; }
                    }
                    if (ok)
                    {
                        blocked = 0;
                        walking = true;
                    }
                    else
                    {
                        velX = velY = 0;
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

            // Holding: a fight strafes (across the line to the enemy, near where it stood), a peek order
            // peeks its angle; otherwise it checks the open angles around it, a few seconds each.
            const bool fighting = hold && grounded && face && fight > 0;
            const bool peeking = hold && grounded && !fighting && peek != Peek::None;
            if (hold && grounded)
            {
                if (!holdAnchor) holdAnchor = std::array<double, 2>{x, y};
                if (fighting) FightStep(now, wantX, wantY, down);
                else if (peeking) PeekStep(now, clear, wantX, wantY);
                else if (!face && now >= nextLook)
                {
                    nextLook = now + Uniform(1.5, 3.5);
                    lookYaw = LookAround();
                }
            }
            if (!fighting) strafePhase = 0;
            if (!peeking) peekFor.reset();
            // Standing (holding, or there) too close to another body: a step aside.
            if (!walking && !moved && grounded && !fighting && !peeking)
                for (const auto& o : others)
                {
                    const double ox = x - o[0], oy = y - o[1], od = std::hypot(ox, oy);
                    if (od < Separation * 0.8 && od > 1) { wantX = ox / od * 200; wantY = oy / od * 200; break; }
                }
            // Anything but walking its way (holding, strafing, peeking, coming to a stop): the same body
            // speeding up and slowing down, and a wall (or the strafe range) stops it.
            if (!moved && grounded)
            {
                Accelerate(velX, velY, wantX, wantY, up, down, dt);
                if (std::hypot(velX, velY) < 0.5 && std::hypot(wantX, wantY) < 0.5) velX = velY = 0;
                else
                {
                    const bool outOfRange = fighting && holdAnchor && std::hypot(x + velX * dt - (*holdAnchor)[0], y + velY * dt - (*holdAnchor)[1]) > StrafeRange &&
                                            std::hypot(x + velX * dt - (*holdAnchor)[0], y + velY * dt - (*holdAnchor)[1]) > std::hypot(x - (*holdAnchor)[0], y - (*holdAnchor)[1]);
                    if (outOfRange || !Move(velX, velY, dt, halfHeight, floor, clear, false, z))
                    {
                        velX = velY = 0;
                        if (fighting && counterStrafe && strafePhase == 0) { strafePhase = 1; phaseUntil = now + CounterTime + Uniform(0.25, 0.6); }
                        else if (fighting) strafeSign = -strafeSign;
                    }
                }
            }
            else if (!grounded) velX = velY = 0;
            // Standing still and looking around, also once there.
            if (!hold && !walking && target == -1 && !face && now >= nextLook)
            {
                nextLook = now + Uniform(1.5, 3.5);
                lookYaw = LookAround();
            }
            // Crouch now and then, jump rarely (visual only: the capsule centre stays on the floor); never
            // while walking silently or crouched.
            const bool quiet = walkGait || crouchStance;
            if (!hold && !quiet && now >= nextCrouch && crouchUntil < now)
            {
                crouchUntil = now + Uniform(0.8, 1.6);
                nextCrouch = now + Uniform(7, 12);
            }
            if (quiet) crouchUntil = std::min(crouchUntil, now);
            const bool crouched = crouchStance || peekCrouched || now < crouchUntil;
            const double crouchedHalf = halfHeight * 0.6;
            const double eye = z + EyeAbove - (crouched ? halfHeight - crouchedHalf : 0);
            // Where it looks: a point it faces (an enemy), the angle it peeks, the corner it pre-aims;
            // else where it walks or looks around.
            lookAt.reset();
            if (face) lookAt = *face;
            else if (peeking) lookAt = peekAt;
            else if (preaim && walking && target == NavTarget && navIndex + 1 < navPath.size())
            {
                // Pre-aim: round the next corner of the path (a little way along the leg after it), at head height.
                const auto& c = navPath[navIndex];
                const auto& n = navPath[navIndex + 1];
                const double ax = c[0] - x, ay = c[1] - y, bx = n[0] - c[0], by = n[1] - c[1];
                const double la = std::hypot(ax, ay), lb = std::hypot(bx, by), spacing = nav ? nav->spacing : 120.0;
                if (la > 1 && lb > 1 && la < spacing * 12 && (ax * bx + ay * by) / (la * lb) < std::cos(25 * Pi / 180))
                {
                    const double along = std::min(lb, spacing * 3);
                    lookAt = std::array<double, 3>{c[0] + bx / lb * along, c[1] + by / lb * along, c[2] + (n[2] - c[2]) * along / lb + halfHeight + EyeAbove};
                }
            }
            double wantPitch = 0;
            const double moving = std::hypot(velX, velY);
            if (lookAt)
            {
                const auto& f = *lookAt;
                const double dx = f[0] - x, dy = f[1] - y, dz = f[2] - eye;
                if (std::hypot(dx, dy) > 1)
                {
                    wantYaw = std::atan2(dy, dx) * 180.0 / Pi;
                    wantPitch = std::atan2(dz, std::hypot(dx, dy)) * 180.0 / Pi;
                }
            }
            else if (walking && moving > GaitSpeed() * 0.2) wantYaw = std::atan2(velY, velX) * 180.0 / Pi; // face where it walks
            else if (!walking) wantYaw = lookYaw;
            if (walking)
            {
                lookYaw = wantYaw; // once there, it keeps looking that way until it looks around
                nextLook = std::max(nextLook, now + 0.8);
            }
            // Human turning: eased, a touch of overshoot, no faster than turnRate (degrees per second).
            yaw = Ease(yaw, wantYaw, yawVel, turnRate, TurnAccel(), overshoot, dt, true);
            pitch = std::clamp(Ease(pitch, wantPitch, pitchVel, turnRate, TurnAccel(), overshoot, dt, false), -89.0, 89.0);
            if (hold || quiet) nextJump = std::max(nextJump, now + 2); // no hops while standing to shoot or plant, peeking, or sneaking
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
                    hop = std::sin(u * Pi) * JumpHeight;
                    vz = std::cos(u * Pi) * JumpHeight * Pi / JumpTime;
                }
            }
            s.crouch = crouched;
            s.halfHeight = s.crouch ? crouchedHalf : halfHeight;
            s.x = x;
            s.y = y;
            // A crouched body is a shorter capsule standing on the same floor: its centre is lower.
            s.z = z + hop - (s.crouch ? halfHeight - s.halfHeight : 0);
            s.vx = velX;
            s.vy = velY;
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

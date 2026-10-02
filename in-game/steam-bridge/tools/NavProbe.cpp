// Development tool: the bots' navigation on a ported CS map, offline. Loads the map-creator map and
// its AimMod objectives file, grows the nav grid with the same code as the game (NavGrid.hpp), and
// reports coverage, which spawns and bomb sites reach each other, and how bot walkers (Walker.hpp)
// fare walking from every side's spawns to every site: arrival time, stuck recoveries, blocked steps;
// running, shift-walking while pre-aiming corners, and round a smoke halfway along the way; then as
// squads of five. Also how they turn: snaps (a step faster than the turn rate) and the largest
// turning acceleration. And the holding spots (SiteSpots.hpp) the bots take on each site, as the
// service asks for them: post-plant (the defenders come from their spawn and the other site) and a
// site hold (the attackers come from theirs): the entrances, the spots that see the bomb or an
// entrance, five distinct ones picked out of grenade range of each other, and a walk from the site
// to each. Exit code 0: every pair connected, every walk and squad arrived, and every site has its
// entrances and five spots that were walked to.
//
//   aimmod_nav_probe <map.json> <map.aimmod.json> [--walk seconds] [--verbose] [--at x y z]
//
// Paths come from the command line only; nothing is written.
#include "MapGeometry.hpp"
#include "NavGrid.hpp"
#include "SiteSpots.hpp"
#include "Walker.hpp"

#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <optional>
#include <string>
#include <vector>

using namespace bridge;

namespace
{
    struct Target
    {
        std::string name;
        std::array<double, 3> at{};
        double half = 0; // a site: its box's half-diagonal across (cm)
    };

    std::vector<std::array<double, 4>> Spawns(const json::Value& cs, const char* side, double scale)
    {
        std::vector<std::array<double, 4>> out;
        const auto* spawns = cs.Get("spawns");
        const auto* list = spawns ? spawns->Get(side) : nullptr;
        if (!list) return out;
        for (const auto& s : list->array)
            if (s.array.size() >= 3)
                out.push_back({s.array[0].number * scale, s.array[1].number * scale, s.array[2].number * scale, s.array.size() > 3 ? s.array[3].number : 0});
        return out;
    }

    // The point halfway along a path (by length).
    std::optional<std::array<double, 3>> Halfway(const std::vector<std::array<double, 3>>& way)
    {
        if (way.size() < 2) return std::nullopt;
        double length = 0;
        for (std::size_t i = 1; i < way.size(); ++i) length += std::hypot(way[i][0] - way[i - 1][0], way[i][1] - way[i - 1][1]);
        double left = length / 2;
        for (std::size_t i = 1; i < way.size(); ++i)
        {
            const double l = std::hypot(way[i][0] - way[i - 1][0], way[i][1] - way[i - 1][1]);
            if (l >= left && l > 0)
            {
                const double u = left / l;
                return std::array<double, 3>{way[i - 1][0] + (way[i][0] - way[i - 1][0]) * u, way[i - 1][1] + (way[i][1] - way[i - 1][1]) * u, way[i - 1][2] + (way[i][2] - way[i - 1][2]) * u};
            }
            left -= l;
        }
        return way.back();
    }

    // How the walkers turn, 60 steps a second: a step turning faster than the turn rate (with its
    // overshoot) is a snap; the turning speed should change no faster than the walker's acceleration.
    struct Turning
    {
        double lastYaw = 0, lastRate = 0;
        bool haveRate = false;
        int snaps = 0;
        double maxAccel = 0, limit = 0;
        void Start(double yaw)
        {
            lastYaw = yaw;
            haveRate = false;
        }
        void Add(double yaw, const ghost::Walker& w)
        {
            constexpr double dt = 1 / 60.0;
            const double turn = ghost::WrapAngle(yaw - lastYaw), rate = turn / dt;
            if (std::fabs(turn) > w.turnRate * (1 + w.overshoot) * dt + 1e-6) ++snaps;
            if (haveRate) maxAccel = std::max(maxAccel, std::fabs(rate - lastRate) / dt);
            limit = std::max(limit, w.TurnAccel());
            lastRate = rate;
            haveRate = true;
            lastYaw = yaw;
        }
        void Merge(const Turning& other)
        {
            snaps += other.snaps;
            maxAccel = std::max(maxAccel, other.maxAccel);
            limit = std::max(limit, other.limit);
        }
    };
} // namespace

int main(int argc, char** argv)
{
    if (argc < 3)
    {
        std::fprintf(stderr, "usage: aimmod_nav_probe <map.json> <map.aimmod.json> [--walk seconds] [--verbose] [--at x y z]\n");
        return 2;
    }
    double walkSeconds = 90;
    for (int i = 3; i + 1 < argc; ++i)
        if (std::strcmp(argv[i], "--walk") == 0) walkSeconds = std::atof(argv[i + 1]);

    std::ifstream objFile(argv[2], std::ios::binary);
    const std::string objText((std::istreambuf_iterator<char>(objFile)), std::istreambuf_iterator<char>());
    json::Limits limits;
    limits.maxBytes = 16u << 20;
    limits.maxDepth = 16;
    limits.maxMembers = 1u << 16;
    const auto objectives = json::Parse(objText, limits);
    const auto* cs = objectives ? objectives->Get("cs") : nullptr;
    if (!cs)
    {
        std::fprintf(stderr, "no CS data in the objectives file\n");
        return 2;
    }
    const double scale = objectives->Num("map_scale").value_or(1);

    tools::MapGeometry map;
    for (int i = 3; i < argc; ++i)
        if (std::strcmp(argv[i], "--verbose") == 0) map.verbose = true;
    std::string error;
    const auto t0 = std::chrono::steady_clock::now();
    if (!map.Load(argv[1], scale, error))
    {
        std::fprintf(stderr, "map: %s\n", error.c_str());
        return 2;
    }
    std::printf("map: %zu brushes (%d skipped, %d rotated: rotation ignored), scale %.2f\n", map.brushes.size(), map.skipped, map.rotated, scale);

    const auto floor = [&](double x, double y, double z) { return map.Floor(x, y, z); };
    for (int i = 3; i + 3 < argc; ++i)
        if (std::strcmp(argv[i], "--at") == 0)
        {
            const double x = std::atof(argv[i + 1]), y = std::atof(argv[i + 2]), z = std::atof(argv[i + 3]);
            const auto f = map.Floor(x, y, z);
            std::printf("floor below (%.0f, %.0f, %.0f): %s %.1f\n", x, y, z, f ? "at" : "none", f.value_or(0));
            for (const auto& b : map.brushes)
                if (b.min.x <= x && x <= b.max.x && b.min.y <= y && y <= b.max.y)
                    std::printf("  brush x %.0f..%.0f y %.0f..%.0f z %.0f..%.0f, %zu planes\n", b.min.x, b.max.x, b.min.y, b.max.y, b.min.z, b.max.z, b.planes.size());
            return 0;
        }
    const auto clear = [&](double ax, double ay, double az, double bx, double by, double bz) { return map.Clear(ax, ay, az, bx, by, bz); };

    const auto tSpawns = Spawns(*cs, "T", scale), ctSpawns = Spawns(*cs, "CT", scale);
    std::vector<Target> targets;
    if (!tSpawns.empty()) targets.push_back({"T spawn", {tSpawns[0][0], tSpawns[0][1], tSpawns[0][2]}});
    if (!ctSpawns.empty()) targets.push_back({"CT spawn", {ctSpawns[0][0], ctSpawns[0][1], ctSpawns[0][2]}});
    if (const auto* sites = cs->Get("bomb_sites"))
        for (const auto& s : sites->array)
        {
            const auto* mn = s.Get("min");
            const auto* mx = s.Get("max");
            if (!mn || !mx || mn->array.size() < 3 || mx->array.size() < 3) continue;
            targets.push_back({"site " + s.Str("name", 8).value_or("?"),
                               {(mn->array[0].number + mx->array[0].number) / 2 * scale, (mn->array[1].number + mx->array[1].number) / 2 * scale,
                                (mn->array[2].number + mx->array[2].number) / 2 * scale},
                               std::hypot(mx->array[0].number - mn->array[0].number, mx->array[1].number - mn->array[1].number) / 2 * scale});
        }

    // The grid as the game builds it (Ghosts.cpp GrowNav): avatar capsule 145, CS movement.
    ghost::Walker tuned;
    tuned.Tune(1100, 79);
    ghost::NavGrid grid;
    grid.stepUp = tuned.stepUp;
    grid.stepDown = tuned.stepDown;
    grid.halfHeight = 145;
    grid.spacing = 120;
    int seeded = 0, seeds = 0;
    for (const auto& s : tSpawns) { ++seeds; seeded += grid.Seed(s[0], s[1], s[2], floor) >= 0; }
    for (const auto& s : ctSpawns) { ++seeds; seeded += grid.Seed(s[0], s[1], s[2], floor) >= 0; }
    for (const auto& t : targets) { ++seeds; seeded += grid.Seed(t.at[0], t.at[1], t.at[2], floor) >= 0; }
    int ticks = 0;
    while (!grid.Done()) { grid.Grow(1500, floor, clear); ++ticks; }
    const double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count();
    std::printf("grid: %zu points, %lld traces, %d game ticks at 1500 traces (%.1f s at 60 fps), seeds on the floor %d/%d, %.0f ms here\n", grid.nodes.size(),
                static_cast<long long>(grid.traces), ticks, ticks / 60.0, seeded, seeds, ms);

    // Which targets reach each other over the grid.
    int pairs = 0, linked = 0;
    for (const auto& a : targets)
        for (const auto& b : targets)
        {
            if (&a == &b) continue;
            bool reached = false;
            const auto path = grid.Path(a.at, b.at, reached);
            ++pairs;
            linked += reached;
            std::printf("  %-9s -> %-9s %s (%zu points)\n", a.name.c_str(), b.name.c_str(), reached ? "reached" : "NOT REACHED", path.size());
        }
    std::printf("coverage: %d/%d target pairs connected (%.0f%%)\n", linked, pairs, pairs ? 100.0 * linked / pairs : 0.0);

    // Holding spots on each site, the way the service asks (BotPositions.cs AreaRadii): post-plant
    // (from the CT spawn and the other site) and a site hold (from the T spawn).
    int areasOk = 0, areasRun = 0, spotWalks = 0, spotArrived = 0;
    std::vector<const Target*> sites;
    for (const auto& t : targets)
        if (t.name.rfind("site", 0) == 0) sites.push_back(&t);
    const auto shared0 = std::make_shared<ghost::NavGrid>(grid);
    for (const auto* site : sites)
        for (const bool postPlant : {true, false})
        {
            ghost::SpotArea::Request request;
            request.key = site->name.substr(5) + (postPlant ? "-post" : "-hold");
            request.centre = site->at;
            request.rmin = std::clamp(site->half * 0.2, 250.0, 700.0);
            request.rmax = std::clamp(site->half * 3.2, 2400.0, 5600.0);
            request.entry = std::clamp(site->half * 1.8, 1200.0, 3600.0);
            if (postPlant)
            {
                if (!ctSpawns.empty()) request.sources.push_back({ctSpawns[0][0], ctSpawns[0][1], ctSpawns[0][2]});
                for (const auto* other : sites)
                    if (other != site) request.sources.push_back(other->at);
            }
            else if (!tSpawns.empty()) request.sources.push_back({tSpawns[0][0], tSpawns[0][1], tSpawns[0][2]});
            ghost::SpotArea area;
            area.Start(request);
            int steps = 0;
            const auto t1 = std::chrono::steady_clock::now();
            while (!area.Done() && steps < 10000) { area.Step(grid, 800, clear); ++steps; }
            const double spotMs = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t1).count();
            int bomb = 0, lurks = 0;
            for (const auto& sp : area.spots) bomb += sp.bomb;
            for (const auto& e : area.entrances) lurks += e.lurk.has_value();
            std::printf("  spots %-9s %zu entrances, %zu spots (%d see the centre), %d lurk spots; %d traces, %d steps, %.0f ms here\n", request.key.c_str(), area.entrances.size(), area.spots.size(), bomb, lurks,
                        area.traces, steps, spotMs);
            for (std::size_t i = 0; i < area.entrances.size(); ++i)
            {
                const auto& e = area.entrances[i];
                int seeing = 0;
                for (const auto& sp : area.spots) seeing += (sp.mask >> i & 1u) != 0;
                std::printf("    entrance %zu at (%.0f, %.0f, %.0f) from source %d, way %.0f cm; %d spots see it\n", i, e.at[0], e.at[1], e.at[2], e.source, e.pathCm, seeing);
            }
            // Five distinct spots, out of one grenade's reach of each other (as the brain picks them):
            // the best for each entrance first, then the bomb.
            std::vector<ghost::SpotArea::Spot> picked;
            const auto apart = [&](const ghost::SpotArea::Spot& sp, double gap) {
                for (const auto& q : picked)
                    if (std::hypot(q.at[0] - sp.at[0], q.at[1] - sp.at[1]) < gap) return false;
                return true;
            };
            for (double gap = 960; picked.size() < 5 && gap > 200; gap *= 0.75)
                for (std::size_t want = 0; want <= area.entrances.size() && picked.size() < 5; ++want)
                    for (const auto& sp : area.spots)
                    {
                        const bool fits = want < area.entrances.size() ? (sp.mask >> want & 1u) != 0 : sp.bomb;
                        if (fits && apart(sp, gap)) { picked.push_back(sp); break; }
                    }
            int arrivedHere = 0;
            for (const auto& sp : picked)
            {
                ghost::Walker w;
                w.nav = shared0;
                w.Tune(1100, 79);
                w.PlaceAt(site->at[0], site->at[1], site->at[2], 0, 145, floor);
                w.goal = std::array<double, 3>{sp.at[0], sp.at[1], sp.at[2] + 145};
                bool there = false;
                for (int f = 0; f < 60 * 30 && !there; ++f)
                {
                    const auto st = w.Step(f / 60.0, 1 / 60.0, 145, floor, clear);
                    there = std::hypot(st.x - sp.at[0], st.y - sp.at[1]) < w.arrive + 40;
                }
                ++spotWalks;
                spotArrived += there;
                arrivedHere += there;
                std::printf("    spot (%.0f, %.0f, %.0f) %s, entrances 0x%x, cover %d, walk %.0f cm: %s\n", sp.at[0], sp.at[1], sp.at[2], sp.bomb ? "sees the bomb" : "no bomb", sp.mask, sp.cover, sp.walk,
                            there ? "walked to" : "NOT REACHED");
            }
            ++areasRun;
            const bool ok = !area.noGrid && !area.entrances.empty() && picked.size() >= 5 && arrivedHere == static_cast<int>(picked.size());
            areasOk += ok;
            if (!ok) std::printf("    FAILED: %s\n", area.noGrid ? "no grid point" : area.entrances.empty() ? "no entrance" : picked.size() < 5 ? "fewer than five distinct spots" : "a spot not walked to");
        }
    std::printf("holding spots: %d/%d areas with entrances and five distinct spots, %d/%d spots walked to\n", areasOk, areasRun, spotArrived, spotWalks);

    // Walkers from each side's first five spawns to every site: running; then shift-walking and
    // pre-aiming corners; then running round a zone to avoid (a smoke) on the middle of the way.
    if (walkSeconds <= 0) return areasOk == areasRun ? 0 : 1;
    const auto shared = std::make_shared<ghost::NavGrid>(grid);
    Turning turning;
    int runs = 0, arrived = 0;
    enum class Run { Plain, WalkPreaim, Avoid };
    for (const Run run : {Run::Plain, Run::WalkPreaim, Run::Avoid})
    {
        const char* runName = run == Run::Plain ? "walk" : run == Run::WalkPreaim ? "sneak" : "avoid";
        // Shift-walking is 52% of the run speed: twice the time.
        const double seconds = run == Run::WalkPreaim ? walkSeconds * 2 : walkSeconds;
        int runRuns = 0, runArrived = 0, zonesThrough = 0;
        double closestToZone = 1e9; // of the walks planned round their zone
        for (const auto* side : {&tSpawns, &ctSpawns})
            for (std::size_t i = 0; i < side->size() && i < 5; ++i)
                for (const auto& t : targets)
                {
                    if (t.name.rfind("site", 0) != 0) continue;
                    ghost::Walker w;
                    w.nav = shared;
                    w.spawns = {{(*side)[i][0], (*side)[i][1], (*side)[i][2]}};
                    w.Tune(1100, 79);
                    w.PlaceAt((*side)[i][0], (*side)[i][1], (*side)[i][2], (*side)[i][3], 145, floor);
                    w.goal = t.at;
                    w.walkGait = w.preaim = run == Run::WalkPreaim;
                    std::optional<std::array<double, 5>> zone;
                    bool plannedThrough = false;
                    double zoneEdge = 1e9;
                    if (run == Run::Avoid)
                    {
                        // Halfway along the way it would walk without the zone.
                        bool reached = false;
                        const auto way = shared->Path({w.x, w.y, w.z}, t.at, reached);
                        if (const auto mid = Halfway(way)) zone = std::array<double, 5>{(*mid)[0], (*mid)[1], (*mid)[2], 300, 300};
                        if (zone)
                        {
                            w.avoid = {*zone};
                            // Whether the plan round it goes through it all the same (no way round worth its cost).
                            bool ok = false;
                            for (const auto& p : shared->Path({w.x, w.y, w.z}, t.at, ok, w.avoid))
                                if (ghost::NavGrid::InZone(p[0], p[1], p[2], *zone)) { plannedThrough = true; break; }
                            zonesThrough += plannedThrough;
                        }
                    }
                    double at = -1;
                    const bool verbose = map.verbose;
                    turning.Start(w.yaw);
                    for (int f = 0; f < static_cast<int>(seconds * 60) && at < 0; ++f)
                    {
                        const auto s = w.Step(f / 60.0, 1 / 60.0, 145, floor, clear);
                        turning.Add(s.yaw, w);
                        if (zone) zoneEdge = std::min(zoneEdge, std::hypot(s.x - (*zone)[0], s.y - (*zone)[1]) - (*zone)[3]);
                        if (verbose && f % 30 == 0)
                        {
                            const auto next = w.target == ghost::Walker::NavTarget && !w.navPath.empty() ? w.navPath[std::min(w.navIndex, w.navPath.size() - 1)] : std::array<double, 3>{};
                            std::printf("      t %5.1f at (%.0f, %.0f, %.0f) target %d path %zu/%zu next (%.0f, %.0f, %.0f) blocked %d plans %d\n", f / 60.0, s.x, s.y, s.z, w.target, w.navIndex,
                                        w.navPath.size(), next[0], next[1], next[2], w.navBlocked, w.plansFound);
                        }
                        if (std::hypot(s.x - t.at[0], s.y - t.at[1]) < w.arrive + 20) at = f / 60.0;
                    }
                    ++runRuns;
                    runArrived += at >= 0;
                    if (zone && !plannedThrough) closestToZone = std::min(closestToZone, zoneEdge);
                    if (at < 0)
                    {
                        const int g = shared->Nearest(t.at[0], t.at[1], t.at[2]), h = shared->Nearest(w.x, w.y, w.z);
                        std::printf("    stopped at (%.0f, %.0f, %.0f) target %d, path %zu at %zu%s; goal's grid point %d (%.0f, %.0f, %.0f), here %d\n", w.x, w.y, w.z, w.target, w.navPath.size(),
                                    w.navIndex, w.navReached ? "" : " (partial)", g, g >= 0 ? shared->nodes[g].x : 0, g >= 0 ? shared->nodes[g].y : 0, g >= 0 ? shared->nodes[g].z : 0, h);
                    }
                    std::printf("  %s %s %zu -> %-7s %s in %5.1f s; %4.0f cm left; stuck recoveries %d, blocked %d, plans %d/%d/%d\n", runName, side == &tSpawns ? "T " : "CT", i,
                                t.name.c_str(), at >= 0 ? "arrived" : "STUCK  ", at >= 0 ? at : seconds, std::hypot(w.x - t.at[0], w.y - t.at[1]), w.stuckRecoveries, w.navBlocked,
                                w.plansFound, w.plansPending, w.plansFailed);
                }
        runs += runRuns;
        arrived += runArrived;
        if (run == Run::Plain) std::printf("walks: %d/%d arrived within %.0f s\n", runArrived, runRuns, seconds);
        else if (run == Run::WalkPreaim) std::printf("shift-walking and pre-aiming: %d/%d arrived within %.0f s\n", runArrived, runRuns, seconds);
        else
            std::printf("round a smoke halfway (radius 300, cost 300 a step): %d/%d arrived within %.0f s; %d planned round it (closest to its edge %.0f cm, negative: inside), %d through it (no way round worth it)\n",
                        runArrived, runRuns, seconds, runRuns - zonesThrough, closestToZone, zonesThrough);
    }

    // A squad: each side's five walk to every site together, keeping clear of each other.
    int squads = 0, squadsArrived = 0;
    for (const auto* side : {&tSpawns, &ctSpawns})
        for (const auto& t : targets)
        {
            if (t.name.rfind("site", 0) != 0 || side->size() < 5) continue;
            std::vector<ghost::Walker> team(5);
            for (std::size_t i = 0; i < team.size(); ++i)
            {
                auto& w = team[i];
                w.nav = shared;
                w.seed ^= static_cast<std::uint32_t>(i * 2654435761u);
                w.Tune(1100, 79);
                w.PlaceAt((*side)[i][0], (*side)[i][1], (*side)[i][2], (*side)[i][3], 145, floor);
                w.goal = t.at;
            }
            double closest = 1e9, at = -1;
            std::vector<Turning> turns(team.size());
            for (std::size_t i = 0; i < team.size(); ++i) turns[i].Start(team[i].yaw);
            for (int f = 0; f < static_cast<int>(walkSeconds * 60) && at < 0; ++f)
            {
                int there = 0;
                for (std::size_t i = 0; i < team.size(); ++i)
                {
                    team[i].others.clear();
                    for (std::size_t j = 0; j < team.size(); ++j)
                        if (j != i) team[i].others.push_back({team[j].x, team[j].y});
                    const auto s = team[i].Step(f / 60.0, 1 / 60.0, 145, floor, clear);
                    turns[i].Add(s.yaw, team[i]);
                    there += std::hypot(s.x - t.at[0], s.y - t.at[1]) < team[i].arrive * 3;
                }
                for (std::size_t i = 0; i < team.size(); ++i)
                    for (std::size_t j = i + 1; j < team.size(); ++j)
                        if (f > 120) closest = std::min(closest, std::hypot(team[i].x - team[j].x, team[i].y - team[j].y));
                if (there == static_cast<int>(team.size())) at = f / 60.0;
            }
            int recoveries = 0;
            for (const auto& w : team) recoveries += w.stuckRecoveries;
            for (const auto& turn : turns) turning.Merge(turn);
            ++squads;
            squadsArrived += at >= 0;
            std::printf("  squad %s -> %-7s %s in %5.1f s; closest two bodies %.0f cm; stuck recoveries %d\n", side == &tSpawns ? "T " : "CT", t.name.c_str(), at >= 0 ? "arrived" : "STUCK  ",
                        at >= 0 ? at : walkSeconds, closest, recoveries);
        }
    std::printf("squads: %d/%d arrived\n", squadsArrived, squads);
    std::printf("turning: %d snaps (a step faster than the turn rate), largest turning acceleration %.0f deg/s^2 (limit %.0f)\n", turning.snaps, turning.maxAccel, turning.limit);
    arrived += squadsArrived - squads; // a squad that didn't arrive fails the run
    return arrived == runs && linked == pairs && areasOk == areasRun ? 0 : 1;
}

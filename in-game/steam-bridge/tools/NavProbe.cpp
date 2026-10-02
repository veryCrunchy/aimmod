// Development tool: the bots' navigation on a ported CS map, offline. Loads the map-creator map and
// its AimMod objectives file, grows the nav grid with the same code as the game (NavGrid.hpp), and
// reports coverage, which spawns and bomb sites reach each other, and how bot walkers (Walker.hpp)
// fare walking from every side's spawns to every site: arrival time, stuck recoveries, blocked steps.
//
//   aimmod_nav_probe <map.json> <map.aimmod.json> [--walk seconds] [--verbose] [--at x y z]
//
// Paths come from the command line only; nothing is written.
#include "MapGeometry.hpp"
#include "NavGrid.hpp"
#include "Walker.hpp"

#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <string>
#include <vector>

using namespace bridge;

namespace
{
    struct Target
    {
        std::string name;
        std::array<double, 3> at{};
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
                                (mn->array[2].number + mx->array[2].number) / 2 * scale}});
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

    // Walkers from each side's first five spawns to every site.
    if (walkSeconds <= 0) return 0;
    const auto shared = std::make_shared<ghost::NavGrid>(grid);
    int runs = 0, arrived = 0;
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
                double at = -1;
                const bool verbose = map.verbose;
                for (int f = 0; f < static_cast<int>(walkSeconds * 60) && at < 0; ++f)
                {
                    const auto s = w.Step(f / 60.0, 1 / 60.0, 145, floor, clear);
                    if (verbose && f % 30 == 0)
                    {
                        const auto next = w.target == ghost::Walker::NavTarget && !w.navPath.empty() ? w.navPath[std::min(w.navIndex, w.navPath.size() - 1)] : std::array<double, 3>{};
                        std::printf("      t %5.1f at (%.0f, %.0f, %.0f) target %d path %zu/%zu next (%.0f, %.0f, %.0f) blocked %d plans %d\n", f / 60.0, s.x, s.y, s.z, w.target, w.navIndex,
                                    w.navPath.size(), next[0], next[1], next[2], w.navBlocked, w.plansFound);
                    }
                    if (std::hypot(s.x - t.at[0], s.y - t.at[1]) < w.arrive + 20) at = f / 60.0;
                }
                ++runs;
                arrived += at >= 0;
                if (at < 0)
                {
                    const int g = shared->Nearest(t.at[0], t.at[1], t.at[2]), h = shared->Nearest(w.x, w.y, w.z);
                    std::printf("    stopped at (%.0f, %.0f, %.0f) target %d, path %zu at %zu%s; goal's grid point %d (%.0f, %.0f, %.0f), here %d\n", w.x, w.y, w.z, w.target, w.navPath.size(),
                                w.navIndex, w.navReached ? "" : " (partial)", g, g >= 0 ? shared->nodes[g].x : 0, g >= 0 ? shared->nodes[g].y : 0, g >= 0 ? shared->nodes[g].z : 0, h);
                }
                std::printf("  walk %s %zu -> %-7s %s in %5.1f s; %4.0f cm left; stuck recoveries %d, blocked %d, plans %d/%d/%d\n", side == &tSpawns ? "T " : "CT", i,
                            t.name.c_str(), at >= 0 ? "arrived" : "STUCK  ", at >= 0 ? at : walkSeconds, std::hypot(w.x - t.at[0], w.y - t.at[1]), w.stuckRecoveries, w.navBlocked,
                            w.plansFound, w.plansPending, w.plansFailed);
            }
    std::printf("walks: %d/%d arrived within %.0f s\n", arrived, runs, walkSeconds);

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
            for (int f = 0; f < static_cast<int>(walkSeconds * 60) && at < 0; ++f)
            {
                int there = 0;
                for (std::size_t i = 0; i < team.size(); ++i)
                {
                    team[i].others.clear();
                    for (std::size_t j = 0; j < team.size(); ++j)
                        if (j != i) team[i].others.push_back({team[j].x, team[j].y});
                    const auto s = team[i].Step(f / 60.0, 1 / 60.0, 145, floor, clear);
                    there += std::hypot(s.x - t.at[0], s.y - t.at[1]) < team[i].arrive * 3;
                }
                for (std::size_t i = 0; i < team.size(); ++i)
                    for (std::size_t j = i + 1; j < team.size(); ++j)
                        if (f > 120) closest = std::min(closest, std::hypot(team[i].x - team[j].x, team[i].y - team[j].y));
                if (there == static_cast<int>(team.size())) at = f / 60.0;
            }
            int recoveries = 0;
            for (const auto& w : team) recoveries += w.stuckRecoveries;
            ++squads;
            squadsArrived += at >= 0;
            std::printf("  squad %s -> %-7s %s in %5.1f s; closest two bodies %.0f cm; stuck recoveries %d\n", side == &tSpawns ? "T " : "CT", t.name.c_str(), at >= 0 ? "arrived" : "STUCK  ",
                        at >= 0 ? at : walkSeconds, closest, recoveries);
        }
    std::printf("squads: %d/%d arrived\n", squadsArrived, squads);
    arrived += squadsArrived - squads; // a squad that didn't arrive fails the run
    return arrived == runs && linked == pairs ? 0 : 1;
}

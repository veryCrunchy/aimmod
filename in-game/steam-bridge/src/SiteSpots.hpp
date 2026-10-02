#pragma once
// Where bots hold a site or a planted bomb (BotOrders.hpp `area` and `from` rows, written back as
// bot-spots.tsv), worked out on the bots' nav grid: no hand-placed positions, so it works on any
// ported map.
//  - Entrances: the ways the other side comes in. A* from each place it comes from (the defenders'
//    spawn, the other site, the attackers' spawn) to the centre; the point `entry` cm along the way
//    from the centre is that way's entrance, twice that out its outer point (a lurker's mark).
//    Another way from the same place: A* again around the entrances found so far (zones it keeps
//    out of), kept when it comes in somewhere else and is not much longer.
//  - Spots: grid points between rmin and rmax from the centre, on its level and reachable over the
//    grid within two rmax of walking, a few cells apart. For each, line traces from a standing eye
//    to the bomb (just above the floor at the centre) and to each entrance at chest height; its
//    cover is how many of the four grid directions are walled within two cells.
//  - Lurk spots: near each outer point, off the way itself, with the way in sight.
// Worked out a trace budget at a time, one A* per step, so it never stalls a frame. Pure: the traces
// come from the mod, so it is tested without the game. Positions in world units (cm), z the floor.

#include "NavGrid.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <limits>
#include <map>
#include <optional>
#include <queue>
#include <string>
#include <vector>

namespace bridge::ghost
{
    struct SpotArea
    {
        using Clear = NavGrid::Clear;
        struct Request
        {
            std::string key;
            std::array<double, 3> centre{};
            double rmin = 300, rmax = 3000, entry = 1500;
            std::vector<std::array<double, 3>> sources;
            bool operator==(const Request&) const = default;
        };
        struct Entrance
        {
            std::array<double, 3> at{}, outer{};
            double pathCm = 0; // the whole way from its source
            int source = 0;
            std::vector<int> way; // grid nodes of the way from the outer point to the entrance
            std::vector<int> path; // the whole way, source to centre (another way keeps out of it)
            std::optional<std::array<double, 3>> lurk;
        };
        struct Spot
        {
            std::array<double, 3> at{};
            bool bomb = false;  // the bomb (the centre) in sight
            unsigned mask = 0;  // the entrances in sight (bit i: entrance i)
            int cover = 0;      // walled grid directions within two cells (0..4)
            double walk = 0;    // walking distance from the centre over the grid
        };
        static constexpr std::size_t MaxEntrances = 4, MaxSpots = 48, MaxCandidates = 600;
        static constexpr int MaxAlternates = 2;
        static constexpr double BombAbove = 25, ShortOfTarget = 40;

        Request request;
        std::vector<Entrance> entrances;
        std::vector<Spot> spots;
        enum class Phase { Paths, Candidates, Traces, Lurk, Done };
        Phase phase = Phase::Paths;
        bool noGrid = false; // the centre is on no grid point
        int traces = 0;

        // Working state.
        std::size_t sourceIndex = 0;
        int alternate = 0;
        double shortest = 0;
        int centreNode = -1;
        std::vector<int> candidates;
        std::vector<double> candidateWalk;
        std::size_t cursor = 0;
        std::vector<Spot> found;
        std::size_t lurkIndex = 0;

        bool Done() const { return phase == Phase::Done; }
        void Start(const Request& r)
        {
            *this = SpotArea{};
            request = r;
        }

        static double Hyp(const std::array<double, 3>& a, const std::array<double, 3>& b) { return std::hypot(a[0] - b[0], a[1] - b[1]); }
        static std::array<double, 3> At(const NavGrid& g, int n)
        {
            const auto& p = g.nodes[static_cast<std::size_t>(n)];
            return {p.x, p.y, p.z};
        }
        // The eye of a body standing on a floor, and the chest of one coming through.
        static double EyeAbove(const NavGrid& g) { return g.halfHeight * 1.73; }
        static double ChestAbove(const NavGrid& g) { return g.halfHeight * 1.3; }
        // A sight line, stopping a little short of the target (the bomb's own model, a body there).
        static bool Sees(const Clear& clear, const std::array<double, 3>& eye, const std::array<double, 3>& to, int& used)
        {
            const double dx = to[0] - eye[0], dy = to[1] - eye[1], dz = to[2] - eye[2], d = std::sqrt(dx * dx + dy * dy + dz * dz);
            if (d < ShortOfTarget * 2) return true;
            const double k = (d - ShortOfTarget) / d;
            ++used;
            return clear(eye[0], eye[1], eye[2], eye[0] + dx * k, eye[1] + dy * k, eye[2] + dz * k);
        }

        // One step: at most one A*, or about `budget` traces. Returns true when the results changed.
        bool Step(const NavGrid& g, int budget, const Clear& clear)
        {
            if (Done()) return false;
            if (centreNode < 0)
            {
                centreNode = g.Nearest(request.centre[0], request.centre[1], request.centre[2] + g.halfHeight);
                if (centreNode < 0)
                {
                    noGrid = true;
                    phase = Phase::Done;
                    return true;
                }
            }
            int used = 0;
            switch (phase)
            {
            case Phase::Paths: StepPath(g); break;
            case Phase::Candidates: Collect(g); break;
            case Phase::Traces:
                while (cursor < candidates.size() && used + 1 + static_cast<int>(entrances.size()) <= budget) TraceCandidate(g, clear, used);
                if (cursor >= candidates.size()) phase = Phase::Lurk;
                break;
            case Phase::Lurk:
                if (lurkIndex < entrances.size()) Lurk(g, clear, entrances[lurkIndex++], used);
                if (lurkIndex >= entrances.size())
                {
                    Select();
                    phase = Phase::Done;
                    traces += used;
                    return true;
                }
                break;
            case Phase::Done: break;
            }
            traces += used;
            return false;
        }

        // One way in: from the current source (or around the entrances found, for another way).
        void StepPath(const NavGrid& g)
        {
            if (sourceIndex >= request.sources.size() || entrances.size() >= MaxEntrances)
            {
                phase = Phase::Candidates;
                return;
            }
            const auto& from = request.sources[sourceIndex];
            // Another way: kept out of the ways in found so far, along them from just inside their
            // entrance to well out past it (circles a few cells wide).
            NavGrid::Avoid avoid;
            if (alternate > 0)
                for (const auto& e : entrances)
                {
                    std::vector<std::array<double, 3>> along;
                    double out = 0;
                    for (std::size_t i = e.path.size(); i-- > 1;)
                    {
                        out += Hyp(At(g, e.path[i]), At(g, e.path[i - 1]));
                        if (out >= request.entry * 0.75 && out <= request.entry * 1.8) along.push_back(At(g, e.path[i - 1]));
                    }
                    const std::size_t count = std::min<std::size_t>(along.size(), 6);
                    const double radius = std::max(g.spacing * 3, request.entry * 0.4);
                    for (std::size_t k = 0; k < count; ++k)
                    {
                        const auto& p = along[count == 1 ? 0 : k * (along.size() - 1) / (count - 1)];
                        // Never round the place it comes from (it has to get out somewhere).
                        if (Hyp(p, from) > radius * 1.2) avoid.push_back({p[0], p[1], p[2], radius, NavGrid::AvoidAlways});
                    }
                }
            bool reached = false;
            auto nodes = g.PathNodes(from, request.centre, reached, avoid);
            bool keep = reached && nodes.size() >= 2;
            double length = 0;
            for (std::size_t i = 1; i < nodes.size(); ++i) length += Hyp(At(g, nodes[i - 1]), At(g, nodes[i]));
            if (keep && length < request.entry * 0.5) keep = false; // it starts on the site
            if (keep && alternate > 0 && length > shortest * 2.2) keep = false;
            // Through a zone all the same (no other way): not another way.
            if (keep && alternate > 0)
                for (const int n : nodes)
                    if (NavGrid::ZoneCost(g.nodes[static_cast<std::size_t>(n)].x, g.nodes[static_cast<std::size_t>(n)].y, g.nodes[static_cast<std::size_t>(n)].z, avoid) > 0) { keep = false; break; }
            if (keep)
            {
                // From the centre end outwards: the entrance at `entry` along the way, the outer point at twice that.
                Entrance e;
                e.pathCm = length;
                e.source = static_cast<int>(sourceIndex);
                double along = 0;
                std::size_t entranceAt = 0, outerAt = 0;
                bool haveEntrance = false;
                for (std::size_t i = nodes.size() - 1; i > 0; --i)
                {
                    along += Hyp(At(g, nodes[i]), At(g, nodes[i - 1]));
                    if (!haveEntrance && along >= request.entry) { entranceAt = i - 1; haveEntrance = true; }
                    if (along >= request.entry * 2) { outerAt = i - 1; break; }
                }
                if (!haveEntrance) entranceAt = 0;
                if (outerAt > entranceAt) outerAt = entranceAt;
                e.at = At(g, nodes[entranceAt]);
                e.outer = At(g, nodes[outerAt]);
                for (std::size_t i = outerAt; i <= entranceAt; ++i) e.way.push_back(nodes[i]);
                e.path = nodes;
                // Somewhere else: well apart, or from another direction as seen from the centre.
                const auto& c = request.centre;
                for (const auto& other : entrances)
                {
                    const double ax = other.at[0] - c[0], ay = other.at[1] - c[1], bx = e.at[0] - c[0], by = e.at[1] - c[1];
                    const double cosine = (ax * bx + ay * by) / std::max(1.0, std::hypot(ax, ay) * std::hypot(bx, by));
                    if (Hyp(other.at, e.at) < request.entry * 0.5 && cosine > std::cos(28 * 3.14159265358979 / 180)) keep = false;
                }
                if (keep)
                {
                    if (alternate == 0) shortest = length;
                    entrances.push_back(std::move(e));
                }
            }
            // The next way from this source, or the next source.
            if (keep && alternate < MaxAlternates) ++alternate;
            else
            {
                ++sourceIndex;
                alternate = 0;
            }
        }

        // The candidate spots: grid points within reach, sampled to at most MaxCandidates.
        void Collect(const NavGrid& g)
        {
            std::vector<double> walk(g.nodes.size(), std::numeric_limits<double>::infinity());
            using Item = std::pair<double, int>;
            std::priority_queue<Item, std::vector<Item>, std::greater<Item>> open;
            walk[static_cast<std::size_t>(centreNode)] = 0;
            open.push({0, centreNode});
            const double limit = request.rmax * 2, diagonal = g.spacing * std::sqrt(2.0);
            while (!open.empty())
            {
                const auto [d, u] = open.top();
                open.pop();
                if (d > walk[static_cast<std::size_t>(u)] + 1e-6) continue;
                const auto relax = [&](int v, double l) {
                    if (v < 0) return;
                    const double c = d + l;
                    if (c > limit || c >= walk[static_cast<std::size_t>(v)]) return;
                    walk[static_cast<std::size_t>(v)] = c;
                    open.push({c, v});
                };
                for (const int v : g.nodes[static_cast<std::size_t>(u)].link) relax(v, g.spacing);
                for (const int dx : {1, -1})
                    for (const int dy : {1, -1}) relax(g.Diagonal(u, dx, dy), diagonal);
            }
            const auto centre = At(g, centreNode);
            const double level = std::max(450.0, request.rmax * 0.2);
            std::vector<int> all;
            for (std::size_t n = 0; n < g.nodes.size(); ++n)
            {
                if (!std::isfinite(walk[n])) continue;
                const auto p = At(g, static_cast<int>(n));
                const double d = Hyp(p, centre);
                if (d < request.rmin || d > request.rmax || std::fabs(p[2] - centre[2]) > level) continue;
                bool nearEntrance = false;
                for (const auto& e : entrances) nearEntrance = nearEntrance || Hyp(p, e.at) < request.entry * 0.3;
                if (!nearEntrance) all.push_back(static_cast<int>(n));
            }
            int stride = 1;
            while (all.size() / static_cast<std::size_t>(stride * stride) > MaxCandidates) ++stride;
            const auto keep = [stride](int v) { return ((v % stride) + stride) % stride == 0; };
            for (const int n : all)
            {
                const auto& node = g.nodes[static_cast<std::size_t>(n)];
                if (keep(node.ix) && keep(node.iy))
                {
                    candidates.push_back(n);
                    candidateWalk.push_back(walk[static_cast<std::size_t>(n)]);
                }
            }
            phase = Phase::Traces;
        }

        // How walled-in a grid point is: the grid directions with a wall (no floor) within two cells.
        static int Cover(const NavGrid& g, int n)
        {
            int cover = 0;
            for (int d = 0; d < 4; ++d) cover += g.Openness(n, d, 2) < 2;
            return cover;
        }

        void TraceCandidate(const NavGrid& g, const Clear& clear, int& used)
        {
            const int n = candidates[cursor];
            const double walk = candidateWalk[cursor];
            ++cursor;
            const auto p = At(g, n);
            const std::array<double, 3> eye{p[0], p[1], p[2] + EyeAbove(g)};
            const auto c = At(g, centreNode);
            Spot s;
            s.at = p;
            s.walk = walk;
            s.bomb = Sees(clear, eye, {c[0], c[1], c[2] + BombAbove}, used);
            for (std::size_t i = 0; i < entrances.size(); ++i)
            {
                const auto& e = entrances[i].at;
                if (Sees(clear, eye, {e[0], e[1], e[2] + ChestAbove(g)}, used)) s.mask |= 1u << i;
            }
            if (!s.bomb && s.mask == 0) return;
            s.cover = Cover(g, n);
            found.push_back(s);
        }

        // A lurker's spot by an entrance's outer point: off the way, the way in sight, walled in.
        void Lurk(const NavGrid& g, const Clear& clear, Entrance& e, int& used)
        {
            if (Hyp(e.outer, e.at) < g.spacing * 2) return;
            const int cx = static_cast<int>(std::lround(e.outer[0] / g.spacing)), cy = static_cast<int>(std::lround(e.outer[1] / g.spacing));
            const int r = std::max(2, static_cast<int>(std::lround(std::min(700.0, request.entry * 0.4) / g.spacing)));
            const std::array<double, 3> mark{e.outer[0], e.outer[1], e.outer[2] + ChestAbove(g)};
            double best = -1;
            for (int ox = -r; ox <= r; ++ox)
                for (int oy = -r; oy <= r; ++oy)
                {
                    const auto it = g.cells.find({cx + ox, cy + oy});
                    if (it == g.cells.end()) continue;
                    for (const int n : it->second)
                    {
                        const auto p = At(g, n);
                        if (std::fabs(p[2] - e.outer[2]) > 300) continue;
                        double off = std::numeric_limits<double>::infinity();
                        for (const int w : e.way) off = std::min(off, Hyp(p, At(g, w)));
                        if (off < g.spacing * 1.8) continue;
                        const double score = Cover(g, n) * 1000 + std::min(off, 600.0);
                        if (score <= best) continue;
                        if (!Sees(clear, {p[0], p[1], p[2] + EyeAbove(g)}, mark, used)) continue;
                        best = score;
                        e.lurk = p;
                    }
                }
        }

        static int Bits(unsigned m)
        {
            int n = 0;
            for (; m; m &= m - 1) ++n;
            return n;
        }
        // The spots kept: the best few for each entrance and for the bomb, then the best of the rest.
        void Select()
        {
            const auto score = [](const Spot& s) { return (s.bomb ? 3.0 : 0.0) + Bits(s.mask) * 1.5 + s.cover; };
            std::vector<std::size_t> order(found.size());
            for (std::size_t i = 0; i < order.size(); ++i) order[i] = i;
            std::stable_sort(order.begin(), order.end(), [&](std::size_t a, std::size_t b) { return score(found[a]) > score(found[b]); });
            std::vector<bool> taken(found.size(), false);
            const auto take = [&](auto&& want, std::size_t count) {
                for (const std::size_t i : order)
                {
                    if (count == 0 || spots.size() >= MaxSpots) return;
                    if (taken[i] || !want(found[i])) continue;
                    taken[i] = true;
                    spots.push_back(found[i]);
                    --count;
                }
            };
            for (std::size_t e = 0; e < entrances.size(); ++e) take([e](const Spot& s) { return (s.mask >> e & 1u) != 0; }, 8);
            take([](const Spot& s) { return s.bomb; }, 10);
            take([](const Spot&) { return true; }, MaxSpots);
            found.clear();
            candidates.clear();
            candidateWalk.clear();
        }
    };

    // bot-spots.tsv (AimModSteam -> service): what each requested area came to.
    //   AIMMOD_SPOTS_1\t<unix ms>
    //   area\t<key>\t<growing|done|nogrid>\t<entrances>\t<spots>
    //   entrance\t<key>\t<i>\t<x>\t<y>\t<z>\t<outer x>\t<outer y>\t<outer z>\t<path cm>\t<source>
    //   lurk\t<key>\t<i>\t<x>\t<y>\t<z>
    //   spot\t<key>\t<x>\t<y>\t<z>\t<bomb 0|1>\t<entrance mask>\t<cover 0..4>\t<walk cm>
    inline std::string FormatSpots(std::int64_t unixMs, const std::map<std::string, SpotArea>& areas)
    {
        std::string text = "AIMMOD_SPOTS_1\t" + std::to_string(unixMs) + "\n";
        char line[256];
        for (const auto& [key, a] : areas)
        {
            std::snprintf(line, sizeof(line), "area\t%s\t%s\t%zu\t%zu\n", key.c_str(), a.noGrid ? "nogrid" : a.Done() ? "done" : "growing", a.Done() ? a.entrances.size() : 0, a.spots.size());
            text += line;
            if (!a.Done()) continue;
            for (std::size_t i = 0; i < a.entrances.size(); ++i)
            {
                const auto& e = a.entrances[i];
                std::snprintf(line, sizeof(line), "entrance\t%s\t%zu\t%.0f\t%.0f\t%.0f\t%.0f\t%.0f\t%.0f\t%.0f\t%d\n", key.c_str(), i, e.at[0], e.at[1], e.at[2], e.outer[0], e.outer[1], e.outer[2], e.pathCm, e.source);
                text += line;
                if (e.lurk)
                {
                    std::snprintf(line, sizeof(line), "lurk\t%s\t%zu\t%.0f\t%.0f\t%.0f\n", key.c_str(), i, (*e.lurk)[0], (*e.lurk)[1], (*e.lurk)[2]);
                    text += line;
                }
            }
            for (const auto& s : a.spots)
            {
                std::snprintf(line, sizeof(line), "spot\t%s\t%.0f\t%.0f\t%.0f\t%d\t%u\t%d\t%.0f\n", key.c_str(), s.at[0], s.at[1], s.at[2], s.bomb ? 1 : 0, s.mask, s.cover, s.walk);
                text += line;
            }
        }
        return text;
    }
} // namespace bridge::ghost

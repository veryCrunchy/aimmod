#pragma once
// Navigation for the bots on maps without nav data (ported maps ship spawns and bomb sites only).
// A grid of floor points, grown outwards from seeds (spawns, waypoints, goals) by line traces:
// a neighbour is linked when the floor between is walkable (no step over stepUp per probe, no
// drop over stepDown, nothing solid across at foot or waist height). Several floors at one grid
// cell (a bridge over a tunnel) are separate nodes. Grown a trace budget per tick, shared by every
// walker on the map; paths are A* over its links. Pure: the mod supplies the traces, so it is
// tested without the game. Positions in world units (cm), z is the floor.

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <deque>
#include <functional>
#include <limits>
#include <map>
#include <optional>
#include <queue>
#include <utility>
#include <vector>

namespace bridge::ghost
{
    struct NavGrid
    {
        using Floor = std::function<std::optional<double>(double x, double y, double z)>;
        using Clear = std::function<bool(double ax, double ay, double az, double bx, double by, double bz)>;

        double spacing = 120;    // cm between grid points
        double stepUp = 45, stepDown = 70, halfHeight = 88;
        static constexpr double FootAbove = 30;
        std::size_t maxNodes = 40000;

        struct Node
        {
            double x = 0, y = 0, z = 0; // grid point and its floor
            int ix = 0, iy = 0;
            std::array<int, 4> link{-1, -1, -1, -1}; // +x, -x, +y, -y
        };
        std::vector<Node> nodes;
        std::map<std::pair<int, int>, std::vector<int>> cells;
        std::deque<std::pair<int, int>> frontier; // (node, direction) still to check
        std::int64_t traces = 0;
        bool Done() const { return frontier.empty(); }

        static constexpr int Dx[4]{1, -1, 0, 0}, Dy[4]{0, 0, 1, -1}, Opposite[4]{1, 0, 3, 2};

        int Find(int ix, int iy, double floorZ) const
        {
            const auto it = cells.find({ix, iy});
            if (it == cells.end()) return -1;
            for (const int n : it->second)
                if (std::fabs(nodes[static_cast<std::size_t>(n)].z - floorZ) < stepUp) return n;
            return -1;
        }
        int Add(int ix, int iy, double floorZ)
        {
            if (nodes.size() >= maxNodes) return -1;
            Node n;
            n.ix = ix;
            n.iy = iy;
            n.x = ix * spacing;
            n.y = iy * spacing;
            n.z = floorZ;
            const int id = static_cast<int>(nodes.size());
            nodes.push_back(n);
            cells[{ix, iy}].push_back(id);
            for (int d = 0; d < 4; ++d) frontier.push_back({id, d});
            return id;
        }

        // A point the grid must reach (a spawn, a waypoint, a goal): the floor at the grid point
        // nearest to it (or one next to that, when it lies in a wall). Returns the node, or -1.
        int Seed(double x, double y, double z, const Floor& floor)
        {
            const int cx = static_cast<int>(std::lround(x / spacing)), cy = static_cast<int>(std::lround(y / spacing));
            for (int r = 0; r <= 1; ++r)
                for (int ox = -r; ox <= r; ++ox)
                    for (int oy = -r; oy <= r; ++oy)
                    {
                        if (r > 0 && std::abs(ox) != r && std::abs(oy) != r) continue;
                        const int ix = cx + ox, iy = cy + oy;
                        ++traces;
                        const auto f = floor(ix * spacing, iy * spacing, z + stepUp);
                        if (!f || *f < z - halfHeight * 3 - stepDown) continue;
                        if (const int have = Find(ix, iy, *f); have >= 0) return have;
                        return Add(ix, iy, *f);
                    }
            return -1;
        }

        // Checks up to `budget` traces' worth of links. Returns the traces used.
        int Grow(int budget, const Floor& floor, const Clear& clear)
        {
            int used = 0;
            while (used < budget && !frontier.empty())
            {
                const auto [id, d] = frontier.front();
                frontier.pop_front();
                Node from = nodes[static_cast<std::size_t>(id)];
                if (from.link[static_cast<std::size_t>(d)] >= 0) continue;
                const int ix = from.ix + Dx[d], iy = from.iy + Dy[d];
                const double tx = ix * spacing, ty = iy * spacing;
                const double mx = (from.x + tx) / 2, my = (from.y + ty) / 2;
                // Two probes (the middle, then the next grid point), each within a step of the last.
                used += 2;
                const auto fm = floor(mx, my, from.z + halfHeight + stepUp);
                if (!fm || *fm > from.z + stepUp || *fm < from.z - stepDown) continue;
                const auto ft = floor(tx, ty, *fm + halfHeight + stepUp);
                if (!ft || *ft > *fm + stepUp || *ft < *fm - stepDown) continue;
                // Nothing across at foot height, nor at the body's waist (rails, low walls).
                used += 2;
                if (!clear(from.x, from.y, from.z + FootAbove, tx, ty, *ft + FootAbove)) continue;
                if (!clear(from.x, from.y, from.z + halfHeight, tx, ty, *ft + halfHeight)) continue;
                int to = Find(ix, iy, *ft);
                if (to < 0) to = Add(ix, iy, *ft);
                if (to < 0) continue;
                nodes[static_cast<std::size_t>(id)].link[static_cast<std::size_t>(d)] = to;
                // Both ways unless it was a drop (you can't climb back up a ledge).
                auto& back = nodes[static_cast<std::size_t>(to)].link[static_cast<std::size_t>(Opposite[d])];
                if (back < 0 && *ft >= from.z - stepUp) back = id;
            }
            traces += used;
            return used;
        }

        // The node nearest to a point (its floor near z), or -1.
        int Nearest(double x, double y, double z) const
        {
            const int cx = static_cast<int>(std::lround(x / spacing)), cy = static_cast<int>(std::lround(y / spacing));
            int best = -1;
            double bestD = std::numeric_limits<double>::infinity();
            for (int ox = -2; ox <= 2; ++ox)
                for (int oy = -2; oy <= 2; ++oy)
                {
                    const auto it = cells.find({cx + ox, cy + oy});
                    if (it == cells.end()) continue;
                    for (const int n : it->second)
                    {
                        const auto& p = nodes[static_cast<std::size_t>(n)];
                        // A floor below the point counts as its own (z is often a body centre); far
                        // above or below is another level.
                        const double dz = z - p.z;
                        const double level = dz >= -stepUp && dz <= halfHeight * 3 + stepUp ? 0 : std::fabs(dz) * 4;
                        const double dist = std::hypot(p.x - x, p.y - y) + level;
                        if (dist < bestD) { bestD = dist; best = n; }
                    }
                }
            return best;
        }

        // A* from the node nearest `from` to the node nearest `to`: the floor points to walk, in order.
        // `reached` is false when the goal isn't connected (yet): the path then ends at the node
        // closest to it.
        std::vector<std::array<double, 3>> Path(const std::array<double, 3>& from, const std::array<double, 3>& to, bool& reached) const
        {
            reached = false;
            std::vector<std::array<double, 3>> out;
            const int a = Nearest(from[0], from[1], from[2]), b = Nearest(to[0], to[1], to[2]);
            if (a < 0) return out;
            const auto& goal = b >= 0 ? nodes[static_cast<std::size_t>(b)] : nodes[static_cast<std::size_t>(a)];
            const double gx = b >= 0 ? goal.x : to[0], gy = b >= 0 ? goal.y : to[1];
            const auto h = [&](int n) { const auto& p = nodes[static_cast<std::size_t>(n)]; return std::hypot(p.x - gx, p.y - gy); };
            std::vector<double> cost(nodes.size(), std::numeric_limits<double>::infinity());
            std::vector<int> parent(nodes.size(), -1);
            using Item = std::pair<double, int>;
            std::priority_queue<Item, std::vector<Item>, std::greater<Item>> open;
            cost[static_cast<std::size_t>(a)] = 0;
            open.push({h(a), a});
            int closest = a;
            double closestH = h(a);
            while (!open.empty())
            {
                const auto [f, u] = open.top();
                open.pop();
                if (f > cost[static_cast<std::size_t>(u)] + h(u) + 1e-6) continue;
                if (u == b) { closest = b; reached = true; break; }
                if (const double hu = h(u); hu < closestH) { closestH = hu; closest = u; }
                for (const int v : nodes[static_cast<std::size_t>(u)].link)
                {
                    if (v < 0) continue;
                    const double c = cost[static_cast<std::size_t>(u)] + spacing;
                    if (c >= cost[static_cast<std::size_t>(v)]) continue;
                    cost[static_cast<std::size_t>(v)] = c;
                    parent[static_cast<std::size_t>(v)] = u;
                    open.push({c + h(v), v});
                }
            }
            for (int n = closest; n >= 0; n = parent[static_cast<std::size_t>(n)])
            {
                const auto& p = nodes[static_cast<std::size_t>(n)];
                out.push_back({p.x, p.y, p.z});
                if (n == a) break;
            }
            std::reverse(out.begin(), out.end());
            return out;
        }
    };
} // namespace bridge::ghost

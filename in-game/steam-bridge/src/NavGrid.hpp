#pragma once
// Navigation for the bots on maps without nav data (ported maps ship spawns and bomb sites only).
// A grid of floor points, grown outwards from seeds (spawns, waypoints, goals) by line traces:
// a neighbour is linked when the floor between is walkable (no step over stepUp per probe, no
// drop over stepDown, nothing solid across at foot or waist height). Several floors at one grid
// cell (a bridge over a tunnel) are separate nodes. Grown a trace budget per tick, shared by every
// walker on the map; paths are A* over its links (and the diagonals between two of them, so a path
// across open floor is no staircase), around zones it is told to keep out of (a smoke, a fire)
// when it can. Pure: the mod supplies the traces, so it is tested without the game. Positions in
// world units (cm), z is the floor.

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
            // The diagonal steps (+x+y, +x-y, -x+y, -x-y): the node reached, -1 none (a corner of a
            // prop in the way), Unchecked until traced.
            std::array<int, 4> diag{Unchecked, Unchecked, Unchecked, Unchecked};
        };
        static constexpr int Unchecked = -2;
        std::vector<Node> nodes;
        std::map<std::pair<int, int>, std::vector<int>> cells;
        std::deque<std::pair<int, int>> frontier; // (node, direction) still to check
        std::size_t diagCursor = 0;               // diagonals are traced for nodes from here on, once the links are in
        std::int64_t traces = 0;
        bool Done() const { return frontier.empty() && diagCursor >= nodes.size(); }

        static constexpr int Dx[4]{1, -1, 0, 0}, Dy[4]{0, 0, 1, -1}, Opposite[4]{1, 0, 3, 2};
        static constexpr int DiagX[4]{1, 1, -1, -1}, DiagY[4]{1, -1, 1, -1};
        static int DiagIndex(int dx, int dy) { return (dx > 0 ? 0 : 2) + (dy > 0 ? 0 : 1); }

        // Zones to keep out of (a smoke, a fire): x, y, z, radius, cost per grid step inside. A cost of
        // AvoidAlways or more is never walked through unless there is no other way at all.
        using Avoid = std::vector<std::array<double, 5>>;
        static constexpr double AvoidAlways = 1000;
        static bool InZone(double px, double py, double pz, const std::array<double, 5>& zone)
        {
            return std::hypot(px - zone[0], py - zone[1]) < zone[3] && std::fabs(pz - zone[2]) < std::max(zone[3], 300.0);
        }
        static double ZoneCost(double px, double py, double pz, const Avoid& avoid)
        {
            double extra = 0;
            for (const auto& zone : avoid)
                if (zone[4] > 0 && InZone(px, py, pz, zone)) extra += zone[4] >= AvoidAlways ? 1e6 : zone[4];
            return extra;
        }
        // Whether the straight line a-b passes through a zone (on its level) that costs anything.
        static bool CrossesZone(const std::array<double, 3>& a, const std::array<double, 3>& b, const std::array<double, 5>& zone)
        {
            if (zone[4] <= 0 || std::fabs((a[2] + b[2]) / 2 - zone[2]) > std::max(zone[3], 300.0) + std::fabs(a[2] - b[2]) / 2) return false;
            const double dx = b[0] - a[0], dy = b[1] - a[1], len = dx * dx + dy * dy;
            const double u = len > 1e-9 ? std::clamp(((zone[0] - a[0]) * dx + (zone[1] - a[1]) * dy) / len, 0.0, 1.0) : 0.0;
            return std::hypot(a[0] + dx * u - zone[0], a[1] + dy * u - zone[1]) < zone[3];
        }

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
        // nearest to it, or one a little further out when that one lies in a wall or on top of a prop
        // (a crate beside a spawn: its top is no floor to start from). Returns the node, or -1.
        int Seed(double x, double y, double z, const Floor& floor)
        {
            const int cx = static_cast<int>(std::lround(x / spacing)), cy = static_cast<int>(std::lround(y / spacing));
            ++traces;
            const auto below = floor(x, y, z + stepUp); // the floor at the point itself
            for (int r = 0; r <= 2; ++r)
                for (int ox = -r; ox <= r; ++ox)
                    for (int oy = -r; oy <= r; ++oy)
                    {
                        if (r > 0 && std::abs(ox) != r && std::abs(oy) != r) continue;
                        const int ix = cx + ox, iy = cy + oy;
                        ++traces;
                        const auto f = floor(ix * spacing, iy * spacing, z + stepUp);
                        if (!f || *f < z - halfHeight * 3 - stepDown) continue;
                        if (below && *f > *below + stepUp) continue;
                        if (const int have = Find(ix, iy, *f); have >= 0) return have;
                        return Add(ix, iy, *f);
                    }
            return -1;
        }

        // Checks up to `budget` traces' worth of links. Returns the traces used.
        int Grow(int budget, const Floor& floor, const Clear& clear)
        {
            int used = 0;
            while (used < budget && !Done())
            {
                if (frontier.empty())
                {
                    used += CheckDiagonals(static_cast<int>(diagCursor++), floor, clear);
                    continue;
                }
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
                // New links (a grid grown again from a new seed): their diagonals are checked too.
                diagCursor = std::min(diagCursor, static_cast<std::size_t>(std::min(id, to)));
            }
            traces += used;
            return used;
        }

        // The node a diagonal step reaches through both of its corner cells (they must agree), before
        // any trace; -1 when a corner is missing.
        int Corners(int a, int k) const
        {
            const int dirX = DiagX[k] > 0 ? 0 : 1, dirY = DiagY[k] > 0 ? 2 : 3;
            const int viaX = Link(a, dirX), viaY = Link(a, dirY);
            if (viaX < 0 || viaY < 0) return -1;
            const int n1 = Link(viaX, dirY), n2 = Link(viaY, dirX);
            return n1 >= 0 && n1 == n2 ? n1 : -1;
        }
        // Traces node a's unchecked diagonal steps: the floor at the middle (where four cells meet, a
        // prop's corner can stand) within a step of both ends, nothing across at foot or waist height.
        // Returns the traces used.
        int CheckDiagonals(int a, const Floor& floor, const Clear& clear)
        {
            int used = 0;
            for (int k = 0; k < 4; ++k)
            {
                if (nodes[static_cast<std::size_t>(a)].diag[static_cast<std::size_t>(k)] != Unchecked) continue;
                const int b = Corners(a, k);
                if (b < 0) continue; // stays unchecked: no diagonal without both corners anyway
                auto& back = nodes[static_cast<std::size_t>(b)].diag[static_cast<std::size_t>(3 - k)];
                int result = -1;
                if (back != Unchecked && Corners(b, 3 - k) == a) result = back >= 0 ? b : -1; // traced the other way
                else
                {
                    const Node& A = nodes[static_cast<std::size_t>(a)];
                    const Node& B = nodes[static_cast<std::size_t>(b)];
                    used += 3;
                    const auto fm = floor((A.x + B.x) / 2, (A.y + B.y) / 2, std::max(A.z, B.z) + halfHeight + stepUp);
                    const bool ok = fm && *fm <= A.z + stepUp && *fm >= A.z - stepDown && *fm <= B.z + stepUp && *fm >= B.z - stepDown &&
                                    clear(A.x, A.y, A.z + FootAbove, B.x, B.y, B.z + FootAbove) && clear(A.x, A.y, A.z + halfHeight, B.x, B.y, B.z + halfHeight);
                    result = ok ? b : -1;
                    if (Corners(b, 3 - k) == a) back = ok ? a : -1;
                }
                nodes[static_cast<std::size_t>(a)].diag[static_cast<std::size_t>(k)] = result;
            }
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

        // The link from a to its neighbour in direction d (-1: none).
        int Link(int a, int d) const { return nodes[static_cast<std::size_t>(a)].link[static_cast<std::size_t>(d)]; }
        int Dir(int a, int b) const
        {
            for (int d = 0; d < 4; ++d)
                if (Link(a, d) == b) return d;
            return -1;
        }

        // The node a diagonal step from a reaches (dx, dy: +-1 each): through both of its corner cells,
        // which must agree (the body keeps clear of a corner), and traced clear; -1 otherwise.
        int Diagonal(int a, int dx, int dy) const
        {
            const int k = DiagIndex(dx, dy);
            const int b = Corners(a, k);
            return b >= 0 && nodes[static_cast<std::size_t>(a)].diag[static_cast<std::size_t>(k)] == b ? b : -1;
        }
        // A single grid step from a to b: a link or a diagonal.
        bool Adjacent(int a, int b) const
        {
            if (a < 0 || b < 0) return false;
            if (Dir(a, b) >= 0) return true;
            for (const int d : nodes[static_cast<std::size_t>(a)].diag)
                if (d == b) return true;
            return false;
        }

        // Stuck recovery: a link the body could not pass after all is taken out (both ways); a
        // diagonal step is no longer taken (the links on its two sides stay).
        void Block(int a, int b)
        {
            if (a < 0 || b < 0 || a >= static_cast<int>(nodes.size()) || b >= static_cast<int>(nodes.size())) return;
            for (auto& l : nodes[static_cast<std::size_t>(a)].link)
                if (l == b) l = -1;
            for (auto& l : nodes[static_cast<std::size_t>(b)].link)
                if (l == a) l = -1;
            for (auto& l : nodes[static_cast<std::size_t>(a)].diag)
                if (l == b) l = -1;
            for (auto& l : nodes[static_cast<std::size_t>(b)].diag)
                if (l == a) l = -1;
            ++blocked;
        }
        int blocked = 0;
        // A grid point the body can't stand on after all: no link reaches it any more.
        void BlockNode(int n)
        {
            if (n < 0 || n >= static_cast<int>(nodes.size())) return;
            for (const int m : nodes[static_cast<std::size_t>(n)].link)
                if (m >= 0) Block(n, m);
            for (auto& l : nodes[static_cast<std::size_t>(n)].diag) l = -1;
            for (auto& other : nodes)
            {
                for (auto& l : other.link)
                    if (l == n) l = -1;
                for (auto& l : other.diag)
                    if (l == n) l = -1;
            }
        }

        // A straight walk from node a to node b over the grid alone (no traces): every grid cell the
        // line crosses has a floor point linked to the last one; a diagonal step needs both of its
        // corner cells, so the body keeps clear of a corner. For string-pulling paths.
        bool Straight(int a, int b) const
        {
            const auto& A = nodes[static_cast<std::size_t>(a)];
            const auto& B = nodes[static_cast<std::size_t>(b)];
            int cx = A.ix, cy = A.iy, cur = a;
            const int tx = B.ix, ty = B.iy;
            const double dx = tx - cx, dy = ty - cy;
            const int steps = static_cast<int>(std::max(std::fabs(dx), std::fabs(dy)) * 2);
            for (int s = 1; s <= steps && cur != b; ++s)
            {
                const double u = static_cast<double>(s) / steps;
                const int nx = static_cast<int>(std::lround(A.ix + dx * u)), ny = static_cast<int>(std::lround(A.iy + dy * u));
                if (nx == cx && ny == cy) continue;
                const auto step = [&](int from, int ddx, int ddy) -> int {
                    const int d = ddx > 0 ? 0 : ddx < 0 ? 1 : ddy > 0 ? 2 : 3;
                    return Link(from, d);
                };
                const int sx = nx - cx, sy = ny - cy;
                if (std::abs(sx) > 1 || std::abs(sy) > 1) return false;
                int next = -1;
                if (sx != 0 && sy != 0)
                {
                    // Diagonal: through both corner cells, and they must agree; not where the
                    // diagonal itself was traced blocked (a prop's corner where the cells meet).
                    const int viaX = step(cur, sx, 0), viaY = step(cur, 0, sy);
                    if (viaX < 0 || viaY < 0) return false;
                    const int n1 = step(viaX, 0, sy), n2 = step(viaY, sx, 0);
                    if (n1 < 0 || n1 != n2) return false;
                    if (nodes[static_cast<std::size_t>(cur)].diag[static_cast<std::size_t>(DiagIndex(sx, sy))] == -1) return false;
                    next = n1;
                }
                else next = step(cur, sx, sy);
                if (next < 0) return false;
                cur = next;
                cx = nx;
                cy = ny;
            }
            return cur == b;
        }

        // String-pulling: from each kept point, the farthest later one a straight walk reaches. A
        // shortcut never cuts across a zone to avoid that the path itself went around.
        std::vector<int> Smooth(const std::vector<int>& path, const Avoid& avoid = {}, std::size_t lookahead = 24) const
        {
            if (path.size() < 3) return path;
            const auto at = [&](int n) { const auto& p = nodes[static_cast<std::size_t>(n)]; return std::array<double, 3>{p.x, p.y, p.z}; };
            const auto acrossZone = [&](std::size_t i, std::size_t j) {
                for (const auto& zone : avoid)
                {
                    if (!CrossesZone(at(path[i]), at(path[j]), zone)) continue;
                    bool pathInside = false;
                    for (std::size_t k = i; k <= j && !pathInside; ++k)
                    {
                        const auto p = at(path[k]);
                        pathInside = InZone(p[0], p[1], p[2], zone);
                    }
                    if (!pathInside) return true;
                }
                return false;
            };
            std::vector<int> out{path.front()};
            std::size_t i = 0;
            while (i + 1 < path.size())
            {
                std::size_t j = std::min(path.size() - 1, i + lookahead);
                while (j > i + 1 && (!Straight(path[i], path[j]) || acrossZone(i, j))) --j;
                out.push_back(path[j]);
                i = j;
            }
            return out;
        }

        // How open the floor is from node a in direction d: grid points in a straight line (at most max).
        int Openness(int a, int d, int max = 24) const
        {
            int n = 0;
            for (int cur = a; n < max; ++n)
            {
                const int next = Link(cur, d);
                if (next < 0) break;
                cur = next;
            }
            return n;
        }

        // A grid point where the floor is only a cell or two wide across the way it is walked (from
        // `prev` to `next`): a doorway, a gap between props. No corner is cut there.
        bool Narrow(int prev, int n, int next) const
        {
            const auto& a = nodes[static_cast<std::size_t>(prev)];
            const auto& b = nodes[static_cast<std::size_t>(next)];
            const bool alongX = std::abs(b.ix - a.ix) >= std::abs(b.iy - a.iy);
            const int left = alongX ? 2 : 0, right = alongX ? 3 : 1;
            return Openness(n, left, 2) + Openness(n, right, 2) <= 1;
        }

        // A* over the grid: node ids from the node nearest `from` to the one nearest `to` (or to the
        // node closest to it, `reached` false, when it isn't connected yet). Steps go along the links,
        // and diagonally where both corner cells are linked; a step into a zone to avoid costs extra.
        std::vector<int> PathNodes(const std::array<double, 3>& from, const std::array<double, 3>& to, bool& reached, const Avoid& avoid = {}) const
        {
            reached = false;
            std::vector<int> out;
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
            const auto relax = [&](int u, int v, double length) {
                const auto& nu = nodes[static_cast<std::size_t>(u)];
                const auto& nv = nodes[static_cast<std::size_t>(v)];
                // A climb costs a little more than flat floor, so routes don't zigzag over steps.
                double c = cost[static_cast<std::size_t>(u)] + length + std::fabs(nv.z - nu.z) * 0.5;
                if (!avoid.empty()) c += ZoneCost(nv.x, nv.y, nv.z, avoid);
                if (c >= cost[static_cast<std::size_t>(v)]) return;
                cost[static_cast<std::size_t>(v)] = c;
                parent[static_cast<std::size_t>(v)] = u;
                open.push({c + h(v), v});
            };
            const double diagonal = spacing * std::sqrt(2.0);
            while (!open.empty())
            {
                const auto [f, u] = open.top();
                open.pop();
                if (f > cost[static_cast<std::size_t>(u)] + h(u) + 1e-6) continue;
                if (u == b) { closest = b; reached = true; break; }
                if (const double hu = h(u); hu < closestH) { closestH = hu; closest = u; }
                for (const int v : nodes[static_cast<std::size_t>(u)].link)
                    if (v >= 0) relax(u, v, spacing);
                for (const int dx : {1, -1})
                    for (const int dy : {1, -1})
                        if (const int v = Diagonal(u, dx, dy); v >= 0) relax(u, v, diagonal);
            }
            for (int n = closest; n >= 0; n = parent[static_cast<std::size_t>(n)])
            {
                out.push_back(n);
                if (n == a) break;
            }
            std::reverse(out.begin(), out.end());
            return out;
        }

        // A* from the node nearest `from` to the node nearest `to`: the floor points to walk, in order.
        // `reached` is false when the goal isn't connected (yet): the path then ends at the node
        // closest to it.
        std::vector<std::array<double, 3>> Path(const std::array<double, 3>& from, const std::array<double, 3>& to, bool& reached, const Avoid& avoid = {}) const
        {
            std::vector<std::array<double, 3>> out;
            for (const int n : PathNodes(from, to, reached, avoid))
            {
                const auto& p = nodes[static_cast<std::size_t>(n)];
                out.push_back({p.x, p.y, p.z});
            }
            return out;
        }
    };
} // namespace bridge::ghost

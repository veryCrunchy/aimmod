#pragma once
// Offline stand-in for the game's line traces: a KovaaK's map-creator map (the JSON AimMod's map
// port writes) as convex brushes, with the floor and wall traces the bots use (Walker / NavGrid
// Floor and Clear). A brush's procedural faces are only the faces left visible (hidden ones are
// culled), so its solid is the convex hull of its vertices. Development tools only; the mod uses
// the game's own traces.

#include "Json.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <fstream>
#include <iterator>
#include <optional>
#include <sstream>
#include <string>
#include <unordered_map>
#include <vector>

namespace bridge::tools
{
    struct Vec
    {
        double x = 0, y = 0, z = 0;
    };
    inline Vec operator-(Vec a, Vec b) { return {a.x - b.x, a.y - b.y, a.z - b.z}; }
    inline Vec Cross(Vec a, Vec b) { return {a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x}; }
    inline double Dot(Vec a, Vec b) { return a.x * b.x + a.y * b.y + a.z * b.z; }

    struct Brush
    {
        std::vector<std::pair<Vec, double>> planes; // outward unit normal n, offset d: inside where dot(n, p) < d
        Vec min{1e30, 1e30, 1e30}, max{-1e30, -1e30, -1e30};
    };

    class MapGeometry
    {
    public:
        static constexpr double Cell = 256;
        std::vector<Brush> brushes;
        int rotated = 0, skipped = 0;
        bool verbose = false; // list the brushes that could not be read
        mutable std::int64_t floorTraces = 0, wallTraces = 0;

        static std::optional<std::array<double, 3>> Triple(const std::string& s)
        {
            std::array<double, 3> v{};
            std::stringstream in(s);
            char comma = 0;
            if (!(in >> v[0] >> comma >> v[1] >> comma >> v[2])) return std::nullopt;
            return v;
        }

        // The convex hull of a brush's points as planes (brute force: brushes have few points).
        static bool Hull(const std::vector<Vec>& points, Brush& out)
        {
            const std::size_t n = points.size();
            if (n < 4 || n > 64) return false;
            for (const auto& p : points)
            {
                out.min = {std::min(out.min.x, p.x), std::min(out.min.y, p.y), std::min(out.min.z, p.z)};
                out.max = {std::max(out.max.x, p.x), std::max(out.max.y, p.y), std::max(out.max.z, p.z)};
            }
            const double eps = 0.01;
            for (std::size_t i = 0; i < n; ++i)
                for (std::size_t j = i + 1; j < n; ++j)
                    for (std::size_t k = j + 1; k < n; ++k)
                    {
                        Vec nrm = Cross(points[j] - points[i], points[k] - points[i]);
                        const double len = std::sqrt(Dot(nrm, nrm));
                        if (len < 1e-6) continue;
                        nrm = {nrm.x / len, nrm.y / len, nrm.z / len};
                        const double d = Dot(nrm, points[i]);
                        bool above = false, below = false;
                        for (const auto& p : points)
                        {
                            const double s = Dot(nrm, p) - d;
                            if (s > eps) above = true;
                            if (s < -eps) below = true;
                            if (above && below) break;
                        }
                        if (above && below) continue;
                        if (above) { nrm = {-nrm.x, -nrm.y, -nrm.z}; }
                        const double dd = Dot(nrm, points[i]);
                        bool dup = false;
                        for (const auto& [m, md] : out.planes)
                            if (Dot(m, nrm) > 0.9999 && std::fabs(md - dd) < 0.05) { dup = true; break; }
                        if (!dup) out.planes.push_back({nrm, dd});
                    }
            return out.planes.size() >= 4;
        }

        // Loads the map's brushes; scale is the scenario's MapScale (world = map units x scale).
        bool Load(const std::string& path, double scale, std::string& error)
        {
            std::ifstream file(path, std::ios::binary);
            if (!file) { error = "cannot open the map"; return false; }
            const std::string text((std::istreambuf_iterator<char>(file)), std::istreambuf_iterator<char>());
            json::Limits limits;
            limits.maxBytes = 64u << 20;
            limits.maxDepth = 16;
            limits.maxMembers = 1u << 20;
            limits.maxStringBytes = 1u << 20;
            const auto root = json::Parse(text, limits);
            const auto* objects = root ? root->Get("objects") : nullptr;
            if (!objects || objects->type != json::Value::Type::Array) { error = "not a map-creator map"; return false; }
            for (const auto& o : objects->array)
            {
                const auto* proc = o.Get("procedural");
                if (!proc || proc->type != json::Value::Type::Array) continue;
                // Brushes players walk through (map-port BRUSH_TYPE): no collision, weapon clips.
                const std::string kind = o.Str("name", 64).value_or("");
                if (kind == "DefaultNoCollision" || kind == "WeaponClip") continue;
                const auto loc = Triple(o.Str("location", 128).value_or("0, 0, 0")).value_or(std::array<double, 3>{});
                const auto rot = Triple(o.Str("rotation", 128).value_or("0, 0, 0")).value_or(std::array<double, 3>{});
                const auto scl = Triple(o.Str("scale", 128).value_or("1, 1, 1")).value_or(std::array<double, 3>{1, 1, 1});
                if (std::fabs(rot[0]) + std::fabs(rot[1]) + std::fabs(rot[2]) > 1e-6) ++rotated;
                std::vector<Vec> points;
                for (const auto& part : proc->array)
                    if (const auto* verts = part.Get("vertices"))
                        for (const auto& vert : verts->array)
                        {
                            const auto p = Triple(vert.Str("location", 128).value_or("")).value_or(std::array<double, 3>{});
                            // The loader multiplies procedural vertices by MapScale once more (map-port kovaaks_json.py).
                            const Vec w{(loc[0] + p[0] * scl[0] * scale) * scale, (loc[1] + p[1] * scl[1] * scale) * scale, (loc[2] + p[2] * scl[2] * scale) * scale};
                            if (std::none_of(points.begin(), points.end(), [&](const Vec& q) { return std::fabs(q.x - w.x) + std::fabs(q.y - w.y) + std::fabs(q.z - w.z) < 0.01; }))
                                points.push_back(w);
                        }
                Brush b;
                if (!Hull(points, b))
                {
                    ++skipped;
                    if (verbose)
                    {
                        Vec lo{1e30, 1e30, 1e30}, hi{-1e30, -1e30, -1e30};
                        for (const auto& p : points) { lo = {std::min(lo.x, p.x), std::min(lo.y, p.y), std::min(lo.z, p.z)}; hi = {std::max(hi.x, p.x), std::max(hi.y, p.y), std::max(hi.z, p.z)}; }
                        std::printf("  skipped brush: %zu points, x %.0f..%.0f y %.0f..%.0f z %.0f..%.0f\n", points.size(), lo.x, hi.x, lo.y, hi.y, lo.z, hi.z);
                    }
                    continue;
                }
                brushes.push_back(std::move(b));
            }
            for (std::size_t i = 0; i < brushes.size(); ++i)
            {
                const auto& b = brushes[i];
                for (int x = Key(b.min.x); x <= Key(b.max.x); ++x)
                    for (int y = Key(b.min.y); y <= Key(b.max.y); ++y) m_cells[Pack(x, y)].push_back(static_cast<int>(i));
            }
            return !brushes.empty();
        }

        // The first brush a -> b enters: its entry fraction, and whether a already lies inside it.
        std::optional<std::pair<double, bool>> Hit(Vec a, Vec b) const
        {
            const Vec d = b - a;
            const int x0 = Key(std::min(a.x, b.x)), x1 = Key(std::max(a.x, b.x)), y0 = Key(std::min(a.y, b.y)), y1 = Key(std::max(a.y, b.y));
            double best = 2;
            bool inside = false;
            ++m_stamp;
            if (m_seen.size() < brushes.size()) m_seen.resize(brushes.size(), 0);
            for (int x = x0; x <= x1; ++x)
                for (int y = y0; y <= y1; ++y)
                {
                    const auto it = m_cells.find(Pack(x, y));
                    if (it == m_cells.end()) continue;
                    for (const int i : it->second)
                    {
                        if (m_seen[static_cast<std::size_t>(i)] == m_stamp) continue;
                        m_seen[static_cast<std::size_t>(i)] = m_stamp;
                        const auto& br = brushes[static_cast<std::size_t>(i)];
                        double enter = 0, exit = 1;
                        bool ok = true;
                        for (const auto& [n, off] : br.planes)
                        {
                            const double denom = Dot(n, d), dist = off - Dot(n, a); // inside: dist > 0
                            if (std::fabs(denom) < 1e-12)
                            {
                                if (dist < 0) { ok = false; break; }
                                continue;
                            }
                            const double t = dist / denom;
                            if (denom < 0) enter = std::max(enter, t); // entering through this plane
                            else exit = std::min(exit, t);
                            if (enter > exit) { ok = false; break; }
                        }
                        if (!ok || enter >= best) continue;
                        // Started inside: no plane was entered (enter stayed 0 and a is inside every plane).
                        bool startIn = true;
                        for (const auto& [n, off] : br.planes)
                            if (Dot(n, a) > off - 0.01) { startIn = false; break; }
                        best = enter;
                        inside = startIn;
                    }
                }
            if (best > 1) return std::nullopt;
            return std::pair<double, bool>{best, inside};
        }

        // The game's floor trace: straight down 3000 from (x, y, z); nothing when it starts inside a solid.
        std::optional<double> Floor(double x, double y, double z) const
        {
            ++floorTraces;
            const auto h = Hit({x, y, z}, {x, y, z - 3000});
            if (!h || h->second) return std::nullopt;
            return z - 3000 * h->first;
        }
        bool Clear(double ax, double ay, double az, double bx, double by, double bz) const
        {
            ++wallTraces;
            return !Hit({ax, ay, az}, {bx, by, bz}).has_value();
        }

    private:
        static int Key(double v) { return static_cast<int>(std::floor(v / Cell)); }
        static std::int64_t Pack(int x, int y) { return (static_cast<std::int64_t>(x) << 32) ^ static_cast<std::uint32_t>(y); }
        std::unordered_map<std::int64_t, std::vector<int>> m_cells;
        mutable std::vector<std::uint32_t> m_seen;
        mutable std::uint32_t m_stamp = 0;
    };
} // namespace bridge::tools

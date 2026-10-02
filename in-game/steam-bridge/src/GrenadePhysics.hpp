#pragma once
// CS grenade flight for the host's game (in-game/docs/game-modes.md 6.6.5). The native service
// asks for a throw's path (grenade-sim.tsv); AimModSteam flies it here with the map's own line
// traces and answers in grenade-paths.tsv, and the host broadcasts the path so every player sees
// the same bounces. The steps are the service's GrenadePhysics (CsGrenades.cs), in the same order:
// gravity, a bounce off what a trace hits (45 % of the speed kept), sliding to a stop on a floor.
// Between keys the path is exact (a parabola in flight, an even slow-down when sliding).
//
// grenade-sim.tsv (service -> AimModSteam):
//   AIMMOD_GRENADESIM_1\t<seq>
//   throw\t<id>\t<kind>\t<x>\t<y>\t<z>\t<vx>\t<vy>\t<vz>
//   los\t<tag>\t<ax>\t<ay>\t<az>\t<bx>\t<by>\t<bz>
//   floor\t<tag>\t<x>\t<y>\t<top z>\t<bottom z>      (a spreading flame, a dropped grenade)
// grenade-paths.tsv (AimModSteam -> service):
//   AIMMOD_GRENADEPATHS_1\t<unix ms>
//   path\t<id>\t<keys>\t<t x y z vx vy vz motion impact> x keys
//   los\t<tag>\t<0|1>
//   floor\t<tag>\t<z|->                                  (-: no floor in between)
// The traces leave out every pawn (Grenades.cpp): KovaaK's characters carry hitbox components that
// would stop a line of sight at the thrower's own eye.

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <functional>
#include <map>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace bridge::grenades
{
    constexpr double Unit = 4.4;                   // cm per CS unit on the ports
    constexpr double Gravity = 800 * 0.4 * Unit;   // CS grenades: 0.4 of sv_gravity 800
    constexpr double Elasticity = 0.45, SlideNormal = 120, StopSpeed = 90, SlideDecel = 700, FloorNz = 0.7, Lift = 2, SlideProbe = 24, SlideWallLift = 6;
    constexpr double Step = 1.0 / 64, MaxSeconds = 8;
    constexpr std::size_t MaxKeys = 24;
    constexpr int Flight = 0, Slide = 1, Rest = 2;
    constexpr int NoImpact = 0, WallImpact = 1, FloorImpact = 2;
    constexpr double ThrowSpeed = 750 * 0.9 * Unit;
    // CS: a throw carries 1.25 times the thrower's velocity, at most MaxInheritCm of it.
    constexpr double InheritShare = 1.25, MaxInheritCm = 1500;

    using Vec = std::array<double, 3>;
    // t: ms after the throw.
    struct Key
    {
        double t, x, y, z, vx, vy, vz;
        int motion, impact;
        bool operator==(const Key&) const = default;
    };
    struct Hit
    {
        Vec point, normal;
    };
    using Trace = std::function<std::optional<Hit>(const Vec& a, const Vec& b)>;

    inline double Len(double x, double y, double z) { return std::sqrt(x * x + y * y + z * z); }

    // The service's GrenadePhysics.ThrowVelocity (pitch up positive, degrees; strength 1 full, 0.5 both
    // buttons, 0 underhand; inherit: the thrower's velocity, cm/s).
    inline Vec ThrowVelocity(double pitch, double yaw, double strength, const Vec& inherit = {0, 0, 0})
    {
        strength = std::clamp(strength, 0.0, 1.0);
        double p = std::clamp(pitch, -89.0, 89.0);
        p += (90 - std::fabs(p)) * 10 / 90;
        const double speed = ThrowSpeed * (0.3 + 0.7 * strength);
        const double pr = p * 3.14159265358979323846 / 180, yr = yaw * 3.14159265358979323846 / 180;
        Vec v{std::cos(pr) * std::cos(yr) * speed, std::cos(pr) * std::sin(yr) * speed, std::sin(pr) * speed};
        if (std::isfinite(inherit[0]) && std::isfinite(inherit[1]) && std::isfinite(inherit[2]))
        {
            const double len = Len(inherit[0], inherit[1], inherit[2]);
            const double k = len > MaxInheritCm ? MaxInheritCm / len : 1;
            for (int i = 0; i < 3; ++i) v[i] += inherit[i] * k * InheritShare;
        }
        return v;
    }
    // Where the grenade leaves the hand: the eye for a full throw, 12 units lower underhand.
    inline Vec ThrowOrigin(const Vec& eye, double strength) { return {eye[0], eye[1], eye[2] + (std::clamp(strength, 0.0, 1.0) * 12 - 12) * Unit}; }

    inline Vec Pos(const Key& k, double tau)
    {
        if (k.motion == Rest || tau <= 0) return {k.x, k.y, k.z};
        if (k.motion == Slide)
        {
            const double s0 = std::sqrt(k.vx * k.vx + k.vy * k.vy);
            if (s0 <= 1e-9) return {k.x, k.y, k.z};
            const double tc = std::min(tau, s0 / SlideDecel);
            const double d = s0 * tc - SlideDecel * tc * tc / 2;
            return {k.x + k.vx / s0 * d, k.y + k.vy / s0 * d, k.z};
        }
        return {k.x + k.vx * tau, k.y + k.vy * tau, k.z + k.vz * tau - Gravity * tau * tau / 2};
    }

    // Where the grenade is `ms` after the throw.
    inline Vec At(const std::vector<Key>& keys, double ms)
    {
        if (keys.empty()) return {0, 0, 0};
        const Key* k = &keys.front();
        for (const Key& key : keys)
        {
            if (key.t <= ms) k = &key;
            else break;
        }
        return Pos(*k, (ms - k->t) / 1000);
    }

    namespace detail
    {
        inline std::optional<Vec> Normal(const Vec& n)
        {
            const double l = Len(n[0], n[1], n[2]);
            if (l < 1e-6 || !std::isfinite(l)) return std::nullopt;
            return Vec{n[0] / l, n[1] / l, n[2] / l};
        }
    } // namespace detail

    inline std::vector<Key> Simulate(const Vec& origin, const Vec& velocity, const Trace& trace)
    {
        std::vector<Key> keys{{0, origin[0], origin[1], origin[2], velocity[0], velocity[1], velocity[2], Flight, NoImpact}};
        Key cur = keys.front();
        while (cur.motion != Rest)
        {
            if (keys.size() >= MaxKeys)
            {
                keys.push_back({cur.t, cur.x, cur.y, cur.z, 0, 0, 0, Rest, NoImpact});
                break;
            }
            std::optional<Key> next;
            double prevTau = 0;
            Vec prev = Pos(cur, 0);
            for (int i = 1; !next; ++i)
            {
                const double tau = i * Step;
                if (cur.t / 1000 + tau > MaxSeconds)
                {
                    const Vec p0 = Pos(cur, prevTau);
                    next = Key{cur.t + prevTau * 1000, p0[0], p0[1], p0[2], 0, 0, 0, Rest, NoImpact};
                    break;
                }
                if (cur.motion == Flight)
                {
                    const Vec p = Pos(cur, tau);
                    if (const auto hit = trace(prev, p))
                        if (const auto n = detail::Normal(hit->normal))
                        {
                            const double seg = Len(p[0] - prev[0], p[1] - prev[1], p[2] - prev[2]);
                            const double f = seg > 1e-9 ? std::clamp(Len(hit->point[0] - prev[0], hit->point[1] - prev[1], hit->point[2] - prev[2]) / seg, 0.0, 1.0) : 0;
                            const double th = prevTau + f * Step;
                            const Vec v{cur.vx, cur.vy, cur.vz - Gravity * th};
                            const double vn = v[0] * (*n)[0] + v[1] * (*n)[1] + v[2] * (*n)[2];
                            if (vn < 0)
                            {
                                const Vec hp{hit->point[0] + (*n)[0] * Lift, hit->point[1] + (*n)[1] * Lift, hit->point[2] + (*n)[2] * Lift};
                                const Vec r{(v[0] - 2 * vn * (*n)[0]) * Elasticity, (v[1] - 2 * vn * (*n)[1]) * Elasticity, (v[2] - 2 * vn * (*n)[2]) * Elasticity};
                                const double t = cur.t + th * 1000;
                                if ((*n)[2] > FloorNz)
                                {
                                    if (-vn * Elasticity < SlideNormal)
                                    {
                                        const double speed = std::sqrt(r[0] * r[0] + r[1] * r[1]);
                                        next = speed < StopSpeed ? Key{t, hp[0], hp[1], hp[2], 0, 0, 0, Rest, FloorImpact} : Key{t, hp[0], hp[1], hp[2], r[0], r[1], 0, Slide, FloorImpact};
                                    }
                                    else next = Key{t, hp[0], hp[1], hp[2], r[0], r[1], r[2], Flight, FloorImpact};
                                }
                                else next = Key{t, hp[0], hp[1], hp[2], r[0], r[1], r[2], Flight, WallImpact};
                                break;
                            }
                        }
                    prev = p;
                    prevTau = tau;
                }
                else
                {
                    const double s0 = std::sqrt(cur.vx * cur.vx + cur.vy * cur.vy);
                    const double stop = s0 / SlideDecel;
                    const double tc = std::min(tau, stop);
                    const Vec p = Pos(cur, tc);
                    const Vec from{prev[0], prev[1], prev[2] + SlideWallLift}, to{p[0], p[1], p[2] + SlideWallLift};
                    if (Len(to[0] - from[0], to[1] - from[1], 0) > 1e-9)
                        if (const auto wall = trace(from, to))
                            if (const auto n = detail::Normal({wall->normal[0], wall->normal[1], 0}))
                            {
                                const double seg = Len(to[0] - from[0], to[1] - from[1], 0);
                                const double f = std::clamp(Len(wall->point[0] - from[0], wall->point[1] - from[1], 0) / seg, 0.0, 1.0);
                                const double th = prevTau + f * (tc - prevTau);
                                const double speed = s0 - SlideDecel * th;
                                const double vx = cur.vx / s0 * speed, vy = cur.vy / s0 * speed;
                                const double vn = vx * (*n)[0] + vy * (*n)[1];
                                const double rx = (vx - 2 * vn * (*n)[0]) * Elasticity, ry = (vy - 2 * vn * (*n)[1]) * Elasticity;
                                Vec hp = Pos(cur, th);
                                hp[0] += (*n)[0] * Lift;
                                hp[1] += (*n)[1] * Lift;
                                const double t = cur.t + th * 1000;
                                next = std::sqrt(rx * rx + ry * ry) < StopSpeed ? Key{t, hp[0], hp[1], hp[2], 0, 0, 0, Rest, WallImpact} : Key{t, hp[0], hp[1], hp[2], rx, ry, 0, Slide, WallImpact};
                                break;
                            }
                    if (!trace(p, Vec{p[0], p[1], p[2] - SlideProbe}))
                    {
                        const double speed = s0 - SlideDecel * tc;
                        next = Key{cur.t + tc * 1000, p[0], p[1], p[2], cur.vx / s0 * speed, cur.vy / s0 * speed, 0, Flight, NoImpact};
                        break;
                    }
                    if (tau >= stop)
                    {
                        next = Key{cur.t + stop * 1000, p[0], p[1], p[2], 0, 0, 0, Rest, NoImpact};
                        break;
                    }
                    prev = p;
                    prevTau = tau;
                }
            }
            keys.push_back(*next);
            cur = *next;
        }
        return keys;
    }

    // A level floor at height z, hit from above (the service's GrenadePhysics.Floor).
    inline Trace Floor(double z)
    {
        return [z](const Vec& a, const Vec& b) -> std::optional<Hit> {
            if (a[2] < z || b[2] >= z || std::fabs(a[2] - b[2]) < 1e-12) return std::nullopt;
            const double f = (a[2] - z) / (a[2] - b[2]);
            return Hit{{a[0] + (b[0] - a[0]) * f, a[1] + (b[1] - a[1]) * f, z}, {0, 0, 1}};
        };
    }

    // The floor under (x, y) between two heights: the first surface a trace down meets, if it is a
    // floor (facing up, as a grenade lands on); nullopt for none or a wall.
    inline std::optional<double> FloorHeight(const Trace& trace, double x, double y, double top, double bottom)
    {
        if (!(top > bottom)) return std::nullopt;
        const auto hit = trace({x, y, top}, {x, y, bottom});
        if (!hit) return std::nullopt;
        const double l = Len(hit->normal[0], hit->normal[1], hit->normal[2]);
        if (l < 1e-6 || hit->normal[2] / l <= FloorNz) return std::nullopt;
        return hit->point[2];
    }

    // ---- files -----------------------------------------------------------------------------------
    struct ThrowRequest
    {
        std::int64_t id{};
        std::string kind;
        Vec origin{}, velocity{};
    };
    struct LosRequest
    {
        int tag{};
        Vec from{}, to{};
    };
    struct FloorRequest
    {
        int tag{};
        double x{}, y{}, top{}, bottom{};
    };
    struct Requests
    {
        std::int64_t sequence{};
        std::vector<ThrowRequest> throws;
        std::vector<LosRequest> los;
        std::vector<FloorRequest> floors;
    };

    namespace detail
    {
        inline std::vector<std::string_view> Split(std::string_view line)
        {
            std::vector<std::string_view> parts;
            std::size_t start = 0;
            for (;;)
            {
                const auto tab = line.find('\t', start);
                parts.push_back(line.substr(start, tab == std::string_view::npos ? std::string_view::npos : tab - start));
                if (tab == std::string_view::npos || parts.size() > 12) break;
                start = tab + 1;
            }
            return parts;
        }
        inline std::optional<double> Number(std::string_view s)
        {
            if (s.empty() || s.size() > 24) return std::nullopt;
            char buffer[32]{};
            for (std::size_t i = 0; i < s.size(); ++i)
            {
                const char c = s[i];
                if (!((c >= '0' && c <= '9') || c == '-' || c == '.' || c == 'e' || c == 'E' || c == '+')) return std::nullopt;
                buffer[i] = c;
            }
            char* end = nullptr;
            const double v = std::strtod(buffer, &end);
            if (end != buffer + s.size() || !std::isfinite(v) || std::fabs(v) > 1e7) return std::nullopt;
            return v;
        }
        inline std::optional<std::int64_t> Integer(std::string_view s, std::int64_t max)
        {
            if (s.empty() || s.size() > 12) return std::nullopt;
            std::int64_t v = 0;
            for (const char c : s)
            {
                if (c < '0' || c > '9') return std::nullopt;
                v = v * 10 + (c - '0');
            }
            return v <= max ? std::optional<std::int64_t>(v) : std::nullopt;
        }
        inline bool Vec6(const std::vector<std::string_view>& p, std::size_t from, Vec& a, Vec& b)
        {
            if (p.size() < from + 6) return false;
            for (std::size_t i = 0; i < 6; ++i)
            {
                const auto v = Number(p[from + i]);
                if (!v) return false;
                (i < 3 ? a : b)[i % 3] = *v;
            }
            return true;
        }
    } // namespace detail

    // Lenient per row, strict on the header; at most 32 throws, 128 line-of-sight checks and 64 floors.
    inline std::optional<Requests> ParseSim(std::string_view text)
    {
        if (text.size() > 64 * 1024) return std::nullopt;
        Requests r;
        bool header = false;
        std::size_t pos = 0;
        while (pos <= text.size())
        {
            auto end = text.find('\n', pos);
            if (end == std::string_view::npos) end = text.size();
            std::string_view line = text.substr(pos, end - pos);
            pos = end + 1;
            if (!line.empty() && line.back() == '\r') line.remove_suffix(1);
            if (line.empty()) { if (end >= text.size()) break; continue; }
            const auto p = detail::Split(line);
            if (!header)
            {
                if (p.size() != 2 || p[0] != "AIMMOD_GRENADESIM_1") return std::nullopt;
                r.sequence = detail::Integer(p[1], 999'999'999'999).value_or(0);
                header = true;
                continue;
            }
            if (p[0] == "throw" && p.size() == 9 && r.throws.size() < 32)
            {
                ThrowRequest t;
                const auto id = detail::Integer(p[1], 1'000'000'000);
                if (!id || p[2].empty() || p[2].size() > 16 || !detail::Vec6(p, 3, t.origin, t.velocity)) continue;
                t.id = *id;
                t.kind = std::string(p[2]);
                r.throws.push_back(std::move(t));
            }
            else if (p[0] == "los" && p.size() == 8 && r.los.size() < 128)
            {
                LosRequest l;
                const auto tag = detail::Integer(p[1], 1'000'000'000);
                if (!tag || !detail::Vec6(p, 2, l.from, l.to)) continue;
                l.tag = static_cast<int>(*tag);
                r.los.push_back(l);
            }
            else if (p[0] == "floor" && p.size() == 6 && r.floors.size() < 64)
            {
                const auto tag = detail::Integer(p[1], 1'000'000'000);
                const auto x = detail::Number(p[2]), y = detail::Number(p[3]), top = detail::Number(p[4]), bottom = detail::Number(p[5]);
                if (!tag || !x || !y || !top || !bottom) continue;
                r.floors.push_back({static_cast<int>(*tag), *x, *y, *top, *bottom});
            }
        }
        if (!header) return std::nullopt;
        return r;
    }

    inline std::string FormatPaths(std::int64_t unixMs, const std::map<std::int64_t, std::vector<Key>>& paths, const std::map<int, bool>& los,
                                   const std::map<int, std::optional<double>>& floors = {})
    {
        std::string text = "AIMMOD_GRENADEPATHS_1\t" + std::to_string(unixMs) + "\n";
        char cell[64];
        for (const auto& [id, keys] : paths)
        {
            text += "path\t" + std::to_string(id) + "\t" + std::to_string(keys.size());
            for (const Key& k : keys)
            {
                for (const double v : {k.t, k.x, k.y, k.z, k.vx, k.vy, k.vz})
                {
                    std::snprintf(cell, sizeof(cell), "\t%.3f", v);
                    text += cell;
                }
                text += "\t" + std::to_string(k.motion) + "\t" + std::to_string(k.impact);
            }
            text += "\n";
        }
        for (const auto& [tag, clear] : los) text += "los\t" + std::to_string(tag) + "\t" + (clear ? "1" : "0") + "\n";
        for (const auto& [tag, z] : floors)
        {
            if (z) std::snprintf(cell, sizeof(cell), "\t%.1f", *z);
            text += "floor\t" + std::to_string(tag) + (z ? std::string(cell) : std::string("\t-")) + "\n";
        }
        return text;
    }
} // namespace bridge::grenades

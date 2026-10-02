#include <aimmod/CsGrenades.hpp>
#include <aimmod/Formats.hpp>

#include <algorithm>
#include <charconv>
#include <cmath>

namespace aimmod::cs
{
    namespace
    {
        std::vector<std::string_view> Cells(std::string_view line)
        {
            std::vector<std::string_view> out;
            std::size_t start = 0;
            for (;;)
            {
                auto tab = line.find('\t', start);
                out.push_back(line.substr(start, tab == std::string_view::npos ? std::string_view::npos : tab - start));
                if (tab == std::string_view::npos || out.size() > 260) return out;
                start = tab + 1;
            }
        }
        bool Num(std::string_view s, double& v)
        {
            auto r = std::from_chars(s.data(), s.data() + s.size(), v);
            return !s.empty() && r.ec == std::errc() && r.ptr == s.data() + s.size() && IsUsableNumber(v) && std::fabs(v) <= 1e7;
        }
        bool Int(std::string_view s, std::int64_t& v)
        {
            auto r = std::from_chars(s.data(), s.data() + s.size(), v);
            return !s.empty() && r.ec == std::errc() && r.ptr == s.data() + s.size() && v >= 0;
        }
        bool Xyz(const std::vector<std::string_view>& c, std::size_t at, double& x, double& y, double& z) { return Num(c[at], x) && Num(c[at + 1], y) && Num(c[at + 2], z); }

        constexpr const wchar_t* Cube = L"/Engine/BasicShapes/Cube.Cube";
        constexpr const wchar_t* Sphere = L"/Engine/BasicShapes/Sphere.Sphere";
        constexpr const wchar_t* Cylinder = L"/Engine/BasicShapes/Cylinder.Cylinder";
    } // namespace

    bool KnownGrenade(std::string_view kind)
    {
        return kind == "he" || kind == "flash" || kind == "smoke" || kind == "decoy" || kind == "molotov" || kind == "incendiary";
    }

    std::optional<GrenadeState> ParseGrenades(std::string_view text)
    {
        if (text.empty() || text.size() > 64 * 1024) return std::nullopt;
        GrenadeState s;
        bool first = true;
        while (!text.empty())
        {
            auto end = text.find('\n');
            std::string_view line = text.substr(0, end);
            text = end == std::string_view::npos ? std::string_view{} : text.substr(end + 1);
            if (!line.empty() && line.back() == '\r') line.remove_suffix(1);
            if (line.empty()) continue;
            const auto c = Cells(line);
            if (first)
            {
                first = false;
                std::int64_t seq = 0;
                if (c.size() != 2 || c[0] != "AIMMOD_GRENADES_1" || !Int(c[1], seq)) return std::nullopt;
                s.sequence = static_cast<std::uint64_t>(seq);
                continue;
            }
            if (c[0] == "match" && c.size() == 2)
            {
                if (c[1].empty() || c[1].size() > 256) return std::nullopt;
                s.scenario = std::string(c[1]);
            }
            else if (c[0] == "hand" && c.size() == 4)
            {
                if ((c[1] != "-" && !KnownGrenade(c[1])) || (c[2] != "0" && c[2] != "1") || !Int(c[3], s.hand.thrownMs)) return std::nullopt;
                s.hand.kind = c[1] == "-" ? "" : std::string(c[1]);
                s.hand.pin = c[2] == "1";
            }
            else if (c[0] == "fly" && c.size() >= 6)
            {
                GrenadeState::Flying f;
                std::int64_t n = 0;
                if (!Int(c[1], f.id) || !KnownGrenade(c[2]) || !Int(c[3], f.startMs) || !Int(c[4], f.endMs) || !Int(c[5], n) || n < 1 || n > 25 ||
                    c.size() != 6 + static_cast<std::size_t>(n) * 8 || s.flying.size() >= 48)
                    return std::nullopt;
                f.kind = std::string(c[2]);
                for (std::int64_t k = 0; k < n; ++k)
                {
                    GrenadeKey key;
                    const std::size_t at = 6 + static_cast<std::size_t>(k) * 8;
                    std::int64_t motion = 0;
                    if (!Num(c[at], key.t) || !Xyz(c, at + 1, key.x, key.y, key.z) || !Xyz(c, at + 4, key.vx, key.vy, key.vz) || !Int(c[at + 7], motion) || motion > 2) return std::nullopt;
                    if (!f.keys.empty() && key.t < f.keys.back().t) return std::nullopt;
                    key.motion = static_cast<int>(motion);
                    f.keys.push_back(key);
                }
                s.flying.push_back(std::move(f));
            }
            else if ((c[0] == "smoke" || c[0] == "decoy") && c.size() == 7)
            {
                GrenadeState::Area a;
                a.kind = std::string(c[0]);
                if (!Int(c[1], a.id) || !Xyz(c, 2, a.x, a.y, a.z) || !Int(c[5], a.startMs) || !Int(c[6], a.endMs)) return std::nullopt;
                a.radius = c[0] == "smoke" ? SmokeRadius : 0;
                (c[0] == "smoke" ? s.smokes : s.decoys).push_back(a);
            }
            else if (c[0] == "fire" && c.size() == 9)
            {
                GrenadeState::Area a;
                if (!Int(c[1], a.id) || (c[2] != "molotov" && c[2] != "incendiary") || !Xyz(c, 3, a.x, a.y, a.z) || !Num(c[6], a.radius) || a.radius <= 0 || a.radius > 2000 ||
                    !Int(c[7], a.startMs) || !Int(c[8], a.endMs))
                    return std::nullopt;
                a.kind = std::string(c[2]);
                s.fires.push_back(a);
            }
            else if (c[0] == "blast" && c.size() == 7)
            {
                GrenadeState::Blast b;
                if (!Int(c[1], b.id) || (!KnownGrenade(c[2]) && c[2] != "extinguished") || !Xyz(c, 3, b.x, b.y, b.z) || !Int(c[6], b.atMs)) return std::nullopt;
                b.kind = std::string(c[2]);
                s.blasts.push_back(b);
            }
            else return std::nullopt;
        }
        if (first || s.scenario.empty()) return std::nullopt;
        return s;
    }

    Point GrenadeAt(const std::vector<GrenadeKey>& keys, double ms)
    {
        if (keys.empty()) return {};
        const GrenadeKey* k = &keys.front();
        for (const GrenadeKey& key : keys)
        {
            if (key.t <= ms) k = &key;
            else break;
        }
        const double tau = (ms - k->t) / 1000;
        if (k->motion == GrenadeRest || tau <= 0) return {k->x, k->y, k->z};
        if (k->motion == GrenadeSlide)
        {
            const double s0 = std::sqrt(k->vx * k->vx + k->vy * k->vy);
            if (s0 <= 1e-9) return {k->x, k->y, k->z};
            const double tc = std::min(tau, s0 / GrenadeSlideDecel);
            const double d = s0 * tc - GrenadeSlideDecel * tc * tc / 2;
            return {k->x + k->vx / s0 * d, k->y + k->vy / s0 * d, k->z};
        }
        return {k->x + k->vx * tau, k->y + k->vy * tau, k->z + k->vz * tau - GrenadeGravity * tau * tau / 2};
    }

    const std::vector<Part>& GrenadeModel(std::string_view kind)
    {
        // Upright (z up), about the size of a hand-held grenade; levers and pins in steel.
        static const std::vector<Part> he = {
            {Sphere, {0, 0, 0}, {6.4, 6.4, 7.6}, {0, 0, 0}, {0.17, 0.21, 0.08}, false},
            {Cylinder, {0, 0, 4.4}, {2.2, 2.2, 2.0}, {0, 0, 0}, {0.42, 0.42, 0.4}, false},
            {Cube, {2.4, 0, 1.6}, {0.8, 1.4, 6.0}, {0, 0, 0}, {0.55, 0.55, 0.52}, false},
            {Cylinder, {-1.9, 0, 5.2}, {1.9, 1.9, 0.3}, {0, 0, 0}, {0.7, 0.7, 0.68}, false},
        };
        static const std::vector<Part> flash = {
            {Cylinder, {0, 0, 0}, {4.8, 4.8, 10.5}, {0, 0, 0}, {0.6, 0.62, 0.64}, false},
            {Cylinder, {0, 0, -2.5}, {5.0, 5.0, 1.2}, {0, 0, 0}, {0.12, 0.22, 0.55}, false},
            {Cylinder, {0, 0, 6.2}, {2.0, 2.0, 2.0}, {0, 0, 0}, {0.3, 0.3, 0.3}, false},
            {Cube, {2.3, 0, 3.0}, {0.8, 1.3, 6.5}, {0, 0, 0}, {0.55, 0.55, 0.52}, false},
        };
        static const std::vector<Part> smoke = {
            {Cylinder, {0, 0, 0}, {5.6, 5.6, 11.0}, {0, 0, 0}, {0.2, 0.25, 0.2}, false},
            {Cylinder, {0, 0, 3.6}, {5.8, 5.8, 1.0}, {0, 0, 0}, {0.75, 0.75, 0.72}, false},
            {Cylinder, {0, 0, -3.6}, {5.8, 5.8, 1.0}, {0, 0, 0}, {0.75, 0.75, 0.72}, false},
            {Cylinder, {0, 0, 6.4}, {2.2, 2.2, 1.8}, {0, 0, 0}, {0.3, 0.3, 0.3}, false},
            {Cube, {2.6, 0, 3.0}, {0.8, 1.3, 6.5}, {0, 0, 0}, {0.55, 0.55, 0.52}, false},
        };
        static const std::vector<Part> decoy = {
            {Cylinder, {0, 0, 0}, {4.8, 4.8, 9.5}, {0, 0, 0}, {0.08, 0.08, 0.09}, false},
            {Cylinder, {0, 0, 1.0}, {5.0, 5.0, 1.4}, {0, 0, 0}, {0.1, 0.45, 0.15}, false},
            {Cylinder, {0, 0, 5.6}, {2.0, 2.0, 1.8}, {0, 0, 0}, {0.3, 0.3, 0.3}, false},
            {Cube, {2.3, 0, 2.6}, {0.8, 1.3, 6.0}, {0, 0, 0}, {0.55, 0.55, 0.52}, false},
        };
        // A bottle with a rag in its neck, the rag's end alight.
        static const std::vector<Part> molotov = {
            {Cylinder, {0, 0, 0}, {6.6, 6.6, 11.0}, {0, 0, 0}, {0.32, 0.17, 0.05}, false},
            {Cylinder, {0, 0, 7.6}, {2.6, 2.6, 5.0}, {0, 0, 0}, {0.3, 0.16, 0.05}, false},
            {Cube, {0, 0, 11.0}, {2.6, 2.6, 3.0}, {0, 0, 0}, {0.78, 0.7, 0.52}, false},
            {Sphere, {0, 0, 13.4}, {2.2, 2.2, 3.0}, {0, 0, 0}, {1.0, 0.45, 0.05}, true},
        };
        static const std::vector<Part> incendiary = {
            {Cylinder, {0, 0, 0}, {5.4, 5.4, 11.0}, {0, 0, 0}, {0.44, 0.44, 0.42}, false},
            {Cylinder, {0, 0, 2.0}, {5.6, 5.6, 1.4}, {0, 0, 0}, {0.6, 0.08, 0.05}, false},
            {Cylinder, {0, 0, 6.4}, {2.2, 2.2, 1.8}, {0, 0, 0}, {0.3, 0.3, 0.3}, false},
            {Cube, {2.5, 0, 3.0}, {0.8, 1.3, 6.5}, {0, 0, 0}, {0.55, 0.55, 0.52}, false},
        };
        if (kind == "flash") return flash;
        if (kind == "smoke") return smoke;
        if (kind == "decoy") return decoy;
        if (kind == "molotov") return molotov;
        if (kind == "incendiary") return incendiary;
        return he;
    }

    Hold GrenadeInHand(bool pin)
    {
        // Low on the right; with the pin pulled it's drawn back and up, ready to throw.
        if (pin) return {{22, 11, -9}, {-10, -20, -15}};
        return {{28, 13, -15}, {5, -10, 0}};
    }

    const std::vector<Puff>& SmokePuffs()
    {
        static const std::vector<Puff> puffs = [] {
            std::vector<Puff> list;
            // A fixed spread of puffs (the same cloud on every machine): a ring low down, a ring higher
            // up and a crown, each sized so the cloud reaches its full radius and height.
            std::uint32_t state = 0x5eed;
            auto next = [&state] {
                state = state * 1664525u + 1013904223u;
                return static_cast<double>(state >> 8) / static_cast<double>(1u << 24);
            };
            constexpr double Pi = 3.14159265358979323846;
            const struct
            {
                int count;
                double ring, z, size;
            } layers[] = {{7, 0.62, -0.45, 430}, {6, 0.45, 0.1, 460}, {3, 0.2, 0.55, 420}, {1, 0, -0.1, 520}};
            for (const auto& layer : layers)
                for (int i = 0; i < layer.count; ++i)
                {
                    const double angle = 2 * Pi * (i + next() * 0.5) / layer.count;
                    const double r = layer.ring * SmokeRadius * (0.9 + 0.2 * next());
                    list.push_back({{std::cos(angle) * r, std::sin(angle) * r, layer.z * SmokeHalfHeight + (next() - 0.5) * 40}, layer.size * (0.9 + 0.2 * next()), next()});
                }
            return list;
        }();
        return puffs;
    }

    double SmokeScale(std::int64_t startMs, std::int64_t endMs, std::int64_t nowMs)
    {
        if (nowMs < startMs || nowMs >= endMs) return 0;
        const double grow = (nowMs - startMs) / 1000.0 / SmokeGrowSeconds, fade = (endMs - nowMs) / 1000.0 / SmokeFadeSeconds;
        return std::clamp(std::min(grow, fade), 0.0, 1.0);
    }

    Point SmokeCentre(double x, double y, double z) { return {x, y, z + SmokeHalfHeight - 40}; }

    std::vector<Flame> FireFlames(double radius, double seconds, double secondsLeft, std::int64_t seed)
    {
        std::vector<Flame> flames;
        const double life = std::clamp(std::min(seconds / 0.3, secondsLeft), 0.0, 1.0);
        if (life <= 0) return flames;
        // The burning pool on the ground.
        flames.push_back({{0, 0, 1.5}, {radius * 2 * (0.6 + 0.4 * life), radius * 2 * (0.6 + 0.4 * life), 2.5}, {0.55, 0.16, 0.02}});
        constexpr int Count = 12;
        constexpr double Golden = 2.39996322972865332;
        for (int i = 0; i < Count; ++i)
        {
            const double angle = i * Golden + static_cast<double>(seed % 7);
            const double r = radius * 0.8 * std::sqrt((i + 0.5) / Count);
            const double flicker = 0.65 + 0.35 * std::sin(seconds * (7.0 + i % 3) + i * 1.7) * std::sin(seconds * 3.1 + i);
            const double height = (55 + 35 * ((i * 37) % 10) / 10.0) * flicker * life;
            const double width = (30 + 12 * ((i * 53) % 7) / 7.0) * (0.7 + 0.3 * life);
            const bool yellow = i % 3 == 0;
            flames.push_back({{std::cos(angle) * r, std::sin(angle) * r, height / 2}, {width, width, height},
                              {1.0, yellow ? 0.72 : 0.38, yellow ? 0.12 : 0.04}});
        }
        return flames;
    }

    double BlastSize(std::string_view kind, double t)
    {
        if (t < 0) return 0;
        auto pulse = [t](double peak, double rise, double fall) {
            if (t < rise) return peak * t / rise;
            if (t < rise + fall) return peak * (1 - (t - rise) / fall);
            return 0.0;
        };
        if (kind == "he") return pulse(420, 0.1, 0.35);
        if (kind == "flash") return pulse(180, 0.05, 0.2);
        if (kind == "molotov" || kind == "incendiary") return pulse(140, 0.08, 0.3);
        if (kind == "extinguished") return pulse(260, 0.2, 0.6);
        if (kind == "decoy") return pulse(50, 0.05, 0.12);
        return 0;
    }

    void BlastColour(std::string_view kind, double c[3])
    {
        const double he[3] = {1.0, 0.55, 0.12}, white[3] = {1, 1, 1}, fire[3] = {1.0, 0.4, 0.05}, steam[3] = {0.72, 0.72, 0.72}, decoy[3] = {0.9, 0.8, 0.5};
        const double* from = kind == "he" ? he : kind == "flash" ? white : kind == "extinguished" ? steam : kind == "decoy" ? decoy : fire;
        for (int i = 0; i < 3; ++i) c[i] = from[i];
    }
} // namespace aimmod::cs

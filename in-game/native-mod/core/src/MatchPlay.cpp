#include <aimmod/Formats.hpp>
#include <aimmod/MatchPlay.hpp>

#include <charconv>
#include <cmath>
#include <vector>

namespace aimmod
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
                if (tab == std::string_view::npos) return out;
                start = tab + 1;
            }
        }
        bool Num(std::string_view s, double& v)
        {
            auto r = std::from_chars(s.data(), s.data() + s.size(), v);
            return r.ec == std::errc() && r.ptr == s.data() + s.size() && IsUsableNumber(v);
        }
        template <typename T>
        bool Int(std::string_view s, T& v)
        {
            auto r = std::from_chars(s.data(), s.data() + s.size(), v);
            return !s.empty() && r.ec == std::errc() && r.ptr == s.data() + s.size();
        }
        bool Flag(std::string_view s, bool& v)
        {
            if (s != "0" && s != "1") return false;
            v = s == "1";
            return true;
        }
        bool Member(std::string_view s)
        {
            if (s.empty() || s.size() > 64) return false;
            for (char c : s)
                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_')) return false;
            return true;
        }
    } // namespace

    std::optional<PlayState> ParsePlayState(std::string_view text)
    {
        if (text.size() > 4096 || text.empty()) return std::nullopt;
        PlayState s;
        bool first = true, health = false;
        while (!text.empty())
        {
            auto end = text.find('\n');
            std::string_view line = text.substr(0, end);
            text = end == std::string_view::npos ? std::string_view{} : text.substr(end + 1);
            if (!line.empty() && line.back() == '\r') line.remove_suffix(1);
            if (line.empty()) continue;
            auto c = Cells(line);
            if (first)
            {
                first = false;
                if (c.size() != 2 || c[0] != "AIMMOD_PLAYSTATE_1" || !Int(c[1], s.sequence)) return std::nullopt;
                continue;
            }
            if (c[0] == "match" && c.size() == 2)
            {
                if (c[1].empty() || c[1].size() > 256) return std::nullopt;
                s.scenario = std::string(c[1]);
            }
            else if (c[0] == "health" && c.size() == 3)
            {
                if (!Num(c[1], s.health) || !Num(c[2], s.maxHealth) || s.maxHealth <= 0 || s.maxHealth > 100000 || s.health < 0 || s.health > s.maxHealth)
                    return std::nullopt;
                health = true;
            }
            else if (c[0] == "alive" && c.size() == 2) { if (!Flag(c[1], s.alive)) return std::nullopt; }
            else if (c[0] == "respawnAt" && c.size() == 2) { if (!Int(c[1], s.respawnAtMs) || s.respawnAtMs < 0) return std::nullopt; }
            else if (c[0] == "protected" && c.size() == 2) { if (!Flag(c[1], s.spawnProtected)) return std::nullopt; }
            else if (c[0] == "hit" && c.size() == 8)
            {
                PlayState::Hit h;
                if (!Int(c[1], h.sequence) || !Member(c[2]) || !Num(c[3], h.damage) || h.damage < 0 || h.damage > 100000 || !Flag(c[4], h.headshot))
                    return std::nullopt;
                for (int i = 0; i < 3; ++i)
                    if (!Num(c[static_cast<std::size_t>(5 + i)], h.direction[i])) return std::nullopt;
                h.attacker = std::string(c[2]);
                s.lastHit = h;
            }
            else return std::nullopt;
        }
        if (first || s.scenario.empty() || !health) return std::nullopt;
        return s;
    }

    std::optional<RoundState> ParseRoundState(std::string_view text)
    {
        if (text.empty() || text.size() > 4096) return std::nullopt;
        // Weapon profile names: printable, no tabs, at most 128 bytes.
        auto profile = [](std::string_view s) {
            if (s.empty() || s.size() > 128 || s.front() == ' ' || s.back() == ' ') return false;
            for (unsigned char c : s)
                if (c < 0x20 || c == 0x7F) return false;
            return true;
        };
        RoundState r;
        bool first = true;
        while (!text.empty())
        {
            auto end = text.find('\n');
            std::string_view line = text.substr(0, end);
            text = end == std::string_view::npos ? std::string_view{} : text.substr(end + 1);
            if (!line.empty() && line.back() == '\r') line.remove_suffix(1);
            if (line.empty()) continue;
            auto c = Cells(line);
            if (first)
            {
                first = false;
                if (c.size() != 2 || c[0] != "AIMMOD_ROUND_1" || !Int(c[1], r.sequence)) return std::nullopt;
                continue;
            }
            if (c[0] == "match" && c.size() == 2)
            {
                if (c[1].empty() || c[1].size() > 256 || !r.scenario.empty()) return std::nullopt;
                r.scenario = std::string(c[1]);
            }
            else if (c[0] == "spawn" && c.size() == 6 && !r.spawn)
            {
                RoundState::Spawn s;
                if (!Member(c[1]) || !Num(c[2], s.x) || !Num(c[3], s.y) || !Num(c[4], s.z) || !Num(c[5], s.yaw)) return std::nullopt;
                if (std::fabs(s.x) > 1e7 || std::fabs(s.y) > 1e7 || std::fabs(s.z) > 1e7 || std::fabs(s.yaw) > 3600) return std::nullopt;
                s.id = std::string(c[1]);
                r.spawn = s;
            }
            else if (c[0] == "phase" && c.size() == 5 && !r.phase)
            {
                RoundState::Phase p;
                if (c[1] != "freeze" && c[1] != "live" && c[1] != "planted" && c[1] != "end" && c[1] != "over") return std::nullopt;
                if (!Flag(c[2], p.frozen) || !Flag(c[3], p.buy) || !Int(c[4], p.endsMs) || p.endsMs < 0) return std::nullopt;
                p.name = std::string(c[1]);
                r.phase = p;
            }
            else if (c[0] == "loadout" && c.size() == 6 && !r.loadout)
            {
                RoundState::Loadout l;
                if (!profile(c[1]) || !profile(c[2]) || !Num(c[3], l.armour) || l.armour < 0 || l.armour > 1000 || !Flag(c[4], l.helmet) ||
                    !Flag(c[5], l.kit))
                    return std::nullopt;
                l.primary = std::string(c[1]);
                l.pistol = std::string(c[2]);
                r.loadout = l;
            }
            else return std::nullopt;
        }
        if (first || r.scenario.empty()) return std::nullopt;
        return r;
    }

    std::optional<double> RayCapsule(const double o[3], const double d[3], const double c[3], double radius, double halfHeight)
    {
        // Segment axis of the capsule: z from c.z - (h - r) to c.z + (h - r).
        const double seg = std::max(0.0, halfHeight - radius);
        double best = -1;
        // Infinite cylinder part (xy), clipped to the segment.
        const double ox = o[0] - c[0], oy = o[1] - c[1];
        const double a = d[0] * d[0] + d[1] * d[1];
        if (a > 1e-12)
        {
            const double b = 2 * (ox * d[0] + oy * d[1]);
            const double cc = ox * ox + oy * oy - radius * radius;
            const double disc = b * b - 4 * a * cc;
            if (disc >= 0)
            {
                const double t = (-b - std::sqrt(disc)) / (2 * a);
                const double z = o[2] + t * d[2] - c[2];
                if (t >= 0 && std::fabs(z) <= seg) best = t;
            }
        }
        // End spheres.
        for (int end = -1; end <= 1; end += 2)
        {
            const double sc[3] = {c[0], c[1], c[2] + end * seg};
            const double w[3] = {o[0] - sc[0], o[1] - sc[1], o[2] - sc[2]};
            const double b = w[0] * d[0] + w[1] * d[1] + w[2] * d[2];
            const double cc = w[0] * w[0] + w[1] * w[1] + w[2] * w[2] - radius * radius;
            const double disc = b * b - cc;
            if (disc < 0) continue;
            const double t = -b - std::sqrt(disc);
            if (t >= 0 && (best < 0 || t < best)) best = t;
        }
        if (best < 0) return std::nullopt;
        return best;
    }

    bool IsHeadHit(const double p[3], const double c[3], double halfHeight) { return p[2] >= c[2] + halfHeight * 0.6; }

    std::string FormatShot(const ShotRecord& s)
    {
        std::string out = "shot\t" + std::to_string(s.unixMs) + "\t" + std::to_string(s.sequence);
        for (double v : s.origin)
        {
            out += '\t';
            AppendNumber(out, v, 7);
        }
        for (double v : s.direction)
        {
            out += '\t';
            AppendNumber(out, v, 6);
        }
        out += "\t" + std::to_string(s.slot) + "\t" + std::to_string(s.target) + "\t" + (s.headshot ? "1" : "0") + "\t" + (s.gameHit ? "1" : "0") + "\n";
        return out;
    }
} // namespace aimmod

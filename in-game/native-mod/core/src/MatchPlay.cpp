#include <aimmod/Formats.hpp>
#include <aimmod/MatchPlay.hpp>

#include <algorithm>
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
            else if (c[0] == "loadout" && (c.size() == 6 || c.size() == 8 || c.size() == 9) && !r.loadout)
            {
                RoundState::Loadout l;
                if (!profile(c[1]) || !profile(c[2]) || !Num(c[3], l.armour) || l.armour < 0 || l.armour > 1000 || !Flag(c[4], l.helmet) ||
                    !Flag(c[5], l.kit))
                    return std::nullopt;
                if (c.size() >= 8 && (!profile(c[6]) || !profile(c[7]))) return std::nullopt;
                if (c.size() == 9 && !profile(c[8])) return std::nullopt;
                l.primary = std::string(c[1]);
                l.pistol = std::string(c[2]);
                if (c.size() >= 8)
                {
                    l.knife = std::string(c[6]);
                    l.bomb = std::string(c[7]);
                }
                if (c.size() == 9) l.grenade = std::string(c[8]);
                r.loadout = l;
            }
            else if (c[0] == "feel" && c.size() == 5 && !r.feel)
            {
                RoundState::Feel f;
                if (!Int(c[1], f.salt) || !Flag(c[2], f.crosshair) || (c[3] != "off" && c[3] != "cs" && c[3] != "all") || !Num(c[4], f.adsSensitivity) ||
                    f.adsSensitivity < 0.05 || f.adsSensitivity > 10)
                    return std::nullopt;
                f.zoom = std::string(c[3]);
                r.feel = f;
            }
            else if (c[0] == "bomb" && c.size() == 7 && !r.bomb)
            {
                RoundState::Bomb b;
                if (c[1] != "dropped" && c[1] != "planted" && c[1] != "defused") return std::nullopt;
                if (!Num(c[2], b.x) || !Num(c[3], b.y) || !Num(c[4], b.z) || !Int(c[5], b.explodesMs) || b.explodesMs < 0 || !Flag(c[6], b.defusing)) return std::nullopt;
                if (std::fabs(b.x) > 1e7 || std::fabs(b.y) > 1e7 || std::fabs(b.z) > 1e7) return std::nullopt;
                b.state = std::string(c[1]);
                r.bomb = b;
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

    bool IsHeadHit(const double p[3], const double c[3], double halfHeight) { return p[2] >= c[2] + halfHeight * HeadZoneFraction; }

    CapsulePass RayCapsulePass(const double o[3], const double d[3], const double c[3], double radius, double halfHeight)
    {
        if (auto t = RayCapsule(o, d, c, radius, halfHeight)) return {0, *t, o[2] + d[2] * *t};
        // Closest points of the ray O + sD (s >= 0) and the axis C + tZ (|t| <= seg), clamped and
        // re-projected (the host's TrackGeometry.HitsCapsule does the same).
        const double seg = std::max(0.0, halfHeight - radius);
        const double wx = o[0] - c[0], wy = o[1] - c[1], wz = o[2] - c[2];
        const double b = d[2], dd = d[0] * wx + d[1] * wy + d[2] * wz, denom = 1 - b * b;
        double s = 0, t = wz;
        if (denom >= 1e-9)
        {
            s = (b * wz - dd) / denom;
            t = (wz - b * dd) / denom;
        }
        s = std::max(0.0, s);
        t = std::clamp(t, -seg, seg);
        s = std::max(0.0, -(d[0] * wx + d[1] * wy + d[2] * (wz - t)));
        t = std::clamp(wz + s * d[2], -seg, seg);
        const double px = wx + s * d[0], py = wy + s * d[1], pz = wz + s * d[2] - t;
        return {std::max(0.0, std::sqrt(px * px + py * py + pz * pz) - radius), s, o[2] + d[2] * s};
    }

    std::optional<TargetPick> PickTarget(const double origin[3], const double direction[3], const std::vector<Capsule>& capsules, double tolerance)
    {
        std::optional<TargetPick> best;
        for (std::size_t i = 0; i < capsules.size(); ++i)
        {
            const Capsule& c = capsules[i];
            if (c.radius <= 0 || c.halfHeight < c.radius) continue;
            const CapsulePass pass = RayCapsulePass(origin, direction, c.center, c.radius, c.halfHeight);
            if (pass.gap > tolerance) continue;
            // An exact hit beats a near pass; exact hits: the nearer target; near passes: the smaller gap.
            bool better = true;
            if (best && (pass.gap == 0) != (best->gap == 0)) better = pass.gap == 0;
            else if (best && pass.gap == 0) better = pass.along < best->along;
            else if (best) better = pass.gap < best->gap - 1e-9 || (std::fabs(pass.gap - best->gap) <= 1e-9 && pass.along < best->along);
            if (!better) continue;
            const double point[3] = {origin[0] + direction[0] * pass.along, origin[1] + direction[1] * pass.along, pass.z};
            best = TargetPick{i, pass.along, pass.gap, IsHeadHit(point, c.center, c.halfHeight)};
        }
        return best;
    }

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
        out += "\t" + std::to_string(s.slot) + "\t" + std::to_string(s.target) + "\t" + (s.headshot ? "1" : "0") + "\t" + (s.gameHit ? "1" : "0");
        const bool drawn = s.target != 0 && s.targetRadius > 0 && s.targetHalfHeight >= s.targetRadius;
        for (double v : {drawn ? s.targetCenter[0] : 0.0, drawn ? s.targetCenter[1] : 0.0, drawn ? s.targetCenter[2] : 0.0, drawn ? s.targetRadius : 0.0,
                         drawn ? s.targetHalfHeight : 0.0})
        {
            out += '\t';
            AppendNumber(out, v, 7);
        }
        out += '\t';
        if (s.gameDamage >= 0 && IsUsableNumber(s.gameDamage)) AppendNumber(out, s.gameDamage, 6);
        else out += "-1";
        out += "\t" + std::to_string(std::clamp(s.source, 0, 3)) + "\t";
        // CS weapon feel: the inaccuracy in milliradians, the seed's shot number, whether the game's trace followed it.
        const bool spread = s.inaccuracy > 0 && IsUsableNumber(s.inaccuracy);
        AppendNumber(out, spread ? std::min(s.inaccuracy, 1.0) * 1000 : 0.0, 6);
        out += "\t" + std::to_string(spread ? s.spreadShot : 0) + "\t" + (spread && s.spreadApplied ? "1" : "0") + "\n";
        return out;
    }

    void ShotLog::Ack(std::uint64_t sequence)
    {
        m_ackSeen = true;
        m_acked = std::max(m_acked, sequence);
        while (!m_shots.empty() && m_shots.front().sequence <= m_acked) m_shots.pop_front();
    }

    void ShotLog::Prune(std::int64_t nowMs)
    {
        const std::size_t kept = m_ackSeen ? MaxKept : LegacyKept;
        const std::int64_t age = m_ackSeen ? MaxAgeMs : LegacyAgeMs;
        while (!m_shots.empty() && (m_shots.size() > kept || m_shots.front().unixMs < nowMs - age))
        {
            if (m_ackSeen) ++m_lost; // never taken by the service
            m_shots.pop_front();
        }
    }

    void ShotLog::Clear()
    {
        m_shots.clear();
        m_acked = 0;
        m_ackSeen = false;
    }

    std::optional<ShotAck> ParseShotRequest(std::string_view text)
    {
        while (!text.empty() && (text.back() == '\n' || text.back() == '\r' || text.back() == ' ')) text.remove_suffix(1);
        if (text.size() > 128) return std::nullopt;
        auto c = Cells(text);
        ShotAck ack;
        if (c.size() != 3 || !Int(c[1], ack.sequence) || !Int(c[2], ack.session)) return std::nullopt;
        return ack;
    }
} // namespace aimmod

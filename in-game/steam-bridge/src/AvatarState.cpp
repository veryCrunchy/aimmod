#include "AvatarState.hpp"

#include "Codec.hpp"
#include "PoseFile.hpp"

#include <cmath>
#include <cstdlib>
#include <vector>

namespace bridge::avatarstate
{
    namespace
    {
        std::vector<std::string_view> Split(std::string_view line)
        {
            std::vector<std::string_view> out;
            std::size_t start = 0;
            while (true)
            {
                const auto at = line.find('\t', start);
                out.push_back(line.substr(start, at == std::string_view::npos ? std::string_view::npos : at - start));
                if (at == std::string_view::npos) break;
                start = at + 1;
            }
            return out;
        }
        std::optional<std::int64_t> Integer(std::string_view s)
        {
            if (s.empty() || s.size() > 19) return std::nullopt;
            std::int64_t v = 0;
            for (const char c : s)
            {
                if (c < '0' || c > '9') return std::nullopt;
                v = v * 10 + (c - '0');
            }
            return v;
        }
        std::optional<double> Number(std::string_view s)
        {
            if (s.empty() || s.size() > 32) return std::nullopt;
            const std::string text(s);
            char* end = nullptr;
            const double v = std::strtod(text.c_str(), &end);
            if (!end || *end != '\0' || !std::isfinite(v)) return std::nullopt;
            return v;
        }
    } // namespace

    std::optional<File> Parse(std::string_view text)
    {
        if (text.size() > 64 * 1024) return std::nullopt;
        File file;
        bool header = false;
        std::size_t start = 0;
        while (start < text.size())
        {
            auto end = text.find('\n', start);
            if (end == std::string_view::npos) end = text.size();
            std::string_view line = text.substr(start, end - start);
            start = end + 1;
            if (!line.empty() && line.back() == '\r') line.remove_suffix(1);
            if (line.empty()) continue;
            const auto f = Split(line);
            if (!header)
            {
                const auto seq = f.size() == 2 && f[0] == "AIMMOD_AVATARS_1" ? Integer(f[1]) : std::nullopt;
                if (!seq) return std::nullopt;
                file.sequence = *seq;
                header = true;
            }
            else if (f[0] == "match")
            {
                const auto id = f.size() == 2 ? posefile::Unescape(f[1]) : std::nullopt;
                if (!id || id->size() > 128) return std::nullopt;
                file.match = *id;
            }
            else if (f[0] == "peer")
            {
                if (f.size() != 7 || file.peers.size() >= 64) return std::nullopt;
                const auto peer = ParseId(f[1]);
                if (!peer || !IsIndividualId(*peer)) return std::nullopt;
                if (f[2] != "0" && f[2] != "1") return std::nullopt;
                if (f[3] != "friend" && f[3] != "enemy") return std::nullopt;
                const auto health = Number(f[4]);
                const auto died = Integer(f[5]), respawn = Integer(f[6]);
                if (!health || *health < -1 || *health > 100000 || !died || !respawn) return std::nullopt;
                PeerState s;
                s.alive = f[2] == "1";
                s.friendly = f[3] == "friend";
                s.health = *health;
                s.diedAt = *died;
                s.respawnAt = *respawn;
                file.peers[*peer] = s;
            }
            else return std::nullopt;
        }
        if (!header) return std::nullopt;
        return file;
    }
} // namespace bridge::avatarstate

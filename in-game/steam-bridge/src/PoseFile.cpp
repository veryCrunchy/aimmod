#include "PoseFile.hpp"

#include "Json.hpp"

#include <cmath>
#include <cstdio>
#include <cstdlib>

namespace bridge::posefile
{
    namespace
    {
        std::vector<std::string_view> Split(std::string_view line, char sep)
        {
            std::vector<std::string_view> out;
            std::size_t start = 0;
            while (true)
            {
                const auto at = line.find(sep, start);
                out.push_back(line.substr(start, at == std::string_view::npos ? std::string_view::npos : at - start));
                if (at == std::string_view::npos) break;
                start = at + 1;
            }
            return out;
        }

        std::optional<double> Number(std::string_view text)
        {
            if (text.empty() || text.size() > 40) return std::nullopt;
            const std::string s(text);
            char* end = nullptr;
            const double v = std::strtod(s.c_str(), &end);
            if (!end || *end != '\0' || !std::isfinite(v)) return std::nullopt;
            return v;
        }

        std::optional<std::int64_t> Integer(std::string_view text)
        {
            if (text.empty() || text.size() > 19) return std::nullopt;
            std::int64_t v = 0;
            for (const char c : text)
            {
                if (c < '0' || c > '9') return std::nullopt;
                v = v * 10 + (c - '0');
            }
            return v;
        }

        std::string Num(double v, int digits)
        {
            char buf[48];
            std::snprintf(buf, sizeof(buf), "%.*g", digits, v);
            return buf;
        }
    } // namespace

    bool ValidStreamId(std::string_view id)
    {
        if (id.empty() || id.size() > 64) return false;
        for (const char c : id)
            if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-')) return false;
        return true;
    }

    std::string StreamIdFor(std::uint64_t steamId)
    {
        // FNV-1a 64 over a domain-separated string.
        const std::string input = "aimmod-spectate:" + std::to_string(steamId);
        std::uint64_t hash = 1469598103934665603ull;
        for (const unsigned char c : input)
        {
            hash ^= c;
            hash *= 1099511628211ull;
        }
        char out[24];
        std::snprintf(out, sizeof(out), "s-%016llx", static_cast<unsigned long long>(hash));
        return out;
    }

    std::string Escape(std::string_view text)
    {
        std::string out;
        for (const unsigned char ch : text)
        {
            if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_' || ch == '.' || ch == '~')
                out.push_back(static_cast<char>(ch));
            else
            {
                char hex[4];
                std::snprintf(hex, sizeof(hex), "%%%02X", ch);
                out += hex;
            }
        }
        return out;
    }

    std::optional<std::string> Unescape(std::string_view text)
    {
        std::string out;
        for (std::size_t i = 0; i < text.size(); ++i)
        {
            if (text[i] != '%')
            {
                out.push_back(text[i]);
                continue;
            }
            if (i + 2 >= text.size()) return std::nullopt;
            auto hex = [](char c) -> int {
                if (c >= '0' && c <= '9') return c - '0';
                if (c >= 'A' && c <= 'F') return c - 'A' + 10;
                if (c >= 'a' && c <= 'f') return c - 'a' + 10;
                return -1;
            };
            const int hi = hex(text[i + 1]), lo = hex(text[i + 2]);
            if (hi < 0 || lo < 0) return std::nullopt;
            out.push_back(static_cast<char>(hi * 16 + lo));
            i += 2;
        }
        return out;
    }

    std::optional<File> Parse(std::string_view text)
    {
        if (text.size() > 64 * 1024) return std::nullopt;
        File file;
        bool header = false, meta = false;
        std::size_t start = 0;
        while (start < text.size())
        {
            auto end = text.find('\n', start);
            if (end == std::string_view::npos) end = text.size();
            std::string_view line = text.substr(start, end - start);
            start = end + 1;
            if (!line.empty() && line.back() == '\r') line.remove_suffix(1);
            if (line.empty()) continue;
            const auto f = Split(line, '\t');
            if (!header)
            {
                const auto seq = (f.size() == 2 || f.size() == 3) && f[0] == "AIMMOD_POSE_1" ? Integer(f[1]) : std::nullopt;
                if (!seq) return std::nullopt;
                if (f.size() == 3)
                {
                    if (!ValidStreamId(f[2])) return std::nullopt;
                    file.stream = std::string(f[2]);
                }
                file.sequence = *seq;
                header = true;
            }
            else if (f[0] == "meta")
            {
                if (f.size() != 4 || meta) return std::nullopt;
                const auto scenario = Unescape(f[1]), map = Unescape(f[2]);
                const auto scale = Number(f[3]);
                if (!scenario || !map || !scale || *scale < 0 || *scale > 1000) return std::nullopt;
                file.scenario = *scenario;
                file.map = *map;
                file.scale = *scale;
                meta = true;
            }
            else if (f[0] == "pose")
            {
                if (f.size() != 9 || file.rows.size() >= MaxRows) return std::nullopt;
                Row row;
                const auto ms = Integer(f[1]);
                if (!ms || (!file.rows.empty() && *ms <= file.rows.back().ms)) return std::nullopt;
                row.ms = *ms;
                for (int i = 0; i < 7; ++i)
                {
                    const auto v = Number(f[2 + i]);
                    if (!v || std::fabs(*v) > 1e8) return std::nullopt;
                    row.v[static_cast<std::size_t>(i)] = *v;
                }
                if (row.v[6] <= 1 || row.v[6] >= 179) return std::nullopt;
                file.rows.push_back(row);
            }
            else if (f[0] == "target")
            {
                if (f.size() != 7 || file.targets.size() >= 64) return std::nullopt;
                Target target;
                target.id = std::string(f[1]);
                for (int i = 0; i < 5; ++i)
                {
                    const auto v = Number(f[2 + i]);
                    if (!v) return std::nullopt;
                    target.v[static_cast<std::size_t>(i)] = *v;
                }
                file.targets.push_back(std::move(target));
            }
            else return std::nullopt;
        }
        if (!header || !meta || file.rows.empty()) return std::nullopt;
        return file;
    }

    std::string Format(const File& file)
    {
        std::string out = "AIMMOD_POSE_1\t" + std::to_string(file.sequence) + (ValidStreamId(file.stream) ? "\t" + file.stream : std::string()) + "\nmeta\t" + Escape(file.scenario) + "\t" + Escape(file.map) + "\t" +
                          Num(file.scale, 9) + "\n";
        const std::size_t first = file.rows.size() > MaxRows ? file.rows.size() - MaxRows : 0;
        for (std::size_t i = first; i < file.rows.size(); ++i)
        {
            out += "pose\t" + std::to_string(file.rows[i].ms);
            for (const double v : file.rows[i].v) out += "\t" + Num(v, 7);
            out += '\n';
        }
        for (const auto& t : file.targets)
        {
            out += "target\t" + t.id;
            for (const double v : t.v) out += "\t" + Num(v, 7);
            out += '\n';
        }
        return out;
    }
    std::optional<ScoreFrame> ScoreFromLiveOverlay(std::string_view text)
    {
        const auto doc = json::Parse(text, json::Limits{8192, 3, 512, 64});
        if (!doc || !doc->IsObject() || doc->Int("version") != 1) return std::nullopt;
        ScoreFrame s;
        if (doc->Bool("active").value_or(false)) s.flags |= 1;
        if (doc->Bool("paused").value_or(false)) s.flags |= 2;
        auto num = [&](const char* key, float& out) {
            if (const auto v = doc->Num(key); v && std::isfinite(*v) && std::fabs(*v) < 1e9) out = static_cast<float>(*v);
        };
        auto count = [&](const char* key, std::uint32_t& out) {
            if (const auto v = doc->Num(key); v && *v >= 0 && *v < 4e9) out = static_cast<std::uint32_t>(*v);
        };
        num("score", s.score);
        num("seconds", s.seconds);
        num("remainingSeconds", s.remaining);
        count("shots", s.shots);
        count("hits", s.hits);
        count("kills", s.kills);
        return s;
    }
} // namespace bridge::posefile
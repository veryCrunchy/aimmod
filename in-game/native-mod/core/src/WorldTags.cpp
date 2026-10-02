#include <aimmod/WorldTags.hpp>

#include <cmath>
#include <cstdio>

namespace aimmod::worldtags
{
    namespace
    {
        std::vector<std::string_view> Cells(std::string_view line)
        {
            std::vector<std::string_view> out;
            std::size_t start = 0;
            while (true)
            {
                const auto tab = line.find('\t', start);
                out.push_back(line.substr(start, tab == std::string_view::npos ? std::string_view::npos : tab - start));
                if (tab == std::string_view::npos) break;
                start = tab + 1;
            }
            return out;
        }
        int Hex(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
        std::optional<std::string> Unescape(std::string_view s)
        {
            std::string out;
            for (std::size_t i = 0; i < s.size(); ++i)
            {
                if (s[i] != '%') { out += s[i]; continue; }
                if (i + 2 >= s.size()) return std::nullopt;
                const int hi = Hex(s[i + 1]), lo = Hex(s[i + 2]);
                if (hi < 0 || lo < 0) return std::nullopt;
                out += static_cast<char>(hi * 16 + lo);
                i += 2;
            }
            return out;
        }
        bool StreamId(std::string_view s)
        {
            if (s.empty() || s.size() > 64) return false;
            for (char c : s)
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_')) return false;
            return true;
        }
        void JsonString(std::string& out, const std::string& s)
        {
            out += '"';
            for (unsigned char c : s)
            {
                if (c == '"' || c == '\\') { out += '\\'; out += static_cast<char>(c); }
                else if (c < 0x20)
                {
                    char hex[8];
                    std::snprintf(hex, sizeof hex, "\\u%04x", c);
                    out += hex;
                }
                else out += static_cast<char>(c);
            }
            out += '"';
        }
        std::string Num(double v, int decimals)
        {
            if (!std::isfinite(v)) v = 0;
            char buf[32];
            std::snprintf(buf, sizeof buf, "%.*f", decimals, v);
            return buf;
        }
    } // namespace

    std::optional<Roster> Parse(std::string_view text)
    {
        if (text.empty() || text.size() > MaxBytes) return std::nullopt;
        Roster roster;
        bool first = true;
        while (!text.empty())
        {
            const auto end = text.find('\n');
            std::string_view line = text.substr(0, end);
            text = end == std::string_view::npos ? std::string_view{} : text.substr(end + 1);
            if (!line.empty() && line.back() == '\r') line.remove_suffix(1);
            if (line.empty()) continue;
            const auto c = Cells(line);
            if (first)
            {
                first = false;
                if (c.size() != 2 || c[0] != "AIMMOD_TAGS_1") return std::nullopt;
                continue;
            }
            if (c[0] != "tag" || (c.size() != 6 && c.size() != 7) || !StreamId(c[1]) || (c[2] != "friend" && c[2] != "enemy") || (c[4] != "0" && c[4] != "1")) return std::nullopt;
            if (c[3] != "T" && c[3] != "CT" && c[3] != "0" && c[3] != "1" && c[3] != "2") return std::nullopt;
            auto name = Unescape(c[5]);
            if (!name || name->size() > 64 || roster.size() >= MaxTags) return std::nullopt;
            std::string gear;
            if (c.size() == 7)
            {
                auto g = Unescape(c[6]);
                if (!g || g->size() > MaxGear) return std::nullopt;
                gear = std::move(*g);
            }
            roster[std::string(c[1])] = Who{c[2] == "friend", std::string(c[3]), c[4] == "1", *name, std::move(gear)};
        }
        if (first) return std::nullopt;
        return roster;
    }

    bool Shown(const Who& who, bool aimedAt, bool visible) { return who.friendly || (aimedAt && visible && who.alive); }

    std::string Json(const std::vector<ScreenTag>& tags)
    {
        std::string out = "{\"tags\":[";
        bool comma = false;
        for (const auto& t : tags)
        {
            if (comma) out += ',';
            comma = true;
            out += "{\"n\":";
            JsonString(out, t.name);
            out += ",\"t\":";
            JsonString(out, t.team);
            out += ",\"f\":" + std::string(t.friendly ? "1" : "0") + ",\"a\":" + (t.alive ? "1" : "0") + ",\"c\":" + (t.aimed ? "1" : "0") +
                   ",\"x\":" + Num(t.x, 4) + ",\"y\":" + Num(t.y, 4) + ",\"d\":" + Num(t.metres, 0);
            if (!t.gear.empty())
            {
                out += ",\"g\":";
                JsonString(out, t.gear);
            }
            out += '}';
        }
        out += "]}";
        return out;
    }
} // namespace aimmod::worldtags

#include <aimmod/Formats.hpp>

#include <charconv>
#include <cmath>
#include <cstdio>

namespace aimmod
{
    std::string EscapeField(std::string_view text)
    {
        std::string out;
        out.reserve(text.size());
        for (char c : text)
        {
            switch (c)
            {
            case '%': out += "%25"; break;
            case '\t': out += "%09"; break;
            case '\r': out += "%0D"; break;
            case '\n': out += "%0A"; break;
            default: out += c; break;
            }
        }
        return out;
    }

    void AppendJsonString(std::string& out, std::string_view text)
    {
        static constexpr char hex[] = "0123456789abcdef";
        out += '"';
        for (char ch : text)
        {
            auto c = static_cast<unsigned char>(ch);
            if (c == '"') out += "\\\"";
            else if (c == '\\') out += "\\\\";
            else if (c < 0x20)
            {
                out += "\\u00";
                out += hex[c >> 4];
                out += hex[c & 15];
            }
            else out += ch;
        }
        out += '"';
    }

    void AppendNumber(std::string& out, double value, int digits)
    {
        char buffer[64];
        if (digits <= 0)
        {
            // Shortest representation that round-trips. Values that are exact
            // floats (the game's native type) print as the float the game shows.
            auto asFloat = static_cast<float>(value);
            std::to_chars_result result = static_cast<double>(asFloat) == value
                ? std::to_chars(buffer, buffer + sizeof(buffer), asFloat)
                : std::to_chars(buffer, buffer + sizeof(buffer), value);
            out.append(buffer, result.ptr);
            return;
        }
        auto result = std::to_chars(buffer, buffer + sizeof(buffer), value, std::chars_format::general, digits);
        out.append(buffer, result.ptr);
    }

    std::string FormatNumber(double value, int digits)
    {
        std::string out;
        AppendNumber(out, value, digits);
        return out;
    }

    std::string IsoUtc(std::time_t seconds)
    {
        std::tm parts{};
#ifdef _WIN32
        gmtime_s(&parts, &seconds);
#else
        gmtime_r(&seconds, &parts);
#endif
        char buffer[32];
        std::snprintf(buffer, sizeof(buffer), "%04d-%02d-%02dT%02d:%02d:%02dZ", parts.tm_year + 1900, parts.tm_mon + 1, parts.tm_mday,
                      parts.tm_hour, parts.tm_min, parts.tm_sec);
        return buffer;
    }

    static std::string Optional(const std::optional<double>& value)
    {
        return value && IsUsableNumber(*value) ? FormatNumber(*value, 0) : std::string{};
    }

    std::string FormatJournalLine(const JournalRun& run)
    {
        std::string line = "run";
        for (const std::string& field : {run.id, run.scenario, FormatNumber(run.score, 0), Optional(run.accuracy), Optional(run.duration),
                                         Optional(run.kills), Optional(run.damage), IsoUtc(run.completedAt)})
        {
            line += '\t';
            line += EscapeField(field);
        }
        line += '\n';
        return line;
    }

    bool IsValidAttemptId(std::string_view id)
    {
        if (id.empty() || id.size() > 100) return false;
        for (char c : id)
            if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-')) return false;
        return true;
    }

    bool IsSimpleToken(std::string_view token)
    {
        if (token.empty() || token.size() > 32 || token[0] < 'a' || token[0] > 'z') return false;
        for (char c : token)
            if (!((c >= 'a' && c <= 'z') || c == '-')) return false;
        return true;
    }

    std::string FormatLiveOverlay(const LiveSnapshot& s)
    {
        std::string out = "{\"version\":1,\"active\":";
        out += s.active ? "true" : "false";
        out += ",\"paused\":";
        out += s.paused ? "true" : "false";
        if (IsSimpleToken(s.scoreStatus))
        {
            out += ",\"scoreStatus\":";
            AppendJsonString(out, s.scoreStatus);
        }
        if (IsValidAttemptId(s.id))
        {
            out += ",\"id\":";
            AppendJsonString(out, s.id);
        }
        if (s.transient) out += ",\"transient\":true";
        if (!s.scenario.empty())
        {
            out += ",\"scenario\":";
            AppendJsonString(out, s.scenario);
        }
        const std::pair<const char*, const std::optional<double>*> fields[] = {
            {"score", &s.score}, {"seconds", &s.seconds}, {"shots", &s.shots}, {"hits", &s.hits}, {"kills", &s.kills},
            {"damage", &s.damage}, {"remainingSeconds", &s.remainingSeconds}, {"lastTimeToKillSeconds", &s.lastTimeToKillSeconds}};
        for (const auto& [key, value] : fields)
        {
            if (!*value || !IsUsableNumber(**value)) continue;
            out += ",\"";
            out += key;
            out += "\":";
            AppendNumber(out, **value, 0);
        }
        out += '}';
        return out;
    }

    std::string FormatReplayHeader(const ReplayHeader& h)
    {
        std::string out = "{\"kind\":\"header\",\"version\":1,\"id\":";
        AppendJsonString(out, h.id);
        out += ",\"scenario\":";
        AppendJsonString(out, h.scenario);
        out += ",\"recordedAt\":";
        AppendJsonString(out, IsoUtc(h.recordedAt));
        out += ",\"coordinates\":\"unreal-centimeters\",\"nominalHz\":60";
        if (!h.mapName.empty() && h.mapName.size() <= 1024 && h.mapScale && IsUsableNumber(*h.mapScale) && *h.mapScale > 0)
        {
            out += ",\"mapName\":";
            AppendJsonString(out, h.mapName);
            out += ",\"mapScale\":";
            AppendNumber(out, *h.mapScale, 9);
        }
        if (IsSimpleToken(h.startEvent))
        {
            out += ",\"startEvent\":";
            AppendJsonString(out, h.startEvent);
        }
        out += '}';
        return out;
    }

    static constexpr int ReplayDigits = 7;

    bool AppendReplayFrame(std::string& out, const ReplayFrame& f)
    {
        if (!IsUsableNumber(f.t) || f.t < 0) return false;
        for (double v : f.camera)
            if (!IsUsableNumber(v)) return false;
        for (const ReplayActor& a : f.actors)
        {
            if (a.id == 0 || !IsUsableNumber(a.x) || !IsUsableNumber(a.y) || !IsUsableNumber(a.z) || !IsUsableNumber(a.radius) ||
                !IsUsableNumber(a.halfHeight))
                return false;
        }
        auto num = [&](double v) { AppendNumber(out, v, ReplayDigits); };
        out += "{\"kind\":\"frame\",\"t\":";
        num(f.t);
        out += ",\"camera\":[";
        for (int i = 0; i < 7; ++i)
        {
            if (i) out += ',';
            num(f.camera[i]);
        }
        out += "],\"actors\":[";
        bool first = true;
        for (const ReplayActor& a : f.actors)
        {
            if (!first) out += ',';
            first = false;
            out += '[';
            out += std::to_string(a.id);
            for (double v : {a.x, a.y, a.z, a.radius, a.halfHeight})
            {
                out += ',';
                num(v);
            }
            out += ']';
        }
        out += ']';
        first = true;
        for (const ReplayActor& a : f.actors)
        {
            if (!a.healthPercent || !IsUsableNumber(*a.healthPercent) || *a.healthPercent < 0 || *a.healthPercent > 1) continue;
            out += first ? ",\"health\":[" : ",";
            first = false;
            out += "{\"id\":";
            out += std::to_string(a.id);
            out += ",\"percent\":";
            num(*a.healthPercent);
            out += '}';
        }
        if (!first) out += ']';
        first = true;
        for (const ReplayActor& a : f.actors)
        {
            if (!a.profile || a.profile->empty() || !IsUsableNumber(a.pitch) || !IsUsableNumber(a.yaw) || !IsUsableNumber(a.roll)) continue;
            out += first ? ",\"appearance\":[" : ",";
            first = false;
            out += "{\"id\":";
            out += std::to_string(a.id);
            out += ",\"profile\":";
            AppendJsonString(out, *a.profile);
            out += ",\"rotation\":[";
            num(a.pitch);
            out += ',';
            num(a.yaw);
            out += ',';
            num(a.roll);
            out += "]}";
        }
        if (!first) out += ']';
        const ReplayStats& s = f.stats;
        first = true;
        auto stat = [&](const char* key, double v) {
            out += first ? ",\"stats\":{" : ",";
            first = false;
            out += '"';
            out += key;
            out += "\":";
            num(v);
        };
        const std::pair<const char*, const std::optional<double>*> fields[] = {
            {"score", &s.score}, {"shots", &s.shots}, {"hits", &s.hits}, {"kills", &s.kills}, {"damage", &s.damage}, {"seconds", &s.seconds}};
        for (const auto& [key, value] : fields)
            if (*value && IsUsableNumber(**value)) stat(key, **value);
        if (s.hitTarget && *s.hitTarget > 0)
        {
            out += first ? ",\"stats\":{" : ",";
            first = false;
            out += "\"hitTarget\":";
            out += std::to_string(*s.hitTarget);
        }
        if (s.hitTime && s.hitDelta && IsUsableNumber(*s.hitTime) && IsUsableNumber(*s.hitDelta) && *s.hitTime >= 0 && *s.hitTime <= f.t &&
            *s.hitDelta > 0)
        {
            stat("hitTime", *s.hitTime);
            stat("hitDelta", *s.hitDelta);
        }
        if (!first) out += '}';
        out += '}';
        return true;
    }

    bool AppendReplayInput(std::string& out, double t, std::string_view action, double value)
    {
        if (!IsUsableNumber(t) || t < 0 || !IsUsableNumber(value)) return false;
        out += "{\"kind\":\"input\",\"t\":";
        AppendNumber(out, t, ReplayDigits);
        out += ",\"action\":";
        AppendJsonString(out, action);
        out += ",\"value\":";
        AppendNumber(out, value, ReplayDigits);
        out += '}';
        return true;
    }

    std::string FormatReplayEnd(std::string_view reason, std::uint32_t frames, std::uint32_t inputEvents, std::optional<double> score)
    {
        std::string out = "{\"kind\":\"end\",\"reason\":";
        AppendJsonString(out, reason);
        out += ",\"frames\":" + std::to_string(frames) + ",\"inputEvents\":" + std::to_string(inputEvents);
        if (reason == "completed" && score && IsUsableNumber(*score))
        {
            out += ",\"score\":";
            AppendNumber(out, *score, 0);
        }
        out += '}';
        return out;
    }

    std::string FormatReplayStatus(std::string_view state, std::uint32_t frames, std::uint32_t inputEvents, std::string_view reason)
    {
        std::string out = "{\"state\":";
        AppendJsonString(out, state);
        out += ",\"frames\":" + std::to_string(frames) + ",\"inputEvents\":" + std::to_string(inputEvents) + ",\"reason\":";
        AppendJsonString(out, reason);
        out += "}\n";
        return out;
    }
} // namespace aimmod

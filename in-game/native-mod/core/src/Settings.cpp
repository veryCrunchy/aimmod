#include <aimmod/Settings.hpp>

namespace aimmod
{
    std::optional<NativeSettings> ParseNativeSettings(std::string_view text)
    {
        std::string normalized;
        normalized.reserve(text.size());
        for (std::size_t i = 0; i < text.size(); ++i)
        {
            if (text[i] == '\r' && i + 1 < text.size() && text[i + 1] == '\n') continue;
            normalized += text[i];
        }
        constexpr std::string_view head = "AIMMOD_SETTINGS_1\nreplayRecordingEnabled\t";
        constexpr std::string_view middle = "\nhubHistoryEnabled\t";
        if (normalized.size() != head.size() + 1 + middle.size() + 1 + 1) return std::nullopt;
        std::string_view n = normalized;
        if (!n.starts_with(head) || n.substr(head.size() + 1, middle.size()) != middle || n.back() != '\n') return std::nullopt;
        char capture = n[head.size()];
        char history = n[head.size() + 1 + middle.size()];
        auto flag = [](char c) { return c == '0' || c == '1'; };
        if (!flag(capture) || !flag(history)) return std::nullopt;
        return NativeSettings{capture == '1', history == '1'};
    }

    std::string FormatCoreActive(std::string_view version, std::time_t now, std::string_view capabilities)
    {
        std::string out(CoreActiveHeader);
        out += '\t';
        out += version;
        out += '\t';
        out += std::to_string(static_cast<long long>(now));
        out += '\t';
        out += capabilities;
        out += '\n';
        return out;
    }

    bool IsReplayPlaybackHeader(std::string_view line)
    {
        if (!line.empty() && line.back() == '\r') line.remove_suffix(1);
        constexpr std::string_view prefix = "AIMMOD_REPLAY_";
        if (!line.starts_with(prefix)) return false;
        line.remove_prefix(prefix.size());
        auto digits = [](std::string_view& v) {
            std::size_t i = 0;
            while (i < v.size() && v[i] >= '0' && v[i] <= '9') ++i;
            v.remove_prefix(i);
            return i > 0;
        };
        if (!digits(line) || line.empty() || line[0] != '\t') return false;
        line.remove_prefix(1);
        if (!digits(line)) return false;
        return line == "\t1";
    }
} // namespace aimmod

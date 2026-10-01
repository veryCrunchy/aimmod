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

    int VirtualKeyFromName(std::string_view n)
    {
        if (n.size() >= 2 && n.size() <= 3 && n[0] == 'F')
        {
            int k = 0;
            for (char c : n.substr(1))
            {
                if (c < '0' || c > '9') return 0;
                k = k * 10 + (c - '0');
            }
            return k >= 1 && k <= 12 ? 0x70 + (k - 1) : 0;
        }
        if (n == "Insert") return 0x2D;
        if (n == "Home") return 0x24;
        if (n == "End") return 0x23;
        if (n == "PageUp") return 0x21;
        if (n == "PageDown") return 0x22;
        if (n == "Pause") return 0x13;
        if (n == "ScrollLock") return 0x91;
        return 0;
    }

    std::optional<ClipSettings> ParseClipSettings(std::string_view text)
    {
        ClipSettings out;
        bool first = true;
        while (!text.empty())
        {
            auto end = text.find('\n');
            std::string_view line = text.substr(0, end);
            text = end == std::string_view::npos ? std::string_view{} : text.substr(end + 1);
            if (!line.empty() && line.back() == '\r') line.remove_suffix(1);
            if (first)
            {
                first = false;
                if (line != "AIMMOD_CLIPS_1") return std::nullopt;
                continue;
            }
            if (line.empty()) continue;
            auto tab = line.find('\t');
            if (tab == std::string_view::npos) return std::nullopt;
            std::string_view key = line.substr(0, tab), value = line.substr(tab + 1);
            if (key == "key")
            {
                out.virtualKey = VirtualKeyFromName(value);
                if (!out.virtualKey) return std::nullopt;
                out.key = std::string(value);
            }
            else if (key == "before" || key == "after")
            {
                double v{};
                try { v = std::stod(std::string(value)); } catch (...) { return std::nullopt; }
                if (!(v >= 0 && v <= 60)) return std::nullopt;
                (key == "before" ? out.before : out.after) = v;
            }
            else return std::nullopt;
        }
        if (first || out.before + out.after < 1) return std::nullopt;
        return out;
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

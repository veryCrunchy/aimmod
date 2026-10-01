#include <aimmod/CosmeticsPreview.hpp>

#include <charconv>
#include <cmath>
#include <cstdio>

namespace aimmod
{
    namespace
    {
        bool IsName(std::string_view s, std::size_t max)
        {
            if (s.empty() || s.size() > max) return false;
            const auto alpha = [](char c) { return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'); };
            if (!alpha(s[0])) return false;
            for (char c : s)
                if (!alpha(c) && !(c >= '0' && c <= '9') && c != '_') return false;
            return true;
        }
        bool Number(std::string_view s, double& out)
        {
            if (s.empty() || s.size() > 32) return false;
            const auto* end = s.data() + s.size();
            auto [ptr, ec] = std::from_chars(s.data(), end, out);
            return ec == std::errc{} && ptr == end && std::isfinite(out);
        }
        bool Integer(std::string_view s, std::int64_t& out)
        {
            if (s.empty() || s.size() > 20) return false;
            const auto* end = s.data() + s.size();
            auto [ptr, ec] = std::from_chars(s.data(), end, out);
            return ec == std::errc{} && ptr == end;
        }
        // "<Name>:<n>[,<n>...]" with exactly `count` numbers in [lo, hi].
        bool Param(std::string_view value, std::size_t count, double lo, double hi, PreviewParam& out)
        {
            const auto colon = value.find(':');
            if (colon == std::string_view::npos || !IsName(value.substr(0, colon), 64)) return false;
            out.name = std::string(value.substr(0, colon));
            std::string_view rest = value.substr(colon + 1);
            for (std::size_t i = 0; i < count; ++i)
            {
                const auto comma = rest.find(',');
                const bool last = i + 1 == count;
                if (last != (comma == std::string_view::npos)) return false;
                if (!Number(last ? rest : rest.substr(0, comma), out.value[i]) || out.value[i] < lo || out.value[i] > hi) return false;
                if (!last) rest = rest.substr(comma + 1);
            }
            return true;
        }
    } // namespace

    std::string PreviewRequest::LookKey() const
    {
        std::string key = model + "|" + skin;
        char buffer[64];
        for (const auto& v : vectors)
        {
            std::snprintf(buffer, sizeof buffer, "|v:%.4f,%.4f,%.4f,%.4f", v.value[0], v.value[1], v.value[2], v.value[3]);
            key += "|" + v.name + buffer;
        }
        for (const auto& s : scalars)
        {
            std::snprintf(buffer, sizeof buffer, "|s:%.4f", s.value[0]);
            key += "|" + s.name + buffer;
        }
        return key;
    }

    std::optional<PreviewRequest> ParsePreviewRequest(std::string_view text, std::int64_t now, std::string* error)
    {
        auto bad = [error](const char* why) -> std::optional<PreviewRequest> {
            if (error) *error = why;
            return std::nullopt;
        };
        if (text.empty() || text.size() > MaxPreviewRequestBytes) return bad("size");
        PreviewRequest r;
        bool version = false, expires = false, seq = false, model = false, skin = false, yaw = false;
        while (!text.empty())
        {
            auto nl = text.find('\n');
            std::string_view line = text.substr(0, nl);
            text = nl == std::string_view::npos ? std::string_view{} : text.substr(nl + 1);
            if (!line.empty() && line.back() == '\r') line.remove_suffix(1);
            if (line.empty()) continue;
            const auto eq = line.find('=');
            if (eq == std::string_view::npos) return bad("line");
            const std::string_view key = line.substr(0, eq), value = line.substr(eq + 1);
            for (char c : value)
                if (static_cast<unsigned char>(c) < 0x20) return bad("control");
            auto once = [&](bool& seen) {
                if (seen) return false;
                seen = true;
                return true;
            };
            if (key == "v")
            {
                if (!once(version) || value != "1") return bad("version");
            }
            else if (key == "expires")
            {
                if (!once(expires) || !Integer(value, r.expires)) return bad("expires");
            }
            else if (key == "seq")
            {
                std::int64_t n{};
                if (!once(seq) || !Integer(value, n) || n < 0) return bad("seq");
                r.seq = static_cast<std::uint64_t>(n);
            }
            else if (key == "model")
            {
                if (!once(model) || !IsName(value, 32)) return bad("model");
                r.model = std::string(value);
            }
            else if (key == "skin")
            {
                if (!once(skin) || (!value.empty() && !IsName(value, 32))) return bad("skin");
                r.skin = std::string(value);
            }
            else if (key == "yaw")
            {
                if (!once(yaw) || !Number(value, r.yaw) || r.yaw < -180.0 || r.yaw > 180.0) return bad("yaw");
            }
            else if (key == "vector")
            {
                PreviewParam p;
                if (r.vectors.size() >= MaxPreviewParams || !Param(value, 4, 0.0, 1.0, p)) return bad("vector");
                r.vectors.push_back(std::move(p));
            }
            else if (key == "scalar")
            {
                PreviewParam p;
                if (r.scalars.size() >= MaxPreviewParams || !Param(value, 1, -10.0, 10.0, p)) return bad("scalar");
                r.scalars.push_back(std::move(p));
            }
            // Unknown keys are ignored (newer services).
        }
        if (!version || !expires || !seq || !model) return bad("missing");
        if (r.expires <= now) return bad("expired");
        if (r.expires > now + MaxPreviewLifetime) return bad("too-far");
        return r;
    }

    PreviewDecision DecidePreview(const std::optional<PreviewRequest>& request, const PreviewGameState& s)
    {
        if (!request) return {false, "Cosmetics page not open"};
        if (s.inChallenge != false) return {false, "challenge or unknown challenge state"};
        if (s.benchmark != false) return {false, "benchmark or unknown benchmark state"};
        if (s.editor != false) return {false, "scenario editor or unknown editor state"};
        if (s.loading != false) return {false, "scenario loading"};
        return {true, "Cosmetics page open"};
    }

    std::string FormatPreviewFrame(std::uint64_t seq, std::string_view file, int width, int height)
    {
        return "v=1\nseq=" + std::to_string(seq) + "\nfile=" + std::string(file) + "\nwidth=" + std::to_string(width) + "\nheight=" + std::to_string(height) + "\n";
    }
} // namespace aimmod

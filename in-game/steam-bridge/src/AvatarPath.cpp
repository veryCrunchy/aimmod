#include "AvatarPath.hpp"

#include <charconv>
#include <cmath>

namespace bridge::ghost
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

        bool Number(std::string_view s, double& v)
        {
            if (s.empty() || s.size() > 32) return false;
            const auto r = std::from_chars(s.data(), s.data() + s.size(), v);
            return r.ec == std::errc{} && r.ptr == s.data() + s.size() && std::isfinite(v) && std::fabs(v) < 1e9;
        }

        // %XX unescape (the pose-file escaping); control characters are refused.
        std::optional<std::string> Unescape(std::string_view s)
        {
            std::string out;
            for (std::size_t i = 0; i < s.size(); ++i)
            {
                char c = s[i];
                if (c == '%')
                {
                    if (i + 2 >= s.size()) return std::nullopt;
                    int v = 0;
                    const auto r = std::from_chars(s.data() + i + 1, s.data() + i + 3, v, 16);
                    if (r.ec != std::errc{} || r.ptr != s.data() + i + 3) return std::nullopt;
                    c = static_cast<char>(v);
                    i += 2;
                }
                if (static_cast<unsigned char>(c) < 0x20) return std::nullopt;
                out.push_back(c);
            }
            if (out.size() > 1024) return std::nullopt;
            return out;
        }

        bool Fail(std::string* error, const char* why)
        {
            if (error) *error = why;
            return false;
        }
    } // namespace

    std::optional<AvatarPath> AvatarPath::Parse(std::string_view text, std::string* error)
    {
        AvatarPath path;
        if (text.size() > 4 * 1024 * 1024) { Fail(error, "file too large"); return std::nullopt; }
        std::size_t start = 0;
        bool header = false;
        while (start < text.size())
        {
            auto end = text.find('\n', start);
            auto line = text.substr(start, end == std::string_view::npos ? std::string_view::npos : end - start);
            start = end == std::string_view::npos ? text.size() : end + 1;
            if (!line.empty() && line.back() == '\r') line.remove_suffix(1);
            if (line.empty()) continue;
            if (!header)
            {
                if (line != "AIMMOD_AVATAR_PATH_1") { Fail(error, "not an avatar path (header)"); return std::nullopt; }
                header = true;
                continue;
            }
            const auto c = Split(line, '\t');
            if (c[0] == "meta" && c.size() == 4)
            {
                auto scenario = Unescape(c[1]), map = Unescape(c[2]);
                double scale = 0;
                if (!scenario || !map || !Number(c[3], scale) || scale <= 0) { Fail(error, "bad meta line"); return std::nullopt; }
                path.scenario = *scenario, path.map = *map, path.scale = scale;
            }
            else if (c[0] == "p" && c.size() == 7)
            {
                double v[6]{};
                for (int i = 0; i < 6; ++i)
                    if (!Number(c[i + 1], v[i])) { Fail(error, "bad path row"); return std::nullopt; }
                const double t = v[0] / 1000.0;
                if (t < 0 || std::fabs(v[4]) > 90.5 || (!path.rows.empty() && t <= path.rows.back().t) || path.rows.size() >= MaxRows)
                {
                    Fail(error, "path rows must start at 0 or later, increase and stay within limits");
                    return std::nullopt;
                }
                path.rows.push_back({t, v[1], v[2], v[3], v[4], v[5]});
            }
            else { Fail(error, "unknown line"); return std::nullopt; }
        }
        if (path.rows.size() < 2) { Fail(error, "fewer than two path rows"); return std::nullopt; }
        return path;
    }

    RemoteTransform AvatarPath::At(double seconds, double eyeAboveCentre) const
    {
        RemoteTransform r;
        if (rows.size() < 2) return r;
        const double duration = Duration();
        double t = duration > 0 ? std::fmod(std::max(0.0, seconds), duration) : 0;
        auto it = std::upper_bound(rows.begin(), rows.end(), t, [](double v, const PathRow& row) { return v < row.t; });
        const std::size_t i = it == rows.begin() ? 0 : std::min<std::size_t>(static_cast<std::size_t>(it - rows.begin()) - 1, rows.size() - 2);
        const PathRow& a = rows[i];
        const PathRow& b = rows[i + 1];
        const double span = std::max(1e-6, b.t - a.t);
        const double u = std::clamp((t - a.t) / span, 0.0, 1.0);
        r.x = a.x + (b.x - a.x) * u;
        r.y = a.y + (b.y - a.y) * u;
        r.z = a.z + (b.z - a.z) * u - eyeAboveCentre;
        r.yaw = LerpAngle(a.yaw, b.yaw, u);
        r.pitch = a.pitch + (b.pitch - a.pitch) * u;
        // A long gap (a cut in the recording) holds the pose instead of sliding.
        if (span <= 0.25)
        {
            r.vx = (b.x - a.x) / span;
            r.vy = (b.y - a.y) / span;
            r.vz = (b.z - a.z) / span;
        }
        r.halfHeight = DefaultHalfHeight;
        return r;
    }
} // namespace bridge::ghost

#include <aimmod/Formats.hpp>
#include <aimmod/PlaybackFrame.hpp>

#include <algorithm>
#include <charconv>
#include <cmath>

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
                if (tab == std::string_view::npos) break;
                start = tab + 1;
            }
            return out;
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
            return r.ec == std::errc() && r.ptr == s.data() + s.size();
        }
        double Angle(double a, double b, double u)
        {
            double d = std::fmod(b - a + 540.0, 360.0);
            if (d < 0) d += 360.0;
            return a + (d - 180.0) * u;
        }
    } // namespace

    std::optional<PlaybackFrame> ParsePlaybackFrame(std::string_view text)
    {
        if (text.empty() || text.back() != '\n' || text.size() > 524288) return std::nullopt;
        PlaybackFrame f;
        std::unordered_map<std::uint32_t, std::size_t> index;
        bool first = true, haveTime = false;
        while (!text.empty())
        {
            auto end = text.find('\n');
            std::string_view line = text.substr(0, end);
            text = text.substr(end + 1);
            if (!line.empty() && line.back() == '\r') line.remove_suffix(1);
            auto c = Cells(line);
            if (first)
            {
                first = false;
                if (c.size() != 3 || c[0] != "AIMMOD_REPLAY_6" || !Int(c[1], f.revision) || (c[2] != "0" && c[2] != "1")) return std::nullopt;
                f.visible = c[2] == "1";
                continue;
            }
            if (c[0] == "time")
            {
                if (c.size() != 5 || !Num(c[1], f.time) || !Num(c[2], f.duration) || (c[3] != "0" && c[3] != "1") || !Num(c[4], f.speed) ||
                    f.speed < 0.1 || f.speed > 4)
                    return std::nullopt;
                f.playing = c[3] == "1";
                haveTime = true;
            }
            else if (c[0] == "clock")
            {
                std::int64_t ms{};
                if (c.size() != 3 || !Int(c[1], ms) || !Num(c[2], f.clockTime)) return std::nullopt;
                f.clockUnixMs = ms;
            }
            else if (c[0] == "motion")
            {
                PlaybackFrame::Sample s;
                if (c.size() != 9 || !Num(c[1], s.t) || f.motion.size() >= 64) return std::nullopt;
                for (int i = 0; i < 7; ++i)
                    if (!Num(c[static_cast<std::size_t>(i) + 2], s.camera[i])) return std::nullopt;
                if (!f.motion.empty() && s.t < f.motion.back().t) return std::nullopt;
                if (s.camera[6] <= 1 || s.camera[6] >= 179) return std::nullopt;
                f.motion.push_back(s);
            }
            else if (c[0] == "actor" || c[0] == "velocity")
            {
                std::uint32_t id{};
                const bool actor = c[0] == "actor";
                if (c.size() != (actor ? 7u : 5u) || !Int(c[1], id) || id == 0) return std::nullopt;
                double v[3];
                for (int i = 0; i < 3; ++i)
                    if (!Num(c[static_cast<std::size_t>(i) + 2], v[i])) return std::nullopt;
                auto it = index.find(id);
                if (it == index.end())
                {
                    if (!actor || f.targets.size() >= 128) return std::nullopt; // velocity rows follow their actor row
                    it = index.emplace(id, f.targets.size()).first;
                    f.targets.push_back({id});
                }
                auto& t = f.targets[it->second];
                if (actor)
                    for (int i = 0; i < 3; ++i) t.position[i] = v[i];
                else
                {
                    for (int i = 0; i < 3; ++i) t.velocity[i] = v[i];
                    t.moving = true;
                }
            }
        }
        if (f.visible && !haveTime) return std::nullopt;
        return f;
    }

    double PlaybackTime(const PlaybackFrame& f, std::int64_t nowUnixMs)
    {
        double t = f.time;
        if (f.playing && f.clockUnixMs)
        {
            // The publisher's clock and ours are the same wall clock: no drift,
            // no smoothing needed. Allow a small negative delta (clock steps).
            const double elapsed = std::max(-0.05, static_cast<double>(nowUnixMs - *f.clockUnixMs) / 1000.0);
            t = f.clockTime + elapsed * f.speed;
        }
        if (!f.motion.empty()) t = std::clamp(t, f.motion.front().t, f.motion.back().t);
        return std::min(t, f.duration);
    }

    bool CameraAt(const PlaybackFrame& f, double t, double out[7])
    {
        if (f.motion.empty()) return false;
        std::size_t i = 0;
        while (i + 1 < f.motion.size() && f.motion[i + 1].t < t) ++i;
        const auto& a = f.motion[i];
        const auto& b = f.motion[std::min(i + 1, f.motion.size() - 1)];
        const double u = b.t > a.t ? std::clamp((t - a.t) / (b.t - a.t), 0.0, 1.0) : 0.0;
        for (int k = 0; k < 7; ++k) out[k] = (k == 4 || k == 5) ? Angle(a.camera[k], b.camera[k], u) : a.camera[k] + (b.camera[k] - a.camera[k]) * u;
        return true;
    }
} // namespace aimmod

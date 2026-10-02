#pragma once
// Bots (the native service's MultiplayerService.Bots.cs). The host's service steers each bot's
// walking avatar and asks what it can see; a client's service passes on where the host has them.
//
// bot-orders.tsv (service -> AimModSteam), rewritten on change and every second:
//   AIMMOD_BOTS_1\t<sequence>
//   bot\t<peer>\t<roam|goal|hold>[\t<x>\t<y>\t<z>[\t<stop>]]   goal: walk there and stay (stop: only
//                                                    that fraction of the way, 0..1); hold: stand still
//   via\t<peer>\t<x>\t<y>\t<z>\t<fraction>         first that fraction of the way to this point, then the goal
//   fight\t<peer>\t<0..1>                            holding in a fight: strafe that hard
//   turn\t<peer>\t<degrees per second>               how fast it turns (by difficulty)
//   debug\t<0|1>                                     draw the bots' paths and goals in the world
//   face\t<peer>\t<x>\t<y>\t<z>                       look at that point (an enemy's eye)
//   place\t<peer>\t<token>\t<x>\t<y>\t<z>\t<yaw>      stand there once per token (a round start, a respawn)
//   sight\t<peer>\t<tag>\t<x>\t<y>\t<z>               trace from the bot's eye to that point
//   pose\t<peer>\t<x>\t<y>\t<z>\t<yaw>                a client: the host's position for this bot (capsule centre)
//
// bot-sight.tsv (AimModSteam -> service), 10 times a second while bots are ordered:
//   AIMMOD_BOTSIGHT_1\t<unix ms>
//   bot\t<peer>\t<x>\t<y>\t<z>\t<yaw>\t<floor>        the bot avatar's capsule centre, and the floor under it
//   seen\t<peer>\t<tag>\t<0|1>                        whether the line from its eye to the target is clear

#include <array>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <map>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace bridge::bots
{
    struct Order
    {
        enum class Mode { Roam, Goal, Hold, Pose };
        Mode mode = Mode::Roam;
        std::optional<std::array<double, 3>> goal;
        std::optional<std::array<double, 3>> face;
        std::string placeToken;
        std::array<double, 4> placeAt{};
        struct Target
        {
            int tag = 0;
            std::array<double, 3> at{};
        };
        std::vector<Target> sight;
        std::array<double, 4> pose{}; // x, y, z, yaw (Mode::Pose)
        double stop = 1;                                  // walk only this fraction of the way to the goal
        std::optional<std::array<double, 4>> via;          // x, y, z, fraction: a detour first
        double fight = 0;                                 // strafe in a fight (0..1)
        double turn = 0;                                  // degrees per second, 0: the walker's default
    };
    struct Orders
    {
        std::int64_t sequence = 0;
        std::map<std::uint64_t, Order> bots;
        bool debug = false;
    };

    namespace detail
    {
        inline std::vector<std::string_view> Split(std::string_view line)
        {
            std::vector<std::string_view> parts;
            std::size_t start = 0;
            for (;;)
            {
                const auto tab = line.find('\t', start);
                parts.push_back(line.substr(start, tab == std::string_view::npos ? std::string_view::npos : tab - start));
                if (tab == std::string_view::npos || parts.size() > 16) break;
                start = tab + 1;
            }
            return parts;
        }
        inline std::optional<double> Number(std::string_view s)
        {
            if (s.empty() || s.size() > 24) return std::nullopt;
            char buffer[32]{};
            for (std::size_t i = 0; i < s.size(); ++i)
            {
                const char c = s[i];
                if (!((c >= '0' && c <= '9') || c == '-' || c == '.' || c == 'e' || c == 'E' || c == '+')) return std::nullopt;
                buffer[i] = c;
            }
            char* end = nullptr;
            const double v = std::strtod(buffer, &end);
            if (end != buffer + s.size() || !std::isfinite(v) || std::fabs(v) > 1e7) return std::nullopt;
            return v;
        }
        inline std::optional<std::uint64_t> Peer(std::string_view s)
        {
            if (s.empty() || s.size() > 2) return std::nullopt;
            std::uint64_t v = 0;
            for (const char c : s)
            {
                if (c < '0' || c > '9') return std::nullopt;
                v = v * 10 + static_cast<std::uint64_t>(c - '0');
            }
            if (v < 1 || v > 16) return std::nullopt;
            return v;
        }
        inline bool Point(const std::vector<std::string_view>& p, std::size_t from, std::array<double, 3>& out)
        {
            if (p.size() < from + 3) return false;
            for (std::size_t i = 0; i < 3; ++i)
            {
                const auto v = Number(p[from + i]);
                if (!v) return false;
                out[i] = *v;
            }
            return true;
        }
    } // namespace detail

    // Lenient per row (a bad row is skipped), strict on the header; at most 16 peers, 8 sight targets each.
    inline std::optional<Orders> Parse(std::string_view text)
    {
        Orders orders;
        std::size_t pos = 0;
        bool header = false;
        int rows = 0;
        while (pos <= text.size() && rows < 512)
        {
            auto end = text.find('\n', pos);
            if (end == std::string_view::npos) end = text.size();
            std::string_view line = text.substr(pos, end - pos);
            pos = end + 1;
            if (!line.empty() && line.back() == '\r') line.remove_suffix(1);
            if (line.empty()) { if (end >= text.size()) break; continue; }
            ++rows;
            const auto p = detail::Split(line);
            if (!header)
            {
                if (p.size() != 2 || p[0] != "AIMMOD_BOTS_1") return std::nullopt;
                const auto seq = detail::Number(p[1]);
                orders.sequence = seq ? static_cast<std::int64_t>(*seq) : 0;
                header = true;
                continue;
            }
            if (p[0] == "debug" && p.size() == 2) { orders.debug = p[1] == "1"; continue; }
            if (p.size() < 3) continue;
            const auto peer = detail::Peer(p[1]);
            if (!peer) continue;
            Order& o = orders.bots[*peer];
            std::array<double, 3> xyz{};
            if (p[0] == "bot")
            {
                if (p[2] == "roam") o.mode = Order::Mode::Roam;
                else if (p[2] == "hold") o.mode = Order::Mode::Hold;
                else if (p[2] == "goal" && detail::Point(p, 3, xyz))
                {
                    o.mode = Order::Mode::Goal;
                    o.goal = xyz;
                    if (p.size() >= 7)
                        if (const auto stop = detail::Number(p[6]); stop && *stop > 0 && *stop <= 1) o.stop = *stop;
                }
            }
            else if (p[0] == "face" && detail::Point(p, 2, xyz)) o.face = xyz;
            else if (p[0] == "via" && p.size() >= 6 && detail::Point(p, 2, xyz))
            {
                if (const auto f = detail::Number(p[5]); f && *f > 0 && *f <= 1) o.via = std::array<double, 4>{xyz[0], xyz[1], xyz[2], *f};
            }
            else if (p[0] == "fight" && p.size() >= 3)
            {
                if (const auto f = detail::Number(p[2]); f && *f >= 0 && *f <= 1) o.fight = *f;
            }
            else if (p[0] == "turn" && p.size() >= 3)
            {
                if (const auto t = detail::Number(p[2]); t && *t >= 30 && *t <= 3600) o.turn = *t;
            }
            else if (p[0] == "place" && p.size() >= 7 && p[2].size() <= 32 && detail::Point(p, 3, xyz))
            {
                const auto yaw = detail::Number(p[6]);
                o.placeToken = std::string(p[2]);
                o.placeAt = {xyz[0], xyz[1], xyz[2], yaw.value_or(0)};
            }
            else if (p[0] == "sight" && p.size() >= 6 && o.sight.size() < 8 && detail::Point(p, 3, xyz))
            {
                const auto tag = detail::Number(p[2]);
                if (tag && *tag >= 0 && *tag < 64 && *tag == std::floor(*tag)) o.sight.push_back({static_cast<int>(*tag), xyz});
            }
            else if (p[0] == "pose" && p.size() >= 6 && detail::Point(p, 2, xyz))
            {
                const auto yaw = detail::Number(p[5]);
                o.mode = Order::Mode::Pose;
                o.pose = {xyz[0], xyz[1], xyz[2], yaw.value_or(0)};
            }
            if (orders.bots.size() > 16) return std::nullopt;
        }
        if (!header) return std::nullopt;
        return orders;
    }

    struct Report
    {
        std::uint64_t peer = 0;
        double x = 0, y = 0, z = 0, yaw = 0;
        double floor = 0; // the floor under it (feet), where a dropped bomb lands
        std::vector<std::pair<int, bool>> seen;
    };
    inline std::string Format(std::int64_t unixMs, const std::vector<Report>& reports)
    {
        std::string text = "AIMMOD_BOTSIGHT_1\t" + std::to_string(unixMs) + "\n";
        char line[160];
        for (const auto& r : reports)
        {
            std::snprintf(line, sizeof(line), "bot\t%llu\t%.1f\t%.1f\t%.1f\t%.1f\t%.1f\n", static_cast<unsigned long long>(r.peer), r.x, r.y, r.z, r.yaw, r.floor);
            text += line;
            for (const auto& [tag, visible] : r.seen)
            {
                std::snprintf(line, sizeof(line), "seen\t%llu\t%d\t%d\n", static_cast<unsigned long long>(r.peer), tag, visible ? 1 : 0);
                text += line;
            }
        }
        return text;
    }
} // namespace bridge::bots

// spectate-view.tsv (service -> AimModSteam), while the local player is dead in a CS round:
//   AIMMOD_VIEW_1\t<unix ms>
//   view\t<peer>          the avatar to watch: a SteamID64, or a stand-in (bot) peer 1..16
// The camera follows that avatar as this game draws it (no pose stream from another machine);
// no file, a stale one (over 3 s) or no view row gives the view back.
namespace bridge::view
{
    constexpr std::int64_t MaxAgeMs = 3000;
    inline std::optional<std::uint64_t> Parse(std::string_view text, std::int64_t nowMs)
    {
        if (text.size() > 4096) return std::nullopt;
        const auto nl = text.find('\n');
        std::string_view head = text.substr(0, nl);
        if (!head.empty() && head.back() == '\r') head.remove_suffix(1);
        const auto h = bots::detail::Split(head);
        if (h.size() != 2 || h[0] != "AIMMOD_VIEW_1") return std::nullopt;
        // Digits only, at most 20, no overflow.
        const auto unsignedNumber = [](std::string_view s) -> std::optional<std::uint64_t> {
            if (s.empty() || s.size() > 20) return std::nullopt;
            std::uint64_t v = 0;
            for (const char c : s)
            {
                if (c < '0' || c > '9') return std::nullopt;
                const auto digit = static_cast<std::uint64_t>(c - '0');
                if (v > (UINT64_MAX - digit) / 10) return std::nullopt;
                v = v * 10 + digit;
            }
            return v;
        };
        const auto at = unsignedNumber(h[1]);
        if (!at || *at > static_cast<std::uint64_t>(INT64_MAX) || std::llabs(static_cast<std::int64_t>(*at) - nowMs) > MaxAgeMs) return std::nullopt;
        if (nl == std::string_view::npos) return std::nullopt;
        std::string_view line = text.substr(nl + 1);
        if (const auto end = line.find('\n'); end != std::string_view::npos) line = line.substr(0, end);
        if (!line.empty() && line.back() == '\r') line.remove_suffix(1);
        const auto p = bots::detail::Split(line);
        if (p.size() != 2 || p[0] != "view") return std::nullopt;
        const auto peer = unsignedNumber(p[1]);
        if (!peer || *peer == 0) return std::nullopt;
        return peer;
    }
} // namespace bridge::view

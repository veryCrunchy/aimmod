#pragma once
// Bots (the native service's MultiplayerService.Bots.cs). The host's service steers each bot's
// walking avatar and asks what it can see; a client's service passes on where the host has them.
//
// bot-orders.tsv (service -> AimModSteam), rewritten on change and every second:
//   AIMMOD_BOTS_1\t<sequence>
//   bot\t<peer>\t<roam|goal|hold>[\t<x>\t<y>\t<z>[\t<stop>]]   goal: walk there and stay (stop: only
//                                                    that fraction of the way, 0..1); hold: stand still
//   via\t<peer>\t<x>\t<y>\t<z>\t<fraction>         first that fraction of the way to this point, then the goal
//   fight\t<peer>\t<0..1>[\t<counter|ad>]            holding in a fight: strafe that hard; counter: strafe
//                                                    0.2-0.45 s, a dead stop within 80 ms, still 0.25-0.6 s
//                                                    (the shooting window), the other way; ad (or none):
//                                                    plain side to side
//   turn\t<peer>\t<degrees per second>               how fast it turns (by difficulty)
//   aim\t<peer>\t<deg/s^2>\t<overshoot>              eased turning: the view's angular acceleration
//                                                    (100..100000; default 6 x turn) up to `turn`, and how
//                                                    much it overshoots and settles (0..0.5, default 0.05)
//   move\t<peer>\t<run|walk>\t<stand|crouch>\t<0|1>  how it walks: run, shift-walk (52% of the run speed,
//                                                    silent) or crouched (34%, a crouched body); 1: with no
//                                                    face order, pre-aim round the next corner of its path
//   peek\t<peer>\t<jiggle|wide|crouch>\t<x>\t<y>\t<z>   holding: peek that point (an angle, at head height)
//                                                    from where it holds: jiggle out and back, swing out
//                                                    wide and stay, or a step out and crouch
//   avoid\t<peer>\t<x>\t<y>\t<z>\t<radius>\t<cost>    keep out of this circle (a smoke, a fire) when it can:
//                                                    radius 10..3000 cm, extra cost per grid step inside
//                                                    0..100000 (1000 or more: only when there is no other
//                                                    way); up to 8 per bot
//   debug\t<0|1>                                     draw the bots' paths and goals in the world
//   face\t<peer>\t<x>\t<y>\t<z>                       look at that point (an enemy's eye)
//   place\t<peer>\t<token>\t<x>\t<y>\t<z>\t<yaw>      stand there once per token (a round start, a respawn)
//   sight\t<peer>\t<tag>\t<x>\t<y>\t<z>               trace from the bot's eye to that point
//   pose\t<peer>\t<x>\t<y>\t<z>\t<yaw>                a client: the host's position for this bot (capsule centre)
//
// bot-sight.tsv (AimModSteam -> service), 10 times a second while bots are ordered:
//   AIMMOD_BOTSIGHT_1\t<unix ms>
//   bot\t<peer>\t<x>\t<y>\t<z>\t<yaw>\t<floor>        the bot avatar's capsule centre, and the floor under it
//   vel\t<peer>\t<speed>\t<0|1>                       its horizontal speed (cm/s; near 0: stopped, e.g. to
//                                                    shoot after a counter-strafe) and whether it crouches
//   seen\t<peer>\t<tag>\t<0|1>                        whether the line from its eye to the target is clear

#include <algorithm>
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
        bool counterStrafe = false;                       // the fight's style: counter-strafing, else side to side
        double turn = 0;                                  // degrees per second, 0: the walker's default
        double aimAccel = 0;                              // deg/s^2, 0: the walker's default (and its overshoot)
        double aimOvershoot = 0;
        enum class Gait { Run, Walk };
        Gait gait = Gait::Run;
        bool crouch = false;                              // crouch-walk, the body crouched
        bool preaim = false;                              // look round the next corner of its path
        enum class Peek { None, Jiggle, Wide, Crouch };
        Peek peek = Peek::None;
        std::array<double, 3> peekAt{};
        std::vector<std::array<double, 5>> avoid;         // x, y, z, radius, cost per grid step
    };
    struct AreaRequest
    {
        std::string key;
        std::array<double, 3> centre{};
        double rmin = 0, rmax = 0, entry = 0;
        std::vector<std::array<double, 3>> sources;
    };
    struct Orders
    {
        std::int64_t sequence = 0;
        std::map<std::uint64_t, Order> bots;
        bool debug = false;
        std::vector<AreaRequest> areas;
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
        // An area's key: 1..32 letters, digits, '.', '_' or '-'.
        inline bool Key(std::string_view s)
        {
            if (s.empty() || s.size() > 32) return false;
            for (const char c : s)
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-')) return false;
            return true;
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

    // Lenient per row (a bad row is skipped), strict on the header; at most 16 peers, 8 sight targets and 8 avoid zones each.
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
            if (p[0] == "area" || p[0] == "from")
            {
                std::array<double, 3> at{};
                if (p.size() < 5 || !detail::Key(p[1]) || !detail::Point(p, 2, at)) continue;
                const auto it = std::find_if(orders.areas.begin(), orders.areas.end(), [&](const AreaRequest& a) { return a.key == p[1]; });
                if (p[0] == "area")
                {
                    if (p.size() < 8 || it != orders.areas.end() || orders.areas.size() >= 4) continue;
                    const auto rmin = detail::Number(p[5]), rmax = detail::Number(p[6]), entry = detail::Number(p[7]);
                    if (!rmin || !rmax || !entry || *rmin < 0 || *rmax < 100 || *rmax > 20000 || *rmin >= *rmax || *entry < 100 || *entry > 20000) continue;
                    orders.areas.push_back({std::string(p[1]), at, *rmin, *rmax, *entry, {}});
                }
                else if (it != orders.areas.end() && it->sources.size() < 4) it->sources.push_back(at);
                continue;
            }
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
                const bool styled = p.size() >= 4;
                const bool counter = styled && p[3] == "counter";
                const bool knownStyle = !styled || counter || p[3] == "ad";
                if (const auto f = detail::Number(p[2]); knownStyle && f && *f >= 0 && *f <= 1)
                {
                    o.fight = *f;
                    o.counterStrafe = counter;
                }
            }
            else if (p[0] == "turn" && p.size() >= 3)
            {
                if (const auto t = detail::Number(p[2]); t && *t >= 30 && *t <= 3600) o.turn = *t;
            }
            else if (p[0] == "aim" && p.size() >= 4)
            {
                const auto a = detail::Number(p[2]), over = detail::Number(p[3]);
                if (a && *a >= 100 && *a <= 100000 && over && *over >= 0 && *over <= 0.5)
                {
                    o.aimAccel = *a;
                    o.aimOvershoot = *over;
                }
            }
            else if (p[0] == "move" && p.size() >= 5)
            {
                const bool run = p[2] == "run", walk = p[2] == "walk", stand = p[3] == "stand", crouch = p[3] == "crouch";
                if ((run || walk) && (stand || crouch) && (p[4] == "0" || p[4] == "1"))
                {
                    o.gait = walk ? Order::Gait::Walk : Order::Gait::Run;
                    o.crouch = crouch;
                    o.preaim = p[4] == "1";
                }
            }
            else if (p[0] == "peek" && p.size() >= 6 && detail::Point(p, 3, xyz))
            {
                const auto mode = p[2] == "jiggle" ? Order::Peek::Jiggle : p[2] == "wide" ? Order::Peek::Wide : p[2] == "crouch" ? Order::Peek::Crouch : Order::Peek::None;
                if (mode != Order::Peek::None)
                {
                    o.peek = mode;
                    o.peekAt = xyz;
                }
            }
            else if (p[0] == "avoid" && p.size() >= 7 && o.avoid.size() < 8 && detail::Point(p, 2, xyz))
            {
                const auto r = detail::Number(p[5]), cost = detail::Number(p[6]);
                if (r && *r >= 10 && *r <= 3000 && cost && *cost >= 0 && *cost <= 100000) o.avoid.push_back({xyz[0], xyz[1], xyz[2], *r, *cost});
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
        double speed = 0;   // horizontal speed (cm/s)
        bool crouch = false;
        double look = -1;   // how far it sees straight ahead (cm), -1: not traced
    };
    inline std::string Format(std::int64_t unixMs, const std::vector<Report>& reports)
    {
        std::string text = "AIMMOD_BOTSIGHT_1\t" + std::to_string(unixMs) + "\n";
        char line[160];
        for (const auto& r : reports)
        {
            std::snprintf(line, sizeof(line), "bot\t%llu\t%.1f\t%.1f\t%.1f\t%.1f\t%.1f\n", static_cast<unsigned long long>(r.peer), r.x, r.y, r.z, r.yaw, r.floor);
            text += line;
            std::snprintf(line, sizeof(line), "vel\t%llu\t%.1f\t%d\n", static_cast<unsigned long long>(r.peer), r.speed, r.crouch ? 1 : 0);
            text += line;
            if (r.look >= 0)
            {
                std::snprintf(line, sizeof(line), "look\t%llu\t%.0f\n", static_cast<unsigned long long>(r.peer), r.look);
                text += line;
            }
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

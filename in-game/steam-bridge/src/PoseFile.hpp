#pragma once
// AimModCore pose format 1 (in-game/native-mod/DESIGN.md, "Spectating"):
//
//   AIMMOD_POSE_1\t<sequence>
//   meta\t<%-escaped scenario>\t<%-escaped map name>\t<map scale>
//   pose\t<unix ms>\t<x>\t<y>\t<z>\t<pitch>\t<yaw>\t<roll>\t<fov>   (1-64 rows, increasing ms)
//   target\t<id>\t<x>\t<y>\t<z>\t<radius>\t<half height>          (optional)
//
// AimModCore writes self-pose.tsv (the local view) while self-pose.request
// is fresh; the bridge writes spectate-pose.tsv (the watched player's view).

#include <array>
#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace bridge::posefile
{
    constexpr std::size_t MaxRows = 64;

    struct Row
    {
        std::int64_t ms = 0;
        std::array<double, 7> v{}; // x y z pitch yaw roll fov
    };
    struct Target
    {
        std::string id;
        std::array<double, 5> v{}; // x y z radius halfHeight
    };
    struct File
    {
        std::int64_t sequence = 0;
        std::string scenario, map;
        double scale = 0;
        std::vector<Row> rows;
        std::vector<Target> targets;
    };

    std::string Escape(std::string_view text);                   // RFC 3986 unreserved kept, rest %XX
    std::optional<std::string> Unescape(std::string_view text);  // strict %XX
    std::optional<File> Parse(std::string_view text);            // strict; rejects anything off-format
    std::string Format(const File& file);                        // LF-terminated lines
} // namespace bridge::posefile

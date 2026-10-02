#pragma once
// Name tags over other players (DESIGN.md "World tags"): the service says who each avatar
// stream is (world-tags.tsv); AimModCore projects the avatars on screen every frame and
// pushes the tags to the notice layer. Teammates are tagged (through walls, like CS);
// enemies only while under the crosshair and in line of sight.
#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace aimmod::worldtags
{
    // world-tags.tsv:
    //   AIMMOD_TAGS_1\t<seq>
    //   tag\t<stream id>\t<friend|enemy>\t<team: T|CT|1|2|0>\t<alive 0/1>\t<name, percent-escaped>[\t<gear>]
    // gear (optional, percent-escaped, at most MaxGear bytes): what the tag shows under the name, as
    // key=value pairs joined by ';' (CS teammates: w weapon, hp, ar armour, hm helmet, kit, c4).
    // AimModCore passes it to the page untouched.
    struct Who
    {
        bool friendly{};
        std::string team; // T, CT, 1, 2 or 0 (no teams)
        bool alive{};
        std::string name;
        std::string gear;
    };
    using Roster = std::map<std::string, Who>; // by stream id
    constexpr std::size_t MaxBytes = 16384, MaxTags = 32, MaxGear = 160;
    std::optional<Roster> Parse(std::string_view text);

    // One tag on screen: x and y in 0..1 of the viewport, distance in metres.
    struct ScreenTag
    {
        std::string name, team;
        bool friendly{}, alive{}, aimed{};
        double x{}, y{}, metres{};
        std::string gear;
    };
    // The JS event payload: {"tags":[{"n":..,"t":..,"f":1,"a":1,"c":0,"x":..,"y":..,"d":..[,"g":..]}]}.
    std::string Json(const std::vector<ScreenTag>& tags);

    // Which tags show: teammates (alive or down), and an enemy only when aimed at and visible.
    bool Shown(const Who& who, bool aimedAt, bool visible);
} // namespace aimmod::worldtags

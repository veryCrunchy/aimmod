#pragma once
// Offline avatar spike (game-modes.md, phase 0): a recorded camera path that
// the avatar test (avatar_test=1) plays on a real avatar bot instead of
// circling the player. The native service exports it from a replay with
// --export-avatar-path <replay id>, as avatar-test-path.tsv:
//
//   AIMMOD_AVATAR_PATH_1
//   meta\t<%-escaped scenario>\t<%-escaped map>\t<map scale>
//   p\t<ms from start>\t<x>\t<y>\t<z>\t<pitch>\t<yaw>        (camera, 2..40000 rows, increasing ms)
//
// Rows are the recorded player's camera (eye). The avatar's capsule centre is
// that eye minus the local player's own eye height, measured live, so the bot
// stands on the floor the player stood on. The path loops.
#include "GhostMath.hpp"

#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace bridge::ghost
{
    struct PathRow
    {
        double t; // seconds from the path start
        double x, y, z, pitch, yaw;
    };

    struct AvatarPath
    {
        static constexpr std::size_t MaxRows = 40000;
        std::string scenario, map;
        double scale = 1;
        std::vector<PathRow> rows;

        // Strict parse; nullopt with a reason on any malformed line.
        static std::optional<AvatarPath> Parse(std::string_view text, std::string* error = nullptr);
        double Duration() const { return rows.empty() ? 0 : rows.back().t; }
        // The avatar transform at `seconds` since the test started (looping).
        // eyeAboveCentre: the local player's camera height above its capsule centre.
        RemoteTransform At(double seconds, double eyeAboveCentre) const;
    };
} // namespace bridge::ghost

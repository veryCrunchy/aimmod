#pragma once
// Replay playback transport (replay-frame.tsv, renderer protocol 6) as the
// native presenter reads it, and the pose it applies at an exact instant.
#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

namespace aimmod
{
    struct PlaybackFrame
    {
        std::uint64_t revision{};
        bool visible{};
        bool playing{};
        double time{}, duration{}, speed{1};
        std::optional<std::int64_t> clockUnixMs; // publication instant of `clockTime`
        double clockTime{};
        struct Sample
        {
            double t{};
            double camera[7]{}; // x y z pitch yaw roll fov
        };
        std::vector<Sample> motion;
        struct Target
        {
            std::uint32_t id{};
            double position[3]{};
            double velocity[3]{};
            bool moving{};
        };
        std::vector<Target> targets;
    };

    // Parses protocol 6 frames; nullopt for anything else or malformed input.
    std::optional<PlaybackFrame> ParsePlaybackFrame(std::string_view text);

    // Playback time at `nowUnixMs`, clamped to the motion window.
    double PlaybackTime(const PlaybackFrame& frame, std::int64_t nowUnixMs);
    // Camera row at playback time `t` (interpolated; yaw/roll wrap-aware).
    bool CameraAt(const PlaybackFrame& frame, double t, double out[7]);
} // namespace aimmod

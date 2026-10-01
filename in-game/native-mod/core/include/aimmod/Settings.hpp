#pragma once
#include <cstdint>
#include <ctime>
#include <optional>
#include <string>
#include <string_view>

namespace aimmod
{
    struct NativeSettings
    {
        bool replayRecordingEnabled{true};
        bool hubHistoryEnabled{true};
    };
    // native-settings.tsv as written by NativeSettings.cs. Returns nullopt for a
    // readable but malformed file (callers fail closed: recording disabled).
    std::optional<NativeSettings> ParseNativeSettings(std::string_view text);

    // clip-settings.tsv (optional): the clip hotkey and the clip window.
    //   AIMMOD_CLIPS_1 / key\tF8 / before\t8 / after\t2
    struct ClipSettings
    {
        std::string key = "F8";
        int virtualKey = 0x77;
        double before = 8.0, after = 2.0;
    };
    std::optional<ClipSettings> ParseClipSettings(std::string_view text);
    // F1-F12, Insert, Home, End, PageUp, PageDown, Pause, ScrollLock; 0 otherwise.
    int VirtualKeyFromName(std::string_view name);

    // core-active.tsv handshake read by the Lua mod.
    inline constexpr std::string_view CoreActiveHeader = "AIMMOD_CORE_1";
    std::string FormatCoreActive(std::string_view version, std::time_t now, std::string_view capabilities);

    // replay-frame.tsv header written by the service while replay playback is
    // shown ("AIMMOD_REPLAY_<n>\t<n>\t1").
    bool IsReplayPlaybackHeader(std::string_view firstLine);
} // namespace aimmod

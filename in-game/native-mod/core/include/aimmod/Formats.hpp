#pragma once
// Wire formats shared with the native service (C#) and the Lua UI mod.
// Every function here is pure; see DESIGN.md "Output contract".
#include <cstdint>
#include <ctime>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace aimmod
{
    // Values outside this range are treated as unavailable by every reader.
    inline bool IsUsableNumber(double value) { return value == value && value < 1e12 && value > -1e12; }

    // Journal field escaping (NativeRuns.Decode is the inverse).
    std::string EscapeField(std::string_view text);
    // JSON string literal including quotes. Control characters, '"' and '\'
    // are escaped; other bytes (UTF-8) pass through unchanged.
    void AppendJsonString(std::string& out, std::string_view text);
    // printf "%.<digits>g" with the C locale.
    void AppendNumber(std::string& out, double value, int digits);
    std::string FormatNumber(double value, int digits);
    // "YYYY-MM-DDTHH:MM:SSZ".
    std::string IsoUtc(std::time_t seconds);

    struct JournalRun
    {
        std::string id;
        std::string scenario;
        double score{};
        std::optional<double> accuracy;  // percent 0..100
        std::optional<double> duration;  // seconds
        std::optional<double> kills;
        std::optional<double> damage;
        std::time_t completedAt{};
    };
    // One completed.tsv line including the trailing '\n'.
    std::string FormatJournalLine(const JournalRun& run);

    struct LiveSnapshot
    {
        bool active{};
        bool paused{};
        bool transient{};
        std::string id;           // empty = omitted
        std::string scenario;     // empty = omitted
        std::string scoreStatus;  // empty = omitted; [a-z-]+ only
        std::string mode;         // empty = omitted (challenge); "freeplay" for AimMod match runs
        std::optional<double> score, seconds, shots, hits, kills, damage, remainingSeconds, lastTimeToKillSeconds;
    };
    // live-overlay.json body (no trailing newline), field order as Telemetry.lua.
    std::string FormatLiveOverlay(const LiveSnapshot& snapshot);

    struct ReplayHeader
    {
        std::string id;
        std::string scenario;
        std::time_t recordedAt{};
        std::string mapName;             // empty = omitted (requires mapScale)
        std::optional<double> mapScale;
        std::string startEvent;          // empty = omitted; [a-z][a-z-]* only
    };
    std::string FormatReplayHeader(const ReplayHeader& header);

    struct ReplayActor
    {
        std::uint32_t id{};
        double x{}, y{}, z{}, radius{}, halfHeight{};
        std::optional<double> healthPercent;  // 0..1
        const std::string* profile{};         // nullptr = no appearance record
        double pitch{}, yaw{}, roll{};        // used with profile
    };
    struct ReplayStats
    {
        std::optional<double> score, shots, hits, kills, damage, seconds;
        std::optional<std::uint32_t> hitTarget;
        std::optional<double> hitTime, hitDelta;
    };
    struct ReplayFrame
    {
        double t{};
        double camera[7]{};  // x y z pitch yaw roll fov
        std::vector<ReplayActor> actors;
        ReplayStats stats;
    };
    // Appends one frame line without the trailing newline. Returns false (and
    // leaves `out` unchanged) if any value is not a usable number.
    bool AppendReplayFrame(std::string& out, const ReplayFrame& frame);
    bool AppendReplayInput(std::string& out, double t, std::string_view action, double value);
    std::string FormatReplayEnd(std::string_view reason, std::uint32_t frames, std::uint32_t inputEvents, std::optional<double> score);
    // replay-status.json body including the trailing newline.
    std::string FormatReplayStatus(std::string_view state, std::uint32_t frames, std::uint32_t inputEvents, std::string_view reason);

    // Replay/journal ids: [A-Za-z0-9_-]{1,100}.
    bool IsValidAttemptId(std::string_view id);
    // Lower-case identifier used for startEvent/scoreStatus: [a-z][a-z-]{0,31}.
    bool IsSimpleToken(std::string_view token);
} // namespace aimmod

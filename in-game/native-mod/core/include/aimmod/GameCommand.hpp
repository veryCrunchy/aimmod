#pragma once
// Game commands the service (or the multiplayer lobby) asks AimModCore to
// perform through core-command.tsv; results come back in
// core-command-result.tsv. Format and rules: DESIGN.md "Game commands".
#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <variant>
#include <vector>

namespace aimmod
{
    struct GameCommand
    {
        enum class Action { LoadScenario, StartScenario, ResetOverrides, RefreshScenarios, CaptureThumbnail, EndRun, QuitRun, EnsureMap };
        enum class Mode { FreePlay, Challenge };
        std::uint64_t sequence{};
        Action action{};
        std::string scenario;
        Mode mode{Mode::FreePlay};
        std::optional<double> timeScale, targetSize, targetSpeed, mapScale;
        std::string weapon;
        // start-scenario: shared randomness for match players (freeplay, or
        // challenge only for generated "AimMod Match - " scenarios).
        std::optional<std::uint32_t> seed;
        // end-run (freeplay AimMod match scenarios only): reload the scenario
        // without playing (stop, default) or restart it in freeplay (reset).
        bool reset{};
        // capture-thumbnail: 1-4 camera views, PNG size, output file name.
        struct View
        {
            double x{}, y{}, z{}, pitch{}, yaw{}, fov{90};
        };
        std::vector<View> views;
        int width{}, height{};
        std::string out;
        bool HasOverrides() const { return timeScale || targetSize || targetSpeed || mapScale || !weapon.empty(); }
    };

    // What resetting a start-scenario's overrides must undo. Requested (not
    // only successfully applied) overrides count, so a partly applied weapon
    // change is still restored.
    struct OverrideRestore
    {
        bool timeDilation{}, adaptive{}, mapScale{}, weapon{};
        bool Any() const { return timeDilation || adaptive || mapScale || weapon; }
    };
    OverrideRestore RestoreFor(const GameCommand& command);

    struct CommandError
    {
        std::uint64_t sequence{};
        std::string code;
        std::string message;
    };

    inline constexpr double MinMultiplier = 0.1, MaxMultiplier = 10.0, MinTimeScale = 0.1, MaxTimeScale = 4.0;

    // Validates everything; a malformed or unsafe request is an error with a
    // code and a message for the player. Never partially accepted.
    std::variant<GameCommand, CommandError> ParseGameCommand(std::string_view text);

    // "AIMMOD_CORE_RESULT_1\t<seq>\t<state>\t<code>\t<message>\n"; state is
    // accepted | done | error. The message is %-escaped like the journal.
    std::string FormatCommandResult(std::uint64_t sequence, std::string_view state, std::string_view code, std::string_view message);

    const char* ActionName(GameCommand::Action action);
    // The seed for spawn event `index` (0 = scenario start) of a match.
    std::uint32_t SeedFor(std::uint32_t matchSeed, std::uint32_t index);
    inline constexpr std::string_view MatchScenarioPrefix = "AimMod Match - ";
    // Whether a match seed may drive the game's randomness: any freeplay run,
    // but a challenge only in AimMod's generated match scenarios. A seed set
    // for a freeplay run never carries into a ranked challenge.
    bool SeedAllowed(std::string_view scenario, bool inChallenge);
    // Scenarios AimMod generates (match arenas, map ports, bisect probes): the
    // only ones whose map AimModCore may rebuild itself (ensure-map), and only
    // outside a challenge, benchmark or the scenario editor.
    bool MapFixAllowed(std::string_view scenario);
    // KovaaK's own "same map" test (MetaGameState, 3.9.11): the current map
    // name equals the scenario's MapName ignoring case, at the same MapScale.
    bool SameMapLoaded(std::string_view currentName, double currentScale, std::string_view wantedName, double wantedScale);
    // A scenario name usable as "<Scenarios>\\<name>.sce": no path separators,
    // reserved characters, dot segments or trailing dots/spaces.
    bool IsScenarioFileName(std::string_view name);
    // Plain "<name>.png": letters, digits, space, - _ . ( ), up to 128 bytes.
    bool IsThumbnailFileName(std::string_view name);
    // "<stem>-<n>.png" for view n of several (the name itself for one view).
    std::string ThumbnailFileName(const std::string& out, std::size_t index, std::size_t count);
} // namespace aimmod

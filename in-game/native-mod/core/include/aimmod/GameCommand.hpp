#pragma once
// Game commands the service (or the multiplayer lobby) asks AimModCore to
// perform through core-command.tsv; results come back in
// core-command-result.tsv. Format and rules: DESIGN.md "Game commands".
#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <variant>

namespace aimmod
{
    struct GameCommand
    {
        enum class Action { LoadScenario, StartScenario, ResetOverrides, RefreshScenarios };
        enum class Mode { FreePlay, Challenge };
        std::uint64_t sequence{};
        Action action{};
        std::string scenario;
        Mode mode{Mode::FreePlay};
        std::optional<double> timeScale, targetSize, targetSpeed, mapScale;
        std::string weapon;
        bool HasOverrides() const { return timeScale || targetSize || targetSpeed || mapScale || !weapon.empty(); }
    };

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
    // A scenario name usable as "<Scenarios>\\<name>.sce": no path separators,
    // reserved characters, dot segments or trailing dots/spaces.
    bool IsScenarioFileName(std::string_view name);
} // namespace aimmod

#include <aimmod/Formats.hpp>
#include <aimmod/GameCommand.hpp>

#include <charconv>
#include <map>

namespace aimmod
{
    namespace
    {
        bool SafeName(std::string_view s)
        {
            if (s.empty() || s.size() > 256) return false;
            for (char c : s)
                if (static_cast<unsigned char>(c) < 0x20 || c == 0x7f) return false;
            return s.find_first_not_of(' ') != std::string_view::npos;
        }
        std::optional<double> Number(std::string_view s)
        {
            double v{};
            auto r = std::from_chars(s.data(), s.data() + s.size(), v);
            if (r.ec != std::errc() || r.ptr != s.data() + s.size() || !IsUsableNumber(v)) return std::nullopt;
            return v;
        }
    } // namespace

    const char* ActionName(GameCommand::Action action)
    {
        switch (action)
        {
        case GameCommand::Action::LoadScenario: return "load-scenario";
        case GameCommand::Action::StartScenario: return "start-scenario";
        default: return "reset-overrides";
        }
    }

    std::variant<GameCommand, CommandError> ParseGameCommand(std::string_view text)
    {
        CommandError error;
        auto fail = [&](const char* code, std::string message) -> std::variant<GameCommand, CommandError> {
            error.code = code;
            error.message = std::move(message);
            return error;
        };
        if (text.size() > 4096) return fail("invalid-command", "Command too large.");
        std::map<std::string, std::string, std::less<>> fields;
        bool first = true;
        while (!text.empty())
        {
            auto end = text.find('\n');
            std::string_view line = text.substr(0, end);
            text = end == std::string_view::npos ? std::string_view{} : text.substr(end + 1);
            if (!line.empty() && line.back() == '\r') line.remove_suffix(1);
            if (first)
            {
                first = false;
                if (line != "AIMMOD_CORE_COMMAND_1") return fail("invalid-command", "Unsupported command format.");
                continue;
            }
            if (line.empty()) continue;
            auto tab = line.find('\t');
            if (tab == std::string_view::npos) return fail("invalid-command", "Malformed command line.");
            std::string key(line.substr(0, tab));
            if (fields.contains(key)) return fail("invalid-command", "Duplicate field: " + key + ".");
            fields.emplace(std::move(key), std::string(line.substr(tab + 1)));
        }
        if (first) return fail("invalid-command", "Empty command.");
        auto get = [&](std::string_view key) -> const std::string* {
            auto it = fields.find(key);
            return it == fields.end() ? nullptr : &it->second;
        };
        GameCommand c;
        const std::string* seq = get("seq");
        if (!seq || seq->empty() || seq->size() > 19 || std::from_chars(seq->data(), seq->data() + seq->size(), c.sequence).ec != std::errc() || c.sequence == 0)
            return fail("invalid-command", "Missing or invalid sequence number.");
        error.sequence = c.sequence;
        for (const auto& [key, value] : fields)
            if (key != "seq" && key != "action" && key != "scenario" && key != "mode" && key != "timeScale" && key != "targetSize" && key != "targetSpeed" &&
                key != "mapScale" && key != "weapon")
                return fail("invalid-command", "Unknown field: " + key + ".");
        const std::string* action = get("action");
        if (!action) return fail("invalid-command", "Missing action.");
        if (*action == "load-scenario") c.action = GameCommand::Action::LoadScenario;
        else if (*action == "start-scenario") c.action = GameCommand::Action::StartScenario;
        else if (*action == "reset-overrides") c.action = GameCommand::Action::ResetOverrides;
        else return fail("invalid-command", "Unknown action.");
        if (c.action != GameCommand::Action::ResetOverrides)
        {
            const std::string* scenario = get("scenario");
            if (!scenario || !SafeName(*scenario)) return fail("invalid-scenario", "Missing or invalid scenario name.");
            c.scenario = *scenario;
        }
        if (const std::string* mode = get("mode"))
        {
            if (*mode == "freeplay") c.mode = GameCommand::Mode::FreePlay;
            else if (*mode == "challenge") c.mode = GameCommand::Mode::Challenge;
            else return fail("invalid-mode", "Mode must be freeplay or challenge.");
        }
        auto range = [&](const char* key, std::optional<double>& target, double lo, double hi) -> bool {
            const std::string* value = get(key);
            if (!value) return true;
            auto n = Number(*value);
            if (!n || *n < lo || *n > hi) return false;
            target = n;
            return true;
        };
        if (!range("timeScale", c.timeScale, MinTimeScale, MaxTimeScale)) return fail("invalid-override", "Time scale must be between 0.1 and 4.");
        if (!range("targetSize", c.targetSize, MinMultiplier, MaxMultiplier)) return fail("invalid-override", "Target size must be between 0.1 and 10.");
        if (!range("targetSpeed", c.targetSpeed, MinMultiplier, MaxMultiplier)) return fail("invalid-override", "Target speed must be between 0.1 and 10.");
        if (!range("mapScale", c.mapScale, MinMultiplier, MaxMultiplier)) return fail("invalid-override", "Map scale must be between 0.1 and 10.");
        if (const std::string* weapon = get("weapon"))
        {
            if (!SafeName(*weapon)) return fail("invalid-override", "Invalid weapon profile name.");
            c.weapon = *weapon;
        }
        if (c.action != GameCommand::Action::StartScenario && (c.HasOverrides() || get("mode")))
            return fail("invalid-command", "Mode and overrides apply to start-scenario only.");
        // Ranked scores stay untouched: modified runs are freeplay only.
        if (c.mode == GameCommand::Mode::Challenge && c.HasOverrides())
            return fail("overrides-freeplay-only", "Overrides are only allowed in freeplay; challenge runs stay unmodified.");
        return c;
    }

    std::string FormatCommandResult(std::uint64_t sequence, std::string_view state, std::string_view code, std::string_view message)
    {
        std::string out = "AIMMOD_CORE_RESULT_1\t" + std::to_string(sequence) + "\t" + std::string(state) + "\t" + std::string(code) + "\t";
        out += EscapeField(message);
        out += '\n';
        return out;
    }
} // namespace aimmod

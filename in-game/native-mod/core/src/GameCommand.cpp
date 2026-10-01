#include <aimmod/Formats.hpp>
#include <aimmod/GameCommand.hpp>

#include <charconv>
#include <cmath>
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
        case GameCommand::Action::RefreshScenarios: return "refresh-scenarios";
        case GameCommand::Action::CaptureThumbnail: return "capture-thumbnail";
        case GameCommand::Action::EndRun: return "end-run";
        case GameCommand::Action::QuitRun: return "quit-run";
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
                key != "mapScale" && key != "weapon" && key != "seed" && key != "width" && key != "height" && key != "out" && key != "view1" && key != "view2" &&
                key != "view3" && key != "view4" && key != "then")
                return fail("invalid-command", "Unknown field: " + key + ".");
        const std::string* action = get("action");
        if (!action) return fail("invalid-command", "Missing action.");
        if (*action == "load-scenario") c.action = GameCommand::Action::LoadScenario;
        else if (*action == "start-scenario") c.action = GameCommand::Action::StartScenario;
        else if (*action == "reset-overrides") c.action = GameCommand::Action::ResetOverrides;
        else if (*action == "refresh-scenarios") c.action = GameCommand::Action::RefreshScenarios;
        else if (*action == "capture-thumbnail") c.action = GameCommand::Action::CaptureThumbnail;
        else if (*action == "end-run") c.action = GameCommand::Action::EndRun;
        else if (*action == "quit-run") c.action = GameCommand::Action::QuitRun;
        else return fail("invalid-command", "Unknown action.");
        if (c.action == GameCommand::Action::LoadScenario || c.action == GameCommand::Action::StartScenario ||
            c.action == GameCommand::Action::CaptureThumbnail || c.action == GameCommand::Action::EndRun)
        {
            const std::string* scenario = get("scenario");
            if (!scenario || !SafeName(*scenario)) return fail("invalid-scenario", "Missing or invalid scenario name.");
            c.scenario = *scenario;
        }
        if (const std::string* then = get("then"))
        {
            if (c.action != GameCommand::Action::EndRun) return fail("invalid-command", "\"then\" applies to end-run only.");
            if (*then != "stop" && *then != "reset") return fail("invalid-command", "end-run then must be stop or reset.");
            c.reset = *then == "reset";
        }
        if (c.action == GameCommand::Action::QuitRun && get("scenario")) return fail("invalid-command", "quit-run takes no scenario.");
        if (c.action == GameCommand::Action::EndRun && !std::string_view(c.scenario).starts_with(MatchScenarioPrefix))
            return fail("not-a-match", "end-run applies to AimMod match scenarios only.");
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
        if (const std::string* seed = get("seed"))
        {
            std::uint64_t value{};
            auto r = std::from_chars(seed->data(), seed->data() + seed->size(), value);
            if (c.action != GameCommand::Action::StartScenario || seed->empty() || r.ec != std::errc() || r.ptr != seed->data() + seed->size() || value > 0xFFFFFFFFull)
                return fail("invalid-seed", "A seed is a whole number from 0 to 4294967295 on start-scenario.");
            // Never in ranked play: freeplay, or AimMod's own generated match scenarios.
            if (!SeedAllowed(c.scenario, c.mode == GameCommand::Mode::Challenge))
                return fail("seed-not-allowed", "Seeds apply to freeplay or AimMod match scenarios only.");
            c.seed = static_cast<std::uint32_t>(value);
        }
        if (c.action == GameCommand::Action::CaptureThumbnail)
        {
            auto integer = [&](const char* key, int lo, int hi, int& target) {
                const std::string* v = get(key);
                if (!v) return false;
                auto n = Number(*v);
                if (!n || *n != static_cast<int>(*n) || *n < lo || *n > hi) return false;
                target = static_cast<int>(*n);
                return true;
            };
            if (!integer("width", 64, 3840, c.width) || !integer("height", 64, 2160, c.height))
                return fail("invalid-thumbnail", "Width must be 64-3840 and height 64-2160 pixels.");
            const std::string* out = get("out");
            if (!out || !IsThumbnailFileName(*out)) return fail("invalid-thumbnail", "Output must be a plain .png file name.");
            c.out = *out;
            for (int i = 1; i <= 4; ++i)
            {
                const std::string* v = get("view" + std::to_string(i));
                if (!v) break;
                double n[6];
                std::string_view rest = *v;
                for (int k = 0; k < 6; ++k)
                {
                    auto comma = rest.find(',');
                    if ((k < 5) == (comma == std::string_view::npos)) return fail("invalid-thumbnail", "A view is x,y,z,pitch,yaw,fov.");
                    auto value = Number(rest.substr(0, comma));
                    if (!value) return fail("invalid-thumbnail", "A view is x,y,z,pitch,yaw,fov.");
                    n[k] = *value;
                    rest = comma == std::string_view::npos ? std::string_view{} : rest.substr(comma + 1);
                }
                if (std::fabs(n[0]) > 1e7 || std::fabs(n[1]) > 1e7 || std::fabs(n[2]) > 1e7 || n[3] < -90 || n[3] > 90 || std::fabs(n[4]) > 3600 ||
                    n[5] < 5 || n[5] > 170)
                    return fail("invalid-thumbnail", "View values out of range.");
                c.views.push_back({n[0], n[1], n[2], n[3], n[4], n[5]});
            }
            if (c.views.empty() || (get("view" + std::to_string(c.views.size() + 1)) != nullptr))
                return fail("invalid-thumbnail", "One to four views (view1..view4, in order).");
            for (int i = static_cast<int>(c.views.size()) + 1; i <= 4; ++i)
                if (get("view" + std::to_string(i))) return fail("invalid-thumbnail", "Views must be numbered in order.");
        }
        else if (get("width") || get("height") || get("out") || get("view1")) return fail("invalid-command", "Thumbnail fields apply to capture-thumbnail only.");
        if (c.action != GameCommand::Action::StartScenario && (c.HasOverrides() || get("mode")))
            return fail("invalid-command", "Mode and overrides apply to start-scenario only.");
        // Ranked scores stay untouched: modified runs are freeplay only.
        if (c.mode == GameCommand::Mode::Challenge && c.HasOverrides())
            return fail("overrides-freeplay-only", "Overrides are only allowed in freeplay; challenge runs stay unmodified.");
        return c;
    }

    OverrideRestore RestoreFor(const GameCommand& c)
    {
        OverrideRestore r;
        if (c.action != GameCommand::Action::StartScenario) return r;
        r.timeDilation = c.timeScale.has_value();
        r.adaptive = c.targetSize.has_value() || c.targetSpeed.has_value();
        r.mapScale = c.mapScale.has_value();
        r.weapon = !c.weapon.empty();
        return r;
    }

    bool IsScenarioFileName(std::string_view name)
    {
        if (!SafeName(name) || name.back() == '.' || name.back() == ' ' || name == "." || name == "..") return false;
        for (char c : name)
            if (c == '\\' || c == '/' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|') return false;
        return true;
    }

    bool SeedAllowed(std::string_view scenario, bool inChallenge) { return !inChallenge || scenario.starts_with(MatchScenarioPrefix); }

    std::uint32_t SeedFor(std::uint32_t matchSeed, std::uint32_t index)
    {
        // splitmix64 of (seed, index): independent, reproducible per event.
        std::uint64_t z = (static_cast<std::uint64_t>(matchSeed) << 32 | index) + 0x9E3779B97F4A7C15ull;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ull;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBull;
        return static_cast<std::uint32_t>(z ^ (z >> 31));
    }

    bool IsThumbnailFileName(std::string_view name)
    {
        if (name.size() < 5 || name.size() > 128 || !name.ends_with(".png") || name.front() == '.' || name.front() == ' ') return false;
        for (char c : name)
            if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == ' ' || c == '-' || c == '_' || c == '.' || c == '(' ||
                  c == ')'))
                return false;
        return name.find("..") == std::string_view::npos;
    }

    std::string ThumbnailFileName(const std::string& out, std::size_t index, std::size_t count)
    {
        if (count <= 1) return out;
        return out.substr(0, out.size() - 4) + "-" + std::to_string(index + 1) + ".png";
    }

    std::string FormatCommandResult(std::uint64_t sequence, std::string_view state, std::string_view code, std::string_view message)
    {
        std::string out = "AIMMOD_CORE_RESULT_1\t" + std::to_string(sequence) + "\t" + std::string(state) + "\t" + std::string(code) + "\t";
        out += EscapeField(message);
        out += '\n';
        return out;
    }
} // namespace aimmod

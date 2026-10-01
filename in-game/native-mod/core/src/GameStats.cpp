#include <aimmod/Formats.hpp>
#include <aimmod/GameStats.hpp>

#include <charconv>

namespace aimmod
{
    namespace
    {
        std::optional<double> Number(std::string_view text)
        {
            while (!text.empty() && (text.back() == '\r' || text.back() == ' ' || text.back() == ',')) text.remove_suffix(1);
            double value{};
            auto result = std::from_chars(text.data(), text.data() + text.size(), value);
            if (result.ec != std::errc() || result.ptr != text.data() + text.size() || !IsUsableNumber(value)) return std::nullopt;
            return value;
        }

        std::optional<double> TimeOfDay(std::string_view text)
        {
            // HH:MM:SS(.mmm)
            if (text.size() < 8 || text[2] != ':' || text[5] != ':') return std::nullopt;
            auto h = Number(text.substr(0, 2)), m = Number(text.substr(3, 2)), s = Number(text.substr(6));
            if (!h || !m || !s || *h > 23 || *m > 59 || *s >= 61) return std::nullopt;
            return *h * 3600 + *m * 60 + *s;
        }
    } // namespace

    std::optional<GameStats> ParseGameStats(std::string_view text)
    {
        GameStats out;
        bool haveScore = false;
        while (!text.empty())
        {
            auto end = text.find('\n');
            std::string_view line = text.substr(0, end);
            text = end == std::string_view::npos ? std::string_view{} : text.substr(end + 1);
            if (!line.empty() && line.back() == '\r') line.remove_suffix(1);
            const auto split = line.find(":,");
            if (split == std::string_view::npos) continue;
            const std::string_view key = line.substr(0, split);
            const std::string_view value = line.substr(split + 2);
            auto first = [](std::optional<double>& target, std::optional<double> v) {
                if (!target) target = v;
            };
            if (key == "Score" && !haveScore)
            {
                if (auto v = Number(value))
                {
                    out.score = *v;
                    haveScore = true;
                }
            }
            else if (key == "Scenario" && out.scenario.empty()) out.scenario = std::string(value);
            else if (key == "Hit Count") first(out.hits, Number(value));
            else if (key == "Miss Count") first(out.misses, Number(value));
            else if (key == "Kills") first(out.kills, Number(value));
            else if (key == "Damage Done") first(out.damage, Number(value));
            else if (key == "Fight Time") first(out.fightTime, Number(value));
            else if (key == "Challenge Start" && !out.challengeStartSeconds) out.challengeStartSeconds = TimeOfDay(value);
        }
        if (!haveScore || out.scenario.empty() || out.scenario.size() > 512) return std::nullopt;
        return out;
    }

    bool IsChallengeStatsFile(std::string_view name)
    {
        return name.size() > 30 && name.find(" - Challenge - ") != std::string_view::npos && name.ends_with(" Stats.csv");
    }

    bool IsChallengeStatsFile(std::wstring_view name)
    {
        return name.size() > 30 && name.find(L" - Challenge - ") != std::wstring_view::npos && name.ends_with(L" Stats.csv");
    }
} // namespace aimmod

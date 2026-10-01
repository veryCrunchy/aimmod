#pragma once
// The per-challenge statistics CSV KovaaK's writes when (and only when) a
// challenge completes: FPSAimTrainer\stats\<scenario> - Challenge - <date> Stats.csv.
// It is the authoritative end-screen record: completion evidence and the
// final score, hits, misses, kills and damage.
#include <optional>
#include <string>
#include <string_view>

namespace aimmod
{
    struct GameStats
    {
        std::string scenario;
        double score{};
        std::optional<double> hits, misses, kills, damage, fightTime;
        std::optional<double> challengeStartSeconds; // local time of day, "HH:MM:SS.mmm"
        std::optional<double> shots() const
        {
            if (!hits || !misses) return std::nullopt;
            return *hits + *misses;
        }
    };

    // Returns nullopt unless the text has a finite Score and a Scenario.
    std::optional<GameStats> ParseGameStats(std::string_view text);
    // "<scenario> - Challenge - " file name prefix check (Stats.csv suffix).
    bool IsChallengeStatsFile(std::string_view fileName);
    // The same check on a wide (directory listing) name: scenario names may
    // hold characters the ANSI code page cannot represent.
    bool IsChallengeStatsFile(std::wstring_view fileName);
} // namespace aimmod

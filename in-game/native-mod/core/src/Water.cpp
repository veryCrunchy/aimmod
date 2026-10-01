#include <aimmod/GameCommand.hpp>
#include <aimmod/Water.hpp>

#include <algorithm>
#include <cmath>

namespace aimmod::water
{
    namespace
    {
        bool Usable(double v) { return std::isfinite(v) && v > 0; }

        // Per movement style, in source units (Source: sv_friction, CGameMovement::WaterMove
        // and CheckWaterJump; Quake 3: pm_swimScale, pm_waterfriction x waterlevel 3,
        // PM_WaterMove and PM_CheckWaterJump).
        struct Rules
        {
            double swimScale;    // swim speed / run speed
            double friction;     // per second, under water
            double sink;         // units/s with no input
            double outOfWater;   // units/s up when climbing out at an edge
            double entryCarry;   // terminal velocity / swim speed
        };
        constexpr Rules CounterStrikeRules{0.8, 4.0, 48.0, 256.0, 1.5};
        constexpr Rules QuakeRules{0.5, 3.0, 60.0, 350.0, 2.0};
    } // namespace

    bool Allowed(std::string_view scenario, bool inChallenge, bool inBenchmark, bool inEditor, bool loading)
    {
        return !inChallenge && !inBenchmark && !inEditor && !loading && MapFixAllowed(scenario);
    }

    const char* StyleName(Style style) { return style == Style::Quake ? "quake" : "cs"; }

    Tuning TuningFor(Style style, double runSpeed, double mapScale)
    {
        const Rules& r = style == Style::Quake ? QuakeRules : CounterStrikeRules;
        const double scale = Usable(mapScale) ? std::clamp(mapScale, 0.1, 100.0) : 4.0;
        const double run = Usable(runSpeed) ? std::clamp(runSpeed, 50.0, 20000.0) : 250.0 * scale;
        Tuning t;
        t.maxSwimSpeed = run * r.swimScale;
        // The engine's fluid drag is 0.5 x FluidFriction per second.
        t.fluidFriction = 2.0 * r.friction;
        t.terminalVelocity = t.maxSwimSpeed * r.entryCarry;
        t.buoyancy = 1.0;
        t.sinkSpeed = std::min(r.sink * scale, t.maxSwimSpeed);
        t.outOfWaterZ = r.outOfWater * scale;
        t.jumpOutOfWaterPitch = -90.0;
        return t;
    }

    double VerticalInput(const Tuning& tuning, bool jumpHeld, double horizontalInput)
    {
        if (jumpHeld) return 1.0;
        if (std::isfinite(horizontalInput) && horizontalInput > 0.1) return 0.0;
        if (!Usable(tuning.maxSwimSpeed) || !std::isfinite(tuning.sinkSpeed) || tuning.sinkSpeed <= 0) return 0.0;
        // Input is a fraction of the swim speed: the engine caps the speed at
        // MaxSwimSpeed x input, so this sinks at sinkSpeed.
        return -std::clamp(tuning.sinkSpeed / tuning.maxSwimSpeed, 0.0, 1.0);
    }

    std::vector<std::string> JumpKeys(std::string_view ini)
    {
        std::vector<std::string> keys;
        std::size_t at = 0;
        while (at < ini.size())
        {
            std::size_t end = ini.find('\n', at);
            if (end == std::string_view::npos) end = ini.size();
            std::string_view line = ini.substr(at, end - at);
            at = end + 1;
            while (!line.empty() && (line.back() == '\r' || line.back() == ' ')) line.remove_suffix(1);
            while (!line.empty() && (line.front() == '+' || line.front() == ' ')) line.remove_prefix(1);
            if (!line.starts_with("ActionMappings=") || line.find("ActionName=\"Jump\"") == std::string_view::npos) continue;
            const auto k = line.find("Key=");
            if (k == std::string_view::npos) continue;
            std::size_t stop = line.find_first_of(",)", k + 4);
            std::string key(line.substr(k + 4, (stop == std::string_view::npos ? line.size() : stop) - (k + 4)));
            const bool plain = !key.empty() && key.size() <= 64 &&
                               std::all_of(key.begin(), key.end(), [](char c) { return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_'; });
            if (plain && std::find(keys.begin(), keys.end(), key) == keys.end() && keys.size() < 8) keys.push_back(key);
        }
        if (keys.empty()) keys.emplace_back("SpaceBar");
        return keys;
    }

    Tint UnderwaterTint() { return {0.55, 0.80, 0.95}; }
} // namespace aimmod::water

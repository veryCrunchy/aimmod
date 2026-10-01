#pragma once
// Game actions AimModCore performs on request (DESIGN.md "Game commands"):
// load a scenario (replay viewing), start a scenario in freeplay or challenge
// mode, optionally with freeplay-only overrides, and reset those overrides.
// Game thread only. Every request is validated, refused while a challenge
// runs, logged, and answered in core-command-result.tsv. Overrides can never
// reach a ranked run: they are freeplay-only, reset on any scenario change,
// and a challenge that starts while any override is still active is
// cancelled before it can be submitted.
#include "GameBindings.hpp"

#include <aimmod/GameCommand.hpp>

#include <Unreal/FWeakObjectPtr.hpp>

#include <filesystem>
#include <optional>
#include <string>

namespace aimmod
{
    class Output;
    namespace game
    {
        struct Bindings;
        struct Scene;
    } // namespace game

    class GameControl
    {
    public:
        GameControl(game::Bindings& bindings, game::Scene& scene, Output& output) : m_b(bindings), m_scene(scene), m_output(output) {}
        void Bind();
        // Capabilities: "load" (scenario load) and "start" (start with play type).
        bool canLoad() const { return m_canLoad; }
        bool canStart() const { return m_canStart; }
        bool overridesActive() const { return m_overrides.active; }
        // Called every engine frame on the game thread.
        void Tick(double now, const std::string& currentScenario, bool inChallenge, bool loading);

    private:
        void Begin(const GameCommand& command, double now, const std::string& currentScenario, bool inChallenge, bool loading);
        bool ScenarioKnown(game::UObject* manager, const std::string& name) const;
        bool StartScenario(const std::string& name, bool playOnLoad);
        bool SetPlayType(game::UObject* manager, GameCommand::Mode mode);
        bool ApplyOverrides(const GameCommand& command);
        void ResetOverrides(const char* why);
        bool Refresh(const char* why);
        void Proceed(const GameCommand& command, double now, game::UObject* manager);
        void Answer(std::uint64_t sequence, const char* state, const std::string& code, const std::string& message);

        game::Bindings& m_b;
        game::Scene& m_scene;
        Output& m_output;
        game::Getter m_start, m_activate, m_persistentPlayType, m_playCurrent, m_localHash, m_onlineHash, m_cancel;
        game::Getter m_timeDilation, m_mapScale, m_adaptiveOverride, m_adaptiveReset, m_weapon;
        game::Getter m_refreshLocal, m_reloadProfiles;
        std::filesystem::path m_scenarioFolder;
        game::UObject* m_startDefault{};
        bool m_canLoad{}, m_canStart{};

        struct Pending
        {
            GameCommand command;
            double deadline{};
            double loadedAt{-1};
            bool started{};
            RC::Unreal::FWeakObjectPtr action;
        };
        std::optional<Pending> m_pending;
        // A load/start waiting for the game to index a newly written scenario.
        struct Refreshing
        {
            GameCommand command;
            double deadline{};
            double nextCheck{};
            bool reloaded{};
        };
        std::optional<Refreshing> m_refreshing;
        struct Overrides
        {
            bool active{};
            bool timeDilation{}, adaptive{};
            std::optional<double> mapScaleBefore;
            std::string scenario;
        } m_overrides;
        std::string m_lastScenario;
    };
} // namespace aimmod

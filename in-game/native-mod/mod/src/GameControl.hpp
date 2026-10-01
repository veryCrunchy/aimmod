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
#include <vector>
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
        bool canCapture() const { return m_canCapture; }
        // "quit": leave the current run the way pause -> Quit does (abandoned, never submitted).
        bool canQuit() const { return m_cancel.ok(); }
        bool overridesActive() const { return m_overrides.active; }
        // Shared match randomness: the game draws from the CRT rand() state of
        // its game thread (it imports rand/srand from the UCRT, like this
        // module). Seeded when the seeded scenario starts or a challenge
        // attempt in it starts, and before each target death/kill (respawn).
        void OnAttemptStarted(const std::string& scenario);
        void OnSpawnEvent();
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
        // quit-run: the pause menu's Quit Challenge handler (Blueprint, bound
        // when first needed), CancelChallenge as fallback, and the freeplay
        // session reset.
        game::Getter m_quitHandler, m_resetFreeplay;
        struct Quitting
        {
            std::uint64_t sequence{};
            double deadline{}, fallbackAt{};
            bool fallback{};
            std::string path;
        };
        std::optional<Quitting> m_quitting;
        void BeginQuit(const GameCommand& command, double now, bool inChallenge);
        void TickQuit(double now, bool inChallenge);
        // Thumbnail capture (camera actor + HighResShot).
        game::Getter m_exec, m_spawnBegin, m_spawnFinish, m_setViewTarget, m_getViewTarget, m_destroy, m_hide, m_place, m_fov;
        game::Field m_cameraComponent, m_fullyLoaded, m_mapLoading;
        game::UObject* m_kismet{};
        game::UClass* m_cameraClass{};
        bool m_canCapture{};
        std::filesystem::path m_screenshots, m_thumbnails;
        struct Capture
        {
            GameCommand command;
            enum class Phase { Loading, Place, Shoot, Wait } phase{Phase::Loading};
            double deadline{}, loadedAt{-1}, until{}, nextCheck{};
            std::size_t view{};
            bool fallbackShot{}, triedFallback{};
            std::vector<std::wstring> before;
            std::wstring candidate;
            std::uintmax_t candidateSize{};
            RC::Unreal::FWeakObjectPtr camera, previousTarget, pawn;
            std::vector<std::string> files;
        };
        std::optional<Capture> m_capture;
        void BeginCapture(const GameCommand& command, double now, const std::string& current, game::UObject* manager);
        void TickCapture(double now, const std::string& current, bool inChallenge, bool loading);
        void EndCapture(const char* state, const std::string& code, const std::string& message);
        std::vector<std::wstring> Screenshots() const;
        std::filesystem::path m_scenarioFolder;
        game::UObject* m_startDefault{};
        bool m_canLoad{}, m_canStart{};

        struct Pending
        {
            GameCommand command;
            double deadline{};
            double loadedAt{-1};
            bool started{};
            bool sawLoading{}; // end-run: the reload began
            double issued{};
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
        struct Seeding
        {
            std::uint32_t seed{};
            std::string scenario;
            std::uint32_t events{};
            std::uint64_t lastEventTick{~0ull};
            bool active{};
        };
        std::optional<Seeding> m_seeding;
        std::uint64_t m_tick{};
        void Reseed(std::uint32_t index, const char* why);
    };
} // namespace aimmod

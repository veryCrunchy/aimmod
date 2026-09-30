#pragma once
// Game-thread observer: drives the lifecycle machine from polls (and optional
// broadcast observations), publishes live telemetry, writes the journal and
// runs the replay sampler. Read-only with respect to the game.
#include "Presenter.hpp"
#include "ReplaySampler.hpp"
#include "World.hpp"

#include <aimmod/Formats.hpp>
#include <aimmod/GameStats.hpp>
#include <aimmod/Lifecycle.hpp>

#include <atomic>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <utility>
#include <vector>

namespace aimmod
{
    class Output;

    class Observer
    {
    public:
        Observer(Output& output, std::string version);
        ~Observer();

        // on_unreal_init: resolve bindings, register callbacks.
        void Initialize();
        // Unregister everything; safe to call more than once.
        void Shutdown();
        std::string Capabilities() const;

    private:
        struct Watch
        {
            UFunction* function{};
            UObject* object{}; // nullptr: any receiver
            Signal signal{};
            bool counts{};     // counted only (no lifecycle signal)
            std::uint16_t counter{};
        };
        struct WatchSet
        {
            std::vector<Watch> items;
        };
        struct InputHook
        {
            Observer* self{};
            std::string action;
            bool axis{};
            int valueOffset{-1};
            game::Kind valueKind{game::Kind::Other};
            UFunction* function{};
            std::pair<int, int> ids{-1, -1};
        };

        void BindFunctions();
        void RegisterCallbacks();
        void OnTick();
        void OnProcessEvent(UObject* context, UFunction* function);
        void Rebind(double now);
        void RefreshWatch();
        void Poll(double now);
        void UpdateMeasurements(bool running, double elapsed, double remaining, const game::Getter::ValueElseResult& score);
        void Handle(const std::vector<LifecycleEvent>& events);
        void PublishLive(const PollSample& sample, bool running);
        void PublishScene(const PollSample& sample, UObject* manager);
        void LogCompatibility(const char* reason);
        bool OnGameThread() const;

        Output& m_output;
        std::string m_version;
        game::Bindings m_b;
        game::Scene m_scene;
        Lifecycle m_lifecycle;
        ReplaySampler m_sampler;
        Presenter m_presenter;

        // Callback registrations.
        std::vector<std::uint64_t> m_callbacks;
        std::vector<std::pair<UFunction*, std::pair<int, int>>> m_hooks;
        std::vector<std::unique_ptr<InputHook>> m_inputHooks;
        std::atomic<bool> m_initialized{false};
        std::atomic<bool> m_shutdown{false};

        // Threading.
        std::atomic<std::uint32_t> m_gameThread{0};
        std::uint32_t m_mainThread{0};
        std::atomic<bool> m_engineTick{false};
        double m_lastTick{};
        bool m_inTick{};

        // ProcessEvent watch list (published copy-on-write; old sets retired).
        std::atomic<const WatchSet*> m_watch{nullptr};
        std::vector<std::unique_ptr<WatchSet>> m_watchSets;
        std::vector<Signal> m_pendingSignals;
        std::vector<std::pair<std::string, std::uint64_t>> m_watchCounts;
        std::vector<std::uint64_t> m_watchSeen; // tick index of the last count
        std::uint64_t m_tickIndex{};
        double m_compatibilityDue{-1};
        std::size_t m_broadcastFunctions{}, m_broadcastFound{}, m_delegateHandlers{};

        // Schedules (seconds, steady clock).
        double m_nextRebind{}, m_nextPoll{}, m_nextSample{}, m_nextWatch{}, m_nextCompatibility{};
        std::uint32_t m_polls{};

        // Scenario name cache.
        std::uint64_t m_scenarioKey{};
        std::string m_scenarioName;
        std::string m_mapName;
        std::optional<double> m_mapScale;
        UObject* m_mapState{};
        std::uint64_t m_mapScenarioKey{};

        // Current attempt measurements (never computed scores).
        AttemptStats m_stats;
        std::string m_scoreStatus;
        std::optional<double> m_remaining, m_lastTimeToKill;
        bool m_running{}, m_paused{};
        std::atomic<std::uint64_t> m_killCredits{0};
        std::atomic<std::uint64_t> m_inputEvents{0};
        std::atomic<std::uint64_t> m_shotHits{0};
        std::uint64_t m_attemptKillBase{};
        std::uint32_t m_attempts{}, m_completed{}, m_journalled{};
        std::string m_sources; // per-attempt value sources, for the log
        std::int64_t m_attemptUnixMs{};
        std::optional<double> m_attemptLocalStart;
        bool m_statsWatch{};
        std::optional<GameStats> m_gameStats;
        bool m_replayProbed{};
    };
} // namespace aimmod

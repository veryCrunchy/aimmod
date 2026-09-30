#pragma once
// Challenge attempt state machine. Engine independent: the mod feeds it one
// PollSample per poll (and optional hook signals); it returns what happened.
// See DESIGN.md "Lifecycle state machine".
#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace aimmod
{
    struct PollSample
    {
        double now{};              // monotonic seconds
        bool available{};          // ScenarioManager and timers were readable
        bool running{};            // in challenge, scenario active, not loading, queue timer zero
        bool paused{};             // game paused (timer frozen)
        double elapsed{};          // GetChallengeTimeElapsed
        double remaining{-1};      // GetChallengeTimeRemaining (<0 = unknown)
        std::uint64_t scenarioKey{}; // identity of the current scenario object
        std::string_view scenario; // current scenario name
        std::optional<double> lastScore;         // StatsManager:GetLastScore
        std::optional<double> lastTimeRemaining; // StatsManager:GetLastChallengeTimeRemaining
        std::optional<double> indicatorScore;    // receiver Get_Score_ValueElse with HasValue
    };

    enum class Signal
    {
        Start,    // a start/queued broadcast was observed
        Complete, // a completion broadcast was observed
        Cancel,   // a cancel broadcast was observed
    };

    struct LifecycleEvent
    {
        enum class Kind { Started, Completed, Canceled };
        Kind kind{};
        std::string id;
        std::string scenario;
        std::string startEvent;          // Started: how the start was confirmed
        std::string reason;              // Canceled: restart|quit|scenario-changed|world-changed|canceled
        std::optional<double> score;     // Completed: final score, never computed
        std::string scoreSource;         // Completed: stats-last-score|indicator|hook
        double duration{};               // Completed/Canceled: challenge seconds observed
    };

    struct LifecycleConfig
    {
        double endGraceSeconds = 3.0;       // wait for the final score after the challenge stops
        double scoreSettleSeconds = 0.5;    // wait for GetLastScore after other completion evidence
        double timerExpiredSeconds = 0.5;   // remaining time that counts as "timer ran out"
        double rewindThreshold = 0.05;      // timer jitter tolerance
        double restartMaxElapsed = 1.0;     // a restart returns the timer to its start
        double unavailableCancelSeconds = 5.0;
    };

    class Lifecycle
    {
    public:
        explicit Lifecycle(std::string idPrefix, LifecycleConfig config = {});

        std::vector<LifecycleEvent> Poll(const PollSample& sample);
        std::vector<LifecycleEvent> OnSignal(Signal signal, double now, std::optional<double> score = std::nullopt);

        enum class State { Idle, Running, Ending };
        State state() const { return m_state; }
        bool active() const { return m_state != State::Idle; }
        const std::string& attemptId() const { return m_attempt.id; }
        const std::string& attemptScenario() const { return m_attempt.scenario; }
        double attemptElapsed() const { return m_attempt.lastElapsed; }

    private:
        struct Attempt
        {
            std::string id;
            std::string scenario;
            std::uint64_t scenarioKey{};
            std::optional<double> baselineScore;
            std::optional<double> baselineTimeRemaining;
            std::optional<double> indicatorScore;
            double lastElapsed{};
            double lastRemaining{-1};
            std::optional<double> rewindCandidate;
            double endDeadline{};
            double evidenceAt{-1};
            bool completionSignal{};
            std::optional<double> signalScore;
            double unavailableSince{-1};
        };

        void Start(const PollSample& sample, std::vector<LifecycleEvent>& out);
        void Cancel(std::string_view reason, std::vector<LifecycleEvent>& out);
        void Complete(const PollSample& sample, std::vector<LifecycleEvent>& out);
        bool Evidence(const PollSample& sample) const;
        double Duration() const;
        void ToIdle(bool armed, std::optional<double> previousElapsed);

        std::string m_prefix;
        LifecycleConfig m_config;
        std::uint64_t m_sequence{};
        State m_state{State::Idle};
        Attempt m_attempt;
        bool m_armed{};
        bool m_startSignal{};
        std::optional<double> m_idleElapsed;
        std::uint64_t m_idleScenarioKey{};
    };
} // namespace aimmod

#include <aimmod/Lifecycle.hpp>

#include <utility>

namespace aimmod
{
    Lifecycle::Lifecycle(std::string idPrefix, LifecycleConfig config) : m_prefix(std::move(idPrefix)), m_config(config) {}

    void Lifecycle::ToIdle(bool armed, std::optional<double> previousElapsed)
    {
        m_state = State::Idle;
        m_attempt = {};
        m_armed = armed;
        m_startSignal = false;
        m_idleElapsed = previousElapsed;
    }

    void Lifecycle::Start(const PollSample& s, std::vector<LifecycleEvent>& out)
    {
        m_attempt = {};
        m_attempt.id = m_prefix + "-" + std::to_string(++m_sequence);
        m_attempt.scenario = std::string(s.scenario);
        m_attempt.scenarioKey = s.scenarioKey;
        m_attempt.baselineScore = s.lastScore;
        m_attempt.baselineTimeRemaining = s.lastTimeRemaining;
        m_attempt.indicatorScore = s.indicatorScore;
        m_attempt.lastElapsed = s.elapsed;
        m_attempt.lastRemaining = s.remaining;
        m_state = State::Running;
        m_armed = false;
        LifecycleEvent e;
        e.kind = LifecycleEvent::Kind::Started;
        e.id = m_attempt.id;
        e.scenario = m_attempt.scenario;
        e.startEvent = m_startSignal ? "broadcast-start" : "native";
        m_startSignal = false;
        out.push_back(std::move(e));
    }

    double Lifecycle::Duration() const
    {
        const Attempt& a = m_attempt;
        if (a.lastRemaining >= 0 && a.lastRemaining <= m_config.timerExpiredSeconds) return a.lastElapsed + a.lastRemaining;
        return a.lastElapsed;
    }

    void Lifecycle::Cancel(std::string_view reason, std::vector<LifecycleEvent>& out)
    {
        LifecycleEvent e;
        e.kind = LifecycleEvent::Kind::Canceled;
        e.id = m_attempt.id;
        e.scenario = m_attempt.scenario;
        e.reason = std::string(reason);
        e.duration = m_attempt.lastElapsed;
        out.push_back(std::move(e));
    }

    bool Lifecycle::Evidence(const PollSample& s) const
    {
        const Attempt& a = m_attempt;
        if (a.completionSignal) return true;
        if (s.lastScore && (!a.baselineScore || *s.lastScore != *a.baselineScore)) return true;
        if (s.lastTimeRemaining && (!a.baselineTimeRemaining || *s.lastTimeRemaining != *a.baselineTimeRemaining)) return true;
        return false;
    }

    void Lifecycle::Complete(const PollSample& s, std::vector<LifecycleEvent>& out)
    {
        const Attempt& a = m_attempt;
        LifecycleEvent e;
        e.kind = LifecycleEvent::Kind::Completed;
        e.id = a.id;
        e.scenario = a.scenario;
        e.duration = Duration();
        // The end screen shows the stats manager's last score. Fall back to the
        // score the game broadcast or its last indicator value; never compute.
        if (s.lastScore)
        {
            e.score = s.lastScore;
            e.scoreSource = "stats-last-score";
        }
        else if (a.signalScore)
        {
            e.score = a.signalScore;
            e.scoreSource = "hook";
        }
        else if (a.indicatorScore)
        {
            e.score = a.indicatorScore;
            e.scoreSource = "indicator";
        }
        out.push_back(std::move(e));
    }

    std::vector<LifecycleEvent> Lifecycle::OnSignal(Signal signal, double now, std::optional<double> score)
    {
        std::vector<LifecycleEvent> out;
        switch (signal)
        {
        case Signal::Start:
            // A start broadcast arms the machine; the advancing timer confirms.
            if (m_state == State::Idle)
            {
                m_armed = true;
                m_startSignal = true;
            }
            break;
        case Signal::Complete:
            if (m_state == State::Idle) break;
            m_attempt.completionSignal = true;
            if (score) m_attempt.signalScore = score;
            if (m_state == State::Running)
            {
                m_state = State::Ending;
                m_attempt.endDeadline = now + m_config.endGraceSeconds;
            }
            break;
        case Signal::Cancel:
            if (m_state == State::Idle || m_attempt.completionSignal) break;
            Cancel("canceled", out);
            ToIdle(false, std::nullopt);
            break;
        }
        return out;
    }

    std::vector<LifecycleEvent> Lifecycle::Poll(const PollSample& s)
    {
        std::vector<LifecycleEvent> out;
        Attempt& a = m_attempt;
        if (m_state != State::Idle)
        {
            if (!s.available)
            {
                if (a.unavailableSince < 0) a.unavailableSince = s.now;
                if (s.now - a.unavailableSince >= m_config.unavailableCancelSeconds)
                {
                    Cancel("world-changed", out);
                    ToIdle(false, std::nullopt);
                }
                return out;
            }
            a.unavailableSince = -1;
            if (s.indicatorScore) a.indicatorScore = s.indicatorScore;
        }

        switch (m_state)
        {
        case State::Idle:
        {
            if (!s.available) return out;
            if (!s.running)
            {
                m_armed = true;
                m_idleElapsed.reset();
                return out;
            }
            if (s.scenarioKey != m_idleScenarioKey) m_idleElapsed.reset();
            m_idleScenarioKey = s.scenarioKey;
            const bool advancing = m_idleElapsed && s.elapsed > *m_idleElapsed;
            if (!m_idleElapsed || s.elapsed > *m_idleElapsed || s.elapsed < *m_idleElapsed - m_config.rewindThreshold) m_idleElapsed = s.elapsed;
            if (m_armed && advancing && !s.paused) Start(s, out);
            return out;
        }
        case State::Running:
        {
            if (s.scenarioKey != a.scenarioKey)
            {
                Cancel("scenario-changed", out);
                ToIdle(true, std::nullopt);
                return out;
            }
            if (!s.running)
            {
                if (s.paused) return out;
                m_state = State::Ending;
                a.endDeadline = s.now + m_config.endGraceSeconds;
                break; // evaluate the ending immediately
            }
            if (s.elapsed < a.lastElapsed - m_config.rewindThreshold && s.elapsed <= m_config.restartMaxElapsed)
            {
                // A restart returns the timer to its start. Require a second,
                // advancing observation so one stale read cannot split a run.
                if (a.rewindCandidate && s.elapsed > *a.rewindCandidate)
                {
                    Cancel("restart", out);
                    const bool signal = m_startSignal;
                    ToIdle(true, std::nullopt);
                    m_startSignal = signal;
                    Start(s, out);
                    return out;
                }
                a.rewindCandidate = s.elapsed;
                return out;
            }
            a.rewindCandidate.reset();
            if (s.elapsed >= a.lastElapsed)
            {
                a.lastElapsed = s.elapsed;
                a.lastRemaining = s.remaining;
            }
            return out;
        }
        case State::Ending:
            break;
        }

        // Ending.
        if (s.scenarioKey != a.scenarioKey && s.running)
        {
            if (Evidence(s)) Complete(s, out);
            else Cancel("scenario-changed", out);
            ToIdle(true, std::nullopt);
            return out;
        }
        const bool evidence = Evidence(s);
        if (evidence && a.evidenceAt < 0) a.evidenceAt = s.now;
        const bool scoreChanged = s.lastScore && (!a.baselineScore || *s.lastScore != *a.baselineScore);
        if (s.running)
        {
            if (s.elapsed >= a.lastElapsed - m_config.rewindThreshold && !evidence)
            {
                // Transient phase change: the same attempt continues.
                m_state = State::Running;
                a.lastElapsed = s.elapsed;
                a.lastRemaining = s.remaining;
                return out;
            }
            // The next attempt already started.
            if (evidence) Complete(s, out);
            else Cancel("restart", out);
            ToIdle(true, s.elapsed);
            return out;
        }
        if (evidence && (scoreChanged || s.now - a.evidenceAt >= m_config.scoreSettleSeconds))
        {
            Complete(s, out);
            ToIdle(false, std::nullopt);
            return out;
        }
        if (s.now >= a.endDeadline)
        {
            const bool timerExpired = a.lastRemaining >= 0 && a.lastRemaining <= m_config.timerExpiredSeconds;
            if (evidence || timerExpired) Complete(s, out);
            else Cancel("quit", out);
            ToIdle(false, std::nullopt);
        }
        return out;
    }
} // namespace aimmod

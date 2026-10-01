#pragma once
// Restart policy for the native service. Pure: callers pass time in seconds.

namespace aimmod
{
    class RestartBackoff
    {
    public:
        RestartBackoff(double initialDelay = 2.0, double maxDelay = 60.0, double stableUptime = 300.0)
            : m_initial(initialDelay), m_max(maxDelay), m_stable(stableUptime), m_delay(initialDelay)
        {
        }
        // The service was started (or found running) at `now`.
        void Started(double now) { m_startedAt = now; }
        // The service is gone at `now`: returns the time of the next start attempt.
        double Exited(double now)
        {
            if (m_startedAt >= 0 && now - m_startedAt >= m_stable) m_delay = m_initial;
            double next = now + m_delay;
            m_delay = m_delay * 2 > m_max ? m_max : m_delay * 2;
            m_startedAt = -1;
            return next;
        }
        double currentDelay() const { return m_delay; }

    private:
        double m_initial, m_max, m_stable, m_delay;
        double m_startedAt{-1};
    };
} // namespace aimmod

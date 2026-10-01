#include <aimmod/ReplayWriter.hpp>

#include <array>
#include <charconv>

namespace aimmod
{
    bool IsReplayAction(std::string_view action)
    {
        static constexpr std::array<std::string_view, 20> actions = {
            "AxisTurn",       "AxisLookUp",      "AxisMoveForward", "AxisMoveRight",  "FirePressed",    "FireReleased",   "AltFirePressed",
            "AltFireReleased", "JumpPressed",    "JumpReleased",    "CrouchPressed",  "CrouchReleased", "ReloadPressed",  "ReloadReleased",
            "ADSPressed",     "ADSReleased",     "AbilityPressed",  "AbilityReleased", "WeaponPressed", "WeaponReleased"};
        for (std::string_view a : actions)
            if (a == action) return true;
        return false;
    }

    // The value a reader parses back from the 7-digit text form.
    static double Rounded(double value)
    {
        char buffer[64];
        auto r = std::to_chars(buffer, buffer + sizeof(buffer), value, std::chars_format::general, 7);
        double parsed = value;
        std::from_chars(buffer, r.ptr, parsed);
        return parsed;
    }

    void ReplayRecording::Begin(const ReplayHeader& header)
    {
        m_id = header.id;
        m_pending = FormatReplayHeader(header);
        m_pending += '\n';
        m_bytes = m_pending.size();
        m_frames = m_inputs = 0;
        m_lastFrame = m_lastInput = -1;
        m_axis.clear();
        m_started = true;
        m_full = false;
    }

    bool ReplayRecording::Reserve(std::size_t lineBytes)
    {
        if (m_bytes + lineBytes + 1 > m_limits.maxBytes - m_limits.endReserve)
        {
            m_full = true;
            return false;
        }
        m_bytes += lineBytes + 1;
        return true;
    }

    ReplayRecording::Result ReplayRecording::AddFrame(const ReplayFrame& frame)
    {
        if (!m_started) return Result::Skipped;
        if (m_full || m_frames >= m_limits.maxFrames || frame.actors.size() > m_limits.maxActors)
        {
            m_full = true;
            return Result::Full;
        }
        const double t = Rounded(frame.t);
        if (!(t > m_lastFrame) || t > 86400) return Result::Skipped;
        if (frame.camera[6] <= 1 || frame.camera[6] >= 179) return Result::Skipped;
        m_scratch.clear();
        ReplayFrame copy = frame;
        copy.t = t;
        // The reader requires hitTime <= t after both are parsed back.
        if (copy.stats.hitTime)
        {
            double hit = Rounded(*copy.stats.hitTime);
            copy.stats.hitTime = hit > t ? t : hit;
        }
        if (!AppendReplayFrame(m_scratch, copy)) return Result::Skipped;
        if (!Reserve(m_scratch.size())) return Result::Full;
        m_pending += m_scratch;
        m_pending += '\n';
        m_lastFrame = t;
        ++m_frames;
        return Result::Ok;
    }

    ReplayRecording::Result ReplayRecording::AddInput(double t, std::string_view action, double value)
    {
        if (!m_started || !IsReplayAction(action)) return Result::Skipped;
        if (m_full || m_inputs >= m_limits.maxInputs)
        {
            m_full = true;
            return Result::Full;
        }
        t = Rounded(t);
        if (t < m_lastInput) t = m_lastInput;
        if (t < 0 || t > 86400 || !IsUsableNumber(value)) return Result::Skipped;
        if (action.starts_with("Axis"))
        {
            // Look values are additive deltas: repeated non-zero values must be
            // kept. Only redundant zeros are dropped.
            auto it = m_axis.find(action);
            const bool redundant = value == 0 && it != m_axis.end() && it->second == 0;
            if (it == m_axis.end()) m_axis.emplace(std::string(action), value);
            else it->second = value;
            if (redundant) return Result::Skipped;
        }
        m_scratch.clear();
        if (!AppendReplayInput(m_scratch, t, action, value)) return Result::Skipped;
        if (!Reserve(m_scratch.size())) return Result::Full;
        m_pending += m_scratch;
        m_pending += '\n';
        m_lastInput = t;
        ++m_inputs;
        return Result::Ok;
    }

    std::string ReplayRecording::Finish(std::string_view reason, std::optional<double> score)
    {
        std::string end = FormatReplayEnd(reason, m_frames, m_inputs, score);
        m_pending += end;
        m_pending += '\n';
        m_bytes += end.size() + 1;
        m_started = false;
        return TakePending();
    }

    std::string ReplayRecording::TakePending()
    {
        std::string out;
        out.swap(m_pending);
        m_pending.reserve(out.capacity());
        return out;
    }
} // namespace aimmod

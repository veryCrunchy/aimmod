#pragma once
// Bounded in-memory replay recording. The game thread appends records; the
// writer thread takes finished chunks and owns the file. Limits match
// ReplayCatalog.cs so every published file is readable.
#include <aimmod/Formats.hpp>

#include <cstdint>
#include <map>
#include <string>
#include <string_view>

namespace aimmod
{
    struct ReplayLimits
    {
        std::size_t maxBytes = 64u * 1024u * 1024u;
        std::uint32_t maxFrames = 36000;
        std::size_t maxActors = 128;
        std::uint32_t maxInputs = 500000;
        std::size_t endReserve = 4096; // room kept for the closing record
    };

    // Input action names accepted by the replay reader.
    bool IsReplayAction(std::string_view action);

    class ReplayRecording
    {
    public:
        enum class Result { Ok, Skipped, Full };

        explicit ReplayRecording(ReplayLimits limits = {}) : m_limits(limits) {}

        void Begin(const ReplayHeader& header);
        // Skipped: invalid values or a timestamp that does not advance.
        Result AddFrame(const ReplayFrame& frame);
        Result AddInput(double t, std::string_view action, double value);
        // Closing record; the recording is finished afterwards.
        std::string Finish(std::string_view reason, std::optional<double> score);
        // Buffered records since the last call (newline terminated).
        std::string TakePending();

        bool started() const { return m_started; }
        bool full() const { return m_full; }
        std::uint32_t frames() const { return m_frames; }
        std::uint32_t inputs() const { return m_inputs; }
        std::size_t bytes() const { return m_bytes; }
        std::size_t pendingBytes() const { return m_pending.size(); }
        const std::string& id() const { return m_id; }

    private:
        bool Reserve(std::size_t lineBytes);

        ReplayLimits m_limits;
        std::string m_id;
        std::string m_pending;
        std::string m_scratch;
        std::size_t m_bytes{};
        std::uint32_t m_frames{};
        std::uint32_t m_inputs{};
        double m_lastFrame{-1};
        double m_lastInput{-1};
        std::map<std::string, double, std::less<>> m_axis;
        bool m_started{};
        bool m_full{};
    };
} // namespace aimmod

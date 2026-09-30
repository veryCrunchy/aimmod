#pragma once
// 60 Hz replay capture on the game thread into a bounded ReplayRecording.
// Reads only: camera, characters (location, capsule, health, profile,
// rotation), the attempt's measured stats and the game's input recording
// entry points. Hands finished chunks to the writer thread.
#include "World.hpp"

#include <aimmod/ReplayWriter.hpp>

#include <cstdint>
#include <string>
#include <unordered_map>
#include <vector>

namespace aimmod
{
    class Output;

    struct AttemptStats
    {
        std::optional<double> score, shots, hits, kills, damage, seconds;
    };

    class ReplaySampler
    {
    public:
        ReplaySampler(game::Bindings& bindings, game::Scene& scene, Output& output) : m_b(bindings), m_scene(scene), m_output(output) {}

        // Opens a recording for the attempt; false if capture is unavailable.
        bool Begin(const std::string& id, const std::string& scenario, const std::string& startEvent);
        // One frame. Returns false when the recording had to stop (reason set).
        bool Sample(const AttemptStats& stats, double now);
        void OnInput(UObject* component, const char* action, bool axis, double value);
        // Caches the game clock for this frame's input events.
        void BeginFrame();
        void OnShotHit(UObject* shooter, UObject* target);
        // Closes the recording. Published only for "completed" with >1 frame.
        void Finish(const std::string& reason, std::optional<double> score);

        bool recording() const { return m_recording.started(); }
        const std::string& stopReason() const { return m_stopReason; }
        std::uint32_t frames() const { return m_recording.frames(); }
        std::uint32_t inputs() const { return m_recording.inputs(); }
        // Read-only probe of the capture path (logged once per world).
        bool Probe(std::string& detail);

    private:
        double GameTime(UObject* player) const;
        std::uint32_t ActorId(UObject* actor);
        void PublishStatus(const char* state, const std::string& reason);

        game::Bindings& m_b;
        game::Scene& m_scene;
        Output& m_output;
        ReplayRecording m_recording;
        std::string m_stopReason;
        double m_start{};
        double m_nextFlush{};
        double m_frameTime{-1};
        RC::Unreal::FWeakObjectPtr m_player, m_character;
        UObject* m_recorder{};
        std::unordered_map<std::uint64_t, std::uint32_t> m_ids;
        std::unordered_map<std::uint32_t, std::string> m_profiles;
        std::uint32_t m_nextId{};
        std::optional<double> m_observedHits;
        std::optional<double> m_hitTime, m_hitDelta;
        std::optional<std::uint32_t> m_hitTarget;
        double m_hitTargetTime{-1};
        std::vector<UObject*> m_actors;
        ReplayFrame m_frame;
    };
} // namespace aimmod

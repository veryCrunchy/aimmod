#pragma once
// Replay capture on the game thread (format 2, see DESIGN.md). Every engine
// frame while recording: the frame's game time and the look/button inputs the
// game consumed in it. At 60 Hz: camera, targets and measured stats (the
// encoder later keeps only what cannot be derived or interpolated). Reads
// only; the finished capture is encoded and written by the writer thread.
#include "World.hpp"

#include <aimmod/ReplayV2.hpp>

#include <cstdint>
#include <memory>
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
        // Once per engine frame while recording. `sample` requests a 60 Hz
        // state sample. Returns false when the recording had to stop.
        bool Tick(const AttemptStats& stats, bool sample);
        void OnInput(UObject* component, const char* action, bool axis, double value);
        void OnShotHit(UObject* shooter, UObject* target);
        // Closes the recording. Published only for "completed" with >1 sample.
        void Finish(const std::string& reason, std::optional<double> score);

        bool recording() const { return m_capture != nullptr; }
        const std::string& stopReason() const { return m_stopReason; }
        std::uint32_t frames() const { return m_samples; }
        std::uint32_t inputs() const { return m_inputs; }
        bool Probe(std::string& detail);

    private:
        double GameTime(UObject* player) const;
        std::uint32_t ActorId(UObject* actor);
        bool SampleState(std::uint32_t frame, const AttemptStats& stats);
        void PublishStatus(const char* state, const std::string& reason);

        game::Bindings& m_b;
        game::Scene& m_scene;
        Output& m_output;
        std::unique_ptr<replay2::Capture> m_capture;
        std::vector<replay2::InputEvent> m_pendingInputs;
        std::vector<std::uint32_t> m_pendingHits;
        std::string m_stopReason;
        double m_start{};
        double m_lastTime{-1};
        std::uint32_t m_samples{}, m_inputs{};
        double m_nextStatus{};
        RC::Unreal::FWeakObjectPtr m_player, m_character;
        UObject* m_recorder{};
        std::unordered_map<std::uint64_t, std::uint32_t> m_ids;
        std::uint32_t m_nextId{};
        float m_moveAxes[2]{};
        std::vector<UObject*> m_actors;
    };
} // namespace aimmod

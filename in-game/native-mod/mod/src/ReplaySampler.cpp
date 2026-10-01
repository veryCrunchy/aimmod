#include "ReplaySampler.hpp"

#include "Output.hpp"

#include <aimmod/Formats.hpp>

#include <Unreal/UObject.hpp>

#include <ctime>

namespace aimmod
{
    using namespace game;

    namespace
    {
        constexpr std::size_t MaxActors = 128;
        // Memory bounds (about 10 minutes at 400 fps with two look axes).
        constexpr std::size_t MaxEngineFrames = 400 * 60 * 15;
        constexpr std::size_t MaxInputs = 4'000'000;
        constexpr std::size_t MaxSamples = 36000;
        constexpr std::size_t MaxActorSamples = 2'000'000;
        constexpr std::size_t MaxPendingInputs = 256;
    } // namespace

    double ReplaySampler::GameTime(UObject* player) const
    {
        auto t = m_b.timeSeconds.Number(m_b.statics, player);
        return t && IsUsableNumber(*t) ? *t : -1;
    }

    void ReplaySampler::PublishStatus(const char* state, const std::string& reason)
    {
        m_output.PublishReplayStatus(FormatReplayStatus(state, m_samples, m_inputs, reason));
    }

    std::uint32_t ReplaySampler::ActorId(UObject* actor)
    {
        // Pointer plus object index: a recycled address is a new actor.
        const std::uint64_t key = reinterpret_cast<std::uint64_t>(actor) ^ (static_cast<std::uint64_t>(actor->GetInternalIndex()) << 47);
        auto it = m_ids.find(key);
        if (it != m_ids.end()) return it->second;
        const std::uint32_t id = ++m_nextId;
        m_ids.emplace(key, id);
        return id;
    }

    bool ReplaySampler::Begin(const std::string& id, const std::string& scenario, const std::string& startEvent)
    {
        m_stopReason.clear();
        if (!m_b.replayReady() || !IsValidAttemptId(id))
        {
            m_stopReason = "capture-unavailable";
            return false;
        }
        UObject* player = m_scene.Player();
        UObject* character = player ? m_b.myCharacter.Object(player) : nullptr;
        UObject* gameState = m_scene.GameState();
        if (!player || !character || !m_b.cameraManager.Object(player))
        {
            m_stopReason = "capture-gate-unavailable";
            return false;
        }
        const double start = GameTime(player);
        if (start < 0)
        {
            m_stopReason = "capture-clock-unavailable";
            return false;
        }
        auto capture = std::make_unique<replay2::Capture>();
        ReplayHeader& header = capture->header;
        header.id = id;
        header.scenario = scenario;
        header.recordedAt = std::time(nullptr);
        header.startEvent = startEvent;
        if (gameState)
        {
            std::string map;
            auto scale = m_b.mapScale.Number(gameState);
            // Never invent a map identity: both values or neither.
            if (m_b.mapName.String(gameState, map) && scale && *scale > 0 && map.find_first_not_of(" \t") != std::string::npos)
            {
                header.mapName = map;
                header.mapScale = scale;
            }
        }
        capture->frameTimes.reserve(60 * 400);
        capture->inputs.reserve(60 * 1000);
        m_capture = std::move(capture);
        m_player = player;
        m_character = character;
        m_recorder = Describe(character).playbackComponent.Object(character);
        m_start = start;
        m_lastTime = -1;
        m_samples = m_inputs = 0;
        m_nextStatus = 0;
        m_ids.clear();
        m_nextId = 0;
        m_pendingInputs.clear();
        m_pendingHits.clear();
        m_moveAxes[0] = m_moveAxes[1] = 0;
        PublishStatus("recording", "");
        return true;
    }

    bool ReplaySampler::Tick(const AttemptStats& stats, bool sample)
    {
        if (!m_capture) return false;
        UObject* player = m_player.Get();
        if (!player)
        {
            Finish("world-changed", std::nullopt);
            return false;
        }
        const double gameTime = GameTime(player);
        if (gameTime < 0) return true;
        const double t = gameTime - m_start;
        if (t < 0)
        {
            Finish("clock-reset", std::nullopt);
            return false;
        }
        replay2::Capture& c = *m_capture;
        if (t <= m_lastTime)
        {
            // Game time is frozen (pause): inputs of this frame changed nothing.
            m_pendingInputs.clear();
            m_pendingHits.clear();
            return true;
        }
        if (c.frameTimes.size() >= MaxEngineFrames || c.inputs.size() + m_pendingInputs.size() > MaxInputs || m_samples >= MaxSamples ||
            c.actors.size() >= MaxActorSamples)
        {
            Finish("size-limit", std::nullopt);
            return false;
        }
        const auto frame = static_cast<std::uint32_t>(c.frameTimes.size());
        c.frameTimes.push_back(t);
        m_lastTime = t;
        for (replay2::InputEvent& e : m_pendingInputs)
        {
            e.frame = frame;
            c.inputs.push_back(e);
        }
        m_inputs += static_cast<std::uint32_t>(m_pendingInputs.size());
        m_pendingInputs.clear();
        for (std::uint32_t target : m_pendingHits) c.hits.push_back({frame, target});
        m_pendingHits.clear();
        if (sample && SampleState(frame, stats)) ++m_samples;
        if (t >= m_nextStatus)
        {
            m_nextStatus = t + 1.0;
            PublishStatus("recording", "");
        }
        return true;
    }

    bool ReplaySampler::SampleState(std::uint32_t frame, const AttemptStats& stats)
    {
        replay2::Capture& c = *m_capture;
        UObject* player = m_player.Get();
        UObject* character = m_character.Get();
        UObject* camera = player ? m_b.cameraManager.Object(player) : nullptr;
        if (!character || !camera) return false;
        double location[3], rotation[3];
        auto fov = m_b.cameraFov.Number(camera);
        if (!m_b.cameraLocation.Vector(camera, location) || !m_b.cameraRotation.Vector(camera, rotation) || !fov || *fov <= 1 || *fov >= 179)
            return false;
        c.camera.push_back({frame, location[0], location[1], location[2], rotation[0], rotation[1], rotation[2], *fov});

        if (UObject* gameState = m_scene.GameState(); gameState && m_b.characters.Objects(gameState, m_actors, MaxActors + 1))
        {
            std::size_t count = 0;
            for (UObject* actor : m_actors)
            {
                if (actor == character || count >= MaxActors) continue;
                if (auto hiddenFlag = m_b.hidden.Bool(actor); hiddenFlag && *hiddenFlag) continue;
                UObject* capsule = m_b.capsule.Object(actor);
                if (!capsule) continue;
                auto radius = m_b.capsuleRadius.Number(capsule);
                auto half = m_b.capsuleHalfHeight.Number(capsule);
                double p[3];
                // The reader rejects a whole replay for one invalid capsule.
                if (!radius || !half || *radius <= 0 || *half < *radius || !m_b.actorLocation.Vector(actor, p)) continue;
                replay2::ActorSample a;
                a.frame = frame;
                a.id = ActorId(actor);
                a.x = p[0];
                a.y = p[1];
                a.z = p[2];
                a.radius = *radius;
                a.halfHeight = *half;
                const ClassInfo& info = Describe(actor);
                if (info.healthPercent.ok()) a.health = info.healthPercent.Number(actor);
                auto profile = c.profiles.find(a.id);
                if (profile == c.profiles.end())
                {
                    std::string name;
                    if (!info.profileName.String(actor, name) || name.empty() || name.size() > 256) name.clear();
                    for (char ch : name)
                        if (static_cast<unsigned char>(ch) < 0x20) name.clear();
                    profile = c.profiles.emplace(a.id, std::move(name)).first;
                }
                if (!profile->second.empty())
                {
                    double r[3];
                    if (m_b.actorRotation.Vector(actor, r))
                    {
                        a.hasRotation = true;
                        a.pitch = r[0];
                        a.yaw = r[1];
                        a.roll = r[2];
                    }
                }
                c.actors.push_back(a);
                ++count;
            }
        }
        c.stats.push_back({frame, stats.score, stats.shots, stats.hits, stats.kills, stats.damage, stats.seconds});
        return true;
    }

    void ReplaySampler::OnInput(UObject* component, const char* action, bool axis, double value)
    {
        if (!m_capture || m_pendingInputs.size() >= MaxPendingInputs) return;
        if (m_recorder && component != m_recorder) return; // another recorder on the same character
        const int index = replay2::ActionIndex(action);
        if (index < 0 || !IsUsableNumber(value)) return;
        // Look axes are per-frame deltas: zeros carry nothing. Movement axes
        // are held values: only changes (press/release) are kept.
        if (index == replay2::AxisTurn || index == replay2::AxisLookUp)
        {
            if (value == 0) return;
        }
        else if (axis)
        {
            float& last = m_moveAxes[index == 2 ? 0 : 1];
            if (static_cast<float>(value) == last) return;
            last = static_cast<float>(value);
        }
        m_pendingInputs.push_back({0, static_cast<std::uint8_t>(index), static_cast<float>(axis ? value : 1.0)});
    }

    bool ReplaySampler::Mark()
    {
        if (!m_capture || m_capture->frameTimes.empty() || m_capture->marks.size() >= 32) return false;
        m_capture->marks.push_back(static_cast<std::uint32_t>(m_capture->frameTimes.size() - 1));
        return true;
    }

    void ReplaySampler::OnShotHit(UObject* shooter, UObject* target)
    {
        if (!m_capture || !shooter || !target || shooter != m_character.Get()) return;
        const std::uint64_t key = reinterpret_cast<std::uint64_t>(target) ^ (static_cast<std::uint64_t>(target->GetInternalIndex()) << 47);
        auto it = m_ids.find(key);
        if (it != m_ids.end() && m_pendingHits.size() < 16) m_pendingHits.push_back(it->second);
    }

    void ReplaySampler::Finish(const std::string& reason, std::optional<double> score)
    {
        if (!m_capture) return;
        std::unique_ptr<replay2::Capture> capture = std::move(m_capture);
        m_stopReason = reason;
        m_player.Reset();
        m_character.Reset();
        m_recorder = nullptr;
        m_pendingInputs.clear();
        m_pendingHits.clear();
        // Only completed attempts are published; interrupted captures are dropped.
        if (reason == "completed" && m_samples > 1)
        {
            capture->reason = reason;
            capture->score = score;
            m_output.ReplayWrite(std::move(capture));
        }
        PublishStatus("ready", reason);
    }

    bool ReplaySampler::Probe(std::string& detail)
    {
        if (!m_b.replayReady())
        {
            detail = "bindings unavailable";
            return false;
        }
        UObject* player = m_scene.Player();
        UObject* character = player ? m_b.myCharacter.Object(player) : nullptr;
        UObject* camera = player ? m_b.cameraManager.Object(player) : nullptr;
        UObject* gameState = m_scene.GameState();
        if (!player || !character || !camera)
        {
            detail = "player/camera unavailable";
            return false;
        }
        double location[3];
        if (!m_b.cameraLocation.Vector(camera, location) || !m_b.cameraFov.Number(camera))
        {
            detail = "camera read failed";
            return false;
        }
        std::size_t targets = 0;
        if (gameState && m_b.characters.Objects(gameState, m_actors, MaxActors + 1))
            for (UObject* actor : m_actors) targets += actor != character;
        detail = "targets=" + std::to_string(targets) + (gameState ? "" : " (no game state)");
        return true;
    }
} // namespace aimmod

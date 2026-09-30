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
        constexpr double FlushSeconds = 1.0;
    } // namespace

    double ReplaySampler::GameTime(UObject* player) const
    {
        auto t = m_b.timeSeconds.Number(m_b.statics, player);
        return t && IsUsableNumber(*t) ? *t : -1;
    }

    void ReplaySampler::PublishStatus(const char* state, const std::string& reason)
    {
        m_output.PublishReplayStatus(FormatReplayStatus(state, m_recording.frames(), m_recording.inputs(), reason));
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
        ReplayHeader header;
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
        m_player = player;
        m_character = character;
        const ClassInfo& c = Describe(character);
        m_recorder = c.playbackComponent.Object(character);
        m_start = start;
        m_frameTime = start;
        m_nextFlush = 0;
        m_ids.clear();
        m_profiles.clear();
        m_nextId = 0;
        m_observedHits.reset();
        m_hitTime.reset();
        m_hitDelta.reset();
        m_hitTarget.reset();
        m_hitTargetTime = -1;
        m_recording.Begin(header);
        m_output.ReplayOpen(id);
        m_output.ReplayAppend(id, m_recording.TakePending());
        PublishStatus("recording", "");
        return true;
    }

    bool ReplaySampler::Sample(const AttemptStats& stats, double now)
    {
        if (!m_recording.started()) return false;
        UObject* player = m_player.Get();
        UObject* character = m_character.Get();
        if (!player)
        {
            Finish("world-changed", std::nullopt);
            return false;
        }
        // A transiently missing character skips the frame; not a boundary.
        if (!character) return true;
        UObject* camera = m_b.cameraManager.Object(player);
        if (!camera) return true;
        const double gameTime = GameTime(player);
        if (gameTime < 0) return true;
        const double t = gameTime - m_start;
        if (t < 0)
        {
            Finish("clock-reset", std::nullopt);
            return false;
        }

        ReplayFrame& f = m_frame;
        f.t = t;
        f.actors.clear();
        f.stats = {};
        double location[3], rotation[3];
        auto fov = m_b.cameraFov.Number(camera);
        if (!m_b.cameraLocation.Vector(camera, location) || !m_b.cameraRotation.Vector(camera, rotation) || !fov) return true;
        f.camera[0] = location[0];
        f.camera[1] = location[1];
        f.camera[2] = location[2];
        f.camera[3] = rotation[0];
        f.camera[4] = rotation[1];
        f.camera[5] = rotation[2];
        f.camera[6] = *fov;

        if (UObject* gameState = m_scene.GameState(); gameState && m_b.characters.Objects(gameState, m_actors, MaxActors + 1))
        {
            for (UObject* actor : m_actors)
            {
                if (actor == character || f.actors.size() >= MaxActors) continue;
                if (auto hiddenFlag = m_b.hidden.Bool(actor); hiddenFlag && *hiddenFlag) continue;
                UObject* capsule = m_b.capsule.Object(actor);
                if (!capsule) continue;
                auto radius = m_b.capsuleRadius.Number(capsule);
                auto half = m_b.capsuleHalfHeight.Number(capsule);
                double p[3];
                // The reader rejects a whole replay for one invalid capsule.
                if (!radius || !half || *radius <= 0 || *half < *radius || !m_b.actorLocation.Vector(actor, p)) continue;
                ReplayActor a;
                a.id = ActorId(actor);
                a.x = p[0];
                a.y = p[1];
                a.z = p[2];
                a.radius = *radius;
                a.halfHeight = *half;
                const ClassInfo& info = Describe(actor);
                if (info.healthPercent.ok()) a.healthPercent = info.healthPercent.Number(actor);
                auto profile = m_profiles.find(a.id);
                if (profile == m_profiles.end())
                {
                    std::string name;
                    if (!info.profileName.String(actor, name) || name.empty() || name.size() > 256) name.clear();
                    for (char ch : name)
                        if (static_cast<unsigned char>(ch) < 0x20) name.clear();
                    profile = m_profiles.emplace(a.id, std::move(name)).first;
                }
                if (!profile->second.empty())
                {
                    double r[3];
                    if (m_b.actorRotation.Vector(actor, r))
                    {
                        a.profile = &profile->second;
                        a.pitch = r[0];
                        a.yaw = r[1];
                        a.roll = r[2];
                    }
                }
                f.actors.push_back(a);
            }
        }

        // Stats measured by the observer; hit markers from the weapon counter.
        f.stats.score = stats.score;
        f.stats.shots = stats.shots;
        f.stats.hits = stats.hits;
        f.stats.kills = stats.kills;
        f.stats.damage = stats.damage;
        f.stats.seconds = stats.seconds;
        if (stats.hits)
        {
            if (m_observedHits && *stats.hits > *m_observedHits)
            {
                m_hitTime = t;
                m_hitDelta = *stats.hits - *m_observedHits;
                if (m_hitTargetTime >= 0 && m_hitTargetTime < t - 0.1) m_hitTarget.reset();
            }
            if (m_observedHits && *stats.hits < *m_observedHits)
            {
                m_hitTime.reset();
                m_hitDelta.reset();
            }
            m_observedHits = stats.hits;
        }
        f.stats.hitTime = m_hitTime;
        f.stats.hitDelta = m_hitDelta;
        if (m_hitTarget && m_hitTargetTime <= t)
        {
            f.stats.hitTarget = m_hitTarget;
            f.stats.hitTime = m_hitTargetTime;
            f.stats.hitDelta = 1.0;
        }

        switch (m_recording.AddFrame(f))
        {
        case ReplayRecording::Result::Full: Finish(m_recording.frames() >= ReplayLimits{}.maxFrames ? "frame-limit" : "size-limit", std::nullopt); return false;
        default: break;
        }
        if (now >= m_nextFlush)
        {
            m_nextFlush = now + FlushSeconds;
            m_output.ReplayAppend(m_recording.id(), m_recording.TakePending());
            PublishStatus("recording", "");
        }
        return true;
    }

    void ReplaySampler::BeginFrame()
    {
        UObject* player = m_player.Get();
        m_frameTime = player ? GameTime(player) : -1;
    }

    void ReplaySampler::OnInput(UObject* component, const char* action, bool axis, double value)
    {
        if (!m_recording.started() || m_recording.full()) return;
        if (m_recorder && component != m_recorder) return; // another recorder on the same character
        if (m_frameTime < 0) return;
        m_recording.AddInput(m_frameTime - m_start, action, axis ? value : 1.0);
    }

    void ReplaySampler::OnShotHit(UObject* shooter, UObject* target)
    {
        if (!m_recording.started() || !shooter || !target || shooter != m_character.Get()) return;
        const std::uint64_t key = reinterpret_cast<std::uint64_t>(target) ^ (static_cast<std::uint64_t>(target->GetInternalIndex()) << 47);
        auto it = m_ids.find(key);
        if (it == m_ids.end()) return; // only targets already recorded in this world
        UObject* player = m_player.Get();
        const double gameTime = player ? GameTime(player) : -1;
        if (gameTime < 0) return;
        m_hitTarget = it->second;
        m_hitTargetTime = gameTime - m_start;
    }

    void ReplaySampler::Finish(const std::string& reason, std::optional<double> score)
    {
        if (!m_recording.started()) return;
        const std::string id = m_recording.id();
        m_output.ReplayAppend(id, m_recording.Finish(reason, score));
        m_output.ReplayClose(id, reason == "completed" && m_recording.frames() > 1);
        m_stopReason = reason;
        m_player.Reset();
        m_character.Reset();
        m_recorder = nullptr;
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

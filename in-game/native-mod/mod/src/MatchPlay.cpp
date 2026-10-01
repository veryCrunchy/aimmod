#include "MatchPlay.hpp"

#include "Log.hpp"
#include "Output.hpp"
#include "World.hpp"

#include <aimmod/Formats.hpp>
#include <aimmod/GameCommand.hpp>

#include <Unreal/CoreUObject/UObject/UnrealType.hpp>
#include <Unreal/UClass.hpp>
#include <Unreal/UObject.hpp>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>

namespace aimmod
{
    using namespace game;

    namespace
    {
        constexpr std::size_t ShotWindow = 32;      // shots kept in self-shots.tsv
        constexpr std::int64_t ShotWindowMs = 3000; // and at most this old
        constexpr int MaxShotsPerFrame = 8;

        std::int64_t UnixMs()
        {
            return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
        }
        void WriteFloat(std::uint8_t* value, double v)
        {
            const float f = static_cast<float>(v);
            std::memcpy(value, &f, 4);
        }
        void WriteBool(std::uint8_t* value, const Param& p, bool v)
        {
            if (p.boolProperty) p.boolProperty->SetPropertyValue(value, v);
        }
        constexpr double DegToRad = 3.14159265358979323846 / 180.0;
    } // namespace

    void MatchPlay::Tick(double now, const std::string& scenario, bool inChallenge, bool loading, const PoseId& poseId,
                         const std::unordered_map<std::uint32_t, std::string>& poseNames)
    {
        TickShots(now, poseId, poseNames);
        TickPlayState(now, scenario, inChallenge, loading);
    }

    // ---------------------------------------------------------------- shots

    void MatchPlay::TickShots(double, const PoseId& poseId, const std::unordered_map<std::uint32_t, std::string>& poseNames)
    {
        if (!m_output.shotsRequested())
        {
            if (m_shotsWanted) Log("match play: shot stream stopped (no request)");
            m_shotsWanted = false;
            m_weapons.clear();
            m_shots.clear();
            return;
        }
        if (!m_shotsWanted)
        {
            m_shotsWanted = true;
            m_session = UnixMs();
            Log("match play: shot stream requested");
        }
        UObject* player = m_scene.Player();
        UObject* character = player ? m_b.myCharacter.Object(player) : nullptr;
        std::vector<WeaponCount> counts;
        if (!character || !ReadWeaponCounters(character, counts))
        {
            m_weapons.clear();
            return;
        }
        // A new character or weapon set only sets the baseline.
        bool same = character == m_shotsCharacter && counts.size() == m_weapons.size();
        for (std::size_t i = 0; same && i < counts.size(); ++i)
            same = counts[i].weapon == m_weapons[i].weapon && counts[i].shots >= m_weapons[i].shots && counts[i].hits >= m_weapons[i].hits;
        if (!same)
        {
            m_shotsCharacter = character;
            m_weapons.clear();
            for (const WeaponCount& c : counts) m_weapons.push_back({c.weapon, c.shots, c.hits});
            return;
        }
        int fired = 0;
        for (std::size_t i = 0; i < counts.size(); ++i) fired += static_cast<int>(std::min(counts[i].shots - m_weapons[i].shots, 64.0));
        if (fired == 0) return;

        UObject* camera = m_b.cameraManager.Object(player);
        double origin[3], rotation[3];
        const bool view = camera && m_b.cameraLocation.Vector(camera, origin) && m_b.cameraRotation.Vector(camera, rotation);
        if (view)
        {
            const double pitch = rotation[0] * DegToRad, yaw = rotation[1] * DegToRad;
            const double direction[3] = {std::cos(pitch) * std::cos(yaw), std::cos(pitch) * std::sin(yaw), std::sin(pitch)};
            // The nearest target capsule the ray meets (no world occlusion test).
            std::uint32_t target = 0;
            bool head = false;
            double best = 0;
            std::vector<UObject*> actors;
            if (UObject* state = m_scene.GameState(); state && m_b.characters.Objects(state, actors, 33))
                for (UObject* actor : actors)
                {
                    if (actor == character) continue;
                    if (auto hidden = m_b.hidden.Bool(actor); hidden && *hidden) continue;
                    UObject* capsule = m_b.capsule.Object(actor);
                    auto radius = capsule ? m_b.capsuleRadius.Number(capsule) : std::nullopt;
                    auto half = capsule ? m_b.capsuleHalfHeight.Number(capsule) : std::nullopt;
                    double center[3];
                    if (!radius || !half || *radius <= 0 || *half < *radius || !m_b.actorLocation.Vector(actor, center)) continue;
                    auto t = RayCapsule(origin, direction, center, *radius, *half);
                    if (!t || (target && *t >= best)) continue;
                    best = *t;
                    target = poseId(actor);
                    const double point[3] = {origin[0] + direction[0] * *t, origin[1] + direction[1] * *t, origin[2] + direction[2] * *t};
                    head = IsHeadHit(point, center, *half);
                }
            const std::int64_t ms = UnixMs();
            for (std::size_t i = 0; i < counts.size(); ++i)
            {
                const int shots = static_cast<int>(std::min(counts[i].shots - m_weapons[i].shots, static_cast<double>(MaxShotsPerFrame)));
                int hits = static_cast<int>(std::min(counts[i].hits - m_weapons[i].hits, static_cast<double>(shots)));
                for (int k = 0; k < shots; ++k)
                {
                    ShotRecord r;
                    r.unixMs = ms;
                    r.sequence = ++m_shotSequence;
                    std::copy(origin, origin + 3, r.origin);
                    std::copy(direction, direction + 3, r.direction);
                    r.slot = counts[i].slot;
                    r.target = target;
                    r.headshot = target != 0 && head;
                    r.gameHit = hits-- > 0;
                    m_shots.push_back(r);
                }
            }
        }
        for (std::size_t i = 0; i < counts.size(); ++i) m_weapons[i] = {counts[i].weapon, counts[i].shots, counts[i].hits};
        if (!view) return;
        const std::int64_t cutoff = UnixMs() - ShotWindowMs;
        while (!m_shots.empty() && (m_shots.size() > ShotWindow || m_shots.front().unixMs < cutoff)) m_shots.pop_front();

        std::string body = "AIMMOD_SHOTS_1\t" + std::to_string(++m_shotsPublish) + "\t" + std::to_string(m_session) + "\n";
        std::string tags;
        const auto avatars = m_output.avatars();
        std::vector<std::uint32_t> tagged;
        for (const ShotRecord& r : m_shots)
        {
            body += FormatShot(r);
            if (!r.target || avatars->empty() || std::find(tagged.begin(), tagged.end(), r.target) != tagged.end()) continue;
            tagged.push_back(r.target);
            if (auto name = poseNames.find(r.target); name != poseNames.end())
                if (auto tag = avatars->find(name->second); tag != avatars->end()) tags += "tag\t" + std::to_string(r.target) + "\t" + tag->second + "\n";
        }
        m_output.PublishSelfShots(std::move(body) + tags);
    }

    // ----------------------------------------------------------- play state

    bool MatchPlay::BindCharacter(UObject* character)
    {
        UClass* cls = character->GetClassPrivate();
        if (cls == m_class) return !m_disabled;
        m_class = cls;
        m_handleDamage.Reset();
        m_setHealth.Reset();
        m_respawn.Reset();
        m_killed.Reset();
        m_overrideInvulnerable.Reset();
        m_resetInvulnerable.Reset();
        m_currentHealth.Reset();
        m_respawnTimer.Reset();
        m_handleDamage.BindName(cls, STR("HandleDamage"), Shape::Command);
        m_setHealth.BindName(cls, STR("SetHealth"), Shape::Command);
        m_respawn.BindName(cls, STR("Respawn"), Shape::Command);
        m_killed.BindName(cls, STR("OnCharacterKilled"), Shape::Command);
        m_overrideInvulnerable.BindName(cls, STR("OverrideInvulnerable"), Shape::Command);
        m_resetInvulnerable.BindName(cls, STR("ResetInvulnerable"), Shape::Command);
        m_currentHealth.BindName(cls, STR("GetCurrentHealth"), Shape::Number);
        m_respawnTimer.BindName(cls, STR("SetRespawnTimer"), Shape::Command); // optional
        std::string missing;
        const std::pair<const Getter*, const char*> required[] = {
            {&m_handleDamage, "HandleDamage"},       {&m_setHealth, "SetHealth"},
            {&m_respawn, "Respawn"},                 {&m_killed, "OnCharacterKilled"},
            {&m_overrideInvulnerable, "OverrideInvulnerable"}, {&m_resetInvulnerable, "ResetInvulnerable"},
            {&m_currentHealth, "GetCurrentHealth"},
        };
        for (const auto& [getter, name] : required)
            if (!getter->ok()) missing += std::string(missing.empty() ? "" : ", ") + name + (getter->error().empty() ? "" : " (" + getter->error() + ")");
        if (!missing.empty())
        {
            m_disabled = true;
            Log("match play: disabled; missing character bindings on " + ClassName(character) + ": " + missing);
            return false;
        }
        Log("match play: character bindings ready on " + ClassName(character) + (m_respawnTimer.ok() ? "" : " (no SetRespawnTimer)"));
        return true;
    }

    std::optional<double> MatchPlay::Health(UObject* character) const { return m_currentHealth.Number(character); }

    void MatchPlay::SetHealth(UObject* character, double health)
    {
        m_setHealth.Call(character, [&](std::uint8_t* value, const Param& p) {
            if (p.kind == Kind::Float) WriteFloat(value, health);
        });
    }

    void MatchPlay::Release(const char* why)
    {
        if (!m_engaged) return;
        UObject* player = m_scene.Player();
        UObject* character = player ? m_b.myCharacter.Object(player) : nullptr;
        if (m_protectionSet && character && character->GetClassPrivate() == m_class)
            m_resetInvulnerable.Call(character, [](std::uint8_t*, const Param&) {});
        m_protectionSet = m_protected = false;
        m_engaged = false;
        m_dead = false;
        m_stateSequence = 0;
        Log(std::string("match play: released (") + why + "); " + std::to_string(m_applied) + " update(s) applied");
        m_applied = 0;
    }

    void MatchPlay::TickPlayState(double now, const std::string& scenario, bool inChallenge, bool loading)
    {
        const auto snapshot = m_output.playState();
        if (snapshot.version != m_version)
        {
            m_version = snapshot.version;
            m_state = snapshot.state;
        }
        const PlayState* s = m_state.get();
        // Gate: generated match scenarios in freeplay only; never a challenge.
        const char* closed = nullptr;
        if (!s) closed = "no play state";
        else if (inChallenge) closed = "challenge";
        else if (loading) closed = "loading";
        else if (!scenario.starts_with(MatchScenarioPrefix)) closed = "not a match scenario";
        else if (s->scenario != scenario) closed = "play state is for another scenario";
        else if (m_disabled) closed = "disabled";
        UObject* player = m_scene.Player();
        UObject* character = player ? m_b.myCharacter.Object(player) : nullptr;
        if (!closed && !character) closed = "no character";
        if (!closed && !BindCharacter(character)) closed = "disabled";
        if (closed)
        {
            if (m_engaged) Release(closed);
            else if (s && m_closedReason != closed)
                Log(std::string("match play: play state ignored (") + closed + ")");
            m_closedReason = s ? closed : "";
            return;
        }
        m_closedReason.clear();
        if (!m_engaged)
        {
            m_engaged = true;
            m_dead = Health(character).value_or(1) <= 0;
            // A hit reported before engaging is history, not an effect to replay.
            m_hitSequence = s->lastHit ? s->lastHit->sequence : 0;
            Log("match play: engaged in \"" + scenario + "\"");
        }

        const bool update = s->sequence != m_stateSequence;
        if (update)
        {
            m_stateSequence = s->sequence;
            ++m_applied;
            if (s->spawnProtected != m_protected || !m_protectionSet)
            {
                m_overrideInvulnerable.Call(character, [&](std::uint8_t* value, const Param& p) {
                    if (p.kind == Kind::Bool) WriteBool(value, p, s->spawnProtected);
                });
                m_protected = s->spawnProtected;
                m_protectionSet = true;
            }
            // The hit effect: the game's own damage handling (flash, sound,
            // indicator), never lethal by itself; health is set right after.
            if (s->lastHit && s->lastHit->sequence > m_hitSequence)
            {
                m_hitSequence = s->lastHit->sequence;
                const double current = Health(character).value_or(0);
                const double amount = std::clamp(s->lastHit->damage, 0.0, std::max(0.0, current - 1.0));
                double location[3]{};
                if (!m_dead && current > 1 && m_b.actorLocation.Vector(character, location))
                {
                    const double* d = s->lastHit->direction;
                    const double origin[3] = {location[0] - d[0] * 100, location[1] - d[1] * 100, location[2] - d[2] * 100};
                    m_handleDamage.Call(character, [&](std::uint8_t* value, const Param& p) {
                        if (p.kind == Kind::Float) WriteFloat(value, p.name == "amount" ? amount : 0.0); // WeaponKB 0: no knockback
                        else if (p.kind == Kind::Vector && p.size == 12)
                        {
                            const float f[3] = {static_cast<float>(origin[0]), static_cast<float>(origin[1]), static_cast<float>(origin[2])};
                            std::memcpy(value, f, 12);
                        }
                        // Attacker stays null: no game character is credited.
                    });
                }
            }
            if (!s->alive && !m_dead)
            {
                m_killed.Call(character, [](std::uint8_t*, const Param&) {});
                m_dead = true;
                if (m_respawnTimer.ok() && s->respawnAtMs > 0)
                {
                    const double seconds = std::clamp((s->respawnAtMs - UnixMs()) / 1000.0, 0.1, 30.0);
                    m_respawnTimer.Call(character, [&](std::uint8_t* value, const Param& p) {
                        if (p.kind == Kind::Float) WriteFloat(value, seconds);
                    });
                }
                Log("match play: died" + (s->lastHit ? " (attacker " + s->lastHit->attacker + ")" : std::string()));
            }
            else if (s->alive && m_dead)
            {
                // The native respawn timer may already have brought the character back.
                if (Health(character).value_or(0) <= 0)
                    m_respawn.Call(character, [](std::uint8_t* value, const Param& p) {
                        if (p.kind == Kind::Bool) WriteBool(value, p, true);
                    });
                m_dead = false;
                Log("match play: respawned");
            }
        }
        // Health follows the host (also undoing local regeneration or damage).
        if (s->alive && !m_dead && (update || now >= m_nextHealthCheck))
        {
            m_nextHealthCheck = now + 0.25;
            if (auto current = Health(character); current && std::fabs(*current - s->health) > 0.5) SetHealth(character, s->health);
        }
    }
} // namespace aimmod

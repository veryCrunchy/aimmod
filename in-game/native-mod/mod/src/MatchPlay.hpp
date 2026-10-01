#pragma once
// Match damage for AimMod game modes (DESIGN.md "Match play"). Game thread.
//  - Shots out: polls the local weapons' shot/hit counters every frame and,
//    while self-shots.request is fresh, publishes each new shot with the
//    camera ray and the target its ray meets (self-pose target ids).
//  - Play state in: applies the host's verdict (health, death, respawn,
//    spawn protection, hit effect) to the local character through the
//    game's own functions, only in generated "AimMod Match - " scenarios in
//    freeplay. Missing bindings disable it (logged); leaving the gate
//    releases spawn protection.
#include "GameBindings.hpp"

#include <aimmod/MatchPlay.hpp>

#include <cstdint>
#include <deque>
#include <functional>
#include <memory>
#include <optional>
#include <string>
#include <unordered_map>
#include <vector>

namespace aimmod
{
    class Output;
    namespace game
    {
        struct Bindings;
        struct Scene;
    } // namespace game

    class MatchPlay
    {
    public:
        using PoseId = std::function<std::uint32_t(game::UObject* actor)>;
        MatchPlay(game::Bindings& bindings, game::Scene& scene, Output& output) : m_b(bindings), m_scene(scene), m_output(output) {}
        // "match-play" capability: the character bindings resolved (known after
        // the first match character was seen; true until proven otherwise).
        bool available() const { return !m_disabled; }
        bool engaged() const { return m_engaged; }
        void Tick(double now, const std::string& scenario, bool inChallenge, bool loading, const PoseId& poseId,
                  const std::unordered_map<std::uint32_t, std::string>& poseNames);

    private:
        void TickShots(double now, const PoseId& poseId, const std::unordered_map<std::uint32_t, std::string>& poseNames);
        void TickPlayState(double now, const std::string& scenario, bool inChallenge, bool loading);
        bool BindCharacter(game::UObject* character);
        void Release(const char* why);
        std::optional<double> Health(game::UObject* character) const;
        void SetHealth(game::UObject* character, double health);

        game::Bindings& m_b;
        game::Scene& m_scene;
        Output& m_output;

        // Shots.
        struct WeaponState
        {
            game::UObject* weapon{};
            double shots{}, hits{};
        };
        std::vector<WeaponState> m_weapons;
        game::UObject* m_shotsCharacter{};
        std::deque<ShotRecord> m_shots;
        std::uint64_t m_shotSequence{}, m_shotsPublish{};
        std::int64_t m_session{};
        bool m_shotsWanted{};

        // Play state.
        game::UClass* m_class{};
        game::Getter m_handleDamage, m_setHealth, m_respawn, m_killed, m_overrideInvulnerable, m_resetInvulnerable, m_currentHealth, m_respawnTimer;
        bool m_disabled{}, m_engaged{}, m_dead{}, m_protected{}, m_protectionSet{};
        std::uint64_t m_version{~0ull}, m_stateSequence{}, m_hitSequence{};
        std::shared_ptr<const PlayState> m_state;
        double m_nextHealthCheck{};
        std::string m_closedReason;
        std::uint32_t m_applied{};
    };
} // namespace aimmod

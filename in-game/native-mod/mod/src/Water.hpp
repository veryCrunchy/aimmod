#pragma once
// Swimmable water in AimMod scenarios (DESIGN.md "Water"). Game thread only.
//  - KovaaK's map-creator Water objects are visual only. For each one in the
//    current map, AimModCore turns the mesh's collision off and spawns an
//    engine water volume (APhysicsVolume, bWaterVolume) over the same box, with
//    an underwater tint (post-process component bound to the box).
//  - The local character's own swimming mode then does the rest; AimModCore
//    tunes it per movement style (CS or Quake, core/Water) and adds the swim
//    input the game has no binding for: jump swims up, no input sinks slowly.
//  - Only in AimMod's own scenarios in freeplay (water::Allowed). Leaving the
//    gate destroys the volumes and restores the mesh collision and movement
//    values. Every peer derives the same volumes from the same map data.
#include "GameBindings.hpp"

#include <aimmod/Water.hpp>

#include <Unreal/FWeakObjectPtr.hpp>

#include <cstdint>
#include <string>
#include <vector>

namespace aimmod
{
    namespace game
    {
        struct Bindings;
        struct Scene;
    } // namespace game

    class Water
    {
    public:
        Water(game::Bindings& bindings, game::Scene& scene) : m_b(bindings), m_scene(scene) {}
        void Bind();
        void Tick(double now, const std::string& scenario, bool inChallenge, bool loading);
        // Any thread: forgets the actors without calling into the game.
        void Shutdown()
        {
            m_volumes.clear();
            m_saved = {};
            m_tuned = false;
            m_world = nullptr;
            m_character = RC::Unreal::FWeakObjectPtr{};
        }

    private:
        struct Volume
        {
            RC::Unreal::FWeakObjectPtr source, mesh, volume;
            std::uint8_t meshCollision{};
            bool collisionSaved{};
        };
        struct Saved
        {
            RC::Unreal::FWeakObjectPtr movement;
            float maxSwimSpeed{}, buoyancy{}, outOfWaterZ{}, jumpOutOfWaterPitch{};
            bool canSwim{true}, canSwimSaved{};
        };

        void Scan(game::UObject* world, game::UObject* character);
        bool Spawn(game::UObject* world, game::UObject* source);
        void TuneCharacter(game::UObject* character);
        void Restore();
        void SwimInput(game::UObject* player, game::UObject* character);
        bool JumpHeld(game::UObject* player);
        void Release(const char* why);

        game::Bindings& m_b;
        game::Scene& m_scene;
        game::Getter m_inBenchmark, m_inEditor;
        game::UClass* m_volumeClass{};
        game::UClass* m_boxClass{};
        game::UClass* m_postClass{};
        bool m_bound{}, m_disabled{};
        game::UObject* m_world{};
        std::vector<Volume> m_volumes;
        Saved m_saved;
        RC::Unreal::FWeakObjectPtr m_character;
        game::Field m_mode; // CharacterMovementComponent.MovementMode
        bool m_tuned{};
        water::Style m_style{water::Style::CounterStrike};
        water::Tuning m_tuning{};
        std::vector<std::string> m_jumpKeys;
        double m_nextScan{}, m_engagedAt{};
        std::string m_scenario, m_lastReason;
        bool m_swimLogged{};
    };
} // namespace aimmod

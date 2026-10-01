#pragma once
// Ghost demo (config ghost_demo=1), game thread only: reads the local
// player's position, view rotation and velocity each tick and hands them to
// the bridge (sent at 30 Hz); spawns one marker per remote lobby member in
// the local world (engine basic shapes, no collision) and moves it with
// ~100 ms interpolation. Never touches gameplay objects other than reading
// the local pose; only spawns, moves and destroys its own actors.
#include "Bridge.hpp"
#include "GameBindings.hpp"

#include <Unreal/FWeakObjectPtr.hpp>

#include <cstdint>
#include <map>
#include <string>

namespace aimmod
{
    class GhostDemo
    {
    public:
        GhostDemo(bridge::Bridge& bridge, bridge::LogFn log);
        void Tick();     // engine tick (game thread)
        void Shutdown(); // destroys every ghost (game thread)

    private:
        struct Ghost
        {
            RC::Unreal::FWeakObjectPtr body, head, visor;
            RC::Unreal::UObject* world = nullptr;
            std::string hiddenScene; // non-empty while hidden because of a scenario mismatch
        };

        bool Bind();
        RC::Unreal::UObject* LocalCharacter(RC::Unreal::UObject*& controller);
        RC::Unreal::UObject* LoadMesh(const wchar_t* path);
        RC::Unreal::UObject* SpawnShape(RC::Unreal::UObject* world, RC::Unreal::UObject* mesh, double sx, double sy, double sz);
        bool EnsureSpawned(Ghost& ghost, RC::Unreal::UObject* world);
        void Destroy(Ghost& ghost);
        void Place(Ghost& ghost, double x, double y, double z, double yaw);

        bridge::Bridge& m_bridge;
        bridge::LogFn m_log;
        bool m_bound = false;
        bool m_failed = false;
        game::Getter m_actorLocation, m_velocity, m_cameraRotation, m_capsuleHalfHeight, m_capsuleRadius;
        game::Getter m_setStaticMesh, m_setMobility, m_setCollision, m_setCastShadow;
        game::Field m_cameraManager, m_myCharacter, m_meshComponent, m_capsule;
        RC::Unreal::UClass* m_meshActorClass = nullptr;
        RC::Unreal::UObject* m_cylinder = nullptr;
        RC::Unreal::UObject* m_sphere = nullptr;
        RC::Unreal::UObject* m_cube = nullptr;
        RC::Unreal::FWeakObjectPtr m_controller;
        double m_nextFind = 0;
        double m_halfHeight = 88, m_radius = 34;
        std::map<std::uint64_t, Ghost> m_ghosts;
    };
} // namespace aimmod

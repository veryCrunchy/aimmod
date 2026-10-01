#pragma once
// Ghost demo (config ghost_demo=1), game thread only.
//  - Reads the local player's position, view rotation, velocity and crouch
//    each tick and hands them to the bridge (sent at 30 Hz).
//  - Shows each remote lobby member on the same scenario as a real KovaaK's
//    character: a bot spawned through the game's own spawn path
//    (ATheMetaAIController::Spawn) with its AI switched off, invulnerable,
//    driven from the pose stream with ~100 ms interpolation. When bots can't
//    be spawned, it falls back to engine basic shapes (no collision).
// It never touches ranked, leaderboards or the local player; it only reads
// the local pose and spawns, drives and removes its own actors.
#include "AvatarPath.hpp"
#include "Bridge.hpp"
#include "GameBindings.hpp"
#include "GhostMath.hpp"
#include "AvatarState.hpp"
#include "Walker.hpp"

#include <Unreal/FWeakObjectPtr.hpp>

#include <cstdint>
#include <filesystem>
#include <map>
#include <optional>
#include <set>
#include <string>

namespace aimmod
{
    struct GhostOptions
    {
        bool avatars = true;       // real characters; shapes are the fallback
        std::string avatarProfile; // bot profile to spawn; empty = the scenario's own bot profile
        int moveMode = 5;          // EMovementMode while driven: 5 Flying (default), 1 Walking, 0 None
        bool driveWithUpdate = true; // UpdateClientLocAndRot(bPlayAnim) vs K2_SetActorLocationAndRotation
        bool avatarTest = false;   // offline check: one avatar circling the local player, no network
        // Offline avatar spike: with avatar_test=1, a recorded path in this file (exported by the
        // service with --export-avatar-path) replaces the circle when its scenario is loaded.
        std::filesystem::path avatarTestPath;
        std::wstring stateDir;     // KovaaksNative: avatars.tsv for AimModCore
        bool showRemote = true;
        bool nativeDeath = false;  // avatar_death=native: the game's Death/Respawn; default hides the avatar    // show remote players (ghost demo); the local pose/camera is read either way
    };

    class GhostDemo
    {
    public:
        GhostDemo(bridge::Bridge& bridge, bridge::LogFn log, GhostOptions options);
        void Tick();     // engine tick (game thread)
        void Shutdown(); // removes every ghost and avatar (game thread)

    private:
        struct Ghost
        {
            // Shapes fallback
            RC::Unreal::FWeakObjectPtr body, head, visor;
            RC::Unreal::UObject* world = nullptr;
            // Avatar bot
            RC::Unreal::FWeakObjectPtr pawn, controller;
            double nextSpawn = 0, nextInert = 0;
            int spawnFailures = 0;
            bool crouching = false;
            bool avatarLogged = false;
            std::string characterProfile; // applied character profile (appearance)
            std::string botProfile;       // bot profile the game reports for it now
            std::string spawnedFrom;      // bot profile we asked for
            std::uint64_t peer = 0;       // remote player (actor tag AimMod.Peer.<id>)
            int spawnTeam = 2;            // team the bot spawned on (the enemy team)
            int team = -1;                // team we last applied
            bool dead = false;            // from avatar-state.tsv
            double health = -1;           // last health applied to the bar
            std::string hiddenScene;      // non-empty while hidden because of a scenario mismatch
            bool respawned = false;       // came back from a death since the last look (walker re-places)
            // Measured: the actor origin's height above the mesh's lowest point (its feet). The body is
            // placed so the feet touch the floor (sample centre minus the sample's half-height).
            double feetToActor = -1;
            double nextFeetMeasure = 0;
            bool feetLogged = false;
            std::string weapon;           // CS: the third-person weapon model shown in its hands ("" none)
            double nextWeapon = 0;        // when to re-apply it (the game may hide it again)
        };
        using Sample = bridge::ghost::RemoteTransform; // remote values only

        bool Bind();
        bool BindAvatars();
        RC::Unreal::UObject* LocalCharacter(RC::Unreal::UObject*& controller);
        RC::Unreal::UObject* LoadMesh(const wchar_t* path);
        RC::Unreal::UObject* SpawnShape(RC::Unreal::UObject* world, RC::Unreal::UObject* mesh, double sx, double sy, double sz);
        bool EnsureShapes(Ghost& ghost, RC::Unreal::UObject* world);
        void DestroyShapes(Ghost& ghost);
        void PlaceShapes(Ghost& ghost, const Sample& s);

        bool EnsureAvatar(std::uint64_t peer, Ghost& ghost, RC::Unreal::UObject* localCharacter);
        void KeepInert(Ghost& ghost);
        void DriveAvatar(Ghost& ghost, const Sample& s);
        void RemoveAvatar(Ghost& ghost);
        std::string ScenarioBotProfile(int& team);
        void Remove(Ghost& ghost);
        void Show(std::uint64_t peer, Ghost& ghost, const Sample& s, RC::Unreal::UObject* world, RC::Unreal::UObject* character);

        bridge::Bridge& m_bridge;
        bridge::LogFn m_log;
        GhostOptions m_options;
        bool m_bound = false;
        bool m_failed = false;
        bool m_shapesFailed = false;
        bool m_avatarsBound = false;
        bool m_avatarsFailed = false;

        game::Getter m_actorLocation, m_velocity, m_cameraRotation, m_cameraLocation, m_cameraFov, m_capsuleHalfHeight, m_capsuleRadius, m_isCrouching, m_getPawn;
        game::Getter m_setStaticMesh, m_setMobility, m_setCollision, m_setCastShadow;
        game::Field m_cameraManager, m_myCharacter, m_meshComponent, m_capsule;
        RC::Unreal::UClass* m_meshActorClass = nullptr;
        // Weak: nothing of ours roots the meshes, so they may be collected.
        RC::Unreal::FWeakObjectPtr m_cylinder, m_sphere, m_cube;

        // Avatars
        game::Getter m_spawnBot, m_getMetaCharacter, m_setUseWeapons, m_stopAiming, m_removeSelf;
        game::Getter m_updateClientLocAndRot, m_overrideInvulnerable, m_startCrouching, m_startUncrouch, m_getTeam, m_loadCharacterProfile;
        game::Getter m_setMovementMode, m_updateVisibility, m_death, m_respawn, m_setTeam, m_setHealth;
        std::optional<bridge::avatarstate::File> m_avatarState;
        double m_nextStateRead = 0;
        double m_nextHelperPark = 0;
        std::set<RC::Unreal::UObject*> m_parkedHelpers; // identity only: logged once each
        void ParkHelperBots();
        void ReadAvatarState();
        void ApplyCombatState(Ghost& ghost, RC::Unreal::UObject* localCharacter);
        // CS: the weapon the remote player holds, on the avatar's own third-person weapon mesh.
        void ApplyWeapon(Ghost& ghost, RC::Unreal::UObject* pawn, const std::string& model);
        RC::Unreal::UClass* m_weaponClass = nullptr;
        game::Getter m_thirdPersonPrimary, m_setVisibility, m_setHiddenInGame;
        std::string m_lastScene; // re-apply looks and AI-off when the scenario changes
        bool m_botsAllowed = false; // bridge::ghost::AvatarBotsAllowed(local scenario)
        bool m_avatarMapDirty = true;
        void WriteAvatarMap();
        game::Field m_movementComponent;
        RC::Unreal::UObject* m_aiControllerDefault = nullptr;
        std::set<RC::Unreal::UObject*> m_ownControllers; // controllers we spawned (identity only)

        RC::Unreal::FWeakObjectPtr m_controller;
        double m_nextFind = 0;
        double m_nextDiagnostic = 0;
        std::map<std::uint64_t, Ghost> m_ghosts;
        double m_testStart = -1;
        int m_devGeneration = 0;
        // Offline avatar spike (recorded path).
        std::optional<bridge::ghost::AvatarPath> m_testPath;
        bool m_testPathTried = false;
        std::string m_testPathScene; // the scenario last reported as not matching the path
        double m_eyeAboveCentre = 64; // local camera height above the capsule centre, measured live
        double m_testPathStart = -1;
        // Developer mode's simulated player walking the arena (dev.avatar mode walk).
        bridge::ghost::Walker m_walker;
        double m_walkAt = -1;
        // Several simulated players (dev.avatar walkers): one walker per synthetic peer.
        struct DevWalk
        {
            bridge::ghost::Walker walker;
            double at = -1;
        };
        std::map<std::uint64_t, DevWalk> m_walkers;
        std::string DevLook(std::uint64_t peer);
        bool IsDevPeer(std::uint64_t peer) const { return peer >= 1 && peer <= 16; }
        game::Getter m_lineTrace;
        RC::Unreal::UObject* m_kismetDefault = nullptr;
        std::int32_t m_hitImpactOffset = -1;
        RC::Unreal::FBoolProperty* m_hitStartPenetrating = nullptr; // a trace that starts inside geometry: no floor
        bool m_traceBound = false;
        bool m_traceForObjects = false; // LineTraceSingleForObjects (WorldStatic + WorldDynamic) rather than the Visibility channel
        int m_traceCount = 0, m_traceHits = 0;
        int m_floorTraces = 0, m_floorHits = 0, m_wallTraces = 0, m_wallHits = 0; // walker diagnostics
        double m_nextTraceLog = 0;
        game::Getter m_lineTraceChannel; // Visibility channel fallback when the object trace finds nothing
        game::Getter m_componentBounds;   // KismetSystemLibrary:GetComponentBounds (the avatar mesh's world box)
        game::Field m_characterMesh;      // Character.Mesh
        bool m_feetBound = false;
        void MeasureFeet(Ghost& ghost, double floorZ);
        // Line trace on Visibility from a to b, ignoring both bodies; the impact point, or nullopt.
        std::optional<std::array<double, 3>> Trace(RC::Unreal::UObject* context, const double a[3], const double b[3], RC::Unreal::UObject* ignore1, RC::Unreal::UObject* ignore2);
        bool LoadTestPath();
    };
} // namespace aimmod

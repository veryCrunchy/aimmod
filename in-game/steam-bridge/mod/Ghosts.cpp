#include "Ghosts.hpp"

#include "PoseFile.hpp"

#include <Unreal/AActor.hpp>
#include <Unreal/Core/Containers/Array.hpp>
#include <Unreal/FAssetData.hpp>
#include <Unreal/FHitResult.hpp>
#include <Unreal/GameplayStatics.hpp>
#include <Unreal/Rotator.hpp>
#include <Unreal/Transform.hpp>
#include <Unreal/UAssetRegistry.hpp>
#include <Unreal/UAssetRegistryHelpers.hpp>
#include <Unreal/UClass.hpp>
#include <Unreal/UObject.hpp>
#include <Unreal/UObjectGlobals.hpp>
#include <Unreal/UnrealCoreStructs.hpp>
#include <Unreal/World.hpp>

#include <Windows.h>

#include <algorithm>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <cstdio>
#include <cstring>
#include <exception>
#include <fstream>
#include <iterator>
#include <vector>

namespace aimmod
{
    using namespace RC::Unreal;
    using game::Kind;
    using game::Param;
    using game::Shape;

    namespace
    {
        constexpr int MaxSpawnFailures = 3;          // then this peer falls back to shapes
        constexpr std::uint64_t TestPeer = 1;         // synthetic peer for avatar_test


        void WriteFloats(std::uint8_t* value, const Param& p, double a, double b, double c)
        {
            if (p.size == 12)
            {
                const float v[3] = {static_cast<float>(a), static_cast<float>(b), static_cast<float>(c)};
                std::memcpy(value, v, sizeof(v));
            }
            else if (p.size == 24)
            {
                const double v[3] = {a, b, c};
                std::memcpy(value, v, sizeof(v));
            }
        }

        // Reads a reflected FString property (MyProfileName) without engine string helpers.
        std::string ReadFString(UObject* object, const wchar_t* property)
        {
            struct RawString
            {
                const wchar_t* data;
                std::int32_t num, max;
            };
            auto* raw = object ? object->GetValuePtrByPropertyNameInChain<RawString>(property) : nullptr;
            if (!raw || !raw->data || raw->num <= 1 || raw->num > 256) return {};
            return game::Narrow(std::wstring(raw->data, static_cast<std::size_t>(raw->num - 1)));
        }
    } // namespace

    GhostDemo::GhostDemo(bridge::Bridge& bridge, bridge::LogFn log, GhostOptions options)
        : m_bridge(bridge), m_log(std::move(log)), m_options(std::move(options))
    {
    }

    bool GhostDemo::Bind()
    {
        if (m_bound) return true;
        if (m_failed) return false;
        bool ok = true;
        ok &= m_actorLocation.BindPath(STR("/Script/Engine.Actor:K2_GetActorLocation"), Shape::Vector);
        ok &= m_velocity.BindPath(STR("/Script/Engine.Actor:GetVelocity"), Shape::Vector);
        ok &= m_cameraRotation.BindPath(STR("/Script/Engine.PlayerCameraManager:GetCameraRotation"), Shape::Vector);
        ok &= m_cameraManager.Bind(game::FindClass(STR("/Script/Engine.PlayerController")), STR("PlayerCameraManager"));
        ok &= m_myCharacter.Bind(game::FindClass(STR("/Script/GameSkillsTrainer.MetaPlayerController")), STR("MyCharacter"));
        m_cameraLocation.BindPath(STR("/Script/Engine.PlayerCameraManager:GetCameraLocation"), Shape::Vector);
        m_cameraFov.BindPath(STR("/Script/Engine.PlayerCameraManager:GetFOVAngle"), Shape::Number);
        m_getPawn.BindPath(STR("/Script/Engine.Controller:K2_GetPawn"), Shape::Object);
        m_isCrouching.BindPath(STR("/Script/GameSkillsTrainer.MetaCharacter:IsCrouching"), Shape::Bool);
        m_capsuleHalfHeight.BindPath(STR("/Script/Engine.CapsuleComponent:GetScaledCapsuleHalfHeight"), Shape::Number);
        m_capsuleRadius.BindPath(STR("/Script/Engine.CapsuleComponent:GetScaledCapsuleRadius"), Shape::Number);
        m_capsule.Bind(game::FindClass(STR("/Script/Engine.Character")), STR("CapsuleComponent"));
        if (!ok)
        {
            m_failed = true;
            m_log("ghost demo: local pose bindings unavailable; demo disabled");
            return false;
        }
        // Shapes fallback (optional).
        bool shapes = true;
        shapes &= m_setStaticMesh.BindPath(STR("/Script/Engine.StaticMeshComponent:SetStaticMesh"), Shape::Command);
        shapes &= m_setMobility.BindPath(STR("/Script/Engine.SceneComponent:SetMobility"), Shape::Command);
        shapes &= m_setCollision.BindPath(STR("/Script/Engine.PrimitiveComponent:SetCollisionEnabled"), Shape::Command);
        m_setCastShadow.BindPath(STR("/Script/Engine.PrimitiveComponent:SetCastShadow"), Shape::Command);
        m_meshActorClass = game::FindClass(STR("/Script/Engine.StaticMeshActor"));
        shapes &= m_meshActorClass != nullptr && m_meshComponent.Bind(m_meshActorClass, STR("StaticMeshComponent"));
        m_shapesFailed = !shapes;
        m_bound = true;
        m_log(std::string("ghost demo: bindings ready (shapes fallback ") + (shapes ? "available" : "unavailable") + ")");
        return true;
    }

    bool GhostDemo::BindAvatars()
    {
        if (m_avatarsBound) return true;
        if (m_avatarsFailed || !m_options.avatars) return false;
        const wchar_t* ai = STR("/Script/GameSkillsTrainer.TheMetaAIController:");
        auto ai_ = [&](const wchar_t* name) { return std::wstring(ai) + name; };
        const wchar_t* mc = STR("/Script/GameSkillsTrainer.MetaCharacter:");
        auto mc_ = [&](const wchar_t* name) { return std::wstring(mc) + name; };
        bool ok = true;
        ok &= m_spawnBot.BindPath(ai_(STR("Spawn")).c_str(), Shape::Command);
        ok &= m_getMetaCharacter.BindPath(ai_(STR("GetMetaCharacter")).c_str(), Shape::Object);
        ok &= m_setUseWeapons.BindPath(ai_(STR("SetUseWeapons")).c_str(), Shape::Command);
        ok &= m_removeSelf.BindPath(ai_(STR("RemoveSelf")).c_str(), Shape::Command);
        ok &= m_overrideInvulnerable.BindPath(mc_(STR("OverrideInvulnerable")).c_str(), Shape::Command);
        m_stopAiming.BindPath(ai_(STR("StopAiming")).c_str(), Shape::Command);
        m_updateClientLocAndRot.BindPath(mc_(STR("UpdateClientLocAndRot")).c_str(), Shape::Command);
        m_startCrouching.BindPath(mc_(STR("StartCrouching")).c_str(), Shape::Command);
        m_startUncrouch.BindPath(mc_(STR("StartUncrouch")).c_str(), Shape::Command);
        m_getTeam.BindPath(mc_(STR("GetTeam")).c_str(), Shape::Number);
        m_loadCharacterProfile.BindPath(mc_(STR("LoadCharacterProfile")).c_str(), Shape::Command);
        m_setMovementMode.BindPath(STR("/Script/Engine.CharacterMovementComponent:SetMovementMode"), Shape::Command);
        m_updateVisibility.BindPath(mc_(STR("UpdateVisibility")).c_str(), Shape::Command);
        m_death.BindPath(mc_(STR("Death")).c_str(), Shape::Command);
        m_respawn.BindPath(mc_(STR("Respawn")).c_str(), Shape::Command);
        m_setTeam.BindPath(mc_(STR("SetTeam")).c_str(), Shape::Command);
        m_setHealth.BindPath(mc_(STR("SetHealth")).c_str(), Shape::Command);
        m_movementComponent.Bind(game::FindClass(STR("/Script/Engine.Character")), STR("CharacterMovement"));
        m_aiControllerDefault = UObjectGlobals::StaticFindObject<UObject*>(nullptr, nullptr, STR("/Script/GameSkillsTrainer.Default__TheMetaAIController"));
        ok &= m_aiControllerDefault != nullptr;
        if (!ok)
        {
            m_avatarsFailed = true;
            m_log("avatars: bot spawn bindings unavailable (" + m_spawnBot.error() + m_overrideInvulnerable.error() +
                  "); using shapes");
            return false;
        }
        m_avatarsBound = true;
        m_log(std::string("avatars: bindings ready; drive=") + (m_options.driveWithUpdate && m_updateClientLocAndRot.ok() ? "UpdateClientLocAndRot" : "SetActorLocationAndRotation") +
              " moveMode=" + std::to_string(m_options.moveMode) + " velocity=" + (m_movementComponent.ok() ? "yes" : "no") + " crouch=" +
              (m_startCrouching.ok() && m_startUncrouch.ok() ? "yes" : "no"));
        return true;
    }

    UObject* GhostDemo::LoadMesh(const wchar_t* path)
    {
        if (UObject* found = UObjectGlobals::StaticFindObject<UObject*>(nullptr, nullptr, path)) return found;
        // Same path as UE4SS's LoadAsset: through the asset registry.
        auto* registry = static_cast<UAssetRegistry*>(UAssetRegistryHelpers::GetAssetRegistry().ObjectPointer);
        if (!registry) return nullptr;
        FAssetData data = registry->GetAssetByObjectPath(FName(path, FNAME_Add));
        if (!data.PackageName().GetComparisonIndex() && !data.ObjectPath().GetComparisonIndex()) return nullptr;
        return UAssetRegistryHelpers::GetAsset(data);
    }

    UObject* GhostDemo::LocalCharacter(UObject*& controller)
    {
        controller = m_controller.Get();
        UObject* character = nullptr;
        if (controller)
        {
            // The possessed pawn is the actor that actually moves (and jumps); MyCharacter is the fallback.
            character = m_getPawn.ok() ? m_getPawn.Object(controller) : nullptr;
            if (!character || !game::IsLiveInstance(character)) character = m_myCharacter.Object(controller);
        }
        if (character && game::IsLiveInstance(character)) return character;
        const double now = bridge::Bridge::Now();
        if (now < m_nextFind) return nullptr;
        m_nextFind = now + 1.0;
        std::vector<UObject*> found;
        UObjectGlobals::FindAllOf(STR("MetaPlayerController"), found);
        for (UObject* candidate : found)
        {
            if (!game::IsLiveInstance(candidate)) continue;
            UObject* c = m_myCharacter.Object(candidate);
            if (c && game::IsLiveInstance(c))
            {
                m_controller = candidate;
                controller = candidate;
                return c;
            }
        }
        return nullptr;
    }

    // --- avatars ----------------------------------------------------------

    std::string GhostDemo::ScenarioBotProfile(int& team)
    {
        team = 2;
        // 1. the lobby's avatar bot (generated match scenarios ship it, possibly as an invisible helper),
        // 2. the config override, 3. any bot the scenario already has.
        std::string named = m_bridge.LobbyValue("aimmod.avatar_bot");
        if (named.empty()) named = m_options.avatarProfile;
        std::vector<UObject*> found;
        UObjectGlobals::FindAllOf(STR("TheMetaAIController"), found);
        if (!named.empty())
        {
            for (UObject* controller : found)
            {
                if (!game::IsLiveInstance(controller) || m_ownControllers.count(controller) || ReadFString(controller, STR("MyProfileName")) != named) continue;
                if (UObject* pawn = m_getMetaCharacter.Object(controller))
                    if (auto t = m_getTeam.ok() ? m_getTeam.Number(pawn) : std::nullopt; t && *t >= 0 && *t < 16) team = static_cast<int>(*t);
                break;
            }
            return named;
        }
        for (UObject* controller : found)
        {
            if (!game::IsLiveInstance(controller) || m_ownControllers.count(controller)) continue;
            const std::string profile = ReadFString(controller, STR("MyProfileName"));
            if (profile.empty()) continue;
            if (UObject* pawn = m_getMetaCharacter.Object(controller))
                if (auto t = m_getTeam.ok() ? m_getTeam.Number(pawn) : std::nullopt; t && *t >= 0 && *t < 16) team = static_cast<int>(*t);
            return profile;
        }
        return {};
    }

    bool GhostDemo::EnsureAvatar(std::uint64_t peer, Ghost& ghost, UObject* localCharacter)
    {
        if (ghost.pawn.Get() && ghost.controller.Get()) return true;
        if (!BindAvatars() || ghost.spawnFailures >= MaxSpawnFailures) return false;
        const double now = bridge::Bridge::Now();
        if (now < ghost.nextSpawn) return false;
        ghost.nextSpawn = now + 2.0;
        RemoveAvatar(ghost); // a half-dead pair from a scenario reset

        int team = 2;
        const std::string profile = ScenarioBotProfile(team);
        if (profile.empty())
        {
            if (++ghost.spawnFailures >= MaxSpawnFailures) m_log("avatars: no bot profile in this scenario; using shapes for " + bridge::Redact(peer));
            return false;
        }
        UObject* controller = nullptr;
        m_spawnBot.Call(
            m_aiControllerDefault,
            [&](std::uint8_t* value, const Param& p) {
                if (p.worldContext) std::memcpy(value, &localCharacter, sizeof(localCharacter));
                else if (p.kind == Kind::String) game::WriteString(value, profile);
                else if (p.kind == Kind::Int32)
                {
                    const std::int32_t v = p.name == "Team" ? team : 0; // Lives 0 = unlimited
                    std::memcpy(value, &v, sizeof(v));
                }
            },
            [&](const std::uint8_t* buffer, const std::vector<Param>& params) {
                for (const Param& p : params)
                    if (p.ret) controller = game::ReadObject(buffer, p);
            });
        UObject* pawn = controller ? m_getMetaCharacter.Object(controller) : nullptr;
        if (!controller || !pawn)
        {
            ++ghost.spawnFailures;
            m_log("avatars: Spawn(\"" + profile + "\") returned no bot for " + bridge::Redact(peer) +
                  (ghost.spawnFailures >= MaxSpawnFailures ? "; using shapes" : "; retrying"));
            return false;
        }
        ghost.controller = controller;
        ghost.pawn = pawn;
        ghost.spawnFailures = 0;
        ghost.crouching = false;
        ghost.characterProfile.clear();
        ghost.botProfile = profile;
        ghost.spawnedFrom = profile;
        ghost.peer = peer;
        ghost.spawnTeam = team;
        ghost.team = team;
        ghost.dead = false;
        ghost.health = -1;
        m_avatarMapDirty = true;
        ghost.nextInert = 0;
        m_ownControllers.insert(controller);
        KeepInert(ghost);
        m_log("avatars: spawned \"" + profile + "\" (team " + std::to_string(team) + ") for " + bridge::Redact(peer) + "; AI off, invulnerable");
        return true;
    }

    void GhostDemo::KeepInert(Ghost& ghost)
    {
        const double now = bridge::Bridge::Now();
        if (now < ghost.nextInert) return;
        ghost.nextInert = now + 1.0; // re-applied: the game may reset bots at challenge start
        UObject* controller = ghost.controller.Get();
        UObject* pawn = ghost.pawn.Get();
        if (!controller || !pawn) return;
        // The game re-profiles bots in place across scenario changes: notice and re-apply the looks.
        if (const std::string current = ReadFString(controller, STR("MyProfileName")); !current.empty() && current != ghost.botProfile)
        {
            m_log("avatars: the game re-profiled an avatar to \"" + current + "\"; re-applying");
            ghost.botProfile = current;
            ghost.characterProfile.clear();
        }
        // Actor tag AimMod.Peer.<SteamID64>: AimModCore finds avatars by it. In-process only, re-applied
        // every refresh so a re-acquired or re-profiled bot keeps it.
        if (auto* tags = pawn->GetValuePtrByPropertyNameInChain<TArray<FName>>(STR("Tags")))
        {
            const FName tag((STR("AimMod.Peer.") + std::to_wstring(ghost.peer)).c_str(), FNAME_Add);
            if (!tags->Contains(tag)) tags->Add(tag);
        }
        // Shown even when spawned from an invisible helper profile (unless it is down in a combat match).
        if (!ghost.dead) static_cast<AActor*>(pawn)->SetActorHiddenInGame(false);
        ghost.team = -1; // re-applied by ApplyCombatState
        if (!ghost.dead && m_updateVisibility.ok())
            m_updateVisibility.Call(pawn, [](std::uint8_t* value, const Param& p) {
                if (p.kind == Kind::Bool) *value = 0;
            });
        m_setUseWeapons.Call(controller, [](std::uint8_t* value, const Param& p) {
            if (p.kind == Kind::Bool) *value = 0;
        });
        if (m_stopAiming.ok()) m_stopAiming.Call(controller, [](std::uint8_t*, const Param&) {});
        static_cast<AActor*>(controller)->SetActorTickEnabled(false); // no AI decisions, aiming or movement input
        m_overrideInvulnerable.Call(pawn, [](std::uint8_t* value, const Param& p) {
            if (p.kind == Kind::Bool) *value = 1;
        });
        if (m_setMovementMode.ok() && m_movementComponent.ok())
            if (UObject* movement = m_movementComponent.Object(pawn))
                m_setMovementMode.Call(movement, [this](std::uint8_t* value, const Param& p) {
                    if (p.kind == Kind::UInt8) *value = p.name == "NewMovementMode" ? static_cast<std::uint8_t>(m_options.moveMode) : 0;
                });
    }

    void GhostDemo::DriveAvatar(Ghost& ghost, const Sample& s)
    {
        UObject* pawn = ghost.pawn.Get();
        if (!pawn) return;
        KeepInert(ghost);
        if (m_options.driveWithUpdate && m_updateClientLocAndRot.ok())
            m_updateClientLocAndRot.Call(pawn, [&](std::uint8_t* value, const Param& p) {
                if (p.kind == Kind::Vector) WriteFloats(value, p, s.x, s.y, s.z);
                else if (p.kind == Kind::Rotator) WriteFloats(value, p, 0, s.yaw, 0); // body yaw only
                else if (p.kind == Kind::Bool) *value = 1;                             // bPlayAnim
            });
        else
        {
            FHitResult hit{};
            static_cast<AActor*>(pawn)->K2_SetActorLocationAndRotation(FVector(s.x, s.y, s.z), FRotator(0, s.yaw, 0), false, hit, true);
        }
        // Velocity drives the run/walk/jump blend in the animation blueprint.
        if (m_movementComponent.ok())
            if (UObject* movement = m_movementComponent.Object(pawn))
                if (auto* velocity = movement->GetValuePtrByPropertyNameInChain<float>(STR("Velocity")))
                {
                    velocity[0] = static_cast<float>(s.vx);
                    velocity[1] = static_cast<float>(s.vy);
                    velocity[2] = static_cast<float>(s.vz);
                }
        if (s.crouch != ghost.crouching && m_startCrouching.ok() && m_startUncrouch.ok())
        {
            ghost.crouching = s.crouch;
            (s.crouch ? m_startCrouching : m_startUncrouch).Call(pawn, [](std::uint8_t*, const Param&) {});
        }
    }

    void GhostDemo::RemoveAvatar(Ghost& ghost)
    {
        UObject* controller = ghost.controller.Get();
        UObject* pawn = ghost.pawn.Get();
        if (controller)
        {
            m_removeSelf.Call(controller, [](std::uint8_t*, const Param&) {});
            m_ownControllers.erase(controller);
        }
        else if (pawn) static_cast<AActor*>(pawn)->K2_DestroyActor();
        if (controller || pawn) m_avatarMapDirty = true;
        ghost.controller = FWeakObjectPtr{};
        ghost.pawn = FWeakObjectPtr{};
    }

    // --- shapes fallback --------------------------------------------------

    UObject* GhostDemo::SpawnShape(UObject* world, UObject* mesh, double sx, double sy, double sz)
    {
        if (!world || !mesh) return nullptr;
        const FTransform transform{FQuat(FRotator(0, 0, 0)), FVector(0, 0, -100000), FVector(sx, sy, sz)};
        AActor* actor = UGameplayStatics::BeginDeferredActorSpawnFromClass(world, m_meshActorClass, transform,
                                                                            ESpawnActorCollisionHandlingMethod::AlwaysSpawn);
        if (!actor) return nullptr;
        if (UObject* component = m_meshComponent.Object(actor))
        {
            m_setMobility.Call(component, [](std::uint8_t* value, const Param& p) {
                if (p.kind == Kind::UInt8) *value = 2; // EComponentMobility::Movable
            });
            m_setCollision.Call(component, [](std::uint8_t* value, const Param& p) {
                if (p.kind == Kind::UInt8) *value = 0; // ECollisionEnabled::NoCollision
            });
            m_setStaticMesh.Call(component, [mesh](std::uint8_t* value, const Param& p) {
                if (p.kind == Kind::Object) std::memcpy(value, &mesh, sizeof(mesh));
            });
            if (m_setCastShadow.ok())
                m_setCastShadow.Call(component, [](std::uint8_t* value, const Param& p) {
                    if (p.kind == Kind::Bool) *value = 0;
                });
        }
        UGameplayStatics::FinishSpawningActor(actor, transform);
        actor->SetActorEnableCollision(false);
        return actor;
    }

    bool GhostDemo::EnsureShapes(Ghost& ghost, UObject* world)
    {
        if (m_shapesFailed) return false;
        if (ghost.world == world && ghost.body.Get() && ghost.head.Get() && ghost.visor.Get()) return true;
        DestroyShapes(ghost);
        if (!m_cylinder) m_cylinder = LoadMesh(STR("/Engine/BasicShapes/Cylinder.Cylinder"));
        if (!m_sphere) m_sphere = LoadMesh(STR("/Engine/BasicShapes/Sphere.Sphere"));
        if (!m_cube) m_cube = LoadMesh(STR("/Engine/BasicShapes/Cube.Cube"));
        if (!m_cylinder || !m_sphere || !m_cube)
        {
            m_log("ghost demo: engine basic shapes not found; shapes disabled");
            m_shapesFailed = true;
            return false;
        }
        const auto l = bridge::ghost::Layout(Sample{}); // sizes are reset from the remote pose every frame
        ghost.body = SpawnShape(world, m_cylinder, l.body.sx, l.body.sy, l.body.sz);
        ghost.head = SpawnShape(world, m_sphere, l.head.sx, l.head.sy, l.head.sz);
        ghost.visor = SpawnShape(world, m_cube, l.visor.sx, l.visor.sy, l.visor.sz);
        ghost.world = world;
        return ghost.body.Get() && ghost.head.Get() && ghost.visor.Get();
    }

    void GhostDemo::DestroyShapes(Ghost& ghost)
    {
        for (FWeakObjectPtr* part : {&ghost.body, &ghost.head, &ghost.visor})
        {
            if (UObject* actor = part->Get()) static_cast<AActor*>(actor)->K2_DestroyActor();
            *part = FWeakObjectPtr{};
        }
        ghost.world = nullptr;
    }

    void GhostDemo::PlaceShapes(Ghost& ghost, const Sample& s)
    {
        // Only remote values: location (with Z), yaw, crouch and the sender's capsule height.
        const auto l = bridge::ghost::Layout(s);
        FHitResult hit{};
        auto place = [&](FWeakObjectPtr& part, const bridge::ghost::ShapePart& p) {
            if (UObject* a = part.Get())
            {
                auto* actor = static_cast<AActor*>(a);
                actor->SetActorScale3D(FVector(p.sx, p.sy, p.sz));
                actor->K2_SetActorLocationAndRotation(FVector(p.x, p.y, p.z), FRotator(0, s.yaw, 0), false, hit, true);
            }
        };
        place(ghost.body, l.body);
        place(ghost.head, l.head);
        place(ghost.visor, l.visor);
    }
    // --- per peer ---------------------------------------------------------

    void GhostDemo::Remove(Ghost& ghost)
    {
        RemoveAvatar(ghost);
        DestroyShapes(ghost);
    }

    void GhostDemo::Show(std::uint64_t peer, Ghost& ghost, const Sample& s, UObject* world, UObject* character)
    {
        if (EnsureAvatar(peer, ghost, character))
        {
            DestroyShapes(ghost);
            // Appearance: a character profile the host put in lobby data (generated scenarios ship it).
            // A lobby-wide avatar bot change needs a new spawn.
            if (const std::string bot = m_bridge.LobbyValue("aimmod.avatar_bot"); !bot.empty() && !ghost.spawnedFrom.empty() && bot != ghost.spawnedFrom && peer != TestPeer)
            {
                m_log("avatars: lobby avatar bot is now \"" + bot + "\"; respawning");
                RemoveAvatar(ghost);
                return;
            }
            std::string wanted = peer == TestPeer ? m_bridge.DevAvatarState().profile : m_bridge.LobbyValue("aimmod.char." + std::to_string(peer));
            if (wanted.empty() && peer != TestPeer) wanted = m_bridge.LobbyValue("aimmod.avatar_char");
            if (!wanted.empty() && wanted != ghost.characterProfile && m_loadCharacterProfile.ok())
            {
                ghost.characterProfile = wanted;
                if (UObject* pawn = ghost.pawn.Get())
                    m_loadCharacterProfile.Call(pawn, [&](std::uint8_t* value, const Param& p) {
                        if (p.kind == Kind::String) game::WriteString(value, wanted);
                    });
                m_log("avatars: " + bridge::Redact(peer) + " uses character profile \"" + wanted + "\"");
            }
            ApplyCombatState(ghost, character);
            DriveAvatar(ghost, s);
            return;
        }
        if (ghost.pawn.Get() == nullptr && ghost.spawnFailures < MaxSpawnFailures && m_avatarsBound) return; // spawn pending
        if (EnsureShapes(ghost, world)) PlaceShapes(ghost, s);
    }

    void GhostDemo::Tick()
    {
        try
        {
            if (!Bind()) return;
            UObject* controller = nullptr;
            UObject* character = LocalCharacter(controller);
            if (!character)
            {
                for (auto& [_, g] : m_ghosts) DestroyShapes(g); // avatars die with the world
                return;
            }
            const double now = bridge::Bridge::Now();

            // Local pose: the possessed pawn's actor location (capsule centre, Z included),
            // velocity, view rotation, crouch and current capsule half-height.
            double location[3]{}, velocity[3]{}, rotation[3]{};
            UObject* camera = m_cameraManager.Object(controller);
            if (m_actorLocation.Vector(character, location) && camera && m_cameraRotation.Vector(camera, rotation))
            {
                m_velocity.Vector(character, velocity);
                bridge::Pose pose;
                pose.x = static_cast<float>(location[0]);
                pose.y = static_cast<float>(location[1]);
                pose.z = static_cast<float>(location[2]);
                pose.pitch = static_cast<float>(rotation[0]);
                pose.yaw = static_cast<float>(rotation[1]);
                pose.vx = static_cast<float>(velocity[0]);
                pose.vy = static_cast<float>(velocity[1]);
                pose.vz = static_cast<float>(velocity[2]);
                bool crouch = false;
                if (m_isCrouching.ok())
                    if (auto c = m_isCrouching.Bool(character); c && *c) crouch = true;
                if (crouch) pose.flags |= bridge::PoseFlagCrouch;
                if (UObject* capsule = m_capsule.ok() ? m_capsule.Object(character) : nullptr)
                    if (auto h = m_capsuleHalfHeight.Number(capsule); h && *h > 5 && *h < 1000)
                    {
                        pose.halfHeight = static_cast<float>(*h);
                        pose.flags |= bridge::PoseFlagHalfHeight;
                    }
                m_bridge.SubmitLocalPose(pose);
                if (now >= m_nextDiagnostic && !m_bridge.Ghosts().empty())
                {
                    m_nextDiagnostic = now + 10;
                    char line[160];
                    std::snprintf(line, sizeof(line), "pose: z=%.1f vz=%.1f half=%.1f crouch=%d", pose.z, pose.vz, pose.halfHeight, crouch ? 1 : 0);
                    m_log(line);
                }
                // Spectate feed: only while someone watches us.
                if (m_bridge.CameraWanted())
                {
                    double eye[3]{};
                    if (m_cameraLocation.Vector(camera, eye))
                    {
                        bridge::CameraFrame frame;
                        frame.x = static_cast<float>(eye[0]);
                        frame.y = static_cast<float>(eye[1]);
                        frame.z = static_cast<float>(eye[2]);
                        frame.pitch = static_cast<float>(rotation[0]);
                        frame.yaw = static_cast<float>(rotation[1]);
                        frame.roll = static_cast<float>(rotation[2]);
                        const auto fov = m_cameraFov.ok() ? m_cameraFov.Number(camera) : std::nullopt;
                        frame.fov = fov && *fov > 1 && *fov < 179 ? static_cast<float>(*fov) : 90.f;
                        m_bridge.SubmitLocalCamera(frame);
                    }
                }
            }
            const auto dev = m_bridge.DevAvatarState();
            if (!m_options.showRemote && !dev.on) return;
            if (now >= m_nextStateRead)
            {
                m_nextStateRead = now + 0.2;
                ReadAvatarState();
            }
            // Scenario changed in the same world: the game may have reset or re-profiled our bots.
            if (const std::string scene = m_bridge.LocalScene(); scene != m_lastScene)
            {
                if (!m_lastScene.empty() && !m_ghosts.empty()) m_log("avatars: scenario changed; re-applying looks and AI-off");
                m_lastScene = scene;
                m_avatarMapDirty = true;
                for (auto& [_, g] : m_ghosts)
                {
                    g.nextInert = 0;
                    g.characterProfile.clear();
                    g.spawnFailures = 0;
                }
            }

            UObject* world = static_cast<AActor*>(character)->GetWorld();
            std::map<std::uint64_t, bool> seen;

            // Offline check: one avatar circling 4 m around the player, or following a
            // recorded path (avatar-test-path.tsv) on the scenario it was recorded in.
            if (m_options.avatarTest || dev.on)
            {
                if (dev.generation != m_devGeneration)
                {
                    // A new developer-menu command: restart and re-read the path the service just wrote.
                    m_devGeneration = dev.generation;
                    m_testPathTried = false;
                    m_testPath.reset();
                    m_testPathStart = -1;
                    m_testStart = -1;
                }
                if (m_testStart < 0)
                {
                    m_testStart = now;
                    m_log(dev.on ? "avatars: developer test avatar active" : "avatars: offline test active (avatar_test=1)");
                }
                const bool wantPath = dev.on ? dev.path : true;
                if (wantPath && !m_testPathTried) LoadTestPath();
                if (!wantPath) m_testPath.reset();
                // Eye height above the capsule centre, so the recorded camera becomes a standing body.
                double eye[3]{};
                if (camera && m_cameraLocation.Vector(camera, eye) && eye[2] - location[2] > 0 && eye[2] - location[2] < 300) m_eyeAboveCentre = eye[2] - location[2];
                const std::string scene = m_bridge.LocalScene();
                const bool pathHere = m_testPath && (m_testPath->scenario.empty() || scene.empty() || scene == m_testPath->scenario);
                if (m_testPath && !pathHere && m_testPathScene != scene)
                {
                    m_testPathScene = scene;
                    m_log("avatars: recorded path is for \"" + m_testPath->scenario + "\", not this scenario; circling instead");
                }
                Sample s;
                if (pathHere)
                {
                    if (m_testPathStart < 0)
                    {
                        m_testPathStart = now;
                        m_log("avatars: following the recorded path (" + std::to_string(m_testPath->rows.size()) + " rows, " + std::to_string(static_cast<int>(m_testPath->Duration())) + " s, looping)");
                    }
                    s = m_testPath->At(now - m_testPathStart, m_eyeAboveCentre);
                }
                else
                {
                    m_testPathStart = -1;
                    const double t = now - m_testStart, w = 0.6, r = 400;
                    s.x = location[0] + std::cos(t * w) * r;
                    s.y = location[1] + std::sin(t * w) * r;
                    s.z = location[2] + (std::fmod(t, 6.0) < 0.6 ? std::sin(std::fmod(t, 6.0) / 0.6 * 3.14159265358979) * 60 : 0); // a hop every 6 s
                    s.vx = -std::sin(t * w) * r * w;
                    s.vy = std::cos(t * w) * r * w;
                    s.yaw = std::atan2(s.vy, s.vx) * 180.0 / 3.14159265358979;
                    s.crouch = std::fmod(t, 10.0) > 7.0;
                    s.halfHeight = s.crouch ? bridge::ghost::DefaultHalfHeight * 0.6 : bridge::ghost::DefaultHalfHeight;
                }
                seen[TestPeer] = true;
                Show(TestPeer, m_ghosts[TestPeer], s, world, character);
            }

            // Remote players: everything below comes from their samples only.
            const std::string localScene = m_bridge.LocalScene();
            for (const auto& peer : m_bridge.Ghosts())
            {
                if (peer.samples.empty()) continue;
                seen[peer.peer] = true;
                Ghost& ghost = m_ghosts[peer.peer];
                const std::string& scene = peer.samples.back().pose.scene;
                if (!scene.empty() && !localScene.empty() && scene != localScene)
                {
                    if (ghost.hiddenScene != scene) m_log("ghost demo: peer " + bridge::Redact(peer.peer) + " on \"" + scene + "\"; not shown");
                    ghost.hiddenScene = scene;
                    Remove(ghost);
                    continue;
                }
                if (!ghost.hiddenScene.empty()) m_log("ghost demo: peer " + bridge::Redact(peer.peer) + " is on this scenario");
                ghost.hiddenScene.clear();
                std::vector<bridge::ghost::TimedPose> samples;
                samples.reserve(peer.samples.size());
                for (const auto& sample : peer.samples) samples.push_back({sample.time, sample.pose});
                Show(peer.peer, ghost, bridge::ghost::Sample(samples, now), world, character);
            }

            // Peers that left, disconnected or went quiet.
            for (auto it = m_ghosts.begin(); it != m_ghosts.end();)
            {
                if (seen.count(it->first)) ++it;
                else
                {
                    Remove(it->second);
                    m_log("ghost demo: removed " + bridge::Redact(it->first));
                    it = m_ghosts.erase(it);
                }
            }
        }
        catch (const std::exception& e)
        {
            m_failed = true;
            m_log(std::string("ghost demo: disabled after an error: ") + e.what());
        }
        if (m_avatarMapDirty) WriteAvatarMap();
    }

    // avatar-state.tsv from the service (combat matches). Stale or missing = everyone alive, enemies.
    void GhostDemo::ReadAvatarState()
    {
        m_avatarState.reset();
        if (m_options.stateDir.empty()) return;
        const std::filesystem::path file = std::filesystem::path(m_options.stateDir) / L"avatar-state.tsv";
        WIN32_FILE_ATTRIBUTE_DATA info{};
        if (!GetFileAttributesExW(file.c_str(), GetFileExInfoStandard, &info)) return;
        FILETIME now{};
        GetSystemTimeAsFileTime(&now);
        const auto u64 = [](FILETIME ft) { return (static_cast<std::uint64_t>(ft.dwHighDateTime) << 32) | ft.dwLowDateTime; };
        if (u64(now) - u64(info.ftLastWriteTime) > 100'000'000ull) return; // older than 10 s: no match running
        std::ifstream in(file, std::ios::binary);
        std::string text((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
        m_avatarState = bridge::avatarstate::Parse(text);
    }

    void GhostDemo::ApplyCombatState(Ghost& ghost, UObject* localCharacter)
    {
        UObject* pawn = ghost.pawn.Get();
        if (!pawn) return;
        const bridge::avatarstate::PeerState* state = nullptr;
        if (m_avatarState)
            if (const auto it = m_avatarState->peers.find(ghost.peer); it != m_avatarState->peers.end()) state = &it->second;
        auto* actor = static_cast<AActor*>(pawn);

        // Death and respawn: purely visual on our own inert, invulnerable bot.
        const bool alive = !state || state->alive;
        if (!alive && !ghost.dead)
        {
            ghost.dead = true;
            if (m_options.nativeDeath && m_death.ok())
                m_death.Call(pawn, [](std::uint8_t* value, const Param& p) {
                    if (p.kind == Kind::Object) std::memset(value, 0, sizeof(void*)); // no killer: no kill credit
                });
            actor->SetActorHiddenInGame(true);
            actor->SetActorEnableCollision(false);
            m_log("avatars: " + bridge::Redact(ghost.peer) + " is down");
        }
        else if (alive && ghost.dead)
        {
            ghost.dead = false;
            if (m_options.nativeDeath && m_respawn.ok())
                m_respawn.Call(pawn, [](std::uint8_t* value, const Param& p) {
                    if (p.kind == Kind::Bool) *value = 0;
                });
            actor->SetActorHiddenInGame(false);
            actor->SetActorEnableCollision(true);
            ghost.nextInert = 0; // re-apply AI-off and invulnerability straight away
            m_log("avatars: " + bridge::Redact(ghost.peer) + " respawned");
        }

        // Teams: friends share the local player's team, everyone else is on the other one.
        if (m_setTeam.ok() && m_getTeam.ok())
        {
            const auto local = m_getTeam.Number(localCharacter);
            const int localTeam = local && *local >= 0 && *local < 16 ? static_cast<int>(*local) : 1;
            const int enemyTeam = ghost.spawnTeam != localTeam ? ghost.spawnTeam : (localTeam == 1 ? 2 : 1);
            const int want = state && state->friendly ? localTeam : enemyTeam;
            if (want != ghost.team)
            {
                ghost.team = want;
                m_setTeam.Call(pawn, [want](std::uint8_t* value, const Param& p) {
                    if (p.kind == Kind::Int32) std::memcpy(value, &want, sizeof(want));
                });
            }
        }

        // Health bar (the avatar stays invulnerable; never set it to 0 while alive).
        if (state && alive && state->health >= 0 && m_setHealth.ok())
        {
            const double health = std::clamp(state->health, 1.0, 100000.0);
            if (std::fabs(health - ghost.health) > 0.5)
            {
                ghost.health = health;
                m_setHealth.Call(pawn, [health](std::uint8_t* value, const Param& p) {
                    if (p.kind == Kind::Float)
                    {
                        const float v = static_cast<float>(health);
                        std::memcpy(value, &v, sizeof(v));
                    }
                });
            }
        }
    }
    // avatars.tsv for AimModCore: which actor is which remote player's stream.
    void GhostDemo::WriteAvatarMap()
    {
        m_avatarMapDirty = false;
        if (m_options.stateDir.empty()) return;
        std::string text = "AIMMOD_AVATARS_1\n";
        int lines = 0;
        for (const auto& [peer, g] : m_ghosts)
        {
            UObject* pawn = g.pawn.Get();
            if (!pawn || lines >= 64) continue;
            const std::string name = game::Narrow(std::wstring(pawn->GetName()));
            if (name.empty() || name.find_first_of("\t\r\n") != std::string::npos) continue;
            text += name + "\t" + bridge::posefile::StreamIdFor(peer) + "\n";
            ++lines;
        }
        const std::filesystem::path file = std::filesystem::path(m_options.stateDir) / L"avatars.tsv";
        const std::wstring temp = file.wstring() + L".tmp";
        {
            std::ofstream out(temp, std::ios::binary | std::ios::trunc);
            if (!out) return;
            out << text;
        }
        MoveFileExW(temp.c_str(), file.c_str(), MOVEFILE_REPLACE_EXISTING);
    }

    // Reads the recorded path once per session. A missing file is normal (circle test).
    bool GhostDemo::LoadTestPath()
    {
        m_testPathTried = true;
        if (m_options.avatarTestPath.empty()) return false;
        std::error_code ec;
        const auto size = std::filesystem::file_size(m_options.avatarTestPath, ec);
        if (ec)
        {
            m_log("avatars: no recorded path (avatar-test-path.tsv); circling");
            return false;
        }
        if (size > 4 * 1024 * 1024)
        {
            m_log("avatars: recorded path is too large; circling");
            return false;
        }
        std::ifstream file(m_options.avatarTestPath, std::ios::binary);
        const std::string text((std::istreambuf_iterator<char>(file)), std::istreambuf_iterator<char>());
        std::string error;
        m_testPath = bridge::ghost::AvatarPath::Parse(text, &error);
        if (!m_testPath)
        {
            m_log("avatars: recorded path unreadable (" + error + "); circling");
            return false;
        }
        m_log("avatars: loaded a recorded path for \"" + m_testPath->scenario + "\" (" + std::to_string(m_testPath->rows.size()) + " rows)");
        return true;
    }

    void GhostDemo::Shutdown()
    {
        try
        {
            for (auto& [_, g] : m_ghosts) Remove(g);
        }
        catch (...)
        {
        }
        m_ghosts.clear();
        WriteAvatarMap(); // header only: no avatars
    }
} // namespace aimmod

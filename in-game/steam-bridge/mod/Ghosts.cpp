#include "Ghosts.hpp"

#include "PoseFile.hpp"

#include <Unreal/AActor.hpp>
#include <Unreal/CoreUObject/UObject/UnrealType.hpp>
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
#include <utility>
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
        if (named.empty() && m_botsAllowed) named = std::string(bridge::ghost::HelperBotProfile); // AimMod arenas always ship it
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

    // The avatar mesh's lowest point against the actor origin, measured from the mesh's world bounds,
    // so the feet (not a capsule of another size, or a mesh offset inside it) stand on the floor.
    void GhostDemo::MeasureFeet(Ghost& ghost, double floorZ)
    {
        const double now = bridge::Bridge::Now();
        if (now < ghost.nextFeetMeasure) return;
        ghost.nextFeetMeasure = now + 2.0;
        if (!m_feetBound)
        {
            m_feetBound = true;
            m_componentBounds.BindPath(STR("/Script/Engine.KismetSystemLibrary:GetComponentBounds"), Shape::Command);
            m_characterMesh.Bind(game::FindClass(STR("/Script/Engine.Character")), STR("Mesh"));
            if (!m_kismetDefault) m_kismetDefault = UObjectGlobals::StaticFindObject<UObject*>(nullptr, nullptr, STR("/Script/Engine.Default__KismetSystemLibrary"));
        }
        UObject* pawn = ghost.pawn.Get();
        UObject* mesh = pawn && m_characterMesh.ok() ? m_characterMesh.Object(pawn) : nullptr;
        double actor[3]{};
        if (!mesh || !m_componentBounds.ok() || !m_kismetDefault || !m_actorLocation.Vector(pawn, actor)) return;
        double origin[3]{}, extent[3]{};
        int vectors = 0;
        m_componentBounds.Call(
            m_kismetDefault, [&](std::uint8_t* value, const Param& p) { if (p.kind == Kind::Object) std::memcpy(value, &mesh, sizeof(mesh)); },
            [&](const std::uint8_t* buffer, const std::vector<Param>& params) {
                for (const Param& p : params)
                    if (p.out && p.kind == Kind::Vector && vectors < 2)
                    {
                        float f[3];
                        std::memcpy(f, buffer + p.offset, sizeof(f));
                        double* v = vectors++ == 0 ? origin : extent;
                        v[0] = f[0]; v[1] = f[1]; v[2] = f[2];
                    }
            });
        if (vectors < 2 || extent[2] <= 10 || extent[2] > 1000) return;
        const double feet = origin[2] - extent[2];
        const double above = actor[2] - feet;
        if (above <= 0 || above > 1000) return;
        ghost.feetToActor = above;
        if (!ghost.feetLogged)
        {
            ghost.feetLogged = true;
            char line[200];
            std::snprintf(line, sizeof(line), "avatars: %s feet at z=%.0f, floor z=%.0f, offset %.0f (origin %.0f above the feet)",
                          bridge::Redact(ghost.peer).c_str(), feet, floorZ, feet - floorZ, above);
            m_log(line);
        }
    }

    void GhostDemo::DriveAvatar(Ghost& ghost, const Sample& input)
    {
        UObject* pawn = ghost.pawn.Get();
        if (!pawn) return;
        KeepInert(ghost);
        // Feet on the floor: the avatar's own capsule on the sample's floor (GhostMath AvatarActorZ).
        Sample s = input;
        const double floorZ = input.z - input.halfHeight;
        MeasureFeet(ghost, floorZ);
        double avatarHalf = -1;
        if (UObject* capsule = m_capsule.ok() ? m_capsule.Object(pawn) : nullptr)
            if (auto h = m_capsuleHalfHeight.Number(capsule); h && *h > 20 && *h < 400) avatarHalf = *h;
        s.z = bridge::ghost::AvatarActorZ(input, avatarHalf, ghost.feetToActor);
        if (!ghost.placementLogged && (avatarHalf > 0 || ghost.feetToActor > 0))
        {
            ghost.placementLogged = true;
            char line[200];
            std::snprintf(line, sizeof(line), "avatars: %s stands on its capsule (half-height %.0f; mesh bounds say %.0f), floor z=%.0f",
                          bridge::Redact(ghost.peer).c_str(), avatarHalf, ghost.feetToActor, floorZ);
            m_log(line);
        }
        auto* actor = static_cast<AActor*>(pawn);
        if (m_options.driveWithUpdate && m_updateClientLocAndRot.ok())
            m_updateClientLocAndRot.Call(pawn, [&](std::uint8_t* value, const Param& p) {
                if (p.kind == Kind::Vector) WriteFloats(value, p, s.x, s.y, s.z);
                else if (p.kind == Kind::Rotator) WriteFloats(value, p, 0, s.yaw, 0); // body yaw only
                else if (p.kind == Kind::Bool) *value = 1;                             // bPlayAnim
            });
        // The drive sweeps: anything in the way (or a round's spawn across the map) leaves the body
        // short of its position. The body always ends where the sample says (GhostMath PlaceAfterDrive).
        double actual[3]{};
        const bool known = m_actorLocation.Vector(pawn, actual);
        const auto placement = !known ? bridge::ghost::Placement::Teleport : bridge::ghost::PlaceAfterDrive(actual, s.x, s.y, s.z);
        if (placement != bridge::ghost::Placement::Driven || !(m_options.driveWithUpdate && m_updateClientLocAndRot.ok()))
        {
            FHitResult hit{};
            actor->K2_SetActorLocationAndRotation(FVector(s.x, s.y, s.z), FRotator(0, s.yaw, 0), false, hit, placement == bridge::ghost::Placement::Teleport);
            if (known && placement != bridge::ghost::Placement::Driven && m_options.driveWithUpdate && ++ghost.corrections == 1)
            {
                char line[200];
                std::snprintf(line, sizeof(line), "avatars: %s was %.0f cm from its position after the drive (blocked); placed directly from now on",
                              bridge::Redact(ghost.peer).c_str(), std::hypot(std::hypot(actual[0] - s.x, actual[1] - s.y), actual[2] - s.z));
                m_log(line);
            }
        }
        ghost.shown = s;
        ghost.shownValid = true;
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
        auto mesh = [&](FWeakObjectPtr& cached, const wchar_t* path) {
            UObject* object = cached.Get();
            if (!object && (object = LoadMesh(path))) cached = object;
            return object;
        };
        UObject* cylinder = mesh(m_cylinder, STR("/Engine/BasicShapes/Cylinder.Cylinder"));
        UObject* sphere = mesh(m_sphere, STR("/Engine/BasicShapes/Sphere.Sphere"));
        UObject* cube = mesh(m_cube, STR("/Engine/BasicShapes/Cube.Cube"));
        if (!cylinder || !sphere || !cube)
        {
            m_log("ghost demo: engine basic shapes not found; shapes disabled");
            m_shapesFailed = true;
            return false;
        }
        const auto l = bridge::ghost::Layout(Sample{}); // sizes are reset from the remote pose every frame
        ghost.body = SpawnShape(world, cylinder, l.body.sx, l.body.sy, l.body.sz);
        ghost.head = SpawnShape(world, sphere, l.head.sx, l.head.sy, l.head.sz);
        ghost.visor = SpawnShape(world, cube, l.visor.sx, l.visor.sy, l.visor.sz);
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
        if (!m_botsAllowed)
        {
            // Never a game bot outside AimMod match scenarios: shapes only.
            RemoveAvatar(ghost);
            if (EnsureShapes(ghost, world)) PlaceShapes(ghost, s);
            return;
        }
        if (EnsureAvatar(peer, ghost, character))
        {
            DestroyShapes(ghost);
            // Appearance: a character profile the host put in lobby data (generated scenarios ship it).
            // A lobby-wide avatar bot change needs a new spawn.
            if (const std::string bot = m_bridge.LobbyValue("aimmod.avatar_bot"); !bot.empty() && !ghost.spawnedFrom.empty() && bot != ghost.spawnedFrom && !IsDevPeer(peer))
            {
                m_log("avatars: lobby avatar bot is now \"" + bot + "\"; respawning");
                RemoveAvatar(ghost);
                return;
            }
            std::string wanted = IsDevPeer(peer) ? DevLook(peer) : m_bridge.LobbyValue("aimmod.char." + std::to_string(peer));
            if (wanted.empty() && !IsDevPeer(peer)) wanted = m_bridge.LobbyValue("aimmod.avatar_char");
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
            if (now >= m_nextOrdersRead)
            {
                m_nextOrdersRead = now + 0.1;
                ReadBotOrders();
            }
            if (now >= m_nextMovementRead)
            {
                m_nextMovementRead = now + 1.0;
                ReadLocalMovement(character);
            }
            m_botReports.clear();
            // Scenario changed in the same world: the game may have reset or re-profiled our bots.
            if (const std::string scene = m_bridge.LocalScene(); scene != m_lastScene)
            {
                if (!m_lastScene.empty() && !m_ghosts.empty()) m_log("avatars: scenario changed; re-applying looks and AI-off");
                m_lastScene = scene;
                m_linkCache.reset(); // another map: which walks are clear is learnt again
                m_nav.reset();
                m_navSeeds.clear();
                m_navDoneLogged = false;
                m_navCoverageLogged = false;
                m_debugMarkers.clear(); // the markers went with the old world
                m_botsAllowed = bridge::ghost::AvatarBotsAllowed(scene);
                m_parkedHelpers.clear();
                m_nextHelperPark = 0;
                if (!m_ghosts.empty() && !m_botsAllowed) m_log("avatars: not an AimMod match scenario; remote players drawn as shapes");
                m_avatarMapDirty = true;
                for (auto& [_, g] : m_ghosts)
                {
                    g.nextInert = 0;
                    g.characterProfile.clear();
                    g.spawnFailures = 0;
                }
            }

            // The arena's own helper bot: re-applied every second (the game resets bots at run start).
            if (m_botsAllowed && now >= m_nextHelperPark)
            {
                m_nextHelperPark = now + 1.0;
                ParkHelperBots();
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
                if (dev.walk && !dev.walkers.empty())
                {
                    // Several simulated lobby players: each walks between its side's spawns, feet on the floor.
                    if (now >= m_nextTraceLog && m_floorTraces > 0)
                    {
                        m_nextTraceLog = now + 30.0;
                        m_log("avatars: walker traces: floor " + std::to_string(m_floorHits) + "/" + std::to_string(m_floorTraces) + " found, walls " +
                              std::to_string(m_wallHits) + "/" + std::to_string(m_wallTraces) + " blocked");
                    }
                    GrowNav(dev.walkers, character, now);
                    for (const auto& w : dev.walkers)
                    {
                        DevWalk& walk = m_walkers[w.peer];
                        if (walk.walker.spawns != w.spawns || walk.walker.own != w.own)
                        {
                            walk.walker = bridge::ghost::Walker{};
                            walk.walker.spawns = w.spawns;
                            walk.walker.own = w.own;
                            walk.placeToken.clear();
                            walk.walker.seed ^= static_cast<std::uint32_t>(w.peer * 2654435761u);
                            walk.at = -1;
                            m_log("avatars: simulated player " + std::to_string(w.peer) + " walks between " + std::to_string(w.spawns.size()) + " spawns");
                        }
                        // One map, one record of which straight walks are clear (Walker LinkCache).
                        if (!m_linkCache) m_linkCache = std::make_shared<bridge::ghost::LinkCache>();
                        walk.walker.links = m_linkCache;
                        walk.walker.nav = m_nav;
                        // Move like the local player does on this map (a ported map is scaled up).
                        if (m_runSpeed != walk.tunedSpeed || m_stepHeight != walk.tunedStep)
                        {
                            walk.tunedSpeed = m_runSpeed;
                            walk.tunedStep = m_stepHeight;
                            walk.walker.Tune(m_runSpeed, m_stepHeight);
                            if (!walk.tuneLogged && m_runSpeed > 0)
                            {
                                walk.tuneLogged = true;
                                m_log("avatars: bot " + std::to_string(w.peer) + " walks at " + std::to_string(static_cast<int>(walk.walker.speed)) + " cm/s, steps up " +
                                      std::to_string(static_cast<int>(walk.walker.stepUp)) + " cm (the local player runs " + std::to_string(static_cast<int>(m_runSpeed)) + ")");
                            }
                        }
                        UObject* pawn = m_ghosts[w.peer].pawn.Get();
                        const auto floor = [&](double fx, double fy, double fz) -> std::optional<double> {
                            const double a[3]{fx, fy, fz}, b[3]{fx, fy, fz - 3000};
                            const auto hit = Trace(character, a, b, pawn, character);
                            ++m_floorTraces;
                            if (hit) ++m_floorHits;
                            return hit ? std::optional<double>((*hit)[2]) : std::nullopt;
                        };
                        const auto clear = [&](double ax, double ay, double az, double bx, double by, double bz) {
                            const double a[3]{ax, ay, az}, b[3]{bx, by, bz};
                            const bool blocked = Trace(character, a, b, pawn, character).has_value();
                            ++m_wallTraces;
                            if (blocked) ++m_wallHits;
                            return !blocked;
                        };
                        const bool wasPlaced = walk.walker.placed;
                        // The avatar's own capsule half-height, so its feet (not a default body) touch the floor.
                        double half = bridge::ghost::DefaultHalfHeight;
                        if (UObject* capsule = pawn && m_capsule.ok() ? m_capsule.Object(pawn) : nullptr)
                            if (auto h = m_capsuleHalfHeight.Number(capsule); h && *h > 20 && *h < 400) half = *h;
                        // Bot orders: a client draws the host's bot where the host has it; the host's bots
                        // walk to a goal, hold still, face an enemy and stand at their round's spawn.
                        const bridge::bots::Order* order = nullptr;
                        if (m_botOrders)
                            if (const auto it = m_botOrders->bots.find(w.peer); it != m_botOrders->bots.end()) order = &it->second;
                        if (order && order->mode == bridge::bots::Order::Mode::Pose)
                        {
                            Sample target;
                            target.x = order->pose[0];
                            target.y = order->pose[1];
                            target.z = order->pose[2];
                            target.yaw = order->pose[3];
                            target.halfHeight = half;
                            const double dt = walk.shownAt < 0 ? 0 : std::clamp(now - walk.shownAt, 0.0, 0.2);
                            walk.shownAt = now;
                            Sample ps = target;
                            if (walk.shown && std::hypot(target.x - walk.shown->x, target.y - walk.shown->y) < 300 && std::fabs(target.z - walk.shown->z) < 200 && dt > 0)
                            {
                                // Smooth toward the host's position (it arrives 10 times a second).
                                const double u = std::min(1.0, dt * 12);
                                ps.x = walk.shown->x + (target.x - walk.shown->x) * u;
                                ps.y = walk.shown->y + (target.y - walk.shown->y) * u;
                                ps.z = walk.shown->z + (target.z - walk.shown->z) * u;
                                ps.yaw = bridge::ghost::LerpAngle(walk.shown->yaw, target.yaw, u);
                                ps.vx = (ps.x - walk.shown->x) / dt;
                                ps.vy = (ps.y - walk.shown->y) / dt;
                            }
                            walk.shown = ps;
                            if (!walk.poseLogged)
                            {
                                walk.poseLogged = true;
                                m_log("avatars: bot " + std::to_string(w.peer) + " follows the host's position");
                            }
                            seen[w.peer] = true;
                            Show(w.peer, m_ghosts[w.peer], ps, world, character);
                            continue;
                        }
                        // A client's bot before the host's first position: nothing to draw yet.
                        if (walk.walker.spawns.empty())
                        {
                            seen[w.peer] = true;
                            continue;
                        }
                        walk.walker.goal = order && order->mode == bridge::bots::Order::Mode::Goal ? order->goal : std::nullopt;
                        walk.walker.hold = order && order->mode == bridge::bots::Order::Mode::Hold;
                        walk.walker.face = order ? order->face : std::nullopt;
                        walk.walker.goalStop = order ? order->stop : 1;
                        walk.walker.via = order ? order->via : std::nullopt;
                        walk.walker.fight = order ? order->fight : 0;
                        walk.walker.counterStrafe = order && order->counterStrafe;
                        if (order && order->turn > 0) walk.walker.turnRate = order->turn;
                        // How it moves: gait and stance, pre-aiming, eased aim, peeks, zones to keep out of.
                        walk.walker.walkGait = order && order->gait == bridge::bots::Order::Gait::Walk;
                        walk.walker.crouchStance = order && order->crouch;
                        walk.walker.preaim = order && order->preaim;
                        walk.walker.aimAccel = order ? order->aimAccel : 0;
                        walk.walker.overshoot = order && order->aimAccel > 0 ? order->aimOvershoot : bridge::ghost::Walker::Overshoot;
                        using Peek = bridge::ghost::Walker::Peek;
                        const auto peekMode = order ? order->peek : bridge::bots::Order::Peek::None;
                        walk.walker.peek = peekMode == bridge::bots::Order::Peek::Jiggle ? Peek::Jiggle
                                           : peekMode == bridge::bots::Order::Peek::Wide ? Peek::Wide
                                           : peekMode == bridge::bots::Order::Peek::Crouch ? Peek::Crouch
                                                                                            : Peek::None;
                        if (order) walk.walker.peekAt = order->peekAt;
                        if (order) walk.walker.avoid = order->avoid;
                        else walk.walker.avoid.clear();
                        // Everyone else's body: the other bots, the remote players and the local player.
                        walk.walker.others.clear();
                        for (const auto& [otherPeer, other] : m_walkers)
                            if (otherPeer != w.peer && other.walker.placed) walk.walker.others.push_back({other.walker.x, other.walker.y});
                        for (const auto& [ghostPeer, g] : m_ghosts)
                            if (!IsDevPeer(ghostPeer) && g.shownValid && !g.dead) walk.walker.others.push_back({g.shown.x, g.shown.y});
                        walk.walker.others.push_back({location[0], location[1]});
                        if (order && !order->placeToken.empty() && order->placeToken != walk.placeToken)
                        {
                            walk.placeToken = order->placeToken;
                            walk.walker.PlaceAt(order->placeAt[0], order->placeAt[1], order->placeAt[2], order->placeAt[3], half, floor);
                            m_ghosts[w.peer].respawned = false;
                            if (!walk.placedLogged)
                            {
                                walk.placedLogged = true;
                                m_log("avatars: bot " + std::to_string(w.peer) + " stands at its spawn z=" + std::to_string(static_cast<int>(walk.walker.z)) +
                                      (walk.walker.grounded ? " on the traced floor" : " (no floor found yet)"));
                            }
                        }
                        if (std::exchange(m_ghosts[w.peer].respawned, false)) walk.walker.PlaceRandom(half, floor);
                        const double dt = walk.at < 0 ? 0 : now - walk.at;
                        walk.at = now;
                        const Sample ws = walk.walker.Step(now, dt, half, floor, clear);
                        // Sight: is the line from the bot's eye to each target clear? (A hit close to the
                        // target is the target's own body: in sight.)
                        if (order && now >= walk.nextSight)
                        {
                            walk.nextSight = now + 0.15;
                            walk.seen.clear();
                            const double eye[3]{ws.x, ws.y, ws.z + ws.halfHeight * 0.73};
                            for (const auto& t : order->sight)
                            {
                                const double to[3]{t.at[0], t.at[1], t.at[2]};
                                const auto hit = Trace(character, eye, to, pawn, character);
                                const bool visible = !hit || std::hypot(std::hypot((*hit)[0] - to[0], (*hit)[1] - to[1]), (*hit)[2] - to[2]) < 60;
                                walk.seen.push_back({t.tag, visible});
                            }
                        }
                        // The floor under it as well (the walker's own z is on the traced floor): a bomb it drops lands there.
                        // Its speed and stance too: the service fires once it has stopped (a counter-strafe).
                        if (order) m_botReports.push_back({w.peer, ws.x, ws.y, ws.z, ws.yaw, walk.walker.z - half, walk.seen, std::hypot(ws.vx, ws.vy), ws.crouch});
                        if (m_botOrders && m_botOrders->debug) DrawBotDebug(w.peer, walk, world, now);
                        // Every 5 s: what each bot is told and how far along it is.
                        if (order && now >= walk.nextStatusLog)
                        {
                            walk.nextStatusLog = now + (m_botOrders && m_botOrders->debug ? 1.0 : 5.0);
                            const auto& b = walk.walker;
                            const char* mode = order->mode == bridge::bots::Order::Mode::Goal ? "goal" : order->mode == bridge::bots::Order::Mode::Hold ? "hold" : "roam";
                            char line[320];
                            if (b.goal)
                                std::snprintf(line, sizeof(line), "avatars: bot %llu %s (%.0f, %.0f, %.0f), %.0f cm away; at (%.0f, %.0f, %.0f) %s; path %zu points, at %zu%s; plans %d found, %d partial, %d none; %d blocked",
                                              static_cast<unsigned long long>(w.peer), mode, (*b.goal)[0], (*b.goal)[1], (*b.goal)[2], std::hypot((*b.goal)[0] - b.x, (*b.goal)[1] - b.y), b.x, b.y, b.z,
                                              b.target == bridge::ghost::Walker::NavTarget ? "walking" : b.target == -1 ? "standing" : "walking to a waypoint", b.navPath.size(), b.navIndex,
                                              b.navReached ? "" : " (not to the goal yet)", b.plansFound, b.plansPending, b.plansFailed, b.navBlocked);
                            else
                                std::snprintf(line, sizeof(line), "avatars: bot %llu %s at (%.0f, %.0f, %.0f)%s", static_cast<unsigned long long>(w.peer), mode, b.x, b.y, b.z, order->face ? ", facing an enemy" : "");
                            // How it moves: gait, stance, peek, fight style, zones it keeps out of, speed and turning.
                            const char* peekName = b.peek == bridge::ghost::Walker::Peek::Jiggle ? "jiggle" : b.peek == bridge::ghost::Walker::Peek::Wide ? "wide" : b.peek == bridge::ghost::Walker::Peek::Crouch ? "crouch" : "none";
                            char moves[200];
                            std::snprintf(moves, sizeof(moves), "; %s, %s%s; peek %s; fight %.2f %s; avoid %zu; %.0f cm/s, yaw turning %.0f deg/s", b.walkGait ? "walk" : "run", b.crouchStance ? "crouched" : "standing",
                                          b.preaim ? ", pre-aiming" : "", peekName, b.fight, b.counterStrafe ? "counter" : "ad", b.avoid.size(), std::hypot(b.velX, b.velY), b.yawVel);
                            m_log(std::string(line) + moves);
                        }
                        if (!wasPlaced && walk.walker.placed)
                            m_log("avatars: simulated player " + std::to_string(w.peer) + " placed at spawn " + std::to_string(walk.walker.at) + " z=" +
                                  std::to_string(static_cast<int>(walk.walker.z)) + (walk.walker.grounded ? " on the traced floor" : " (no floor found yet: waiting there)"));
                        seen[w.peer] = true;
                        Show(w.peer, m_ghosts[w.peer], ws, world, character);
                    }
                }
                else if (dev.walk && !dev.spawns.empty())
                {
                    // A simulated lobby player: walks between the arena's spawns, feet on the floor.
                    if (m_walker.spawns != dev.spawns)
                    {
                        m_walker = bridge::ghost::Walker{};
                        m_walker.spawns = dev.spawns;
                        m_walkAt = -1;
                        m_log("avatars: simulated player walks between " + std::to_string(dev.spawns.size()) + " spawns");
                    }
                    UObject* pawn = m_ghosts[TestPeer].pawn.Get();
                    const auto floor = [&](double fx, double fy, double fz) -> std::optional<double> {
                        const double a[3]{fx, fy, fz}, b[3]{fx, fy, fz - 3000};
                        const auto hit = Trace(character, a, b, pawn, character);
                        return hit ? std::optional<double>((*hit)[2]) : std::nullopt;
                    };
                    const auto clear = [&](double ax, double ay, double az, double bx, double by, double bz) {
                        const double a[3]{ax, ay, az}, b[3]{bx, by, bz};
                        return !Trace(character, a, b, pawn, character).has_value();
                    };
                    if (std::exchange(m_ghosts[TestPeer].respawned, false)) m_walker.PlaceRandom(bridge::ghost::DefaultHalfHeight, floor);
                    const double dt = m_walkAt < 0 ? 0 : now - m_walkAt;
                    m_walkAt = now;
                    s = m_walker.Step(now, dt, bridge::ghost::DefaultHalfHeight, floor, clear);
                }
                else if (pathHere)
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
                if (!(dev.walk && !dev.walkers.empty()))
                {
                    seen[TestPeer] = true;
                    Show(TestPeer, m_ghosts[TestPeer], s, world, character);
                }
            }

            if (!(m_botOrders && m_botOrders->debug) && !m_debugMarkers.empty()) ClearBotDebug();

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

            // Dead in a CS round: the camera follows a living player's avatar (spectate-view.tsv).
            if (now >= m_nextViewRead)
            {
                m_nextViewRead = now + 0.2;
                ReadSpectateView();
            }
            TickSpectateView(controller, character);

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
        WriteBotSight();
    }

    // bot-orders.tsv from the service. Older than 3 s: no orders (the bots walk on their own).
    void GhostDemo::ReadBotOrders()
    {
        if (m_options.stateDir.empty()) return;
        const std::filesystem::path file = std::filesystem::path(m_options.stateDir) / L"bot-orders.tsv";
        WIN32_FILE_ATTRIBUTE_DATA info{};
        if (!GetFileAttributesExW(file.c_str(), GetFileExInfoStandard, &info) || info.nFileSizeLow > 64 * 1024 || info.nFileSizeHigh != 0)
        {
            m_botOrders.reset();
            return;
        }
        FILETIME now{};
        GetSystemTimeAsFileTime(&now);
        const auto u64 = [](FILETIME ft) { return (static_cast<std::uint64_t>(ft.dwHighDateTime) << 32) | ft.dwLowDateTime; };
        if (u64(now) - u64(info.ftLastWriteTime) > 30'000'000ull)
        {
            m_botOrders.reset();
            return;
        }
        std::ifstream in(file, std::ios::binary);
        std::string text((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
        m_botOrders = bridge::bots::Parse(text);
        if (m_botOrders && !m_botOrders->bots.empty() && !m_botOrdersLogged)
        {
            m_botOrdersLogged = true;
            m_log("avatars: bot orders for " + std::to_string(m_botOrders->bots.size()) + " bots (the service steers them; sight by line traces)");
        }
    }

    // What the bot avatars report: where each is and which targets it sees (10 times a second).
    void GhostDemo::WriteBotSight()
    {
        const auto reports = std::move(m_botReports);
        m_botReports.clear();
        if (m_options.stateDir.empty() || reports.empty()) return;
        const double now = bridge::Bridge::Now();
        if (now < m_nextSightWrite) return;
        m_nextSightWrite = now + 0.1;
        FILETIME ft{};
        GetSystemTimeAsFileTime(&ft);
        const std::uint64_t ticks = (static_cast<std::uint64_t>(ft.dwHighDateTime) << 32) | ft.dwLowDateTime;
        const auto unixMs = static_cast<std::int64_t>((ticks - 116444736000000000ull) / 10000ull);
        const std::filesystem::path file = std::filesystem::path(m_options.stateDir) / L"bot-sight.tsv";
        const std::wstring temp = file.wstring() + L".tmp";
        {
            std::ofstream out(temp, std::ios::binary | std::ios::trunc);
            if (!out) return;
            out << bridge::bots::Format(unixMs, reports);
        }
        MoveFileExW(temp.c_str(), file.c_str(), MOVEFILE_REPLACE_EXISTING);
    }

    // The local player's run speed and step height (CharacterMovement), so bots move like a player
    // on this map: a ported map is scaled up and the walker's defaults would crawl.
    void GhostDemo::ReadLocalMovement(UObject* character)
    {
        const bool bound = BindAvatars() && m_movementComponent.ok();
        UObject* movement = bound ? m_movementComponent.Object(character) : nullptr;
        const auto read = [](UObject* m, double& run, double& up) {
            const auto* walk = m ? m->GetValuePtrByPropertyNameInChain<float>(STR("MaxWalkSpeed")) : nullptr;
            const auto* step = m ? m->GetValuePtrByPropertyNameInChain<float>(STR("MaxStepHeight")) : nullptr;
            run = walk && std::isfinite(*walk) && *walk > 50 && *walk < 5000 ? *walk : -1;
            up = step && std::isfinite(*step) && *step > 5 && *step < 300 ? *step : -1;
        };
        double run = -1, up = -1;
        read(movement, run, up);
        const char* from = "the local player";
        // Not readable there: an avatar's own (the AimMod profile runs like a KovaaK's player).
        if (run <= 0 || up <= 0)
            for (auto& [_, g] : m_ghosts)
                if (UObject* pawn = g.pawn.Get())
                {
                    double r2 = -1, u2 = -1;
                    read(bound ? m_movementComponent.Object(pawn) : nullptr, r2, u2);
                    if (run <= 0 && r2 > 0) run = r2, from = "an avatar";
                    if (up <= 0 && u2 > 0) up = u2;
                    if (run > 0 && up > 0) break;
                }
        const bool readable = run > 0;
        // Neither readable: the avatars' own profile (Avatars.cs: MaxSpeed 1100, StepUpHeight 75).
        if (run <= 0) run = 1100;
        if (up <= 0) up = 75;
        m_runSpeed = run;
        m_stepHeight = up;
        if (!m_movementLogged && !m_walkers.empty())
        {
            m_movementLogged = true;
            m_log("avatars: movement for the bots from " + std::string(readable ? from : "the avatar profile (not readable in game)") + ": run " + std::to_string(static_cast<int>(run)) + " cm/s, step " +
                  std::to_string(static_cast<int>(up)) + " cm");
        }
    }

    // Bot debug overlay: a cube on its goal, a sphere where it looks (an enemy, the peeked angle, the
    // corner it pre-aims), one on each zone it keeps out of (up to 4), and the rest of the 25 small
    // spheres along its path (from where it is), moved four times a second; unused markers wait far
    // below the map.
    void GhostDemo::DrawBotDebug(std::uint64_t peer, const DevWalk& walk, UObject* world, double now)
    {
        (void)now;
        if (m_shapesFailed || !world) return;
        auto& markers = m_debugMarkers[peer];
        constexpr std::size_t Count = 25;
        UObject* sphere = m_sphere.Get();
        if (!sphere && (sphere = LoadMesh(STR("/Engine/BasicShapes/Sphere.Sphere")))) m_sphere = sphere;
        UObject* cube = m_cube.Get();
        if (!cube && (cube = LoadMesh(STR("/Engine/BasicShapes/Cube.Cube")))) m_cube = cube;
        if (!sphere || !cube) return;
        while (markers.size() < Count)
        {
            UObject* actor = SpawnShape(world, markers.empty() ? cube : sphere, markers.empty() ? 0.6 : 0.25, markers.empty() ? 0.6 : 0.25, markers.empty() ? 0.6 : 0.25);
            if (!actor) return;
            markers.push_back(actor);
        }
        const auto& b = walk.walker;
        std::vector<std::array<double, 3>> points;
        if (b.goal) points.push_back(*b.goal);
        else points.push_back({0, 0, -100000});
        if (b.lookAt) points.push_back(*b.lookAt);
        else points.push_back({0, 0, -100000});
        for (std::size_t i = 0; i < b.avoid.size() && i < 4; ++i) points.push_back({b.avoid[i][0], b.avoid[i][1], b.avoid[i][2]});
        for (std::size_t i = b.navIndex; i < b.navPath.size() && points.size() < Count; ++i) points.push_back({b.navPath[i][0], b.navPath[i][1], b.navPath[i][2] + 40});
        for (std::size_t i = 0; i < markers.size(); ++i)
            if (UObject* actor = markers[i].Get())
            {
                const auto p = i < points.size() ? points[i] : std::array<double, 3>{0, 0, -100000};
                FHitResult hit{};
                static_cast<AActor*>(actor)->K2_SetActorLocationAndRotation(FVector(p[0], p[1], p[2]), FRotator(0, 0, 0), false, hit, true);
            }
    }

    void GhostDemo::ClearBotDebug()
    {
        for (auto& [_, markers] : m_debugMarkers)
            for (auto& m : markers)
                if (UObject* actor = m.Get()) static_cast<AActor*>(actor)->K2_DestroyActor();
        m_debugMarkers.clear();
    }

    // The bots' nav grid: seeded at every walker's spawns and waypoints and at their goals, grown a
    // trace budget per tick (about 1500 traces: a few seconds for a whole ported map, during freeze time).
    void GhostDemo::GrowNav(const std::vector<bridge::Bridge::DevAvatar::Walker>& walkers, UObject* character, double now)
    {
        if (!m_botOrders || m_botOrders->bots.empty()) return; // only bots need it
        if (!m_nav)
        {
            // The bodies' size first (the grid's step and waist checks are theirs): wait for an avatar.
            double half = -1;
            for (auto& [_, g] : m_ghosts)
                if (UObject* capsule = g.pawn.Get() && m_capsule.ok() ? m_capsule.Object(g.pawn.Get()) : nullptr)
                    if (auto h = m_capsuleHalfHeight.Number(capsule); h && *h > 20 && *h < 400) { half = *h; break; }
            if (half < 0) return;
            m_nav = std::make_shared<bridge::ghost::NavGrid>();
            bridge::ghost::Walker tuned;
            tuned.Tune(m_runSpeed, m_stepHeight);
            m_nav->stepUp = tuned.stepUp;
            m_nav->stepDown = tuned.stepDown;
            m_nav->halfHeight = half;
            m_nav->spacing = 120;
            m_navDoneLogged = false;
            m_log("avatars: building the bots' nav grid (" + std::to_string(static_cast<int>(m_nav->spacing)) + " cm, step " + std::to_string(static_cast<int>(m_nav->stepUp)) + " cm)");
        }
        const auto floor = [&](double fx, double fy, double fz) -> std::optional<double> {
            const double a[3]{fx, fy, fz}, b[3]{fx, fy, fz - 3000};
            const auto hit = Trace(character, a, b, character, nullptr);
            return hit ? std::optional<double>((*hit)[2]) : std::nullopt;
        };
        const auto clear = [&](double ax, double ay, double az, double bx, double by, double bz) {
            const double a[3]{ax, ay, az}, b[3]{bx, by, bz};
            return !Trace(character, a, b, character, nullptr).has_value();
        };
        const auto seed = [&](const std::array<double, 3>& p) {
            char key[64];
            std::snprintf(key, sizeof(key), "%.0f,%.0f,%.0f", p[0], p[1], p[2]);
            if (m_navSeeds.insert(key).second) m_nav->Seed(p[0], p[1], p[2], floor);
        };
        for (const auto& w : walkers)
            for (const auto& s : w.spawns) seed(s);
        for (const auto& [_, o] : m_botOrders->bots)
            if (o.goal) seed(*o.goal);
        if (!m_nav->Done()) m_nav->Grow(1500, floor, clear);
        // Coverage, once the grid is complete: from each walker's own spawn, which of its waypoints and
        // goals (the bomb sites, the other spawns) the grid connects; the ones it doesn't are named.
        if (m_nav->Done() && !m_navCoverageLogged)
        {
            m_navCoverageLogged = true;
            int pairs = 0, linked = 0;
            std::string missing;
            for (const auto& w : walkers)
            {
                if (w.spawns.empty()) continue;
                std::vector<std::array<double, 3>> goals(w.spawns.begin() + std::min<std::size_t>(w.spawns.size(), static_cast<std::size_t>(std::max(1, w.own))), w.spawns.end());
                if (const auto it = m_botOrders->bots.find(w.peer); it != m_botOrders->bots.end() && it->second.goal) goals.push_back(*it->second.goal);
                for (const auto& g : goals)
                {
                    bool reached = false;
                    m_nav->PathNodes(w.spawns.front(), g, reached);
                    ++pairs;
                    if (reached) ++linked;
                    else if (missing.size() < 240)
                    {
                        char item[64];
                        std::snprintf(item, sizeof(item), "%s(%.0f, %.0f, %.0f) from bot %llu", missing.empty() ? "" : "; ", g[0], g[1], g[2], static_cast<unsigned long long>(w.peer));
                        missing += item;
                    }
                }
            }
            char line[200];
            std::snprintf(line, sizeof(line), "avatars: nav coverage %d/%d spawn-to-waypoint pairs connected (%.0f%%), %zu grid points", linked, pairs, pairs ? 100.0 * linked / pairs : 100.0,
                          m_nav->nodes.size());
            m_log(std::string(line) + (missing.empty() ? "" : "; unreachable: " + missing));
        }
        if (!m_navDoneLogged && (m_nav->Done() || now >= m_nextNavLog))
        {
            m_nextNavLog = now + 5.0;
            if (m_nav->Done()) m_navDoneLogged = true;
            m_log("avatars: nav grid " + std::to_string(m_nav->nodes.size()) + " points" + (m_nav->Done() ? " (done)" : " (growing, " + std::to_string(m_nav->frontier.size()) + " to check)") + ", " +
                  std::to_string(m_nav->traces) + " traces");
        }
    }

    // spectate-view.tsv (service -> AimModSteam, BotOrders.hpp bridge::view): the peer to watch.
    void GhostDemo::ReadSpectateView()
    {
        m_viewWanted.reset();
        if (m_options.stateDir.empty()) return;
        const std::filesystem::path file = std::filesystem::path(m_options.stateDir) / L"spectate-view.tsv";
        WIN32_FILE_ATTRIBUTE_DATA info{};
        if (!GetFileAttributesExW(file.c_str(), GetFileExInfoStandard, &info) || info.nFileSizeLow > 4096 || info.nFileSizeHigh != 0) return;
        std::ifstream in(file, std::ios::binary);
        std::string text((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
        FILETIME ft{};
        GetSystemTimeAsFileTime(&ft);
        const std::uint64_t ticks = (static_cast<std::uint64_t>(ft.dwHighDateTime) << 32) | ft.dwLowDateTime;
        const auto unixMs = static_cast<std::int64_t>((ticks - 116444736000000000ull) / 10000ull);
        m_viewWanted = bridge::view::Parse(text, unixMs);
    }

    void GhostDemo::StopSpectateView(UObject* controller, UObject* character)
    {
        if (m_viewing && controller && character && m_setViewTarget.ok())
            m_setViewTarget.Call(controller, [&](std::uint8_t* value, const Param& p) {
                if (p.kind == Kind::Object) std::memcpy(value, &character, sizeof(character));
            });
        if (m_viewing) m_log("avatars: spectator camera off; the view is yours again");
        m_viewing = false;
        m_spectatePeer = 0;
        if (UObject* camera = m_spectateCamera.Get()) static_cast<AActor*>(camera)->K2_DestroyActor();
        m_spectateCamera = FWeakObjectPtr{};
    }

    // A chase camera behind the watched avatar, at its eye height, pulled in where a wall is closer.
    // It follows the avatar as drawn here, so bots and players alike are watched without a pose
    // stream from their machine.
    void GhostDemo::TickSpectateView(UObject* controller, UObject* character)
    {
        const auto it = m_viewWanted ? m_ghosts.find(*m_viewWanted) : m_ghosts.end();
        UObject* pawn = it != m_ghosts.end() ? it->second.pawn.Get() : nullptr;
        if (!controller || !character || !pawn || !it->second.shownValid || it->second.dead)
        {
            StopSpectateView(controller, character);
            return;
        }
        if (!m_viewBound)
        {
            m_viewBound = true;
            m_setViewTarget.BindPath(STR("/Script/Engine.PlayerController:SetViewTargetWithBlend"), Shape::Command);
            m_cameraSetFov.BindPath(STR("/Script/Engine.CameraComponent:SetFieldOfView"), Shape::Command);
            m_cameraActorClass = game::FindClass(STR("/Script/Engine.CameraActor"));
            if (m_cameraActorClass) m_cameraComponent.Bind(m_cameraActorClass, STR("CameraComponent"));
            if (!m_setViewTarget.ok() || !m_cameraActorClass) m_log("avatars: spectator camera unavailable (" + m_setViewTarget.error() + ")");
        }
        if (!m_setViewTarget.ok() || !m_cameraActorClass) return;
        const Ghost& ghost = it->second;
        double half = ghost.shown.halfHeight;
        if (UObject* capsule = m_capsule.ok() ? m_capsule.Object(pawn) : nullptr)
            if (auto h = m_capsuleHalfHeight.Number(capsule); h && *h > 20 && *h < 400) half = *h;
        const auto view = bridge::ghost::ChaseCamera(ghost.shown, half);
        // Pulled in to the first wall between the eye and the camera.
        double camera[3]{view.camera[0], view.camera[1], view.camera[2]};
        if (const auto hit = Trace(character, view.eye.data(), camera, pawn, character))
        {
            const double dx = view.eye[0] - (*hit)[0], dy = view.eye[1] - (*hit)[1], dz = view.eye[2] - (*hit)[2];
            const double d = std::max(1.0, std::hypot(std::hypot(dx, dy), dz));
            const double back = std::min(20.0, d);
            camera[0] = (*hit)[0] + dx / d * back;
            camera[1] = (*hit)[1] + dy / d * back;
            camera[2] = (*hit)[2] + dz / d * back;
        }
        UObject* actor = m_spectateCamera.Get();
        if (!actor)
        {
            UObject* world = static_cast<AActor*>(character)->GetWorld();
            const FTransform transform{FQuat(FRotator(0, 0, 0)), FVector(camera[0], camera[1], camera[2]), FVector(1, 1, 1)};
            AActor* spawned = world ? UGameplayStatics::BeginDeferredActorSpawnFromClass(world, m_cameraActorClass, transform, ESpawnActorCollisionHandlingMethod::AlwaysSpawn) : nullptr;
            if (!spawned) return;
            UGameplayStatics::FinishSpawningActor(spawned, transform);
            spawned->SetActorEnableCollision(false);
            m_spectateCamera = spawned;
            actor = spawned;
            m_viewing = false;
            if (m_cameraSetFov.ok() && m_cameraComponent.ok())
                if (UObject* component = m_cameraComponent.Object(actor))
                    if (UObject* manager = m_cameraManager.Object(controller))
                    {
                        const auto fov = m_cameraFov.ok() ? m_cameraFov.Number(manager) : std::nullopt;
                        const float value = fov && *fov > 1 && *fov < 179 ? static_cast<float>(*fov) : 90.f;
                        m_cameraSetFov.Call(component, [value](std::uint8_t* v, const Param& p) {
                            if (p.kind == Kind::Float) std::memcpy(v, &value, sizeof(value));
                        });
                    }
        }
        FHitResult hit{};
        static_cast<AActor*>(actor)->K2_SetActorLocationAndRotation(FVector(camera[0], camera[1], camera[2]), FRotator(view.pitch, view.yaw, 0), false, hit, true);
        // Switched to (or the game took the view back, e.g. its own death camera): view through it.
        const double now = bridge::Bridge::Now();
        if (!m_viewing || m_spectatePeer != *m_viewWanted || now >= m_nextViewApply)
        {
            m_nextViewApply = now + 1.0;
            m_setViewTarget.Call(controller, [&](std::uint8_t* value, const Param& p) {
                if (p.kind == Kind::Object) std::memcpy(value, &actor, sizeof(actor));
            });
            if (m_spectatePeer != *m_viewWanted) m_log("avatars: spectating " + bridge::Redact(*m_viewWanted));
            m_viewing = true;
            m_spectatePeer = *m_viewWanted;
        }
    }

    // The look of a developer stand-in: its walker's profile, else the test avatar's.
    std::string GhostDemo::DevLook(std::uint64_t peer)
    {
        const auto dev = m_bridge.DevAvatarState();
        for (const auto& w : dev.walkers)
            if (w.peer == peer && !w.profile.empty()) return w.profile;
        return dev.profile;
    }

    std::optional<std::array<double, 3>> GhostDemo::Trace(UObject* context, const double a[3], const double b[3], UObject* ignore1, UObject* ignore2)
    {
        if (!m_traceBound)
        {
            m_traceBound = true;
            // By object type (the map's static and dynamic geometry), whatever their Visibility response:
            // map-creator pieces don't all block the Visibility channel. The channel trace is the fallback.
            m_traceForObjects = m_lineTrace.BindPath(STR("/Script/Engine.KismetSystemLibrary:LineTraceSingleForObjects"), Shape::Command);
            m_lineTraceChannel.BindPath(STR("/Script/Engine.KismetSystemLibrary:LineTraceSingle"), Shape::Command);
            if (!m_traceForObjects) m_lineTrace = m_lineTraceChannel;
            m_kismetDefault = UObjectGlobals::StaticFindObject<UObject*>(nullptr, nullptr, STR("/Script/Engine.Default__KismetSystemLibrary"));
            for (const Param& p : m_lineTrace.params())
                if (p.out && p.structType && p.name == "OutHit")
                    for (FProperty* member : p.structType->ForEachProperty())
                    {
                        if (member->GetName() == STR("ImpactPoint")) m_hitImpactOffset = member->GetOffset_Internal();
                        if (member->GetName() == STR("bStartPenetrating")) m_hitStartPenetrating = CastField<FBoolProperty>(member);
                    }
            m_log(m_lineTrace.ok() && m_kismetDefault && m_hitImpactOffset >= 0 ? std::string("avatars: line traces ready (") + (m_traceForObjects ? "world static and dynamic objects" : "visibility channel") + "; simulated player walks on the floor)"
                                                                               : "avatars: line traces unavailable (" + m_lineTrace.error() + "); the simulated player stays on its spawn height");
        }
        if (!m_lineTrace.ok() || !m_kismetDefault || m_hitImpactOffset < 0 || !context) return std::nullopt;
        TArray<AActor*> ignore;
        if (ignore1) ignore.Add(static_cast<AActor*>(ignore1));
        if (ignore2) ignore.Add(static_cast<AActor*>(ignore2));
        // Every object type but pawns (2): KovaaK's map pieces may use a custom object channel.
        TArray<std::uint8_t> objectTypes;
        for (std::uint8_t t = 0; t < 32; ++t)
            if (t != 2) objectTypes.Add(t);
        // One trace with `fn`; nullopt without a hit or when it starts inside geometry.
        const auto run = [&](const game::Getter& fn) -> std::optional<std::array<double, 3>> {
            bool hit = false, startedInside = false;
            std::array<double, 3> point{};
            int vectors = 0;
            fn.Call(
                m_kismetDefault,
                [&](std::uint8_t* value, const Param& p) {
                    if (p.worldContext) std::memcpy(value, &context, sizeof(context));
                    else if (p.kind == Kind::Vector && vectors < 2)
                    {
                        const double* v = vectors++ == 0 ? a : b;
                        WriteFloats(value, p, v[0], v[1], v[2]);
                    }
                    else if (p.kind == Kind::Array && p.name == "ActorsToIgnore") std::memcpy(value, &ignore, sizeof(ignore));
                    else if (p.kind == Kind::Array && p.name == "ObjectTypes") std::memcpy(value, &objectTypes, sizeof(objectTypes));
                    else if (p.kind == Kind::Bool && p.boolProperty) p.boolProperty->SetPropertyValue(value, p.name == "bIgnoreSelf");
                    // TraceChannel 0 = Visibility, DrawDebugType 0 = none, colours and DrawTime zero.
                },
                [&](const std::uint8_t* buffer, const std::vector<Param>& params) {
                    for (const Param& p : params)
                    {
                        if (p.ret && p.boolProperty) hit = p.boolProperty->GetPropertyValue(const_cast<std::uint8_t*>(buffer) + p.offset);
                        else if (p.out && p.name == "OutHit")
                        {
                            float f[3];
                            std::memcpy(f, buffer + p.offset + m_hitImpactOffset, sizeof(f));
                            point = {f[0], f[1], f[2]};
                            if (m_hitStartPenetrating && m_hitStartPenetrating->GetPropertyValue(const_cast<std::uint8_t*>(buffer) + p.offset + m_hitStartPenetrating->GetOffset_Internal()))
                                startedInside = true;
                        }
                    }
                });
            if (!hit || startedInside) return std::nullopt;
            return point;
        };
        // By object type first; what that misses, the Visibility channel may still see.
        auto found = run(m_lineTrace);
        if (!found && m_traceForObjects && m_lineTraceChannel.ok()) found = run(m_lineTraceChannel);
        // The parameter copies of the lists are plain memory; `ignore` and `objectTypes` free their buffers.
        ++m_traceCount;
        if (found) ++m_traceHits;
        return found;
    }

    // The scenario's own instance of the helper bot (not one of our avatars): hidden, no
    // collision (shots and players pass), AI off, invulnerable, no movement, and parked far
    // outside the map, so it can't be seen, hit, block anyone or count for accuracy.
    void GhostDemo::ParkHelperBots()
    {
        if (!BindAvatars()) return;
        std::vector<UObject*> found;
        UObjectGlobals::FindAllOf(STR("TheMetaAIController"), found);
        for (UObject* controller : found)
        {
            if (!game::IsLiveInstance(controller) || m_ownControllers.count(controller)) continue;
            // In an AimMod arena the helper bot is the only bot of the scenario's own; anything else is left
            // over from the previous scenario in the same world (bots are re-used across loads) and would
            // take shots and count for KovaaK's accuracy.
            const std::string botProfile = ReadFString(controller, STR("MyProfileName"));
            const bool helper = bridge::ghost::IsHelperBot(botProfile);
            UObject* pawn = m_getMetaCharacter.Object(controller);
            if (!pawn || !game::IsLiveInstance(pawn)) continue;
            auto* actor = static_cast<AActor*>(pawn);
            actor->SetActorHiddenInGame(true);
            actor->SetActorEnableCollision(false);
            static_cast<AActor*>(controller)->SetActorTickEnabled(false);
            m_setUseWeapons.Call(controller, [](std::uint8_t* value, const Param& p) {
                if (p.kind == Kind::Bool) *value = 0;
            });
            if (m_stopAiming.ok()) m_stopAiming.Call(controller, [](std::uint8_t*, const Param&) {});
            m_overrideInvulnerable.Call(pawn, [](std::uint8_t* value, const Param& p) {
                if (p.kind == Kind::Bool) *value = 1;
            });
            if (m_setMovementMode.ok() && m_movementComponent.ok())
                if (UObject* movement = m_movementComponent.Object(pawn))
                    m_setMovementMode.Call(movement, [](std::uint8_t* value, const Param& p) {
                        if (p.kind == Kind::UInt8) *value = 0; // MOVE_None: no gravity, stays parked
                    });
            FHitResult hit{};
            actor->K2_SetActorLocationAndRotation(FVector(bridge::ghost::HelperParkX, bridge::ghost::HelperParkY, bridge::ghost::HelperParkZ), FRotator(0, 0, 0), false, hit, true);
            if (m_parkedHelpers.insert(pawn).second)
                m_log(helper ? std::string("avatars: parked the arena's helper bot outside the map (hidden, no collision, AI off)")
                             : "avatars: parked a leftover bot \"" + botProfile + "\" from the previous scenario (hidden, no collision, AI off)");
        }
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
            ghost.respawned = true;
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

        // CS: what they hold (avatar-state.tsv's weapon column); nothing while down or outside CS.
        ApplyWeapon(ghost, pawn, state && alive ? state->weapon : std::string());
    }

    void GhostDemo::ApplyWeapon(Ghost& ghost, UObject* pawn, const std::string& model)
    {
        const double now = static_cast<double>(GetTickCount64()) / 1000.0;
        if (model == ghost.weapon && (model.empty() || now < ghost.nextWeapon)) return;
        ghost.nextWeapon = now + 1.0;
        UClass* cls = pawn->GetClassPrivate();
        if (cls != m_weaponClass)
        {
            m_weaponClass = cls;
            m_thirdPersonPrimary.Reset();
            m_thirdPersonPrimary.BindName(cls, STR("GetThirdPersonWeaponMeshComponent_Primary"), Shape::Object);
            m_setVisibility.BindPath(STR("/Script/Engine.SceneComponent:SetVisibility"), Shape::Command);
            m_setHiddenInGame.BindPath(STR("/Script/Engine.SceneComponent:SetHiddenInGame"), Shape::Command);
            if (!m_thirdPersonPrimary.ok()) m_log("avatars: no third-person weapon on " + game::ClassName(pawn) + "; held weapons not shown");
        }
        UObject* component = m_thirdPersonPrimary.ok() ? m_thirdPersonPrimary.Object(pawn) : nullptr;
        const bool changed = model != ghost.weapon;
        ghost.weapon = model;
        if (!component || !game::IsLiveInstance(component)) return;
        if (!model.empty() && m_setStaticMesh.ok())
        {
            const std::wstring path = bridge::avatarstate::ThirdPersonMesh(model);
            if (UObject* mesh = path.empty() ? nullptr : LoadMesh(path.c_str()))
                m_setStaticMesh.Call(component, [mesh](std::uint8_t* value, const Param& p) {
                    if (p.kind == Kind::Object) std::memcpy(value, &mesh, sizeof(mesh));
                });
        }
        const bool show = !model.empty();
        m_setVisibility.Call(component, [show](std::uint8_t* value, const Param& p) {
            if (p.kind == Kind::Bool && p.boolProperty) p.boolProperty->SetPropertyValue(value, p.name == "bNewVisibility" ? show : false);
        });
        m_setHiddenInGame.Call(component, [show](std::uint8_t* value, const Param& p) {
            if (p.kind == Kind::Bool && p.boolProperty) p.boolProperty->SetPropertyValue(value, p.name == "NewHidden" ? !show : false);
        });
        if (changed) m_log("avatars: " + bridge::Redact(ghost.peer) + (show ? " holds " + model : " holds nothing to show"));
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

#include "Ghosts.hpp"

#include <Unreal/AActor.hpp>
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

#include <cmath>
#include <cstring>
#include <exception>
#include <vector>

namespace aimmod
{
    using namespace RC::Unreal;
    using game::Kind;
    using game::Param;
    using game::Shape;

    namespace
    {
        constexpr double InterpolationDelay = 0.1; // seconds behind the newest sample
        constexpr double MaxExtrapolation = 0.1;
        constexpr int MaxSpawnFailures = 3;          // then this peer falls back to shapes
        constexpr std::uint64_t TestPeer = 1;         // synthetic peer for avatar_test

        double WrapAngle(double a)
        {
            while (a > 180) a -= 360;
            while (a < -180) a += 360;
            return a;
        }
        double LerpAngle(double a, double b, double t) { return a + WrapAngle(b - a) * t; }

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
        UObject* character = controller ? m_myCharacter.Object(controller) : nullptr;
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
        if (!m_options.avatarProfile.empty()) return m_options.avatarProfile;
        std::vector<UObject*> found;
        UObjectGlobals::FindAllOf(STR("TheMetaAIController"), found);
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
        const double d = m_radius * 2 / 100.0; // basic shapes are 100 units
        ghost.body = SpawnShape(world, m_cylinder, d, d, m_halfHeight * 1.6 / 100.0);
        ghost.head = SpawnShape(world, m_sphere, d * 0.9, d * 0.9, d * 0.9);
        ghost.visor = SpawnShape(world, m_cube, d * 0.35, d * 0.7, d * 0.2);
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
        FHitResult hit{};
        const double rad = s.yaw * 3.14159265358979 / 180.0;
        const double headZ = s.z + m_halfHeight * 0.85;
        if (UObject* a = ghost.body.Get())
            static_cast<AActor*>(a)->K2_SetActorLocationAndRotation(FVector(s.x, s.y, s.z - m_halfHeight * 0.1), FRotator(0, s.yaw, 0), false, hit, true);
        if (UObject* a = ghost.head.Get()) static_cast<AActor*>(a)->K2_SetActorLocationAndRotation(FVector(s.x, s.y, headZ), FRotator(0, s.yaw, 0), false, hit, true);
        if (UObject* a = ghost.visor.Get())
            static_cast<AActor*>(a)->K2_SetActorLocationAndRotation(FVector(s.x + std::cos(rad) * m_radius, s.y + std::sin(rad) * m_radius, headZ),
                                                                    FRotator(0, s.yaw, 0), false, hit, true);
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
            const std::string wanted = m_bridge.LobbyValue("aimmod.char." + std::to_string(peer));
            if (!wanted.empty() && wanted != ghost.characterProfile && m_loadCharacterProfile.ok())
            {
                ghost.characterProfile = wanted;
                if (UObject* pawn = ghost.pawn.Get())
                    m_loadCharacterProfile.Call(pawn, [&](std::uint8_t* value, const Param& p) {
                        if (p.kind == Kind::String) game::WriteString(value, wanted);
                    });
                m_log("avatars: " + bridge::Redact(peer) + " uses character profile \"" + wanted + "\"");
            }
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
            // Local pose.
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
                if (m_isCrouching.ok())
                    if (auto crouch = m_isCrouching.Bool(character); crouch && *crouch) pose.flags |= 1;
                m_bridge.SubmitLocalPose(pose);
            }
            if (UObject* capsule = m_capsule.ok() ? m_capsule.Object(character) : nullptr)
            {
                if (auto h = m_capsuleHalfHeight.Number(capsule); h && *h > 10 && *h < 1000) m_halfHeight = *h;
                if (auto r = m_capsuleRadius.Number(capsule); r && *r > 5 && *r < 500) m_radius = *r;
            }

            UObject* world = static_cast<AActor*>(character)->GetWorld();
            const double now = bridge::Bridge::Now();
            std::map<std::uint64_t, bool> seen;

            // Offline check: one avatar circling 4 m around the player.
            if (m_options.avatarTest)
            {
                if (m_testStart < 0)
                {
                    m_testStart = now;
                    m_log("avatars: offline test active (avatar_test=1)");
                }
                const double t = now - m_testStart, w = 0.6, r = 400;
                Sample s{location[0] + std::cos(t * w) * r, location[1] + std::sin(t * w) * r, location[2], 0, 0, 0, 0, 0,
                         std::fmod(t, 10.0) > 7.0};
                s.vx = -std::sin(t * w) * r * w;
                s.vy = std::cos(t * w) * r * w;
                s.yaw = std::atan2(s.vy, s.vx) * 180.0 / 3.14159265358979;
                seen[TestPeer] = true;
                Show(TestPeer, m_ghosts[TestPeer], s, world, character);
            }

            // Remote players.
            const std::string localScene = m_bridge.LocalScene();
            const double renderTime = now - InterpolationDelay;
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

                // Interpolate ~100 ms behind the newest sample.
                const auto& v = peer.samples;
                Sample s{};
                const auto from = [&](const bridge::Pose& p) {
                    return Sample{p.x, p.y, p.z, p.yaw, p.pitch, p.vx, p.vy, p.vz, (p.flags & 1) != 0};
                };
                if (renderTime <= v.front().time) s = from(v.front().pose);
                else if (renderTime >= v.back().time)
                {
                    s = from(v.back().pose);
                    const double dt = std::min(renderTime - v.back().time, MaxExtrapolation);
                    s.x += s.vx * dt;
                    s.y += s.vy * dt;
                    s.z += s.vz * dt;
                }
                else
                {
                    std::size_t i = 1;
                    while (i < v.size() && v[i].time < renderTime) ++i;
                    const Sample a = from(v[i - 1].pose), b = from(v[i].pose);
                    const double span = v[i].time - v[i - 1].time;
                    const double t = span > 0 ? (renderTime - v[i - 1].time) / span : 1;
                    s = b;
                    s.x = a.x + (b.x - a.x) * t;
                    s.y = a.y + (b.y - a.y) * t;
                    s.z = a.z + (b.z - a.z) * t;
                    s.yaw = LerpAngle(a.yaw, b.yaw, t);
                    s.vx = a.vx + (b.vx - a.vx) * t;
                    s.vy = a.vy + (b.vy - a.vy) * t;
                    s.vz = a.vz + (b.vz - a.vz) * t;
                }
                Show(peer.peer, ghost, s, world, character);
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
    }
} // namespace aimmod

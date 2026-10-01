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
#include <exception>
#include <vector>

namespace aimmod
{
    using namespace RC::Unreal;
    using game::Shape;

    namespace
    {
        constexpr double InterpolationDelay = 0.1; // seconds behind the newest sample
        constexpr double MaxExtrapolation = 0.1;

        double WrapAngle(double a)
        {
            while (a > 180) a -= 360;
            while (a < -180) a += 360;
            return a;
        }
        double LerpAngle(double a, double b, double t) { return a + WrapAngle(b - a) * t; }
    } // namespace

    GhostDemo::GhostDemo(bridge::Bridge& bridge, bridge::LogFn log) : m_bridge(bridge), m_log(std::move(log)) {}

    bool GhostDemo::Bind()
    {
        if (m_bound) return true;
        if (m_failed) return false;
        bool ok = true;
        ok &= m_actorLocation.BindPath(STR("/Script/Engine.Actor:K2_GetActorLocation"), Shape::Vector);
        ok &= m_velocity.BindPath(STR("/Script/Engine.Actor:GetVelocity"), Shape::Vector);
        ok &= m_cameraRotation.BindPath(STR("/Script/Engine.PlayerCameraManager:GetCameraRotation"), Shape::Vector);
        m_capsuleHalfHeight.BindPath(STR("/Script/Engine.CapsuleComponent:GetScaledCapsuleHalfHeight"), Shape::Number);
        m_capsuleRadius.BindPath(STR("/Script/Engine.CapsuleComponent:GetScaledCapsuleRadius"), Shape::Number);
        ok &= m_setStaticMesh.BindPath(STR("/Script/Engine.StaticMeshComponent:SetStaticMesh"), Shape::Command);
        ok &= m_setMobility.BindPath(STR("/Script/Engine.SceneComponent:SetMobility"), Shape::Command);
        ok &= m_setCollision.BindPath(STR("/Script/Engine.PrimitiveComponent:SetCollisionEnabled"), Shape::Command);
        m_setCastShadow.BindPath(STR("/Script/Engine.PrimitiveComponent:SetCastShadow"), Shape::Command);
        ok &= m_cameraManager.Bind(game::FindClass(STR("/Script/Engine.PlayerController")), STR("PlayerCameraManager"));
        ok &= m_myCharacter.Bind(game::FindClass(STR("/Script/GameSkillsTrainer.MetaPlayerController")), STR("MyCharacter"));
        m_capsule.Bind(game::FindClass(STR("/Script/Engine.Character")), STR("CapsuleComponent"));
        m_meshActorClass = game::FindClass(STR("/Script/Engine.StaticMeshActor"));
        ok &= m_meshActorClass != nullptr && m_meshComponent.Bind(m_meshActorClass, STR("StaticMeshComponent"));
        if (!ok)
        {
            m_failed = true;
            m_log("ghost demo: game bindings unavailable; ghosts disabled (" + m_setMobility.error() + m_setStaticMesh.error() + ")");
            return false;
        }
        m_bound = true;
        m_log("ghost demo: bindings ready");
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

    UObject* GhostDemo::SpawnShape(UObject* world, UObject* mesh, double sx, double sy, double sz)
    {
        if (!world || !mesh) return nullptr;
        // Far below the map until the first Place.
        const FTransform transform{FQuat(FRotator(0, 0, 0)), FVector(0, 0, -100000), FVector(sx, sy, sz)};
        AActor* actor = UGameplayStatics::BeginDeferredActorSpawnFromClass(world, m_meshActorClass, transform,
                                                                            ESpawnActorCollisionHandlingMethod::AlwaysSpawn);
        if (!actor) return nullptr;
        if (UObject* component = m_meshComponent.Object(actor))
        {
            // Movable before registration, never collides, shows our mesh.
            m_setMobility.Call(component, [](std::uint8_t* value, const game::Param& p) {
                if (p.kind == game::Kind::UInt8) *value = 2; // EComponentMobility::Movable
            });
            m_setCollision.Call(component, [](std::uint8_t* value, const game::Param& p) {
                if (p.kind == game::Kind::UInt8) *value = 0; // ECollisionEnabled::NoCollision
            });
            m_setStaticMesh.Call(component, [mesh](std::uint8_t* value, const game::Param& p) {
                if (p.kind == game::Kind::Object) std::memcpy(value, &mesh, sizeof(mesh));
            });
            if (m_setCastShadow.ok())
                m_setCastShadow.Call(component, [](std::uint8_t* value, const game::Param& p) {
                    if (p.kind == game::Kind::Bool) *value = 0;
                });
        }
        UGameplayStatics::FinishSpawningActor(actor, transform);
        actor->SetActorEnableCollision(false);
        return actor;
    }

    bool GhostDemo::EnsureSpawned(Ghost& ghost, UObject* world)
    {
        if (ghost.world == world && ghost.body.Get() && ghost.head.Get() && ghost.visor.Get()) return true;
        Destroy(ghost);
        if (!m_cylinder) m_cylinder = LoadMesh(STR("/Engine/BasicShapes/Cylinder.Cylinder"));
        if (!m_sphere) m_sphere = LoadMesh(STR("/Engine/BasicShapes/Sphere.Sphere"));
        if (!m_cube) m_cube = LoadMesh(STR("/Engine/BasicShapes/Cube.Cube"));
        if (!m_cylinder || !m_sphere || !m_cube)
        {
            if (!m_failed) m_log("ghost demo: engine basic shapes not found; ghosts disabled");
            m_failed = true;
            return false;
        }
        const double d = m_radius * 2 / 100.0; // basic shapes are 100 units
        ghost.body = SpawnShape(world, m_cylinder, d, d, m_halfHeight * 1.6 / 100.0);
        ghost.head = SpawnShape(world, m_sphere, d * 0.9, d * 0.9, d * 0.9);
        ghost.visor = SpawnShape(world, m_cube, d * 0.35, d * 0.7, d * 0.2);
        ghost.world = world;
        return ghost.body.Get() && ghost.head.Get() && ghost.visor.Get();
    }

    void GhostDemo::Destroy(Ghost& ghost)
    {
        for (FWeakObjectPtr* part : {&ghost.body, &ghost.head, &ghost.visor})
        {
            if (UObject* actor = part->Get()) static_cast<AActor*>(actor)->K2_DestroyActor();
            *part = FWeakObjectPtr{};
        }
        ghost.world = nullptr;
    }

    void GhostDemo::Place(Ghost& ghost, double x, double y, double z, double yaw)
    {
        FHitResult hit{};
        const double rad = yaw * 3.14159265358979 / 180.0;
        const double headZ = z + m_halfHeight * 0.85;
        if (UObject* a = ghost.body.Get()) static_cast<AActor*>(a)->K2_SetActorLocationAndRotation(FVector(x, y, z - m_halfHeight * 0.1), FRotator(0, yaw, 0), false, hit, true);
        if (UObject* a = ghost.head.Get()) static_cast<AActor*>(a)->K2_SetActorLocationAndRotation(FVector(x, y, headZ), FRotator(0, yaw, 0), false, hit, true);
        if (UObject* a = ghost.visor.Get())
            static_cast<AActor*>(a)->K2_SetActorLocationAndRotation(FVector(x + std::cos(rad) * m_radius, y + std::sin(rad) * m_radius, headZ), FRotator(0, yaw, 0), false, hit,
                                                                    true);
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
                for (auto& [_, g] : m_ghosts) Destroy(g);
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
                m_bridge.SubmitLocalPose(pose);
            }
            if (UObject* capsule = m_capsule.ok() ? m_capsule.Object(character) : nullptr)
            {
                if (auto h = m_capsuleHalfHeight.Number(capsule); h && *h > 10 && *h < 1000) m_halfHeight = *h;
                if (auto r = m_capsuleRadius.Number(capsule); r && *r > 5 && *r < 500) m_radius = *r;
            }

            // Remote ghosts.
            UObject* world = static_cast<AActor*>(character)->GetWorld();
            const std::string localScene = m_bridge.LocalScene();
            const double renderTime = bridge::Bridge::Now() - InterpolationDelay;
            std::map<std::uint64_t, bool> seen;
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
                    Destroy(ghost);
                    continue;
                }
                if (!ghost.hiddenScene.empty()) m_log("ghost demo: peer " + bridge::Redact(peer.peer) + " is on this scenario");
                ghost.hiddenScene.clear();
                if (!EnsureSpawned(ghost, world)) continue;

                // Interpolate ~100 ms behind the newest sample.
                const auto& s = peer.samples;
                double x, y, z, yaw;
                if (renderTime <= s.front().time)
                {
                    x = s.front().pose.x, y = s.front().pose.y, z = s.front().pose.z, yaw = s.front().pose.yaw;
                }
                else if (renderTime >= s.back().time)
                {
                    const auto& p = s.back().pose;
                    const double dt = std::min(renderTime - s.back().time, MaxExtrapolation);
                    x = p.x + p.vx * dt, y = p.y + p.vy * dt, z = p.z + p.vz * dt, yaw = p.yaw;
                }
                else
                {
                    std::size_t i = 1;
                    while (i < s.size() && s[i].time < renderTime) ++i;
                    const auto& a = s[i - 1];
                    const auto& b = s[i];
                    const double span = b.time - a.time;
                    const double t = span > 0 ? (renderTime - a.time) / span : 1;
                    x = a.pose.x + (b.pose.x - a.pose.x) * t;
                    y = a.pose.y + (b.pose.y - a.pose.y) * t;
                    z = a.pose.z + (b.pose.z - a.pose.z) * t;
                    yaw = LerpAngle(a.pose.yaw, b.pose.yaw, t);
                }
                Place(ghost, x, y, z, yaw);
            }
            // Peers that left, disconnected or went quiet.
            for (auto it = m_ghosts.begin(); it != m_ghosts.end();)
            {
                if (seen.count(it->first)) ++it;
                else
                {
                    Destroy(it->second);
                    m_log("ghost demo: removed ghost of " + bridge::Redact(it->first));
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
            for (auto& [_, g] : m_ghosts) Destroy(g);
        }
        catch (...)
        {
        }
        m_ghosts.clear();
    }
} // namespace aimmod

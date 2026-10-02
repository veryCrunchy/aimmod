#include "CsGear.hpp"

#include "Log.hpp"
#include "Reflect.hpp"
#include "World.hpp"

#include <Unreal/AActor.hpp>
#include <Unreal/Core/HAL/UnrealMemory.hpp>
#include <Unreal/CoreUObject/UObject/Class.hpp>
#include <Unreal/FProperty.hpp>
#include <Unreal/GameplayStatics.hpp>
#include <Unreal/NameTypes.hpp>
#include <Unreal/Property/FBoolProperty.hpp>
#include <Unreal/Property/FStructProperty.hpp>
#include <Unreal/Rotator.hpp>
#include <Unreal/Transform.hpp>
#include <Unreal/UClass.hpp>
#include <Unreal/UObject.hpp>
#include <Unreal/UObjectGlobals.hpp>
#include <Unreal/UnrealCoreStructs.hpp>
#include <Unreal/World.hpp>

#include <chrono>
#include <cmath>
#include <cstring>

namespace aimmod
{
    using namespace reflect;
    using game::Kind;
    using game::Param;
    using game::Shape;
    using RC::Unreal::FWeakObjectPtr;

    namespace
    {
        constexpr std::uint8_t NoCollision = 0, SnapToTarget = 2, Movable = 2; // ECollisionEnabled, EAttachmentRule, EComponentMobility
        constexpr const wchar_t* ShapeMaterial = STR("/Engine/BasicShapes/BasicShapeMaterial.BasicShapeMaterial");

        std::int64_t UnixMs()
        {
            return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
        }
        void Noop(std::uint8_t*, const Param&) {}

        UObject* AddComponent(UObject* owner, UClass* type)
        {
            UObject* component = nullptr;
            Call(owner, STR("/Script/Engine.Actor:AddComponentByClass"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                if (n == STR("Class")) WriteObject(v, type);
                else if (n == STR("bManualAttachment")) WriteBoolParam(v, p, true);
                else if (n == STR("RelativeTransform"))
                    if (auto* s = RC::Unreal::CastField<RC::Unreal::FStructProperty>(p))
                        for (const char* field : {"Rotation.W", "Scale3D.X", "Scale3D.Y", "Scale3D.Z"}) game::SetStructPath(v, s->GetStruct(), field, 1);
            }, &component);
            return Valid(component, type, owner) ? component : nullptr;
        }
        bool Attach(UObject* component, UObject* parent)
        {
            return Call(component, STR("/Script/Engine.SceneComponent:K2_AttachToComponent"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                if (n == STR("Parent")) WriteObject(v, parent);
                else if (n == STR("SocketName")) WriteFName(v, p, STR("None"));
                else if (n == STR("LocationRule") || n == STR("RotationRule") || n == STR("ScaleRule")) *v = SnapToTarget;
            });
        }
        void Relative(UObject* component, const double location[3], const double rotation[3], const double* scale)
        {
            Call(component, STR("/Script/Engine.SceneComponent:K2_SetRelativeLocationAndRotation"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                if (n == STR("NewLocation")) WriteFloats(v, p, {static_cast<float>(location[0]), static_cast<float>(location[1]), static_cast<float>(location[2])});
                else if (n == STR("NewRotation")) WriteFloats(v, p, {static_cast<float>(rotation[0]), static_cast<float>(rotation[1]), static_cast<float>(rotation[2])});
                else if (n == STR("bTeleport")) WriteBoolParam(v, p, true);
            });
            if (scale)
                Call(component, STR("/Script/Engine.SceneComponent:SetRelativeScale3D"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                    if (n == STR("NewScale3D")) WriteFloats(v, p, {static_cast<float>(scale[0]), static_cast<float>(scale[1]), static_cast<float>(scale[2])});
                });
        }
        void CallByte(UObject* component, const wchar_t* function, std::uint8_t value)
        {
            Call(component, function, [&](const std::wstring&, FProperty*, std::uint8_t* v) { *v = value; });
        }
    } // namespace

    bool CsGear::Bind(UObject* player)
    {
        if (!player) return false;
        UClass* cls = player->GetClassPrivate();
        if (m_bound && cls == m_controllerClass) return !m_disabled;
        m_bound = true;
        m_controllerClass = cls;
        m_currentNum.BindPath(STR("/Script/GameSkillsTrainer.WeaponHandler:GetCurrentWeaponNum"), Shape::Number);
        m_keyJustPressed.BindPath(STR("/Script/Engine.PlayerController:WasInputKeyJustPressed"), Shape::Command);
        std::string missing;
        for (int slot = 0; slot < cs::Slots; ++slot)
        {
            const std::wstring n = std::to_wstring(slot + 1);
            m_pressed[slot].BindName(cls, (STR("Weapon") + n + STR("Pressed")).c_str(), Shape::Command);
            m_released[slot].BindName(cls, (STR("Weapon") + n + STR("Released")).c_str(), Shape::Command);
            if (!m_pressed[slot].ok() || !m_released[slot].ok()) missing += " Weapon" + std::to_string(slot + 1);
        }
        if (!m_currentNum.ok()) missing += " GetCurrentWeaponNum";
        m_disabled = !missing.empty();
        Log(m_disabled ? "cs gear: weapon switching unavailable (missing" + missing + ")"
                       : std::string("cs gear: ready (wheel and Q switch weapons") + (m_keyJustPressed.ok() ? ")" : "; no key reads: native 1-4 only)"));
        return !m_disabled;
    }

    int CsGear::InHand(UObject* handler) const
    {
        const auto n = handler ? m_currentNum.Number(handler) : std::nullopt;
        return n && *n >= 0 && *n < 8 ? static_cast<int>(*n) : -1;
    }

    void CsGear::Press(UObject* player, int slot)
    {
        if (slot < 0 || slot >= cs::Slots) return;
        if (m_releaseSlot >= 0) m_released[m_releaseSlot].Call(player, Noop);
        m_pressed[slot].Call(player, Noop);
        m_releaseSlot = slot; // released next frame, like a key tap
    }

    bool CsGear::KeyPressed(UObject* player, const char* key) const
    {
        if (!m_keyJustPressed.ok()) return false;
        bool pressed = false;
        const RC::Unreal::FName name(Widen(key).c_str(), RC::Unreal::FNAME_Add); // FKey: KeyName first, details stay null
        m_keyJustPressed.Call(
            player,
            [&](std::uint8_t* value, const Param& p) {
                if (p.kind == Kind::Other && p.structType && p.size >= static_cast<std::int32_t>(sizeof(name))) std::memcpy(value, &name, sizeof(name));
            },
            [&](const std::uint8_t* buffer, const std::vector<Param>& params) {
                for (const Param& p : params)
                    if (p.ret && p.boolProperty) pressed = p.boolProperty->GetPropertyValue(const_cast<std::uint8_t*>(buffer) + p.offset);
            });
        return pressed;
    }

    void CsGear::LoadoutApplied(const cs::Loadout* before, const cs::Loadout& after)
    {
        if (!before) m_switcher.Reset();
        const int draw = cs::AfterLoadout(before, after, m_switcher.current());
        m_loadout = after;
        m_haveLoadout = true;
        if (draw >= 0)
        {
            m_wanted = draw;
            m_wantedUntil = 0; // set on the next tick (no clock here)
        }
    }

    void CsGear::Tick(double now, UObject* player, UObject* character, UObject* handler, const RoundState& round)
    {
        if (!Bind(player)) return;
        if (m_releaseSlot >= 0)
        {
            m_released[m_releaseSlot].Call(player, Noop);
            m_releaseSlot = -1;
        }
        const int hand = InHand(handler);
        m_switcher.Observe(hand);
        if (m_haveLoadout)
        {
            int target = -1;
            if (KeyPressed(player, "MouseScrollDown")) target = cs::Cycle(m_loadout, hand, 1);
            else if (KeyPressed(player, "MouseScrollUp")) target = cs::Cycle(m_loadout, hand, -1);
            else if (KeyPressed(player, "Q")) target = m_switcher.Previous(m_loadout);
            if (target >= 0 && target != hand) m_wanted = target, m_wantedUntil = 0;
        }
        // Press the slot's own Weapon<N> action until the game has it in hand (it can't switch
        // mid-reload or while firing), for at most a second.
        if (m_wanted >= 0)
        {
            if (m_wantedUntil == 0) m_wantedUntil = now + 1.0, m_nextPress = 0;
            if (hand == m_wanted || now > m_wantedUntil || !m_loadout.Has(m_wanted)) m_wanted = -1;
            else if (m_releaseSlot < 0 && now >= m_nextPress)
            {
                Press(player, m_wanted);
                m_nextPress = now + 0.4; // the switch itself takes the profile's QuickSwitchTime
            }
        }
        HandModels(character, hand);
        KnifeAttacks(now, player, character, hand);
        WorldBomb(now, player, character, round.bomb);
    }

    // A slash when the knife's own shot counter moves (left mouse, KovaaK's fire), a stab on the right
    // mouse at most once a second; the knife model plays the move (cs::KnifePose).
    void CsGear::KnifeAttacks(double now, UObject* player, UObject* character, int hand)
    {
        std::vector<game::WeaponCount> counts;
        std::optional<double> shots;
        if (game::ReadWeaponCounters(character, counts))
            for (const auto& c : counts)
                if (c.slot == cs::KnifeSlot) shots = c.shots;
        if (hand == cs::KnifeSlot && m_loadout.Has(cs::KnifeSlot))
        {
            if (shots && m_knifeShots && *shots > *m_knifeShots)
            {
                m_move = m_lastSlash = cs::NextSlash(m_lastSlash);
                m_moveAt = m_lastAttack = now;
            }
            else if (KeyPressed(player, "RightMouseButton") && now - m_lastAttack >= cs::StabInterval)
            {
                m_move = cs::KnifeMove::Stab;
                m_moveAt = m_lastAttack = now;
                m_lastSlash = cs::KnifeMove::None;
                m_stabRequested = true;
            }
        }
        m_knifeShots = shots;
        UObject* knife = m_knife.Get();
        if (!knife || !Alive(knife)) return;
        const bool moving = m_move != cs::KnifeMove::None && now - m_moveAt < cs::KnifeMoveSeconds(m_move);
        if (!moving && !m_posed) return;
        const cs::Hold rest = cs::InHand(cs::KnifeSlot);
        const cs::Hold delta = moving ? cs::KnifePose(m_move, now - m_moveAt) : cs::Hold{};
        double offset[3], rotation[3];
        for (int i = 0; i < 3; ++i) offset[i] = rest.offset[i] + delta.offset[i], rotation[i] = rest.rotation[i] + delta.rotation[i];
        Relative(knife, offset, rotation, nullptr);
        m_posed = moving;
        if (!moving) m_move = cs::KnifeMove::None;
    }

    UObject* CsGear::BuildModel(UObject* owner, UObject* parent, const std::vector<cs::Part>& parts, std::vector<FWeakObjectPtr>* lights)
    {
        UClass* sceneClass = game::FindClass(STR("/Script/Engine.SceneComponent"));
        UClass* meshClass = game::FindClass(STR("/Script/Engine.StaticMeshComponent"));
        UObject* material = LoadGameAsset(ShapeMaterial);
        if (!sceneClass || !meshClass) return nullptr;
        UObject* root = AddComponent(owner, sceneClass);
        if (!root || !Attach(root, parent)) return nullptr;
        int built = 0;
        for (const cs::Part& part : parts)
        {
            UObject* mesh = LoadGameAsset(part.mesh);
            UObject* component = mesh ? AddComponent(owner, meshClass) : nullptr;
            if (!component) continue;
            CallByte(component, STR("/Script/Engine.SceneComponent:SetMobility"), Movable);
            CallByte(component, STR("/Script/Engine.PrimitiveComponent:SetCollisionEnabled"), NoCollision);
            Call(component, STR("/Script/Engine.PrimitiveComponent:SetCastShadow"), [](const std::wstring&, FProperty* p, std::uint8_t* v) { WriteBoolParam(v, p, false); });
            Call(component, STR("/Script/Engine.StaticMeshComponent:SetStaticMesh"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                if (n == STR("NewMesh")) WriteObject(v, mesh);
            });
            // The engine's basic shape material, tinted through its Color parameter.
            UObject* mid = nullptr;
            if (material)
                Call(Default(STR("/Script/Engine.Default__KismetMaterialLibrary")), STR("/Script/Engine.KismetMaterialLibrary:CreateDynamicMaterialInstance"),
                     [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                         if (n == STR("WorldContextObject")) WriteObject(v, owner);
                         else if (n == STR("Parent")) WriteObject(v, material);
                     },
                     &mid);
            if (mid)
            {
                Call(mid, STR("/Script/Engine.MaterialInstanceDynamic:SetVectorParameterValue"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                    if (n == STR("ParameterName")) WriteFName(v, p, STR("Color"));
                    else if (n == STR("Value")) WriteFloats(v, p, {static_cast<float>(part.colour[0]), static_cast<float>(part.colour[1]), static_cast<float>(part.colour[2]), 1.0f});
                });
                Call(component, STR("/Script/Engine.PrimitiveComponent:SetMaterial"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                    if (n == STR("Material")) WriteObject(v, mid); // ElementIndex stays 0
                });
            }
            if (!Attach(component, root)) continue;
            const double scale[3] = {part.size[0] / 100, part.size[1] / 100, part.size[2] / 100};
            Relative(component, part.offset, part.rotation, scale);
            if (part.light && lights) lights->push_back(FWeakObjectPtr(component));
            ++built;
        }
        return built > 0 ? root : nullptr;
    }

    void CsGear::HandModels(UObject* character, int slot)
    {
        if (!character) return;
        if (m_handOwner.Get() != character)
        {
            // A new body (respawn): the old models went with the old one.
            m_handOwner = FWeakObjectPtr(character);
            m_handBuilt = m_handFailed = false;
            m_shownSlot = -2;
        }
        if (!m_handBuilt && !m_handFailed)
        {
            m_handBuilt = true;
            UObject* camera = GetObject(character, STR("FirstPersonCamera"));
            UObject* knife = camera ? BuildModel(character, camera, cs::KnifeModel(), nullptr) : nullptr;
            UObject* bomb = camera ? BuildModel(character, camera, cs::BombModel(), nullptr) : nullptr;
            if (!knife || !bomb)
            {
                m_handFailed = true;
                Log(std::string("cs gear: no knife or bomb in the hand (") + (camera ? "the models could not be built" : "no first-person camera") + ")");
            }
            for (auto [root, which] : {std::pair{knife, cs::KnifeSlot}, std::pair{bomb, cs::BombSlot}})
                if (root)
                {
                    const cs::Hold hold = cs::InHand(which);
                    Relative(root, hold.offset, hold.rotation, nullptr);
                    SetVisible(root, false, true);
                }
            m_knife = FWeakObjectPtr(knife);
            m_handBomb = FWeakObjectPtr(bomb);
        }
        // Only AimMod's own knife and bomb need drawing: KovaaK's shows the guns.
        const int show = (slot == cs::KnifeSlot || slot == cs::BombSlot) && m_loadout.Has(slot) ? slot : -1;
        if (show == m_shownSlot) return;
        m_shownSlot = show;
        if (UObject* k = m_knife.Get(); k && Alive(k)) SetVisible(k, show == cs::KnifeSlot, true);
        if (UObject* b = m_handBomb.Get(); b && Alive(b)) SetVisible(b, show == cs::BombSlot, true);
    }

    // The highest floor between `top` and `bottom` at (x, y): a line trace for the map's static and
    // dynamic geometry (not pawns, so nobody standing on it gets in the way). Nullopt without a hit.
    std::optional<double> CsGear::FloorBelow(UObject* context, double x, double y, double top, double bottom)
    {
        UObject* kismet = Default(STR("/Script/Engine.Default__KismetSystemLibrary"));
        if (!context || !kismet) return std::nullopt;
        struct RawBytes { std::uint8_t* data; std::int32_t num, max; };
        bool hit = false, inside = false;
        double z = 0;
        std::uint8_t* types = nullptr;
        std::uint8_t* ignore = nullptr;
        const bool ran = Call(kismet, STR("/Script/Engine.KismetSystemLibrary:LineTraceSingleForObjects"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
            if (n == STR("WorldContextObject")) WriteObject(v, context);
            else if (n == STR("Start")) WriteFloats(v, p, {static_cast<float>(x), static_cast<float>(y), static_cast<float>(top)});
            else if (n == STR("End")) WriteFloats(v, p, {static_cast<float>(x), static_cast<float>(y), static_cast<float>(bottom)});
            else if (n == STR("ObjectTypes"))
            {
                // WorldStatic and WorldDynamic.
                auto* data = static_cast<std::uint8_t*>(RC::Unreal::FMemory::Malloc(2));
                if (!data) return;
                data[0] = 0;
                data[1] = 1;
                const RawBytes raw{data, 2, 2};
                std::memcpy(v, &raw, sizeof raw);
                types = v;
            }
            else if (n == STR("ActorsToIgnore")) WriteObjectArray(v, context), ignore = v;
            else if (n == STR("bIgnoreSelf")) WriteBoolParam(v, p, true);
        }, nullptr, [&](const std::wstring& n, FProperty* p, const std::uint8_t* v) {
            if (n == STR("ReturnValue")) hit = *v != 0;
            else if (n == STR("OutHit"))
                if (auto* s = RC::Unreal::CastField<RC::Unreal::FStructProperty>(p))
                    for (FProperty* member : s->GetStruct()->ForEachProperty())
                    {
                        if (member->GetName() == STR("ImpactPoint"))
                        {
                            float f[3];
                            std::memcpy(f, v + member->GetOffset_Internal(), sizeof f);
                            z = f[2];
                        }
                        else if (member->GetName() == STR("bStartPenetrating"))
                            if (auto* b = RC::Unreal::CastField<RC::Unreal::FBoolProperty>(member)) inside = b->GetPropertyValue(const_cast<std::uint8_t*>(v) + member->GetOffset_Internal());
                    }
            if (n == STR("ObjectTypes") || n == STR("ActorsToIgnore"))
            {
                RawBytes raw;
                std::memcpy(&raw, v, sizeof raw);
                if (raw.data) RC::Unreal::FMemory::Free(raw.data);
                if (n == STR("ObjectTypes")) types = nullptr;
                else ignore = nullptr;
            }
        });
        // The call did not run: free what was written.
        if (!ran)
            for (std::uint8_t* left : {types, ignore})
                if (left)
                {
                    RawBytes raw;
                    std::memcpy(&raw, left, sizeof raw);
                    if (raw.data) RC::Unreal::FMemory::Free(raw.data);
                }
        if (!ran || !hit || inside || !std::isfinite(z)) return std::nullopt;
        return z;
    }

    void CsGear::WorldBomb(double now, UObject* player, UObject* character, const std::optional<RoundState::Bomb>& bomb)
    {
        UObject* actor = m_bombActor.Get();
        if (actor && !Alive(actor)) actor = nullptr;
        if (!bomb)
        {
            if (actor && !m_bombKey.empty()) static_cast<RC::Unreal::AActor*>(actor)->SetActorHiddenInGame(true);
            m_bombKey.clear();
            return;
        }
        if (!actor)
        {
            if (now < m_nextBombTry || !character) return;
            m_nextBombTry = now + 2.0;
            m_bombLights.clear();
            m_bombKey.clear();
            UObject* world = static_cast<RC::Unreal::AActor*>(character)->GetWorld();
            UClass* actorClass = game::FindClass(STR("/Script/Engine.StaticMeshActor"));
            if (!world || !actorClass) return;
            using namespace RC::Unreal;
            const FTransform transform{FQuat(FRotator(0, 0, 0)), FVector(static_cast<float>(bomb->x), static_cast<float>(bomb->y), static_cast<float>(bomb->z)), FVector(1, 1, 1)};
            AActor* spawned = UGameplayStatics::BeginDeferredActorSpawnFromClass(world, actorClass, transform, ESpawnActorCollisionHandlingMethod::AlwaysSpawn);
            if (!spawned) return;
            UGameplayStatics::FinishSpawningActor(spawned, transform);
            spawned->SetActorEnableCollision(false);
            UObject* root = GetObject(spawned, STR("StaticMeshComponent"));
            if (root)
            {
                CallByte(root, STR("/Script/Engine.SceneComponent:SetMobility"), Movable);
                CallByte(root, STR("/Script/Engine.PrimitiveComponent:SetCollisionEnabled"), NoCollision);
            }
            if (!root || !BuildModel(spawned, root, cs::BombModel(), &m_bombLights))
            {
                Call(spawned, STR("/Script/Engine.Actor:K2_DestroyActor"));
                Log("cs gear: the bomb's world model could not be built");
                return;
            }
            m_bombActor = FWeakObjectPtr(spawned);
            actor = spawned;
            m_lightShown = true;
            Log("cs gear: bomb model placed in the world");
        }
        // Where it lies: the host gives the carrier's capsule centre height; the floor is the local
        // player's eye height below the eye there (everyone has the same body).
        const std::string key = bomb->state + "@" + std::to_string(std::lround(bomb->x)) + "," + std::to_string(std::lround(bomb->y)) + "," + std::to_string(std::lround(bomb->z));
        if (key != m_bombKey)
        {
            m_bombKey = key;
            double floor = bomb->z - 60;
            UObject* camera = player ? m_b.cameraManager.Object(player) : nullptr;
            UObject* capsule = character ? m_b.capsule.Object(character) : nullptr;
            double eye[3], body[3];
            const auto half = capsule ? m_b.capsuleHalfHeight.Number(capsule) : std::nullopt;
            if (camera && half && m_b.cameraLocation.Vector(camera, eye) && m_b.actorLocation.Vector(character, body))
            {
                const double eyeHeight = eye[2] - (body[2] - *half);
                if (eyeHeight > 20 && eyeHeight < 400) floor = bomb->z + 64 - eyeHeight;
            }
            // Better: the floor itself, traced straight down from above that guess. Whoever dropped
            // it (a player, a bot, a body that sat a little high or low), it lies on the ground.
            if (const auto ground = FloorBelow(character, bomb->x, bomb->y, std::max(floor, bomb->z) + 120, floor - 400))
            {
                if (std::fabs(*ground - floor) > 8) Log("cs gear: the bomb's floor traced at z=" + std::to_string(std::lround(*ground)) + " (estimate z=" + std::to_string(std::lround(floor)) + ")");
                floor = *ground;
            }
            // A steady, slightly turned rest, the same on every machine.
            const double yaw = std::fmod(std::fabs(bomb->x * 0.37 + bomb->y * 0.11), 360.0);
            Call(actor, STR("/Script/Engine.Actor:K2_SetActorLocationAndRotation"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                if (n == STR("NewLocation")) WriteFloats(v, p, {static_cast<float>(bomb->x), static_cast<float>(bomb->y), static_cast<float>(floor + 0.5)});
                else if (n == STR("NewRotation")) WriteFloats(v, p, {0.0f, static_cast<float>(yaw), 0.0f});
                else if (n == STR("bTeleport")) WriteBoolParam(v, p, true);
            });
            static_cast<RC::Unreal::AActor*>(actor)->SetActorHiddenInGame(false);
            Log("cs gear: bomb " + bomb->state);
        }
        // The light flashes with the beep while planted, and is dark otherwise.
        const bool light = bomb->state == "planted" && bomb->explodesMs > 0 && cs::LightOn((bomb->explodesMs - UnixMs()) / 1000.0);
        if (light != m_lightShown)
        {
            m_lightShown = light;
            for (auto& weak : m_bombLights)
                if (UObject* l = weak.Get(); l && Alive(l)) SetVisible(l, light, false);
        }
    }

    void CsGear::Release(UObject* player, const char* why)
    {
        if (m_releaseSlot >= 0 && player && m_released[m_releaseSlot].ok()) m_released[m_releaseSlot].Call(player, Noop);
        if (UObject* k = m_knife.Get(); k && Alive(k)) SetVisible(k, false, true);
        if (UObject* b = m_handBomb.Get(); b && Alive(b)) SetVisible(b, false, true);
        if (UObject* a = m_bombActor.Get(); a && Alive(a)) Call(a, STR("/Script/Engine.Actor:K2_DestroyActor"));
        const bool had = m_haveLoadout || m_bombActor.Get();
        m_bombActor = FWeakObjectPtr();
        m_bombLights.clear();
        m_bombKey.clear();
        m_shownSlot = -2;
        m_haveLoadout = false;
        m_wanted = m_releaseSlot = -1;
        m_switcher.Reset();
        if (had) Log(std::string("cs gear: released (") + why + ")");
    }
} // namespace aimmod

#include "Water.hpp"

#include "Log.hpp"
#include "Reflect.hpp"
#include "World.hpp"

#include <Unreal/AActor.hpp>
#include <Unreal/CoreUObject/UObject/Class.hpp>
#include <Unreal/CoreUObject/UObject/UnrealType.hpp>
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

#include <Windows.h>

#include <algorithm>
#include <cmath>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <optional>
#include <sstream>

namespace aimmod
{
    using namespace RC::Unreal;
    using namespace reflect;

    namespace
    {
        constexpr std::uint8_t NoCollision = 0, QueryOnly = 1;                // ECollisionEnabled
        constexpr std::uint8_t ResponseOverlap = 1;                           // ECollisionResponse
        constexpr std::uint8_t ChannelWorldStatic = 0;                        // ECollisionChannel
        constexpr std::uint8_t MoveFalling = 3, MoveSwimming = 4;             // EMovementMode
        constexpr std::size_t MaxVolumes = 256;
        constexpr double ScanInterval = 1.0, SlowScanInterval = 5.0, SettleTime = 15.0;

        std::optional<bool> GetBoolProperty(UObject* object, const wchar_t* name)
        {
            auto* p = object ? CastField<FBoolProperty>(PropertyOf(object->GetClassPrivate(), name)) : nullptr;
            if (!p) return std::nullopt;
            return p->GetPropertyValueInContainer(object);
        }
        bool SetInt(UObject* object, const wchar_t* name, std::int32_t value)
        {
            FProperty* p = object ? PropertyOf(object->GetClassPrivate(), name) : nullptr;
            if (!p || p->GetSize() != 4) return false;
            std::memcpy(At(object, p), &value, 4);
            return true;
        }
        // NavAgentProps.bCanSwim (a bit of FMovementProperties, the struct's base).
        FBoolProperty* CanSwimBit(UObject* movement, std::uint8_t*& at)
        {
            auto* props = movement ? CastField<FStructProperty>(PropertyOf(movement->GetClassPrivate(), STR("NavAgentProps"))) : nullptr;
            if (!props) return nullptr;
            FProperty* bit = PropertyOf(props->GetStruct(), STR("bCanSwim"));
            auto* b = CastField<FBoolProperty>(bit);
            if (!b) return nullptr;
            at = At(movement, props) + b->GetOffset_Internal();
            return b;
        }
        bool CallByte(UObject* self, const wchar_t* fn, std::uint8_t value)
        {
            return Call(self, fn, [&](const std::wstring&, FProperty*, std::uint8_t* v) { *v = value; });
        }
        std::wstring InputIniPath()
        {
            wchar_t base[MAX_PATH * 2]{};
            const DWORD n = GetEnvironmentVariableW(L"LOCALAPPDATA", base, static_cast<DWORD>(std::size(base)));
            if (n == 0 || n >= std::size(base)) return {};
            return (std::filesystem::path(base) / L"FPSAimTrainer" / L"Saved" / L"Config" / L"WindowsNoEditor" / L"Input.ini").wstring();
        }
        std::vector<std::string> ReadJumpKeys()
        {
            std::string text;
            const std::wstring path = InputIniPath();
            if (!path.empty())
            {
                std::ifstream in(std::filesystem::path(path), std::ios::binary);
                if (in)
                {
                    text.assign(std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>());
                    if (text.size() > 1 << 20) text.clear();
                }
            }
            return water::JumpKeys(text);
        }
        std::string Fixed(double v)
        {
            std::ostringstream s;
            s.precision(0);
            s << std::fixed << v;
            return s.str();
        }
    } // namespace

    void Water::Bind()
    {
        if (m_bound) return;
        m_bound = true;
        m_inBenchmark.BindPath(STR("/Script/GameSkillsTrainer.ScenarioManager:IsCurrentlyInBenchmark"), game::Shape::Bool);
        m_inEditor.BindPath(STR("/Script/GameSkillsTrainer.ScenarioManager:IsInScenarioEditor"), game::Shape::Bool);
        m_volumeClass = UObjectGlobals::StaticFindObject<UClass*>(nullptr, nullptr, STR("/Script/Engine.PhysicsVolume"));
        m_boxClass = UObjectGlobals::StaticFindObject<UClass*>(nullptr, nullptr, STR("/Script/Engine.BoxComponent"));
        m_postClass = UObjectGlobals::StaticFindObject<UClass*>(nullptr, nullptr, STR("/Script/Engine.PostProcessComponent"));
        m_disabled = !m_volumeClass || !m_boxClass || !m_inBenchmark.ok() || !m_inEditor.ok();
        if (m_disabled) Warn("water: unavailable (engine water volume, box or scenario state bindings missing)");
    }

    void Water::Tick(double now, const std::string& scenario, bool inChallenge, bool loading)
    {
        if (!m_bound) Bind();
        if (m_disabled) return;
        UObject* manager = m_scene.Manager();
        UObject* player = m_scene.Player();
        UObject* character = player && m_b.myCharacter.ok() ? m_b.myCharacter.Object(player) : nullptr;
        const bool benchmark = manager ? m_inBenchmark.Bool(manager).value_or(true) : true;
        const bool editor = manager ? m_inEditor.Bool(manager).value_or(true) : true;
        if (!water::Allowed(scenario, inChallenge, benchmark, editor, loading))
        {
            Release(!manager ? "game not ready" : inChallenge ? "challenge" : benchmark ? "benchmark" : editor ? "editor" : loading ? "loading" : "not an AimMod scenario");
            return;
        }
        if (!character) return; // respawning: keep the volumes
        UObject* world = static_cast<AActor*>(character)->GetWorld();
        if (!world) return Release("no world");
        if (world != m_world || scenario != m_scenario)
        {
            // A new world took the old actors with it; a new scenario starts over.
            if (m_world == world) Release("scenario changed");
            m_volumes.clear();
            m_saved = {};
            m_tuned = false;
            m_world = world;
            m_scenario = scenario;
            m_swimLogged = false;
            m_nextScan = 0;
            m_engagedAt = 0;
            m_character = RC::Unreal::FWeakObjectPtr{};
            m_jumpKeys = ReadJumpKeys();
        }
        if (now >= m_nextScan)
        {
            // Often while the map settles after a load, then rarely (a full
            // object search costs a few milliseconds).
            if (m_engagedAt == 0) m_engagedAt = now;
            m_nextScan = now + (now - m_engagedAt < SettleTime ? ScanInterval : SlowScanInterval);
            Scan(world, character);
        }
        if (m_volumes.empty()) return;
        if (m_character.Get() != character) TuneCharacter(character); // respawned
        SwimInput(player, character);
    }

    void Water::Scan(UObject* world, UObject* character)
    {
        // Volumes whose Water object went away (the map was rebuilt).
        std::size_t removed = 0;
        for (auto it = m_volumes.begin(); it != m_volumes.end();)
        {
            if (it->source.Get()) { ++it; continue; }
            if (UObject* v = it->volume.Get()) Call(v, STR("/Script/Engine.Actor:K2_DestroyActor"));
            it = m_volumes.erase(it);
            ++removed;
        }
        // The map creator spawns its Water objects as this Blueprint class.
        std::vector<UObject*> found, fresh;
        UObjectGlobals::FindAllOf(STR("MapCreatorWaterInstance_C"), found);
        for (UObject* source : found)
        {
            if (!game::IsLiveInstance(source) || static_cast<AActor*>(source)->GetWorld() != world) continue;
            if (std::any_of(m_volumes.begin(), m_volumes.end(), [&](const Volume& v) { return v.source.Get() == source; })) continue;
            if (m_volumes.size() + fresh.size() < MaxVolumes) fresh.push_back(source);
        }
        if (m_volumes.empty() && fresh.empty())
        {
            if (m_tuned) Restore();
            return;
        }
        TuneCharacter(character); // the volumes take the character's tuning
        std::size_t added = 0;
        for (UObject* source : fresh)
            if (Spawn(world, source)) ++added;
        if (added || removed)
        {
            m_lastReason.clear();
            Log("water: " + std::to_string(m_volumes.size()) + " swimmable volume(s) (" + water::StyleName(m_style) + ": swim " + Fixed(m_tuning.maxSwimSpeed) +
                " cm/s, sink " + Fixed(m_tuning.sinkSpeed) + " cm/s, friction " + Fixed(0.5 * m_tuning.fluidFriction) + "/s)");
        }
    }

    bool Water::Spawn(UObject* world, UObject* source)
    {
        UObject* mesh = GetObject(source, STR("EditorMesh"));
        float origin[3], extent[3];
        if (!mesh || !Bounds(mesh, origin, extent)) return false;
        for (float e : extent)
            if (!std::isfinite(e) || e < 1.0f) return false;

        Volume entry;
        entry.source = FWeakObjectPtr(source);
        entry.mesh = FWeakObjectPtr(mesh);
        // The Water mesh stays visible but never blocks or catches traces.
        Call(mesh, STR("/Script/Engine.PrimitiveComponent:GetCollisionEnabled"), {}, nullptr, [&](const std::wstring& n, FProperty* p, const std::uint8_t* v) {
            if (n == STR("ReturnValue") && p->GetSize() == 1) entry.meshCollision = *v, entry.collisionSaved = true;
        });
        CallByte(mesh, STR("/Script/Engine.PrimitiveComponent:SetCollisionEnabled"), NoCollision);

        const FTransform transform{FQuat(FRotator(0, 0, 0)), FVector(origin[0], origin[1], origin[2]), FVector(1, 1, 1)};
        AActor* volume = UGameplayStatics::BeginDeferredActorSpawnFromClass(world, m_volumeClass, transform, ESpawnActorCollisionHandlingMethod::AlwaysSpawn);
        if (!volume)
        {
            m_volumes.push_back(entry); // keep the collision change undoable; retried never
            return false;
        }
        SetBool(volume, STR("bWaterVolume"), true);
        SetBool(volume, STR("bPhysicsOnContact"), false);
        SetFloat(volume, STR("FluidFriction"), static_cast<float>(m_tuning.fluidFriction));
        SetFloat(volume, STR("TerminalVelocity"), static_cast<float>(m_tuning.terminalVelocity));
        SetInt(volume, STR("Priority"), 1);
        UGameplayStatics::FinishSpawningActor(volume, transform);
        entry.volume = FWeakObjectPtr(volume);

        // The engine finds water volumes through their root primitive (bounds,
        // overlap query, distance test). A runtime volume has no brush, so a
        // query-only box over the Water object becomes its root.
        UObject* box = nullptr;
        Call(volume, STR("/Script/Engine.Actor:AddComponentByClass"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
            if (n == STR("Class")) WriteObject(v, m_boxClass);
            else if (n == STR("bManualAttachment")) WriteBoolParam(v, p, true);
            else if (n == STR("RelativeTransform"))
                if (auto* s = CastField<FStructProperty>(p))
                    for (const char* field : {"Rotation.W", "Scale3D.X", "Scale3D.Y", "Scale3D.Z"}) game::SetStructPath(v, s->GetStruct(), field, 1);
        }, &box);
        if (!box || !box->IsA(m_boxClass))
        {
            Call(volume, STR("/Script/Engine.Actor:K2_DestroyActor"));
            entry.volume = FWeakObjectPtr{};
            m_volumes.push_back(entry);
            return false;
        }
        CallByte(box, STR("/Script/Engine.PrimitiveComponent:SetCollisionEnabled"), NoCollision);
        Call(box, STR("/Script/Engine.BoxComponent:SetBoxExtent"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
            if (n == STR("InBoxExtent")) WriteFloats(v, p, {extent[0], extent[1], extent[2]});
            else if (n == STR("bUpdateOverlaps")) WriteBoolParam(v, p, false);
        });
        SetWorldLocation(box, origin);
        CallByte(box, STR("/Script/Engine.PrimitiveComponent:SetCollisionObjectType"), ChannelWorldStatic);
        CallByte(box, STR("/Script/Engine.PrimitiveComponent:SetCollisionResponseToAllChannels"), ResponseOverlap);
        Call(box, STR("/Script/Engine.PrimitiveComponent:SetGenerateOverlapEvents"), [](const std::wstring&, FProperty* p, std::uint8_t* v) { WriteBoolParam(v, p, false); });
        CallByte(box, STR("/Script/Engine.PrimitiveComponent:SetCollisionEnabled"), QueryOnly);
        SetObject(volume, STR("RootComponent"), box);

        // Underwater tint while the camera is inside the box.
        if (m_postClass)
        {
            UObject* post = nullptr;
            Call(volume, STR("/Script/Engine.Actor:AddComponentByClass"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                if (n == STR("Class")) WriteObject(v, m_postClass);
                else if (n == STR("bManualAttachment")) WriteBoolParam(v, p, false);
                else if (n == STR("RelativeTransform"))
                    if (auto* s = CastField<FStructProperty>(p))
                        for (const char* field : {"Rotation.W", "Scale3D.X", "Scale3D.Y", "Scale3D.Z"}) game::SetStructPath(v, s->GetStruct(), field, 1);
            }, &post);
            if (post && post->IsA(m_postClass))
            {
                SetBool(post, STR("bUnbound"), false);
                SetFloat(post, STR("BlendRadius"), 1.0f);
                SetFloat(post, STR("BlendWeight"), 1.0f);
                SetFloat(post, STR("Priority"), 1.0f);
                if (auto* settings = CastField<FStructProperty>(PropertyOf(post->GetClassPrivate(), STR("Settings"))))
                {
                    const water::Tint tint = water::UnderwaterTint();
                    std::uint8_t* s = At(post, settings);
                    game::SetStructPath(s, settings->GetStruct(), "SceneColorTint.R", tint.r);
                    game::SetStructPath(s, settings->GetStruct(), "SceneColorTint.G", tint.g);
                    game::SetStructPath(s, settings->GetStruct(), "SceneColorTint.B", tint.b);
                    game::SetStructPath(s, settings->GetStruct(), "SceneColorTint.A", 1.0);
                    game::SetStructPath(s, settings->GetStruct(), "bOverride_SceneColorTint", 1);
                }
            }
        }
        m_volumes.push_back(entry);
        return true;
    }

    void Water::TuneCharacter(UObject* character)
    {
        m_character = FWeakObjectPtr(character);
        UObject* movement = GetObject(character, STR("CharacterMovement"));
        if (!movement) return;
        if (!m_mode.ok()) m_mode.Bind(movement->GetClassPrivate(), STR("MovementMode"));
        if (m_tuned && m_saved.movement.Get() == movement)
        {
            // Profile reloads can rewrite movement values; keep ours.
            SetFloat(movement, STR("MaxSwimSpeed"), static_cast<float>(m_tuning.maxSwimSpeed));
            SetFloat(movement, STR("Buoyancy"), static_cast<float>(m_tuning.buoyancy));
            return;
        }
        if (m_tuned) Restore(); // a new character (respawn)
        const bool quake = GetBoolProperty(movement, STR("bEnableQuakeMovement")).value_or(false);
        m_style = quake ? water::Style::Quake : water::Style::CounterStrike;
        double run = GetFloat(movement, STR("MaxWalkSpeed")).value_or(0.0f);
        if (!(run > 0)) run = GetFloat(movement, STR("MaxSpeed")).value_or(0.0f);
        const double scale = m_scene.GameState() && m_b.mapScale.ok() ? m_b.mapScale.Number(m_scene.GameState()).value_or(4.0) : 4.0;
        m_tuning = water::TuningFor(m_style, run, scale);

        Saved saved;
        saved.movement = FWeakObjectPtr(movement);
        saved.maxSwimSpeed = GetFloat(movement, STR("MaxSwimSpeed")).value_or(300.0f);
        saved.buoyancy = GetFloat(movement, STR("Buoyancy")).value_or(1.0f);
        saved.outOfWaterZ = GetFloat(movement, STR("OutofWaterZ")).value_or(420.0f);
        saved.jumpOutOfWaterPitch = GetFloat(movement, STR("JumpOutOfWaterPitch")).value_or(11.25f);
        std::uint8_t* bitAt = nullptr;
        if (FBoolProperty* bit = CanSwimBit(movement, bitAt))
        {
            saved.canSwim = bit->GetPropertyValue(bitAt);
            saved.canSwimSaved = true;
            bit->SetPropertyValue(bitAt, true);
        }
        SetFloat(movement, STR("MaxSwimSpeed"), static_cast<float>(m_tuning.maxSwimSpeed));
        SetFloat(movement, STR("Buoyancy"), static_cast<float>(m_tuning.buoyancy));
        SetFloat(movement, STR("OutofWaterZ"), static_cast<float>(m_tuning.outOfWaterZ));
        SetFloat(movement, STR("JumpOutOfWaterPitch"), static_cast<float>(m_tuning.jumpOutOfWaterPitch));
        m_saved = saved;
        m_tuned = true;
        // Volumes spawned before the tuning was known get it now.
        for (const Volume& v : m_volumes)
            if (UObject* volume = v.volume.Get())
            {
                SetFloat(volume, STR("FluidFriction"), static_cast<float>(m_tuning.fluidFriction));
                SetFloat(volume, STR("TerminalVelocity"), static_cast<float>(m_tuning.terminalVelocity));
            }
    }

    void Water::Restore()
    {
        if (!m_tuned) return;
        m_tuned = false;
        UObject* movement = m_saved.movement.Get();
        if (!movement) return;
        if (GetByte(movement, STR("MovementMode")).value_or(0) == MoveSwimming)
            Call(movement, STR("/Script/Engine.CharacterMovementComponent:SetMovementMode"), [](const std::wstring& n, FProperty*, std::uint8_t* v) {
                if (n == STR("NewMovementMode")) *v = MoveFalling;
            });
        SetFloat(movement, STR("MaxSwimSpeed"), m_saved.maxSwimSpeed);
        SetFloat(movement, STR("Buoyancy"), m_saved.buoyancy);
        SetFloat(movement, STR("OutofWaterZ"), m_saved.outOfWaterZ);
        SetFloat(movement, STR("JumpOutOfWaterPitch"), m_saved.jumpOutOfWaterPitch);
        std::uint8_t* bitAt = nullptr;
        if (m_saved.canSwimSaved)
            if (FBoolProperty* bit = CanSwimBit(movement, bitAt)) bit->SetPropertyValue(bitAt, m_saved.canSwim);
    }

    bool Water::JumpHeld(UObject* player)
    {
        for (const std::string& key : m_jumpKeys)
        {
            bool down = false;
            Call(player, STR("/Script/Engine.PlayerController:IsInputKeyDown"),
                 [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                     if (n != STR("Key")) return;
                     FName name(Widen(key).c_str(), FNAME_Add); // FKey: KeyName first, details stay null
                     std::memcpy(v, &name, sizeof(name));
                 },
                 nullptr,
                 [&](const std::wstring& n, FProperty* p, const std::uint8_t* v) {
                     if (n == STR("ReturnValue"))
                         if (auto* b = CastField<FBoolProperty>(p)) down = b->GetPropertyValue(v);
                 });
            if (down) return true;
        }
        return false;
    }

    void Water::SwimInput(UObject* player, UObject* character)
    {
        UObject* movement = m_saved.movement.Get();
        if (!movement || !m_mode.ok() || m_mode.Number(movement).value_or(0) != MoveSwimming) return;
        if (!m_swimLogged)
        {
            m_swimLogged = true;
            Log("water: swimming");
        }
        float last[3]{};
        Call(character, STR("/Script/Engine.Pawn:GetLastMovementInputVector"), {}, nullptr, [&](const std::wstring& n, FProperty* p, const std::uint8_t* v) {
            if (n == STR("ReturnValue")) ReadFloats(v, p, last, 3);
        });
        const double horizontal = std::hypot(static_cast<double>(last[0]), static_cast<double>(last[1]));
        const double up = water::VerticalInput(m_tuning, JumpHeld(player), horizontal);
        if (up == 0.0) return;
        Call(character, STR("/Script/Engine.Pawn:AddMovementInput"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
            if (n == STR("WorldDirection")) WriteFloats(v, p, {0.0f, 0.0f, 1.0f});
            else if (n == STR("ScaleValue")) WriteFloats(v, p, {static_cast<float>(up)});
            else if (n == STR("bForce")) WriteBoolParam(v, p, false);
        });
    }

    void Water::Release(const char* why)
    {
        if (m_volumes.empty() && !m_tuned)
        {
            m_world = nullptr;
            m_scenario.clear();
            return;
        }
        Restore();
        for (const Volume& v : m_volumes)
        {
            if (UObject* volume = v.volume.Get()) Call(volume, STR("/Script/Engine.Actor:K2_DestroyActor"));
            if (UObject* mesh = v.mesh.Get(); mesh && v.collisionSaved) CallByte(mesh, STR("/Script/Engine.PrimitiveComponent:SetCollisionEnabled"), v.meshCollision);
        }
        m_volumes.clear();
        m_world = nullptr;
        m_scenario.clear();
        if (m_lastReason != why)
        {
            m_lastReason = why;
            Log(std::string("water: off (") + why + ")");
        }
    }
} // namespace aimmod

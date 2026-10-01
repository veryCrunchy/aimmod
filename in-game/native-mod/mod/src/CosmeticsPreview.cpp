#include "CosmeticsPreview.hpp"

#include "Log.hpp"
#include "World.hpp"

#include <Unreal/AActor.hpp>
#include <Unreal/CoreUObject/UObject/Class.hpp>
#include <Unreal/CoreUObject/UObject/UnrealType.hpp>
#include <Unreal/Core/HAL/UnrealMemory.hpp>
#include <Unreal/FProperty.hpp>
#include <Unreal/Property/FArrayProperty.hpp>
#include <Unreal/Property/FBoolProperty.hpp>
#include <Unreal/Property/FNameProperty.hpp>
#include <Unreal/Property/FObjectProperty.hpp>
#include <Unreal/Property/FStrProperty.hpp>
#include <Unreal/GameplayStatics.hpp>
#include <Unreal/NameTypes.hpp>
#include <Unreal/Rotator.hpp>
#include <Unreal/Transform.hpp>
#include <Unreal/UClass.hpp>
#include <Unreal/UFunction.hpp>
#include <Unreal/UObject.hpp>
#include <Unreal/UObjectGlobals.hpp>
#include <Unreal/UnrealCoreStructs.hpp>
#include <Unreal/World.hpp>

#include <Windows.h>

#include <cmath>
#include <cstring>
#include <ctime>
#include <fstream>
#include <functional>
#include <set>

namespace aimmod
{
    using namespace RC::Unreal;

    namespace
    {
        // The game's own character preview stage (character profile menu).
        constexpr const wchar_t* StageClass =
            L"/Game/FirstPersonBP/Blueprints/UI/CharacterStuff/CharcterSkinPreview/CharacterSkinPreviewActorUserInterfaceBP.CharacterSkinPreviewActorUserInterfaceBP_C";
        constexpr const wchar_t* ModelPack = L"/Game/FirstPersonBP/Blueprints/Bodies/Characters/CharacterModelPacks/Default_CharacterModelPack.Default_CharacterModelPack";
        constexpr const wchar_t* SkinPack = L"/Game/FirstPersonBP/Blueprints/Bodies/Characters/CharacterSkinPacks/Default_CharacterSkinPack.Default_CharacterSkinPack";
        // Far above any map: nothing in the scenario can see it, and its point
        // lights cannot reach the map.
        constexpr double StageHeight = 500000.0;
        constexpr int Size = 384;
        constexpr double ReadInterval = 0.2, MinCaptureInterval = 0.1;

        struct RawArray
        {
            void* data;
            std::int32_t num;
            std::int32_t max;
        };

        bool Guarded(UObject* self, UFunction* function, void* parms)
        {
            __try
            {
                self->ProcessEvent(function, parms);
                return true;
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                return false;
            }
        }

        FProperty* PropertyOf(UStruct* type, const wchar_t* name)
        {
            if (!type) return nullptr;
            FName fname(name, FNAME_Find);
            if (fname == FName()) return nullptr;
            for (FProperty* p : type->ForEachPropertyInChain())
                if (p->GetFName() == fname) return p;
            return nullptr;
        }
        std::uint8_t* At(UObject* object, FProperty* p) { return reinterpret_cast<std::uint8_t*>(object) + p->GetOffset_Internal(); }

        UObject* GetObject(UObject* object, const wchar_t* name)
        {
            FProperty* p = object ? PropertyOf(object->GetClassPrivate(), name) : nullptr;
            if (!p || !CastField<FObjectPropertyBase>(p)) return nullptr;
            UObject* value;
            std::memcpy(&value, At(object, p), sizeof(value));
            return value;
        }
        bool SetObject(UObject* object, const wchar_t* name, UObject* value)
        {
            FProperty* p = object ? PropertyOf(object->GetClassPrivate(), name) : nullptr;
            if (!p || !CastField<FObjectPropertyBase>(p)) return false;
            std::memcpy(At(object, p), &value, sizeof(value));
            return true;
        }
        bool SetBool(UObject* object, const wchar_t* name, bool value)
        {
            auto* p = object ? CastField<FBoolProperty>(PropertyOf(object->GetClassPrivate(), name)) : nullptr;
            if (!p) return false;
            p->SetPropertyValueInContainer(object, value);
            return true;
        }
        bool SetByte(UObject* object, const wchar_t* name, std::uint8_t value)
        {
            FProperty* p = object ? PropertyOf(object->GetClassPrivate(), name) : nullptr;
            if (!p || p->GetSize() != 1) return false;
            *At(object, p) = value;
            return true;
        }
        std::vector<UObject*> GetObjects(UObject* object, const wchar_t* name, std::size_t limit)
        {
            std::vector<UObject*> out;
            auto* p = object ? CastField<FArrayProperty>(PropertyOf(object->GetClassPrivate(), name)) : nullptr;
            if (!p || !CastField<FObjectPropertyBase>(p->GetInner())) return out;
            RawArray raw;
            std::memcpy(&raw, At(object, p), sizeof(raw));
            if (!raw.data || raw.num < 0) return out;
            for (std::int32_t i = 0; i < raw.num && out.size() < limit; ++i) out.push_back(static_cast<UObject**>(raw.data)[i]);
            return out;
        }
        std::wstring GetName(UObject* object, const wchar_t* name)
        {
            FProperty* p = object ? PropertyOf(object->GetClassPrivate(), name) : nullptr;
            if (!p || !CastField<FNameProperty>(p)) return {};
            FName value;
            std::memcpy(&value, At(object, p), sizeof(value));
            return value.ToString();
        }

        // One reflected call; `fill` writes each input parameter by name.
        using Fill = std::function<void(const std::wstring& name, FProperty* p, std::uint8_t* value)>;
        bool Call(UObject* self, const wchar_t* path, const Fill& fill = {}, UObject** returned = nullptr)
        {
            auto* fn = self ? UObjectGlobals::StaticFindObject<UFunction*>(nullptr, nullptr, path) : nullptr;
            if (!fn || fn->GetParmsSize() > 1024) return false;
            alignas(16) std::uint8_t buffer[1024];
            std::memset(buffer, 0, fn->GetParmsSize());
            std::vector<std::uint8_t*> strings;
            FProperty* ret = nullptr;
            for (FProperty* p : fn->ForEachProperty())
            {
                if (!p->HasAnyPropertyFlags(CPF_Parm)) continue;
                if (p->HasAnyPropertyFlags(CPF_ReturnParm)) { ret = p; continue; }
                std::uint8_t* value = buffer + p->GetOffset_Internal();
                if (fill) fill(p->GetName(), p, value);
                if (CastField<FStrProperty>(p)) strings.push_back(value);
            }
            const bool ok = Guarded(self, fn, buffer);
            for (std::uint8_t* s : strings)
            {
                RawArray raw;
                std::memcpy(&raw, s, sizeof(raw));
                if (raw.data) FMemory::Free(raw.data);
            }
            if (ok && returned && ret && CastField<FObjectPropertyBase>(ret)) std::memcpy(returned, buffer + ret->GetOffset_Internal(), sizeof(UObject*));
            return ok;
        }
        UObject* Default(const wchar_t* path) { return UObjectGlobals::StaticFindObject<UObject*>(nullptr, nullptr, path); }

        void WriteFString(std::uint8_t* value, const std::wstring& text)
        {
            const auto length = static_cast<std::int32_t>(text.size());
            auto* chars = static_cast<wchar_t*>(FMemory::Malloc(static_cast<SIZE_T>(length + 1) * sizeof(wchar_t)));
            if (!chars) return;
            std::memcpy(chars, text.c_str(), static_cast<std::size_t>(length + 1) * sizeof(wchar_t));
            RawArray raw{chars, length + 1, length + 1};
            std::memcpy(value, &raw, sizeof(raw));
        }
        void WriteFName(std::uint8_t* value, FProperty* p, const std::wstring& text)
        {
            FName name(text.c_str(), FNAME_Add);
            if (p->GetSize() == sizeof(name)) std::memcpy(value, &name, sizeof(name));
        }
        void WriteFloats(std::uint8_t* value, FProperty* p, std::initializer_list<float> numbers)
        {
            if (static_cast<std::size_t>(p->GetSize()) < numbers.size() * sizeof(float)) return;
            std::size_t i = 0;
            for (float n : numbers) std::memcpy(value + sizeof(float) * i++, &n, sizeof(float));
        }
        // TScriptInterface<ICharacterModelInterface> for a native implementer:
        // the object plus the interface's address inside it.
        bool WriteInterface(std::uint8_t* value, FProperty* p, UObject* object)
        {
            if (!object || p->GetSize() != 2 * static_cast<std::int32_t>(sizeof(void*))) return false;
            for (auto* cls = object->GetClassPrivate(); cls; cls = static_cast<UClass*>(cls->GetSuperStruct()))
            {
                auto& interfaces = cls->GetInterfaces();
                for (std::int32_t i = 0; i < interfaces.Num(); ++i)
                {
                    const FImplementedInterface& entry = interfaces[i];
                    if (!entry.Class || entry.Class->GetName() != STR("CharacterModelInterface")) continue;
                    void* pointers[2] = {object, reinterpret_cast<std::uint8_t*>(object) + entry.PointerOffset};
                    std::memcpy(value, pointers, sizeof(pointers));
                    return true;
                }
            }
            return false;
        }

        void WriteAtomically(const std::filesystem::path& path, const std::string& text)
        {
            auto temporary = path;
            temporary += L".tmp";
            {
                std::ofstream out(temporary, std::ios::binary | std::ios::trunc);
                if (!out) return;
                out.write(text.data(), static_cast<std::streamsize>(text.size()));
                if (!out) return;
            }
            MoveFileExW(temporary.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING);
        }
    } // namespace

    CosmeticsPreview::CosmeticsPreview(game::Scene& scene, std::filesystem::path root) : m_scene(scene), m_root(std::move(root))
    {
        m_requestPath = m_root / L"cosmetics-preview.txt";
        m_framePath = m_root / L"cosmetics-preview-frame.txt";
        m_frames = m_root / L"cosmetics-preview";
    }

    void CosmeticsPreview::Bind()
    {
        m_bound = true;
        m_isBenchmark.BindPath(STR("/Script/GameSkillsTrainer.ScenarioManager:IsCurrentlyInBenchmark"), game::Shape::Bool);
        m_isEditor.BindPath(STR("/Script/GameSkillsTrainer.ScenarioManager:IsInScenarioEditor"), game::Shape::Bool);
        m_isInChallenge.BindPath(STR("/Script/GameSkillsTrainer.ScenarioManager:IsInChallenge"), game::Shape::Bool);
        const wchar_t* required[] = {
            STR("/Script/Engine.KismetRenderingLibrary:CreateRenderTarget2D"), STR("/Script/Engine.KismetRenderingLibrary:ExportRenderTarget"),
            STR("/Script/Engine.SceneCaptureComponent2D:CaptureScene"), STR("/Script/GameSkillsTrainer.CharacterModelFunctionLibrary:ApplyCharacterModel"),
            STR("/Script/GameSkillsTrainer.CharacterSkinFunctionLibrary:ApplyCharacterSkin"),
        };
        for (const wchar_t* path : required)
            if (!game::FindFunction(path)) m_unavailable = "missing " + game::Narrow(path);
        if (!m_isBenchmark.ok() || !m_isEditor.ok() || !m_isInChallenge.ok()) m_unavailable = "scenario state getters unavailable";
        m_available = m_unavailable.empty();
        // A frame from an earlier session is never shown.
        std::error_code error;
        std::filesystem::remove(m_framePath, error);
        Log(m_available ? "cosmetics preview available (runs only while the Cosmetics page is open, never in challenges)"
                        : "cosmetics preview unavailable: " + m_unavailable);
    }

    std::optional<PreviewRequest> CosmeticsPreview::ReadRequest()
    {
        std::ifstream in(m_requestPath, std::ios::binary);
        if (!in) return std::nullopt;
        std::string text(MaxPreviewRequestBytes + 1, '\0');
        in.read(text.data(), static_cast<std::streamsize>(text.size()));
        text.resize(static_cast<std::size_t>(in.gcount()));
        return ParsePreviewRequest(text, static_cast<std::int64_t>(std::time(nullptr)));
    }

    PreviewGameState CosmeticsPreview::GameState(bool inChallenge, bool loading) const
    {
        // Without a scenario manager everything stays unknown, which counts as
        // "no" (the observer's own flags default to false in that case).
        PreviewGameState s;
        if (UObject* manager = m_scene.Manager())
        {
            const auto challenge = m_isInChallenge.Bool(manager);
            s.inChallenge = challenge ? std::optional<bool>(*challenge || inChallenge) : std::nullopt;
            s.loading = loading;
            s.benchmark = m_isBenchmark.Bool(manager);
            s.editor = m_isEditor.Bool(manager);
        }
        return s;
    }

    void CosmeticsPreview::Tick(double now, bool inChallenge, bool loading)
    {
        if (!m_bound) Bind();
        if (!m_available || now < m_nextRead) return;
        m_nextRead = now + ReadInterval;
        const PreviewGameState state = GameState(inChallenge, loading);
        // In a challenge (or unknown state) nothing is read or spawned at all.
        if (state.inChallenge != std::optional<bool>(false) || state.loading != std::optional<bool>(false))
        {
            const char* reason = state.inChallenge != std::optional<bool>(false) ? "challenge or unknown game state" : "loading";
            if (m_lastReason != reason)
            {
                m_lastReason = reason;
                Log(std::string("cosmetics preview: off (") + reason + ")");
            }
            return Teardown(reason);
        }
        const auto request = ReadRequest();
        const PreviewDecision decision = DecidePreview(request, state);
        if (decision.reason != m_lastReason)
        {
            m_lastReason = decision.reason;
            Log(std::string("cosmetics preview: ") + decision.reason);
        }
        UObject* player = m_scene.Player();
        UObject* world = player ? static_cast<AActor*>(player)->GetWorld() : nullptr;
        if (!decision.run || !world) return Teardown(decision.run ? "no world" : decision.reason);
        if (!EnsureStage(world)) return;

        const std::string key = request->LookKey();
        if (key != m_lookKey)
        {
            m_lookKey = key;
            ApplyLook(*request);
            m_dirty = true;
            m_followUps = {now + 0.35, now + 1.2}; // meshes stream in and the stage fades in
        }
        if (request->yaw != m_yaw)
        {
            m_yaw = request->yaw;
            ApplyRotation(m_yaw);
            m_dirty = true;
        }
        m_seq = request->seq;
        for (double& at : m_followUps)
            if (at > 0 && now >= at) { m_dirty = true; at = 0; }
        if (m_dirty && now >= m_nextCapture && Capture())
        {
            m_dirty = false;
            m_nextCapture = now + MinCaptureInterval;
        }
    }

    bool CosmeticsPreview::EnsureStage(UObject* world)
    {
        if (m_stage.Get() && m_world == world) return true;
        if (m_stage.Get() || m_target.Get()) Teardown("world changed");
        if (!m_stageClass) m_stageClass = UObjectGlobals::StaticFindObject<UClass*>(nullptr, nullptr, StageClass);
        if (!m_stageClass) { Teardown("the game's preview stage class is not loaded"); return false; }

        UObject* rendering = Default(STR("/Script/Engine.Default__KismetRenderingLibrary"));
        UObject* target = nullptr;
        Call(rendering, STR("/Script/Engine.KismetRenderingLibrary:CreateRenderTarget2D"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
            if (n == STR("WorldContextObject")) std::memcpy(v, &world, sizeof(world));
            else if (n == STR("Width") || n == STR("Height")) { const std::int32_t s = Size; std::memcpy(v, &s, sizeof(s)); }
            else if (n == STR("Format")) *v = 2; // RTF_RGBA8: exported as PNG
            else if (n == STR("ClearColor")) WriteFloats(v, p, {0.06f, 0.08f, 0.07f, 1.0f});
        }, &target);
        if (!target) { Teardown("render target unavailable"); return false; }

        const FTransform transform{FQuat(FRotator(0, 0, 0)), FVector(0, 0, StageHeight), FVector(1, 1, 1)};
        AActor* stage = UGameplayStatics::BeginDeferredActorSpawnFromClass(world, m_stageClass, transform, ESpawnActorCollisionHandlingMethod::AlwaysSpawn);
        if (!stage) { Teardown("preview stage could not be spawned"); return false; }
        UGameplayStatics::FinishSpawningActor(stage, transform);
        stage->SetActorEnableCollision(false);

        m_stage = FWeakObjectPtr(stage);
        m_target = FWeakObjectPtr(target);
        m_world = world;
        UObject* capture = GetObject(stage, STR("SceneCaptureComponent2D"));
        UObject* meshes = GetObject(stage, STR("Meshes"));
        UObject* mesh = GetObject(stage, STR("SkeletalMesh"));
        m_capture = FWeakObjectPtr(capture);
        m_meshes = FWeakObjectPtr(meshes);
        m_mesh = FWeakObjectPtr(mesh);
        if (!capture || !meshes) { Teardown("preview stage layout changed"); return false; }

        // Our render target, never the game's shared one; capture on demand only;
        // render the stage and nothing else; tone-mapped colour.
        SetObject(capture, STR("TextureTarget"), target);
        SetBool(capture, STR("bCaptureEveryFrame"), false);
        SetBool(capture, STR("bCaptureOnMovement"), false);
        SetByte(capture, STR("PrimitiveRenderMode"), 2); // PRM_UseShowOnlyList
        SetByte(capture, STR("CaptureSource"), 2);       // SCS_FinalColorLDR
        Call(capture, STR("/Script/Engine.SceneCaptureComponent:ShowOnlyActorComponents"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
            if (n == STR("InActor")) std::memcpy(v, &stage, sizeof(stage));
        });
        // The stage's directional light would light the whole map: switch it
        // off (the stage's point lights do not reach 5 km down). No shadows.
        if (UObject* sun = GetObject(stage, STR("DirectionalLight")))
            if (!Call(sun, STR("/Script/Engine.SceneComponent:SetVisibility"), [](const std::wstring&, FProperty*, std::uint8_t* v) { *v = 0; }))
            {
                // Never leave a second sun over the map.
                Teardown("the stage light could not be switched off");
                return false;
            }
        // The stage's point lights: short reach (well under the 5 km to the map), no shadows.
        for (const wchar_t* name : {STR("PointLight"), STR("PointLight1")})
            if (UObject* light = GetObject(stage, name))
            {
                float radius = 1e9f;
                if (FProperty* p = PropertyOf(light->GetClassPrivate(), STR("AttenuationRadius")); p && p->GetSize() == 4) std::memcpy(&radius, At(light, p), 4);
                if (radius > 3000.0f)
                    Call(light, STR("/Script/Engine.PointLightComponent:SetAttenuationRadius"), [](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                    if (n == STR("NewRadius")) WriteFloats(v, p, {3000.0f});
                });
                Call(light, STR("/Script/Engine.LightComponent:SetCastShadows"), [](const std::wstring&, FProperty*, std::uint8_t* v) { *v = 0; });
            }
        // No part of the stage casts a shadow (the map's sun would otherwise
        // project the floating stage onto the map).
        for (const wchar_t* name : {STR("Wall"), STR("Floor"), STR("SkeletalMesh"), STR("Weapon1"), STR("Weapon2"), STR("SphereBody"), STR("SphereHead"),
                                    STR("CubeBody"), STR("CubeHead"), STR("CylinderBody"), STR("CylinderBottom"), STR("CylinderHead"), STR("CylinderTop")})
            if (UObject* part = GetObject(stage, name))
                Call(part, STR("/Script/Engine.PrimitiveComponent:SetCastShadow"), [](const std::wstring&, FProperty*, std::uint8_t* v) { *v = 0; });

        m_baseYaw = 0;
        if (FProperty* p = PropertyOf(meshes->GetClassPrivate(), STR("RelativeRotation")); p && p->GetSize() == 12)
        {
            float rotation[3];
            std::memcpy(rotation, At(meshes, p), sizeof(rotation));
            m_baseYaw = rotation[1];
        }
        m_lookKey.clear();
        m_yaw = 1e9;
        m_originals.clear();
        std::error_code error;
        std::filesystem::create_directories(m_frames, error);
        Log("cosmetics preview stage spawned");
        return true;
    }

    bool CosmeticsPreview::FreeLook(const std::string& model, const std::string& skin) const
    {
        // Only names from the game's free Default packs; anything else (a DLC
        // model or skin) is never applied, even to the preview.
        const std::wstring wantModel = Widen(model), wantSkin = Widen(skin);
        bool modelOk = false, skinOk = skin.empty() || skin == "Default";
        for (UObject* asset : GetObjects(Default(ModelPack), STR("Models"), 64))
            if (asset && GetName(GetObject(asset, STR("CharacterModel")), STR("Name")) == wantModel) modelOk = true;
        if (!skinOk)
            for (UObject* asset : GetObjects(Default(SkinPack), STR("Skins"), 128))
                if (asset && GetName(GetObject(asset, STR("CharacterSkin")), STR("Name")) == wantSkin) skinOk = true;
        return modelOk && skinOk;
    }

    void CosmeticsPreview::ApplyLook(const PreviewRequest& request)
    {
        AActor* stage = static_cast<AActor*>(m_stage.Get());
        if (!stage) return;
        if (FreeLook(request.model, request.skin))
        {
            Call(Default(STR("/Script/GameSkillsTrainer.Default__CharacterModelFunctionLibrary")), STR("/Script/GameSkillsTrainer.CharacterModelFunctionLibrary:ApplyCharacterModel"),
                 [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                     if (n == STR("ModelName")) WriteFName(v, p, Widen(request.model));
                     else if (n == STR("Character")) WriteInterface(v, p, stage);
                 });
            if (!request.skin.empty())
                Call(Default(STR("/Script/GameSkillsTrainer.Default__CharacterSkinFunctionLibrary")), STR("/Script/GameSkillsTrainer.CharacterSkinFunctionLibrary:ApplyCharacterSkin"),
                     [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                         if (n == STR("SkinName")) WriteFName(v, p, Widen(request.skin));
                         else if (n == STR("ModelName")) WriteFName(v, p, Widen(request.model));
                         else if (n == STR("Character")) WriteInterface(v, p, stage);
                     });
        }
        else Log("cosmetics preview: look is not a free Default-pack look; showing the stage default");

        // Catalog parameters on fresh dynamic instances parented on the look's
        // own materials (the stage is AimMod's; nothing to restore elsewhere).
        UObject* mesh = GetObject(stage, STR("SkeletalMesh"));
        m_mesh = FWeakObjectPtr(mesh);
        if (!mesh || (request.vectors.empty() && request.scalars.empty())) return;
        UObject* materials = Default(STR("/Script/Engine.Default__KismetMaterialLibrary"));
        UObject* world = m_world;
        for (std::int32_t slot = 0; slot < 16; ++slot)
        {
            UObject* material = nullptr;
            Call(mesh, STR("/Script/Engine.PrimitiveComponent:GetMaterial"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                if (n == STR("ElementIndex")) std::memcpy(v, &slot, sizeof(slot));
            }, &material);
            if (!material) break;
            UObject* mid = nullptr;
            Call(materials, STR("/Script/Engine.KismetMaterialLibrary:CreateDynamicMaterialInstance"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                if (n == STR("WorldContextObject")) std::memcpy(v, &world, sizeof(world));
                else if (n == STR("Parent")) std::memcpy(v, &material, sizeof(material));
            }, &mid);
            if (!mid) continue;
            for (const PreviewParam& param : request.vectors)
                Call(mid, STR("/Script/Engine.MaterialInstanceDynamic:SetVectorParameterValue"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                    if (n == STR("ParameterName")) WriteFName(v, p, Widen(param.name));
                    else if (n == STR("Value"))
                        WriteFloats(v, p, {static_cast<float>(param.value[0]), static_cast<float>(param.value[1]), static_cast<float>(param.value[2]), static_cast<float>(param.value[3])});
                });
            for (const PreviewParam& param : request.scalars)
                Call(mid, STR("/Script/Engine.MaterialInstanceDynamic:SetScalarParameterValue"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                    if (n == STR("ParameterName")) WriteFName(v, p, Widen(param.name));
                    else if (n == STR("Value")) WriteFloats(v, p, {static_cast<float>(param.value[0])});
                });
            Call(mesh, STR("/Script/Engine.PrimitiveComponent:SetMaterial"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                if (n == STR("ElementIndex")) std::memcpy(v, &slot, sizeof(slot));
                else if (n == STR("Material")) std::memcpy(v, &mid, sizeof(mid));
            });
        }
    }

    void CosmeticsPreview::ApplyRotation(double yaw)
    {
        UObject* meshes = m_meshes.Get();
        if (!meshes) return;
        Call(meshes, STR("/Script/Engine.SceneComponent:K2_SetRelativeRotation"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
            if (n == STR("NewRotation")) WriteFloats(v, p, {0.0f, static_cast<float>(m_baseYaw + yaw), 0.0f});
            else if (n == STR("bTeleport")) *v = 1;
        });
    }

    bool CosmeticsPreview::Capture()
    {
        UObject* capture = m_capture.Get();
        UObject* target = m_target.Get();
        if (!capture || !target || !m_world) return false;
        if (!Call(capture, STR("/Script/Engine.SceneCaptureComponent2D:CaptureScene"))) return false;
        // Alternate two files so the service never serves a half-written PNG.
        const std::wstring file = m_frameIndex == 0 ? L"preview-0.png" : L"preview-1.png";
        const std::wstring folder = m_frames.wstring();
        UObject* world = m_world;
        if (!Call(Default(STR("/Script/Engine.Default__KismetRenderingLibrary")), STR("/Script/Engine.KismetRenderingLibrary:ExportRenderTarget"),
                  [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                      if (n == STR("WorldContextObject")) std::memcpy(v, &world, sizeof(world));
                      else if (n == STR("TextureRenderTarget")) std::memcpy(v, &target, sizeof(target));
                      else if (n == STR("FilePath")) WriteFString(v, folder);
                      else if (n == STR("FileName") || n == STR("Filename")) WriteFString(v, file);
                  }))
            return false;
        std::error_code error;
        if (!std::filesystem::is_regular_file(m_frames / file, error)) return false;
        m_frameIndex ^= 1;
        WriteAtomically(m_framePath, FormatPreviewFrame(++m_frameSeq, game::Narrow(file), Size, Size));
        return true;
    }

    void CosmeticsPreview::Teardown(const char* why)
    {
        const bool had = m_stage.Get() || m_target.Get();
        if (AActor* stage = static_cast<AActor*>(m_stage.Get())) stage->K2_DestroyActor();
        if (UObject* target = m_target.Get())
            Call(Default(STR("/Script/Engine.Default__KismetRenderingLibrary")), STR("/Script/Engine.KismetRenderingLibrary:ReleaseRenderTarget2D"),
                 [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                     if (n == STR("TextureRenderTarget")) std::memcpy(v, &target, sizeof(target));
                 });
        m_stage = m_target = m_capture = m_meshes = m_mesh = FWeakObjectPtr{};
        m_world = nullptr;
        m_lookKey.clear();
        m_followUps.clear();
        m_dirty = false;
        if (had)
        {
            std::error_code error;
            std::filesystem::remove(m_framePath, error);
            Log(std::string("cosmetics preview stage removed (") + why + ")");
        }
    }

    // May run off the game thread (mod unload): no engine calls. The stage is
    // inert without the tick (capture on demand only) and goes with the level.
    void CosmeticsPreview::Shutdown()
    {
        m_stage = m_target = m_capture = m_meshes = m_mesh = FWeakObjectPtr{};
        m_world = nullptr;
        std::error_code error;
        std::filesystem::remove(m_framePath, error);
    }
} // namespace aimmod

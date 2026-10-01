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
#include <Unreal/Property/FStructProperty.hpp>
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
#include <wincodec.h>

#include <algorithm>
#include <cmath>
#include <cstring>
#include <ctime>
#include <fstream>
#include <functional>
#include <iterator>
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
        // lights (900 cm reach) cannot reach the map.
        constexpr double StageHeight = 500000.0;
        constexpr int Size = PreviewSize, CaptureSize = PreviewSize * PreviewSupersample;
        constexpr double ReadInterval = 0.2, MinCaptureInterval = 0.15;
        constexpr float Fov = 30.0f, LightReach = 900.0f;
        constexpr std::uint8_t SourceFinalColor = 2, SourceNormal = 6; // ESceneCaptureSource
        constexpr std::uint8_t Movable = 2;                              // EComponentMobility
        constexpr std::uint8_t ExposureBasic = 1;                        // EAutoExposureMethod
        constexpr std::uint8_t Unitless = 0, Candelas = 1, Lumens = 2;   // ELightUnits
        constexpr double Pi = 3.14159265358979;

        // The preview's own light rig, placed around the character relative to
        // the camera (cm along the view, to the right, up), in candela.
        struct RigLight
        {
            float forward, right, up;
            float color[3];
            float candela;
        };
        constexpr RigLight Rig[3] = {
            {-220, -160, 140, {1.00f, 0.95f, 0.88f}, 32}, // key: camera left, above
            {-240, 220, 10, {0.78f, 0.88f, 1.00f}, 11},   // fill: camera right, low
            {200, 130, 170, {0.70f, 1.00f, 0.86f}, 26},   // rim: behind, mint
        };
        // Stage parts that are not the character: never rendered by the preview.
        constexpr const wchar_t* HiddenParts[] = {STR("StaticMeshes"), STR("Cylinder"),       STR("Cube"),         STR("Sphere"),       STR("SphereBody"),
                                                  STR("SphereHead"),   STR("CubeBody"),       STR("CubeHead"),     STR("CylinderBody"), STR("CylinderBottom"),
                                                  STR("CylinderTop"),  STR("CylinderHead"),   STR("Weapon1"),      STR("Weapon2"),      STR("Wall"),
                                                  STR("Floor")};

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
        std::optional<std::uint8_t> GetByte(UObject* object, const wchar_t* name)
        {
            FProperty* p = object ? PropertyOf(object->GetClassPrivate(), name) : nullptr;
            if (!p || p->GetSize() != 1) return std::nullopt;
            return *At(object, p);
        }
        bool SetFloat(UObject* object, const wchar_t* name, float value)
        {
            FProperty* p = object ? PropertyOf(object->GetClassPrivate(), name) : nullptr;
            if (!p || p->GetSize() != 4) return false;
            std::memcpy(At(object, p), &value, 4);
            return true;
        }
        std::optional<float> GetFloat(UObject* object, const wchar_t* name)
        {
            FProperty* p = object ? PropertyOf(object->GetClassPrivate(), name) : nullptr;
            if (!p || p->GetSize() != 4) return std::nullopt;
            float value;
            std::memcpy(&value, At(object, p), 4);
            return value;
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

        // TSoftObjectPtr / TSoftClassPtr (UE 4.26: weak pointer, tag, then
        // FSoftObjectPath{FName AssetPathName, FString SubPath}): the asset path.
        constexpr std::int32_t SoftPtrSize = 0x28, SoftPathOffset = 0x10;
        std::wstring SoftPathAt(const std::uint8_t* value)
        {
            FName name;
            std::memcpy(&name, value + SoftPathOffset, sizeof(name));
            std::wstring path = name.ToString();
            return path == STR("None") ? std::wstring{} : path;
        }
        std::wstring SoftPath(UObject* object, const wchar_t* name)
        {
            FProperty* p = object ? PropertyOf(object->GetClassPrivate(), name) : nullptr;
            if (!p || p->GetSize() != SoftPtrSize) return {};
            return SoftPathAt(At(object, p));
        }
        std::vector<std::wstring> SoftPaths(UObject* object, const wchar_t* name, std::size_t limit)
        {
            std::vector<std::wstring> out;
            auto* p = object ? CastField<FArrayProperty>(PropertyOf(object->GetClassPrivate(), name)) : nullptr;
            if (!p || p->GetInner()->GetSize() != SoftPtrSize) return out;
            RawArray raw;
            std::memcpy(&raw, At(object, p), sizeof(raw));
            if (!raw.data || raw.num < 0) return out;
            for (std::int32_t i = 0; i < raw.num && out.size() < limit; ++i) out.push_back(SoftPathAt(static_cast<const std::uint8_t*>(raw.data) + i * SoftPtrSize));
            return out;
        }
        // Only the game's own content, reached through its Default packs.
        UObject* LoadGameAsset(const std::wstring& path)
        {
            if (path.rfind(STR("/Game/"), 0) != 0 || path.find(STR("..")) != std::wstring::npos) return nullptr;
            return game::FindOrLoadAsset(path);
        }

        // One reflected call; `fill` writes each input parameter by name and
        // `read` sees every parameter (outputs, return value) afterwards.
        using Fill = std::function<void(const std::wstring& name, FProperty* p, std::uint8_t* value)>;
        using Read = std::function<void(const std::wstring& name, FProperty* p, const std::uint8_t* value)>;
        bool Call(UObject* self, const wchar_t* path, const Fill& fill = {}, UObject** returned = nullptr, const Read& read = {})
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
            if (ok && read)
                for (FProperty* p : fn->ForEachProperty())
                    if (p->HasAnyPropertyFlags(CPF_Parm)) read(p->GetName(), p, buffer + p->GetOffset_Internal());
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
        bool ReadFloats(const std::uint8_t* value, FProperty* p, float* out, std::size_t count)
        {
            if (static_cast<std::size_t>(p->GetSize()) < count * sizeof(float)) return false;
            std::memcpy(out, value, count * sizeof(float));
            return true;
        }
        void WriteObject(std::uint8_t* value, UObject* object) { std::memcpy(value, &object, sizeof(object)); }
        // Bool parameter (plain or bitfield) by property.
        void WriteBoolParam(std::uint8_t* value, FProperty* p, bool on)
        {
            if (auto* b = CastField<FBoolProperty>(p)) b->SetPropertyValue(value, on);
        }

        // Members of the capture's PostProcessSettings struct, by name.
        struct PostProcess
        {
            std::uint8_t* value{};
            UStruct* type{};
            explicit PostProcess(UObject* capture)
            {
                auto* p = capture ? CastField<FStructProperty>(PropertyOf(capture->GetClassPrivate(), STR("PostProcessSettings"))) : nullptr;
                if (p) value = At(capture, p), type = p->GetStruct();
            }
            bool Set(const std::string& field, double number) const { return value && game::SetStructField(value, type, field.c_str(), number); }
            // The member and its bOverride_ flag.
            bool Override(const std::string& field, double number) const { return Set("bOverride_" + field, 1) && Set(field, number); }
            std::optional<double> Get(const std::string& field) const
            {
                if (!value) return std::nullopt;
                for (FProperty* p : type->ForEachProperty())
                    if (game::Narrow(p->GetName()) == field) return game::ReadNumber(value + p->GetOffset_Internal(), game::Describe(p).kind);
                return std::nullopt;
            }
        };

        // PNG in and out through the Windows Imaging Component.
        template <class T>
        struct Com
        {
            T* p{};
            Com() = default;
            Com(const Com&) = delete;
            Com& operator=(const Com&) = delete;
            ~Com()
            {
                if (p) p->Release();
            }
            T** operator&() { return &p; }
            T* operator->() const { return p; }
        };
        struct ComScope
        {
            HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
            ~ComScope()
            {
                if (SUCCEEDED(hr)) CoUninitialize();
            }
        };
        bool ReadPng(const std::filesystem::path& path, PreviewPixels& out)
        {
            ComScope com;
            Com<IWICImagingFactory> factory;
            Com<IWICBitmapDecoder> decoder;
            Com<IWICBitmapFrameDecode> frame;
            Com<IWICFormatConverter> converter;
            UINT w = 0, h = 0;
            if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory.p))) ||
                FAILED(factory->CreateDecoderFromFilename(path.c_str(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnDemand, &decoder)) ||
                FAILED(decoder->GetFrame(0, &frame)) || FAILED(factory->CreateFormatConverter(&converter)) ||
                FAILED(converter->Initialize(frame.p, GUID_WICPixelFormat32bppRGBA, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom)) ||
                FAILED(converter->GetSize(&w, &h)) || w == 0 || h == 0 || w > 4096 || h > 4096)
                return false;
            out.width = static_cast<int>(w);
            out.height = static_cast<int>(h);
            out.rgba.resize(static_cast<std::size_t>(w) * h * 4);
            return SUCCEEDED(converter->CopyPixels(nullptr, w * 4, static_cast<UINT>(out.rgba.size()), out.rgba.data()));
        }
        bool WritePng(const std::filesystem::path& path, const PreviewPixels& image)
        {
            if (!image.Valid()) return false;
            ComScope com;
            Com<IWICImagingFactory> factory;
            Com<IWICStream> stream;
            Com<IWICBitmapEncoder> encoder;
            Com<IWICBitmapFrameEncode> frame;
            if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory.p))) || FAILED(factory->CreateStream(&stream)) ||
                FAILED(stream->InitializeFromFilename(path.c_str(), GENERIC_WRITE)) || FAILED(factory->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder)) ||
                FAILED(encoder->Initialize(stream.p, WICBitmapEncoderNoCache)) || FAILED(encoder->CreateNewFrame(&frame, nullptr)) || FAILED(frame->Initialize(nullptr)) ||
                FAILED(frame->SetSize(static_cast<UINT>(image.width), static_cast<UINT>(image.height))))
                return false;
            WICPixelFormatGUID format = GUID_WICPixelFormat32bppBGRA;
            if (FAILED(frame->SetPixelFormat(&format)) || format != GUID_WICPixelFormat32bppBGRA) return false;
            std::vector<std::uint8_t> bgra(image.rgba);
            for (std::size_t i = 0; i < bgra.size(); i += 4) std::swap(bgra[i], bgra[i + 2]);
            return SUCCEEDED(frame->WritePixels(static_cast<UINT>(image.height), static_cast<UINT>(image.width) * 4, static_cast<UINT>(bgra.size()), bgra.data())) &&
                   SUCCEEDED(frame->Commit()) && SUCCEEDED(encoder->Commit());
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

        bool Location(UObject* component, float out[3])
        {
            bool ok = false;
            Call(component, STR("/Script/Engine.SceneComponent:K2_GetComponentLocation"), {}, nullptr, [&](const std::wstring& n, FProperty* p, const std::uint8_t* v) {
                if (n == STR("ReturnValue")) ok = ReadFloats(v, p, out, 3);
            });
            return ok;
        }
        bool SetWorldLocation(UObject* component, const float at[3])
        {
            return Call(component, STR("/Script/Engine.SceneComponent:K2_SetWorldLocation"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                if (n == STR("NewLocation")) WriteFloats(v, p, {at[0], at[1], at[2]});
                else if (n == STR("bTeleport")) WriteBoolParam(v, p, true);
            });
        }
        bool SetVisible(UObject* component, bool visible, bool propagate)
        {
            const bool a = Call(component, STR("/Script/Engine.SceneComponent:SetVisibility"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                if (n == STR("bNewVisibility")) WriteBoolParam(v, p, visible);
                else if (n == STR("bPropagateToChildren")) WriteBoolParam(v, p, propagate);
            });
            const bool b = Call(component, STR("/Script/Engine.SceneComponent:SetHiddenInGame"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                if (n == STR("NewHidden")) WriteBoolParam(v, p, !visible);
                else if (n == STR("bPropagateToChildren")) WriteBoolParam(v, p, propagate);
            });
            return a && b;
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
            STR("/Script/Engine.SceneCaptureComponent2D:CaptureScene"),        STR("/Script/Engine.SkinnedMeshComponent:SetSkeletalMesh"),
            STR("/Script/Engine.KismetSystemLibrary:GetComponentBounds"),      STR("/Script/Engine.SceneComponent:K2_SetWorldLocationAndRotation"),
            STR("/Script/Engine.LocalLightComponent:SetAttenuationRadius"),
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
            m_followUps = {now + 0.35, now + 1.2}; // meshes and textures stream in
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
        if (!m_stageClass) m_stageClass = static_cast<UClass*>(LoadGameAsset(StageClass));
        if (!m_stageClass) { Teardown("the game's preview stage class is not loaded"); return false; }

        UObject* rendering = Default(STR("/Script/Engine.Default__KismetRenderingLibrary"));
        UObject* target = nullptr;
        Call(rendering, STR("/Script/Engine.KismetRenderingLibrary:CreateRenderTarget2D"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
            if (n == STR("WorldContextObject")) std::memcpy(v, &world, sizeof(world));
            else if (n == STR("Width") || n == STR("Height")) { const std::int32_t s = CaptureSize; std::memcpy(v, &s, sizeof(s)); }
            else if (n == STR("Format")) *v = 2; // RTF_RGBA8: exported as PNG
            else if (n == STR("ClearColor")) WriteFloats(v, p, {0, 0, 0, 1});
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
        if (!capture || !meshes || !mesh) { Teardown("preview stage layout changed"); return false; }

        // Our render target, never the game's shared one; capture on demand only;
        // render the stage and nothing else.
        SetObject(capture, STR("TextureTarget"), target);
        SetBool(capture, STR("bCaptureEveryFrame"), false);
        SetBool(capture, STR("bCaptureOnMovement"), false);
        SetByte(capture, STR("PrimitiveRenderMode"), 2); // PRM_UseShowOnlyList
        SetByte(capture, STR("CaptureSource"), SourceFinalColor);
        SetFloat(capture, STR("FOVAngle"), Fov);
        SetFloat(capture, STR("PostProcessBlendWeight"), 1.0f);
        Call(capture, STR("/Script/Engine.SceneCaptureComponent:ShowOnlyActorComponents"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
            if (n == STR("InActor")) std::memcpy(v, &stage, sizeof(stage));
        });
        // Fixed exposure: a scene capture keeps no eye-adaptation history, so
        // auto exposure starts from nothing and leaves the capture near black.
        // Min = max brightness pins it. The unit is luminance, or EV100 when the
        // project extends the luminance range (then the defaults are negative).
        const PostProcess post(capture);
        const bool ev100 = post.Get("AutoExposureMinBrightness").value_or(0) < 0;
        const double brightness = ev100 ? 3.0 : 1.0; // average luminance 1 (EV100 3 = 0.125 * 2^3)
        const bool exposure = post.Override("AutoExposureMethod", ExposureBasic) && post.Override("AutoExposureMinBrightness", brightness) &&
                              post.Override("AutoExposureMaxBrightness", brightness) && post.Override("AutoExposureBias", 0);
        // No lens effects on a product shot; a little bloom only.
        post.Override("BloomIntensity", 0.2);
        post.Override("VignetteIntensity", 0);
        post.Override("MotionBlurAmount", 0);
        post.Override("LensFlareIntensity", 0);
        post.Override("GrainIntensity", 0);
        if (!exposure) Log("cosmetics preview: fixed exposure unavailable; the frame is levelled after capture only");

        // The stage's directional light would light the whole map: switch it
        // off. The preview has its own short-range rig instead (Frame()).
        if (UObject* sun = GetObject(stage, STR("DirectionalLight")))
            if (!SetVisible(sun, false, false))
            {
                // Never leave a second sun over the map.
                Teardown("the stage light could not be switched off");
                return false;
            }
        m_lights.clear();
        std::vector<UObject*> lights;
        for (const wchar_t* name : {STR("PointLight"), STR("PointLight1")})
            if (UObject* light = GetObject(stage, name)) lights.push_back(light);
        if (UClass* pointLight = UObjectGlobals::StaticFindObject<UClass*>(nullptr, nullptr, STR("/Script/Engine.PointLightComponent")); pointLight && lights.size() < 3)
        {
            UObject* added = nullptr;
            Call(stage, STR("/Script/Engine.Actor:AddComponentByClass"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                if (n == STR("Class")) WriteObject(v, pointLight);
                else if (n == STR("RelativeTransform"))
                    if (auto* s = CastField<FStructProperty>(p))
                        for (const char* field : {"Rotation.W", "Scale3D.X", "Scale3D.Y", "Scale3D.Z"}) game::SetStructPath(v, s->GetStruct(), field, 1);
            }, &added);
            if (added && added->IsA(pointLight)) lights.push_back(added);
        }
        bool channels = true;
        for (UObject* light : lights)
        {
            Call(light, STR("/Script/Engine.SceneComponent:SetMobility"), [](const std::wstring&, FProperty*, std::uint8_t* v) { *v = Movable; });
            Call(light, STR("/Script/Engine.LocalLightComponent:SetAttenuationRadius"), [](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                if (n == STR("NewRadius")) WriteFloats(v, p, {LightReach});
            });
            Call(light, STR("/Script/Engine.LightComponentBase:SetCastShadows"), [](const std::wstring&, FProperty* p, std::uint8_t* v) { WriteBoolParam(v, p, false); });
            // Never a light that could reach the map: if the reach did not take, it stays off.
            if (GetFloat(light, STR("AttenuationRadius")).value_or(1e9f) > LightReach + 1)
            {
                SetVisible(light, false, false);
                continue;
            }
            // Channel 1 only, as the character below: the map's own lights (channel 0) leave it alone.
            channels &= Call(light, STR("/Script/Engine.LightComponent:SetLightingChannels"), [](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                WriteBoolParam(v, p, n == STR("bChannel1"));
            });
            m_lights.push_back(FWeakObjectPtr(light));
        }
        if (m_lights.empty()) Log("cosmetics preview: no light rig; the frame is levelled after capture only");
        // The character joins the rig's channel only if every rig light is on it;
        // otherwise everything stays on the default channel.
        const bool meshChannel = channels && !m_lights.empty() &&
                                 Call(mesh, STR("/Script/Engine.PrimitiveComponent:SetLightingChannels"), [](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                                     WriteBoolParam(v, p, n == STR("bChannel1"));
                                 });
        if (!meshChannel)
            for (const FWeakObjectPtr& weak : m_lights)
                Call(weak.Get(), STR("/Script/Engine.LightComponent:SetLightingChannels"), [](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                    WriteBoolParam(v, p, n == STR("bChannel0"));
                });

        // Only the character is rendered: the shape models, weapons, wall and
        // floor are hidden (the backdrop is composed after capture). No part
        // of the stage casts a shadow onto the map.
        for (const wchar_t* name : HiddenParts)
            if (UObject* part = GetObject(stage, name)) SetVisible(part, false, true);
        for (const wchar_t* name : HiddenParts)
            if (UObject* part = GetObject(stage, name)) Call(part, STR("/Script/Engine.PrimitiveComponent:SetCastShadow"), [](const std::wstring&, FProperty* p, std::uint8_t* v) { WriteBoolParam(v, p, false); });
        Call(mesh, STR("/Script/Engine.PrimitiveComponent:SetCastShadow"), [](const std::wstring&, FProperty* p, std::uint8_t* v) { WriteBoolParam(v, p, false); });

        m_baseYaw = 0;
        if (FProperty* p = PropertyOf(meshes->GetClassPrivate(), STR("RelativeRotation")); p && p->GetSize() == 12)
        {
            float rotation[3];
            std::memcpy(rotation, At(meshes, p), sizeof(rotation));
            m_baseYaw = rotation[1];
        }
        // The stage camera's own position marks the front of the character.
        if (!Location(capture, m_cameraHome))
        {
            m_cameraHome[0] = m_cameraHome[1] = 0;
            m_cameraHome[2] = static_cast<float>(StageHeight);
        }
        m_lookKey.clear();
        m_yaw = 1e9;
        std::error_code error;
        std::filesystem::create_directories(m_frames, error);
        Log("cosmetics preview stage spawned (" + std::to_string(m_lights.size()) + " rig lights" + (exposure ? ", fixed exposure" : "") + ")");
        return true;
    }

    std::optional<CosmeticsPreview::Look> CosmeticsPreview::FreeLook(const std::string& model, const std::string& skin)
    {
        // Only looks from the game's free Default packs; anything else (a DLC
        // model or skin) is never applied, even to the preview. The packs are
        // loaded on demand: outside the character menu the game has not loaded them.
        UObject* modelPack = LoadGameAsset(ModelPack);
        UObject* skinPack = LoadGameAsset(SkinPack);
        const std::wstring wantModel = Widen(model), wantSkin = Widen(skin);
        UObject* found = nullptr;
        std::string seen;
        for (UObject* asset : GetObjects(modelPack, STR("Models"), 64))
        {
            UObject* candidate = asset ? GetObject(asset, STR("CharacterModel")) : nullptr;
            const std::wstring name = GetName(candidate, STR("Name"));
            seen += (seen.empty() ? "" : ",") + game::Narrow(name);
            if (name == wantModel) found = candidate;
        }
        if (!found)
        {
            if (m_logPacks) Log("cosmetics preview: " + model + " is not in the Default model pack (" + (modelPack ? seen : std::string("pack not loaded")) + ")");
            m_logPacks = false;
            return std::nullopt;
        }
        Look look;
        look.mesh = LoadGameAsset(SoftPath(found, STR("SkeletalMesh")));
        if (UObject* anim = LoadGameAsset(SoftPath(found, STR("AnimationBlueprintClass"))); anim && anim->IsA<UClass>()) look.anim = static_cast<UClass*>(anim);
        if (!skin.empty() && skin != "Default")
        {
            UObject* chosen = nullptr;
            for (UObject* asset : GetObjects(skinPack, STR("Skins"), 128))
            {
                UObject* candidate = asset ? GetObject(asset, STR("CharacterSkin")) : nullptr;
                const std::wstring owner = GetName(candidate, STR("OwningCharacterModelName"));
                if (GetName(candidate, STR("Name")) == wantSkin && (owner.empty() || owner == STR("None") || owner == wantModel)) chosen = candidate;
            }
            if (chosen)
            {
                if (UObject* mesh = LoadGameAsset(SoftPath(chosen, STR("SkeletalMesh")))) look.mesh = mesh;
                for (const std::wstring& path : SoftPaths(chosen, STR("Materials"), 16)) look.materials.push_back(LoadGameAsset(path));
            }
            else if (m_logPacks)
            {
                Log("cosmetics preview: skin " + skin + " is not in the Default skin pack; showing " + model + "'s default skin");
                m_logPacks = false;
            }
        }
        if (!look.mesh)
        {
            Log("cosmetics preview: " + model + "'s mesh could not be loaded");
            return std::nullopt;
        }
        return look;
    }

    void CosmeticsPreview::ApplyLook(const PreviewRequest& request)
    {
        AActor* stage = static_cast<AActor*>(m_stage.Get());
        UObject* mesh = m_mesh.Get();
        if (!stage || !mesh) return;
        // The requested model and skin go straight onto the stage's skeletal
        // mesh: mesh, animation (one evaluation gives a standing pose, even
        // paused) and the skin's materials.
        if (const auto look = FreeLook(request.model, request.skin))
        {
            Call(mesh, STR("/Script/Engine.SkinnedMeshComponent:SetSkeletalMesh"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                if (n == STR("NewMesh")) WriteObject(v, look->mesh);
                else if (n == STR("bReinitPose")) WriteBoolParam(v, p, true);
            });
            if (look->anim)
            {
                Call(mesh, STR("/Script/Engine.SkeletalMeshComponent:SetAnimationMode"), [](const std::wstring&, FProperty*, std::uint8_t* v) { *v = 0; }); // AnimationBlueprint
                Call(mesh, STR("/Script/Engine.SkeletalMeshComponent:SetAnimClass"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                    if (n == STR("NewClass")) WriteObject(v, look->anim);
                });
            }
            // Every slot gets the skin's material, or none (the mesh's own): nothing
            // from the previous look stays behind.
            std::int32_t slots = 0;
            Call(mesh, STR("/Script/Engine.PrimitiveComponent:GetNumMaterials"), {}, nullptr, [&](const std::wstring& n, FProperty* p, const std::uint8_t* v) {
                if (n == STR("ReturnValue") && p->GetSize() == 4) std::memcpy(&slots, v, 4);
            });
            slots = std::clamp<std::int32_t>(std::max<std::int32_t>(slots, static_cast<std::int32_t>(look->materials.size())), 0, 16);
            for (std::int32_t slot = 0; slot < slots; ++slot)
            {
                UObject* material = slot < static_cast<std::int32_t>(look->materials.size()) ? look->materials[static_cast<std::size_t>(slot)] : nullptr;
                Call(mesh, STR("/Script/Engine.PrimitiveComponent:SetMaterial"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                    if (n == STR("ElementIndex")) std::memcpy(v, &slot, sizeof(slot));
                    else if (n == STR("Material")) WriteObject(v, material);
                });
            }
            Log("cosmetics preview: showing " + request.model + (request.skin.empty() ? "" : " " + request.skin) + (look->anim ? "" : " (no animation)"));
        }
        else Log("cosmetics preview: look is not a free Default-pack look; showing the stage default");
        SetVisible(mesh, true, false);
        Call(mesh, STR("/Script/Engine.ActorComponent:SetTickableWhenPaused"), [](const std::wstring&, FProperty* p, std::uint8_t* v) { WriteBoolParam(v, p, true); });

        // Catalog parameters on fresh dynamic instances parented on the look's
        // own materials (the stage is AimMod's; nothing to restore elsewhere).
        if (!request.vectors.empty() || !request.scalars.empty())
        {
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
        Frame();
    }

    // Camera and lights on the character: the camera keeps the stage's own
    // front view, moves back until the whole character (turned any way) fits
    // a 30 degree view, and the rig sits around it. ComposePreview then frames
    // the frame exactly on the silhouette.
    void CosmeticsPreview::Frame()
    {
        UObject* mesh = m_mesh.Get();
        UObject* meshes = m_meshes.Get();
        UObject* capture = m_capture.Get();
        if (!mesh || !meshes || !capture) return;
        float origin[3]{}, extent[3]{}, pivot[3]{};
        bool bounds = false;
        Call(Default(STR("/Script/Engine.Default__KismetSystemLibrary")), STR("/Script/Engine.KismetSystemLibrary:GetComponentBounds"),
             [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                 if (n == STR("Component")) WriteObject(v, mesh);
             },
             nullptr,
             [&](const std::wstring& n, FProperty* p, const std::uint8_t* v) {
                 if (n == STR("Origin")) bounds = ReadFloats(v, p, origin, 3);
                 else if (n == STR("BoxExtent")) bounds = bounds && ReadFloats(v, p, extent, 3);
             });
        if (!Location(meshes, pivot)) Location(mesh, pivot);
        const auto sane = [](float e) { return std::isfinite(e) && e >= 5.0f && e <= 600.0f; };
        if (!bounds || !sane(extent[0]) || !sane(extent[1]) || !sane(extent[2]) || std::abs(origin[2] - pivot[2]) > 600)
        {
            // A standing character on the stage's pivot.
            origin[0] = pivot[0], origin[1] = pivot[1], origin[2] = pivot[2] + 90;
            extent[0] = extent[1] = 40, extent[2] = 95;
        }
        const float centre[3] = {pivot[0], pivot[1], origin[2]};
        float dir[2] = {centre[0] - m_cameraHome[0], centre[1] - m_cameraHome[1]};
        const float length = std::hypot(dir[0], dir[1]);
        if (length < 1.0f) dir[0] = 1, dir[1] = 0;
        else dir[0] /= length, dir[1] /= length;
        const float right[2] = {-dir[1], dir[0]};
        const double halfHeight = extent[2];
        const double halfWidth = std::hypot(extent[0], extent[1]) + std::hypot(origin[0] - pivot[0], origin[1] - pivot[1]);
        const auto distance = static_cast<float>(PreviewCameraDistance(halfHeight, halfWidth, Fov));
        const float lift = static_cast<float>(halfHeight * 0.08);
        const float camera[3] = {centre[0] - dir[0] * distance, centre[1] - dir[1] * distance, centre[2] + lift};
        const auto yaw = static_cast<float>(std::atan2(dir[1], dir[0]) * 180.0 / Pi);
        const auto pitch = static_cast<float>(-std::atan2(lift, distance) * 180.0 / Pi);
        Call(capture, STR("/Script/Engine.SceneComponent:K2_SetWorldLocationAndRotation"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
            if (n == STR("NewLocation")) WriteFloats(v, p, {camera[0], camera[1], camera[2]});
            else if (n == STR("NewRotation")) WriteFloats(v, p, {pitch, yaw, 0.0f});
            else if (n == STR("bTeleport")) WriteBoolParam(v, p, true);
        });

        for (std::size_t i = 0; i < m_lights.size() && i < std::size(Rig); ++i)
        {
            UObject* light = m_lights[i].Get();
            if (!light) continue;
            const RigLight& r = Rig[i];
            const float at[3] = {centre[0] + dir[0] * r.forward + right[0] * r.right, centre[1] + dir[1] * r.forward + right[1] * r.right, centre[2] + r.up};
            SetWorldLocation(light, at);
            SetBool(light, STR("bUseInverseSquaredFalloff"), true);
            Call(light, STR("/Script/Engine.LightComponent:SetLightColor"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                if (n == STR("NewLightColor")) WriteFloats(v, p, {r.color[0], r.color[1], r.color[2], 1.0f});
                else if (n == STR("bSRGB")) WriteBoolParam(v, p, true);
            });
            // Candela in the light's own unit (1 cd = 625 unitless, 4 pi lm).
            const std::uint8_t units = GetByte(light, STR("IntensityUnits")).value_or(Unitless);
            const auto intensity = static_cast<float>(units == Candelas ? r.candela : units == Lumens ? r.candela * 4 * Pi : r.candela * 625.0);
            Call(light, STR("/Script/Engine.LightComponent:SetIntensity"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                if (n == STR("NewIntensity")) WriteFloats(v, p, {intensity});
            });
            SetVisible(light, true, false);
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

    bool CosmeticsPreview::CaptureTo(std::uint8_t source, const std::wstring& file)
    {
        UObject* capture = m_capture.Get();
        UObject* target = m_target.Get();
        UObject* world = m_world;
        if (!capture || !target || !world || !SetByte(capture, STR("CaptureSource"), source)) return false;
        if (!Call(capture, STR("/Script/Engine.SceneCaptureComponent2D:CaptureScene"))) return false;
        const std::wstring folder = m_frames.wstring();
        std::error_code error;
        std::filesystem::remove(m_frames / file, error);
        if (!Call(Default(STR("/Script/Engine.Default__KismetRenderingLibrary")), STR("/Script/Engine.KismetRenderingLibrary:ExportRenderTarget"),
                  [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                      if (n == STR("WorldContextObject")) std::memcpy(v, &world, sizeof(world));
                      else if (n == STR("TextureRenderTarget")) std::memcpy(v, &target, sizeof(target));
                      else if (n == STR("FilePath")) WriteFString(v, folder);
                      else if (n == STR("FileName") || n == STR("Filename")) WriteFString(v, file);
                  }))
            return false;
        return std::filesystem::is_regular_file(m_frames / file, error);
    }

    bool CosmeticsPreview::Capture()
    {
        // Colour, then normals (the cut-out mask), then back to colour.
        const bool captured = CaptureTo(SourceFinalColor, L"capture-color.png") && CaptureTo(SourceNormal, L"capture-normal.png");
        SetByte(m_capture.Get(), STR("CaptureSource"), SourceFinalColor);
        PreviewPixels color, normals;
        if (!captured || !ReadPng(m_frames / L"capture-color.png", color) || !ReadPng(m_frames / L"capture-normal.png", normals)) return false;
        const PreviewComposition frame = ComposePreview(color, normals, Size);
        if (frame.empty && !m_loggedEmpty)
        {
            m_loggedEmpty = true;
            Log("cosmetics preview: the capture shows no character");
        }
        // Alternate two files so the service never serves a half-written PNG.
        const std::wstring file = m_frameIndex == 0 ? L"preview-0.png" : L"preview-1.png";
        const std::filesystem::path temporary = m_frames / L"preview.tmp.png";
        if (!WritePng(temporary, frame.image) || !MoveFileExW(temporary.c_str(), (m_frames / file).c_str(), MOVEFILE_REPLACE_EXISTING)) return false;
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
        m_lights.clear();
        m_world = nullptr;
        m_lookKey.clear();
        m_followUps.clear();
        m_dirty = false;
        if (had)
        {
            std::error_code error;
            std::filesystem::remove(m_framePath, error);
            for (const wchar_t* file : {L"capture-color.png", L"capture-normal.png"}) std::filesystem::remove(m_frames / file, error);
            Log(std::string("cosmetics preview stage removed (") + why + ")");
        }
    }

    // May run off the game thread (mod unload): no engine calls. The stage is
    // inert without the tick (capture on demand only) and goes with the level.
    void CosmeticsPreview::Shutdown()
    {
        m_stage = m_target = m_capture = m_meshes = m_mesh = FWeakObjectPtr{};
        m_lights.clear();
        m_world = nullptr;
        std::error_code error;
        std::filesystem::remove(m_framePath, error);
    }
} // namespace aimmod

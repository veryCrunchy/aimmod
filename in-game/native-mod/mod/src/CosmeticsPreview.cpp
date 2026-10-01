#include "CosmeticsPreview.hpp"

#include "Accessory.hpp"
#include "Log.hpp"
#include "Output.hpp"
#include "Reflect.hpp"
#include "World.hpp"

#include <Unreal/AActor.hpp>
#include <Unreal/CoreUObject/UObject/Class.hpp>
#include <Unreal/CoreUObject/UObject/UnrealType.hpp>
#include <Unreal/FProperty.hpp>
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
#include <iterator>
#include <set>

namespace aimmod
{
    using namespace RC::Unreal;
    using namespace reflect;

    namespace
    {
        constexpr const wchar_t* ModelPack = L"/Game/FirstPersonBP/Blueprints/Bodies/Characters/CharacterModelPacks/Default_CharacterModelPack.Default_CharacterModelPack";
        constexpr const wchar_t* SkinPack = L"/Game/FirstPersonBP/Blueprints/Bodies/Characters/CharacterSkinPacks/Default_CharacterSkinPack.Default_CharacterSkinPack";
        // Far above any map: nothing in the scenario can see it, and its point
        // lights (900 cm reach) cannot reach the map.
        constexpr double StageHeight = 500000.0;
        constexpr int Size = PreviewSize, CaptureSize = PreviewSize * PreviewSupersample;
        constexpr double ReadInterval = 0.2, MinCaptureInterval = 0.15;
        constexpr float Fov = 30.0f, LightReach = 900.0f;
        // ESceneCaptureSource: tone-mapped colour; scene colour with inverse opacity in alpha; normals.
        constexpr std::uint8_t SourceFinalColor = 2, SourceSceneColor = 0, SourceNormal = 6;
        constexpr std::uint8_t Movable = 2;                            // EComponentMobility
        constexpr std::uint8_t ExposureBasic = 1;                      // EAutoExposureMethod
        constexpr std::uint8_t Unitless = 0, Candelas = 1, Lumens = 2; // ELightUnits
        constexpr double Pi = 3.14159265358979;
        // The weapon view's fallback: the game's own third-person pistol.
        constexpr const wchar_t* FallbackWeapon = L"/Game/SourceArt/Weapons/FN_Pistol/FN_Pistol.FN_Pistol";

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

        // Mean sRGB luminance of the character pixels in a composed frame's
        // source, for the log (is the character lit?).
        double MeanLuminance(const PreviewPixels& color, const PreviewComposition& c)
        {
            if (c.empty || !color.Valid()) return 0;
            double sum = 0;
            long long n = 0;
            for (int y = c.top; y <= c.bottom; y += 4)
                for (int x = c.left; x <= c.right; x += 4)
                {
                    const std::uint8_t* p = &color.rgba[(static_cast<std::size_t>(y) * color.width + x) * 4];
                    sum += 0.2126 * p[0] + 0.7152 * p[1] + 0.0722 * p[2];
                    ++n;
                }
            return n ? sum / n / 255.0 : 0;
        }
    } // namespace

    CosmeticsPreview::CosmeticsPreview(game::Scene& scene, Output& output) : m_scene(scene), m_output(output), m_root(output.root())
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
        // Kill switch: "cosmetics_preview=0" in Mods\AimModCore\config.txt.
        if (const auto dir = m_output.modDirectory(); !dir.empty())
        {
            std::ifstream in(dir / L"config.txt", std::ios::binary);
            std::string config(4096, '\0');
            if (in) in.read(config.data(), static_cast<std::streamsize>(config.size()));
            config.resize(in ? config.size() : static_cast<std::size_t>(in.gcount()));
            if (!PreviewEnabledByConfig(config)) m_unavailable = "turned off in config.txt (cosmetics_preview=0)";
        }
        m_available = m_unavailable.empty();
        m_params.Bind();
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
            // A challenge or load may be changing the level: no engine call at all.
            // The references stay; the next settled tick checks them.
            return;
        }
        const auto request = ReadRequest();
        const PreviewDecision decision = DecidePreview(request, state);
        if (decision.reason != m_lastReason)
        {
            m_lastReason = decision.reason;
            Log(std::string("cosmetics preview: ") + decision.reason);
        }
        UObject* player = m_scene.Player();
        UObject* world = Alive(player) ? static_cast<AActor*>(player)->GetWorld() : nullptr;
        if (!Alive(world)) world = nullptr;
        if (!world) return;
        if (m_world && m_world != world) Forget("world changed");
        if (!decision.run)
        {
            // Debounced: the page's heartbeat can flap; park only after 10 s without a request.
            if (!m_parked && m_stage.Get() && PreviewParkDue(now, m_lastRequest)) Park(decision.reason);
            return;
        }
        m_lastRequest = now;
        if (!EnsureStage(world)) return;
        if (!StageValid()) return Disable("the stage failed validation right after it was built");
        if (m_parked) Unpark();

        const std::string key = request->LookKey();
        if (key != m_lookKey)
        {
            m_lookKey = key;
            ApplyLook(*request);
            m_dirty = true;
            m_logFrame = true;
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

    // AimModCore's own stage: a plain actor whose every component AimModCore
    // creates and holds (root, turntable, character mesh, capture, three
    // lights). Nothing depends on the game's character-menu blueprint.
    UObject* CosmeticsPreview::AddPart(UObject* stage, const wchar_t* classPath, UObject* attachTo)
    {
        UClass* type = UObjectGlobals::StaticFindObject<UClass*>(nullptr, nullptr, classPath);
        if (!InObjectArray(type)) return nullptr;
        UObject* part = nullptr;
        Call(stage, STR("/Script/Engine.Actor:AddComponentByClass"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
            if (n == STR("Class")) WriteObject(v, type);
            else if (n == STR("bManualAttachment")) WriteBoolParam(v, p, attachTo != nullptr);
            else if (n == STR("RelativeTransform"))
                if (auto* s = CastField<FStructProperty>(p))
                    for (const char* field : {"Rotation.W", "Scale3D.X", "Scale3D.Y", "Scale3D.Z"}) game::SetStructPath(v, s->GetStruct(), field, 1);
        }, &part);
        if (!Valid(part, type, stage)) return nullptr;
        if (attachTo)
            Call(part, STR("/Script/Engine.SceneComponent:K2_AttachToComponent"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                if (n == STR("Parent")) WriteObject(v, attachTo);
                // KeepRelative (0) for location, rotation and scale.
            });
        return part;
    }

    bool CosmeticsPreview::EnsureStage(UObject* world)
    {
        // The world's one stage, reused while every part of it is valid.
        if (m_world == world && StageValid()) return true;
        if (m_stage.Get() || m_world)
        {
            // A stage that stops validating in its own world is not rebuilt forever.
            if (m_world == world && ++m_rebuilds > 3) return Disable("the stage kept failing validation"), false;
            if (m_world != world) m_rebuilds = 0;
            Forget(m_world == world ? "stage no longer usable" : "world changed");
        }

        UObject* rendering = Default(STR("/Script/Engine.Default__KismetRenderingLibrary"));
        UObject* target = nullptr;
        Call(rendering, STR("/Script/Engine.KismetRenderingLibrary:CreateRenderTarget2D"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
            if (n == STR("WorldContextObject")) WriteObject(v, world);
            else if (n == STR("Width") || n == STR("Height")) { const std::int32_t s = CaptureSize; std::memcpy(v, &s, sizeof(s)); }
            else if (n == STR("Format")) *v = 2; // RTF_RGBA8: exported as PNG
            else if (n == STR("ClearColor")) WriteFloats(v, p, {0, 0, 0, 1});
        }, &target);
        if (!Alive(target)) return Disable("render target unavailable"), false;

        UClass* actorClass = UObjectGlobals::StaticFindObject<UClass*>(nullptr, nullptr, STR("/Script/Engine.Actor"));
        if (!InObjectArray(actorClass)) return Disable("Actor class not found"), false;
        const FTransform transform{FQuat(FRotator(0, 0, 0)), FVector(0, 0, StageHeight), FVector(1, 1, 1)};
        AActor* stage = UGameplayStatics::BeginDeferredActorSpawnFromClass(world, actorClass, transform, ESpawnActorCollisionHandlingMethod::AlwaysSpawn);
        if (!Valid(stage, actorClass)) return Disable("the stage could not be spawned"), false;
        UGameplayStatics::FinishSpawningActor(stage, transform);
        if (!Valid(stage, actorClass)) return Disable("the stage did not survive spawning"), false;
        stage->SetActorEnableCollision(false);

        // The first scene component becomes the root; the rest hang off it.
        UObject* root = AddPart(stage, STR("/Script/Engine.SceneComponent"), nullptr);
        UObject* turntable = root ? AddPart(stage, STR("/Script/Engine.SceneComponent"), root) : nullptr;
        UObject* mesh = turntable ? AddPart(stage, STR("/Script/Engine.SkeletalMeshComponent"), turntable) : nullptr;
        UObject* capture = root ? AddPart(stage, STR("/Script/Engine.SceneCaptureComponent2D"), root) : nullptr;
        if (!root || !turntable || !mesh || !capture) return Disable("the stage's parts could not be created"), false;
        if (UObject* r = GetObject(stage, STR("RootComponent")); r != root) return Disable("the stage's root is not its first part"), false;
        std::vector<UObject*> lights;
        for (int i = 0; i < 3; ++i)
            if (UObject* light = AddPart(stage, STR("/Script/Engine.PointLightComponent"), root)) lights.push_back(light);

        m_stage = FWeakObjectPtr(stage);
        m_target = FWeakObjectPtr(target);
        m_world = world;
        m_capture = FWeakObjectPtr(capture);
        m_meshes = FWeakObjectPtr(turntable);
        m_mesh = FWeakObjectPtr(mesh);
        for (UObject* part : {static_cast<UObject*>(stage), root, turntable, mesh, capture}) m_parts.push_back(FWeakObjectPtr(part));

        // Our render target; capture on demand only; render the stage and nothing else.
        SetObject(capture, STR("TextureTarget"), target);
        SetBool(capture, STR("bCaptureEveryFrame"), false);
        SetBool(capture, STR("bCaptureOnMovement"), false);
        SetBool(capture, STR("bConsiderUnrenderedOpaquePixelAsFullyTranslucent"), true);
        SetByte(capture, STR("PrimitiveRenderMode"), 2); // PRM_UseShowOnlyList
        SetByte(capture, STR("CaptureSource"), SourceFinalColor);
        SetFloat(capture, STR("FOVAngle"), Fov);
        SetFloat(capture, STR("PostProcessBlendWeight"), 1.0f);
        ShowInCapture(mesh);
        // Fixed exposure: a scene capture keeps no eye-adaptation history, so
        // auto exposure starts from nothing. Min = max brightness pins it. The
        // unit is luminance, or EV100 when the project extends the luminance
        // range (then the defaults are negative).
        const PostProcess post(capture);
        const bool ev100 = post.Get("AutoExposureMinBrightness").value_or(0) < 0;
        const double brightness = ev100 ? 3.0 : 1.0;
        const bool exposure = post.Override("AutoExposureMethod", ExposureBasic) && post.Override("AutoExposureMinBrightness", brightness) &&
                              post.Override("AutoExposureMaxBrightness", brightness) && post.Override("AutoExposureBias", 0);
        post.Override("BloomIntensity", 0.2);
        post.Override("VignetteIntensity", 0);
        post.Override("MotionBlurAmount", 0);
        post.Override("LensFlareIntensity", 0);
        post.Override("GrainIntensity", 0);

        // The light rig: short reach (far from the map 5 km below), no shadows.
        m_lights.clear();
        for (UObject* light : lights)
        {
            Call(light, STR("/Script/Engine.LocalLightComponent:SetAttenuationRadius"), [](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                if (n == STR("NewRadius")) WriteFloats(v, p, {LightReach});
            });
            Call(light, STR("/Script/Engine.LightComponentBase:SetCastShadows"), [](const std::wstring&, FProperty* p, std::uint8_t* v) { WriteBoolParam(v, p, false); });
            if (GetFloat(light, STR("AttenuationRadius")).value_or(1e9f) > LightReach + 1)
            {
                SetVisible(light, false, false); // never a light that could reach the map
                continue;
            }
            m_lights.push_back(FWeakObjectPtr(light));
        }
        Call(mesh, STR("/Script/Engine.PrimitiveComponent:SetCastShadow"), [](const std::wstring&, FProperty* p, std::uint8_t* v) { WriteBoolParam(v, p, false); });
        Call(mesh, STR("/Script/Engine.ActorComponent:SetTickableWhenPaused"), [](const std::wstring&, FProperty* p, std::uint8_t* v) { WriteBoolParam(v, p, true); });
        m_baseYaw = 0;
        m_lookKey.clear();
        m_yaw = 1e9;
        m_parked = false;
        std::error_code error;
        std::filesystem::create_directories(m_frames, error);
        Log("cosmetics preview stage spawned (AimMod's own stage, " + std::to_string(m_lights.size()) + " rig lights" + (exposure ? ", fixed exposure" : ", exposure not pinned") +
            (ev100 ? ", EV100" : "") + ")");
        return true;
    }

    bool CosmeticsPreview::StageValid()
    {
        UObject* stage = m_stage.Get();
        if (!Valid(stage)) return false;
        for (FWeakObjectPtr& part : m_parts)
            if (UObject* o = part.Get(); o != stage && !Valid(o, nullptr, stage)) return false;
        return Alive(m_target.Get());
    }

    // Any failed check ends the preview for this session: no stage, no frames
    // (the page shows its 2D swatches). A cosmetics bug must never take the game down.
    void CosmeticsPreview::Disable(const std::string& why)
    {
        if (!m_available) return;
        m_available = false;
        Log("cosmetics preview disabled for this session: " + why);
        Forget(why.c_str());
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
        if (!Alive(stage) || !Alive(mesh)) return;
        HideAccessories();
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
        // The game's character materials read opacity and colours from the
        // component's material data. A fresh component has zeros there: the
        // model dithers away to scattered pixels. Apply the game's defaults.
        if (!ApplyMaterialDataDefaults(mesh)) Log("cosmetics preview: material data defaults unavailable; the model may look faded");

        // Catalog tints keep the skin: on fresh dynamic instances of each slot's
        // own material, only its paint and accent parameters are recoloured
        // (cosmetics::MapColours), the same as on avatars in matches. Each
        // slot's parameters and what was recoloured are logged once per look.
        if (!request.vectors.empty() || !request.scalars.empty())
        {
            cosmetics::Item tint;
            tint.id = "preview";
            for (const PreviewParam& param : request.vectors) tint.vector.push_back({param.name, {param.value[0], param.value[1], param.value[2], param.value[3]}});
            for (const PreviewParam& param : request.scalars) tint.scalar.push_back({param.name, param.value[0]});
            UObject* materials = Default(STR("/Script/Engine.Default__KismetMaterialLibrary"));
            for (std::int32_t slot = 0; slot < 16; ++slot)
            {
                UObject* material = nullptr;
                Call(mesh, STR("/Script/Engine.PrimitiveComponent:GetMaterial"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                    if (n == STR("ElementIndex")) std::memcpy(v, &slot, sizeof(slot));
                }, &material);
                if (!Alive(material)) break;
                std::set<std::string> vectors, scalars, textures;
                m_params.Names(material, vectors, scalars, textures);
                const auto colours = cosmetics::MapColours(tint, vectors);
                const bool baseScheme = vectors.contains("MetalPaint") && vectors.contains("TriangularPaint");
                std::string recoloured;
                for (const auto& [name, c] : colours) recoloured += " " + name;
                Log("cosmetics preview: slot " + std::to_string(slot) + " " + m_params.Describe(material) + " -> " + (colours.empty() ? std::string("nothing to recolour") : "recolours" + recoloured));
                if (colours.empty()) continue;
                UObject* mid = nullptr;
                Call(materials, STR("/Script/Engine.KismetMaterialLibrary:CreateDynamicMaterialInstance"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                    if (n == STR("WorldContextObject")) WriteObject(v, stage);
                    else if (n == STR("Parent")) WriteObject(v, material);
                }, &mid);
                if (!Alive(mid)) continue;
                for (const auto& [name, c] : colours)
                    Call(mid, STR("/Script/Engine.MaterialInstanceDynamic:SetVectorParameterValue"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                        if (n == STR("ParameterName")) WriteFName(v, p, Widen(name));
                        else if (n == STR("Value")) WriteFloats(v, p, {static_cast<float>(c.r), static_cast<float>(c.g), static_cast<float>(c.b), static_cast<float>(c.a)});
                    });
                if (baseScheme)
                    for (const PreviewParam& param : request.scalars)
                        Call(mid, STR("/Script/Engine.MaterialInstanceDynamic:SetScalarParameterValue"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                            if (n == STR("ParameterName")) WriteFName(v, p, Widen(param.name));
                            else if (n == STR("Value")) WriteFloats(v, p, {static_cast<float>(param.value[0])});
                        });
                Call(mesh, STR("/Script/Engine.PrimitiveComponent:SetMaterial"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                    if (n == STR("ElementIndex")) std::memcpy(v, &slot, sizeof(slot));
                    else if (n == STR("Material")) WriteObject(v, mid);
                });
            }
        }
        Frame(mesh);
        WearAccessories(request);
        if (request.weaponView) ShowWeapon(request);
        else HoldWeapon(request);
    }

    void CosmeticsPreview::ShowInCapture(UObject* component)
    {
        UObject* capture = m_capture.Get();
        if (!Alive(capture) || !Alive(component)) return;
        if (!Call(capture, STR("/Script/Engine.SceneCaptureComponent:ShowOnlyComponent"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                if (n == STR("InComponent")) WriteObject(v, component);
            }))
            Log("cosmetics preview: a new part could not join the capture (it stays invisible)");
    }

    CosmeticsPreview::Part* CosmeticsPreview::FindPart(std::vector<Part>& parts, const std::string& key)
    {
        for (Part& part : parts)
            if (part.key == key && Alive(part.component.Get())) return &part;
        return nullptr;
    }

    // A weapon mesh: the player's selected viewmodel weapon (weapon view), else
    // the game's own third-person pistol (what KovaaK's characters hold on
    // HoldRight). Made once per weapon and kept; the finish is set on each use.
    UObject* CosmeticsPreview::MakeWeapon(const PreviewRequest& request, bool selectedWeapon)
    {
        UObject* stage = m_stage.Get();
        if (!Alive(stage)) return nullptr;
        UObject* selected = nullptr;
        if (selectedWeapon)
            if (UObject* character = Alive(m_scene.Player()) ? GetObject(m_scene.Player(), STR("MyCharacter")) : nullptr; Alive(character))
                if (UObject* view = GetObject(character, STR("ViewModel_Native")); Alive(view))
                    Call(view, STR("/Script/GameSkillsTrainer.FPSPlayer_WeaponComponentActor:GetSelectWeaponMesh"), {}, &selected);
        if (!Alive(selected)) selected = nullptr;
        UObject* skeletal = selected ? GetObject(selected, STR("SkeletalMesh")) : nullptr;
        UObject* fixed = selected && !skeletal ? GetObject(selected, STR("StaticMesh")) : nullptr;
        if (!Alive(skeletal) && !Alive(fixed)) skeletal = nullptr, fixed = LoadGameAsset(FallbackWeapon), selected = nullptr;
        if (!Alive(skeletal) && !Alive(fixed))
        {
            Log("cosmetics preview: no weapon mesh found (neither the selected weapon nor the game's pistol)");
            return nullptr;
        }
        const std::string key = selectedWeapon ? "view|" + game::ObjectName(skeletal ? skeletal : fixed) : "held";
        Part* part = FindPart(m_weapons, key);
        if (!part)
        {
            UClass* type = UObjectGlobals::StaticFindObject<UClass*>(nullptr, nullptr, skeletal ? STR("/Script/Engine.SkeletalMeshComponent") : STR("/Script/Engine.StaticMeshComponent"));
            UObject* weapon = nullptr;
            Call(stage, STR("/Script/Engine.Actor:AddComponentByClass"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                if (n == STR("Class")) WriteObject(v, type);
                else if (n == STR("bManualAttachment")) WriteBoolParam(v, p, true);
                else if (n == STR("RelativeTransform"))
                    if (auto* s = CastField<FStructProperty>(p))
                        for (const char* field : {"Rotation.W", "Scale3D.X", "Scale3D.Y", "Scale3D.Z"}) game::SetStructPath(v, s->GetStruct(), field, 1);
            }, &weapon);
            if (!Alive(weapon) || !type || !weapon->IsA(type))
            {
                Log("cosmetics preview: the weapon component could not be added");
                return nullptr;
            }
            Call(weapon, STR("/Script/Engine.PrimitiveComponent:SetCollisionEnabled"), [](const std::wstring&, FProperty*, std::uint8_t* v) { *v = 0; });
            Call(weapon, STR("/Script/Engine.PrimitiveComponent:SetCastShadow"), [](const std::wstring&, FProperty* p, std::uint8_t* v) { WriteBoolParam(v, p, false); });
            if (skeletal)
                Call(weapon, STR("/Script/Engine.SkinnedMeshComponent:SetSkeletalMesh"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                    if (n == STR("NewMesh")) WriteObject(v, skeletal);
                    else if (n == STR("bReinitPose")) WriteBoolParam(v, p, true);
                });
            else
                Call(weapon, STR("/Script/Engine.StaticMeshComponent:SetStaticMesh"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                    if (n == STR("NewMesh")) WriteObject(v, fixed);
                });
            Part made;
            made.key = key;
            made.component = FWeakObjectPtr(weapon);
            // The weapon's own materials (its skin): the selected weapon's, else the mesh's.
            std::int32_t slots = 0;
            Call(weapon, STR("/Script/Engine.PrimitiveComponent:GetNumMaterials"), {}, nullptr, [&](const std::wstring& n, FProperty* p, const std::uint8_t* v) {
                if (n == STR("ReturnValue") && p->GetSize() == 4) std::memcpy(&slots, v, 4);
            });
            for (std::int32_t slot = 0; slot < std::clamp(slots, 0, 8); ++slot)
            {
                UObject* material = nullptr;
                Call(selected && skeletal ? selected : weapon, STR("/Script/Engine.PrimitiveComponent:GetMaterial"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                    if (n == STR("ElementIndex")) std::memcpy(v, &slot, sizeof(slot));
                }, &material);
                made.materials.push_back(FWeakObjectPtr(Alive(material) ? material : nullptr));
            }
            ApplyMaterialDataDefaults(weapon);
            ShowInCapture(weapon);
            m_weapons.push_back(std::move(made));
            part = &m_weapons.back();
            Log(std::string("cosmetics preview: weapon made (") + (selected ? "selected weapon" : "the game's pistol") + ")");
        }
        ApplyFinish(*part, request);
        UObject* weapon = part->component.Get();
        SetVisible(weapon, true, false);
        return weapon;
    }

    // The weapon's own materials, with the requested finish on dynamic instances.
    void CosmeticsPreview::ApplyFinish(Part& part, const PreviewRequest& request)
    {
        UObject* weapon = part.component.Get();
        UObject* stage = m_stage.Get();
        if (!Alive(weapon) || !Alive(stage)) return;
        const cosmetics::Item* finish = nullptr;
        if (!request.finish.empty())
        {
            const Output::CosmeticsInputs inputs = m_output.cosmetics();
            cosmetics::ResolveOptions options;
            if (inputs.library) options.allowDrafts = inputs.allowDrafts;
            std::string why = "no catalog";
            finish = inputs.library ? cosmetics::Resolve(inputs.library->index, request.finish, options, &why) : nullptr;
            if (finish && finish->kind != "weapon_finish") why = "not a weapon finish", finish = nullptr;
            if (!finish) Log("cosmetics preview: finish " + request.finish + " not shown (" + why + ")");
        }
        for (std::int32_t slot = 0; slot < static_cast<std::int32_t>(part.materials.size()); ++slot)
        {
            UObject* material = part.materials[static_cast<std::size_t>(slot)].Get();
            if (!Alive(material)) continue;
            std::set<std::string> vectors, scalars, textures;
            m_params.Names(material, vectors, scalars, textures);
            const auto colours = finish ? cosmetics::MapColours(*finish, vectors) : std::vector<std::pair<std::string, cosmetics::Color>>{};
            if (finish)
            {
                std::string recoloured;
                for (const auto& [name, c] : colours) recoloured += " " + name;
                Log("cosmetics preview: weapon slot " + std::to_string(slot) + " " + m_params.Describe(material) + " -> " +
                    (colours.empty() ? std::string("nothing to recolour") : "recolours" + recoloured));
            }
            if (finish && !colours.empty())
            {
                UObject* mid = nullptr;
                Call(Default(STR("/Script/Engine.Default__KismetMaterialLibrary")), STR("/Script/Engine.KismetMaterialLibrary:CreateDynamicMaterialInstance"),
                     [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                         if (n == STR("WorldContextObject")) WriteObject(v, stage);
                         else if (n == STR("Parent")) WriteObject(v, material);
                     },
                     &mid);
                if (Alive(mid))
                {
                    for (const auto& [name, c] : colours)
                        Call(mid, STR("/Script/Engine.MaterialInstanceDynamic:SetVectorParameterValue"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                            if (n == STR("ParameterName")) WriteFName(v, p, Widen(name));
                            else if (n == STR("Value")) WriteFloats(v, p, {static_cast<float>(c.r), static_cast<float>(c.g), static_cast<float>(c.b), static_cast<float>(c.a)});
                        });
                    material = mid;
                }
            }
            Call(weapon, STR("/Script/Engine.PrimitiveComponent:SetMaterial"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                if (n == STR("ElementIndex")) std::memcpy(v, &slot, sizeof(slot));
                else if (n == STR("Material")) WriteObject(v, material);
            });
        }
        if (finish) Log("cosmetics preview: finish " + finish->id + " on the weapon");
    }

    // The weapon view: the weapon alone on the stage's turntable.
    void CosmeticsPreview::ShowWeapon(const PreviewRequest& request)
    {
        HideWeapons();
        UObject* mesh = m_mesh.Get();
        UObject* meshes = m_meshes.Get();
        if (!Alive(mesh) || !Alive(meshes)) return;
        const bool fresh = !std::any_of(m_weapons.begin(), m_weapons.end(), [](const Part& p) { return p.key.rfind("view|", 0) == 0 && Alive(p.component.Get()); });
        UObject* weapon = MakeWeapon(request, true);
        if (!weapon) return;
        float pivot[3], origin[3], extent[3], at[3];
        if (fresh || !Alive(GetObject(weapon, STR("AttachParent"))))
        {
            // On the turntable, its bounds centred over the pivot, a metre up (set once).
            Call(weapon, STR("/Script/Engine.SceneComponent:K2_AttachToComponent"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                if (n == STR("Parent")) WriteObject(v, meshes);
                // KeepRelative (0) for location, rotation and scale.
            });
            if (Location(meshes, pivot) && Bounds(weapon, origin, extent) && Location(weapon, at))
            {
                const float to[3] = {at[0] + pivot[0] - origin[0], at[1] + pivot[1] - origin[1], at[2] + pivot[2] + 100 - origin[2]};
                SetWorldLocation(weapon, to);
            }
        }
        SetVisible(mesh, false, false);
        HideAccessories();
        Frame(weapon);
        Log("cosmetics preview: weapon view");
    }

    // The character view: the game's pistol in the right hand, as KovaaK's
    // characters hold it, with the finish.
    void CosmeticsPreview::HoldWeapon(const PreviewRequest& request)
    {
        HideWeapons();
        UObject* mesh = m_mesh.Get();
        if (!Alive(mesh)) return;
        bool socket = false;
        Call(mesh, STR("/Script/Engine.SceneComponent:DoesSocketExist"),
             [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                 if (n == STR("InSocketName")) WriteFName(v, p, STR("HoldRight"));
             },
             nullptr,
             [&](const std::wstring& n, FProperty* p, const std::uint8_t* v) {
                 if (n == STR("ReturnValue") && p->GetSize() == 1) socket = *v != 0;
             });
        if (!socket)
        {
            Log("cosmetics preview: this model has no HoldRight socket; no weapon in hand");
            return;
        }
        UObject* weapon = MakeWeapon(request, false);
        if (!weapon) return;
        // Snapped to the hand on every use (the model may have changed).
        Call(weapon, STR("/Script/Engine.SceneComponent:K2_AttachToComponent"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
            if (n == STR("Parent")) WriteObject(v, mesh);
            else if (n == STR("SocketName")) WriteFName(v, p, STR("HoldRight"));
            else if (n == STR("LocationRule") || n == STR("RotationRule") || n == STR("ScaleRule")) *v = 2; // SnapToTarget
        });
    }

    void CosmeticsPreview::HideWeapons()
    {
        for (Part& part : m_weapons)
            if (UObject* weapon = part.component.Get(); Alive(weapon)) SetVisible(weapon, false, false);
    }

    // Accessories from the installed catalog, by id: only released fit
    // accessories (game meshes, curated). Made once per item and model, attached
    // in the pose the camera sees, then hidden and shown as the look changes.
    void CosmeticsPreview::WearAccessories(const PreviewRequest& request)
    {
        if (request.accessories.empty()) return;
        const Output::CosmeticsInputs inputs = m_output.cosmetics();
        UObject* stage = m_stage.Get();
        UObject* mesh = m_mesh.Get();
        if (!inputs.library || !Alive(stage) || !Alive(mesh))
        {
            Log(std::string("cosmetics preview: accessories not shown (") + (!inputs.library ? "no verified catalog" : "no stage") + ")");
            return;
        }
        cosmetics::ResolveOptions options;
        options.allowDrafts = inputs.allowDrafts;
        options.verifiedPaks = inputs.library->verifiedPaks;
        for (const std::string& id : request.accessories)
        {
            const std::string key = id + "|" + request.model;
            if (Part* part = FindPart(m_accessories, key))
            {
                SetVisible(part->component.Get(), true, false);
                Log("cosmetics preview: wearing " + id + " (kept)");
                continue;
            }
            std::string why;
            const cosmetics::Item* item = cosmetics::Resolve(inputs.library->index, id, options, &why);
            if (!item || item->kind != "accessory" || !item->fit)
            {
                Log("cosmetics preview: accessory " + id + " not shown (" + (item ? std::string("not a game-mesh accessory") : why) + ")");
                continue;
            }
            if (m_accessories.size() >= 32)
            {
                Log("cosmetics preview: accessory " + id + " not shown (too many parts on the stage)");
                continue;
            }
            UObject* worn = AttachFitAccessory(stage, mesh, *item, why);
            if (!Alive(worn))
            {
                Log("cosmetics preview: accessory " + id + " not shown (" + why + ")");
                continue;
            }
            ShowInCapture(worn);
            m_accessories.push_back({key, FWeakObjectPtr(worn), {}});
            float origin[3]{}, extent[3]{};
            Bounds(worn, origin, extent);
            char size[96];
            std::snprintf(size, sizeof size, " (%.0f x %.0f x %.0f cm)", extent[0] * 2, extent[1] * 2, extent[2] * 2);
            Log("cosmetics preview: wearing " + id + size);
        }
    }

    void CosmeticsPreview::HideAccessories()
    {
        for (Part& part : m_accessories)
            if (UObject* worn = part.component.Get(); Alive(worn)) SetVisible(worn, false, false);
    }

    // Camera and lights on the character: the camera keeps the stage's own
    // front view, moves back until the whole character (turned any way) fits
    // a 30 degree view, and the rig sits around it. ComposePreview then frames
    // the frame exactly on the silhouette.
    void CosmeticsPreview::Frame(UObject* mesh)
    {
        UObject* meshes = m_meshes.Get();
        UObject* capture = m_capture.Get();
        if (!Alive(mesh) || !Alive(meshes) || !Alive(capture)) return;
        float origin[3]{}, extent[3]{}, pivot[3]{};
        const bool bounds = Bounds(mesh, origin, extent);
        if (!Location(meshes, pivot)) Location(mesh, pivot);
        const auto sane = [](float e) { return std::isfinite(e) && e >= 0.5f && e <= 600.0f; };
        if (!bounds || !sane(extent[0]) || !sane(extent[1]) || !sane(extent[2]) || std::abs(origin[2] - pivot[2]) > 600)
        {
            // A standing character on the stage's pivot.
            origin[0] = pivot[0], origin[1] = pivot[1], origin[2] = pivot[2] + 90;
            extent[0] = extent[1] = 40, extent[2] = 95;
        }
        const float centre[3] = {pivot[0], pivot[1], origin[2]};
        // Facing the character's front: its forward from the shoulders, turned
        // back by the turntable's current yaw (the camera stays put as it turns).
        if (mesh == m_mesh.Get())
        {
            double forward[3];
            if (CharacterForward(mesh, forward))
            {
                const double yaw = (m_baseYaw + (std::abs(m_yaw) <= 360 ? m_yaw : 0)) * Pi / 180.0;
                const double fx = forward[0] * std::cos(-yaw) - forward[1] * std::sin(-yaw), fy = forward[0] * std::sin(-yaw) + forward[1] * std::cos(-yaw);
                const double l = std::hypot(fx, fy);
                if (l > 1e-6) m_viewDir[0] = static_cast<float>(-fx / l), m_viewDir[1] = static_cast<float>(-fy / l);
            }
            else Log("cosmetics preview: no shoulder bones; the camera keeps its default front");
        }
        float dir[2] = {m_viewDir[0], m_viewDir[1]};
        const float right[2] = {-dir[1], dir[0]};
        const double halfHeight = extent[2];
        const double halfWidth = std::hypot(extent[0], extent[1]) + std::hypot(origin[0] - pivot[0], origin[1] - pivot[1]);
        const auto distance = static_cast<float>(PreviewCameraDistance(halfHeight, halfWidth, Fov));
        const float lift = static_cast<float>(halfHeight * 0.08);
        const double camera[3] = {centre[0] - dir[0] * distance, centre[1] - dir[1] * distance, centre[2] + lift};
        const double rotation[3] = {-std::atan2(lift, distance) * 180.0 / Pi, std::atan2(dir[1], dir[0]) * 180.0 / Pi, 0};
        Call(capture, STR("/Script/Engine.SceneComponent:K2_SetWorldLocationAndRotation"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
            if (n == STR("NewLocation")) WriteFloats(v, p, {static_cast<float>(camera[0]), static_cast<float>(camera[1]), static_cast<float>(camera[2])});
            else if (n == STR("NewRotation")) WriteFloats(v, p, {static_cast<float>(rotation[0]), static_cast<float>(rotation[1]), 0.0f});
            else if (n == STR("bTeleport")) WriteBoolParam(v, p, true);
        });

        for (std::size_t i = 0; i < m_lights.size() && i < std::size(Rig); ++i)
        {
            UObject* light = m_lights[i].Get();
            if (!Alive(light)) continue;
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
        if (!Alive(meshes)) return;
        Call(meshes, STR("/Script/Engine.SceneComponent:K2_SetRelativeRotation"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
            if (n == STR("NewRotation")) WriteFloats(v, p, {0.0f, static_cast<float>(m_baseYaw + yaw), 0.0f});
            else if (n == STR("bTeleport")) *v = 1;
        });
    }

    bool CosmeticsPreview::CaptureTo(std::uint8_t source, const std::wstring& file)
    {
        UObject* capture = m_capture.Get();
        UObject* target = m_target.Get();
        UObject* world = m_stage.Get();
        if (!Alive(capture) || !Alive(target) || !Alive(world) || !SetByte(capture, STR("CaptureSource"), source)) return false;
        if (!Call(capture, STR("/Script/Engine.SceneCaptureComponent2D:CaptureScene"))) return false;
        const std::wstring folder = m_frames.wstring();
        std::error_code error;
        std::filesystem::remove(m_frames / file, error);
        if (!Call(Default(STR("/Script/Engine.Default__KismetRenderingLibrary")), STR("/Script/Engine.KismetRenderingLibrary:ExportRenderTarget"),
                  [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                      if (n == STR("WorldContextObject")) WriteObject(v, world);
                      else if (n == STR("TextureRenderTarget")) WriteObject(v, target);
                      else if (n == STR("FilePath")) WriteFString(v, folder);
                      else if (n == STR("FileName") || n == STR("Filename")) WriteFString(v, file);
                  }))
            return false;
        return std::filesystem::is_regular_file(m_frames / file, error);
    }

    bool CosmeticsPreview::Capture()
    {
        // Colour (tone-mapped, fixed exposure), then the mask: scene colour's
        // inverse-opacity alpha; world normals only if that alpha is unusable.
        PreviewPixels color, mask;
        bool ok = CaptureTo(SourceFinalColor, L"capture-color.png") && ReadPng(m_frames / L"capture-color.png", color) &&
                  CaptureTo(SourceSceneColor, L"capture-mask.png") && ReadPng(m_frames / L"capture-mask.png", mask);
        PreviewComposition frame = ok ? ComposePreview(color, mask, PreviewMask::InverseAlpha, Size) : PreviewComposition{};
        const char* used = "alpha";
        if (ok && frame.empty && CaptureTo(SourceNormal, L"capture-mask.png") && ReadPng(m_frames / L"capture-mask.png", mask))
        {
            frame = ComposePreview(color, mask, PreviewMask::Background, Size);
            used = "normals";
        }
        if (UObject* c = m_capture.Get(); Alive(c)) SetByte(c, STR("CaptureSource"), SourceFinalColor);
        if (!ok) return false;
        if (m_logFrame)
        {
            // Once per look: enough to tell a lighting problem from a mask problem.
            m_logFrame = false;
            char line[200];
            std::snprintf(line, sizeof line, "cosmetics preview frame: %s mask, character %.1f%% of the capture, mean brightness %.2f, gain %.2f%s", used,
                          frame.coverage * 100, MeanLuminance(color, frame), frame.gain, frame.empty ? " (no character found)" : "");
            Log(line);
        }
        // Alternate two files so the service never serves a half-written PNG.
        const std::wstring file = m_frameIndex == 0 ? L"preview-0.png" : L"preview-1.png";
        const std::filesystem::path temporary = m_frames / L"preview.tmp.png";
        if (!WritePng(temporary, frame.image) || !MoveFileExW(temporary.c_str(), (m_frames / file).c_str(), MOVEFILE_REPLACE_EXISTING)) return false;
        m_frameIndex ^= 1;
        WriteAtomically(m_framePath, FormatPreviewFrame(++m_frameSeq, game::Narrow(file), Size, Size));
        return true;
    }

    // The page closed (debounced): hide the stage, stop capturing, keep it.
    void CosmeticsPreview::Park(const char* why)
    {
        UObject* stage = m_stage.Get();
        if (Alive(stage))
            Call(stage, STR("/Script/Engine.Actor:SetActorHiddenInGame"), [](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                if (n == STR("bNewHidden")) WriteBoolParam(v, p, true);
            });
        m_parked = true;
        m_lookKey.clear(); // reapplied on the next open
        m_yaw = 1e9;
        m_followUps.clear();
        m_dirty = false;
        std::error_code error;
        std::filesystem::remove(m_framePath, error);
        for (const wchar_t* file : {L"capture-color.png", L"capture-mask.png", L"capture-normal.png"}) std::filesystem::remove(m_frames / file, error);
        Log(std::string("cosmetics preview stage parked (") + why + ")");
    }

    void CosmeticsPreview::Unpark()
    {
        if (UObject* stage = m_stage.Get(); Alive(stage))
            Call(stage, STR("/Script/Engine.Actor:SetActorHiddenInGame"), [](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                if (n == STR("bNewHidden")) WriteBoolParam(v, p, false);
            });
        m_parked = false;
        m_dirty = true;
        Log("cosmetics preview stage reused");
    }

    // No engine call: the stage and its parts belong to their level now.
    void CosmeticsPreview::Forget(const char* why)
    {
        const bool had = m_stage.Get() || m_world;
        m_stage = m_target = m_capture = m_meshes = m_mesh = FWeakObjectPtr{};
        m_parts.clear();
        m_lights.clear();
        m_accessories.clear();
        m_weapons.clear();
        m_world = nullptr;
        m_parked = false;
        m_lookKey.clear();
        m_yaw = 1e9;
        m_followUps.clear();
        m_dirty = false;
        std::error_code error;
        std::filesystem::remove(m_framePath, error);
        if (had) Log(std::string("cosmetics preview stage left to the level (") + why + ")");
    }

    // May run off the game thread (mod unload): no engine calls. The stage is
    // inert without the tick (capture on demand only) and goes with the level.
    void CosmeticsPreview::Shutdown()
    {
        m_stage = m_target = m_capture = m_meshes = m_mesh = FWeakObjectPtr{};
        m_parts.clear();
        m_lights.clear();
        m_accessories.clear();
        m_weapons.clear();
        m_world = nullptr;
        std::error_code error;
        std::filesystem::remove(m_framePath, error);
    }
} // namespace aimmod

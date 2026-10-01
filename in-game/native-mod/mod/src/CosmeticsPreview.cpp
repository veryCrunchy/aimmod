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
        // Stage parts that are not the character: never rendered by the preview.
        constexpr const wchar_t* HiddenParts[] = {STR("StaticMeshes"), STR("Cylinder"),     STR("Cube"),           STR("Sphere"),      STR("SphereBody"),
                                                  STR("SphereHead"),   STR("CubeBody"),     STR("CubeHead"),       STR("CylinderBody"), STR("CylinderBottom"),
                                                  STR("CylinderTop"),  STR("CylinderHead"), STR("Weapon1"),        STR("Weapon2"),     STR("Wall"),
                                                  STR("Floor")};

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
            // A challenge or load usually changes the level: never destroy then.
            return Teardown(reason, false);
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
        if (!world) return Teardown("no world", false);
        if (PreviewMayDestroy(state)) DestroyOrphans(world);
        if (!decision.run) return Teardown(decision.reason, PreviewMayDestroy(state));
        if (!EnsureStage(world)) return;

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

    bool CosmeticsPreview::EnsureStage(UObject* world)
    {
        if (m_stage.Get() && m_world == world && Alive(m_stage.Get())) return true;
        if (m_stage.Get() || m_target.Get() || m_world) Teardown(m_world == world ? "stage gone" : "world changed", m_world == world);
        // Found again for every spawn, never cached: the class can be unloaded with the menu.
        m_stageClass = UObjectGlobals::StaticFindObject<UClass*>(nullptr, nullptr, StageClass);
        if (!m_stageClass) m_stageClass = static_cast<UClass*>(LoadGameAsset(StageClass));
        if (!Alive(m_stageClass)) { m_stageClass = nullptr; Teardown("the game's preview stage class is not loaded", false); return false; }

        UObject* rendering = Default(STR("/Script/Engine.Default__KismetRenderingLibrary"));
        UObject* target = nullptr;
        Call(rendering, STR("/Script/Engine.KismetRenderingLibrary:CreateRenderTarget2D"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
            if (n == STR("WorldContextObject")) WriteObject(v, world);
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
        // render the stage and nothing else. Pixels nothing rendered count as
        // empty (alpha 1) in the scene-colour mask capture.
        SetObject(capture, STR("TextureTarget"), target);
        SetBool(capture, STR("bCaptureEveryFrame"), false);
        SetBool(capture, STR("bCaptureOnMovement"), false);
        SetBool(capture, STR("bConsiderUnrenderedOpaquePixelAsFullyTranslucent"), true);
        SetByte(capture, STR("PrimitiveRenderMode"), 2); // PRM_UseShowOnlyList
        SetByte(capture, STR("CaptureSource"), SourceFinalColor);
        SetFloat(capture, STR("FOVAngle"), Fov);
        SetFloat(capture, STR("PostProcessBlendWeight"), 1.0f);
        Call(capture, STR("/Script/Engine.SceneCaptureComponent:ShowOnlyActorComponents"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
            if (n == STR("InActor")) WriteObject(v, stage);
        });
        // Fixed exposure: a scene capture keeps no eye-adaptation history, so
        // auto exposure starts from nothing. Min = max brightness pins it. The
        // unit is luminance, or EV100 when the project extends the luminance
        // range (then the defaults are negative).
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
            m_lights.push_back(FWeakObjectPtr(light));
        }

        // Only the character is rendered: the shape models, weapons, wall and
        // floor are hidden (the backdrop is composed after capture). No part
        // of the stage casts a shadow onto the map.
        for (const wchar_t* name : HiddenParts)
            if (UObject* part = GetObject(stage, name))
            {
                SetVisible(part, false, true);
                Call(part, STR("/Script/Engine.PrimitiveComponent:SetCastShadow"), [](const std::wstring&, FProperty* p, std::uint8_t* v) { WriteBoolParam(v, p, false); });
            }
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
        Log("cosmetics preview stage spawned (" + std::to_string(m_lights.size()) + " rig lights" + (exposure ? ", fixed exposure" : ", exposure not pinned") +
            (ev100 ? ", EV100" : "") + ")");
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
        RemoveAccessories();
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

        // Catalog tints: a paint job on fresh dynamic instances. A skin's
        // material may use a master without the tint's parameters, so the
        // parent is the mesh's own base material for the slot when that one has
        // them (as in matches), else the slot's material. Each slot's
        // parameters are logged once per look.
        if (!request.vectors.empty() || !request.scalars.empty())
        {
            UObject* materials = Default(STR("/Script/Engine.Default__KismetMaterialLibrary"));
            UObject* world = m_world;
            std::set<std::string> vectorNames, scalarNames;
            for (const PreviewParam& param : request.vectors) vectorNames.insert(param.name);
            for (const PreviewParam& param : request.scalars) scalarNames.insert(param.name);
            for (std::int32_t slot = 0; slot < 16; ++slot)
            {
                UObject* material = nullptr;
                Call(mesh, STR("/Script/Engine.PrimitiveComponent:GetMaterial"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                    if (n == STR("ElementIndex")) std::memcpy(v, &slot, sizeof(slot));
                }, &material);
                if (!material) break;
                UObject* base = m_params.MeshDefault(mesh, slot);
                const bool skinFits = m_params.Has(material, vectorNames, scalarNames), baseFits = base && m_params.Has(base, vectorNames, scalarNames);
                Log("cosmetics preview: slot " + std::to_string(slot) + " skin " + m_params.Describe(material) + (base && base != material ? "; base " + m_params.Describe(base) : "") +
                    (baseFits ? " -> tint on base" : skinFits ? " -> tint on skin" : " -> tint parameters missing"));
                if (baseFits) material = base;
                else if (!skinFits) continue;
                UObject* mid = nullptr;
                Call(materials, STR("/Script/Engine.KismetMaterialLibrary:CreateDynamicMaterialInstance"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                    if (n == STR("WorldContextObject")) WriteObject(v, world);
                    else if (n == STR("Parent")) WriteObject(v, material);
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
                    else if (n == STR("Material")) WriteObject(v, mid);
                });
            }
        }
        Frame(mesh);
        WearAccessories(request);
        if (request.weaponView) ShowWeapon(request);
        else RemoveWeapon();
    }

    // The weapon view: the player's selected weapon (or the game's pistol) on
    // its own, with the requested finish, turning on the stage's pivot.
    void CosmeticsPreview::ShowWeapon(const PreviewRequest& request)
    {
        RemoveWeapon();
        UObject* stage = m_stage.Get();
        UObject* mesh = m_mesh.Get();
        UObject* meshes = m_meshes.Get();
        if (!stage || !mesh || !meshes) return;
        // The selected viewmodel weapon's asset and materials.
        UObject* selected = nullptr;
        if (UObject* character = GetObject(m_scene.Player(), STR("MyCharacter")))
            if (UObject* view = GetObject(character, STR("ViewModel_Native")))
                Call(view, STR("/Script/GameSkillsTrainer.FPSPlayer_WeaponComponentActor:GetSelectWeaponMesh"), {}, &selected);
        UObject* skeletal = selected ? GetObject(selected, STR("SkeletalMesh")) : nullptr;
        UObject* fixed = selected && !skeletal ? GetObject(selected, STR("StaticMesh")) : nullptr;
        if (!skeletal && !fixed) fixed = LoadGameAsset(FallbackWeapon);
        if (!skeletal && !fixed)
        {
            Log("cosmetics preview: no weapon to show");
            return;
        }
        UClass* type = UObjectGlobals::StaticFindObject<UClass*>(nullptr, nullptr, skeletal ? STR("/Script/Engine.SkeletalMeshComponent") : STR("/Script/Engine.StaticMeshComponent"));
        UObject* weapon = nullptr;
        Call(stage, STR("/Script/Engine.Actor:AddComponentByClass"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
            if (n == STR("Class")) WriteObject(v, type);
            else if (n == STR("bManualAttachment")) WriteBoolParam(v, p, true);
            else if (n == STR("RelativeTransform"))
                if (auto* s = CastField<FStructProperty>(p))
                    for (const char* field : {"Rotation.W", "Scale3D.X", "Scale3D.Y", "Scale3D.Z"}) game::SetStructPath(v, s->GetStruct(), field, 1);
        }, &weapon);
        if (!weapon || !type || !weapon->IsA(type)) return;
        m_weapon = FWeakObjectPtr(weapon);
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
        // The selected weapon's own materials (its skin), then the finish on top.
        const cosmetics::Item* finish = nullptr;
        if (!request.finish.empty())
            if (const Output::CosmeticsInputs inputs = m_output.cosmetics(); inputs.library)
            {
                cosmetics::ResolveOptions options;
                options.allowDrafts = inputs.allowDrafts;
                finish = cosmetics::Resolve(inputs.library->index, request.finish, options);
                if (finish && finish->kind != "weapon_finish") finish = nullptr;
            }
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
            if (!material) continue;
            if (finish)
            {
                UObject* mid = nullptr;
                Call(Default(STR("/Script/Engine.Default__KismetMaterialLibrary")), STR("/Script/Engine.KismetMaterialLibrary:CreateDynamicMaterialInstance"),
                     [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                         if (n == STR("WorldContextObject")) WriteObject(v, stage);
                         else if (n == STR("Parent")) WriteObject(v, material);
                     },
                     &mid);
                if (mid)
                {
                    for (const auto& [name, c] : finish->vector)
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
        ApplyMaterialDataDefaults(weapon);
        // On the stage's turntable, its bounds centred over the pivot, a metre up.
        Call(weapon, STR("/Script/Engine.SceneComponent:K2_AttachToComponent"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
            if (n == STR("Parent")) WriteObject(v, meshes);
            // KeepRelative (0) for location, rotation and scale.
        });
        float pivot[3], origin[3], extent[3], at[3];
        if (Location(meshes, pivot) && Bounds(weapon, origin, extent) && Location(weapon, at))
        {
            const float to[3] = {at[0] + pivot[0] - origin[0], at[1] + pivot[1] - origin[1], at[2] + pivot[2] + 100 - origin[2]};
            SetWorldLocation(weapon, to);
        }
        // Only the weapon is shown.
        SetVisible(mesh, false, false);
        for (FWeakObjectPtr& worn : m_accessories) SetVisible(worn.Get(), false, false);
        SetVisible(weapon, true, false);
        Frame(weapon);
        Log(std::string("cosmetics preview: weapon view (") + (skeletal ? "selected weapon" : selected ? "selected weapon" : "the game's pistol") + (finish ? ", " + finish->id : std::string()) + ")");
    }

    void CosmeticsPreview::RemoveWeapon()
    {
        if (UObject* weapon = m_weapon.Get()) RemoveAccessory(weapon);
        m_weapon = FWeakObjectPtr{};
    }

    // Accessories from the installed catalog, by id: only released fit
    // accessories (game meshes, curated). Attached in the pose the camera sees.
    void CosmeticsPreview::WearAccessories(const PreviewRequest& request)
    {
        if (request.accessories.empty()) return;
        const Output::CosmeticsInputs inputs = m_output.cosmetics();
        UObject* stage = m_stage.Get();
        UObject* mesh = m_mesh.Get();
        if (!inputs.library || !stage || !mesh) return;
        cosmetics::ResolveOptions options;
        options.allowDrafts = inputs.allowDrafts;
        options.verifiedPaks = inputs.library->verifiedPaks;
        for (const std::string& id : request.accessories)
        {
            std::string why;
            const cosmetics::Item* item = cosmetics::Resolve(inputs.library->index, id, options, &why);
            if (!item || item->kind != "accessory" || !item->fit)
            {
                Log("cosmetics preview: accessory " + id + " not shown (" + (item ? std::string("not a game-mesh accessory") : why) + ")");
                continue;
            }
            if (UObject* worn = AttachFitAccessory(stage, mesh, *item, why)) m_accessories.push_back(FWeakObjectPtr(worn));
            else Log("cosmetics preview: accessory " + id + " not shown (" + why + ")");
        }
    }

    void CosmeticsPreview::RemoveAccessories()
    {
        for (FWeakObjectPtr& weak : m_accessories) RemoveAccessory(weak.Get());
        m_accessories.clear();
    }

    // Camera and lights on the character: the camera keeps the stage's own
    // front view, moves back until the whole character (turned any way) fits
    // a 30 degree view, and the rig sits around it. ComposePreview then frames
    // the frame exactly on the silhouette.
    void CosmeticsPreview::Frame(UObject* mesh)
    {
        UObject* meshes = m_meshes.Get();
        UObject* capture = m_capture.Get();
        if (!mesh || !meshes || !capture) return;
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
        float dir[2] = {centre[0] - m_cameraHome[0], centre[1] - m_cameraHome[1]};
        const float length = std::hypot(dir[0], dir[1]);
        if (length < 1.0f) dir[0] = 1, dir[1] = 0;
        else dir[0] /= length, dir[1] /= length;
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
        SetByte(m_capture.Get(), STR("CaptureSource"), SourceFinalColor);
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

    void CosmeticsPreview::Teardown(const char* why, bool destroy)
    {
        UObject* stage = m_stage.Get();
        UObject* target = m_target.Get();
        const bool had = stage || target;
        if (had)
        {
            if (destroy && SafeToDestroy(stage, m_world))
            {
                // Our own components first, then the stage (it was spawned by AimModCore).
                RemoveAccessories();
                RemoveWeapon();
                static_cast<AActor*>(stage)->K2_DestroyActor();
                if (Alive(target))
                    Call(Default(STR("/Script/Engine.Default__KismetRenderingLibrary")), STR("/Script/Engine.KismetRenderingLibrary:ReleaseRenderTarget2D"),
                         [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                             if (n == STR("TextureRenderTarget")) WriteObject(v, target);
                         });
            }
            else if (m_orphans.size() < 16)
                m_orphans.push_back({m_stage, m_target, m_world}); // destroyed later in a safe tick, or by the level
        }
        m_stage = m_target = m_capture = m_meshes = m_mesh = FWeakObjectPtr{};
        m_lights.clear();
        m_accessories.clear();
        m_weapon = FWeakObjectPtr{};
        m_world = nullptr;
        m_lookKey.clear();
        m_followUps.clear();
        m_dirty = false;
        if (had)
        {
            std::error_code error;
            std::filesystem::remove(m_framePath, error);
            for (const wchar_t* file : {L"capture-color.png", L"capture-mask.png", L"capture-normal.png"}) std::filesystem::remove(m_frames / file, error);
            Log(std::string("cosmetics preview stage ") + (destroy ? "removed" : "left to the level") + " (" + why + ")");
        }
    }

    // Stages forgotten during a transition: destroyed once the same world is
    // current and safe again; dropped when their world is gone (the level took them).
    void CosmeticsPreview::DestroyOrphans(UObject* world)
    {
        std::vector<Orphan> keep;
        for (Orphan& o : m_orphans)
        {
            UObject* stage = o.stage.Get();
            if (!stage || !world || o.world != world || !Alive(stage)) continue;
            if (!SafeToDestroy(stage, world))
            {
                keep.push_back(o);
                continue;
            }
            static_cast<AActor*>(stage)->K2_DestroyActor();
            if (UObject* target = o.target.Get(); Alive(target))
                Call(Default(STR("/Script/Engine.Default__KismetRenderingLibrary")), STR("/Script/Engine.KismetRenderingLibrary:ReleaseRenderTarget2D"),
                     [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                         if (n == STR("TextureRenderTarget")) WriteObject(v, target);
                     });
            Log("cosmetics preview: an earlier stage was removed");
        }
        m_orphans = std::move(keep);
    }

    // May run off the game thread (mod unload): no engine calls. The stage is
    // inert without the tick (capture on demand only) and goes with the level.
    void CosmeticsPreview::Shutdown()
    {
        m_orphans.clear();
        m_stage = m_target = m_capture = m_meshes = m_mesh = FWeakObjectPtr{};
        m_lights.clear();
        m_accessories.clear();
        m_world = nullptr;
        std::error_code error;
        std::filesystem::remove(m_framePath, error);
    }
} // namespace aimmod

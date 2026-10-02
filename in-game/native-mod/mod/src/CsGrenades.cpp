#include "CsGrenades.hpp"

#include "CsGear.hpp"
#include "Log.hpp"
#include "Reflect.hpp"

#include <Unreal/AActor.hpp>
#include <Unreal/FProperty.hpp>
#include <Unreal/Property/FStructProperty.hpp>
#include <Unreal/UClass.hpp>
#include <Unreal/UObject.hpp>
#include <Unreal/UObjectGlobals.hpp>

#include <Windows.h>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>
#include <fstream>
#include <iterator>

namespace aimmod
{
    using namespace reflect;
    using RC::Unreal::FWeakObjectPtr;

    namespace
    {
        constexpr const wchar_t* Sphere = L"/Engine/BasicShapes/Sphere.Sphere";
        constexpr const wchar_t* Cylinder = L"/Engine/BasicShapes/Cylinder.Cylinder";
        constexpr const char* Kinds[] = {"he", "flash", "smoke", "decoy", "molotov", "incendiary"};
        constexpr double RadToDeg = 57.29577951308232;
        // The flash layer sits above every other view (the notice layer is 20000).
        constexpr std::int32_t FlashZOrder = 30000;
        constexpr std::uint8_t Collapsed = 1, HitTestInvisible = 3;
        // The after-image: one capture of the final picture at half of 1080p (stretched to the screen).
        constexpr std::int32_t CaptureWidth = 960, CaptureHeight = 540;
        constexpr std::uint8_t RgbaTarget = 2, FinalColour = 2; // ETextureRenderTargetFormat RTF_RGBA8, ESceneCaptureSource SCS_FinalColorLDR

        std::int64_t UnixMs()
        {
            return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
        }
        void WriteInt32(std::uint8_t* value, std::int32_t number) { std::memcpy(value, &number, sizeof number); }
        // The smoke cloud: grey puffs, lighter higher up (as if lit from above), a touch cool.
        const std::vector<cs::Part>& PuffParts()
        {
            static const std::vector<cs::Part> parts = [] {
                std::vector<cs::Part> list;
                for (const cs::Puff& p : cs::SmokePuffs())
                {
                    const double g = 0.5 + 0.22 * p.shade;
                    list.push_back({Sphere, {p.offset[0], p.offset[1], p.offset[2]}, {p.size, p.size, p.size * 0.8}, {0, p.phase * RadToDeg, 0}, {g, g * 1.01, g * 1.04}, false});
                }
                return list;
            }();
            return parts;
        }
        std::vector<cs::Part> FireParts(double radius, std::int64_t seed)
        {
            std::vector<cs::Part> list;
            bool pool = true;
            for (const cs::Flame& f : cs::FireFlames(radius, 1.0, 10.0, seed))
            {
                list.push_back({pool ? Cylinder : Sphere, {f.offset[0], f.offset[1], f.offset[2]}, {f.size[0], f.size[1], f.size[2]}, {0, 0, 0}, {f.colour[0], f.colour[1], f.colour[2]}, false});
                pool = false;
            }
            return list;
        }
        std::vector<cs::Part> BlastParts(const std::string& kind)
        {
            cs::Part p{Sphere, {0, 0, 0}, {100, 100, 100}, {0, 0, 0}, {1, 1, 1}, false};
            cs::BlastColour(kind, p.colour);
            return {p};
        }
        UObject* AddComponentByClass(UObject* owner, const wchar_t* classPath)
        {
            UClass* type = game::FindClass(classPath);
            UObject* component = nullptr;
            if (!type || !owner) return nullptr;
            Call(owner, STR("/Script/Engine.Actor:AddComponentByClass"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                if (n == STR("Class")) WriteObject(v, type);
                else if (n == STR("bManualAttachment")) WriteBoolParam(v, p, false);
                else if (n == STR("RelativeTransform"))
                    if (auto* s = RC::Unreal::CastField<RC::Unreal::FStructProperty>(p))
                        for (const char* field : {"Rotation.W", "Scale3D.X", "Scale3D.Y", "Scale3D.Z"}) game::SetStructPath(v, s->GetStruct(), field, 1);
            }, &component);
            return Valid(component, type) ? component : nullptr;
        }
        void Visibility(UObject* widget, std::uint8_t value)
        {
            if (widget) Call(widget, STR("/Script/UMG.Widget:SetVisibility"), [value](const std::wstring& n, FProperty*, std::uint8_t* v) {
                if (n == STR("InVisibility")) *v = value;
            });
        }
        bool Colour(UObject* image, double r, double g, double b, double a)
        {
            return Call(image, STR("/Script/UMG.Image:SetColorAndOpacity"), [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                if (n == STR("InColorAndOpacity")) WriteFloats(v, p, {static_cast<float>(r), static_cast<float>(g), static_cast<float>(b), static_cast<float>(a)});
            });
        }
        void FullScreen(UObject* slot)
        {
            Call(slot, STR("/Script/UMG.CanvasPanelSlot:SetAutoSize"), [](const std::wstring&, FProperty* p, std::uint8_t* v) { WriteBoolParam(v, p, false); });
            Call(slot, STR("/Script/UMG.CanvasPanelSlot:SetAnchors"), [](const std::wstring&, FProperty* p, std::uint8_t* v) { WriteFloats(v, p, {0.0f, 0.0f, 1.0f, 1.0f}); });
            Call(slot, STR("/Script/UMG.CanvasPanelSlot:SetAlignment"), [](const std::wstring&, FProperty* p, std::uint8_t* v) { WriteFloats(v, p, {0.0f, 0.0f}); });
            Call(slot, STR("/Script/UMG.CanvasPanelSlot:SetOffsets"), [](const std::wstring&, FProperty* p, std::uint8_t* v) { WriteFloats(v, p, {0.0f, 0.0f, 0.0f, 0.0f}); });
        }
    } // namespace

    void CsGrenades::Read(const std::filesystem::path& root)
    {
        const std::int64_t now = UnixMs();
        if (now < m_nextRead) return;
        m_nextRead = now + 40;
        const auto file = root / L"grenades.tsv";
        WIN32_FILE_ATTRIBUTE_DATA data{};
        std::uint64_t stamp = 0;
        if (GetFileAttributesExW(file.c_str(), GetFileExInfoStandard, &data) && data.nFileSizeHigh == 0 && data.nFileSizeLow <= 64 * 1024)
            stamp = (static_cast<std::uint64_t>(data.ftLastWriteTime.dwHighDateTime) << 32) | data.ftLastWriteTime.dwLowDateTime;
        FILETIME nowFile{};
        GetSystemTimeAsFileTime(&nowFile);
        const std::uint64_t nowStamp = (static_cast<std::uint64_t>(nowFile.dwHighDateTime) << 32) | nowFile.dwLowDateTime;
        // Not rewritten for 5 s: the service stopped (nothing to draw).
        if (stamp == 0 || nowStamp - stamp >= 5ull * 10000000ull)
        {
            m_state.reset();
            m_stamp = 0;
            return;
        }
        if (stamp == m_stamp) return;
        m_stamp = stamp;
        std::ifstream in(file, std::ios::binary);
        const std::string text((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
        std::string why;
        m_state = cs::ParseGrenades(text, &why);
        if (!m_state && !m_logged)
        {
            m_logged = true;
            std::string line;
            for (char c : why) line += c == '\t' ? ' ' : c;
            Log("cs grenades: grenades.tsv ignored (malformed at \"" + line + "\")");
        }
    }

    void CsGrenades::Tick(UObject* character, int hand, const std::filesystem::path& root, const std::string& scenario)
    {
        Read(root);
        if (m_state && m_state->scenario != scenario) m_state.reset();
        const std::int64_t now = UnixMs();
        Hand(character, hand, now);
        World(character, now);
        InsideView(character, now);
        Flash(character, now);
    }

    void CsGrenades::Hand(UObject* character, int hand, std::int64_t now)
    {
        if (!character) return;
        if (m_handOwner.Get() != character)
        {
            // A new body (a respawn): the old models went with the old one.
            m_handOwner = FWeakObjectPtr(character);
            m_handModels.clear();
            m_handBuilt = false;
            m_handShown.clear();
            m_handPose.clear();
        }
        std::string want;
        bool pin = false;
        if (m_state && hand == cs::GrenadeSlot && !m_state->hand.kind.empty() && !(m_state->hand.thrownMs > 0 && now - m_state->hand.thrownMs < cs::ThrownHideSeconds * 1000))
            want = m_state->hand.kind, pin = m_state->hand.pin;
        if (!want.empty() && !m_handBuilt)
        {
            m_handBuilt = true;
            UObject* camera = GetObject(character, STR("FirstPersonCamera"));
            int built = 0;
            for (const char* kind : Kinds)
                if (UObject* model = camera ? CsGear::BuildModel(character, camera, cs::GrenadeModel(kind), nullptr) : nullptr)
                {
                    SetVisible(model, false, true);
                    m_handModels[kind] = FWeakObjectPtr(model);
                    ++built;
                }
            Log("cs grenades: " + std::to_string(built) + " grenade model(s) for the hand" + (camera ? "" : " (no first-person camera)"));
        }
        if (want != m_handShown)
        {
            if (auto it = m_handModels.find(m_handShown); it != m_handModels.end())
                if (UObject* old = it->second.Get(); old && Alive(old)) SetVisible(old, false, true);
            m_handShown = want;
            m_handPose.clear();
            if (auto it = m_handModels.find(want); it != m_handModels.end())
                if (UObject* model = it->second.Get(); model && Alive(model)) SetVisible(model, true, true);
        }
        const std::string pose = want + (pin ? "+pin" : "");
        if (!want.empty() && pose != m_handPose)
            if (auto it = m_handModels.find(want); it != m_handModels.end())
                if (UObject* model = it->second.Get(); model && Alive(model))
                {
                    const cs::Hold hold = cs::GrenadeInHand(pin);
                    CsGear::Place(model, hold.offset, hold.rotation, nullptr);
                    m_handPose = pose;
                }
    }

    CsGrenades::Shown* CsGrenades::Ensure(const std::string& key, UObject* character, double x, double y, double z, const std::vector<cs::Part>& parts, bool keepParts)
    {
        if (auto it = m_shown.find(key); it != m_shown.end())
        {
            UObject* actor = it->second.actor.Get();
            if (actor && Alive(actor))
            {
                it->second.seen = true;
                return &it->second;
            }
            m_shown.erase(it);
        }
        if (!character || UnixMs() < m_nextBuildTry) return nullptr;
        UObject* actor = CsGear::SpawnHolder(character, x, y, z);
        UObject* root = actor ? GetObject(actor, STR("StaticMeshComponent")) : nullptr;
        std::vector<FWeakObjectPtr> built;
        UObject* model = root ? CsGear::BuildModel(actor, root, parts, nullptr, keepParts ? &built : nullptr) : nullptr;
        if (!model)
        {
            if (actor) Call(actor, STR("/Script/Engine.Actor:K2_DestroyActor"));
            m_nextBuildTry = UnixMs() + 2000; // the game can't build models now: try again later
            Log("cs grenades: a model could not be built (" + key + ")");
            return nullptr;
        }
        Shown& s = m_shown[key];
        s.actor = FWeakObjectPtr(actor);
        s.model = FWeakObjectPtr(model);
        s.parts = std::move(built);
        s.partShown.assign(s.parts.size(), 1);
        s.seen = true;
        return &s;
    }

    void CsGrenades::Move(UObject* actor, double x, double y, double z, double pitch, double yaw)
    {
        Call(actor, STR("/Script/Engine.Actor:K2_SetActorLocationAndRotation"), [&](const std::wstring& n, RC::Unreal::FProperty* p, std::uint8_t* v) {
            if (n == STR("NewLocation")) WriteFloats(v, p, {static_cast<float>(x), static_cast<float>(y), static_cast<float>(z)});
            else if (n == STR("NewRotation")) WriteFloats(v, p, {static_cast<float>(pitch), static_cast<float>(yaw), 0.0f});
            else if (n == STR("bTeleport")) WriteBoolParam(v, p, true);
        });
    }

    // Every puff of one smoke where cs::SmokePuff has it, 30 times a second.
    void CsGrenades::PoseSmoke(Shown& s, double age, double left, std::int64_t now)
    {
        if (now - s.posedAt < 33) return;
        s.posedAt = now;
        const auto& puffs = cs::SmokePuffs();
        for (std::size_t i = 0; i < s.parts.size() && i < puffs.size(); ++i)
        {
            UObject* component = s.parts[i].Get();
            if (!component || !Alive(component)) continue;
            const cs::PuffPose pose = cs::SmokePuff(puffs[i], age, left);
            if (static_cast<bool>(s.partShown[i]) != pose.shown)
            {
                SetVisible(component, pose.shown, false);
                s.partShown[i] = pose.shown ? 1 : 0;
            }
            if (!pose.shown) continue;
            // Squashed spheres turning slowly, so the cloud's outline keeps changing.
            const double rotation[3] = {0, puffs[i].phase * RadToDeg + age * (i % 2 ? 4.0 : -3.0), 0};
            const double scale[3] = {pose.size[0] / 100, pose.size[1] / 100, pose.size[2] / 100};
            CsGear::Place(component, pose.offset, rotation, scale);
        }
    }

    void CsGrenades::World(UObject* character, std::int64_t now)
    {
        for (auto& [_, s] : m_shown) s.seen = false;
        auto scaleTo = [](Shown& s, double sx, double sy, double sz) {
            if (std::fabs(sx - s.scale) < 0.01 && sz == sx) return;
            if (UObject* model = s.model.Get(); model && Alive(model))
            {
                const double zero[3] = {0, 0, 0}, scale[3] = {sx, sy, sz};
                CsGear::Place(model, zero, zero, scale);
                s.scale = sz == sx ? sx : -1;
            }
        };
        if (m_state)
        {
            for (const auto& f : m_state->flying)
            {
                if (f.endMs > 0 && now >= f.endMs) continue; // it went off
                const double ms = std::max<double>(0, static_cast<double>(now - f.startMs));
                const cs::Point p = cs::GrenadeAt(f.keys, ms);
                // Spinning in the air; on its side once it slides or rests.
                const cs::GrenadeKey* key = &f.keys.front();
                for (const auto& k : f.keys)
                    if (k.t <= ms) key = &k;
                if (Shown* s = Ensure("fly:" + std::to_string(f.id), character, p.x, p.y, p.z, cs::GrenadeModel(f.kind)))
                {
                    const bool flying = key->motion == cs::GrenadeFlight;
                    Move(s->actor.Get(), p.x, p.y, p.z + (flying ? 0 : 3), flying ? std::fmod(ms * 0.9, 360.0) : 90, std::fmod(ms * (flying ? 0.4 : 0) + f.id * 47.0, 360.0));
                }
            }
            for (const auto& a : m_state->smokes)
            {
                if (Shown* can = Ensure("smoke-can:" + std::to_string(a.id), character, a.x, a.y, a.z + 3, cs::GrenadeModel("smoke"))) Move(can->actor.Get(), a.x, a.y, a.z + 3, 90, a.id * 47.0);
                if (now < a.startMs || now >= a.endMs) continue;
                const cs::Point c = cs::SmokeCentre(a.x, a.y, a.z);
                if (Shown* s = Ensure("smoke:" + std::to_string(a.id), character, c.x, c.y, c.z, PuffParts(), true))
                    PoseSmoke(*s, (now - a.startMs) / 1000.0, (a.endMs - now) / 1000.0, now);
            }
            for (const auto& a : m_state->fires)
            {
                if (now < a.startMs || now >= a.endMs) continue;
                const double t = (now - a.startMs) / 1000.0, left = (a.endMs - now) / 1000.0;
                if (Shown* s = Ensure("fire:" + std::to_string(a.id), character, a.x, a.y, a.z, FireParts(a.radius, a.id)))
                {
                    // The flames flicker and the fire dies down in its last second.
                    const double life = std::clamp(std::min(t / 0.3, left), 0.05, 1.0);
                    const double flicker = 0.8 + 0.2 * std::sin(t * 9.1) * std::sin(t * 5.3 + 1.0);
                    scaleTo(*s, life * (0.97 + 0.03 * std::sin(t * 4.0)), life * (0.97 + 0.03 * std::cos(t * 3.7)), life * flicker);
                }
            }
            for (const auto& a : m_state->decoys)
                if (now >= a.startMs && now < a.endMs)
                    if (Shown* s = Ensure("decoy:" + std::to_string(a.id), character, a.x, a.y, a.z + 3, cs::GrenadeModel("decoy"))) Move(s->actor.Get(), a.x, a.y, a.z + 3, 90, a.id * 47.0);
            for (const auto& b : m_state->blasts)
            {
                const double size = cs::BlastSize(b.kind, (now - b.atMs) / 1000.0);
                if (size <= 0) continue;
                if (Shown* s = Ensure("blast:" + std::to_string(b.id), character, b.x, b.y, b.z, BlastParts(b.kind))) scaleTo(*s, size / 100, size / 100, size / 100);
            }
        }
        for (auto it = m_shown.begin(); it != m_shown.end();)
        {
            if (it->second.seen) { ++it; continue; }
            if (UObject* actor = it->second.actor.Get(); actor && Alive(actor)) Call(actor, STR("/Script/Engine.Actor:K2_DestroyActor"));
            it = m_shown.erase(it);
        }
    }

    std::optional<CsGrenades::View> CsGrenades::CameraView(UObject* character) const
    {
        UObject* controller = character ? GetObject(character, STR("Controller")) : nullptr;
        UObject* manager = controller ? GetObject(controller, STR("PlayerCameraManager")) : nullptr;
        if (!manager) return std::nullopt;
        View view;
        float f[3]{};
        bool located = false, turned = false;
        Call(manager, STR("/Script/Engine.PlayerCameraManager:GetCameraLocation"), {}, nullptr, [&](const std::wstring& n, FProperty* p, const std::uint8_t* v) {
            if (n == STR("ReturnValue") && ReadFloats(v, p, f, 3))
            {
                for (int i = 0; i < 3; ++i) view.location[i] = f[i];
                located = true;
            }
        });
        Call(manager, STR("/Script/Engine.PlayerCameraManager:GetCameraRotation"), {}, nullptr, [&](const std::wstring& n, FProperty* p, const std::uint8_t* v) {
            if (n == STR("ReturnValue") && ReadFloats(v, p, f, 3))
            {
                for (int i = 0; i < 3; ++i) view.rotation[i] = f[i];
                turned = true;
            }
        });
        Call(manager, STR("/Script/Engine.PlayerCameraManager:GetFOVAngle"), {}, nullptr, [&](const std::wstring& n, FProperty*, const std::uint8_t* v) {
            if (n == STR("ReturnValue"))
            {
                float fov = 0;
                std::memcpy(&fov, v, sizeof fov);
                if (fov > 5 && fov < 170) view.fov = fov;
            }
        });
        if (!located || !turned) return std::nullopt;
        return view;
    }

    UObject* CsGrenades::Effects(UObject* character)
    {
        if (UObject* actor = m_effects.Get(); actor && Alive(actor)) return actor;
        if (m_effectsFailed || !character) return nullptr;
        const auto view = CameraView(character);
        UObject* actor = CsGear::SpawnHolder(character, view ? view->location[0] : 0, view ? view->location[1] : 0, view ? view->location[2] : 0);
        if (!actor)
        {
            m_effectsFailed = true;
            Log("cs grenades: no effects actor (smoke inside view and flash after-image off)");
            return nullptr;
        }
        m_effects = FWeakObjectPtr(actor);
        // Inside a smoke: the picture flattened to a light grey (no colour, almost no contrast), blended
        // in by how deep the camera is in the cloud.
        UObject* post = AddComponentByClass(actor, STR("/Script/Engine.PostProcessComponent"));
        bool graded = false;
        if (post)
        {
            SetBool(post, STR("bUnbound"), true);
            SetFloat(post, STR("BlendWeight"), 0.0f);
            SetFloat(post, STR("Priority"), 50.0f);
            if (auto* settings = RC::Unreal::CastField<RC::Unreal::FStructProperty>(PropertyOf(post->GetClassPrivate(), STR("Settings"))))
            {
                std::uint8_t* s = At(post, settings);
                RC::Unreal::UStruct* type = settings->GetStruct();
                auto vec = [&](const char* name, double x, double w) {
                    bool ok = true;
                    for (const char* axis : {".X", ".Y", ".Z"}) ok = game::SetStructPath(s, type, std::string(name) + axis, x) && ok;
                    ok = game::SetStructPath(s, type, std::string(name) + ".W", w) && ok;
                    return game::SetStructPath(s, type, std::string("bOverride_") + name, 1) && ok;
                };
                graded = vec("ColorSaturation", 0.0, 1.0) && vec("ColorContrast", 0.12, 1.0) && vec("ColorGain", 1.7, 1.0);
                game::SetStructPath(s, type, "VignetteIntensity", 0.5);
                game::SetStructPath(s, type, "bOverride_VignetteIntensity", 1);
            }
            m_post = FWeakObjectPtr(post);
        }
        UObject* capture = AddComponentByClass(actor, STR("/Script/Engine.SceneCaptureComponent2D"));
        if (capture)
        {
            SetBool(capture, STR("bCaptureEveryFrame"), false);
            SetBool(capture, STR("bCaptureOnMovement"), false);
            SetByte(capture, STR("CaptureSource"), FinalColour);
            m_capture = FWeakObjectPtr(capture);
        }
        Log(std::string("cs grenades: effects ready (smoke inside view ") + (post && graded ? "on" : post ? "partial" : "off") + ", flash after-image capture " + (capture ? "on" : "off") + ")");
        return actor;
    }

    // The camera inside a smoke: the grey inside view, by how deep it is (the puffs are not seen from inside).
    void CsGrenades::InsideView(UObject* character, std::int64_t now)
    {
        double depth = 0;
        if (m_state && !m_state->smokes.empty())
            if (const auto view = CameraView(character)) depth = cs::SmokeDepth(m_state->smokes, now, view->location[0], view->location[1], view->location[2]);
        if (depth <= 0 && m_insideWeight <= 0) return;
        if (depth > 0) Effects(character);
        UObject* post = m_post.Get();
        if (!post || !Alive(post)) return;
        const double weight = std::round(depth * 50) / 50;
        if (weight == m_insideWeight) return;
        if (weight > 0 && !m_insideLogged)
        {
            m_insideLogged = true;
            Log("cs grenades: the camera is inside a smoke (grey inside view)");
        }
        m_insideWeight = weight;
        SetFloat(post, STR("BlendWeight"), static_cast<float>(weight));
    }

    // The flash layer: its own host widget in the viewport above everything, a canvas with the
    // after-image under a white image, both full screen, never taking input.
    bool CsGrenades::FlashLayer(UObject* character)
    {
        if (UObject* host = m_flashHost.Get(); host && Alive(host) && m_white.Get()) return true;
        if (m_flashFailed) return false;
        UObject* player = character ? GetObject(character, STR("Controller")) : nullptr;
        UObject* library = Default(STR("/Script/UMG.Default__WidgetBlueprintLibrary"));
        UClass* hostType = RC::Unreal::UObjectGlobals::StaticFindObject<UClass*>(nullptr, nullptr, STR("/Game/FirstPersonBP/Blueprints/UI/Palette/PalettedBorderWidget.PalettedBorderWidget_C"));
        UClass* canvasType = game::FindClass(STR("/Script/UMG.CanvasPanel"));
        UClass* imageType = game::FindClass(STR("/Script/UMG.Image"));
        auto fail = [&](const char* why, UObject* host) {
            m_flashFailed = true;
            Warn(std::string("cs grenades: no flash layer (") + why + "); the notice layer's white still shows");
            if (host) Call(host, STR("/Script/UMG.Widget:RemoveFromParent"));
            return false;
        };
        if (!player || !library || !hostType || !canvasType || !imageType) return fail("widget classes missing", nullptr);
        UObject* host = nullptr;
        Call(library, STR("/Script/UMG.WidgetBlueprintLibrary:Create"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
            if (n == STR("WorldContextObject") || n == STR("OwningPlayer")) WriteObject(v, player);
            else if (n == STR("WidgetType")) WriteObject(v, hostType);
        }, &host);
        UObject* tree = host && Alive(host) ? GetObject(host, STR("WidgetTree")) : nullptr;
        if (!tree || !Alive(tree)) return fail("no host widget", host);
        RC::Unreal::FStaticConstructObjectParameters canvasParams(canvasType, tree);
        UObject* canvas = RC::Unreal::UObjectGlobals::StaticConstructObject(canvasParams);
        if (!Alive(canvas) || !SetObject(tree, STR("RootWidget"), canvas)) return fail("no canvas", host);
        Visibility(canvas, HitTestInvisible);
        UObject* images[2]{};
        for (UObject*& image : images)
        {
            RC::Unreal::FStaticConstructObjectParameters imageParams(imageType, tree);
            image = RC::Unreal::UObjectGlobals::StaticConstructObject(imageParams);
            UObject* slot = nullptr;
            if (Alive(image))
                Call(canvas, STR("/Script/UMG.CanvasPanel:AddChildToCanvas"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
                    if (n == STR("Content")) WriteObject(v, image);
                }, &slot);
            if (!Alive(slot)) return fail("no image", host);
            FullScreen(slot);
            Visibility(image, HitTestInvisible);
        }
        SetBool(host, STR("bIsFocusable"), false);
        Visibility(host, Collapsed);
        Call(host, STR("/Script/UMG.UserWidget:AddToViewport"), [](const std::wstring& n, FProperty*, std::uint8_t* v) {
            if (n == STR("ZOrder")) WriteInt32(v, FlashZOrder);
        });
        SetBool(host, STR("bIsFocusable"), false); // Blueprint Construct can restore the defaults
        Colour(images[0], 1, 1, 1, 0);
        Colour(images[1], 1, 1, 1, 0);
        m_flashHost = FWeakObjectPtr(host);
        m_afterImage = FWeakObjectPtr(images[0]);
        m_white = FWeakObjectPtr(images[1]);
        m_flashShown = false;
        m_lastWhite = m_lastAfter = -1;
        Log("cs grenades: flash layer ready (z-order " + std::to_string(FlashZOrder) + ", above the notice layer)");
        return true;
    }

    // The after-image: the view as it is now, once, into a half-resolution target the layer shows.
    void CsGrenades::Capture(UObject* character)
    {
        m_captured = false;
        UObject* actor = Effects(character);
        UObject* capture = m_capture.Get();
        UObject* image = m_afterImage.Get();
        const auto view = CameraView(character);
        if (!actor || !capture || !Alive(capture) || !image || !view) return;
        UObject* target = m_target.Get();
        if (!target || !Alive(target))
        {
            target = nullptr;
            Call(Default(STR("/Script/Engine.Default__KismetRenderingLibrary")), STR("/Script/Engine.KismetRenderingLibrary:CreateRenderTarget2D"),
                 [&](const std::wstring& n, FProperty* p, std::uint8_t* v) {
                     if (n == STR("WorldContextObject")) WriteObject(v, character);
                     else if (n == STR("Width")) WriteInt32(v, CaptureWidth);
                     else if (n == STR("Height")) WriteInt32(v, CaptureHeight);
                     else if (n == STR("Format")) *v = RgbaTarget;
                     else if (n == STR("ClearColor")) WriteFloats(v, p, {1.0f, 1.0f, 1.0f, 1.0f});
                     else if (n == STR("bAutoGenerateMipMaps")) WriteBoolParam(v, p, false);
                 },
                 &target);
            if (!target || !Alive(target))
            {
                Log("cs grenades: no render target for the flash after-image");
                m_capture = FWeakObjectPtr{};
                return;
            }
            m_target = FWeakObjectPtr(target);
        }
        // The capture and the layer may be new (a new round or body) while the target is kept.
        SetObject(capture, STR("TextureTarget"), target);
        Call(image, STR("/Script/UMG.Image:SetBrushResourceObject"), [&](const std::wstring& n, FProperty*, std::uint8_t* v) {
            if (n == STR("ResourceObject")) WriteObject(v, target);
        });
        SetFloat(capture, STR("FOVAngle"), static_cast<float>(view->fov));
        Move(actor, view->location[0], view->location[1], view->location[2], view->rotation[0], view->rotation[1]);
        m_captured = Call(capture, STR("/Script/Engine.SceneCaptureComponent2D:CaptureScene"));
    }

    void CsGrenades::ShowFlash(double white, double after)
    {
        UObject* host = m_flashHost.Get();
        if (!host || !Alive(host)) return;
        const bool show = white > 0.003 || after > 0.003;
        if (show != m_flashShown)
        {
            Visibility(host, show ? HitTestInvisible : Collapsed);
            m_flashShown = show;
        }
        if (!show) return;
        white = std::round(white * 200) / 200;
        after = std::round(after * 200) / 200;
        if (UObject* image = m_afterImage.Get(); image && after != m_lastAfter && Colour(image, 1, 1, 1, after)) m_lastAfter = after;
        if (UObject* image = m_white.Get(); image && white != m_lastWhite && Colour(image, 1, 1, 1, white)) m_lastWhite = white;
    }

    // A flash that hit this player (grenades.tsv's flash line): the white every frame, and for a
    // strong one the frozen after-image under it.
    void CsGrenades::Flash(UObject* character, std::int64_t now)
    {
        const auto& flash = m_state ? m_state->flash : std::nullopt;
        if (!flash || now >= flash->atMs + flash->holdMs + flash->fadeMs)
        {
            if (m_flashShown) ShowFlash(0, 0);
            return;
        }
        if (flash->id != m_flashId)
        {
            m_flashId = flash->id;
            m_captured = false;
            if (FlashLayer(character) && flash->peak >= cs::AfterImageMinPeak) Capture(character);
            if (++m_flashes <= 3 || m_flashes % 20 == 0)
                Log("cs grenades: flashed (white " + std::to_string(static_cast<int>(flash->peak * 100)) + "% for " + std::to_string(flash->holdMs) + " ms, clearing over " +
                    std::to_string(flash->fadeMs) + " ms, " + std::to_string(now - flash->atMs) + " ms after the pop; after-image " +
                    (m_captured ? "captured" : flash->peak >= cs::AfterImageMinPeak ? "not available" : "none (a glance)") + "; " + std::to_string(m_flashes) + " so far)");
        }
        if (!m_flashHost.Get()) return;
        ShowFlash(cs::FlashWhite(*flash, now), m_captured ? cs::FlashAfterImage(*flash, now) : 0);
    }

    void CsGrenades::DropEffects()
    {
        if (UObject* actor = m_effects.Get(); actor && Alive(actor)) Call(actor, STR("/Script/Engine.Actor:K2_DestroyActor"));
        if (UObject* host = m_flashHost.Get(); host && Alive(host)) Call(host, STR("/Script/UMG.Widget:RemoveFromParent"));
        m_effects = m_post = m_capture = m_flashHost = m_afterImage = m_white = FWeakObjectPtr{};
        m_insideWeight = -1;
        m_flashShown = m_captured = false;
        m_flashId = -1;
        m_lastWhite = m_lastAfter = -1;
    }

    void CsGrenades::Release(const char* why)
    {
        const bool had = !m_shown.empty() || !m_handShown.empty() || m_effects.Get() || m_flashHost.Get();
        for (auto& [_, s] : m_shown)
            if (UObject* actor = s.actor.Get(); actor && Alive(actor)) Call(actor, STR("/Script/Engine.Actor:K2_DestroyActor"));
        m_shown.clear();
        for (auto& [_, weak] : m_handModels)
            if (UObject* model = weak.Get(); model && Alive(model)) SetVisible(model, false, true);
        m_handShown.clear();
        m_handPose.clear();
        DropEffects();
        m_state.reset();
        m_stamp = 0;
        if (had) Log(std::string("cs grenades: released (") + why + ")");
    }
} // namespace aimmod

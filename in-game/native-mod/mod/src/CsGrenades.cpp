#include "CsGrenades.hpp"

#include "CsGear.hpp"
#include "Log.hpp"
#include "Reflect.hpp"

#include <Unreal/AActor.hpp>
#include <Unreal/FProperty.hpp>
#include <Unreal/UObject.hpp>

#include <Windows.h>

#include <chrono>
#include <cmath>
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

        std::int64_t UnixMs()
        {
            return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
        }
        // The smoke cloud: grey puffs, a little lighter or darker each.
        const std::vector<cs::Part>& PuffParts()
        {
            static const std::vector<cs::Part> parts = [] {
                std::vector<cs::Part> list;
                for (const cs::Puff& p : cs::SmokePuffs())
                {
                    const double g = 0.4 + 0.2 * p.shade;
                    list.push_back({Sphere, {p.offset[0], p.offset[1], p.offset[2]}, {p.size, p.size, p.size * 0.8}, {0, 0, 0}, {g, g, g * 1.03}, false});
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
        m_state = cs::ParseGrenades(text);
        if (!m_state && !m_logged)
        {
            m_logged = true;
            Log("cs grenades: grenades.tsv ignored (malformed)");
        }
    }

    void CsGrenades::Tick(UObject* character, int hand, const std::filesystem::path& root, const std::string& scenario)
    {
        Read(root);
        if (m_state && m_state->scenario != scenario) m_state.reset();
        const std::int64_t now = UnixMs();
        Hand(character, hand, now);
        World(character, now);
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

    CsGrenades::Shown* CsGrenades::Ensure(const std::string& key, UObject* character, double x, double y, double z, const std::vector<cs::Part>& parts)
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
        UObject* model = root ? CsGear::BuildModel(actor, root, parts, nullptr) : nullptr;
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
                const double scale = cs::SmokeScale(a.startMs, a.endMs, now);
                if (Shown* can = Ensure("smoke-can:" + std::to_string(a.id), character, a.x, a.y, a.z + 3, cs::GrenadeModel("smoke"))) Move(can->actor.Get(), a.x, a.y, a.z + 3, 90, a.id * 47.0);
                if (scale <= 0) continue;
                const cs::Point c = cs::SmokeCentre(a.x, a.y, a.z);
                if (Shown* s = Ensure("smoke:" + std::to_string(a.id), character, c.x, c.y, c.z, PuffParts())) scaleTo(*s, std::max(0.02, scale), std::max(0.02, scale), std::max(0.02, scale));
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

    void CsGrenades::Release(const char* why)
    {
        const bool had = !m_shown.empty() || !m_handShown.empty();
        for (auto& [_, s] : m_shown)
            if (UObject* actor = s.actor.Get(); actor && Alive(actor)) Call(actor, STR("/Script/Engine.Actor:K2_DestroyActor"));
        m_shown.clear();
        for (auto& [_, weak] : m_handModels)
            if (UObject* model = weak.Get(); model && Alive(model)) SetVisible(model, false, true);
        m_handShown.clear();
        m_handPose.clear();
        m_state.reset();
        m_stamp = 0;
        if (had) Log(std::string("cs grenades: released (") + why + ")");
    }
} // namespace aimmod

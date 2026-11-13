#include "Grenades.hpp"

#include <Unreal/AActor.hpp>
#include <Unreal/CoreUObject/UObject/UnrealType.hpp>
#include <Unreal/Core/Containers/Array.hpp>
#include <Unreal/FProperty.hpp>
#include <Unreal/UClass.hpp>
#include <Unreal/UObject.hpp>
#include <Unreal/UObjectGlobals.hpp>

#include <Windows.h>

#include <cstring>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <set>
#include <vector>

namespace aimmod
{
    using namespace RC::Unreal;
    using game::Kind;
    using game::Param;
    using game::Shape;
    namespace g = bridge::grenades;

    namespace
    {
        void WriteVector(std::uint8_t* value, const Param& p, const g::Vec& v)
        {
            if (p.size == 12)
            {
                const float f[3] = {static_cast<float>(v[0]), static_cast<float>(v[1]), static_cast<float>(v[2])};
                std::memcpy(value, f, sizeof(f));
            }
            else if (p.size == 24) std::memcpy(value, v.data(), sizeof(double) * 3);
        }
        std::uint64_t FileStamp(const std::filesystem::path& file, std::uint64_t& size)
        {
            WIN32_FILE_ATTRIBUTE_DATA info{};
            if (!GetFileAttributesExW(file.c_str(), GetFileExInfoStandard, &info)) return 0;
            size = (static_cast<std::uint64_t>(info.nFileSizeHigh) << 32) | info.nFileSizeLow;
            return (static_cast<std::uint64_t>(info.ftLastWriteTime.dwHighDateTime) << 32) | info.ftLastWriteTime.dwLowDateTime;
        }
        std::int64_t UnixMs()
        {
            FILETIME ft{};
            GetSystemTimeAsFileTime(&ft);
            const std::uint64_t ticks = (static_cast<std::uint64_t>(ft.dwHighDateTime) << 32) | ft.dwLowDateTime;
            return static_cast<std::int64_t>((ticks - 116444736000000000ull) / 10000ull);
        }
    } // namespace

    bool GrenadeSim::Bind()
    {
        if (m_bound) return m_ready;
        m_bound = true;
        // By object type first (map-creator pieces don't all block the Visibility channel), like the bots' traces.
        m_byObjects = m_trace.BindPath(STR("/Script/Engine.KismetSystemLibrary:LineTraceSingleForObjects"), Shape::Command);
        if (!m_byObjects) m_trace.BindPath(STR("/Script/Engine.KismetSystemLibrary:LineTraceSingle"), Shape::Command);
        m_kismet = UObjectGlobals::StaticFindObject<UObject*>(nullptr, nullptr, STR("/Script/Engine.Default__KismetSystemLibrary"));
        m_getPawn.BindPath(STR("/Script/Engine.Controller:K2_GetPawn"), Shape::Object);
        for (const Param& p : m_trace.params())
            if (p.out && p.structType && p.name == "OutHit")
                for (FProperty* member : p.structType->ForEachProperty())
                {
                    if (member->GetName() == STR("ImpactPoint")) m_impactPoint = member->GetOffset_Internal();
                    if (member->GetName() == STR("ImpactNormal")) m_impactNormal = member->GetOffset_Internal();
                    if (member->GetName() == STR("bStartPenetrating")) m_startPenetrating = CastField<FBoolProperty>(member);
                }
        m_ready = m_trace.ok() && m_kismet && m_impactPoint >= 0 && m_impactNormal >= 0;
        m_log(m_ready ? std::string("grenades: line traces ready (") + (m_byObjects ? "world objects" : "visibility channel") + ", every pawn left out); throws fly on this map"
                      : "grenades: line traces unavailable (" + m_trace.error() + "); the service flies throws over a level floor");
        return m_ready;
    }

    UObject* GrenadeSim::Context()
    {
        if (UObject* c = m_context.Get(); c && game::IsLiveInstance(c)) return c;
        const double now = bridge::Bridge::Now();
        if (now < m_nextFind) return nullptr;
        m_nextFind = now + 1.0;
        std::vector<UObject*> found;
        UObjectGlobals::FindAllOf(STR("MetaPlayerController"), found);
        for (UObject* candidate : found)
            if (game::IsLiveInstance(candidate))
            {
                m_context = FWeakObjectPtr(candidate);
                return candidate;
            }
        return nullptr;
    }

    void GrenadeSim::FindPawns(double now)
    {
        if (now < m_nextPawns) return;
        m_nextPawns = now + 1.0;
        m_pawns.clear();
        // Every KovaaK's character (FindAllOf takes subclasses too: the local FPSCharacter, bots, avatars),
        // and whatever the controllers possess besides.
        std::set<UObject*> pawns;
        std::vector<UObject*> found;
        UObjectGlobals::FindAllOf(STR("MetaCharacter"), found);
        for (UObject* character : found)
            if (game::IsLiveInstance(character)) pawns.insert(character);
        if (m_getPawn.ok())
            for (const wchar_t* type : {STR("MetaPlayerController"), STR("TheMetaAIController")})
            {
                found.clear();
                UObjectGlobals::FindAllOf(type, found);
                for (UObject* controller : found)
                    if (game::IsLiveInstance(controller))
                        if (UObject* pawn = m_getPawn.Object(controller); pawn && game::IsLiveInstance(pawn)) pawns.insert(pawn);
            }
        for (UObject* pawn : pawns)
            if (m_pawns.size() < 64) m_pawns.emplace_back(pawn);
    }

    std::optional<g::Hit> GrenadeSim::Trace(UObject* context, const g::Vec& a, const g::Vec& b, bool occlusion)
    {
        // No pawn stops a grenade or a line of sight: every player's and bot's body is left out, its
        // hitbox components too (they block line traces under their own object type).
        struct ArrayView { void* data; std::int32_t num, max; };
        ArrayView ignore{m_ignore.data(), static_cast<std::int32_t>(m_ignore.size()), static_cast<std::int32_t>(m_ignore.size())};
        ArrayView objectTypes{m_objectTypes.data(), 31, 31};
        bool hit = false, inside = false;
        g::Hit result{};
        int vectors = 0;
        ++m_traces;
        const bool called = m_trace.Call(
            m_kismet,
            [&](std::uint8_t* value, const Param& p) {
                if (p.worldContext) std::memcpy(value, &context, sizeof(context));
                else if (p.kind == Kind::Vector && vectors < 2) WriteVector(value, p, vectors++ == 0 ? a : b);
                else if (p.kind == Kind::Array && p.name == "ActorsToIgnore") std::memcpy(value, &ignore, sizeof(ignore));
                else if (p.kind == Kind::Array && p.name == "ObjectTypes") std::memcpy(value, &objectTypes, sizeof(objectTypes));
                else if (p.kind == Kind::Bool && p.boolProperty) p.boolProperty->SetPropertyValue(value, p.name == "bIgnoreSelf");
            },
            [&](const std::uint8_t* buffer, const std::vector<Param>& params) {
                for (const Param& p : params)
                {
                    if (p.ret && p.boolProperty) hit = p.boolProperty->GetPropertyValue(const_cast<std::uint8_t*>(buffer) + p.offset);
                    else if (p.out && p.name == "OutHit")
                    {
                        float point[3], normal[3];
                        std::memcpy(point, buffer + p.offset + m_impactPoint, sizeof(point));
                        std::memcpy(normal, buffer + p.offset + m_impactNormal, sizeof(normal));
                        result = {{point[0], point[1], point[2]}, {normal[0], normal[1], normal[2]}};
                        if (m_startPenetrating && m_startPenetrating->GetPropertyValue(const_cast<std::uint8_t*>(buffer) + p.offset + m_startPenetrating->GetOffset_Internal())) inside = true;
                    }
                }
            });
        if (occlusion && g::Occluded(called, hit, inside)) return g::Hit{a, {0, 0, 1}};
        if (!hit || inside) return std::nullopt;
        return result;
    }

    void GrenadeSim::Tick()
    {
        if (m_stateDir.empty()) return;
        const double now = bridge::Bridge::Now();
        if (now < m_nextRead) return;
        m_nextRead = now + 0.02;
        if (!m_files) m_files=std::make_unique<FileMailbox>(std::filesystem::path(m_stateDir)/L"grenade-sim.tsv",std::filesystem::path(m_stateDir)/L"grenade-paths.tsv");
        auto text=m_files->Read();
        if (!text || text->empty()) { m_requests.reset(); m_pending.clear(); return; }
        if (text!=m_text) { m_requests=g::ParseSim(*text); m_text=std::move(text); }
        if (!m_requests) return;
        const auto& requests = m_requests;
        UObject* context = Context();
        if (!Bind() || !context) return; // the service's level-floor fallback flies them
        FindPawns(now);
        m_ignore.clear();
        for (const auto& weak : m_pawns)
            if (auto* pawn = weak.Get(); pawn && game::IsLiveInstance(pawn)) m_ignore.push_back(pawn);
        for (std::uint8_t t = 0, i = 0; t < 32; ++t) if (t != 2) m_objectTypes[i++] = t;
        int budget = 64;
        const auto trace = [&](const g::Vec& a, const g::Vec& b) { return Trace(context, a, b); };
        // Fly each new throw once; keep answers only for what is still asked.
        std::set<std::int64_t> ids;
        for (const auto& t : requests->throws)
        {
            ids.insert(t.id);
            if (!m_paths.contains(t.id))
            {
                auto [it, inserted] = m_pending.try_emplace(t.id, t.origin, t.velocity);
                if (it->second.Advance(trace, budget))
                {
                    m_paths[t.id] = std::move(it->second.keys);
                    m_pending.erase(it);
                }
            }
        }
        std::erase_if(m_pending, [&](const auto& kv) { return !ids.contains(kv.first); });
        std::erase_if(m_paths, [&](const auto& kv) { return !ids.contains(kv.first); });
        std::set<int> tags;
        for (const auto& l : requests->los)
        {
            tags.insert(l.tag);
            if (!m_los.contains(l.tag) && budget > 0) { --budget; m_los[l.tag] = !Trace(context, l.from, l.to, true).has_value(); }
        }
        std::erase_if(m_los, [&](const auto& kv) { return !tags.contains(kv.first); });
        std::set<int> floorTags;
        for (const auto& f : requests->floors)
        {
            floorTags.insert(f.tag);
            if (!m_floors.contains(f.tag) && budget > 0) { --budget; m_floors[f.tag] = g::FloorHeight(trace, f.x, f.y, f.top, f.bottom); }
        }
        std::erase_if(m_floors, [&](const auto& kv) { return !floorTags.contains(kv.first); });
        if (!m_logged)
        {
            m_logged = true;
            m_log("grenades: first answers (" + std::to_string(m_paths.size()) + " path(s), " + std::to_string(m_los.size()) + " line(s) of sight, " + std::to_string(m_floors.size()) +
                  " floor(s), " + std::to_string(m_traces) + " traces; " + std::to_string(m_pawns.size()) + " pawn(s) left out)");
        }
        m_files->Write(g::FormatPaths(UnixMs(), m_paths, m_los, m_floors));
    }
} // namespace aimmod

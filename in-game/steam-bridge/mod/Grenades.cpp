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
        for (const Param& p : m_trace.params())
            if (p.out && p.structType && p.name == "OutHit")
                for (FProperty* member : p.structType->ForEachProperty())
                {
                    if (member->GetName() == STR("ImpactPoint")) m_impactPoint = member->GetOffset_Internal();
                    if (member->GetName() == STR("ImpactNormal")) m_impactNormal = member->GetOffset_Internal();
                    if (member->GetName() == STR("bStartPenetrating")) m_startPenetrating = CastField<FBoolProperty>(member);
                }
        m_ready = m_trace.ok() && m_kismet && m_impactPoint >= 0 && m_impactNormal >= 0;
        m_log(m_ready ? std::string("grenades: line traces ready (") + (m_byObjects ? "world objects" : "visibility channel") + "); throws fly on this map"
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

    std::optional<g::Hit> GrenadeSim::Trace(UObject* context, const g::Vec& a, const g::Vec& b)
    {
        TArray<AActor*> ignore;
        // Every object type but pawns (2): no player or avatar stops a grenade or a line of sight.
        TArray<std::uint8_t> objectTypes;
        for (std::uint8_t t = 0; t < 32; ++t)
            if (t != 2) objectTypes.Add(t);
        bool hit = false, inside = false;
        g::Hit result{};
        int vectors = 0;
        ++m_traces;
        m_trace.Call(
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
        if (!hit || inside) return std::nullopt;
        return result;
    }

    void GrenadeSim::Tick()
    {
        if (m_stateDir.empty()) return;
        const double now = bridge::Bridge::Now();
        if (now < m_nextRead) return;
        m_nextRead = now + 0.02;
        const std::filesystem::path file = std::filesystem::path(m_stateDir) / L"grenade-sim.tsv";
        std::uint64_t size = 0;
        const std::uint64_t stamp = FileStamp(file, size);
        if (stamp == 0 || stamp == m_stamp || size > 64 * 1024) return;
        m_stamp = stamp;
        std::ifstream in(file, std::ios::binary);
        const std::string text((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
        const auto requests = g::ParseSim(text);
        if (!requests || requests->sequence == m_sequence) return;
        m_sequence = requests->sequence;
        if (requests->throws.empty() && requests->los.empty())
        {
            m_paths.clear();
            m_los.clear();
            return;
        }
        UObject* context = Context();
        if (!Bind() || !context) return; // the service's level-floor fallback flies them
        const auto trace = [&](const g::Vec& a, const g::Vec& b) { return Trace(context, a, b); };
        // Fly each new throw once; keep answers only for what is still asked.
        std::set<std::int64_t> ids;
        for (const auto& t : requests->throws)
        {
            ids.insert(t.id);
            if (!m_paths.contains(t.id)) m_paths[t.id] = g::Simulate(t.origin, t.velocity, trace);
        }
        std::erase_if(m_paths, [&](const auto& kv) { return !ids.contains(kv.first); });
        std::set<int> tags;
        for (const auto& l : requests->los)
        {
            tags.insert(l.tag);
            if (!m_los.contains(l.tag)) m_los[l.tag] = !Trace(context, l.from, l.to).has_value();
        }
        std::erase_if(m_los, [&](const auto& kv) { return !tags.contains(kv.first); });
        if (!m_logged)
        {
            m_logged = true;
            m_log("grenades: first answers (" + std::to_string(m_paths.size()) + " path(s), " + std::to_string(m_los.size()) + " line(s) of sight, " + std::to_string(m_traces) + " traces)");
        }
        const std::filesystem::path out = std::filesystem::path(m_stateDir) / L"grenade-paths.tsv";
        const std::wstring temp = out.wstring() + L".tmp";
        {
            std::ofstream o(temp, std::ios::binary | std::ios::trunc);
            if (!o) return;
            o << g::FormatPaths(UnixMs(), m_paths, m_los);
        }
        MoveFileExW(temp.c_str(), out.c_str(), MOVEFILE_REPLACE_EXISTING);
    }
} // namespace aimmod

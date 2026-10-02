#pragma once
// CS grenades on the host's game (in-game/docs/game-modes.md 6.6.5), game thread only. The native
// service asks (grenade-sim.tsv) for each throw's path and for line-of-sight checks from blasts;
// this flies every throw once with the map's own line traces (GrenadePhysics.hpp, the service's
// physics), answers line-of-sight checks and floor heights in grenade-paths.tsv. It reads and traces
// only: nothing is spawned or moved. No pawn stops a trace: KovaaK's characters carry hitbox
// components that block line traces, and the line from a flash to the thrower's own eye ends inside
// the thrower (build 50: your own flash never blinded you).
#include "Bridge.hpp"
#include "GameBindings.hpp"
#include "GrenadePhysics.hpp"

#include <Unreal/FWeakObjectPtr.hpp>

#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <vector>

namespace RC::Unreal
{
    class FBoolProperty;
}

namespace aimmod
{
    class GrenadeSim
    {
    public:
        GrenadeSim(bridge::LogFn log, std::wstring stateDir) : m_log(std::move(log)), m_stateDir(std::move(stateDir)) {}
        void Tick();

    private:
        bool Bind();
        RC::Unreal::UObject* Context();
        // Every pawn in the world (the local player's and every bot's or avatar's), found again each second.
        void FindPawns(double now);
        // One line trace by object type (the map's static and dynamic geometry, no pawns): the impact
        // point and the surface normal, or nullopt (no hit, or a trace that starts inside geometry).
        std::optional<bridge::grenades::Hit> Trace(RC::Unreal::UObject* context, const bridge::grenades::Vec& a, const bridge::grenades::Vec& b);

        bridge::LogFn m_log;
        std::wstring m_stateDir;
        bool m_bound = false, m_ready = false, m_byObjects = false, m_logged = false;
        game::Getter m_trace, m_getPawn;
        std::vector<RC::Unreal::FWeakObjectPtr> m_pawns;
        double m_nextPawns = 0;
        RC::Unreal::UObject* m_kismet = nullptr;
        std::int32_t m_impactPoint = -1, m_impactNormal = -1;
        RC::Unreal::FBoolProperty* m_startPenetrating = nullptr;
        RC::Unreal::FWeakObjectPtr m_context;
        double m_nextFind = 0, m_nextRead = 0;
        std::uint64_t m_stamp = 0;
        std::int64_t m_sequence = -1;
        // Answers kept for what the service still asks (by id and tag).
        std::map<std::int64_t, std::vector<bridge::grenades::Key>> m_paths;
        std::map<int, bool> m_los;
        std::map<int, std::optional<double>> m_floors;
        int m_traces = 0;
    };
} // namespace aimmod

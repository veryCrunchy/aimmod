#pragma once
// Host-authoritative match damage (game modes: deathmatch, vampiric 1v1,
// instagib): the local player's shots out (self-shots.tsv) and the host's
// verdict on this player in (play-state.tsv). See DESIGN.md "Match play".
#include <cstdint>
#include <optional>
#include <string>
#include <string_view>

namespace aimmod
{
    struct PlayState
    {
        std::uint64_t sequence{};
        std::string scenario;           // must be the current AimMod match scenario
        double health{}, maxHealth{};
        bool alive{true};
        std::int64_t respawnAtMs{};     // 0 = none
        bool spawnProtected{};
        struct Hit
        {
            std::uint64_t sequence{};
            std::string attacker;       // lobby member id
            double damage{};
            bool headshot{};
            double direction[3]{};      // from the attacker towards this player
        };
        std::optional<Hit> lastHit;
    };
    // Validated; nullopt for anything malformed (the whole file is ignored).
    std::optional<PlayState> ParsePlayState(std::string_view text);

    // round-state.tsv (service; game-modes.md 6.2.1): what play-state does not
    // carry. AIMMOD_ROUND_1\t<seq>, match, and optional spawn / phase / loadout.
    struct RoundState
    {
        std::uint64_t sequence{};
        std::string scenario;
        struct Spawn
        {
            std::string id;
            double x{}, y{}, z{}, yaw{};
        };
        std::optional<Spawn> spawn;
        struct Phase
        {
            std::string name; // freeze | live | planted | end | over
            bool frozen{}, buy{};
            std::int64_t endsMs{};
        };
        std::optional<Phase> phase;
        struct Loadout
        {
            std::string primary, pistol; // weapon profile names; "-" = empty slot
            double armour{};
            bool helmet{}, kit{};
            // CS slots 2 and 3 (game-modes.md 6.6.2): the knife, and the bomb for its carrier.
            // Empty when the line has no such columns (the slot is left as the scenario has it).
            std::string knife, bomb;
        };
        std::optional<Loadout> loadout;
        // CS: the bomb while it lies in the world (dropped, planted or defused), where AimModCore draws it.
        struct Bomb
        {
            std::string state; // dropped | planted | defused
            double x{}, y{}, z{};
            std::int64_t explodesMs{}; // local unix ms; 0 unless planted
            bool defusing{};
        };
        std::optional<Bomb> bomb;
    };
    std::optional<RoundState> ParseRoundState(std::string_view text);

    // First intersection of a ray (unit direction) with a vertical capsule
    // centred at `center`. Returns the distance along the ray, or nullopt.
    std::optional<double> RayCapsule(const double origin[3], const double direction[3], const double center[3], double radius, double halfHeight);
    // A hit in the top fifth of the capsule counts as a head hit (estimate).
    bool IsHeadHit(const double point[3], const double center[3], double halfHeight);

    struct ShotRecord
    {
        std::int64_t unixMs{};
        std::uint64_t sequence{};
        double origin[3]{}, direction[3]{};
        int slot{};
        std::uint32_t target{};         // self-pose target id, 0 = none
        bool headshot{};
        bool gameHit{};                 // the game's own hit counter advanced
    };
    // "shot\t<ms>\t<seq>\t<ox oy oz>\t<dx dy dz>\t<slot>\t<target>\t<headshot>\t<gameHit>\n"
    std::string FormatShot(const ShotRecord& shot);
} // namespace aimmod

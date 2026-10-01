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

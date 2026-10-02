#pragma once
// Host-authoritative match damage (game modes: deathmatch, vampiric 1v1,
// instagib): the local player's shots out (self-shots.tsv) and the host's
// verdict on this player in (play-state.tsv). See DESIGN.md "Match play".
#include <cstddef>
#include <cstdint>
#include <deque>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

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
            // CS slots 2, 4 and 3 (game-modes.md 6.6.2, 6.6.5): the knife, the bomb for its carrier,
            // and the grenade slot while grenades are carried. Empty when the line has no such
            // columns (the slot is left as the scenario has it).
            std::string knife, bomb, grenade;
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
        // CS weapon feel (CsFeel.hpp): the match's spread salt (the host rebuilds every bullet's ray
        // from it), the dynamic crosshair (on by default), the lobby's ADS zoom (off | cs | all) and
        // the zoomed sensitivity of the first scope level.
        struct Feel
        {
            std::uint64_t salt{};
            bool crosshair{true};
            std::string zoom{"cs"};
            double adsSensitivity{1};
        };
        std::optional<Feel> feel;
    };
    std::optional<RoundState> ParseRoundState(std::string_view text);

    // First intersection of a ray (unit direction) with a vertical capsule
    // centred at `center`. Returns the distance along the ray, or nullopt.
    std::optional<double> RayCapsule(const double origin[3], const double direction[3], const double center[3], double radius, double halfHeight);
    // The head zone: a point at least 60 % of the half height above the centre (the top fifth of
    // the capsule). The host (CombatMatch) uses the same rule, so a headshot here is one there.
    constexpr double HeadZoneFraction = 0.6;
    bool IsHeadHit(const double point[3], const double center[3], double halfHeight);

    // How a ray passes a vertical capsule: the gap between the ray and the capsule's surface
    // (0 when it meets it), the distance along the ray there and that point's height.
    struct CapsulePass
    {
        double gap{}, along{}, z{};
    };
    CapsulePass RayCapsulePass(const double origin[3], const double direction[3], const double center[3], double radius, double halfHeight);

    // A drawn target: self-pose target id and its capsule.
    struct Capsule
    {
        std::uint32_t id{};
        double center[3]{};
        double radius{}, halfHeight{};
    };
    struct TargetPick
    {
        std::size_t index{};
        double along{}, gap{};
        bool head{};
    };
    // The capsule the ray meets first; with none, the one it passes nearest within `tolerance` cm
    // (the visible mesh and the game's own hitboxes stick out of the capsule: the head above it,
    // arms beside it). nullopt: nothing within reach.
    std::optional<TargetPick> PickTarget(const double origin[3], const double direction[3], const std::vector<Capsule>& capsules, double tolerance);
    // Reach for a shot the game counted as a hit whose ray misses every capsule.
    constexpr double GameHitToleranceCm = 15;

    struct ShotRecord
    {
        std::int64_t unixMs{};
        std::uint64_t sequence{};
        double origin[3]{}, direction[3]{};
        int slot{};
        std::uint32_t target{};         // self-pose target id, 0 = none
        bool headshot{};
        bool gameHit{};                 // the game's own hit counter advanced
        // The drawn capsule of `target` at the shot (what the shooter saw), when target != 0.
        double targetCenter[3]{};
        double targetRadius{}, targetHalfHeight{};
        double gameDamage{-1};          // damage per hit the game counted in that frame, -1 unknown
        // How the target was found: 0 none, 1 the ray meets its capsule, 2 the ray passes within
        // GameHitToleranceCm of it (game hits only), 3 the game named it (Send_ShotHit).
        int source{};
        // CS weapon feel: the dynamic inaccuracy the bullet was fired with (rad, 0: none), the shot
        // number whose seed drew its offset, and whether the game's own trace followed that offset
        // (KovaaK's per-bullet spread); 0: only this ray did (the game's trace stayed on the crosshair).
        double inaccuracy{};
        std::uint64_t spreadShot{};
        bool spreadApplied{};
    };
    enum ShotSource : int { SourceNone = 0, SourceRay = 1, SourceNear = 2, SourceGame = 3 };
    // "shot\t<ms>\t<seq>\t<ox oy oz>\t<dx dy dz>\t<slot>\t<target>\t<headshot>\t<gameHit>
    //  \t<cx cy cz>\t<radius>\t<half height>\t<game damage>\t<source>\t<inaccuracy mrad>\t<spread shot>\t<applied>\n"
    std::string FormatShot(const ShotRecord& shot);

    // The shots self-shots.tsv carries. Kept until the service acknowledges them (it writes the
    // last shot seq it took into self-shots.request), so a slow or stalled reader never loses one;
    // bounded by MaxKept and MaxAgeMs (what falls out unacknowledged is counted as lost). Before
    // the first acknowledgement (or from a service that never sends one) the window is the last
    // LegacyKept shots, at most LegacyAgeMs old.
    class ShotLog
    {
    public:
        static constexpr std::size_t MaxKept = 256, LegacyKept = 64;
        static constexpr std::int64_t MaxAgeMs = 15000, LegacyAgeMs = 5000;
        void Add(const ShotRecord& shot) { m_shots.push_back(shot); }
        // The service took every shot up to `sequence`.
        void Ack(std::uint64_t sequence);
        void Prune(std::int64_t nowMs);
        void Clear();
        const std::deque<ShotRecord>& shots() const { return m_shots; }
        std::uint64_t acked() const { return m_acked; }
        std::uint64_t lost() const { return m_lost; }
        bool ackSeen() const { return m_ackSeen; }

    private:
        std::deque<ShotRecord> m_shots;
        std::uint64_t m_acked{}, m_lost{};
        bool m_ackSeen{};
    };
    // self-shots.request: "<unix ms>[\t<acked shot seq>\t<session>]". nullopt: no acknowledgement.
    struct ShotAck
    {
        std::uint64_t sequence{};
        std::int64_t session{};
    };
    std::optional<ShotAck> ParseShotRequest(std::string_view text);
} // namespace aimmod

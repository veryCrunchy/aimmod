#pragma once
// CS grenades in the world and in the hand (in-game/docs/game-modes.md 6.6.5), the engine-free half:
// the service's grenades.tsv, where a grenade in flight is (the host's path, the same formulas as the
// service's GrenadePhysics), and AimMod's own simple models: the six grenades (engine basic shapes,
// tinted), the smoke cloud's puffs, the fire's flames and the blasts. AimModCore's CsGrenades draws
// them; only in AimMod match scenarios with a CS round.
#include <aimmod/CsGear.hpp>

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace aimmod::cs
{
    // The service's GrenadePhysics constants that drawing a path needs.
    constexpr double GrenadeGravity = 800 * 0.4 * 4.4, GrenadeSlideDecel = 700;
    constexpr int GrenadeFlight = 0, GrenadeSlide = 1, GrenadeRest = 2;
    // The smoke's size (GrenadeRules): 6.2 m round, 3 m half height, on the ground; it spreads out over
    // 1 s and thins over the last 2 s.
    constexpr double SmokeRadius = 620, SmokeHalfHeight = 300, SmokeGrowSeconds = 1.0, SmokeFadeSeconds = 2.0;

    struct GrenadeKey
    {
        double t{}, x{}, y{}, z{}, vx{}, vy{}, vz{}; // t: ms after the throw
        int motion{};
    };

    // grenades.tsv (service, MultiplayerService.Grenades.cs):
    //   AIMMOD_GRENADES_1\t<seq>
    //   match\t<scenario>
    //   hand\t<kind|->\t<pin 0/1>\t<thrown at, local unix ms, 0>
    //   fly\t<id>\t<kind>\t<thrown ms>\t<goes off ms, 0>\t<keys>\t<t x y z vx vy vz motion> x keys
    //   smoke\t<id>\t<x>\t<y>\t<z>\t<start ms>\t<end ms>
    //   fire\t<id>\t<kind>\t<x>\t<y>\t<z>\t<radius>\t<start ms>\t<end ms>
    //   decoy\t<id>\t<x>\t<y>\t<z>\t<start ms>\t<end ms>
    //   blast\t<id>\t<kind>\t<x>\t<y>\t<z>\t<at ms>
    struct GrenadeState
    {
        std::uint64_t sequence{};
        std::string scenario;
        struct Hand
        {
            std::string kind; // empty: none in hand
            bool pin{};
            std::int64_t thrownMs{};
        } hand;
        struct Flying
        {
            std::int64_t id{};
            std::string kind;
            std::int64_t startMs{}, endMs{};
            std::vector<GrenadeKey> keys;
        };
        struct Area
        {
            std::int64_t id{};
            std::string kind; // smoke, decoy, or the fire's grenade (molotov, incendiary)
            double x{}, y{}, z{}, radius{};
            std::int64_t startMs{}, endMs{};
        };
        struct Blast
        {
            std::int64_t id{};
            std::string kind; // he, flash, molotov, incendiary, decoy, extinguished
            double x{}, y{}, z{};
            std::int64_t atMs{};
        };
        // A flash that blinded this player: white `peak` for `holdMs` from `atMs`, then clearing over `fadeMs`.
        struct Flash
        {
            std::int64_t id{}, atMs{};
            int holdMs{}, fadeMs{};
            double peak{};
        };
        std::vector<Flying> flying;
        std::vector<Area> smokes, fires, decoys;
        std::vector<Blast> blasts;
        std::optional<Flash> flash;
    };
    // Validated; nullopt for a bad header or row (the whole file is ignored). `why`: the first bad line.
    std::optional<GrenadeState> ParseGrenades(std::string_view text, std::string* why = nullptr);
    bool KnownGrenade(std::string_view kind);

    // Where a grenade is `ms` after its throw (the host's path).
    struct Point
    {
        double x{}, y{}, z{};
    };
    Point GrenadeAt(const std::vector<GrenadeKey>& keys, double ms);

    // AimMod's grenade models, about 7-12 cm (x up the grenade's length, resting upright).
    const std::vector<Part>& GrenadeModel(std::string_view kind);
    // The grenade in the hand: at rest, the pin pulled (raised back, ready), and gone for a moment after a throw.
    Hold GrenadeInHand(bool pin);
    constexpr double ThrownHideSeconds = 0.35;

    // The smoke cloud: many soft grey puffs (squashed spheres) that overlap to fill the cloud's size.
    // Each one comes out of the canister and spreads to its place over about a second (the inner ones
    // first), then drifts and billows slowly; at the end the outer ones thin out first.
    struct Puff
    {
        double offset[3]; // cm from the cloud's centre at full size
        double size;      // diameter at full size
        double shade;     // 0..1 lighter (higher up is lighter, as if lit from above)
        double delay;     // s after the pop before it starts spreading out
        double phase;     // radians: where its drift and billow start
        double inner;     // 0 at the cloud's edge .. 1 at its heart (the last to thin out)
    };
    const std::vector<Puff>& SmokePuffs();
    // One puff `age` s after the pop with `left` s to go: where it is (cm from the cloud's centre),
    // its size (cm, x y z) and whether it shows at all.
    struct PuffPose
    {
        double offset[3]{}, size[3]{};
        bool shown{};
    };
    PuffPose SmokePuff(const Puff& puff, double age, double left);
    double SmokeScale(std::int64_t startMs, std::int64_t endMs, std::int64_t nowMs);
    // The cloud's centre over the spot it popped (it sits on the ground).
    Point SmokeCentre(double x, double y, double z);
    // How deep in a smoke a point is (0 outside .. 1 in the thick of it), as the service's
    // GrenadeRules.SmokeInside: the camera's grey inside view.
    double SmokeDepth(const std::vector<GrenadeState::Area>& smokes, std::int64_t nowMs, double x, double y, double z);

    // The flash on this player's screen: the white (0..1, the service's GrenadeRules.FlashAlpha curve)
    // and the frozen after-image under it (0..1: the frame from the moment of the flash, which lingers
    // a little after the white starts clearing). Only a strong flash leaves an after-image.
    double FlashWhite(const GrenadeState::Flash& flash, std::int64_t nowMs);
    double FlashAfterImage(const GrenadeState::Flash& flash, std::int64_t nowMs);
    constexpr double AfterImageMinPeak = 0.6;

    // Fire: low flames inside the radius that flicker with time (`seconds` since it caught) and die
    // down in the last second.
    struct Flame
    {
        double offset[3], size[3], colour[3];
    };
    std::vector<Flame> FireFlames(double radius, double seconds, double secondsLeft, std::int64_t seed);

    // A blast's sphere: its diameter (cm) `seconds` after it went off and its colour; 0 when it's over.
    double BlastSize(std::string_view kind, double seconds);
    void BlastColour(std::string_view kind, double colour[3]);
} // namespace aimmod::cs

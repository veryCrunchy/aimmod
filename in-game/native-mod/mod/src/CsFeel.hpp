#pragma once
// CS weapon feel in the hand (in-game/docs/game-modes.md 6.6.6; numbers and formulas in
// core/include/aimmod/CsFeel.hpp). Game thread; only while MatchPlay's CS round state is engaged
// in an AimMod match scenario (never in challenges, benchmarks or anything ranked).
//  - Spread: every frame the weapon in hand gets the next shot's deterministic offset (the match
//    salt and the next shot number) as KovaaK's own per-bullet spread (PerBulletSpread, one entry),
//    so the game's trace, tracer, decal and hit counter follow the bullet. The units of KovaaK's
//    entry and how the game composes it with the camera are measured once (GetPerBulletSpread,
//    GetHitscanDestination) and every arming is read back; when that can't be confirmed the entry
//    stays at zero and only AimModCore's own ray (with a world trace) carries the spread
//    (spreadApplied = false; the service claims those on that ray).
//  - Scope (snipers): right mouse cycles the CS levels with KovaaK's own zoom driven from here
//    (the handler's BlockADS keeps the game's own right mouse out of it); out after each shot,
//    back after the bolt while the button is held; the weapon model and KovaaK's crosshair are
//    hidden while scoped; the zoomed sensitivity follows the level.
//  - Speed: the movement component's max speeds scaled by the weapon's CS speed (knife fastest,
//    scoped AWP slowest); cs-movement.tsv tells AimModSteam the unscaled speed for its bots.
//  - Sights: the scope view and the dynamic crosshair are drawn by the notice layer (cshud.js) from
//    an AimModScope event (level, scope-in, blur, crosshair gap).
#include "GameBindings.hpp"

#include <Unreal/FWeakObjectPtr.hpp>

#include <aimmod/CsFeel.hpp>
#include <aimmod/CsGear.hpp>
#include <aimmod/MatchPlay.hpp>

#include <cstdint>
#include <filesystem>
#include <optional>
#include <string>
#include <vector>

namespace aimmod
{
    namespace game
    {
        struct Bindings;
        struct Scene;
    } // namespace game

    class CsFeel
    {
    public:
        CsFeel(game::Bindings& bindings, game::Scene& scene) : m_b(bindings), m_scene(scene) {}
        // Every frame while the CS round state is engaged: hand is the slot in hand (-1 unknown),
        // nextShot the number the next shot will get.
        void Tick(double now, game::UObject* player, game::UObject* character, game::UObject* handler, int hand, const cs::Loadout& loadout,
                  const std::optional<RoundState::Feel>& feel, std::uint64_t nextShot, const std::filesystem::path& root);
        // A shot counted in `slot`: its spread fields, and the bullet's direction (dir; the camera
        // ray when the shot has no spread). True when the shot has spread.
        bool Bullet(int slot, ShotRecord& r, double dir[3]) const;
        // After the shot was recorded: the penalty grows, a sniper leaves its scope.
        void Fired(double now, int slot);
        // AimModCore's own spread ray only (the game's trace didn't follow it): whether the map's
        // geometry stands between the eye and the point.
        bool WorldBetween(game::UObject* context, const double from[3], const double to[3]) const;
        // The notice layer's Gameface widget (null without one): the AimModScope event.
        void Sights(double now, game::UObject* widget);
        // Leaving the match scenario (or the round state): everything back as the game had it.
        void Release(game::UObject* player, game::UObject* character, const char* why);

    private:
        bool Bind();
        void Motion(game::UObject* character, cs::Motion& m) const;
        void Arm(double now, game::UObject* player, game::UObject* weapon, const cs::WeaponFeel& w, std::uint64_t nextShot, std::uint64_t salt);
        bool WritePbs(game::UObject* weapon, double distance, double angle);
        std::optional<std::pair<double, double>> ReadPbsRotator(game::UObject* weapon) const;
        bool Calibrate(game::UObject* weapon);
        void CheckComposition(double now, game::UObject* weapon, double pitch, double yaw, const cs::Offset& o, double addPitch, double addYaw);
        void Zoom(double now, game::UObject* player, game::UObject* handler, game::UObject* weapon, const cs::WeaponFeel& w, double adsSensitivity);
        void SetZoomFov(game::UObject* weapon, const cs::WeaponFeel& w, int level);
        void SetSensitivity(game::UObject* weapon, const cs::WeaponFeel& w, int level, double adsSensitivity);
        void HideCrosshair(game::UObject* player, bool hidden);
        void HideWeapon(game::UObject* character, game::UObject* handler, bool hidden);
        void Speed(double now, game::UObject* character, const cs::WeaponFeel* w, bool scoped, const std::filesystem::path& root);
        void RestoreSpeed(game::UObject* character);
        bool Key(game::UObject* player, const char* key, bool held) const;

        game::Bindings& m_b;
        game::Scene& m_scene;
        bool m_bound{}, m_engaged{};
        game::Getter m_keyJustPressed, m_keyDown, m_isFalling, m_isCrouching, m_zoomIn, m_zoomOut, m_fullZoomIn;
        game::Field m_movement;
        // KovaaK's per-bullet spread entry (Distance, Angle) and zoom fields in each profile copy.
        struct Copy
        {
            game::Path pbs, use, sens, tbs;
            game::Path distance, angle; // within a SingleBulletSpread element
        };
        std::vector<Copy> m_copies;
        bool m_pbsBound{}, m_pbsMissing{};

        // Calibration of KovaaK's entry: the rotator (pitch, yaw) of entries (1, 0) and (1, 90).
        enum class Calib { Unknown, Ready, Failed };
        Calib m_calib{Calib::Unknown};
        double m_basis[2][2]{}; // [pitch, yaw] of (1, 0) and (1, 90)
        // How the game turns the camera by the entry: added to the camera's rotator, or in the camera's frame.
        enum class Compose { Unknown, Add, Local };
        Compose m_compose{Compose::Unknown};
        double m_nextComposeCheck{};
        int m_verified{}, m_mismatches{};
        double m_nextVerify{}, m_nextMismatchLog{};

        // What the weapon in hand is armed with (the next shot's offset).
        struct Armed
        {
            bool on{};
            int slot{-1};
            std::uint64_t shot{}, salt{};
            double inaccuracy{}, spread{};
            bool applied{};
        };
        Armed m_armed;

        // The weapon in hand.
        RC::Unreal::FWeakObjectPtr m_weapon, m_character;
        const cs::WeaponFeel* m_feel{};
        int m_hand{-1};
        cs::Accuracy m_accuracy;
        cs::Scope m_scope;
        double m_last{-1};
        cs::Motion m_motion;
        bool m_crosshair{true};
        std::string m_zoomMode{"cs"};
        // Zoom: the handler whose BlockADS we set, the game's first-level FOV and sensitivity, what is applied.
        RC::Unreal::FWeakObjectPtr m_blockedHandler;
        bool m_blockBefore{}, m_blockGivenUp{};
        double m_fovBase{}, m_sensBase{-1}, m_nextZoomCall{}, m_zoomWanted{};
        int m_appliedLevel{}, m_zoomTries{};
        bool m_fullZoomDone{};
        // KovaaK's crosshair texture (AMetaGameplayHud.Crosshair) while hidden, and the hidden view model.
        RC::Unreal::FWeakObjectPtr m_crosshairTexture, m_player;
        game::UClass* m_hudClass{};
        game::Path m_hudTexture;
        bool m_crosshairHidden{}, m_weaponHidden{};
        // Speed: each max speed field as the game set it, and what was written over it.
        struct SpeedField
        {
            const wchar_t* name;
            double game{};
            float written{};
            bool have{};
        };
        std::vector<SpeedField> m_speeds;
        RC::Unreal::FWeakObjectPtr m_speedBody;
        double m_speedShare{1}, m_nextMovementFile{};
        std::string m_speedLogged;
        // The sights event: last sent and when.
        std::string m_sightsSent;
        double m_nextSights{};
        game::Getter m_createEvent, m_addString, m_trigger, m_ready;
        bool m_sightsBound{};
        bool m_loggedArm{}, m_loggedFallback{};
    };
} // namespace aimmod

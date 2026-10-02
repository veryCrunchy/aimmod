#pragma once
// Match damage for AimMod game modes (DESIGN.md "Match play"). Game thread.
//  - Shots out: polls the local weapons' shot/hit counters every frame and,
//    while self-shots.request is fresh, publishes each new shot with the
//    camera ray and the target its ray meets (self-pose target ids).
//  - Play state in: applies the host's verdict (health, death, respawn,
//    spawn protection, hit effect) to the local character through the
//    game's own functions, only in generated "AimMod Match - " scenarios in
//    freeplay. Missing bindings disable it (logged); leaving the gate
//    releases spawn protection.
#include "CsGear.hpp"
#include "GameBindings.hpp"

#include <Unreal/FWeakObjectPtr.hpp>

#include <aimmod/MatchPlay.hpp>

#include <cstdint>
#include <deque>
#include <functional>
#include <memory>
#include <optional>
#include <string>
#include <unordered_map>
#include <vector>

namespace aimmod
{
    class Output;
    namespace game
    {
        struct Bindings;
        struct Scene;
    } // namespace game

    class MatchPlay
    {
    public:
        using PoseId = std::function<std::uint32_t(game::UObject* actor)>;
        MatchPlay(game::Bindings& bindings, game::Scene& scene, Output& output) : m_b(bindings), m_scene(scene), m_output(output), m_gear(bindings, scene) {}
        // "match-play" capability: the character bindings resolved (known after
        // the first match character was seen; true until proven otherwise).
        bool available() const { return !m_disabled; }
        bool engaged() const { return m_engaged; }
        void Tick(double now, const std::string& scenario, bool inChallenge, bool loading, const PoseId& poseId,
                  const std::unordered_map<std::uint32_t, std::string>& poseNames);
        // WeaponParentActor:Send_ShotHit (when the game calls it through reflection): the actor the
        // game's own hit landed on, used as the shot's target. Game thread.
        void OnShotHit(game::UObject* shooter, game::UObject* target, double damage);

    private:
        void TickShots(double now, const PoseId& poseId, const std::unordered_map<std::uint32_t, std::string>& poseNames);
        // The visible characters other than the local one, with their capsules (what the shooter sees).
        void DrawnTargets(game::UObject* character, const PoseId& poseId, std::vector<Capsule>& capsules, std::vector<game::UObject*>& actors);
        // The camera ray and the nearest drawn target it meets (origin, direction, target, headshot, capsule).
        bool AimRay(game::UObject* player, game::UObject* character, const PoseId& poseId, ShotRecord& shot);
        void PublishShots(const std::unordered_map<std::uint32_t, std::string>& poseNames);
        void LogShotStats(const char* why);
        void TickPlayState(double now, const std::string& scenario, bool inChallenge, bool loading);
        bool BindCharacter(game::UObject* character);
        void Release(const char* why);
        std::optional<double> Health(game::UObject* character) const;
        void SetHealth(game::UObject* character, double health);
        // round-state.tsv: spawn teleports, freeze, loadout, CS round respawns.
        void TickRound(double now, const std::string& scenario, bool inChallenge, bool loading);
        bool BindRound();
        void ReleaseRound(const char* why);
        void ApplyLoadout(game::UObject* character, const RoundState::Loadout& loadout);
        // Frozen before go-live: no jump and no movement (MOVE_None) on top of ignored move input.
        void FreezeBody(game::UObject* character, bool frozen);
        // In AimMod matches the weapon is shown in first person even with KovaaK's Show Weapon off.
        void ShowWeapons(double now, game::UObject* character);
        double m_nextShowCheck{};
        bool m_weaponShownLogged{}, m_knifeQuietLogged{};
        // Restart lock: while a fresh round state names the scenario on screen (any match, not
        // only AimMod arenas), KovaaK's restart bind (ResetSession) is renamed in the input
        // settings and the pause menu's restart button is collapsed; both come back after.
        void TickRestartLock(double now, bool wanted);
        bool BindLock();
        int RenameActions(const std::string& from, const wchar_t* to, bool keepKeys);
        void ShowRestartButtons(bool hidden);

        game::Bindings& m_b;
        game::Scene& m_scene;
        Output& m_output;

        // Shots.
        struct WeaponState
        {
            game::UObject* weapon{};
            double shots{}, hits{}, damage{-1};
        };
        std::vector<WeaponState> m_weapons;
        game::UObject* m_shotsCharacter{};
        ShotLog m_shots;
        std::uint64_t m_shotSequence{}, m_shotsPublish{};
        std::int64_t m_session{};
        bool m_shotsWanted{};
        double m_lastShotsTick{-1};
        // Send_ShotHit targets not yet given to a shot (at most 0.25 s old).
        struct HookHit
        {
            RC::Unreal::FWeakObjectPtr target;
            double damage{-1}, at{};
        };
        std::vector<HookHit> m_hookHits;
        // Diagnostics, logged every 10 s while shots flow and when the stream stops.
        struct ShotStats
        {
            std::uint64_t shots{}, gameHits{}, onCapsule{}, nearCapsule{}, named{}, gameHitNoTarget{}, rayOnly{}, hookCalls{};
        };
        ShotStats m_shotStats, m_shotStatsLogged;
        double m_nextShotLog{}, m_nextNoTargetLog{};

        // Play state.
        game::UClass* m_class{};
        game::Getter m_handleDamage, m_setHealth, m_respawn, m_killed, m_overrideInvulnerable, m_resetInvulnerable, m_currentHealth, m_respawnTimer;
        bool m_disabled{}, m_engaged{}, m_dead{}, m_protected{}, m_protectionSet{};
        std::uint64_t m_version{~0ull}, m_stateSequence{}, m_hitSequence{};
        std::shared_ptr<const PlayState> m_state;
        double m_nextHealthCheck{};
        std::string m_closedReason;
        std::uint32_t m_applied{};

        // Round state.
        game::Getter m_teleport, m_controlRotation, m_ignoreMove, m_moveIgnored, m_setWeapon, m_loadWeapons, m_selectWeapon, m_selectedWeapon;
        game::Path m_selectable; // WeaponHandler.SelectableWeapon (TArray<bool>)
        bool m_roundBound{}, m_roundDisabled{}, m_roundEngaged{}, m_frozen{};
        std::uint64_t m_roundVersion{~0ull};
        std::shared_ptr<const RoundState> m_round;
        std::string m_spawnId, m_loadoutKey, m_roundClosed;
        game::UObject* m_loadoutHandler{};
        std::vector<std::pair<int, bool>> m_selectableBefore; // slot -> original SelectableWeapon
        bool m_loadoutChanged{};
        // CS: the slots as last applied (to draw what changed), and the weapons in the hand and the bomb in the world.
        std::optional<cs::Loadout> m_csLoadout;
        CsGear m_gear;
        struct PendingSpawn
        {
            RoundState::Spawn spawn;
            double notBefore{}, giveUp{};
        };
        std::optional<PendingSpawn> m_pendingSpawn;
        game::Field m_characterMovement;
        game::Getter m_setMovementMode;
        RC::Unreal::FWeakObjectPtr m_frozenBody;
        std::optional<std::int32_t> m_jumpBefore;
        bool m_movementOff{}, m_freezeBound{};

        // Restart lock.
        game::UObject* m_inputSettings{};
        game::Path m_actionMappings, m_actionName, m_actionKey;
        game::Getter m_rebuildKeymaps, m_saveKeyMappings, m_keyJustPressed, m_setVisibility, m_getVisibility;
        game::Field m_resetButton;
        bool m_lockBound{}, m_lockDisabled{}, m_locked{};
        std::vector<std::vector<std::uint8_t>> m_lockedKeys; // FKey bytes of the renamed binds
        std::vector<std::pair<RC::Unreal::FWeakObjectPtr, std::uint8_t>> m_hiddenButtons; // button, visibility before
        double m_nextButtonScan{};
        std::uint64_t m_blockedPresses{};
    };
} // namespace aimmod

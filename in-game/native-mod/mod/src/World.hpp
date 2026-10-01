#pragma once
// Resolved game bindings shared by the observer and the replay sampler.
// Game thread only.
#include "GameBindings.hpp"

#include <Unreal/FWeakObjectPtr.hpp>

#include <memory>
#include <optional>
#include <string>
#include <vector>

namespace aimmod::game
{
    // Process-wide reflected functions/properties (UFunctions of native
    // classes live for the whole process).
    struct Bindings
    {
        // ScenarioManager
        Getter isInChallenge, isScenarioLoading, queueRemaining, timeElapsed, timeRemaining, currentScenario;
        // Scenario
        Getter scenarioActive, scenarioInChallenge, scenarioName;
        // StatsManager
        Getter lastScore, lastTimeRemaining;
        // PerformanceIndicatorsStateReceiver (ValueElse)
        Getter indicatorScore, indicatorShots, indicatorHits, indicatorKills, indicatorDamage;
        // GameplayStatics (static; called on the default object)
        Getter timeSeconds, gamePaused;
        UObject* statics{};
        // Camera and actors
        Getter cameraLocation, cameraRotation, cameraFov;
        Getter actorLocation, actorRotation;
        Getter capsuleRadius, capsuleHalfHeight;
        Field cameraManager; // PlayerController.PlayerCameraManager
        Field myCharacter;   // MetaPlayerController.MyCharacter
        Field hidden;        // Actor.bHidden
        Field capsule;       // Character.CapsuleComponent
        // MetaGameState
        Getter characters, mapName, mapScale;

        bool lifecycleReady() const
        {
            return isInChallenge.ok() && timeElapsed.ok() && currentScenario.ok() && scenarioName.ok();
        }
        bool replayReady() const
        {
            return lifecycleReady() && timeSeconds.ok() && statics && cameraLocation.ok() && cameraRotation.ok() && cameraFov.ok() &&
                   cameraManager.ok() && myCharacter.ok();
        }
    };

    // Per-class reflected members resolved on first use (bot and player
    // characters, weapons and handlers can be different Blueprint classes).
    struct ClassInfo
    {
        UClass* cls{};
        // Characters
        Getter healthPercent, profileName, killCount, lastTimeToKill;
        Field damageDone, weaponHandler, playbackComponent;
        // Weapon handler
        Getter weapons;
        // Weapons
        Field shotsFired, shotsHit;
    };
    const ClassInfo& Describe(UObject* object);
    void ClearClassCache();

    // Weakly held objects of the current world / game instance.
    struct Scene
    {
        RC::Unreal::FWeakObjectPtr manager, stats, indicators, player, gameState;
        UObject* instance{}; // identity only (outer of the manager)

        UObject* Manager() const { return manager.Get(); }
        UObject* Stats() const { return stats.Get(); }
        UObject* Indicators() const { return indicators.Get(); }
        UObject* Player() const { return player.Get(); }
        UObject* GameState() const { return gameState.Get(); }
    };

    // Local character counters (fallback when the indicator receiver has no value).
    struct LocalCounters
    {
        std::optional<double> shots, hits, kills, damage, lastTimeToKill;
    };
    LocalCounters ReadLocalCounters(UObject* character);

    // Per-weapon session counters (slot = index in GetWeapons, duplicates
    // skipped). False when any counter is unavailable.
    struct WeaponCount
    {
        UObject* weapon{};
        int slot{};
        double shots{}, hits{};
    };
    bool ReadWeaponCounters(UObject* character, std::vector<WeaponCount>& out);
} // namespace aimmod::game

namespace aimmod
{
    using game::UClass;
    using game::UFunction;
    using game::UObject;
} // namespace aimmod

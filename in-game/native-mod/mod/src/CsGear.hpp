#pragma once
// CS weapons in the hand and the bomb in the world (in-game/docs/game-modes.md 6.6.2). Game
// thread; only while MatchPlay's round state is engaged in an AimMod match scenario with a CS
// loadout (never in challenges, benchmarks or anything ranked).
//  - Switching: KovaaK's own Weapon1..Weapon4 keys switch the slots natively (the loadout fills
//    them); AimModCore adds the mouse wheel (next / previous slot with a weapon) and Q (the last
//    weapon) by pressing the same Weapon<N> action on the player controller, and draws a newly
//    bought weapon (cs::AfterLoadout).
//  - In the hand: the knife and the bomb use KovaaK's "Blank" viewmodel; AimMod's own simple
//    knife and bomb models (cs::KnifeModel, cs::BombModel: engine basic shapes, tinted) sit in
//    front of the first-person camera while that slot is held.
//  - In the world: the dropped or planted bomb is an AimMod StaticMeshActor with the bomb model
//    where the host says it lies; when planted its light flashes with the beep (cs::LightOn).
#include "GameBindings.hpp"

#include <Unreal/FWeakObjectPtr.hpp>

#include <aimmod/CsGear.hpp>
#include <aimmod/MatchPlay.hpp>

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

    class CsGear
    {
    public:
        CsGear(game::Bindings& bindings, game::Scene& scene) : m_b(bindings), m_scene(scene) {}
        // After MatchPlay applied a loadout (changed or new handler): draws what CS would.
        void LoadoutApplied(const cs::Loadout* before, const cs::Loadout& after);
        // Every frame while the CS round state is engaged.
        void Tick(double now, game::UObject* player, game::UObject* character, game::UObject* handler, const RoundState& round);
        // Leaving the match scenario (or the round state): models hidden, the world bomb removed.
        void Release(game::UObject* player, const char* why);

    private:
        bool Bind(game::UObject* player);
        int InHand(game::UObject* handler) const;
        void Press(game::UObject* player, int slot);
        bool KeyPressed(game::UObject* player, const char* key) const;
        game::UObject* BuildModel(game::UObject* owner, game::UObject* parent, const std::vector<cs::Part>& parts, std::vector<RC::Unreal::FWeakObjectPtr>* lights);
        void HandModels(game::UObject* character, int slot);
        void WorldBomb(double now, game::UObject* player, game::UObject* character, const std::optional<RoundState::Bomb>& bomb);

        game::Bindings& m_b;
        game::Scene& m_scene;

        bool m_bound{}, m_disabled{};
        game::Getter m_currentNum, m_keyJustPressed;
        game::Getter m_pressed[cs::Slots], m_released[cs::Slots];
        game::UClass* m_controllerClass{};

        cs::Loadout m_loadout;
        bool m_haveLoadout{};
        cs::Switcher m_switcher;
        int m_wanted{-1};       // a slot to draw once the game can switch
        double m_wantedUntil{}, m_nextPress{};
        int m_releaseSlot{-1};  // a Weapon<N> action to release next frame

        // First-person models on the camera.
        RC::Unreal::FWeakObjectPtr m_handOwner, m_knife, m_handBomb;
        bool m_handBuilt{}, m_handFailed{};
        int m_shownSlot{-2};

        // The bomb in the world.
        RC::Unreal::FWeakObjectPtr m_bombActor;
        std::vector<RC::Unreal::FWeakObjectPtr> m_bombLights;
        std::string m_bombKey;
        bool m_lightShown{};
        double m_nextBombTry{};
    };
} // namespace aimmod

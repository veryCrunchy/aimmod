#pragma once
// CS grenades drawn by AimModCore (in-game/docs/game-modes.md 6.6.5). Game thread; only while
// MatchPlay's CS round state is engaged in an AimMod match scenario. From the service's
// grenades.tsv (cs::ParseGrenades):
//  - in the hand: AimMod's model of the grenade key 4 has selected (cs::GrenadeModel), on the
//    first-person camera while the grenade slot is held; drawn back with the pin pulled, gone for a
//    moment after a throw;
//  - in flight: the same model on an AimMod actor following the host's path (cs::GrenadeAt), spinning;
//  - smokes: a cloud of 40 soft grey puffs (squashed spheres) that come out of the canister, spread
//    over about a second, drift and billow slowly and thin out from the edge at the end
//    (cs::SmokePuff, posed at 30 Hz); with the camera inside one, a grey inside view (a post-process
//    component that flattens the picture to grey, by cs::SmokeDepth);
//    fires: a burning pool with flames that flicker (cs::FireFlames); decoys: the decoy lying where
//    it stopped; blasts: an HE fireball, a flash's white pop, a fire grenade bursting or a fire put out;
//  - a flash that hit this player: a white over the whole screen, above every other layer, and under
//    it the frozen after-image (one scene capture of the view at the flash, half resolution), both by
//    cs::FlashWhite and cs::FlashAfterImage every frame.
// Everything is engine basic shapes, tinted; AimMod's own actors, no collision, removed with the round.
#include "GameBindings.hpp"

#include <Unreal/FWeakObjectPtr.hpp>

#include <aimmod/CsGrenades.hpp>

#include <cstdint>
#include <filesystem>
#include <map>
#include <optional>
#include <string>
#include <vector>

namespace aimmod
{
    class CsGrenades
    {
    public:
        void Tick(game::UObject* character, int hand, const std::filesystem::path& root, const std::string& scenario);
        void Release(const char* why);

    private:
        void Read(const std::filesystem::path& root);
        void Hand(game::UObject* character, int hand, std::int64_t nowMs);
        void World(game::UObject* character, std::int64_t nowMs);
        struct Shown
        {
            RC::Unreal::FWeakObjectPtr actor, model;
            std::vector<RC::Unreal::FWeakObjectPtr> parts; // smokes: one per puff
            std::vector<char> partShown;
            std::int64_t posedAt{};
            double scale{-1};
            bool seen{};
        };
        Shown* Ensure(const std::string& key, game::UObject* character, double x, double y, double z, const std::vector<cs::Part>& parts, bool keepParts = false);
        void Move(game::UObject* actor, double x, double y, double z, double pitch, double yaw);
        void PoseSmoke(Shown& s, double age, double left, std::int64_t nowMs);

        // The camera: where the local player looks from (PlayerCameraManager), for the inside view and the after-image.
        struct View
        {
            double location[3]{}, rotation[3]{};
            double fov{90};
        };
        std::optional<View> CameraView(game::UObject* character) const;
        // An AimMod actor near the player carrying the smoke's inside view and the after-image capture.
        game::UObject* Effects(game::UObject* character);
        void InsideView(game::UObject* character, std::int64_t nowMs);
        void Flash(game::UObject* character, std::int64_t nowMs);
        bool FlashLayer(game::UObject* character);
        void Capture(game::UObject* character);
        void ShowFlash(double white, double after);
        void DropEffects();

        std::optional<cs::GrenadeState> m_state;
        std::uint64_t m_stamp{};
        std::int64_t m_nextRead{}, m_nextBuildTry{};
        std::map<std::string, Shown> m_shown;
        // First-person models, one per grenade kind, built on the camera of this body.
        RC::Unreal::FWeakObjectPtr m_handOwner;
        std::map<std::string, RC::Unreal::FWeakObjectPtr> m_handModels;
        std::string m_handShown, m_handPose;
        bool m_handBuilt{}, m_logged{};

        // Effects: the holder actor, its post-process (inside a smoke) and scene capture (after-image).
        RC::Unreal::FWeakObjectPtr m_effects, m_post, m_capture, m_target;
        double m_insideWeight{-1};
        bool m_effectsFailed{}, m_insideLogged{};
        // The flash layer: a host widget in the viewport above everything, the after-image and the white.
        RC::Unreal::FWeakObjectPtr m_flashHost, m_afterImage, m_white;
        bool m_flashFailed{}, m_flashShown{}, m_captured{};
        std::int64_t m_flashId{-1};
        double m_lastWhite{-1}, m_lastAfter{-1};
        int m_flashes{};
    };
} // namespace aimmod

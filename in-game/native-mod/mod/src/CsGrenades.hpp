#pragma once
// CS grenades drawn by AimModCore (in-game/docs/game-modes.md 6.6.5). Game thread; only while
// MatchPlay's CS round state is engaged in an AimMod match scenario. From the service's
// grenades.tsv (cs::ParseGrenades):
//  - in the hand: AimMod's model of the grenade key 4 has selected (cs::GrenadeModel), on the
//    first-person camera while the grenade slot is held; drawn back with the pin pulled, gone for a
//    moment after a throw;
//  - in flight: the same model on an AimMod actor following the host's path (cs::GrenadeAt), spinning;
//  - smokes: a cloud of grey puffs that grows in and thins out (cs::SmokePuffs); fires: a burning
//    pool with flames that flicker (cs::FireFlames); decoys: the decoy lying where it stopped;
//    blasts: an HE fireball, a flash's white pop, a fire grenade bursting or a fire put out.
// Everything is engine basic shapes, tinted; AimMod's own actors, no collision, removed with the round.
#include "GameBindings.hpp"

#include <Unreal/FWeakObjectPtr.hpp>

#include <aimmod/CsGrenades.hpp>

#include <cstdint>
#include <filesystem>
#include <map>
#include <optional>
#include <string>

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
            double scale{-1};
            bool seen{};
        };
        Shown* Ensure(const std::string& key, game::UObject* character, double x, double y, double z, const std::vector<cs::Part>& parts);
        void Move(game::UObject* actor, double x, double y, double z, double pitch, double yaw);

        std::optional<cs::GrenadeState> m_state;
        std::uint64_t m_stamp{};
        std::int64_t m_nextRead{}, m_nextBuildTry{};
        std::map<std::string, Shown> m_shown;
        // First-person models, one per grenade kind, built on the camera of this body.
        RC::Unreal::FWeakObjectPtr m_handOwner;
        std::map<std::string, RC::Unreal::FWeakObjectPtr> m_handModels;
        std::string m_handShown, m_handPose;
        bool m_handBuilt{}, m_logged{};
    };
} // namespace aimmod

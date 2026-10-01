// CS weapon slots, switching and the bomb's light and models (core/include/aimmod/CsGear.hpp).
#include <aimmod/CsGear.hpp>

namespace csgear_checks
{
    using namespace aimmod;
    using namespace aimmod::cs;

    inline Loadout Make(std::string primary, std::string pistol, std::string knife = "AimMod CS Knife", std::string bomb = "-")
    {
        Loadout l;
        l.names = {std::move(primary), std::move(pistol), std::move(knife), std::move(bomb)};
        return l;
    }

    inline void RoundLines()
    {
        auto r = ParseRoundState("AIMMOD_ROUND_1\t4\nmatch\tAimMod Match - CS - 0a1b2c3d\nloadout\tAimMod CS AK-47\tAimMod CS Glock-18\t100\t1\t0\tAimMod CS Knife\tAimMod CS C4\n"
                                 "bomb\tplanted\t-1200.5\t300\t64\t1790000040000\t1\n");
        CHECK(r && r->loadout && r->loadout->knife == "AimMod CS Knife" && r->loadout->bomb == "AimMod CS C4" && r->bomb && r->bomb->state == "planted" &&
                  r->bomb->x == -1200.5 && r->bomb->explodesMs == 1790000040000 && r->bomb->defusing,
              "round state: the knife and bomb slots, and where the bomb lies");
        auto old = ParseRoundState("AIMMOD_ROUND_1\t4\nmatch\tA\nloadout\t-\tAimMod CS USP-S\t0\t0\t0\n");
        CHECK(old && old->loadout && old->loadout->knife.empty() && old->loadout->bomb.empty() && !old->bomb, "the six-column loadout still reads (knife and bomb left alone)");
        CHECK(!ParseRoundState("AIMMOD_ROUND_1\t1\nmatch\tA\nloadout\t-\t-\t0\t0\t0\tKnife\n") && !ParseRoundState("AIMMOD_ROUND_1\t1\nmatch\tA\nbomb\tcarried\t0\t0\t0\t0\t0\n") &&
                  !ParseRoundState("AIMMOD_ROUND_1\t1\nmatch\tA\nbomb\tplanted\t0\t0\t0\t-5\t0\n") && !ParseRoundState("AIMMOD_ROUND_1\t1\nmatch\tA\nbomb\tdropped\t1e9\t0\t0\t0\t0\n"),
              "malformed knife, bomb columns and bomb lines are refused");
        const Loadout l = FromRound(*r->loadout);
        CHECK(l.Has(PrimarySlot) && l.Has(PistolSlot) && l.Has(KnifeSlot) && l.Has(BombSlot) && !FromRound(*old->loadout).Has(KnifeSlot) && !FromRound(*old->loadout).Has(PrimarySlot),
              "round loadout to slots");
    }

    inline void Switching()
    {
        const Loadout full = Make("AimMod CS AK-47", "AimMod CS Glock-18", "AimMod CS Knife", "AimMod CS C4");
        const Loadout pistol = Make("-", "AimMod CS USP-S");
        CHECK(Best(full) == PrimarySlot && Best(pistol) == PistolSlot && Best(Make("-", "-")) == KnifeSlot && Best(Make("-", "-", "-")) == -1, "CS draws the primary, then the pistol, then the knife");
        CHECK(Cycle(full, PrimarySlot, 1) == PistolSlot && Cycle(full, BombSlot, 1) == PrimarySlot && Cycle(full, PrimarySlot, -1) == BombSlot && Cycle(pistol, PistolSlot, 1) == KnifeSlot &&
                  Cycle(pistol, KnifeSlot, 1) == PistolSlot && Cycle(pistol, PistolSlot, -1) == KnifeSlot && Cycle(Make("-", "-"), KnifeSlot, 1) == KnifeSlot && Cycle(full, -1, 1) == PrimarySlot,
              "the wheel steps through the slots with weapons and wraps round");
        Switcher s;
        CHECK(s.Previous(full) == PrimarySlot, "Q before anything was held: the best weapon");
        s.Observe(PrimarySlot);
        s.Observe(PrimarySlot);
        s.Observe(KnifeSlot);
        CHECK(s.current() == KnifeSlot && s.last() == PrimarySlot && s.Previous(full) == PrimarySlot, "Q goes back to the weapon before");
        s.Observe(PrimarySlot);
        CHECK(s.Previous(full) == KnifeSlot, "Q again flips back");
        s.Observe(BombSlot);
        CHECK(s.Previous(Make("AimMod CS AK-47", "AimMod CS Glock-18")) == PrimarySlot, "Q with the bomb gone: the weapon before it");
        Switcher t;
        t.Observe(PistolSlot);
        t.Observe(KnifeSlot);
        CHECK(t.Previous(Make("-", "-")) == -1, "Q with only the knife does nothing");
    }

    inline void Loadouts()
    {
        const Loadout start = Make("-", "AimMod CS Glock-18");
        CHECK(AfterLoadout(nullptr, start, -1) == PistolSlot && AfterLoadout(nullptr, Make("AimMod CS AK-47", "AimMod CS Glock-18"), PistolSlot) == PrimarySlot, "the round's first loadout draws the best weapon");
        const Loadout bought = Make("AimMod CS AK-47", "AimMod CS Glock-18");
        CHECK(AfterLoadout(&start, bought, PistolSlot) == PrimarySlot && AfterLoadout(&bought, Make("AimMod CS AWP", "AimMod CS Glock-18"), PistolSlot) == PrimarySlot,
              "a bought primary is drawn, also one that replaces the old primary");
        CHECK(AfterLoadout(&bought, Make("AimMod CS AK-47", "AimMod CS Desert Eagle"), PrimarySlot) == PistolSlot, "a bought pistol is drawn");
        CHECK(AfterLoadout(&bought, Make("AimMod CS AK-47", "AimMod CS Glock-18", "AimMod CS Knife", "AimMod CS C4"), PrimarySlot) == -1, "picking up the bomb keeps the gun in hand");
        const Loadout carrying = Make("AimMod CS AK-47", "AimMod CS Glock-18", "AimMod CS Knife", "AimMod CS C4");
        CHECK(AfterLoadout(&carrying, bought, BombSlot) == PrimarySlot && AfterLoadout(&carrying, bought, KnifeSlot) == -1, "dropping or planting the bomb from the hand draws the best weapon");
        CHECK(AfterLoadout(&bought, Make("AimMod CS AK-47", "AimMod CS Glock-18"), PrimarySlot) == -1, "an unchanged loadout keeps the weapon");
    }

    inline void Bomb()
    {
        CHECK(BeepInterval(40) == 1.0 && BeepInterval(2) == 0.13 && BeepInterval(10) > 0.25 && BeepInterval(10) < 0.35, "the light follows the service's beep schedule");
        CHECK(LightOn(40) && LightOn(39.95) && !LightOn(39.5) && LightOn(39.0) && LightOn(0.5) && !LightOn(45) && !LightOn(-1), "the light flashes at each beep and stays on in the last second");
        int flashes = 0;
        bool was = false;
        for (double r = 10.0; r > 5.0; r -= 0.01)
        {
            const bool on = LightOn(r);
            if (on && !was) ++flashes;
            was = on;
        }
        CHECK(flashes >= 15 && flashes <= 30, "about 3-6 flashes a second between 10 and 5 s left");
        for (const auto* model : {&BombModel(), &KnifeModel()})
            for (const Part& p : *model)
            {
                const std::wstring mesh = p.mesh;
                CHECK(mesh.rfind(L"/Engine/BasicShapes/", 0) == 0 && p.size[0] > 0 && p.size[1] > 0 && p.size[2] > 0 && p.size[0] <= 40 && p.colour[0] <= 1 && p.colour[1] <= 1 && p.colour[2] <= 1,
                      "models are small engine basic shapes, tinted");
            }
        CHECK(std::count_if(BombModel().begin(), BombModel().end(), [](const Part& p) { return p.light; }) == 1 &&
                  std::none_of(KnifeModel().begin(), KnifeModel().end(), [](const Part& p) { return p.light; }),
              "the bomb has one light; the knife none");
        CHECK(InHand(BombSlot).offset[0] > 0 && InHand(KnifeSlot).offset[1] > 0, "both are held in front, the knife to the right");
    }

    inline void Knife()
    {
        CHECK(NextSlash(KnifeMove::None) == KnifeMove::SlashRight && NextSlash(KnifeMove::SlashRight) == KnifeMove::SlashLeft && NextSlash(KnifeMove::SlashLeft) == KnifeMove::SlashRight &&
                  NextSlash(KnifeMove::Stab) == KnifeMove::SlashRight,
              "slashes alternate right and left");
        CHECK(KnifeMoveSeconds(KnifeMove::SlashRight) == 0.25 && KnifeMoveSeconds(KnifeMove::Stab) > KnifeMoveSeconds(KnifeMove::SlashLeft) && StabInterval == 1.0, "a slash takes 0.25 s, the heavier stab longer");
        const Hold rest = KnifePose(KnifeMove::SlashRight, 0), done = KnifePose(KnifeMove::SlashRight, 0.25), none = KnifePose(KnifeMove::None, 0.1);
        CHECK(rest.offset[0] == 0 && done.rotation[1] == 0 && none.offset[0] == 0, "the knife is at rest before and after a move");
        const Hold right = KnifePose(KnifeMove::SlashRight, 0.25 * 0.35), left = KnifePose(KnifeMove::SlashLeft, 0.25 * 0.35);
        CHECK(std::fabs(right.rotation[1] + 55) < 1e-6 && std::fabs(left.rotation[1] - 55) < 1e-6 && right.offset[1] < 0 && left.offset[1] > 0, "the slash swings fully at its peak, mirrored for the other side");
        const Hold late = KnifePose(KnifeMove::SlashRight, 0.24);
        CHECK(std::fabs(late.rotation[1]) < 2, "and is nearly back at the end");
        const Hold thrust = KnifePose(KnifeMove::Stab, 0.38 * 0.3);
        CHECK(std::fabs(thrust.offset[0] - 24) < 1e-6 && thrust.rotation[1] == 0, "the stab thrusts straight ahead");
    }

    inline void Run()
    {
        Knife();
        RoundLines();
        Switching();
        Loadouts();
        Bomb();
    }
} // namespace csgear_checks

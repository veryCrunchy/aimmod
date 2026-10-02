// CS weapon feel (core/include/aimmod/CsFeel.hpp): inaccuracy by movement, deterministic spread
// seeds (the host's CsFeel.cs must give the same numbers), the scope's zoom cycle and the speeds.
#include <aimmod/CsFeel.hpp>

namespace csfeel_checks
{
    using namespace aimmod;
    using namespace aimmod::cs;

    inline const WeaponFeel& W(const char* id) { return *FeelById(id); }

    inline void SpreadBySpeed()
    {
        CHECK(Feels().size() == 11 && FeelByProfile("AimMod CS AK-47") == FeelById("ak47") && FeelByProfile("AimMod CS Knife")->maxSpeed == KnifeSpeed && !FeelByProfile("AimMod CS Banana"),
              "every CS item has a feel, found by its KovaaK's profile");
        const auto& ak = W("ak47");
        const double still = ConeNow(ak, {0, false, false}, 0), crouched = ConeNow(ak, {0, false, true}, 0), walk = ConeNow(ak, {ak.maxSpeed * 0.52, false, false}, 0),
                     run = ConeNow(ak, {ak.maxSpeed, false, false}, 0), jump = ConeNow(ak, {0, true, false}, 0), slow = ConeNow(ak, {ak.maxSpeed * AccurateShare, false, false}, 0);
        CHECK(crouched < still && still < 0.01 && slow == still && still < walk && walk < run && run > 0.12 && jump > 0.3,
              "the AK: crouched beats standing, the accurate range adds nothing, walking costs some, running and jumping a lot");
        for (const char* id : {"glock", "usp", "deagle", "mac10", "mp9", "ak47", "m4a1s", "awp"})
        {
            const auto& w = W(id);
            CHECK(w.Spreads() && ConeNow(w, {w.maxSpeed, false, false}, 0) > ConeNow(w, {0, false, false}, 0) && ConeNow(w, {0, true, false}, 0) > ConeNow(w, {w.maxSpeed, false, false}, 0) * 0.9 &&
                      ConeNow(w, {0, false, true}, 0) < ConeNow(w, {0, false, false}, 0),
                  (std::string("moving, jumping and crouching change the cone: ") + id).c_str());
        }
        CHECK(ConeNow(W("glock"), {240, false, false}, 0) * 4 < run && ConeNow(W("usp"), {240, false, false}, 0) * 4 < run && ConeNow(W("mp9"), {240, false, false}, 0) * 3 < run &&
                  ConeNow(W("mac10"), {240, false, false}, 0) < ConeNow(W("m4a1s"), {225, false, false}, 0),
              "running: pistols and SMGs are forgiving, rifles are not");
        const auto& awp = W("awp");
        CHECK(ConeNow(awp, {0, false, false}, 0) > 0.05 && ConeNow(awp, {0, false, false}, 1) < 0.005 && ConeNow(awp, {awp.scopedSpeed, false, false}, 1) > 0.1 &&
                  ConeNow(awp, {0, false, false}, 0.5) > ConeNow(awp, {0, false, false}, 1) * 5,
              "the AWP: a no-scope is wide, scoped and still it is a laser, scoped and moving it is not; scoping in takes a moment");
        CHECK(!W("knife").Spreads() && ConeNow(W("knife"), {250, true, false}, 0) == 0, "the knife has no spread");
        // What the host accepts: never more than the client's honest cone in the same motion.
        for (const auto& w : Feels())
            for (double speed : {0.0, 60.0, 120.0, 200.0, 250.0})
                for (bool air : {false, true})
                    for (bool crouch : {false, true})
                        for (double blend : {0.0, 1.0})
                            CHECK(MinimumCone(w, speed, air) <= ConeNow(w, {speed, air, crouch}, blend) + 1e-12, (std::string("the host's least cone is never above the client's: ") + w.id).c_str());
        CHECK(MinimumCone(ak, 215, false) > 0.1 && MinimumCone(ak, 0, true) > 0.3, "the host's least cone for a running or jumping rifle is still wide");
    }

    inline void Recoil()
    {
        const auto& ak = W("ak47");
        Accuracy a;
        a.Tick(0.016, ak, {0, false, false}, 0);
        const double first = a.inaccuracy();
        for (int i = 0; i < 10; ++i)
        {
            a.Shot(ak);
            a.Tick(0.1, ak, {0, false, false}, 0);
        }
        const double sprayed = a.inaccuracy();
        for (int i = 0; i < 60; ++i) a.Tick(1.0 / 60, ak, {0, false, false}, 0);
        CHECK(std::fabs(first - ConeNow(ak, {0, false, false}, 0)) < 1e-12 && sprayed > first * 2 && a.inaccuracy() < first + 0.001,
              "first shot accuracy, the spray grows the cone, a second of rest recovers it");
        Accuracy g;
        const auto& glock = W("glock");
        g.Tick(0.016, glock, {0, false, false}, 0);
        g.Shot(glock);
        const double after = g.penalty();
        g.Tick(glock.recoverStand, glock, {0, false, false}, 0);
        CHECK(std::fabs(g.penalty() - after * RecoveredShare) < 1e-9, "the penalty is down to its recovered share after the recovery time");
        Accuracy l;
        l.Tick(0.016, ak, {0, true, false}, 0);
        l.Tick(0.016, ak, {0, false, false}, 0);
        CHECK(l.penalty() > ak.land * 0.9, "landing from a jump costs accuracy for a moment");
        Accuracy s;
        s.Tick(0.016, ak, {215, false, false}, 0);
        const double moving = s.settled();
        s.Tick(0.1, ak, {0, false, false}, 0);
        const double soon = s.settled();
        for (int i = 0; i < 36; ++i) s.Tick(1.0 / 60, ak, {0, false, false}, 0);
        CHECK(moving > 0.12 && soon > moving * 0.3 && s.settled() < ConeNow(ak, {0, false, false}, 0) + 0.01, "the settled cone takes a moment to calm after stopping");
    }

    inline void Seeds()
    {
        // The same numbers as the host's (MultiplayerChecks.CsFeel): salt, randoms, offsets.
        const std::uint64_t salt = Salt("cs-golden");
        CHECK(salt == 9077073264004251771ull && Salt("") == 14695981039346656037ull, "the match salt is FNV-1a 64 of the match id");
        double u[4];
        SpreadRandoms(salt, 7, u);
        CHECK(std::fabs(u[0] - 0.80751303501723604) < 1e-15 && std::fabs(u[1] - 0.18479337744944391) < 1e-15 && std::fabs(u[2] - 0.79089596736284984) < 1e-15 &&
                  std::fabs(u[3] - 0.86836906912763567) < 1e-15,
              "the randoms of shot 7 are the host's");
        const Offset a = SpreadOffset(salt, 1, 0.05, 0.0006), b = SpreadOffset(salt, 123456789, 0.05, 0.0006), again = SpreadOffset(salt, 1, 0.05, 0.0006);
        CHECK(std::fabs(a.right - -0.024038644358901809) < 1e-12 && std::fabs(a.up - -0.0084995707911488753) < 1e-12 && std::fabs(b.right - 0.0074422910951327586) < 1e-12 &&
                  std::fabs(b.up - 0.02792096125188772) < 1e-12 && a.right == again.right && a.up == again.up,
              "a shot's offset comes from its seed alone: the host rebuilds it exactly");
        const Offset other = SpreadOffset(Salt("another match"), 1, 0.05, 0.0006);
        CHECK(other.right != a.right && SpreadOffset(salt, 2, 0.05, 0.0006).right != a.right, "another match or another shot draws another offset");
        // The cone holds every offset, and most land well inside it (CS's centre-weighted spread).
        double worst = 0, sum = 0;
        for (std::uint64_t shot = 1; shot <= 4000; ++shot)
        {
            const Offset o = SpreadOffset(salt, shot, 0.02, 0.001);
            const double r = std::hypot(o.right, o.up);
            worst = std::max(worst, r);
            sum += r;
        }
        CHECK(worst <= 0.021 + 1e-12 && worst > 0.018 && sum / 4000 > 0.008 && sum / 4000 < 0.0125, "offsets stay inside the cone, weighted to its centre");
        CHECK(SpreadOffset(salt, 9, 0, 0).right == 0 && SpreadOffset(salt, 9, 0, 0).up == 0, "no inaccuracy, no offset");
    }

    inline void Rays()
    {
        double d[3];
        SpreadDirection(10, 30, {0, 0}, d);
        CHECK(std::fabs(d[0] - std::cos(10 * 3.14159265358979 / 180) * std::cos(30 * 3.14159265358979 / 180)) < 1e-9 && std::fabs(d[2] - std::sin(10 * 3.14159265358979 / 180)) < 1e-9,
              "no offset: the camera ray");
        SpreadDirection(0, 0, {0.1, 0}, d);
        CHECK(d[1] > 0 && std::fabs(std::atan2(d[1], d[0]) - std::atan(0.1)) < 1e-12 && std::fabs(d[2]) < 1e-12, "a right offset turns the ray right by atan(offset)");
        SpreadDirection(0, 0, {0, 0.1}, d);
        CHECK(d[2] > 0 && std::fabs(std::asin(d[2]) - std::atan(0.1)) < 1e-12, "an up offset lifts it");
        // The rotator forms give the same ray.
        for (const double pitch : {-60.0, -10.0, 0.0, 35.0, 80.0})
        {
            const Offset o{0.07, -0.04};
            double want[3], add[2], local[2];
            SpreadDirection(pitch, 170, o, want);
            SpreadRotator(pitch, 170, o, add[0], add[1]);
            const double p = (pitch + add[0]) * 3.14159265358979323846 / 180, y = (170 + add[1]) * 3.14159265358979323846 / 180;
            CHECK(std::fabs(std::cos(p) * std::cos(y) - want[0]) < 1e-9 && std::fabs(std::cos(p) * std::sin(y) - want[1]) < 1e-9 && std::fabs(std::sin(p) - want[2]) < 1e-9 &&
                      std::fabs(add[1]) < 30,
                  "adding the spread rotator to the camera's gives the bullet's ray");
            SpreadLocalRotator(o, local[0], local[1]);
            const double lp = local[0] * 3.14159265358979323846 / 180, ly = local[1] * 3.14159265358979323846 / 180;
            const double n = std::sqrt(1 + o.right * o.right + o.up * o.up);
            CHECK(std::fabs(std::cos(lp) * std::cos(ly) - 1 / n) < 1e-12 && std::fabs(std::cos(lp) * std::sin(ly) - o.right / n) < 1e-12 && std::fabs(std::sin(lp) - o.up / n) < 1e-12,
                  "the local rotator points along (1, right, up) in the camera's frame");
        }
    }

    inline void ScopeCycle()
    {
        const auto& awp = W("awp");
        Scope s;
        s.Press(awp, 1.0);
        CHECK(s.level() == 1 && s.Tick(1.0, true) && !s.Tick(1.01, true), "right mouse once: the first zoom level");
        CHECK(s.Blend(1.0) == 0 && s.Blend(1.0 + ScopeInSeconds / 2) > 0.4 && s.Blend(1.0 + ScopeInSeconds + 1e-9) == 1, "scoping in takes a short moment");
        s.Press(awp, 1.5);
        CHECK(s.level() == 2 && s.Blend(1.5) == 1, "again: the second level, no new scope-in");
        s.Press(awp, 2.0);
        CHECK(s.level() == 0 && s.Tick(2.0, false) && s.Blend(2.0) == 0, "a third time: out of the scope");
        s.Press(awp, 3.0);
        s.Press(awp, 3.1);
        s.Shot(3.5, 1.46);
        CHECK(s.level() == 0 && s.resumePending() && s.Tick(3.5, true), "a shot takes you out of the scope");
        s.Tick(4.0, true);
        CHECK(s.level() == 0, "no scope during the bolt");
        CHECK(s.Tick(3.5 + 1.46, true) && s.level() == 2 && s.Blend(3.5 + 1.46) == 0, "held through the bolt: back to the same level, scoping in again");
        s.Shot(6.0, 1.46);
        s.Tick(6.0, true);
        CHECK(!s.Tick(6.0 + 1.5, false) && s.level() == 0 && !s.resumePending(), "released before the bolt ends: it stays out");
        s.Press(awp, 8.0);
        s.Out();
        CHECK(s.level() == 0 && s.Tick(8.1, true), "switching weapons or reloading leaves the scope");
        Scope rifle;
        rifle.Press(W("ak47"), 1.0);
        CHECK(rifle.level() == 0 && !rifle.Tick(1.0, true), "a rifle has no CS scope");
        CHECK(LevelFov(40, awp, 1) == 40 && std::fabs(std::tan(LevelFov(40, awp, 2) * 3.14159265358979323846 / 360) / std::tan(20 * 3.14159265358979323846 / 180) -
                                                       std::tan(5 * 3.14159265358979323846 / 180) / std::tan(20 * 3.14159265358979323846 / 180)) < 1e-12 &&
                  std::fabs(LevelFov(50, awp, 2) - LevelFov(40, awp, 2)) > 1,
              "the second level zooms CS's 40 to 10, from whatever FOV the game's first level has");
        CHECK(LevelSensitivity(1, awp, 1) == 1 && LevelSensitivity(1, awp, 2) > 0.2 && LevelSensitivity(1, awp, 2) < 0.3 && LevelSensitivity(0.5, awp, 2) * 2 == LevelSensitivity(1, awp, 2),
              "the sensitivity follows the zoom: the lobby's on level 1, scaled by the zoom on level 2");
    }

    inline void Speeds()
    {
        CHECK(SpeedShare(&W("knife"), false) == 1 && SpeedShare(nullptr, false) == 1, "the knife runs at full speed");
        CHECK(SpeedShare(&W("awp"), false) < SpeedShare(&W("ak47"), false) && SpeedShare(&W("ak47"), false) < SpeedShare(&W("m4a1s"), false) &&
                  SpeedShare(&W("m4a1s"), false) < SpeedShare(&W("mp9"), false) && SpeedShare(&W("deagle"), false) < SpeedShare(&W("usp"), false) &&
                  SpeedShare(&W("glock"), false) < SpeedShare(&W("knife"), false) && std::fabs(SpeedShare(&W("ak47"), false) - 215.0 / 250) < 1e-12,
              "each weapon has its CS speed: the AWP slowest, then rifles, SMGs and pistols, the knife fastest");
        CHECK(SpeedShare(&W("awp"), true) == 0.4 && SpeedShare(&W("ak47"), true) == SpeedShare(&W("ak47"), false), "scoped, the AWP is slower still");
    }

    inline void Sights()
    {
        CHECK(CrosshairGap(0, 90) == 0 && CrosshairGap(0.02, 90) > CrosshairGap(0.01, 90) && std::fabs(CrosshairGap(0.1, 90) - std::tan(0.1)) < 1e-12 && CrosshairGap(0.1, 0) == 0,
              "the crosshair gap is the cone's edge on the screen");
        const auto& awp = W("awp");
        Accuracy a;
        a.Tick(0.016, awp, {100, false, false}, 1);
        CHECK(ScopeBlur(awp, a.settled(), 1) == 1 && ScopeBlur(awp, a.settled(), 0) == 0 && ScopeBlur(W("ak47"), 1, 1) == 0, "moving scoped smears the scope; unscoped there is none");
        for (int i = 0; i < 6; ++i) a.Tick(1.0 / 60, awp, {0, false, false}, 1);
        const double soon = ScopeBlur(awp, a.settled(), 1);
        for (int i = 0; i < 40; ++i) a.Tick(1.0 / 60, awp, {0, false, false}, 1);
        CHECK(soon > 0.5 && ScopeBlur(awp, a.settled(), 1) < 0.05, "after stopping the scope takes a moment to settle");
        CHECK(std::fabs(ScopeBlur(awp, 0, 0.3) - 0.7) < 1e-12 && ScopeBlur(awp, 0, 1) == 0, "scoping in clears from a blur");
    }

    inline void Run()
    {
        SpreadBySpeed();
        Recoil();
        Seeds();
        Rays();
        ScopeCycle();
        Speeds();
        Sights();
    }
} // namespace csfeel_checks

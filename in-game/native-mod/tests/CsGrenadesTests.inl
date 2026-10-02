// CS grenades drawn by AimModCore (core/include/aimmod/CsGrenades.hpp): grenades.tsv, paths, models.
#include <aimmod/CsGrenades.hpp>

namespace csgrenades_checks
{
    using namespace aimmod;
    using namespace aimmod::cs;

    inline void File()
    {
        const auto s = ParseGrenades("AIMMOD_GRENADES_1\t9\nmatch\tAimMod Match - CS - 0a1b2c3d\nhand\tflash\t1\t1790000000000\n"
                                     "fly\t5\the\t1790000001000\t1790000002500\t2\t0\t0\t0\t180\t800\t0\t0\t0\t506.2\t405\t0\t2\t360\t0\t0\t2\n"
                                     "smoke\t6\t1\t2\t3\t1790000000000\t1790000018000\nfire\t7\tmolotov\t4\t5\t6\t250\t1790000000500\t1790000007500\n"
                                     "decoy\t8\t7\t8\t9\t1790000000000\t1790000015000\nblast\t9\the\t1\t1\t1\t1790000002500\nblast\t10\textinguished\t4\t5\t6\t1790000003000\n");
        CHECK(s && s->sequence == 9 && s->hand.kind == "flash" && s->hand.pin && s->hand.thrownMs == 1790000000000 && s->flying.size() == 1 && s->flying[0].keys.size() == 2 &&
                  s->flying[0].keys[1].motion == GrenadeRest && s->smokes.size() == 1 && s->fires.size() == 1 && s->fires[0].radius == 250 && s->decoys.size() == 1 && s->blasts.size() == 2,
              "grenades.tsv: the hand, a grenade in flight, a smoke, a fire, a decoy and blasts");
        const auto none = ParseGrenades("AIMMOD_GRENADES_1\t1\nmatch\tA\nhand\t-\t0\t0\n");
        CHECK(none && none->hand.kind.empty() && none->flying.empty(), "nothing in hand, nothing thrown");
        CHECK(!ParseGrenades("AIMMOD_GRENADES_1\t1\nmatch\tA\nhand\tbanana\t0\t0\n") && !ParseGrenades("AIMMOD_GRENADES_1\t1\nmatch\tA\nfly\t1\the\t0\t0\t2\t0\t0\t0\t0\t0\t0\t0\t0\n") &&
                  !ParseGrenades("AIMMOD_GRENADES_1\t1\nmatch\tA\nfire\t1\tsmoke\t0\t0\t0\t250\t0\t1\n") && !ParseGrenades("AIMMOD_GRENADES_1\t1\nhand\t-\t0\t0\n") &&
                  !ParseGrenades("AIMMOD_ROUND_1\t1\nmatch\tA\n") && !ParseGrenades("AIMMOD_GRENADES_1\t1\nmatch\tA\nsmoke\t1\t1e9\t0\t0\t0\t1\n"),
              "unknown kinds, short paths, wrong fire kinds, a missing match line and huge numbers are refused");
    }

    inline void Paths()
    {
        // The service's formulas: a parabola in flight, an even slow-down sliding, still at rest.
        const std::vector<GrenadeKey> keys = {{0, 0, 0, 180, 800, 0, 300, GrenadeFlight}, {500, 400, 0, 2, 600, 0, 0, GrenadeSlide}, {1357.1, 657.1, 0, 2, 0, 0, 0, GrenadeRest}};
        const Point a = GrenadeAt(keys, 250), b = GrenadeAt(keys, 800), c = GrenadeAt(keys, 5000), d = GrenadeAt(keys, -10);
        CHECK(std::fabs(a.x - 200) < 1e-9 && std::fabs(a.z - (180 + 75 - GrenadeGravity * 0.0625 / 2)) < 1e-9, "in flight: x = vx t, z falls with gravity");
        CHECK(std::fabs(b.x - (400 + 600 * 0.3 - GrenadeSlideDecel * 0.09 / 2)) < 1e-9 && b.z == 2 && c.x == 657.1 && d.x == 0 && d.z == 180, "sliding slows evenly; at rest it stays; before the throw it's in the hand");
    }

    inline void Models()
    {
        for (const char* kind : {"he", "flash", "smoke", "decoy", "molotov", "incendiary"})
        {
            const auto& parts = GrenadeModel(kind);
            bool small = !parts.empty();
            for (const Part& p : parts)
                small = small && std::wstring(p.mesh).rfind(L"/Engine/BasicShapes/", 0) == 0 && p.size[0] > 0 && p.size[2] > 0 && p.size[0] <= 12 && p.size[2] <= 12 && p.colour[0] <= 1 && p.colour[1] <= 1 && p.colour[2] <= 1;
            CHECK(small && KnownGrenade(kind), "each grenade is AimMod's own small model of basic shapes");
        }
        CHECK(&GrenadeModel("flash") != &GrenadeModel("he") && &GrenadeModel("molotov") != &GrenadeModel("incendiary") && !KnownGrenade("c4"), "every grenade looks like itself");
        CHECK(GrenadeInHand(true).offset[0] < GrenadeInHand(false).offset[0] && GrenadeInHand(true).offset[2] > GrenadeInHand(false).offset[2], "pulling the pin draws the grenade back and up");
        // The smoke cloud reaches its full size, the same on every machine, and grows in and thins out.
        double reach = 0, top = 0, bottom = 0;
        for (const Puff& p : SmokePuffs())
        {
            reach = std::max(reach, std::hypot(p.offset[0], p.offset[1]) + p.size / 2);
            top = std::max(top, p.offset[2] + p.size * 0.4);
            bottom = std::min(bottom, p.offset[2] - p.size * 0.4);
        }
        CHECK(SmokePuffs().size() >= 12 && reach > SmokeRadius * 0.85 && reach < SmokeRadius * 1.25 && top > SmokeHalfHeight * 0.6 && bottom < -SmokeHalfHeight * 0.6, "the puffs fill the smoke's size");
        CHECK(SmokeScale(1000, 19000, 1000) == 0 && SmokeScale(1000, 19000, 1500) == 0.5 && SmokeScale(1000, 19000, 9000) == 1 && SmokeScale(1000, 19000, 18000) == 0.5 && SmokeScale(1000, 19000, 19000) == 0,
              "a smoke spreads out over 1 s and thins over its last 2 s");
        CHECK(SmokeCentre(0, 0, 10).z == 10 + SmokeHalfHeight - 40, "the cloud sits on the ground");
        const auto flames = FireFlames(250, 2.0, 5.0, 3), later = FireFlames(250, 2.2, 5.0, 3), dying = FireFlames(250, 2.0, 0.5, 3);
        bool within = flames.size() > 6;
        for (std::size_t i = 1; i < flames.size(); ++i) within = within && std::hypot(flames[i].offset[0], flames[i].offset[1]) <= 250 && flames[i].size[2] > 0;
        CHECK(within && flames[1].size[2] != later[1].size[2] && dying[1].size[2] < flames[1].size[2] && FireFlames(250, 2, 0, 3).empty(), "flames stay in the fire, flicker, and die down at the end");
        CHECK(BlastSize("he", 0.1) > BlastSize("flash", 0.05) && BlastSize("he", 1) == 0 && BlastSize("flash", 0.05) > 0 && BlastSize("he", -0.1) == 0 && BlastSize("smoke", 0.1) == 0, "an HE fireball, a short flash pop");
        double white[3], fire[3];
        BlastColour("flash", white);
        BlastColour("molotov", fire);
        CHECK(white[0] == 1 && white[1] == 1 && white[2] == 1 && fire[0] == 1 && fire[2] < 0.2, "the flash is white, fire is orange");
    }

    // The smoke's puffs: out of the canister, spread to their places in about a second, drifting
    // slowly, the edge thinning first at the end; the camera's depth inside; the flash's white and
    // after-image.
    inline void SmokeAndFlash()
    {
        const auto& puffs = SmokePuffs();
        CHECK(puffs.size() == 40, "a smoke is 40 puffs");
        bool fromCan = true, spread = true, bounded = true, drifts = false, gone = true;
        double maxDelay = 0;
        for (const Puff& p : puffs)
        {
            maxDelay = std::max(maxDelay, p.delay);
            const PuffPose start = SmokePuff(p, p.delay + 0.02, 17), placed = SmokePuff(p, 1.2, 16), later = SmokePuff(p, 9.0, 9), end = SmokePuff(p, 17.99, 0.01);
            const double home = std::hypot(p.offset[0], p.offset[1]);
            fromCan = fromCan && std::hypot(start.offset[0], start.offset[1]) < home * 0.35 + 20 && start.size[0] < p.size * 0.5;
            spread = spread && placed.shown && std::fabs(std::hypot(placed.offset[0], placed.offset[1]) - home) < 60 && placed.size[0] > p.size * 0.85;
            const double moved = std::hypot(later.offset[0] - p.offset[0], later.offset[1] - p.offset[1]);
            bounded = bounded && moved < 45 && std::fabs(later.size[0] / p.size - 1) < 0.15;
            drifts = drifts || moved > 5;
            gone = gone && end.size[0] < p.size * 0.1;
        }
        CHECK(fromCan && maxDelay < 0.4, "puffs come out of the canister, the inner ones first");
        CHECK(spread, "about a second after the pop the cloud has spread to its full size");
        CHECK(bounded && drifts, "the puffs drift and billow slowly, never far from their place");
        const Puff* edge = nullptr;
        const Puff* heart = nullptr;
        for (const Puff& p : puffs)
        {
            if (!edge || p.inner < edge->inner) edge = &p;
            if (!heart || p.inner > heart->inner) heart = &p;
        }
        CHECK(SmokePuff(*edge, 17, 1.0).size[0] / edge->size < SmokePuff(*heart, 17, 1.0).size[0] / heart->size && gone && !SmokePuff(*heart, 18, 0).shown,
              "at the end the edge thins out first and the cloud is gone");
        const std::vector<GrenadeState::Area> smokes = {{1, "smoke", 0, 0, 0, SmokeRadius, 1000, 19000}};
        const Point c = SmokeCentre(0, 0, 0);
        CHECK(SmokeDepth(smokes, 9000, c.x, c.y, c.z) == 1 && SmokeDepth(smokes, 9000, 2000, 0, c.z) == 0 && SmokeDepth(smokes, 500, c.x, c.y, c.z) == 0 &&
                  SmokeDepth(smokes, 9000, SmokeRadius * 0.85, 0, c.z) > 0 && SmokeDepth(smokes, 9000, SmokeRadius * 0.85, 0, c.z) < 1,
              "the camera is deep in the smoke at its heart, thinly at its edge, not at all outside it or before it pops");

        std::string why;
        const auto f = ParseGrenades("AIMMOD_GRENADES_1\t3\nmatch\tA\nhand\t-\t0\t0\nflash\t77\t1790000005000\t2000\t3000\t1\n", &why);
        CHECK(f && f->flash && f->flash->id == 77 && f->flash->atMs == 1790000005000 && f->flash->holdMs == 2000 && f->flash->fadeMs == 3000 && f->flash->peak == 1,
              "grenades.tsv: the flash that hit this player");
        CHECK(!ParseGrenades("AIMMOD_GRENADES_1\t3\nmatch\tA\nflash\t1\t0\t2000\t3000\t1.5\n", &why) && why.rfind("flash", 0) == 0 &&
                  !ParseGrenades("AIMMOD_GRENADES_1\t3\nhand\t-\t0\t0\n", &why) && why == "no match line",
              "a bad file says which line was wrong");
        const GrenadeState::Flash full{1, 10000, 2000, 3000, 1}, glance{2, 10000, 200, 1000, 0.45};
        CHECK(FlashWhite(full, 9999) == 0 && FlashWhite(full, 11000) == 1 && FlashWhite(full, 13500) == 0.25 && FlashWhite(full, 15000) == 0 && FlashWhite(glance, 10100) == 0.45,
              "the white: full while it holds, clearing after");
        CHECK(FlashAfterImage(full, 11000) > 0.8 && FlashAfterImage(full, 13000) > 0.2 && FlashAfterImage(full, 14500) == 0 && FlashAfterImage(glance, 10100) == 0,
              "a strong flash leaves a frozen after-image that lingers into the fade; a glance leaves none");
    }

    inline void Run()
    {
        File();
        SmokeAndFlash();
        Paths();
        Models();
    }
} // namespace csgrenades_checks

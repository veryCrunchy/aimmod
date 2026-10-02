namespace AimMod.InGame.Multiplayer;

// CS grenades (game-modes.md 6.6.5): prices and carry limits, buying, the throw and its physics
// (deterministic, bounces, the fallback floor), HE falloff and armour, flash blindness by view angle
// and line of sight, smoke blocking sight and flashes, fire over time and put out by smoke, the files
// between the service, AimModSteam and AimModCore, sounds, the HUD and world-tag bits, and bots.
static partial class MultiplayerChecks
{
    // The level-floor path both GrenadePhysics implementations must give (AimModSteam's
    // BridgeTests checks the same numbers): eye at 180 cm over a floor at 0, a full throw ahead.
    internal const double GoldenRestMs = 2206, GoldenRestX = 3852.8;

    static void CsGrenadeChecks()
    {
        GrenadeRulesChecks();
        GrenadePhysicsChecks();
        GrenadeMatchChecks();
        GrenadeSmokeAndFireChecks();
        GrenadeFilesAndViews();
        GrenadeBotChecks();
        GrenadeOwnFlashChecks();
        GrenadeThrowChecks();
        GrenadeFireSpreadChecks();
        GrenadeDropChecks();
        GrenadeDecoyAndSoundChecks();
        GrenadeIntelChecks();
        GrenadeWireChecks();
    }

    static void GrenadeRulesChecks()
    {
        string?[] P(params string[] ids) => ids.Select(id => GrenadeRules.Find(id)?.Price.ToString() + "/" + GrenadeRules.Find(id)?.Side).ToArray();
        Check(P("he", "flash", "smoke", "decoy", "molotov", "incendiary").SequenceEqual(["300/any", "200/any", "300/any", "50/any", "400/T", "600/CT"]),
            "Grenade prices and sides: HE $300, flash $200, smoke $300, decoy $50, molotov $400 (T), incendiary $600 (CT)");
        Check(GrenadeRules.CarryProblem(["flash"], "flash") is null && GrenadeRules.CarryProblem(["flash", "flash"], "flash") == "owned" && GrenadeRules.CarryProblem(["he"], "he") == "owned"
            && GrenadeRules.CarryProblem(["he", "flash", "flash", "smoke"], "decoy") == "carry-limit" && GrenadeRules.CarryProblem(["molotov"], "incendiary") == "owned"
            && GrenadeRules.CarryProblem(["he", "flash", "smoke"], "molotov") is null && GrenadeRules.CarryProblem([], "nade") == "unknown-item",
            "Carry limits: four in all, two flashbangs, one of each other, one fire grenade");
        Check(GrenadeRules.Next(["flash", "he", "smoke"], "he") == "flash" && GrenadeRules.Next(["flash", "he", "smoke"], "smoke") == "he" && GrenadeRules.Next(["flash", "flash"], "flash") == "flash"
            && GrenadeRules.Next([], null) is null && GrenadeRules.Sorted(["decoy", "smoke", "he", "flash"]).SequenceEqual(["he", "flash", "smoke", "decoy"]),
            "Key 4 cycles the grenades you carry in CS order: HE, flash, smoke, fire, decoy");
        Check(CsRules.FindAny("he")?.Label == "HE Grenade" && CsRules.ByProfile("AimMod CS Molotov")?.Id == "molotov" && CsRules.ByProfile("AimMod CS Incendiary Grenade")?.Label == "Incendiary Grenade"
            && CsRules.Find("he") is null && CsRules.Profiles.Contains(CsRules.Grenade) && !CsRules.Profiles.Any(w => w.Class == "grenade" && w != CsRules.Grenade),
            "The kill feed knows grenades by name; the arena carries one grenade-slot profile, not one per grenade");
        // HE: 98 at the blast, falling off with distance, nothing past 15.4 m; armour takes half.
        var d = new[] { 0.0, 200, 500, 1000, 1500 }.Select(GrenadeRules.HeDamageAt).ToArray();
        Check(Math.Abs(d[0] - 98) < 1e-9 && d.Zip(d.Skip(1)).All(p => p.Second < p.First) && GrenadeRules.HeDamageAt(GrenadeRules.HeRadiusCm) == 0 && d[2] is > 55 and < 65 && d[3] is > 10 and < 20,
            "HE damage falls off with distance (" + string.Join(", ", d.Select(x => Math.Round(x))) + ")");
        var (health, armor) = CsRules.Armor(80, false, 100, false, GrenadeRules.HeArmorPenetration);
        Check(health == 40 && armor == 20, "Armour halves HE damage to health");
        // Flash: full in your face, less to the side, a little behind you, nothing far away.
        Check(GrenadeRules.FlashAmount(1, 300) == 1 && GrenadeRules.FlashAmount(0.3, 300) is > 0.6 and < 0.75 && GrenadeRules.FlashAmount(-1, 300) == 0.1
            && GrenadeRules.FlashAmount(1, 1500) == 1 && GrenadeRules.FlashAmount(1, (GrenadeRules.FlashNearCm + GrenadeRules.FlashFarCm) / 2) is > 0.45 and < 0.55
            && GrenadeRules.FlashAmount(1, GrenadeRules.FlashFarCm) == 0, "Flash amount by view angle and distance (full within 400 units, none past 1500)");
        var full = GrenadeRules.FlashFor(1000, 1)!;
        Check(full is { HoldMs: 2500, FadeMs: 2800 } && GrenadeRules.FlashAlpha(full, 1500) == 1 && GrenadeRules.FlashAlpha(full, 3400) == 1 && GrenadeRules.FlashAlpha(full, 1000 + 2500 + 1400) is > 0.2 and < 0.3
            && GrenadeRules.FlashAlpha(full, 6300) == 0 && GrenadeRules.FlashFor(0, 0.03) is null && GrenadeRules.FlashAlpha(GrenadeRules.FlashFor(0, 0.2), 10) < 0.6,
            "A full flash whites out for 2.5 s and clears over 2.8 s (about 5 s, as in CS); a glance only dims");
        var half = GrenadeRules.FlashFor(0, 0.6)!;
        Check(half.HoldMs + half.FadeMs is > 2000 and < 3000 && GrenadeRules.FlashPeak(half) > 0.9, "A flash from the side or further off: fully white, about 2.5 s in all");
        // The flash reaches this player's screen: grenades.tsv (AimModCore's white and after-image) in local
        // time, and the notice page's own timing (so its white plays smoothly between polls).
        var flashed = MultiplayerService.GrenadesBody("AimMod Match - X", null, false, 0, [], 400, full);
        Check(flashed.Contains("\nflash\t1000\t600\t2500\t2800\t1\n", StringComparison.Ordinal), "grenades.tsv carries the flash that hit you, in this machine's time");
        var me = new CsPlayerView("me", 1, CsRules.T, 800, true, 100, 0, false, false, null, "glock", 0, 0, Flash: full);
        Check(MultiplayerService.FlashFx(me, 2000) is { Id: 1000, Age: 1000, Hold: 2500, Fade: 2800, Peak: 1 } && MultiplayerService.FlashFx(me, 6400) is null && MultiplayerService.FlashFx(me with { Flash = null }, 2000) is null,
            "The HUD gets the flash's timing while it lasts");
        var shots = GrenadeRules.DecoyShots(7, "rifle");
        Check(shots.Count > 20 && shots.SequenceEqual(GrenadeRules.DecoyShots(7, "rifle")) && !shots.SequenceEqual(GrenadeRules.DecoyShots(8, "rifle")) && shots.All(t => t is > 0 and < GrenadeRules.DecoyMs)
            && GrenadeRules.DecoyShots(7, "sniper").Count < shots.Count / 3, "Decoy gunfire: bursts like the owner's weapon, the same on every machine");
    }

    // A floor at z = 0 and a wall at x = 800 (facing -x).
    static (double[] Point, double[] Normal)? Room(double[] a, double[] b)
    {
        double? best = null; double[]? normal = null;
        if (a[2] >= 0 && b[2] < 0) { best = a[2] / (a[2] - b[2]); normal = [0, 0, 1]; }
        if (a[0] <= 800 && b[0] > 800) { var f = (800 - a[0]) / (b[0] - a[0]); if (best is null || f < best) { best = f; normal = [-1, 0, 0]; } }
        if (best is not { } t) return null;
        return ([a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t], normal!);
    }

    static void GrenadePhysicsChecks()
    {
        var full = GrenadePhysics.ThrowVelocity(0, 0, 1); var under = GrenadePhysics.ThrowVelocity(0, 90, 0); var mid = GrenadePhysics.ThrowVelocity(0, 0, 0.5);
        double Speed(double[] v) => Math.Sqrt(v.Sum(x => x * x));
        Check(Math.Abs(Speed(full) - GrenadePhysics.ThrowSpeed) < 1e-6 && Math.Abs(Speed(under) - GrenadePhysics.ThrowSpeed * 0.3) < 1e-6 && Math.Abs(Speed(mid) - GrenadePhysics.ThrowSpeed * 0.65) < 1e-6
            && full[2] > 0 && Math.Abs(Math.Atan2(full[2], full[0]) * 180 / Math.PI - 10) < 1e-6 && Math.Abs(under[0]) < 1e-6 && under[1] > 0,
            "Throws: full on fire, 30 % underhand, 65 % with both buttons, aimed 10 degrees up at level");
        var keys = GrenadePhysics.Simulate([0, 0, 180], full, Room);
        var again = GrenadePhysics.Simulate([0, 0, 180], full, Room);
        Check(keys.SequenceEqual(again), "The same throw gives the same path every time");
        Check(keys[0].Motion == GrenadePhysics.Flight && keys[^1].Motion == GrenadePhysics.Rest && keys.Any(k => k.Impact == GrenadePhysics.WallImpact) && keys.Any(k => k.Impact == GrenadePhysics.FloorImpact)
            && keys.All(k => k.X <= 800 && k.Z >= -0.01) && keys[^1].X < 800 && keys.Zip(keys.Skip(1)).All(p => p.Second.T >= p.First.T),
            "A throw at a wall bounces off it, lands and comes to rest in the room (" + keys.Count + " keys, rest at " + Math.Round(keys[^1].X) + " cm, " + Math.Round(keys[^1].T) + " ms)");
        Check(keys.Zip(keys.Skip(1)).All(p => { var at = GrenadePhysics.Pos(p.First, (p.Second.T - p.First.T) / 1000); return Math.Sqrt(Math.Pow(at[0] - p.Second.X, 2) + Math.Pow(at[1] - p.Second.Y, 2) + Math.Pow(at[2] - p.Second.Z, 2)) < 3; }),
            "Between keys the path is exact: each key starts where the last one's motion leads");
        var wallHit = keys.First(k => k.Impact == GrenadePhysics.WallImpact);
        var before = keys[keys.IndexOf(wallHit) - 1];
        Check(wallHit.Vx < 0 && Math.Abs(wallHit.Vx) < Math.Abs(before.Vx) * 0.5, "A bounce keeps 45 % of the speed");
        // The level-floor fallback, and the numbers AimModSteam's physics must match.
        var flat = GrenadePhysics.Simulate([0, 0, 180], full, GrenadePhysics.Floor(0));
        Check(Math.Abs(flat[^1].T - GoldenRestMs) < 0.5 && Math.Abs(flat[^1].X - GoldenRestX) < 0.5 && flat.Any(k => k.Motion == GrenadePhysics.Slide),
            "Level floor: the throw bounces, slides and rests where both implementations say (" + Math.Round(flat[^1].T, 1) + " ms, " + Math.Round(flat[^1].X, 1) + " cm)");
        var drop = GrenadePhysics.Simulate([0, 0, 100], [0, 0, 0], GrenadePhysics.Floor(0));
        Check(drop[^1] is { Motion: GrenadePhysics.Rest, Impact: GrenadePhysics.FloorImpact } && Math.Abs(drop[^1].Z - GrenadePhysics.Lift) < 0.01 && drop.Count <= 6, "A dropped grenade falls and settles on the floor");
        var lost = GrenadePhysics.Simulate([0, 0, 100], [100, 0, 0], (_, _) => null);
        Check(lost[^1].Motion == GrenadePhysics.Rest && Math.Abs(lost[^1].T - GrenadePhysics.MaxSeconds * 1000) < 20, "With nothing to hit, the path ends after 8 s");
        // When each kind goes off.
        var he = GrenadePhysics.Detonation(GrenadeRules.He, flat); var smoke = GrenadePhysics.Detonation(GrenadeRules.Smoke, flat); var molotov = GrenadePhysics.Detonation(GrenadeRules.Molotov, flat);
        var firstFloor = flat.Skip(1).First(k => k.Impact == GrenadePhysics.FloorImpact);
        var think = GrenadePhysics.NextThink(flat[^1].T);
        Check(he.T == 1600 && GrenadePhysics.Detonation(GrenadeRules.Flash, flat).T == 1600 && he.Works && Math.Abs(he.At[0] - GrenadePhysics.At(flat, 1600)[0]) < 1e-9
            && smoke.T == think && think > flat[^1].T && think - flat[^1].T <= GrenadeRules.ThinkMs && think % GrenadeRules.ThinkMs == 0 && smoke.At[0] == flat[^1].X
            && GrenadePhysics.Detonation(GrenadeRules.Decoy, flat).T == think && molotov.Works && molotov.T == firstFloor.T && molotov.At[0] == firstFloor.X,
            "HE and flash go off at 1.6 s (CS's think after 1.5 s), a smoke and a decoy at the first think once they lie still, a molotov on landing");
        var high = GrenadePhysics.Simulate([0, 0, 2000], [500, 0, 900], GrenadePhysics.Floor(0));
        Check(GrenadePhysics.Detonation(GrenadeRules.Incendiary, high) is { T: 2000, Works: false }, "A fire grenade still in the air at 2 s bursts without fire");
    }

    // A CS match with players standing still at eye height 180 over a floor at 0.
    static CsMatch GrenadeMatch(long t0, params (string Id, int Team, double X, double Y, double Yaw)[] who)
    {
        var match = new CsMatch(who.Select(w => w.Id).ToList(), t0, 6, true, null, who.ToDictionary(w => w.Id, w => w.Team));
        foreach (var w in who)
        {
            var list = new List<TrackSample>();
            for (var t = t0 + 1000; t < t0 + 40_000; t += 50) list.Add(new TrackSample(t, w.X, w.Y, 180, 0, w.Yaw));
            for (var i = 0; i < list.Count; i += 60) match.Combat.Track(w.Id, new TrackBatch("m", 1, list.Skip(i).Take(60).ToList(), []));
        }
        return match;
    }
    static CsPlayerView PlayerOf(CsMatch m, string id) => m.View().Players.First(p => p.Member == id);
    // Ticks the match every 50 ms from `from` to `to`.
    static void Run(CsMatch m, long from, long to) { for (var t = from; t <= to; t += 50) m.Tick(t); }

    static void GrenadeMatchChecks()
    {
        const long t0 = 40_000_000;
        var m = GrenadeMatch(t0, ("t1", 1, 0, 0, 0), ("t2", 1, 0, 300, 0), ("ct1", 2, 600, 0, 180), ("ct2", 2, 600, 300, 90));
        // Buying: prices, the side, the limits; grenades come in slot order.
        Check(m.Buy("t1", "flash", t0) is null && m.Buy("t1", "he", t0) is null && m.Buy("t1", "flash", t0) is null && m.Buy("t1", "flash", t0) == "owned" && m.Buy("t1", "incendiary", t0) == "side"
            && PlayerOf(m, "t1") is { Money: 100, Grenades: ["he", "flash", "flash"] }, "Buying grenades: two flashes and an HE for $700, in slot order; a third flash and the CT incendiary refused");
        Check(m.Buy("t2", "molotov", t0) is null && m.Buy("t2", "smoke", t0) is null && m.Buy("ct1", "incendiary", t0) is null && m.Buy("ct1", "molotov", t0) == "side"
            && m.Buy("ct2", "he", t0) is null && m.Buy("ct2", "smoke", t0) is null && m.Buy("ct2", "decoy", t0) is null && m.Buy("ct2", "flash", t0) == "money", "Each side buys its own fire grenade; money still counts");
        Check(m.Throw("t1", "he", 1, [0, 0, 180], 0, 0, t0 + 100) == "freeze", "No throwing in freeze time");
        var live = t0 + CsRules.FreezeMs;
        m.Tick(live);
        Check(m.Throw("t1", "decoy", 1, [0, 0, 180], 0, 0, live) == "no-grenade" && m.Throw("t1", "he", 1, [900, 0, 180], 0, 0, live) == "origin", "A throw needs the grenade and must start where the thrower is");
        // HE dropped at ct1's feet: ct1 and ct2 hurt by distance, the thrower too, not their teammate (friendly fire off).
        Check(m.BotThrow("t1", "he", [600, 0, 180], [0, 0, 0], live) is null && PlayerOf(m, "t1").Grenades!.SequenceEqual(["flash", "flash"]), "Throwing takes the grenade");
        Check(m.View().Grenades?.Single() is { State: "flying", Kind: "he", Ends: null }, "A thrown grenade flies, its path pending the game's traces");
        Run(m, live + 50, live + 400);
        Check(m.View().Grenades?.Single() is { State: "flying", Ends: { } off } && off == live + 1600, "Without an answer from the game the level-floor fallback flies it; it goes off 1.6 s after the throw");
        Run(m, live + 450, live + 1600);
        var blast = m.View().Grenades?.SingleOrDefault(g => g.State == "blast");
        Check(blast is { Kind: "he", Pos: { } bp } && Math.Abs(bp[0] - 600) < 1 && m.Grenades.LosRequests.Count == 8, "The HE explodes where it lay and asks the game for line of sight to everyone in reach");
        Run(m, live + 1650, live + 1950);
        double Hp(string id) => PlayerOf(m, id).Health;
        var expected = 100 - GrenadeRules.HeDamageAt(Math.Sqrt(Math.Pow(116 - (GrenadePhysics.Lift + 10), 2)));
        Check(Math.Abs(Hp("ct1") - expected) < 1.5 && Hp("ct2") is > 15 and < 30 && Hp("t1") is > 45 and < 60 && Hp("t2") == 100,
            "HE damage by distance: " + string.Join(", ", new[] { "ct1", "ct2", "t1", "t2" }.Select(id => id + " " + Hp(id))));
        // A flash at t1's feet: t1 and ct1 face it, ct2 looks away.
        var t = live + 2000;
        var flashLog = new List<string>();
        m.Grenades.Trace = flashLog.Add;
        m.BotThrow("t1", "flash", [300, 0, 180], [0, 0, 0], t);
        Run(m, t, t + 1900);
        Check(flashLog.Count == 1 && flashLog[0].Contains("t1 blinded 1.00", StringComparison.Ordinal) && flashLog[0].Contains("ct2 blinded 0.1", StringComparison.Ordinal)
            && flashLog[0].Contains("counted clear", StringComparison.Ordinal), "The host logs what each flash did: who it blinded, how much, and whether the game answered the sight lines");
        var f1 = PlayerOf(m, "t1").Flash; var fc1 = PlayerOf(m, "ct1").Flash; var fc2 = PlayerOf(m, "ct2").Flash;
        Check(f1 is { Amount: 1 } && fc1 is { Amount: 1 } && fc2 is { Amount: < 0.2 } && fc2.HoldMs < 100 && PlayerOf(m, "t2").Flash is { Amount: 1 },
            "Flashes blind by where you look: the thrower, a teammate and an enemy facing it fully, someone looking away barely");
        // Kills by grenade: the kill feed names it, the reward is paid; your own grenade costs a kill.
        var k = GrenadeMatch(t0, ("a", 1, 0, 0, 0), ("b", 2, 100, 0, 180));
        k.Buy("a", "he", t0); k.Buy("b", "incendiary", t0);
        k.Tick(live);
        k.Combat.AreaHit("b", "a", 95, CsRules.Bomb.Combat, live, null); // softened up
        k.BotThrow("b", "incendiary", [0, 0, 180], [0, 0, 0], live + 10);
        Run(k, live + 50, live + 1500);
        var kill = k.View().Events.LastOrDefault(e => e.Kind == "kill");
        Check(kill is { Member: "a" } && kill.Text!.StartsWith("b\tincendiary\t0", StringComparison.Ordinal) && !PlayerOf(k, "a").Alive && PlayerOf(k, "a").Grenades is null && k.View().Events.Any(e => e is { Kind: "money", Member: "b", Text: "kill", Amount: 300 }),
            "Fire kills over time; the kill feed says incendiary, the killer gets $300, the dead lose their grenades");
        Check(CsRules.FindAny(kill!.Text!.Split('\t')[1])?.Label == "Incendiary Grenade" && CsRules.FindAny("he")?.Label == "HE Grenade" && CsRules.FindAny("molotov")?.Label == "Molotov",
            "The kill feed names the grenade (Incendiary Grenade, HE Grenade, Molotov)");
        var self = GrenadeMatch(t0, ("a", 1, 0, 0, 0), ("b", 2, 3000, 0, 180));
        self.Buy("a", "he", t0); self.Tick(live);
        self.Combat.AreaHit("b", "a", 60, CsRules.Bomb.Combat, live, null);
        self.BotThrow("a", "he", [0, 0, 180], [0, 0, 0], live + 10);
        Run(self, live + 50, live + 2000);
        Check(!PlayerOf(self, "a").Alive && PlayerOf(self, "a").Kills == 0 && self.View().Events.Last(e => e.Kind == "kill").Text!.StartsWith("a\the", StringComparison.Ordinal), "Your own HE can kill you; it isn't a kill");
        // Line of sight: the game says the blast is behind a wall, so no damage.
        var wall = GrenadeMatch(t0, ("a", 1, 0, 0, 0), ("b", 2, 300, 0, 180));
        wall.Buy("a", "he", t0); wall.Tick(live);
        wall.BotThrow("a", "he", [300, 0, 180], [0, 0, 0], live);
        Run(wall, live + 50, live + 1650);
        foreach (var los in wall.Grenades.LosRequests) wall.Grenades.AnswerLos(los.Tag, Math.Abs(los.To[0]) < 1); // a sees it, b doesn't
        Run(wall, live + 1700, live + 1750);
        Check(PlayerOf(wall, "b").Health == 100 && PlayerOf(wall, "a").Health < 100, "No HE damage through a wall the game's line trace hits");
        // The game's path replaces the fallback while it waits.
        var traced = GrenadeMatch(t0, ("a", 1, 0, 0, 0), ("b", 2, 3000, 0, 180));
        traced.Buy("a", "smoke", t0); traced.Tick(live);
        traced.BotThrow("a", "smoke", [0, 0, 180], [1000, 0, 0], live);
        var request = traced.Grenades.PathRequests.Single();
        Check(request is { Kind: "smoke" } && request.Velocity[0] == 1000, "The host asks its game to fly the throw");
        var path = GrenadePhysics.Simulate(request.Origin, request.Velocity, Room);
        Check(traced.Grenades.SetPath(request.Id, path) && traced.Grenades.PathRequests.Count == 0 && !traced.Grenades.SetPath(request.Id, path)
            && traced.View().Grenades!.Single().Keys!.Length == path.Count * 9, "The game's path is taken once and broadcast with the grenade");
        Run(traced, live + 50, live + (long)path[^1].T + 1300);
        Check(traced.View().Grenades?.Single(g => g.State == "smoke").Pos is { } sp && Math.Abs(sp[0] - path[^1].X) < 0.1, "The smoke pops where the traced path came to rest");
        // Grenades are gone at halftime and at a new round's start nothing still flies.
        Check(GrenadeMatch(t0, ("a", 1, 0, 0, 0), ("b", 2, 3000, 0, 180)) is var h && h.Buy("a", "flash", t0) is null && PlayerOf(h, "a").Grenades!.Count == 1, "A bought grenade stays until it's thrown or you die");
    }

    static void GrenadeSmokeAndFireChecks()
    {
        const long t0 = 50_000_000; var live = t0 + CsRules.FreezeMs;
        var smoke = new CsGrenadeView(1, "smoke", "a", "smoke", 0, GrenadeRules.SmokeMs, [0, 0, 0], Radius: GrenadeRules.SmokeRadiusCm);
        CsGrenadeView[] list = [smoke];
        Check(GrenadeRules.SmokeBlocks(list, [-1500, 0, 180], [1500, 0, 180], 5000) && !GrenadeRules.SmokeBlocks(list, [-1500, 900, 180], [1500, 900, 180], 5000)
            && !GrenadeRules.SmokeBlocks(list, [-1500, 0, 180], [1500, 0, 180], 100) && !GrenadeRules.SmokeBlocks(list, [-1500, 0, 180], [1500, 0, 180], GrenadeRules.SmokeMs + 1)
            && !GrenadeRules.SmokeBlocks(list, [-1500, 0, 180], [-1000, 0, 180], 5000), "A smoke blocks a line through it once grown, not beside it, before or after");
        Check(GrenadeRules.SmokeInside(list, [0, 0, 180], 5000) == 1 && GrenadeRules.SmokeInside(list, [2000, 0, 180], 5000) == 0, "Inside the cloud the HUD veils the view");
        // Bots: no sight through the smoke, none while flashed.
        var sight = new Dictionary<string, BotSight> { ["bot"] = new(5000, -1500, 0, 116, 0, new HashSet<int> { 1, 2 }) };
        BotPlayer[] players = [new("x", 1, 1500, 0, 180, 1, true, 0), new("y", 2, -1500, 900, 180, 1, true, 0)];
        CsView View(IReadOnlyList<CsGrenadeView>? grenades, CsFlashView? flash = null) => new(1, "live", 0, 0, [0, 0], CsRules.T, 6, true,
            [new("bot", 2, CsRules.CT, 800, true, 100, 0, false, false, null, "usp", 0, 0, Flash: flash)], new CsBombView("carried", null, null, null, null, null, null, null, null), null, null, null, [], null, null, grenades);
        var seen = MultiplayerService.GrenadeSight(View(list), sight, players, 5000)["bot"].Visible;
        Check(seen.SetEquals([2]) && MultiplayerService.GrenadeSight(View(null), sight, players, 5000)["bot"].Visible.Count == 2
            && MultiplayerService.GrenadeSight(View(null, GrenadeRules.FlashFor(4800, 1)), sight, players, 5000)["bot"].Visible.Count == 0, "Bots can't see through smoke or while flashed");
        // A smoke puts a fire out; a molotov into a smoke never catches; a flash behind a smoke doesn't blind.
        var m = GrenadeMatch(t0, ("t", 1, 0, 0, 0), ("ct", 2, 3000, 3000, 180));
        Check(m.Buy("t", "molotov", t0) is null && m.Buy("ct", "smoke", t0) is null, "Buying a molotov and a smoke");
        m.Tick(live);
        m.BotThrow("t", "molotov", [0, 1000, 180], [0, 0, 0], live);
        Run(m, live + 50, live + 600);
        Check(m.View().Grenades?.SingleOrDefault(g => g.State == "fire") is { Kind: "molotov", Pos: { } fp, Flames.Length: >= 4 } && Math.Abs(fp[1] - 1000) < 1 && Math.Abs(fp[2]) < 0.5, "A molotov catches where it lands, on the floor");
        m.BotThrow("ct", "smoke", [0, 1300, 180], [0, 0, 0], live + 600);
        Run(m, live + 650, live + 2200);
        var after = m.View().Grenades ?? [];
        Check(after.All(g => g.State != "fire") && after.Any(g => g is { State: "blast", Kind: "extinguished" }) && after.Any(g => g.State == "smoke"), "A smoke puts out the fire it covers");
        var fizzle = GrenadeMatch(t0, ("t", 1, 0, 0, 0), ("ct", 2, 3000, 3000, 180));
        fizzle.Buy("t", "smoke", t0); fizzle.Buy("t", "molotov", t0); fizzle.Tick(live);
        fizzle.BotThrow("t", "smoke", [0, 1000, 180], [0, 0, 0], live);
        Run(fizzle, live + 50, live + 1500);
        Check(fizzle.BotThrow("t", "molotov", [0, 1100, 180], [0, 0, 0], live + 1500) is null, "The molotov goes");
        Run(fizzle, live + 1550, live + 2500);
        Check((fizzle.View().Grenades ?? []).All(g => g.State != "fire") && fizzle.View().Grenades!.Any(g => g is { State: "blast", Kind: "extinguished" }), "A fire grenade into a smoke never catches");
        var fire = GrenadeMatch(t0, ("t", 1, 0, 0, 0), ("ct", 2, 3000, 3000, 180));
        fire.Buy("t", "molotov", t0); fire.Tick(live);
        fire.BotThrow("t", "molotov", [3000, 3000, 180], [0, 0, 0], live);
        Run(fire, live + 50, live + 1100);
        var hp = PlayerOf(fire, "ct").Health;
        Check(hp is >= 50 and <= 70, "Fire burns 40 a second in quarter-second ticks, armour or not (" + hp + " after about 1 s)");
        Run(fire, live + 1150, live + 3000);
        var burning = fire.View().Grenades!.Single(g => g.State == "fire");
        var lastFlame = Enumerable.Range(0, burning.Flames!.Length / 4).Max(i => burning.Flames[i * 4 + 3]);
        Check(burning.Ends == burning.At + (long)lastFlame + GrenadeRules.FireMs, "Each flame burns 7 s; the fire lasts until its last flame dies");
        Run(fire, live + 3050, burning.Ends!.Value + 100);
        Check(!PlayerOf(fire, "ct").Alive && (fire.View().Grenades ?? []).All(g => g.State != "fire"), "Standing in it is deadly; once its flames die it's gone");
        var hidden = GrenadeMatch(t0, ("t", 1, 0, 0, 0), ("ct", 2, 2600, 0, 180));
        hidden.Buy("t", "smoke", t0); hidden.Buy("t", "flash", t0); hidden.Tick(live);
        hidden.BotThrow("t", "smoke", [1300, 0, 180], [0, 0, 0], live);
        Run(hidden, live + 50, live + 3000);
        hidden.BotThrow("t", "flash", [400, 0, 180], [0, 0, 0], live + 3000);
        Run(hidden, live + 3050, live + 5000);
        Check(PlayerOf(hidden, "ct").Flash is null && PlayerOf(hidden, "t").Flash is { Amount: 1 }, "A smoke between you and a flash keeps you from being blinded");
    }

    static void GrenadeFilesAndViews()
    {
        var sim = GrenadeFiles.Sim(4, [new GrenadePathRequest(3, "he", [1, 2, 3.04], [10, 0, -5])], [new GrenadeLosRequest(9, [0, 0, 0], [5, 5, 5])]);
        Check(sim == "AIMMOD_GRENADESIM_1\t4\nthrow\t3\the\t1\t2\t3\t10\t0\t-5\nlos\t9\t0\t0\t0\t5\t5\t5\n", "grenade-sim.tsv asks for a throw's path and a line of sight");
        var keys = GrenadePhysics.Simulate([0, 0, 180], [800, 0, 0], GrenadePhysics.Floor(0));
        var text = "AIMMOD_GRENADEPATHS_1\t1000\npath\t3\t" + keys.Count + "\t" + string.Join('\t', GrenadePhysics.Flat(keys).Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "\nlos\t9\t0\nlos\t10\t1\nbad\tline\n";
        var parsed = GrenadeFiles.Paths(text, 1500);
        Check(parsed is { } p && p.Paths[3].Count == keys.Count && Math.Abs(p.Paths[3][^1].X - keys[^1].X) < 0.06 && p.Los[9] == false && p.Los[10] && GrenadeFiles.Paths(text, 100_000) is null
            && GrenadeFiles.Paths("AIMMOD_GRENADEPATHS_1\t1000\npath\t3\t2\t1\t2\n", 1000)!.Value.Paths.Count == 0, "grenade-paths.tsv: paths and answers read back; a stale or broken row says nothing");
        var flying = new CsGrenadeView(5, "flash", "me", "flying", 10_000, 11_500, null, GrenadePhysics.Flat(keys));
        var body = MultiplayerService.GrenadesBody("AimMod Match - X", "he", true, 9_000, [flying, new(6, "smoke", "me", "smoke", 9_000, 27_000, [1, 2, 3]),
            new(7, "molotov", "me", "fire", 9_500, 16_500, [4, 5, 6], Radius: 250), new(8, "he", "me", "blast", 9_900, null, [7, 8, 9])], 1000);
        Check(body.StartsWith("match\tAimMod Match - X\nhand\the\t1\t9000\nfly\t5\tflash\t9000\t10500\t" + keys.Count + "\t0\t0\t0\t180\t", StringComparison.Ordinal)
            && body.Contains("\nsmoke\t6\t1\t2\t3\t8000\t26000\n") && body.Contains("\nfire\t7\tmolotov\t4\t5\t6\t250\t8500\t15500\n") && body.Contains("\nblast\t8\the\t7\t8\t9\t8900\n"),
            "grenades.tsv for AimModCore: the grenade in hand, paths in flight, smokes, fires and blasts in local time");
        // Teammates' tags list their grenades; enemies show nothing.
        var cs = new CsView(1, "live", 0, 0, [0, 0], CsRules.T, 6, true,
            [new("mate", 1, CsRules.T, 800, true, 90, 0, false, false, null, "glock", 0, 0, Holding: "glock", Grenades: ["he", "flash", "flash", "smoke"])], new CsBombView("carried", null, null, null, null, null, null, null, null), null, null, null, [], null);
        Check(MultiplayerService.CsGear(cs, "mate", true) == "w=Glock-18;hp=90;g=he,flash,flash,smoke" && MultiplayerService.CsGear(cs, "mate", false) is null, "Name tags: a teammate's grenades (g=he,flash,flash,smoke)");
        // Sounds: a throw, its bounces, the blast; nothing from before the first view.
        var plan = new GrenadeSoundPlan();
        CsView With(params CsGrenadeView[] g) => cs with { Grenades = g };
        Check(plan.Update("m", With(new CsGrenadeView(1, "he", "x", "blast", 900, null, [0, 0, 0])), "mate", 1000).Count == 0, "Grenade sounds start from the first view");
        var cues = plan.Update("m", With(flying with { Id = 2, At = 1000, Owner = "x" }), "mate", 1100);
        Check(cues.Single() is { Sound: "nade-throw", From: [0, 0, 180] }, "Someone else's throw whooshes from where they threw");
        var bounceAt = 1000 + (long)keys.First(k => k.Impact != GrenadePhysics.NoImpact).T;
        Check(plan.Update("m", With(flying with { Id = 2, At = 1000, Owner = "x" }), "mate", bounceAt + 20).Any(c => c.Sound == "nade-bounce"), "Bounces click as the path reaches them");
        Check(plan.Update("m", With(new CsGrenadeView(3, "he", "x", "blast", bounceAt + 30, null, [5, 5, 5])), "mate", bounceAt + 50).Single() is { Sound: "he-explosion", From: [5, 5, 5] }
            && plan.Update("m", With(new CsGrenadeView(4, "smoke", "x", "smoke", bounceAt + 60, bounceAt + 18_060, [1, 1, 1])), "mate", bounceAt + 100).Single().Sound == "smoke-hiss",
            "The HE booms, the smoke hisses, from where they are");
        var decoyStart = bounceAt + 200;
        var decoy = new CsGrenadeView(9, "decoy", "x", "decoy", decoyStart, decoyStart + GrenadeRules.DecoyMs, [3, 3, 3], null, "rifle");
        var first = GrenadeRules.DecoyShots(9, "rifle")[0];
        plan.Update("m", With(decoy), "mate", decoyStart - 500);
        plan.Update("m", With(decoy), "mate", decoyStart + first - 100, 500);
        Check(plan.Timed.Any(c => c.Cue is { Sound: "gun-rifle", From: [3, 3, 3] } && c.At == decoyStart + first - 500), "A decoy fires its owner's kind of gun: the rifle gunfire other players make, at the shot's own moment");
        Check(GrenadeSounds.All.Keys.All(BombSounds.All.ContainsKey), "Grenade sounds play through the round-sound mixer");
    }

    static void GrenadeBotChecks()
    {
        var me = new CsPlayerView("bot", 1, CsRules.T, 1000, true, 100, 100, true, false, "ak47", "glock", 0, 0);
        Check(BotGrenades.Buys(me, BotSkills.Normal).SequenceEqual(["smoke", "flash", "he"]) && BotGrenades.Buys(me, BotSkills.Easy).SequenceEqual(["flash"])
            && BotGrenades.Buys(me with { Primary = null, Money = 800 }, BotSkills.Hard).Count == 0 && BotGrenades.Buys(me with { Money = 5000 }, BotSkills.Hard).SequenceEqual(["smoke", "flash", "molotov", "he"])
            && BotGrenades.Buys(me with { Grenades = ["flash", "flash", "he", "smoke"] }, BotSkills.Hard).Count == 0, "Bots buy utility with the money left after their guns, within the carry limits");
        // Aim: a lob lands on the spot; a timed throw is there when it goes off.
        var lob = GrenadeAim.Lob([0, 0, 180], [1200, 0, 0]);
        var landed = GrenadePhysics.Simulate([0, 0, 180], lob, GrenadePhysics.Floor(0)).Skip(1).First(k => k.Impact == GrenadePhysics.FloorImpact);
        var timed = GrenadeAim.Timed([0, 0, 180], [800, 300, 500], 1.5);
        var there = GrenadePhysics.At(GrenadePhysics.Simulate([0, 0, 180], timed, (_, _) => null), 1500);
        Check(Math.Abs(landed.X - 1200) < 40 && Math.Abs(landed.Y) < 1 && Math.Sqrt(Math.Pow(there[0] - 800, 2) + Math.Pow(there[1] - 300, 2) + Math.Pow(there[2] - 500, 2)) < 1
            && Math.Sqrt(GrenadeAim.Timed([0, 0, 0], [99_999, 0, 0], 1).Sum(v => v * v)) <= GrenadePhysics.ThrowSpeed + 1e-6, "Bot aim: lobs land on the spot, timed throws arrive at the fuse, never above a full throw");
        // A Terrorist bot near a site smokes it off from the CT side; a CT bot smokes the planted bomb.
        var sites = new[] { new CsSiteView("A", 1400, 0, 0) };
        CsView View(CsPlayerView self, CsBombView bomb) => new(2, "live", 0, 0, [0, 0], CsRules.T, 6, true, [self, new("ct", 2, CsRules.CT, 800, true, 100, 0, false, false, null, "usp", 0, 0)],
            bomb, null, null, null, [], new Dictionary<string, double[]> { ["ct"] = [4000, 0, 180, 0] }, sites);
        var carried = new CsBombView("carried", "bot", null, null, null, null, null, null, null);
        var t = me with { Grenades = ["flash", "smoke"] };
        var world = new BotWorld(100_000, LobbyModes.Cs, [("bot", BotSkills.Normal)], [new BotPlayer("bot", 0, 0, 0, 180, 1, true, 0)], new Dictionary<string, BotSight>(), View(t, carried), null, []);
        var plan = BotGrenades.Plan(world, world.Cs!, t, "bot", new HashSet<string>());
        Check(plan is { Kind: "smoke" } && plan.Target[0] > 1400 && plan.Target[0] < 2300, "A Terrorist bot heading for a site smokes it off towards the defenders' spawn");
        Check(BotGrenades.Plan(world, world.Cs!, t, "bot", new HashSet<string> { "smoke" }) is { Kind: "flash" } f && f.Target[2] > 300, "Then it flashes over the site");
        var ct = new CsPlayerView("bot", 2, CsRules.CT, 1000, true, 100, 0, false, true, null, "usp", 0, 0, Grenades: ["smoke"]);
        var planted = new CsBombView("planted", null, "A", [1200, 0, 0], 200_000, null, null, null, null);
        var ctWorld = world with { Cs = View(ct, planted) };
        Check(BotGrenades.Plan(ctWorld, ctWorld.Cs!, ct, "bot", new HashSet<string>()) is { Kind: "smoke" } s && s.Target[0] == 1200, "A CT bot smokes the planted bomb to defuse under it");
        var early = new BotGrenades().Step(world with { Cs = world.Cs! with { Phase = "freeze", PhaseEndsAt = world.Now + CsRules.FreezeMs - 1000 } });
        var later = new BotGrenades().Step(world with { Cs = world.Cs! with { Phase = "freeze", PhaseEndsAt = world.Now + 5000 } });
        var go = new BotGrenades().Step(world);
        Check(early.Buys.Count == 0 && later.Buys.SequenceEqual([("bot", "flash"), ("bot", "he")]) && later.Throws.Count == 0 && go.Throws.Single() is { Kind: "smoke", Bot: "bot" },
            "Bots buy in freeze time after a moment, and throw once the round is live");
    }

    // Build 50: your own flashbang never blinded you. The host asks the game for a line of sight from
    // the pop to everyone in reach, the thrower too; the game's line traces stopped on the thrower's own
    // hitboxes (KovaaK's characters carry hitbox components that block traces), so the thrower read
    // "behind a wall". AimModSteam now leaves every pawn out of grenade traces; with the line clear,
    // looking at your own flash blinds you fully and looking away barely.
    static void GrenadeOwnFlashChecks()
    {
        const long t0 = 60_000_000; var live = t0 + CsRules.FreezeMs;
        (CsFlashView? Me, CsFlashView? Mate, IReadOnlyList<GrenadeLosRequest> Asked, List<string> Log) Pop(double yaw)
        {
            var m = GrenadeMatch(t0, ("me", 1, 0, 0, yaw), ("mate", 1, 0, 300, yaw), ("foe", 2, 4000, 3000, 0));
            var log = new List<string>();
            m.Grenades.Trace = log.Add;
            m.Buy("me", "flash", t0); m.Tick(live);
            // An underhand throw a little down the floor ahead (the view the throw was made with; the track
            // then says where they look when it pops).
            Check(m.Throw("me", "flash", 0, [0, 0, 180], -20, 0, live) is null, "The thrower throws their own flash");
            Run(m, live + 50, live + 1650);
            var asked = m.Grenades.LosRequests;
            foreach (var los in asked) m.Grenades.AnswerLos(los.Tag, true); // the game: nothing between the pop and their eyes
            Run(m, live + 1700, live + 1750);
            return (PlayerOf(m, "me").Flash, PlayerOf(m, "mate").Flash, asked, log);
        }
        var facing = Pop(0);
        Check(facing.Asked.Any(l => l.To.SequenceEqual(new double[] { 0, 0, 180 })) && facing.Asked.Any(l => l.To.SequenceEqual(new double[] { 0, 300, 180 })),
            "The host asks the game for the line from the pop to the thrower's own eye, and to their teammate's");
        Check(facing.Me is { Amount: >= 0.95 } me && GrenadeRules.FlashPeak(me) == 1 && me.HoldMs >= 2000 && facing.Mate is { Amount: >= 0.9 } && facing.Log.Single().Contains("me blinded 1.00", StringComparison.Ordinal)
            && facing.Log[0].Contains("sight from the game", StringComparison.Ordinal),
            "A thrower looking at their own flash is fully blinded (white " + GrenadeRules.FlashPeak(facing.Me!) + " for " + facing.Me!.HoldMs + " ms), and so is a teammate beside them");
        var away = Pop(180);
        Check(away.Me is { Amount: <= 0.15 } a && GrenadeRules.FlashPeak(a) <= 0.25 && a.HoldMs < 100 && GrenadeRules.FlashAlpha(a, a.At + 600) < 0.05,
            "Looking away from your own flash you're barely touched (white " + (away.Me is { } x ? GrenadeRules.FlashPeak(x) : 0) + ", gone within half a second)");
        // A wall between (the game's answer): nobody is blinded, the thrower included.
        var walled = GrenadeMatch(t0, ("me", 1, 0, 0, 0), ("foe", 2, 600, 0, 180));
        walled.Buy("me", "flash", t0); walled.Tick(live);
        walled.Throw("me", "flash", 0, [0, 0, 180], -20, 0, live);
        Run(walled, live + 50, live + 1650);
        foreach (var los in walled.Grenades.LosRequests) walled.Grenades.AnswerLos(los.Tag, false);
        Run(walled, live + 1700, live + 1750);
        Check(PlayerOf(walled, "me").Flash is null && PlayerOf(walled, "foe").Flash is null, "Behind a wall (the game's trace hits it) a flash does nothing, to anyone");
    }

    static void GrenadeThrowChecks()
    {
        // Strength levels, the underhand drop and the thrower's own velocity (CS: 1.25 times it).
        var full = GrenadePhysics.ThrowVelocity(0, 0, 1);
        var run = GrenadePhysics.ThrowVelocity(0, 0, 1, [1000, 0, 0]); var jump = GrenadePhysics.ThrowVelocity(0, 0, 1, [0, 0, 400]); var wild = GrenadePhysics.ThrowVelocity(0, 0, 1, [1e6, 0, 0]);
        Check(Math.Abs(run[0] - full[0] - 1250) < 1e-6 && run[2] == full[2] && Math.Abs(jump[2] - full[2] - 500) < 1e-6 && Math.Abs(wild[0] - full[0] - 1.25 * GrenadePhysics.MaxInheritCm) < 1e-6
            && GrenadePhysics.ThrowVelocity(0, 0, 1, [double.NaN, 0, 0]).SequenceEqual(full), "A throw carries 1.25 times the thrower's run or jump (CS), at most 15 m/s of it");
        Check(GrenadePhysics.ThrowOrigin([0, 0, 180], 0)[2] == 180 - 12 * GrenadePhysics.Unit && GrenadePhysics.ThrowOrigin([0, 0, 180], 1)[2] == 180 && Math.Abs(GrenadePhysics.ThrowOrigin([0, 0, 180], 0.5)[2] - (180 - 6 * GrenadePhysics.Unit)) < 1e-9,
            "Underhand leaves the hand 12 units lower than a full throw (CS)");
        double Rest(double[] v, double z = 180) => GrenadePhysics.Simulate([0, 0, z], v, GrenadePhysics.Floor(0))[^1].X;
        var (under, medium, fullX, runX) = (Rest(GrenadePhysics.ThrowVelocity(0, 0, 0)), Rest(GrenadePhysics.ThrowVelocity(0, 0, 0.5)), Rest(full), Rest(run));
        Check(under < medium && medium < fullX && runX > fullX + 500, FormattableString.Invariant($"Throws go further with strength and on the run: underhand {under / 100:0} m, medium {medium / 100:0} m, full {fullX / 100:0} m, running {runX / 100:0} m"));
        // Bounces keep 45 % of the speed; a slide slows evenly to a stop.
        var keys = GrenadePhysics.Simulate([0, 0, 180], full, GrenadePhysics.Floor(0));
        var hit = keys.FindIndex(1, k => k.Impact == GrenadePhysics.FloorImpact);
        var p = keys[hit - 1]; var tau = (keys[hit].T - p.T) / 1000;
        var before = Math.Sqrt(p.Vx * p.Vx + p.Vy * p.Vy + Math.Pow(p.Vz - GrenadePhysics.Gravity * tau, 2)); var after = Math.Sqrt(keys[hit].Vx * keys[hit].Vx + keys[hit].Vy * keys[hit].Vy + keys[hit].Vz * keys[hit].Vz);
        var slide = keys.FindIndex(k => k.Motion == GrenadePhysics.Slide);
        Check(Math.Abs(after / before - GrenadePhysics.Elasticity) < 0.01 && slide > 0 && keys[slide + 1] is { Motion: GrenadePhysics.Rest } stop
            && Math.Abs(stop.T - keys[slide].T - Math.Sqrt(keys[slide].Vx * keys[slide].Vx + keys[slide].Vy * keys[slide].Vy) / GrenadePhysics.SlideDecel * 1000) < 1,
            "A floor bounce keeps 45 % of the speed; the roll slows evenly and stops");
        // The host: a throw on the run carries the thrower's velocity from their own track.
        const long t0 = 65_000_000; var live = t0 + CsRules.FreezeMs;
        var m = new CsMatch(["runner", "foe"], t0, 6, true, null, new Dictionary<string, int> { ["runner"] = 1, ["foe"] = 2 });
        var track = new List<TrackSample>();
        for (var t = live - 2000; t <= live; t += 25) track.Add(new TrackSample(t, (t - (live - 2000)) * 0.6, 0, 180, 0, 0));
        for (var i = 0; i < track.Count; i += 60) m.Combat.Track("runner", new TrackBatch("m", 1, track.Skip(i).Take(60).ToList(), []));
        m.Combat.Track("foe", new TrackBatch("m", 1, [new TrackSample(live, 5000, 5000, 180, 0, 0)], []));
        m.Buy("runner", "smoke", t0); m.Tick(live);
        Check(m.Throw("runner", "smoke", 1, [1200, 0, 180], 0, 0, live) is null && m.Grenades.PathRequests.Single() is { } req && Math.Abs(req.Velocity[0] - full[0] - 1.25 * 600) < 2 && Math.Abs(req.Velocity[1]) < 1,
            "The host adds the thrower's run (600 cm/s from their track) to the throw");
        // Pin and release (CS): draw first, the pin takes 0.35 s, the grenade leaves 0.1 s after you let go.
        var tr = new GrenadeTrigger();
        Check(tr.Step(1100, 1000, false, false, true, false) is null && !tr.Pulled, "A click left over from the gun (the grenade still being drawn) does nothing");
        Check(tr.Step(2000, 1000, true, false, false, false) is null && tr.Pulled && tr.TakePinSound() && !tr.TakePinSound(), "Holding fire pulls the pin (its sound once)");
        Check(tr.Step(2100, 1000, false, false, false, false) is null && tr.Released && tr.Step(2449, 1000, false, false, false, false) is null && tr.Step(2450, 1000, false, false, false, false) == 1 && !tr.Pulled,
            "Let go before the pin is out, the throw goes once it is, 0.1 s later (full strength)");
        tr.Step(3000, 1000, true, true, false, false);
        Check(tr.Step(4000, 1000, true, true, false, false) is null && MultiplayerService.GrenadeHint(true, tr) == "Medium throw · let go to throw" && tr.Step(4100, 1000, false, false, false, false) is null
            && MultiplayerService.GrenadeHint(true, tr) == "Medium throw…" && tr.Step(4200, 1000, false, false, false, false) == 0.5, "Both buttons held: a medium throw, 0.1 s after you let go");
        var under2 = new GrenadeTrigger();
        under2.Step(5000, 1000, false, true, false, false);
        Check(under2.Step(6000, 1000, false, false, false, false) is null && under2.Step(6100, 1000, false, false, false, false) == 0, "Right mouse alone: underhand");
        var tap = new GrenadeTrigger();
        Check(tap.Step(7000, 1000, false, false, true, false) is null && tap.Pulled && tap.Released && tap.Step(7449, 1000, false, false, false, false) is null && tap.Step(7450, 1000, false, false, false, false) == 1,
            "A quick tap throws as soon as the pin is out (0.45 s)");
        Check(MultiplayerService.GrenadeHint(false, new GrenadeTrigger()) is null && MultiplayerService.GrenadeHint(true, new GrenadeTrigger()) == "Fire: throw · Right: underhand · Both: medium",
            "The HUD says how to throw while a grenade is in hand, and which throw you hold");
    }

    // The fire's flames on the floor: ring by ring from where it caught, tested against the game's
    // answers (a wall, a step down, a ledge up) and against a smoke.
    static List<double[]> FlamesOf(CsGrenadeView fire) => Enumerable.Range(0, (fire.Flames?.Length ?? 0) / 4).Select(i => fire.Flames!.Skip(i * 4).Take(4).ToArray()).ToList();
    static void GrenadeFireSpreadChecks()
    {
        const long t0 = 70_000_000; var live = t0 + CsRules.FreezeMs;
        double Flat(double[] a, double[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]));
        // Open level floor (no answers from the game: level, no walls).
        var open = GrenadeMatch(t0, ("t", 1, 0, 0, 0), ("ct", 2, 6000, 6000, 180));
        open.Buy("t", "molotov", t0); open.Buy("ct", "smoke", t0); open.Tick(live);
        open.BotThrow("t", "molotov", [2000, 0, 180], [0, 0, 0], live);
        Run(open, live + 50, live + 3000);
        var fire = open.View().Grenades!.Single(g => g.State == "fire");
        var flames = FlamesOf(fire);
        var closest = flames.SelectMany((a, i) => flames.Skip(i + 1).Select(b => Flat(a, b))).Min();
        Check(flames.Count == GrenadeRules.MaxFlames && flames.All(f => Flat(f, fire.Pos!) <= GrenadeRules.FireRangeCm + 1 && Math.Abs(f[2]) < 0.5) && closest >= GrenadeRules.FlameSpacingCm * 0.7
            && flames[0][3] == 0 && flames.Select(f => f[3]).Distinct().Count() >= 2 && fire.Radius <= GrenadeRules.FireRangeCm + GrenadeRules.FlameRadiusCm && fire.Radius > GrenadeRules.FlameSpacingCm,
            FormattableString.Invariant($"A fire spreads over the floor: {flames.Count} flames 42 units apart, ring by ring, within 150 units ({fire.Radius / 100:0.0} m)"));
        Check(GrenadeRules.Burning(fire, fire.At + 1).Count() == 1 && GrenadeRules.Burning(fire, fire.At + (long)flames.Max(f => f[3])).Count() == GrenadeRules.MaxFlames
            && !GrenadeRules.Burning(fire, fire.Ends!.Value).Any(), "Its flames light ring by ring and each burns 7 s");
        // Standing in an outer flame burns; just beyond the fire doesn't.
        var edge = flames.OrderByDescending(f => Flat(f, fire.Pos!)).First();
        Check(GrenadeIntel.InFire(open.View(), [edge[0], edge[1], 0], live + 2900) && !GrenadeIntel.InFire(open.View(), [fire.Pos![0] + GrenadeRules.FireRangeCm + 300, 0, 0], live + 2900),
            "An outer flame burns as much as the middle; beyond the fire nothing does");
        // A smoke at its edge puts out the flames it covers; the rest burn on.
        open.BotThrow("ct", "smoke", [2900, 0, 180], [0, 0, 0], live + 3000);
        Run(open, live + 3050, live + 4500);
        var left = open.View().Grenades!.SingleOrDefault(g => g.State == "fire");
        var smoke = open.View().Grenades!.Single(g => g.State == "smoke");
        Check(left is not null && FlamesOf(left).Count is > 0 and < GrenadeRules.MaxFlames && FlamesOf(left).All(f => !GrenadeRules.Extinguishes(smoke.Pos!, f))
            && open.View().Grenades!.Any(g => g is { State: "blast", Kind: "extinguished" }), "A smoke puts out the flames it covers; the rest of the fire burns on");

        // The game's answers: a wall east (x 2100), a step down 60 cm north (y > 150), a ledge 140 cm up south (y < -150).
        var room = GrenadeMatch(t0, ("t", 1, 0, 0, 0), ("ct", 2, 6000, 6000, 180));
        room.Buy("t", "molotov", t0); room.Tick(live);
        room.BotThrow("t", "molotov", [2000, 0, 180], [0, 0, 0], live);
        for (var t = live + 50; t <= live + 3000; t += 50)
        {
            foreach (var los in room.Grenades.LosRequests) room.Grenades.AnswerLos(los.Tag, (los.From[0] - 2100) * (los.To[0] - 2100) > 0 && los.To[0] < 2100);
            foreach (var f in room.Grenades.FloorRequests) room.Grenades.AnswerFloor(f.Tag, f.Y > 150 ? -60 : f.Y < -150 ? 140 : 0);
            room.Tick(t);
        }
        var shaped = FlamesOf(room.View().Grenades!.Single(g => g.State == "fire"));
        Check(shaped.All(f => f[0] < 2100) && shaped.Any(f => f[2] == -60) && shaped.All(f => f[2] != 140) && shaped.All(f => f[2] == 0 || f[1] > 150),
            "The flames follow the floor the game traces: never through a wall, down a step, not up a ledge (" + shaped.Count + " flames)");
    }

    static void GrenadeDropChecks()
    {
        Check(GrenadeRules.DropOnDeath(["he", "flash", "smoke"], null) == "he" && GrenadeRules.DropOnDeath(["flash", "molotov"], null) == "molotov" && GrenadeRules.DropOnDeath(["he", "flash"], "flash") == "flash"
            && GrenadeRules.DropOnDeath(["he"], "smoke") == "he" && GrenadeRules.DropOnDeath([], null) is null, "Going down drops the grenade in your hand (CS), else your most valuable one");
        const long t0 = 80_000_000; var live = t0 + CsRules.FreezeMs;
        var m = GrenadeMatch(t0, ("a", 1, 0, 0, 0), ("b", 2, 50, 0, 180), ("c", 1, 3000, 0, 0));
        m.Buy("a", "he", t0); m.Buy("a", "flash", t0); m.Buy("a", "smoke", t0);
        Check(m.Hold("a", CsRules.GrenadeSlot, "flash") is null && PlayerOf(m, "a").HeldGrenade == "flash" && m.Hold("a", CsRules.GrenadeSlot, "molotov") is null && PlayerOf(m, "a").HeldGrenade is null
            && m.Hold("a", CsRules.GrenadeSlot, "flash") is null, "The host knows which grenade a player holds (only one they carry)");
        m.Tick(live);
        m.Combat.AreaHit("b", "a", 200, CsRules.Bomb.Combat, live + 100, null);
        var dropped = m.View().Grenades?.SingleOrDefault(g => g.State == "dropped");
        Check(!PlayerOf(m, "a").Alive && dropped is { Kind: "flash", Owner: "a", Pos: [0, 0, var dz] } && Math.Abs(dz - GrenadePhysics.Lift) < 0.01 && m.Grenades.FloorRequests.Count == 1,
            "Going down with a flash in hand drops it on the floor under you (until the game says where the floor is)");
        m.Grenades.AnswerFloor(m.Grenades.FloorRequests.Single().Tag, -20);
        Check(m.View().Grenades!.Single(g => g.State == "dropped").Pos![2] == -20 + GrenadePhysics.Lift
            && MultiplayerService.GrenadesBody("AimMod Match - X", null, false, 0, m.View().Grenades!, 0).Contains("\ndropped\t" + dropped!.Id + "\tflash\t0\t0\t-18\n", StringComparison.Ordinal),
            "It lies on the floor the game traced; AimModCore draws it there");
        var pickupSounds = new RoundSoundPlan();
        pickupSounds.Update("m", m.View(), "b");
        Run(m, live + 150, live + 300);
        Check(pickupSounds.Update("m", m.View(), "b").Any(c => c.Sound == "picked"), "Picking one up clicks");
        Check(PlayerOf(m, "b").Grenades is ["flash"] && !(m.View().Grenades ?? []).Any(g => g.State == "dropped") && m.View().Events.Any(e => e is { Kind: "grenade-picked", Member: "b", Text: "flash" }),
            "Anyone walking over it picks it up, an enemy too");
        // Someone who can't carry it walks past it.
        var full = GrenadeMatch(t0, ("a", 1, 0, 0, 0), ("b", 2, 50, 0, 180), ("c", 1, 3000, 0, 0));
        full.Buy("a", "he", t0); full.Buy("b", "he", t0); full.Tick(live);
        full.Combat.AreaHit("b", "a", 200, CsRules.Bomb.Combat, live + 100, null);
        Run(full, live + 150, live + 300);
        Check(PlayerOf(full, "b").Grenades is ["he"] && full.View().Grenades!.Count(g => g is { State: "dropped", Kind: "he" }) == 1, "One you can't carry (you already have an HE) stays on the floor");
        // A new round: nothing lies around.
        full.Grenades.Clear();
        Check(full.View().Grenades is null || !full.View().Grenades!.Any(g => g.State == "dropped"), "Dropped grenades are gone with the round");
    }

    static void GrenadeDecoyAndSoundChecks()
    {
        // The decoy's shots: bursts at its owner's gun's fire rate.
        static List<List<long>> Bursts(IReadOnlyList<long> shots, long within)
        {
            var bursts = new List<List<long>>();
            foreach (var s in shots) { if (bursts.Count == 0 || s - bursts[^1][^1] > within) bursts.Add([]); bursts[^1].Add(s); }
            return bursts;
        }
        var ak = GrenadeRules.DecoyShots(11, "rifle", GrenadeRules.DecoyMs, "ak47");
        var akGap = (long)Math.Round(CsRules.Find("ak47")!.Combat.TimeBetweenShots * 1000);
        var akBursts = Bursts(ak, akGap + 5);
        Check(akBursts.SkipLast(1).All(b => b.Count is >= 3 and <= 7) && akBursts.All(b => b.Count <= 7 && b.Zip(b.Skip(1)).All(p => p.Second - p.First == akGap)) && akBursts.Zip(akBursts.Skip(1)).All(p => p.Second[0] - p.First[^1] >= 600 + akGap)
            && ak.All(t => t is >= 400 and < GrenadeRules.DecoyMs - 300) && akBursts.Count >= 4,
            "A decoy with an AK-47: bursts of 3-7 shots " + akGap + " ms apart (its fire rate), 0.6-2.6 s between bursts, over its 15 s");
        var deagle = CsRules.Weapons.FirstOrDefault(w => w.Class == "pistol" && !w.Combat.FullyAuto);
        var taps = GrenadeRules.DecoyShots(12, "pistol", GrenadeRules.DecoyMs, deagle?.Id);
        var awp = GrenadeRules.DecoyShots(13, "sniper", GrenadeRules.DecoyMs, "awp");
        Check(taps.Zip(taps.Skip(1)).All(p => p.Second - p.First >= 220) && Bursts(taps, 400).All(b => b.Count <= 3) && awp.Zip(awp.Skip(1)).All(p => p.Second - p.First >= 1460 + 600) && awp.Count is >= 3 and < 10,
            "A pistol taps 1-3 shots, an AWP fires single shots at its own pace");
        // The match: a decoy takes its owner's gun.
        const long t0 = 90_000_000; var live = t0 + CsRules.FreezeMs;
        var m = GrenadeMatch(t0, ("a", 1, 0, 0, 0), ("b", 2, 4000, 4000, 180));
        m.Buy("a", "decoy", t0); m.Tick(live);
        m.BotThrow("a", "decoy", [600, 0, 180], [0, 0, 0], live);
        Check(m.View().Grenades!.Single() is { State: "flying", Weapon: "pistol", Gun: "glock" }, "A decoy carries its owner's gun (their Glock-18, a pistol)");
        Run(m, live + 50, live + 1500);
        var decoy = m.View().Grenades!.Single(g => g.State == "decoy");
        Check(decoy is { Weapon: "pistol", Gun: "glock", Pos: { } } && decoy.Ends == decoy.At + GrenadeRules.DecoyMs && (decoy.At - live) % GrenadeRules.ThinkMs == 0 && decoy.At - live < 1500,
            "It starts once it lies still and fires for 15 s");
        // Every shot plays once, as the rifle gunfire other players make, at its own moment, however the updates fall.
        var plan = new GrenadeSoundPlan();
        var cs = m.View();
        plan.Update("m", cs, "b", decoy.At - 10, 300);
        var played = new List<TimedCue>();
        for (var t = decoy.At; t < decoy.Ends!.Value + 500; t += 250) { plan.Update("m", cs, "b", t, 300); played.AddRange(plan.Timed); }
        var shots = GrenadeRules.DecoyShots(decoy.Id, decoy.Weapon, GrenadeRules.DecoyMs, decoy.Gun);
        Check(played.Count == shots.Count && played.Select(c => c.At).SequenceEqual(shots.Select(s => decoy.At + s - 300)) && played.All(c => c.Cue is { Sound: "gun-pistol", Gain: GrenadeSoundPlan.DecoyShotGain } && c.Cue.From!.SequenceEqual(decoy.Pos!)),
            "A decoy's shots sound like the owner's gun (" + played.Count + " shots of gun-pistol), each at its own moment so its fire rate holds");
        Run(m, live + 1550, decoy.Ends!.Value + 50);
        var pop = m.View().Grenades!.SingleOrDefault(g => g.State == "blast" && g.Kind == "decoy");
        Check(pop is not null && plan.Update("m", m.View(), "b", decoy.Ends.Value + 60).Any(c => c is { Sound: "decoy-pop", Gain: GrenadeSoundPlan.DecoyPopGain }), "At the end a small pop");
        // Volumes: the HE loudest, the flash's pop next, the pin, throw and bounces small and quiet, the hiss and the fire between.
        Check(GrenadeSoundPlan.HeGain > GrenadeSoundPlan.FlashGain && GrenadeSoundPlan.FlashGain > GrenadeSoundPlan.IgniteGain && GrenadeSoundPlan.IgniteGain > GrenadeSoundPlan.SmokeGain
            && GrenadeSoundPlan.SmokeGain > GrenadeSoundPlan.BounceGain && GrenadeSoundPlan.BounceGain >= GrenadeSoundPlan.ThrowGain && GrenadeSoundPlan.PinGain < GrenadeSoundPlan.SmokeGain
            && GrenadeSoundPlan.CrackleGain < GrenadeSoundPlan.IgniteGain && GrenadeSoundPlan.DecoyShotGain == 0.9, "Grenade volumes: the HE loudest, then the flash, fire and smoke, the small metal sounds quietest; a decoy as loud as gunfire");
        Check(!GrenadeSounds.All.Keys.Any(k => k.StartsWith("decoy-", StringComparison.Ordinal) && k != "decoy-pop") && GunSounds.All.ContainsKey("gun-rifle"), "A decoy has no gunfire of its own: it plays the real thing");
    }

    // What the bot logic can read (GrenadeIntel): blindness over time, smokes, fires, sounds, dropped grenades.
    static void GrenadeIntelChecks()
    {
        var flash = GrenadeRules.FlashFor(1000, 1)!;
        var keys = GrenadePhysics.Simulate([0, 0, 180], GrenadePhysics.ThrowVelocity(0, 0, 1), GrenadePhysics.Floor(0));
        CsGrenadeView[] grenades =
        [
            new(1, "smoke", "x", "smoke", 2000, 2000 + GrenadeRules.SmokeMs, [500, 0, 0], Radius: GrenadeRules.SmokeRadiusCm),
            new(2, "molotov", "x", "fire", 3000, 3000 + 160 + GrenadeRules.FireMs, [0, 800, 0], Radius: 300, Flames: [0, 800, 0, 0, 185, 800, 0, 160]),
            new(3, "decoy", "x", "decoy", 4000, 4000 + GrenadeRules.DecoyMs, [100, 100, 0], Weapon: "rifle", Gun: "ak47"),
            new(4, "flash", "x", "flying", 5000, 6600, Keys: GrenadePhysics.Flat(keys)),
            new(5, "he", "x", "dropped", 5500, Pos: [9, 9, 2]),
            new(6, "he", "x", "blast", 5600, Pos: [1, 2, 3]),
        ];
        var cs = new CsView(1, "live", 0, 0, [0, 0], CsRules.T, 6, true, [new("me", 1, CsRules.T, 800, true, 100, 0, false, false, null, "glock", 0, 0, Flash: flash)],
            new CsBombView("carried", null, null, null, null, null, null, null, null), null, null, null, [], null, null, grenades);
        var until = GrenadeIntel.BlindUntil(cs, "me")!.Value;
        Check(GrenadeIntel.Blind(cs, "me", 2000) == 1 && Math.Abs(GrenadeRules.FlashAlpha(flash, until) - 0.6) < 0.02 && GrenadeIntel.Blind(cs, "me", until + 400) < 0.6 && GrenadeIntel.BlindUntil(cs, "nobody") is null,
            "Bots read how white a player's screen is over time and until when they can't see (" + (until - 1000) + " ms after the pop)");
        var smokes = GrenadeIntel.Smokes(cs, 2500);
        Check(smokes.Single() is { Scale: 0.5, Radius: GrenadeRules.SmokeRadiusCm, Centre: [500, 0, _] } && GrenadeIntel.Smokes(cs, 5000).Single().Scale == 1 && GrenadeIntel.Smokes(cs, 2000 + GrenadeRules.SmokeMs).Count == 0
            && GrenadeIntel.SmokeBlocks(cs, [-500, 0, 200], [1500, 0, 200], 5000), "Smokes over time: the cloud, its size now, what it hides");
        Check(GrenadeIntel.Fires(cs, 3100).Single().Flames.Count == 1 && GrenadeIntel.Fires(cs, 3200).Single().Flames.Count == 2 && GrenadeIntel.InFire(cs, [185, 800, 0], 3200) && !GrenadeIntel.InFire(cs, [185, 800, 0], 3100)
            && GrenadeIntel.Fires(cs, 3000 + 160 + GrenadeRules.FireMs).Count == 0, "Fires: the flames burning now, where it's hot");
        var sounds = GrenadeIntel.Sounds(cs, 4000, 4000 + GrenadeRules.DecoyMs);
        var gunfire = sounds.Where(s => s.Kind == "gunfire").ToList();
        Check(gunfire.Count == GrenadeRules.DecoyShots(3, "rifle", GrenadeRules.DecoyMs, "ak47").Count && gunfire.All(s => s is { Decoy: true, Weapon: "ak47", Grenade: 3 })
            && sounds.Any(s => s.Kind == "bounce" && s.Grenade == 4) && sounds.Any(s => s is { Kind: "he", T: 5600 }) && sounds.Zip(sounds.Skip(1)).All(p => p.First.T <= p.Second.T),
            "Grenade sounds for bots: a decoy's shots tagged as a decoy (players hear gunfire), bounces, blasts, in time order");
        Check(GrenadeIntel.Dropped(cs).Single() is { Id: 5, Kind: "he" }, "Bots see grenades lying on the floor");
    }

    // The new rows between the host and its game (floors), the flames and dropped grenades for
    // AimModCore, and the same path on host and client.
    static void GrenadeWireChecks()
    {
        var sim = GrenadeFiles.Sim(5, [], [], [new GrenadeFloorRequest(12, 100, 200.04, 260, -150)]);
        Check(sim == "AIMMOD_GRENADESIM_1\t5\nfloor\t12\t100\t200\t260\t-150\n", "grenade-sim.tsv asks for the floor under a spot (a spreading flame, a dropped grenade)");
        var answers = GrenadeFiles.Paths("AIMMOD_GRENADEPATHS_1\t1000\nfloor\t12\t-60.5\nfloor\t13\t-\nfloor\t14\tx\n", 1000);
        Check(answers is { } a && a.Floors[12] == -60.5 && a.Floors.ContainsKey(13) && a.Floors[13] is null && !a.Floors.ContainsKey(14), "grenade-paths.tsv: a floor's height, or none");
        var fire = new CsGrenadeView(7, "incendiary", "me", "fire", 10_000, 17_160, [4, 5, 6], Radius: 300, Flames: [4, 5, 6, 0, 189, 5, -54, 160]);
        var body = MultiplayerService.GrenadesBody("AimMod Match - X", null, false, 0, [fire], 1000);
        Check(body.Contains("\nfire\t7\tincendiary\t4\t5\t6\t300\t9000\t16160\nflame\t7\t4\t5\t6\t110\t9000\t16000\nflame\t7\t189\t5\t-54\t110\t9160\t16160\n", StringComparison.Ordinal),
            "grenades.tsv: each flame of a fire on the floor, with its own start and end in local time");
        // Host and client draw the same path: the broadcast keys (1 decimal) and grenades.tsv put the
        // grenade within a centimetre of the host's own path all the way.
        var keys = GrenadePhysics.Simulate([0, 0, 180], GrenadePhysics.ThrowVelocity(10, 30, 1, [300, 120, 200]), Room);
        var client = GrenadePhysics.Unflat(GrenadePhysics.Flat(keys));
        var line = MultiplayerService.GrenadesBody("A", null, false, 0, [new CsGrenadeView(1, "he", "me", "flying", 5000, 6600, null, GrenadePhysics.Flat(keys))], 0).Split('\n').First(l => l.StartsWith("fly\t", StringComparison.Ordinal)).Split('\t');
        var drawn = new List<GrenadePhysics.Key>();
        for (var i = 6; i + 7 < line.Length; i += 8)
        {
            double V(int k) => double.Parse(line[i + k], System.Globalization.CultureInfo.InvariantCulture);
            drawn.Add(new GrenadePhysics.Key(V(0), V(1), V(2), V(3), V(4), V(5), V(6), (int)V(7), 0));
        }
        var worst = 0.0;
        for (var t = 0.0; t <= keys[^1].T + 500; t += 10)
        {
            var h = GrenadePhysics.At(keys, t);
            foreach (var other in new[] { GrenadePhysics.At(client, t), GrenadePhysics.At(drawn, t) })
                worst = Math.Max(worst, Math.Sqrt(Math.Pow(h[0] - other[0], 2) + Math.Pow(h[1] - other[1], 2) + Math.Pow(h[2] - other[2], 2)));
        }
        Check(drawn.Count == keys.Count && worst < 1, FormattableString.Invariant($"Host and clients draw the same path (at most {worst:0.00} cm apart over {keys[^1].T / 1000:0.0} s)"));
    }
}

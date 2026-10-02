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
        Check(he.T == 1500 && he.Works && Math.Abs(he.At[0] - GrenadePhysics.At(flat, 1500)[0]) < 1e-9 && smoke.T == Math.Max(flat[^1].T, 1000) + 250 && smoke.At[0] == flat[^1].X
            && molotov.Works && molotov.T == firstFloor.T && molotov.At[0] == firstFloor.X, "HE and flash go off at 1.5 s, a smoke once it rests, a molotov on landing");
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
        Check(m.View().Grenades?.Single() is { State: "flying", Ends: { } off } && off == live + 1500, "Without an answer from the game the level-floor fallback flies it; it goes off 1.5 s after the throw");
        Run(m, live + 450, live + 1500);
        var blast = m.View().Grenades?.SingleOrDefault(g => g.State == "blast");
        Check(blast is { Kind: "he", Pos: { } bp } && Math.Abs(bp[0] - 600) < 1 && m.Grenades.LosRequests.Count == 8, "The HE explodes where it lay and asks the game for line of sight to everyone in reach");
        Run(m, live + 1550, live + 1900);
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
        Run(wall, live + 50, live + 1500);
        foreach (var los in wall.Grenades.LosRequests) wall.Grenades.AnswerLos(los.Tag, Math.Abs(los.To[0]) < 1); // a sees it, b doesn't
        Run(wall, live + 1550, live + 1600);
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
        Check(m.View().Grenades?.SingleOrDefault(g => g.State == "fire") is { Kind: "molotov", Pos: { } fp, Radius: GrenadeRules.FireRadiusCm } && Math.Abs(fp[1] - 1000) < 1, "A molotov catches where it lands");
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
        Run(fire, live + 1150, live + GrenadeRules.FireMs + 800);
        Check(!PlayerOf(fire, "ct").Alive && (fire.View().Grenades ?? []).All(g => g.State != "fire"), "Standing in it is deadly; after 7 s it's gone");
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
        plan.Update("m", With(decoy), "mate", decoyStart);
        Check(plan.Update("m", With(decoy), "mate", decoyStart + first + 10).Any(c => c is { Sound: "decoy-rifle", From: [3, 3, 3] }), "A decoy fires its owner's kind of gun");
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
}

namespace AimMod.InGame.Multiplayer;

// CS weapon feel (game-modes.md 6.6.6): inaccuracy by movement per weapon, the seeds the host
// reproduces (the same numbers as AimModCore's CsFeelTests.inl), the host accepting spread shots on
// the bullet's own ray and refusing impossible ones, the speeds, the shot feed and the settings.
static partial class MultiplayerChecks
{
    static void CsFeelChecks()
    {
        // The table matches the CS items: every weapon, the knife, grenades and the bomb.
        Check(CsRules.Profiles.All(w => CsFeel.ByProfile(w.Combat.Name) is { } f && f.Id == w.Id) && CsFeel.All.Length == 11 && CsFeel.ByProfile("AimMod CS Banana") is null,
            "Every CS item has its weapon feel, found by its KovaaK's profile");

        // Spread by speed, per weapon.
        var ak = CsFeel.ById("ak47")!; var awp = CsFeel.ById("awp")!; var glock = CsFeel.ById("glock")!; var mp9 = CsFeel.ById("mp9")!;
        double Cone(CsFeelSpec w, double speed, bool air = false, bool crouch = false, double blend = 0) => CsFeel.ConeNow(w, speed, air, crouch, blend);
        Check(Cone(ak, 0, crouch: true) < Cone(ak, 0) && Cone(ak, 0) < 0.01 && Cone(ak, ak.MaxSpeed * CsFeel.AccurateShare) == Cone(ak, 0) && Cone(ak, ak.MaxSpeed * 0.52) > Cone(ak, 0)
            && Cone(ak, ak.MaxSpeed) > 0.12 && Cone(ak, 0, air: true) > 0.3, "The AK: crouched beats standing, walking costs some, running and jumping a lot");
        Check(Cone(glock, 240) * 4 < Cone(ak, 215) && Cone(mp9, 240) * 3 < Cone(ak, 215), "Running: pistols and SMGs are forgiving, rifles are not");
        Check(Cone(awp, 0) > 0.05 && Cone(awp, 0, blend: 1) < 0.005 && Cone(awp, awp.ScopedSpeed, blend: 1) > 0.1, "The AWP: wide unscoped, a laser scoped and still, wide scoped and moving");
        Check(CsFeel.All.All(w => new[] { 0.0, 100, 250 }.All(s => CsFeel.MinimumCone(w, s, false) <= Cone(w, s) + 1e-12 && CsFeel.MinimumCone(w, s, false) <= Cone(w, s, crouch: true, blend: 1) + 1e-12)),
            "The host's least cone is never above an honest client's");
        Check(!CsFeel.ById("knife")!.Spreads && CsFeel.ById("c4")!.Spreads == false && ak.Spreads, "The knife, grenades and the bomb have no spread");

        // Seeds: the same numbers as AimModCore.
        var salt = CsFeel.Salt("cs-golden");
        var u = CsFeel.Randoms(salt, 7);
        Check(salt == 9077073264004251771UL && CsFeel.Salt("") == 14695981039346656037UL && Math.Abs(u[0] - 0.80751303501723604) < 1e-15 && Math.Abs(u[1] - 0.18479337744944391) < 1e-15
            && Math.Abs(u[2] - 0.79089596736284984) < 1e-15 && Math.Abs(u[3] - 0.86836906912763567) < 1e-15, "The match salt and a shot's randoms are AimModCore's");
        var o1 = CsFeel.Offset(salt, 1, 0.05, 0.0006); var o2 = CsFeel.Offset(salt, 123456789, 0.05, 0.0006);
        Check(Math.Abs(o1.Right - -0.024038644358901809) < 1e-12 && Math.Abs(o1.Up - -0.0084995707911488753) < 1e-12 && Math.Abs(o2.Right - 0.0074422910951327586) < 1e-12 && Math.Abs(o2.Up - 0.02792096125188772) < 1e-12,
            "A shot's offset is rebuilt exactly from its seed");
        var d = CsFeel.Direction(0, 0, (0.1, 0));
        Check(Math.Abs(Math.Atan2(d.Y, d.X) - Math.Atan(0.1)) < 1e-12 && Math.Abs(d.Z) < 1e-12 && CsFeel.Direction(10, 30, (0, 0)) is var straight && Math.Abs(straight.Z - Math.Sin(10 * Math.PI / 180)) < 1e-12,
            "A right offset turns the ray right; none leaves the camera ray");
        Check(CsFeel.Line("m-1", true, AdsZooms.Cs, 1) == "feel\t" + CsFeel.Salt("m-1") + "\t1\tcs\t1" && CsFeel.Line("m-1", false, AdsZooms.All, 0.8).EndsWith("\t0\tall\t0.8", StringComparison.Ordinal),
            "The round state's feel line: salt, crosshair, zoom, zoomed sensitivity");

        // Speeds: the knife fastest, then pistols, SMGs, rifles, the AWP, the scoped AWP slowest.
        double Share(string id, bool scoped = false) => CsFeel.SpeedShare(CsFeel.ById(id), scoped);
        Check(Share("knife") == 1 && Share("glock") < 1 && Share("deagle") < Share("glock") && Share("m4a1s") < Share("mp9") && Share("ak47") < Share("m4a1s") && Share("awp") < Share("ak47")
            && Share("awp", true) == 0.4 && Math.Abs(Share("ak47") - 0.86) < 1e-12 && CsFeel.SpeedShare(null) == 1, "Each weapon has its CS speed against the knife's 250 u/s");

        HostSpread();
        FeedSpread();

        // The arena: every CS gun carries one per-bullet spread entry for AimModCore to fill; the knife doesn't;
        // the scoped AWP's slower walk is AimModCore's.
        var content = new FakeContent();
        var cs = LobbyRules.Apply(new LobbySettings(Scenario: content.Scenario("Synthetic A")), J(new { mode = "cs" }), 2, content).Settings!;
        var arena = MatchScenario.Generate(new(BaseScenario, cs with { Scenario = new ScenarioChoice("Synthetic A", ContentLibrary.TextHash(BaseScenario), "synthetic_map", ContentLibrary.TextHash("m"), 60) }));
        string Section(string name) { var at = arena.IndexOf("[Weapon Profile]\nName=" + name + "\n", StringComparison.Ordinal); var end = arena.IndexOf("\n\n", at, StringComparison.Ordinal); return arena[at..(end < 0 ? arena.Length : end + 1)]; }
        Check(CsRules.Weapons.All(w => Section(w.Combat.Name).Contains("\nUsePerBulletSpread=true\nPBS0=0.0,0.0\n", StringComparison.Ordinal)) && !Section(CsRules.Knife.Combat.Name).Contains("UsePerBulletSpread")
            && Section("AimMod CS AK-47").Contains("\nSpreadSSA=0.0,0.0,0.0,0.0\n") && Section("AimMod CS AWP").Contains("\nADSMoveFactor=1.0\n"),
            "CS guns carry a per-bullet spread entry (KovaaK's random spread stays off); the AWP's scoped walk is AimModCore's");
        Check(cs.DynamicCrosshair && !LobbyRules.Apply(cs, J(new { dynamicCrosshair = false }), 2, content).Settings!.DynamicCrosshair && !LobbyRules.Apply(cs, J(new { dynamicCrosshair = 3 }), 2, content).Result.Ok,
            "The dynamic crosshair is on by default and can be switched off");
    }

    // The host rebuilds each CS bullet's ray from the claim and validates the hit on it.
    static void HostSpread()
    {
        const long t0 = 40_000_000;
        var live = t0 + CsRules.FreezeMs + 1000;
        // "a" (team 1: T, the Glock) shoots at "b", 15 m along x.
        CsMatch Setup(double speedCm, double yaw, double riseCm = 0)
        {
            var m = new CsMatch(["a", "b"], t0, 6, true, null, new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 });
            var a = new List<TrackSample>(); var b = new List<TrackSample>();
            for (var t = t0 + 12_000; t < t0 + 20_000; t += 17)
            {
                var s = (t - t0 - 12_000) / 1000.0;
                a.Add(new TrackSample(t, 0, speedCm * s, 164 + riseCm * s, 0, yaw));
                b.Add(new TrackSample(t, 1500, speedCm * s, 164, 0, 180));
            }
            for (var i = 0; i < a.Count; i += 60) { m.Combat.Track("a", new TrackBatch("m", 1, a.Skip(i).Take(60).ToList(), [])); m.Combat.Track("b", new TrackBatch("m", 1, b.Skip(i).Take(60).ToList(), [])); }
            m.Tick(t0 + CsRules.FreezeMs);
            return m;
        }
        double Y(long t, double speedCm) => speedCm * (t - t0 - 12_000) / 1000.0;
        HitClaim Shot(long t, long seq, double pitch, double yaw, double? spread, long? spreadShot, double speedCm = 0) =>
            new("m", 1, seq, t, 0, Y(t, speedCm), 164, pitch, yaw, false, 1500, Y(t, speedCm), 100, 45, 115, CsRules.PistolSlot, seq, false, null, 1, spread, spreadShot);
        var glock = CsFeel.ById("glock")!;
        var honest = CsFeel.ConeNow(glock, 0, false, false, 0);

        // Standing still, an honest Glock shot at the chest lands (its cone is a few cm at 15 m).
        var still = Setup(0, 0);
        Check(still.Combat.Claim("a", Shot(live, 1, 0, 0, honest, 1), live + 30, 40) is null, "A standing Glock shot with its honest inaccuracy lands on the bullet's ray (" + still.Combat.LastDetail + ")");
        // A claim without spread numbers still gets the shooter's least cone from the host.
        Check(still.Combat.Claim("a", Shot(live + 500, 2, 0, 0, null, null), live + 530, 40) is null, "A claim without spread numbers is turned by the host's least cone");

        // A big inaccuracy whose seeded offset leaves the target: the camera is dead on, the bullet misses.
        var salt = CsFeel.Salt("m");
        var shot = Enumerable.Range(10, 400).First(n => CsFeel.Offset(salt, (ulong)n, 0.2, glock.Spread) is var o && Math.Abs(o.Right) > 0.08 && Math.Abs(o.Up) < 0.02);
        var wide = Setup(0, 0);
        Check(wide.Combat.Claim("a", Shot(live, shot, 0, 0, 0.2, shot), live + 30, 40) == "ray-miss", "The spread is real: a crosshair on the chest misses when the bullet's offset leaves the body (" + wide.Combat.LastDetail + ")");
        // ...and aimed so the bullet's ray (not the crosshair) meets the target, it lands.
        var off = CsFeel.Offset(salt, (ulong)shot, 0.2, glock.Spread);
        var yaw = -Math.Atan(off.Right) * 180 / Math.PI; var pitch = -Math.Atan(off.Up) * 180 / Math.PI;
        var aimed = Setup(0, yaw);
        Check(aimed.Combat.Claim("a", Shot(live, shot, pitch, yaw, 0.2, shot), live + 30, 40) is null, "The host validates on the bullet's ray: aimed off by the offset, the bullet lands (" + aimed.Combat.LastDetail + ")");

        // Running at 250 u/s, a claim of a standing shot is impossible; the honest running cone is accepted.
        var runCm = 250 * CsFeel.UnitCm;
        var runner = Setup(runCm, 0);
        var motion = runner.Combat.MotionAt("a", live);
        Check(Math.Abs(motion.Speed - 250) < 5 && !motion.Air, "The host measures the shooter's speed from its own track (" + Math.Round(motion.Speed, 1) + " u/s)");
        var running = CsFeel.ConeNow(glock, 250, false, false, 0);
        var lie = runner.Combat.Claim("a", Shot(live, 1, 0, 0, honest, 1, runCm), live + 30, 40);
        Check(lie == "spread", "A running shooter claiming a standing shot's accuracy is refused (" + (lie ?? "accepted") + ", " + runner.Combat.LastDetail + ")");
        var truth = runner.Combat.Claim("a", Shot(live + 400, 2, 0, 0, running, 2, runCm), live + 430, 40);
        Check(truth is not "spread", "The honest running cone passes the spread check (" + (truth ?? "accepted") + ")");
        // Jumping: the track rising and falling fast.
        var jumper = Setup(0, 0, 900);
        Check(jumper.Combat.MotionAt("a", live).Air && !still.Combat.MotionAt("a", live).Air && CsFeel.MinimumCone(glock, 0, true) > CsFeel.MinimumCone(glock, 0, false) + 0.1,
            "A fast-rising track reads as airborne, which needs the jump cone");

        // The seed must be the shot's (or a few before it, for shots counted in one frame).
        var seeds = Setup(0, 0);
        Check(seeds.Combat.Claim("a", Shot(live, 20, 0, 0, honest, 21), live + 30, 40) == "spread" && seeds.Combat.Claim("a", Shot(live + 400, 40, 0, 0, honest, 31), live + 430, 40) == "spread"
            && seeds.Combat.Claim("a", Shot(live + 800, 50, 0, 0, honest, 47), live + 830, 40) is null && seeds.Combat.Claim("a", Shot(live + 1200, 60, 0, 0, 0.7, 60), live + 1230, 40) == "spread",
            "A seed from a later shot, one too far back or an absurd inaccuracy is refused; a seed a few shots back is fine");

        // The claim carries the spread to the host and back.
        var claim = Shot(live, 9, 1.5, -2, 0.0123456, 7);
        var read = HitClaim.Read(J(claim.Body()));
        Check(read is { Spread: { } sp, SpreadShot: 7 } && Math.Abs(sp - 0.0123456) < 1e-7 && HitClaim.Read(J(Shot(live, 9, 0, 0, null, null).Body()))?.Spread is null,
            "A hit claim carries its inaccuracy and seed");
    }

    // AimModCore's shot rows with the spread columns, and which of them are claimed.
    static void FeedSpread()
    {
        const string head = "AIMMOD_SHOTS_1\t5\t77\n";
        string Row(long seq, string hit, string spread) => "shot\t1790000000000\t" + seq + "\t0\t0\t164\t1\t0\t0\t1\t9\t0\t" + hit + "\t1500\t0\t100\t45\t115\t-1\t1\t" + spread + "\n";
        var parsed = ShotFeed.Parse(head + Row(1, "1", "6.4\t1\t1") + Row(2, "0", "6.4\t2\t0") + Row(3, "0", "6.4\t3\t1") + Row(4, "0", "0\t0\t0"));
        Check(parsed is { Shots.Count: 4 } p && p.Shots[0] is { Spread: { } s1, SpreadShot: 1, SpreadApplied: true } && Math.Abs(s1 - 0.0064) < 1e-12 && p.Shots[1] is { SpreadApplied: false, Spread: not null }
            && p.Shots[3].Spread is null, "Shot rows carry the inaccuracy, the seed's shot and whether the game's trace followed it");
        var feed = new ShotFeed(Path.GetTempPath());
        var claims = feed.Take(parsed, "m", 1, 0, (_, _) => null, 1790000000100);
        // Shot 1: a game hit; shot 2: the game's trace stayed on the crosshair, AimModCore's ray decides; shot 3: the
        // game traced the bullet and it missed; shot 4: no spread, no game hit.
        Check(claims.Select(c => c.Shot).SequenceEqual(new long[] { 1, 2 }) && claims[0].Spread is { } c1 && Math.Abs(c1 - 0.0064) < 1e-12 && claims[0].SpreadShot == 1,
            "Claims: the game's hits, and bullets only AimModCore's ray carried; not the game's misses");
        Check(ShotFeed.Parse(head + Row(1, "1", "9999\t1\t1")) is null && ShotFeed.Parse(head + Row(1, "1", "6\tx\t1")) is null && ShotFeed.Parse(head + Row(1, "1", "6\t1\t2")) is null,
            "Malformed spread columns refuse the file");
    }
}

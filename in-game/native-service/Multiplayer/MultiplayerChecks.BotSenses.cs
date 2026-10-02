using Aim = AimMod.InGame.Multiplayer.BotAim;

namespace AimMod.InGame.Multiplayer;

// Bot AI v2 (game-modes.md, bots): senses (sight through smoke as it grows, footsteps by gait,
// gunfire and decoys), the side's memory, decoys seen through by hard bots and believed by easy
// ones, smoke calls per situation, flashes (blinded by any flash, hard bots looking away from their
// own), eased turning without snaps, and what the bridge and the developer menu are told.
static partial class MultiplayerChecks
{
    static void BotSenseChecks()
    {
        BotTurnSmoothing();
        BotSmokeSight();
        BotFootstepHearing();
        BotDecoys();
        BotSmokeDecisions();
        BotFlashes();
        BotMovementOrders();
    }

    // Eased turning: never faster than the rate, never a speed change over the angular acceleration,
    // no single-step snap, settles on target, a little overshoot by skill.
    static void BotTurnSmoothing()
    {
        foreach (var id in BotSkills.All)
        {
            var p = Aim.Profile(BotSkills.For(id));
            const double dt = 1 / 60.0;
            double angle = 0, vel = 0, maxDv = 0, maxStep = 0, maxVel = 0, past = 0;
            for (var i = 0; i < 180; i++)
            {
                var (a0, v0) = (angle, vel);
                Aim.Ease(ref angle, ref vel, 179, dt, p, true);
                maxDv = Math.Max(maxDv, Math.Abs(vel - v0)); maxStep = Math.Max(maxStep, Math.Abs(Aim.Wrap(angle - a0))); maxVel = Math.Max(maxVel, Math.Abs(vel));
                if (angle > 179) past = Math.Max(past, angle - 179);
            }
            Check(maxDv <= p.Accel * dt + 1e-6 && maxVel <= p.Rate + 1e-6 && maxStep <= p.Rate * dt + 1e-6 && Math.Abs(angle - 179) < 1,
                FormattableString.Invariant($"A {id} bot's turn is eased: speed change at most {p.Accel} deg/s/s (saw {maxDv / dt:0}), at most {p.Rate} deg/s, no snap, and it settles ({angle:0.0})"));
            Check(past < 15 && (p.Overshoot < 0.1 || past > 0.2), FormattableString.Invariant($"A {id} bot overshoots a 179 degree turn a little ({past:0.0} degrees) and settles back"));
            // Random targets every 0.4 s: still no snaps.
            var random = new Random(4); double a = 0, v = 0, target = 0; var worst = 0.0;
            for (var i = 0; i < 600; i++)
            {
                if (i % 24 == 0) target = random.NextDouble() * 360 - 180;
                var v0 = v;
                Aim.Ease(ref a, ref v, target, dt, p, true);
                worst = Math.Max(worst, Math.Abs(v - v0));
            }
            Check(worst <= p.Accel * dt + 1e-6, "A " + id + " bot never snaps between targets either");
        }
        var easy = Aim.Profile(BotSkills.For(BotSkills.Easy)); var hard = Aim.Profile(BotSkills.For(BotSkills.Hard));
        Check(hard.Rate > easy.Rate && hard.Accel > easy.Accel && hard.Overshoot < easy.Overshoot, "Harder bots turn faster, accelerate harder and overshoot less");
    }

    // Smoke: a line through the cloud is blocked once it has grown (it billows out over a second),
    // a line past its edge or a short one inside it is not, nor once it has thinned out.
    static void BotSmokeSight()
    {
        var smoke = new CsGrenadeView(1, "smoke", "a", "smoke", 0, GrenadeRules.SmokeMs, [0, 0, 0], Radius: GrenadeRules.SmokeRadiusCm);
        CsGrenadeView[] list = [smoke];
        double[] a = [-1500, 0, 180], b = [1500, 0, 180];
        Check(BotVision.SmokeBlocks(list, a, b, 5000) && BotVision.SmokeBlocks(list, b, a, 5000), "A smoke hides a line straight through it, both ways");
        Check(!BotVision.SmokeBlocks(list, [-1500, 610, 180], [1500, 610, 180], 5000) && !BotVision.SmokeBlocks(list, [-1500, 900, 180], [1500, 900, 180], 5000),
            "A line grazing its edge (little smoke in the way) or passing beside it is clear");
        Check(!BotVision.SmokeBlocks(list, [-100, 0, 180], [0, 0, 180], 5000) && BotVision.SmokeBlocks(list, [-300, 0, 180], [200, 0, 180], 5000), "Inside a smoke you see only up close");
        Check(!BotVision.SmokeBlocks(list, [-1500, 450, 180], [1500, 450, 180], 150) && BotVision.SmokeBlocks(list, [-1500, 450, 180], [1500, 450, 180], 1500),
            "The cloud grows: a line off its middle is clear as it pops, hidden once it has billowed out");
        Check(!BotVision.SmokeBlocks(list, a, b, GrenadeRules.SmokeMs - 300) && BotVision.Inside(list, [0, 0, 200], 5000) > 0.9 && BotVision.Inside(list, [2000, 0, 200], 5000) == 0,
            "As it thins out at the end it stops hiding; inside it a bot knows it is in smoke");
        // Bots' sight after smoke (the brain's world): the target behind it is gone, the other stays.
        var sight = new Dictionary<string, BotSight> { ["bot"] = new(5000, -1500, 0, 116, 0, new HashSet<int> { 1, 2 }) };
        BotPlayer[] players = [new("bot", 0, -1500, 0, 180, 2, true, 0), new("x", 1, 1500, 0, 180, 1, true, 0), new("y", 2, -1500, 900, 180, 1, true, 0)];
        var view = new CsView(1, "live", 0, 0, [0, 0], CsRules.T, 6, true, [], new CsBombView("carried", null, null, null, null, null, null, null, null), null, null, null, [], null, null, list);
        Check(BotVision.Sight(view, sight, players, 5000)["bot"].Visible.SetEquals([2]), "Bots see no one through a smoke");
    }

    // Footsteps: a run is heard (farther by harder bots, as far as a player hears it at best);
    // walking (shift) and crouching are silent.
    static void BotFootstepHearing()
    {
        Check(BotHearing.Gait(1000, false, true) == "run" && BotHearing.Gait(570, false, true) == "walk" && BotHearing.Gait(370, true, true) == "crouch" && BotHearing.Gait(50, false, true) == "still",
            "CS gaits by speed: a run, a shift-walk, a crouch-walk, standing");
        Check(BotHearing.StepRange("run", BotSkills.Hard) == GunSounds.StepRangeCm && BotHearing.StepRange("run", BotSkills.Easy) < BotHearing.StepRange("run", BotSkills.Normal)
            && BotHearing.StepRange("run", BotSkills.Normal) < BotHearing.StepRange("run", BotSkills.Hard) && BotHearing.StepRange("walk", BotSkills.Hard) == 0 && BotHearing.StepRange("crouch", BotSkills.Hard) == 0,
            FormattableString.Invariant($"Footstep earshot: a run {BotHearing.StepRange("run", BotSkills.Easy) / 100:0} m (easy), {BotHearing.StepRange("run", BotSkills.Normal) / 100:0} m (normal), {BotHearing.StepRange("run", BotSkills.Hard) / 100:0} m (hard); walking and crouching silent"));
        Check(BotHearing.ShotRange(true, BotSkills.Hard) < BotHearing.ShotRange(false, BotSkills.Hard) && BotHearing.ShotRange(false, BotSkills.Easy) < BotHearing.ShotRange(false, BotSkills.Hard),
            "Gunfire carries farther than footsteps, less through a wall, and harder bots hear it farther");
        // In a round: a Terrorist runs 18 m from two defenders (hard and easy), then walks 6 m from them.
        var map = BotMap();
        const long t0 = 60_000_000;
        var cs = new CsMatch(["t1", "e1", "h1"], t0, 12, true, map, new Dictionary<string, int> { ["t1"] = 1, ["e1"] = 2, ["h1"] = 2 });
        cs.Tick(t0 + CsRules.FreezeMs + 1);
        var t = t0 + CsRules.FreezeMs + 200;
        BotWorld World(BotPlayer runner) => new(t, LobbyModes.Cs, [("e1", BotSkills.Easy), ("h1", BotSkills.Hard)],
            [runner, new BotPlayer("e1", 1, 0, 1800, 164, 2, true, 0), new BotPlayer("h1", 2, 0, -1800, 164, 2, true, 0)],
            new Dictionary<string, BotSight> { ["e1"] = new(t, 0, 1800, 100, 0, new HashSet<int>()), ["h1"] = new(t, 0, -1800, 100, 0, new HashSet<int>()) }, cs.View(), map, []);
        var brain = new BotBrain(seed: 41);
        brain.Reset("cs#steps");
        var run = brain.Step(World(new BotPlayer("t1", 0, 0, 0, 164, 1, true, 1000)));
        var e1 = run.Debug!.First(d => d.Bot == "e1"); var h1 = run.Debug!.First(d => d.Bot == "h1");
        Check(h1.Heard.Any(h => h.StartsWith("steps", StringComparison.Ordinal)) && !e1.Heard.Any(), "A running enemy 18 m off: the hard bot hears it, the easy one doesn't");
        var quiet = new BotBrain(seed: 42);
        quiet.Reset("cs#walk");
        var walk = quiet.Step(World(new BotPlayer("t1", 0, 0, 1200, 164, 1, true, 560)));
        Check(walk.Debug!.All(d => d.Heard.Count == 0) && !quiet.KnowledgeOf(CsRules.CT).Fresh(t).Any(), "Shift-walking 6 m from a bot: nobody hears a thing");
    }

    // Decoys: the same fake gunfire is believed by an easy bot (it rotates) and seen through by a
    // hard one (it never moves, the same bursts, no hits near it); a hard bot that saw the grenade
    // land there knows at the first burst.
    static void BotDecoys()
    {
        var map = BotMap();
        const long t0 = 70_000_000;
        var live = t0 + CsRules.FreezeMs;
        (CsMatch Cs, long Lands) Thrown(double[] from)
        {
            var cs = new CsMatch(["t1", "c1", "c2"], t0, 12, true, map, new Dictionary<string, int> { ["t1"] = 1, ["c1"] = 2, ["c2"] = 2 });
            cs.Combat.Track("t1", new TrackBatch("m", 1, [new TrackSample(t0 + 5, 0, -3000, 180, 0, 90)], []));
            Check(cs.Buy("t1", "decoy", t0 + 10) is null, "A Terrorist buys a decoy in its buy zone");
            cs.Tick(live + 1);
            var target = new[] { -2200.0, 600, 0 };
            Check(cs.BotThrow("t1", "decoy", from, GrenadeAim.Lob(from, target), live + 50) is null, "A Terrorist throws a decoy");
            return (cs, live + 50);
        }
        BotWorld World(CsMatch cs, long t, double yaw, double cy) => new(t, LobbyModes.Cs, [("c1", BotSkills.Easy), ("c2", BotSkills.Hard)],
            [new BotPlayer("t1", 0, -400, -2600, 164, 1, true, 0), new BotPlayer("c1", 1, -1000, cy, 164, 2, true, 0), new BotPlayer("c2", 2, -900, cy, 164, 2, true, 0)],
            new Dictionary<string, BotSight> { ["c1"] = new(t, -1000, cy, 100, yaw, new HashSet<int>()), ["c2"] = new(t, -900, cy, 100, yaw, new HashSet<int>()) }, cs.View(), map, []);
        // Not seen landing (both look away, too far to hear it land): judged by the pattern.
        var (m, at) = Thrown([-1600, -900, 180]);
        var brain = new BotBrain(seed: 51);
        brain.Reset("cs#decoy");
        BotStep? step = null;
        for (var t = at; t < at + 7000; t += 100) { m.Tick(t); step = brain.Step(World(m, t, 90, 1800)); }
        var source = brain.KnowledgeOf(CsRules.CT).Sounds.OrderByDescending(s => s.Bursts.Count).FirstOrDefault();
        Check(source is { } src && src.Bursts.Count >= 3 && src.Verdicts.GetValueOrDefault("c2", "").StartsWith("decoy", StringComparison.Ordinal) && src.Verdicts.GetValueOrDefault("c1") == "believed",
            "A decoy's gunfire: the hard bot calls it a decoy (" + (source?.Verdicts.GetValueOrDefault("c2") ?? "none") + "), the easy bot believes it");
        Check(source?.Cues.Contains("never moves") == true && source.Cues.Contains("no hits") && source.CalledBy == "c2", "Its cues: the shooter never moves and nothing near it gets hit; the hard bot tells the side");
        var c1 = step!.Orders.First(o => o.Member == "c1"); var c2 = step.Orders.First(o => o.Member == "c2");
        Check(c1.Goal is { } g1 && Math.Abs(g1[0] + 3000) < 1600 && !c2.Role!.StartsWith("rotate", StringComparison.Ordinal) && c2.Goal is { } g2 && g2[0] > 0,
            "The easy bot heads to the fake fight at B (it ignores the call); the hard one stays on its site (" + c1.Role + "; " + c2.Role + ")");
        // Seen landing: the hard bot watches the throw come down there, then hears the gun start.
        var (m2, at2) = Thrown([-1600, 1400, 180]);
        var watcher = new BotBrain(seed: 52);
        watcher.Reset("cs#decoy2");
        long firstShot = 0;
        for (var t = at2; t < at2 + 4000; t += 100)
        {
            m2.Tick(t);
            watcher.Step(World(m2, t, 200, 1300));
            var s2 = watcher.KnowledgeOf(CsRules.CT).Sounds.FirstOrDefault();
            if (s2 is not null && firstShot == 0) firstShot = s2.FirstT;
            if (s2?.Verdicts.GetValueOrDefault("c2", "").StartsWith("decoy", StringComparison.Ordinal) == true) break;
        }
        var seen = watcher.KnowledgeOf(CsRules.CT).Sounds.FirstOrDefault();
        Check(seen is { } s3 && s3.Bursts.Count <= 2 && s3.Verdicts.GetValueOrDefault("c2", "").Contains("a grenade landed there") && s3.Verdicts.GetValueOrDefault("c1") == "believed",
            "A hard bot that saw a grenade land where the gunfire starts calls it a decoy at once (" + (seen?.Bursts.Count ?? 0) + " bursts in); the easy one still falls for it");
        // The cues on their own: a moving shooter with hits is believed by everyone.
        var team = new TeamKnowledge();
        var real = new SoundSource { Id = 1, Weapon = "rifle" };
        for (var i = 0; i < 16; i++) real.Add(1000 + i * 100 + i / 4 * 600, [i * 40.0, 0, 160]);
        var ctx = new BotEars.Context([new CombatEvent(1, "damage", 1500, "v", "shooter", 27, false, 73, 100)], [new BotPlayer("shooter", 0, 600, 0, 160, 1, true, 400), new BotPlayer("v", 1, 1500, 0, 160, 2, true, 0)], 1, null, 4000);
        var (score, cues) = BotEars.Cues(real, team, ctx);
        Check(score < BotEars.Threshold(BotSkills.Hard) && cues.Count == 0, "Real gunfire (a shooter on the move, hitting someone) is believed even by a hard bot");
        Check(BotEars.Cues(real, team, ctx with { Plausible = new HashSet<string> { "pistol", "knife" } }).Cues.Contains("wrong gun for their buy"), "A rifle from a side that only has pistols this round is suspicious");
    }

    // Smoke calls, one per situation and difficulty.
    static void BotSmokeDecisions()
    {
        SmokeSituation S(bool inside = false, bool blocking = true, long left = 14_000, bool flash = false, bool pressure = false, bool defending = false, bool mates = true) =>
            new(inside, blocking, left, flash, pressure, defending, mates);
        Check(BotTactics.Decide(S(inside: true, blocking: false, defending: true), BotSkills.Hard, 0.3) == SmokeChoice.Leave
            && BotTactics.Decide(S(inside: true, blocking: false), BotSkills.Easy, 0.9) == SmokeChoice.Leave && BotTactics.Decide(S(inside: true), BotSkills.Normal, 0.9) == SmokeChoice.Push,
            "Never standing in a smoke for nothing: out of it, or on through when the way lies beyond");
        Check(BotTactics.Decide(S(defending: true), BotSkills.Hard, 0.9) == SmokeChoice.HoldEdge, "A hard defender waits a smoke out, holding the edge someone would come out of");
        Check(BotTactics.Decide(S(flash: true), BotSkills.Hard, 0.9) == SmokeChoice.FlashPush, "A hard attacker with a flash and mates near flashes through the smoke and pushes");
        Check(BotTactics.Decide(S(), BotSkills.Hard, 0.1) == SmokeChoice.Reroute, "Without a flash it may take another way round");
        Check(BotTactics.Decide(S(left: 3000), BotSkills.Hard, 0.5) == SmokeChoice.WideSwing && BotTactics.Decide(S(left: 3000, defending: true), BotSkills.Hard, 0.5) == SmokeChoice.WideSwing,
            "As a smoke fades a hard bot swings wide on its edge");
        Check(BotTactics.Decide(S(pressure: true, flash: true), BotSkills.Normal, 0.5) == SmokeChoice.FlashPush && BotTactics.Decide(S(pressure: true), BotSkills.Hard, 0.5) == SmokeChoice.Push,
            "Out of time (a planted bomb to defuse, the round clock): through the smoke, behind a flash if it has one");
        Check(BotTactics.Decide(S(), BotSkills.Easy, 0.2) == SmokeChoice.Push && BotTactics.Decide(S(), BotSkills.Easy, 0.8) == SmokeChoice.HoldEdge, "Easy bots walk into it or wait about");
        Check(BotTactics.Decide(S(blocking: false), BotSkills.Hard, 0.5) == SmokeChoice.None, "A smoke off to the side changes nothing");
        // In a round: a hard CT anchor with a smoke between it and the Terrorists' way holds the edge;
        // the walker routes round a fire.
        var map = BotMap();
        const long t0 = 80_000_000;
        var cs = new CsMatch(["c1", "t1"], t0, 12, true, map, new Dictionary<string, int> { ["t1"] = 1, ["c1"] = 2 });
        cs.Tick(t0 + CsRules.FreezeMs + 1);
        var t = t0 + CsRules.FreezeMs + 3000;
        var smoke = new CsGrenadeView(9, "smoke", "t1", "smoke", t - 3000, t + 12_000, [2000, -1000, 0], Radius: GrenadeRules.SmokeRadiusCm);
        var fire = new CsGrenadeView(10, "molotov", "t1", "fire", t - 500, t + 5000, [2000, 1500, 0], Radius: GrenadeRules.FireRadiusCm);
        var view = cs.View() with { Grenades = [smoke, fire] };
        var brain = new BotBrain(seed: 61);
        brain.Reset("cs#smoke");
        var c1 = new BotPlayer("c1", 0, 3000, 0, 164, 2, true, 0);
        var w = new BotWorld(t, LobbyModes.Cs, [("c1", BotSkills.Hard)], [c1, new BotPlayer("t1", 1, 0, -3000, 164, 1, true, 0)],
            new Dictionary<string, BotSight> { ["c1"] = new(t, 3000, 0, 100, -90, new HashSet<int>()) }, view, map, []);
        var plan = brain.Step(w);
        var order = plan.Orders.Single();
        Check(order.Role.StartsWith("holding the smoke's edge", StringComparison.Ordinal) && order.Face is { } edge && Math.Sqrt((edge[0] - 2000) * (edge[0] - 2000) + (edge[1] + 1000) * (edge[1] + 1000)) < 700 && plan.Debug!.Single().Smoke!.StartsWith("HoldEdge", StringComparison.Ordinal),
            "A hard CT at its site with the Terrorists' way smoked off holds the smoke's edge (" + order.Role + ")");
        Check(order.Avoid is { Count: 1 } av && av[0][0] == 2000 && av[0][4] >= 1000, "Every bot routes round a fire on the floor");
    }

    // Flashes: a hard bot looks away from its own flash as it pops (and is barely touched); an easy
    // bot doesn't (and blinds itself); a blinded bot backs off and turns away.
    static void BotFlashes()
    {
        double Amount(string skill)
        {
            const long t0 = 90_000_000;
            var cs = new CsMatch(["b", "e"], t0, 6, true, null, new Dictionary<string, int> { ["b"] = 1, ["e"] = 2 });
            cs.Buy("b", "flash", t0 + 10);
            var live = t0 + CsRules.FreezeMs;
            var brain = new BotBrain(seed: 71);
            brain.Reset("cs#flash-" + skill);
            var aim = new BotAimState(); var profile = Aim.Profile(BotSkills.For(skill));
            void Track(long t) => cs.Combat.Track("b", new TrackBatch("m", 1, [new TrackSample(t, 0, 0, 180, 0, aim.Yaw)], []));
            void TrackE(long t) => cs.Combat.Track("e", new TrackBatch("m", 1, [new TrackSample(t, 5000, 5000, 180, 0, 0)], []));
            Track(live - 100); TrackE(live - 100);
            cs.Tick(live + 1);
            // Its own flash, straight ahead: it pops 6 m in front of its face.
            cs.BotThrow("b", "flash", [0, 0, 180], GrenadeAim.Timed([0, 0, 180], [600, 0, 260], GrenadeRules.FlashFuseMs / 1000.0), live + 20);
            for (var t = live + 50; t < live + 2200; t += 50)
            {
                Track(t); TrackE(t);
                cs.Tick(t);
                var view = cs.View();
                var step = brain.Step(new BotWorld(t, LobbyModes.Cs, [("b", skill)], [new BotPlayer("b", 0, 0, 0, 180, 1, true, 0), new BotPlayer("e", 1, 5000, 5000, 180, 2, true, 0)],
                    new Dictionary<string, BotSight> { ["b"] = new(t, 0, 0, 116, aim.Yaw, new HashSet<int>()) }, view, null, []));
                // The walker turns the body where it is told to look (the same eased turn).
                if (step.Orders.Single().Face is { } f) Aim.Turn(aim, Aim.Angles([0, 0, 180], f).Yaw, 0, profile, 0.05);
            }
            return cs.View().Players.First(p => p.Member == "b").Flash?.Amount ?? 0;
        }
        var hard = Amount(BotSkills.Hard); var normal = Amount(BotSkills.Normal); var easy = Amount(BotSkills.Easy);
        Check(hard < 0.3 && normal < 0.4 && easy > 0.9, FormattableString.Invariant($"A hard bot turns away from its own flash as it pops ({hard:0.00} blind), so does a normal one ({normal:0.00}); an easy bot flashes itself ({easy:0.00})"));
        // Teammates' flashes: bots call theirs, so harder teammates know; easy bots neither call nor listen.
        var keys = GrenadePhysics.Flat(GrenadePhysics.Simulate([0, 0, 180], GrenadeAim.Timed([0, 0, 180], [500, 0, 260], 1.5), (_, _) => null));
        var flying = new CsGrenadeView(3, "flash", "mate", "flying", 1000, 2500, null, keys);
        bool Mate(string m) => m is "mate" or "me";
        var hardKnows = BotTactics.FlashThreats([flying], "me", BotSkills.Hard, Mate, m => m == "mate" ? BotSkills.Normal : null, 180, [300, 100, 180], 1800, _ => 0.9, null);
        var easyKnows = BotTactics.FlashThreats([flying], "me", BotSkills.Easy, Mate, m => m == "mate" ? BotSkills.Normal : null, 180, [300, 100, 180], 1800, _ => 0.9, null);
        var humanMate = BotTactics.FlashThreats([flying], "me", BotSkills.Normal, Mate, _ => null, 180, [300, 100, 180], 1800, _ => 0.9, null);
        Check(hardKnows.Single().Why == "called by mate" && easyKnows.Count == 0 && humanMate.Count == 0, "A teammate bot's flash is called: harder teammates look away, easy ones don't");
        // Blind: a bot backs off and turns away; while half blind it sees only up close.
        const long t1 = 95_000_000;
        var m = new CsMatch(["b", "e"], t1, 6, true, null, new Dictionary<string, int> { ["b"] = 1, ["e"] = 2 });
        m.Tick(t1 + CsRules.FreezeMs + 1);
        var now = t1 + CsRules.FreezeMs + 500;
        var v = m.View();
        var flashed = v with { Players = v.Players.Select(p => p.Member == "b" ? p with { Flash = GrenadeRules.FlashFor(now - 100, 1) } : p).ToArray() };
        var blindBrain = new BotBrain(seed: 72);
        blindBrain.Reset("cs#blind");
        BotWorld W(CsView cs, long t) => new(t, LobbyModes.Cs, [("b", BotSkills.Normal)], [new BotPlayer("b", 0, 0, 0, 180, 1, true, 0), new BotPlayer("e", 1, 3000, 0, 180, 2, true, 0)],
            new Dictionary<string, BotSight> { ["b"] = new(t, 0, 0, 116, 0, new HashSet<int> { 1 }) }, cs, null, []);
        blindBrain.Step(W(v, now - 600));
        var blind = blindBrain.Step(W(flashed, now));
        var bo = blind.Orders.Single();
        Check(blind.Debug!.Single().Blind > 0.9 && blind.Shots.Count == 0 && bo.Role.StartsWith("blind", StringComparison.Ordinal), "Blinded by a flash, a bot loses its target and aims badly (" + bo.Role + ")");
        var later = blindBrain.Step(W(flashed with { Players = flashed.Players.Select(p => p.Member == "b" ? p with { Flash = GrenadeRules.FlashFor(now - 100, 1) } : p).ToArray() }, now + 3000));
        var calm = new BotBrain(seed: 73);
        calm.Reset("cs#blind-calm");
        var away = calm.Step(new BotWorld(now, LobbyModes.Cs, [("b", BotSkills.Normal)], [new BotPlayer("b", 0, 0, 0, 180, 1, true, 0), new BotPlayer("e", 1, 3000, 0, 180, 2, true, 0)],
            new Dictionary<string, BotSight> { ["b"] = new(now, 0, 0, 116, 0, new HashSet<int>()) }, flashed, null, [])).Orders.Single();
        Check(away.Role == "blind (backing off)" && away.Face is { } af && af[0] < 0 && away.Goal is { } ag && ag[0] < 0 && later.Orders.Single().Role.StartsWith("blind", StringComparison.Ordinal),
            "Blind with nothing to spray at: it backs off and turns away from where it was looking");
    }

    // What the bridge and the developer menu are told: walking gaits, peeks, counter-strafing,
    // routes round fires, the vel report back, and the debug line.
    static void BotMovementOrders()
    {
        var hard = BotTactics.Move(BotSkills.Hard, 1500, false, false, false, 0.5);
        var normal = BotTactics.Move(BotSkills.Normal, 2000, false, false, false, 0.5);
        var easy = BotTactics.Move(BotSkills.Easy, 800, false, false, false, 0.5);
        Check(hard is { Gait: "walk", PreAim: true } && normal.Gait == "run" && easy is { Gait: "run", PreAim: false } && BotTactics.Move(BotSkills.Hard, 1500, true, false, false, 0.5).Gait == "run",
            "Hard bots shift-walk near the enemy (silent) unless they hurry; easy bots just run; harder bots pre-aim corners");
        Check(BotTactics.Move(BotSkills.Hard, 1500, false, true, true, 0.5).Peek == "jiggle" && BotTactics.Move(BotSkills.Hard, 1500, false, true, true, 0.9).Peek == "crouch"
            && BotTactics.Move(BotSkills.Easy, 1500, false, true, true, 0.5).Peek is null, "Holding towards a sound it can't see, a hard bot jiggle- or crouch-peeks; an easy bot just stands");
        Check(Aim.FightStyle(BotSkills.For(BotSkills.Hard)) == "counter" && Aim.FightStyle(BotSkills.For(BotSkills.Easy)) == "ad" && Aim.WaitsToStop(BotSkills.For(BotSkills.Hard), 600)
            && !Aim.WaitsToStop(BotSkills.For(BotSkills.Hard), 60) && !Aim.WaitsToStop(BotSkills.For(BotSkills.Easy), 600) && !Aim.WaitsToStop(BotSkills.For(BotSkills.Hard), null)
            && Aim.Moving(900) < 0.5 && Aim.Moving(80) == 1, "Harder bots counter-strafe and shoot from a stop; shooting on the move costs aim (easy bots do it anyway)");
        var ak = CsFeel.ById("ak47"); var awp = CsFeel.ById("awp");
        Check(Aim.MoveAccuracy(ak, 0, false, 1500) == 1 && Aim.MoveAccuracy(ak, 900, false, 1500) < 0.4 && Aim.MoveAccuracy(awp, 0, false, 3000) == 1 && Aim.MoveAccuracy(awp, 700, false, 3000) < 0.2
              && Aim.MoveAccuracy(ak, 300, true, 3000) >= Aim.MoveAccuracy(ak, 300, false, 3000) && Aim.MoveAccuracy(null, 900, false, 1500) == Aim.Moving(900),
            "A bot's own movement costs it what the players' cones cost them (CsFeel): an AK on the run, an AWP unscoped on the move; still, it's on");
        var sight = MultiplayerService.ParseBotSight("AIMMOD_BOTSIGHT_1\t1000\nbot\t1\t10\t20\t145\t90\t0\nvel\t1\t412.5\t1\nbot\t2\t5\t5\t100\t0\n", 1000);
        Check(sight is { } s && s["1"].Speed == 412.5 && s["1"].Crouch && s["2"].Speed is null && !s["2"].Crouch, "bot-sight.tsv carries each bot's speed and crouch (older bridges don't)");
        var line = MultiplayerService.BotDebugLine(new BotDebugInfo("Nova", "anchor", ["gunfire (rifle) 23 m through a wall"], ["#2 rifle: decoy (never moves, no hits)"], "HoldEdge (smoke 9 m, 8 s left)", 0.7, "looking away (own flash)", "walk/stand"));
        Check(line == "Nova: anchor | walk/stand | heard gunfire (rifle) 23 m through a wall | sources #2 rifle: decoy (never moves, no hits) | smoke: HoldEdge (smoke 9 m, 8 s left) | blind 0.70 | looking away (own flash)",
            "The bot debug line: role, movement, what it heard, the decoys it called, its smoke call, blindness and flashes");
    }
}

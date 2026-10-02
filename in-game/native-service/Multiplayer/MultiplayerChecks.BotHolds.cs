using Aim = AimMod.InGame.Multiplayer.BotAim;

namespace AimMod.InGame.Multiplayer;

// Holding ground (BotPositions.cs): the areas its game works out (bot-spots.tsv), post-plant spots
// with a role each, a head that keeps checking its angles, the defuse reaction, the idle watchdog,
// the defenders' split and its rebalancing, deaths called out (rotations with a reaction time), a
// contested site, the lurker, the retake and the site holds.
static partial class MultiplayerChecks
{
    static void BotHoldChecks()
    {
        BotAreaFiles();
        BotPostPlantSpots();
        BotDefuseReaction();
        BotIdleWatchdog();
        BotSplitShapes();
        BotDeathCallouts();
        BotContestedSite();
        BotRetakePlay();
        BotSiteHolds();
    }

    // A synthetic area round a point: two ways in (north and east of it), spots on a grid round it;
    // a block south-west of the centre hides the bomb from the spots behind it.
    static SpotArea SyntheticArea(string key, double cx, double cy)
    {
        var entrances = new[]
        {
            new SpotEntrance(0, [cx, cy + 1300, 0], [cx, cy + 2600, 0], 5200, 0, [cx + 500, cy + 2700, 0]),
            new SpotEntrance(1, [cx + 1300, cy, 0], [cx + 2600, cy, 0], 9000, 1, [cx + 2700, cy - 500, 0]),
        };
        var spots = new List<HoldSpot>();
        for (var x = -2100; x <= 2100; x += 300)
            for (var y = -2100; y <= 2100; y += 300)
            {
                var d = Math.Sqrt(x * x + y * y);
                if (d < 250 || d > 2400) continue;
                var bomb = !(x < -400 && y < -400);
                var mask = (y > -300 ? 1 : 0) | (x > -300 ? 2 : 0);
                if (!bomb && mask == 0) continue;
                spots.Add(new HoldSpot([cx + x, cy + y, 0], bomb, mask, Math.Abs(x) + Math.Abs(y) > 1800 ? 3 : 1, d * 1.2));
            }
        return new SpotArea(key, "done", entrances, spots);
    }

    static void BotAreaFiles()
    {
        var text = "AIMMOD_SPOTS_1\t5\narea\tbomb-3\tdone\t2\t3\nentrance\tbomb-3\t0\t10\t1300\t0\t10\t2600\t0\t5200\t0\nlurk\tbomb-3\t0\t500\t2700\t0\n"
                   + "entrance\tbomb-3\t1\t1300\t0\t0\t2600\t0\t0\t9000\t1\nspot\tbomb-3\t400\t500\t0\t1\t3\t2\t700\nspot\tbomb-3\t-900\t-900\t0\t0\t1\t3\t1500\nspot\tbomb-3\tx\t1\t2\t1\t1\t1\t1\n"
                   + "area\tsite-A\tgrowing\t0\t0\narea\tbad\tmaybe\t0\t0\n";
        var areas = BotAreas.Parse(text);
        Check(areas is { Count: 2 } && areas["bomb-3"] is { Done: true, Entrances.Count: 2, Spots.Count: 2 } a && a.Entrances[0].Lurk is [500, 2700, 0] && a.Spots[0].SeesBomb && a.Spots[0].Sees(1) && !a.Spots[1].SeesBomb
              && !areas["site-A"].Done, "bot-spots.tsv: areas, their ways in (with a lurk spot), and spots with what they see (bad rows skipped)");
        Check(BotAreas.Parse("AIMMOD_SPOTS_2\t1\n") is null, "bot-spots.tsv needs its header");
        var rows = BotAreas.Format([new AreaRequest("bomb-3", [-3000.4, 10, 40], 250, 2400, 1200, [[0, 3000, 40], [3000, 0, 60]])]);
        Check(rows == "area\tbomb-3\t-3000\t10\t40\t250\t2400\t1200\nfrom\tbomb-3\t0\t3000\t40\nfrom\tbomb-3\t3000\t0\t60\n", "The orders ask for an area and where the other side comes from");
        var (rmin, rmax, entry) = BotAreas.Radii(new ObjectiveZone("bomb_site", "any", "B", [0, 0, 0], [1760, 1472, 384]));
        Check(rmin == 250 && rmax is > 3600 and < 3700 && entry is > 2000 and < 2100, "An area reaches further round a bigger site");
        Check(BotAreas.Key("site-A 1") == "site-A-1", "Area keys are made safe for the bridge");
    }

    // A planted bomb on B with four Terrorist bots on it: each its own spot, out of one grenade's reach
    // of the others, each seeing the bomb or a way in, one per role; their heads keep moving.
    static (CsMatch Cs, CsView Planted, long T) PlantedMatch(string[] ts, string[] cts, long t0)
    {
        var map = BotMap();
        var teams = ts.ToDictionary(x => x, _ => 1);
        foreach (var c in cts) teams[c] = 2;
        var cs = new CsMatch(ts.Concat(cts).ToList(), t0, 12, true, map, teams);
        cs.Tick(t0 + CsRules.FreezeMs + 1);
        var t = t0 + CsRules.FreezeMs + 30_000;
        var planted = cs.View() with { Phase = "planted", Bomb = cs.View().Bomb with { State = "planted", Position = [-3000, 0, 40], Carrier = null, Site = "B", ExplodesAt = t + 35_000 } };
        return (cs, planted, t);
    }

    static void BotPostPlantSpots()
    {
        string[] ts = ["t1", "t2", "t3", "t4"];
        var (_, planted, t) = PlantedMatch(ts, ["c1"], 60_000_000);
        var map = BotMap();
        var area = SyntheticArea("bomb-" + planted.Round, -3000, 0);
        var areas = new Dictionary<string, SpotArea> { [area.Key] = area };
        var brain = new BotBrain(seed: 41);
        brain.Reset("cs#post-spots");
        var pos = ts.ToDictionary(x => x, x => new[] { -3000 + Array.IndexOf(ts, x) * 60.0, 0 });
        BotWorld World(long now) => new(now, LobbyModes.Cs, ts.Select(x => (x, BotSkills.Normal)).ToList(),
            ts.Select((x, i) => new BotPlayer(x, i, pos[x][0], pos[x][1], 164, 1, true, 0)).Append(new BotPlayer("c1", 9, 0, 3000, 164, 2, true, 0)).ToList(),
            ts.ToDictionary(x => x, x => new BotSight(now, pos[x][0], pos[x][1], 100, 90, new HashSet<int>(), 0, 0, false, 2500)), planted, map, [], null, areas);
        var step = brain.Step(World(t));
        Check(step.Areas is { Count: >= 3 } req && req[0].Key == area.Key && req[0].Sources.Count == 2 && req.Any(r => r.Key == "site-A"),
            "After the plant the brain asks for the bomb's area first (from the defenders' spawn and the other site), and the sites'");
        var slots = brain.HoldSlots("T");
        var spots = slots.Select(x => x.Spot).ToList();
        var apart = spots.SelectMany((a, i) => spots.Skip(i + 1).Select(b => BotAreas.Dist2(a, b))).Min();
        var seeing = slots.All(x => area.Spots.Any(s => s.At[0] == x.Spot[0] && s.At[1] == x.Spot[1] && (s.SeesBomb || s.Mask != 0)) || x.Role == "lurk");
        Check(slots.Count == 4 && apart >= 600 && seeing && slots.Select(x => x.Role).Distinct().Count() == 4,
            FormattableString.Invariant($"Post-plant: four bots, four distinct spots ({apart:0} cm apart at least), each seeing the bomb or a way in, one role each ({string.Join(", ", slots.Select(x => x.Role))})"));
        Check(slots.Any(x => x.Role == "bomb" && x.SeesBomb) && slots.Any(x => x.Role == "entrance 0") && slots.Any(x => x.Role == "lurk" && x.Spot is [-2500, 2700, 0]),
            "Roles: one on the bomb, one on the defenders' main way in, a lurker out by it (four bots)");
        Check(step.Orders.All(o => o.Role!.StartsWith("post-plant (", StringComparison.Ordinal) && o.Goal is { } g && BotAreas.Dist2(g, [-3000, 0]) > 200), "and every bot walks off the bomb to its spot");
        // At their spots: the heads keep moving over their angles, never at the floor or a wall.
        foreach (var s in slots) pos[s.Member] = [s.Spot[0], s.Spot[1]];
        var faces = ts.ToDictionary(x => x, _ => new List<double[]>());
        for (var i = 1; i <= 120; i++)
            foreach (var o in brain.Step(World(t + i * 100)).Orders)
                if (o.Face is { } f) faces[o.Member].Add(f);
        bool Varied(string x) => faces[x].Count > 60 && faces[x].Select(p => Math.Round(Math.Atan2(p[1] - pos[x][1], p[0] - pos[x][0]) * 180 / Math.PI)).Distinct().Count() >= 4;
        Check(ts.All(Varied), "Holding, every bot's look keeps moving (its angles, a few degrees of drift): no dead stare ("
            + string.Join(", ", ts.Select(x => x + " " + faces[x].Count + " looks, " + faces[x].Select(p => Math.Round(Math.Atan2(p[1] - pos[x][1], p[0] - pos[x][0]) * 180 / Math.PI)).Distinct().Count() + " ways")) + ")");
        Check(ts.All(x => faces[x].All(f => BotAreas.Dist2(f, pos[x]) > 300)), "and it never looks at a point at its feet");
        var mainWay = slots.First(x => x.Role == "entrance 0");
        var onWay = faces[mainWay.Member].Count(f => AngleTo(pos[mainWay.Member], f, mainWay.Primary) < 8);
        Check(onWay > faces[mainWay.Member].Count / 3, "The one holding the main way in looks at it most of the time");
        Check(brain.IdleRePlans == 0, "Bots with a clear view ahead never trip the idle watchdog");
        // Late, outnumbered: they play safe (far, walled-in spots, crouched).
        var late = planted with { Bomb = planted.Bomb with { ExplodesAt = t + 12_000 + 8000 } };
        var lateBrain = new BotBrain(seed: 42);
        lateBrain.Reset("cs#post-late");
        BotWorld Late(long now) => World(now) with { Cs = late with { Bomb = late.Bomb with { ExplodesAt = now + 11_000 } } };
        var safe = lateBrain.Step(Late(t));
        Check(safe.Orders.All(o => o.Role!.Contains("playing safe", StringComparison.Ordinal)) && lateBrain.HoldSlots("T").Count(x => x.Role == "passive") >= 2,
            "With too little time left for a defuse without a kit, the Terrorists hold safe angles");
    }

    static double AngleTo(double[] from, double[] a, double[] b) =>
        Math.Abs(Aim.Wrap((Math.Atan2(a[1] - from[1], a[0] - from[0]) - Math.Atan2(b[1] - from[1], b[0] - from[0])) * 180 / Math.PI));

    // A defuse starts: after a moment the nearest Terrorists swing on the bomb; a Hard one burns it.
    static void BotDefuseReaction()
    {
        string[] ts = ["t1", "t2", "t3"];
        var (cs, planted, t) = PlantedMatch(ts, ["c1"], 61_000_000);
        var map = BotMap();
        var area = SyntheticArea("bomb-" + planted.Round, -3000, 0);
        var brain = new BotBrain(seed: 43);
        brain.Reset("cs#defuse");
        var at = new Dictionary<string, double[]> { ["t1"] = [-3000, 1800], ["t2"] = [-1500, -300], ["t3"] = [-4800, 1500] };
        var skills = new Dictionary<string, string> { ["t1"] = BotSkills.Hard, ["t2"] = BotSkills.Normal, ["t3"] = BotSkills.Normal };
        CsView View(bool defusing, long now) => planted with
        {
            Players = planted.Players.Select(p => p.Member == "t1" ? p with { Grenades = [GrenadeRules.Molotov] } : p).ToArray(),
            Bomb = defusing ? planted.Bomb with { Defuser = "c1", DefuseDoneAt = t + 9000 } : planted.Bomb,
        };
        BotWorld World(long now, bool defusing) => new(now, LobbyModes.Cs, ts.Select(x => (x, skills[x])).ToList(),
            ts.Select((x, i) => new BotPlayer(x, i, at[x][0], at[x][1], 164, 1, true, 0)).Append(new BotPlayer("c1", 9, -3000, 60, 164, 2, true, 0)).ToList(),
            ts.ToDictionary(x => x, x => new BotSight(now, at[x][0], at[x][1], 100, 0, new HashSet<int>(), 0, 0, false, 2500)), View(defusing, now), map, [], null,
            new Dictionary<string, SpotArea> { [area.Key] = area });
        brain.Step(World(t, false));
        var started = brain.Step(World(t + 100, true));
        Check(started.Orders.All(o => !o.Role!.Contains("swinging", StringComparison.Ordinal)) || started.Orders.Count(o => o.Role!.Contains("swinging", StringComparison.Ordinal)) <= 1,
            "The moment a defuse starts, the bots haven't reacted yet");
        var react = brain.Step(World(t + 800, true));
        var swing = react.Orders.Where(o => o.Role!.Contains("swinging on the defuse", StringComparison.Ordinal)).ToList();
        Check(swing.Count == 2 && swing.All(o => o.Member is "t1" or "t2") && swing.All(o => o.Face is { } f && Math.Abs(f[0] + 3000) < 1 && Math.Abs(f[1]) < 1),
            "Then the two nearest swing on the defuse, looking at the bomb");
        Check(react.Utility is { } u && u.Any(r => r.Bot == "t1" && r.Kind == GrenadeRules.Molotov && Math.Abs(r.Target[0] + 3000) < 1),
            "and the Hard one throws its molotov on the bomb");
        Check(react.Orders.First(o => o.Member == "t3").Role!.Contains("defuse heard", StringComparison.Ordinal), "The far one keeps its spot, an ear on the bomb");
        _ = cs;
    }

    // The idle watchdog: a bot still, the same way, a wall in its face for 2.5 s re-plans.
    static void BotIdleWatchdog()
    {
        string[] ts = ["t1", "t2"];
        var (_, planted, t) = PlantedMatch(ts, ["c1"], 62_000_000);
        var map = BotMap();
        var area = SyntheticArea("bomb-" + planted.Round, -3000, 0);
        var brain = new BotBrain(seed: 44);
        brain.Reset("cs#idle");
        var at = new Dictionary<string, double[]> { ["t1"] = [-3000, 300], ["t2"] = [-1500, -300] };
        double look = 2500;
        BotWorld World(long now) => new(now, LobbyModes.Cs, ts.Select(x => (x, BotSkills.Normal)).ToList(),
            ts.Select((x, i) => new BotPlayer(x, i, at[x][0], at[x][1], 164, 1, true, 0)).Append(new BotPlayer("c1", 9, 0, 3000, 164, 2, true, 0)).ToList(),
            ts.ToDictionary(x => x, x => new BotSight(now, at[x][0], at[x][1], 100, 45, new HashSet<int>(), 0, 0, false, x == "t1" ? look : 2500)), planted, map, [], null,
            new Dictionary<string, SpotArea> { [area.Key] = area });
        brain.Step(World(t));
        var first = brain.HoldSlots("T").First(x => x.Member == "t1");
        at["t1"] = [first.Spot[0], first.Spot[1]];
        look = 80;
        var fired = -1L; BotStep? after = null;
        for (var i = 1; i <= 40 && fired < 0; i++)
        {
            var step = brain.Step(World(t + i * 100));
            if (brain.IdleRePlans > 0) { fired = i * 100; after = step; }
        }
        Check(fired is >= 2500 and <= 2800, "Still at a wall for 2.5 s, the idle watchdog fires (" + fired + " ms)");
        Check(brain.Notes.Any(n => n.Contains("stood still facing a wall", StringComparison.Ordinal)) && after!.Orders.First(o => o.Member == "t1") is { Face: null } o && o.Role!.Contains("looking round", StringComparison.Ordinal),
            "It logs it and looks round the open ways for a moment");
        var again = brain.Step(World(t + fired + 100));
        var moved = brain.HoldSlots("T").First(x => x.Member == "t1");
        Check(BotAreas.Dist2(moved.Spot, first.Spot) >= BotHolds.BannedCm && again.Orders.First(o => o.Member == "t1").Goal is not null,
            "and its hold is planned again: a new spot, the old one banned for the round");
    }

    // The defenders' split: its shapes and the read from earlier rounds; rebalanced as defenders die.
    static void BotSplitShapes()
    {
        var even = BotSplits.Read([], 2, 0);
        Check(BotSplits.Shape(5, even, false).Name == "2-1-2" && BotSplits.Shape(4, even, false).Name == "2-2" && BotSplits.Shape(2, even, false).Name == "1-1",
            "Even read: 2-1-2 with five, 2-2 with four, 1-1 with two");
        var three = BotSplits.Shape(3, even, false);
        Check(three.Counts[0] == 2 && three.Counts[1] == 1, "Three: two where the read points (the favoured site), one alone on the other");
        Check(BotSplits.Read([1], 2, 0)[1] < BotSplits.Heavy, "One hit on a site isn't a read yet");
        var hitB = BotSplits.Read([0, 1, 1, 1], 2, 0);
        Check(hitB[1] >= BotSplits.Heavy && BotSplits.Shape(5, hitB, false).Name == "2-3" && BotSplits.Shape(4, hitB, false).Name == "1-3",
            "After the Terrorists hit B in most of the last rounds: 3-2 (and 3-1) heavy on B, nobody forward");
        Check(BotSplits.Shape(5, even, true).Counts.Max() == 3, "On their own eco the defenders stack");
        var sites = new[] { new CsSiteView("A", 3000, 0, 60), new CsSiteView("B", -3000, 0, 60) };
        var five = BotSplits.Assign(BotSplits.Shape(5, even, false), [("c1", [2900, 0]), ("c2", [3100, 0]), ("c3", [0, 0]), ("c4", [-2900, 0]), ("c5", [-3100, 0])], null, sites);
        Check(five["c1"] == 0 && five["c2"] == 0 && five["c4"] == 1 && five["c5"] == 1 && five["c3"] == -1, "Each defender to the site nearest it, the one in between forward");
        var four = BotSplits.Assign(BotSplits.Shape(4, even, false), [("c2", [3100, 0]), ("c3", [0, 0]), ("c4", [-2900, 0]), ("c5", [-3100, 0])], five, sites);
        Check(four.Count(kv => kv.Value == 0) == 2 && four.Count(kv => kv.Value == 1) == 2 && four["c2"] == 0 && four["c3"] == 0 && four["c4"] == 1,
            "One dies on A: rebalanced 2-2, the forward one fills A, the others keep their sites");
    }

    // A CT dies on A: after the side's reaction time the B players rotate (or reposition); a full execute pulls everyone but one.
    static void BotDeathCallouts()
    {
        var map = BotMap();
        const long t0 = 63_000_000;
        string[] cts = ["c1", "c2", "c3", "c4", "c5"];
        var teams = cts.ToDictionary(x => x, _ => 2);
        teams["t1"] = 1; teams["t2"] = 1; teams["t3"] = 1;
        var cs = new CsMatch(["t1", "t2", "t3", .. cts], t0, 12, true, map, teams);
        cs.Tick(t0 + CsRules.FreezeMs + 1);
        var t = t0 + CsRules.FreezeMs + 2000;
        var at = new Dictionary<string, double[]> { ["c1"] = [2900, 100], ["c2"] = [3100, -100], ["c3"] = [0, 1500], ["c4"] = [-2900, 100], ["c5"] = [-3100, -100] };
        var alive = cts.ToHashSet();
        var events = new List<CombatEvent>();
        var brain = new BotBrain(seed: 45);
        brain.Reset("cs#death");
        BotWorld World(long now) => new(now, LobbyModes.Cs, cts.Select(x => (x, BotSkills.Normal)).ToList(),
            cts.Select((x, i) => new BotPlayer(x, i, at[x][0], at[x][1], 164, 2, alive.Contains(x), 0))
                .Concat([new BotPlayer("t1", 7, 3600, 900, 164, 1, true, 0), new BotPlayer("t2", 8, 3700, 1000, 164, 1, true, 0), new BotPlayer("t3", 9, 0, -3000, 164, 1, true, 0)]).ToList(),
            cts.Where(alive.Contains).ToDictionary(x => x, x => new BotSight(now, at[x][0], at[x][1], 100, 0, new HashSet<int>(), 0, 0, false, 2500)),
            cs.View() with { Players = cs.View().Players.Select(p => p with { Alive = p.Side == CsRules.T || alive.Contains(p.Member) }).ToArray() }, map, events.ToArray());
        string Role(BotStep s, string m) => s.Orders.First(o => o.Member == m).Role ?? "";
        var before = brain.Step(World(t));
        Check(brain.CtSplit is { } sp && sp["c1"] == 0 && sp["c2"] == 0 && sp["c4"] == 1 && sp["c5"] == 1 && sp["c3"] == -1, "Before: 2-1-2");
        // c1 dies on A to two Terrorists (both hurt it).
        var died = t + 100;
        events.Add(new CombatEvent(1, "damage", died - 600, "c1", "t1", 30, false, 70, null));
        events.Add(new CombatEvent(2, "damage", died - 300, "c1", "t2", 40, false, 30, null));
        events.Add(new CombatEvent(3, "death", died, "c1", "t2", 30, true, 0, 100));
        alive.Remove("c1");
        var soon = brain.Step(World(died + 200));
        Check(brain.KnowledgeOf(CsRules.CT).Deaths.SingleOrDefault() is { Victim: "c1", Count: 2, Site: "A" } d && d.Killers.Count == 2,
            "c1's death is called out: on A, killed by two Terrorists");
        Check(Role(soon, "c4").StartsWith("anchor", StringComparison.Ordinal) && Role(soon, "c5").StartsWith("anchor", StringComparison.Ordinal) && Role(soon, "c3") == "rotator",
            "Right after the death, before the side reacted, the others still hold");
        var later = brain.Step(World(died + 1200));
        double[] GoalOf(BotStep s, string m) => s.Orders.First(o => o.Member == m).Goal ?? [at[m][0], at[m][1]];
        Check(Role(later, "c4").StartsWith("rotate", StringComparison.Ordinal) && GoalOf(later, "c4")[0] > 1000 && GoalOf(later, "c3")[0] > 1000 && Role(later, "c5").StartsWith("anchor", StringComparison.Ordinal) && GoalOf(later, "c5")[0] < -1000,
            "After the reaction time the forward one fills A and a B player rotates there (three against the two called), one stays on B: " + string.Join(", ", cts.Where(alive.Contains).Select(m => m + " " + Role(later, m))));
        Check(brain.CtSplit is { } after && after.Count == 4 && after.Values.Count(v => v == 0) == 2 && after.Values.Count(v => v == 1) == 2,
            "and the split is rebalanced to 2-2 over the four left");
        // The call fades: nothing more seen there, it is forgotten after a while.
        Check(!brain.KnowledgeOf(CsRules.CT).FreshDeaths(died + TeamKnowledge.DeathMs + 100).Any() && brain.KnowledgeOf(CsRules.CT).FreshDeaths(died + 5000).Any(),
            "A death call fades over time");
        // A full execute: c2 dies on A too, three there: everyone but one anchor on B goes, setting up to retake.
        events.Add(new CombatEvent(4, "damage", died + 1500, "c2", "t3", 30, false, 70, null));
        events.Add(new CombatEvent(5, "death", died + 1600, "c2", "t1", 70, true, 0, 100));
        alive.Remove("c2");
        var exec = brain.Step(World(died + 3000));
        var going = cts.Where(alive.Contains).Where(m => Role(exec, m).StartsWith("rotate", StringComparison.Ordinal)).ToList();
        Check(going.Count == 2 && cts.Where(alive.Contains).Count(m => Role(exec, m).StartsWith("anchor", StringComparison.Ordinal)) == 1 && going.All(m => Role(exec, m) == "rotate (retake setup)"),
            "Two down on A (a full execute): all but one anchor go over, setting up short of the site to retake: " + string.Join(", ", cts.Where(alive.Contains).Select(m => m + " " + Role(exec, m))));
        // The dead are no threat to watch: no bot faces where a dead enemy fell.
        var k = new TeamKnowledge();
        k.Saw("x", [1, 2, 3], 10);
        k.Forget("x");
        Check(!k.Fresh(20).Any(), "A dead enemy is forgotten at once");
    }

    // Two Terrorists die on their site before the call: the site is contested, they go to the other.
    static void BotContestedSite()
    {
        var map = BotMap();
        const long t0 = 64_000_000;
        string[] ts = ["t1", "t2", "t3", "t4"];
        var teams = ts.ToDictionary(x => x, _ => 1);
        teams["c1"] = 2; teams["c2"] = 2;
        var cs = new CsMatch([.. ts, "c1", "c2"], t0, 12, true, map, teams);
        cs.Tick(t0 + CsRules.FreezeMs + 1);
        var t = t0 + CsRules.FreezeMs + 1000;
        var brain = new BotBrain(seed: 46);
        brain.Reset("cs#contested");
        var alive = ts.ToHashSet();
        var events = new List<CombatEvent>();
        var at = ts.ToDictionary(x => x, x => new[] { Array.IndexOf(ts, x) * 100.0, -2000 });
        BotWorld World(long now) => new(now, LobbyModes.Cs, ts.Select(x => (x, BotSkills.Normal)).ToList(),
            ts.Select((x, i) => new BotPlayer(x, i, at[x][0], at[x][1], 164, 1, alive.Contains(x), 0)).Concat([new BotPlayer("c1", 8, 0, 3000, 164, 2, true, 0), new BotPlayer("c2", 9, 0, 3000, 164, 2, true, 0)]).ToList(),
            ts.Where(alive.Contains).ToDictionary(x => x, x => new BotSight(now, at[x][0], at[x][1], 100, 0, new HashSet<int>(), 0, 0, false, 2500)),
            cs.View() with { Players = cs.View().Players.Select(p => p with { Alive = p.Side == CsRules.CT || alive.Contains(p.Member) }).ToArray() }, map, events.ToArray());
        brain.Step(World(t));
        var plan = brain.PlanFor(CsRules.T)!;
        var lurker = BotStrategy.Lurker(plan with { Style = BotStyles.Default }, null);
        Check(lurker is not null && BotStrategy.TJob(plan with { Style = BotStyles.Default, ExecuteAt = t + 20_000 }, lurker, false, t, cs.View().Sites!, null).Role == "lurk",
            "On a default with four, one Terrorist lurks out towards the other site");
        var siteName = cs.View().Sites![plan.Site].Name;
        var site = cs.View().Sites![plan.Site];
        // Two die there.
        foreach (var (m, id) in new[] { ("t1", 1L), ("t2", 2L) })
        {
            at[m] = [site.X + 100 * id, site.Y];
            events.Add(new CombatEvent(id, "death", t + 100 * id, m, "c1", 100, true, 0, 100));
            alive.Remove(m);
        }
        brain.Step(World(t + 1500));
        var now = brain.PlanFor(CsRules.T)!;
        Check(now.Site != plan.Site && brain.Notes.Any(n => n.Contains(siteName + " is contested", StringComparison.Ordinal)), "Two Terrorists down on their site before the call: it's contested, they go to the other site");
    }

    // The retake: gather by a way in, go in one behind the other, one defuses, the others cover; no
    // retake when the clock can't allow a defuse.
    static void BotRetakePlay()
    {
        string[] cts = ["c1", "c2", "c3"];
        var (_, planted, t) = PlantedMatch(["t1"], cts, 65_000_000);
        var map = BotMap();
        var area = SyntheticArea("bomb-" + planted.Round, -3000, 0);
        var brain = new BotBrain(seed: 47);
        brain.Reset("cs#retake");
        var at = new Dictionary<string, double[]> { ["c1"] = [-2800, 3500], ["c2"] = [-2600, 3500], ["c3"] = [3000, 0] };
        CsView View(long explodes) => planted with { Bomb = planted.Bomb with { ExplodesAt = explodes } };
        var explodesAt = t + 35_000;
        BotWorld World(long now) => new(now, LobbyModes.Cs, cts.Select(x => (x, BotSkills.Normal)).ToList(),
            cts.Select((x, i) => new BotPlayer(x, i, at[x][0], at[x][1], 164, 2, true, 0)).Append(new BotPlayer("t1", 9, -3000, 2000, 164, 1, true, 0)).ToList(),
            cts.ToDictionary(x => x, x => new BotSight(now, at[x][0], at[x][1], 100, 0, new HashSet<int>(), 0, 0, false, 2500)), View(explodesAt), map, [], null,
            new Dictionary<string, SpotArea> { [area.Key] = area });
        var gather = brain.Step(World(t));
        Check(gather.Orders.All(o => o.Role == "retake (gathering)") && gather.Orders.First(o => o.Member == "c1").Goal is [-3000, 2600, 0], "Retake: they gather out of sight by the way in nearest them");
        at["c1"] = [-3000, 2600]; at["c2"] = [-2900, 2700];
        var go = brain.Step(World(t + 500));
        Check(go.Orders.Count(o => o.Role == "retake (waiting to trade)") >= 1 && go.Orders.Any(o => o.Role is "retake (entry)" or "retake (defuse)"), "Two gathered: they go in, one close behind the other");
        var all = brain.Step(World(t + 3000));
        Check(all.Orders.Count(o => o.Role == "retake (defuse)") == 1 && all.Orders.Count(o => o.Role is "retake (cover)" or "retake (entry)") == 2
              && all.Orders.Where(o => o.Role != "retake (defuse)").All(o => o.Goal is { } g && area.Spots.Any(s => s.SeesBomb && s.At[0] == g[0] && s.At[1] == g[1])),
            "One defuses, the others cover from spots that see the bomb");
        explodesAt = t + 3000 + 6000;
        var late = brain.Step(World(t + 3000));
        Check(late.Orders.Where(o => BotAreas.Dist2([at[o.Member][0], at[o.Member][1]], [-3000, 0]) > 1500).All(o => o.Role == "retake (saving: no time to defuse)"),
            "With no time for a defuse, the far ones save instead");
    }

    // Before the plant: the anchors of a site take distinct spots that watch its ways in.
    static void BotSiteHolds()
    {
        var map = BotMap();
        const long t0 = 66_000_000;
        string[] cts = ["c1", "c2", "c3", "c4"];
        var teams = cts.ToDictionary(x => x, _ => 2);
        teams["t1"] = 1;
        var cs = new CsMatch(["t1", .. cts], t0, 12, true, map, teams);
        cs.Tick(t0 + CsRules.FreezeMs + 1);
        var t = t0 + CsRules.FreezeMs + 500;
        var areas = new Dictionary<string, SpotArea> { ["site-A"] = SyntheticArea("site-A", 3000, 0), ["site-B"] = SyntheticArea("site-B", -3000, 0) };
        var brain = new BotBrain(seed: 48);
        brain.Reset("cs#holds");
        var at = new Dictionary<string, double[]> { ["c1"] = [2900, 0], ["c2"] = [3100, 0], ["c3"] = [-2900, 0], ["c4"] = [-3100, 0] };
        var step = brain.Step(new BotWorld(t, LobbyModes.Cs, cts.Select(x => (x, BotSkills.Hard)).ToList(),
            cts.Select((x, i) => new BotPlayer(x, i, at[x][0], at[x][1], 164, 2, true, 0)).Append(new BotPlayer("t1", 9, 0, -3000, 164, 1, true, 0)).ToList(),
            cts.ToDictionary(x => x, x => new BotSight(t, at[x][0], at[x][1], 100, 0, new HashSet<int>(), 0, 0, false, 2500)), cs.View(), map, [], null, areas));
        var a = brain.HoldSlots("CT-A");
        Check(a.Count == 2 && BotAreas.Dist2(a[0].Spot, a[1].Spot) >= 500 && a.All(x => x.Entrance >= 0 && areas["site-A"].Spots.Any(s => s.At[0] == x.Spot[0] && s.At[1] == x.Spot[1] && s.Sees(x.Entrance))),
            "Site A's two anchors take distinct spots, each watching a way in");
        Check(step.Orders.Where(o => at[o.Member][0] > 0).All(o => o.Role!.StartsWith("anchor (", StringComparison.Ordinal)), "and their roles say which: " + string.Join(", ", step.Orders.Select(o => o.Role)));
    }
}

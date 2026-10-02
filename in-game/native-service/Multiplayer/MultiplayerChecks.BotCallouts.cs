namespace AimMod.InGame.Multiplayer;

// Bot callouts in chat (BotCallouts.cs): the line for each event, places from the map, how chatty
// each difficulty is, the rate limit and de-duplication, rare round-end lines to everyone, and the
// events the brain raises (spotted, deaths, the plan).
static partial class MultiplayerChecks
{
    static void BotCalloutChecks()
    {
        BotCalloutTexts();
        BotCalloutLimits();
        BotCalloutEvents();
    }

    static void BotCalloutTexts()
    {
        bool All(BotEvent e, Func<string, bool> ok) => Enumerable.Range(0, 24).All(v => BotCallouts.Text(e, v) is { Length: > 0 and < 60 } t && ok(t));
        bool Has(string t, params string[] any) => any.Any(a => t.Contains(a, StringComparison.OrdinalIgnoreCase));
        Check(All(new("b", "spotted", 2, null, "B site"), t => t.Contains('2') && t.Contains("B site")), "Spotted: how many and where (\"2 B site\")");
        Check(All(new("b", "spotted", 1, null, "long", "low"), t => t.Contains("long") && Has(t, "low", "lit")) && All(new("b", "spotted", 1, null, null), t => !t.Contains("null")),
            "One spotted, low (\"One long, he's low\"); without a place it still reads");
        Check(All(new("b", "last", 1, null, "long"), t => Has(t, "last") && t.Contains("long")), "The last one (\"Last one long\")");
        Check(All(new("b", "utility", 0, null, "A main", GrenadeRules.Smoke), t => Has(t, "smok") && t.Contains("A main")) && All(new("b", "utility", 0, null, null, GrenadeRules.Flash), t => Has(t, "flash"))
              && All(new("b", "utility", 0, null, "the bomb", GrenadeRules.Molotov), t => t.Contains("bomb")), "Utility: \"Smoking A main\", \"Flashing out\", \"Molly on the bomb\"");
        Check(All(new("b", "plan", 0, null, null, "go A"), t => t.Contains('A')) && All(new("b", "plan", 0, null, null, "lurk B"), t => Has(t, "lurk") && t.Contains('B'))
              && All(new("b", "plan", 0, null, null, "split B"), t => Has(t, "split")) && All(new("b", "plan", 0, null, null, "anchor A"), t => t.Contains('A'))
              && All(new("b", "plan", 0, null, null, "rush B"), t => Has(t, "rush")), "The plan: going A, lurking B, a split, a rush, holding a site");
        Check(All(new("b", "rotate", 0, null, "B"), t => t.Contains('B') && Has(t, "rotat", "coming", "on my way")), "Rotating B");
        Check(All(new("b", "planting", 0, null, "A"), t => Has(t, "planting")) && All(new("b", "planted", 0, null, "A"), t => Has(t, "plant", "down") && t.Contains('A'))
              && All(new("b", "dropped", 0, null, "mid"), t => t.Contains("mid") && Has(t, "bomb")) && All(new("b", "defusing"), t => Has(t, "defus", "bomb"))
              && All(new("b", "defuse-heard"), t => Has(t, "defus", "bomb")), "The bomb: \"Planting A\", \"Planted A\", \"Bomb down mid\", \"Defusing, cover me\", \"They're defusing!\"");
        Check(All(new("b", "died", 2, null, "A", "low"), t => t.Contains('A') && t.Contains("2 there") && Has(t, "low", "lit", "hit one")), "A death: \"I died A, 2 there, one low\"");
        Check(All(new("b", "low", 25), t => Has(t, "low", "lit")) && All(new("b", "decoy", 0, null, "B"), t => Has(t, "decoy") && t.Contains('B')) && All(new("b", "save"), t => Has(t, "sav")),
            "Low, a decoy called out, saving");
        // Places: the map's callout zone, its site, near a site, a spawn, else mid.
        var map = BotMap();
        var named = map with { Zones = [.. map.Zones, new ObjectiveZone("callout", "any", "Long", [3500, -800, 0], [4500, 800, 300])] };
        Check(BotCallouts.Place(map, [3000, 0, 100]) == "A site" && BotCallouts.Place(map, [3000, 1200, 100]) == "A" && BotCallouts.Place(map, [0, -3000, 100]) == "T spawn"
              && BotCallouts.Place(map, [0, 0, 100]) == "mid" && BotCallouts.Place(named, [4000, 0, 100]) == "Long", "Places as players call them: a callout zone, a site, near a site, a spawn, mid");
    }

    static void BotCalloutLimits()
    {
        string Team(string m) => m.StartsWith('c') ? CsRules.CT : CsRules.T;
        // One line a bot every 3 s, whatever it has to say.
        var c = new BotCallouts(seed: 3);
        var said = new List<(long T, BotChat Line)>();
        var events = new[] { new BotEvent("c1", "died", 2, null, "A"), new BotEvent("c1", "defusing"), new BotEvent("c1", "save") };
        for (long t = 0; t <= 8000; t += 100)
            foreach (var line in c.Step(t, t == 0 ? events : [], Team, _ => BotSkills.Hard)) said.Add((t, line));
        Check(said.Count is 2 or 3 && said.Zip(said.Skip(1)).All(p => p.Second.T - p.First.T >= BotCallouts.PerBotMs) && said.All(x => x.Line.TeamOnly),
            "At most one line a bot every 3 s (callouts are team only); a stale one is dropped (" + said.Count + " said)");
        // The same line twice from a team within 5 s: once.
        var d = new BotCallouts(seed: 4);
        var lines = new List<BotChat>();
        for (long t = 0; t <= 3000; t += 100)
            lines.AddRange(d.Step(t, t == 0 ? [new BotEvent("c1", "died", 2, null, "B site"), new BotEvent("c2", "died", 2, null, "B site")] : t == 2000 ? [new BotEvent("c3", "died", 2, null, "B site")] : [],
                Team, _ => BotSkills.Hard, _ => "2 B site"));
        Check(lines.Count == 1 && lines[0].Text == "2 B site", "Two teammates calling the same thing within 5 s: one line in chat");
        var e = new BotCallouts(seed: 5);
        var both = new List<BotChat>();
        for (long t = 0; t <= 3000; t += 100)
            both.AddRange(e.Step(t, t == 0 ? [new BotEvent("c1", "died", 2, null, "B site"), new BotEvent("t1", "died", 2, null, "B site")] : [], Team, _ => BotSkills.Hard, _ => "2 B site"));
        Check(both.Count == 2, "The other team's chat is its own: both see the line");
        // Easy bots say less, and later.
        Check(BotCallouts.Chattiness("bot", BotSkills.Easy) < BotCallouts.Chattiness("bot", BotSkills.Hard) && BotCallouts.Typing(BotSkills.Easy).Lo > BotCallouts.Typing(BotSkills.Hard).Hi,
            "Easy bots are less chatty and type slower");
        int Said(string skill, out long firstAfter)
        {
            var cc = new BotCallouts(seed: 6);
            var count = 0; firstAfter = -1;
            for (var i = 0; i < 60; i++)
                for (long t = 0; t < 4000; t += 100)
                {
                    var now = i * 10_000 + t;
                    var got = cc.Step(now, t == 0 ? [new BotEvent("b" + i % 7, "spotted", 1, null, "mid")] : [], Team, _ => skill).Count;
                    if (got > 0 && firstAfter < 0) firstAfter = t;
                    count += got;
                }
            return count;
        }
        var easy = Said(BotSkills.Easy, out var easyFirst);
        var hard = Said(BotSkills.Hard, out var hardFirst);
        Check(easy < hard && easyFirst >= BotCallouts.Typing(BotSkills.Easy).Lo && hardFirst <= BotCallouts.Typing(BotSkills.Hard).Hi,
            $"Over 60 sightings an easy bot called {easy}, a hard one {hard}; the easy one typed for {easyFirst} ms, the hard one {hardFirst} ms");
        // Round end: now and then, to everyone.
        var r = new BotCallouts(seed: 7);
        var bye = new List<BotChat>();
        for (var i = 0; i < 200; i++)
            for (long t = 0; t < 4000; t += 200)
                bye.AddRange(r.Step(i * 10_000 + t, t == 0 ? [new BotEvent("b" + i % 5, "round-end", 0, null, null, i % 2 == 0 ? "won" : "lost")] : [], Team, _ => BotSkills.Normal));
        Check(bye.Count is > 2 and < 70 && bye.All(b => !b.TeamOnly && b.Text is "nt" or "gg" or "nice try" or "nice" or "nice one" or "ns" or "wp"),
            "At a round's end a bot says \"gg\" or \"nt\" to everyone, rarely (" + bye.Count + " in 200 rounds)");
    }

    // The brain raises the events: a fresh sighting (with how many), a bot's own death, the plan.
    static void BotCalloutEvents()
    {
        var map = BotMap();
        const long t0 = 67_000_000;
        var cs = new CsMatch(["t1", "t2", "c1"], t0, 12, true, map, new Dictionary<string, int> { ["t1"] = 1, ["t2"] = 1, ["c1"] = 2 });
        cs.Tick(t0 + CsRules.FreezeMs + 1);
        var t = t0 + CsRules.FreezeMs + 500;
        var brain = new BotBrain(seed: 71);
        brain.Reset("cs#callouts");
        var seen = new HashSet<int>();
        BotWorld World(long now) => new(now, LobbyModes.Cs, [("c1", BotSkills.Hard)],
            [new BotPlayer("t1", 0, 3100, 600, 164, 1, true, 0), new BotPlayer("t2", 1, 3200, 700, 164, 1, true, 0), new BotPlayer("c1", 2, 3000, -600, 164, 2, true, 0)],
            new Dictionary<string, BotSight> { ["c1"] = new(now, 3000, -600, 100, 90, seen, 0, 0, false, 2500) }, cs.View(), map, []);
        var quiet = brain.Step(World(t));
        Check(quiet.Events!.All(e => e.Kind != "spotted") && quiet.Events!.Any(e => e.Kind == "plan" && e.Bot == "c1" && e.Detail!.StartsWith("anchor", StringComparison.Ordinal)),
            "Going live, a defender says which site it holds; nothing spotted yet");
        seen.UnionWith([0, 1]);
        var spot = brain.Step(World(t + 100));
        Check(spot.Events!.SingleOrDefault(e => e.Kind == "spotted") is { Bot: "c1", Count: 2, Place: "A" }, "Two Terrorists come into view by A: \"2 A\"");
        Check(brain.Step(World(t + 200)).Events!.All(e => e.Kind != "spotted"), "and it isn't called again while they stay in view");
    }
}

using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

// The load gate and generated-arena validity: a live CS match once kept the previous map
// (its arena listed profiles out of KovaaK's section order) while the rounds ran on.
static partial class MultiplayerChecks
{
    // A port-style base: CRLF, KovaaK's grouped section order, a multi-line JSON map whose
    // lines look like profile keys, and a blank line before the map.
    const string PortBase = "Name=Synthetic Port\r\nPlayerCharacters=Player\r\nBotCharacters=target.bot\r\nTimelimit=60.0\r\nPlayerProfile=Player\r\nMapName=synthetic_port.map\r\nMapScale=3.8\r\nDescription=Synthetic\r\n\r\n"
        + "[Aim Profile]\r\nName=Default\r\nMinReactionTime=0.3\r\n\r\n[Bot Profile]\r\nName=target\r\nCharacterProfile=target\r\n\r\n"
        + "[Character Profile]\r\nName=Player\r\nMaxHealth=100.0\r\nWeaponProfileNames=Synthetic Gun;;;;;;;\r\nMaxSpeed=1000.0\r\nMainBBHeight=230.0\r\n\r\n"
        + "[Character Profile]\r\nName=target\r\nMaxSpeed=800.0\r\nMainBBHeight=200.0\r\nMainBBRadius=40.0\r\n\r\n"
        + "[Dodge Profile]\r\nName=Default\r\nMaxTargetDistance=2000.0\r\n\r\n[Weapon Profile]\r\nName=Synthetic Gun\r\nType=Hitscan\r\nTimeBetweenShots=0.5\r\nCategory=SemiAuto\r\n\r\n"
        + "[Map Data]\r\n{\r\n  \"name\": \"synthetic_port\",\r\n  \"Name=not a section\": 1,\r\n  \"gameObjects\": [\r\n    { \"type\": \"SpawnPoint\", \"location\": \"0, 0, 0\", \"rotation\": \"0, 0, 90\" }\r\n  ]\r\n}\r\n\r\n";

    static void LoadGate()
    {
        var content = new FakeContent();
        var choice = new ScenarioChoice("Synthetic Port", ContentLibrary.TextHash(PortBase), "synthetic_port", ContentLibrary.TextHash("m"), 60);
        static int Rank(string title) => Array.IndexOf(new[] { "[Aim Profile]", "[Bot Profile]", "[Character Profile]", "[Dodge Profile]", "[Sprint Ability Profile]", "[Weapon Profile]", "[Map Data]" }, title);
        foreach (var mode in LobbyModes.All)
        {
            var settings = LobbyRules.Apply(new LobbySettings(Scenario: choice), J(new { mode, movement = "cs", targetSize = 1.5 }), 2, content).Settings! with { Scenario = choice };
            var arena = MatchScenario.Generate(new(PortBase, settings));
            Check(MatchScenario.Validate(PortBase, arena).Count == 0 && MatchScenario.MapSectionOf(arena) == MatchScenario.MapSectionOf(PortBase)
                && arena.EndsWith(PortBase[PortBase.IndexOf("[Map Data]", StringComparison.Ordinal)..], StringComparison.Ordinal),
                "Every mode's arena validates and keeps the map (MapName, MapScale, [Map Data] byte for byte): " + mode);
            var ranks = arena.Replace("\r\n", "\n").Split('\n').Where(l => l.StartsWith('[') && l.EndsWith(']') && !l.Contains('"')).Select(Rank).Where(r => r >= 0).ToList();
            Check(ranks.SequenceEqual(ranks.Order()), "Arena sections are grouped in KovaaK's order, profiles before the map: " + mode);
        }
        Check(MatchScenario.Validate(PortBase, PortBase).Count == 0, "The base scenario itself validates");
        var reordered = PortBase.Replace("[Weapon Profile]\r\nName=Synthetic Gun\r\nType=Hitscan\r\nTimeBetweenShots=0.5\r\nCategory=SemiAuto\r\n\r\n", "")
            .Replace("[Aim Profile]", "[Weapon Profile]\r\nName=Synthetic Gun\r\nType=Hitscan\r\n\r\n[Aim Profile]");
        Check(MatchScenario.Validate(PortBase, reordered).Any(p => p.Contains("comes after", StringComparison.Ordinal)), "Out-of-order sections are reported");
        Check(MatchScenario.Validate(PortBase, PortBase.Replace("\"rotation\": \"0, 0, 90\"", "\"rotation\": \"0, 0, 91\"")).Any(p => p.Contains("[Map Data]", StringComparison.Ordinal))
            && MatchScenario.Validate(PortBase, PortBase.Replace("MapName=synthetic_port.map", "MapName=kovaim1.map")).Any(p => p.StartsWith("MapName", StringComparison.Ordinal))
            && MatchScenario.Validate(PortBase, PortBase.Replace("MapScale=3.8", "MapScale=1.0")).Any(p => p.StartsWith("MapScale", StringComparison.Ordinal)),
            "A changed map section, MapName or MapScale is reported");
        Check(MatchScenario.Validate(PortBase, PortBase.Replace("WeaponProfileNames=Synthetic Gun", "WeaponProfileNames=Missing Gun")).Any(p => p.Contains("Missing Gun", StringComparison.Ordinal)), "A weapon the file doesn't define is reported");
        Check(MatchScenario.MapOf(PortBase) == ("synthetic_port.map", 3.8), "The expected map comes from the scenario header");
        // Bisect variants: the base plus one part of the arena each, map untouched.
        var dm = LobbyRules.Apply(new LobbySettings(Scenario: choice), J(new { mode = "deathmatch" }), 2, content).Settings! with { Scenario = choice };
        var full = MatchScenario.Generate(new(PortBase, dm));
        var variants = MatchScenario.Bisect(PortBase, full);
        Check(variants.Count == 9 && variants.All(v => v.Name.StartsWith(MatchScenario.ProbePrefix, StringComparison.Ordinal) && v.Text.Contains("Name=" + v.Name, StringComparison.Ordinal) && MatchScenario.MapSectionOf(v.Text) == MatchScenario.MapSectionOf(PortBase))
            && variants.Select(v => v.Name).Distinct().Count() == 9, "Bisect variants are named AimMod Probe (never cleaned as match scenarios) and keep the map");
        Check(variants[0].Text == PortBase.Replace("Name=Synthetic Port", "Name=" + variants[0].Name), "Variant 00 is the base byte for byte apart from its name");
        Check(variants.All(v => MatchScenario.Validate(PortBase, v.Text).Count == 0)
            && variants[8].Text.Split('\n').Where(l => !l.StartsWith("Name=", StringComparison.Ordinal)).Order().SequenceEqual(full.Split('\n').Where(l => !l.StartsWith("Name=", StringComparison.Ordinal)).Order()),
            "Every variant validates, and the last one has everything the arena has");

        // What this machine's game shows against the round.
        const string sc = "AimMod Match - Synthetic";
        var expected = ("aim_map.map", (double?)3.8);
        GameScene Scene(string scenario = sc, string map = "aim_map", double? scale = 3.8, bool loading = false) => new(true, scenario, map, scale, false, false, loading, false);
        Check(MatchScenario.SameMap("aim_map", "aim_map.map") && MatchScenario.SameMap("AIM_MAP.map", "aim_map.map") && !MatchScenario.SameMap("kovaim1.map", "aim_map.map") && !MatchScenario.SameMap("", "aim_map.map"),
            "Map names match with or without the extension, ignoring case");
        Check(ScenarioLoader.SceneProblem(Scene(), sc, expected) is null && ScenarioLoader.SceneProblem(Scene(loading: true), sc, expected) == "loading"
            && ScenarioLoader.SceneProblem(null, sc, expected) is not null && ScenarioLoader.SceneProblem(Scene(scenario: "Other"), sc, expected) is not null,
            "The scene counts only when it shows the round's scenario, not loading");
        Check(ScenarioLoader.SceneProblem(Scene(map: "kovaim1.map"), sc, expected)!.Contains("kovaim1.map", StringComparison.Ordinal) && ScenarioLoader.SceneProblem(Scene(scale: 1), sc, expected)!.Contains("scale", StringComparison.Ordinal),
            "The live bug's state (right scenario, the previous map) is a load problem, and so is the wrong scale");

        // Host: no start until everyone is loaded; a failure waits for retry or abort.
        var (core, clock, advance) = Lobby();
        core.Join("p2", "Two");
        ReadyAll(core);
        core.RequireLoading = true;
        core.Apply("host", "start", default, content);
        core.Apply("host", "loaded", J(new { ok = true, attempt = 0 }), content);
        core.Apply("p2", "loaded", J(new { ok = false, reason = "KovaaK’s kept the map “kovaim1.map”.", attempt = 0 }), content);
        core.Tick();
        var m = core.Snapshot().Match!;
        Check(m.Phase == MatchPhases.Loading && m.LoadFailed && m.LoadIssues!["p2"].Contains("kovaim1", StringComparison.Ordinal) && core.Snapshot().Chat.Any(c => c.Text.Contains("retry", StringComparison.Ordinal)),
            "A player whose map didn't load fails the load with the reason; the match doesn't start");
        advance(LobbyCore.LoadingMs + 1); core.Tick();
        Check(core.Snapshot().Match!.Phase == MatchPhases.Loading && core.Snapshot().Match!.StartsAt is null, "A failed load never starts the match on its own, not even after the timeout");
        Check(!core.Apply("p2", "retry-load", default, content).Ok && core.Apply("host", "retry-load", default, content).Ok, "Only the host retries the load");
        m = core.Snapshot().Match!;
        Check(m is { Phase: MatchPhases.Loading, LoadFailed: false, LoadAttempt: 1 } && m.Loaded!.Count == 0 && m.LoadIssues is null, "Retry starts a new load attempt with nobody loaded");
        core.Apply("p2", "loaded", J(new { ok = true, attempt = 0 }), content); core.Tick();
        Check(core.Snapshot().Match!.Loaded!.Count == 0, "A report from the earlier attempt doesn't count");
        core.Apply("host", "loaded", J(new { ok = true, attempt = 1 }), content); core.Tick();
        Check(core.Snapshot().Match!.Phase == MatchPhases.Loading, "One loaded player of two still waits");
        core.Apply("p2", "loaded", J(new { ok = true, attempt = 1 }), content); core.Tick();
        Check(core.Snapshot().Match!.Phase == MatchPhases.Countdown, "The countdown starts once everyone's map has loaded");
        // A silent player: the wait times out into the same retry-or-abort state, and the host can end it.
        (core, clock, advance) = Lobby();
        core.Join("p2", "Two");
        ReadyAll(core);
        core.RequireLoading = true;
        core.Apply("host", "start", default, content);
        core.Apply("host", "loaded", J(new { }), content);
        advance(LobbyCore.LoadingMs - 1000); core.Tick();
        Check(core.Snapshot().Match is { Phase: MatchPhases.Loading, LoadFailed: false }, "The load waits up to its time limit");
        advance(2000); core.Tick();
        Check(core.Snapshot().Match is { Phase: MatchPhases.Loading, LoadFailed: true }, "Past the limit the load fails instead of starting");
        Check(core.Apply("host", "end", default, content).Ok && core.Snapshot().Match is null or { Phase: MatchPhases.Final }, "The host can abort a failed load");
    }

    // Developer mode's test avatar stands in for a simulated player: AimModCore tags its hull with
    // bridge peer 1's stream, which maps back to that member.
    static void StandInStream(string root)
    {
        var tracker = new SelfPoseTracker(Path.Combine(root, "standin"));
        var frame = new LivePoseFrame(1, "", "AimMod Match - Synthetic", "synthetic_map", 1, [new LivePose(5000, [0, 0, 0, 0, 0])], [[7, 400, 0, 0, 45, 115], [8, 900, 0, 0, 45, 115]])
            { Tags = new Dictionary<int, string> { [7] = StreamIds.For(MultiplayerService.StandInPeer), [8] = StreamIds.For("other") } };
        tracker.Take(frame, 0, ["me", "sim-a", "other"], (StreamIds.For(MultiplayerService.StandInPeer), "sim-a"));
        Check(tracker.LastSeen[7].Member == "sim-a" && tracker.LastSeen[8].Member == "other", "The test avatar's stream maps to the simulated member it stands in for");
        tracker.Reset();
        tracker.Take(frame, 0, ["me", "sim-a", "other"]);
        Check(tracker.LastSeen[7].Member is null, "Without a stand-in, peer 1's stream is nobody");
    }

    // Every simulated player is a stand-in of its own (synthetic peers 1, 2, 3, ...), not just the first.
    static void StandIns(string root)
    {
        long now = 9_500_000;
        var game = Path.Combine(root, "game");
        var output = Path.Combine(root, "standins-output");
        Directory.CreateDirectory(output);
        var control = new FakeGame("load", "start") { Root = game };
        var service = new MultiplayerService(new OfflineTransport(), new ContentLibrary(game), control, () => new LocalRun(false, null, null, null, null, 0, 0, 0, null), () => [], () => null, output, simulation: true, () => now, autoTick: false, seed: 21);
        void Run(int ms) { for (var t = 0; t < ms; t += 100) { now += 100; service.Tick(); } }
        service.Act("create", J(new { mode = "deathmatch", scenario = "Synthetic A" }));
        for (var i = 0; i < 3; i++) service.Act("sim", J(new { op = "add" }));
        Run(12_000);
        Check(service.Act("start", default).Ok, "A deathmatch with three simulated players starts");
        for (var i = 0; i < 300 && !File.Exists(Path.Combine(output, "avatar-state.tsv")); i++) Run(100);
        Run(1000);
        var avatars = File.ReadAllText(Path.Combine(output, "avatar-state.tsv"));
        Check(service.StandIns.Count == 3 && service.StandIns.Values.Order().SequenceEqual(["1", "2", "3"]) && new[] { "1", "2", "3" }.All(p => avatars.Contains("peer\t" + p + "\t", StringComparison.Ordinal)),
            "Each simulated player is a stand-in with its own synthetic peer, listed in avatar-state.tsv");
        var tracker = new SelfPoseTracker(Path.Combine(root, "standins-pose"));
        var ids = service.StandIns.ToDictionary(p => p.Value, p => p.Key);
        var frame = new LivePoseFrame(1, "", "x", "m", 1, [new LivePose(5000, [0, 0, 0, 0, 0])], [[4, 100, 0, 0, 45, 115], [5, 200, 0, 0, 45, 115]])
            { Tags = new Dictionary<int, string> { [4] = StreamIds.For("2"), [5] = StreamIds.For("3") } };
        tracker.Take(frame, 0, ids.Values, service.StandIns.Select(p => (StreamIds.For(p.Value), p.Key)));
        Check(tracker.LastSeen[4].Member == ids["2"] && tracker.LastSeen[5].Member == ids["3"], "Each stand-in's stream maps back to its own member");
        service.Dispose();
    }

    // Hit claims pair the shot's ray with the target as drawn when it was fired; start spawns.
    static void ClaimTiming(string root)
    {
        var tracker = new SelfPoseTracker(Path.Combine(root, "claims"));
        LivePoseFrame Frame(long seq, long t, double x) => new(seq, "", "AimMod Match - Synthetic", "synthetic_map", 1, [new LivePose(t, [0, 0, 0, 0, 0])], [[7, x, 0, 0, 45, 115]]);
        tracker.Take(Frame(1, 1000, 100), 0); tracker.Take(Frame(2, 1100, 200), 0); tracker.Take(Frame(3, 1200, 300), 0);
        Check(tracker.SeenAt(7, 1050) is { X: 150 } && tracker.SeenAt(7, 1200) is { X: 300 } && tracker.SeenAt(7, 1290) is { X: 300 } && tracker.SeenAt(7, 1400) is null && tracker.SeenAt(8, 1100) is null,
            "Drawn targets are kept briefly and interpolated to any moment");
        var feed = new ShotFeed(Path.Combine(root, "claims"));
        var shots = ShotFeed.Parse("AIMMOD_SHOTS_1\t1\ts\nshot\t1050\t1\t0\t0\t164\t1\t0\t0\t0\t7\t0\t1\n");
        var claims = feed.Take(shots, "m", 1, 0, (id, t) => tracker.SeenAt(id, t));
        Check(claims.Count == 1 && claims[0].TargetX == 150 && claims[0].T == 1050, "A claim carries the target where it was drawn at the shot, not where it is now (300)");
        var combat = new CombatMatch(LobbyModes.TeamDeathmatch, ["a", "b", "c", "d"], 20, 0, 0, 60_000)
            { Spawns = [new(0, 0, 0, 0, 1), new(100, 0, 0, 0, 1), new(0, 900, 0, 180, 2), new(100, 900, 0, 180, 2)] };
        combat.PlaceAll(0);
        var starts = combat.EventsSince(0).Where(e => e.Kind == "respawn").ToList();
        Check(starts.Count == 4 && starts.Select(e => (e.Spawn![0], e.Spawn[1])).Distinct().Count() == 4
            && starts.All(e => (e.Spawn![1] < 450) == (combat.View().Players.First(p => p.Member == e.Member).Team == 1)),
            "Every player starts on a spawn of its own, on its team's side");
    }

    // A restart that gets past the lock (KovaaK's run timer jumps back): the match carries on with the
    // score the player had, and the player is told restart is off.
    static void RestartDuringMatch(string root)
    {
        long now = 8_000_000;
        var game = Path.Combine(root, "game");
        var output = Path.Combine(root, "restart-output");
        Directory.CreateDirectory(output);
        var control = new FakeGame("load", "start") { Root = game };
        string? scenario = null; long? startedAt = null; double offsetSeconds = 0;
        LocalRun Live() => scenario is null || startedAt is null || now < startedAt ? new LocalRun(false, null, null, null, null, 0, 0, 0, null)
            : new LocalRun(true, scenario, Math.Round(((now - startedAt.Value) / 1000.0 - offsetSeconds) * 100), (now - startedAt.Value) / 1000.0 - offsetSeconds, null, 10, 5, 0, null);
        var service = new MultiplayerService(new OfflineTransport(), new ContentLibrary(game), control, Live, () => [], () => null, output, simulation: true, () => now, autoTick: false, seed: 13);
        JsonElement Match() => JsonSerializer.SerializeToElement(service.View(), Protocol.Json).GetProperty("lobby").GetProperty("match");
        void Run(int ms) { for (var t = 0; t < ms; t += 100) { now += 100; service.Tick(); } }
        service.Act("create", J(new { mode = "ffa-rounds", scenario = "Synthetic A" }));
        service.Act("settings", Patch(new { rounds = 1, movement = "cs" }));
        service.Act("sim", J(new { op = "add" }));
        Run(12_000);
        Check(service.Act("start", default).Ok, "A generated freeplay round starts");
        for (var i = 0; i < 200 && Match().GetProperty("phase").GetString() != MatchPhases.Live; i++) Run(100);
        scenario = control.Calls.Last(c => c.StartsWith("load ", StringComparison.Ordinal))[5..];
        startedAt = now;
        Run(5000);
        double Mine() => Match().GetProperty("live").EnumerateArray().First(l => l.GetProperty("memberId").GetString() == service.SelfId).GetProperty("score").GetDouble();
        var before = Mine();
        Check(before >= 400, "Live score frames arrive from the freeplay run");
        offsetSeconds = (now - startedAt.Value) / 1000.0 - 0.2; // KovaaK's restarted the run
        Run(300);
        Check(JsonDocument.Parse(service.NoticeText()).RootElement.GetProperty("title").GetString() == "Restart is off during a match", "A restart that got through is noticed and explained");
        Run(3000);
        Check(Mine() >= before, "The restarted run never lowers the match score: the score before the restart stands");
        // AimModCore switched the restart key off; pressing it anyway gets the same explanation.
        File.WriteAllText(Path.Combine(output, "match-lock.tsv"), "AIMMOD_LOCK_1\t0\t1\n");
        Run(2000);
        Check(!JsonDocument.Parse(service.NoticeText()).RootElement.GetProperty("title").ToString().Contains("Restart", StringComparison.Ordinal), "No restart notice before a press");
        File.WriteAllText(Path.Combine(output, "match-lock.tsv"), "AIMMOD_LOCK_1\t1\t2\n");
        Run(200);
        Check(JsonDocument.Parse(service.NoticeText()).RootElement.GetProperty("title").GetString() == "Restart is off during a match", "A press of the switched-off restart key is explained");
        service.Dispose();
    }

    // CS competitive only on maps with the AimMod CS map spec (the port's "cs" block).
    static string CsSpec(int spawns = 5, bool siteB = true, bool ctBuy = true)
    {
        string Points(double x, double y) => string.Join(",", Enumerable.Range(0, spawns).Select(i => "[" + (x + i * 40) + "," + y + ",40,90]"));
        var sites = "{\"name\":\"A\",\"min\":[250,-650,0],\"max\":[300,-600,40]}" + (siteB ? ",{\"name\":\"B\",\"min\":[-300,-650,0],\"max\":[-250,-600,40]}" : "");
        return "{\"format\":\"aimmod.map-objectives\",\"version\":1,\"map_scale\":4,\"zones\":[],\"spawns\":[],\"cs\":{\"format\":\"aimmod.cs-map\",\"version\":1,"
            + "\"spawns\":{\"T\":[" + Points(0, 600) + "],\"CT\":[" + Points(0, -600) + "]},\"bomb_sites\":[" + sites + "],"
            + "\"buy_zones\":{\"T\":[{\"min\":[-50,550,-20],\"max\":[250,650,60]}],\"CT\":[" + (ctBuy ? "{\"min\":[-50,-650,-20],\"max\":[250,-550,60]}" : "") + "]},"
            + "\"callouts\":[{\"name\":\"Long A\",\"min\":[240,-660,-10],\"max\":[310,-590,50]}]}}";
    }
    static void CsMaps(string root)
    {
        var spec = MapObjectives.Parse(CsSpec())!;
        Check(spec.CsProblem is null && spec.BombSites.Select(z => z.Name).SequenceEqual(["A", "B"]) && spec.SpawnsFor(CsRules.T).Count == 5 && spec.BuyZones(CsRules.CT).Count == 1
            && spec.BombSites[0].Min[0] == 1000 && spec.Callouts.Single().Name == "Long A", "The CS map spec gives sites A and B, team spawns and buy zones, in centimetres (map_scale)");
        Check(MapObjectives.Parse(CsSpec(spawns: 4))!.CsProblem == "Fewer than 5 T spawns" && MapObjectives.Parse(CsSpec(siteB: false))!.CsProblem == "Needs bomb sites A and B"
            && MapObjectives.Parse(CsSpec(ctBuy: false))!.CsProblem == "No CT buy zone", "Missing pieces say why the map isn't a CS map");
        Check(MapObjectives.Parse("{\"format\":\"aimmod.map-objectives\",\"version\":1,\"zones\":[{\"type\":\"bomb_site\",\"aabb\":{\"min\":[0,0,0],\"max\":[1,1,1]}}],\"spawns\":[]}")!.CsProblem == MapObjectives.NoCsData,
            "Bomb-site zones without the CS map spec don't make a CS map");
        // The library marks scenarios by their map's spec; the host refuses others in CS.
        var game = Path.Combine(root, "csgame");
        WriteText(Path.Combine(game, "Saved", "SaveGames", "Scenarios", "Synthetic Dust.sce"), BaseScenario.Replace("Name=Synthetic A", "Name=Synthetic Dust").Replace("MapName=synthetic_map.map", "MapName=aimmod_de_synthetic_css.json"));
        WriteText(Path.Combine(game, "Saved", "SaveGames", "Scenarios", "Synthetic Aim.sce"), BaseScenario.Replace("Name=Synthetic A", "Name=Synthetic Aim"));
        WriteText(Path.Combine(game, "maps", "aimmod_de_synthetic_css.json"), "{}");
        WriteText(Path.Combine(game, "maps", "aimmod_de_synthetic_css.aimmod.json"), CsSpec());
        var library = new ContentLibrary(game);
        Check(library.Scenario("Synthetic Dust")!.CsProblem is null && library.Scenario("Synthetic Aim")!.CsProblem == MapObjectives.NoCsData, "Scenarios carry whether their map is a CS map");
        var cs = LobbyRules.Apply(new LobbySettings(Scenario: library.Scenario("Synthetic Dust")), J(new { mode = "cs" }), 6, library).Settings!;
        var refused = LobbyRules.Apply(cs, J(new { scenario = "Synthetic Aim" }), 6, library);
        Check(!refused.Result.Ok && refused.Result.Code == "cs-map" && refused.Result.Message!.Contains(MapObjectives.NoCsData, StringComparison.Ordinal), "In CS the host refuses a scenario whose map isn't a CS map, saying why");
        var (core, _, _) = Lobby(LobbyRules.Apply(new LobbySettings(Scenario: library.Scenario("Synthetic Aim")), J(new { mode = "cs" }), 6, library).Settings);
        Check(LobbyRules.StartBlockers(core.Snapshot()).Any(b => b.Code == "cs-map"), "Switching to CS with a non-CS map blocks the start until a CS map is picked");
        // The host switching to CS with an aim map gets the first CS map instead; the library says which are CS maps.
        long now = 7_000_000;
        var service = new MultiplayerService(new OfflineTransport(), library, new FakeGame("load"), () => new LocalRun(false, null, null, null, null, 0, 0, 0, null), () => [], () => null, null, simulation: true, () => now, autoTick: false);
        service.Act("create", J(new { mode = "score-race", scenario = "Synthetic Aim" }));
        var switched = service.Act("settings", Patch(new { mode = "cs" })).Ok;
        var csSettings = JsonSerializer.SerializeToElement(service.View(), Protocol.Json).GetProperty("lobby").GetProperty("settings");
        Check(switched && csSettings.GetProperty("mode").GetString() == LobbyModes.Cs && csSettings.GetProperty("scenario").GetProperty("name").GetString() == "Synthetic Dust", "Switching to CS with an aim map picks the first CS map");
        var eligibility = JsonSerializer.SerializeToElement(service.View(), Protocol.Json).GetProperty("lobby").GetProperty("eligibility");
        Check(eligibility.GetProperty("Synthetic Dust").GetProperty("ok").GetBoolean() && eligibility.GetProperty("Synthetic Aim") is var aimFit
            && !aimFit.GetProperty("ok").GetBoolean() && aimFit.GetProperty("reason").GetString() == MapObjectives.NoCsData && aimFit.GetProperty("players").GetString() == "6, 8 or 10 players",
            "lobby.eligibility: scenario name -> { ok, reason, players } for the current mode (CS)");
        var libraryView = JsonSerializer.SerializeToElement(service.LibraryView(), Protocol.Json).GetProperty("scenarios").EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!, x => x.GetProperty("modes").GetProperty("cs"));
        Check(libraryView["Synthetic Dust"].GetProperty("ok").GetBoolean() && libraryView["Synthetic Aim"] is var aim && !aim.GetProperty("ok").GetBoolean() && aim.GetProperty("reason").GetString() == MapObjectives.NoCsData,
            "The library view says per scenario whether it can host CS, and why not (modes.cs.ok / reason)");
        service.Dispose();
        // Runtime: plant only in a site, buy only in your zone, the site letter and callout on the HUD.
        long t = 0;
        var ids = new[] { "t1", "t2", "t3", "c1", "c2", "c3" };
        var match = new CsMatch(ids, 0, 6, true, spec, ids.ToDictionary(id => id, id => id.StartsWith('t') ? 1 : 2));
        void Track(string id, double x, double y, double z) => match.Combat.Track(id, new TrackBatch("m", 1, [new TrackSample(t, x, y, z + 64, 0, 0), new TrackSample(t + 200, x, y, z + 64, 0, 0)], []));
        var tSpawn = match.View().Spawns!["t1"];
        Check(tSpawn[1] == 2400 && match.View().Spawns!["c1"][1] == -2400, "Each round starts on your side's spawns");
        Track("t1", 0, 2400, 160);
        Check(match.Buy("t1", "kevlar", t) is null, "Buying works inside your buy zone during buy time");
        Track("t2", 0, 0, 160);
        Check(match.Buy("t2", "kevlar", t) == "buy-zone", "Outside your buy zone nothing can be bought");
        t = CsRules.FreezeMs + 1; match.Tick(t);
        var carrier = match.View().Bomb.Carrier!;
        t += 300; Track(carrier, 0, 0, 160); Track(carrier, 0, 0, 160);
        Check(match.Use(carrier, true, t) == "not-in-site", "The bomb can't be planted outside a site");
        t += 300; Track(carrier, 1100, -2500, 40); t += 300; Track(carrier, 1100, -2500, 40);
        var view = match.View();
        Check(view.Players.First(p => p.Member == carrier) is { Site: "A", Callout: "Long A" } && view.Sites!.Select(s => s.Name).SequenceEqual(["A", "B"]), "Inside a site the HUD gets its letter (and the callout)");
        Check(match.Use(carrier, true, t) is null && match.View().Bomb.Site == "A", "Inside site A, holding E plants at A");
        // Drop (G) and pick up: in front of the carrier; not straight back by the dropper; any alive T over it; never a CT.
        t = 0;
        var round = new CsMatch(ids, 0, 6, true, spec, ids.ToDictionary(id => id, id => id.StartsWith('t') ? 1 : 2));
        void At(string id, double x, double y, double yaw) => round.Combat.Track(id, new TrackBatch("m", 1, [new TrackSample(t, x, y, 224, 0, yaw), new TrackSample(t + 50, x, y, 224, 0, yaw)], []));
        t = CsRules.FreezeMs + 10; round.Tick(t);
        var holder = round.View().Bomb.Carrier!;
        var mate = ids.First(id => id.StartsWith('t') && id != holder);
        Check(round.Drop(mate, t) == "no-bomb" && round.Drop("c1", t) == "no-bomb", "Only the carrier can drop the bomb");
        At(holder, 0, 0, 0);
        Check(round.Drop(holder, t + 60) is null && round.View().Bomb is { State: "dropped", Carrier: null, Position: [70, 0, 160] }, "The carrier drops the bomb 70 cm in front of them");
        t += 200; At(holder, 70, 0, 0); round.Tick(t + 60);
        Check(round.View().Bomb.State == "dropped", "The dropper doesn't pick it straight back up");
        At("c1", 70, 0, 0); round.Tick(t + 100);
        Check(round.View().Bomb.State == "dropped", "Counter-Terrorists walk over the bomb");
        At(holder, 900, 900, 0); At(mate, 75, 5, 0); round.Tick(t + 120);
        Check(round.View().Bomb is { State: "carried" } b2 && b2.Carrier == mate, "A teammate walking over it picks it up");
        Check(round.Use(holder, true, t + 130) == "no-bomb", "Without the bomb, E says so");
    }

    // A client whose KovaaK's keeps the previous map (the live CS bug): it loads again once,
    // then reports the problem; the host sees Retry and Abort; the scenario is kept for debugging.
    static void LoadGateService(string root)
    {
        long now = 6_000_000;
        var game = Path.Combine(root, "game");
        var output = Path.Combine(root, "loadgate-output");
        Directory.CreateDirectory(output);
        var control = new FakeGame("load", "start") { Root = game, StuckMap = "kovaim1.map" };
        var service = new MultiplayerService(new OfflineTransport(), new ContentLibrary(game), control, () => new LocalRun(false, null, null, null, null, 0, 0, 0, null), () => [], () => null, output, simulation: true, () => now, autoTick: false, seed: 9);
        JsonElement Notice() => JsonDocument.Parse(service.NoticeText()).RootElement;
        string Phase() => JsonSerializer.SerializeToElement(service.View(), Protocol.Json).GetProperty("lobby").GetProperty("match").GetProperty("phase").GetString()!;
        JsonElement Round() => JsonSerializer.SerializeToElement(service.View(), Protocol.Json).GetProperty("lobby").GetProperty("round");
        void Run(int ms) { for (var t = 0; t < ms; t += 100) { now += 100; service.Tick(); } }
        service.Act("create", J(new { mode = "deathmatch", scenario = "Synthetic A" }));
        service.Act("sim", J(new { op = "add" }));
        Run(12_000);
        Check(service.Act("start", default).Ok, "A deathmatch starts loading its arena");
        Run(500);
        var name = control.Calls.Last(c => c.StartsWith("load ", StringComparison.Ordinal))[5..];
        Check(name.StartsWith(MatchScenario.Prefix, StringComparison.Ordinal) && Notice().GetProperty("title").GetString() == "Waiting for everyone to load (1/2)",
            "While a map loads, the toast counts who is ready (the simulated opponent is)");
        Run(2000);
        Check(Round().GetProperty("map").GetString() == "wrong" && Round().GetProperty("message").GetString()!.Contains("kovaim1.map", StringComparison.Ordinal) && !Round().GetProperty("message").GetString()!.Contains("Loaded", StringComparison.Ordinal),
            "The round box shows the map check (wrong map), never \"Loaded\" while the map is wrong");
        Run(14_000);
        Check(control.Calls.Count(c => c == "load " + name) == 2 && Phase() == MatchPhases.Loading, "After 15 s on the wrong map the client loads the scenario again");
        Run(16_000);
        var failed = Notice();
        Check(Phase() == MatchPhases.Loading && failed.GetProperty("title").GetString() == "Couldn’t load the match (1/2)" && failed.GetProperty("body").GetString()!.Contains("kovaim1.map", StringComparison.Ordinal)
            && failed.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("action").GetString()).SequenceEqual(["retry-load", "end"]),
            "Still the wrong map: the host sees why, with Retry and Abort, and the match hasn't started");
        Check(Round().GetProperty("map").GetString() == "failed", "The round box says the map failed");
        Check(failed.GetProperty("eyebrow").GetString() == "AimMod · Match" && failed.GetProperty("interactive").GetBoolean() && failed.GetProperty("layout").GetString() == "toast",
            "The load failure is labelled as a match notice and asks for clicks in the toast layer");
        Check(File.Exists(Path.Combine(game, "Saved", "SaveGames", "Scenarios", name + ".sce")), "A failed match scenario stays on disk while the match is open");
        static string? PlayId(JsonElement n) => n.TryGetProperty("play", out var p) && p.ValueKind == JsonValueKind.Object ? p.GetProperty("id").GetString() : null;
        Check(PlayId(failed) is null, "No play request while the match is still loading");
        control.StuckMap = null;
        var retryAt = now;
        Check(service.Act("retry-load", J(new { id = failed.GetProperty("actions")[0].GetProperty("id").GetString() })).Ok, "The host retries the load");
        Run(1500);
        Check(Round().GetProperty("map").GetString() == "ok", "After the retry the map check passes");
        Check(control.Calls.Count(c => c == "load " + name) == 3 && Phase() is MatchPhases.Countdown or MatchPhases.Live, "With the map there, the retried load starts the match");
        var started = Notice();
        Check(PlayId(started) is { } playId && playId.EndsWith("-1", StringComparison.Ordinal) && started.GetProperty("play").GetProperty("since").GetInt64() is var since && since >= retryAt && since <= now,
            "Once the retried load starts the match, the game is asked to take input back (one id per attempt, since the Retry)");
        Run(300);
        Check(PlayId(Notice()) == PlayId(started), "The request keeps its id while it's sent, so it's handled once");
        // In play the notice layer stays full-screen, so the scoreboard key never resizes it.
        Check(Notice().GetProperty("layout").GetString() == "full", "During the match the notice layer keeps the full-screen layout");
        static bool Swallow(JsonElement n) => n.TryGetProperty("swallowMenu", out var w) && w.ValueKind == JsonValueKind.True;
        Check(!Swallow(Notice()), "No pause menu to swallow before Escape");
        service.CsEscapeForTest();
        Check(Swallow(Notice()) && !(Notice().TryGetProperty("cursor", out var cur) && cur.ValueKind == JsonValueKind.True),
            "Escape closes the buy menu (no cursor) and asks AimModNativeUI to close the pause menu it opened");
        Run(1600);
        Check(!Swallow(Notice()), "Only for a moment: a later Escape opens KovaaK's menu as usual");
        service.Act("leave", default);
        Run(300);
        Check(!File.Exists(Path.Combine(game, "Saved", "SaveGames", "Scenarios", name + ".sce")) && File.Exists(Path.Combine(output, "match-debug", name + ".sce")),
            "Afterwards the game's copy goes; the scenario that failed is kept under match-debug");
        service.Dispose();
    }

    // The same stuck map with AimModCore's ensure-map: the map is loaded right away, without
    // reloading the scenario; an AimModCore that answers unsupported falls back to the reload.
    static void LoadGateEnsureMap(string root)
    {
        foreach (var variant in new[] { "fixed", "unsupported", "stale" })
        {
            var unsupported = variant == "unsupported";
            long now = 7_000_000;
            var game = Path.Combine(root, "game");
            var output = Path.Combine(root, "ensure-output-" + variant);
            Directory.CreateDirectory(output);
            var control = new FakeGame("load", "start", "map") { Root = game, StuckMap = "kovaim1.map", EnsureUnsupported = unsupported, EnsureLeavesStaleScene = variant == "stale" };
            var service = new MultiplayerService(new OfflineTransport(), new ContentLibrary(game), control, () => new LocalRun(false, null, null, null, null, 0, 0, 0, null), () => [], () => null, output, simulation: true, () => now, autoTick: false, seed: 11);
            string Phase() => JsonSerializer.SerializeToElement(service.View(), Protocol.Json).GetProperty("lobby").GetProperty("match").GetProperty("phase").GetString()!;
            void Run(int ms) { for (var t = 0; t < ms; t += 100) { now += 100; service.Tick(); } }
            service.Act("create", J(new { mode = "deathmatch", scenario = "Synthetic A" }));
            service.Act("sim", J(new { op = "add" }));
            Run(12_000);
            Check(service.Act("start", default).Ok, "A deathmatch starts loading its arena (ensure-map, " + variant + ")");
            Run(500);
            var name = control.Calls.Last(c => c.StartsWith("load ", StringComparison.Ordinal))[5..];
            Run(2500);
            if (variant == "stale")
            {
                Run(1500);
                Check(control.Calls.Count(c => c == "ensure-map " + name) == 1 && control.Calls.Count(c => c == "load " + name) == 1 && Phase() is MatchPhases.Countdown or MatchPhases.Live,
                    "ensure-map done for the round's scenario counts as the map loaded, even while the scene report still names the previous map");
            }
            else if (!unsupported)
            {
                Check(control.Calls.Count(c => c == "ensure-map " + name) == 1 && control.Calls.Count(c => c == "load " + name) == 1,
                    "The right scenario with the previous map: the client loads the map directly at once, without reloading the scenario");
                Run(1500);
                Check(Phase() is MatchPhases.Countdown or MatchPhases.Live, "With the map loaded directly, the match starts");
                Check(control.Calls.Count(c => c == "ensure-map " + name) == 1, "ensure-map is sent once per load attempt");
            }
            else
            {
                Check(control.Calls.Count(c => c == "ensure-map " + name) == 1 && control.Calls.Count(c => c == "load " + name) == 2,
                    "AimModCore answers ensure-map unsupported: the client falls back to loading the scenario again");
                Run(30_000);
                Check(Phase() == MatchPhases.Loading && control.Calls.Count(c => c == "load " + name) == 2, "Still the wrong map after the fallback: the load fails, the match waits for the host");
            }
            service.Act("leave", default);
            Run(300);
            service.Dispose();
        }
        // Only AimMod's own scenarios may have their map loaded this way.
        Check(GameCommands.MapFixAllowed("AimMod Match - Synthetic - 0a1b2c3d") && GameCommands.MapFixAllowed("AimMod - aim_synthetic (CSS) - CS Movement")
            && !GameCommands.MapFixAllowed("VT Pasu") && !GameCommands.MapFixAllowed("AimMod Match - a\\b") && !GameCommands.MapFixAllowed(null),
            "ensure-map is limited to AimMod's generated scenarios");
        var commands = new GameCommands(Path.Combine(root, "ensure-output-fixed"));
        Check(commands.Send(new("ensure-map", "VT Pasu", null, null, null, null, null, null)).Error == "not-a-match"
            && commands.Send(new("ensure-map", "AimMod Match - X", "challenge", null, null, null, null, null)).Error == "invalid-command"
            && commands.Send(new("ensure-map", "AimMod Match - X", null, null, null, null, 2.0, null)).Error == "invalid-command"
            && commands.Send(new("ensure-map", "AimMod Match - X", null, null, null, null, null, null)).Sequence is long
            && File.ReadAllText(Path.Combine(root, "ensure-output-fixed", "core-command.tsv")).Contains("action\tensure-map\nscenario\tAimMod Match - X\n", StringComparison.Ordinal),
            "The service writes ensure-map with just the scenario, and refuses other scenarios, modes and overrides");
    }
}

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

        // What this machine's game shows against the round.
        const string sc = "AimMod Match - Synthetic";
        var expected = ("aim_map.map", (double?)3.8);
        GameScene Scene(string scenario = sc, string map = "aim_map", double? scale = 3.8, bool loading = false) => new(true, scenario, map, scale, false, false, loading, false);
        Check(MatchScenario.SameMap("aim_map", "aim_map.map") && MatchScenario.SameMap("AIM_MAP.map", "aim_map.map") && !MatchScenario.SameMap("kovaim1.map", "aim_map.map") && !MatchScenario.SameMap("", "aim_map.map"),
            "Map names match with or without the extension, ignoring case");
        Check(MultiplayerService.SceneProblem(Scene(), sc, expected) is null && MultiplayerService.SceneProblem(Scene(loading: true), sc, expected) == "loading"
            && MultiplayerService.SceneProblem(null, sc, expected) is not null && MultiplayerService.SceneProblem(Scene(scenario: "Other"), sc, expected) is not null,
            "The scene counts only when it shows the round's scenario, not loading");
        Check(MultiplayerService.SceneProblem(Scene(map: "kovaim1.map"), sc, expected)!.Contains("kovaim1.map", StringComparison.Ordinal) && MultiplayerService.SceneProblem(Scene(scale: 1), sc, expected)!.Contains("scale", StringComparison.Ordinal),
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
        void Run(int ms) { for (var t = 0; t < ms; t += 100) { now += 100; service.Tick(); } }
        service.Act("create", J(new { mode = "deathmatch", scenario = "Synthetic A" }));
        service.Act("sim", J(new { op = "add" }));
        Run(12_000);
        Check(service.Act("start", default).Ok, "A deathmatch starts loading its arena");
        Run(500);
        var name = control.Calls.Last(c => c.StartsWith("load ", StringComparison.Ordinal))[5..];
        Check(name.StartsWith(MatchScenario.Prefix, StringComparison.Ordinal) && Notice().GetProperty("title").GetString() == "Waiting for everyone to load (1/2)",
            "While a map loads, the toast counts who is ready (the simulated opponent is)");
        Run(16_000);
        Check(control.Calls.Count(c => c == "load " + name) == 2 && Phase() == MatchPhases.Loading, "After 15 s on the wrong map the client loads the scenario again");
        Run(16_000);
        var failed = Notice();
        Check(Phase() == MatchPhases.Loading && failed.GetProperty("title").GetString() == "Couldn’t load the match (1/2)" && failed.GetProperty("body").GetString()!.Contains("kovaim1.map", StringComparison.Ordinal)
            && failed.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("action").GetString()).SequenceEqual(["retry-load", "end"]),
            "Still the wrong map: the host sees why, with Retry and Abort, and the match hasn't started");
        Check(File.Exists(Path.Combine(game, "Saved", "SaveGames", "Scenarios", name + ".sce")), "A failed match scenario stays on disk while the match is open");
        control.StuckMap = null;
        Check(service.Act("retry-load", J(new { id = failed.GetProperty("actions")[0].GetProperty("id").GetString() })).Ok, "The host retries the load");
        Run(1500);
        Check(control.Calls.Count(c => c == "load " + name) == 3 && Phase() is MatchPhases.Countdown or MatchPhases.Live, "With the map there, the retried load starts the match");
        service.Act("leave", default);
        Run(300);
        Check(!File.Exists(Path.Combine(game, "Saved", "SaveGames", "Scenarios", name + ".sce")) && File.Exists(Path.Combine(output, "match-debug", name + ".sce")),
            "Afterwards the game's copy goes; the scenario that failed is kept under match-debug");
        service.Dispose();
    }
}

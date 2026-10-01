using System.Text.Json;
using AimMod.InGame.Multiplayer;

namespace AimMod.InGame;

// The shared scenario load (ScenarioLoader) and the replay's automatic load on top of it.
// Synthetic scenarios and a scripted game only.
static class ScenarioLoaderChecks
{
    // AimModCore as the loader sees it: answers per request, core-scene.json, and every command sent.
    sealed class ScriptedGame(params string[] caps) : IGameControl
    {
        public readonly List<string> Calls = [];
        public HashSet<string> Caps { get; } = caps.ToHashSet();
        public IReadOnlySet<string> Capabilities => Caps;
        readonly Dictionary<long, GameCommandResult> answers = new();
        long sequence, newest;
        public GameScene? Current;
        public bool? Challenge;
        // What load-scenario does: answer and show the scenario (default), or anything else.
        public Action<long, string>? OnLoad;
        public (string Map, double Scale)? EnsureFixes;
        public long LastLoad;
        public GameScene? Scene => Current;
        public bool? ChallengeRunning => Challenge;
        public void Answer(long seq, string state, string code, string message = "") { answers[seq] = new(seq, state, code, message); newest = Math.Max(newest, seq); }
        public long? Load(string scenario)
        {
            Calls.Add("load " + scenario);
            LastLoad = ++sequence;
            if (OnLoad is { } script) script(sequence, scenario);
            return sequence;
        }
        public long? Start(string scenario, string mode, long? seed = null) { Calls.Add("start " + mode + " " + scenario); return ++sequence; }
        public long? Refresh() { Calls.Add("refresh"); return ++sequence; }
        public long? EnsureMap(string scenario)
        {
            if (!Caps.Contains("map") || !GameCommands.MapFixAllowed(scenario)) return null;
            Calls.Add("ensure-map " + scenario);
            var seq = ++sequence;
            if (EnsureFixes is var (map, scale) && Current is { } shown) { Current = shown with { MapName = map, MapScale = scale }; Answer(seq, "done", "map-loaded", map); }
            else Answer(seq, "error", "unsupported", "");
            return seq;
        }
        public GameCommandResult? Result => answers.GetValueOrDefault(newest);
        public GameCommandResult? ResultFor(long seq) => answers.GetValueOrDefault(seq);
    }

    static GameScene Shows(string scenario, string map, double scale, bool loading = false, bool challenge = false) => new(true, scenario, map, scale, challenge, challenge, loading, false);

    public static void Run()
    {
        var checks = 0;
        void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
        var root = Path.Combine(Path.GetTempPath(), "aimmod-loader-check-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "game", "Saved", "SaveGames", "Scenarios");
        Directory.CreateDirectory(folder);
        try
        {
            const string plain = "Synthetic Loader", port = "AimMod - aim_synthetic (CSS) - CS Movement";
            File.WriteAllText(Path.Combine(folder, plain + ".sce"), "Name=" + plain + "\nMapName=synthetic_map.map\nMapScale=2.5\nTimelimit=60.0\n");
            File.WriteAllText(Path.Combine(folder, port + ".sce"), "Name=" + port + "\nMapName=aim_synthetic.map\nMapScale=3.8\nTimelimit=60.0\n");
            var library = new ContentLibrary(Path.Combine(root, "game"));
            long now = 1_000_000;

            // Success: load-scenario, its answer, then the scene with the file's map at its scale on two polls.
            var game = new ScriptedGame("load", "start");
            var loader = new ScenarioLoader(game, () => now, library);
            Check(loader.Load(plain).Phase == ScenarioLoadPhase.Loading && game.Calls.SequenceEqual(["load " + plain]) && loader.Status.Message == "Loading “" + plain + "”…", "A load sends load-scenario and says it's loading");
            game.Answer(game.LastLoad, "accepted", "loading");
            game.Current = Shows("Previous", "other", 1, loading: true);
            Check(loader.Tick().Phase == ScenarioLoadPhase.Loading, "An accepted load is still loading");
            game.Answer(game.LastLoad, "done", "loaded");
            Check(loader.Tick() is { Phase: ScenarioLoadPhase.Checking, Map: "checking" }, "Done loaded isn't enough while KovaaK's still shows its loading screen");
            game.Current = Shows(plain, "synthetic_map", 2.5);
            Check(loader.Tick().Phase == ScenarioLoadPhase.Checking && loader.Tick() is { Phase: ScenarioLoadPhase.Loaded, Map: "ok" }, "The scenario counts once the scene shows it with its map at its scale, twice in a row");
            Check(game.Calls.SequenceEqual(["load " + plain]), "One load and nothing else: no start, so never a challenge run");
            Check(loader.ExpectedMap(plain) == ("synthetic_map.map", 2.5), "The expected map comes from the scenario file");

            // A stale map on an AimMod scenario: fixed at once through ensure-map, without loading again.
            game = new ScriptedGame("load", "map") { EnsureFixes = ("aim_synthetic", 3.8) };
            game.OnLoad = (seq, s) => { game.Answer(seq, "done", "loaded"); game.Current = Shows(s, "defaultscenario", 4.33); };
            loader = new ScenarioLoader(game, () => now, library);
            loader.Load(port);
            Check(loader.Tick() is { Phase: ScenarioLoadPhase.Checking, Map: "checking" } s1 && s1.Message.Contains("kept the previous map", StringComparison.Ordinal)
                && game.Calls.SequenceEqual(["load " + port, "ensure-map " + port]), "The right scenario on the previous map sends ensure-map right away");
            Check(loader.Tick().Phase == ScenarioLoadPhase.Checking && loader.Tick().Phase == ScenarioLoadPhase.Loaded && game.Calls.Count(c => c.StartsWith("load ", StringComparison.Ordinal)) == 1,
                "With the map fixed the scenario counts as loaded, after one load");
            // A stock scenario on a wrong map isn't ensure-map's: it's loaded again once, then fails with the reason.
            game = new ScriptedGame("load", "map");
            game.OnLoad = (seq, s) => { game.Answer(seq, "done", "already-loaded"); game.Current = Shows(s, "kovaim1", 2.5); };
            loader = new ScenarioLoader(game, () => now, library);
            loader.Load(plain);
            loader.Tick();
            Check(game.Calls.SequenceEqual(["load " + plain, "load " + plain]) && loader.Status.Message.Contains("again", StringComparison.Ordinal), "A wrong map that ensure-map can't fix loads the scenario again");
            now += ScenarioLoader.RetryMs; loader.Tick();
            Check(loader.Status is { Phase: ScenarioLoadPhase.Failed, Failure: ScenarioLoadFailure.WrongScene, Map: "failed" } && loader.Status.Message.Contains("kovaim1", StringComparison.Ordinal),
                "Still the wrong map 15 s after the fix: failed, naming the map the game kept");

            // Missing: AimModCore doesn't know the scenario.
            game = new ScriptedGame("load");
            game.OnLoad = (seq, _) => game.Answer(seq, "error", "unknown-scenario", "not installed");
            loader = new ScenarioLoader(game, () => now, library);
            loader.Load("Synthetic Missing");
            Check(loader.Tick() is { Phase: ScenarioLoadPhase.Failed, Failure: ScenarioLoadFailure.Missing, Code: "unknown-scenario" } missing && missing.Message.Contains("isn’t installed", StringComparison.Ordinal),
                "A scenario KovaaK's doesn't have fails as missing");
            game.OnLoad = (seq, _) => game.Answer(seq, "error", "challenge-active");
            loader.Load(plain);
            Check(loader.Tick().Failure == ScenarioLoadFailure.ChallengeActive, "A running challenge is its own failure (AimModCore never interrupts one)");

            // Timeouts: KovaaK's never finishes loading, or AimModCore never answers.
            game = new ScriptedGame("load");
            game.OnLoad = (seq, s) => { game.Answer(seq, "done", "loaded"); game.Current = Shows(s, "synthetic_map", 2.5, loading: true); };
            loader = new ScenarioLoader(game, () => now, library);
            loader.Load(plain);
            loader.Tick();
            now += ScenarioLoader.FailMs - 1; loader.Tick();
            Check(loader.Status.Phase == ScenarioLoadPhase.Checking, "A slow load waits up to its limit");
            now += 1; loader.Tick();
            Check(loader.Status is { Phase: ScenarioLoadPhase.Failed, Failure: ScenarioLoadFailure.Timeout } && game.Calls.Count == 1, "Still loading at the limit: timed out, never reloaded mid-load");
            game.OnLoad = null;
            loader.Load(plain);
            now += ScenarioLoader.AnswerMs; loader.Tick();
            Check(loader.Status.Failure == ScenarioLoadFailure.Timeout, "No answer from AimModCore: timed out");

            // Busy: asked again a moment later. No load capability: unavailable, nothing sent.
            game = new ScriptedGame("load");
            var busyOnce = true;
            game.OnLoad = (seq, s) => { if (busyOnce) { busyOnce = false; game.Answer(seq, "error", "busy"); } else { game.Answer(seq, "done", "loaded"); game.Current = Shows(s, "synthetic_map", 2.5); } };
            loader = new ScenarioLoader(game, () => now, library);
            loader.Load(plain);
            loader.Tick(); now += ScenarioLoader.BusyRetryMs; loader.Tick(); loader.Tick(); loader.Tick();
            Check(game.Calls.Count == 2 && loader.Status.Phase == ScenarioLoadPhase.Loaded, "A busy game is asked again");
            game = new ScriptedGame();
            loader = new ScenarioLoader(game, () => now, library);
            Check(loader.Load(plain).Failure == ScenarioLoadFailure.Unavailable && game.Calls.Count == 0, "Without the load capability nothing is sent");

            ReplayAutoLoadChecks(library, Check);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        Console.WriteLine(checks + " scenario load checks passed.");
    }

    static void ReplayAutoLoadChecks(ContentLibrary library, Action<bool, string> Check)
    {
        long now = 2_000_000;
        const string stock = "Synthetic Stock";
        ReplayFrame Frame(double t) => new(t, [0, 0, 0, 0, 0, 0, 103], [[1, 100, 0, 0, 10, 10]]);
        var replay = new NativeReplay(2, "synthetic-replay", stock, "2026-01-01", "completed", 1, [Frame(0), Frame(1)], [], "Map_A", 1);
        var game = new ScriptedGame("load", "start");
        game.Current = Shows("Synthetic Other", "other", 1);
        ScenarioSource? source = new(false, false, null, null);
        var gate = new ReplayStartGate();
        var auto = new ReplayAutoLoad(gate, game, () => now, library, _ => source);
        NativeReplay? started = null;
        void Pump(int ticks = 1) { for (var i = 0; i < ticks; i++) { now += 250; started ??= gate.Poll(_ => replay, () => game.Current, () => (true, "unavailable")); if (started is null) auto.Tick(); } }
        void Press() => gate.Wait(replay.Id, replay.Scenario, ReplayStartGate.Evaluate(replay, game.Current, true, "unavailable")!, null, replay.MapName, replay.MapScale);
        JsonElement Status() => JsonSerializer.SerializeToElement(gate.Status);

        // Another scenario in the game: the replay's loads by itself, then the replay starts.
        Press();
        Pump();
        Check(game.Calls.SequenceEqual(["load " + stock]) && gate.Block is { Reason: "scenario-loading" } b && b.Message == "Loading “" + stock + "”…",
            "A replay recorded elsewhere loads its scenario by itself, with a short message");
        Pump(8);
        Check(game.Calls.Count == 1, "The load is sent once while it's under way");
        game.Answer(game.LastLoad, "done", "loaded");
        game.Current = Shows(stock, "Map_A", 1);
        Pump(3);
        Check(started == replay && gate.PendingId is null && !game.Calls.Any(c => c.StartsWith("start ", StringComparison.Ordinal)),
            "Once the game shows the replay's world it starts, and nothing ever started a run (never a challenge)");

        // Not installed, with a Workshop map port: say so, offer the download, load once it's installed.
        started = null;
        game.Current = Shows("Synthetic Other", "other", 1);
        game.OnLoad = (seq, _) => game.Answer(seq, "error", "unknown-scenario");
        source = new(false, true, null, null);
        Press();
        Pump(2);
        Check(gate.Block is { Reason: "scenario-missing" } m && m.Message.Contains("isn’t installed", StringComparison.Ordinal) && m.Message.Contains("Steam Workshop", StringComparison.Ordinal)
            && Status().GetProperty("download").GetProperty("workshop").GetBoolean(), "A missing scenario says so and offers the Workshop download");
        source = new(false, true, "downloading", 40);
        Pump();
        Check(gate.Block!.Message.Contains("40%", StringComparison.Ordinal), "The download's progress is shown");
        source = new(true, false, null, null);
        game.OnLoad = (seq, s) => { game.Answer(seq, "done", "loaded"); game.Current = Shows(s, "Map_A", 1); };
        Pump(4);
        Check(game.Calls.Count(c => c == "load " + stock) == 2, "Not before the game has had a moment to index the download");
        Pump((int)(ReplayAutoLoad.InstalledDelayMs / 250) + 4);
        Check(game.Calls.Count(c => c == "load " + stock) == 3 && started == replay, "Once installed it loads by itself and the replay starts");

        // Missing without a known Workshop item: install it yourself; Retry loads again.
        started = null;
        game.Current = Shows("Synthetic Other", "other", 1);
        game.OnLoad = (seq, _) => game.Answer(seq, "error", "unknown-scenario");
        source = new(false, false, null, null);
        Press();
        Pump(2);
        Check(gate.Block!.Message.Contains("Retry", StringComparison.Ordinal) && Status().GetProperty("download").ValueKind == JsonValueKind.Null, "No Workshop item: no download, just what to do");
        var loads = game.Calls.Count;
        Press();
        Pump();
        Check(game.Calls.Count == loads + 1, "Retry now loads again");

        // A challenge in another scenario: wait for it to end, then load.
        game.OnLoad = (seq, _) => game.Answer(seq, "error", "challenge-active");
        game.Challenge = true;
        Press();
        Pump(2);
        Check(gate.Block is { Reason: "challenge-active" } c && c.Message.Contains("Finish or quit", StringComparison.Ordinal), "A running challenge is never interrupted; the player is told to finish it");
        loads = game.Calls.Count;
        game.Challenge = false;
        game.OnLoad = (seq, s) => { game.Answer(seq, "done", "loaded"); game.Current = Shows(s, "Map_A", 1); };
        Pump((int)(ReplayAutoLoad.ChallengeDelayMs / 250) + 4);
        Check(game.Calls.Count == loads + 1 && started == replay, "When the challenge ends the scenario loads and the replay starts");

        // Without AimModCore's load: nothing sent, the gate says to load it by hand.
        started = null;
        game = new ScriptedGame { Current = Shows("Synthetic Other", "other", 1) };
        gate = new ReplayStartGate();
        auto = new ReplayAutoLoad(gate, game, () => now, library, _ => source);
        Press();
        Pump(4);
        Check(game.Calls.Count == 0 && gate.Block is { Reason: "scenario-mismatch" } manual && manual.Message.Contains(stock, StringComparison.Ordinal), "Without game commands the player is asked to load it");
    }
}

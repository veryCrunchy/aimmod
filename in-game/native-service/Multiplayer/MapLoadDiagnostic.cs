using System.Diagnostics;
using System.Globalization;

namespace AimMod.InGame.Multiplayer;

// Developer diagnostic (--check-map-load "<scenario>" [--game <root>] [--output <folder>] [--no-load]):
// with KovaaK's and AimModCore running, loads an AimMod scenario the way a match does
// (load-scenario), then asks AimModCore for its map (ensure-map), and prints what
// core-scene.json shows after each step against the scenario file's MapName/MapScale.
// Exit code 0 when KovaaK's ends up on the scenario's own map.
static class MapLoadDiagnostic
{
    public static int Run(string[] args)
    {
        string? Option(string name) { var i = Array.IndexOf(args, name); return i > 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var scenario = args[1];
        var output = Option("--output") is { } o ? Path.GetFullPath(o) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AimMod", "KovaaksNative");
        if (!GameCommands.MapFixAllowed(scenario)) { Console.Error.WriteLine("Only AimMod's own scenarios (\"AimMod Match - \", \"AimMod - \", \"AimMod Probe \")."); return 2; }
        var root = ContentLibrary.Locate(Option("--game"));
        var file = root is null ? null : Path.Combine(root, "Saved", "SaveGames", "Scenarios", scenario + ".sce");
        var expected = file is not null && File.Exists(file) ? MatchScenario.MapOf(File.ReadAllText(file)) : (null, null);
        Console.WriteLine("scenario: " + scenario);
        Console.WriteLine("expected: " + (expected.MapName ?? "?") + " at " + Num(expected.MapScale) + (file is not null && File.Exists(file) ? "" : " (scenario file not found)"));
        var caps = GameCommands.Capabilities(output);
        Console.WriteLine("capabilities: " + (caps.Count == 0 ? "none (is KovaaK's running with AimModCore?)" : string.Join(',', caps.Order())));
        if (!caps.Contains("load") || !caps.Contains("map")) { Console.Error.WriteLine("AimModCore must advertise load and map."); return 3; }
        var commands = new GameCommands(output);
        Print("before");
        if (!args.Contains("--no-load"))
        {
            var load = Send(commands, "load-scenario", scenario, TimeSpan.FromSeconds(45));
            if (load is null || load.State == "error") return 4;
            Thread.Sleep(1500); // what the match load gate sees once the load is done
            Print("after load-scenario");
        }
        var fix = Send(commands, "ensure-map", scenario, TimeSpan.FromSeconds(20));
        Thread.Sleep(1000);
        var scene = Print("after ensure-map");
        var ok = fix is { State: "done" } && scene is { Loading: false } && scene.Scenario == scenario
            && (expected.MapName is null || MatchScenario.SameMap(scene.MapName, expected.MapName))
            && (expected.MapScale is not double want || scene.MapScale is double shown && Math.Abs(shown - want) <= 0.01);
        Console.WriteLine(ok ? "result: map ok" : "result: map NOT loaded");
        return ok ? 0 : 1;

        GameScene? Print(string when)
        {
            var s = GameScene.Read(output);
            Console.WriteLine(when + ": " + (s is null ? "core-scene.json unavailable" : "scenario=\"" + s.Scenario + "\" mapName=\"" + s.MapName + "\" mapScale=" + Num(s.MapScale) + " loading=" + (s.Loading ? "true" : "false") + " inChallenge=" + (s.InChallenge ? "true" : "false")));
            return s;
        }
    }

    static string Num(double? v) => v is double d ? d.ToString("0.###", CultureInfo.InvariantCulture) : "?";

    // Sends one command and waits for its final answer (accepted/refreshing answers are interim).
    static GameCommandResult? Send(GameCommands commands, string action, string scenario, TimeSpan timeout)
    {
        var (sequence, error) = commands.Send(new(action, scenario, null, null, null, null, null, null));
        if (sequence is not long seq) { Console.Error.WriteLine(action + ": refused (" + error + ")"); return null; }
        var clock = Stopwatch.StartNew();
        GameCommandResult? last = null;
        while (clock.Elapsed < timeout)
        {
            if (commands.ResultFor(seq) is { } r && r != last)
            {
                last = r;
                Console.WriteLine(action + ": " + r.State + " " + r.Code + (r.Message.Length > 0 ? " (" + r.Message + ")" : "") + " after " + clock.ElapsedMilliseconds + " ms");
                if (r.State is "done" or "error") return r;
            }
            Thread.Sleep(100);
        }
        Console.Error.WriteLine(action + ": no answer within " + timeout.TotalSeconds + " s");
        return last;
    }
}

namespace AimMod.InGame;

// Byte-exact samples produced by the AimModCore formatter
// (in-game/native-mod: aimmod_core_tests --write-samples). Keeps the native
// mod and these readers on one contract. Synthetic data only.
static class CoreFormatChecks
{
    const string Journal = "run\t1790000000-42-1\tSynthetic target test\t321.5\t80\t60\t12\t1500\t2026-01-01T00:01:00Z\n";
    const string Live = "{\"version\":1,\"active\":true,\"paused\":false,\"scoreStatus\":\"available\",\"id\":\"1790000000-42-1\",\"scenario\":\"Synthetic target test\",\"score\":100.5,\"seconds\":12.25,\"shots\":10,\"hits\":8,\"kills\":3,\"remainingSeconds\":47.75,\"lastTimeToKillSeconds\":1.5}";
    const string Replay =
        "{\"kind\":\"header\",\"version\":1,\"id\":\"1790000000-42-1\",\"scenario\":\"Synthetic target test\",\"recordedAt\":\"2026-01-01T00:00:00Z\",\"coordinates\":\"unreal-centimeters\",\"nominalHz\":60,\"mapName\":\"Map_A\",\"mapScale\":1,\"startEvent\":\"native\"}\n" +
        "{\"kind\":\"frame\",\"t\":0.01666667,\"camera\":[1,2,3,4,5,6,90],\"actors\":[[1,10,20,30,40,90]],\"health\":[{\"id\":1,\"percent\":0.5}],\"appearance\":[{\"id\":1,\"profile\":\"Bot\",\"rotation\":[0,90,0]}],\"stats\":{\"score\":1,\"seconds\":0.01666667}}\n" +
        "{\"kind\":\"input\",\"t\":0.01666667,\"action\":\"AxisTurn\",\"value\":0.5}\n" +
        "{\"kind\":\"frame\",\"t\":0.03333333,\"camera\":[1,2,3,4,5,6,90],\"actors\":[[1,20,20,30,40,90]],\"health\":[{\"id\":1,\"percent\":0.5}],\"appearance\":[{\"id\":1,\"profile\":\"Bot\",\"rotation\":[0,90,0]}],\"stats\":{\"score\":2,\"seconds\":0.03333333}}\n" +
        "{\"kind\":\"input\",\"t\":0.03333333,\"action\":\"AxisTurn\",\"value\":0.5}\n" +
        "{\"kind\":\"frame\",\"t\":0.05,\"camera\":[1,2,3,4,5,6,90],\"actors\":[[1,30,20,30,40,90]],\"health\":[{\"id\":1,\"percent\":0.5}],\"appearance\":[{\"id\":1,\"profile\":\"Bot\",\"rotation\":[0,90,0]}],\"stats\":{\"score\":3,\"seconds\":0.05}}\n" +
        "{\"kind\":\"input\",\"t\":0.05,\"action\":\"AxisTurn\",\"value\":0.5}\n" +
        "{\"kind\":\"end\",\"reason\":\"completed\",\"frames\":3,\"inputEvents\":3,\"score\":321.5}\n";

    // Format 2 (compact) synthetic attempt from the same tool, LZMS compressed.
    const string Compact = "QU1SUExBWTICAAAAzQEAAHsia2luZCI6ImhlYWRlciIsInZlcnNpb24iOjIsImlkIjoiMTc5MDAwMDAwMC00Mi0yIiwic2NlbmFyaW8iOiJTeW50aGV0aWMgdGFyZ2V0IHRlc3QiLCJyZWNvcmRlZEF0IjoiMjAyNi0wMS0wMVQwMDowMDowMFoiLCJjb29yZGluYXRlcyI6InVucmVhbC1jZW50aW1ldGVycyIsIm5vbWluYWxIeiI6NjAsIm1hcE5hbWUiOiJNYXBfQSIsIm1hcFNjYWxlIjoxLCJzdGFydEV2ZW50IjoibmF0aXZlIiwicmVhc29uIjoiY29tcGxldGVkIiwiZnJhbWVzIjoxNzIsImlucHV0RXZlbnRzIjoyMDUyLCJzY29yZSI6MzIxLjUsImR1cmF0aW9uIjoyLjk5ODUsImVuY29kaW5nIjp7ImtleWZyYW1lcyI6NiwicXVhbnR1bSI6MC4wNywieWF3UGVyVW5pdCI6MC4xMTQ1ODU5OTksInBpdGNoUGVyVW5pdCI6LTAuMTE0NTg2LCJrZXlmcmFtZUVycm9yTWF4Ijo3LjM3NGUtMTAsImtleWZyYW1lRXJyb3JSbXMiOjUuMTI3ZS0xMH19BQAAAM0gAAAKUeXAGADoBc0gAAAAAAAAzSAAAAAAAABMAQAAC0qy1doDBybVsUyvB3hxLFGmoQSQ1H8ePNlBXUa5KZq7gkedYSami6iPAAAAmhhwZs9VAKYP7E/AOjgQqHIDBzbYAQeYDeAHIIDV0mENsYgH4gAWRascnCQCYDoW9EMfUF/4BJAhB0oxhojIB+IBjNAygC7wTo/WkAEEdG9CAwEB187A88LHwNvXDW7ZAAaqTu9h9s8irjSBB8QvTMAqYBmAgdJTABjA4NfqSVBeRf30Vgk+CDBv63vSJAgQMKgGEAgKrCsDrgSBAIOAgwECBIECgQCFAcs2ASugwcABgQBCQYDAgAKBlRAQMGBAcCCAEFBQESgIDBAEJFj9DAQOAhAGCggEBBQGAggGEuyZ4dUk2GfS3PCJBBNuUrYAxZsGru0Z4tXUZ9LEdhaPBOhtXsKNwxcQoPUEMKsKAACAIPyJXS8EqQAGODIUCbA=";

    public static void Run()
    {
        int checks = 0;
        void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
        var root = Path.Combine(Path.GetTempPath(), "aimmod-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "replays"));
        try
        {
            var run = NativeRuns.Parse(Journal.TrimEnd('\n'));
            Check(run is { Id: "native:1790000000-42-1", Scenario: "Synthetic target test", Score: 321.5, Accuracy: 80, Duration: 60 } && run.Kills == 12 && run.Damage == 1500, "native journal line parses");

            File.WriteAllText(Path.Combine(root, "live-overlay.json"), Live);
            var live = LiveOverlayState.Read(root, _ => null, false);
            Check(live.Active && live.Scenario == "Synthetic target test" && live.Score == 100.5 && live.Seconds == 12.25 && live.Accuracy == 80 && live.RemainingSeconds == 47.75 && live.DurationSeconds == 60 && live.LastTimeToKillSeconds == 1.5 && live.AttemptId == "1790000000-42-1", "native live snapshot reads");

            File.WriteAllText(Path.Combine(root, "replays", "1790000000-42-1.amreplay"), Replay);
            var catalog = new ReplayCatalog(root);
            var replay = catalog.Read("1790000000-42-1");
            Check(replay is { Reason: "completed", MapName: "Map_A" } && replay.Frames.Count == 3 && replay.Inputs.Count == 3 && replay.Frames[^1].Stats?.Score == 321.5, "native replay reads");
            Check(catalog.List().Any(r => r.Id == "1790000000-42-1" && r.Frames == 3), "native replay is listed");

            File.WriteAllBytes(Path.Combine(root, "replays", "1790000000-42-2.amreplay"), Convert.FromBase64String(Compact));
            var compact = catalog.Read("1790000000-42-2");
            Check(compact is { Version: 2, Reason: "completed", Scenario: "Synthetic target test", MapName: "Map_A", MapScale: 1 } && compact.Motion is not null && compact.Frames.Count > 10, "format 2 replay decodes");
            Check(compact!.Inputs.Count > 1000 && compact.Inputs.Any(i => i.Action == "FirePressed") && compact.Frames[^1].Stats?.Score == 321.5, "format 2 inputs and final score");
            var start = compact.Motion!.Rotation(compact.Motion.Times[0]);
            Check(Math.Abs(start.Yaw - (10 - 0.35 * 0.114586)) < 1e-3 && Math.Abs(start.Pitch - 0.14 * 0.114586) < 1e-3, "format 2 start orientation");
            var gap = NativeReplayPlayback.Sample(compact, 600 / 400.0);
            var moving = NativeReplayPlayback.Sample(compact, 300 / 400.0 + 0.001);
            Check(gap.Actors.Length == 0 && moving.Actors.Length == 1 && Math.Abs(moving.Actors[0][1] - (1000 + 300.4)) < 1, "format 2 target track with gap");
            Check(catalog.List().Any(r => r.Id == "1790000000-42-2" && r.Frames > 100), "format 2 replay is listed from its header");

            // Replay start gate: a clear reason until the game shows the replay's world paused.
            GameScene Scene(string scenario = "Synthetic target test", bool challenge = false, bool paused = false, bool loading = false) =>
                new(true, scenario, "Map_A", 1, challenge, challenge, loading, paused);
            Check(ReplayStartGate.Evaluate(compact, Scene("Other"), true, "unavailable")?.Reason == "scenario-mismatch", "other scenario blocks with its name");
            Check(ReplayStartGate.Evaluate(compact, Scene("Other"), true, "unavailable")!.Message.Contains("Synthetic target test"), "mismatch names the scenario to load");
            Check(ReplayStartGate.Evaluate(compact, Scene(challenge: true), true, "unavailable")?.Reason == "challenge-active", "running challenge blocks");
            Check(ReplayStartGate.Evaluate(compact, Scene(loading: true), true, "unavailable")?.Reason == "scenario-loading", "loading scenario blocks");
            Check(ReplayStartGate.Evaluate(compact, Scene(challenge: true, paused: true), true, "unavailable") is null, "paused challenge world starts");
            Check(ReplayStartGate.Evaluate(compact, Scene(), false, "challenge-active")?.Reason == "challenge-active", "renderer preflight reason surfaced");
            Check(ReplayStartGate.Evaluate(compact, null, false, "unavailable")?.Reason == "game-unavailable", "no game");
            var startGate = new ReplayStartGate();
            startGate.Wait(compact.Id, compact.Scenario, new("scenario-mismatch", "x"));
            var sceneNow = Scene("Other");
            Check(startGate.Poll(_ => compact, () => sceneNow, () => (true, "unavailable")) is null && startGate.Block?.Reason == "scenario-mismatch", "pending start keeps waiting");
            sceneNow = Scene();
            Check(startGate.Poll(_ => compact, () => sceneNow, () => (true, "unavailable")) == compact && startGate.PendingId is null, "pending start begins once the scenario is ready");

            // Protocol 6 publishes a render-rate motion window while playing.
            var playback = new NativeReplayPlayback(root, () => true, () => 6);
            playback.Load(compact);
            playback.Command("seek", 0.4);
            var paused = playback.Snapshot();
            Check(paused.StartsWith("AIMMOD_REPLAY_6\t") && !paused.Contains("\nmotion\t"), "paused frames carry no motion window");
            playback.Command("play");
            var lines = playback.Snapshot().Split('\n');
            var motionRows = lines.Where(l => l.StartsWith("motion\t")).ToArray();
            Check(motionRows.Length is > 2 and <= NativeReplayPlayback.MotionSamples && motionRows.All(l => l.Split('\t').Length == 9), "motion window rows");
            Check(lines.Any(l => l.StartsWith("velocity\t1\t")), "target velocity row");
            Check(lines.Count(l => l.StartsWith("clock\t") && l.Split('\t').Length == 3 && long.TryParse(l.Split('\t')[1], out var ms) && Math.Abs(ms - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) < 5000) == 1 && !paused.Contains("\nclock\t"),
                "publication clock row only while playing");
            var legacy = new NativeReplayPlayback(root, () => true, () => 5);
            legacy.Load(compact); legacy.Command("play");
            Check(!legacy.Snapshot().Contains("motion\t"), "older renderers receive protocol 5 frames");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"{checks} native core format checks passed.");
    }
}

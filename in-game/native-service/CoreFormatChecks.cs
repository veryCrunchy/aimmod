using System.Globalization;

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

    static bool Throws(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } }

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

            // Game commands: file format shared with AimModCore (GameCommand.cpp).
            var commands = new GameCommands(root);
            var sent = commands.Send(new("start-scenario", "Synthetic target test", "freeplay", 0.5, 1.5, null, null, "Track Master 100"));
            var written = File.ReadAllText(Path.Combine(root, "core-command.tsv"));
            Check(sent.Sequence is > 0 && written.StartsWith("AIMMOD_CORE_COMMAND_1\nseq\t" + sent.Sequence + "\naction\tstart-scenario\nscenario\tSynthetic target test\nmode\tfreeplay\ntimeScale\t0.5\ntargetSize\t1.5\nweapon\tTrack Master 100\n"),
                "start command written in the native format");
            var thumbnail = commands.Send(new("capture-thumbnail", "Synthetic target test", null, null, null, null, null, null, 1920, 1080, "synthetic thumb.png",
                [new(100, -20.5, 300, -10, 45, 90), new(0, 0, 0, 0, 180, 70)]));
            var thumbnailText = File.ReadAllText(Path.Combine(root, "core-command.tsv"));
            Check(thumbnail.Sequence is > 0 && thumbnailText.Contains("action\tcapture-thumbnail\nscenario\tSynthetic target test\nwidth\t1920\nheight\t1080\nout\tsynthetic thumb.png\nview1\t100,-20.5,300,-10,45,90\nview2\t0,0,0,0,180,70\n"),
                "thumbnail command written in the native format");
            Check(commands.Send(new("capture-thumbnail", "S", null, null, null, null, null, null, 1920, 1080, "../x.png", [new(0, 0, 0, 0, 0, 90)])).Error == "invalid-thumbnail"
                && commands.Send(new("capture-thumbnail", "S", null, null, null, null, null, null, 9000, 1080, "x.png", [new(0, 0, 0, 0, 0, 90)])).Error == "invalid-thumbnail"
                && commands.Send(new("capture-thumbnail", "S", null, null, null, null, null, null, 1920, 1080, "x.png", [])).Error == "invalid-thumbnail",
                "thumbnail requests validated");
            var seeded = commands.Send(new("start-scenario", "AimMod Match - Synthetic - 1", "freeplay", null, null, null, null, null, Seed: 4294967295));
            Check(seeded.Sequence is > 0 && File.ReadAllText(Path.Combine(root, "core-command.tsv")).EndsWith("mode\tfreeplay\nseed\t4294967295\n"), "seed written");
            Check(commands.Send(new("start-scenario", "VT PGT", "challenge", null, null, null, null, null, Seed: 1)).Error == "seed-not-allowed"
                && commands.Send(new("start-scenario", "X", null, null, null, null, null, null, Seed: -1)).Error == "invalid-seed", "seeds kept out of ranked play");
            Check(ReplayCompare.Spawns(compact).Count == 2, "spawn events: first appearance and the respawn after the gap");
            var ended = commands.Send(new("end-run", "AimMod Match - Synthetic - 0a1b2c3d", null, null, null, null, null, null, Then: "reset"));
            Check(ended.Sequence is > 0 && File.ReadAllText(Path.Combine(root, "core-command.tsv")).EndsWith("action\tend-run\nscenario\tAimMod Match - Synthetic - 0a1b2c3d\nthen\treset\n"), "end-run written");
            Check(commands.Send(new("end-run", "VT Pasu", null, null, null, null, null, null)).Error == "not-a-match"
                && commands.Send(new("load-scenario", "X", null, null, null, null, null, null, Then: "reset")).Error == "invalid-command", "end-run kept to match scenarios");
            var quit = commands.Send(new("quit-run", null, null, null, null, null, null, null));
            Check(quit.Sequence is > 0 && File.ReadAllText(Path.Combine(root, "core-command.tsv")).EndsWith("\naction\tquit-run\n"), "quit-run written without fields");
            var second = commands.Send(new("reset-overrides", null, null, null, null, null, null, null));
            Check(second.Sequence > sent.Sequence && !File.ReadAllText(Path.Combine(root, "core-command.tsv")).Contains("scenario"), "sequences increase; reset carries no scenario");
            Check(commands.Send(new("delete", "x", null, null, null, null, null, null)).Error == "invalid-command" && commands.Send(new("load-scenario", "a\u0001b", null, null, null, null, null, null)).Error == "invalid-scenario",
                "malformed commands rejected before the game sees them");
            File.WriteAllText(Path.Combine(root, "core-command-result.tsv"), "AIMMOD_CORE_RESULT_1\t42\terror\tchallenge-active\tFinish%09it\n");
            Check(commands.Result() == new GameCommandResult(42, "error", "challenge-active", "Finish\tit"), "native result parsed");
            File.WriteAllText(Path.Combine(root, "core-active.tsv"), $"AIMMOD_CORE_1\t0.1.0\t{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}\ttelemetry,replay,load,start\n");
            Check(GameCommands.Capabilities(root).Contains("load") && GameCommands.Capabilities(root).Contains("start"), "capabilities from the heartbeat");

            // Received replays: format 2 only, decoded before they enter the library.
            var importRoot = Path.Combine(root, "import");
            var imported = ReplayImport.Import(importRoot, Convert.FromBase64String(Compact));
            Check(imported.Id == "1790000000-42-2" && imported.Scenario == "Synthetic target test" && new ReplayCatalog(importRoot).Read("1790000000-42-2") is not null, "received replay imported");
            Check(ReplayImport.Import(importRoot, Convert.FromBase64String(Compact)).Id == "1790000000-42-2", "same replay again is idempotent");
            var damagedReplay = Convert.FromBase64String(Compact); damagedReplay[^5] ^= 0x55;
            Check(ReplayImport.Import(Path.Combine(root, "import2"), damagedReplay).Error == "invalid-replay" && ReplayImport.Import(importRoot, "{\"kind\":\"header\"}"u8.ToArray()).Error == "unsupported-format",
                "damaged or format 1 transfers rejected");

            // Run vs run: the comparison replay's camera on the same timeline.
            var versus = new NativeReplayPlayback(root, () => true, () => 6);
            versus.Load(compact, compact);
            versus.Command("seek", 0.4); versus.Command("play");
            var versusLines = versus.Snapshot().Split('\n');
            Check(versusLines.Count(l => l.StartsWith("ghost\t") && l.Split('\t').Length == 8) == 1 && versusLines.Count(l => l.StartsWith("ghostmotion\t")) > 2, "ghost rows for the comparison run");
            var other = compact with { Scenario = "Other" };
            Check(Throws(() => versus.Load(compact, other)), "comparison must be the same scenario");

            // Spectating: pose format 1 from the bridge, shown behind the newest pose.
            var posePath = Path.Combine(root, "spectate-pose.tsv");
            var poseMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            File.WriteAllText(posePath, "AIMMOD_POSE_1\t1\nmeta\tSynthetic%20target%20test\tMap_A\t1\n" + string.Concat(Enumerable.Range(0, 10).Select(i => $"pose\t{poseMs + i * 33}\t0\t0\t0\t0\t{i * 3}\t0\t90\n")) + "target\t1\t100\t0\t0\t40\t90\n");
            var frame = LivePoseFrame.Parse(File.ReadAllText(posePath));
            Check(frame is { Poses.Count: 10, Targets.Count: 1, Scenario: "Synthetic target test" }, "pose stream parsed");
            Check(LivePoseFrame.Parse("AIMMOD_POSE_1\t1\npose\t5\t0\t0\t0\t0\t0\t0\t90\npose\t4\t0\t0\t0\t0\t0\t0\t90\n") is null, "pose times must increase");
            var duel = LivePoseFrame.Parse("AIMMOD_POSE_1\t2\tsender\npose\t5\t0\t0\t0\t0\t0\t0\t90\ntarget\t3\t100\t0\t0\t40\t90\ntag\t3\t76561190000000001\n"
                + "self\t5\t10\t20\t30\t35\t88\t1\nfire\t5\t42\t1\nfuture\tanything\n");
            Check(duel is { Self: [5, 10, 20, 30, 35, 88, 1], Fire: [5, 42, 1] } && duel.Tags[3] == "76561190000000001", "avatar tags, own body and firing parsed; unknown rows ignored");
            Check(LivePoseFrame.Parse("AIMMOD_POSE_1\t2\npose\t5\t0\t0\t0\t0\t0\t0\t90\nself\t5\t1\t2\t3\t40\t30\t0\n") is null
                && LivePoseFrame.Parse("AIMMOD_POSE_1\t2\npose\t5\t0\t0\t0\t0\t0\t0\t90\nfire\t5\t-1\t0\n") is null, "malformed optional rows rejected");
            var spectate = new NativeReplayPlayback(root, () => true, () => 6);
            var feed = new LivePoseFeed(posePath);
            Check(feed.Update(), "live feed reads the stream");
            spectate.Spectate(feed, "Synthetic target test", "Map_A", 1, "peer");
            var liveLines = spectate.Snapshot().Split('\n');
            var liveMotion = liveLines.Where(l => l.StartsWith("motion\t")).Select(l => double.Parse(l.Split('\t')[1], CultureInfo.InvariantCulture)).ToArray();
            var liveTime = double.Parse(liveLines.First(l => l.StartsWith("time\t")).Split('\t')[1], CultureInfo.InvariantCulture);
            Check(liveLines[0].EndsWith("\t1") && liveMotion.Length >= 2 && Math.Abs(liveMotion[0] - liveTime) < 1e-9 && Math.Abs(liveMotion[^1] - liveTime - LivePoseFeed.Delay) < 0.002
                && liveLines.Any(l => l.StartsWith("actor\t1\t")) && liveLines.Any(l => l.StartsWith("clock\t")), "live frames: delayed display time, window to the newest pose, targets");
            Check(!spectate.Command("seek", 1), "live view cannot be seeked");
            // Follow the leader: switching the watched player in place cuts to the new stream.
            string Stream(string id, long start, int yaw) => $"AIMMOD_POSE_1\t1\t{id}\nmeta\tSynthetic%20target%20test\tMap_A\t1\n" + string.Concat(Enumerable.Range(0, 6).Select(i => $"pose\t{start + i * 33}\t0\t0\t0\t0\t{yaw}\t0\t90\n"));
            var followFeed = new LivePoseFeed(posePath, "playerA");
            File.WriteAllText(posePath, Stream("playerA", poseMs + 10_000, 10));
            Check(followFeed.Update() && followFeed.Window().Window[^1].Camera[4] == 10, "named stream followed");
            File.WriteAllText(posePath, Stream("playerB", poseMs - 50_000, 70)); // another machine's clock
            Check(followFeed.Update() && followFeed.Window().Window[^1].Camera[4] == 10, "an unrequested stream is ignored without ending the view");
            followFeed.Follow("playerB");
            Check(followFeed.Update() && followFeed.Switches == 1 && followFeed.Window().Window.All(p => p.Camera[4] == 70), "switch cuts to the new stream, no blend across the old buffer");
            var followView = new NativeReplayPlayback(root, () => true, () => 6);
            followView.Spectate(new LivePoseFeed(posePath, "playerB"), "Synthetic target test", "Map_A", 1, "b");
            Check(followView.FollowStream("Synthetic target test", "Map_A", "playerC") && !followView.FollowStream("Other", "Map_A", "playerC"), "in-place switch only for the same world");

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

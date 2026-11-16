namespace AimMod.InGame;
static class NativeInspectionChecks
{
    internal static void Run()
    {
        int checks = 0; void Check(bool value, string name) { checks++; if (!value) throw new Exception("Native inspection check: " + name); }
        // 30 seconds: one click a second at a still target; every second click lands and scores 100.
        // The first 10 seconds score double, so the run has a clear strongest and slowest stretch.
        var frames = new List<ReplayFrame>(); var inputs = new List<ReplayInput>();
        double score = 0, shots = 0, hits = 0;
        for (int i = 0; i <= 300; i++)
        {
            var t = i / 10.0;
            if (i > 0 && i % 10 == 0)
            {
                shots++; inputs.Add(new(t - .05, "FirePressed", 1)); inputs.Add(new(t - .02, "FireReleased", 0));
                if (shots % 2 == 0) { hits++; score += t <= 10 ? 200 : 100; }
            }
            frames.Add(new(t, [0, 0, 0, 0, 2, 0, 90], [[1, 1000, 0, 0, 20, 20]], new(score, shots, hits, hits, null, t)));
        }
        var built = NativeInspection.Build("native:synthetic", new NativeReplay(1, "synthetic", "Synthetic", "2026-01-01T00:00:00Z", "completed", 30, frames, inputs));
        var summary = built.Details.Summary!;
        Check(built.Run.Id == "native:synthetic" && built.Run.Scenario == "Synthetic" && built.Run.Score == 2000, "run identity and final score");
        Check(summary.ShotsFired == 30 && summary.ShotsHit == 15 && summary.Accuracy == 50 && summary.AverageShotsToHit == 2, "shot totals");
        Check(Math.Abs(summary.ScorePerMinute!.Value - 4000) < 1e-6 && Math.Abs(summary.KillsPerSecond!.Value - .5) < 1e-6, "run averages");
        Check(built.Details.Timeline.Count == 30 && built.Details.Timeline[^1].Score == 2000, "one timeline point a second");
        var late = built.Details.Timeline[24];
        Check(Math.Abs(late.ScorePerMinute!.Value - 2400) < 1 || Math.Abs(late.ScorePerMinute!.Value - 3600) < 1, "pace is measured over the last five seconds");
        Check(late.Accuracy is >= 40 and <= 60, "accuracy is measured over the last five seconds");
        Check(summary.PeakScorePerMinute > summary.ScorePerMinute, "peak pace comes from a full window");
        Check(built.Windows.Length == 2 && built.Windows[0].Kind == "peak_pace" && built.Windows[0].EndMs <= 10500 && built.Windows[1].Kind == "pace_drop", "strongest and slowest stretch");
        Check(built.Shots.Length == 30 && built.Shots[0].Kind == "miss" && built.Shots[1].Kind == "hit", "each click is a hit or a miss");
        var target = built.Shots[0].Targets.Single();
        Check(target.Nearest && Math.Abs(target.YawErrorDegrees!.Value - 2) < .05 && Math.Abs(target.PitchErrorDegrees!.Value) < .05, "aim error against the nearest target");
        var plain = NativeInspection.Build("native:plain", new NativeReplay(1, "plain", "Synthetic", "2026-01-01T00:00:00Z", "completed", 1,
            [new(0, [0, 0, 0, 0, 0, 0, 90], []), new(1, [0, 0, 0, 0, 0, 0, 90], [])], []));
        Check(plain.Details.Summary!.Score is null && plain.Details.Summary.Accuracy is null && plain.Shots.Length == 0 && plain.Windows.Length == 0, "a replay without counters stays unknown");
        // Three acquisitions: 200 ms wait, a 12 degree flick past a 10 degree target,
        // three missed clicks while beyond it, then a hit after returning to its centre.
        var responseFrames = new List<ReplayFrame>(); var responseInputs = new List<ReplayInput>();
        for (int i = 0; i <= 900; i++)
        {
            var t = i / 250.0; var cycle = (int)(t / 1.2); var local = t - cycle * 1.2;
            var yaw = local < .4 ? 0 : local < .6 ? (local - .4) * 60 : local < .76 ? 12 : local < .84 ? 12 - (local - .76) * 25 : 10;
            var visible = local >= .2 && local < .912;
            var angle = 10 * Math.PI / 180;
            responseFrames.Add(new(t, [0, 0, 0, 0, yaw, 0, 90], visible ? [[cycle + 1, 1000 * Math.Cos(angle), 1000 * Math.Sin(angle), 0, 5, 5]] : [],
                new(null, null, cycle * 1.0 + (local >= .9 ? 1 : 0), cycle * 1.0 + (local >= .9 ? 1 : 0), null, t)));
        }
        for (int cycle = 0; cycle < 3; cycle++)
            foreach (var click in new[] { .64, .68, .72, .896 })
            { responseInputs.Add(new(cycle * 1.2 + click, "FirePressed", 1)); responseInputs.Add(new(cycle * 1.2 + click + .008, "FireReleased", 0)); }
        var responseReplay = new NativeReplay(1, "responses", "Synthetic", "2026-01-01", "completed", 3.6, responseFrames, responseInputs);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var responses = NativeInspection.Build("native:responses", responseReplay);
        timer.Stop();
        Check(responses.Episodes.Length == 3 && responses.Response!.Episodes == 3, "one episode per target appearance");
        Check(responses.Episodes.All(e => e.ReactionMs is >= 180 and <= 204), "known 200 ms reactions within smoothing tolerance");
        Check(responses.Episodes.All(e => e.OvershootDegrees is > 1.5 and < 2.1 && e.Corrections >= 1 && !e.Stable), "overshoot and correction before hit");
        Check(responses.Episodes.All(e => e.FirstOnTargetMs is >= 350 and <= 380), "first capsule arrival");
        Check(responses.Shots.Count(s => s.Kind == "miss") == 9 && responses.Windows.Any(w => w.Kind == "miss_streak" && w.Detail!.Length > 0), "known three click miss streak");
        Check(responses.Shots[0].TargetAgeMs is >= 430 and <= 450 && responses.Shots[0].MissDirection == "right" && responses.Shots[0].MissDegrees is > 1.9 and < 2.1, "shot age and signed miss error");
        Check(responses.Details.Summary!.AverageFireToHitMs is >= 0 and <= 8 && Math.Abs(responses.Details.Summary.CorrectiveShotRatio!.Value - 1) < .001, "fire to hit and corrective ratio");
        Check(timer.Elapsed.TotalSeconds < 1, "short replay analysis is bounded");
        var unknownResponses = NativeInspection.Build("native:unknown", responseReplay with { Frames = responseFrames.Select(f => f with { Stats = null }).ToArray() });
        Check(unknownResponses.Shots.All(s => s.Kind == "unknown") && unknownResponses.Details.Summary!.AverageFireToHitMs is null && unknownResponses.Details.Summary.CorrectiveShotRatio is null, "missing outcomes do not become misses");
        Console.WriteLine($"Synthetic response analysis: {responses.Episodes.Length} episodes, {timer.Elapsed.TotalMilliseconds:0.0} ms.");
        Check(responses.Episodes.All(e => e.TimeToKillMs is >= 690 and <= 710) && responses.Episodes.Skip(1).All(e => e.KillToMovementMs is > 650 and < 720), "kill and next movement timings");
        var held = NativeInspection.Build("native:held", responseReplay with { Inputs = [new(0, "FirePressed", 1), new(3.6, "FireReleased", 0)] });
        Check(held.Shots.All(s => s.Kind != "miss") && held.Details.Summary!.CorrectiveShotRatio is null && held.Details.Summary.AverageFireToHitMs is null, "held fire does not produce discrete missed clicks");
        var root = Path.Combine(Path.GetTempPath(), "native-inspection-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = new ReplayCatalog(root);
            Check(NativeInspection.Read(catalog, "native:missing") is null && NativeInspection.Read(catalog, "desktop-run") is null && NativeInspection.Read(catalog, "native:../x") is null, "unknown and foreign ids are not answered");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"Native inspection checks passed ({checks}).");
    }
}

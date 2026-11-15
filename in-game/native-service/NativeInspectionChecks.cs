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

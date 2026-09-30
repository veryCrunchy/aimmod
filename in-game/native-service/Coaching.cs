using System.Globalization;

namespace AimMod.InGame;

record CoachingInsight(string Id, string Level, string Title, string Detail,
    string? Metric = null, double? StartSec = null, double? EndSec = null);
record RunCoaching(IReadOnlyList<CoachingInsight> Tips, IReadOnlyList<CoachingInsight> Moments);

// Thresholds ported from src/coaching/engine.ts buildRunMomentInsights/buildRunCoachingTips.
// Observations describe the measured run; missing telemetry never implies good performance.
static class Coaching
{
    static string F(double value, string format = "0.0") => value.ToString(format, CultureInfo.InvariantCulture);
    public static RunCoaching Analyze(Run run, RunDetails details)
    {
        var moments = Moments(details.Timeline, details.Summary?.Duration ?? run.Duration);
        var tips = new List<CoachingInsight>(moments.Take(2));
        var s = details.Summary;
        var accuracy = s?.Accuracy ?? run.Accuracy;
        if (s?.ShotsFired is >= 20 && s.ShotsHit is >= 0)
        {
            double ratio = s.ShotsHit > 0 ? s.ShotsFired.Value / s.ShotsHit.Value : s.ShotsFired.Value;
            if (ratio >= 1.7) tips.Add(new("shot-correction-high", "warning", "High correction load", $"This run averaged {F(ratio, "0.00")} shots per hit. Try a short practice block at a comfortable pace and focus on first-shot placement."));
            else if (ratio <= 1.2) tips.Add(new("shot-correction-good", "good", "Clean shot conversion", $"This run averaged {F(ratio, "0.00")} shots per hit. Increase pace in small steps while preserving conversion."));
        }
        if (accuracy is < 75) tips.Add(new("acc-low", "warning", "Build accuracy", "Accuracy was below 75%. Ease your pace and confirm the target before firing."));
        else if (accuracy is >= 92) tips.Add(new("acc-strong", "good", "Strong accuracy", "Accuracy reached at least 92% on this run. Try a small pace increase while keeping shot conversion steady."));
        if (s?.DamageEfficiency is < 85) tips.Add(new("damage-conversion", "tip", "Damage conversion", "Damage efficiency was below 85%. Focus on target confirmation before firing."));
        // A zero-kill tracking scenario does not establish a target-switch speed problem.
        if (s?.KillsPerSecond is > 0 and < .75 && accuracy is >= 88)
            tips.Add(new("speed-gap", "tip", "Build target-switch pace", "Precision was strong while kill pace remained below 0.75 per second. Practice planning the next target during each confirmation."));
        if (s?.ScorePerMinute is double average && s.PeakScorePerMinute is > 0 && (s.PeakScorePerMinute.Value - average) / s.PeakScorePerMinute.Value > .12)
            tips.Add(new("pace-variation", "tip", "Steadier pace", "Average pace was more than 12% below its peak. Use a brief reset cue to keep your rhythm consistent."));
        if (tips.Count == 0 && accuracy.HasValue)
            tips.Add(new("baseline", "tip", "Choose one focus", "These measurements do not show a strong coaching signal. Pick one focus for the next block and compare runs in the same scenario."));
        return new(tips.DistinctBy(t => t.Id).Take(4).ToArray(), moments);
    }

    public static IReadOnlyList<CoachingInsight> Moments(IReadOnlyList<RunTimelinePoint> input, double duration)
    {
        var points = input.Where(p => double.IsFinite(p.Time) && p.Time >= 0).OrderBy(p => p.Time).ToArray();
        if (points.Length < 4) return [];
        duration = double.IsFinite(duration) && duration > 0 ? duration : points[^1].Time;
        double earlyEnd = Math.Max(1, Math.Floor(duration / 3)), lateStart = Math.Floor(duration * 2 / 3);
        double? Mean(double from, double to, Func<RunTimelinePoint, double?> select)
        {
            var values = points.Where(p => p.Time >= from && p.Time <= to).Select(select).Where(v => v.HasValue && double.IsFinite(v.Value)).Select(v => v!.Value).ToArray();
            return values.Length >= 2 ? values.Average() : null;
        }
        var earlyPace = Mean(0, earlyEnd, p => p.ScorePerMinute);
        var latePace = Mean(lateStart, duration, p => p.ScorePerMinute);
        var earlyAcc = Mean(0, earlyEnd, p => p.Accuracy);
        var lateAcc = Mean(lateStart, duration, p => p.Accuracy);
        var result = new List<CoachingInsight>();
        if (earlyPace is > 0 && latePace is > 0 && (earlyPace - latePace) / earlyPace > .12)
        {
            var delta = lateAcc - earlyAcc;
            bool control = delta >= 2.5;
            result.Add(new("late-pace-drop", control ? "tip" : "warning", control ? "More control, less pace" : "Late-run pace drop",
                $"Pace fell from {F(earlyPace.Value, "0")} to {F(latePace.Value, "0")} SPM in the final third." + (control ? $" Accuracy improved by {F(delta!.Value)} percentage points. Add pace back gradually." : " Try holding your opening rhythm through the finish."), "spm", lateStart, duration));
        }
        if (earlyAcc.HasValue && lateAcc - earlyAcc >= 3)
            result.Add(new("accuracy-build", "good", "Accuracy improved", $"Accuracy increased from {F(earlyAcc.Value)}% to {F(lateAcc!.Value)}% in the final third.", "accuracy", lateStart, duration));
        var windows = new[] { ("Opening", 0d, earlyEnd), ("Middle", earlyEnd, Math.Max(earlyEnd + 1, lateStart)), ("Closing", lateStart, duration) };
        var corrections = new List<CoachingInsight>();
        double worstRatio = 0;
        foreach (var (label, from, to) in windows)
        {
            var valid = points.Where(p => p.Time >= from && p.Time <= to && p.ShotsFired is >= 0 && p.ShotsHit is >= 0).ToArray();
            if (valid.Length < 2) continue;
            // Do not invent interval counts across a counter reset or mismatched samples.
            if (valid.Zip(valid.Skip(1)).Any(pair => pair.Second.ShotsFired < pair.First.ShotsFired || pair.Second.ShotsHit < pair.First.ShotsHit)) continue;
            var fired = valid[^1].ShotsFired!.Value - valid[0].ShotsFired!.Value;
            var hit = valid[^1].ShotsHit!.Value - valid[0].ShotsHit!.Value;
            if (fired < 6 || hit > fired) continue;
            double ratio = hit > 0 ? fired / hit : fired;
            if (ratio < 1.6 || ratio <= worstRatio) continue;
            worstRatio = ratio;
            corrections.Clear();
            corrections.Add(new("correction-window", ratio >= 2 ? "warning" : "tip", "Correction-heavy window", $"{label} section needed {F(ratio, "0.00")} shots per hit ({F(hit / fired * 100, "0")}% hit conversion). Focus on first-shot placement in this section.", "accuracy", from, to));
        }
        result.AddRange(corrections);
        var peak = points.Where(p => p.ScorePerMinute is double n && double.IsFinite(n)).MaxBy(p => p.ScorePerMinute);
        if (peak?.ScorePerMinute is > 0)
            result.Add(new("peak-pace", "good", "Peak tempo", $"Best pace reached {F(peak.ScorePerMinute.Value, "0")} SPM around {F(peak.Time, "0")}s.", "spm", Math.Max(0, peak.Time - 4), Math.Min(duration, peak.Time + 4)));
        var low = points.Where(p => p.Accuracy is >= 0 and <= 100).MinBy(p => p.Accuracy);
        if (low?.Accuracy is < 78)
            result.Add(new("low-accuracy", "tip", "Accuracy dip", $"Lowest measured accuracy was {F(low.Accuracy.Value)}% around {F(low.Time, "0")}s. Settle your aim before the click in this section.", "accuracy", Math.Max(0, low.Time - 4), Math.Min(duration, low.Time + 4)));
        return result.OrderBy(i => i.Level == "warning" ? 0 : i.Level == "tip" ? 1 : 2).Take(4).ToArray();
    }

    public static void SelfTest()
    {
        static void Check(bool ok, string name) { if (!ok) throw new Exception("Coaching check failed: " + name); }
        Check(RunMetrics.Accuracy(null, null, null) == null, "unknown accuracy");
        Check(RunMetrics.Accuracy(.9, 20, 40) == 50, "shot counts take precedence");
        Check(RunMetrics.Accuracy(double.NaN, null, null) == null, "nonfinite accuracy");
        Check(RunMetrics.Accuracy(.9, null, null) == 90, "bridge ratio normalization");
        var run = new Run("synthetic", "Practice", 100, null, 60, 0, 0, "", null, null, null, null, false);
        Check(Analyze(run, new(run.Id, null, [])).Tips.Count == 0, "unknown metrics do not imply stable run");
        var points = Enumerable.Range(0, 61).Select(t => new RunTimelinePoint(t, t < 21 ? 100 : 70, null, 90, null, null, null, null, null)).ToArray();
        Check(Moments(points, 60).Any(m => m.Id == "late-pace-drop" && m.Level == "warning"), "late pace threshold");
        Check(!Moments(points.Select(p => p with { ScorePerMinute = 90 }).ToArray(), 60).Any(m => m.Id == "late-pace-drop"), "constant pace");
        Check(!Moments(points, 60).Any(m => m.Id == "correction-window"), "unknown shot counts");
        Console.WriteLine("8 coaching and metric checks passed.");
    }
}

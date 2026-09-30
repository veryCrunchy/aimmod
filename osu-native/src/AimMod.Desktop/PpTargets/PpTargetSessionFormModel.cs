namespace AimMod.Desktop.PpTargets;

public sealed record PpSessionPatternForm(string Setup, string Pattern, double AccuracyDelta,
    double MissRateDelta, int Plays, int Maps, string Observation, bool LegacyScore = false, bool UsesSimilarMaps = false);

public sealed record PpSessionSupport(string Setup, bool LegacyScore, int RecentPlays, int ComparablePlays, int ComparableMaps);

public sealed record PpSessionForm(DateTimeOffset ExpiresAt, IReadOnlyList<PpSessionPatternForm> Patterns,
    IReadOnlyList<PpSessionSupport>? Support = null)
{
    public string StatusFor(string setup, bool legacyScore, DateTimeOffset now)
    {
        if (ExpiresAt <= now) return "Session form: no active session. Your longer-term skill profile is used.";
        var support = Support?.FirstOrDefault(s => s.Setup == setup && s.LegacyScore == legacyScore);
        if (support is null) return "Session form: no recent replay results for this mod setup.";
        if (support.RecentPlays < 5)
            return $"Session form: {support.RecentPlays}/5 recent plays for this mod setup. Your longer-term skill profile is used.";
        return $"Session form: {support.RecentPlays} recent plays; {support.ComparablePlays}/5 comparable plays across {support.ComparableMaps}/3 maps. Your longer-term skill profile is used.";
    }

    public string Summary => Patterns.Count == 0 ? "Session form: more comparable plays needed"
        : string.Join(" / ", Patterns.Where(p => p.Pattern != "Overall")
            .OrderByDescending(p => Math.Abs(p.AccuracyDelta) + Math.Abs(p.MissRateDelta))
            .Take(2).Select(p => $"{p.Pattern}: {p.Observation}")
            .DefaultIfEmpty(Patterns[0].Observation));
}

/// <summary>Short-term performance evidence, not a diagnosis of fatigue or physical warm-up.</summary>
public static class PpTargetSessionFormModel
{
    private sealed record Residual(PpPatternEvidence Play, double Accuracy, double Misses, bool UsesSimilarMaps = false);

    public static PpSessionForm Build(IEnumerable<PpPatternEvidence> evidence, DateTimeOffset now)
    {
        var all = evidence.Where(e => e.PlayedAt <= now && e.PlayedAt >= now.AddDays(-30)
                && e.SetupKey is not null && e.Features.PointCount >= 20
                && !e.ModsKey.Split('+').Any(m => m is "RX" or "AP" or "AT" or "CN" or "NF" or "SD" or "PF"))
            .GroupBy(e => e.ScoreId).Select(g => g.First()).OrderByDescending(e => e.PlayedAt).ToArray();
        var current = new List<PpPatternEvidence>();
        DateTimeOffset last = now;
        foreach (var play in all)
        {
            if (last - play.PlayedAt >= TimeSpan.FromMinutes(60) || now - play.PlayedAt > TimeSpan.FromHours(3)) break;
            current.Add(play); last = play.PlayedAt;
            if (current.Count == 24) break;
        }
        var results = new List<PpSessionPatternForm>();
        var supportCounts = new List<PpSessionSupport>();
        DateTimeOffset expires = current.Count > 0 ? current[0].PlayedAt.AddMinutes(60) : now;
        foreach (var setup in current.GroupBy(e => (Setup: e.SetupKey!, e.LegacyScore)))
        foreach (string pattern in new[] { "Overall", "Jumps", "Streams", "Bursts", "Speed", "Direction changes" })
        {
            var residuals = new List<Residual>();
            foreach (var play in setup)
            {
                if (!valid(play, pattern)) continue;
                // Compare the same map and complete mod configuration on earlier days.
                // Harder maps and repeated attempts today cannot lower their own baseline.
                var baseline = all.Where(e => e.MapKey == play.MapKey && e.SetupKey == setup.Key.Setup && e.LegacyScore == setup.Key.LegacyScore
                        && e.PlayedAt.UtcDateTime.Date < current[^1].PlayedAt.UtcDateTime.Date
                        && valid(e, pattern)
                        && Math.Abs(e.Features.PointCount - play.Features.PointCount) <= .1 * e.Features.PointCount)
                    .GroupBy(e => e.PlayedAt.UtcDateTime.Date).ToArray();
                if (baseline.Length >= 2)
                {
                    double accuracy = median(baseline.Select(g => median(g.Select(e => e.Outcomes[pattern].Accuracy))));
                    double misses = median(baseline.Select(g => median(g.Select(e => e.Outcomes[pattern].MissRate))));
                    residuals.Add(new(play, play.Outcomes[pattern].Accuracy - accuracy, play.Outcomes[pattern].MissRate - misses));
                    continue;
                }
                // New maps can still measure session form against comparable previous
                // patterns. Never use this session's outcomes to establish its baseline.
                var neighbours = all.Where(e => e.MapKey != play.MapKey && e.SetupKey == setup.Key.Setup
                        && e.LegacyScore == setup.Key.LegacyScore && e.PlayedAt <= current[^1].PlayedAt.AddHours(-6)
                        && valid(e, pattern) && comparableGeometry(play.Features, e.Features))
                    .Select(e => (Play: e, Similarity: PpTargetPatternModel.Similarity(play.Features, e.Features, pattern)))
                    .Where(e => e.Similarity >= .35)
                    .GroupBy(e => e.Play.MapKey).OrderByDescending(g => g.Max(e => e.Similarity)).Take(8).ToArray();
                if (neighbours.Length < 3 || neighbours.Sum(g => g.Count()) < 5) continue;
                // One vote per map prevents retry-heavy maps from defining the baseline.
                double historicalAccuracy = median(neighbours.Select(g => g.Sum(e => e.Similarity * e.Play.Outcomes[pattern].Accuracy) / g.Sum(e => e.Similarity)));
                double historicalMisses = median(neighbours.Select(g => g.Sum(e => e.Similarity * e.Play.Outcomes[pattern].MissRate) / g.Sum(e => e.Similarity)));
                residuals.Add(new(play, play.Outcomes[pattern].Accuracy - historicalAccuracy,
                    play.Outcomes[pattern].MissRate - historicalMisses, true));
            }
            int maps = residuals.Select(r => r.Play.MapKey).Distinct().Count();
            if (pattern == "Overall") supportCounts.Add(new(setup.Key.Setup, setup.Key.LegacyScore, setup.Count(), residuals.Count, maps));
            if (residuals.Count < 5 || maps < 3) continue;
            // Each map gets one vote; a retry loop cannot dominate current form.
            double a = median(residuals.GroupBy(r => r.Play.MapKey).Select(g => median(g.Select(r => r.Accuracy))));
            double m = median(residuals.GroupBy(r => r.Play.MapKey).Select(g => median(g.Select(r => r.Misses))));
            bool usesSimilar = residuals.Any(r => r.UsesSimilarMaps);
            double support = Math.Min(.65, residuals.Count / (double)(residuals.Count + 8))
                * (usesSimilar ? .5 : 1);
            double adjustedA = Math.Clamp(a * support, usesSimilar ? -.0075 : -.015, usesSimilar ? .005 : .01);
            double adjustedM = Math.Clamp(m * support, usesSimilar ? -.005 : -.01, usesSimilar ? .0075 : .015);
            string observation = a - m > .0075 ? "above your usual level" : a - m < -.0075 ? "below your usual level" : "close to your usual level";
            if (residuals.Count >= 6)
            {
                var ordered = residuals.OrderBy(r => r.Play.PlayedAt).ToArray();
                int half = ordered.Length / 2;
                double trend = median(ordered.TakeLast(half).Select(r => r.Accuracy - r.Misses))
                    - median(ordered.Take(half).Select(r => r.Accuracy - r.Misses));
                if (trend >= .015) observation += "; improving through this session";
                else if (trend <= -.015) observation += "; recent plays have dipped";
            }
            results.Add(new(setup.Key.Setup, pattern, adjustedA, adjustedM, residuals.Count, maps, observation, setup.Key.LegacyScore, usesSimilar));
        }
        return new(expires, results, supportCounts);
    }

    private static bool comparableGeometry(PpPatternFeatures a, PpPatternFeatures b) =>
        a.InvalidPointCount == 0 && b.InvalidPointCount == 0
        && a.HitRadius is > 0 && b.HitRadius is > 0 && Math.Abs(Math.Log(a.HitRadius.Value / b.HitRadius.Value)) <= Math.Log(1.15)
        && a.ClockRate is > 0 && b.ClockRate is > 0 && Math.Abs(a.ClockRate.Value - b.ClockRate.Value) < .01
        && a.DurationSeconds is > 0 && b.DurationSeconds is > 0
        && Math.Abs(Math.Log(a.DurationSeconds.Value / b.DurationSeconds.Value)) <= Math.Log(1.6)
        && a.PointCount > 0 && b.PointCount > 0 && Math.Abs(Math.Log((double)a.PointCount / b.PointCount)) <= Math.Log(1.6);

    private static bool valid(PpPatternEvidence e, string pattern) => e.Outcomes.TryGetValue(pattern, out var o)
        && o.ObjectCount >= 8 && double.IsFinite(o.Accuracy) && o.Accuracy is >= 0 and <= 1
        && double.IsFinite(o.MissRate) && o.MissRate is >= 0 and <= 1;

    private static double median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        return (ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) / 2;
    }
}

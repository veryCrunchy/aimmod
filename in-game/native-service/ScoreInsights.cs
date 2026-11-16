namespace AimMod.InGame;

sealed record InsightBand(double? Value, double? ChangePoints);
sealed record InsightLearning(double? PointsPer10Runs, double? PointsPerWeek, int Samples, string Confidence, string Statement);
sealed record InsightPlateau(bool? Detected, int? Runs, double? Days);
sealed record ScenarioInsight(string Name, int Samples, int PreviousSamples, InsightBand Floor, InsightBand Typical, InsightBand Peak,
    double? SpreadPercent, InsightLearning Learning, InsightPlateau Plateau, double? LaterScoreChangePoints,
    double? LaterAccuracyChangePercentagePoints, double? PersonalBest, double? Latest, double? GapToBestPercent, int NearBestSamples, int Within2Percent, int Within5Percent);
sealed record PracticeInsight(string Key, string Label, double? Value, string Unit, int Samples, int ComparisonSamples, string Confidence, string Action);

// History equality includes corrections to existing records, not only appended runs.
static class ScoreInsights
{
    static readonly object Gate = new();
    static Run[]? history;
    static Dictionary<string, ScenarioInsight> scenarios = new(StringComparer.OrdinalIgnoreCase);
    static PracticeInsight[] practice = [];
    internal sealed record Entry(Run Run, DateTimeOffset Time);
    public static object Build(Run[] runs, string selected)
    {
        lock (Gate)
        {
            if (history is null || !runs.SequenceEqual(history))
            {
                var entries = runs.Where(r => double.IsFinite(r.Score) && r.Score != 0 && Math.Abs(r.Score) <= 1e12 && double.IsFinite(r.Duration) && r.Duration is > 0 and <= 1e9)
                    .Select(r => HubHistory.Date(r.Timestamp) is var t && t != DateTimeOffset.MinValue ? new Entry(r, t) : null)
                    .OfType<Entry>().OrderBy(e => e.Time).ThenBy(e => e.Run.Id, StringComparer.Ordinal).ToArray();
                scenarios = entries.GroupBy(e => e.Run.Scenario.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => Analyze(g.Key, g.ToArray()), StringComparer.OrdinalIgnoreCase);
                practice = PracticeStructure.Analyze(entries);
                history = runs.ToArray();
            }
            return new { scenario = scenarios.GetValueOrDefault(selected.Trim()) ?? Analyze(selected, []), practice };
        }
    }
    internal static double Quantile(IEnumerable<double> values, double p)
    {
        var a = values.Order().ToArray();
        if (a.Length == 0) return 0;
        var x = (a.Length - 1) * p; var i = (int)x;
        return a[i] + (a[Math.Min(i + 1, a.Length - 1)] - a[i]) * (x - i);
    }
    internal static ScenarioInsight Analyze(string name, Entry[] all)
    {
        var recent = all.TakeLast(30).ToArray(); var previous = all.SkipLast(recent.Length).TakeLast(30).ToArray();
        double? Q(Entry[] a, double p) => a.Length >= 10 ? Quantile(a.Select(e => e.Run.Score), p) : null;
        InsightBand Band(double p) { var v = Q(recent, p); var old = Q(previous, p); return new(v, v - old); }
        var floor = Band(.1); var typical = Band(.5); var peak = Band(.9);
        var slopes = new List<double>(); var week = new List<double>();
        if (recent.Length >= 20)
            for (int i = 0; i < recent.Length; i++) for (int j = i + 1; j < recent.Length; j++)
            {
                slopes.Add((recent[j].Run.Score - recent[i].Run.Score) / (j - i));
                var days = (recent[j].Time - recent[i].Time).TotalDays;
                if (days >= 1) week.Add((recent[j].Run.Score - recent[i].Run.Score) / days * 7);
            }
        var enough = slopes.Count > 0;
        var strong = enough && (Quantile(slopes, .25) > 0 || Quantile(slopes, .75) < 0);
        var learning = new InsightLearning(enough ? Quantile(slopes, .5) * 10 : null,
            enough && (recent[^1].Time - recent[0].Time).TotalDays >= 7 && recent.Select(e => e.Time.UtcDateTime.Date).Distinct().Count() >= 3 && week.Count > 0 ? Quantile(week, .5) : null,
            recent.Length, enough ? strong ? "supported" : "mixed" : "low", enough ? strong ? "Recent scores show a clear direction." : "Recent scores vary; the direction is uncertain." : "Not enough runs for a learning rate (20 needed).");
        int? since = null; double? elapsed = null;
        if (all.Length >= 30)
        {
            var best = Quantile(all.Take(10).Select(e => e.Run.Score), .5); var last = 9;
            for (int i = 10; i < all.Length; i++)
            {
                var median = Quantile(all.Skip(i - 9).Take(10).Select(e => e.Run.Score), .5);
                if (median > best + Math.Max(Math.Abs(best) * .01, .000001)) { best = median; last = i; }
            }
            since = all.Length - 1 - last; elapsed = (all[^1].Time - all[last].Time).TotalDays;
        }
        var half = recent.Length / 2; var earlier = recent.Take(half).ToArray(); var later = recent.TakeLast(half).ToArray();
        var ea = earlier.Where(e => e.Run.Accuracy is >= 0 and <= 100).Select(e => e.Run.Accuracy!.Value).ToArray();
        var la = later.Where(e => e.Run.Accuracy is >= 0 and <= 100).Select(e => e.Run.Accuracy!.Value).ToArray();
        double? pb = all.Length > 0 ? all.Max(e => e.Run.Score) : null; double? latest = all.LastOrDefault()?.Run.Score;
        return new(name, recent.Length, previous.Length, floor, typical, peak,
            typical.Value is { } t && t != 0 ? (peak.Value - floor.Value) / Math.Abs(t) * 100 : null,
            learning, new(since.HasValue ? since >= 20 : null, since, elapsed),
            half >= 10 ? Quantile(later.Select(e => e.Run.Score), .5) - Quantile(earlier.Select(e => e.Run.Score), .5) : null,
            ea.Length >= 10 && la.Length >= 10 ? Quantile(la, .5) - Quantile(ea, .5) : null,
            pb, latest, pb.HasValue && pb != 0 ? (pb - latest) / Math.Abs(pb.Value) * 100 : null, recent.Length,
            recent.Count(e => pb - e.Run.Score <= Math.Abs(pb ?? 0) * .02), recent.Count(e => pb - e.Run.Score <= Math.Abs(pb ?? 0) * .05));
    }
}

using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.PpTargets;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop.Coaching;

/// <summary>A mean over plays with its 95% confidence interval and how many plays it rests on.</summary>
public sealed record Estimate(double Mean, double Low, double High, int Plays)
{
    /// <summary>Student's t interval of the mean; null below two values.</summary>
    public static Estimate? Of(IEnumerable<double> values)
    {
        double[] v = values.Where(double.IsFinite).ToArray();
        if (v.Length < 2) return null;
        double mean = v.Average();
        double sd = Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / (v.Length - 1));
        double half = t95(v.Length - 1) * sd / Math.Sqrt(v.Length);
        return new(mean, mean - half, mean + half, v.Length);
    }

    private static readonly double[] t_table = [12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228,
        2.201, 2.179, 2.160, 2.145, 2.131, 2.120, 2.110, 2.101, 2.093, 2.086, 2.080, 2.074, 2.069, 2.064, 2.060, 2.056, 2.052, 2.048, 2.045, 2.042];

    private static double t95(int degrees) => degrees <= t_table.Length ? t_table[degrees - 1] : 1.96;
}

/// <param name="EarlyShare">Share of all timed hits that were early.</param>
public sealed record FolderTiming(Estimate? UnstableRate, Estimate? MeanOffsetMs, double? EarlyShare, int Plays, int Hits);

/// <param name="Share">This key's share of the hits whose key is known.</param>
public sealed record FolderKey(string Key, int Hits, double Share, Estimate? MeanOffsetMs, Estimate? UnstableRate);

/// <summary>One pattern class in the player's earlier and more recent half of analysed plays.</summary>
public sealed record FolderPatternTrend(string Pattern, int Plays, Estimate? EarlierAccuracy, Estimate? RecentAccuracy,
    Estimate? EarlierMissRate, Estimate? RecentMissRate, Estimate? EarlierUnstableRate, Estimate? RecentUnstableRate);

public sealed record FolderMiss(LocalReplay Run, double TimeMs, ReplayMissReason? Reason);

public sealed record ReplayFolderSummary(int Plays, int FailedPlays, int PlaysWithKeys, FolderTiming Timing,
    IReadOnlyList<FolderKey> Keys, IReadOnlyList<FolderPatternTrend> Patterns, IReadOnlyList<FolderMiss> RecentMisses)
{
    public static ReplayFolderSummary Empty { get; } = new(0, 0, 0, new(null, null, null, 0, 0), [], [], []);
}

/// <summary>
/// Sums up every analysed replay in the library, failed plays included, so timing, key bias and pattern trends do
/// not need replays watched again. Every aggregate is a mean over plays with its interval and play count.
/// </summary>
public static class ReplayFolderAnalysis
{
    private const int minimum_hits = 5, recent_misses = 12, recent_plays_for_misses = 8;

    public static ReplayFolderSummary Build(IReadOnlyList<LocalReplay> runs, IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses, DateTimeOffset now)
    {
        var plays = runs.Where(r => r.RulesetShortName == "osu" && ScoreMods.IsManualPlay(r) && analyses.ContainsKey(r.ScoreId))
            .DistinctBy(r => r.ScoreId).OrderBy(r => r.PlayedAt).ToArray();
        if (plays.Length == 0) return ReplayFolderSummary.Empty;

        var urs = new List<double>();
        var means = new List<double>();
        int hits = 0, early = 0, withKeys = 0;
        var keyHits = new Dictionary<string, int>();
        var keyMeans = new Dictionary<string, List<double>>();
        var keyUrs = new Dictionary<string, List<double>>();
        foreach (var run in plays)
        {
            double? clock = PpTargetDifficulty.ClockRate(run.Mods, run.ModsJson);
            var taps = analyses[run.ScoreId].Judgements.Where(ReplayJudgementClassifier.IsTapTimingSample).ToArray();
            var (mean, deviation) = PpTargetPatternModel.HitError(taps, clock);
            if (mean is { } m && deviation is { } d) { means.Add(m); urs.Add(d * 10); }
            foreach (var j in taps)
            {
                double offset = j.TimeOffsetMs / (j.GameplayRate is > 0 ? j.GameplayRate.Value : clock is > 0 ? clock.Value : 1);
                if (Math.Abs(offset) > 400) continue;
                hits++;
                if (offset < 0) early++;
            }
            var keyed = taps.Where(j => j.PressedKey is ReplayPressedKey.Left or ReplayPressedKey.Right).GroupBy(j => j.PressedKey!).ToArray();
            if (keyed.Length == 0) continue;
            withKeys++;
            foreach (var group in keyed)
            {
                keyHits[group.Key] = keyHits.GetValueOrDefault(group.Key) + group.Count();
                var (keyMean, keyDeviation) = PpTargetPatternModel.HitError(group, clock);
                if (keyMean is null || keyDeviation is null || group.Count() < minimum_hits) continue;
                (keyMeans.TryGetValue(group.Key, out var a) ? a : keyMeans[group.Key] = []).Add(keyMean.Value);
                (keyUrs.TryGetValue(group.Key, out var b) ? b : keyUrs[group.Key] = []).Add(keyDeviation.Value * 10);
            }
        }

        int keyedHits = keyHits.Values.Sum();
        var keys = keyHits.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => new FolderKey(k.Key, k.Value, k.Value / (double)keyedHits,
            Estimate.Of(keyMeans.GetValueOrDefault(k.Key) ?? []), Estimate.Of(keyUrs.GetValueOrDefault(k.Key) ?? []))).ToArray();

        return new ReplayFolderSummary(plays.Length, plays.Count(p => !p.Passed), withKeys,
            new FolderTiming(Estimate.Of(urs), Estimate.Of(means), hits == 0 ? null : early / (double)hits, urs.Count, hits),
            keys, patterns(plays, analyses, now), misses(plays, analyses));
    }

    /// <summary>Each pattern class in the earlier and recent half of the plays that contain it.</summary>
    private static IReadOnlyList<FolderPatternTrend> patterns(LocalReplay[] plays, IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses, DateTimeOffset now)
    {
        var evidence = PpTargetPatternModel.BuildProfile(plays, analyses, now: now, recencyDays: 3650).Evidence.OrderBy(e => e.PlayedAt).ToArray();
        var trends = new List<FolderPatternTrend>();
        foreach (string pattern in evidence.SelectMany(e => e.Outcomes.Keys).Distinct().Order(StringComparer.Ordinal))
        {
            var outcomes = evidence.Where(e => e.Outcomes.TryGetValue(pattern, out var o) && o.ObjectCount >= minimum_hits)
                .Select(e => e.Outcomes[pattern]).ToArray();
            if (outcomes.Length < 4) continue;
            var earlier = outcomes[..(outcomes.Length / 2)];
            var recent = outcomes[(outcomes.Length / 2)..];
            trends.Add(new FolderPatternTrend(pattern, outcomes.Length,
                Estimate.Of(earlier.Select(o => o.Accuracy)), Estimate.Of(recent.Select(o => o.Accuracy)),
                Estimate.Of(earlier.Select(o => o.MissRate)), Estimate.Of(recent.Select(o => o.MissRate)),
                Estimate.Of(earlier.Select(o => o.UnstableRate ?? double.NaN)), Estimate.Of(recent.Select(o => o.UnstableRate ?? double.NaN))));
        }
        return trends.OrderBy(t => t.Pattern == "Overall" ? 0 : 1).ThenByDescending(t => t.Plays).ToArray();
    }

    private static IReadOnlyList<FolderMiss> misses(LocalReplay[] plays, IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses) =>
        plays.OrderByDescending(p => p.PlayedAt).Take(recent_plays_for_misses)
            .SelectMany(p => analyses[p.ScoreId].Judgements.Where(j => j.MaximumResult == "Great" && ReplayJudgementClassifier.IsMiss(j))
                .Select(j => new FolderMiss(p, j.StartTimeMs, j.MissAnalysis?.Reason)))
            .Take(recent_misses).ToArray();
}

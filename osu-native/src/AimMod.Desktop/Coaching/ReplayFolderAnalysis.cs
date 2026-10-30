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

    /// <summary>The interval cut to the values the measure can take, such as accuracy between 0 and 1.</summary>
    public Estimate Within(double minimum, double maximum) => this with { Low = Math.Max(minimum, Low), High = Math.Min(maximum, High) };

    private static readonly double[] t_table = [12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228,
        2.201, 2.179, 2.160, 2.145, 2.131, 2.120, 2.110, 2.101, 2.093, 2.086, 2.080, 2.074, 2.069, 2.064, 2.060, 2.056, 2.052, 2.048, 2.045, 2.042];

    private static double t95(int degrees) => degrees <= t_table.Length ? t_table[degrees - 1] : 1.96;
}

/// <param name="EarlyShare">Share of all timed hits that were early.</param>
public sealed record FolderTiming(Estimate? UnstableRate, Estimate? MeanOffsetMs, double? EarlyShare, int Plays, int Hits);

/// <param name="Share">This key's share of the hits whose key is known.</param>
public sealed record FolderKey(string Key, int Hits, double Share, Estimate? MeanOffsetMs, Estimate? UnstableRate);

/// <summary>One pattern class in the earlier and later plays: by halves of the plays, or before and since a date.</summary>
public sealed record FolderPatternTrend(string Pattern, int Plays, Estimate? EarlierAccuracy, Estimate? RecentAccuracy,
    Estimate? EarlierMissRate, Estimate? RecentMissRate, Estimate? EarlierUnstableRate, Estimate? RecentUnstableRate);

public sealed record FolderMiss(LocalReplay Run, double TimeMs, ReplayMissReason? Reason);

/// <summary>Timing and keys of the plays before a date and of the plays on or after it.</summary>
public sealed record FolderComparison(DateTimeOffset Since, int PlaysBefore, int PlaysSince, FolderTiming Before, FolderTiming After,
    IReadOnlyList<FolderKey> KeysBefore, IReadOnlyList<FolderKey> KeysSince);

public sealed record ReplayFolderSummary(int Plays, int FailedPlays, int PlaysWithKeys, FolderTiming Timing,
    IReadOnlyList<FolderKey> Keys, IReadOnlyList<FolderPatternTrend> Patterns, IReadOnlyList<FolderMiss> RecentMisses)
{
    public static ReplayFolderSummary Empty { get; } = new(0, 0, 0, new(null, null, null, 0, 0), [], [], []);

    /// <summary>Set when the summary compares plays before and since a date; patterns are then split by that date too.</summary>
    public FolderComparison? Comparison { get; init; }
}

/// <summary>
/// Sums up every analysed replay in the library, failed plays included, so timing, key bias and pattern trends do
/// not need replays watched again. Every aggregate is a mean over plays with its interval and play count.
/// </summary>
public static class ReplayFolderAnalysis
{
    private const int minimum_hits = 5, recent_misses = 12, recent_plays_for_misses = 8;

    /// <param name="localSets">Installed maps, for circle size. Without a play's circle size its jumps, bursts and streams cannot be told apart.</param>
    /// <param name="since">Compare the plays before this time with the plays on or after it, instead of earlier and recent halves.</param>
    public static ReplayFolderSummary Build(IReadOnlyList<LocalReplay> runs, IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses, DateTimeOffset now,
        IEnumerable<LocalBeatmapSet>? localSets = null, DateTimeOffset? since = null)
    {
        var plays = runs.Where(r => r.RulesetShortName == "osu" && ScoreMods.IsManualPlay(r) && analyses.ContainsKey(r.ScoreId))
            .DistinctBy(r => r.ScoreId).OrderBy(r => r.PlayedAt).ToArray();
        if (plays.Length == 0) return ReplayFolderSummary.Empty;

        var (timing, keys, withKeys) = timingAndKeys(plays, analyses);
        FolderComparison? comparison = null;
        if (since is { } cut)
        {
            var before = plays.Where(p => p.PlayedAt < cut).ToArray();
            var after = plays.Where(p => p.PlayedAt >= cut).ToArray();
            var (beforeTiming, beforeKeys, _) = timingAndKeys(before, analyses);
            var (afterTiming, afterKeys, _) = timingAndKeys(after, analyses);
            comparison = new FolderComparison(cut, before.Length, after.Length, beforeTiming, afterTiming, beforeKeys, afterKeys);
        }
        return new ReplayFolderSummary(plays.Length, plays.Count(p => !p.Passed), withKeys, timing, keys,
            patterns(plays, analyses, now, localSets, since), misses(plays, analyses)) { Comparison = comparison };
    }

    private static (FolderTiming Timing, IReadOnlyList<FolderKey> Keys, int WithKeys) timingAndKeys(LocalReplay[] plays, IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses)
    {
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
            Estimate.Of(keyMeans.GetValueOrDefault(k.Key) ?? []), Estimate.Of(keyUrs.GetValueOrDefault(k.Key) ?? [])?.Within(0, double.MaxValue))).ToArray();
        return (new FolderTiming(Estimate.Of(urs)?.Within(0, double.MaxValue), Estimate.Of(means), hits == 0 ? null : early / (double)hits, urs.Count, hits), keys, withKeys);
    }

    /// <summary>
    /// Each pattern class in the earlier and recent half of the plays that contain it, or before and since a date.
    /// A play's circle size comes from a miss's measured hit radius, or else from the installed map.
    /// </summary>
    private static IReadOnlyList<FolderPatternTrend> patterns(LocalReplay[] plays, IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses, DateTimeOffset now,
        IEnumerable<LocalBeatmapSet>? localSets, DateTimeOffset? since)
    {
        var contexts = new Dictionary<Guid, PpPatternContext>();
        foreach (var play in plays)
            if (analyses[play.ScoreId].Judgements.Select(j => j.MissAnalysis?.HitRadius).FirstOrDefault(r => r is > 0 && double.IsFinite(r.Value)) is { } radius)
                contexts[play.ScoreId] = new PpPatternContext(radius, null);
        var evidence = PpTargetPatternModel.BuildProfile(plays, analyses, contexts, now: now, recencyDays: 3650, localSets: localSets)
            .Evidence.OrderBy(e => e.PlayedAt).ToArray();
        var trends = new List<FolderPatternTrend>();
        foreach (string pattern in evidence.SelectMany(e => e.Outcomes.Keys).Distinct().Order(StringComparer.Ordinal))
        {
            var withPattern = evidence.Where(e => e.Outcomes.TryGetValue(pattern, out var o) && o.ObjectCount >= minimum_hits).ToArray();
            PpPatternOutcome[] earlier, recent;
            if (since is { } cut)
            {
                earlier = withPattern.Where(e => e.PlayedAt < cut).Select(e => e.Outcomes[pattern]).ToArray();
                recent = withPattern.Where(e => e.PlayedAt >= cut).Select(e => e.Outcomes[pattern]).ToArray();
                if (earlier.Length < 2 && recent.Length < 2) continue;
            }
            else
            {
                var outcomes = withPattern.Select(e => e.Outcomes[pattern]).ToArray();
                if (outcomes.Length < 4) continue;
                earlier = outcomes[..(outcomes.Length / 2)];
                recent = outcomes[(outcomes.Length / 2)..];
            }
            Estimate? share(IEnumerable<double> values) => Estimate.Of(values)?.Within(0, 1);
            Estimate? ur(IEnumerable<PpPatternOutcome> half) => Estimate.Of(half.Select(o => o.UnstableRate ?? double.NaN))?.Within(0, double.MaxValue);
            trends.Add(new FolderPatternTrend(pattern, earlier.Length + recent.Length,
                share(earlier.Select(o => o.Accuracy)), share(recent.Select(o => o.Accuracy)),
                share(earlier.Select(o => o.MissRate)), share(recent.Select(o => o.MissRate)),
                ur(earlier), ur(recent)));
        }
        return trends.OrderBy(t => t.Pattern == "Overall" ? 0 : 1).ThenByDescending(t => t.Plays).ToArray();
    }

    private static IReadOnlyList<FolderMiss> misses(LocalReplay[] plays, IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses) =>
        plays.OrderByDescending(p => p.PlayedAt).Take(recent_plays_for_misses)
            .SelectMany(p => analyses[p.ScoreId].Judgements.Where(j => j.MaximumResult == "Great" && ReplayJudgementClassifier.IsMiss(j))
                .Select(j => new FolderMiss(p, j.StartTimeMs, j.MissAnalysis?.Reason)))
            .Take(recent_misses).ToArray();
}

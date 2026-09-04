using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.ScoreHistory;

namespace AimMod.Desktop.Coaching;

public enum StatisticsTimeRange
{
    All,
    Days30,
    Days90,
    Year,
}

public enum StatisticsModFilter
{
    Any,
    NoMod,
    Hidden,
    HardRock,
    DoubleTime,
    Other,
}

public enum StatisticsRunSort
{
    Recent,
    PerformancePoints,
    Accuracy,
    StarRating,
}

public enum StatisticsScoreSource
{
    All,
    Online,
    Local,
}

public sealed record StatisticsRunQuery(
    string SearchText = "",
    StatisticsTimeRange TimeRange = StatisticsTimeRange.All,
    StatisticsModFilter ModFilter = StatisticsModFilter.Any,
    StatisticsRunSort Sort = StatisticsRunSort.Recent,
    StatisticsScoreSource Source = StatisticsScoreSource.All,
    double MinimumStars = 0,
    double MaximumStars = 100,
    bool MissFreeOnly = false);

public sealed record StatisticsMapSummary(
    Guid BeatmapId,
    int PlayCount,
    double? AverageAccuracy,
    double? BestAccuracy,
    double? AccuracyChange,
    double? BestPerformancePoints,
    double MissFreeRate,
    int BestCombo,
    DateTimeOffset? FirstPlayedAt,
    DateTimeOffset? LastPlayedAt);

public sealed record StatisticsWorkspaceModel(
    IReadOnlyList<LocalReplay> Runs,
    int UnfilteredRunCount,
    int CachedOnlineRunCount,
    double? AverageAccuracy,
    double? BestAccuracy,
    double? AverageStarRating,
    double MissFreeRate,
    double? MedianPerformancePoints,
    int PerformancePointRunCount,
    int BestCombo,
    long TotalScore,
    IReadOnlyList<CoachingChartSeries> Series)
{
    public static StatisticsWorkspaceModel Build(
        IReadOnlyList<LocalReplay> source,
        StatisticsRunQuery query,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);

        DateTimeOffset reference = now ?? DateTimeOffset.Now;
        DateTimeOffset? earliest = query.TimeRange switch
        {
            StatisticsTimeRange.Days30 => reference.AddDays(-30),
            StatisticsTimeRange.Days90 => reference.AddDays(-90),
            StatisticsTimeRange.Year => reference.AddYears(-1),
            _ => null,
        };
        string search = query.SearchText.Trim();
        double minimumStars = Math.Max(0, Math.Min(query.MinimumStars, query.MaximumStars));
        double maximumStars = Math.Max(minimumStars, Math.Max(query.MinimumStars, query.MaximumStars));

        LocalReplay[] all = source.Where(run => string.Equals(run.RulesetShortName, "osu", StringComparison.OrdinalIgnoreCase))
                                  .GroupBy(run => run.ScoreId)
                                  .Select(group => group.OrderByDescending(run => run.PlayedAt).First())
                                  .ToArray();
        bool unrestrictedStars = minimumStars <= 0 && maximumStars >= 100;
        int onlineRunCount = all.Count(run => run.OnlineScoreId > 0);
        int localRunCount = all.Count(run => run.IsLocallyStored);
        IEnumerable<LocalReplay> filtered = all.Where(run => unrestrictedStars && !double.IsFinite(run.StarRating)
                                                              || run.StarRating >= minimumStars && run.StarRating <= maximumStars)
                                               .Where(run => earliest is null || run.PlayedAt >= earliest)
                                               .Where(run => !query.MissFreeOnly || run.MissCount == 0)
                                               .Where(run => query.Source switch
                                               {
                                                   StatisticsScoreSource.Online => run.OnlineScoreId > 0,
                                                   StatisticsScoreSource.Local => run.IsLocallyStored,
                                                   _ => true,
                                               })
                                               .Where(run => matchesMod(run.Mods, query.ModFilter));
        if (!string.IsNullOrWhiteSpace(search))
        {
            filtered = filtered.Where(run => contains(run.Title, search)
                                             || contains(run.Artist, search)
                                             || contains(run.Difficulty, search)
                                             || contains(run.Player, search)
                                             || run.Mods.Any(mod => contains(mod, search)));
        }

        LocalReplay[] runs = sort(filtered, query.Sort).ToArray();
        LocalReplay[] chronological = runs.OrderBy(run => run.PlayedAt).ToArray();
        double[] accuracies = chronological.Where(run => validAccuracy(run.Accuracy)).Select(run => run.Accuracy).ToArray();
        double[] performancePoints = chronological.Where(run => run.PerformancePoints is >= 0)
                                                  .Select(run => run.PerformancePoints!.Value)
                                                  .OrderBy(value => value)
                                                  .ToArray();

        return new StatisticsWorkspaceModel(
            runs,
            // Source memberships intentionally overlap: a submitted lazer score can
            // be both locally stored and present in the online score window.
            localRunCount + onlineRunCount,
            onlineRunCount,
            accuracies.Length == 0 ? null : accuracies.Average(),
            accuracies.Length == 0 ? null : accuracies.Max(),
            chronological.Where(run => double.IsFinite(run.StarRating) && run.StarRating >= 0).Select(run => run.StarRating).DefaultIfEmpty(double.NaN).Average() is var averageStars && double.IsFinite(averageStars) ? averageStars : null,
            chronological.Length == 0 ? 0 : chronological.Count(run => run.MissCount == 0) / (double)chronological.Length,
            median(performancePoints),
            performancePoints.Length,
            chronological.Length == 0 ? 0 : chronological.Max(run => Math.Max(0, run.MaxCombo)),
            chronological.Aggregate(0L, (sum, run) => saturatingAdd(sum, Math.Max(0, run.TotalScore))),
            new[]
            {
                series("statisticsAccuracy", "Accuracy", "percent", chronological.Where(run => validAccuracy(run.Accuracy)), run => run.Accuracy * 100),
                series("statisticsPp", "Performance", "pp", chronological.Where(run => run.PerformancePoints is >= 0), run => run.PerformancePoints!.Value),
                series("statisticsStars", "Difficulty", "stars", chronological.Where(run => double.IsFinite(run.StarRating) && run.StarRating >= 0), run => run.StarRating),
                series("statisticsMisses", "Misses", "count", chronological, run => Math.Max(0, run.MissCount)),
            });
    }

    public static StatisticsMapSummary BuildMapSummary(IReadOnlyList<LocalReplay> source, Guid beatmapId)
    {
        ArgumentNullException.ThrowIfNull(source);
        LocalReplay[] runs = source.Where(run => run.BeatmapId == beatmapId)
                                  .OrderBy(run => run.PlayedAt)
                                  .ToArray();
        double[] accuracies = runs.Where(run => validAccuracy(run.Accuracy)).Select(run => run.Accuracy).ToArray();
        double? change = accuracies.Length < 2 ? null : accuracies[^1] - accuracies[0];
        return new StatisticsMapSummary(
            beatmapId,
            runs.Length,
            accuracies.Length == 0 ? null : accuracies.Average(),
            accuracies.Length == 0 ? null : accuracies.Max(),
            change,
            runs.Where(run => run.PerformancePoints is >= 0).Select(run => run.PerformancePoints).Max(),
            runs.Length == 0 ? 0 : runs.Count(run => run.MissCount == 0) / (double)runs.Length,
            runs.Length == 0 ? 0 : runs.Max(run => Math.Max(0, run.MaxCombo)),
            runs.FirstOrDefault()?.PlayedAt,
            runs.LastOrDefault()?.PlayedAt);
    }

    private static CoachingChartSeries series(
        string key,
        string label,
        string unit,
        IEnumerable<LocalReplay> runs,
        Func<LocalReplay, double> value) =>
        new(key, label, unit, runs.Select(run => new CoachingChartPoint(run.ScoreId, run.PlayedAt, value(run))).ToArray());

    private static IEnumerable<LocalReplay> sort(IEnumerable<LocalReplay> runs, StatisticsRunSort mode) => mode switch
    {
        StatisticsRunSort.PerformancePoints => runs.OrderByDescending(run => run.PerformancePoints ?? double.MinValue).ThenByDescending(run => run.PlayedAt),
        StatisticsRunSort.Accuracy => runs.OrderByDescending(run => run.Accuracy).ThenByDescending(run => run.PlayedAt),
        StatisticsRunSort.StarRating => runs.OrderByDescending(run => run.StarRating).ThenByDescending(run => run.PlayedAt),
        _ => runs.OrderByDescending(run => run.PlayedAt),
    };

    private static bool matchesMod(IReadOnlyList<string> mods, StatisticsModFilter filter)
    {
        HashSet<string> values = mods.Select(mod => mod.Trim().ToUpperInvariant()).Where(mod => mod.Length > 0).ToHashSet();
        return filter switch
        {
            StatisticsModFilter.NoMod => values.Count == 0,
            StatisticsModFilter.Hidden => values.Contains("HD"),
            StatisticsModFilter.HardRock => values.Contains("HR"),
            StatisticsModFilter.DoubleTime => values.Contains("DT") || values.Contains("NC"),
            StatisticsModFilter.Other => values.Count > 0 && !values.Overlaps(new[] { "HD", "HR", "DT", "NC" }),
            _ => true,
        };
    }

    private static bool contains(string value, string search) => value.Contains(search, StringComparison.OrdinalIgnoreCase);
    private static bool validAccuracy(double value) => double.IsFinite(value) && value is >= 0 and <= 1;

    private static double? median(IReadOnlyList<double> values) => values.Count switch
    {
        0 => null,
        _ when values.Count % 2 == 1 => values[values.Count / 2],
        _ => (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2,
    };

    private static long saturatingAdd(long left, long right) => left > long.MaxValue - right ? long.MaxValue : left + right;
}

public static class StatisticsUnifiedScoreAdapter
{
    public static IReadOnlyList<LocalReplay> Merge(
        IReadOnlyList<LocalReplay> localScores,
        IReadOnlyList<ScoreHistoryEntry> onlineScores)
    {
        ArgumentNullException.ThrowIfNull(localScores);
        ArgumentNullException.ThrowIfNull(onlineScores);
        return ScoreHistoryMerger.MergeAsLocalReplays(localScores, onlineScores);
    }
}

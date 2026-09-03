using AimMod.Desktop.LocalLibrary;

namespace AimMod.Desktop.Coaching;

public sealed record StatisticsTimeAxis(string Start, string Middle, string End);

public sealed record StatisticsHistoryModel(
    int LoadedRunCount,
    int TotalAvailableRunCount,
    bool IsComplete,
    long AccumulatedScore,
    long RecentScore,
    int RollingWindowSize,
    double? RollingAccuracy,
    double? AccuracyChange,
    double? RollingAccuracySpread,
    double? AccuracySpreadChange,
    double? RollingMissFreeRate,
    double? MissFreeRateChange,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    StatisticsTimeAxis TimeAxis,
    IReadOnlyList<CoachingChartSeries> Series)
{
    public static StatisticsHistoryModel Build(IReadOnlyList<LocalReplay> runs, int? totalAvailableRunCount = null)
    {
        ArgumentNullException.ThrowIfNull(runs);

        LocalReplay[] history = runs.Where(run => string.Equals(run.RulesetShortName, "osu", StringComparison.OrdinalIgnoreCase))
                                    .GroupBy(run => run.ScoreId)
                                    .Select(group => group.OrderByDescending(run => run.PlayedAt).First())
                                    .OrderBy(run => run.PlayedAt)
                                    .ToArray();
        int available = Math.Max(history.Length, totalAvailableRunCount ?? history.Length);
        int rollingWindow = history.Length switch
        {
            0 => 0,
            <= 5 => history.Length,
            < 20 => 5,
            < 60 => 10,
            _ => 20,
        };
        RollingComparison accuracy = compareWindows(
            history.Where(run => validAccuracy(run.Accuracy)).Select(run => run.Accuracy).ToArray(),
            rollingWindow);
        RollingComparison misses = compareWindows(
            history.Select(run => run.MissCount == 0 ? 1d : 0d).ToArray(),
            rollingWindow);
        RollingSpreadComparison spread = compareSpreadWindows(
            history.Where(run => validAccuracy(run.Accuracy)).Select(run => run.Accuracy).ToArray(),
            rollingWindow);

        long accumulatedScore = 0;
        CoachingChartPoint[] scoreSeries = history.Select(run =>
                                                    {
                                                        accumulatedScore = saturatingAdd(accumulatedScore, Math.Max(0, run.TotalScore));
                                                        return point(run, accumulatedScore);
                                                    })
                                                    .ToArray();
        CoachingChartPoint[] runSeries = history.Select((run, index) => point(run, index + 1)).ToArray();
        CoachingChartPoint[] rollingAccuracy = rollingSeries(
            history.Where(run => validAccuracy(run.Accuracy)).ToArray(),
            rollingWindow,
            window => window.Average(run => run.Accuracy) * 100);
        CoachingChartPoint[] rollingSpread = rollingSeries(
            history.Where(run => validAccuracy(run.Accuracy)).ToArray(),
            rollingWindow,
            window => standardDeviation(window.Select(run => run.Accuracy)) * 100);
        CoachingChartPoint[] rollingMissFree = rollingSeries(
            history,
            rollingWindow,
            window => window.Count(run => run.MissCount == 0) * 100d / window.Count);

        StatisticsTimeAxis axis = buildTimeAxis(history);
        return new StatisticsHistoryModel(
            history.Length,
            available,
            history.Length >= available,
            accumulatedScore,
            history.TakeLast(rollingWindow).Aggregate(0L, (sum, run) => saturatingAdd(sum, Math.Max(0, run.TotalScore))),
            rollingWindow,
            accuracy.Current,
            accuracy.Change,
            spread.Current,
            spread.Change,
            misses.Current,
            misses.Change,
            history.FirstOrDefault()?.PlayedAt,
            history.LastOrDefault()?.PlayedAt,
            axis,
            new[]
            {
                new CoachingChartSeries("historyCumulativeScore", "Accumulated score", "score", scoreSeries),
                new CoachingChartSeries("historyCumulativeRuns", "Accumulated plays", "count", runSeries),
                new CoachingChartSeries("historyRollingAccuracy", $"Rolling {rollingWindow}-play accuracy", "percent", rollingAccuracy),
                new CoachingChartSeries("historyRollingAccuracySpread", $"Rolling {rollingWindow}-play accuracy spread", "percentage-points", rollingSpread),
                new CoachingChartSeries("historyRollingMissFree", $"Rolling {rollingWindow}-play miss-free rate", "percent", rollingMissFree),
            });
    }

    private static CoachingChartPoint[] rollingSeries(
        IReadOnlyList<LocalReplay> history,
        int windowSize,
        Func<IReadOnlyList<LocalReplay>, double> calculate)
    {
        var points = new CoachingChartPoint[history.Count];
        for (int index = 0; index < history.Count; index++)
        {
            int start = Math.Max(0, index - windowSize + 1);
            LocalReplay[] window = history.Skip(start).Take(index - start + 1).ToArray();
            points[index] = point(history[index], calculate(window));
        }

        return points;
    }

    private static RollingComparison compareWindows(IReadOnlyList<double> values, int windowSize)
    {
        if (values.Count == 0)
            return new RollingComparison(null, null);

        int currentCount = Math.Min(windowSize, values.Count);
        double current = values.TakeLast(currentCount).Average();
        if (values.Count < currentCount * 2)
            return new RollingComparison(current, null);

        double previous = values.Skip(values.Count - currentCount * 2).Take(currentCount).Average();
        return new RollingComparison(current, current - previous);
    }

    private static RollingSpreadComparison compareSpreadWindows(IReadOnlyList<double> values, int windowSize)
    {
        if (values.Count < 2)
            return new RollingSpreadComparison(null, null);

        int currentCount = Math.Min(windowSize, values.Count);
        double current = standardDeviation(values.TakeLast(currentCount));
        if (values.Count < currentCount * 2)
            return new RollingSpreadComparison(current, null);

        double previous = standardDeviation(values.Skip(values.Count - currentCount * 2).Take(currentCount));
        return new RollingSpreadComparison(current, current - previous);
    }

    private static StatisticsTimeAxis buildTimeAxis(IReadOnlyList<LocalReplay> history)
    {
        if (history.Count == 0)
            return new StatisticsTimeAxis(string.Empty, string.Empty, string.Empty);

        DateTimeOffset first = history[0].PlayedAt;
        DateTimeOffset middle = history[history.Count / 2].PlayedAt;
        DateTimeOffset last = history[^1].PlayedAt;
        string format = last - first <= TimeSpan.FromDays(2)
            ? "dd MMM HH:mm"
            : first.Year == last.Year
                ? "dd MMM"
                : "MMM yyyy";
        return new StatisticsTimeAxis(first.ToString(format), middle.ToString(format), last.ToString(format));
    }

    private static CoachingChartPoint point(LocalReplay run, double value) =>
        new(run.ScoreId, run.PlayedAt, value);

    private static bool validAccuracy(double value) => double.IsFinite(value) && value is >= 0 and <= 1;

    private static double standardDeviation(IEnumerable<double> values)
    {
        double[] samples = values.Where(double.IsFinite).ToArray();
        if (samples.Length == 0)
            return 0;
        double mean = samples.Average();
        return Math.Sqrt(samples.Average(value => (value - mean) * (value - mean)));
    }

    private static long saturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private sealed record RollingComparison(double? Current, double? Change);

    private sealed record RollingSpreadComparison(double? Current, double? Change);
}

public sealed record StatisticsHistoryLoadResult(
    IReadOnlyList<LocalReplay> Runs,
    int TotalAvailableRunCount,
    bool IsComplete);

public static class StatisticsHistoryLoader
{
    public const int PageSize = 200;
    public const int MaximumRuns = 10_000;

    public static async ValueTask<StatisticsHistoryLoadResult> LoadAsync(
        ILocalLibrarySource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var byScore = new Dictionary<Guid, LocalReplay>();
        int offset = 0;
        int totalAvailable = 0;
        while (offset < MaximumRuns)
        {
            LocalLibraryPage<LocalReplay> page = await source.SearchReplaysAsync(new LocalLibraryQuery(
                RulesetShortName: "osu",
                Sort: LocalLibrarySort.RecentlyPlayed,
                Offset: offset,
                Limit: Math.Min(PageSize, MaximumRuns - offset)), cancellationToken).ConfigureAwait(false);
            totalAvailable = Math.Max(totalAvailable, page.Total);
            foreach (LocalReplay replay in page.Items)
                byScore.TryAdd(replay.ScoreId, replay);

            if (page.Items.Count == 0 || !page.HasMore)
                break;

            offset += page.Items.Count;
        }

        LocalReplay[] runs = byScore.Values.OrderByDescending(run => run.PlayedAt).ToArray();
        return new StatisticsHistoryLoadResult(
            runs,
            totalAvailable,
            runs.Length >= totalAvailable);
    }
}

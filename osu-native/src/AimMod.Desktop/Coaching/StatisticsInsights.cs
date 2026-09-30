using System.Globalization;
using AimMod.Desktop.LocalLibrary;

namespace AimMod.Desktop.Coaching;

/// <summary>The four play measurements the statistics page can summarise and chart.</summary>
public enum StatisticsMetric
{
    Accuracy,
    Performance,
    Misses,
    Stars,
}

public sealed record StatisticsHistogramBin(double Start, double End, int Count, string Label);

/// <summary>Everything needed to draw one metric: headline, change, sparkline, trend and distribution.</summary>
public sealed record StatisticsMetricView(
    StatisticsMetric Metric,
    IReadOnlyList<CoachingChartPoint> Plays,
    IReadOnlyList<CoachingChartPoint> Rolling,
    int RollingWindow,
    IReadOnlyList<double> RollingBand,
    double? Headline,
    double? Change,
    string ComparisonLabel,
    IReadOnlyList<double> Sparkline,
    IReadOnlyList<StatisticsHistogramBin> Distribution,
    int MissingCount)
{
    public static StatisticsMetricView Empty(StatisticsMetric metric) =>
        new(metric, [], [], 0, [], null, null, string.Empty, [], [], 0);

    /// <summary>True when a higher value is an improvement. Difficulty has no better direction.</summary>
    public bool? HigherIsBetter => Metric switch
    {
        StatisticsMetric.Accuracy or StatisticsMetric.Performance => true,
        StatisticsMetric.Misses => false,
        _ => null,
    };
}

public sealed record StatisticsInsights(
    IReadOnlyList<StatisticsMetricView> Metrics,
    int LocalCount,
    int SubmittedCount,
    int OnlineOnlyCount,
    int MissingPpCount,
    double? MissFreeRate)
{
    public static StatisticsInsights Empty { get; } = new(
        Enum.GetValues<StatisticsMetric>().Select(StatisticsMetricView.Empty).ToArray(), 0, 0, 0, 0, null);

    public StatisticsMetricView For(StatisticsMetric metric) =>
        Metrics.FirstOrDefault(view => view.Metric == metric) ?? StatisticsMetricView.Empty(metric);
}

public static class StatisticsInsightsBuilder
{
    private const int sparkline_buckets = 12;

    /// <param name="current">Plays in the selected view.</param>
    /// <param name="comparisonCurrent">The recent window compared for the change indicator.</param>
    /// <param name="comparisonPrevious">The equal-length window before <paramref name="comparisonCurrent"/>.</param>
    /// <param name="comparisonLabel">Short text naming the comparison, such as "vs previous 30 days".</param>
    public static StatisticsInsights Build(
        IReadOnlyList<LocalReplay> current,
        IReadOnlyList<LocalReplay> comparisonCurrent,
        IReadOnlyList<LocalReplay> comparisonPrevious,
        string comparisonLabel)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(comparisonCurrent);
        ArgumentNullException.ThrowIfNull(comparisonPrevious);

        LocalReplay[] chronological = current.OrderBy(run => run.PlayedAt).ToArray();
        var metrics = Enum.GetValues<StatisticsMetric>().Select(metric =>
        {
            CoachingChartPoint[] plays = chronological.Where(run => Value(run, metric) is not null)
                                                      .Select(run => new CoachingChartPoint(run.ScoreId, run.PlayedAt, Value(run, metric)!.Value))
                                                      .ToArray();
            int window = RollingWindowFor(plays.Length);
            (CoachingChartPoint[] rolling, double[] band) = RollingMeanWithBand(plays, window);
            double? previous = Aggregate(comparisonPrevious, metric);
            double? recent = Aggregate(comparisonCurrent, metric);
            return new StatisticsMetricView(
                metric,
                plays,
                rolling,
                window,
                band,
                Aggregate(chronological, metric),
                previous is { } before && recent is { } after ? after - before : null,
                comparisonLabel,
                Sparkline(chronological, metric),
                Histogram(plays.Select(point => point.Value).ToArray(), metric),
                chronological.Length - plays.Length);
        }).ToArray();

        return new StatisticsInsights(
            metrics,
            chronological.Count(run => run.IsLocallyStored),
            chronological.Count(run => run.OnlineScoreId > 0),
            chronological.Count(run => !run.IsLocallyStored),
            chronological.Count(run => run.PerformancePoints is not >= 0),
            chronological.Length == 0 ? null : chronological.Count(run => run.MissCount == 0) * 100d / chronological.Length);
    }

    /// <summary>The metric value of one play in display units (accuracy in percent), or null when unknown.</summary>
    public static double? Value(LocalReplay run, StatisticsMetric metric) => metric switch
    {
        StatisticsMetric.Accuracy => double.IsFinite(run.Accuracy) && run.Accuracy is >= 0 and <= 1 ? run.Accuracy * 100 : null,
        StatisticsMetric.Performance => run.PerformancePoints is { } pp && pp >= 0 ? pp : null,
        StatisticsMetric.Misses => Math.Max(0, run.MissCount),
        StatisticsMetric.Stars => double.IsFinite(run.StarRating) && run.StarRating >= 0 ? run.StarRating : null,
        _ => null,
    };

    /// <summary>Median for PP, which is skewed by a few large scores; mean for everything else.</summary>
    public static double? Aggregate(IEnumerable<LocalReplay> runs, StatisticsMetric metric)
    {
        double[] values = runs.Select(run => Value(run, metric)).OfType<double>().ToArray();
        if (values.Length == 0)
            return null;
        if (metric != StatisticsMetric.Performance)
            return values.Average();
        Array.Sort(values);
        return values.Length % 2 == 1 ? values[values.Length / 2] : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2;
    }

    /// <summary>
    /// About 5% of the plays in view (at least 20), rounded to a friendly number, so the line shows the trend
    /// rather than session-to-session noise. Small views fall back to shorter windows.
    /// </summary>
    public static int RollingWindowFor(int count)
    {
        if (count < 15)
            return Math.Max(1, Math.Min(3, count));
        if (count < 60)
            return 5;
        if (count < 200)
            return 10;
        int target = Math.Max(20, (int)Math.Round(count * 0.05));
        int[] friendly = [20, 25, 30, 40, 50, 75, 100, 150, 200, 250, 300, 400, 500];
        return friendly.OrderBy(value => Math.Abs(value - target)).First();
    }

    /// <summary>Rolling mean plus the half-width of its 95% interval (1.96 standard errors).</summary>
    public static (CoachingChartPoint[] Mean, double[] Band) RollingMeanWithBand(IReadOnlyList<CoachingChartPoint> points, int window)
    {
        if (points.Count == 0 || window <= 0)
            return ([], []);
        var mean = new CoachingChartPoint[points.Count];
        var band = new double[points.Count];
        double sum = 0;
        double squares = 0;
        for (int index = 0; index < points.Count; index++)
        {
            double value = points[index].Value;
            sum += value;
            squares += value * value;
            if (index >= window)
            {
                double removed = points[index - window].Value;
                sum -= removed;
                squares -= removed * removed;
            }

            int size = Math.Min(window, index + 1);
            double average = sum / size;
            double variance = Math.Max(0, squares / size - average * average);
            mean[index] = points[index] with { Value = average };
            band[index] = size < 2 ? 0 : 1.96 * Math.Sqrt(variance / size);
        }

        return (mean, band);
    }

    public static CoachingChartPoint[] RollingMean(IReadOnlyList<CoachingChartPoint> points, int window)
    {
        if (points.Count == 0 || window <= 0)
            return [];
        var result = new CoachingChartPoint[points.Count];
        double sum = 0;
        for (int index = 0; index < points.Count; index++)
        {
            sum += points[index].Value;
            if (index >= window)
                sum -= points[index - window].Value;
            int size = Math.Min(window, index + 1);
            result[index] = points[index] with { Value = sum / size };
        }

        return result;
    }

    private static double[] Sparkline(IReadOnlyList<LocalReplay> chronological, StatisticsMetric metric)
    {
        if (chronological.Count < 2)
            return [];
        long start = chronological[0].PlayedAt.UtcTicks;
        long span = Math.Max(1, chronological[^1].PlayedAt.UtcTicks - start);
        return chronological.GroupBy(run => Math.Min(sparkline_buckets - 1, (int)((run.PlayedAt.UtcTicks - start) * sparkline_buckets / (double)span)))
                            .OrderBy(group => group.Key)
                            .Select(group => Aggregate(group, metric))
                            .OfType<double>()
                            .ToArray();
    }

    public static StatisticsHistogramBin[] Histogram(IReadOnlyList<double> values, StatisticsMetric metric)
    {
        if (values.Count == 0)
            return [];
        (double Start, double End, string Label)[] edges = metric switch
        {
            // One bin per miss count so every bar covers the same range; the last bin collects the long tail.
            StatisticsMetric.Misses => Enumerable.Range(0, Math.Clamp((int)values.Max() + 1, 2, 21))
                                                 .Select(count => count == 20
                                                     ? (20d, double.MaxValue, "20+")
                                                     : ((double)count, (double)count, count.ToString(CultureInfo.InvariantCulture)))
                                                 .ToArray(),
            _ => evenEdges(values, metric),
        };
        return edges.Select((edge, index) => new StatisticsHistogramBin(
            edge.Start,
            edge.End,
            values.Count(value => metric == StatisticsMetric.Misses
                ? value >= edge.Start && value <= edge.End
                : value >= edge.Start && (value < edge.End || index == edges.Length - 1 && value <= edge.End)),
            edge.Label)).ToArray();
    }

    private static (double, double, string)[] evenEdges(IReadOnlyList<double> values, StatisticsMetric metric)
    {
        double minimum = values.Min();
        double maximum = values.Max();
        double step = metric switch
        {
            StatisticsMetric.Accuracy => maximum - minimum > 16 ? 2 : 1,
            StatisticsMetric.Performance => maximum - minimum > 160 ? 20 : 10,
            _ => maximum - minimum > 3 ? 0.5 : 0.25,
        };
        double first = Math.Floor(minimum / step) * step;
        double last = metric == StatisticsMetric.Accuracy ? Math.Min(100, Math.Ceiling(maximum / step) * step) : Math.Ceiling(maximum / step) * step;
        if (last <= first)
            last = first + step;
        int count = Math.Clamp((int)Math.Round((last - first) / step), 1, 24);
        return Enumerable.Range(0, count).Select(index =>
        {
            double start = first + index * step;
            string label = metric switch
            {
                StatisticsMetric.Accuracy => start.ToString("0", CultureInfo.InvariantCulture),
                StatisticsMetric.Performance => start.ToString("0", CultureInfo.InvariantCulture),
                _ => start.ToString("0.##", CultureInfo.InvariantCulture),
            };
            return (start, start + step, label);
        }).ToArray();
    }
}

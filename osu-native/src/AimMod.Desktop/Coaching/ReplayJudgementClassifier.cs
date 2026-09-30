using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop.Coaching;

/// <summary>Shared judgement predicates and sample statistics for coaching, practice and replay summaries.</summary>
internal static class ReplayJudgementClassifier
{
    public static bool IsMiss(ReplayObjectJudgement judgement) =>
        string.Equals(judgement.Result, "Miss", StringComparison.OrdinalIgnoreCase);

    public static bool IsSliderBreak(ReplayObjectJudgement judgement) =>
        judgement.Result is "LargeTickMiss" or "SmallTickMiss" or "SliderTailMiss";

    public static bool IsTimingSample(ReplayObjectJudgement judgement) =>
        !IsMiss(judgement)
        && double.IsFinite(judgement.TimeOffsetMs)
        && string.Equals(judgement.MaximumResult, "Great", StringComparison.OrdinalIgnoreCase);

    public static bool IsTapTimingSample(ReplayObjectJudgement judgement) =>
        IsTimingSample(judgement)
        && judgement.ObjectType.EndsWith("Circle", StringComparison.OrdinalIgnoreCase);

    public static double Distance(ReplayPoint left, ReplayPoint right)
    {
        double x = left.X - right.X;
        double y = left.Y - right.Y;
        return Math.Sqrt(x * x + y * y);
    }

    public static T[] Concat<T>(IReadOnlyList<T[]> arrays)
    {
        int total = 0;
        foreach (T[] array in arrays)
            total += array.Length;

        var result = new T[total];
        int offset = 0;
        foreach (T[] array in arrays)
        {
            array.CopyTo(result, offset);
            offset += array.Length;
        }

        return result;
    }

    /// <summary>Median of finite values, or NaN when there are none.</summary>
    public static double Median(IEnumerable<double> values)
    {
        double[] ordered = sortedFinite(values);
        if (ordered.Length == 0)
            return double.NaN;
        return ordered.Length % 2 == 1
            ? ordered[ordered.Length / 2]
            : (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2;
    }

    /// <summary>Nearest-rank percentile of finite values, or NaN when there are none.</summary>
    public static double NearestRankPercentile(IEnumerable<double> values, double quantile)
    {
        double[] ordered = sortedFinite(values);
        if (ordered.Length == 0)
            return double.NaN;

        int nearestRank = Math.Clamp((int)Math.Ceiling(Math.Clamp(quantile, 0, 1) * ordered.Length) - 1, 0, ordered.Length - 1);
        return ordered[nearestRank];
    }

    /// <summary>Linearly interpolated percentile of finite values, or zero when there are none.</summary>
    public static double InterpolatedPercentile(IEnumerable<double> values, double quantile)
    {
        double[] ordered = sortedFinite(values);
        if (ordered.Length == 0)
            return 0;
        double index = Math.Clamp(quantile, 0, 1) * (ordered.Length - 1);
        int lower = (int)Math.Floor(index);
        int upper = (int)Math.Ceiling(index);
        return lower == upper ? ordered[lower] : ordered[lower] + (ordered[upper] - ordered[lower]) * (index - lower);
    }

    /// <summary>Population standard deviation of finite values, or zero when there are none.</summary>
    public static double StandardDeviation(IEnumerable<double> values)
    {
        double[] samples = values.Where(double.IsFinite).ToArray();
        if (samples.Length == 0)
            return 0;
        double mean = samples.Average();
        return Math.Sqrt(samples.Average(value => Math.Pow(value - mean, 2)));
    }

    private static double[] sortedFinite(IEnumerable<double> values)
    {
        double[] ordered = values.Where(double.IsFinite).ToArray();
        Array.Sort(ordered);
        return ordered;
    }
}

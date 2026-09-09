namespace AimMod.Desktop.Trainers;

public sealed record TrainerProgressComparison(double? FirstAccuracy, double? LatestAccuracy,
    double FirstMisses, double LatestMisses, double? FirstSpread, double? LatestSpread)
{
    public static TrainerProgressComparison? Build(TrainerResult current, IEnumerable<TrainerResult> history)
    {
        var runs = history.Append(current).Where(r => r.CompletedAt <= current.CompletedAt
                && r.Engine == current.Engine && r.Assisted == current.Assisted
                && r.Settings.ComparisonKey() == current.Settings.ComparisonKey())
            .DistinctBy(r => r.Id).OrderBy(r => r.CompletedAt).ToArray();
        if (runs.Length < 6) return null;
        var first = runs.Take(3).ToArray();
        var last = runs.TakeLast(3).ToArray();
        return new(mean(first, r => r.Accuracy), mean(last, r => r.Accuracy),
            first.Average(r => r.Misses), last.Average(r => r.Misses),
            mean(first, r => r.SpreadMs), mean(last, r => r.SpreadMs));
    }

    private static double? mean(TrainerResult[] runs, Func<TrainerResult, double?> value)
    {
        var values = runs.Select(value).ToArray();
        return values.All(v => v is { } n && double.IsFinite(n)) ? values.Average(v => v!.Value) : null;
    }
}

namespace AimMod.Desktop.Trainers;

/// <summary>Shared completed-run checks and personal accuracy references for practice.</summary>
public static class TrainerPerformance
{
    public static bool IsCompleted(TrainerResult r, DateTimeOffset now) => !r.Assisted && r.UsesOsuJudgements
        && r.Id != Guid.Empty && r.CompletedAt <= now && r.Notes >= 1
        && r.Hits >= 0 && r.Hits <= r.Notes && r.Misses >= 0 && r.Misses <= r.Notes
        && r.Accuracy is >= 0 and <= 100 && r.PlayedSeconds is {} seconds && double.IsFinite(seconds)
        && seconds >= r.Settings.Seconds * .8
        && (r.SpreadMs is null || double.IsFinite(r.SpreadMs.Value) && r.SpreadMs >= 0);

    public static double Reference(IEnumerable<TrainerResult> runs)
    {
        var scores = runs.Where(r => r.Accuracy is >= 80 and <= 100 && r.Misses <= r.Notes * .05)
            .Select(r => r.Accuracy!.Value).ToArray();
        return scores.Length >= 3 ? Median(scores) : 95;
    }

    public static bool Steady(TrainerResult r, double reference) => r.Accuracy >= Math.Max(80, reference - 2)
        && r.Misses <= r.Notes * .05 && r.Hits >= r.Notes * .95;
    public static bool Struggling(TrainerResult r, double reference) => r.Accuracy < Math.Max(80, reference - 4)
        || r.Misses > r.Notes * .1 || r.Hits < r.Notes * .9;
    public static double Median(IEnumerable<double> values)
    {
        var a = values.Order().ToArray();
        return (a[(a.Length - 1) / 2] + a[a.Length / 2]) / 2;
    }
}

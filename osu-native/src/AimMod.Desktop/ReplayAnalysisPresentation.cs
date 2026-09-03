using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop;

public sealed record ReplayAnalysisPresentation(string Summary, string NotableMoments, string NextPlay);

public static class ReplayAnalysisPresenter
{
    public static ReplayAnalysisPresentation Present(ReplayAnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        ReplayJudgementSummary summary = result.Summary;
        string distribution = $"{summary.Great:N0} great  /  {summary.Ok:N0} ok  /  {summary.Meh:N0} meh  /  {summary.Miss:N0} miss";
        if (summary.SliderBreaks > 0)
            distribution += $"  /  {summary.SliderBreaks:N0} slider break";

        ReplayObjectJudgement[] misses = result.Judgements
                                                     .Where(isObjectMiss)
                                                     .OrderBy(judgement => judgement.StartTimeMs)
                                                     .ToArray();

        string moments;
        string nextPlay;

        if (misses.Length > 0)
        {
            moments = "Misses at " + string.Join("  /  ", misses.Take(4).Select(formatMoment));
            if (misses.Length > 4)
                moments += $"  /  +{misses.Length - 4:N0} more";

            nextPlay = $"Next play: review the pattern around {formatTime(misses[0].StartTimeMs)}, then retry the same difficulty.";
        }
        else if (summary.SliderBreaks > 0)
        {
            ReplayObjectJudgement? firstBreak = result.Judgements
                                                        .Where(isSliderBreak)
                                                        .OrderBy(judgement => judgement.StartTimeMs)
                                                        .FirstOrDefault();
            moments = firstBreak is null
                ? $"{summary.SliderBreaks:N0} slider breaks recorded"
                : $"First slider break near {formatMoment(firstBreak)}";
            nextPlay = "Next play: keep the cursor centred on slider paths through their final ticks.";
        }
        else
        {
            moments = "No misses or slider breaks in this run";
            nextPlay = summary.Meh > 0
                ? "Next play: keep the same control while tightening hit timing on the lowest judgements."
                : "Next play: compare this clean run with nearby attempts for repeatability.";
        }

        return new ReplayAnalysisPresentation(distribution, moments, nextPlay);
    }

    internal static IReadOnlyList<ReplayObjectJudgement> SelectNotableJudgements(ReplayAnalysisResult result, int maximum = 5)
    {
        ReplayObjectJudgement[] objectMisses = result.Judgements
                                                      .Where(isObjectMiss)
                                                      .OrderBy(judgement => judgement.StartTimeMs)
                                                      .Take(maximum)
                                                      .ToArray();

        return objectMisses.Length > 0
            ? objectMisses
            : result.Judgements
                    .Where(isSliderBreak)
                    .OrderBy(judgement => judgement.StartTimeMs)
                    .Take(maximum)
                    .ToArray();
    }

    private static bool isObjectMiss(ReplayObjectJudgement judgement) =>
        string.Equals(judgement.Result, "Miss", StringComparison.OrdinalIgnoreCase);

    private static bool isSliderBreak(ReplayObjectJudgement judgement) =>
        judgement.Result is "LargeTickMiss" or "SmallTickMiss" or "SliderTailMiss";

    private static string formatMoment(ReplayObjectJudgement judgement)
    {
        string objectLabel = judgement.ObjectIndex is int index ? $"object {index + 1:N0}" : judgement.ObjectType;
        return $"{formatTime(judgement.StartTimeMs)} ({objectLabel})";
    }

    private static string formatTime(double milliseconds)
    {
        TimeSpan time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}"
            : $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds:000}";
    }
}

using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop.Coaching;

public enum CoachingTimeRange
{
    Days7,
    Days30,
    Days90,
    Year,
    All,
}

public sealed record CoachingSessionSummary(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int PlayCount,
    TimeSpan Duration,
    double? MedianAccuracy);

public sealed record GlobalCoachingSummary(
    int RunCount,
    int LocalRunCount,
    int SubmittedRunCount,
    int DistinctBeatmapCount,
    int ExactAnalysisRunCount,
    DateTimeOffset? FirstPlayAt,
    DateTimeOffset? LastPlayAt,
    double? MedianAccuracy);

public sealed record NativeCoachingWorkspaceModel(
    IReadOnlyList<LocalReplay> History,
    IReadOnlyList<LocalReplay> TrendRuns,
    IReadOnlyList<LocalReplay> SessionRuns,
    LocalReplay? SelectedRun,
    CoachingSessionSummary? Session,
    GlobalCoachingSummary Global,
    CoachingReport Report)
{
    public GlobalCoachingProfile GlobalProfile { get; init; } = GlobalCoachingProfile.Empty;

    public const int MaximumTrendRuns = 30;

    public static NativeCoachingWorkspaceModel Build(
        IReadOnlyList<LocalReplay> runs,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses,
        Guid? selectedScoreId = null,
        CoachingTimeRange timeRange = CoachingTimeRange.Days30,
        DateTimeOffset? now = null) =>
        new CoachingModelBuilder().Build(runs, analyses, selectedScoreId, timeRange, now);

    internal static DateTimeOffset? EarliestPlay(CoachingTimeRange timeRange, DateTimeOffset reference) => timeRange switch
    {
        CoachingTimeRange.Days7 => reference.AddDays(-7),
        CoachingTimeRange.Days30 => reference.AddDays(-30),
        CoachingTimeRange.Days90 => reference.AddDays(-90),
        CoachingTimeRange.Year => reference.AddYears(-1),
        _ => null,
    };

    /// <summary>Latest record per score of manual osu!standard plays within the range, newest first.</summary>
    internal static LocalReplay[] NormaliseHistory(
        IReadOnlyList<LocalReplay> runs,
        CoachingTimeRange timeRange,
        DateTimeOffset? now = null)
    {
        DateTimeOffset? earliest = EarliestPlay(timeRange, now ?? DateTimeOffset.Now);
        return runs.Where(run => string.Equals(run.RulesetShortName, "osu", StringComparison.OrdinalIgnoreCase))
                   .Where(CoachingRunKeys.IsManualPlay)
                   .GroupBy(run => run.ScoreId)
                   .Select(group => group.OrderByDescending(run => run.PlayedAt).First())
                   .Where(run => earliest is null || run.PlayedAt >= earliest)
                   .OrderByDescending(run => run.PlayedAt)
                   .Take(CoachingLimits.MaximumRuns)
                   .ToArray();
    }

    internal static LocalReplay[] TrendRunsOf(IReadOnlyList<LocalReplay> history) =>
        history.OrderBy(run => run.PlayedAt)
               .TakeLast(MaximumTrendRuns)
               .ToArray();

    internal static GlobalCoachingSummary SummariseGlobal(
        IReadOnlyList<LocalReplay> history,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses)
    {
        double[] accuracies = history.Select(run => run.Accuracy)
                                     .Where(value => double.IsFinite(value) && value is >= 0 and <= 1)
                                     .OrderBy(value => value)
                                     .ToArray();
        double? median = accuracies.Length switch
        {
            0 => null,
            var count when count % 2 == 1 => accuracies[count / 2],
            var count => (accuracies[count / 2 - 1] + accuracies[count / 2]) / 2,
        };

        return new GlobalCoachingSummary(
            history.Count,
            history.Count(run => run.IsLocallyStored),
            history.Count(run => run.OnlineScoreId > 0),
            history.Select(run => run.BeatmapId).Where(id => id != Guid.Empty).Distinct().Count(),
            history.Count(run => analyses.TryGetValue(run.ScoreId, out ReplayAnalysisResult? analysis)
                                 && analysis.Summary is not null
                                 && analysis.Judgements is not null),
            history.Count == 0 ? null : history.Min(run => run.PlayedAt),
            history.Count == 0 ? null : history.Max(run => run.PlayedAt),
            median);
    }

    /// <param name="chronological">History ordered by play time, oldest first.</param>
    internal static LocalReplay[] FindSession(IReadOnlyList<LocalReplay> chronological, Guid selectedScoreId)
    {
        var sessions = new List<List<LocalReplay>>();
        foreach (LocalReplay run in chronological)
        {
            if (sessions.Count == 0
                || run.PlayedAt - sessions[^1][^1].PlayedAt > TimeSpan.FromMinutes(CoachingLimits.SessionGapMinutes))
            {
                sessions.Add(new List<LocalReplay>());
            }

            sessions[^1].Add(run);
        }

        return sessions.FirstOrDefault(group => group.Any(run => run.ScoreId == selectedScoreId))?.ToArray()
               ?? Array.Empty<LocalReplay>();
    }

    internal static CoachingSessionSummary SummariseSession(IReadOnlyList<LocalReplay> runs)
    {
        LocalReplay[] chronological = runs.OrderBy(run => run.PlayedAt).ToArray();
        double[] accuracies = chronological.Select(run => run.Accuracy)
                                           .Where(value => double.IsFinite(value) && value is >= 0 and <= 1)
                                           .OrderBy(value => value)
                                           .ToArray();
        double? median = accuracies.Length switch
        {
            0 => null,
            var count when count % 2 == 1 => accuracies[count / 2],
            var count => (accuracies[count / 2 - 1] + accuracies[count / 2]) / 2,
        };

        return new CoachingSessionSummary(
            chronological[0].PlayedAt,
            chronological[^1].PlayedAt,
            chronological.Length,
            chronological[^1].PlayedAt - chronological[0].PlayedAt,
            median);
    }
}

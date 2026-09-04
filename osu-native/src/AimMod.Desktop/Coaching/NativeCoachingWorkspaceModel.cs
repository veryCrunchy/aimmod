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
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(analyses);

        DateTimeOffset reference = now ?? DateTimeOffset.Now;
        DateTimeOffset? earliest = timeRange switch
        {
            CoachingTimeRange.Days7 => reference.AddDays(-7),
            CoachingTimeRange.Days30 => reference.AddDays(-30),
            CoachingTimeRange.Days90 => reference.AddDays(-90),
            CoachingTimeRange.Year => reference.AddYears(-1),
            _ => null,
        };
        LocalReplay[] history = runs.Where(run => string.Equals(run.RulesetShortName, "osu", StringComparison.OrdinalIgnoreCase))
                                    .GroupBy(run => run.ScoreId)
                                    .Select(group => group.OrderByDescending(run => run.PlayedAt).First())
                                    .Where(run => earliest is null || run.PlayedAt >= earliest)
                                    .OrderByDescending(run => run.PlayedAt)
                                    .Take(CoachingLimits.MaximumRuns)
                                    .ToArray();
        LocalReplay? selected = selectedScoreId is { } scoreId
            ? history.FirstOrDefault(run => run.ScoreId == scoreId)
            : null;

        LocalReplay[] trendRuns = history.OrderBy(run => run.PlayedAt)
                                         .TakeLast(MaximumTrendRuns)
                                         .ToArray();
        LocalReplay[] sessionRuns = selected is null
            ? Array.Empty<LocalReplay>()
            : findSession(history, selected.ScoreId);
        CoachingSessionSummary? session = sessionRuns.Length == 0 ? null : summariseSession(sessionRuns);
        GlobalCoachingSummary global = summariseGlobal(history, analyses);
        CoachingReport report = selected is null
            ? CoachingReportBuilder.BuildGlobal(history, analyses)
            : CoachingReportBuilder.Build(history, analyses, selected.ScoreId);

        return new NativeCoachingWorkspaceModel(history, trendRuns, sessionRuns, selected, session, global, report)
        {
            GlobalProfile = GlobalCoachingProfileBuilder.Build(history, analyses),
        };
    }

    private static GlobalCoachingSummary summariseGlobal(
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

    private static LocalReplay[] findSession(IReadOnlyList<LocalReplay> history, Guid selectedScoreId)
    {
        LocalReplay[] chronological = history.OrderBy(run => run.PlayedAt).ToArray();
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

    private static CoachingSessionSummary summariseSession(IReadOnlyList<LocalReplay> runs)
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

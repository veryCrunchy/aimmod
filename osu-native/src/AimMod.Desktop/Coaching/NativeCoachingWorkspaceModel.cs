using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop.Coaching;

public sealed record CoachingSessionSummary(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int PlayCount,
    TimeSpan Duration,
    double? MedianAccuracy);

public sealed record NativeCoachingWorkspaceModel(
    IReadOnlyList<LocalReplay> History,
    IReadOnlyList<LocalReplay> TrendRuns,
    IReadOnlyList<LocalReplay> SessionRuns,
    LocalReplay? SelectedRun,
    CoachingSessionSummary? Session,
    CoachingReport Report)
{
    public const int MaximumTrendRuns = 30;

    public static NativeCoachingWorkspaceModel Build(
        IReadOnlyList<LocalReplay> runs,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses,
        Guid? selectedScoreId = null)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(analyses);

        LocalReplay[] history = runs.Where(run => string.Equals(run.RulesetShortName, "osu", StringComparison.OrdinalIgnoreCase))
                                    .OrderByDescending(run => run.PlayedAt)
                                    .Take(CoachingLimits.MaximumRuns)
                                    .ToArray();
        LocalReplay? selected = selectedScoreId is { } scoreId
            ? history.FirstOrDefault(run => run.ScoreId == scoreId)
            : history.FirstOrDefault();
        selected ??= history.FirstOrDefault();

        LocalReplay[] trendRuns = history.OrderBy(run => run.PlayedAt)
                                         .TakeLast(MaximumTrendRuns)
                                         .ToArray();
        LocalReplay[] sessionRuns = selected is null
            ? Array.Empty<LocalReplay>()
            : findSession(history, selected.ScoreId);
        CoachingSessionSummary? session = sessionRuns.Length == 0 ? null : summariseSession(sessionRuns);
        CoachingReport report = CoachingReportBuilder.Build(history, analyses, selected?.ScoreId);

        return new NativeCoachingWorkspaceModel(history, trendRuns, sessionRuns, selected, session, report);
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

using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop.Practice;

public sealed record PracticeMapCandidate(
    LocalReplay SourceReplay,
    IReadOnlyList<Guid> AnalysisScoreIds,
    int AnalysedAttempts,
    int MissCount,
    double WeaknessScore)
{
    public string Evidence => $"{MissCount:N0} exact {(MissCount == 1 ? "miss" : "misses")} across {AnalysedAttempts:N0} analysed {(AnalysedAttempts == 1 ? "attempt" : "attempts")}";
}

public sealed record PracticeMapGenerationRequest(PracticeMapCandidate Candidate, PracticeDrillType DrillType);

public sealed record PracticeMapGenerationResult(bool Success, string Message, string? DirectoryPath = null);

public static class PracticeMapCandidateBuilder
{
    public static IReadOnlyList<PracticeMapCandidate> Build(
        IEnumerable<LocalReplay> replays,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses,
        int limit = 5)
    {
        ArgumentNullException.ThrowIfNull(replays);
        ArgumentNullException.ThrowIfNull(analyses);
        if (limit <= 0)
            return Array.Empty<PracticeMapCandidate>();

        return replays.Where(run => run.IsLocallyStored && run.HasReplayFile && validMap(run))
                      .GroupBy(mapKey, StringComparer.Ordinal)
                      .Select(group => build(group, analyses))
                      .Where(candidate => candidate is not null)
                      .Cast<PracticeMapCandidate>()
                      .OrderByDescending(candidate => candidate.WeaknessScore)
                      .ThenByDescending(candidate => candidate.MissCount)
                      .ThenByDescending(candidate => candidate.SourceReplay.PlayedAt)
                      .Take(limit)
                      .ToArray();
    }

    private static PracticeMapCandidate? build(
        IGrouping<string, LocalReplay> group,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses)
    {
        var evidence = group.Select(run => (Run: run, Analysis: analyses.GetValueOrDefault(run.ScoreId)))
                            .Where(item => item.Analysis?.Judgements is not null)
                            .ToArray();
        ReplayObjectJudgement[] misses = evidence.SelectMany(item => item.Analysis!.Judgements)
                                                  .Where(item => string.Equals(item.Result, "Miss", StringComparison.OrdinalIgnoreCase)
                                                                 && item.ObjectIndex is >= 0)
                                                  .ToArray();
        if (misses.Length == 0)
            return null;

        double score = misses.Sum(item => 1 + Math.Clamp(item.MissAnalysis?.Confidence ?? 0.25, 0, 1));
        LocalReplay source = evidence.OrderByDescending(item => item.Run.PlayedAt).First().Run;
        return new PracticeMapCandidate(source, evidence.Select(item => item.Run.ScoreId).ToArray(),
            evidence.Length, misses.Length, score);
    }

    private static bool validMap(LocalReplay run) => run.BeatmapId != Guid.Empty || !string.IsNullOrWhiteSpace(run.BeatmapHash);

    private static string mapKey(LocalReplay run) => run.BeatmapId != Guid.Empty
        ? $"id:{run.BeatmapId:N}"
        : $"hash:{run.BeatmapHash.Trim().ToLowerInvariant()}";
}

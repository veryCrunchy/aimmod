using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop.Practice;

public sealed record PracticeMapCandidate(
    LocalReplay SourceReplay,
    IReadOnlyList<Guid> AnalysisScoreIds,
    int AnalysedAttempts,
    int MissCount,
    double WeaknessScore,
    int AttemptsWithMisses = 0,
    double AverageMissConfidence = 0)
{
    public string Evidence => $"{MissCount:N0} exact {(MissCount == 1 ? "miss" : "misses")} across {AnalysedAttempts:N0} analysed {(AnalysedAttempts == 1 ? "attempt" : "attempts")}";
}

public sealed record PracticeMapGenerationRequest(PracticeMapCandidate Candidate, PracticeDrillType DrillType);

public sealed record PracticeMapGenerationResult(
    bool Success,
    string Message,
    string? DirectoryPath = null,
    string? ArchivePath = null,
    LazerBeatmapArchive? LazerArchive = null);

public enum PracticeCandidateSort
{
    WeakestFirst,
    MostRepeated,
    MostExactMisses,
    RecentlyPlayed,
    HardestFirst,
    EasiestFirst,
    Title,
}

public enum PracticeEvidenceFilter
{
    AnyEvidence,
    RepeatedAcrossAttempts,
    HighConfidence,
    ThreePlusMisses,
    FivePlusMisses,
}

public sealed record PracticeCandidateQuery(
    string SearchText = "",
    PracticeCandidateSort Sort = PracticeCandidateSort.WeakestFirst,
    PracticeEvidenceFilter Evidence = PracticeEvidenceFilter.AnyEvidence,
    double MinimumStars = 0,
    double MaximumStars = 10)
{
    public PracticeCandidateQuery Normalised()
    {
        double minimum = Math.Clamp(Math.Min(MinimumStars, MaximumStars), 0, 10);
        double maximum = Math.Clamp(Math.Max(MinimumStars, MaximumStars), 0, 10);
        return this with { SearchText = SearchText.Trim(), MinimumStars = minimum, MaximumStars = maximum };
    }
}

public sealed record PracticeCandidatePage(
    IReadOnlyList<PracticeMapCandidate> Items,
    int Total,
    int Available);

public static class PracticeMapCandidateSearch
{
    public static PracticeCandidatePage Search(
        IEnumerable<PracticeMapCandidate> candidates,
        PracticeCandidateQuery query,
        int limit = 100)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(query);
        PracticeMapCandidate[] available = candidates.ToArray();
        PracticeCandidateQuery safe = query.Normalised();
        IEnumerable<PracticeMapCandidate> filtered = available.Where(candidate => isMatch(candidate, safe));
        IOrderedEnumerable<PracticeMapCandidate> ordered = safe.Sort switch
        {
            PracticeCandidateSort.MostRepeated => filtered.OrderByDescending(candidate => candidate.AttemptsWithMisses)
                                                         .ThenByDescending(candidate => candidate.AnalysedAttempts)
                                                         .ThenByDescending(candidate => candidate.WeaknessScore),
            PracticeCandidateSort.MostExactMisses => filtered.OrderByDescending(candidate => candidate.MissCount)
                                                          .ThenByDescending(candidate => candidate.WeaknessScore),
            PracticeCandidateSort.RecentlyPlayed => filtered.OrderByDescending(candidate => candidate.SourceReplay.PlayedAt)
                                                             .ThenByDescending(candidate => candidate.WeaknessScore),
            PracticeCandidateSort.HardestFirst => filtered.OrderByDescending(candidate => candidate.SourceReplay.StarRating)
                                                          .ThenByDescending(candidate => candidate.WeaknessScore),
            PracticeCandidateSort.EasiestFirst => filtered.OrderBy(candidate => candidate.SourceReplay.StarRating)
                                                          .ThenByDescending(candidate => candidate.WeaknessScore),
            PracticeCandidateSort.Title => filtered.OrderBy(candidate => candidate.SourceReplay.Title, StringComparer.OrdinalIgnoreCase)
                                                     .ThenByDescending(candidate => candidate.WeaknessScore),
            _ => filtered.OrderByDescending(candidate => candidate.WeaknessScore)
                         .ThenByDescending(candidate => candidate.MissCount),
        };
        PracticeMapCandidate[] matches = ordered.ThenBy(candidate => candidate.SourceReplay.Title, StringComparer.OrdinalIgnoreCase)
                                                .ToArray();
        return new PracticeCandidatePage(matches.Take(Math.Clamp(limit, 0, 100)).ToArray(), matches.Length, available.Length);
    }

    private static bool isMatch(PracticeMapCandidate candidate, PracticeCandidateQuery query)
    {
        LocalReplay replay = candidate.SourceReplay;
        if (replay.StarRating < query.MinimumStars || replay.StarRating > query.MaximumStars)
            return false;
        if (query.Evidence switch
            {
                PracticeEvidenceFilter.RepeatedAcrossAttempts => candidate.AttemptsWithMisses < 2,
                PracticeEvidenceFilter.HighConfidence => candidate.AverageMissConfidence < 0.7,
                PracticeEvidenceFilter.ThreePlusMisses => candidate.MissCount < 3,
                PracticeEvidenceFilter.FivePlusMisses => candidate.MissCount < 5,
                _ => false,
            })
            return false;
        if (query.SearchText.Length == 0)
            return true;
        return replay.Title.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase)
               || replay.Artist.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase)
               || replay.Difficulty.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase)
               || replay.Player.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase)
               || replay.Mods.Any(mod => mod.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase));
    }
}

public static class PracticeMapCandidateBuilder
{
    public static IReadOnlyList<PracticeMapCandidate> Build(
        IEnumerable<LocalReplay> replays,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses,
        int limit = 100)
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
        int attemptsWithMisses = evidence.Count(item => item.Analysis!.Judgements.Any(judgement =>
            string.Equals(judgement.Result, "Miss", StringComparison.OrdinalIgnoreCase) && judgement.ObjectIndex is >= 0));
        double averageMissConfidence = misses.Average(item => Math.Clamp(item.MissAnalysis?.Confidence ?? 0.25, 0, 1));
        LocalReplay source = evidence.OrderByDescending(item => item.Run.PlayedAt).First().Run;
        return new PracticeMapCandidate(source, evidence.Select(item => item.Run.ScoreId).ToArray(),
            evidence.Length, misses.Length, score, attemptsWithMisses, averageMissConfidence);
    }

    private static bool validMap(LocalReplay run) => run.BeatmapId != Guid.Empty || !string.IsNullOrWhiteSpace(run.BeatmapHash);

    private static string mapKey(LocalReplay run) => run.BeatmapId != Guid.Empty
        ? $"id:{run.BeatmapId:N}"
        : $"hash:{run.BeatmapHash.Trim().ToLowerInvariant()}";
}

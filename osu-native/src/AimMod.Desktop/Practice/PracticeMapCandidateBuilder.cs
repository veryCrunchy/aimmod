using AimMod.Desktop.Coaching;
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
    public string Evidence => MissCount == 0
        ? $"Timing practice / {AnalysedAttempts:N0} analysed {(AnalysedAttempts == 1 ? "attempt" : "attempts")}"
        : $"{MissCount:N0} exact {(MissCount == 1 ? "miss" : "misses")} across {AnalysedAttempts:N0} analysed {(AnalysedAttempts == 1 ? "attempt" : "attempts")}";
}

/// <summary>Why a played difficulty has no drills. Its coaching page still offers adaptive practice.</summary>
public enum PlayedWithoutDrillsReason { NoReplayFile, NotAnalysed, AnalysisFailed, NoProblemsFound }

/// <summary>A difficulty played in the coaching timeframe that the drills list cannot offer sections for yet.</summary>
/// <param name="Play">The play its coaching page opens from: the newest passed play, otherwise the newest play.</param>
public sealed record PlayedDifficultyWithoutDrills(LocalReplay Play, int Plays, PlayedWithoutDrillsReason Reason)
{
    public string ReasonLabel => Reason switch
    {
        PlayedWithoutDrillsReason.NoReplayFile => "no replay saved",
        PlayedWithoutDrillsReason.NotAnalysed => "not analysed yet",
        PlayedWithoutDrillsReason.AnalysisFailed => "analysis failed",
        _ => "no misses or timing problems found",
    };

    public string Explanation => Reason switch
    {
        PlayedWithoutDrillsReason.NoReplayFile => "Drills are built from replays, and osu! did not save a replay for these plays. Its coaching page still runs adaptive practice on the map.",
        PlayedWithoutDrillsReason.NotAnalysed => "Its replays have not been analysed yet. Opening its coaching page analyses them now; drills appear here once they show misses or timing problems.",
        PlayedWithoutDrillsReason.AnalysisFailed => "AimMod could not analyse its replays, usually because the map changed after they were set or its file is missing. Its coaching page still runs adaptive practice.",
        _ => "Its analysed replays have no misses or timing problems to drill. Its coaching page can still practise the whole map or a chosen part.",
    };
}

public sealed record PracticeMapGenerationRequest(
    PracticeMapCandidate Candidate,
    PracticeDrillType DrillType,
    PracticeMapOptions? Options = null,
    IProgress<string>? Progress = null, bool CreateSet = false, IReadOnlyList<LocalReplay>? SourceHistory = null, bool Automatic = false,
    bool CreateBreakdown = false);

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

    /// <summary>The star range and search text, without the evidence filter, for plays that are not drills.</summary>
    public static bool MatchesPlay(LocalReplay replay, PracticeCandidateQuery query)
    {
        PracticeCandidateQuery safe = query.Normalised();
        return isMatch(new PracticeMapCandidate(replay, [], 0, 0, 0), safe with { Evidence = PracticeEvidenceFilter.AnyEvidence });
    }

    private static bool isMatch(PracticeMapCandidate candidate, PracticeCandidateQuery query)
    {
        LocalReplay replay = candidate.SourceReplay;
        // The slider's ends mean "any": 0 includes unrated maps and 10 is shown as 10+.
        bool lowerBound = query.MinimumStars > 0;
        bool upperBound = query.MaximumStars < 10;
        if (!double.IsFinite(replay.StarRating))
        {
            if (lowerBound || upperBound)
                return false;
        }
        else if (lowerBound && replay.StarRating < query.MinimumStars || upperBound && replay.StarRating > query.MaximumStars)
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
        ReplayObjectJudgement[][] missesByAttempt = evidence.Select(item => ReplayJudgementDigest.For(item.Analysis!).Misses
                                                                 .Where(judgement => judgement.ObjectIndex is >= 0)
                                                                 .ToArray())
                                                        .ToArray();
        ReplayObjectJudgement[] misses = ReplayJudgementClassifier.Concat(missesByAttempt);
        int timingPhrases = evidence.Count(item => TappingCoaching.Build(item.Analysis) is not null);
        if (misses.Length == 0 && timingPhrases == 0)
            return null;

        double score = misses.Sum(item => 1 + Math.Clamp(item.MissAnalysis?.Confidence ?? 0.25, 0, 1)) + timingPhrases;
        int attemptsWithMisses = missesByAttempt.Count(attempt => attempt.Length > 0);
        double averageMissConfidence = misses.Length == 0 ? 0 : misses.Average(item => Math.Clamp(item.MissAnalysis?.Confidence ?? 0.25, 0, 1));
        LocalReplay source = evidence.OrderByDescending(item => item.Run.PlayedAt).First().Run;
        return new PracticeMapCandidate(source, evidence.Select(item => item.Run.ScoreId).ToArray(),
            evidence.Length, misses.Length, score, attemptsWithMisses, averageMissConfidence);
    }

    /// <summary>
    /// Locally played difficulties in the history that the drills list has no entry for, with the reason. Grouped the
    /// same way as drills, one entry per difficulty, newest first.
    /// </summary>
    public static IReadOnlyList<PlayedDifficultyWithoutDrills> PlayedWithoutDrills(
        IEnumerable<LocalReplay> history,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses,
        IEnumerable<PracticeMapCandidate> drills,
        Func<Guid, bool>? analysisFailed = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(analyses);
        var drilled = drills.Select(candidate => mapKey(candidate.SourceReplay)).ToHashSet(StringComparer.Ordinal);
        return history.Where(run => run.IsLocallyStored && validMap(run) && string.Equals(run.RulesetShortName, "osu", StringComparison.OrdinalIgnoreCase))
            .GroupBy(mapKey, StringComparer.Ordinal)
            .Where(group => !drilled.Contains(group.Key))
            .Select(group =>
            {
                var plays = group.OrderByDescending(run => run.PlayedAt).ToArray();
                var withReplay = plays.Where(run => run.HasReplayFile).ToArray();
                PlayedWithoutDrillsReason reason = withReplay.Length == 0 ? PlayedWithoutDrillsReason.NoReplayFile
                    : withReplay.Any(run => analyses.GetValueOrDefault(run.ScoreId)?.Judgements is not null) ? PlayedWithoutDrillsReason.NoProblemsFound
                    : analysisFailed is not null && withReplay.All(run => analysisFailed(run.ScoreId)) ? PlayedWithoutDrillsReason.AnalysisFailed
                    : PlayedWithoutDrillsReason.NotAnalysed;
                return new PlayedDifficultyWithoutDrills(plays.FirstOrDefault(run => run.Passed) ?? plays[0], plays.Length, reason);
            })
            .OrderByDescending(item => item.Play.PlayedAt)
            .ToArray();
    }

    private static bool validMap(LocalReplay run) => run.BeatmapId != Guid.Empty || !string.IsNullOrWhiteSpace(run.BeatmapHash);

    private static string mapKey(LocalReplay run) => run.BeatmapId != Guid.Empty
        ? $"id:{run.BeatmapId:N}"
        : $"hash:{run.BeatmapHash.Trim().ToLowerInvariant()}";
}

using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop;

public sealed record ReplayRecurringMiss(
    int ObjectIndex,
    double StartTimeMs,
    int MissedAttempts,
    int AnalysedAttempts,
    ReplayMissReason? DominantReason)
{
    public double RecurrenceRate => AnalysedAttempts == 0 ? 0 : MissedAttempts / (double)AnalysedAttempts;
}

public sealed record ReplayMapPatternReport(
    int TotalAttempts,
    int AnalysedAttempts,
    IReadOnlyList<ReplayRecurringMiss> RecurringMisses,
    IReadOnlyDictionary<ReplayMissReason, int> MissReasons);

public static class ReplayMissInsightPresenter
{
    public static string Describe(ReplayObjectJudgement judgement)
    {
        ReplayMissAnalysis? evidence = judgement.MissAnalysis;
        if (evidence is null)
            return judgement.Result;

        string qualifier = evidence.Confidence >= 0.8 ? "Likely" : "Possible";
        string press = evidence.PressTimeOffsetMs is { } offset ? $"{Math.Abs(offset):0} ms" : "No click";
        string distance = $"{Math.Max(0, evidence.DistanceAtPress ?? evidence.DistanceAtObjectTime):0} px";
        return evidence.Reason switch
        {
            ReplayMissReason.EarlyClick => $"{qualifier} early click · {press} before target · {distance} away",
            ReplayMissReason.LateClick => $"{qualifier} late click · left target, then clicked {press} late",
            ReplayMissReason.Undershoot => $"{qualifier} undershoot · still approaching · {distance} from centre",
            ReplayMissReason.Overshoot => $"{qualifier} overshoot · already moving away · {distance} from centre",
            ReplayMissReason.OnTargetNoClick => "Cursor reached the target, but no new click registered",
            ReplayMissReason.AimDeviation => $"Aim deviation · closest approach {evidence.ClosestDistance:0} px",
            _ => $"Unclassified miss · closest approach {evidence.ClosestDistance:0} px",
        };
    }

    public static string Label(ReplayMissReason reason) => reason switch
    {
        ReplayMissReason.EarlyClick => "early clicks",
        ReplayMissReason.LateClick => "late clicks",
        ReplayMissReason.Undershoot => "undershoots",
        ReplayMissReason.Overshoot => "overshoots",
        ReplayMissReason.OnTargetNoClick => "on-target misses without a click",
        ReplayMissReason.AimDeviation => "aim deviations",
        _ => "unclassified misses",
    };
}

public static class ReplayMapPatternAnalyzer
{
    public static ReplayMapPatternReport Build(
        LocalReplay selected,
        IEnumerable<LocalReplay> history,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(analyses);

        LocalReplay[] attempts = history.Where(run => IsSameDifficultyAndSetup(selected, run))
                                        .DistinctBy(run => run.ScoreId)
                                        .OrderByDescending(run => run.PlayedAt)
                                        .ToArray();
        ReplayAnalysisResult[] exact = attempts.Select(run => analyses.GetValueOrDefault(run.ScoreId))
                                               .Where(result => result?.Judgements is not null)
                                               .Cast<ReplayAnalysisResult>()
                                               .ToArray();

        ReplayRecurringMiss[] recurring = exact.SelectMany(result => result.Judgements.Where(isTopLevelMiss))
                                                .Where(judgement => judgement.ObjectIndex is not null)
                                                .GroupBy(judgement => judgement.ObjectIndex!.Value)
                                                .Select(group => new ReplayRecurringMiss(
                                                    group.Key,
                                                    group.Select(judgement => judgement.StartTimeMs).Where(double.IsFinite).DefaultIfEmpty(0).Average(),
                                                    group.Count(),
                                                    exact.Length,
                                                    dominantReason(group)))
                                                .Where(pattern => pattern.AnalysedAttempts >= 3 && pattern.MissedAttempts >= 2)
                                                .OrderByDescending(pattern => pattern.MissedAttempts)
                                                .ThenBy(pattern => pattern.ObjectIndex)
                                                .Take(8)
                                                .ToArray();
        IReadOnlyDictionary<ReplayMissReason, int> reasons = exact.SelectMany(result => result.Judgements)
                                                                  .Where(isTopLevelMiss)
                                                                  .Select(judgement => judgement.MissAnalysis?.Reason ?? ReplayMissReason.Unknown)
                                                                  .GroupBy(reason => reason)
                                                                  .ToDictionary(group => group.Key, group => group.Count());

        return new ReplayMapPatternReport(attempts.Length, exact.Length, recurring, reasons);
    }

    private static ReplayMissReason? dominantReason(IEnumerable<ReplayObjectJudgement> judgements) =>
        judgements.Where(judgement => judgement.MissAnalysis is not null)
                  .GroupBy(judgement => judgement.MissAnalysis!.Reason)
                  .OrderByDescending(group => group.Count())
                  .ThenBy(group => group.Key)
                  .Select(group => (ReplayMissReason?)group.Key)
                  .FirstOrDefault();

    internal static bool IsSameDifficultyAndSetup(LocalReplay selected, LocalReplay candidate)
    {
        if (!string.IsNullOrWhiteSpace(selected.BeatmapHash) && !string.IsNullOrWhiteSpace(candidate.BeatmapHash))
        {
            if (!string.Equals(selected.BeatmapHash, candidate.BeatmapHash, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        else if (selected.BeatmapId != Guid.Empty && candidate.BeatmapId != Guid.Empty)
        {
            if (selected.BeatmapId != candidate.BeatmapId)
                return false;
        }
        else if (selected.SetId != candidate.SetId
                 || !string.Equals(selected.Difficulty, candidate.Difficulty, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(canonicalMods(selected), canonicalMods(candidate), StringComparison.Ordinal);
    }

    private static string canonicalMods(LocalReplay replay) => !string.IsNullOrWhiteSpace(replay.ModsJson)
        ? replay.ModsJson.Trim()
        : string.Join(',', replay.Mods.OrderBy(mod => mod, StringComparer.OrdinalIgnoreCase).Select(mod => mod.ToUpperInvariant()));

    private static bool isTopLevelMiss(ReplayObjectJudgement judgement) =>
        string.Equals(judgement.Result, "Miss", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(judgement.NestedPath);
}

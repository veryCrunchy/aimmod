using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop.Coaching;

public sealed record GlobalCoachingCoverage(
    int HistoryRunCount,
    int ReplayAvailableRunCount,
    int AnalysedRunCount,
    int DistinctMapCount,
    int AnalysedMapCount,
    int JudgementCount,
    CoachingConfidence Confidence,
    int MissCount = 0,
    int ClassifiedMissCount = 0)
{
    public double ReplayCoverage => ReplayAvailableRunCount == 0
        ? 0
        : Math.Clamp((double)AnalysedRunCount / ReplayAvailableRunCount, 0, 1);

    public double MissClassificationCoverage => MissCount == 0
        ? 0
        : Math.Clamp((double)ClassifiedMissCount / MissCount, 0, 1);
}

public sealed record GlobalMissReasonShare(
    ReplayMissReason Reason,
    int Count,
    double Share,
    int RunCount,
    int MapCount,
    double AverageClassifierConfidence = 0,
    CoachingConfidence Confidence = CoachingConfidence.Insufficient);

public enum CoachingSkillArea
{
    AimControl,
    AimPrecision,
    TapTiming,
    AimTapCoordination,
}

public sealed record GlobalSkillAreaEvidence(
    CoachingSkillArea Area,
    string Label,
    int EvidenceCount,
    int RunCount,
    int MapCount,
    double ShareOfClassifiedMisses,
    double AnalysedMapCoverage,
    CoachingConfidence Confidence,
    string Detail);

public sealed record GlobalRecurringWeakness(
    string Key,
    string Label,
    string Detail,
    int EvidenceCount,
    int RunCount,
    int MapCount,
    CoachingConfidence Confidence = CoachingConfidence.Insufficient);

public sealed record GlobalCoachingPriority(
    string Title,
    string Detail,
    string Value,
    CoachingConfidence Confidence);

public sealed record GlobalCoachingProfile(
    GlobalCoachingCoverage Coverage,
    IReadOnlyList<GlobalMissReasonShare> MissReasons,
    string TimingTendency,
    string TimingDetail,
    string AimTendency,
    string AimDetail,
    IReadOnlyList<GlobalRecurringWeakness> RecurringWeaknesses,
    IReadOnlyList<GlobalCoachingPriority> Priorities,
    IReadOnlyList<GlobalSkillAreaEvidence>? SkillAreas = null)
{
    public IReadOnlyList<GlobalSkillAreaEvidence> MeasuredSkillAreas => SkillAreas ?? Array.Empty<GlobalSkillAreaEvidence>();

    public static GlobalCoachingProfile Empty { get; } = new(
        new GlobalCoachingCoverage(0, 0, 0, 0, 0, 0, CoachingConfidence.Insufficient),
        Array.Empty<GlobalMissReasonShare>(),
        "Not measured",
        "Exact replay timing has not been measured yet.",
        "Not measured",
        "Cursor placement has not been measured yet.",
        Array.Empty<GlobalRecurringWeakness>(),
        Array.Empty<GlobalCoachingPriority>(),
        Array.Empty<GlobalSkillAreaEvidence>());
}

public static class GlobalCoachingProfileBuilder
{
    private const double centred_timing_ms = CoachingLimits.CentredTimingThresholdMilliseconds;

    public static GlobalCoachingProfile Build(
        IReadOnlyList<LocalReplay> runs,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(analyses);

        LocalReplay[] history = runs.Where(run => string.Equals(run.RulesetShortName, "osu", StringComparison.OrdinalIgnoreCase))
                                    .ToArray();
        if (history.Length == 0)
            return GlobalCoachingProfile.Empty;

        AnalysedRun[] exact = history.Select(run => new AnalysedRun(run, analyses.GetValueOrDefault(run.ScoreId)))
                                     .Where(item => valid(item.Analysis))
                                     .Select(item => item with { Analysis = item.Analysis! })
                                     .ToArray();
        ReplayObjectJudgement[] judgements = exact.SelectMany(item => item.Analysis!.Judgements).ToArray();
        ClassifiedMiss[] classifiedMisses = exact.SelectMany(item => item.Analysis!.Judgements
                                                                         .Where(isMiss)
                                                                         .Where(judgement => judgement.MissAnalysis is { Reason: not ReplayMissReason.Unknown })
                                                                         .Select(judgement => new ClassifiedMiss(item, judgement)))
                                                  .ToArray();
        ReplayObjectJudgement[] misses = judgements.Where(isMiss).ToArray();
        double[] timing = judgements.Where(isTimingSample).Select(item => item.TimeOffsetMs).ToArray();
        double[] cursorDistances = judgements.Where(item => !isMiss(item)
                                                             && item.ObjectPosition is not null
                                                             && item.CursorPosition is not null)
                                              .Select(item => distance(item.ObjectPosition!, item.CursorPosition!))
                                              .Where(double.IsFinite)
                                              .ToArray();

        int distinctMaps = history.Select(mapKey).Distinct(StringComparer.Ordinal).Count();
        int analysedMaps = exact.Select(item => mapKey(item.Run)).Distinct(StringComparer.Ordinal).Count();
        int replayAvailableRuns = Math.Max(history.Count(run => run.HasReplayFile), exact.Length);
        CoachingConfidence confidence = confidenceFor(exact.Length, analysedMaps, replayAvailableRuns);
        CoachingConfidence classificationCoverageConfidence = confidenceForCoverage(misses.Length == 0
            ? 0
            : (double)classifiedMisses.Length / misses.Length);
        var coverage = new GlobalCoachingCoverage(
            history.Length,
            replayAvailableRuns,
            exact.Length,
            distinctMaps,
            analysedMaps,
            judgements.Length,
            confidence,
            misses.Length,
            classifiedMisses.Length);

        GlobalMissReasonShare[] reasons = classifiedMisses.GroupBy(item => item.Judgement.MissAnalysis!.Reason)
                                                .Select(group =>
                                                {
                                                    ClassifiedMiss[] evidence = group.ToArray();
                                                    int runCount = evidence.Select(item => item.Run.Run.ScoreId).Distinct().Count();
                                                    int mapCount = evidence.Select(item => mapKey(item.Run.Run)).Distinct(StringComparer.Ordinal).Count();
                                                    double averageClassifierConfidence = evidence.Average(item => classifierConfidence(item.Judgement));
                                                    return new GlobalMissReasonShare(
                                                        group.Key,
                                                        evidence.Length,
                                                        classifiedMisses.Length == 0 ? 0 : (double)evidence.Length / classifiedMisses.Length,
                                                        runCount,
                                                        mapCount,
                                                        averageClassifierConfidence,
                                                        capConfidence(
                                                            confidenceForEvidence(evidence.Length, runCount, mapCount, averageClassifierConfidence),
                                                            confidence,
                                                            classificationCoverageConfidence));
                                                })
                                                .OrderByDescending(reasonEvidenceScore)
                                                .ThenByDescending(item => item.Count)
                                                .ThenBy(item => item.Reason)
                                                .ToArray();

        (string timingTendency, string timingDetail) = timingSummary(timing);
        (string aimTendency, string aimDetail) = aimSummary(cursorDistances, reasons);
        GlobalSkillAreaEvidence[] skillAreas = buildSkillAreas(
            classifiedMisses,
            analysedMaps,
            confidence,
            classificationCoverageConfidence);
        GlobalRecurringWeakness[] recurring = recurringWeaknesses(exact, reasons);
        GlobalCoachingPriority[] priorities = buildPriorities(coverage, reasons, recurring, timing);

        return new GlobalCoachingProfile(
            coverage,
            reasons,
            timingTendency,
            timingDetail,
            aimTendency,
            aimDetail,
            recurring,
            priorities,
            skillAreas);
    }

    private static GlobalRecurringWeakness[] recurringWeaknesses(
        IReadOnlyList<AnalysedRun> exact,
        IReadOnlyList<GlobalMissReasonShare> reasons)
    {
        var weaknesses = new List<GlobalRecurringWeakness>();
        foreach (GlobalMissReasonShare reason in reasons.Where(item => item.Count >= 2 && (item.MapCount >= 2 || item.RunCount >= 2)))
        {
            string label = ReplayMissInsightPresenter.Label(reason.Reason);
            weaknesses.Add(new GlobalRecurringWeakness(
                $"reason:{reason.Reason}",
                label,
                $"{reason.Count:N0} classified misses across {reason.RunCount:N0} analysed plays and {reason.MapCount:N0} maps.",
                reason.Count,
                reason.RunCount,
                reason.MapCount,
                reason.Confidence));
        }

        foreach (IGrouping<string, AnalysedRun> map in exact.GroupBy(item => mapKey(item.Run), StringComparer.Ordinal)
                                                            .Where(group => group.Count() >= 2))
        {
            var mapReasons = map.SelectMany(item => item.Analysis!.Judgements
                                                        .Where(isMiss)
                                                        .Where(judgement => judgement.MissAnalysis is { Reason: not ReplayMissReason.Unknown })
                                                        .Select(judgement => new { item.Run.ScoreId, Reason = judgement.MissAnalysis!.Reason }))
                                .ToArray();
            if (mapReasons.Length < 2)
                continue;

            var dominant = mapReasons.GroupBy(item => item.Reason)
                                     .Select(group => new
                                     {
                                         Reason = group.Key,
                                         Count = group.Count(),
                                         RunCount = group.Select(item => item.ScoreId).Distinct().Count(),
                                     })
                                     .Where(group => group.RunCount >= 2)
                                     .OrderByDescending(group => group.RunCount)
                                     .ThenByDescending(group => group.Count)
                                     .FirstOrDefault();
            if (dominant is null)
                continue;

            AnalysedRun latest = map.OrderByDescending(item => item.Run.PlayedAt).First();
            CoachingConfidence weaknessConfidence = capConfidence(
                confidenceForEvidence(dominant.Count, dominant.RunCount, 1, 1),
                confidenceFor(map.Count(), 1, map.Count()));
            weaknesses.Add(new GlobalRecurringWeakness(
                $"map:{map.Key}:{dominant.Reason}",
                $"Repeated on {latest.Run.Title}",
                $"{ReplayMissInsightPresenter.Label(dominant.Reason)} appeared {dominant.Count:N0} times across {dominant.RunCount:N0} analysed attempts on [{latest.Run.Difficulty}].",
                dominant.Count,
                dominant.RunCount,
                1,
                weaknessConfidence));
        }

        return weaknesses.OrderByDescending(item => item.MapCount)
                         .ThenByDescending(item => item.Confidence)
                         .ThenByDescending(item => item.EvidenceCount)
                         .Take(5)
                         .ToArray();
    }

    private static GlobalSkillAreaEvidence[] buildSkillAreas(
        IReadOnlyList<ClassifiedMiss> classifiedMisses,
        int analysedMapCount,
        CoachingConfidence profileConfidence,
        CoachingConfidence classificationCoverageConfidence)
    {
        return classifiedMisses.GroupBy(item => skillArea(item.Judgement.MissAnalysis!.Reason))
                               .Select(group =>
                               {
                                   ClassifiedMiss[] evidence = group.ToArray();
                                   int count = evidence.Length;
                                   int runCount = evidence.Select(item => item.Run.Run.ScoreId).Distinct().Count();
                                   int mapCount = evidence.Select(item => mapKey(item.Run.Run)).Distinct(StringComparer.Ordinal).Count();
                                   double share = classifiedMisses.Count == 0 ? 0 : (double)count / classifiedMisses.Count;
                                   double mapCoverage = analysedMapCount == 0 ? 0 : (double)mapCount / analysedMapCount;
                                   double averageClassifierConfidence = evidence.Average(item => classifierConfidence(item.Judgement));
                                   CoachingConfidence confidence = capConfidence(
                                       confidenceForEvidence(count, runCount, mapCount, averageClassifierConfidence),
                                       profileConfidence,
                                       classificationCoverageConfidence);
                                   CoachingSkillArea area = group.Key;
                                   return new GlobalSkillAreaEvidence(
                                       area,
                                       skillAreaLabel(area),
                                       count,
                                       runCount,
                                       mapCount,
                                       share,
                                       mapCoverage,
                                       confidence,
                                       $"{count:N0} classified misses across {runCount:N0} plays and {mapCount:N0} maps ({share * 100:0}% of classified misses).");
                               })
                               .OrderByDescending(item => item.MapCount)
                               .ThenByDescending(item => item.ShareOfClassifiedMisses)
                               .ThenByDescending(item => item.EvidenceCount)
                               .ToArray();
    }

    private static GlobalCoachingPriority[] buildPriorities(
        GlobalCoachingCoverage coverage,
        IReadOnlyList<GlobalMissReasonShare> reasons,
        IReadOnlyList<GlobalRecurringWeakness> recurring,
        IReadOnlyList<double> timing)
    {
        var priorities = new List<GlobalCoachingPriority>();
        GlobalMissReasonShare? dominant = reasons.OrderByDescending(reasonEvidenceScore).FirstOrDefault();
        if (dominant is not null)
        {
            priorities.Add(new GlobalCoachingPriority(
                practiceTitle(dominant.Reason),
                practiceDetail(dominant),
                $"{dominant.Share * 100:0}% of misses",
                dominant.Confidence));
        }

        if (timing.Count >= 10)
        {
            double median = percentile(timing, 0.5);
            double spread = standardDeviation(timing);
            if (Math.Abs(median) > centred_timing_ms || spread >= 25)
            {
                priorities.Add(new GlobalCoachingPriority(
                    Math.Abs(median) > centred_timing_ms ? "Correct timing bias" : "Stabilise tapping",
                    Math.Abs(median) > centred_timing_ms
                        ? $"Median hit timing is {formatSigned(median)} across {timing.Count:N0} exact taps. Use lower-density patterns and centre the hit window before adding speed."
                        : $"Timing spread is {spread:0.0} ms across {timing.Count:N0} exact taps. Build repeatability on comfortable BPM before increasing difficulty.",
                    Math.Abs(median) > centred_timing_ms ? formatSigned(median) : $"{spread:0.0} ms spread",
                    capConfidence(confidenceForSamples(timing.Count), coverage.Confidence)));
            }
        }

        GlobalRecurringWeakness? repeatedMap = recurring.FirstOrDefault(item => item.MapCount == 1);
        if (repeatedMap is not null)
        {
            priorities.Add(new GlobalCoachingPriority(
                "Review a repeated failure",
                repeatedMap.Detail,
                $"{repeatedMap.RunCount:N0} attempts",
                repeatedMap.Confidence));
        }

        if (priorities.Count == 0)
        {
            priorities.Add(new GlobalCoachingPriority(
                "Build replay evidence",
                coverage.ReplayAvailableRunCount == 0
                    ? "No saved local replays are available for object-level coaching. Submitted scores still contribute to performance trends."
                    : $"{coverage.AnalysedRunCount:N0} of {coverage.ReplayAvailableRunCount:N0} replay-backed plays are analysed. More maps will make mechanics priorities more reliable.",
                $"{coverage.ReplayCoverage * 100:0}% covered",
                coverage.Confidence));
        }

        return priorities.OrderByDescending(priority => priority.Confidence)
                         .Take(4)
                         .ToArray();
    }

    private static (string Value, string Detail) timingSummary(IReadOnlyList<double> timing)
    {
        if (timing.Count == 0)
            return ("Not measured", "Exact replay timing has not been measured yet.");

        double median = percentile(timing, 0.5);
        double spread = standardDeviation(timing);
        string value = median < -centred_timing_ms ? "Early bias" : median > centred_timing_ms ? "Late bias" : "Centred";
        return (value, $"Median {formatSigned(median)}, {spread:0.0} ms spread across {timing.Count:N0} taps.");
    }

    private static (string Value, string Detail) aimSummary(
        IReadOnlyList<double> distances,
        IReadOnlyList<GlobalMissReasonShare> reasons)
    {
        if (distances.Count == 0)
            return ("Not measured", "Cursor placement has not been measured yet.");

        double median = percentile(distances, 0.5);
        double p90 = percentile(distances, 0.9);
        GlobalMissReasonShare? aimReason = reasons.FirstOrDefault(item => item.Reason is ReplayMissReason.Undershoot
            or ReplayMissReason.Overshoot or ReplayMissReason.AimDeviation);
        string value = aimReason is null ? $"{median:0.0} unit median" : ReplayMissInsightPresenter.Label(aimReason.Reason);
        return (value, $"Successful hits: {median:0.0} playfield units median cursor error, {p90:0.0} at p90 across {distances.Count:N0} samples.");
    }

    private static CoachingConfidence confidenceFor(int analysedRuns, int analysedMaps, int replayRuns)
    {
        double coverage = replayRuns == 0 ? 0 : (double)analysedRuns / replayRuns;
        if (analysedRuns >= 20 && analysedMaps >= 10 && coverage >= 0.5)
            return CoachingConfidence.High;
        if (analysedRuns >= 8 && analysedMaps >= 4)
            return CoachingConfidence.Medium;
        if (analysedRuns >= 2)
            return CoachingConfidence.Low;
        return CoachingConfidence.Insufficient;
    }

    private static CoachingConfidence confidenceForEvidence(int evidenceCount, int runCount, int mapCount, double classifierConfidence)
    {
        CoachingConfidence confidence = (evidenceCount, runCount, mapCount) switch
        {
            (>= 15, >= 10, >= 5) => CoachingConfidence.High,
            (>= 8, >= 5, >= 3) => CoachingConfidence.Medium,
            (>= 3, >= 2, _) or (_, _, >= 2) => CoachingConfidence.Low,
            _ => CoachingConfidence.Insufficient,
        };

        if (classifierConfidence is > 0 and < 0.5 && confidence > CoachingConfidence.Low)
            return (CoachingConfidence)((int)confidence - 1);
        return confidence;
    }

    private static CoachingConfidence confidenceForSamples(int sampleCount) => sampleCount switch
    {
        >= 100 => CoachingConfidence.High,
        >= 30 => CoachingConfidence.Medium,
        >= 10 => CoachingConfidence.Low,
        _ => CoachingConfidence.Insufficient,
    };

    private static CoachingConfidence confidenceForCoverage(double coverage) => coverage switch
    {
        >= 0.75 => CoachingConfidence.High,
        >= 0.5 => CoachingConfidence.Medium,
        >= 0.25 => CoachingConfidence.Low,
        _ => CoachingConfidence.Insufficient,
    };

    private static CoachingConfidence capConfidence(params CoachingConfidence[] values) =>
        values.Length == 0 ? CoachingConfidence.Insufficient : values.Min();

    private static double reasonEvidenceScore(GlobalMissReasonShare reason) =>
        reason.MapCount * 1000 + reason.RunCount * 100 + reason.Count * 10 + reason.AverageClassifierConfidence;

    private static double classifierConfidence(ReplayObjectJudgement judgement)
    {
        double value = judgement.MissAnalysis?.Confidence ?? 0;
        return double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
    }

    private static CoachingSkillArea skillArea(ReplayMissReason reason) => reason switch
    {
        ReplayMissReason.Undershoot or ReplayMissReason.Overshoot => CoachingSkillArea.AimControl,
        ReplayMissReason.AimDeviation => CoachingSkillArea.AimPrecision,
        ReplayMissReason.EarlyClick or ReplayMissReason.LateClick => CoachingSkillArea.TapTiming,
        ReplayMissReason.OnTargetNoClick => CoachingSkillArea.AimTapCoordination,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Only classified miss reasons have a skill area."),
    };

    private static string skillAreaLabel(CoachingSkillArea area) => area switch
    {
        CoachingSkillArea.AimControl => "Aim control",
        CoachingSkillArea.AimPrecision => "Aim precision",
        CoachingSkillArea.TapTiming => "Tap timing",
        CoachingSkillArea.AimTapCoordination => "Aim-tap coordination",
        _ => throw new ArgumentOutOfRangeException(nameof(area)),
    };

    private static string practiceTitle(ReplayMissReason reason) => reason switch
    {
        ReplayMissReason.EarlyClick => "Delay premature clicks",
        ReplayMissReason.LateClick => "Commit earlier",
        ReplayMissReason.Undershoot => "Finish jump travel",
        ReplayMissReason.Overshoot => "Control jump braking",
        ReplayMissReason.OnTargetNoClick => "Coordinate aim and tapping",
        ReplayMissReason.AimDeviation => "Improve approach precision",
        _ => "Review classified misses",
    };

    private static string practiceDetail(GlobalMissReasonShare reason) => reason.Reason switch
    {
        ReplayMissReason.EarlyClick => $"Early clicks account for {reason.Count:N0} classified misses across {reason.MapCount:N0} maps. Practise readable jump patterns below your limit and wait for cursor arrival.",
        ReplayMissReason.LateClick => $"Late clicks account for {reason.Count:N0} classified misses across {reason.MapCount:N0} maps. Practise committing as the cursor enters the target instead of correcting after arrival.",
        ReplayMissReason.Undershoot => $"Undershoots account for {reason.Count:N0} classified misses across {reason.MapCount:N0} maps. Isolate longer jumps and complete the full movement before tapping.",
        ReplayMissReason.Overshoot => $"Overshoots account for {reason.Count:N0} classified misses across {reason.MapCount:N0} maps. Use lower-BPM jump sections to train braking at the target centre.",
        ReplayMissReason.OnTargetNoClick => $"The cursor reached the target without a click on {reason.Count:N0} misses across {reason.MapCount:N0} maps. Prioritise hand synchronisation drills.",
        ReplayMissReason.AimDeviation => $"Aim deviation accounts for {reason.Count:N0} classified misses across {reason.MapCount:N0} maps. Practise the affected spacing at a controlled rate.",
        _ => $"Review {reason.Count:N0} classified misses across {reason.MapCount:N0} maps.",
    };

    private static bool valid(ReplayAnalysisResult? analysis) => analysis?.Summary is not null && analysis.Judgements is not null;

    private static bool isMiss(ReplayObjectJudgement judgement) =>
        string.Equals(judgement.Result, "Miss", StringComparison.OrdinalIgnoreCase);

    private static bool isTimingSample(ReplayObjectJudgement judgement) =>
        !isMiss(judgement)
        && string.Equals(judgement.MaximumResult, "Great", StringComparison.OrdinalIgnoreCase)
        && double.IsFinite(judgement.TimeOffsetMs);

    private static string mapKey(LocalReplay run) => run.BeatmapId != Guid.Empty
        ? run.BeatmapId.ToString("N")
        : $"{run.Title}\u001f{run.Difficulty}";

    private static double distance(ReplayPoint left, ReplayPoint right)
    {
        double x = left.X - right.X;
        double y = left.Y - right.Y;
        return Math.Sqrt(x * x + y * y);
    }

    private static double standardDeviation(IEnumerable<double> values)
    {
        double[] samples = values.Where(double.IsFinite).ToArray();
        if (samples.Length == 0)
            return 0;
        double mean = samples.Average();
        return Math.Sqrt(samples.Average(value => Math.Pow(value - mean, 2)));
    }

    private static double percentile(IEnumerable<double> values, double percentile)
    {
        double[] ordered = values.Where(double.IsFinite).OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
            return 0;
        double index = Math.Clamp(percentile, 0, 1) * (ordered.Length - 1);
        int lower = (int)Math.Floor(index);
        int upper = (int)Math.Ceiling(index);
        return lower == upper ? ordered[lower] : ordered[lower] + (ordered[upper] - ordered[lower]) * (index - lower);
    }

    private static string formatSigned(double value) => $"{value:+0.0;-0.0;0.0} ms";

    private sealed record AnalysedRun(LocalReplay Run, ReplayAnalysisResult? Analysis);

    private sealed record ClassifiedMiss(AnalysedRun Run, ReplayObjectJudgement Judgement);
}

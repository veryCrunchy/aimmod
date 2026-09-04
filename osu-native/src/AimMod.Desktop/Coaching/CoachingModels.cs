using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop.Coaching;

public enum CoachingRunSort
{
    Recent,
    Accuracy,
    StarRating,
    Misses,
}

public sealed record CoachingRunQuery(
    string SearchText = "",
    string RulesetShortName = "osu",
    bool RequireReplayFile = false,
    CoachingRunSort Sort = CoachingRunSort.Recent,
    int Offset = 0,
    int Limit = 40)
{
    public const int MaximumSearchLength = 256;
    public const int MaximumPageSize = 100;

    public CoachingRunQuery Normalised()
    {
        string search = (SearchText ?? string.Empty).Trim();
        if (search.Length > MaximumSearchLength)
            search = search[..MaximumSearchLength];

        return this with
        {
            SearchText = search,
            RulesetShortName = (RulesetShortName ?? string.Empty).Trim(),
            Offset = Math.Max(0, Offset),
            Limit = Math.Clamp(Limit, 1, MaximumPageSize),
        };
    }
}

public sealed record CoachingRecentRun(
    Guid ScoreId,
    Guid BeatmapId,
    string Title,
    string Artist,
    string Difficulty,
    string RulesetShortName,
    DateTimeOffset PlayedAt,
    double StarRating,
    double Accuracy,
    int MissCount,
    double? PerformancePoints,
    IReadOnlyList<string> Mods,
    bool CanAnalyse);

public sealed record CoachingRunPage(
    IReadOnlyList<CoachingRecentRun> Items,
    int Total,
    int Offset,
    int Limit)
{
    public bool HasMore => Offset + Items.Count < Total;
}

public sealed record CoachingAccuracySummary(
    int RunCount,
    double? Average,
    double? Best,
    double? Latest,
    double? RecentChange);

public sealed record CoachingMissSummary(
    int RunCount,
    int Total,
    double? Average,
    int RunsWithoutMisses,
    int AnalysedRunCount,
    int AnalysedObjectMisses,
    int AnalysedSliderBreaks);

public sealed record CoachingTimingSummary(
    int AnalysedRunCount,
    int SampleCount,
    double? MeanOffsetMilliseconds,
    double? MeanAbsoluteOffsetMilliseconds,
    double? StandardDeviationMilliseconds,
    int EarlyCount,
    int CentredCount,
    int LateCount);

public sealed record CoachingChartPoint(
    Guid ScoreId,
    DateTimeOffset PlayedAt,
    double Value);

public sealed record CoachingChartSeries(
    string Key,
    string Label,
    string Unit,
    IReadOnlyList<CoachingChartPoint> Points);

public sealed record CoachingAdvice(
    string Title,
    string Detail,
    Guid? ScoreId,
    Guid? BeatmapId,
    double? ReviewTimeMilliseconds);

public enum CoachingConfidence
{
    Insufficient,
    Low,
    Medium,
    High,
}

public sealed record CoachingHistoryQuality(
    int RunCount,
    int ValidAccuracyRunCount,
    int DistinctSetupCount,
    int ExactAnalysisRunCount);

public sealed record CoachingPerformanceTrend(
    int WindowSize,
    double? RecentAccuracyChange,
    int MatchedSetupCount,
    int MatchedComparisonCount,
    double? MatchedAccuracyChange,
    int ImprovedComparisonCount,
    int SteadyComparisonCount,
    int DeclinedComparisonCount,
    string Direction,
    CoachingConfidence Confidence);

public sealed record CoachingSetupBenchmark(
    Guid ScoreId,
    int PriorRunCount,
    double? PriorMedianAccuracy,
    double? BestPriorAccuracy,
    double? AccuracyChangeFromBest,
    int? BestPriorMissCount,
    int? MissChangeFromBest,
    double? EmpiricalAccuracyPercentile,
    string Summary);

public sealed record CoachingDifficultyBand(
    double MinimumStars,
    double MaximumStars,
    int RunCount,
    double AverageAccuracy,
    double AccuracyStandardDeviation,
    double MissFreeRate,
    double SustainableResultRate);

public sealed record CoachingDifficultyFit(
    CoachingDifficultyBand? BestFit,
    IReadOnlyList<CoachingDifficultyBand> Bands,
    CoachingConfidence Confidence,
    string Summary);

public sealed record CoachingAccuracyPrediction(
    Guid ScoreId,
    Guid BeatmapId,
    double StarRating,
    double ExpectedAccuracy,
    double LowerAccuracy,
    double UpperAccuracy,
    double ExpectedMisses,
    int SampleCount,
    double EffectiveSampleSize,
    int SameSetupSampleCount,
    CoachingConfidence Confidence,
    string Method);

public sealed record CoachingSessionDrift(
    int SessionCount,
    double? AccuracyChange,
    double? MissChange,
    CoachingConfidence Confidence,
    string Summary);

public sealed record CoachingMechanicsProfile(
    int ExactAnalysisRunCount,
    int JudgementCount,
    int TimingSampleCount,
    double? MeanTimingOffsetMilliseconds,
    double? TimingStandardDeviationMilliseconds,
    int CursorDistanceSampleCount,
    double? MeanCursorDistancePlayfieldUnits,
    int ExactMissCount,
    string? WeakestMapSegment,
    double? MedianTimingOffsetMilliseconds,
    double? NinetiethPercentileAbsoluteTimingOffsetMilliseconds,
    double? MedianCursorDistancePlayfieldUnits,
    double? NinetiethPercentileCursorDistancePlayfieldUnits,
    IReadOnlyList<CoachingMapSegment> MapSegments,
    IReadOnlyDictionary<ReplayMissReason, int>? MissReasonCounts = null,
    ReplayMissReason? DominantMissReason = null);

public sealed record CoachingMapSegment(
    string Key,
    string Label,
    int PrimaryJudgementCount,
    int MissCount,
    double? MissRate,
    int SliderBreakCount);

public sealed record CoachingRecommendation(
    int Rank,
    Guid BeatmapId,
    Guid ScoreId,
    string Title,
    string Difficulty,
    string Intent,
    string Reason,
    double? ExpectedAccuracy,
    CoachingConfidence Confidence,
    int SampleCount);

public sealed record CoachingPpOpportunity(
    int Rank,
    Guid BeatmapId,
    Guid ScoreId,
    string Title,
    string Difficulty,
    double StarRating,
    double CurrentPp,
    double ProjectedPp,
    double RealisticGain,
    double? TargetAccuracy,
    int? TargetMissCount,
    CoachingConfidence Confidence,
    int SameSetupSampleCount,
    int SimilarStarSampleCount,
    string Reason,
    double ProfilePpGain = 0);

public sealed record CoachingPpPlan(
    int PpRunCount,
    double? CurrentBestScorePp,
    double? BestOpportunityGain,
    double? TopThreeRawGain,
    CoachingConfidence Confidence,
    string Summary,
    IReadOnlyList<CoachingPpOpportunity> Opportunities,
    double? BestProfilePpGain = null,
    double? TopThreeProfilePpGain = null);

public static class CoachingPpWeighting
{
    private const int maximum_weighted_scores = 100;
    private const double score_weight = 0.95;

    public static double CalculateProfileGain(
        IReadOnlyList<LocalReplay> history,
        Guid beatmapId,
        double projectedPp)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (beatmapId == Guid.Empty || !double.IsFinite(projectedPp) || projectedPp < 0)
            return 0;

        Dictionary<Guid, double> bestByBeatmap = history.Where(run => run.BeatmapId != Guid.Empty
                                                                       && run.PerformancePoints is { } pp
                                                                       && double.IsFinite(pp)
                                                                       && pp >= 0)
                                                            .GroupBy(run => run.BeatmapId)
                                                            .ToDictionary(
                                                                group => group.Key,
                                                                group => group.Max(run => run.PerformancePoints!.Value));
        double before = weightedTotal(bestByBeatmap.Values);
        bestByBeatmap[beatmapId] = Math.Max(projectedPp, bestByBeatmap.GetValueOrDefault(beatmapId));
        return Math.Max(0, weightedTotal(bestByBeatmap.Values) - before);
    }

    public static double CalculateCombinedProfileGain(
        IReadOnlyList<LocalReplay> history,
        IEnumerable<(Guid BeatmapId, double ProjectedPp)> projections)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(projections);
        Dictionary<Guid, double> bestByBeatmap = history.Where(run => run.BeatmapId != Guid.Empty
                                                                       && run.PerformancePoints is { } pp
                                                                       && double.IsFinite(pp)
                                                                       && pp >= 0)
                                                            .GroupBy(run => run.BeatmapId)
                                                            .ToDictionary(
                                                                group => group.Key,
                                                                group => group.Max(run => run.PerformancePoints!.Value));
        double before = weightedTotal(bestByBeatmap.Values);
        foreach ((Guid targetBeatmapId, double targetPp) in projections)
        {
            if (targetBeatmapId == Guid.Empty || !double.IsFinite(targetPp) || targetPp < 0)
                continue;
            bestByBeatmap[targetBeatmapId] = Math.Max(targetPp, bestByBeatmap.GetValueOrDefault(targetBeatmapId));
        }
        return Math.Max(0, weightedTotal(bestByBeatmap.Values) - before);
    }

    private static double weightedTotal(IEnumerable<double> scores) =>
        scores.OrderByDescending(score => score)
              .Take(maximum_weighted_scores)
              .Select((score, index) => score * Math.Pow(score_weight, index))
              .Sum();
}

public sealed record CoachingIntelligence(
    CoachingHistoryQuality History,
    CoachingPerformanceTrend Trend,
    CoachingDifficultyFit DifficultyFit,
    CoachingAccuracyPrediction? SelectedRunPrediction,
    CoachingSetupBenchmark? SelectedRunBenchmark,
    CoachingSessionDrift SessionDrift,
    CoachingMechanicsProfile Mechanics,
    CoachingPpPlan PpPlan,
    IReadOnlyList<CoachingRecommendation> Recommendations)
{
    public static CoachingIntelligence Empty { get; } = new(
        new CoachingHistoryQuality(0, 0, 0, 0),
        new CoachingPerformanceTrend(0, null, 0, 0, null, 0, 0, 0, "No trend yet", CoachingConfidence.Insufficient),
        new CoachingDifficultyFit(null, Array.Empty<CoachingDifficultyBand>(), CoachingConfidence.Insufficient, "Play a few maps to measure your current fit."),
        null,
        null,
        new CoachingSessionDrift(0, null, null, CoachingConfidence.Insufficient, "No multi-play sessions yet."),
        new CoachingMechanicsProfile(0, 0, 0, null, null, 0, null, 0, null, null, null, null, null, Array.Empty<CoachingMapSegment>()),
        new CoachingPpPlan(0, null, null, null, CoachingConfidence.Insufficient, "No local osu!standard plays with PP values are available yet.", Array.Empty<CoachingPpOpportunity>()),
        Array.Empty<CoachingRecommendation>());
}

public sealed record CoachingReport(
    CoachingRecentRun? SelectedRun,
    CoachingAccuracySummary Accuracy,
    CoachingMissSummary Misses,
    CoachingTimingSummary Timing,
    IReadOnlyList<CoachingChartSeries> Series,
    CoachingAdvice NextPlay)
{
    public CoachingIntelligence Intelligence { get; init; } = CoachingIntelligence.Empty;
}

public static class CoachingLimits
{
    public const int MaximumRuns = StatisticsHistoryLoader.MaximumRuns;
    public const double CentredTimingThresholdMilliseconds = 10;
    public const int MinimumTimingSamplesForDirectionAdvice = 10;
    public const double DirectionAdviceThresholdMilliseconds = 12;
    public const double DifficultyBandWidth = 0.5;
    public const int PredictionNeighbourLimit = 80;
    public const int RecommendationLimit = 5;
    public const int MaximumRecommendationCandidates = 200;
    public const int MaximumPpCandidateSetups = 600;
    public const int MinimumRunsPerDifficultyBand = 3;
    public const int MinimumPlaysPerSession = 4;
    public const int SessionGapMinutes = 45;
}

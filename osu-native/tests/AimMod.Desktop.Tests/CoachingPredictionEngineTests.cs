using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class CoachingPredictionEngineTests
{
    [Test]
    public void EmptyHistoryDoesNotInventPredictionsOrRecommendations()
    {
        CoachingIntelligence result = CoachingPredictionEngine.Build(
            Array.Empty<LocalReplay>(),
            new Dictionary<Guid, ReplayAnalysisResult>());

        Assert.Multiple(() =>
        {
            Assert.That(result.History.RunCount, Is.Zero);
            Assert.That(result.SelectedRunPrediction, Is.Null);
            Assert.That(result.DifficultyFit.BestFit, Is.Null);
            Assert.That(result.DifficultyFit.Confidence, Is.EqualTo(CoachingConfidence.Insufficient));
            Assert.That(result.Trend.MatchedAccuracyChange, Is.Null);
            Assert.That(result.PpPlan.Opportunities, Is.Empty);
            Assert.That(result.Recommendations, Is.Empty);
        });
    }

    [Test]
    public void SparseHistoryKeepsItsEstimateMarkedInsufficient()
    {
        LocalReplay first = run(1, 1, 4.2, 0.94, 1, "Hidden");
        LocalReplay target = run(2, 2, 4.3, 0.95, 0, "Hidden");

        CoachingIntelligence result = CoachingPredictionEngine.Build(
            new[] { first, target },
            new Dictionary<Guid, ReplayAnalysisResult>(),
            target.ScoreId);

        Assert.Multiple(() =>
        {
            Assert.That(result.SelectedRunPrediction, Is.Not.Null);
            Assert.That(result.SelectedRunPrediction!.SampleCount, Is.EqualTo(1));
            Assert.That(result.SelectedRunPrediction.Confidence, Is.EqualTo(CoachingConfidence.Insufficient));
            Assert.That(result.SelectedRunPrediction.UpperAccuracy - result.SelectedRunPrediction.LowerAccuracy,
                Is.GreaterThanOrEqualTo(0.059));
            Assert.That(result.DifficultyFit.BestFit, Is.Null);
            Assert.That(result.Recommendations, Is.Not.Empty);
        });
    }

    [Test]
    public void PredictionFavoursMatchingMapAndModsOverMixedHistory()
    {
        Guid matchingBeatmap = id(100);
        var runs = new List<LocalReplay>
        {
            run(1, 100, 5.0, 0.95, 1, "Hidden"),
            run(2, 100, 5.0, 0.96, 0, "Hidden"),
            run(3, 100, 5.0, 0.94, 1, "Hidden"),
            run(4, 201, 5.0, 0.80, 8),
            run(5, 202, 6.5, 0.84, 6, "Hidden"),
            run(6, 203, 4.9, 0.82, 7, "HardRock"),
        };
        LocalReplay target = run(7, 100, 5.0, 0.97, 0, "Hidden");
        runs.Add(target);

        CoachingAccuracyPrediction? prediction = CoachingPredictionEngine.Predict(runs, target);

        Assert.Multiple(() =>
        {
            Assert.That(target.BeatmapId, Is.EqualTo(matchingBeatmap));
            Assert.That(prediction, Is.Not.Null);
            Assert.That(prediction!.SameSetupSampleCount, Is.EqualTo(3));
            Assert.That(prediction.SampleCount, Is.EqualTo(6));
            Assert.That(prediction.ExpectedAccuracy, Is.GreaterThan(0.90));
            Assert.That(prediction.ExpectedAccuracy, Is.LessThan(0.96));
            Assert.That(prediction.LowerAccuracy, Is.LessThan(prediction.ExpectedAccuracy));
            Assert.That(prediction.UpperAccuracy, Is.GreaterThan(prediction.ExpectedAccuracy));
            Assert.That(prediction.Method, Does.Contain("personal history"));
            Assert.That(prediction.Method, Does.Contain("not a guarantee"));
        });
    }

    [Test]
    public void RetrospectivePredictionDoesNotReadFutureResults()
    {
        LocalReplay prior = run(1, 1, 5.0, 0.90, 3);
        LocalReplay target = run(2, 1, 5.0, 0.91, 2);
        LocalReplay future = run(3, 1, 5.0, 1.00, 0);

        CoachingAccuracyPrediction? prediction = CoachingPredictionEngine.Predict(new[] { prior, target, future }, target);

        Assert.Multiple(() =>
        {
            Assert.That(prediction, Is.Not.Null);
            Assert.That(prediction!.SampleCount, Is.EqualTo(1));
            Assert.That(prediction.ExpectedAccuracy, Is.EqualTo(0.90).Within(0.0001));
        });
    }

    [Test]
    public void RepeatedSetupTrendSeparatesImprovementFromMixedMapOrder()
    {
        LocalReplay[] runs =
        {
            run(1, 1, 4.0, 0.90, 3),
            run(2, 2, 6.0, 0.80, 8, "HardRock"),
            run(3, 1, 4.0, 0.94, 1),
            run(4, 3, 3.5, 0.98, 0, "Hidden"),
            run(5, 2, 6.0, 0.84, 5, "HardRock"),
            run(6, 3, 3.5, 0.99, 0, "Hidden"),
        };

        CoachingIntelligence result = CoachingPredictionEngine.Build(
            runs,
            new Dictionary<Guid, ReplayAnalysisResult>());

        Assert.Multiple(() =>
        {
            Assert.That(result.Trend.MatchedSetupCount, Is.EqualTo(3));
            Assert.That(result.Trend.MatchedComparisonCount, Is.EqualTo(3));
            Assert.That(result.Trend.MatchedAccuracyChange, Is.EqualTo(0.04).Within(0.0001));
            Assert.That(result.Trend.ImprovedComparisonCount, Is.EqualTo(3));
            Assert.That(result.Trend.SteadyComparisonCount, Is.Zero);
            Assert.That(result.Trend.DeclinedComparisonCount, Is.Zero);
            Assert.That(result.Trend.Direction, Is.EqualTo("Improving on repeated setups"));
            Assert.That(result.Trend.Confidence, Is.EqualTo(CoachingConfidence.Low));
            Assert.That(result.Recommendations.First().Intent, Is.EqualTo("Confirm improvement"));
        });
    }

    [Test]
    public void PpPlanRanksWeightedProfileGains()
    {
        Guid targetBeatmap = id(70);
        LocalReplay[] runs =
        {
            run(1, 70, 5.3, 0.94, 2) with { PerformancePoints = 220 },
            run(2, 70, 5.3, 0.97, 0) with { PerformancePoints = 260 },
            run(3, 71, 5.1, 0.96, 0) with { PerformancePoints = 245 },
            run(4, 72, 5.4, 0.95, 1) with { PerformancePoints = 252 },
            run(5, 73, 5.6, 0.94, 2) with { PerformancePoints = 270 },
            run(6, 70, 5.3, 0.93, 3) with { PerformancePoints = 230 },
        };

        CoachingPpPlan plan = CoachingPredictionEngine.Build(
            runs,
            new Dictionary<Guid, ReplayAnalysisResult>()).PpPlan;

        Assert.Multiple(() =>
        {
            Assert.That(plan.PpRunCount, Is.EqualTo(runs.Length));
            Assert.That(plan.CurrentBestScorePp, Is.EqualTo(270).Within(0.0001));
            Assert.That(plan.Opportunities, Is.Not.Empty);
            Assert.That(plan.Opportunities[0].BeatmapId, Is.Not.EqualTo(targetBeatmap), "A lower retry must not outrank an existing personal best on the same map.");
            Assert.That(plan.Opportunities[0].ProfilePpGain, Is.GreaterThan(0));
            Assert.That(plan.Opportunities, Has.None.Matches<CoachingPpOpportunity>(opportunity => opportunity.BeatmapId == targetBeatmap));
            Assert.That(plan.BestProfilePpGain, Is.EqualTo(plan.Opportunities[0].ProfilePpGain).Within(0.0001));
            Assert.That(plan.TopThreeProfilePpGain, Is.GreaterThanOrEqualTo(plan.BestProfilePpGain!.Value));
            Assert.That(plan.Summary, Does.Contain("profile pp"));
        });
    }

    [Test]
    public void PpPlanDoesNotInventOpportunitiesWithoutPpValues()
    {
        LocalReplay[] runs =
        {
            run(1, 70, 5.3, 0.94, 2) with { PerformancePoints = null },
            run(2, 70, 5.3, 0.97, 0) with { PerformancePoints = null },
            run(3, 71, 5.1, 0.96, 0) with { PerformancePoints = null },
        };

        CoachingPpPlan plan = CoachingPredictionEngine.Build(
            runs,
            new Dictionary<Guid, ReplayAnalysisResult>()).PpPlan;

        Assert.Multiple(() =>
        {
            Assert.That(plan.PpRunCount, Is.Zero);
            Assert.That(plan.CurrentBestScorePp, Is.Null);
            Assert.That(plan.BestOpportunityGain, Is.Null);
            Assert.That(plan.Opportunities, Is.Empty);
            Assert.That(plan.Confidence, Is.EqualTo(CoachingConfidence.Insufficient));
        });
    }

    [Test]
    public void PpPlanDoesNotTreatRetriesAsIndependentSimilarSetupEvidence()
    {
        var runs = new List<LocalReplay>
        {
            run(1, 70, 5.3, 0.92, 3) with { PerformancePoints = 100 },
            run(2, 70, 5.3, 0.92, 3) with { PerformancePoints = 100 },
            run(3, 70, 5.3, 0.92, 3) with { PerformancePoints = 100 },
            run(4, 70, 5.3, 0.92, 3) with { PerformancePoints = 100 },
        };
        runs.AddRange(Enumerable.Range(5, 12)
                                .Select(day => run(day, 71, 5.3, 0.98, 0) with { PerformancePoints = 220 }));

        CoachingPpOpportunity opportunity = CoachingPredictionEngine.Build(
            runs,
            new Dictionary<Guid, ReplayAnalysisResult>()).PpPlan.Opportunities
                                                       .Single(item => item.BeatmapId == id(70));

        Assert.Multiple(() =>
        {
            Assert.That(opportunity.SameSetupSampleCount, Is.EqualTo(3));
            Assert.That(opportunity.SimilarStarSampleCount, Is.EqualTo(2),
                "The cross-setup sample size should count distinct beatmap/mod setups, not retries.");
            Assert.That(opportunity.Confidence, Is.EqualTo(CoachingConfidence.Insufficient));
        });
    }

    [Test]
    public void FullHistoryWithManyDistinctSetupsStaysInteractive()
    {
        DateTimeOffset start = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        LocalReplay[] runs = Enumerable.Range(0, CoachingLimits.MaximumRuns)
                                       .Select(index => run(1, index + 1, 4.5 + index % 30 / 10.0, 0.9 + index % 90 / 1000.0, index % 5) with
                                       {
                                           PlayedAt = start.AddMinutes(index),
                                           PerformancePoints = 100 + index % 350,
                                       })
                                       .ToArray();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        CoachingIntelligence intelligence = CoachingPredictionEngine.Build(
            runs,
            new Dictionary<Guid, ReplayAnalysisResult>());

        stopwatch.Stop();
        Assert.Multiple(() =>
        {
            Assert.That(intelligence.History.RunCount, Is.EqualTo(CoachingLimits.MaximumRuns));
            Assert.That(intelligence.Recommendations, Has.Count.LessThanOrEqualTo(CoachingLimits.RecommendationLimit));
            Assert.That(intelligence.PpPlan.PpRunCount, Is.EqualTo(CoachingLimits.MaximumRuns));
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)));
        });
    }

    [Test]
    public void SetupBenchmarkUsesOnlyEarlierMatchingMapAndMods()
    {
        LocalReplay earlierDifferentMods = run(1, 50, 5.2, 0.99, 0, "HardRock");
        LocalReplay earlierMatch = run(2, 50, 5.2, 0.94, 2, "Hidden", "DoubleTime");
        LocalReplay earlierMatchBest = run(3, 50, 5.2, 0.96, 1, "doubletime", "hidden");
        LocalReplay target = run(4, 50, 5.2, 0.95, 3, "HIDDEN", "DOUBLETIME");
        LocalReplay futureMatch = run(5, 50, 5.2, 1.00, 0, "Hidden", "DoubleTime");

        CoachingSetupBenchmark benchmark = CoachingPredictionEngine.BuildSetupBenchmark(
            new[] { earlierDifferentMods, earlierMatch, earlierMatchBest, target, futureMatch },
            target);

        Assert.Multiple(() =>
        {
            Assert.That(benchmark.PriorRunCount, Is.EqualTo(2));
            Assert.That(benchmark.PriorMedianAccuracy, Is.EqualTo(0.95).Within(0.0001));
            Assert.That(benchmark.BestPriorAccuracy, Is.EqualTo(0.96).Within(0.0001));
            Assert.That(benchmark.AccuracyChangeFromBest, Is.EqualTo(-0.01).Within(0.0001));
            Assert.That(benchmark.BestPriorMissCount, Is.EqualTo(1));
            Assert.That(benchmark.MissChangeFromBest, Is.EqualTo(2));
            Assert.That(benchmark.EmpiricalAccuracyPercentile, Is.EqualTo(0.5));
            Assert.That(benchmark.Summary, Does.Contain("1.00 accuracy points below"));
        });
    }

    [Test]
    public void MechanicsUsesSegmentMissRatesAndReportsRobustDistributions()
    {
        LocalReplay replay = run(1, 70, 5.0, 0.91, 4);
        var judgements = new List<ReplayObjectJudgement>();
        for (int index = 0; index < 20; index++)
        {
            judgements.Add(judgement(
                index < 2 ? "Miss" : "Great",
                index + 1,
                1_000 + index * 500,
                new ReplayPoint(100, 100),
                new ReplayPoint(103, 104)));
        }

        for (int index = 0; index < 4; index++)
            judgements.Add(judgement(index < 2 ? "Miss" : "Great", 5, 40_000 + index * 1_000));
        for (int index = 0; index < 20; index++)
            judgements.Add(judgement("Great", 5, 80_000 + index * 500));
        judgements.Add(judgement("SliderTailMiss", 0, 45_000, objectType: "SliderTail", maximumResult: "Great") with { NestedPath = "0" });
        judgements.Add(judgement("Great", 50, 99_000, new ReplayPoint(0, 0), new ReplayPoint(30, 40)));

        CoachingMechanicsProfile mechanics = CoachingPredictionEngine.Build(
            new[] { replay },
            new Dictionary<Guid, ReplayAnalysisResult> { [replay.ScoreId] = exactAnalysis(judgements.ToArray()) }).Mechanics;

        Assert.Multiple(() =>
        {
            Assert.That(mechanics.MapSegments, Has.Count.EqualTo(3));
            Assert.That(mechanics.MapSegments[0].MissCount, Is.EqualTo(2));
            Assert.That(mechanics.MapSegments[0].MissRate, Is.EqualTo(0.1).Within(0.0001));
            Assert.That(mechanics.MapSegments[1].MissCount, Is.EqualTo(2));
            Assert.That(mechanics.MapSegments[1].MissRate, Is.EqualTo(0.5).Within(0.0001));
            Assert.That(mechanics.MapSegments[1].SliderBreakCount, Is.EqualTo(1));
            Assert.That(mechanics.WeakestMapSegment, Is.EqualTo("middle third"));
            Assert.That(mechanics.MedianTimingOffsetMilliseconds, Is.EqualTo(5).Within(0.0001));
            Assert.That(mechanics.NinetiethPercentileAbsoluteTimingOffsetMilliseconds, Is.EqualTo(17));
            Assert.That(mechanics.MedianCursorDistancePlayfieldUnits, Is.EqualTo(5).Within(0.0001));
            Assert.That(mechanics.NinetiethPercentileCursorDistancePlayfieldUnits, Is.EqualTo(5).Within(0.0001));
        });
    }

    [Test]
    public void DifficultyFitChoosesHighestSustainableMeasuredBand()
    {
        LocalReplay[] runs =
        {
            run(1, 1, 4.1, 0.98, 0),
            run(2, 2, 4.2, 0.97, 0),
            run(3, 3, 4.4, 0.96, 1),
            run(4, 4, 5.1, 0.95, 1),
            run(5, 5, 5.2, 0.94, 2),
            run(6, 6, 5.4, 0.93, 1),
            run(7, 7, 6.1, 0.88, 5),
            run(8, 8, 6.2, 0.89, 4),
            run(9, 9, 6.4, 0.87, 7),
        };

        CoachingDifficultyFit fit = CoachingPredictionEngine.Build(
            runs,
            new Dictionary<Guid, ReplayAnalysisResult>()).DifficultyFit;

        Assert.Multiple(() =>
        {
            Assert.That(fit.BestFit, Is.Not.Null);
            Assert.That(fit.BestFit!.MinimumStars, Is.EqualTo(5.0));
            Assert.That(fit.BestFit.MaximumStars, Is.EqualTo(5.5));
            Assert.That(fit.BestFit.RunCount, Is.EqualTo(3));
            Assert.That(fit.BestFit.SustainableResultRate, Is.EqualTo(1));
            Assert.That(fit.Confidence, Is.EqualTo(CoachingConfidence.Low));
        });
    }

    [Test]
    public void SessionSignalReportsObservedDriftWithoutClaimingCause()
    {
        LocalReplay[] runs =
        {
            runAt(1, 0, 1, 4.5, 0.97, 0),
            runAt(1, 5, 2, 4.5, 0.96, 1),
            runAt(1, 10, 3, 5.2, 0.91, 4),
            runAt(1, 15, 4, 5.2, 0.90, 5),
            runAt(2, 0, 5, 4.5, 0.96, 0),
            runAt(2, 5, 6, 4.5, 0.95, 1),
            runAt(2, 10, 7, 5.1, 0.90, 4),
            runAt(2, 15, 8, 5.1, 0.89, 5),
        };

        CoachingSessionDrift drift = CoachingPredictionEngine.Build(
            runs,
            new Dictionary<Guid, ReplayAnalysisResult>()).SessionDrift;

        Assert.Multiple(() =>
        {
            Assert.That(drift.SessionCount, Is.EqualTo(2));
            Assert.That(drift.AccuracyChange, Is.LessThan(-0.05));
            Assert.That(drift.MissChange, Is.GreaterThan(3));
            Assert.That(drift.Confidence, Is.EqualTo(CoachingConfidence.Low));
            Assert.That(drift.Summary, Does.Contain("Map order may explain"));
            Assert.That(drift.Summary, Does.Not.Contain("fatigue").IgnoreCase);
        });
    }

    [Test]
    public void ExactAnalysisAddsTimingCursorAndMapSegmentSignals()
    {
        LocalReplay replay = run(1, 1, 5.0, 0.91, 3);
        ReplayAnalysisResult analysis = exactAnalysis(
            judgement("Great", 10, 10_000, new ReplayPoint(100, 100), new ReplayPoint(103, 104)),
            judgement("Great", -10, 20_000, new ReplayPoint(50, 50), new ReplayPoint(50, 50)),
            judgement("LargeTickHit", 80, 50_000, objectType: "SliderTick", maximumResult: "LargeTickHit"),
            judgement("Miss", 0, 81_000),
            judgement("Miss", 0, 88_000),
            judgement("Miss", 0, 90_000));

        CoachingMechanicsProfile mechanics = CoachingPredictionEngine.Build(
            new[] { replay },
            new Dictionary<Guid, ReplayAnalysisResult> { [replay.ScoreId] = analysis }).Mechanics;

        Assert.Multiple(() =>
        {
            Assert.That(mechanics.ExactAnalysisRunCount, Is.EqualTo(1));
            Assert.That(mechanics.TimingSampleCount, Is.EqualTo(2));
            Assert.That(mechanics.MeanTimingOffsetMilliseconds, Is.Zero.Within(0.0001));
            Assert.That(mechanics.TimingStandardDeviationMilliseconds, Is.EqualTo(10).Within(0.0001));
            Assert.That(mechanics.CursorDistanceSampleCount, Is.EqualTo(2));
            Assert.That(mechanics.MeanCursorDistancePlayfieldUnits, Is.EqualTo(2.5).Within(0.0001));
            Assert.That(mechanics.ExactMissCount, Is.EqualTo(3));
            Assert.That(mechanics.WeakestMapSegment, Is.EqualTo("closing third"));
        });
    }

    private static LocalReplay run(
        int day,
        int beatmap,
        double stars,
        double accuracy,
        int misses,
        params string[] mods) => runAt(day, 0, beatmap, stars, accuracy, misses, mods);

    private static LocalReplay runAt(
        int day,
        int minute,
        int beatmap,
        double stars,
        double accuracy,
        int misses,
        params string[] mods) => new(
            id(10_000 + day * 100 + minute + beatmap),
            id(1_000 + beatmap),
            id(beatmap),
            $"Map {beatmap}",
            "Fixture Artist",
            $"{stars:0.0} star",
            "osu",
            "Player",
            new DateTimeOffset(2026, 1, day, 12, minute, 0, TimeSpan.Zero),
            stars,
            accuracy,
            1_000_000,
            500,
            misses,
            200,
            mods,
            true);

    private static Guid id(int value) => new(value, 0, 0, new byte[8]);

    private static ReplayAnalysisResult exactAnalysis(params ReplayObjectJudgement[] judgements) => new(
        ReplayAnalysisProtocol.EngineVersion,
        "officialRulesetPlayback",
        true,
        ReplayAnalysisProtocol.WallClockTimeoutMs,
        Array.Empty<int>(),
        judgements,
        new ReplayJudgementSummary(
            judgements.Count(item => item.Result == "Great"),
            0,
            0,
            judgements.Count(item => item.Result == "Miss"),
            0,
            0));

    private static ReplayObjectJudgement judgement(
        string result,
        double offset,
        double startTime,
        ReplayPoint? objectPosition = null,
        ReplayPoint? cursorPosition = null,
        string objectType = "HitCircle",
        string maximumResult = "Great") => new(
            0,
            null,
            objectType,
            startTime,
            startTime,
            result,
            maximumResult,
            startTime + offset,
            offset,
            1,
            objectPosition,
            cursorPosition,
            0,
            result == "Miss" ? 0 : 1);
}

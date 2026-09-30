using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Tests.Reference;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;
using static AimMod.Desktop.Tests.CoachingFixtures;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class CoachingModelEquivalenceTests
{
    private static readonly int[] seeds = [1, 2, 3, 5, 8];

    [TestCaseSource(nameof(seeds))]
    public void PredictionEngineMatchesReference(int seed)
    {
        (LocalReplay[] runs, Dictionary<Guid, ReplayAnalysisResult> analyses) = History(seed, 420);
        Guid?[] selections = [null, .. runs.Where((_, index) => index % 97 == 0).Select(run => (Guid?)run.ScoreId)];
        CoachingIntelligence global = CoachingPredictionEngine.Build(runs, analyses);
        Assert.Multiple(() =>
        {
            Assert.That(global.Recommendations, Is.Not.Empty);
            Assert.That(global.PpPlan.Opportunities, Is.Not.Empty);
            Assert.That(global.Mechanics.ExactMissCount, Is.GreaterThan(0));
            Assert.That(global.Mechanics.MissReasonCounts, Is.Not.Empty);
        });
        foreach (Guid? selection in selections)
        {
            Assert.That(
                Serialise(CoachingPredictionEngine.Build(runs, analyses, selection)),
                Is.EqualTo(Serialise(ReferenceCoachingPredictionEngine.Build(runs, analyses, selection))),
                $"selection {selection}");
        }
    }

    [TestCaseSource(nameof(seeds))]
    public void PublicPredictionMatchesReferenceForUnsortedInput(int seed)
    {
        (LocalReplay[] runs, _) = History(seed, 300);
        foreach (LocalReplay target in runs.Where((_, index) => index % 23 == 0))
        {
            Assert.That(
                Serialise(CoachingPredictionEngine.Predict(runs, target)),
                Is.EqualTo(Serialise(ReferenceCoachingPredictionEngine.Predict(runs, target))));
        }
    }

    [TestCaseSource(nameof(seeds))]
    public void GlobalProfileMatchesReference(int seed)
    {
        (LocalReplay[] runs, Dictionary<Guid, ReplayAnalysisResult> analyses) = History(seed, 360);
        Assert.That(
            Serialise(GlobalCoachingProfileBuilder.Build(runs, analyses)),
            Is.EqualTo(Serialise(ReferenceGlobalCoachingProfileBuilder.Build(runs, analyses))));
    }

    [TestCaseSource(nameof(seeds))]
    public void ReportsMatchReferenceAndSelectionReusesTheGlobalReport(int seed)
    {
        (LocalReplay[] runs, Dictionary<Guid, ReplayAnalysisResult> analyses) = History(seed, 300);
        CoachingReport global = CoachingReportBuilder.BuildGlobal(runs, analyses);
        Assert.That(Serialise(global), Is.EqualTo(Serialise(ReferenceCoachingReportBuilder.BuildGlobal(runs, analyses))));
        foreach (LocalReplay selected in runs.Where((_, index) => index % 41 == 0))
        {
            string expected = Serialise(ReferenceCoachingReportBuilder.Build(runs, analyses, selected.ScoreId));
            Assert.Multiple(() =>
            {
                Assert.That(Serialise(CoachingReportBuilder.Build(runs, analyses, selected.ScoreId)), Is.EqualTo(expected));
                Assert.That(Serialise(CoachingReportBuilder.BuildForSelection(global, runs, analyses, selected.ScoreId)), Is.EqualTo(expected));
            });
        }
    }

    [TestCaseSource(nameof(seeds))]
    public void CachedWorkspaceModelsMatchReference(int seed)
    {
        (LocalReplay[] runs, Dictionary<Guid, ReplayAnalysisResult> analyses) = History(seed, 360);
        var builder = new CoachingModelBuilder();
        foreach (CoachingTimeRange range in new[] { CoachingTimeRange.All, CoachingTimeRange.Days90 })
        {
            Guid?[] selections = [null, .. runs.Where((_, index) => index % 53 == 0).Select(run => (Guid?)run.ScoreId), null];
            foreach (Guid? selection in selections)
            {
                Assert.That(
                    Serialise(builder.Build(runs, analyses, selection, range, Now)),
                    Is.EqualTo(Serialise(ReferenceCoachingWorkspaceModel.Build(runs, analyses, selection, range, Now))),
                    $"{range} selection {selection}");
            }
        }

        Assert.That(builder.GlobalBuildCount, Is.EqualTo(2));
    }

    [Test]
    public void ProfileBaselineMatchesProfileGain()
    {
        (LocalReplay[] runs, _) = History(13, 500, maps: 150);
        var baseline = new CoachingPpWeighting.ProfileBaseline(runs);
        var random = new Random(4);
        foreach (LocalReplay run in runs.Take(120).Append(runs[0] with { BeatmapId = Guid.NewGuid() }))
        {
            double projected = random.NextDouble() * 500;
            Assert.That(baseline.Gain(run.BeatmapId, projected),
                Is.EqualTo(CoachingPpWeighting.CalculateProfileGain(runs, run.BeatmapId, projected)));
        }
        Assert.That(baseline.Gain(Guid.Empty, 100), Is.Zero);
        Assert.That(baseline.Gain(runs[0].BeatmapId, double.NaN), Is.Zero);
    }

    [Test]
    public void BuilderReusesTheGlobalScopeUntilItsInputsChange()
    {
        (LocalReplay[] runs, Dictionary<Guid, ReplayAnalysisResult> analyses) = History(21, 120);
        var builder = new CoachingModelBuilder();
        NativeCoachingWorkspaceModel global = builder.Build(runs, analyses, null, CoachingTimeRange.All, Now);
        Guid selected = global.History[3].ScoreId;
        NativeCoachingWorkspaceModel first = builder.Build(runs, analyses, selected, CoachingTimeRange.All, Now);

        Assert.Multiple(() =>
        {
            Assert.That(builder.Build(runs, analyses, null, CoachingTimeRange.All, Now), Is.SameAs(global));
            Assert.That(builder.Build(runs, analyses, selected, CoachingTimeRange.All, Now), Is.SameAs(first));
            Assert.That(builder.TryGetCached(runs, analyses, selected, CoachingTimeRange.All, out var cached, Now), Is.True);
            Assert.That(cached, Is.SameAs(first));
            Assert.That(builder.TryGetCached(runs, analyses, Guid.NewGuid(), CoachingTimeRange.All, out var unknown, Now), Is.True);
            Assert.That(unknown, Is.SameAs(global));
            Assert.That(builder.TryGetCached(runs, analyses, global.History[4].ScoreId, CoachingTimeRange.All, out _, Now), Is.False);
            Assert.That(builder.TryGetCached(runs.ToArray(), analyses, null, CoachingTimeRange.All, out _, Now), Is.False);
            Assert.That(builder.TryGetCached(runs, analyses, null, CoachingTimeRange.Days30, out _, Now), Is.False);
            Assert.That(builder.GlobalBuildCount, Is.EqualTo(1));
        });

        analyses[Guid.NewGuid()] = analyses.Values.First();
        Assert.That(builder.Build(runs, analyses, null, CoachingTimeRange.All, Now), Is.Not.SameAs(global));
        Assert.That(builder.GlobalBuildCount, Is.EqualTo(2));
        builder.Invalidate();
        Assert.That(builder.TryGetCached(runs, analyses, null, CoachingTimeRange.All, out _, Now), Is.False);
    }

    [Test]
    public void DigestMatchesDirectJudgementScans()
    {
        ReplayAnalysisResult analysis = Analysis(new Random(9), 400);
        ReplayJudgementDigest digest = ReplayJudgementDigest.For(analysis);
        IReadOnlyList<ReplayObjectJudgement> all = analysis.Judgements;
        Assert.Multiple(() =>
        {
            Assert.That(ReplayJudgementDigest.For(analysis), Is.SameAs(digest));
            Assert.That(digest.JudgementCount, Is.EqualTo(all.Count));
            Assert.That(digest.Misses, Is.EqualTo(all.Where(j => j.Result == "Miss").ToArray()));
            Assert.That(digest.ClassifiedMisses, Is.EqualTo(all.Where(j => j.Result == "Miss" && j.MissAnalysis is { Reason: not ReplayMissReason.Unknown }).ToArray()));
            Assert.That(digest.TimingOffsets, Is.EqualTo(all.Where(j => j.Result != "Miss" && j.MaximumResult == "Great" && double.IsFinite(j.TimeOffsetMs))
                .Select(j => j.TimeOffsetMs).ToArray()));
            Assert.That(digest.TapTimingOffsets, Is.EqualTo(all.Where(j => j.Result != "Miss" && j.MaximumResult == "Great" && double.IsFinite(j.TimeOffsetMs)
                && j.ObjectType.EndsWith("Circle")).Select(j => j.TimeOffsetMs).ToArray()));
            Assert.That(digest.SegmentJudgements.Sum(), Is.EqualTo(all.Count(j => j.NestedPath is null)));
            Assert.That(digest.SegmentSliderBreaks.Sum(), Is.EqualTo(all.Count(j => j.Result is "LargeTickMiss" or "SliderTailMiss")));
        });
    }

    [Test]
    public void ClassifierHandlesCaseAndEmptyStatistics()
    {
        var miss = new ReplayObjectJudgement(0, null, "HitCircle", 0, 0, "miss", "Great", 0, 0, 1, null, null, 0, 0);
        var hit = miss with { Result = "Great", TimeOffsetMs = 4 };
        Assert.Multiple(() =>
        {
            Assert.That(ReplayJudgementClassifier.IsMiss(miss), Is.True);
            Assert.That(ReplayJudgementClassifier.IsTimingSample(miss), Is.False);
            Assert.That(ReplayJudgementClassifier.IsTapTimingSample(hit), Is.True);
            Assert.That(ReplayJudgementClassifier.IsTapTimingSample(hit with { ObjectType = "Slider" }), Is.False);
            Assert.That(ReplayJudgementClassifier.IsTimingSample(hit with { TimeOffsetMs = double.NaN }), Is.False);
            Assert.That(ReplayJudgementClassifier.IsSliderBreak(hit with { Result = "SmallTickMiss" }), Is.True);
            Assert.That(ReplayJudgementClassifier.Median([]), Is.NaN);
            Assert.That(ReplayJudgementClassifier.Median([3, double.NaN, 1, 2, 10]), Is.EqualTo(2.5));
            Assert.That(ReplayJudgementClassifier.NearestRankPercentile([1, 2, 3, 4], 0.9), Is.EqualTo(4));
            Assert.That(ReplayJudgementClassifier.InterpolatedPercentile([], 0.5), Is.Zero);
            Assert.That(ReplayJudgementClassifier.InterpolatedPercentile([0, 10], 0.25), Is.EqualTo(2.5));
            Assert.That(ReplayJudgementClassifier.StandardDeviation([2, 4, double.PositiveInfinity]), Is.EqualTo(1));
        });
    }
}

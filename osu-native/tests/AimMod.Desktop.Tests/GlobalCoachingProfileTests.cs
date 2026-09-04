using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class GlobalCoachingProfileTests
{
    [Test]
    public void AggregatesMechanicsAcrossMapsIntoActionablePriorities()
    {
        LocalReplay first = run(1, 101);
        LocalReplay second = run(2, 202);
        LocalReplay pending = run(3, 303);
        var analyses = new Dictionary<Guid, ReplayAnalysisResult>
        {
            [first.ScoreId] = analysis(
                Enumerable.Repeat(hit(24, 3, 4), 10)
                          .Concat(new[] { miss(ReplayMissReason.Overshoot), miss(ReplayMissReason.Overshoot) })),
            [second.ScoreId] = analysis(
                Enumerable.Repeat(hit(18, 6, 8), 10)
                          .Concat(new[] { miss(ReplayMissReason.Overshoot), miss(ReplayMissReason.Undershoot) })),
            [Guid.NewGuid()] = analysis(new[] { miss(ReplayMissReason.EarlyClick) }),
        };

        GlobalCoachingProfile profile = GlobalCoachingProfileBuilder.Build(
            new[] { first, second, pending },
            analyses);

        Assert.Multiple(() =>
        {
            Assert.That(profile.Coverage.HistoryRunCount, Is.EqualTo(3));
            Assert.That(profile.Coverage.ReplayAvailableRunCount, Is.EqualTo(3));
            Assert.That(profile.Coverage.AnalysedRunCount, Is.EqualTo(2));
            Assert.That(profile.Coverage.AnalysedMapCount, Is.EqualTo(2));
            Assert.That(profile.Coverage.ReplayCoverage, Is.EqualTo(2d / 3).Within(0.001));
            Assert.That(profile.Coverage.Confidence, Is.EqualTo(CoachingConfidence.Low));
            Assert.That(profile.MissReasons[0].Reason, Is.EqualTo(ReplayMissReason.Overshoot));
            Assert.That(profile.MissReasons[0].Count, Is.EqualTo(3));
            Assert.That(profile.MissReasons[0].Share, Is.EqualTo(0.75).Within(0.001));
            Assert.That(profile.MissReasons[0].MapCount, Is.EqualTo(2));
            Assert.That(profile.TimingTendency, Is.EqualTo("Late bias"));
            Assert.That(profile.TimingDetail, Does.Contain("20 taps"));
            Assert.That(profile.AimDetail, Does.Contain("20 samples"));
            Assert.That(profile.RecurringWeaknesses, Has.Some.Matches<GlobalRecurringWeakness>(item =>
                item.Key == "reason:Overshoot" && item.MapCount == 2));
            Assert.That(profile.Priorities[0].Title, Is.EqualTo("Control jump braking"));
            Assert.That(profile.Priorities[0].Value, Is.EqualTo("75% of misses"));
        });
    }

    [Test]
    public void ProducesHonestPartialProfileFromOneCachedReplay()
    {
        LocalReplay analysed = run(1, 101);
        LocalReplay pending = run(2, 202);
        var analyses = new Dictionary<Guid, ReplayAnalysisResult>
        {
            [analysed.ScoreId] = analysis(new[]
            {
                hit(-4, 3, 4),
                miss(ReplayMissReason.EarlyClick),
            }),
        };

        GlobalCoachingProfile profile = GlobalCoachingProfileBuilder.Build(
            new[] { analysed, pending },
            analyses);

        Assert.Multiple(() =>
        {
            Assert.That(profile.Coverage.AnalysedRunCount, Is.EqualTo(1));
            Assert.That(profile.Coverage.ReplayCoverage, Is.EqualTo(0.5));
            Assert.That(profile.Coverage.Confidence, Is.EqualTo(CoachingConfidence.Insufficient));
            Assert.That(profile.MissReasons, Has.Count.EqualTo(1));
            Assert.That(profile.TimingTendency, Is.EqualTo("Centred"));
            Assert.That(profile.AimTendency, Is.Not.EqualTo("Not measured"));
            Assert.That(profile.Priorities, Is.Not.Empty);
            Assert.That(profile.Priorities[0].Confidence, Is.EqualTo(CoachingConfidence.Insufficient));
        });
    }

    [Test]
    public void WorkspaceModelRefreshesProfileWhenAnalysisCacheGrows()
    {
        LocalReplay first = run(1, 101);
        LocalReplay second = run(2, 202);
        var analyses = new Dictionary<Guid, ReplayAnalysisResult>
        {
            [first.ScoreId] = analysis(new[] { hit(0, 0, 0) }),
        };

        NativeCoachingWorkspaceModel partial = NativeCoachingWorkspaceModel.Build(new[] { first, second }, analyses);
        analyses[second.ScoreId] = analysis(new[] { hit(2, 0, 0) });
        NativeCoachingWorkspaceModel updated = NativeCoachingWorkspaceModel.Build(new[] { first, second }, analyses);

        Assert.Multiple(() =>
        {
            Assert.That(partial.GlobalProfile.Coverage.AnalysedRunCount, Is.EqualTo(1));
            Assert.That(updated.GlobalProfile.Coverage.AnalysedRunCount, Is.EqualTo(2));
            Assert.That(updated.GlobalProfile.Coverage.ReplayCoverage, Is.EqualTo(1));
        });
    }

    [Test]
    public void ReportsClassifiedCoverageAndGroupsRelatedMissesBySkillArea()
    {
        LocalReplay first = run(1, 101);
        LocalReplay second = run(2, 202);
        var analyses = new Dictionary<Guid, ReplayAnalysisResult>
        {
            [first.ScoreId] = analysis(new[]
            {
                miss(ReplayMissReason.Overshoot),
                miss(ReplayMissReason.Unknown),
            }),
            [second.ScoreId] = analysis(new[] { miss(ReplayMissReason.Undershoot) }),
        };

        GlobalCoachingProfile profile = GlobalCoachingProfileBuilder.Build(new[] { first, second }, analyses);
        GlobalSkillAreaEvidence aimControl = profile.MeasuredSkillAreas.Single(area => area.Area == CoachingSkillArea.AimControl);

        Assert.Multiple(() =>
        {
            Assert.That(profile.Coverage.MissCount, Is.EqualTo(3));
            Assert.That(profile.Coverage.ClassifiedMissCount, Is.EqualTo(2));
            Assert.That(profile.Coverage.MissClassificationCoverage, Is.EqualTo(2d / 3).Within(0.001));
            Assert.That(aimControl.EvidenceCount, Is.EqualTo(2));
            Assert.That(aimControl.RunCount, Is.EqualTo(2));
            Assert.That(aimControl.MapCount, Is.EqualTo(2));
            Assert.That(aimControl.ShareOfClassifiedMisses, Is.EqualTo(1));
            Assert.That(aimControl.AnalysedMapCoverage, Is.EqualTo(1));
            Assert.That(aimControl.Confidence, Is.EqualTo(CoachingConfidence.Low));
        });
    }

    [Test]
    public void SameMapWeaknessRequiresEvidenceFromMultipleAttempts()
    {
        LocalReplay first = run(1, 101);
        LocalReplay second = run(2, 101);
        var analyses = new Dictionary<Guid, ReplayAnalysisResult>
        {
            [first.ScoreId] = analysis(new[]
            {
                miss(ReplayMissReason.Overshoot),
                miss(ReplayMissReason.Overshoot),
            }),
            [second.ScoreId] = analysis(new[] { hit(0, 0, 0) }),
        };

        GlobalCoachingProfile profile = GlobalCoachingProfileBuilder.Build(new[] { first, second }, analyses);

        Assert.That(profile.RecurringWeaknesses, Has.None.Matches<GlobalRecurringWeakness>(weakness =>
            weakness.Key.StartsWith("map:", StringComparison.Ordinal)));
    }

    [Test]
    public void CrossMapRecurrenceOutranksAHighCountFromOnePlay()
    {
        LocalReplay isolated = run(1, 101);
        LocalReplay recurringA = run(2, 202);
        LocalReplay recurringB = run(3, 303);
        var analyses = new Dictionary<Guid, ReplayAnalysisResult>
        {
            [isolated.ScoreId] = analysis(Enumerable.Repeat(miss(ReplayMissReason.EarlyClick), 8)),
            [recurringA.ScoreId] = analysis(new[] { miss(ReplayMissReason.Overshoot) }),
            [recurringB.ScoreId] = analysis(new[] { miss(ReplayMissReason.Overshoot) }),
        };

        GlobalCoachingProfile profile = GlobalCoachingProfileBuilder.Build(
            new[] { isolated, recurringA, recurringB },
            analyses);

        Assert.Multiple(() =>
        {
            Assert.That(profile.MissReasons[0].Reason, Is.EqualTo(ReplayMissReason.Overshoot));
            Assert.That(profile.Priorities[0].Title, Is.EqualTo("Control jump braking"));
            Assert.That(profile.Priorities[0].Confidence, Is.EqualTo(CoachingConfidence.Low));
        });
    }

    private static ReplayAnalysisResult analysis(IEnumerable<ReplayObjectJudgement> judgements)
    {
        ReplayObjectJudgement[] values = judgements.ToArray();
        return new ReplayAnalysisResult(
            ReplayAnalysisProtocol.EngineVersion,
            "officialRulesetPlayback",
            true,
            ReplayAnalysisProtocol.WallClockTimeoutMs,
            Array.Empty<int>(),
            values,
            new ReplayJudgementSummary(
                values.Count(item => item.Result == "Great"),
                0,
                0,
                values.Count(item => item.Result == "Miss"),
                0,
                0));
    }

    private static ReplayObjectJudgement hit(double offset, float cursorX, float cursorY) => new(
        1, null, "HitCircle", 1_000, 1_000, "Great", "Great", 1_000 + offset, offset, 1,
        new ReplayPoint(0, 0), new ReplayPoint(cursorX, cursorY), 10, 11);

    private static ReplayObjectJudgement miss(ReplayMissReason reason) => new(
        2, null, "HitCircle", 2_000, 2_000, "Miss", "Great", 2_100, 100, 1,
        new ReplayPoint(256, 192), new ReplayPoint(300, 192), 20, 0,
        new ReplayMissAnalysis(
            reason, 32, 40, 10, new ReplayPoint(290, 192), 10, 40,
            new ReplayPoint(296, 192), 45, true, false, true, 0.5, Confidence: 0.9));

    private static LocalReplay run(int day, int map) => new(
        Guid.NewGuid(),
        id(map),
        id(map + 10_000),
        $"Map {map}",
        "Fixture Artist",
        "Insane",
        "osu",
        "Player",
        new DateTimeOffset(2026, 9, day, 12, 0, 0, TimeSpan.Zero),
        5.4,
        0.95,
        1_000_000,
        500,
        1,
        220,
        Array.Empty<string>(),
        true);

    private static Guid id(int value) => new(value, 0, 0, new byte[8]);
}

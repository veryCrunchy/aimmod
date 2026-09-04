using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class NativeCoachingWorkspaceTests
{
    [Test]
    public void ConstructsWithoutConflictingLayoutAxes()
    {
        var source = new InMemoryLocalLibrarySource(Array.Empty<LocalBeatmapSet>(), Array.Empty<LocalReplay>());

        Assert.DoesNotThrow(() => _ = new NativeCoachingWorkspace(
            source,
            new Dictionary<Guid, ReplayAnalysisResult>(),
            _ => { }));
    }

    [Test]
    public void SelectsTheSessionContainingTheChosenRun()
    {
        DateTimeOffset start = new(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);
        LocalReplay previousSession = run(start.AddHours(-3), 0.91, 3);
        LocalReplay selected = run(start, 0.94, 1);
        LocalReplay sameSession = run(start.AddMinutes(20), 0.96, 0);
        LocalReplay laterSession = run(start.AddHours(2), 0.93, 2);

        NativeCoachingWorkspaceModel model = NativeCoachingWorkspaceModel.Build(
            new[] { laterSession, sameSession, selected, previousSession },
            new Dictionary<Guid, ReplayAnalysisResult>(),
            selected.ScoreId);

        Assert.Multiple(() =>
        {
            Assert.That(model.SelectedRun?.ScoreId, Is.EqualTo(selected.ScoreId));
            Assert.That(model.SessionRuns.Select(item => item.ScoreId), Is.EqualTo(new[] { selected.ScoreId, sameSession.ScoreId }));
            Assert.That(model.Session?.PlayCount, Is.EqualTo(2));
            Assert.That(model.Session?.Duration, Is.EqualTo(TimeSpan.FromMinutes(20)));
            Assert.That(model.Session?.MedianAccuracy, Is.EqualTo(0.95).Within(0.0001));
        });
    }

    [Test]
    public void BoundsHistoryAndTrendSeries()
    {
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        LocalReplay[] runs = Enumerable.Range(0, CoachingLimits.MaximumRuns + 20)
                                       .Select(index => run(start.AddMinutes(index), 0.9 + index % 10 / 100.0, index % 3))
                                       .ToArray();

        NativeCoachingWorkspaceModel model = NativeCoachingWorkspaceModel.Build(
            runs,
            new Dictionary<Guid, ReplayAnalysisResult>());

        Assert.Multiple(() =>
        {
            Assert.That(model.History, Has.Count.EqualTo(CoachingLimits.MaximumRuns));
            Assert.That(model.TrendRuns, Has.Count.EqualTo(NativeCoachingWorkspaceModel.MaximumTrendRuns));
            Assert.That(model.TrendRuns, Is.Ordered.By(nameof(LocalReplay.PlayedAt)));
            Assert.That(model.SelectedRun, Is.Null);
            Assert.That(model.Report.SelectedRun, Is.Null);
            Assert.That(model.Report.Intelligence.SelectedRunPrediction, Is.Null);
        });
    }

    [Test]
    public void GlobalDefaultAggregatesMergedHistoryAndMatchingExactAnalyses()
    {
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        LocalReplay localOnly = run(start, 0.91, 3);
        LocalReplay submittedLocal = run(start.AddDays(1), 0.95, 1) with { OnlineScoreId = 42 };
        LocalReplay onlineOnly = run(start.AddDays(2), 0.97, 0) with
        {
            IsLocallyStored = false,
            OnlineScoreId = 43,
            HasReplayFile = false,
        };
        var analyses = new Dictionary<Guid, ReplayAnalysisResult>
        {
            [localOnly.ScoreId] = exactAnalysis(),
            [Guid.NewGuid()] = exactAnalysis(),
        };

        NativeCoachingWorkspaceModel model = NativeCoachingWorkspaceModel.Build(
            new[] { onlineOnly, localOnly, submittedLocal },
            analyses);

        Assert.Multiple(() =>
        {
            Assert.That(model.SelectedRun, Is.Null);
            Assert.That(model.Session, Is.Null);
            Assert.That(model.Global.RunCount, Is.EqualTo(3));
            Assert.That(model.Global.LocalRunCount, Is.EqualTo(2));
            Assert.That(model.Global.SubmittedRunCount, Is.EqualTo(2));
            Assert.That(model.Global.DistinctBeatmapCount, Is.EqualTo(3));
            Assert.That(model.Global.ExactAnalysisRunCount, Is.EqualTo(1));
            Assert.That(model.Global.FirstPlayAt, Is.EqualTo(start));
            Assert.That(model.Global.LastPlayAt, Is.EqualTo(start.AddDays(2)));
            Assert.That(model.Global.MedianAccuracy, Is.EqualTo(0.95).Within(0.0001));
            Assert.That(model.Report.Intelligence.History.RunCount, Is.EqualTo(3));
            Assert.That(model.Report.Intelligence.Mechanics.ExactAnalysisRunCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void LeavesAnHonestEmptyWorkspace()
    {
        NativeCoachingWorkspaceModel model = NativeCoachingWorkspaceModel.Build(
            Array.Empty<LocalReplay>(),
            new Dictionary<Guid, ReplayAnalysisResult>());

        Assert.Multiple(() =>
        {
            Assert.That(model.SelectedRun, Is.Null);
            Assert.That(model.Session, Is.Null);
            Assert.That(model.SessionRuns, Is.Empty);
            Assert.That(model.TrendRuns, Is.Empty);
            Assert.That(model.Global.RunCount, Is.Zero);
            Assert.That(model.Global.MedianAccuracy, Is.Null);
            Assert.That(model.Report.Intelligence.Recommendations, Is.Empty);
        });
    }

    [Test]
    public void GlobalMechanicsInsightSurfacesDominantMissCause()
    {
        var mechanics = new CoachingMechanicsProfile(
            3, 120, 80, 2, 14, 70, 6, 5, null, 1, 20, 4, 12, Array.Empty<CoachingMapSegment>(),
            new Dictionary<ReplayMissReason, int>
            {
                [ReplayMissReason.Overshoot] = 4,
                [ReplayMissReason.EarlyClick] = 1,
            },
            ReplayMissReason.Overshoot);

        Assert.Multiple(() =>
        {
            Assert.That(NativeCoachingWorkspace.MechanicsValue(mechanics), Is.EqualTo("overshoots"));
            Assert.That(NativeCoachingWorkspace.MechanicsDetail(mechanics), Does.Contain("most common cause overshoots (4)"));
        });
    }

    [Test]
    public void MechanicsProfileKeepsLegacyPositionalConstructionValid()
    {
        var mechanics = new CoachingMechanicsProfile(
            0, 0, 0, null, null, 0, null, 0, null, null, null, null, null, Array.Empty<CoachingMapSegment>());

        Assert.Multiple(() =>
        {
            Assert.That(mechanics.MissReasonCounts, Is.Null);
            Assert.That(mechanics.DominantMissReason, Is.Null);
        });
    }

    private static ReplayAnalysisResult exactAnalysis() => new(
        ReplayAnalysisProtocol.EngineVersion,
        "officialRulesetPlayback",
        true,
        ReplayAnalysisProtocol.WallClockTimeoutMs,
        Array.Empty<int>(),
        Array.Empty<ReplayObjectJudgement>(),
        new ReplayJudgementSummary(0, 0, 0, 0, 0, 0));

    private static LocalReplay run(DateTimeOffset playedAt, double accuracy, int misses) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        $"Map {playedAt:HHmm}",
        "Fixture Artist",
        "Insane",
        "osu",
        "Player",
        playedAt,
        5.2,
        accuracy,
        1_000_000,
        500,
        misses,
        200,
        Array.Empty<string>(),
        true);
}

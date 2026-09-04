using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Practice;
using AimMod.Osu.Runtime;
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
            Assert.That(model.GlobalProfile.Coverage.AnalysedRunCount, Is.EqualTo(1));
            Assert.That(model.GlobalProfile.Coverage.HistoryRunCount, Is.EqualTo(3));
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
            Assert.That(model.GlobalProfile, Is.EqualTo(GlobalCoachingProfile.Empty));
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

    [Test]
    public void GlobalProfilePresentationShowsCoverageAndLeadingEvidence()
    {
        var profile = GlobalCoachingProfile.Empty with
        {
            Coverage = new GlobalCoachingCoverage(10, 8, 4, 7, 3, 500, CoachingConfidence.Low),
            MissReasons = new[]
            {
                new GlobalMissReasonShare(ReplayMissReason.Overshoot, 6, 0.6, 3, 2),
                new GlobalMissReasonShare(ReplayMissReason.EarlyClick, 4, 0.4, 2, 2),
            },
            TimingTendency = "Late bias",
            AimTendency = "overshoots",
        };

        Assert.Multiple(() =>
        {
            Assert.That(NativeCoachingWorkspace.ProfileCoverageValue(profile), Is.EqualTo("50%"));
            Assert.That(NativeCoachingWorkspace.ProfileEvidenceSummary(profile), Is.EqualTo("overshoots 60%  /  early clicks 40%"));
            Assert.That(NativeCoachingWorkspace.ProfileTendencySummary(profile), Is.EqualTo("Timing: Late bias  /  Aim: overshoots"));
        });
    }

    [Test]
    public void ProgressCopyKeepsCachedAndFailureCountsVisible()
    {
        Assert.Multiple(() =>
        {
            Assert.That(NativeCoachingWorkspace.AnalysisProgressDetail(3, 7, 12), Is.EqualTo("3 of 7 in this pass  //  12 already analysed"));
            Assert.That(NativeCoachingWorkspace.AnalysisCompletionDetail(15, 2), Is.EqualTo("15 analysed  //  2 could not be read"));
            Assert.That(NativeCoachingWorkspace.ConfidenceLabel(CoachingConfidence.Low), Is.EqualTo("Low"));
        });
    }

    [Test]
    public void PracticeCandidateCopyIncludesDecisionEvidence()
    {
        LocalReplay source = run(new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero), 0.94, 3) with
        {
            StarRating = 6.27,
            Difficulty = "Expert",
        };
        var candidate = new PracticeMapCandidate(source, new[] { source.ScoreId }, 4, 7, 12.5);

        Assert.Multiple(() =>
        {
            Assert.That(NativeCoachingWorkspace.PracticeEvidenceSummary(candidate), Is.EqualTo("6.27*  //  7 exact misses  //  4 analysed attempts"));
            Assert.That(NativeCoachingWorkspace.PracticeSourceSummary(candidate), Does.Contain("Source difficulty: Expert"));
            Assert.That(NativeCoachingWorkspace.PracticeSourceSummary(candidate), Does.Contain("last played Sep 3, 2026"));
        });
    }

    [Test]
    public void PracticeCandidateHeaderReportsTheAvailablePool()
    {
        Assert.Multiple(() =>
        {
            Assert.That(NativeCoachingWorkspace.PracticeCandidateDetail(new PracticeCandidatePage([], 0, 0)), Is.EqualTo("No maps ready"));
            Assert.That(NativeCoachingWorkspace.PracticeCandidateDetail(new PracticeCandidatePage([], 0, 37)), Is.EqualTo("0 of 37 maps"));
            Assert.That(NativeCoachingWorkspace.PracticeCandidateDetail(new PracticeCandidatePage([null!], 1, 1)), Is.EqualTo("1 practice map ready"));
            Assert.That(NativeCoachingWorkspace.PracticeCandidateDetail(new PracticeCandidatePage(new PracticeMapCandidate[37], 37, 37)), Is.EqualTo("37 practice maps ready"));
            Assert.That(NativeCoachingWorkspace.PracticeCandidateDetail(new PracticeCandidatePage(new PracticeMapCandidate[100], 140, 140)), Is.EqualTo("Top 100 of 140 maps"));
        });
    }

    [Test]
    public void PracticeFiltersUseReadableLabels()
    {
        Assert.Multiple(() =>
        {
            Assert.That(NativeCoachingWorkspace.PracticeSortLabel(PracticeCandidateSort.WeakestFirst), Is.EqualTo("Weakest first"));
            Assert.That(NativeCoachingWorkspace.PracticeSortLabel(PracticeCandidateSort.MostRepeated), Is.EqualTo("Most repeated"));
            Assert.That(NativeCoachingWorkspace.PracticeEvidenceLabel(PracticeEvidenceFilter.AnyEvidence), Is.EqualTo("Any evidence"));
            Assert.That(NativeCoachingWorkspace.PracticeEvidenceLabel(PracticeEvidenceFilter.RepeatedAcrossAttempts), Is.EqualTo("Repeated misses"));
        });
    }

    [Test]
    public void PracticeCandidatePoolBuildsOnceUntilInvalidated()
    {
        int builds = 0;
        IReadOnlyList<PracticeMapCandidate> expected = Array.Empty<PracticeMapCandidate>();
        var cache = new PracticeCandidatePoolCache(500, (_, _, limit) =>
        {
            builds++;
            Assert.That(limit, Is.EqualTo(500));
            return expected;
        });
        var replays = Array.Empty<LocalReplay>();
        var analyses = new Dictionary<Guid, ReplayAnalysisResult>();

        IReadOnlyList<PracticeMapCandidate> first = cache.Get(replays, analyses);
        IReadOnlyList<PracticeMapCandidate> second = cache.Get(replays, analyses);
        cache.Invalidate();
        IReadOnlyList<PracticeMapCandidate> third = cache.Get(replays, analyses);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.SameAs(expected));
            Assert.That(second, Is.SameAs(expected));
            Assert.That(third, Is.SameAs(expected));
            Assert.That(builds, Is.EqualTo(2));
        });
    }

    [Test]
    public void PracticePageComparisonSkipsOnlyEquivalentRows()
    {
        LocalReplay source = run(new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero), 0.94, 3);
        var candidate = new PracticeMapCandidate(source, new[] { source.ScoreId }, 2, 4, 5.5, 2, 0.75);
        var equivalent = candidate with { AnalysisScoreIds = candidate.AnalysisScoreIds.ToArray() };
        var changedAnalysisIds = candidate with { AnalysisScoreIds = new[] { Guid.NewGuid() } };
        var changedEvidence = candidate with { MissCount = 5 };

        Assert.Multiple(() =>
        {
            Assert.That(NativeCoachingWorkspace.SamePracticeCandidatePage(null, new PracticeCandidatePage([candidate], 1, 1)), Is.False);
            Assert.That(NativeCoachingWorkspace.SamePracticeCandidatePage(
                new PracticeCandidatePage([candidate], 1, 1),
                new PracticeCandidatePage([equivalent], 1, 1)), Is.True);
            Assert.That(NativeCoachingWorkspace.SamePracticeCandidatePage(
                new PracticeCandidatePage([candidate], 1, 1),
                new PracticeCandidatePage([changedAnalysisIds], 1, 1)), Is.False);
            Assert.That(NativeCoachingWorkspace.SamePracticeCandidatePage(
                new PracticeCandidatePage([candidate], 1, 1),
                new PracticeCandidatePage([changedEvidence], 1, 1)), Is.False);
            Assert.That(NativeCoachingWorkspace.SamePracticeCandidatePage(
                new PracticeCandidatePage([candidate], 1, 1),
                new PracticeCandidatePage([candidate], 2, 2)), Is.False);
        });
    }

    [Test]
    public void PracticeStarFilterUsesAShortDebounce()
    {
        Assert.That(NativeCoachingWorkspace.PracticeFilterDebounceMilliseconds, Is.InRange(100, 250));
    }

    [Test]
    public void PracticeLaunchCopyDistinguishesSuccessfulHandoffFromManualFallback()
    {
        Assert.Multiple(() =>
        {
            Assert.That(NativeCoachingWorkspace.PracticeLaunchSucceeded(LazerBeatmapInstallStatus.Sent), Is.True);
            Assert.That(NativeCoachingWorkspace.PracticeLaunchSucceeded(LazerBeatmapInstallStatus.LazerStarted), Is.True);
            Assert.That(NativeCoachingWorkspace.PracticeLaunchSucceeded(LazerBeatmapInstallStatus.LaunchFailed), Is.False);
            Assert.That(NativeCoachingWorkspace.PracticeLaunchMessage(LazerBeatmapInstallStatus.LazerStarted), Does.Contain("importing"));
            Assert.That(NativeCoachingWorkspace.PracticeLaunchMessage(LazerBeatmapInstallStatus.LazerNotFound), Does.Contain("export folder"));
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

using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class CoachingDomainTests
{
    [Test]
    public void SearchesRecentRunsWithReplayAndPagingFilters()
    {
        LocalReplay[] runs = new[]
        {
            run(1, "Warmup", 0.91, 3, false, "NoMod"),
            run(2, "Streams", 0.97, 0, true, "Hidden"),
            run(3, "Jumps", 0.95, 1, true, "HardRock"),
        };

        CoachingRunPage page = CoachingRunSearch.Search(runs, new CoachingRunQuery(
            SearchText: "artist hidden",
            RequireReplayFile: true,
            Sort: CoachingRunSort.Accuracy,
            Limit: 1));

        Assert.Multiple(() =>
        {
            Assert.That(page.Total, Is.EqualTo(1));
            Assert.That(page.Items, Has.Count.EqualTo(1));
            Assert.That(page.Items[0].Title, Is.EqualTo("Streams"));
            Assert.That(page.Items[0].CanAnalyse, Is.True);
            Assert.That(page.HasMore, Is.False);
        });
    }

    [Test]
    public void BuildsAggregateTrendsAndChartReadySeries()
    {
        LocalReplay[] runs = new[]
        {
            run(4, "Fourth", 0.96, 0, true),
            run(1, "First", 0.90, 3, true),
            run(3, "Third", 0.94, 1, true),
            run(2, "Second", 0.92, 2, true),
        };
        var analyses = new Dictionary<Guid, ReplayAnalysisResult>
        {
            [runs[0].ScoreId] = analysis(
                new ReplayJudgementSummary(2, 0, 0, 0, 0, 0),
                judgement("Great", 20),
                judgement("Great", 30)),
            [runs[2].ScoreId] = analysis(
                new ReplayJudgementSummary(1, 0, 0, 1, 1, 0),
                judgement("Great", -5),
                judgement("Great", 5),
                judgement("Miss", 0, 42_100)),
        };

        CoachingReport report = CoachingReportBuilder.Build(runs, analyses);

        Assert.Multiple(() =>
        {
            Assert.That(report.Accuracy.RunCount, Is.EqualTo(4));
            Assert.That(report.Accuracy.Average, Is.EqualTo(0.93).Within(0.0001));
            Assert.That(report.Accuracy.RecentChange, Is.EqualTo(0.04).Within(0.0001));
            Assert.That(report.Misses.Total, Is.EqualTo(6));
            Assert.That(report.Misses.AnalysedRunCount, Is.EqualTo(2));
            Assert.That(report.Misses.AnalysedObjectMisses, Is.EqualTo(1));
            Assert.That(report.Misses.AnalysedSliderBreaks, Is.EqualTo(1));
            Assert.That(report.Timing.SampleCount, Is.EqualTo(4));
            Assert.That(report.Timing.MeanOffsetMilliseconds, Is.EqualTo(12.5).Within(0.0001));
            Assert.That(report.Timing.CentredCount, Is.EqualTo(2));
            Assert.That(report.Timing.LateCount, Is.EqualTo(2));
            Assert.That(report.Series.Single(series => series.Key == "accuracy").Points.Select(point => point.Value),
                Is.EqualTo(new[] { 90, 92, 94, 96 }));
            Assert.That(report.Series.Single(series => series.Key == "playCount").Points.Select(point => point.Value),
                Is.EqualTo(new[] { 1, 2, 3, 4 }));
            Assert.That(report.Series.Single(series => series.Key == "cumulativeScore").Points.Last().Value,
                Is.EqualTo(runs.Sum(run => run.TotalScore)));
            Assert.That(report.Series.Single(series => series.Key == "timingOffset").Points, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public void GivesAdviceAtTheFirstMeasuredMissInTheSelectedRun()
    {
        LocalReplay selected = run(1, "Streams", 0.94, 1, true);
        ReplayAnalysisResult result = analysis(
            new ReplayJudgementSummary(20, 0, 0, 1, 0, 0),
            judgement("Great", 5, 1_000),
            judgement("Miss", 0, 42_100),
            judgement("Miss", 0, 60_000));

        CoachingReport report = CoachingReportBuilder.Build(
            new[] { selected },
            new Dictionary<Guid, ReplayAnalysisResult> { [selected.ScoreId] = result },
            selected.ScoreId);

        Assert.Multiple(() =>
        {
            Assert.That(report.NextPlay.ScoreId, Is.EqualTo(selected.ScoreId));
            Assert.That(report.NextPlay.ReviewTimeMilliseconds, Is.EqualTo(42_100));
            Assert.That(report.NextPlay.Detail, Does.Contain("0:42.100"));
            Assert.That(report.NextPlay.Detail, Does.Contain("at most 0 misses"));
            Assert.That(report.NextPlay.Detail, Does.Contain("at least 94.0% accuracy"));
            Assert.That(report.NextPlay.Detail, Does.Not.Contain("engine").IgnoreCase);
            Assert.That(report.NextPlay.Detail, Does.Not.Contain("source").IgnoreCase);
        });
    }

    [Test]
    public void LeavesTimingEmptyWhenNoAnalysisIsAvailable()
    {
        LocalReplay selected = run(1, "Jumps", 0.91, 2, true);

        CoachingReport report = CoachingReportBuilder.Build(
            new[] { selected },
            new Dictionary<Guid, ReplayAnalysisResult>());

        Assert.Multiple(() =>
        {
            Assert.That(report.Timing.SampleCount, Is.Zero);
            Assert.That(report.Timing.MeanOffsetMilliseconds, Is.Null);
            Assert.That(report.Series.Single(series => series.Key == "timingOffset").Points, Is.Empty);
            Assert.That(report.NextPlay.Detail, Does.Contain("2 misses"));
            Assert.That(report.NextPlay.ReviewTimeMilliseconds, Is.Null);
        });
    }

    [Test]
    public void LinksSliderBreakAdviceToTheFirstMeasuredBreak()
    {
        LocalReplay selected = run(1, "Sliders", 0.96, 0, true);
        ReplayAnalysisResult result = analysis(
            new ReplayJudgementSummary(20, 0, 0, 0, 2, 0),
            judgement("Great", 0, 1_000),
            judgement("SliderTailMiss", 0, 31_250),
            judgement("SliderTailMiss", 0, 48_000));

        CoachingReport report = CoachingReportBuilder.Build(
            new[] { selected },
            new Dictionary<Guid, ReplayAnalysisResult> { [selected.ScoreId] = result },
            selected.ScoreId);

        Assert.Multiple(() =>
        {
            Assert.That(report.NextPlay.ReviewTimeMilliseconds, Is.EqualTo(31_250));
            Assert.That(report.NextPlay.Detail, Does.Contain("0:31.250"));
            Assert.That(report.NextPlay.Detail, Does.Contain("at most 1 slider breaks"));
        });
    }

    [Test]
    public void NextPlayUsesTheEarlierMatchingSetupAsItsTarget()
    {
        Guid beatmapId = Guid.NewGuid();
        LocalReplay earlier = run(1, "Same map", 0.97, 0, true, "Hidden") with { BeatmapId = beatmapId };
        LocalReplay selected = run(2, "Same map", 0.92, 4, true, "hidden") with { BeatmapId = beatmapId };

        CoachingReport report = CoachingReportBuilder.Build(
            new[] { earlier, selected },
            new Dictionary<Guid, ReplayAnalysisResult>(),
            selected.ScoreId);

        Assert.Multiple(() =>
        {
            Assert.That(report.Intelligence.SelectedRunBenchmark, Is.Not.Null);
            Assert.That(report.Intelligence.SelectedRunBenchmark!.BestPriorAccuracy, Is.EqualTo(0.97));
            Assert.That(report.NextPlay.Detail, Does.Contain("at most 0 misses"));
            Assert.That(report.NextPlay.Detail, Does.Contain("at least 97.0% accuracy"));
        });
    }

    [Test]
    public void TimingAdviceRequestsARepeatBeforeAnOffsetChange()
    {
        LocalReplay selected = run(1, "Timing", 0.98, 0, true);
        ReplayObjectJudgement[] hits = Enumerable.Range(0, 12)
                                                 .Select(index => judgement("Great", 18, 1_000 + index * 500))
                                                 .ToArray();
        ReplayAnalysisResult result = analysis(new ReplayJudgementSummary(12, 0, 0, 0, 0, 0), hits);

        CoachingReport report = CoachingReportBuilder.Build(
            new[] { selected },
            new Dictionary<Guid, ReplayAnalysisResult> { [selected.ScoreId] = result },
            selected.ScoreId);

        Assert.Multiple(() =>
        {
            Assert.That(report.NextPlay.Detail, Does.Contain("18 ms late"));
            Assert.That(report.NextPlay.Detail, Does.Contain("before changing an offset"));
            Assert.That(report.NextPlay.Detail, Does.Not.Contain("tap earlier").IgnoreCase);
        });
    }

    [Test]
    public void ReturnsAnHonestEmptyReport()
    {
        CoachingReport report = CoachingReportBuilder.Build(
            Array.Empty<LocalReplay>(),
            new Dictionary<Guid, ReplayAnalysisResult>());

        Assert.Multiple(() =>
        {
            Assert.That(report.SelectedRun, Is.Null);
            Assert.That(report.Accuracy.Average, Is.Null);
            Assert.That(report.Misses.Average, Is.Null);
            Assert.That(report.Timing.MeanOffsetMilliseconds, Is.Null);
            Assert.That(report.NextPlay.ScoreId, Is.Null);
            Assert.That(report.NextPlay.Detail, Does.Contain("saved replay"));
        });
    }

    private static LocalReplay run(
        int day,
        string title,
        double accuracy,
        int misses,
        bool hasReplay,
        params string[] mods) => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            title,
            "Fixture Artist",
            "Hard",
            "osu",
            "Player",
            new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero),
            5.2,
            accuracy,
            1_000_000 + day,
            500,
            misses,
            200 + day,
            mods,
            hasReplay);

    private static ReplayAnalysisResult analysis(
        ReplayJudgementSummary summary,
        params ReplayObjectJudgement[] judgements) => new(
            ReplayAnalysisProtocol.EngineVersion,
            "officialRulesetPlayback",
            true,
            ReplayAnalysisProtocol.WallClockTimeoutMs,
            Array.Empty<int>(),
            judgements,
            summary);

    private static ReplayObjectJudgement judgement(
        string result,
        double offset,
        double startTime = 10_000) => new(
            0,
            null,
            "HitCircle",
            startTime,
            startTime,
            result,
            "Great",
            startTime + offset,
            offset,
            1,
            null,
            null,
            0,
            result == "Miss" ? 0 : 1);
}

using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class StatisticsInsightsTests
{
    private static readonly DateTimeOffset now = new(2026, 9, 30, 20, 0, 0, TimeSpan.Zero);

    [Test]
    public void BoundedPeriodComparesAgainstTheEqualWindowBefore()
    {
        LocalReplay[] runs =
        [
            run(now.AddDays(-5), 0.95, 1, 120),
            run(now.AddDays(-10), 0.93, 3, 100),
            run(now.AddDays(-40), 0.90, 6, 80),
            run(now.AddDays(-50), 0.88, 8, null),
        ];

        StatisticsWorkspaceModel model = StatisticsWorkspaceModel.Build(runs, new StatisticsRunQuery(TimeRange: StatisticsTimeRange.Days30), now);
        StatisticsMetricView accuracy = model.Insights.For(StatisticsMetric.Accuracy);
        StatisticsMetricView misses = model.Insights.For(StatisticsMetric.Misses);
        StatisticsMetricView pp = model.Insights.For(StatisticsMetric.Performance);

        Assert.Multiple(() =>
        {
            Assert.That(model.Runs, Has.Count.EqualTo(2));
            Assert.That(accuracy.Headline, Is.EqualTo(94).Within(0.001));
            Assert.That(accuracy.Change, Is.EqualTo(94 - 89).Within(0.001), "Previous 30 days average 89%.");
            Assert.That(accuracy.ComparisonLabel, Is.EqualTo("vs prev 30d"));
            Assert.That(misses.Change, Is.EqualTo(2 - 7).Within(0.001));
            Assert.That(misses.HigherIsBetter, Is.False);
            Assert.That(pp.Headline, Is.EqualTo(110), "PP headline is the median.");
            Assert.That(pp.Change, Is.EqualTo(110 - 80).Within(0.001), "Plays without PP are ignored, not counted as zero.");
        });
    }

    [Test]
    public void AllTimeComparesTheLatestThirtyDaysWithTheThirtyBefore()
    {
        LocalReplay[] runs =
        [
            run(now.AddDays(-2), 0.96, 0, 100),
            run(now.AddDays(-45), 0.90, 0, 100),
            run(now.AddDays(-200), 0.50, 0, 100),
        ];

        StatisticsMetricView accuracy = StatisticsWorkspaceModel.Build(runs, new StatisticsRunQuery(), now).Insights.For(StatisticsMetric.Accuracy);

        Assert.Multiple(() =>
        {
            Assert.That(accuracy.Headline, Is.EqualTo((96 + 90 + 50) / 3d).Within(0.001));
            Assert.That(accuracy.Change, Is.EqualTo(6).Within(0.001), "Very old plays outside both windows do not affect the change.");
        });
    }

    [Test]
    public void NoEarlierPlaysMeansNoChangeRatherThanZero()
    {
        StatisticsMetricView accuracy = StatisticsWorkspaceModel.Build([run(now.AddDays(-1), 0.95, 0, 100)],
            new StatisticsRunQuery(TimeRange: StatisticsTimeRange.Days30), now).Insights.For(StatisticsMetric.Accuracy);

        Assert.That(accuracy.Change, Is.Null);
    }

    [TestCase(10, 3)]
    [TestCase(40, 5)]
    [TestCase(150, 10)]
    [TestCase(300, 20)]
    [TestCase(2_600, 150)]
    public void RollingWindowGrowsWithTheNumberOfPlays(int count, int expected) =>
        Assert.That(StatisticsInsightsBuilder.RollingWindowFor(count), Is.EqualTo(expected));

    [Test]
    public void RollingMeanAndBandFollowTheWindow()
    {
        CoachingChartPoint[] points = Enumerable.Range(0, 6)
                                                .Select(index => new CoachingChartPoint(Guid.NewGuid(), now.AddHours(index), index % 2 == 0 ? 90 : 94))
                                                .ToArray();

        (CoachingChartPoint[] mean, double[] band) = StatisticsInsightsBuilder.RollingMeanWithBand(points, 4);

        Assert.Multiple(() =>
        {
            Assert.That(mean[^1].Value, Is.EqualTo(92).Within(0.001));
            Assert.That(mean[0].Value, Is.EqualTo(90));
            Assert.That(band[0], Is.Zero, "A single play has no interval.");
            Assert.That(band[^1], Is.EqualTo(1.96 * 2 / 2).Within(0.001), "SD 2 over 4 plays gives a standard error of 1.");
        });
    }

    [Test]
    public void MissHistogramUsesOneBinPerCountWithATail()
    {
        StatisticsHistogramBin[] bins = StatisticsInsightsBuilder.Histogram([0, 0, 1, 3, 25], StatisticsMetric.Misses);

        Assert.Multiple(() =>
        {
            Assert.That(bins, Has.Length.EqualTo(21));
            Assert.That(bins[0].Count, Is.EqualTo(2));
            Assert.That(bins[1].Count, Is.EqualTo(1));
            Assert.That(bins[2].Count, Is.Zero);
            Assert.That(bins[^1].Label, Is.EqualTo("20+"));
            Assert.That(bins[^1].Count, Is.EqualTo(1));
            Assert.That(bins.Sum(bin => bin.Count), Is.EqualTo(5));
        });
    }

    [Test]
    public void AccuracyHistogramCountsEveryPlayOnce()
    {
        double[] values = [80.2, 85, 90.5, 95.1, 99.9, 100];
        StatisticsHistogramBin[] bins = StatisticsInsightsBuilder.Histogram(values, StatisticsMetric.Accuracy);

        Assert.Multiple(() =>
        {
            Assert.That(bins.Sum(bin => bin.Count), Is.EqualTo(values.Length));
            Assert.That(bins[^1].End, Is.LessThanOrEqualTo(100));
        });
    }

    [Test]
    public void SourceCountsAndMissingPpAreReported()
    {
        LocalReplay local = run(now, 0.95, 0, 100);
        LocalReplay submitted = run(now, 0.95, 0, null) with { OnlineScoreId = 5 };
        LocalReplay onlineOnly = run(now, 0.95, 2, 90) with { OnlineScoreId = 6, IsLocallyStored = false };

        StatisticsInsights insights = StatisticsWorkspaceModel.Build([local, submitted, onlineOnly], new StatisticsRunQuery(), now).Insights;

        Assert.Multiple(() =>
        {
            Assert.That(insights.LocalCount, Is.EqualTo(2));
            Assert.That(insights.SubmittedCount, Is.EqualTo(2));
            Assert.That(insights.OnlineOnlyCount, Is.EqualTo(1));
            Assert.That(insights.MissingPpCount, Is.EqualTo(1));
            Assert.That(insights.MissFreeRate, Is.EqualTo(200 / 3d).Within(0.001));
        });
    }

    [Test]
    public void TypicalResultUsesPlaysNearTheSameStarRating()
    {
        var index = new StatisticsMapIndex([
            run(now, 0.90, 0, 100) with { StarRating = 4.4 },
            run(now, 0.94, 0, 120) with { StarRating = 4.6 },
            run(now, 0.50, 0, 10) with { StarRating = 6.5 },
        ]);

        StatisticsTypicalResult typical = index.Typical(4.5);

        Assert.Multiple(() =>
        {
            Assert.That(typical.PlayCount, Is.EqualTo(2));
            Assert.That(typical.AccuracyPercent, Is.EqualTo(92).Within(0.001));
            Assert.That(typical.MedianPerformancePoints, Is.EqualTo(110));
            Assert.That(index.Typical(double.NaN).PlayCount, Is.Zero);
        });
    }

    [Test]
    public void AttemptsAreReturnedOldestFirst()
    {
        Guid beatmap = Guid.NewGuid();
        LocalReplay latest = run(now, 0.95, 0, 100) with { BeatmapId = beatmap };
        LocalReplay first = run(now.AddDays(-3), 0.90, 0, 100) with { BeatmapId = beatmap };
        var index = new StatisticsMapIndex([latest, first, run(now, 0.8, 0, 50)]);

        Assert.That(index.Attempts(latest).Select(item => item.ScoreId), Is.EqualTo(new[] { first.ScoreId, latest.ScoreId }));
    }

    private static LocalReplay run(DateTimeOffset playedAt, double accuracy, int misses, double? pp) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Map", "Artist", "Insane", "osu", "Player",
        playedAt, 4.5, accuracy, 900_000, 500, misses, pp, [], true);
}

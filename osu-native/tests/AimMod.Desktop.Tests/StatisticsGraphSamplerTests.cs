using AimMod.Desktop.Coaching;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class StatisticsGraphSamplerTests
{
    [Test]
    public void SparseCumulativeHistoryCarriesThePreviousTotalAcrossTime()
    {
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        CoachingChartPoint[] points =
        {
            point(1, start, 100),
            point(2, start.AddDays(30), 200),
            point(3, start.AddDays(31), 300),
        };

        CoachingChartPoint[] sampled = StatisticsGraphSampler.SampleCumulative(points, 5);

        Assert.Multiple(() =>
        {
            Assert.That(sampled, Has.Length.EqualTo(5));
            Assert.That(sampled.Select(item => item.Value), Is.EqualTo(new[] { 100d, 100d, 100d, 100d, 300d }));
            Assert.That(sampled[0].PlayedAt, Is.EqualTo(start));
            Assert.That(sampled[^1].PlayedAt, Is.EqualTo(start.AddDays(31)));
            Assert.That(sampled[2].PlayedAt, Is.EqualTo(start.AddDays(15.5)));
        });
    }

    [Test]
    public void DenseHistoryRemainsBoundedAndKeepsItsFinalValue()
    {
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        CoachingChartPoint[] points = Enumerable.Range(0, 200)
                                                .Select(index => point(index + 1, start.AddMinutes(index), index + 1))
                                                .ToArray();

        CoachingChartPoint[] sampled = StatisticsGraphSampler.SampleCumulative(points, 80);

        Assert.Multiple(() =>
        {
            Assert.That(sampled, Has.Length.EqualTo(80));
            Assert.That(sampled[0].Value, Is.EqualTo(1));
            Assert.That(sampled[^1].Value, Is.EqualTo(200));
            Assert.That(sampled.Select(item => item.PlayedAt), Is.Ordered.Ascending);
        });
    }

    [Test]
    public void EmptyAndSinglePointSeriesStayExact()
    {
        DateTimeOffset at = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        CoachingChartPoint one = point(1, at, 42);

        Assert.Multiple(() =>
        {
            Assert.That(StatisticsGraphSampler.SampleCumulative(Array.Empty<CoachingChartPoint>(), 80), Is.Empty);
            Assert.That(StatisticsGraphSampler.SampleCumulative(new[] { one }, 80), Is.EqualTo(new[] { one }));
        });
    }

    private static CoachingChartPoint point(int id, DateTimeOffset playedAt, double value) =>
        new(new Guid(id, 0, 0, new byte[8]), playedAt, value);
}

using System.Reflection;
using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class StatisticsGraphLayoutTests
{
    [Test]
    public void NiceTicksCoverTheDataWithRoundSteps()
    {
        double[] accuracy = StatisticsChartFormat.NiceTicks(80.4, 99.6);
        double[] pp = StatisticsChartFormat.NiceTicks(21, 139);

        Assert.Multiple(() =>
        {
            Assert.That(accuracy.First(), Is.LessThanOrEqualTo(80.4));
            Assert.That(accuracy.Last(), Is.GreaterThanOrEqualTo(99.6));
            Assert.That(accuracy, Is.EqualTo(new double[] { 80, 85, 90, 95, 100 }));
            Assert.That(pp.First(), Is.LessThanOrEqualTo(21));
            Assert.That(pp.Last(), Is.GreaterThanOrEqualTo(139));
            Assert.That(pp.Zip(pp.Skip(1), (a, b) => b - a).Distinct().Count(), Is.EqualTo(1), "Ticks must be evenly spaced.");
        });
    }

    [Test]
    public void TimeTicksUseMonthsForLongRangesAndDaysForShortOnes()
    {
        var start = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);
        var months = StatisticsChartFormat.TimeTicks(start, start.AddDays(182), 8);
        var days = StatisticsChartFormat.TimeTicks(start, start.AddDays(30), 8);

        Assert.Multiple(() =>
        {
            Assert.That(months.Length, Is.InRange(4, 8));
            Assert.That(months.All(tick => tick.Time.ToLocalTime().Day == 1), Is.True, "Long ranges are labelled at month starts.");
            Assert.That(days.Length, Is.InRange(2, 8));
            Assert.That(days.All(tick => tick.Time >= start && tick.Time <= start.AddDays(30)), Is.True);
        });
    }

    [Test]
    public void MissingValuesUseADashRatherThanZero()
    {
        Assert.Multiple(() =>
        {
            Assert.That(StatisticsChartFormat.Value(StatisticsMetric.Performance, null), Is.EqualTo(StatisticsChartFormat.Missing));
            Assert.That(StatisticsChartFormat.Value(StatisticsMetric.Accuracy, 91.234), Is.EqualTo("91.23%"));
            Assert.That(StatisticsChartFormat.Value(StatisticsMetric.Misses, 0), Is.EqualTo("0"));
        });
    }

    [Test]
    public void ChangeColourFollowsWhichDirectionIsBetter()
    {
        Assert.Multiple(() =>
        {
            Assert.That(StatisticsChartFormat.ChangeStyle(StatisticsMetric.Accuracy, 1.2, true).Colour, Is.EqualTo(AimModPalette.Success));
            Assert.That(StatisticsChartFormat.ChangeStyle(StatisticsMetric.Misses, 1.2, false).Colour, Is.EqualTo(AimModPalette.Danger));
            Assert.That(StatisticsChartFormat.ChangeStyle(StatisticsMetric.Misses, -0.8, false).Colour, Is.EqualTo(AimModPalette.Success));
            Assert.That(StatisticsChartFormat.ChangeStyle(StatisticsMetric.Stars, 0.4, null).Colour, Is.EqualTo(AimModPalette.Cyan),
                "Harder maps are neither better nor worse.");
            Assert.That(StatisticsChartFormat.ChangeStyle(StatisticsMetric.Accuracy, 0.01, true).Colour, Is.EqualTo(AimModPalette.Muted));
        });
    }

    [Test]
    public void ReferenceLineUsesMedianForPpAndMeanOtherwise()
    {
        CoachingChartPoint[] points = [point(0, 10), point(1, 20), point(2, 90)];
        var pp = new StatisticsMetricView(StatisticsMetric.Performance, points, [], 0, [], 20, null, string.Empty, [], [], 0);
        var accuracy = pp with { Metric = StatisticsMetric.Accuracy, Headline = 40 };

        Assert.Multiple(() =>
        {
            Assert.That(StatisticsTrendChart.ReferenceValue(pp), Is.EqualTo(20));
            Assert.That(StatisticsTrendChart.ReferenceValue(accuracy), Is.EqualTo(40).Within(0.001));
            Assert.That(StatisticsTrendChart.ReferenceValue(StatisticsMetricView.Empty(StatisticsMetric.Accuracy)), Is.Null);
        });
    }

    [Test]
    public void FilterPopoverAndToolbarDrawAboveTheScrollingContentWithoutClipping()
    {
        using var workspace = new NativeStatisticsWorkspace(new InMemoryLocalLibrarySource([], []), _ => { });
        var layer = field<Container>(workspace, "filterLayer");
        var popover = field<Container>(workspace, "filterPopover");
        var content = field<Container>(workspace, "contentViewport");
        var dropdown = field<ScoreModFilterDropdown>(workspace, "modDropdown");
        var modsRow = popover.Children.OfType<Container>().Single(row => row.Children.Any(child => ReferenceEquals(child, dropdown)));
        Drawable[] otherRows = popover.Children.Where(child => !ReferenceEquals(child, modsRow) && child.Y > 0 && child.Y < modsRow.Y).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(layer.Depth, Is.LessThan(content.Depth), "The popover must draw and receive input above the charts and plays.");
            Assert.That(popover.Masking, Is.False, "An open mods menu must be able to extend beyond the popover.");
            Assert.That(layer.Masking, Is.False);
            Assert.That(modsRow.Masking, Is.False);
            Assert.That(otherRows, Is.Not.Empty);
            Assert.That(otherRows.All(row => modsRow.Depth < row.Depth), Is.True, "The mods menu must cover the rows beside it.");
            Assert.That(popover.Children.OfType<FillFlowContainer>(), Is.Empty,
                "Rows are fixed-position so the popup layer can raise one without reordering a flow.");
        });
    }

    private static CoachingChartPoint point(int day, double value) =>
        new(Guid.NewGuid(), new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(day), value);

    private static T field<T>(object instance, string name)
        where T : class =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance) as T
        ?? throw new AssertionException($"{instance.GetType().Name}.{name} was not found.");
}

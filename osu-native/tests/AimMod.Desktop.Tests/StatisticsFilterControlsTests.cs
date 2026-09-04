using AimMod.Desktop.Coaching;
using NUnit.Framework;
using osu.Framework.Bindables;
using osu.Game.Graphics.UserInterfaceV2;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class StatisticsFilterControlsTests
{
    [Test]
    public void NativeDropdownPreservesPeriodOptionsAndTwoWayBinding()
    {
        var period = new Bindable<StatisticsTimeRange>(StatisticsTimeRange.Days90);
        using var dropdown = new StatisticsFilterDropdown<StatisticsTimeRange>("Period", period);

        Assert.Multiple(() =>
        {
            Assert.That(dropdown, Is.InstanceOf<ShearedDropdown<StatisticsTimeRange>>());
            Assert.That(dropdown.Items, Is.EqualTo(Enum.GetValues<StatisticsTimeRange>()));
            Assert.That(dropdown.Current.Value, Is.EqualTo(StatisticsTimeRange.Days90));
        });

        dropdown.Current.Value = StatisticsTimeRange.Year;
        Assert.That(period.Value, Is.EqualTo(StatisticsTimeRange.Year));
        period.Value = StatisticsTimeRange.All;
        Assert.That(dropdown.Current.Value, Is.EqualTo(StatisticsTimeRange.All));
    }

    [Test]
    public void SourceAndSortControlsRetainEveryModelOption()
    {
        using var source = new StatisticsFilterDropdown<StatisticsScoreSource>("Source", new(StatisticsScoreSource.Local));
        using var sort = new StatisticsFilterDropdown<StatisticsRunSort>("Sort", new(StatisticsRunSort.PerformancePoints));

        Assert.Multiple(() =>
        {
            Assert.That(source.Items, Is.EqualTo(Enum.GetValues<StatisticsScoreSource>()));
            Assert.That(sort.Items, Is.EqualTo(Enum.GetValues<StatisticsRunSort>()));
            Assert.That(source.Current.Value, Is.EqualTo(StatisticsScoreSource.Local));
            Assert.That(sort.Current.Value, Is.EqualTo(StatisticsRunSort.PerformancePoints));
        });
    }
}

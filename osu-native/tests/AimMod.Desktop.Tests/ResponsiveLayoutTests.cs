using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class ResponsiveLayoutTests
{
    [TestCase(1_200, NativeBeatmapFilterLayout.Row)]
    [TestCase(800, NativeBeatmapFilterLayout.TwoColumns)]
    [TestCase(520, NativeBeatmapFilterLayout.Stacked)]
    public void OnlineBeatmapFiltersStackInsteadOfOverflowing(float width, NativeBeatmapFilterLayout expected) =>
        Assert.That(NativeOfficialBeatmapSearchScreen.CalculateFilterLayout(width), Is.EqualTo(expected));

    [TestCase(1_000, false)]
    [TestCase(639, true)]
    public void InstalledFiltersMoveToTwoRowsInNarrowWindows(float width, bool compact) =>
        Assert.That(LocalLibrary.NativeInstalledBeatmapBrowser.UsesCompactToolbar(width), Is.EqualTo(compact));

    [TestCase(1_200, 4, 4)]
    [TestCase(600, 4, 2)]
    [TestCase(300, 4, 1)]
    [TestCase(1_200, 2, 2)]
    [TestCase(600, 2, 1)]
    public void HistoryMetricsAndGraphsWrapInNarrowWindows(float width, int preferred, int expected) =>
        Assert.That(Coaching.ReplayHistoryScreen.ColumnsFor(width, preferred), Is.EqualTo(expected));
}

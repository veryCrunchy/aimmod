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
}

using System.Reflection;
using AimMod.Osu.Runtime;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class OnlineBeatmapLayoutTests
{
    private const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public void FiltersRenderAheadOfStatusResultsAndInspectorWithoutClipping()
    {
        using var screen = new NativeOfficialBeatmapSearchScreen(() => null, () => null);
        var band = field<Container>(screen, "filterBand");
        Assert.Multiple(() =>
        {
            Assert.That(band.Depth, Is.LessThan(field<Drawable>(screen, "resultStatus").Depth));
            Assert.That(band.Depth, Is.LessThan(field<Drawable>(screen, "resultViewport").Depth));
            Assert.That(band.Depth, Is.LessThan(field<Drawable>(screen, "inspectorRail").Depth));
            Assert.That(band.Masking, Is.False, "Status and sort menus must not be clipped by their row.");
        });
    }

    [TestCase(1_200, true)]
    [TestCase(899, false)]
    public void InspectorOnlyAppearsBesideResultsInWideLayouts(float width, bool side) =>
        Assert.That(NativeOfficialBeatmapSearchScreen.UsesSideInspector(width), Is.EqualTo(side));

    [Test]
    public void RowDownloadStaysSecondaryAndInspectorMirrorsItsState()
    {
        var cardType = typeof(NativeOfficialBeatmapSearchScreen).GetNestedType("OnlineBeatmapCard", BindingFlags.NonPublic)!;
        var set = new OfficialBeatmapSet(1, "Synthetic", "Synthetic", "Artist", "Artist", "Mapper", "", "ranked", null, null, 10, 1, false, false,
            null, null, null, null, [new OfficialBeatmapDifficulty(2, "Hard", "osu", 4, 180, 90, 4, 9, 8, 5, 100, 25, 500)]);
        Func<OfficialBeatmapSet, Task<OnlineBeatmapImportResult>> import = value => Task.FromResult(new OnlineBeatmapImportResult(OnlineBeatmapImportStatus.Success, value.BeatmapSetId));
        Func<LazerBeatmapArchive, Task<LazerBeatmapInstallResult>> install = _ => Task.FromResult(new LazerBeatmapInstallResult(LazerBeatmapInstallStatus.Sent));
        using var card = (Drawable)Activator.CreateInstance(cardType, set, import, install)!;
        var background = (osu.Framework.Graphics.Shapes.Box)cardType.GetField("actionBackground", flags)!.GetValue(card)!;
        Assert.That(background.Colour.AverageColour.Linear, Is.Not.EqualTo(AimModPalette.Accent.ToLinear()), "Rows must not each carry a mint primary action.");
        Assert.That((bool)cardType.GetProperty("CanAct")!.GetValue(card)!, Is.True);
        cardType.GetMethod("SetInstalled")!.Invoke(card, null);
        Assert.Multiple(() =>
        {
            Assert.That((bool)cardType.GetProperty("CanAct")!.GetValue(card)!, Is.False);
            Assert.That(cardType.GetProperty("ActionLabel")!.GetValue(card), Is.EqualTo("Installed"));
        });
    }

    private static T field<T>(object instance, string name) => (T)instance.GetType().GetField(name, flags)!.GetValue(instance)!;
}

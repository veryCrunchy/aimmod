using System.Reflection;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Visuals;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class InstalledBeatmapLayoutTests
{
    [Test]
    public void AccuracyColumnsShareTheInspectorWidthEqually()
    {
        var values = NativeInstalledBeatmapBrowser.AccuracyPoints.ToDictionary(point => point, point => point * 3d);
        using var card = create("PpByAccuracy", values, 0.972);
        var columns = ((Container)((FillFlowContainer)card).Children[0]).Children.OfType<Container>().ToArray();
        Assert.That(columns, Has.Length.EqualTo(5), "95, 97, 98, 99 and SS");
        for (int i = 0; i < columns.Length; i++)
        {
            Assert.That(columns[i].RelativeSizeAxes, Is.EqualTo(Axes.X));
            Assert.That(columns[i].Width, Is.EqualTo(0.2f).Within(0.0001));
            Assert.That(columns[i].X, Is.EqualTo(i / 5f).Within(0.0001));
        }
    }

    [TestCase(96.0, 288.0)]
    [TestCase(99.5, 298.5)]
    [TestCase(94.0, null)]
    public void UsualAccuracyPpInterpolatesBetweenCalculatedPoints(double accuracy, double? expected)
    {
        var values = NativeInstalledBeatmapBrowser.AccuracyPoints.ToDictionary(point => point, point => point * 3d);
        var method = nested("PpByAccuracy").GetMethod("Interpolate", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.That((double?)method.Invoke(null, [values, accuracy]), expected is null ? Is.Null : Is.EqualTo(expected.Value).Within(0.001));
    }

    [Test]
    public void RowsAreCompactAndShowThePlayersResultInsteadOfPlayCounts()
    {
        var difficulty = new LocalBeatmapDifficulty(Guid.NewGuid(), 1, "A long synthetic difficulty", "osu", 5, 180, 120000, 4, 9, 8, 5, 0);
        var set = new LocalBeatmapSet(Guid.NewGuid(), 1, new string('T', 120), "Artist", "Mapper", "", DateTimeOffset.UnixEpoch, null, [difficulty], 0);
        using var row = create("BeatmapSetRow", set, (Action<LocalBeatmapSet>)(_ => { }));
        string status() => (string)row.GetType().GetProperty("StatusTextForTesting", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(row)!;
        Assert.Multiple(() =>
        {
            Assert.That(row.Height, Is.InRange(72, 88));
            Assert.That(status(), Does.StartWith("Unplayed"));
        });
        row.GetType().GetMethod("SetHistory")!.Invoke(row, [new BeatmapPlaySummary(0.9721, 243.4, 3, DateTimeOffset.Now.AddDays(-2))]);
        Assert.That(status(), Is.EqualTo("97.21% 243pp 3 plays · 2d ago"));
    }

    [Test]
    public void SkillHeadlineNamesTheSkillsAboveTheUsualLevel()
    {
        var headline = nested("BeatmapInspector").GetMethod("SkillHeadline", BindingFlags.Static | BindingFlags.NonPublic)!;
        string text(MapBrowserSkillValue[] values, double? usualStars) => (((string, osu.Framework.Graphics.Colour4))headline.Invoke(null, [values, 6.0, usualStars])!).Item1;
        Assert.Multiple(() =>
        {
            Assert.That(text([new("Aim", 6, 5.5), new("Speed", 7.4, 5)], 5.2), Is.EqualTo("Harder than your usual on Speed. 0.8 stars above your usual 5.2."));
            Assert.That(text([new("Aim", 5, 5.5), new("Speed", 5, 5)], 6.0), Is.EqualTo("Close to what you usually play. Right at your usual 6.0 stars."));
            Assert.That(text([new("Aim", 5), new("Speed", 5)], null), Does.StartWith("Set a few scores"));
        });
    }

    [Test]
    public void HistoryFindsUsualStarsFromBestPasses()
    {
        Guid set = Guid.NewGuid();
        LocalReplay replay(double stars, double pp, double accuracy, bool passed = true) => new(Guid.NewGuid(), set, Guid.NewGuid(), "T", "A", "D", "osu", "P",
            DateTimeOffset.Now, stars, accuracy, 1, 1, 0, pp, [], true, Passed: passed);
        var history = new InstalledBeatmapHistory([replay(5, 200, 0.97), replay(6, 260, 0.95), replay(5.5, 230, 0.96), replay(9, 10, 0.5, passed: false)]);
        Assert.Multiple(() =>
        {
            Assert.That(history.UsualStars, Is.EqualTo(5.5).Within(0.001));
            Assert.That(history.UsualAccuracy, Is.EqualTo(0.96).Within(0.001));
            Assert.That(history.BySet[set].Plays, Is.EqualTo(4));
            Assert.That(history.BySet[set].BestPp, Is.EqualTo(260));
            Assert.That(new InstalledBeatmapHistory([replay(5, 1, 1)]).UsualStars, Is.Null, "A single play is not a usual level.");
        });
    }

    [Test]
    public void ToolbarAndFilterPopoverRenderAheadOfResultsWithoutClipping()
    {
        using var browser = new NativeInstalledBeatmapBrowser(new InMemoryLocalLibrarySource([], []));
        var toolbar = field<Container>(browser, "toolbar");
        var popover = field<Container>(browser, "filterPopover");
        Assert.Multiple(() =>
        {
            Assert.That(toolbar.Depth, Is.LessThan(field<Container>(browser, "listPanel").Depth));
            Assert.That(toolbar.Depth, Is.LessThan(field<Container>(browser, "rightRail").Depth));
            Assert.That(toolbar.Masking, Is.False, "Dropdown and popover ancestors must stay unmasked.");
            Assert.That(toolbar.Children, Does.Contain(popover));
            Assert.That(popover.Alpha, Is.Zero);
        });
        browser.SetFilterPopoverForTesting(true);
        Assert.Multiple(() =>
        {
            Assert.That(popover.Alpha, Is.EqualTo(1));
            Assert.That(field<Drawable>(browser, "popoverDismiss").Alpha, Is.EqualTo(1), "Clicking outside closes the popover.");
        });
    }

    [TestCase(1_400, true)]
    [TestCase(899, false)]
    public void InspectorMovesOverTheListInNarrowLayouts(float width, bool side) =>
        Assert.That(NativeInstalledBeatmapBrowser.UsesSideInspector(width), Is.EqualTo(side));

    private static Type nested(string type) => typeof(NativeInstalledBeatmapBrowser).GetNestedType(type, BindingFlags.NonPublic)!;

    private static CompositeDrawable create(string type, params object?[] args) => (CompositeDrawable)Activator.CreateInstance(
        nested(type), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, args, null)!;

    private static T field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
}

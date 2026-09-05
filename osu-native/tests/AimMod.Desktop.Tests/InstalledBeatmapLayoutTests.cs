using System.Reflection;
using AimMod.Desktop.LocalLibrary;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Graphics.Containers;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class InstalledBeatmapLayoutTests
{
    [Test]
    public void AccuracyColumnsUseQuarterOfActualInspectorWidth()
    {
        using var card = create("PpAccuracyCard", new Dictionary<int, double> { [95] = 1234, [98] = 2345, [99] = 3456, [100] = 4567 }, null);
        var content = (Container)card.GetType().BaseType!.GetField("content", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(card)!;
        var columns = content.Children.OfType<Container>().ToArray();
        Assert.That(columns, Has.Length.EqualTo(4));
        for (int i = 0; i < columns.Length; i++)
        {
            Assert.That(columns[i].RelativeSizeAxes, Is.EqualTo(Axes.X));
            Assert.That(columns[i].RelativePositionAxes, Is.EqualTo(Axes.X));
            Assert.That(columns[i].Width, Is.EqualTo(.25f));
            Assert.That(columns[i].X, Is.EqualTo(i / 4f));
        }
        Assert.That(content.Children.Where(child => child.Y >= 87).Select(child => child.Y), Is.EquivalentTo(new[] { 87f, 103f }));
    }

    [Test]
    public void NarrowMapRowsKeepEveryDifficultyAndTableColumnScrollable()
    {
        var difficulty = new LocalBeatmapDifficulty(Guid.NewGuid(), 1, "A long synthetic difficulty", "osu", 5, 180, 120000, 4, 9, 8, 5, 0);
        var set = new LocalBeatmapSet(Guid.NewGuid(), 1, new string('T', 120), "Artist", "Mapper", "", DateTimeOffset.UnixEpoch, null, [difficulty], 0);
        using var row = create("BeatmapSetRow", 1, set, (Action<LocalBeatmapSet>)(_ => { }), (Action<LocalBeatmapSet, LocalBeatmapDifficulty>)((_, _) => { }));
        var pills = field<OsuScrollContainer>(row, "pillScroll");
        var table = field<OsuScrollContainer>(row, "tableScroll");
        var content = field<Container>(row, "tableContent");
        Assert.That(((CompositeDrawable)pills.Child).AutoSizeAxes, Is.EqualTo(Axes.Both));
        Assert.That(pills.RelativeSizeAxes, Is.EqualTo(Axes.None), "Remaining row width is computed after the fixed artwork/text offset.");
        Assert.That(table.RelativeSizeAxes, Is.EqualTo(Axes.X));
        Assert.That(content.Width, Is.GreaterThanOrEqualTo(710), "The last score column must remain in the horizontal scroll content.");
        Assert.That(content.Children.OfType<OsuScrollContainer>().Single().Y, Is.EqualTo(32));
    }

    private static CompositeDrawable create(string type, params object?[] args) => (CompositeDrawable)Activator.CreateInstance(
        typeof(NativeInstalledBeatmapBrowser).GetNestedType(type, BindingFlags.NonPublic)!,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, args, null)!;
    private static T field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
}

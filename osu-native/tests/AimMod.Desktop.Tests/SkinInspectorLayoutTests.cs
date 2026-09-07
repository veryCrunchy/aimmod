using System.Reflection;
using AimMod.Desktop.Skins;
using AimMod.Desktop.Skins.Online;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Graphics.Containers;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class SkinInspectorLayoutTests
{
    [TestCase(OnlineSkinCatalogStatus.Unavailable)]
    [TestCase(OnlineSkinCatalogStatus.Success)]
    [TestCase(OnlineSkinCatalogStatus.InvalidResponse)]
    public void EmptyOsuckResultsOfferAnExplicitBrowserRoute(OnlineSkinCatalogStatus status)
    {
        var response = new OnlineSkinCatalogSearchResult([
            new("skins-osuck-net", "skins.osuck.net", new("https://skins.osuck.net/"), new(status, [], 1, 30, false))]);
        var entry = NativeOnlineSkinsView.BrowserFallbackEntries(response).Single();
        Assert.That(entry.Name, Is.EqualTo("Browse skins.osuck.net"));
        Assert.That(entry.DetailsUri.AbsoluteUri, Is.EqualTo("https://skins.osuck.net/"));
        using var view = new NativeOnlineSkinsView(null, null, Path.GetTempPath());
        typeof(NativeOnlineSkinsView).GetMethod("select", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(view, [entry, true]);
        Assert.Multiple(() =>
        {
            Assert.That(field<bool>(view, "browseOnly"), Is.True);
            Assert.That(field<Container>(view, "artwork").Height, Is.Zero);
            Assert.That(field<FillFlowContainer>(view, "detailActions").Y, Is.EqualTo(16));
        });
    }

    [Test]
    public void BrowserFallbackDoesNotReplaceAvailableSkinsOrInventOtherProviders()
    {
        var entry = new OnlineSkinCatalogEntry("skins-osuck-net", "123", "Test", "Creator", new("https://skins.osuck.net/skins/123"), [],
            new("skins-osuck-net", "skins.osuck.net", new("https://skins.osuck.net/"), ""));
        var response = new OnlineSkinCatalogSearchResult([
            new("skins-osuck-net", "skins.osuck.net", new("https://skins.osuck.net/"), new(OnlineSkinCatalogStatus.Success, [entry], 1, 30, false)),
            new("creator-releases", "Creator releases", new("https://github.com/"), new(OnlineSkinCatalogStatus.Unavailable, [], 1, 30, false))]);
        Assert.That(NativeOnlineSkinsView.BrowserFallbackEntries(response), Is.Empty);
    }

    [Test]
    public void InstalledSearchBoxFillsOnlyItsFixedHeightPanel()
    {
        using var workspace = new NativeSkinsScreen();
        var search = field<Drawable>(workspace, "searchBox");
        var panel = field<Container>(workspace, "searchPanel");
        Assert.Multiple(() =>
        {
            Assert.That(search.RelativeSizeAxes, Is.EqualTo(Axes.X));
            Assert.That(search.Width, Is.EqualTo(1));
            Assert.That(search.Height, Is.EqualTo(panel.Height));
            Assert.That(panel.AutoSizeAxes, Is.EqualTo(Axes.None));
            Assert.That(panel.Height, Is.GreaterThan(0).And.LessThan(100));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void InspectorArtworkAndEveryActionShareAScrollableContentArea(bool online)
    {
        using CompositeDrawable workspace = online
            ? new NativeOnlineSkinsView(null, null, Path.GetTempPath())
            : new NativeSkinsScreen();
        var detail = field<Container>(workspace, "detailPanel");
        var scroll = detail.Children.OfType<OsuScrollContainer>().Single();
        var content = (Container)scroll.Child;
        var actions = content.Children.OfType<FillFlowContainer>().Single();

        Assert.Multiple(() =>
        {
            Assert.That(detail.Masking, Is.True);
            Assert.That(scroll.RelativeSizeAxes, Is.EqualTo(Axes.Both));
            Assert.That(content.RelativeSizeAxes, Is.EqualTo(Axes.X));
            Assert.That(content.AutoSizeAxes, Is.EqualTo(Axes.Y));
            Assert.That(actions.AutoSizeAxes, Is.EqualTo(Axes.Y));
            Assert.That(actions.Padding.Bottom, Is.GreaterThanOrEqualTo(16));
            foreach (string name in online ? new[] { "previewButton", "saveButton", "importButton", "sourceButton" } : new[] { "applyButton" })
                Assert.That(actions.Children, Does.Contain(field<Drawable>(workspace, name)), $"{name} must remain reachable by scrolling a short inspector.");
        });
    }

    private static T field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
}

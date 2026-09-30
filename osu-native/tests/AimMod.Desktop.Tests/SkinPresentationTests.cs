using System.Reflection;
using AimMod.Desktop.Skins;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class SkinPresentationTests
{
    [TestCase("-       《CK》 WhiteCat 3.0 ~ DT [-       《CK》 WhiteCat 3.0 ~ DT]", "CK", "WhiteCat 3.0 ~ DT")]
    [TestCase("- # BTMC |  ?Freedom Dive  ↓? [- # BTMC ?Freedom Dive ↓?]", null, "BTMC | Freedom Dive")]
    [TestCase("《CK》 Bacon boi 1.0 『blue』 [- 《CK》 Bacon boi 1.0 『blue』 (2)]", "CK", "Bacon boi 1.0 · blue")]
    [TestCase("!!! Aristia(Edit) [!!! Aristia(Edit)]", null, "Aristia(Edit)")]
    [TestCase("#  seoul v10  #", null, "seoul v10")]
    [TestCase("- ⌈ Kindle ⌋ -", null, "Kindle")]
    [TestCase("Rafis HDDT [rafis_hddt_v3]", null, "Rafis HDDT")]
    [TestCase("osu! \"argon\" (2022)", null, "osu! \"argon\" (2022)")]
    [TestCase("Why? [Different folder]", null, "Why? [Different folder]")]
    [TestCase("   ", null, "Unnamed skin")]
    public void DisplayNamesDropFolderNoiseButKeepTheSkinsOwnName(string stored, string? tag, string title)
    {
        SkinLabel label = SkinDisplayName.Split(stored);
        Assert.Multiple(() =>
        {
            Assert.That(label.Tag, Is.EqualTo(tag));
            Assert.That(label.Title, Is.EqualTo(title));
        });
    }

    [Test]
    public void DisplayNameNeverChangesTheStoredIdentity()
    {
        var skin = new InstalledLazerSkin(new ExternalLazerSkinSummary(Guid.NewGuid(), "- # BTMC |  ?Freedom Dive  ↓?", "BTMC", "", false, 3), string.Empty);
        Assert.Multiple(() =>
        {
            Assert.That(skin.Name, Is.EqualTo("- # BTMC |  ?Freedom Dive  ↓?"));
            Assert.That(skin.DisplayName, Is.EqualTo("BTMC | Freedom Dive"));
            Assert.That(SkinDisplayName.Differs(skin.Name), Is.True);
            Assert.That(SkinDisplayName.Differs("Clean Name"), Is.False);
        });
    }

    [Test]
    public void FiltersKeepOnlyTheChosenSourceAndSortNewestFirst()
    {
        InstalledLazerSkin old = skin("Zeta", InstalledSkinOrigin.Stable, DateTimeOffset.UtcNow.AddDays(-30));
        InstalledLazerSkin recent = skin("Alpha", InstalledSkinOrigin.Stable, DateTimeOffset.UtcNow.AddDays(-1));
        InstalledLazerSkin lazer = skin("- Mid", InstalledSkinOrigin.Lazer, null);
        InstalledLazerSkin[] all = [old, recent, lazer];

        Assert.Multiple(() =>
        {
            Assert.That(InstalledSkinFilters.Apply(all, InstalledSkinSourceFilter.All, InstalledSkinSort.Name), Is.EqualTo(new[] { recent, lazer, old }),
                "Name order follows the readable name, not the stored '- ' prefix.");
            Assert.That(InstalledSkinFilters.Apply(all, InstalledSkinSourceFilter.Stable, InstalledSkinSort.RecentlyAdded), Is.EqualTo(new[] { recent, old }));
            Assert.That(InstalledSkinFilters.Apply(all, InstalledSkinSourceFilter.Lazer, InstalledSkinSort.Name), Is.EqualTo(new[] { lazer }));
            Assert.That(InstalledSkinFilters.Apply(all, InstalledSkinSourceFilter.All, InstalledSkinSort.RecentlyAdded).Last(), Is.SameAs(lazer),
                "Skins without a known date sort last.");
        });
    }

    [Test]
    public void PreviewAssetsReadComboColoursFontPrefixesAndPreferHighResolution()
    {
        string directory = Directory.CreateTempSubdirectory("aimmod-skin-assets-").FullName;
        try
        {
            string ini = Path.Combine(directory, "skin.ini");
            File.WriteAllText(ini, "[General]\nName: Test\nVersion: 2.5\n[Colours]\nCombo1: 255,0,0\nCombo2 : 0, 128, 255 // blue\nSliderBorder: 10,20,30\n[Fonts]\nHitCirclePrefix: numbers\\num\n");
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["skin.ini"] = ini,
                ["hitcircle.png"] = Path.Combine(directory, "hitcircle.png"),
                ["hitcircle@2x.png"] = Path.Combine(directory, "hitcircle@2x.png"),
                ["numbers/num-1.png"] = Path.Combine(directory, "num-1.png"),
            };
            SkinPreviewAssets assets = SkinPreviewAssets.Read(files);

            Assert.Multiple(() =>
            {
                Assert.That(assets.ComboColours, Has.Count.EqualTo(2));
                Assert.That(assets.Combo(1).B, Is.EqualTo(1).Within(0.01));
                Assert.That(assets.Combo(2), Is.EqualTo(assets.Combo(0)), "Combo colours cycle.");
                Assert.That(assets.SliderBorder, Is.Not.Null);
                Assert.That(assets.Version, Is.EqualTo("2.5"));
                Assert.That(assets.HitCirclePrefix, Is.EqualTo("numbers/num"));
                Assert.That(assets.Find("hitcircle")!.HighResolution, Is.True);
                Assert.That(assets.Find($"{assets.HitCirclePrefix}-1"), Is.Not.Null);
                Assert.That(assets.HasGameplayElements, Is.True);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task StableSkinsListTheirOwnGameplayElementsAndFolder()
    {
        string root = Directory.CreateTempSubdirectory("aimmod-stable-skins-").FullName;
        try
        {
            string folder = Path.Combine(root, "- idke 1.2 -");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "skin.ini"), "[General]\nName: - idke 1.2 -\nAuthor: idke\n");
            foreach (string name in new[] { "hitcircle@2x.png", "approachcircle.png", "default-1.png", "normal-hitnormal.wav", "menu-background.jpg" })
                File.WriteAllBytes(Path.Combine(folder, name), [1]);

            InstalledLazerSkin skin = (await new OsuStableInstalledSkinSource(root).SearchAsync()).Items.Single();
            Assert.Multiple(() =>
            {
                Assert.That(skin.DisplayName, Is.EqualTo("idke 1.2"));
                Assert.That(skin.ElementFiles.Keys, Is.EquivalentTo(new[] { "skin.ini", "hitcircle@2x.png", "approachcircle.png", "default-1.png" }));
                Assert.That(skin.HasFolder, Is.True);
                Assert.That(skin.AddedAt, Is.Not.Null);
                Assert.That(skin.Summary.FileCount, Is.EqualTo(6));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestCase("hitcircleoverlay@2x.png", 0)]
    [TestCase("skin.ini", 0)]
    [TestCase("numbers/default-4.png", 1)]
    [TestCase("score-x.png", 1)]
    [TestCase("x.png", -1)]
    [TestCase("menu-background.jpg", -1)]
    [TestCase("a/b/default-1.png", -1)]
    [TestCase("../hitcircle.png", -1)]
    [TestCase("sub/skin.ini", -1)]
    public void OnlyBoundedGameplayElementsAreListedForThumbnails(string name, int priority) =>
        Assert.That(ExternalLazerSkinProtocol.PreviewElementPriority(name), Is.EqualTo(priority));

    [Test]
    public void FilterRowsDrawAheadOfResultsAndStayUnmaskedForOpenMenus()
    {
        using var screen = new NativeSkinsScreen();
        var toolbar = field<Container>(screen, "toolbar");
        var body = field<Container>(screen, "body");
        var status = field<Drawable>(screen, "status");
        using var online = new NativeOnlineSkinsView(null, null, Path.GetTempPath());
        var band = field<Container>(online, "filterBand");
        var results = field<Container>(online, "resultViewport");

        Assert.Multiple(() =>
        {
            Assert.That(toolbar.Depth, Is.LessThan(body.Depth), "Installed filters must draw over the cards.");
            Assert.That(toolbar.Depth, Is.LessThan(status.Depth));
            Assert.That(toolbar.Masking, Is.False);
            Assert.That(field<Container>(screen, "installedContent").Masking, Is.False);
            Assert.That(band.Depth, Is.LessThan(results.Depth), "Online filters must draw over the results.");
            Assert.That(band.Masking, Is.False);
            Assert.That(band.Children.OfType<CompositeDrawable>().All(group => !group.Masking), Is.True);
        });
    }

    private static InstalledLazerSkin skin(string name, InstalledSkinOrigin origin, DateTimeOffset? added) =>
        new(new ExternalLazerSkinSummary(Guid.NewGuid(), name, "Creator", "", false, 2), string.Empty, origin) { AddedAt = added };

    private static T field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
}

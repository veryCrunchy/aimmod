using System.Reflection;
using AimMod.Desktop.Skins;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class SkinListReuseTests
{
    [Test]
    public void SelectingASkinRestylesRowsInsteadOfRebuildingTheList()
    {
        using var screen = new NativeSkinsScreen();
        InstalledLazerSkin first = skin("First");
        InstalledLazerSkin second = skin("Second");
        show(screen, first, second);
        Drawable[] before = rows(screen);

        typeof(NativeSkinsScreen).GetMethod("select", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(screen, [second]);
        Drawable[] after = rows(screen);

        Assert.Multiple(() =>
        {
            Assert.That(before, Has.Length.EqualTo(2));
            Assert.That(after, Is.EqualTo(before), "Selection must keep the same row instances.");
        });
    }

    [Test]
    public void RefreshingTheSameSkinsKeepsTheirRows()
    {
        using var screen = new NativeSkinsScreen();
        InstalledLazerSkin first = skin("First");
        InstalledLazerSkin second = skin("Second");
        show(screen, first, second);
        Drawable[] before = rows(screen);
        show(screen, second, first);
        Assert.That(rows(screen), Is.EquivalentTo(before));
    }

    private static void show(NativeSkinsScreen screen, params InstalledLazerSkin[] skins)
    {
        int revision = (int)typeof(NativeSkinsScreen).GetField("revision", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(screen)!;
        typeof(NativeSkinsScreen).GetMethod("showSkins", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(screen, [revision, new InstalledLazerSkinPage(skins, skins.Length, 0, 100)]);
    }

    private static Drawable[] rows(NativeSkinsScreen screen) =>
        ((FillFlowContainer)typeof(NativeSkinsScreen).GetField("list", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(screen)!)
        .Children.ToArray();

    private static InstalledLazerSkin skin(string name) =>
        new(new ExternalLazerSkinSummary(Guid.NewGuid(), name, "Creator", "", false, 2), string.Empty);
}

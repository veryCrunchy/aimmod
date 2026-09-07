using System.Reflection;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Visuals;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class BeatmapDropdownLayoutTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void SharedHeaderKeepsMenuHeightUnconstrained(bool ppTargets)
    {
        using CompositeDrawable workspace = ppTargets
            ? new NativePpTargetsWorkspace(new InMemoryLocalLibrarySource([], []), () => null, () => null)
            : new NativeOfficialBeatmapSearchScreen(() => null, () => null);
        foreach (string field in ppTargets ? new[] { "categoryDropdown", "lengthDropdown", "sortDropdown" } : new[] { "categoryDropdown", "sortDropdown" })
        {
            var dropdown = (CompositeDrawable)workspace.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(workspace)!;
            object header = property(dropdown, "Header");
            Assert.Multiple(() =>
            {
                Assert.That(((Drawable)header).Height, Is.EqualTo(AimModVisualStyle.ControlHeight));
                Assert.That(dropdown.AutoSizeAxes.HasFlag(Axes.Y), Is.True, "The menu must still grow when opened.");
            });
        }
    }

    [Test]
    public void PpMenuAncestorsRenderAheadOfStatusAndResultsWithoutClipping()
    {
        using var workspace = new NativePpTargetsWorkspace(new InMemoryLocalLibrarySource([], []), () => null, () => null);
        var band = (Container)property(workspace, "filterBand");
        var header = (Container)property(workspace, "filterHeader");
        Assert.Multiple(() =>
        {
            Assert.That(band.Depth, Is.LessThan(((Drawable)property(workspace, "status")).Depth));
            Assert.That(band.Depth, Is.LessThan(((Drawable)property(workspace, "resultCount")).Depth));
            Assert.That(header.Depth, Is.LessThan(((Drawable)property(workspace, "resultViewport")).Depth));
            Assert.That(band.Masking || header.Masking, Is.False);
        });
    }

    private static object property(object instance, string name)
    {
        for (Type? type = instance.GetType(); type is not null; type = type.BaseType)
        {
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property is not null) return property.GetValue(instance)!;
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field is not null) return field.GetValue(instance)!;
        }
        throw new AssertionException($"Missing member {name}");
    }
}

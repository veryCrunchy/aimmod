using System.Reflection;
using AimMod.Desktop.LocalLibrary;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class BeatmapDropdownLayoutTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void EmptyLabelPreservesNativeHeaderHeightWithoutFixingMenuHeight(bool ppTargets)
    {
        using CompositeDrawable workspace = ppTargets
            ? new NativePpTargetsWorkspace(new InMemoryLocalLibrarySource([], []), () => null, () => null)
            : new NativeOfficialBeatmapSearchScreen(() => null, () => null);
        foreach (string field in ppTargets ? new[] { "categoryDropdown", "lengthDropdown", "sortDropdown" } : new[] { "categoryDropdown", "sortDropdown" })
        {
            var dropdown = (CompositeDrawable)workspace.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(workspace)!;
            object header = property(dropdown, "Header");
            var label = (Container)property(header, "LabelContainer");
            Assert.Multiple(() =>
            {
                Assert.That(label.AutoSizeAxes, Is.EqualTo(Axes.X));
                Assert.That(label.Height, Is.EqualTo(30));
                Assert.That(dropdown.AutoSizeAxes.HasFlag(Axes.Y), Is.True, "The menu must still grow when opened.");
            });
        }
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

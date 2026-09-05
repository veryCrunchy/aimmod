using System.Reflection;
using AimMod.Desktop.Coaching;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Graphics.UserInterface;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class CoachingTrendLayoutTests
{
    [Test]
    public void TraceAndOverlaysUsePositiveResponsiveBoundsWithoutInternalGraphPadding()
    {
        Type type = typeof(NativeCoachingWorkspace).GetNestedType("CoachingTrendChart", BindingFlags.NonPublic)!;
        using var chart = (CompositeDrawable)Activator.CreateInstance(type, nonPublic: true)!;
        var graph = (LineGraph)type.GetField("graph", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(chart)!;
        var markers = (Container)type.GetField("markers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(chart)!;
        var bars = (Container)type.GetField("missBars", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(chart)!;

        Assert.Multiple(() =>
        {
            Assert.That(chart.Height, Is.EqualTo(220));
            Assert.That(graph.RelativeSizeAxes, Is.EqualTo(Axes.Both));
            Assert.That(graph.Padding, Is.EqualTo(new MarginPadding()), "LineGraph calculates its path from DrawSize, so padding must live outside it.");
            Assert.That(markers.RelativeSizeAxes, Is.EqualTo(Axes.Both));
            Assert.That(markers.Width, Is.EqualTo(1));
            Assert.That(bars.RelativeSizeAxes, Is.EqualTo(Axes.Both));
            Assert.That(bars.Width, Is.EqualTo(1));
        });
    }
}

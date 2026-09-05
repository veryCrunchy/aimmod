using System.Reflection;
using AimMod.Desktop.Visuals;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class CompactReplayLayoutTests
{
    [Test]
    public void NotableJumpActionsHaveTheirOwnHorizontalScrollViewport()
    {
        using var route = new NativeReplayRouteView();
        var moments = (FillFlowContainer<Drawable>)typeof(NativeReplayRouteView)
            .GetField("momentButtons", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(route)!;
        Assert.Multiple(() =>
        {
            Assert.That(moments.AutoSizeAxes, Is.EqualTo(Axes.Both));
            Assert.That(moments.RelativeSizeAxes, Is.EqualTo(Axes.None));
            Assert.That(moments.Direction, Is.EqualTo(FillDirection.Horizontal));
        });
        var scroll = (AimModScrollContainer)typeof(NativeReplayRouteView)
            .GetField("momentScroll", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(route)!;
        Assert.Multiple(() =>
        {
            Assert.That(scroll.Child, Is.SameAs(moments));
            Assert.That(scroll.RelativeSizeAxes, Is.EqualTo(Axes.X));
            Assert.That(scroll.ScrollbarOverlapsContent, Is.False);
            Assert.That(scroll.Height, Is.GreaterThanOrEqualTo(42));
            Assert.That(scroll.Y + scroll.Height + 22, Is.LessThanOrEqualTo(240), "The transport must reserve the scroll gutter within its padding.");
        });
    }
}

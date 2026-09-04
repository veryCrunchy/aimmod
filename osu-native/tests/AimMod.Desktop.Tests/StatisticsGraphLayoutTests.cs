using System.Reflection;
using AimMod.Desktop.Coaching;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Graphics.UserInterface;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class StatisticsGraphLayoutTests
{
    [Test]
    public void TraceIsClippedToInsetPlotViewport()
    {
        Type cardType = typeof(NativeStatisticsWorkspace).GetNestedType(
            "StatisticsGraphCard",
            BindingFlags.NonPublic) ?? throw new AssertionException("Statistics graph card type was not found.");

        var card = (CompositeDrawable?)Activator.CreateInstance(
            cardType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: ["Accuracy", Colour4.Cyan, (Func<double, string>)(value => $"{value:0.00}%")],
            culture: null) ?? throw new AssertionException("Statistics graph card could not be constructed.");

        var viewport = getField<Container>(cardType, card, "plotViewport");
        var graph = getField<LineGraph>(cardType, card, "graph");
        var innerViewport = viewport.Child as Container;

        Assert.Multiple(() =>
        {
            Assert.That(card.Masking, Is.True, "The card must clip all decoration to its rounded bounds.");
            Assert.That(viewport.RelativeSizeAxes, Is.EqualTo(Axes.Both));
            Assert.That(viewport.Padding.Top, Is.GreaterThanOrEqualTo(48), "The plot must clear the card heading and range label.");
            Assert.That(viewport.Padding.Bottom, Is.GreaterThanOrEqualTo(16), "The trace must not touch the card's bottom edge.");
            Assert.That(viewport.Padding.Left, Is.GreaterThanOrEqualTo(12));
            Assert.That(viewport.Padding.Right, Is.GreaterThanOrEqualTo(12));
            Assert.That(innerViewport, Is.Not.Null, "The padded viewport must contain a dedicated clipping container.");
            Assert.That(innerViewport?.Masking, Is.True, "The inner plot area must mask the chart trace.");
            Assert.That(innerViewport?.RelativeSizeAxes, Is.EqualTo(Axes.Both));
            Assert.That(innerViewport?.Child, Is.SameAs(graph), "The graph must be clipped by the inner plot viewport.");
            Assert.That(graph.RelativeSizeAxes, Is.EqualTo(Axes.Both));
        });
    }

    private static T getField<T>(Type owner, object instance, string name)
        where T : class =>
        owner.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance) as T
        ?? throw new AssertionException($"{owner.Name}.{name} was not found.");
}

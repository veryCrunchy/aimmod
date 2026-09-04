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
            Assert.That(innerViewport?.Children, Does.Contain(graph), "The graph must be clipped by the inner plot viewport.");
            Assert.That(graph.RelativeSizeAxes, Is.EqualTo(Axes.Both));
        });
    }

    [Test]
    public void SparseSeriesAddsOnlyBoundedPointMarkers()
    {
        (Type cardType, CompositeDrawable card) = createCard();
        var points = new[]
        {
            new CoachingChartPoint(Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-2), 91),
            new CoachingChartPoint(Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-1), 95),
            new CoachingChartPoint(Guid.NewGuid(), DateTimeOffset.UtcNow, 93),
        };

        cardType.GetMethod("SetSeries", BindingFlags.Instance | BindingFlags.Public)?.Invoke(card, [points]);
        var pointLayer = getField<Container>(cardType, card, "pointLayer");

        Assert.Multiple(() =>
        {
            Assert.That(pointLayer.Masking, Is.False, "The containing plot viewport owns clipping for every marker.");
            Assert.That(pointLayer.Children, Has.Count.EqualTo(points.Length));
            Assert.That(pointLayer.Children.All(point => point.RelativePositionAxes == Axes.Both), Is.True);
            Assert.That(pointLayer.Children.All(point => point.X is >= 0 and <= 1), Is.True);
            Assert.That(pointLayer.Children.All(point => point.Y is >= 0 and <= 1), Is.True);
        });
    }

    [Test]
    public void DenseSeriesAvoidsRenderingACloudOfMarkers()
    {
        (Type cardType, CompositeDrawable card) = createCard();
        CoachingChartPoint[] points = Enumerable.Range(0, 80)
                                                        .Select(index => new CoachingChartPoint(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(index), index))
                                                        .ToArray();

        cardType.GetMethod("SetSeries", BindingFlags.Instance | BindingFlags.Public)?.Invoke(card, [points]);

        Assert.That(getField<Container>(cardType, card, "pointLayer").Children, Is.Empty);
    }

    private static (Type Type, CompositeDrawable Card) createCard()
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

        return (cardType, card);
    }

    private static T getField<T>(Type owner, object instance, string name)
        where T : class =>
        owner.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance) as T
        ?? throw new AssertionException($"{owner.Name}.{name} was not found.");
}

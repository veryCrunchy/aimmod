using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Replays;
using NUnit.Framework;
using osuTK;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class ReplayRouteLayoutTests
{
    [TestCase(1_133, 688)]
    [TestCase(879, 640)]
    public void WideRoutesPlacePlaybackBesideOneSidePanel(float width, float height)
    {
        var layout = NativeReplayRouteView.CalculateLayout(width, height);
        Assert.Multiple(() =>
        {
            Assert.That(layout.Mode, Is.EqualTo(NativeReplayRouteView.ReplayRouteLayoutMode.TwoColumn));
            Assert.That(layout.Side.Width, Is.GreaterThanOrEqualTo(340), "The side panel keeps a usable minimum width.");
            Assert.That(layout.Playback.Width, Is.GreaterThan(layout.Side.Width), "Playback is the wide column.");
            Assert.That(layout.Side.X, Is.GreaterThan(layout.Playback.Right), "Columns must not overlap.");
            Assert.That(layout.Side.Right, Is.EqualTo(width).Within(0.01f));
            Assert.That(layout.Block.Height + ReplayNowPlayingBar.BarHeight, Is.LessThanOrEqualTo(height));
        });
    }

    [Test]
    public void NarrowRoutesStackThePanelBelowPlayback()
    {
        var layout = NativeReplayRouteView.CalculateLayout(792, 845);
        Assert.Multiple(() =>
        {
            Assert.That(layout.Mode, Is.EqualTo(NativeReplayRouteView.ReplayRouteLayoutMode.Stacked));
            Assert.That(layout.Playback.Width, Is.EqualTo(792));
            Assert.That(layout.Side.Width, Is.EqualTo(792));
            Assert.That(layout.Side.Y, Is.GreaterThan(layout.Playback.Bottom), "The panel sits below the transport.");
            Assert.That(layout.Side.Height, Is.GreaterThanOrEqualTo(260), "The stacked panel stays usable.");
        });
    }

    [TestCase(781, 600)]
    [TestCase(1_200, 400)]
    [TestCase(300, 900)]
    public void ViewportKeepsWidescreenSurfaceAndAttachedTransport(float width, float height)
    {
        ReplayPlaybackLayout layout = ReplayPlaybackLayout.Calculate(width, height);
        Vector2 surface = ReplayViewport.Fit(layout.ViewportSize.X, layout.ViewportSize.Y);
        Assert.Multiple(() =>
        {
            Assert.That(surface.X / surface.Y, Is.EqualTo(ReplayViewport.SurfaceAspect).Within(0.001f), "The game surface is never stretched.");
            Assert.That(surface.X, Is.LessThanOrEqualTo(layout.ViewportSize.X + 0.01f));
            Assert.That(surface.Y, Is.LessThanOrEqualTo(layout.ViewportSize.Y + 0.01f));
            Assert.That(layout.TransportY, Is.EqualTo(layout.ViewportSize.Y + ReplayPlaybackLayout.Gap), "The transport is attached below the viewport.");
            Assert.That(layout.Height, Is.LessThanOrEqualTo(height + 0.01f));
        });
    }

    [Test]
    public void ViewportFitsTheWholeReferenceSurface()
    {
        Vector2 fitted = ReplayViewport.Fit(620, 900);
        Assert.Multiple(() =>
        {
            Assert.That(fitted.X, Is.EqualTo(620).Within(0.01f));
            Assert.That(fitted.Y, Is.EqualTo(620 / ReplayViewport.SurfaceAspect).Within(0.01f));
            Assert.That(ReplayViewport.Fit(0, 100), Is.EqualTo(Vector2.Zero));
        });
    }

    [TestCase(0, "0:00.0")]
    [TestCase(61_234, "1:01.2")]
    [TestCase(-50, "0:00.0")]
    public void TransportClockUsesTenthsOfASecond(double milliseconds, string expected) =>
        Assert.That(NativeReplayRouteView.formatClock(milliseconds), Is.EqualTo(expected));

    [Test]
    public void GroupSignatureChangesWhenAttemptsOrPpChange()
    {
        LocalReplay first = replay(null);
        LocalReplay second = replay(null);
        var group = new ReplayBrowserMapGroup("map", "Title", "Artist", "Hard", DateTimeOffset.UnixEpoch, [first, second]);
        string signature = NativeReplayRouteView.ReplayGroupSignature(group);
        Assert.Multiple(() =>
        {
            Assert.That(NativeReplayRouteView.ReplayGroupSignature(group with { Attempts = [first, second] }), Is.EqualTo(signature));
            Assert.That(NativeReplayRouteView.ReplayGroupSignature(group with { Attempts = [first] }), Is.Not.EqualTo(signature));
            Assert.That(NativeReplayRouteView.ReplayGroupSignature(group with { Attempts = [first, second with { PerformancePoints = 250 }] }),
                Is.Not.EqualTo(signature), "Hydrated PP must refresh the displayed attempts.");
        });
    }

    private static LocalReplay replay(double? pp) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Title", "Artist", "Hard", "osu", "Player",
        DateTimeOffset.UnixEpoch, 5, 0.98, 1_000_000, 500, 1, pp, [], true);
}

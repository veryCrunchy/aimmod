using AimMod.Desktop.LocalLibrary;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class ReplayRouteLayoutTests
{
    [Test]
    public void WideWindowsShowLibraryPlaybackAndDetailsTogether()
    {
        var layout = NativeReplayRouteView.CalculateLayout(1_400, detailsOpen: false, libraryOpen: true);
        Assert.Multiple(() =>
        {
            Assert.That(layout.Mode, Is.EqualTo(NativeReplayRouteView.ReplayRouteLayoutMode.Wide));
            Assert.That(layout.ShowBrowser && layout.ShowPlayback && layout.ShowInspector, Is.True);
        });
    }

    [Test]
    public void MediumWindowsSwapPlaybackForDetails()
    {
        var playback = NativeReplayRouteView.CalculateLayout(900, detailsOpen: false, libraryOpen: true);
        var details = NativeReplayRouteView.CalculateLayout(900, detailsOpen: true, libraryOpen: true);
        Assert.Multiple(() =>
        {
            Assert.That(playback.ShowBrowser && playback.ShowPlayback && !playback.ShowInspector, Is.True);
            Assert.That(details.ShowBrowser && !details.ShowPlayback && details.ShowInspector, Is.True);
        });
    }

    [TestCase(false, true, true, false, false)]
    [TestCase(false, false, false, true, false)]
    [TestCase(true, true, false, false, true)]
    public void NarrowWindowsShowExactlyOnePane(bool detailsOpen, bool libraryOpen, bool browser, bool playback, bool inspector)
    {
        var layout = NativeReplayRouteView.CalculateLayout(600, detailsOpen, libraryOpen);
        Assert.Multiple(() =>
        {
            Assert.That(layout.Mode, Is.EqualTo(NativeReplayRouteView.ReplayRouteLayoutMode.Narrow));
            Assert.That(layout.ShowBrowser, Is.EqualTo(browser));
            Assert.That(layout.ShowPlayback, Is.EqualTo(playback));
            Assert.That(layout.ShowInspector, Is.EqualTo(inspector));
            Assert.That(layout.BrowserWidth, Is.EqualTo(600), "The narrow library uses the full width.");
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

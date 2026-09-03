using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Skins;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class NativeScreenConstructionTests
{
    private readonly InMemoryLocalLibrarySource source = new(
        Array.Empty<LocalBeatmapSet>(),
        Array.Empty<LocalReplay>());

    [TestCase(NativeLocalLibraryMode.Beatmaps)]
    [TestCase(NativeLocalLibraryMode.Replays)]
    public void LibraryRoutesConstructWithoutConflictingLayoutAxes(NativeLocalLibraryMode mode)
    {
        Assert.DoesNotThrow(() => _ = new NativeLocalLibraryScreen(source, mode, _ => { }));
    }

    [TestCase(ReplayHistoryScreenMode.Statistics)]
    [TestCase(ReplayHistoryScreenMode.Coaching)]
    public void HistoryRoutesConstructWithoutConflictingLayoutAxes(ReplayHistoryScreenMode mode)
    {
        Assert.DoesNotThrow(() => _ = new ReplayHistoryScreen(
            source,
            mode,
            new Dictionary<Guid, ReplayAnalysisResult>(),
            _ => { }));
    }

    [Test]
    public void ReplayRouteConstructsWithoutConflictingLayoutAxes()
    {
        Assert.DoesNotThrow(() => _ = new NativeReplayRouteView());
    }

    [Test]
    public void SkinsRouteConstructsWithoutAConnectedLazerLibrary()
    {
        Assert.DoesNotThrow(() => _ = new NativeSkinsScreen());
    }

    [Test]
    public void BeatmapDiscoveryRoutesConstructWithoutConflictingLayoutAxes()
    {
        Assert.Multiple(() =>
        {
            Assert.DoesNotThrow(() => _ = new NativeInstalledBeatmapBrowser(source));
            Assert.DoesNotThrow(() => _ = new NativeBeatmapDiscoveryScreen(source, () => null, () => null));
            Assert.DoesNotThrow(() => _ = new NativeOfficialBeatmapSearchScreen(() => null, () => null));
        });
    }
}

using AimMod.Desktop.Coaching;
using AimMod.Desktop.Hub;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Skins;
using AimMod.Desktop.Skins.Online;
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
    public void HubSettingsPanelConstructsWithoutConfiguredServices()
    {
        Assert.DoesNotThrow(() => _ = new NativeHubSettingsPanel(null, null, null, null, null, null));
    }

    [Test]
    public void SkinsRouteConstructsWithoutAConnectedLazerLibrary()
    {
        Assert.DoesNotThrow(() => _ = new NativeSkinsScreen());
    }

    [Test]
    public void SkinsRouteSwitchesBetweenInstalledAndOnlineCatalogs()
    {
        using var screen = new NativeSkinsScreen();

        Assert.That(screen.GetCurrentTabForTesting(), Is.EqualTo(NativeSkinsScreen.SkinsWorkspaceTab.Installed));
        screen.SelectTabForTesting(NativeSkinsScreen.SkinsWorkspaceTab.Online);
        Assert.That(screen.GetCurrentTabForTesting(), Is.EqualTo(NativeSkinsScreen.SkinsWorkspaceTab.Online));
    }

    [Test]
    public void OnlineSkinCatalogUsesSafeDefaultFilters()
    {
        using var view = new NativeOnlineSkinsView(null, null, Path.Combine(Path.GetTempPath(), "aimmod-skin-ui-tests"));

        Assert.Multiple(() =>
        {
            Assert.That(view.SelectedProviderForTesting, Is.EqualTo("All providers"));
            Assert.That(view.SelectedRulesetForTesting, Is.EqualTo(OnlineSkinRuleset.Standard));
            Assert.That(view.SelectedSortForTesting, Is.EqualTo(OnlineSkinSort.Newest));
        });
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

    [Test]
    public void StatisticsWorkspaceConstructsWithoutConflictingLayoutAxes()
    {
        Assert.DoesNotThrow(() => _ = new NativeStatisticsWorkspace(source, _ => { }));
    }

    [Test]
    public void BeatmapDiscoveryCanReturnToInstalledTab()
    {
        var screen = new NativeBeatmapDiscoveryScreen(source, () => null, () => null);

        screen.SelectTab(NativeBeatmapDiscoveryScreen.BeatmapDiscoveryTab.Installed);
        object? installed = screen.GetActiveScreenForTesting();
        screen.SelectTab(NativeBeatmapDiscoveryScreen.BeatmapDiscoveryTab.Online);
        screen.SelectTab(NativeBeatmapDiscoveryScreen.BeatmapDiscoveryTab.Installed);

        Assert.Multiple(() =>
        {
            Assert.That(screen.GetCurrentTabForTesting(), Is.EqualTo(NativeBeatmapDiscoveryScreen.BeatmapDiscoveryTab.Installed));
            Assert.That(screen.GetActiveScreenTypeForTesting(), Is.EqualTo(typeof(NativeInstalledBeatmapBrowser)));
            Assert.That(screen.GetActiveScreenForTesting(), Is.SameAs(installed));
        });
    }
}

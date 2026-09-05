using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public class HubReplayExportIntegrationTests
{
    [Test]
    public async Task ExportRealLazerReplayWithoutPlaybackAssets()
    {
        string? root = Environment.GetEnvironmentVariable("AIMMOD_VERIFY_LOCAL_SHARE_EXPORT_ROOT");
        string? executable = Environment.GetEnvironmentVariable("AIMMOD_VERIFY_SHARE_WORKER_PATH");
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(executable))
            Assert.Ignore("Set AIMMOD_VERIFY_LOCAL_SHARE_EXPORT_ROOT and AIMMOD_VERIFY_SHARE_WORKER_PATH to opt into read-only local replay export verification.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var runtime = SidecarRuntimeClient.Start(executable!);
        var requests = new SidecarRuntimeRequestClient(runtime);
        var catalog = new ExternalLazerCatalogClient(requests);
        var source = new ExternalLazerLocalLibrarySource(root!, catalog.SearchAsync);
        LocalLibraryPage<LocalReplay> page = await source.SearchReplaysAsync(new LocalLibraryQuery(
            RulesetShortName: "osu", Sort: LocalLibrarySort.RecentlyPlayed, Limit: 30), timeout.Token);
        LocalReplay replay = page.Items.First(item => item.HasReplayFile);
        Assert.That(replay.ReplayPath, Is.Empty, "Exercise the lazer summary without a filesystem replay path.");

        var opener = new ExternalLazerReplayOpenService(root!, new ExternalLazerAssetClient(requests));
        string exported;
        await using (IReplayFileLease lease = await opener.OpenReplayFileAsync(replay, timeout.Token))
        {
            exported = lease.ReplayPath;
            Assert.That(Path.IsPathFullyQualified(exported), Is.True);
            Assert.That(new FileInfo(exported).Length, Is.GreaterThan(64));
            TestContext.WriteLine("Exported a real lazer replay without opening playback or uploading data.");
        }
        Assert.That(File.Exists(exported), Is.False, "The temporary export must be released after use.");
    }
}

using AimMod.Desktop.Discovery;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Trainers;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class StableInstallationSmokeTests
{
    [Test]
    public async Task ReadsInstalledStableLibraryAndControls()
    {
        string? root = Environment.GetEnvironmentVariable("AIMMOD_REAL_STABLE_ROOT");
        if (string.IsNullOrWhiteSpace(root)) Assert.Ignore("Select a stable installation with AIMMOD_REAL_STABLE_ROOT.");
        var platform = OperatingSystem.IsWindows() ? OsuHostPlatform.Windows : OsuHostPlatform.Linux;
        var found = new OsuStableDiscoveryService(new PhysicalOsuDiscoveryFileSystem()).Discover(platform,
            new OsuDiscoveryEnvironment(ExplicitStableRoot: root, CurrentUserName: Environment.UserName));
        var installation = found.CompleteInstallations.Single();
        var source = new OsuStableLocalLibrarySource(installation.CanonicalPath, installation.SongsPath);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var maps = await source.SearchBeatmapSetsAsync(new(Limit: 10), timeout.Token);
        var replays = await source.SearchReplaysAsync(new(Limit: 10), timeout.Token);
        Assert.That(maps.Warning, Is.Null);
        Assert.That(replays.Warning, Is.Null);
        Assert.That(maps.Items.SelectMany(set => set.Difficulties).All(d => d.Origin == LocalLibraryOrigin.Stable), Is.True);
        if (installation.ConfigurationPath is { } config)
        {
            Assert.That(new FileInfo(config).Length, Is.LessThan(1024 * 1024));
            var controls = TrainerOsuSettingsReader.Stable(await File.ReadAllTextAsync(config, timeout.Token));
            Assert.DoesNotThrow(() => new TrainerSettings(Keys: controls.Keys, OffsetMs: controls.OffsetMs).Validate());
        }
        TestContext.Out.WriteLine($"Stable library readable: {maps.Total} map sets, {replays.Total} local scores; controls valid.");
    }
}

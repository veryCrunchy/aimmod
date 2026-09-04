using AimMod.Desktop.Discovery;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class OsuStableDiscoveryServiceTests
{
    [Test]
    public void DiscoversValidatedExplicitStableInstallation()
    {
        string root = Directory.CreateTempSubdirectory("aimmod-stable-discovery-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "osu!.db"), "database");
            string songs = Directory.CreateDirectory(Path.Combine(root, "Songs")).FullName;
            string skins = Directory.CreateDirectory(Path.Combine(root, "Skins")).FullName;

            OsuStableDiscoveryResult result = new OsuStableDiscoveryService(new PhysicalOsuDiscoveryFileSystem()).Discover(
                OsuHostPlatform.Windows,
                new OsuDiscoveryEnvironment(ExplicitStableRoot: root));

            Assert.Multiple(() =>
            {
                Assert.That(result.CompleteInstallations, Has.Count.EqualTo(1));
                Assert.That(result.CompleteInstallations[0].SongsPath, Is.EqualTo(songs));
                Assert.That(result.CompleteInstallations[0].SkinsPath, Is.EqualTo(skins));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ReportsIncompleteRootWithoutTreatingItAsUsable()
    {
        string root = Directory.CreateTempSubdirectory("aimmod-stable-discovery-").FullName;
        try
        {
            OsuStableDiscoveryResult result = new OsuStableDiscoveryService(new PhysicalOsuDiscoveryFileSystem()).Discover(
                OsuHostPlatform.Windows,
                new OsuDiscoveryEnvironment(ExplicitStableRoot: root));

            Assert.Multiple(() =>
            {
                Assert.That(result.CompleteInstallations, Is.Empty);
                Assert.That(result.Installations.Single().Problems, Does.Contain("osu!.db is missing or empty."));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void UsesConfiguredBeatmapDirectoryOutsideInstallation()
    {
        string root = Directory.CreateTempSubdirectory("aimmod-stable-discovery-").FullName;
        string songs = Directory.CreateTempSubdirectory("aimmod-stable-songs-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "osu!.db"), "database");
            File.WriteAllText(Path.Combine(root, "osu!.player.cfg"), $"Username = player\nBeatmapDirectory = \"{songs}\"\n");

            OsuStableDiscoveryResult result = new OsuStableDiscoveryService(new PhysicalOsuDiscoveryFileSystem()).Discover(
                OsuHostPlatform.Windows,
                new OsuDiscoveryEnvironment(ExplicitStableRoot: root, CurrentUserName: "player"));

            Assert.That(result.CompleteInstallations.Single().SongsPath, Is.EqualTo(songs));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(songs, recursive: true);
        }
    }
}

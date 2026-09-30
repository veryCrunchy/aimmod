using AimMod.Desktop.Discovery;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class OsuStableDiscoveryServiceTests
{
    [TestCase("\"D:\\Games\\osu!\\osu!.exe\" \"%1\"", "D:\\Games\\osu!")]
    [TestCase("D:\\Rhythm Games\\osu!\\osu!.exe %1", "D:\\Rhythm Games\\osu!")]
    [TestCase("\"D:\\Games\\osu!lazer\\osu!.exe\" \"%1\"", "D:\\Games\\osu!lazer")]
    [TestCase("cmd.exe /c \"D:\\Games\\osu!\\osu!.exe\"", null)]
    [TestCase("\"D:\\Games\\osu!\\osu!.exe", null)]
    [TestCase("osu!.exe %1", null)]
    public void RegistryCommandIsParsedAsAPathAndNeverExecuted(string command, string? expected)
        => Assert.That(WindowsStableInstallationPaths.RootFromCommand(command), Is.EqualTo(expected));

    [Test]
    public void FindsRegisteredNonDefaultStableRootAndRejectsLazerOnlyHint()
    {
        var fs = new SyntheticFileSystem(OsuHostPlatform.Windows, @"D:\Rhythm Games\osu!");
        fs.AddDirectory(@"D:\lazer");
        var result = new OsuStableDiscoveryService(fs).Discover(OsuHostPlatform.Windows,
            new OsuDiscoveryEnvironment(RegisteredStableRoots: [@"D:\lazer", @"D:\Rhythm Games\osu!"]));
        Assert.That(result.CompleteInstallations.Single().CanonicalPath, Is.EqualTo(@"D:\Rhythm Games\osu!"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PresenceIdentityRequiresAnUnambiguousUsernameMatch(bool ambiguous)
    {
        string root = Directory.CreateTempSubdirectory("aimmod-presence-fixture-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "osu!.db"), "database");
            Directory.CreateDirectory(Path.Combine(root, "Songs"));
            File.WriteAllText(Path.Combine(root, "osu!.current.cfg"), "Username = Old Name");
            var presence = new OsuParsers.Database.PresenceDatabase
            {
                OsuVersion = 20250101,
                Players =
                [
                    new OsuParsers.Database.Objects.Player { UserId = 42, Username = "Old Name" },
                    new OsuParsers.Database.Objects.Player { UserId = 99, Username = ambiguous ? "Old Name" : "Other Player" },
                ],
            };
            presence.Save(Path.Combine(root, "presence.db"));
            var result = new OsuStableDiscoveryService(new PhysicalOsuDiscoveryFileSystem()).Discover(HostPlatform,
                new OsuDiscoveryEnvironment(ExplicitStableRoot: root, CurrentUserName: "current"));
            Assert.That(result.CompleteInstallations.Single().RememberedUserId, Is.EqualTo(ambiguous ? (int?)null : 42));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public void TruncatedPresenceDoesNotBreakStableDiscovery()
    {
        var fs = new SyntheticFileSystem(OsuHostPlatform.Linux, "/stable");
        fs.AddConfig("current", "Username = Player", 1);
        fs.AddFile("/stable/presence.db", "truncated");
        Assert.That(discover(fs, OsuHostPlatform.Linux, "/stable", "current").CompleteInstallations.Single().RememberedUserId, Is.Null);
    }

    [TestCase(OsuHostPlatform.Windows, @"C:\Games\osu!", @"D:\Beatmaps")]
    [TestCase(OsuHostPlatform.Linux, "/games/osu", "/beatmaps")]
    public void SelectsCurrentOsUserConfigurationWithoutMixingStaleValues(OsuHostPlatform platform, string root, string oldSongs)
    {
        var fs = new SyntheticFileSystem(platform, root);
        fs.AddDirectory(oldSongs);
        fs.AddConfig("old", $"Username = PreviousAccount\nBeatmapDirectory = {oldSongs}", 20);
        fs.AddConfig("desktop-user", "Username = CurrentAccount", 10);

        var installation = discover(fs, platform, root, "desktop-user").CompleteInstallations.Single();

        Assert.Multiple(() =>
        {
            Assert.That(installation.RememberedUsername, Is.EqualTo("CurrentAccount"));
            Assert.That(installation.SongsPath, Is.EqualTo(OsuDiscoveryPath.Combine(platform, root, "Songs")));
        });
    }

    [TestCase("Username =")]
    [TestCase("# Username = OldAccount\nSavePassword = 0")]
    [TestCase("Username = First\nUsername =")]
    public void EmptyOrMissingUsernameDoesNotResurrectAnotherAccount(string config)
    {
        var fs = new SyntheticFileSystem(OsuHostPlatform.Linux, "/stable");
        fs.AddConfig("old", "Username = OldAccount", 20);
        fs.AddConfig("current", config, 10);

        Assert.That(discover(fs, OsuHostPlatform.Linux, "/stable", "current")
            .CompleteInstallations.Single().RememberedUsername, Is.Null);
    }

    [Test]
    public void UsesNewestConfigWhenOsUserHasChanged()
    {
        var fs = new SyntheticFileSystem(OsuHostPlatform.Linux, "/stable");
        fs.AddConfig("old", "Username = OldAccount\nBeatmapDirectory = Missing", 10);
        fs.AddConfig("renamed", "\uFEFFUsername = \"NewAccount\"\nBeatmapDirectory = Songs", 20);

        Assert.That(discover(fs, OsuHostPlatform.Linux, "/stable", "new-os-user")
            .CompleteInstallations.Single().RememberedUsername, Is.EqualTo("NewAccount"));
    }

    [Test]
    public void UnreadableSelectedConfigDoesNotUseStaleConfig()
    {
        var fs = new SyntheticFileSystem(OsuHostPlatform.Linux, "/stable");
        fs.AddConfig("old", "Username = OldAccount\nBeatmapDirectory = Missing", 10);
        fs.AddConfig("current", null, 20);

        var installation = discover(fs, OsuHostPlatform.Linux, "/stable", "current").CompleteInstallations.Single();
        Assert.That(installation.RememberedUsername, Is.Null);
        Assert.That(installation.SongsPath, Is.EqualTo("/stable/Songs"));
    }

    [TestCase(OsuHostPlatform.Windows, @"C:\Games\osu!", @"D:\Beatmaps")]
    [TestCase(OsuHostPlatform.Linux, "/stable", "/beatmaps")]
    public void ResolvesAbsoluteBeatmapPathsForRequestedPlatform(OsuHostPlatform platform, string root, string songs)
    {
        var fs = new SyntheticFileSystem(platform, root);
        fs.AddDirectory(songs);
        fs.AddConfig("current", $"BeatmapDirectory = \"{songs}\"\nUsername = Account", 10);
        Assert.That(discover(fs, platform, root, "current").CompleteInstallations.Single().SongsPath, Is.EqualTo(songs));
    }

    [Test]
    public void MissingSelectedSongsDoesNotFallBackToAnotherUsersLibrary()
    {
        var fs = new SyntheticFileSystem(OsuHostPlatform.Linux, "/stable");
        fs.AddConfig("old", "BeatmapDirectory = Songs", 10);
        fs.AddConfig("current", "BeatmapDirectory = Missing", 20);
        Assert.That(discover(fs, OsuHostPlatform.Linux, "/stable", "current").CompleteInstallations, Is.Empty);
    }

    [Test]
    public void StableAndLazerDiscoveryRemainIndependent()
    {
        var fs = new SyntheticFileSystem(OsuHostPlatform.Linux, "/stable");
        fs.AddConfig("current", "Username = StableAccount", 10);
        fs.AddDirectory("/lazer");
        fs.AddDirectory("/lazer/files");
        fs.AddFile("/lazer/client.realm", "database");
        fs.AddFile("/lazer/game.ini", "Username = DifferentLazerAccount");
        var environment = new OsuDiscoveryEnvironment(ExplicitStableRoot: "/stable", ExplicitDataRoot: "/lazer", CurrentUserName: "current");

        var stable = new OsuStableDiscoveryService(fs).Discover(OsuHostPlatform.Linux, environment);
        var lazer = new OsuLazerDiscoveryService(fs).Discover(OsuHostPlatform.Linux, environment);
        Assert.Multiple(() =>
        {
            Assert.That(stable.CompleteInstallations.Single().RememberedUsername, Is.EqualTo("StableAccount"));
            Assert.That(stable.CompleteInstallations.Single().CanonicalPath, Is.EqualTo("/stable"));
            Assert.That(lazer.CompleteDataRoots.Single().CanonicalPath, Is.EqualTo("/lazer"));
        });
    }

    private static OsuStableDiscoveryResult discover(SyntheticFileSystem fs, OsuHostPlatform platform, string root, string user) =>
        new OsuStableDiscoveryService(fs).Discover(platform, new OsuDiscoveryEnvironment(ExplicitStableRoot: root, CurrentUserName: user));

    private sealed class SyntheticFileSystem : IOsuDiscoveryFileSystem
    {
        private readonly OsuHostPlatform platform;
        private readonly string root;
        private readonly Dictionary<string, DiscoveryEntry> entries;
        private readonly Dictionary<string, string?> contents = new();
        private readonly Dictionary<string, DateTime> configs = new();

        public SyntheticFileSystem(OsuHostPlatform platform, string root)
        {
            this.platform = platform;
            this.root = root;
            entries = new(OsuDiscoveryPath.Comparer(platform));
            AddDirectory(root);
            AddDirectory(OsuDiscoveryPath.Combine(platform, root, "Songs"));
            AddFile(OsuDiscoveryPath.Combine(platform, root, "osu!.db"), "database");
        }

        public void AddDirectory(string path) => entries[path] = new(DiscoveryEntryKind.Directory);
        public void AddFile(string path, string? content)
        {
            entries[path] = new(DiscoveryEntryKind.File, content?.Length ?? 1);
            contents[path] = content;
        }

        public void AddConfig(string user, string? content, int age)
        {
            string path = OsuDiscoveryPath.Combine(platform, root, $"osu!.{user}.cfg");
            AddFile(path, content);
            configs[path] = DateTime.UnixEpoch.AddDays(age);
        }

        public DiscoveryEntry Inspect(string path) => entries.GetValueOrDefault(path, new(DiscoveryEntryKind.Missing));
        public string? CanonicalizeExisting(string path) => entries.ContainsKey(path) ? path : null;
        public string ReadAllText(string path, int maximumBytes) => contents[path] ?? throw new IOException("Synthetic read failure.");
        public IEnumerable<string> EnumerateFiles(string directory, string searchPattern) => directory == root ? configs.Keys : [];
        public DateTime GetLastWriteTimeUtc(string path) => configs.GetValueOrDefault(path);
    }

    private static OsuHostPlatform HostPlatform => OperatingSystem.IsWindows() ? OsuHostPlatform.Windows : OsuHostPlatform.Linux;
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
                HostPlatform,
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
                HostPlatform,
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
                HostPlatform,
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

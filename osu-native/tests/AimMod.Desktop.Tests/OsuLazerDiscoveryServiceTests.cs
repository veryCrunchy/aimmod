using AimMod.Desktop.Discovery;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class OsuLazerDiscoveryServiceTests
{
    [Test]
    public void DiscoversLinuxOverrideAndConventionalLocations()
    {
        var fileSystem = new SyntheticFileSystem(OsuHostPlatform.Linux);
        addCompleteRoot(fileSystem, "/override/osu");
        addCompleteRoot(fileSystem, "/xdg/osu");
        addCompleteRoot(fileSystem, "/home/test/.local/share/osu");
        addCompleteRoot(fileSystem, "/home/test/.var/app/sh.ppy.osu/data/osu");

        OsuLazerDiscoveryResult result = discover(fileSystem, OsuHostPlatform.Linux, new OsuDiscoveryEnvironment(
            HomeDirectory: "/home/test",
            XdgDataHome: "/xdg",
            ExplicitDataRoot: "/override/osu"));

        Assert.Multiple(() =>
        {
            Assert.That(result.CompleteDataRoots, Has.Count.EqualTo(4));
            Assert.That(result.CompleteDataRoots.Single(root => root.CanonicalPath == "/override/osu").Sources,
                Is.EqualTo(new[] { OsuDataRootSource.EnvironmentOverride }));
        });
    }

    [Test]
    public void FollowsWindowsStorageRedirect()
    {
        var fileSystem = new SyntheticFileSystem(OsuHostPlatform.Windows);
        fileSystem.AddDirectory(@"C:\Users\test\AppData\Roaming\osu");
        fileSystem.AddFile(@"C:\Users\test\AppData\Roaming\osu\storage.ini", "[Storage]\r\nFullPath = \"D:\\Games\\osu-data\"\r\n");
        addCompleteRoot(fileSystem, @"D:\Games\osu-data");

        OsuLazerDiscoveryResult result = discover(fileSystem, OsuHostPlatform.Windows,
            new OsuDiscoveryEnvironment(AppData: @"C:\Users\test\AppData\Roaming"));

        Assert.That(result.CompleteDataRoots, Has.Count.EqualTo(1));
        Assert.That(result.CompleteDataRoots[0].CanonicalPath, Is.EqualTo(@"D:\Games\osu-data"));
        Assert.That(result.CompleteDataRoots[0].Sources, Is.EqualTo(new[] { OsuDataRootSource.StorageRedirect }));
    }

    [Test]
    public void DiscoversMacOsConventionalLocation()
    {
        var fileSystem = new SyntheticFileSystem(OsuHostPlatform.MacOS);
        addCompleteRoot(fileSystem, "/Users/test/Library/Application Support/osu");

        OsuLazerDiscoveryResult result = discover(fileSystem, OsuHostPlatform.MacOS,
            new OsuDiscoveryEnvironment(HomeDirectory: "/Users/test"));

        Assert.That(result.CompleteDataRoots.Select(root => root.CanonicalPath),
            Is.EqualTo(new[] { "/Users/test/Library/Application Support/osu" }));
    }

    [TestCase(OsuHostPlatform.Linux, "FullPath=relative/path")]
    [TestCase(OsuHostPlatform.Windows, "FullPath=C:relative")]
    [TestCase(OsuHostPlatform.Windows, "FullPath=\\\\server")]
    public void RejectsRelativeStorageRedirects(OsuHostPlatform platform, string contents)
    {
        StorageRedirectParseResult result = OsuLazerDiscoveryService.ParseStorageConfiguration(platform, contents);

        Assert.Multiple(() =>
        {
            Assert.That(result.HasRedirect, Is.False);
            Assert.That(result.Error, Does.Contain("absolute"));
        });
    }

    [Test]
    public void ParsesBomCommentsQuotesAndCaseInsensitiveKey()
    {
        StorageRedirectParseResult result = OsuLazerDiscoveryService.ParseStorageConfiguration(
            OsuHostPlatform.Windows,
            "\uFEFF; generated\r\n[Storage]\r\nfullpath = \"D:\\osu data\"\r\n");

        Assert.Multiple(() =>
        {
            Assert.That(result.Error, Is.Null);
            Assert.That(result.FullPath, Is.EqualTo(@"D:\osu data"));
        });
    }

    [Test]
    public void RejectsDuplicateStorageRedirectKeys()
    {
        StorageRedirectParseResult result = OsuLazerDiscoveryService.ParseStorageConfiguration(
            OsuHostPlatform.Linux,
            "FullPath=/one\nFullPath=/two\n");

        Assert.That(result.Error, Does.Contain("more than one"));
    }

    [Test]
    public void CanonicalisesAndDeduplicatesAliasedCandidates()
    {
        var fileSystem = new SyntheticFileSystem(OsuHostPlatform.Linux);
        fileSystem.AddDirectory("/override-alias", canonicalPath: "/data/osu", symbolicLink: true);
        fileSystem.AddDirectory("/home/test/.local/share/osu", canonicalPath: "/data/osu", symbolicLink: true);
        addCompleteRoot(fileSystem, "/data/osu");

        OsuLazerDiscoveryResult result = discover(fileSystem, OsuHostPlatform.Linux, new OsuDiscoveryEnvironment(
            HomeDirectory: "/home/test",
            ExplicitDataRoot: "/override-alias"));

        Assert.Multiple(() =>
        {
            Assert.That(result.CompleteDataRoots, Has.Count.EqualTo(1));
            Assert.That(result.CompleteDataRoots[0].CanonicalPath, Is.EqualTo("/data/osu"));
            Assert.That(result.CompleteDataRoots[0].Sources, Is.EqualTo(new[]
            {
                OsuDataRootSource.EnvironmentOverride,
                OsuDataRootSource.ConventionalLocation,
            }));
        });
    }

    [Test]
    public void RejectsMarkerSymlinkThatEscapesRoot()
    {
        var fileSystem = new SyntheticFileSystem(OsuHostPlatform.Linux);
        fileSystem.AddDirectory("/data/osu");
        fileSystem.AddFile("/data/osu/client.realm", "realm");
        fileSystem.AddDirectory("/data/osu/files", canonicalPath: "/outside/files", symbolicLink: true);
        fileSystem.AddFile("/data/osu/game.ini", "settings");

        OsuLazerDiscoveryResult result = discover(fileSystem, OsuHostPlatform.Linux,
            new OsuDiscoveryEnvironment(ExplicitDataRoot: "/data/osu"));

        Assert.Multiple(() =>
        {
            Assert.That(result.CompleteDataRoots, Is.Empty);
            Assert.That(result.DataRoots, Has.Count.EqualTo(1));
            Assert.That(result.DataRoots[0].HasFileStore, Is.False);
            Assert.That(result.DataRoots[0].Problems, Does.Contain("files resolves outside the data root."));
        });
    }

    [Test]
    public void DoesNotFollowSymlinkedStorageConfiguration()
    {
        var fileSystem = new SyntheticFileSystem(OsuHostPlatform.Linux);
        fileSystem.AddDirectory("/xdg/osu");
        fileSystem.AddFile("/xdg/osu/storage.ini", "FullPath=/secret/osu", symbolicLink: true);
        addCompleteRoot(fileSystem, "/secret/osu");

        OsuLazerDiscoveryResult result = discover(fileSystem, OsuHostPlatform.Linux,
            new OsuDiscoveryEnvironment(XdgDataHome: "/xdg"));

        Assert.Multiple(() =>
        {
            Assert.That(result.DataRoots, Is.Empty);
            Assert.That(result.RejectedCandidates.Any(candidate => candidate.Reason.Contains("symbolic link", StringComparison.Ordinal)), Is.True);
            Assert.That(fileSystem.ReadPaths, Is.Empty);
        });
    }

    [Test]
    public void RejectsOversizedStorageConfigurationWithoutReadingIt()
    {
        var fileSystem = new SyntheticFileSystem(OsuHostPlatform.Linux);
        fileSystem.AddDirectory("/xdg/osu");
        fileSystem.AddFile("/xdg/osu/storage.ini", "FullPath=/data/osu", length: OsuLazerDiscoveryService.MaximumStorageConfigurationBytes + 1);

        OsuLazerDiscoveryResult result = discover(fileSystem, OsuHostPlatform.Linux,
            new OsuDiscoveryEnvironment(XdgDataHome: "/xdg"));

        Assert.Multiple(() =>
        {
            Assert.That(result.RejectedCandidates.Any(candidate => candidate.Reason.Contains("64 KiB", StringComparison.Ordinal)), Is.True);
            Assert.That(fileSystem.ReadPaths, Is.Empty);
        });
    }

    [Test]
    public void ReportsPartialRootWithoutTreatingItAsReady()
    {
        var fileSystem = new SyntheticFileSystem(OsuHostPlatform.Linux);
        fileSystem.AddDirectory("/data/osu");
        fileSystem.AddFile("/data/osu/client.realm", "realm");
        fileSystem.AddDirectory("/data/osu/files");

        OsuLazerDiscoveryResult result = discover(fileSystem, OsuHostPlatform.Linux,
            new OsuDiscoveryEnvironment(ExplicitDataRoot: "/data/osu"));

        Assert.Multiple(() =>
        {
            Assert.That(result.CompleteDataRoots, Is.Empty);
            Assert.That(result.DataRoots[0].Problems, Does.Contain("game.ini is missing."));
        });
    }

    [Test]
    public void WindowsDeduplicationIsCaseInsensitive()
    {
        var fileSystem = new SyntheticFileSystem(OsuHostPlatform.Windows);
        fileSystem.AddDirectory(@"D:\OSU", canonicalPath: @"D:\OSU");
        fileSystem.AddDirectory(@"C:\Users\test\AppData\Roaming\osu", canonicalPath: @"d:\osu", symbolicLink: true);
        addCompleteRoot(fileSystem, @"D:\OSU");

        OsuLazerDiscoveryResult result = discover(fileSystem, OsuHostPlatform.Windows, new OsuDiscoveryEnvironment(
            AppData: @"C:\Users\test\AppData\Roaming",
            ExplicitDataRoot: @"D:\OSU"));

        Assert.That(result.CompleteDataRoots, Has.Count.EqualTo(1));
        Assert.That(result.CompleteDataRoots[0].Sources, Has.Count.EqualTo(2));
    }

    private static OsuLazerDiscoveryResult discover(
        SyntheticFileSystem fileSystem,
        OsuHostPlatform platform,
        OsuDiscoveryEnvironment environment) => new OsuLazerDiscoveryService(fileSystem).Discover(platform, environment);

    private static void addCompleteRoot(SyntheticFileSystem fileSystem, string root)
    {
        fileSystem.AddDirectory(root);
        fileSystem.AddFile(fileSystem.Combine(root, "client.realm"), "realm");
        fileSystem.AddDirectory(fileSystem.Combine(root, "files"));
        fileSystem.AddFile(fileSystem.Combine(root, "game.ini"), "settings");
    }

    private sealed class SyntheticFileSystem : IOsuDiscoveryFileSystem
    {
        private readonly OsuHostPlatform platform;
        private readonly Dictionary<string, Node> nodes;

        public SyntheticFileSystem(OsuHostPlatform platform)
        {
            this.platform = platform;
            nodes = new Dictionary<string, Node>(OsuDiscoveryPath.Comparer(platform));
        }

        public List<string> ReadPaths { get; } = new();

        public string Combine(string root, string name) => OsuDiscoveryPath.Combine(platform, root, name);

        public void AddDirectory(string path, string? canonicalPath = null, bool symbolicLink = false) =>
            add(path, new DiscoveryEntry(DiscoveryEntryKind.Directory, IsSymbolicLink: symbolicLink), canonicalPath, null);

        public void AddFile(string path, string contents, string? canonicalPath = null, bool symbolicLink = false, long? length = null) =>
            add(path, new DiscoveryEntry(DiscoveryEntryKind.File, length ?? contents.Length, symbolicLink), canonicalPath, contents);

        public DiscoveryEntry Inspect(string path) => nodes.TryGetValue(normalise(path), out Node? node)
            ? node.Entry
            : new DiscoveryEntry(DiscoveryEntryKind.Missing);

        public string? CanonicalizeExisting(string path) => nodes.TryGetValue(normalise(path), out Node? node)
            ? node.CanonicalPath
            : null;

        public string ReadAllText(string path, int maximumBytes)
        {
            string normalised = normalise(path);
            ReadPaths.Add(normalised);
            Node node = nodes[normalised];
            if (node.Entry.Length > maximumBytes)
                throw new InvalidDataException();
            return node.Contents ?? throw new InvalidDataException();
        }

        private void add(string path, DiscoveryEntry entry, string? canonicalPath, string? contents)
        {
            string normalised = normalise(path);
            nodes[normalised] = new Node(entry, normalise(canonicalPath ?? path), contents);
        }

        private string normalise(string path) => platform == OsuHostPlatform.Windows
            ? path.Replace('/', '\\').TrimEnd('\\')
            : path.Replace('\\', '/').TrimEnd('/');

        private sealed record Node(DiscoveryEntry Entry, string CanonicalPath, string? Contents);
    }
}

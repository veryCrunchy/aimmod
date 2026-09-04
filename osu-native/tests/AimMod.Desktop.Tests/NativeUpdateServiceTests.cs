using AimMod.Desktop.Updates;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class NativeUpdateServiceTests
{
    [TestCase(new[] { "--worker" }, false)]
    [TestCase(new[] { "--probe" }, false)]
    [TestCase(new string[0], true)]
    [TestCase(new[] { "--veloapp-install", "1.0.0" }, true)]
    public void BootstrapRunsOnlyForDesktopAndVelopackModes(string[] arguments, bool expected)
    {
        Assert.That(Program.ShouldRunVelopackBootstrap(arguments), Is.EqualTo(expected));
    }

    [TestCase(NativeUpdatePlatform.Windows, NativeUpdateChannel.Stable, "win-stable")]
    [TestCase(NativeUpdatePlatform.Windows, NativeUpdateChannel.Preview, "win-preview")]
    [TestCase(NativeUpdatePlatform.Linux, NativeUpdateChannel.Stable, "linux-stable")]
    [TestCase(NativeUpdatePlatform.Linux, NativeUpdateChannel.Preview, "linux-preview")]
    public void ResolvesDedicatedPlatformChannel(NativeUpdatePlatform platform, NativeUpdateChannel preference, string expected)
    {
        Assert.That(NativeUpdateFeeds.ChannelFor(platform, preference), Is.EqualTo(expected));
    }

    [Test]
    public void ResolvesFixedReleaseFeeds()
    {
        Assert.Multiple(() =>
        {
            Assert.That(NativeUpdateFeeds.FeedFor(NativeUpdateChannel.Stable), Is.EqualTo(
                "https://github.com/veryCrunchy/aimmod/releases/download/aimmod-osu-stable"));
            Assert.That(NativeUpdateFeeds.FeedFor(NativeUpdateChannel.Preview), Is.EqualTo(
                "https://github.com/veryCrunchy/aimmod/releases/download/aimmod-osu-preview"));
        });
    }

    [Test]
    public void FilePreferenceStorePersistsPreviewChannel()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"aimmod-update-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "channel.txt");

        try
        {
            var store = new FileNativeUpdatePreferenceStore(path);
            store.Save(NativeUpdateChannel.Preview);

            Assert.That(new FileNativeUpdatePreferenceStore(path).Load(), Is.EqualTo(NativeUpdateChannel.Preview));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Test]
    public void UpdateSurfaceConstructsForHomeWorkspace()
    {
        using var service = createService(new FakeBackend { IsInstalled = false });

        Assert.DoesNotThrow(() => new NativeUpdateSurface(service).Dispose());
    }

    [TestCase(1100, true, true)]
    [TestCase(800, false, true)]
    [TestCase(520, false, false)]
    [TestCase(360, false, false)]
    public void UpdateSurfaceKeepsPrimaryContentClearAtNarrowWidths(float width, bool showsChannels, bool showsDetail)
    {
        NativeUpdateSurfaceLayout layout = NativeUpdateSurface.CalculateLayout(width);

        Assert.Multiple(() =>
        {
            Assert.That(layout.ShowChannels, Is.EqualTo(showsChannels));
            Assert.That(layout.ShowDetail, Is.EqualTo(showsDetail));
            Assert.That(layout.TextX + layout.TextWidth, Is.LessThanOrEqualTo(layout.ActionLeft - 12));
            if (layout.ShowChannels)
                Assert.That(layout.TextX + layout.TextWidth, Is.LessThanOrEqualTo(layout.ChannelLeft - 12));
        });
    }

    [Test]
    public async Task UnpackagedBuildSkipsNetworkCheck()
    {
        var backend = new FakeBackend { IsInstalled = false };
        using var service = createService(backend);

        await service.CheckAsync();

        Assert.Multiple(() =>
        {
            Assert.That(backend.CheckCount, Is.Zero);
            Assert.That(service.State.Stage, Is.EqualTo(NativeUpdateStage.Unavailable));
        });
    }

    [Test]
    public async Task AvailableUpdateDownloadsWithProgressAndRestarts()
    {
        var release = new NativeUpdateRelease("2.3.4", new object());
        var backend = new FakeBackend
        {
            IsInstalled = true,
            Release = release,
            DownloadProgress = [12, 68, 100],
        };
        using var service = createService(backend);

        await service.CheckAsync();
        Assert.That(service.State.Stage, Is.EqualTo(NativeUpdateStage.Available));

        await service.DownloadAsync();
        Assert.Multiple(() =>
        {
            Assert.That(service.State.Stage, Is.EqualTo(NativeUpdateStage.ReadyToRestart));
            Assert.That(service.State.Progress, Is.EqualTo(100));
            Assert.That(backend.Downloaded, Is.SameAs(release));
        });

        service.ApplyAndRestart();
        Assert.That(backend.Applied, Is.SameAs(release));
    }

    [Test]
    public async Task NetworkFailureBecomesRetryableState()
    {
        var backend = new FakeBackend
        {
            IsInstalled = true,
            CheckError = new HttpRequestException("offline"),
        };
        using var service = createService(backend);

        await service.CheckAsync();
        Assert.That(service.State.Stage, Is.EqualTo(NativeUpdateStage.Failed));
    }

    [Test]
    public async Task SelectingPreviewPersistsAndChecksThatChannel()
    {
        var store = new FakePreferenceStore();
        var factory = new FakeBackendFactory(new FakeBackend { IsInstalled = true });
        using var service = new NativeUpdateService(store, factory);

        await service.SelectChannelAsync(NativeUpdateChannel.Preview);

        Assert.Multiple(() =>
        {
            Assert.That(store.Saved, Is.EqualTo(NativeUpdateChannel.Preview));
            Assert.That(factory.CreatedFor, Is.EqualTo(NativeUpdateChannel.Preview));
            Assert.That(service.State.Channel, Is.EqualTo(NativeUpdateChannel.Preview));
        });
    }

    private static NativeUpdateService createService(FakeBackend backend) =>
        new(new FakePreferenceStore(), new FakeBackendFactory(backend));

    private sealed class FakePreferenceStore : INativeUpdatePreferenceStore
    {
        public NativeUpdateChannel Loaded { get; set; } = NativeUpdateChannel.Stable;

        public NativeUpdateChannel? Saved { get; private set; }

        public NativeUpdateChannel Load() => Loaded;

        public void Save(NativeUpdateChannel channel) => Saved = channel;
    }

    private sealed class FakeBackendFactory : INativeUpdateBackendFactory
    {
        private readonly INativeUpdateBackend backend;

        public FakeBackendFactory(INativeUpdateBackend backend)
        {
            this.backend = backend;
        }

        public NativeUpdateChannel? CreatedFor { get; private set; }

        public INativeUpdateBackend Create(NativeUpdateChannel channel)
        {
            CreatedFor = channel;
            return backend;
        }
    }

    private sealed class FakeBackend : INativeUpdateBackend
    {
        public bool IsInstalled { get; init; }

        public string? CurrentVersion { get; init; } = "1.0.0";

        public NativeUpdateRelease? Release { get; init; }

        public Exception? CheckError { get; init; }

        public int[] DownloadProgress { get; init; } = [];

        public int CheckCount { get; private set; }

        public NativeUpdateRelease? Downloaded { get; private set; }

        public NativeUpdateRelease? Applied { get; private set; }

        public Task<NativeUpdateRelease?> CheckForUpdatesAsync()
        {
            CheckCount++;
            return CheckError is null
                ? Task.FromResult(Release)
                : Task.FromException<NativeUpdateRelease?>(CheckError);
        }

        public Task DownloadAsync(NativeUpdateRelease release, Action<int> progress, CancellationToken cancellationToken)
        {
            Downloaded = release;
            foreach (int value in DownloadProgress)
                progress(value);
            return Task.CompletedTask;
        }

        public void ApplyAndRestart(NativeUpdateRelease release) => Applied = release;
    }
}

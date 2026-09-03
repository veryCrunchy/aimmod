using AimMod.Osu.Runtime;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class OnlineBeatmapImportServiceTests
{
    private string temporaryDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"aimmod-import-service-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(temporaryDirectory))
            Directory.Delete(temporaryDirectory, true);
    }

    [Test]
    public async Task ImportsDownloadedArchiveInvalidatesLibraryAndCleansStagingFile()
    {
        string archive = Path.Combine(temporaryDirectory, "download.osz");
        await File.WriteAllBytesAsync(archive, [0x50, 0x4b, 0x03, 0x04]);
        var client = new StubClient
        {
            Download = (_, _, _, _) => Task.FromResult(new OfficialBeatmapDownloadResult(
                OfficialBeatmapRequestStatus.Success,
                archive,
                4)),
        };
        string? importedPath = null;
        int invalidations = 0;
        var service = new OnlineBeatmapImportService(
            client,
            temporaryDirectory,
            (path, _) =>
            {
                importedPath = path;
                Assert.That(File.Exists(path), Is.True);
                return Task.FromResult(true);
            },
            () => invalidations++);

        OnlineBeatmapImportResult result = await service.ImportAsync(createSet());

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(OnlineBeatmapImportStatus.Success));
            Assert.That(importedPath, Is.EqualTo(archive));
            Assert.That(invalidations, Is.EqualTo(1));
            Assert.That(File.Exists(archive), Is.False);
        });
    }

    [Test]
    public async Task DownloadDisabledSetNeverUsesNetwork()
    {
        var client = new StubClient
        {
            Download = (_, _, _, _) => throw new InvalidOperationException("Download was not expected."),
        };
        var service = new OnlineBeatmapImportService(
            client,
            temporaryDirectory,
            (_, _) => throw new InvalidOperationException("Import was not expected."),
            () => throw new InvalidOperationException("Invalidation was not expected."));

        OnlineBeatmapImportResult result = await service.ImportAsync(createSet() with { DownloadDisabled = true });

        Assert.That(result.Status, Is.EqualTo(OnlineBeatmapImportStatus.DownloadDisabled));
    }

    [Test]
    public async Task FailedImportCleansArchiveWithoutInvalidatingLibrary()
    {
        string archive = Path.Combine(temporaryDirectory, "failed.osz");
        await File.WriteAllBytesAsync(archive, [0x50, 0x4b, 0x03, 0x04]);
        var client = new StubClient
        {
            Download = (_, _, _, _) => Task.FromResult(new OfficialBeatmapDownloadResult(
                OfficialBeatmapRequestStatus.Success,
                archive,
                4)),
        };
        int invalidations = 0;
        var service = new OnlineBeatmapImportService(
            client,
            temporaryDirectory,
            (_, _) => Task.FromResult(false),
            () => invalidations++);

        OnlineBeatmapImportResult result = await service.ImportAsync(createSet());

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(OnlineBeatmapImportStatus.ImportFailed));
            Assert.That(invalidations, Is.Zero);
            Assert.That(File.Exists(archive), Is.False);
        });
    }

    [Test]
    public async Task PreservesLazerCopyBeforeAimModImporterConsumesTheDownload()
    {
        string archive = Path.Combine(temporaryDirectory, "handoff.osz");
        await File.WriteAllBytesAsync(archive, [0x50, 0x4b, 0x03, 0x04]);
        var client = new StubClient
        {
            Download = (_, _, _, _) => Task.FromResult(new OfficialBeatmapDownloadResult(
                OfficialBeatmapRequestStatus.Success,
                archive,
                4)),
        };
        var handoff = new StubLazerInstallService();
        var service = new OnlineBeatmapImportService(
            client,
            temporaryDirectory,
            (path, _) =>
            {
                Assert.That(handoff.PreservedSource, Is.EqualTo(path));
                File.Delete(path);
                return Task.FromResult(true);
            },
            () => { },
            handoff);

        OnlineBeatmapImportResult result = await service.ImportAsync(createSet());

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(OnlineBeatmapImportStatus.Success));
            Assert.That(result.LazerArchive, Is.EqualTo(handoff.Archive));
            Assert.That(handoff.PreservedSource, Is.EqualTo(archive));
            Assert.That(handoff.Installed, Is.Null, "Saving in AimMod must not launch lazer without a second click.");
        });
    }

    [Test]
    public async Task FailedAimModImportDiscardsPreparedLazerCopy()
    {
        string archive = Path.Combine(temporaryDirectory, "failed-handoff.osz");
        await File.WriteAllBytesAsync(archive, [0x50, 0x4b, 0x03, 0x04]);
        var client = new StubClient
        {
            Download = (_, _, _, _) => Task.FromResult(new OfficialBeatmapDownloadResult(
                OfficialBeatmapRequestStatus.Success,
                archive,
                4)),
        };
        var handoff = new StubLazerInstallService();
        var service = new OnlineBeatmapImportService(
            client,
            temporaryDirectory,
            (_, _) => Task.FromResult(false),
            () => { },
            handoff);

        OnlineBeatmapImportResult result = await service.ImportAsync(createSet());

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(OnlineBeatmapImportStatus.ImportFailed));
            Assert.That(handoff.Discarded, Is.EqualTo(handoff.Archive));
        });
    }

    [Test]
    public async Task ExplicitLazerInstallDelegatesThePreparedArchive()
    {
        var handoff = new StubLazerInstallService
        {
            InstallResult = new LazerBeatmapInstallResult(LazerBeatmapInstallStatus.LazerStarted),
        };
        var service = new OnlineBeatmapImportService(
            new StubClient(),
            temporaryDirectory,
            (_, _) => Task.FromResult(true),
            () => { },
            handoff);

        LazerBeatmapInstallResult result = await service.InstallInLazerAsync(handoff.Archive);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(LazerBeatmapInstallStatus.LazerStarted));
            Assert.That(handoff.Installed, Is.EqualTo(handoff.Archive));
        });
    }

    [TestCase(OfficialBeatmapRequestStatus.SignedOut, OnlineBeatmapImportStatus.SignedOut)]
    [TestCase(OfficialBeatmapRequestStatus.TokenExpired, OnlineBeatmapImportStatus.TokenExpired)]
    [TestCase(OfficialBeatmapRequestStatus.Unauthorized, OnlineBeatmapImportStatus.Unauthorized)]
    [TestCase(OfficialBeatmapRequestStatus.NetworkError, OnlineBeatmapImportStatus.NetworkError)]
    public async Task PreservesActionableDownloadFailure(
        OfficialBeatmapRequestStatus downloadStatus,
        OnlineBeatmapImportStatus expected)
    {
        var client = new StubClient
        {
            Download = (_, _, _, _) => Task.FromResult(new OfficialBeatmapDownloadResult(downloadStatus)),
        };
        var service = new OnlineBeatmapImportService(client, temporaryDirectory, (_, _) => Task.FromResult(true), () => { });

        OnlineBeatmapImportResult result = await service.ImportAsync(createSet());

        Assert.That(result.Status, Is.EqualTo(expected));
    }

    private static OfficialBeatmapSet createSet() => new(
        123,
        "Title",
        "Title",
        "Artist",
        "Artist",
        "Mapper",
        "",
        "ranked",
        null,
        null,
        0,
        0,
        false,
        false,
        null,
        null,
        null,
        null,
        []);

    private sealed class StubClient : IOfficialBeatmapDiscoveryClient
    {
        public Func<int, string, bool, CancellationToken, Task<OfficialBeatmapDownloadResult>>? Download { get; init; }

        public Task<OfficialBeatmapSearchResult> SearchAsync(
            OfficialBeatmapSearchQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OfficialBeatmapSearchResult.Empty(OfficialBeatmapRequestStatus.Success));

        public Task<OfficialBeatmapDownloadResult> DownloadAsync(
            int beatmapSetId,
            string destinationDirectory,
            bool noVideo = false,
            CancellationToken cancellationToken = default) =>
            Download?.Invoke(beatmapSetId, destinationDirectory, noVideo, cancellationToken)
            ?? throw new InvalidOperationException("No download behavior was configured.");
    }

    private sealed class StubLazerInstallService : ILazerBeatmapInstallService
    {
        public LazerBeatmapArchive Archive { get; } = new(123, Guid.Parse("9f5d6055-5717-4742-88dc-f5957ae04d06"));

        public string? PreservedSource { get; private set; }

        public LazerBeatmapArchive? Discarded { get; private set; }

        public LazerBeatmapArchive? Installed { get; private set; }

        public LazerBeatmapInstallResult InstallResult { get; init; } = new(LazerBeatmapInstallStatus.Sent);

        public Task<LazerBeatmapArchive> PreserveAsync(
            string sourceArchive,
            int beatmapSetId,
            CancellationToken cancellationToken = default)
        {
            PreservedSource = sourceArchive;
            return Task.FromResult(Archive);
        }

        public Task<LazerBeatmapInstallResult> InstallAsync(
            LazerBeatmapArchive archive,
            CancellationToken cancellationToken = default)
        {
            Installed = archive;
            return Task.FromResult(InstallResult);
        }

        public void Discard(LazerBeatmapArchive archive) => Discarded = archive;
    }
}

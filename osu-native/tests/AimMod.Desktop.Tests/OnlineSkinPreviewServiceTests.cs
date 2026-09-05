using System.IO.Compression;
using System.Net;
using AimMod.Desktop.Skins.Online;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class OnlineSkinPreviewServiceTests
{
    private string temporaryDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"aimmod-skin-preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(temporaryDirectory))
            Directory.Delete(temporaryDirectory, true);
    }

    [Test]
    public async Task PreviewCanBeSavedImportedAndCleansTemporaryCopy()
    {
        byte[] archive = createOsk();
        var transport = new ArchiveTransport(archive);
        OnlineSkinPreviewService service = createService(transport);

        OnlineSkinPreviewResult result = await service.PrepareAsync(createEntry());

        Assert.That(result.Status, Is.EqualTo(OnlineSkinDownloadStatus.Success));
        await using OnlineSkinPreview preview = result.Preview!;
        Assert.That(preview.IsAvailable, Is.True);
        string saved = await service.SaveAsync(preview, Path.Combine(temporaryDirectory, "saved"));
        var destination = new RecordingDestination();
        OnlineSkinImportResult imported = await service.ImportAsync(preview, destination);
        string temporaryArchive = preview.ArchivePath;

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(saved), Is.True);
            Assert.That(Path.GetExtension(saved), Is.EqualTo(".osk"));
            Assert.That(imported.Success, Is.True);
            Assert.That(destination.ImportedPath, Is.EqualTo(temporaryArchive));
            Assert.That(preview.CacheKey, Does.Match("^download:[0-9a-f]{64}$"));
        });

        await preview.DisposeAsync();
        Assert.That(File.Exists(temporaryArchive), Is.False);
    }

    [Test]
    public async Task VerifiedArchiveCacheAvoidsSecondNetworkDownload()
    {
        var transport = new ArchiveTransport(createOsk());
        OnlineSkinPreviewService service = createService(transport);

        OnlineSkinPreviewResult first = await service.PrepareAsync(createEntry());
        await first.Preview!.DisposeAsync();
        OnlineSkinPreviewResult second = await service.PrepareAsync(createEntry());
        await second.Preview!.DisposeAsync();

        Assert.That(transport.RequestCount, Is.EqualTo(1));
    }

    [Test]
    public async Task SensitivePreviewRequiresExplicitConfirmation()
    {
        var transport = new ArchiveTransport(createOsk());
        OnlineSkinPreviewService service = createService(transport);
        OnlineSkinCatalogEntry sensitive = createEntry() with { IsSensitive = true };

        OnlineSkinPreviewResult rejected = await service.PrepareAsync(sensitive);
        OnlineSkinPreviewResult confirmed = await service.PrepareAsync(sensitive, allowSensitive: true);

        Assert.Multiple(() =>
        {
            Assert.That(rejected.Status, Is.EqualTo(OnlineSkinDownloadStatus.Rejected));
            Assert.That(confirmed.Status, Is.EqualTo(OnlineSkinDownloadStatus.Success));
            Assert.That(transport.RequestCount, Is.EqualTo(1));
        });
        await confirmed.Preview!.DisposeAsync();
    }

    [Test]
    public async Task SaveRejectsTamperedPreviewAndCancelledSaveLeavesNoFile()
    {
        OnlineSkinPreviewService service = createService(new ArchiveTransport(createOsk()));
        OnlineSkinPreviewResult result = await service.PrepareAsync(createEntry());
        await using OnlineSkinPreview preview = result.Preview!;
        string saved = Path.Combine(temporaryDirectory, "saved");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(async () => await service.SaveAsync(preview, saved, cancellation.Token));
        Assert.That(Directory.Exists(saved), Is.False);
        await File.WriteAllTextAsync(preview.ArchivePath, "not an archive");
        Assert.ThrowsAsync<InvalidOperationException>(async () => await service.SaveAsync(preview, saved));
        Assert.That(Directory.Exists(saved), Is.False);
    }

    [Test]
    public async Task ActivePreviewIsNotExpiredDuringAnotherPreparation()
    {
        OnlineSkinPreviewService service = createService(new ArchiveTransport(createOsk()));
        OnlineSkinPreviewResult result = await service.PrepareAsync(createEntry());
        await using OnlineSkinPreview preview = result.Preview!;
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(preview.ArchivePath)!, DateTime.UtcNow.AddDays(-1));
        await service.CleanupExpiredAsync();
        Assert.That(preview.IsAvailable, Is.True);
    }

    [Test]
    public async Task SavedVariantNamePreservesVariantIdentity()
    {
        OnlineSkinPreviewService service = createService(new ArchiveTransport(createOsk()));
        OnlineSkinPreviewResult result = await service.PrepareAsync(createEntry() with { Variant = "HD" });
        await using OnlineSkinPreview preview = result.Preview!;
        string saved = await service.SaveAsync(preview, Path.Combine(temporaryDirectory, "saved"));
        Assert.That(Path.GetFileName(saved), Does.EndWith(" - HD.osk"));
    }

    [Test]
    public async Task ExpiredPreviewDirectoriesAreRemoved()
    {
        string stale = Path.Combine(temporaryDirectory, "previews", "preview-stale");
        Directory.CreateDirectory(stale);
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromDays(1));
        OnlineSkinPreviewService service = createService(new ArchiveTransport(createOsk()));

        await service.CleanupExpiredAsync();

        Assert.That(Directory.Exists(stale), Is.False);
    }

    private OnlineSkinPreviewService createService(ISkinHttpTransport transport)
    {
        var validator = new OnlineSkinArchiveValidator();
        var http = new SecureSkinHttpClient(transport);
        var pipeline = new OnlineSkinDownloadResolverPipeline(
            new GoogleDriveSkinDownloadResolver(http, validator),
            new DirectHttpsSkinDownloadResolver(http, validator),
            new ExternalSkinDownloadResolver());
        var cache = new OnlineSkinCatalogCache(Path.Combine(temporaryDirectory, "cache"));
        return new OnlineSkinPreviewService(Path.Combine(temporaryDirectory, "previews"), cache, pipeline, validator);
    }

    private static OnlineSkinCatalogEntry createEntry()
    {
        Uri details = new("https://skins.osuck.net/skins/183");
        return new OnlineSkinCatalogEntry(
            "skins-osuck-net",
            "183",
            "Fixture: Skin?",
            "Creator",
            details,
            [],
            new OnlineSkinSourceAttribution("skins-osuck-net", "skins.osuck.net", details, "Fixture attribution"),
            SkinDownloadTargetClassifier.Classify(new Uri("https://skins.osuck.net/files/fixture.osk")));
    }

    private static byte[] createOsk()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry ini = zip.CreateEntry("skin.ini");
            using StreamWriter writer = new(ini.Open());
            writer.Write("[General]\nName: Fixture\n");
        }
        return stream.ToArray();
    }

    private sealed class ArchiveTransport(byte[] archive) : ISkinHttpTransport
    {
        public int RequestCount { get; private set; }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(archive),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            return Task.FromResult(response);
        }
    }

    private sealed class RecordingDestination : IOnlineSkinArchiveDestination
    {
        public string? ImportedPath { get; private set; }

        public Task<OnlineSkinImportResult> ImportAsync(string validatedOskPath, CancellationToken cancellationToken = default)
        {
            ImportedPath = validatedOskPath;
            return Task.FromResult(new OnlineSkinImportResult(true));
        }
    }
}

using System.IO.Compression;
using System.Net;
using AimMod.Desktop.Skins.Online;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class OnlineSkinDownloadResolverTests
{
    private string temporaryDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"aimmod-skin-resolver-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(temporaryDirectory))
            Directory.Delete(temporaryDirectory, true);
    }

    [Test]
    public async Task DirectHttpsStreamsAndValidatesOsk()
    {
        byte[] osk = createOsk();
        var transport = new FixtureTransport((request, _) => archiveResponse(request, osk));
        OnlineSkinDownloadResolverPipeline pipeline = createPipeline(transport);
        string destination = path("direct.download");

        OnlineSkinResolvedDownload result = await pipeline.ResolveAsync(
            SkinDownloadTargetClassifier.Classify(new Uri("https://cdn.osuskins.net/files/skin.osk")), destination);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(OnlineSkinDownloadStatus.Success));
            Assert.That(result.ArchivePath, Is.EqualTo(destination));
            Assert.That(result.Validation?.IsValid, Is.True);
            Assert.That(result.CacheKey, Does.StartWith("osk:"));
        });
    }

    [Test]
    public async Task TrustedRedirectIsFollowedAndEveryHostIsValidated()
    {
        byte[] osk = createOsk();
        var transport = new FixtureTransport((request, count) => count == 1
            ? redirectResponse(request, "https://cdn.osuskins.net/files/final.osk")
            : archiveResponse(request, osk));
        OnlineSkinDownloadResolverPipeline pipeline = createPipeline(transport);

        OnlineSkinResolvedDownload result = await pipeline.ResolveAsync(
            SkinDownloadTargetClassifier.Classify(new Uri("https://osuskins.net/files/start.osk")), path("redirect.download"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(OnlineSkinDownloadStatus.Success));
            Assert.That(transport.Requests.Select(uri => uri.Host), Is.EqualTo(new[] { "osuskins.net", "cdn.osuskins.net" }));
        });
    }

    [Test]
    public async Task PublicGoogleDriveFileIdUsesDedicatedResolver()
    {
        byte[] osk = createOsk();
        var transport = new FixtureTransport((request, _) => archiveResponse(request, osk));
        OnlineSkinDownloadResolverPipeline pipeline = createPipeline(transport);

        OnlineSkinResolvedDownload result = await pipeline.ResolveAsync(
            SkinDownloadTargetClassifier.Classify(new Uri("https://drive.google.com/file/d/0123456789abcdef/view?usp=sharing")),
            path("drive.download"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(OnlineSkinDownloadStatus.Success));
            Assert.That(transport.Requests.Single().Host, Is.EqualTo("drive.usercontent.google.com"));
            Assert.That(transport.Requests.Single().Query, Does.Contain("id=0123456789abcdef"));
        });
    }

    [Test]
    public async Task MegaAndUnsupportedTargetsProduceBrowserHandoffWithoutNetwork()
    {
        var transport = new FixtureTransport((_, _) => throw new AssertionException("Network was not expected."));
        OnlineSkinDownloadResolverPipeline pipeline = createPipeline(transport);

        OnlineSkinResolvedDownload mega = await pipeline.ResolveAsync(
            SkinDownloadTargetClassifier.Classify(new Uri("https://mega.nz/file/abc#key")), path("mega.download"));
        OnlineSkinResolvedDownload unsupported = await pipeline.ResolveAsync(
            SkinDownloadTargetClassifier.Classify(new Uri("https://files.example/skin.osk")), path("unsupported.download"));

        Assert.Multiple(() =>
        {
            Assert.That(mega.Status, Is.EqualTo(OnlineSkinDownloadStatus.ExternalBrowserRequired));
            Assert.That(mega.Message, Does.Contain("MEGA"));
            Assert.That(unsupported.Status, Is.EqualTo(OnlineSkinDownloadStatus.ExternalBrowserRequired));
            Assert.That(transport.Requests, Is.Empty);
        });
    }

    [Test]
    public async Task RedirectToArbitraryHostIsRejectedBeforeSecondRequest()
    {
        var transport = new FixtureTransport((request, _) => redirectResponse(request, "https://evil.example/payload.osk"));
        OnlineSkinDownloadResolverPipeline pipeline = createPipeline(transport);

        OnlineSkinResolvedDownload result = await pipeline.ResolveAsync(
            SkinDownloadTargetClassifier.Classify(new Uri("https://skins.osuck.net/skins/183/download")), path("evil.download"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(OnlineSkinDownloadStatus.Rejected));
            Assert.That(result.Message, Does.Contain("evil.example"));
            Assert.That(transport.Requests, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task OversizedDeclaredPayloadIsRejectedWithoutWritingArchive()
    {
        var transport = new FixtureTransport((request, _) =>
        {
            var response = archiveResponse(request, [0x50, 0x4b]);
            response.Content.Headers.ContentLength = 10_000;
            return response;
        });
        OnlineSkinArchiveValidator validator = new(new OnlineSkinArchiveLimits(MaximumArchiveBytes: 128));
        var http = new SecureSkinHttpClient(transport);
        var direct = new DirectHttpsSkinDownloadResolver(http, validator, maximumBytes: 128);
        var pipeline = new OnlineSkinDownloadResolverPipeline(direct, new ExternalSkinDownloadResolver());
        string destination = path("large.download");

        OnlineSkinResolvedDownload result = await pipeline.ResolveAsync(
            SkinDownloadTargetClassifier.Classify(new Uri("https://cdn.osuskins.net/files/large.osk")), destination);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(OnlineSkinDownloadStatus.TooLarge));
            Assert.That(File.Exists(destination), Is.False);
        });
    }

    [Test]
    public async Task HtmlInterstitialIsNeverAcceptedAsPreview()
    {
        var transport = new FixtureTransport((request, _) => response(request, HttpStatusCode.OK, "text/html", "<html>sign in</html>"u8.ToArray()));
        OnlineSkinDownloadResolverPipeline pipeline = createPipeline(transport);

        OnlineSkinResolvedDownload result = await pipeline.ResolveAsync(
            SkinDownloadTargetClassifier.Classify(new Uri("https://skins.osuck.net/skins/183/download")), path("html.download"));

        Assert.That(result.Status, Is.EqualTo(OnlineSkinDownloadStatus.ExternalBrowserRequired));
    }

    [Test]
    public async Task ArchiveValidatorRejectsTraversalAndMissingSkinIni()
    {
        string malicious = path("malicious.osk");
        await using (FileStream stream = File.Create(malicious))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            zip.CreateEntry("../skin.ini");
        }
        var validator = new OnlineSkinArchiveValidator();

        OnlineSkinArchiveValidation result = await validator.ValidateAsync(malicious);

        Assert.That(result.ErrorCode, Is.EqualTo("unsafe_entry"));
    }

    private OnlineSkinDownloadResolverPipeline createPipeline(ISkinHttpTransport transport)
    {
        var http = new SecureSkinHttpClient(transport);
        var validator = new OnlineSkinArchiveValidator();
        return new OnlineSkinDownloadResolverPipeline(
            new GoogleDriveSkinDownloadResolver(http, validator),
            new DirectHttpsSkinDownloadResolver(http, validator),
            new ExternalSkinDownloadResolver());
    }

    private string path(string name) => Path.Combine(temporaryDirectory, name);

    private static byte[] createOsk()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry ini = zip.CreateEntry("skin.ini");
            using (StreamWriter writer = new(ini.Open()))
            {
                writer.WriteLine("[General]");
                writer.WriteLine("Name: Fixture");
            }
            ZipArchiveEntry image = zip.CreateEntry("hitcircle.png");
            using Stream target = image.Open();
            target.Write([1, 2, 3, 4]);
        }
        return stream.ToArray();
    }

    private static HttpResponseMessage archiveResponse(HttpRequestMessage request, byte[] bytes) =>
        response(request, HttpStatusCode.OK, "application/octet-stream", bytes);

    private static HttpResponseMessage redirectResponse(HttpRequestMessage request, string location)
    {
        var result = new HttpResponseMessage(HttpStatusCode.Redirect) { RequestMessage = request };
        result.Headers.Location = new Uri(location);
        return result;
    }

    private static HttpResponseMessage response(HttpRequestMessage request, HttpStatusCode status, string contentType, byte[] bytes)
    {
        var result = new HttpResponseMessage(status)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(bytes),
        };
        result.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return result;
    }

    private sealed class FixtureTransport(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : ISkinHttpTransport
    {
        private int count;
        public List<Uri> Requests { get; } = [];

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request, Interlocked.Increment(ref count)));
        }
    }
}

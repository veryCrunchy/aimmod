using System.IO.Compression;
using AimMod.Desktop.Skins.Online;
using Microsoft.Playwright;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class SkinDownloadBrowserTests
{
    private string root = null!;

    [SetUp]
    public void SetUp() => root = Path.Combine(Path.GetTempPath(), "aimmod-browser-test-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    [TestCase("https://osuskins.net/skin/example", true)]
    [TestCase("https://skins.osuck.net/skins/1", true)]
    [TestCase("https://download123.mediafire.com/file/skin.osk", true)]
    [TestCase("https://download123.mediafire.com.evil.example/skin.osk", false)]
    [TestCase("https://fakeosuck.net/file.osk", false)]
    [TestCase("https://osuskins.net.evil.example/skin/example", false)]
    [TestCase("file:///download.osk", false)]
    [TestCase("https://user@osuskins.net/skin/example", false)]
    [TestCase("https://osuskins.net:444/skin/example", false)]
    public void OnlyApprovedDownloadPagesCanOpen(string url, bool allowed) =>
        Assert.That(SkinDownloadBrowserPolicy.CanNavigate(new Uri(url)), Is.EqualTo(allowed));

    [TestCase("skin.osk", true)]
    [TestCase("skin.ZIP", true)]
    [TestCase("skin.osk.exe", false)]
    [TestCase("setup.msi", false)]
    [TestCase("skin.html", false)]
    public void OnlyArchiveCandidatesAreAccepted(string name, bool allowed) =>
        Assert.That(SkinDownloadBrowserPolicy.IsCandidate(name), Is.EqualTo(allowed));

    [TestCase(OnlineSkinDownloadStatus.ExternalBrowserRequired, 1)]
    [TestCase(OnlineSkinDownloadStatus.Unsupported, 1)]
    [TestCase(OnlineSkinDownloadStatus.NetworkError, 1)]
    [TestCase(OnlineSkinDownloadStatus.Rejected, 0)]
    [TestCase(OnlineSkinDownloadStatus.TooLarge, 0)]
    [TestCase(OnlineSkinDownloadStatus.InvalidArchive, 0)]
    public async Task BrowserFallbackDoesNotOverrideSecurityRejections(OnlineSkinDownloadStatus status, int opens)
    {
        var browser = new FakeBrowser(archive());
        var service = serviceWith(new FakeResolver(status), browser);
        var result = await service.PrepareAsync(skin());
        Assert.That(browser.Opens, Is.EqualTo(opens));
        if (opens == 1)
        {
            Assert.That(browser.Page, Is.EqualTo(skin().DetailsUri));
            Assert.That(result.Status, Is.EqualTo(OnlineSkinDownloadStatus.Success));
        }
        if (result.Preview is not null) await result.Preview.DisposeAsync();
    }

    [Test]
    public async Task DirectDownloadDoesNotOpenBrowser()
    {
        var browser = new FakeBrowser(archive());
        var service = serviceWith(new FakeResolver(OnlineSkinDownloadStatus.Success), browser);
        var result = await service.PrepareAsync(skin());
        Assert.That(browser.Opens, Is.Zero);
        Assert.That(result.Status, Is.EqualTo(OnlineSkinDownloadStatus.Success));
        await result.Preview!.DisposeAsync();
    }

    [Test]
    public async Task CapturedSkinCanBeSavedAndImportedButIsNotCachedUnderAnAmbiguousPage()
    {
        var browser = new FakeBrowser(archive());
        var service = serviceWith(new FakeResolver(OnlineSkinDownloadStatus.ExternalBrowserRequired), browser);
        var result = await service.PrepareAsync(skin());
        await using var preview = result.Preview!;
        var destination = new Destination();
        string saved = await service.SaveAsync(preview, Path.Combine(root, "saved"));
        Assert.That(File.Exists(saved), Is.True);
        Assert.That((await service.ImportAsync(preview, destination)).Success, Is.True);
        Assert.That(destination.Path, Is.EqualTo(preview.ArchivePath));
        var cached = await service.PrepareAsync(skin());
        await cached.Preview!.DisposeAsync();
        Assert.That(browser.Opens, Is.EqualTo(2));
    }

    [Test]
    public async Task ProviderBrowsingPreservesActualFilenameAndNeverReusesAnotherSkin()
    {
        var browser = new FakeBrowser(archive());
        var service = serviceWith(new FakeResolver(OnlineSkinDownloadStatus.Success), browser);
        var page = skin() with { Download = null };
        var first = await service.PrepareFromBrowserAsync(page);
        var second = await service.PrepareFromBrowserAsync(page);
        Assert.That(browser.Opens, Is.EqualTo(2));
        Assert.That(first.Preview!.Skin.Name, Is.EqualTo("Downloaded fixture"));
        await first.Preview.DisposeAsync();
        await second.Preview!.DisposeAsync();
    }

    [Test]
    public async Task BrowserCannotSmuggleNonSkinContentsIntoPreview()
    {
        var result = await serviceWith(new FakeResolver(OnlineSkinDownloadStatus.ExternalBrowserRequired), new FakeBrowser("not a zip"u8.ToArray())).PrepareAsync(skin());
        Assert.That(result.Status, Is.EqualTo(OnlineSkinDownloadStatus.InvalidArchive));
        Assert.That(result.Preview, Is.Null);
        Assert.That(Directory.GetFiles(root, "*", SearchOption.AllDirectories), Is.Empty);
    }

    [Test]
    public async Task NoLinkAndCancelledBrowserDoNotStartSaveOrImport()
    {
        var browser = new FakeBrowser([], OnlineSkinDownloadStatus.Cancelled);
        var result = await serviceWith(new FakeResolver(OnlineSkinDownloadStatus.Unsupported), browser).PrepareAsync(skin() with { Download = null });
        Assert.That(result.Status, Is.EqualTo(OnlineSkinDownloadStatus.Cancelled));
        Assert.That(result.Preview, Is.Null);
        Assert.That(browser.Opens, Is.EqualTo(1));
        Assert.That(Directory.GetFiles(root, "*", SearchOption.AllDirectories), Is.Empty);
    }

    [TestCase("fixture.osk", true, OnlineSkinDownloadStatus.Success)]
    [TestCase("fixture.zip", true, OnlineSkinDownloadStatus.Success)]
    [TestCase("fixture.zip", false, OnlineSkinDownloadStatus.InvalidArchive)]
    [TestCase("fixture.exe", false, OnlineSkinDownloadStatus.InvalidArchive)]
    [Explicit("Uses an installed browser with synthetic intercepted pages. No provider network requests.")]
    public async Task BrowserCapturesValidSkinsDiscardsOthersAndCloses(string fileName, bool valid, OnlineSkinDownloadStatus expected)
    {
        bool closed = false;
        var browser = new SkinDownloadBrowser(new(), async (playwright, sessionRoot) =>
        {
            string downloads = Path.Combine(sessionRoot, "downloads");
            Directory.CreateDirectory(downloads);
            var context = await playwright.Chromium.LaunchPersistentContextAsync(Path.Combine(sessionRoot, "profile"), new()
            {
                Channel = Environment.GetEnvironmentVariable("AIMMOD_SKIN_BROWSER_TEST_CHANNEL") ?? "msedge",
                Headless = true, AcceptDownloads = true, DownloadsPath = downloads,
            });
            context.Close += (_, _) => closed = true;
            var page = context.Pages.First();
            await page.RouteAsync("**/*", route => route.Request.Url.EndsWith("/download", StringComparison.Ordinal)
                ? route.FulfillAsync(new()
                {
                    ContentType = "application/octet-stream", BodyBytes = valid ? archive() : "not a skin"u8.ToArray(),
                    Headers = new Dictionary<string, string> { ["Content-Disposition"] = $"attachment; filename=\"{fileName}\"" },
                })
                : route.FulfillAsync(new()
                {
                    ContentType = "text/html",
                    Body = "<title>AimMod synthetic skin download</title><script>setTimeout(()=>location.href='/download',200)</script>",
                }));
            return context;
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await browser.DownloadAsync(new("https://osuskins.net/skin/fixture"), Path.Combine(root, "skin.download"), cancellationToken: timeout.Token);
        Assert.That(result.Status, Is.EqualTo(expected));
        Assert.That(closed, Is.True);
        Assert.That(Directory.GetDirectories(root, "browser-*"), Is.Empty);
        Assert.That(File.Exists(Path.Combine(root, "skin.download")), Is.EqualTo(expected == OnlineSkinDownloadStatus.Success));
    }

    [TestCase(false)]
    [TestCase(true)]
    [Explicit("Uses an installed browser with a synthetic page to check cancellation cleanup.")]
    public async Task BrowserCancellationClosesTheOwnedSession(bool callerCancels)
    {
        bool closed = false;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var browser = new SkinDownloadBrowser(new(), async (playwright, sessionRoot) =>
        {
            Directory.CreateDirectory(Path.Combine(sessionRoot, "downloads"));
            var context = await playwright.Chromium.LaunchPersistentContextAsync(Path.Combine(sessionRoot, "profile"), new()
            {
                Channel = Environment.GetEnvironmentVariable("AIMMOD_SKIN_BROWSER_TEST_CHANNEL") ?? "msedge", Headless = true,
            });
            context.Close += (_, _) => closed = true;
            var page = context.Pages.First();
            await page.RouteAsync("**/*", route => route.FulfillAsync(new() { ContentType = "text/html", Body = "<title>Fixture</title>" }));
            page.DOMContentLoaded += async (_, _) =>
            {
                if (callerCancels) cancellation.Cancel();
                else await context.CloseAsync();
            };
            return context;
        });
        if (callerCancels)
            Assert.CatchAsync<OperationCanceledException>(async () => await browser.DownloadAsync(new("https://osuskins.net/skin/fixture"), Path.Combine(root, "skin.download"), cancellationToken: cancellation.Token));
        else
            Assert.That((await browser.DownloadAsync(new("https://osuskins.net/skin/fixture"), Path.Combine(root, "skin.download"), cancellationToken: cancellation.Token)).Status,
                Is.EqualTo(OnlineSkinDownloadStatus.Cancelled));
        Assert.That(closed, Is.True);
        Assert.That(Directory.GetFileSystemEntries(root), Is.Empty);
    }

    [Test]
    public async Task MissingBrowserCleansUpAndReturnsAnActionableError()
    {
        var browser = new SkinDownloadBrowser(new(), (_, _) => Task.FromResult<IBrowserContext?>(null));
        var result = await browser.DownloadAsync(new("https://osuskins.net/skin/fixture"), Path.Combine(root, "skin.download"));
        Assert.That(result.Status, Is.EqualTo(OnlineSkinDownloadStatus.Unsupported));
        Assert.That(result.Message, Does.Contain("installed"));
        Assert.That(Directory.GetFileSystemEntries(root), Is.Empty);
    }

    private OnlineSkinPreviewService serviceWith(IOnlineSkinDownloadResolver resolver, ISkinDownloadBrowser browser) =>
        new(Path.Combine(root, "previews"), new(Path.Combine(root, "cache")), new(resolver), new(), browser);

    private static OnlineSkinCatalogEntry skin()
    {
        Uri page = new("https://osuskins.net/skin/fixture");
        return new("osuskins-net", "fixture", "Fixture", "Example creator", page, [], new("osuskins-net", "osuskins.net", page, "Fixture"),
            new(new(page, "fixture/download"), OnlineSkinDownloadKind.FormPost, [page.Host], BrowserHandoffUri: page));
    }

    private static byte[] archive()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(zip.CreateEntry("skin.ini").Open())) writer.Write("[General]\nName: Fixture\n");
        return stream.ToArray();
    }

    private sealed class FakeResolver(OnlineSkinDownloadStatus status) : IOnlineSkinDownloadResolver
    {
        public bool CanResolve(OnlineSkinDownloadTarget target) => true;
        public async Task<OnlineSkinResolvedDownload> ResolveAsync(OnlineSkinDownloadTarget target, string path, CancellationToken cancellationToken = default)
        {
            if (status == OnlineSkinDownloadStatus.Success) await File.WriteAllBytesAsync(path, archive(), cancellationToken);
            return new(status, status == OnlineSkinDownloadStatus.Success ? path : null, ExternalUri: target.Uri);
        }
    }

    private sealed class FakeBrowser(byte[] bytes, OnlineSkinDownloadStatus status = OnlineSkinDownloadStatus.Success) : ISkinDownloadBrowser
    {
        public int Opens { get; private set; }
        public Uri? Page { get; private set; }
        public bool CanOpen(Uri page) => SkinDownloadBrowserPolicy.CanNavigate(page);
        public async Task<OnlineSkinResolvedDownload> DownloadAsync(Uri page, string path, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            Opens++;
            Page = page;
            if (status == OnlineSkinDownloadStatus.Success) await File.WriteAllBytesAsync(path, bytes, cancellationToken);
            return new(status, status == OnlineSkinDownloadStatus.Success ? path : null, FileName: "Downloaded fixture.osk");
        }
    }

    private sealed class Destination : IOnlineSkinArchiveDestination
    {
        public string? Path { get; private set; }
        public Task<OnlineSkinImportResult> ImportAsync(string path, CancellationToken cancellationToken = default)
        {
            Path = path;
            return Task.FromResult(new OnlineSkinImportResult(true));
        }
    }
}

using AimMod.Desktop.Skins.Online;
using NUnit.Framework;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class SkinScreenshotCacheTests
{
    [Test]
    public async Task WebpScreenshotsBecomePngAndSurviveRestart()
    {
        string root = Directory.CreateTempSubdirectory("skin-image-test-").FullName;
        try
        {
            using var original = new Image<Rgba32>(64, 32, Color.HotPink);
            using var bytes = new MemoryStream();
            original.SaveAsWebp(bytes);
            var http = new ImageHttp(bytes.ToArray());
            var uri = new Uri("https://cdn.osuskins.net/screenshots/test.webp");
            string path = await new SkinScreenshotCache(http, root).GetAsync(uri);
            using Image restored = Image.Load(path);
            Assert.That(restored.Width, Is.EqualTo(64));
            Assert.That(restored.Height, Is.EqualTo(32));
            Assert.That(Image.DetectFormat(path).Name, Is.EqualTo("PNG"));
            Assert.That(await new SkinScreenshotCache(http, root).GetAsync(uri), Is.EqualTo(path));
            Assert.That(http.Calls, Is.EqualTo(1));
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public void UntrustedScreenshotHostsAreRejectedBeforeNetworkAccess()
    {
        var http = new ImageHttp([]);
        var cache = new SkinScreenshotCache(http, Path.GetTempPath());
        Assert.ThrowsAsync<InvalidDataException>(() => cache.GetAsync(new Uri("https://localhost/private")));
        Assert.That(http.Calls, Is.Zero);
    }

    [Test]
    public async Task ConcurrentPreviewsDownloadAndDecodeOneImage()
    {
        string root = Directory.CreateTempSubdirectory("skin-preview-concurrency-").FullName;
        try
        {
            using var original = new Image<Rgba32>(64, 32, Color.HotPink);
            using var bytes = new MemoryStream();
            original.SaveAsWebp(bytes);
            var http = new ImageHttp(bytes.ToArray());
            var cache = new SkinScreenshotCache(http, root);
            var results = await Task.WhenAll(Enumerable.Range(0, 12)
                .Select(_ => cache.GetAsync(new Uri("https://cdn.osuskins.net/screenshots/test.webp"))));
            Assert.That(results.Distinct().Count(), Is.EqualTo(1));
            Assert.That(http.Calls, Is.EqualTo(1));
            using var restored = Image.Load(results[0]);
            Assert.That(restored.Width, Is.EqualTo(64));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class ImageHttp(byte[] bytes) : ISecureSkinHttpClient
    {
        public int Calls { get; private set; }
        public async Task<SkinHttpPayload> GetBytesAsync(Uri uri, SkinHttpFetchOptions options, CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Delay(25, cancellationToken);
            return new SkinHttpPayload(bytes, uri, "image/webp");
        }
        public Task<SkinHttpFile> DownloadAsync(Uri uri, string destinationPath, SkinHttpFetchOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

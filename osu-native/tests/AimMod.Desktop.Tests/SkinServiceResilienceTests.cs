using System.Net;
using AimMod.Desktop.Skins.Online;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class SkinServiceResilienceTests
{
    private static readonly SkinHttpFetchOptions options = new(
        ["cdn.example.test"], ["application/zip"], 1024 * 1024, TimeSpan.FromSeconds(5));

    private string directory = null!;

    [SetUp]
    public void SetUp() => directory = Directory.CreateTempSubdirectory("aimmod-skin-resilience-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(directory, true);

    [Test]
    public async Task DownloadNeverDeletesAPreExistingDestination()
    {
        string destination = Path.Combine(directory, "skin.osk");
        await File.WriteAllTextAsync(destination, "existing");
        using var client = new SecureSkinHttpClient(new BytesTransport([1, 2, 3]));

        Assert.ThrowsAsync<IOException>(() => client.DownloadAsync(new Uri("https://cdn.example.test/a.osk"), destination, options));

        Assert.That(await File.ReadAllTextAsync(destination), Is.EqualTo("existing"));
    }

    [Test]
    public void FailedDownloadRemovesItsOwnPartialFile()
    {
        string destination = Path.Combine(directory, "partial.osk");
        using var client = new SecureSkinHttpClient(new ResettingTransport());

        var error = Assert.ThrowsAsync<SkinHttpException>(() => client.DownloadAsync(new Uri("https://cdn.example.test/a.osk"), destination, options));

        Assert.Multiple(() =>
        {
            Assert.That(error!.Code, Is.EqualTo("network_error"));
            Assert.That(File.Exists(destination), Is.False);
        });
    }

    [Test]
    public void MidBodyStreamResetsBecomeSkinHttpExceptionsWhenBuffering()
    {
        using var client = new SecureSkinHttpClient(new ResettingTransport());

        var error = Assert.ThrowsAsync<SkinHttpException>(() => client.GetBytesAsync(new Uri("https://cdn.example.test/a.osk"), options));

        Assert.That(error!.Code, Is.EqualTo("network_error"));
    }

    [Test]
    public async Task OneFailingProviderDoesNotHideTheOthers()
    {
        var service = new OnlineSkinCatalogService([new FakeProvider("good", [entry("good", "1")]), new ThrowingProvider("bad")]);

        OnlineSkinCatalogSearchResult result = await service.SearchAsync(new OnlineSkinCatalogQuery());

        Assert.Multiple(() =>
        {
            Assert.That(result.Providers, Has.Count.EqualTo(2));
            Assert.That(result.Providers.Single(provider => provider.ProviderId == "bad").Page.Status, Is.EqualTo(OnlineSkinCatalogStatus.Unavailable));
            Assert.That(result.Items.Select(item => item.Id), Is.EqualTo(new[] { "1" }));
        });
    }

    [Test]
    public void CallerCancellationStillPropagatesFromProviders()
    {
        var service = new OnlineSkinCatalogService([new ThrowingProvider("bad", new OperationCanceledException())]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.CatchAsync<OperationCanceledException>(() => service.SearchAsync(new OnlineSkinCatalogQuery(), cancellationToken: cancellation.Token));
    }

    [Test]
    public async Task CorruptCatalogCacheEntriesAreMissesAndAreEvicted()
    {
        var cache = new OnlineSkinCatalogCache(Path.Combine(directory, "cache"));
        string key = "catalog:good:details:v3:1";
        await cache.PutBytesAsync(key, "{ not json"u8.ToArray(), "catalog");
        var inner = new FakeProvider("good", [entry("good", "1")]);
        var provider = new CachedOnlineSkinCatalogProvider(inner, cache);

        OnlineSkinCatalogEntry? first = await provider.GetDetailsAsync("1");
        OnlineSkinCatalogEntry? second = await provider.GetDetailsAsync("1");

        Assert.Multiple(() =>
        {
            Assert.That(first?.Id, Is.EqualTo("1"));
            Assert.That(second?.Id, Is.EqualTo("1"));
            Assert.That(inner.DetailCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task CatalogCacheWriteFailuresAreNotFatal()
    {
        string blocker = Path.Combine(directory, "blocker");
        await File.WriteAllTextAsync(blocker, "file");
        var provider = new CachedOnlineSkinCatalogProvider(
            new FakeProvider("good", [entry("good", "1")]),
            new OnlineSkinCatalogCache(Path.Combine(blocker, "cache")));

        OnlineSkinCatalogPage page = await provider.SearchAsync(new OnlineSkinCatalogQuery());

        Assert.That(page.Items, Has.Count.EqualTo(1));
    }

    private static OnlineSkinCatalogEntry entry(string provider, string id) => new(
        provider, id, "Skin " + id, "Creator", new Uri("https://example.test/" + id), [],
        new OnlineSkinSourceAttribution(provider, provider, new Uri("https://example.test/"), "notice"));

    private sealed class BytesTransport(byte[] bytes) : ISkinHttpTransport
    {
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes), RequestMessage = request };
            response.Content.Headers.ContentType = new("application/zip");
            return Task.FromResult(response);
        }
    }

    private sealed class ResettingTransport : ISkinHttpTransport
    {
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ResettingStream()), RequestMessage = request };
            response.Content.Headers.ContentType = new("application/zip");
            return Task.FromResult(response);
        }
    }

    private sealed class ResettingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connection reset");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new IOException("connection reset");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private class FakeProvider(string id, IReadOnlyList<OnlineSkinCatalogEntry> entries) : IOnlineSkinCatalogProvider
    {
        public int DetailCalls { get; private set; }
        public string Id => id;
        public string DisplayName => id;
        public Uri HomePage => new("https://example.test/");

        public virtual Task<OnlineSkinCatalogPage> SearchAsync(OnlineSkinCatalogQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OnlineSkinCatalogPage(OnlineSkinCatalogStatus.Success, entries, query.Page, query.PageSize, false));

        public Task<OnlineSkinCatalogEntry?> GetDetailsAsync(string detailId, CancellationToken cancellationToken = default)
        {
            DetailCalls++;
            return Task.FromResult(entries.FirstOrDefault(item => item.Id == detailId));
        }
    }

    private sealed class ThrowingProvider(string id, Exception? error = null) : FakeProvider(id, [])
    {
        public override Task<OnlineSkinCatalogPage> SearchAsync(OnlineSkinCatalogQuery query, CancellationToken cancellationToken = default) =>
            throw (error ?? new InvalidOperationException("provider failure"));
    }
}

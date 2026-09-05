using AimMod.Desktop;
using AimMod.Osu.Runtime;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class CachedOfficialBeatmapDiscoveryClientTests
{
    private string temporaryDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"aimmod-beatmap-cache-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(temporaryDirectory))
            Directory.Delete(temporaryDirectory, true);
    }

    [Test]
    public async Task SuccessfulSearchesAreReusedFromDisk()
    {
        string path = Path.Combine(temporaryDirectory, "search-cache.json");
        var inner = new StubBeatmapClient(new OfficialBeatmapSearchResult(
            OfficialBeatmapRequestStatus.Success,
            new[]
            {
                new OfficialBeatmapSet(
                    123,
                    "Farm map",
                    "Farm map",
                    "Artist",
                    "Artist",
                    "mapper",
                    "",
                    "ranked",
                    null,
                    null,
                    10_000,
                    500,
                    false,
                    false,
                    null,
                    null,
                    null,
                    null,
                    Array.Empty<OfficialBeatmapDifficulty>()),
            },
            1));
        var query = new OfficialBeatmapSearchQuery("farm", MinimumStars: 5, MaximumStars: 6);

        using (var firstClient = new CachedOfficialBeatmapDiscoveryClient(inner, path))
        {
            OfficialBeatmapSearchResult first = await firstClient.SearchAsync(query);
            OfficialBeatmapSearchResult second = await firstClient.SearchAsync(query);

            Assert.Multiple(() =>
            {
                Assert.That(first.Status, Is.EqualTo(OfficialBeatmapRequestStatus.Success));
                Assert.That(second.BeatmapSets[0].Title, Is.EqualTo("Farm map"));
                Assert.That(inner.SearchCount, Is.EqualTo(1));
            });
        }

        using var secondClient = new CachedOfficialBeatmapDiscoveryClient(inner, path);
        OfficialBeatmapSearchResult cached = await secondClient.SearchAsync(query);

        Assert.Multiple(() =>
        {
            Assert.That(cached.Status, Is.EqualTo(OfficialBeatmapRequestStatus.Success));
            Assert.That(cached.BeatmapSets[0].BeatmapSetId, Is.EqualTo(123));
            Assert.That(inner.SearchCount, Is.EqualTo(1));
        });
    }

    [TestCase(OfficialBeatmapRequestStatus.ServerError)]
    [TestCase(OfficialBeatmapRequestStatus.RateLimited)]
    public async Task FailedSearchesAreNotCachedAndDownloadsPassThrough(OfficialBeatmapRequestStatus status)
    {
        string path = Path.Combine(temporaryDirectory, "search-cache.json");
        var inner = new StubBeatmapClient(OfficialBeatmapSearchResult.Empty(status));
        using var client = new CachedOfficialBeatmapDiscoveryClient(inner, path);

        OfficialBeatmapSearchResult first = await client.SearchAsync(new OfficialBeatmapSearchQuery("farm"));
        OfficialBeatmapSearchResult second = await client.SearchAsync(new OfficialBeatmapSearchQuery("farm"));
        OfficialBeatmapDownloadResult download = await client.DownloadAsync(123, temporaryDirectory, noVideo: true);

        Assert.Multiple(() =>
        {
            Assert.That(first.Status, Is.EqualTo(status));
            Assert.That(second.Status, Is.EqualTo(status));
            Assert.That(inner.SearchCount, Is.EqualTo(2));
            Assert.That(inner.DownloadCount, Is.EqualTo(1));
            Assert.That(download.Status, Is.EqualTo(OfficialBeatmapRequestStatus.Success));
        });
    }

    private sealed class StubBeatmapClient(OfficialBeatmapSearchResult searchResult) : IOfficialBeatmapDiscoveryClient
    {
        public int SearchCount { get; private set; }
        public int DownloadCount { get; private set; }

        public Task<OfficialBeatmapSearchResult> SearchAsync(
            OfficialBeatmapSearchQuery query,
            CancellationToken cancellationToken = default)
        {
            SearchCount++;
            return Task.FromResult(searchResult);
        }

        public Task<OfficialBeatmapDownloadResult> DownloadAsync(
            int beatmapSetId,
            string destinationDirectory,
            bool noVideo = false,
            CancellationToken cancellationToken = default)
        {
            DownloadCount++;
            return Task.FromResult(new OfficialBeatmapDownloadResult(OfficialBeatmapRequestStatus.Success, Path.Combine(destinationDirectory, "map.osz"), 1));
        }
    }
}

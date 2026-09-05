using AimMod.Desktop.PpTargets;
using AimMod.Osu.Runtime;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class PpTargetCatalogScannerTests
{
    [Test]
    public async Task AlternatesSortsFollowsOpaqueCursorsAndMergesDuplicateSetDifficulties()
    {
        const string cursor = "opaque+/=cursor";
        var query = new OfficialBeatmapSearchQuery(" farm ", 4, 6, OfficialBeatmapCategory.Ranked, OfficialBeatmapSort.Rating);
        var client = new StubClient((request, _) => Task.FromResult((request.Sort, request.Cursor) switch
        {
            (OfficialBeatmapSort.Rating, null) => success([set(1, 11)], cursor),
            (OfficialBeatmapSort.Plays, null) => success([set(2, 21)]),
            (OfficialBeatmapSort.Favourites, null) => success([set(1, 12)]),
            (OfficialBeatmapSort.Rating, cursor) => success([set(1, 11, 13)]),
            _ => throw new AssertionException("Unexpected page"),
        }));
        var updates = new List<PpTargetCatalogScanProgress>();
        var progress = new InlineProgress(value => { updates.Add(value); query = query with { SearchText = "changed UI query" }; });
        PpTargetCatalogScanResult result = await new PpTargetCatalogScanner(client).ScanAsync(query, progress: progress);
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(OfficialBeatmapRequestStatus.Success));
            Assert.That(result.StopReason, Is.EqualTo(PpTargetCatalogScanStopReason.Completed));
            Assert.That(result.IsPartial, Is.False);
            Assert.That(result.Pages, Is.EqualTo(4));
            Assert.That(result.SetCount, Is.EqualTo(2));
            Assert.That(result.DifficultyCount, Is.EqualTo(4));
            Assert.That(result.BeatmapSets[0].Difficulties.Select(d => d.BeatmapId), Is.EquivalentTo(new[] { 11, 12, 13 }));
            Assert.That(client.Requests.Select(r => r.Sort), Is.EqualTo(new[] { OfficialBeatmapSort.Rating, OfficialBeatmapSort.Plays, OfficialBeatmapSort.Favourites, OfficialBeatmapSort.Rating }));
            Assert.That(client.Requests.All(r => r.SearchText == "farm" && r.MinimumStars == 4 && r.MaximumStars == 6 && r.Limit == 50), Is.True);
            Assert.That(updates.Last(), Is.EqualTo(new PpTargetCatalogScanProgress(4, 2, 4)));
        });
    }

    [Test]
    public async Task RepeatedCursorStopsEachStreamWithoutLosingOtherSorts()
    {
        var client = new StubClient((_, _) => Task.FromResult(success([set(1, 11)], "repeat")));
        PpTargetCatalogScanResult result = await new PpTargetCatalogScanner(client).ScanAsync(new(Sort: OfficialBeatmapSort.Rating));
        Assert.That(result.Pages, Is.EqualTo(6));
        Assert.That(result.StopReason, Is.EqualTo(PpTargetCatalogScanStopReason.RepeatedCursor));
        Assert.That(result.SetCount, Is.EqualTo(1));
        Assert.That(result.DifficultyCount, Is.EqualTo(1));
    }

    [Test]
    public async Task EmptyFilteredPageStillFollowsCursor()
    {
        var client = new StubClient((query, _) => Task.FromResult(query.Cursor is null ? success([], "next") : success([set((int)query.Sort + 1, 11)])));
        PpTargetCatalogScanResult result = await new PpTargetCatalogScanner(client).ScanAsync(new(Sort: OfficialBeatmapSort.Rating));
        Assert.That(result.Pages, Is.EqualTo(6));
        Assert.That(result.SetCount, Is.EqualTo(3));
    }

    [TestCase(OfficialBeatmapRequestStatus.RateLimited)]
    [TestCase(OfficialBeatmapRequestStatus.ServerError)]
    [TestCase(OfficialBeatmapRequestStatus.SessionChanged)]
    public async Task FailurePreservesPartialResultsAndStopsImmediately(OfficialBeatmapRequestStatus status)
    {
        int calls = 0;
        var client = new StubClient((_, _) => Task.FromResult(++calls == 1 ? success([set(1, 11)], "next") : OfficialBeatmapSearchResult.Empty(status)));
        PpTargetCatalogScanResult result = await new PpTargetCatalogScanner(client).ScanAsync(new(Sort: OfficialBeatmapSort.Rating));
        Assert.That(result.Status, Is.EqualTo(status));
        Assert.That(result.SetCount, Is.EqualTo(1));
        Assert.That(result.IsPartial, Is.True);
        Assert.That(calls, Is.EqualTo(2));
    }

    [Test]
    public async Task TransportFailurePreservesPartialResults()
    {
        int calls = 0;
        var client = new StubClient((_, _) => ++calls == 1 ? Task.FromResult(success([set(1, 11)])) : throw new HttpRequestException("offline"));
        PpTargetCatalogScanResult result = await new PpTargetCatalogScanner(client).ScanAsync(new(Sort: OfficialBeatmapSort.Rating));
        Assert.That(result.Status, Is.EqualTo(OfficialBeatmapRequestStatus.NetworkError));
        Assert.That(result.SetCount, Is.EqualTo(1));
    }

    [Test]
    public async Task PageAndSetBudgetsAreStrict()
    {
        int calls = 0;
        var client = new StubClient((_, _) => Task.FromResult(success([set(++calls, calls)], $"cursor-{calls}")));
        var pages = await new PpTargetCatalogScanner(client, maximumPages: 5).ScanAsync(new(Sort: OfficialBeatmapSort.Rating));
        Assert.That(pages.Pages, Is.EqualTo(5));
        Assert.That(pages.StopReason, Is.EqualTo(PpTargetCatalogScanStopReason.PageLimit));
        var many = new StubClient((_, _) => Task.FromResult(success([set(1, 11), set(2, 21), set(3, 31)])));
        var sets = await new PpTargetCatalogScanner(many, maximumSets: 2).ScanAsync(new(Sort: OfficialBeatmapSort.Rating));
        Assert.That(sets.SetCount, Is.EqualTo(2));
        Assert.That(sets.StopReason, Is.EqualTo(PpTargetCatalogScanStopReason.SetLimit));
        Assert.That(many.Requests.Count, Is.EqualTo(1));
    }

    [Test]
    public void CancellationIsHonouredEvenWhenClientIgnoresIt()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new StubClient((_, _) => { cancellation.Cancel(); return Task.FromResult(success([set(1, 11)])); });
        Assert.ThrowsAsync<OperationCanceledException>(async () => await new PpTargetCatalogScanner(client).ScanAsync(new(), cancellation.Token));
        Assert.That(client.Requests.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task CachedDiscoveryPagesSurviveReopenAndSeparateCursorsAndSorts()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"aimmod-scanner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "pages.json");
            var inner = new StubClient((query, _) => Task.FromResult(success([set((int)query.Sort * 10 + (query.Cursor is null ? 1 : 2), 11)], query.Cursor is null ? "next" : null)));
            var query = new OfficialBeatmapSearchQuery(Sort: OfficialBeatmapSort.Rating);
            using (var cache = new CachedOfficialBeatmapDiscoveryClient(inner, path))
                Assert.That((await new PpTargetCatalogScanner(cache).ScanAsync(query)).SetCount, Is.EqualTo(6));
            var offline = new StubClient((_, _) => throw new AssertionException("All cursor/sort pages should be cached."));
            using var reopened = new CachedOfficialBeatmapDiscoveryClient(offline, path);
            PpTargetCatalogScanResult result = await new PpTargetCatalogScanner(reopened).ScanAsync(query);
            Assert.That(result.SetCount, Is.EqualTo(6));
            Assert.That(inner.Requests.Count, Is.EqualTo(6));
            Assert.That(offline.Requests, Is.Empty);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static OfficialBeatmapSearchResult success(IReadOnlyList<OfficialBeatmapSet> sets, string? cursor = null) => new(OfficialBeatmapRequestStatus.Success, sets, NextCursor: cursor);
    private static OfficialBeatmapSet set(int id, params int[] difficulties) => new(id, "Map", "Map", "Artist", "Artist", "Mapper", "", "ranked", null, null, 100, 10, false, false, null, null, null, null,
        difficulties.Select(d => new OfficialBeatmapDifficulty(d, "Difficulty", "osu", 5, 180, 120, 4, 9, 8, 6, 100, 50, 200)).ToArray());
    private sealed class InlineProgress(Action<PpTargetCatalogScanProgress> action) : IProgress<PpTargetCatalogScanProgress>
    {
        public void Report(PpTargetCatalogScanProgress value) => action(value);
    }
    private sealed class StubClient(Func<OfficialBeatmapSearchQuery, CancellationToken, Task<OfficialBeatmapSearchResult>> search) : IOfficialBeatmapDiscoveryClient
    {
        public List<OfficialBeatmapSearchQuery> Requests { get; } = [];
        public Task<OfficialBeatmapSearchResult> SearchAsync(OfficialBeatmapSearchQuery query, CancellationToken cancellationToken = default)
        {
            Requests.Add(query);
            return search(query, cancellationToken);
        }
        public Task<OfficialBeatmapDownloadResult> DownloadAsync(int beatmapSetId, string destinationDirectory, bool noVideo = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

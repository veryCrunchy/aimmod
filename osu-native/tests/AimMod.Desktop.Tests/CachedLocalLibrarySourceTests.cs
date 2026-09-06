using AimMod.Desktop.LocalLibrary;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class CachedLocalLibrarySourceTests
{
    private string root = null!;
    private string database = null!;
    [SetUp] public void SetUp()
    {
        root = Directory.CreateTempSubdirectory("library-cache-test-").FullName;
        database = Path.Combine(root, "scores.db");
        File.WriteAllText(database, "synthetic database");
    }
    [TearDown] public void TearDown() => Directory.Delete(root, true);

    private CachedLocalLibrarySource wrap(ILocalLibrarySource source) => new(source, Path.Combine(root, "cache"), database);

    [Test]
    public async Task RestartReusesMapAndReplayQueriesWithoutScanning()
    {
        var first = new CountingSource();
        var cache = wrap(first);
        await cache.SearchBeatmapSetsAsync(new());
        await cache.SearchReplaysAsync(new());
        var second = new CountingSource();
        var reopened = wrap(second);
        await reopened.SearchBeatmapSetsAsync(new());
        var restored = await reopened.SearchReplaysAsync(new());
        Assert.That(restored.Items.Single().Player, Is.EqualTo("Synthetic player"));
        Assert.That(restored.Items.Single().HasReplayFile, Is.True);
        Assert.That(first.Calls, Is.EqualTo(2));
        Assert.That(second.Calls, Is.Zero);
    }

    [Test]
    public async Task DatabaseChangesAndDifferentQueriesRequireFreshResults()
    {
        var source = new CountingSource();
        var cache = wrap(source);
        await cache.SearchReplaysAsync(new());
        File.AppendAllText(database, "changed");
        await cache.SearchReplaysAsync(new());
        await cache.SearchReplaysAsync(new(SearchText: "another map"));
        Assert.That(source.Calls, Is.EqualTo(3));
    }

    [Test]
    public async Task InvalidationRefreshesOnceAndThenReusesUpdatedCache()
    {
        var source = new CountingSource();
        var cache = wrap(source);
        await cache.SearchReplaysAsync(new());
        cache.Invalidate();
        await cache.SearchReplaysAsync(new());
        await cache.SearchReplaysAsync(new());
        Assert.That(source.Calls, Is.EqualTo(2));
    }

    [Test]
    public async Task CorruptOrDisconnectedCacheCannotHideLiveLibraryErrors()
    {
        var source = new CountingSource();
        var cache = wrap(source);
        await cache.SearchReplaysAsync(new());
        File.WriteAllText(Directory.GetFiles(Path.Combine(root, "cache"), "*.json").Single(), "invalid json");
        cache = wrap(source);
        await cache.SearchReplaysAsync(new());
        File.Delete(database);
        await cache.SearchReplaysAsync(new());
        Assert.That(source.Calls, Is.EqualTo(3));
    }

    [Test]
    public async Task WarmPagesReuseDetachedResultsWithoutReadingDiskAgain()
    {
        var source = new CountingSource();
        var cache = wrap(source);
        var first = await cache.SearchReplaysAsync(new());
        File.Delete(Directory.GetFiles(Path.Combine(root, "cache"), "*.json").Single());
        var second = await cache.SearchReplaysAsync(new());
        Assert.That(second, Is.SameAs(first));
        Assert.That(source.Calls, Is.EqualTo(1));
        File.AppendAllText(database, "new score");
        await cache.SearchReplaysAsync(new());
        Assert.That(source.Calls, Is.EqualTo(2));
    }

    [Test]
    public async Task CachedReplayPageDoesNotWaitBehindBeatmapScan()
    {
        var source = new BlockingSource();
        var cache = wrap(source);
        await cache.SearchReplaysAsync(new());
        Task<LocalLibraryPage<LocalBeatmapSet>> scan = cache.SearchBeatmapSetsAsync(new()).AsTask();
        try
        {
            await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var replay = await cache.SearchReplaysAsync(new()).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
            Assert.That(replay.Items, Has.Count.EqualTo(1));
            Assert.That(scan.IsCompleted, Is.False);
        }
        finally { source.Release.TrySetResult(); await scan; }
    }

    [Test]
    public async Task InvalidationDuringScanDoesNotPublishItsOldResult()
    {
        var source = new BlockingSource();
        var cache = wrap(source);
        var scan = cache.SearchBeatmapSetsAsync(new()).AsTask();
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cache.Invalidate();
        source.Release.TrySetResult();
        await scan;
        await cache.SearchBeatmapSetsAsync(new());
        await cache.SearchBeatmapSetsAsync(new());
        Assert.That(source.MapCalls, Is.EqualTo(2));
    }

    [Test]
    public async Task ConcurrentSameQueryScansOnceAndCancelledWaiterDoesNotCancelScan()
    {
        var source = new BlockingSource();
        var cache = wrap(source);
        var first = cache.SearchBeatmapSetsAsync(new()).AsTask();
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var cancelled = cache.SearchBeatmapSetsAsync(new(), cancellation.Token).AsTask();
        var second = cache.SearchBeatmapSetsAsync(new()).AsTask();
        cancellation.Cancel();
        source.Release.TrySetResult();
        await Task.WhenAll(first, second);
        Assert.CatchAsync<OperationCanceledException>(async () => await cancelled);
        Assert.That(source.MapCalls, Is.EqualTo(1));
    }

    private sealed class BlockingSource : ILocalLibrarySource
    {
        private readonly CountingSource source = new();
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int MapCalls;
        public async ValueTask<LocalLibraryPage<LocalBeatmapSet>> SearchBeatmapSetsAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default)
        {
            MapCalls++;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await source.SearchBeatmapSetsAsync(query, cancellationToken);
        }
        public ValueTask<LocalLibraryPage<LocalReplay>> SearchReplaysAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default) => source.SearchReplaysAsync(query, cancellationToken);
        public void Invalidate() { }
    }

    private sealed class CountingSource : ILocalLibrarySource
    {
        public int Calls { get; private set; }
        public ValueTask<LocalLibraryPage<LocalBeatmapSet>> SearchBeatmapSetsAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(new LocalLibraryPage<LocalBeatmapSet>([], 0, query.Offset, query.Limit));
        }
        public ValueTask<LocalLibraryPage<LocalReplay>> SearchReplaysAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default)
        {
            Calls++;
            LocalReplay replay = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Empty, Guid.Empty,
                "Synthetic map", "Artist", "Difficulty", "osu", "Synthetic player", DateTimeOffset.UnixEpoch,
                5, .98, 1_000_000, 500, 1, null, [], true, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            return ValueTask.FromResult(new LocalLibraryPage<LocalReplay>([replay], 1, query.Offset, query.Limit));
        }
        public void Invalidate() { }
    }
}

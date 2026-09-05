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
        await cache.SearchReplaysAsync(new());
        File.Delete(database);
        await cache.SearchReplaysAsync(new());
        Assert.That(source.Calls, Is.EqualTo(3));
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

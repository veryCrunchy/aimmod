using System.Text.Json;
using AimMod.Desktop.LocalLibrary;
using NUnit.Framework;
using OsuParsers.Enums;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class LocalLibraryMergeTests
{
    private static readonly DateTimeOffset epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [TestCase(LocalLibrarySort.RecentlyAdded, 60)]
    [TestCase(LocalLibrarySort.Title, 37)]
    [TestCase(LocalLibrarySort.RecentlyPlayed, 200)]
    [TestCase(LocalLibrarySort.StarRating, 1)]
    public async Task SequentialPagesMatchIndependentFullPrefixMerges(LocalLibrarySort sort, int limit)
    {
        (LocalBeatmapSet[] first, LocalBeatmapSet[] second, LocalBeatmapSet[] third) = createOverlappingSources();
        CompositeLocalLibrarySource create() => new([
            new InMemoryLocalLibrarySource(first, []),
            new InMemoryLocalLibrarySource(second, []),
            new InMemoryLocalLibrarySource(third, []),
        ]);
        CompositeLocalLibrarySource shared = create();
        int pages = 0;
        for (int offset = 0; offset < 700 && pages < 40; offset += limit, pages++)
        {
            var query = new LocalLibraryQuery(Sort: sort, Offset: offset, Limit: limit);
            LocalLibraryPage<LocalBeatmapSet> reused = await shared.SearchBeatmapSetsAsync(query);
            // A fresh composite reads every source prefix from the start, like the original merge.
            LocalLibraryPage<LocalBeatmapSet> independent = await create().SearchBeatmapSetsAsync(query);
            Assert.That(describe(reused), Is.EqualTo(describe(independent)), $"page at offset {offset}");
        }
    }

    [Test]
    public async Task SequentialReplayPagesMatchIndependentMerges()
    {
        LocalReplay[] a = Enumerable.Range(1, 300).Select(id => replay(id, epoch.AddMinutes(id * 11 % 307), id % 2 == 0)).ToArray();
        LocalReplay[] b = Enumerable.Range(1, 300).Where(id => id % 4 == 0).Select(id => replay(id, epoch.AddMinutes(id * 11 % 307), true))
            .Concat(Enumerable.Range(301, 50).Select(id => replay(id, epoch.AddMinutes(id), false))).ToArray();
        CompositeLocalLibrarySource create() => new([new InMemoryLocalLibrarySource([], a), new InMemoryLocalLibrarySource([], b)]);
        CompositeLocalLibrarySource shared = create();
        for (int offset = 0; offset < 400; offset += 45)
        {
            var query = new LocalLibraryQuery(Offset: offset, Limit: 45);
            Assert.That(describe(await shared.SearchReplaysAsync(query)), Is.EqualTo(describe(await create().SearchReplaysAsync(query))));
        }
    }

    [Test]
    public async Task SequentialPagingReadsEachSourceForwardOnly()
    {
        (LocalBeatmapSet[] first, LocalBeatmapSet[] second, _) = createOverlappingSources();
        var a = new CountingSource(new InMemoryLocalLibrarySource(first, []));
        var b = new CountingSource(new InMemoryLocalLibrarySource(second, []));
        var composite = new CompositeLocalLibrarySource([a, b]);

        for (int offset = 0; offset < 600; offset += 60)
            await composite.SearchBeatmapSetsAsync(new LocalLibraryQuery(Offset: offset, Limit: 60));

        Assert.Multiple(() =>
        {
            Assert.That(a.OffsetZeroReads, Is.EqualTo(1));
            Assert.That(b.OffsetZeroReads, Is.EqualTo(1));
            Assert.That(a.Reads, Is.LessThanOrEqualTo(4));
        });
    }

    [Test]
    public async Task FirstPageAndInvalidateReadFreshData()
    {
        var inner = new MutableSource();
        var composite = new CompositeLocalLibrarySource([inner]);
        Assert.That((await composite.SearchBeatmapSetsAsync(new LocalLibraryQuery())).Items, Has.Count.EqualTo(1));

        inner.Sets = [set(1, epoch), set(2, epoch.AddDays(1))];
        Assert.That((await composite.SearchBeatmapSetsAsync(new LocalLibraryQuery())).Items, Has.Count.EqualTo(2));

        inner.Sets = [set(1, epoch), set(2, epoch.AddDays(1)), set(3, epoch.AddDays(2))];
        composite.Invalidate();
        Assert.That((await composite.SearchBeatmapSetsAsync(new LocalLibraryQuery(Offset: 1))).Items, Has.Count.EqualTo(2));
    }

    [Test]
    public void StableStarRatingFallsBackToTheDifficultyAffectingMods()
    {
        var ratings = new Dictionary<Mods, double> { [Mods.None] = 5, [Mods.DoubleTime] = 7, [Mods.HardRock] = 5.4 };

        Assert.Multiple(() =>
        {
            Assert.That(OsuStableLocalLibrarySource.StarRatingFor(ratings, Mods.DoubleTime, 5), Is.EqualTo(7));
            Assert.That(OsuStableLocalLibrarySource.StarRatingFor(ratings, Mods.DoubleTime | Mods.Hidden, 5), Is.EqualTo(7));
            Assert.That(OsuStableLocalLibrarySource.StarRatingFor(ratings, Mods.Nightcore | Mods.DoubleTime | Mods.Hidden, 5), Is.EqualTo(7));
            Assert.That(OsuStableLocalLibrarySource.StarRatingFor(ratings, Mods.HardRock | Mods.Hidden, 5), Is.EqualTo(5.4));
            Assert.That(OsuStableLocalLibrarySource.StarRatingFor(ratings, Mods.Easy, 4.2), Is.EqualTo(4.2));
        });
    }

    [Test]
    public async Task CorruptStableDatabaseIsNotRebuiltOnEveryCallUntilBackoffExpires()
    {
        string root = Directory.CreateTempSubdirectory("aimmod-stable-backoff-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Songs"));
            await File.WriteAllBytesAsync(Path.Combine(root, "osu!.db"), [1, 2, 3, 4, 5, 6, 7, 8]);
            var time = new ManualTimeProvider();
            var source = new OsuStableLocalLibrarySource(root, Path.Combine(root, "Songs"), time);

            Exception first = Assert.CatchAsync(async () => await source.SearchBeatmapSetsAsync(new LocalLibraryQuery()))!;
            Exception second = Assert.CatchAsync(async () => await source.SearchBeatmapSetsAsync(new LocalLibraryQuery()))!;
            time.Advance(TimeSpan.FromMinutes(1));
            Exception third = Assert.CatchAsync(async () => await source.SearchBeatmapSetsAsync(new LocalLibraryQuery()))!;

            Assert.Multiple(() =>
            {
                Assert.That(second, Is.SameAs(first));
                Assert.That(third, Is.Not.SameAs(first));
            });
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task CachedSourceEvictsTheLeastRecentlyUsedPageFirst()
    {
        string root = Directory.CreateTempSubdirectory("aimmod-cached-lru-").FullName;
        try
        {
            string database = Path.Combine(root, "library.db");
            await File.WriteAllTextAsync(database, "db");
            var inner = new CountingSource(new InMemoryLocalLibrarySource(Enumerable.Range(1, 10).Select(index => set(index, epoch.AddDays(index))), []));
            var cached = new CachedLocalLibrarySource(inner, Path.Combine(root, "cache"), database);

            await cached.SearchBeatmapSetsAsync(new LocalLibraryQuery(Limit: 1));
            for (int limit = 2; limit <= 128; limit++)
                await cached.SearchBeatmapSetsAsync(new LocalLibraryQuery(Limit: limit));
            await cached.SearchBeatmapSetsAsync(new LocalLibraryQuery(Limit: 1));
            await cached.SearchBeatmapSetsAsync(new LocalLibraryQuery(Limit: 129));
            Directory.Delete(Path.Combine(root, "cache"), true);
            int reads = inner.Reads;

            await cached.SearchBeatmapSetsAsync(new LocalLibraryQuery(Limit: 1));
            Assert.That(inner.Reads, Is.EqualTo(reads), "the recently touched page must still be resident");
            await cached.SearchBeatmapSetsAsync(new LocalLibraryQuery(Limit: 2));
            Assert.That(inner.Reads, Is.EqualTo(reads + 1), "the least recently used page is evicted first");
        }
        finally { Directory.Delete(root, true); }
    }

    private static string describe<T>(LocalLibraryPage<T> page) => JsonSerializer.Serialize(new { page.Items, page.Total, page.Offset, page.Limit, page.Warning });

    private static (LocalBeatmapSet[] First, LocalBeatmapSet[] Second, LocalBeatmapSet[] Third) createOverlappingSources()
    {
        // Ids 1..500 in the first source, every third id repeated in the second, every fifth in the third.
        LocalBeatmapSet[] first = Enumerable.Range(1, 500).Select(id => set(id, epoch.AddMinutes(id * 7 % 503))).ToArray();
        LocalBeatmapSet[] second = Enumerable.Range(1, 500).Where(id => id % 3 == 0).Select(id => set(id, epoch.AddMinutes(id * 7 % 509), id % 2 == 0 ? "bg.jpg" : ""))
            .Concat(Enumerable.Range(501, 120).Select(id => set(id, epoch.AddMinutes(id * 7 % 503 + 600)))).ToArray();
        LocalBeatmapSet[] third = Enumerable.Range(1, 500).Where(id => id % 5 == 0).Select(id => set(id, epoch.AddMinutes(id * 7 % 503)))
            .Concat(Enumerable.Range(621, 40).Select(id => set(id, epoch.AddMinutes(id * 7 % 503 + 1200)))).ToArray();
        return (first, second, third);
    }

    private static LocalBeatmapSet set(int onlineId, DateTimeOffset added, string background = "") => new(
        new Guid(onlineId, 0, 0, new byte[8]), onlineId, $"Title {onlineId % 97:0000}", "Artist", "Mapper", "", added, added.AddHours(onlineId % 17),
        [new LocalBeatmapDifficulty(new Guid(onlineId, 1, 0, new byte[8]), onlineId * 10, "Insane", "osu", 3 + onlineId % 5, 180, 120_000, 4, 9, 8, 6, 1, $"{onlineId:x32}")],
        1, background);

    private static LocalReplay replay(int id, DateTimeOffset playedAt, bool hasReplay) => new(
        new Guid(id, 2, hasReplay ? (short)1 : (short)0, new byte[8]), Guid.Empty, Guid.Empty, "Title", "Artist", "Insane", "osu", "player",
        playedAt, 5, 0.9 + id % 10 / 100d, 1_000 * id, 500, 1, null, ["HD"], hasReplay, $"{id:x32}", OnlineScoreId: id);

    private class CountingSource(ILocalLibrarySource inner) : ILocalLibrarySource
    {
        public int Reads { get; private set; }
        public int OffsetZeroReads { get; private set; }

        public ValueTask<LocalLibraryPage<LocalBeatmapSet>> SearchBeatmapSetsAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default)
        {
            Reads++;
            if (query.Offset == 0)
                OffsetZeroReads++;
            return inner.SearchBeatmapSetsAsync(query, cancellationToken);
        }

        public ValueTask<LocalLibraryPage<LocalReplay>> SearchReplaysAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default) =>
            inner.SearchReplaysAsync(query, cancellationToken);

        public void Invalidate() => inner.Invalidate();
    }

    private sealed class MutableSource : ILocalLibrarySource
    {
        public LocalBeatmapSet[] Sets { get; set; } = [set(1, epoch)];

        public ValueTask<LocalLibraryPage<LocalBeatmapSet>> SearchBeatmapSetsAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default) =>
            new InMemoryLocalLibrarySource(Sets, []).SearchBeatmapSetsAsync(query, cancellationToken);

        public ValueTask<LocalLibraryPage<LocalReplay>> SearchReplaysAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LocalLibraryPage<LocalReplay>([], 0, 0, query.Limit));

        public void Invalidate() { }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now += amount;
    }
}

using AimMod.Desktop.LocalLibrary;
using NUnit.Framework;
using Realms;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class RealmLocalReplayMetadataSourceTests
{
    [Test]
    public void SnapshotQueryRunsAgainstRealRealmWithoutUnsupportedTake()
    {
        string path = Path.Combine(Path.GetTempPath(), $"aimmod-replay-query-{Guid.NewGuid():N}.realm");
        var configuration = new RealmConfiguration(path);
        try
        {
            using Realm realm = Realm.GetInstance(configuration);
            Assert.That(RealmLocalReplayMetadataSource.ReadDetachedScores(realm, null), Is.Empty);
        }
        finally
        {
            Realm.DeleteRealm(configuration);
        }
    }

    [Test]
    public async Task SearchesFiltersSortsAndPagesOneDetachedSnapshot()
    {
        var provider = new RecordingSnapshotProvider(new[]
        {
            replay(1, "Target Map", "Artist One", 5.4, 0.95, 200, "HD"),
            replay(2, "Target Map", "Artist One", 5.8, 0.99, 300, "HR"),
            replay(3, "Other Map", "Artist Two", 3.2, 0.97, 100, "HR"),
        });
        using var source = new RealmLocalReplayMetadataSource(provider, 42);

        LocalLibraryPage<LocalReplay> first = await source.SearchReplaysAsync(new LocalLibraryQuery(
            SearchText: "target artist HR",
            MinimumStars: 5,
            MaximumStars: 6,
            Sort: LocalLibrarySort.Accuracy,
            Limit: 1));
        LocalLibraryPage<LocalReplay> second = await source.SearchReplaysAsync(new LocalLibraryQuery(
            MinimumStars: 5,
            Sort: LocalLibrarySort.Score,
            Offset: 1,
            Limit: 1));

        Assert.Multiple(() =>
        {
            Assert.That(provider.ReadCount, Is.EqualTo(1));
            Assert.That(provider.LastUserId, Is.EqualTo(42));
            Assert.That(first.Total, Is.EqualTo(1));
            Assert.That(first.Items.Single().ScoreId, Is.EqualTo(scoreId(2)));
            Assert.That(first.Items.Single().Mods, Does.Contain("HR"));
            Assert.That(second.Total, Is.EqualTo(2));
            Assert.That(second.Offset, Is.EqualTo(1));
            Assert.That(second.Items.Single().ScoreId, Is.EqualTo(scoreId(1)));
        });
    }

    [Test]
    public async Task InvalidateBuildsOneFreshSnapshot()
    {
        var provider = new RecordingSnapshotProvider(new[]
        {
            replay(1, "Map", "Artist", 4, 0.9, 100),
        });
        using var source = new RealmLocalReplayMetadataSource(provider, null);

        await source.SearchReplaysAsync(new LocalLibraryQuery());
        await source.SearchReplaysAsync(new LocalLibraryQuery());
        source.Invalidate();
        await source.SearchReplaysAsync(new LocalLibraryQuery());

        Assert.Multiple(() =>
        {
            Assert.That(provider.ReadCount, Is.EqualTo(2));
            Assert.That(provider.LastUserId, Is.Null);
        });
    }

    [Test]
    public void ACancelledPageWaitDoesNotPublishPartialRows()
    {
        var provider = new BlockingSnapshotProvider();
        using var source = new RealmLocalReplayMetadataSource(provider, 7);
        using var cancellation = new CancellationTokenSource();

        Task<LocalLibraryPage<LocalReplay>> search = source.SearchReplaysAsync(new LocalLibraryQuery(), cancellation.Token).AsTask();
        provider.WaitUntilStarted();
        cancellation.Cancel();

        try
        {
            Assert.CatchAsync<OperationCanceledException>(async () => await search);
        }
        finally
        {
            provider.Release();
        }
    }

    private static LocalReplay replay(
        int id,
        string title,
        string artist,
        double stars,
        double accuracy,
        long totalScore,
        params string[] mods) => new(
            scoreId(id),
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            title,
            artist,
            "Insane",
            "osu",
            "LocalPlayer",
            new DateTimeOffset(2026, 1, id, 0, 0, 0, TimeSpan.Zero),
            stars,
            accuracy,
            totalScore,
            500,
            1,
            100,
            mods,
            true);

    private static Guid scoreId(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");

    private sealed class RecordingSnapshotProvider(IReadOnlyList<LocalReplay> rows) : RealmLocalReplayMetadataSource.ILocalReplaySnapshotProvider
    {
        private int readCount;

        public int ReadCount => Volatile.Read(ref readCount);
        public int? LastUserId { get; private set; }

        public IReadOnlyList<LocalReplay> ReadSnapshot(int? userId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref readCount);
            LastUserId = userId;
            return rows;
        }
    }

    private sealed class BlockingSnapshotProvider : RealmLocalReplayMetadataSource.ILocalReplaySnapshotProvider
    {
        private readonly ManualResetEventSlim started = new();
        private readonly ManualResetEventSlim release = new();

        public IReadOnlyList<LocalReplay> ReadSnapshot(int? userId, CancellationToken cancellationToken)
        {
            started.Set();
            release.Wait(cancellationToken);
            return Array.Empty<LocalReplay>();
        }

        public void WaitUntilStarted() => Assert.That(started.Wait(TimeSpan.FromSeconds(5)), Is.True, "Snapshot did not start.");

        public void Release() => release.Set();
    }
}

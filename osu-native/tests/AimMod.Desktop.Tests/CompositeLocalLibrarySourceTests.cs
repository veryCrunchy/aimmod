using AimMod.Desktop.LocalLibrary;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class CompositeLocalLibrarySourceTests
{
    [Test]
    public async Task AddingLazerPreservesStableAndExistingLibraries()
    {
        var existing = new InMemoryLocalLibrarySource([map(Guid.NewGuid(), Guid.NewGuid(), 10, "")], []);
        var stable = new InMemoryLocalLibrarySource([map(Guid.NewGuid(), Guid.NewGuid(), 20, "")],
            [replay(Guid.NewGuid(), true, LocalLibraryOrigin.Stable)]);
        var lazer = new InMemoryLocalLibrarySource([map(Guid.NewGuid(), Guid.NewGuid(), 30, "")], []);
        var source = new SwitchableLocalLibrarySource(existing);
        source.SwitchTo(new CompositeLocalLibrarySource([source.Current, stable]));
        source.SwitchTo(new CompositeLocalLibrarySource([lazer, source.Current]));

        var maps = await source.SearchBeatmapSetsAsync(new LocalLibraryQuery());
        var replays = await source.SearchReplaysAsync(new LocalLibraryQuery());
        Assert.Multiple(() =>
        {
            Assert.That(maps.Items.Select(item => item.OnlineId), Is.EquivalentTo(new[] { 10, 20, 30 }));
            Assert.That(replays.Items.Single().Origin, Is.EqualTo(LocalLibraryOrigin.Stable));
            Assert.That(replays.Items.Single().HasReplayFile, Is.True);
        });
    }

    [Test]
    public async Task UnresponsiveInstallationDoesNotHideAvailableMaps()
    {
        var source = new CompositeLocalLibrarySource(new ILocalLibrarySource[]
        {
            new UnresponsiveSource(),
            new InMemoryLocalLibrarySource([map(Guid.NewGuid(), Guid.NewGuid(), 42, "")], []),
        }, TimeSpan.FromMilliseconds(30));
        var result = await source.SearchBeatmapSetsAsync(new LocalLibraryQuery());
        Assert.That(result.Items, Has.Count.EqualTo(1));
        Assert.That(result.Warning, Does.Contain("Partial library"));
    }

    [Test]
    public void AllUnavailableInstallationsProduceAnErrorInsteadOfAnEmptyLibrary()
    {
        var source = new CompositeLocalLibrarySource([new UnresponsiveSource()], TimeSpan.FromMilliseconds(30));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await source.SearchReplaysAsync(new LocalLibraryQuery()));
    }

    private sealed class UnresponsiveSource : ILocalLibrarySource
    {
        public async ValueTask<LocalLibraryPage<LocalBeatmapSet>> SearchBeatmapSetsAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new([], 0, 0, 1);
        }
        public async ValueTask<LocalLibraryPage<LocalReplay>> SearchReplaysAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new([], 0, 0, 1);
        }
        public void Invalidate() { }
    }

    [Test]
    public async Task MergesSameOnlineSetAndPrefersPlayableReplay()
    {
        LocalBeatmapSet first = map(Guid.NewGuid(), Guid.NewGuid(), 42, "");
        LocalBeatmapSet second = map(Guid.NewGuid(), Guid.NewGuid(), 42, "background.jpg");
        LocalReplay metadata = replay(Guid.NewGuid(), false, LocalLibraryOrigin.Online);
        LocalReplay playable = replay(Guid.NewGuid(), true, LocalLibraryOrigin.Stable);
        var source = new CompositeLocalLibrarySource(new ILocalLibrarySource[]
        {
            new InMemoryLocalLibrarySource([first], [metadata]),
            new InMemoryLocalLibrarySource([second], [playable]),
        });

        LocalLibraryPage<LocalBeatmapSet> maps = await source.SearchBeatmapSetsAsync(new LocalLibraryQuery());
        LocalLibraryPage<LocalReplay> replays = await source.SearchReplaysAsync(new LocalLibraryQuery());

        Assert.Multiple(() =>
        {
            Assert.That(maps.Total, Is.EqualTo(1));
            Assert.That(maps.Items[0].BackgroundPath, Is.EqualTo("background.jpg"));
            Assert.That(replays.Total, Is.EqualTo(1));
            Assert.That(replays.Items[0].Origin, Is.EqualTo(LocalLibraryOrigin.Stable));
            Assert.That(replays.Items[0].HasReplayFile, Is.True);
        });
    }

    private static LocalBeatmapSet map(Guid setId, Guid beatmapId, int onlineId, string background) => new(
        setId, onlineId, "Title", "Artist", "Mapper", "", DateTimeOffset.UtcNow, null,
        [new LocalBeatmapDifficulty(beatmapId, 84, "Insane", "osu", 5, 180, 120_000, 4, 9, 8, 6, 1, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")],
        1, background);

    private static LocalReplay replay(Guid scoreId, bool hasReplay, LocalLibraryOrigin origin) => new(
        scoreId, Guid.NewGuid(), Guid.NewGuid(), "Title", "Artist", "Insane", "osu", "player",
        new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), 5, 0.98, 1_000_000, 500, 1, null,
        ["Hidden"], hasReplay, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", OnlineScoreId: 99, Origin: origin);
}

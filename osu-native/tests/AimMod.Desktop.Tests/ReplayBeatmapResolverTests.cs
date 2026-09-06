using AimMod.Desktop.LocalLibrary;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class ReplayBeatmapResolverTests
{
    private static LocalReplay replay() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        "Song", "Artist", "Hard", "osu", "Player", DateTimeOffset.UtcNow, 4, .95, 100000, 100, 2, 50, [], true);

    [Test]
    public async Task KnownOnlineDifficultyDoesNotScanLibrary()
    {
        var source = new Library();
        Assert.That(await ReplayBeatmapResolver.ResolveAsync(source, replay() with { OnlineBeatmapId = 123 }, default), Is.EqualTo(123));
        Assert.That(source.Calls, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MatchesExactDifficultyIdentityNotTitle(bool useHash)
    {
        var play = replay() with { BeatmapHash = "ABC" };
        var wrong = new LocalBeatmapDifficulty(Guid.NewGuid(), 100, "Hard", "osu", 4, 180, 100000, 4, 9, 8, 5, 0, "other");
        var right = wrong with { BeatmapId = useHash ? Guid.NewGuid() : play.BeatmapId, OnlineId = 200, BeatmapHash = "abc" };
        var set = new LocalBeatmapSet(Guid.NewGuid(), 1, play.Title, play.Artist, "Mapper", "", DateTimeOffset.UtcNow, null, [wrong, right], 0);
        Assert.That(await ReplayBeatmapResolver.ResolveAsync(new Library(set), play, default), Is.EqualTo(200));
    }

    [Test]
    public void MissingIdentityDoesNotGuessAndCancellationIsHonoured()
    {
        Assert.ThrowsAsync<InvalidOperationException>(async () => await ReplayBeatmapResolver.ResolveAsync(new Library(), replay(), default));
        Assert.ThrowsAsync<OperationCanceledException>(async () => await ReplayBeatmapResolver.ResolveAsync(new Library(), replay() with { OnlineBeatmapId = 123 }, new CancellationToken(true)));
    }

    private sealed class Library(params LocalBeatmapSet[] sets) : ILocalLibrarySource
    {
        public int Calls;
        public ValueTask<LocalLibraryPage<LocalBeatmapSet>> SearchBeatmapSetsAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(new LocalLibraryPage<LocalBeatmapSet>(sets, sets.Length, 0, 200));
        }
        public ValueTask<LocalLibraryPage<LocalReplay>> SearchReplaysAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Invalidate() { }
    }
}

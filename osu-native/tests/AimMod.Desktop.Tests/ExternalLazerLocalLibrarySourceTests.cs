using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class ExternalLazerLocalLibrarySourceTests
{
    [Test]
    public async Task MapsGroupedCatalogSetsIntoTheNativeLibraryModel()
    {
        ExternalLazerCatalogSearchRequest? sent = null;
        Guid setId = Guid.NewGuid();
        Guid beatmapId = Guid.NewGuid();
        var source = new ExternalLazerLocalLibrarySource(
            Path.GetFullPath("lazer"),
            (request, _) =>
            {
                sent = request;
                var difficulty = new ExternalLazerBeatmapDifficulty(
                    beatmapId, 7, new string('a', 64), new string('b', 32), "Insane", "osu", 5.25, 180, 120_000, 4, 9, 8, 6, 3);
                var set = new ExternalLazerBeatmapSet(
                    setId, 9, "Title", "Artist", "Mapper", "Source", DateTimeOffset.UnixEpoch, null, new[] { difficulty }, 2);
                return Task.FromResult(new ExternalLazerCatalogSearchResult(
                    ExternalLazerCatalogEntryKind.BeatmapSets,
                    new[] { set },
                    Array.Empty<ExternalLazerReplaySummary>(),
                    1,
                    request.Offset,
                    request.Limit));
            });

        LocalLibraryPage<LocalBeatmapSet> page = await source.SearchBeatmapSetsAsync(
            new LocalLibraryQuery(SearchText: "title", MinimumStars: 4, MaximumStars: 6, Offset: 0, Limit: 60));

        Assert.Multiple(() =>
        {
            Assert.That(sent?.LibraryRoot, Is.EqualTo(Path.GetFullPath("lazer")));
            Assert.That(sent?.Kind, Is.EqualTo(ExternalLazerCatalogEntryKind.BeatmapSets));
            Assert.That(sent?.MinimumStars, Is.EqualTo(4));
            Assert.That(sent?.MaximumStars, Is.EqualTo(6));
            Assert.That(page.Items.Single().SetId, Is.EqualTo(setId));
            Assert.That(page.Items.Single().Difficulties.Single().BeatmapId, Is.EqualTo(beatmapId));
            Assert.That(page.Items.Single().LocalReplayCount, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task SwitchingSourcesNotifiesAndRoutesTheNextQuery()
    {
        var first = new RecordingSource(1);
        var second = new RecordingSource(2);
        var source = new SwitchableLocalLibrarySource(first);
        int changes = 0;
        source.SourceChanged += () => changes++;

        await source.SearchBeatmapSetsAsync(new LocalLibraryQuery());
        source.SwitchTo(second);
        await source.SearchBeatmapSetsAsync(new LocalLibraryQuery());

        Assert.Multiple(() =>
        {
            Assert.That(first.Calls, Is.EqualTo(1));
            Assert.That(second.Calls, Is.EqualTo(1));
            Assert.That(changes, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task CancellationAfterDispatchWaitsForSnapshotCleanup()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken workerToken = default;
        var source = new ExternalLazerLocalLibrarySource(
            Path.GetFullPath("lazer"),
            async (request, cancellationToken) =>
            {
                workerToken = cancellationToken;
                started.SetResult();
                await release.Task;
                return new ExternalLazerCatalogSearchResult(
                    request.Kind,
                    Array.Empty<ExternalLazerBeatmapSet>(),
                    Array.Empty<ExternalLazerReplaySummary>(),
                    0,
                    request.Offset,
                    request.Limit);
            });
        using var cancellation = new CancellationTokenSource();

        Task query = source.SearchBeatmapSetsAsync(new LocalLibraryQuery(), cancellation.Token).AsTask();
        await started.Task;
        cancellation.Cancel();

        Assert.Multiple(() =>
        {
            Assert.That(query.IsCompleted, Is.False);
            Assert.That(workerToken.CanBeCanceled, Is.False);
        });

        release.SetResult();
        Assert.CatchAsync<OperationCanceledException>(async () => await query);
    }

    private sealed class RecordingSource(int total) : ILocalLibrarySource
    {
        public int Calls { get; private set; }

        public ValueTask<LocalLibraryPage<LocalBeatmapSet>> SearchBeatmapSetsAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(new LocalLibraryPage<LocalBeatmapSet>(Array.Empty<LocalBeatmapSet>(), total, 0, 60));
        }

        public ValueTask<LocalLibraryPage<LocalReplay>> SearchReplaysAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LocalLibraryPage<LocalReplay>(Array.Empty<LocalReplay>(), total, 0, 60));

        public void Invalidate()
        {
        }
    }
}

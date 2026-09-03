using AimMod.Desktop.LocalLibrary;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class LocalLibraryControllerTests
{
    [Test]
    public async Task PublishesLoadingThenEmptyForAnExternalSource()
    {
        var source = new FakeLocalLibrarySource
        {
            BeatmapSearch = (query, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(new LocalLibraryPage<LocalBeatmapSet>(
                    Array.Empty<LocalBeatmapSet>(),
                    0,
                    query.Offset,
                    query.Limit));
            },
        };
        using var controller = new LocalLibraryController(source, NativeLocalLibraryMode.Beatmaps);
        var observed = new List<LocalLibraryLoadStatus>();
        controller.StateChanged += (_, change) => observed.Add(change.State.Status);

        LocalLibraryLoadState result = await controller.LoadAsync(new LocalLibraryQuery());

        Assert.Multiple(() =>
        {
            Assert.That(observed, Is.EqualTo(new[] { LocalLibraryLoadStatus.Loading, LocalLibraryLoadStatus.Empty }));
            Assert.That(result.Status, Is.EqualTo(LocalLibraryLoadStatus.Empty));
            Assert.That(result.ItemCount, Is.Zero);
            Assert.That(result.ErrorMessage, Is.Null);
        });
    }

    [Test]
    public async Task CancelsThePreviousSourceRequestWhenAQueryChanges()
    {
        var firstRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken firstToken = default;
        int callCount = 0;
        var source = new FakeLocalLibrarySource
        {
            BeatmapSearch = async (query, cancellationToken) =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    firstToken = cancellationToken;
                    firstRequestStarted.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                return new LocalLibraryPage<LocalBeatmapSet>(Array.Empty<LocalBeatmapSet>(), 0, query.Offset, query.Limit);
            },
        };
        using var controller = new LocalLibraryController(source, NativeLocalLibraryMode.Beatmaps);

        Task<LocalLibraryLoadState> first = controller.LoadAsync(new LocalLibraryQuery(SearchText: "first"));
        await firstRequestStarted.Task;
        Assert.That(controller.State.Status, Is.EqualTo(LocalLibraryLoadStatus.Loading));
        Task<LocalLibraryLoadState> second = controller.LoadAsync(new LocalLibraryQuery(SearchText: "second"));

        await Task.WhenAll(first, second);

        Assert.Multiple(() =>
        {
            Assert.That(firstToken.IsCancellationRequested, Is.True);
            Assert.That(callCount, Is.EqualTo(2));
            Assert.That(controller.State.Status, Is.EqualTo(LocalLibraryLoadStatus.Empty));
        });
    }

    [Test]
    public async Task ConvertsSourceFailuresIntoRetryableErrorState()
    {
        var source = new FakeLocalLibrarySource
        {
            ReplaySearch = (_, _) => ValueTask.FromException<LocalLibraryPage<LocalReplay>>(
                new InvalidOperationException("worker catalog unavailable")),
        };
        using var controller = new LocalLibraryController(source, NativeLocalLibraryMode.Replays);

        LocalLibraryLoadState result = await controller.LoadAsync(new LocalLibraryQuery());

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(LocalLibraryLoadStatus.Error));
            Assert.That(result.ErrorMessage, Is.EqualTo("worker catalog unavailable"));
            Assert.That(result.ItemCount, Is.Zero);
        });
    }

    [Test]
    public async Task AppendsWorkerPagesWithoutDroppingEarlierRows()
    {
        LocalReplay firstReplay = replay(Guid.Parse("11111111-1111-1111-1111-111111111111"), "First");
        LocalReplay secondReplay = replay(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Second");
        var source = new FakeLocalLibrarySource
        {
            ReplaySearch = (query, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<LocalReplay> items = query.Offset == 0 ? new[] { firstReplay } : new[] { secondReplay };
                return ValueTask.FromResult(new LocalLibraryPage<LocalReplay>(items, 2, query.Offset, 1));
            },
        };
        using var controller = new LocalLibraryController(source, NativeLocalLibraryMode.Replays);

        await controller.LoadAsync(new LocalLibraryQuery(Offset: 0, Limit: 1));
        LocalLibraryLoadState result = await controller.LoadAsync(new LocalLibraryQuery(Offset: 1, Limit: 1), append: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(LocalLibraryLoadStatus.Ready));
            Assert.That(result.Replays.Select(item => item.Title), Is.EqualTo(new[] { "First", "Second" }));
            Assert.That(result.Total, Is.EqualTo(2));
            Assert.That(result.HasMore, Is.False);
        });
    }

    private static LocalReplay replay(Guid id, string title) => new(
        id,
        Guid.Empty,
        Guid.Empty,
        title,
        "Artist",
        "Difficulty",
        "osu",
        "Player",
        DateTimeOffset.UnixEpoch,
        5,
        0.98,
        1_000_000,
        500,
        1,
        null,
        Array.Empty<string>(),
        true);

    private sealed class FakeLocalLibrarySource : ILocalLibrarySource
    {
        public Func<LocalLibraryQuery, CancellationToken, ValueTask<LocalLibraryPage<LocalBeatmapSet>>>? BeatmapSearch { get; init; }

        public Func<LocalLibraryQuery, CancellationToken, ValueTask<LocalLibraryPage<LocalReplay>>>? ReplaySearch { get; init; }

        public ValueTask<LocalLibraryPage<LocalBeatmapSet>> SearchBeatmapSetsAsync(
            LocalLibraryQuery query,
            CancellationToken cancellationToken = default) =>
            BeatmapSearch?.Invoke(query, cancellationToken)
            ?? throw new AssertionException("The beatmap source was not expected to run.");

        public ValueTask<LocalLibraryPage<LocalReplay>> SearchReplaysAsync(
            LocalLibraryQuery query,
            CancellationToken cancellationToken = default) =>
            ReplaySearch?.Invoke(query, cancellationToken)
            ?? throw new AssertionException("The replay source was not expected to run.");

        public void Invalidate()
        {
        }
    }
}

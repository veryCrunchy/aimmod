using System.Collections.Concurrent;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.PpTargets;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public class WorkspacePerformanceTests
{
    [Test]
    public async Task RapidFiltersRunOnlyActiveAndLatestQuery()
    {
        using var worker = new LatestBackgroundQuery<int>();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = new ConcurrentQueue<int>();
        var published = new ConcurrentQueue<int>();
        CancellationToken firstToken = default;
        worker.Submit(token =>
        {
            firstToken = token;
            executed.Enqueue(0);
            started.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return 0;
        }, published.Enqueue, error => finished.TrySetException(error));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (int i = 1; i <= 100; i++)
            {
                int filter = i;
                worker.Submit(_ => { executed.Enqueue(filter); return filter; }, result =>
                {
                    published.Enqueue(result);
                    finished.TrySetResult(result);
                }, error => finished.TrySetException(error));
            }
            Assert.That(firstToken.IsCancellationRequested, Is.True);
        }
        finally { release.Set(); }
        Assert.That(await finished.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(100));
        Assert.That(executed.ToArray(), Is.EqualTo(new[] { 0, 100 }));
        Assert.That(published.ToArray(), Is.EqualTo(new[] { 100 }));
    }

    [Test]
    public async Task WorkerRecoversAfterFailedQuery()
    {
        using var worker = new LatestBackgroundQuery<int>();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.Submit(_ => throw new InvalidOperationException(), _ => Assert.Fail(), _ => failed.SetResult());
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var completed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.Submit(_ => 42, value => completed.SetResult(value), error => completed.SetException(error));
        Assert.That(await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(42));
    }

    [Test]
    public void RankingHonoursCancellationBeforeEnumeratingCatalog()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => PpTargetRanker.Rank(
            PpTargetPreferenceProfile.Empty, [], cancellationToken: cancellation.Token));
    }

    [Test]
    public async Task SynchronousLibrarySourceCannotBlockFilterCaller()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeLocalLibrarySource
        {
            BeatmapSearch = (query, token) =>
            {
                started.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                return ValueTask.FromResult(new LocalLibraryPage<LocalBeatmapSet>([], 0, 0, query.Limit));
            },
        };
        using var controller = new LocalLibraryController(source, NativeLocalLibraryMode.Beatmaps);
        Task<LocalLibraryLoadState> loading;
        try
        {
            loading = controller.LoadAsync(new LocalLibraryQuery());
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(loading.IsCompleted, Is.False, "The caller must regain control while the source is blocked.");
        }
        finally { release.Set(); }
        Assert.That((await loading).Status, Is.EqualTo(LocalLibraryLoadStatus.Empty));
    }
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

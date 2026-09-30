using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.ScoreHistory;
using AimMod.Osu.Runtime;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class ScoreHistorySessionTests
{
    [Test]
    public async Task ConcurrentCallersShareOneLocalLoad()
    {
        int loads = 0;
        var release = new TaskCompletionSource();
        var session = new ScoreHistorySession(new InMemoryLocalLibrarySource([], []), async (_, token) =>
        {
            Interlocked.Increment(ref loads);
            await release.Task.WaitAsync(token);
            return result(play(1));
        });

        Task<StatisticsHistoryLoadResult> first = session.GetLocalAsync();
        Task<StatisticsHistoryLoadResult> second = session.GetLocalAsync(refresh: true);
        release.SetResult();

        Assert.That(await second, Is.SameAs(await first));
        Assert.That(await session.GetLocalAsync(), Is.SameAs(await first));
        Assert.That(loads, Is.EqualTo(1));
    }

    [Test]
    public async Task RefreshReturnsThePreviousInstanceWhenPlaysAreUnchanged()
    {
        LocalReplay[] runs = [play(1), play(2)];
        var session = new ScoreHistorySession(new InMemoryLocalLibrarySource([], []),
            (_, _) => ValueTask.FromResult(result(runs.Select(run => run with { Mods = run.Mods.ToArray() }).ToArray())));

        StatisticsHistoryLoadResult first = await session.GetLocalAsync();
        StatisticsHistoryLoadResult unchanged = await session.GetLocalAsync(refresh: true);
        runs = [play(1), play(2) with { Accuracy = 0.5 }];
        StatisticsHistoryLoadResult changed = await session.GetLocalAsync(refresh: true);

        Assert.Multiple(() =>
        {
            Assert.That(unchanged, Is.SameAs(first));
            Assert.That(changed, Is.Not.SameAs(first));
            Assert.That(changed.Runs[1].Accuracy, Is.EqualTo(0.5));
        });
    }

    [Test]
    public async Task InvalidateCancelsTheLoadInFlightAndReloads()
    {
        int loads = 0;
        var firstLoadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new ScoreHistorySession(new InMemoryLocalLibrarySource([], []), async (_, token) =>
        {
            if (Interlocked.Increment(ref loads) == 1)
            {
                firstLoadStarted.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return result(play(loads));
        });

        Task<StatisticsHistoryLoadResult> stale = session.GetLocalAsync();
        await firstLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        int revision = session.Revision;
        session.Invalidate();

        Assert.That(async () => await stale, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(session.Revision, Is.EqualTo(revision + 1));
        Assert.That((await session.GetLocalAsync()).Runs, Has.Count.EqualTo(1));
        Assert.That(loads, Is.EqualTo(2));
    }

    [Test]
    public async Task CallerCancellationDoesNotCancelTheSharedLoad()
    {
        var release = new TaskCompletionSource();
        var session = new ScoreHistorySession(new InMemoryLocalLibrarySource([], []), async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return result(play(1));
        });

        using var caller = new CancellationTokenSource();
        Task<StatisticsHistoryLoadResult> abandoned = session.GetLocalAsync(cancellationToken: caller.Token);
        caller.Cancel();
        Assert.That(async () => await abandoned, Throws.InstanceOf<OperationCanceledException>());
        release.SetResult();
        Assert.That((await session.GetLocalAsync()).Runs, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task FailedLoadsAreRetriedByTheNextCaller()
    {
        int loads = 0;
        var session = new ScoreHistorySession(new InMemoryLocalLibrarySource([], []), (_, _) =>
            Interlocked.Increment(ref loads) == 1
                ? throw new IOException("Synthetic failure")
                : ValueTask.FromResult(result(play(1))));

        Assert.That(async () => await session.GetLocalAsync(), Throws.InstanceOf<IOException>());
        Assert.That((await session.GetLocalAsync()).Runs, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task OnlineHistoryIsCachedForItsLifetimeAndFailuresAreNotCached()
    {
        DateTimeOffset now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var service = new CountingService();
        var session = new ScoreHistorySession(new InMemoryLocalLibrarySource([], []), onlineLifetime: TimeSpan.FromMinutes(2), clock: () => now);

        OnlineAccountScoreHistoryResult? first = await session.GetOnlineAsync(service);
        Assert.That(await session.GetOnlineAsync(service), Is.SameAs(first));
        now += TimeSpan.FromMinutes(3);
        Assert.That(await session.GetOnlineAsync(service), Is.Not.SameAs(first));
        Assert.That(service.Calls, Is.EqualTo(2));

        service.Fail = true;
        Assert.That(async () => await session.GetOnlineAsync(service, refresh: true), Throws.InstanceOf<HttpRequestException>());
        service.Fail = false;
        Assert.That(await session.GetOnlineAsync(service), Is.Not.Null);
        Assert.That(service.Calls, Is.EqualTo(4));
        Assert.That(await session.GetOnlineAsync(null), Is.Null);
    }

    [Test]
    public void MergeReusesTheResultForTheSameOrEquivalentInputs()
    {
        var session = new ScoreHistorySession(new InMemoryLocalLibrarySource([], []));
        LocalReplay[] local = [play(1), play(2)];
        IReadOnlyList<LocalReplay> first = session.Merge(local, []);

        Assert.Multiple(() =>
        {
            Assert.That(session.Merge(local, []), Is.SameAs(first));
            Assert.That(session.Merge(local.Select(run => run with { Mods = run.Mods.ToArray() }).ToArray(), []), Is.SameAs(first));
            Assert.That(session.Merge([play(1)], []), Is.Not.SameAs(first));
        });
    }

    [Test]
    public void SessionsAreSharedPerSource()
    {
        var source = new InMemoryLocalLibrarySource([], []);
        Assert.That(ScoreHistorySession.For(source), Is.SameAs(ScoreHistorySession.For(source)));
        Assert.That(ScoreHistorySession.For(new InMemoryLocalLibrarySource([], [])), Is.Not.SameAs(ScoreHistorySession.For(source)));
    }

    private static StatisticsHistoryLoadResult result(params LocalReplay[] runs) => new(runs, runs.Length, true);

    private static LocalReplay play(int index) => new(
        new Guid(index, 0, 0, new byte[8]), Guid.Empty, new Guid(index, 1, 0, new byte[8]),
        $"Synthetic {index}", "Synthetic Artist", "Hard", "osu", "Synthetic Player",
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(index),
        5, 0.95, 100_000, 300, 1, 120, ["HD"], true);

    private sealed class CountingService : IAccountScoreHistoryService
    {
        public int Calls;
        public bool Fail;

        public Task<OnlineAccountScoreHistoryResult> FetchAccountAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            if (Fail)
                throw new HttpRequestException("Synthetic failure");
            var coverage = new OnlineScoreCoverage(OsuBestScoresFetchStatus.Success, false, DateTimeOffset.UtcNow, "recent", 100, false);
            return Task.FromResult(new OnlineAccountScoreHistoryResult(null, [], coverage, coverage));
        }

        public Task<OnlineBeatmapScoreHistoryResult> FetchBeatmapAsync(int beatmapId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

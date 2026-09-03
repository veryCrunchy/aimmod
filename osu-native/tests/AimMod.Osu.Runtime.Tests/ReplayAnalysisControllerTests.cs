using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Osu.Runtime.Tests;

[TestFixture]
public sealed class ReplayAnalysisControllerTests
{
    private static readonly ReplayAnalysisRequest request = new("/stage", "/stage/map.osu", "/stage/play.osr");

    [Test]
    public async Task PublishesRunningThenCompletedWithResult()
    {
        ReplayAnalysisResult expected = ReplayAnalysisClientTests.createResult(8);
        var client = new ImmediateClient(expected);
        using var controller = new ReplayAnalysisController(client);
        var states = new List<ReplayAnalysisState>();
        controller.StateChanged += (_, args) => states.Add(args.State);

        ReplayAnalysisState final = await controller.AnalyseAsync(request);

        Assert.Multiple(() =>
        {
            Assert.That(states.Select(state => state.Status), Is.EqualTo(new[]
            {
                ReplayAnalysisStatus.Running,
                ReplayAnalysisStatus.Completed,
            }));
            Assert.That(states[0].Progress, Is.EqualTo(ReplayAnalysisProgress.Judging));
            Assert.That(final.Result, Is.SameAs(expected));
            Assert.That(final.Error, Is.Null);
            Assert.That(final.IsBusy, Is.False);
            Assert.That(states.Select(state => state.Revision), Is.Ordered.Ascending);
        });
    }

    [Test]
    public async Task ConvertsWorkerFailureIntoRouteState()
    {
        using var controller = new ReplayAnalysisController(new FailingClient(
            new ReplayAnalysisClientException("beatmap_load_failed", "The beatmap could not be loaded.")));

        ReplayAnalysisState final = await controller.AnalyseAsync(request);

        Assert.Multiple(() =>
        {
            Assert.That(final.Status, Is.EqualTo(ReplayAnalysisStatus.Failed));
            Assert.That(final.Error, Is.EqualTo(new ReplayAnalysisFailure("beatmap_load_failed", "The beatmap could not be loaded.")));
            Assert.That(final.Result, Is.Null);
        });
    }

    [Test]
    public async Task CancelStopsTheActiveRequest()
    {
        using var controller = new ReplayAnalysisController(new CancellingClient());

        Task<ReplayAnalysisState> analysis = controller.AnalyseAsync(request);
        controller.Cancel();
        ReplayAnalysisState final = await analysis;

        Assert.That(final.Status, Is.EqualTo(ReplayAnalysisStatus.Cancelled));
    }

    [Test]
    public async Task ANewRequestSupersedesAStaleResult()
    {
        var client = new DeferredClient();
        using var controller = new ReplayAnalysisController(client);
        var completedResults = new List<ReplayAnalysisResult>();
        controller.StateChanged += (_, args) =>
        {
            if (args.State.Result is not null)
                completedResults.Add(args.State.Result);
        };

        Task<ReplayAnalysisState> first = controller.AnalyseAsync(request);
        Task<ReplayAnalysisState> second = controller.AnalyseAsync(request);
        ReplayAnalysisResult newest = ReplayAnalysisClientTests.createResult(2);
        ReplayAnalysisResult stale = ReplayAnalysisClientTests.createResult(1);

        client.Complete(1, newest);
        ReplayAnalysisState secondState = await second;
        client.Complete(0, stale);
        ReplayAnalysisState firstState = await first;

        Assert.Multiple(() =>
        {
            Assert.That(secondState.Result, Is.SameAs(newest));
            Assert.That(firstState.Result, Is.SameAs(newest));
            Assert.That(controller.State.Result, Is.SameAs(newest));
            Assert.That(completedResults, Is.EqualTo(new[] { newest }));
        });
    }

    [Test]
    public void UnexpectedErrorsUseASafeMessage()
    {
        using var controller = new ReplayAnalysisController(new FailingClient(new InvalidOperationException("sensitive detail")));

        ReplayAnalysisState final = controller.AnalyseAsync(request).GetAwaiter().GetResult();

        Assert.That(final.Error, Is.EqualTo(new ReplayAnalysisFailure("analysis_failed", "AimMod could not analyse this replay.")));
    }

    private sealed class ImmediateClient(ReplayAnalysisResult result) : IReplayAnalysisClient
    {
        public Task<ReplayAnalysisResult> AnalyseAsync(ReplayAnalysisRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private sealed class FailingClient(Exception exception) : IReplayAnalysisClient
    {
        public Task<ReplayAnalysisResult> AnalyseAsync(ReplayAnalysisRequest request, CancellationToken cancellationToken = default) =>
            Task.FromException<ReplayAnalysisResult>(exception);
    }

    private sealed class CancellingClient : IReplayAnalysisClient
    {
        public async Task<ReplayAnalysisResult> AnalyseAsync(ReplayAnalysisRequest request, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class DeferredClient : IReplayAnalysisClient
    {
        private readonly List<TaskCompletionSource<ReplayAnalysisResult>> requests = new();

        public Task<ReplayAnalysisResult> AnalyseAsync(ReplayAnalysisRequest request, CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<ReplayAnalysisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            requests.Add(completion);
            return completion.Task;
        }

        public void Complete(int index, ReplayAnalysisResult result) => requests[index].SetResult(result);
    }
}

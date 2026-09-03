using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Osu.Runtime;

public enum ReplayAnalysisStatus
{
    Idle,
    Running,
    Completed,
    Failed,
    Cancelled,
}

public sealed record ReplayAnalysisProgress(string Stage, double? Fraction = null)
{
    public static ReplayAnalysisProgress Judging { get; } = new("judging");
}

public sealed record ReplayAnalysisFailure(string Code, string Message);

public sealed record ReplayAnalysisState(
    long Revision,
    ReplayAnalysisStatus Status,
    ReplayAnalysisProgress? Progress = null,
    ReplayAnalysisResult? Result = null,
    ReplayAnalysisFailure? Error = null)
{
    public bool IsBusy => Status == ReplayAnalysisStatus.Running;
}

public sealed class ReplayAnalysisStateChangedEventArgs(ReplayAnalysisState state) : EventArgs
{
    public ReplayAnalysisState State { get; } = state;
}

/// <summary>
/// Owns one replay analysis at a time and exposes immutable state for a native route.
/// Event handlers may run on a worker continuation; the view should marshal updates
/// to its update thread and ignore revisions older than the latest one it handled.
/// </summary>
public sealed class ReplayAnalysisController : IDisposable
{
    private readonly IReplayAnalysisClient client;
    private readonly object stateLock = new();
    private CancellationTokenSource? activeRequest;
    private long requestGeneration;
    private ReplayAnalysisState state = new(0, ReplayAnalysisStatus.Idle);
    private bool disposed;

    public event EventHandler<ReplayAnalysisStateChangedEventArgs>? StateChanged;

    public ReplayAnalysisState State
    {
        get
        {
            lock (stateLock)
                return state;
        }
    }

    public ReplayAnalysisController(IReplayAnalysisClient client)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<ReplayAnalysisState> AnalyseAsync(ReplayAnalysisRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        CancellationTokenSource requestCancellation;
        long generation;

        lock (stateLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            activeRequest?.Cancel();
            activeRequest = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestCancellation = activeRequest;
            generation = ++requestGeneration;
        }

        publish(generation, ReplayAnalysisStatus.Running, ReplayAnalysisProgress.Judging);

        try
        {
            ReplayAnalysisResult result = await client.AnalyseAsync(request, requestCancellation.Token).ConfigureAwait(false);
            return publish(generation, ReplayAnalysisStatus.Completed, result: result);
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
            return publish(generation, ReplayAnalysisStatus.Cancelled);
        }
        catch (ReplayAnalysisClientException exception)
        {
            return publish(
                generation,
                ReplayAnalysisStatus.Failed,
                error: new ReplayAnalysisFailure(exception.Code, exception.Message));
        }
        catch (Exception)
        {
            return publish(
                generation,
                ReplayAnalysisStatus.Failed,
                error: new ReplayAnalysisFailure("analysis_failed", "AimMod could not analyse this replay."));
        }
        finally
        {
            lock (stateLock)
            {
                if (generation == requestGeneration && ReferenceEquals(activeRequest, requestCancellation))
                {
                    activeRequest = null;
                }
            }

            requestCancellation.Dispose();
        }
    }

    public void Cancel()
    {
        lock (stateLock)
            activeRequest?.Cancel();
    }

    public void Reset()
    {
        CancellationTokenSource? requestToCancel;
        long generation;

        lock (stateLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            requestToCancel = activeRequest;
            activeRequest = null;
            generation = ++requestGeneration;
        }

        requestToCancel?.Cancel();
        publish(generation, ReplayAnalysisStatus.Idle);
    }

    public void Dispose()
    {
        CancellationTokenSource? requestToCancel;

        lock (stateLock)
        {
            if (disposed)
                return;

            disposed = true;
            requestToCancel = activeRequest;
            activeRequest = null;
            requestGeneration++;
        }

        requestToCancel?.Cancel();
    }

    private ReplayAnalysisState publish(
        long generation,
        ReplayAnalysisStatus status,
        ReplayAnalysisProgress? progress = null,
        ReplayAnalysisResult? result = null,
        ReplayAnalysisFailure? error = null)
    {
        ReplayAnalysisState nextState;

        lock (stateLock)
        {
            if (generation != requestGeneration)
                return state;

            nextState = new ReplayAnalysisState(state.Revision + 1, status, progress, result, error);
            state = nextState;
        }

        StateChanged?.Invoke(this, new ReplayAnalysisStateChangedEventArgs(nextState));
        return nextState;
    }
}

namespace AimMod.Desktop;

/// <summary>One running query and one replacement, even when input changes faster than a query finishes.</summary>
internal sealed class LatestBackgroundQuery<T> : IDisposable
{
    private readonly object gate = new();
    private Work? pending;
    private CancellationTokenSource? active;
    private bool running;
    private bool disposed;

    public void Submit(Func<CancellationToken, T> query, Action<T> completed, Action<Exception> failed)
    {
        lock (gate)
        {
            if (disposed) return;
            pending = new(query, completed, failed);
            active?.Cancel();
            if (running) return;
            running = true;
            _ = Task.Run(run);
        }
    }

    private void run()
    {
        while (true)
        {
            Work work;
            CancellationTokenSource cancellation;
            lock (gate)
            {
                if (disposed || pending is null)
                {
                    running = false;
                    return;
                }
                work = pending;
                pending = null;
                active = cancellation = new CancellationTokenSource();
            }
            try
            {
                T result = work.Query(cancellation.Token);
                if (!cancellation.IsCancellationRequested) work.Completed(result);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (!cancellation.IsCancellationRequested) work.Failed(error);
            }
            finally
            {
                lock (gate)
                {
                    active = null;
                    cancellation.Dispose();
                }
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            pending = null;
            active?.Cancel();
        }
    }

    private sealed record Work(Func<CancellationToken, T> Query, Action<T> Completed, Action<Exception> Failed);
}

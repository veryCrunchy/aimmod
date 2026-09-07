namespace AimMod.Desktop.LocalLibrary;

internal enum LocalLibraryLoadStatus
{
    Idle,
    Loading,
    Ready,
    Empty,
    Error,
}

internal sealed record LocalLibraryLoadState(
    long Revision,
    LocalLibraryLoadStatus Status,
    IReadOnlyList<LocalBeatmapSet> BeatmapSets,
    IReadOnlyList<LocalReplay> Replays,
    int Total,
    bool HasMore,
    string? ErrorMessage = null)
{
    public int ItemCount => BeatmapSets.Count + Replays.Count;

    public bool IsLoading => Status == LocalLibraryLoadStatus.Loading;
}

internal sealed class LocalLibraryLoadStateChangedEventArgs(LocalLibraryLoadState state) : EventArgs
{
    public LocalLibraryLoadState State { get; } = state;
}

/// <summary>
/// Owns one native-library request at a time. The source can be an in-process
/// fallback or an adapter over the external lazer worker.
/// </summary>
internal sealed class LocalLibraryController : IDisposable
{
    private readonly ILocalLibrarySource source;
    private readonly NativeLocalLibraryMode mode;
    private readonly TimeSpan requestTimeout;
    public LocalLibraryProgress? Progress => (source as ILocalLibraryProgressSource)?.Progress;
    private readonly object stateLock = new();
    private CancellationTokenSource? activeRequest;
    private long requestGeneration;
    private LocalLibraryLoadState state = new(
        0,
        LocalLibraryLoadStatus.Idle,
        Array.Empty<LocalBeatmapSet>(),
        Array.Empty<LocalReplay>(),
        0,
        false);
    private bool disposed;

    public LocalLibraryController(ILocalLibrarySource source, NativeLocalLibraryMode mode, TimeSpan? requestTimeout = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.mode = mode;
        this.requestTimeout = requestTimeout ?? TimeSpan.FromMinutes(2);
    }

    public event EventHandler<LocalLibraryLoadStateChangedEventArgs>? StateChanged;

    public LocalLibraryLoadState State
    {
        get
        {
            lock (stateLock)
                return state;
        }
    }

    public async Task<LocalLibraryLoadState> LoadAsync(
        LocalLibraryQuery query,
        bool append = false,
        CancellationToken cancellationToken = default,
        Func<LocalBeatmapSet, bool>? beatmapFilter = null)
    {
        ArgumentNullException.ThrowIfNull(query);

        CancellationTokenSource requestCancellation;
        LocalLibraryLoadState previous;
        long generation;

        lock (stateLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            activeRequest?.Cancel();
            requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            activeRequest = requestCancellation;
            generation = ++requestGeneration;
            previous = state;
        }

        publish(generation, new LocalLibraryLoadState(
            previous.Revision + 1,
            LocalLibraryLoadStatus.Loading,
            append ? previous.BeatmapSets : Array.Empty<LocalBeatmapSet>(),
            append ? previous.Replays : Array.Empty<LocalReplay>(),
            append ? previous.Total : 0,
            false));

        try
        {
            if (mode == NativeLocalLibraryMode.Beatmaps)
            {
                Task<LocalLibraryPage<LocalBeatmapSet>> search = beatmapFilter is null
                    ? source.SearchBeatmapSetsAsync(query, requestCancellation.Token).AsTask()
                    : ReadFilteredBeatmapPageAsync(source, query, beatmapFilter, requestCancellation.Token);
                LocalLibraryPage<LocalBeatmapSet> page = await search.WaitAsync(requestTimeout, requestCancellation.Token).ConfigureAwait(false);
                IReadOnlyList<LocalBeatmapSet> items = append
                    ? previous.BeatmapSets.Concat(page.Items).ToArray()
                    : page.Items;
                return publishResult(generation, items, Array.Empty<LocalReplay>(), page.Total, page.HasMore, page.Warning);
            }

            LocalLibraryPage<LocalReplay> replayPage = await source.SearchReplaysAsync(query, requestCancellation.Token)
                .AsTask().WaitAsync(requestTimeout, requestCancellation.Token).ConfigureAwait(false);
            IReadOnlyList<LocalReplay> replays = append
                ? previous.Replays.Concat(replayPage.Items).ToArray()
                : replayPage.Items;
            return publishResult(generation, Array.Empty<LocalBeatmapSet>(), replays, replayPage.Total, replayPage.HasMore, replayPage.Warning);
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
            return State;
        }
        catch (Exception error)
        {
            requestCancellation.Cancel();
            return publish(generation, new LocalLibraryLoadState(
                State.Revision + 1,
                LocalLibraryLoadStatus.Error,
                append ? previous.BeatmapSets : Array.Empty<LocalBeatmapSet>(),
                append ? previous.Replays : Array.Empty<LocalReplay>(),
                append ? previous.Total : 0,
                append && previous.HasMore,
                error is TimeoutException
                    ? "The library took too long to respond. Check that your osu! drive is connected, then retry."
                    : error.Message));
        }
        finally
        {
            lock (stateLock)
            {
                if (generation == requestGeneration && ReferenceEquals(activeRequest, requestCancellation))
                    activeRequest = null;
            }

            requestCancellation.Dispose();
        }
    }

    internal static async Task<LocalLibraryPage<LocalBeatmapSet>> ReadFilteredBeatmapPageAsync(
        ILocalLibrarySource source, LocalLibraryQuery query, Func<LocalBeatmapSet, bool> matches, CancellationToken token)
    {
        // Apply the extra filters before paging, including matches beyond the first source page.
        var filtered = new List<LocalBeatmapSet>();
        var scan = query with { Offset = 0, Limit = 200 };
        string? warning = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var page = await source.SearchBeatmapSetsAsync(scan, token).ConfigureAwait(false);
            warning ??= page.Warning;
            filtered.AddRange(page.Items.Where(matches));
            if (!page.HasMore || page.Items.Count == 0) break;
            scan = scan with { Offset = scan.Offset + page.Items.Count };
        }
        return new(filtered.Skip(query.Offset).Take(query.Limit).ToArray(), filtered.Count, query.Offset, query.Limit) { Warning = warning };
    }

    public void Cancel()
    {
        lock (stateLock)
        {
            activeRequest?.Cancel();
            activeRequest = null;
            requestGeneration++;
        }
    }

    public void Dispose()
    {
        lock (stateLock)
        {
            if (disposed)
                return;

            disposed = true;
            activeRequest?.Cancel();
            activeRequest = null;
            requestGeneration++;
        }
    }

    private LocalLibraryLoadState publishResult(
        long generation,
        IReadOnlyList<LocalBeatmapSet> beatmapSets,
        IReadOnlyList<LocalReplay> replays,
        int total,
        bool hasMore, string? warning = null) =>
        publish(generation, new LocalLibraryLoadState(
            State.Revision + 1,
            total == 0 ? LocalLibraryLoadStatus.Empty : LocalLibraryLoadStatus.Ready,
            beatmapSets,
            replays,
            total,
            hasMore, warning));

    private LocalLibraryLoadState publish(long generation, LocalLibraryLoadState nextState)
    {
        lock (stateLock)
        {
            if (generation != requestGeneration)
                return state;

            state = nextState;
        }

        StateChanged?.Invoke(this, new LocalLibraryLoadStateChangedEventArgs(nextState));
        return nextState;
    }
}

using System.Runtime.CompilerServices;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.ScoreHistory;

namespace AimMod.Desktop.Coaching;

/// <summary>
/// Loads the local score history and the submitted account scores once and shares the results between the
/// coaching, statistics and PP target workspaces. Loads are deduplicated while in flight, cancelled when the
/// source changes, and an unchanged reload returns the previous instance so identity-keyed caches survive.
/// </summary>
internal sealed class ScoreHistorySession
{
    public static readonly TimeSpan DefaultOnlineLifetime = TimeSpan.FromMinutes(2);

    private static readonly ConditionalWeakTable<ILocalLibrarySource, ScoreHistorySession> sessions = new();

    private readonly object gate = new();
    private readonly ILocalLibrarySource source;
    private readonly Func<ILocalLibrarySource, CancellationToken, ValueTask<StatisticsHistoryLoadResult>> loadLocal;
    private readonly TimeSpan onlineLifetime;
    private readonly Func<DateTimeOffset> clock;
    private int revision;
    private CancellationTokenSource generation = new();
    private Task<StatisticsHistoryLoadResult>? localTask;
    private StatisticsHistoryLoadResult? lastLocal;
    private OnlineEntry? online;
    private MergeEntry? lastMerge;

    public ScoreHistorySession(
        ILocalLibrarySource source,
        Func<ILocalLibrarySource, CancellationToken, ValueTask<StatisticsHistoryLoadResult>>? loadLocal = null,
        TimeSpan? onlineLifetime = null,
        Func<DateTimeOffset>? clock = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.loadLocal = loadLocal ?? ((library, token) => StatisticsHistoryLoader.LoadAsync(library, token));
        this.onlineLifetime = onlineLifetime ?? DefaultOnlineLifetime;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        if (source is ILocalLibrarySourceChanged changes)
            changes.SourceChanged += Invalidate;
    }

    public static ScoreHistorySession For(ILocalLibrarySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return sessions.GetValue(source, key => new ScoreHistorySession(key));
    }

    /// <summary>Increments whenever cached history is discarded.</summary>
    public int Revision
    {
        get
        {
            lock (gate)
                return revision;
        }
    }

    /// <summary>Discards cached history and cancels loads in flight, for a changed or explicitly refreshed source.</summary>
    public void Invalidate()
    {
        CancellationTokenSource stale;
        lock (gate)
        {
            revision++;
            stale = generation;
            generation = new CancellationTokenSource();
            localTask = null;
            lastLocal = null;
            online = null;
            lastMerge = null;
        }

        stale.Cancel();
    }

    /// <summary>
    /// Returns the local history. <paramref name="refresh"/> re-reads the source unless a read is already running;
    /// a re-read with unchanged plays returns the previous result instance.
    /// </summary>
    public Task<StatisticsHistoryLoadResult> GetLocalAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<StatisticsHistoryLoadResult> task;
        lock (gate)
        {
            if (localTask is null || localTask.IsFaulted || localTask.IsCanceled || refresh && localTask.IsCompleted)
                localTask = startLocal(generation.Token);
            task = localTask;
        }

        return task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Returns the account history of the current service, cached for a short lifetime. A failed request is not
    /// cached, so the next caller retries. Returns null when no account service is available.
    /// </summary>
    public Task<OnlineAccountScoreHistoryResult?> GetOnlineAsync(
        IAccountScoreHistoryService? service,
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (service is null)
            return Task.FromResult<OnlineAccountScoreHistoryResult?>(null);

        Task<OnlineAccountScoreHistoryResult?> task;
        lock (gate)
        {
            bool reusable = online is not null
                            && ReferenceEquals(online.Service, service)
                            && !online.Task.IsFaulted
                            && !online.Task.IsCanceled
                            && !(online.Task.IsCompleted && (refresh || online.IsStale(clock(), onlineLifetime)));
            if (!reusable)
                online = startOnline(service, generation.Token);
            task = online!.Task;
        }

        return task.WaitAsync(cancellationToken);
    }

    /// <summary>Merges local plays with submitted scores, reusing the previous result for the same inputs.</summary>
    public IReadOnlyList<LocalReplay> Merge(IReadOnlyList<LocalReplay> local, IReadOnlyList<ScoreHistoryEntry> submitted)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(submitted);
        lock (gate)
        {
            if (lastMerge is { } cached && ReferenceEquals(cached.Local, local) && ReferenceEquals(cached.Submitted, submitted))
                return cached.Merged;
        }

        IReadOnlyList<LocalReplay> merged = ScoreHistoryMerger.MergeAsLocalReplays(local, submitted);
        lock (gate)
        {
            if (lastMerge is { } previous && SameRuns(previous.Merged, merged))
                merged = previous.Merged;
            lastMerge = new MergeEntry(local, submitted, merged);
        }

        return merged;
    }

    /// <summary>Structural equality of two play lists, including the stored mod lists.</summary>
    internal static bool SameRuns(IReadOnlyList<LocalReplay> left, IReadOnlyList<LocalReplay> right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left.Count != right.Count)
            return false;
        for (int i = 0; i < left.Count; i++)
        {
            LocalReplay a = left[i];
            LocalReplay b = right[i];
            if (ReferenceEquals(a, b))
                continue;
            if (a.ScoreId != b.ScoreId || !a.Mods.SequenceEqual(b.Mods) || a with { Mods = b.Mods } != b)
                return false;
        }

        return true;
    }

    private Task<StatisticsHistoryLoadResult> startLocal(CancellationToken token)
    {
        Task<StatisticsHistoryLoadResult> task = Task.Run(async () =>
        {
            StatisticsHistoryLoadResult loaded = await loadLocal(source, token).ConfigureAwait(false);
            lock (gate)
            {
                if (token.IsCancellationRequested)
                    return loaded;
                if (lastLocal is { } previous
                    && previous.TotalAvailableRunCount == loaded.TotalAvailableRunCount
                    && previous.IsComplete == loaded.IsComplete
                    && SameRuns(previous.Runs, loaded.Runs))
                    return previous;
                lastLocal = loaded;
                return loaded;
            }
        }, token);
        task.ContinueWith(completed =>
        {
            _ = completed.Exception;
            lock (gate)
            {
                if (ReferenceEquals(localTask, completed))
                    localTask = null;
            }
        }, CancellationToken.None, TaskContinuationOptions.NotOnRanToCompletion, TaskScheduler.Default);
        return task;
    }

    private OnlineEntry startOnline(IAccountScoreHistoryService service, CancellationToken token)
    {
        Task<OnlineAccountScoreHistoryResult?> task = Task.Run<OnlineAccountScoreHistoryResult?>(
            async () => await service.FetchAccountAsync(token).ConfigureAwait(false), token);
        var entry = new OnlineEntry(service, task);
        task.ContinueWith(completed =>
        {
            _ = completed.Exception;
            lock (gate)
            {
                if (completed.IsCompletedSuccessfully)
                    entry.CompletedAt = clock();
                else if (ReferenceEquals(online, entry))
                    online = null;
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return entry;
    }

    private sealed record MergeEntry(IReadOnlyList<LocalReplay> Local, IReadOnlyList<ScoreHistoryEntry> Submitted, IReadOnlyList<LocalReplay> Merged);

    private sealed class OnlineEntry(IAccountScoreHistoryService service, Task<OnlineAccountScoreHistoryResult?> task)
    {
        public IAccountScoreHistoryService Service { get; } = service;

        public Task<OnlineAccountScoreHistoryResult?> Task { get; } = task;

        public DateTimeOffset? CompletedAt { get; set; }

        public bool IsStale(DateTimeOffset now, TimeSpan lifetime) => CompletedAt is not { } completed || now - completed > lifetime;
    }
}

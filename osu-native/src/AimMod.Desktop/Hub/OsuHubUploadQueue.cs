using System.Text.Json;

namespace AimMod.Desktop.Hub;

public enum HubUploadQueueStatus
{
    Queued,
    Uploading,
    Completed,
    Failed,
    Cancelled,
}

public sealed record HubUploadQueueItem(
    Guid Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    HubUploadQueueStatus Status,
    int AttemptCount,
    string Title,
    OsuHubSyncRequest Request,
    string ReplayPath,
    string ShareUrl = "",
    string Error = "",
    string AutomaticAccountScope = "",
    Guid AutomaticGeneration = default);

public interface IOsuHubUploadQueue
{
    event Action? Changed;

    IReadOnlyList<HubUploadQueueItem> Snapshot();
    Task<HubUploadQueueItem> EnqueueAsync(OsuHubSyncRequest request, string? replayPath, string title, CancellationToken cancellationToken = default);
    Task<HubUploadQueueItem?> TryEnqueueAutomaticAsync(OsuHubSyncRequest request, string? replayPath, string title,
        string deduplicationKey, string accountScope, Guid generation, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This queue does not support durable automatic sharing.");
    void SetAutomaticUploadPermission(Func<HubUploadQueueItem, bool> permission) { }
    Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> RetryAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class OsuHubUploadQueue : IOsuHubUploadQueue, IDisposable
{
    public const int MaximumEntries = 100;
    private const int current_version = 1;
    private static readonly JsonSerializerOptions json_options = new(JsonSerializerDefaults.Web);

    private readonly string path;
    private readonly IOsuHubUploader uploader;
    private readonly object stateGate = new();
    private readonly SemaphoreSlim persistenceGate = new(1, 1);
    private readonly SemaphoreSlim automaticGate = new(1, 1);
    private readonly SemaphoreSlim signal = new(0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<HubUploadQueueItem> items;
    private readonly HashSet<string> automaticKeys;
    private readonly HashSet<Guid> unpersistedAutomaticItems = [];
    private Func<HubUploadQueueItem, bool>? automaticPermission;
    private CancellationTokenSource? activeUpload;
    private Guid? activeId;
    private readonly Task? worker;

    public event Action? Changed;

    public OsuHubUploadQueue(string path, IOsuHubUploader uploader, bool startWorker = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("The Hub upload queue path must be absolute.", nameof(path));
        this.path = path;
        this.uploader = uploader ?? throw new ArgumentNullException(nameof(uploader));
        QueueDocument? document = load(path);
        automaticKeys = new HashSet<string>(document?.AutomaticKeys ?? [], StringComparer.Ordinal);
        items = (document?.Items ?? []).Select(item => item.Status == HubUploadQueueStatus.Uploading
                ? item with { Status = HubUploadQueueStatus.Queued, Error = "", UpdatedAt = DateTimeOffset.UtcNow }
                : item)
            .OrderBy(item => item.CreatedAt)
            .TakeLast(MaximumEntries)
            .ToList();

        if (startWorker)
        {
            worker = Task.Run(processAsync);
            if (items.Any(item => item.Status == HubUploadQueueStatus.Queued))
                releaseSignal();
        }
    }

    public IReadOnlyList<HubUploadQueueItem> Snapshot()
    {
        lock (stateGate)
            return items.OrderByDescending(item => item.UpdatedAt).ToArray();
    }

    public void SetAutomaticUploadPermission(Func<HubUploadQueueItem, bool> permission)
    {
        lock (stateGate)
            automaticPermission = permission;
        releaseSignal();
    }

    public async Task<HubUploadQueueItem?> TryEnqueueAutomaticAsync(OsuHubSyncRequest request, string? replayPath, string title,
        string deduplicationKey, string accountScope, Guid generation, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deduplicationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountScope);
        if (generation == Guid.Empty)
            throw new ArgumentException("Automatic sharing requires an opt-in generation.", nameof(generation));
        await automaticGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        HubUploadQueueItem? item = null;
        try
        {
            lock (stateGate)
            {
                if (automaticKeys.Contains(deduplicationKey)
                    || items.Any(existing => existing.Request.Profile.OsuUserId == request.Profile.OsuUserId
                        && existing.Request.Score.ClientScoreId == request.Score.ClientScoreId
                        && (string.IsNullOrEmpty(existing.AutomaticAccountScope) || existing.AutomaticAccountScope == accountScope)))
                    return null;
                trimTerminalEntries();
                if (items.Count >= MaximumEntries)
                    throw new InvalidOperationException("The Hub upload queue is full.");
                DateTimeOffset now = DateTimeOffset.UtcNow;
                item = new HubUploadQueueItem(Guid.NewGuid(), now, now, HubUploadQueueStatus.Queued, 0,
                    title, request, replayPath ?? "", AutomaticAccountScope: accountScope, AutomaticGeneration: generation);
                items.Add(item);
                unpersistedAutomaticItems.Add(item.Id);
                automaticKeys.Add(deduplicationKey);
            }
            await persistAsync(cancellationToken).ConfigureAwait(false);
            lock (stateGate)
                unpersistedAutomaticItems.Remove(item.Id);
            raiseChanged();
            releaseSignal();
            return item;
        }
        catch
        {
            if (item is not null)
                lock (stateGate)
                {
                    items.RemoveAll(candidate => candidate.Id == item.Id);
                    unpersistedAutomaticItems.Remove(item.Id);
                    automaticKeys.Remove(deduplicationKey);
                }
            throw;
        }
        finally { automaticGate.Release(); }
    }

    public async Task<HubUploadQueueItem> EnqueueAsync(
        OsuHubSyncRequest request,
        string? replayPath,
        string title,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        HubUploadQueueItem item;
        lock (stateGate)
        {
            trimTerminalEntries();
            if (items.Count >= MaximumEntries)
                throw new InvalidOperationException("The Hub upload queue is full. Cancel or retry existing uploads before adding another replay.");
            item = new HubUploadQueueItem(
                Guid.NewGuid(), now, now, HubUploadQueueStatus.Queued, 0,
                string.IsNullOrWhiteSpace(title) ? request.BeatmapSet.Title : title.Trim(),
                request,
                replayPath ?? string.Empty);
            items.Add(item);
        }
        try
        {
            await persistAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (stateGate)
                items.RemoveAll(candidate => candidate.Id == item.Id);
            throw;
        }
        catch (Exception error)
        {
            traceFailure("persist", error);
        }
        raiseChanged();
        releaseSignal();
        return item;
    }

    public async Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        bool changed = false;
        CancellationTokenSource? cancellation = null;
        lock (stateGate)
        {
            int index = items.FindIndex(item => item.Id == id);
            if (index >= 0 && items[index].Status is HubUploadQueueStatus.Queued or HubUploadQueueStatus.Uploading)
            {
                items[index] = items[index] with
                {
                    Status = HubUploadQueueStatus.Cancelled,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Error = "Cancelled by user.",
                };
                changed = true;
                if (activeId == id)
                    cancellation = activeUpload;
            }
        }
        cancellation?.Cancel();
        if (!changed)
            return false;
        await persistSafeAsync(cancellationToken).ConfigureAwait(false);
        raiseChanged();
        return true;
    }

    public async Task<bool> RetryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        bool changed = false;
        lock (stateGate)
        {
            int index = items.FindIndex(item => item.Id == id);
            if (index >= 0 && items[index].Status is HubUploadQueueStatus.Failed or HubUploadQueueStatus.Cancelled)
            {
                items[index] = items[index] with
                {
                    Status = HubUploadQueueStatus.Queued,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Error = "",
                };
                changed = true;
            }
        }
        if (!changed)
            return false;
        releaseSignal();
        await persistSafeAsync(cancellationToken).ConfigureAwait(false);
        raiseChanged();
        return true;
    }

    private async Task processAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                await signal.WaitAsync(lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                break;
            }

            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    if (!await runNextAsync().ConfigureAwait(false))
                        break;
                }
                catch (Exception error)
                {
                    traceFailure("worker", error);
                    if (lifetime.IsCancellationRequested)
                        break;
                }
            }
        }
    }

    private async Task<bool> runNextAsync()
    {
        HubUploadQueueItem? next;
        CancellationToken uploadToken;
        lock (stateGate)
        {
            next = items.FirstOrDefault(item => item.Status == HubUploadQueueStatus.Queued
                && !unpersistedAutomaticItems.Contains(item.Id)
                && (string.IsNullOrEmpty(item.AutomaticAccountScope) || automaticPermission?.Invoke(item) == true));
            if (next is null)
                return false;
            int index = items.FindIndex(item => item.Id == next.Id);
            next = next with
            {
                Status = HubUploadQueueStatus.Uploading,
                AttemptCount = next.AttemptCount + 1,
                UpdatedAt = DateTimeOffset.UtcNow,
                Error = "",
            };
            items[index] = next;
            activeId = next.Id;
            activeUpload = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            uploadToken = activeUpload.Token;
        }

        try
        {
            await persistSafeAsync(CancellationToken.None).ConfigureAwait(false);
            raiseChanged();

            try
            {
                string? replayPath = string.IsNullOrWhiteSpace(next.ReplayPath) ? null : next.ReplayPath;
                OsuHubUploadResult result = string.IsNullOrEmpty(next.AutomaticAccountScope)
                    ? await uploader.UploadAsync(next.Request, replayPath, uploadToken).ConfigureAwait(false)
                    : await uploader.UploadAutomaticAsync(next.Request, next.AutomaticAccountScope, replayPath, uploadToken).ConfigureAwait(false);
                update(next.Id, item => item with
                {
                    Status = HubUploadQueueStatus.Completed,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    ShareUrl = result.ShareUri.AbsoluteUri,
                    Error = "",
                });
            }
            catch (OperationCanceledException)
            {
                update(next.Id, item => item.Status != HubUploadQueueStatus.Uploading
                    ? item
                    : lifetime.IsCancellationRequested
                        ? item with { Status = HubUploadQueueStatus.Queued, UpdatedAt = DateTimeOffset.UtcNow, Error = "" }
                        : item with { Status = HubUploadQueueStatus.Cancelled, UpdatedAt = DateTimeOffset.UtcNow, Error = "Cancelled by user." });
            }
            catch (Exception error)
            {
                update(next.Id, item => item.Status != HubUploadQueueStatus.Uploading
                    ? item
                    : item with
                    {
                        Status = HubUploadQueueStatus.Failed,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        Error = userFacingError(error),
                    });
            }
        }
        finally
        {
            lock (stateGate)
            {
                activeUpload?.Dispose();
                activeUpload = null;
                activeId = null;
                int index = items.FindIndex(candidate => candidate.Id == next.Id);
                if (index >= 0 && items[index].Status == HubUploadQueueStatus.Uploading)
                    items[index] = items[index] with
                    {
                        Status = HubUploadQueueStatus.Failed,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        Error = "The replay could not be uploaded. Retry when AimMod Hub is available.",
                    };
            }
            await persistSafeAsync(CancellationToken.None).ConfigureAwait(false);
            raiseChanged();
        }

        return true;
    }

    private async Task persistSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await persistAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            traceFailure("persist", error);
        }
    }

    private void raiseChanged()
    {
        Action? handlers = Changed;
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action)handler)(); }
            catch (Exception error) { traceFailure("changed handler", error); }
        }
    }

    private void releaseSignal()
    {
        try { signal.Release(); }
        catch (Exception error) when (error is ObjectDisposedException or SemaphoreFullException) { }
    }

    private static void traceFailure(string operation, Exception error) =>
        System.Diagnostics.Trace.TraceWarning($"Hub upload queue {operation} failed: {error.GetType().Name}: {error.Message}");

    private void update(Guid id, Func<HubUploadQueueItem, HubUploadQueueItem> transform)
    {
        lock (stateGate)
        {
            int index = items.FindIndex(item => item.Id == id);
            if (index >= 0)
                items[index] = transform(items[index]);
        }
    }

    private void trimTerminalEntries()
    {
        while (items.Count >= MaximumEntries)
        {
            int index = items.FindIndex(item => item.Status is HubUploadQueueStatus.Completed or HubUploadQueueStatus.Cancelled);
            if (index < 0)
                index = items.FindIndex(item => item.Status == HubUploadQueueStatus.Failed);
            if (index < 0)
                return;
            items.RemoveAt(index);
        }
    }

    private async Task persistAsync(CancellationToken cancellationToken)
    {
        await persistenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            HubUploadQueueItem[] snapshot;
            string[] keys;
            lock (stateGate)
            {
                snapshot = items.OrderBy(item => item.CreatedAt).TakeLast(MaximumEntries).ToArray();
                keys = automaticKeys.Order(StringComparer.Ordinal).ToArray();
            }
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, new QueueDocument(current_version, snapshot, keys), json_options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            persistenceGate.Release();
        }
    }

    private static QueueDocument? load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            using FileStream stream = File.OpenRead(path);
            QueueDocument? document = JsonSerializer.Deserialize<QueueDocument>(stream, json_options);
            return document?.Version == current_version && document.Items is not null ? document : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string userFacingError(Exception error) => error switch
    {
        InvalidOperationException => error.Message,
        FileNotFoundException => error.Message,
        HttpRequestException => "AimMod Hub could not accept this upload. Check your connection and retry.",
        _ => "The replay could not be uploaded. Retry when AimMod Hub is available.",
    };

    public void Dispose()
    {
        lifetime.Cancel();
        lock (stateGate)
            activeUpload?.Cancel();
        releaseSignal();
        bool stopped = false;
        try { stopped = worker?.Wait(TimeSpan.FromSeconds(2)) != false; }
        catch (AggregateException error) when (error.InnerExceptions.All(inner => inner is OperationCanceledException)) { stopped = true; }
        if (stopped)
        {
            signal.Dispose();
            persistenceGate.Dispose();
        }
        lifetime.Dispose();
    }

    private sealed record QueueDocument(int Version, IReadOnlyList<HubUploadQueueItem> Items, IReadOnlyList<string>? AutomaticKeys = null);
}

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
    string Error = "");

public interface IOsuHubUploadQueue
{
    event Action? Changed;

    IReadOnlyList<HubUploadQueueItem> Snapshot();
    Task<HubUploadQueueItem> EnqueueAsync(OsuHubSyncRequest request, string? replayPath, string title, CancellationToken cancellationToken = default);
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
    private readonly SemaphoreSlim signal = new(0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<HubUploadQueueItem> items;
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
        items = load(path).Select(item => item.Status == HubUploadQueueStatus.Uploading
                ? item with { Status = HubUploadQueueStatus.Queued, Error = "", UpdatedAt = DateTimeOffset.UtcNow }
                : item)
            .OrderBy(item => item.CreatedAt)
            .TakeLast(MaximumEntries)
            .ToList();

        if (startWorker)
        {
            worker = Task.Run(processAsync);
            if (items.Any(item => item.Status == HubUploadQueueStatus.Queued))
                signal.Release();
        }
    }

    public IReadOnlyList<HubUploadQueueItem> Snapshot()
    {
        lock (stateGate)
            return items.OrderByDescending(item => item.UpdatedAt).ToArray();
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
        await persistAsync(cancellationToken).ConfigureAwait(false);
        Changed?.Invoke();
        signal.Release();
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
        await persistAsync(cancellationToken).ConfigureAwait(false);
        Changed?.Invoke();
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
        await persistAsync(cancellationToken).ConfigureAwait(false);
        Changed?.Invoke();
        signal.Release();
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

            while (true)
            {
                HubUploadQueueItem? next;
                lock (stateGate)
                {
                    next = items.FirstOrDefault(item => item.Status == HubUploadQueueStatus.Queued);
                    if (next is null)
                        break;
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
                }

                await persistAsync(CancellationToken.None).ConfigureAwait(false);
                Changed?.Invoke();

                try
                {
                    OsuHubUploadResult result = await uploader.UploadAsync(
                        next.Request,
                        string.IsNullOrWhiteSpace(next.ReplayPath) ? null : next.ReplayPath,
                        activeUpload.Token).ConfigureAwait(false);
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
                    update(next.Id, item => lifetime.IsCancellationRequested
                        ? item with { Status = HubUploadQueueStatus.Queued, UpdatedAt = DateTimeOffset.UtcNow, Error = "" }
                        : item with { Status = HubUploadQueueStatus.Cancelled, UpdatedAt = DateTimeOffset.UtcNow, Error = "Cancelled by user." });
                }
                catch (Exception error)
                {
                    update(next.Id, item => item with
                    {
                        Status = HubUploadQueueStatus.Failed,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        Error = userFacingError(error),
                    });
                }
                finally
                {
                    lock (stateGate)
                    {
                        activeUpload?.Dispose();
                        activeUpload = null;
                        activeId = null;
                    }
                    await persistAsync(CancellationToken.None).ConfigureAwait(false);
                    Changed?.Invoke();
                }
            }
        }
    }

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
            lock (stateGate)
                snapshot = items.OrderBy(item => item.CreatedAt).TakeLast(MaximumEntries).ToArray();
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, new QueueDocument(current_version, snapshot), json_options, cancellationToken).ConfigureAwait(false);
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

    private static IReadOnlyList<HubUploadQueueItem> load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return [];
            using FileStream stream = File.OpenRead(path);
            QueueDocument? document = JsonSerializer.Deserialize<QueueDocument>(stream, json_options);
            return document?.Version == current_version && document.Items is not null ? document.Items : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
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
        try { signal.Release(); }
        catch (SemaphoreFullException) { }
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

    private sealed record QueueDocument(int Version, IReadOnlyList<HubUploadQueueItem> Items);
}

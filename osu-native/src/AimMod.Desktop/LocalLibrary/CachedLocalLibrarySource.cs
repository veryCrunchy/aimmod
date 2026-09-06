using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AimMod.Desktop.LocalLibrary;

/// <summary>Private, bounded page cache. Database changes invalidate both maps and score history.</summary>
public sealed class CachedLocalLibrarySource : ILocalLibrarySource, ILocalLibraryProgressSource
{
    private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
    private readonly ILocalLibrarySource source;
    private readonly string directory;
    private readonly string[] databases;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object memoryLock = new();
    private readonly Dictionary<string, MemoryEntry> memory = new();
    private long memoryBytes;
    private const long memory_budget = 32 * 1024 * 1024;
    private long revision;
    private long invalidatedAt;
    public LocalLibraryProgress? Progress => (source as ILocalLibraryProgressSource)?.Progress;

    public CachedLocalLibrarySource(ILocalLibrarySource source, string cacheDirectory, params string[] databasePaths)
    {
        this.source = source;
        directory = Path.GetFullPath(cacheDirectory);
        databases = databasePaths.Select(Path.GetFullPath).ToArray();
        if (databases.Length == 0) throw new ArgumentException("Database paths are required.", nameof(databasePaths));
    }

    public ValueTask<LocalLibraryPage<LocalBeatmapSet>> SearchBeatmapSetsAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default) =>
        read("maps", query, source.SearchBeatmapSetsAsync, cancellationToken);

    public ValueTask<LocalLibraryPage<LocalReplay>> SearchReplaysAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default) =>
        read("replays", query, source.SearchReplaysAsync, cancellationToken);

    public void Invalidate()
    {
        Interlocked.Increment(ref revision);
        Interlocked.Exchange(ref invalidatedAt, DateTimeOffset.UtcNow.UtcTicks);
        lock (memoryLock) { memory.Clear(); memoryBytes = 0; }
        source.Invalidate();
    }

    private async ValueTask<LocalLibraryPage<T>> read<T>(string kind, LocalLibraryQuery query,
        Func<LocalLibraryQuery, CancellationToken, ValueTask<LocalLibraryPage<T>>> search, CancellationToken token)
    {
        query = query.Normalised();
        token.ThrowIfCancellationRequested();
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "1|" + kind + "|" + string.Join('|', databases) + "|" + JsonSerializer.Serialize(query, json))));
        string path = Path.Combine(directory, key + ".json");
        // Healthy cached pages should not wait for an unrelated worker scan.
        if (await readCached<T>(key, path, query, token).ConfigureAwait(false) is { } cached) return cached;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string? stamp = databaseStamp();
            long startedRevision = Volatile.Read(ref revision);
            if (await readCached<T>(key, path, query, token).ConfigureAwait(false) is { } queuedCache) return queuedCache;

            LocalLibraryPage<T> result = await search(query, token).ConfigureAwait(false);
            if (stamp is not null && result.Warning is null && stamp == databaseStamp()
                && startedRevision == Volatile.Read(ref revision))
            {
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    Directory.CreateDirectory(directory);
                    var document = new Document<T>(stamp, DateTimeOffset.UtcNow, result);
                    await using (var output = File.Create(temporary))
                        await JsonSerializer.SerializeAsync(output, document, json, token).ConfigureAwait(false);
                    if (startedRevision != Volatile.Read(ref revision) || stamp != databaseStamp()) return result;
                    File.Move(temporary, path, true);
                    if (startedRevision == Volatile.Read(ref revision) && stamp == databaseStamp())
                        remember(key, document, new FileInfo(path).Length, startedRevision);
                    foreach (var old in new DirectoryInfo(directory).EnumerateFiles("*.json")
                        .OrderByDescending(file => file.LastWriteTimeUtc).Skip(128))
                        old.Delete();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
                {
                    Console.Error.WriteLine("[AimMod] Could not save local library results.");
                }
                finally
                {
                    try { File.Delete(temporary); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
            }
            return result;
        }
        finally { gate.Release(); }
    }

    private async Task<LocalLibraryPage<T>?> readCached<T>(string key, string path, LocalLibraryQuery query, CancellationToken token)
    {
        long currentRevision = Volatile.Read(ref revision);
        string? stamp = databaseStamp();
        if (stamp is null) return null;
        Document<T>? saved = null;
        lock (memoryLock)
        {
            if (memory.TryGetValue(key, out var entry) && entry.Revision == currentRevision)
                saved = entry.Document as Document<T>;
        }
        long bytes = 0;
        if (saved is null)
        {
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length > 8 * 1024 * 1024) return null;
                bytes = file.Length;
                await using var input = File.OpenRead(path);
                saved = await JsonSerializer.DeserializeAsync<Document<T>>(input, json, token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
        }
        token.ThrowIfCancellationRequested();
        if (saved?.Stamp != stamp || saved.SavedAt > DateTimeOffset.UtcNow
            || saved.SavedAt.UtcTicks < Volatile.Read(ref invalidatedAt)
            || DateTimeOffset.UtcNow - saved.SavedAt >= TimeSpan.FromDays(7)
            || saved.Page is not { Items: not null, Warning: null } page
            || page.Offset != query.Offset || page.Limit != query.Limit || page.Total < 0
            || currentRevision != Volatile.Read(ref revision) || stamp != databaseStamp()) return null;
        if (bytes > 0) remember(key, saved, bytes, currentRevision);
        return page;
    }

    private void remember(string key, object document, long bytes, long currentRevision)
    {
        if (bytes > memory_budget) return;
        lock (memoryLock)
        {
            if (currentRevision != Volatile.Read(ref revision)) return;
            if (memory.Remove(key, out var previous)) memoryBytes -= previous.Bytes;
            while (memory.Count > 0 && (memory.Count >= 128 || memoryBytes + bytes > memory_budget))
            {
                string oldest = memory.Keys.First();
                memoryBytes -= memory[oldest].Bytes;
                memory.Remove(oldest);
            }
            memory.Add(key, new(document, bytes, currentRevision));
            memoryBytes += bytes;
        }
    }

    private sealed record MemoryEntry(object Document, long Bytes, long Revision);

    private string? databaseStamp()
    {
        try
        {
            // Do not reuse a healthy snapshot when its installation is disconnected.
            if (!File.Exists(databases[0])) return null;
            return string.Join('|', databases.Select(path =>
            {
                var file = new FileInfo(path);
                return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "missing";
            }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private sealed record Document<T>(string Stamp, DateTimeOffset SavedAt, LocalLibraryPage<T> Page);
}

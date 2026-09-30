using System.Text.Json;

namespace AimMod.Desktop.Hub;

public sealed record OsuHubSyncCacheEntry(
    string ContentHash,
    string Visibility,
    string ShareId,
    string ReplaySha256,
    bool ReplayUploaded,
    DateTimeOffset SyncedAt);

public interface IOsuHubSyncCache
{
    OsuHubSyncCacheEntry? Find(string contentHash);
    Task SaveAsync(OsuHubSyncCacheEntry entry, CancellationToken cancellationToken = default);
}

public sealed class FileOsuHubSyncCache : IOsuHubSyncCache
{
    private const int current_version = 1;
    private const int maximum_entries = 10_000;
    private static readonly JsonSerializerOptions json_options = new(JsonSerializerDefaults.Web);

    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object memoryGate = new();
    private OsuHubSyncCacheEntry[]? memory;

    public FileOsuHubSyncCache(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("The Hub sync cache path must be absolute.", nameof(path));
        this.path = path;
    }

    public OsuHubSyncCacheEntry? Find(string contentHash) => current()
        .FirstOrDefault(entry => string.Equals(entry.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase));

    // Reading the file per lookup is slow and, on Windows, blocks the replacing move of a concurrent save.
    private OsuHubSyncCacheEntry[] current()
    {
        lock (memoryGate)
            return memory ??= loadEntries().ToArray();
    }

    public async Task SaveAsync(OsuHubSyncCacheEntry entry, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            OsuHubSyncCacheEntry[] entries = current()
                .Where(candidate => !string.Equals(candidate.ContentHash, entry.ContentHash, StringComparison.OrdinalIgnoreCase))
                .Append(entry)
                .OrderBy(candidate => candidate.SyncedAt)
                .TakeLast(maximum_entries)
                .ToArray();
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            await using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, new CacheDocument(current_version, entries), json_options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, path, true);
            lock (memoryGate)
                memory = entries;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            gate.Release();
        }
    }

    private IReadOnlyList<OsuHubSyncCacheEntry> loadEntries()
    {
        try
        {
            if (!File.Exists(path))
                return [];
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            CacheDocument? document = JsonSerializer.Deserialize<CacheDocument>(stream, json_options);
            return document?.Version == current_version && document.Entries is not null
                ? document.Entries.Where(entry => entry?.ContentHash is not null).ToArray()
                : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private sealed record CacheDocument(int Version, IReadOnlyList<OsuHubSyncCacheEntry> Entries);
}

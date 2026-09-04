using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AimMod.Desktop.Skins.Online;

public sealed record OnlineSkinCacheOptions(
    long MaximumBytes = 512L * 1024 * 1024,
    int MaximumEntries = 400,
    long MaximumEntryBytes = 256L * 1024 * 1024,
    TimeSpan? MaximumAge = null)
{
    public TimeSpan EntryMaximumAge => MaximumAge ?? TimeSpan.FromDays(7);
}

public sealed class OnlineSkinCatalogCache
{
    private static readonly JsonSerializerOptions json_options = new(JsonSerializerDefaults.Web);

    private readonly string root;
    private readonly string entriesRoot;
    private readonly string indexPath;
    private readonly OnlineSkinCacheOptions options;
    private readonly SemaphoreSlim gate = new(1, 1);

    public OnlineSkinCatalogCache(string root, OnlineSkinCacheOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Path.IsPathFullyQualified(root))
            throw new ArgumentException("The cache path must be absolute.", nameof(root));
        this.root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        entriesRoot = Path.Combine(this.root, "entries");
        indexPath = Path.Combine(this.root, "index.json");
        this.options = options ?? new OnlineSkinCacheOptions();
        if (this.options.MaximumBytes <= 0 || this.options.MaximumEntries <= 0 || this.options.MaximumEntryBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    public async Task<byte[]?> ReadBytesAsync(string key, TimeSpan? maximumAge = null, CancellationToken cancellationToken = default)
    {
        string temporary = Path.Combine(root, $"read-{Guid.NewGuid():N}.tmp");
        if (!await TryCopyToAsync(key, temporary, maximumAge, cancellationToken).ConfigureAwait(false))
            return null;
        try
        {
            return await File.ReadAllBytesAsync(temporary, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public async Task PutBytesAsync(string key, ReadOnlyMemory<byte> bytes, string kind, CancellationToken cancellationToken = default)
    {
        if (bytes.Length > options.MaximumEntryBytes)
            throw new ArgumentOutOfRangeException(nameof(bytes), "The cache item exceeds the configured entry limit.");
        Directory.CreateDirectory(root);
        string temporary = Path.Combine(root, $"write-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes.ToArray(), cancellationToken).ConfigureAwait(false);
            await PutFileAsync(key, temporary, kind, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public async Task PutFileAsync(string key, string sourcePath, string kind, CancellationToken cancellationToken = default)
    {
        validateKey(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!Path.IsPathFullyQualified(sourcePath))
            throw new ArgumentException("The cached file path must be absolute.", nameof(sourcePath));
        var source = new FileInfo(sourcePath);
        if (!source.Exists)
            throw new FileNotFoundException("The cached file no longer exists.", sourcePath);
        if (source.Length > Math.Min(options.MaximumEntryBytes, options.MaximumBytes))
            throw new ArgumentOutOfRangeException(nameof(sourcePath), "The cache item exceeds the configured entry limit.");

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(entriesRoot);
            List<CacheEntry> entries = await readIndex(cancellationToken).ConfigureAwait(false);
            string identity = identityFor(key);
            string destination = Path.Combine(entriesRoot, identity + ".bin");
            string temporary = destination + $".{Guid.NewGuid():N}.tmp";
            File.Copy(source.FullName, temporary, overwrite: false);
            try
            {
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                deleteQuietly(temporary);
            }

            entries.RemoveAll(entry => string.Equals(entry.Key, key, StringComparison.Ordinal));
            entries.Add(new CacheEntry(key, identity + ".bin", source.Length, DateTimeOffset.UtcNow, sanitizeKind(kind)));
            prune(entries, key);
            await writeIndex(entries, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> TryCopyToAsync(
        string key,
        string destinationPath,
        TimeSpan? maximumAge = null,
        CancellationToken cancellationToken = default)
    {
        validateKey(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (!Path.IsPathFullyQualified(destinationPath))
            throw new ArgumentException("The cache destination must be absolute.", nameof(destinationPath));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<CacheEntry> entries = await readIndex(cancellationToken).ConfigureAwait(false);
            CacheEntry? entry = entries.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
            string? source = entry is null ? null : safeEntryPath(entry.FileName);
            TimeSpan ageLimit = maximumAge ?? options.EntryMaximumAge;
            if (entry is null || source is null || !File.Exists(source) || DateTimeOffset.UtcNow - entry.WrittenAt > ageLimit)
            {
                if (entry is not null)
                {
                    entries.Remove(entry);
                    deleteQuietly(source);
                    await writeIndex(entries, cancellationToken).ConfigureAwait(false);
                }
                return false;
            }

            string fullDestination = Path.GetFullPath(destinationPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
            File.Copy(source, fullDestination, overwrite: false);
            entries[entries.IndexOf(entry)] = entry with { LastAccessed = DateTimeOffset.UtcNow };
            await writeIndex(entries, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task PruneAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<CacheEntry> entries = await readIndex(cancellationToken).ConfigureAwait(false);
            prune(entries, preservedKey: null);
            await writeIndex(entries, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        validateKey(key);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<CacheEntry> entries = await readIndex(cancellationToken).ConfigureAwait(false);
            foreach (CacheEntry entry in entries.Where(entry => string.Equals(entry.Key, key, StringComparison.Ordinal)).ToArray())
            {
                entries.Remove(entry);
                deleteQuietly(safeEntryPath(entry.FileName));
            }
            await writeIndex(entries, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private void prune(List<CacheEntry> entries, string? preservedKey)
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - options.EntryMaximumAge;
        foreach (CacheEntry expired in entries.Where(entry => entry.WrittenAt < cutoff).ToArray())
        {
            entries.Remove(expired);
            deleteQuietly(safeEntryPath(expired.FileName));
        }

        long total = entries.Sum(entry => Math.Max(0, entry.Length));
        foreach (CacheEntry candidate in entries
                     .Where(entry => !string.Equals(entry.Key, preservedKey, StringComparison.Ordinal))
                     .OrderBy(entry => entry.LastAccessedAt)
                     .ThenBy(entry => entry.WrittenAt)
                     .ToArray())
        {
            if (entries.Count <= options.MaximumEntries && total <= options.MaximumBytes)
                break;
            entries.Remove(candidate);
            total -= Math.Max(0, candidate.Length);
            deleteQuietly(safeEntryPath(candidate.FileName));
        }

        if (Directory.Exists(entriesRoot))
        {
            var indexed = new HashSet<string>(entries.Select(entry => entry.FileName), StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.EnumerateFiles(entriesRoot, "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                if (!indexed.Contains(name))
                    deleteQuietly(file);
            }
        }
    }

    private async Task<List<CacheEntry>> readIndex(CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = File.OpenRead(indexPath);
            return await JsonSerializer.DeserializeAsync<List<CacheEntry>>(stream, json_options, cancellationToken).ConfigureAwait(false) ?? [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private async Task writeIndex(List<CacheEntry> entries, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(root);
        string temporary = indexPath + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 16_384, FileOptions.Asynchronous))
            await JsonSerializer.SerializeAsync(stream, entries, json_options, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, indexPath, overwrite: true);
    }

    private string? safeEntryPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName)
            return null;
        string path = Path.GetFullPath(Path.Combine(entriesRoot, fileName));
        return path.StartsWith(entriesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    private static void validateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > 1_024)
            throw new ArgumentOutOfRangeException(nameof(key));
    }

    private static string identityFor(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    private static string sanitizeKind(string kind) => string.IsNullOrWhiteSpace(kind) ? "unknown" : kind.Trim()[..Math.Min(kind.Trim().Length, 32)];

    private static void deleteQuietly(string? path)
    {
        if (path is null)
            return;
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record CacheEntry(
        string Key,
        string FileName,
        long Length,
        DateTimeOffset WrittenAt,
        string Kind,
        DateTimeOffset? LastAccessed = null)
    {
        public DateTimeOffset LastAccessedAt => LastAccessed ?? WrittenAt;
    }
}

public sealed class CachedOnlineSkinCatalogProvider : IOnlineSkinCatalogProvider
{
    private static readonly JsonSerializerOptions json_options = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan search_lifetime = TimeSpan.FromHours(6);
    private static readonly TimeSpan details_lifetime = TimeSpan.FromDays(2);

    private readonly IOnlineSkinCatalogProvider inner;
    private readonly OnlineSkinCatalogCache cache;

    public CachedOnlineSkinCatalogProvider(IOnlineSkinCatalogProvider inner, OnlineSkinCatalogCache cache)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public string Id => inner.Id;
    public string DisplayName => inner.DisplayName;
    public Uri HomePage => inner.HomePage;

    public async Task<OnlineSkinCatalogPage> SearchAsync(OnlineSkinCatalogQuery query, CancellationToken cancellationToken = default)
    {
        query = query.Normalize();
        string key = $"catalog:{Id}:search:{JsonSerializer.Serialize(query, json_options)}";
        byte[]? cached = await cache.ReadBytesAsync(key, search_lifetime, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
            return JsonSerializer.Deserialize<OnlineSkinCatalogPage>(cached, json_options)!;

        OnlineSkinCatalogPage page = await inner.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        if (page.Status == OnlineSkinCatalogStatus.Success)
            await cache.PutBytesAsync(key, JsonSerializer.SerializeToUtf8Bytes(page, json_options), "catalog", cancellationToken).ConfigureAwait(false);
        return page;
    }

    public async Task<OnlineSkinCatalogEntry?> GetDetailsAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        string key = $"catalog:{Id}:details:{id}";
        byte[]? cached = await cache.ReadBytesAsync(key, details_lifetime, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
            return JsonSerializer.Deserialize<OnlineSkinCatalogEntry>(cached, json_options);

        OnlineSkinCatalogEntry? details = await inner.GetDetailsAsync(id, cancellationToken).ConfigureAwait(false);
        if (details is not null)
            await cache.PutBytesAsync(key, JsonSerializer.SerializeToUtf8Bytes(details, json_options), "catalog", cancellationToken).ConfigureAwait(false);
        return details;
    }
}

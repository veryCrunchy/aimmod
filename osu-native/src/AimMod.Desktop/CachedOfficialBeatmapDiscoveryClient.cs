using System.Text.Json;
using AimMod.Osu.Runtime;

namespace AimMod.Desktop;

public sealed class CachedOfficialBeatmapDiscoveryClient : IOfficialBeatmapDiscoveryClient, IOfficialBeatmapDifficultyClient, IDisposable
{
    private const int current_version = 1;
    private const int maximum_entries = 64;
    private static readonly TimeSpan search_ttl = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions json_options = new(JsonSerializerDefaults.Web);

    private readonly IOfficialBeatmapDiscoveryClient inner;
    private readonly string path;
    private readonly SemaphoreSlim writeGate = new(1, 1);

    public CachedOfficialBeatmapDiscoveryClient(IOfficialBeatmapDiscoveryClient inner, string path)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("The beatmap search cache path must be absolute.", nameof(path));
        this.path = path;
    }

    public async Task<OfficialBeatmapSearchResult> SearchAsync(
        OfficialBeatmapSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        OfficialBeatmapSearchQuery normalised = query.Normalised();
        string key = cacheKey(normalised);
        if (tryLoad(key) is { } cached)
            return cached;

        OfficialBeatmapSearchResult result = await inner.SearchAsync(normalised, cancellationToken).ConfigureAwait(false);
        if (result.Status == OfficialBeatmapRequestStatus.Success)
            await saveAsync(key, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public Task<OfficialBeatmapDownloadResult> DownloadAsync(
        int beatmapSetId,
        string destinationDirectory,
        bool noVideo = false,
        CancellationToken cancellationToken = default) =>
        inner.DownloadAsync(beatmapSetId, destinationDirectory, noVideo, cancellationToken);

    public Task<OfficialBeatmapDifficultyDownloadResult> DownloadDifficultyAsync(
        int beatmapId,
        string destinationDirectory,
        CancellationToken cancellationToken = default) =>
        inner is IOfficialBeatmapDifficultyClient difficultyClient
            ? difficultyClient.DownloadDifficultyAsync(beatmapId, destinationDirectory, cancellationToken)
            : Task.FromResult(new OfficialBeatmapDifficultyDownloadResult(OfficialBeatmapRequestStatus.InvalidResponse, beatmapId));

    public void Dispose()
    {
        writeGate.Dispose();
        if (inner is IDisposable disposable)
            disposable.Dispose();
    }

    private OfficialBeatmapSearchResult? tryLoad(string key)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            using FileStream stream = File.OpenRead(path);
            CacheDocument? document = JsonSerializer.Deserialize<CacheDocument>(stream, json_options);
            if (document?.Version != current_version || document.Entries is null)
                return null;

            CacheEntry? entry = document.Entries.FirstOrDefault(candidate => candidate.Key == key);
            if (entry is null || DateTimeOffset.UtcNow - entry.CachedAt > search_ttl)
                return null;
            return entry.Result.Status == OfficialBeatmapRequestStatus.Success ? entry.Result : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private async Task saveAsync(string key, OfficialBeatmapSearchResult result, CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CacheEntry[] existing = loadEntries();
            CacheEntry[] entries = existing.Where(entry => entry.Key != key)
                                           .Append(new CacheEntry(key, DateTimeOffset.UtcNow, result))
                                           .OrderBy(entry => entry.CachedAt)
                                           .TakeLast(maximum_entries)
                                           .ToArray();
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, new CacheDocument(current_version, entries), json_options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        finally
        {
            writeGate.Release();
        }
    }

    private CacheEntry[] loadEntries()
    {
        try
        {
            if (!File.Exists(path))
                return [];
            using FileStream stream = File.OpenRead(path);
            CacheDocument? document = JsonSerializer.Deserialize<CacheDocument>(stream, json_options);
            return document?.Version == current_version && document.Entries is not null
                ? document.Entries.ToArray()
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static string cacheKey(OfficialBeatmapSearchQuery query) => JsonSerializer.Serialize(query, json_options);

    private sealed record CacheDocument(int Version, IReadOnlyList<CacheEntry> Entries);

    private sealed record CacheEntry(string Key, DateTimeOffset CachedAt, OfficialBeatmapSearchResult Result);
}

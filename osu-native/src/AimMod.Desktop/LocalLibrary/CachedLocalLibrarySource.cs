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
        source.Invalidate();
    }

    private async ValueTask<LocalLibraryPage<T>> read<T>(string kind, LocalLibraryQuery query,
        Func<LocalLibraryQuery, CancellationToken, ValueTask<LocalLibraryPage<T>>> search, CancellationToken token)
    {
        query = query.Normalised();
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string? stamp = databaseStamp();
            long startedRevision = Volatile.Read(ref revision);
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                "1|" + kind + "|" + string.Join('|', databases) + "|" + JsonSerializer.Serialize(query, json))));
            string path = Path.Combine(directory, key + ".json");
            if (stamp is not null)
            {
                try
                {
                    var file = new FileInfo(path);
                    if (file.Exists && file.Length <= 8 * 1024 * 1024)
                    {
                        await using var input = File.OpenRead(path);
                        var saved = await JsonSerializer.DeserializeAsync<Document<T>>(input, json, token).ConfigureAwait(false);
                        if (saved?.Stamp == stamp && saved.SavedAt <= DateTimeOffset.UtcNow
                            && saved.SavedAt.UtcTicks >= Volatile.Read(ref invalidatedAt)
                            && DateTimeOffset.UtcNow - saved.SavedAt < TimeSpan.FromDays(7)
                            && saved.Page is { Items: not null, Warning: null } page
                            && page.Offset == query.Offset && page.Limit == query.Limit && page.Total >= 0
                            && startedRevision == Volatile.Read(ref revision))
                            return page;
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
            }

            LocalLibraryPage<T> result = await search(query, token).ConfigureAwait(false);
            if (stamp is not null && result.Warning is null && stamp == databaseStamp()
                && startedRevision == Volatile.Read(ref revision))
            {
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    Directory.CreateDirectory(directory);
                    await using (var output = File.Create(temporary))
                        await JsonSerializer.SerializeAsync(output, new Document<T>(stamp, DateTimeOffset.UtcNow, result), json, token).ConfigureAwait(false);
                    File.Move(temporary, path, true);
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

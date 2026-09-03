using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop.PpTargets;

public sealed record LocalScorePpHydrationResult(
    IReadOnlyList<LocalReplay> Runs,
    int StoredCount,
    int CachedCount,
    int CalculatedCount,
    int UnavailableCount);

public sealed record LocalScorePpHydrationProgress(int Completed, int Total);

public interface ILocalScorePpHydrationService
{
    Task<LocalScorePpHydrationResult> HydrateAsync(
        IReadOnlyList<LocalReplay> runs,
        CancellationToken cancellationToken = default,
        IProgress<LocalScorePpHydrationProgress>? progress = null);
}

public sealed class LocalScorePpHydrationService : ILocalScorePpHydrationService
{
    private const int cache_version = 1;
    private const int hashes_per_batch = 128;
    private const int maximum_cache_entries = 20_000;
    private static readonly JsonSerializerOptions json_options = new(JsonSerializerDefaults.Web);

    private readonly string libraryRoot;
    private readonly string cachePath;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, CacheEntry> cache;

    public LocalScorePpHydrationService(string libraryRoot, string cachePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(cachePath);
        if (!Path.IsPathFullyQualified(libraryRoot) || !Path.IsPathFullyQualified(cachePath))
            throw new ArgumentException("Local score PP paths must be absolute.");

        this.libraryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
        this.cachePath = Path.GetFullPath(cachePath);
        cache = loadCache(this.cachePath);
    }

    public async Task<LocalScorePpHydrationResult> HydrateAsync(
        IReadOnlyList<LocalReplay> runs,
        CancellationToken cancellationToken = default,
        IProgress<LocalScorePpHydrationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(runs);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ppByScore = new Dictionary<Guid, double>();
            int stored = 0;
            int cached = 0;
            foreach (LocalReplay run in runs)
            {
                if (validPp(run.PerformancePoints))
                {
                    ppByScore[run.ScoreId] = run.PerformancePoints!.Value;
                    stored++;
                }
                else if (validInput(run) && cache.TryGetValue(cacheKey(run), out CacheEntry? entry) && validPp(entry.PerformancePoints))
                {
                    ppByScore[run.ScoreId] = entry.PerformancePoints;
                    cached++;
                }
            }

            LocalReplay[] missing = runs.Where(run => !ppByScore.ContainsKey(run.ScoreId) && validInput(run))
                                        .GroupBy(run => run.ScoreId)
                                        .Select(group => group.First())
                                        .ToArray();
            int calculated = 0;
            int processed = 0;
            progress?.Report(new LocalScorePpHydrationProgress(0, missing.Length));
            if (missing.Length > 0)
            {
                await using SidecarRuntimeClient runtime = SidecarRuntimeClient.Start();
                var runtimeClient = new SidecarRuntimeRequestClient(runtime);
                var assetClient = new ExternalLazerAssetClient(runtimeClient);
                var ppClient = new PpWhatIfClient(runtimeClient);

                IGrouping<string, LocalReplay>[] hashGroups = missing.GroupBy(run => run.BeatmapHash, StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (IGrouping<string, LocalReplay>[] batch in hashGroups.Chunk(hashes_per_batch))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string[] hashes = batch.Select(group => group.Key).ToArray();
                    await using ExternalLazerAssetStagingLease lease = await assetClient.ResolveToPrivateStagingAsync(
                        libraryRoot, hashes, Array.Empty<Guid>(), cancellationToken).ConfigureAwait(false);
                    Dictionary<string, ExternalLazerResolvedAsset> beatmaps = lease.Result.Files
                        .Where(file => string.Equals(file.Kind, "Beatmap", StringComparison.Ordinal))
                        .GroupBy(file => file.OwnerId, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

                    foreach (LocalReplay run in batch.SelectMany(group => group))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!beatmaps.TryGetValue(run.BeatmapHash, out ExternalLazerResolvedAsset? beatmap))
                        {
                            processed++;
                            progress?.Report(new LocalScorePpHydrationProgress(processed, missing.Length));
                            continue;
                        }
                        try
                        {
                            PpWhatIfResult result = await ppClient.CalculateAsync(new PpWhatIfRequest(
                                Path.GetDirectoryName(beatmap.StagedPath)!,
                                beatmap.StagedPath,
                                run.Mods,
                                run.Accuracy,
                                run.MissCount,
                                run.MaxCombo,
                                run.HitStatistics,
                                run.ModsJson), cancellationToken).ConfigureAwait(false);
                            ppByScore[run.ScoreId] = result.PerformancePoints;
                            cache[cacheKey(run)] = new CacheEntry(cacheKey(run), result.PerformancePoints, DateTimeOffset.UtcNow);
                            calculated++;
                        }
                        catch (Exception error) when (error is not OperationCanceledException)
                        {
                        }
                        finally
                        {
                            processed++;
                            progress?.Report(new LocalScorePpHydrationProgress(processed, missing.Length));
                        }
                    }

                    await trySaveCacheAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            LocalReplay[] hydrated = runs.Select(run => ppByScore.TryGetValue(run.ScoreId, out double pp)
                ? run with { PerformancePoints = pp }
                : run).ToArray();
            return new LocalScorePpHydrationResult(
                hydrated,
                stored,
                cached,
                calculated,
                hydrated.Count(run => !validPp(run.PerformancePoints)));
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task trySaveCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            string? directory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            CacheEntry[] entries = cache.Values.OrderBy(entry => entry.CalculatedAt).TakeLast(maximum_cache_entries).ToArray();
            string temporaryPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await JsonSerializer.SerializeAsync(stream, new CacheDocument(cache_version, entries), json_options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                File.Move(temporaryPath, cachePath, true);
            }
            finally
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
        }
    }

    private static Dictionary<string, CacheEntry> loadCache(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
            using FileStream stream = File.OpenRead(path);
            CacheDocument? document = JsonSerializer.Deserialize<CacheDocument>(stream, json_options);
            if (document?.Version != cache_version || document.Entries is null)
                return new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
            return document.Entries.Where(entry => entry.Key.Length == 64 && validPp(entry.PerformancePoints))
                           .TakeLast(maximum_cache_entries)
                           .ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        }
    }

    private static bool validInput(LocalReplay run) => run is not null
        && run.ScoreId != Guid.Empty
        && run.BeatmapHash is { Length: 32 or 64 }
        && run.BeatmapHash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')
        && run.HitStatistics is not null
        && double.IsFinite(run.Accuracy) && run.Accuracy is >= 0 and <= 1
        && run.MaxCombo >= 0 && run.MissCount >= 0;

    private static bool validPp(double? value) => value is >= 0 and <= 10_000 && double.IsFinite(value.Value);

    private static string cacheKey(LocalReplay run)
    {
        PpScoreStatistics statistics = run.HitStatistics!;
        string raw = string.Join('|',
            PpCalculationProtocol.EngineVersion,
            run.BeatmapHash.ToLowerInvariant(),
            run.ModsJson,
            string.Join(',', run.Mods.Order(StringComparer.Ordinal)),
            run.Accuracy.ToString("R", CultureInfo.InvariantCulture),
            run.MaxCombo,
            statistics.Great, statistics.Ok, statistics.Meh, statistics.Miss,
            statistics.SliderTailHit, statistics.LargeTickMiss);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private sealed record CacheDocument(int Version, IReadOnlyList<CacheEntry> Entries);
    private sealed record CacheEntry(string Key, double PerformancePoints, DateTimeOffset CalculatedAt);
}

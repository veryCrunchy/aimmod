using System.Text.Json;
using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop.Coaching;

public sealed record CoachingPpProjectionRequest(
    LocalReplay Run,
    CoachingPpOpportunity Opportunity,
    IReadOnlyList<LocalReplay>? History = null);

public sealed record CoachingExactPpProjection(
    Guid ScoreId,
    double CurrentPp,
    double ProjectedPp,
    double RealisticGain,
    double ProfilePpGain,
    bool HasProfileEstimate,
    PpWhatIfResult Calculation);

public interface ICoachingPpProjectionService
{
    Task<IReadOnlyDictionary<Guid, CoachingExactPpProjection>> CalculateAsync(
        IReadOnlyList<CoachingPpProjectionRequest> requests,
        CancellationToken cancellationToken = default);
}

public sealed class CoachingPpProjectionService : ICoachingPpProjectionService
{
    private const int cache_version = 1;
    private const int maximum_cache_entries = 2_048;
    private const int maximum_batch_size = 8;

    private static readonly JsonSerializerOptions json_options = new(JsonSerializerDefaults.Web);

    private readonly string libraryRoot;
    private readonly string cachePath;
    private readonly SemaphoreSlim calculationGate = new(1, 1);
    private readonly Dictionary<string, CacheEntry> cache;

    public CoachingPpProjectionService(string libraryRoot, string cachePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(cachePath);
        if (!Path.IsPathFullyQualified(libraryRoot) || !Path.IsPathFullyQualified(cachePath))
            throw new ArgumentException("Coaching PP paths must be absolute.");

        this.libraryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
        this.cachePath = Path.GetFullPath(cachePath);
        cache = loadCache(this.cachePath);
    }

    public async Task<IReadOnlyDictionary<Guid, CoachingExactPpProjection>> CalculateAsync(
        IReadOnlyList<CoachingPpProjectionRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        CoachingPpProjectionRequest[] valid = requests.Where(isValid)
                                                       .DistinctBy(request => cacheKey(request))
                                                       .Take(maximum_batch_size)
                                                       .ToArray();
        if (valid.Length == 0)
            return new Dictionary<Guid, CoachingExactPpProjection>();

        await calculationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var completed = new Dictionary<Guid, CoachingExactPpProjection>();
            CoachingPpProjectionRequest[] missing = valid.Where(request => !tryReadCached(request, completed)).ToArray();
            if (missing.Length == 0)
                return completed;

            await using SidecarRuntimeClient runtime = SidecarRuntimeClient.Start();
            var runtimeClient = new SidecarRuntimeRequestClient(runtime);
            var assetClient = new ExternalLazerAssetClient(runtimeClient);
            var ppClient = new PpWhatIfClient(runtimeClient);
            string[] hashes = missing.Select(request => request.Run.BeatmapHash)
                                     .Distinct(StringComparer.OrdinalIgnoreCase)
                                     .ToArray();

            await using ExternalLazerAssetStagingLease lease = await assetClient.ResolveToPrivateStagingAsync(
                libraryRoot,
                hashes,
                Array.Empty<Guid>(),
                cancellationToken).ConfigureAwait(false);

            var beatmaps = lease.Result.Files.Where(file => string.Equals(file.Kind, "Beatmap", StringComparison.Ordinal))
                                       .GroupBy(file => file.OwnerId, StringComparer.OrdinalIgnoreCase)
                                       .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            bool changed = false;
            foreach (CoachingPpProjectionRequest request in missing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!beatmaps.TryGetValue(request.Run.BeatmapHash, out ExternalLazerResolvedAsset? beatmap))
                    continue;

                string stagingDirectory = Path.GetDirectoryName(beatmap.StagedPath)!;
                string targetKey = cacheKey(request);
                PpWhatIfResult result;
                if (cache.TryGetValue(targetKey, out CacheEntry? cachedTarget) && validResult(cachedTarget.Result))
                {
                    result = cachedTarget.Result;
                }
                else
                {
                    var ceilingRequest = new PpWhatIfRequest(
                        stagingDirectory,
                        beatmap.StagedPath,
                        normaliseMods(request.Run.Mods),
                        request.Opportunity.TargetAccuracy!.Value,
                        request.Opportunity.TargetMissCount!.Value,
                        null);
                    PpWhatIfResult ceiling = await ppClient.CalculateAsync(ceilingRequest, cancellationToken).ConfigureAwait(false);
                    int targetCombo = estimateTargetCombo(request, ceiling.MaxCombo);
                    result = targetCombo == ceiling.MaxCombo
                        ? ceiling
                        : await ppClient.CalculateAsync(ceilingRequest with { MaxCombo = targetCombo }, cancellationToken).ConfigureAwait(false);
                    cache[targetKey] = new CacheEntry(targetKey, DateTimeOffset.UtcNow, result);
                    changed = true;
                }
                double currentPp = request.Run.PerformancePoints is { } storedPp && double.IsFinite(storedPp) && storedPp >= 0
                    ? storedPp
                    : await calculateCurrentPpAsync(request, stagingDirectory, beatmap.StagedPath, ppClient, cancellationToken).ConfigureAwait(false);
                if (request.Run.PerformancePoints is null)
                    changed = true;
                var projection = createProjection(request, result, currentPp);
                completed[projection.ScoreId] = projection;
            }

            if (changed)
            {
                try
                {
                    await saveCacheAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
                {
                }
            }
            return completed;
        }
        finally
        {
            calculationGate.Release();
        }
    }

    private bool tryReadCached(
        CoachingPpProjectionRequest request,
        IDictionary<Guid, CoachingExactPpProjection> completed)
    {
        if (!cache.TryGetValue(cacheKey(request), out CacheEntry? entry)
            || !validResult(entry.Result))
            return false;

        double currentPp = request.Run.PerformancePoints is { } storedPp && double.IsFinite(storedPp) && storedPp >= 0
            ? storedPp
            : request.Opportunity.CurrentPp;
        if (currentPp <= 0)
            return false;

        CoachingExactPpProjection projection = createProjection(request, entry.Result, currentPp);
        completed[projection.ScoreId] = projection;
        return true;
    }

    private async Task<double> calculateCurrentPpAsync(
        CoachingPpProjectionRequest request,
        string stagingDirectory,
        string beatmapPath,
        IPpWhatIfClient client,
        CancellationToken cancellationToken)
    {
        string key = scenarioCacheKey(
            request.Run,
            request.Run.Accuracy,
            request.Run.MissCount,
            request.Run.MaxCombo);
        if (cache.TryGetValue(key, out CacheEntry? cached) && validResult(cached.Result))
            return cached.Result.PerformancePoints;

        PpWhatIfResult result = await client.CalculateAsync(new PpWhatIfRequest(
            stagingDirectory,
            beatmapPath,
            normaliseMods(request.Run.Mods),
            request.Run.Accuracy,
            request.Run.MissCount,
            request.Run.MaxCombo), cancellationToken).ConfigureAwait(false);
        cache[key] = new CacheEntry(key, DateTimeOffset.UtcNow, result);
        return result.PerformancePoints;
    }

    private async Task saveCacheAsync(CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        CacheEntry[] entries = cache.Values.OrderBy(entry => entry.CalculatedAt)
                                          .TakeLast(maximum_cache_entries)
                                          .ToArray();
        string temporaryPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await JsonSerializer.SerializeAsync(stream, new CacheDocument(cache_version, entries), json_options, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, cachePath, overwrite: true);
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

            return document.Entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && validResult(entry.Result))
                           .TakeLast(maximum_cache_entries)
                           .ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        }
    }

    private static CoachingExactPpProjection createProjection(
        CoachingPpProjectionRequest request,
        PpWhatIfResult result,
        double currentPp)
    {
        IReadOnlyList<LocalReplay> history = request.History ?? Array.Empty<LocalReplay>();
        bool hasProfileEstimate = history.Any(run => run.PerformancePoints is { } pp && double.IsFinite(pp) && pp >= 0);
        double rawGain = Math.Max(0, result.PerformancePoints - currentPp);
        return new CoachingExactPpProjection(
            request.Run.ScoreId,
            currentPp,
            result.PerformancePoints,
            rawGain,
            hasProfileEstimate
                ? CoachingPpWeighting.CalculateProfileGain(history, request.Run.BeatmapId, result.PerformancePoints)
                : rawGain,
            hasProfileEstimate,
            result);
    }

    internal static int EstimateTargetCombo(CoachingPpProjectionRequest request, int beatmapMaxCombo) =>
        estimateTargetCombo(request, beatmapMaxCombo);

    private static int estimateTargetCombo(CoachingPpProjectionRequest request, int beatmapMaxCombo)
    {
        if (beatmapMaxCombo <= 0)
            return 0;

        int currentCombo = Math.Clamp(request.Run.MaxCombo, 0, beatmapMaxCombo);
        int currentMisses = Math.Max(0, request.Run.MissCount);
        int targetMisses = Math.Clamp(request.Opportunity.TargetMissCount ?? currentMisses, 0, currentMisses);
        double accuracyGain = Math.Max(0, (request.Opportunity.TargetAccuracy ?? request.Run.Accuracy) - request.Run.Accuracy);
        double missRecovery = currentMisses == 0 ? 0 : (double)(currentMisses - targetMisses) / currentMisses;
        double recovery = Math.Clamp(0.18 + 0.57 * missRecovery + 2.5 * accuracyGain, 0.18, 0.85);
        int projected = currentCombo + (int)Math.Round((beatmapMaxCombo - currentCombo) * recovery);
        return Math.Clamp(projected, currentCombo, beatmapMaxCombo);
    }

    private static bool isValid(CoachingPpProjectionRequest request) =>
        request is not null
        && request.Run.MaxCombo >= 0
        && request.Run.MissCount >= 0
        && request.Run.Accuracy is >= 0 and <= 1
        && validHash(request.Run.BeatmapHash)
        && request.Opportunity.TargetAccuracy is >= 0 and <= 1
        && request.Opportunity.TargetMissCount is >= 0;

    private static bool validResult(PpWhatIfResult result) =>
        result is not null
        && string.Equals(result.EngineVersion, PpCalculationProtocol.EngineVersion, StringComparison.Ordinal)
        && result.DifficultyVersion > 0
        && double.IsFinite(result.PerformancePoints)
        && result.PerformancePoints >= 0;

    private static bool validHash(string hash) =>
        hash is { Length: 32 or 64 }
        && hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static IReadOnlyList<string> normaliseMods(IReadOnlyList<string> mods) =>
        (mods ?? Array.Empty<string>()).Select(mod => (mod ?? string.Empty).Trim().ToUpperInvariant())
                                         .Where(mod => mod.Length > 0 && mod is not "NM")
                                         .Distinct(StringComparer.Ordinal)
                                         .Order(StringComparer.Ordinal)
                                         .ToArray();

    private static string cacheKey(CoachingPpProjectionRequest request) => scenarioCacheKey(
        request.Run,
        request.Opportunity.TargetAccuracy!.Value,
        request.Opportunity.TargetMissCount!.Value,
        request.Run.MaxCombo);

    private static string scenarioCacheKey(LocalReplay run, double accuracy, int misses, int combo) => string.Join('|',
        PpCalculationProtocol.EngineVersion,
        run.BeatmapHash.ToLowerInvariant(),
        string.Join(',', normaliseMods(run.Mods)),
        accuracy.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        misses,
        combo);

    private sealed record CacheDocument(int Version, IReadOnlyList<CacheEntry> Entries);

    private sealed record CacheEntry(string Key, DateTimeOffset CalculatedAt, PpWhatIfResult Result);
}

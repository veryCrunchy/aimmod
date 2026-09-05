using System.Text.Json;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop.PpTargets;

public sealed record PpTargetExactRequest(
    int BeatmapId,
    string? BeatmapHash,
    IReadOnlyList<string> Mods,
    double ExpectedAccuracy,
    double Attainability,
    PpPatternProfile? PatternProfile = null);

public sealed record PpTargetExactCalculationProgress(int Completed, int Total);

public interface IPpTargetExactCalculationService
{
    Task<IReadOnlyDictionary<int, PpTargetEstimate>> CalculateAsync(
        IReadOnlyList<PpTargetExactRequest> requests,
        CancellationToken cancellationToken = default,
        IProgress<PpTargetExactCalculationProgress>? progress = null);
}

public sealed class PpTargetExactCalculationService : IPpTargetExactCalculationService
{
    private const int cache_version = 4;
    private const int maximum_batch_size = 50;
    private const int maximum_cache_entries = 2_048;
    private static readonly JsonSerializerOptions json_options = new(JsonSerializerDefaults.Web);

    private readonly string libraryRoot;
    private readonly string cachePath;
    private readonly IOfficialBeatmapDifficultyClient? difficultyClient;
    private readonly string difficultyDownloadDirectory;
    private readonly Func<SidecarRuntimeClient> runtimeFactory;
    private readonly SemaphoreSlim calculationGate = new(1, 1);
    private readonly Dictionary<string, CacheEntry> cache;
    private readonly PpTargetBeatmapPatternReader patternReader;

    public PpTargetExactCalculationService(string libraryRoot, string cachePath)
        : this(libraryRoot, cachePath, null, Path.Combine(Path.GetTempPath(), "aimmod-pp-target-difficulties"))
    {
    }

    public PpTargetExactCalculationService(
        string libraryRoot,
        string cachePath,
        IOfficialBeatmapDifficultyClient? difficultyClient,
        string difficultyDownloadDirectory)
        : this(libraryRoot, cachePath, difficultyClient, difficultyDownloadDirectory, SidecarRuntimeClient.Start)
    {
    }

    internal PpTargetExactCalculationService(
        string libraryRoot,
        string cachePath,
        IOfficialBeatmapDifficultyClient? difficultyClient,
        string difficultyDownloadDirectory,
        Func<SidecarRuntimeClient> runtimeFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(cachePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(difficultyDownloadDirectory);
        if (!Path.IsPathFullyQualified(libraryRoot) || !Path.IsPathFullyQualified(cachePath) || !Path.IsPathFullyQualified(difficultyDownloadDirectory))
            throw new ArgumentException("PP target calculation paths must be absolute.");

        this.libraryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
        this.cachePath = Path.GetFullPath(cachePath);
        this.difficultyClient = difficultyClient;
        this.difficultyDownloadDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(difficultyDownloadDirectory));
        this.runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
        cache = loadCache(this.cachePath);
        patternReader = new PpTargetBeatmapPatternReader(Directory.Exists(this.cachePath)
            ? Path.Combine(this.cachePath, "beatmap-patterns") : this.cachePath + ".beatmaps");
    }

    public async Task<IReadOnlyDictionary<int, PpTargetEstimate>> CalculateAsync(
        IReadOnlyList<PpTargetExactRequest> requests,
        CancellationToken cancellationToken = default,
        IProgress<PpTargetExactCalculationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(requests);
        PpTargetExactRequest[] valid = requests.Where(isValid)
                                               .DistinctBy(request => cacheKey(request))
                                               .Take(maximum_batch_size)
                                               .ToArray();
        if (valid.Length == 0)
            return new Dictionary<int, PpTargetEstimate>();

        await calculationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var completed = new Dictionary<int, PpTargetEstimate>();
            var retainedFiles = new Dictionary<string, PpTargetBeatmapFile>();
            var missingRequests = new List<PpTargetExactRequest>();
            foreach (PpTargetExactRequest request in valid)
            {
                PpTargetBeatmapFile? retained = await patternReader.TryGetCachedFileAsync(request.BeatmapId, request.BeatmapHash, cancellationToken).ConfigureAwait(false);
                if (retained is not null)
                    retainedFiles[cacheKey(request)] = retained;
                if (retained is null || !tryReadCached(request, retained.ContentHash, completed))
                    missingRequests.Add(request);
            }
            PpTargetExactRequest[] missing = missingRequests.ToArray();
            progress?.Report(new PpTargetExactCalculationProgress(completed.Count, valid.Length));
            if (missing.Length == 0)
                return completed;

            await using SidecarRuntimeClient runtime = runtimeFactory();
            var runtimeClient = new SidecarRuntimeRequestClient(runtime);
            var assetClient = new ExternalLazerAssetClient(runtimeClient);
            var ppClient = new PpWhatIfClient(runtimeClient);
            string[] hashes = missing.Where(request => !retainedFiles.ContainsKey(cacheKey(request))).Select(request => request.BeatmapHash)
                                     .Where(hash => !string.IsNullOrWhiteSpace(hash))
                                     .Cast<string>()
                                     .Distinct(StringComparer.OrdinalIgnoreCase)
                                     .ToArray();

            await using ExternalLazerAssetStagingLease? lease = hashes.Length == 0
                ? null
                : await assetClient.ResolveToPrivateStagingAsync(
                    libraryRoot,
                    hashes,
                    Array.Empty<Guid>(),
                    cancellationToken).ConfigureAwait(false);
            Dictionary<string, ExternalLazerResolvedAsset> beatmaps = (lease?.Result.Files ?? [])
                .Where(file => string.Equals(file.Kind, "Beatmap", StringComparison.Ordinal))
                .GroupBy(file => file.OwnerId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            Exception? firstCalculationFailure = null;
            for (int index = 0; index < missing.Length; index++)
            {
                PpTargetExactRequest request = missing[index];
                cancellationToken.ThrowIfCancellationRequested();
                string? downloadedPath = null;
                retainedFiles.TryGetValue(cacheKey(request), out PpTargetBeatmapFile? retained);
                retained ??= await patternReader.TryGetCachedFileAsync(request.BeatmapId, request.BeatmapHash, cancellationToken).ConfigureAwait(false);
                string? beatmapPath = retained?.Path ?? (request.BeatmapHash is { Length: > 0 } hash && beatmaps.TryGetValue(hash, out ExternalLazerResolvedAsset? localBeatmap)
                    ? localBeatmap.StagedPath
                    : null);

                if (beatmapPath is null && difficultyClient is not null)
                {
                    OfficialBeatmapDifficultyDownloadResult download = await difficultyClient.DownloadDifficultyAsync(
                        request.BeatmapId,
                        difficultyDownloadDirectory,
                        cancellationToken).ConfigureAwait(false);
                    if (download.Status == OfficialBeatmapRequestStatus.Success)
                        beatmapPath = downloadedPath = download.BeatmapPath;
                    else
                        firstCalculationFailure ??= new InvalidOperationException(
                            $"Beatmap difficulty {request.BeatmapId} download failed with status {download.Status}.");
                }
                if (beatmapPath is null)
                {
                    progress?.Report(new PpTargetExactCalculationProgress(valid.Length - missing.Length + index + 1, valid.Length));
                    continue;
                }

                try
                {
                    string stagingDirectory = Path.GetDirectoryName(beatmapPath)!;
                    IReadOnlyList<string> mods = PpTargetMods.Normalise(request.Mods);
                    PpTargetBeatmapFile file = retained ?? await PpTargetBeatmapPatternReader.IdentifyAsync(beatmapPath, request.BeatmapHash, cancellationToken).ConfigureAwait(false);
                    PpTargetBeatmapPatternGeometry geometry = await patternReader.ReadAsync(file, mods, cancellationToken).ConfigureAwait(false);
                    await patternReader.RetainAsync(file, request.BeatmapId, request.BeatmapHash, cancellationToken).ConfigureAwait(false);
                    PpPatternPrediction? prediction = request.PatternProfile is { } profile
                        ? PpTargetPatternModel.Predict(PpTargetPatternModel.ExtractFeatures(geometry.Points, geometry.HitRadius, geometry.ClockRate), profile, mods)
                        : null;
                    double accuracy = measuredFraction(prediction?.ExpectedAccuracy) ?? request.ExpectedAccuracy;
                    double attainability = measuredFraction(prediction?.Fit) ?? request.Attainability;
                    PpWhatIfResult ceiling = await ppClient.CalculateAsync(new PpWhatIfRequest(
                        stagingDirectory, beatmapPath, mods, 1, 0, null), cancellationToken).ConfigureAwait(false);
                    (int misses, int combo) = ExpectedScoreShape(attainability, ceiling.MaxCombo, ceiling.ObjectCount, prediction?.ExpectedMissRate);
                    accuracy = FeasibleAccuracy(accuracy, misses, ceiling.ObjectCount);
                    PpWhatIfResult expected = await ppClient.CalculateAsync(new PpWhatIfRequest(
                        stagingDirectory, beatmapPath, mods, accuracy, misses, combo), cancellationToken).ConfigureAwait(false);
                    if (prediction?.ExpectedAccuracy is not null)
                        prediction = prediction with { ExpectedAccuracy = expected.Accuracy };
                    // Keep the original request fields as the ranker's estimate identity.
                    PpTargetEstimate estimate = createEstimate(request, expected, ceiling) with
                    {
                        PatternPrediction = prediction,
                        PatternProfileIdentity = request.PatternProfile?.Identity,
                    };
                    if (prediction?.ExpectedAccuracy is not null || prediction?.ExpectedMissRate is not null)
                        estimate = estimate with { Method = estimate.Method + " Projected accuracy/misses use measured head evidence; combo remains heuristic and slider tracking is unmeasured." };
                    string key = cacheKey(request, file.ContentHash);
                    cache[key] = new CacheEntry(key, DateTimeOffset.UtcNow, estimate);
                    completed[request.BeatmapId] = estimate;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    firstCalculationFailure ??= error;
                }
                finally
                {
                    if (downloadedPath is not null)
                        deleteIfPresent(downloadedPath);
                    progress?.Report(new PpTargetExactCalculationProgress(
                        valid.Length - missing.Length + index + 1,
                        valid.Length));
                }
            }

            if (completed.Count > valid.Length - missing.Length)
                await trySaveCacheAsync().ConfigureAwait(false);

            if (completed.Count == 0 && firstCalculationFailure is not null)
                throw new InvalidOperationException("Official PP calculation failed for every requested beatmap difficulty.", firstCalculationFailure);

            return completed;
        }
        finally
        {
            calculationGate.Release();
        }
    }

    public async Task<IReadOnlyDictionary<int, double>> CalculateAccuracyCurveAsync(
        int beatmapId,
        string? beatmapHash,
        IReadOnlyList<string> mods,
        IReadOnlyList<int> accuracies,
        CancellationToken cancellationToken = default)
    {
        int[] points = accuracies.Where(accuracy => accuracy is >= 0 and <= 100).Distinct().Order().ToArray();
        if (beatmapId <= 0 || points.Length == 0)
            return new Dictionary<int, double>();

        PpTargetExactRequest[] requests = points.Select(accuracy => new PpTargetExactRequest(
            beatmapId,
            beatmapHash,
            mods,
            accuracy / 100d,
            1)).Where(isValid).ToArray();
        if (requests.Length == 0)
            return new Dictionary<int, double>();

        await calculationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var completed = new Dictionary<int, double>();
            var missing = new List<(int Accuracy, PpTargetExactRequest Request)>();
            foreach (PpTargetExactRequest request in requests)
            {
                int accuracy = (int)Math.Round(request.ExpectedAccuracy * 100);
                if (cache.TryGetValue(cacheKey(request), out CacheEntry? entry) && validEstimate(entry.Estimate))
                    completed[accuracy] = accuracy == 100 ? entry.Estimate.RealisticMaximumPp : entry.Estimate.ExpectedPp;
                else
                    missing.Add((accuracy, request));
            }
            if (missing.Count == 0)
                return completed;

            await using SidecarRuntimeClient runtime = runtimeFactory();
            var runtimeClient = new SidecarRuntimeRequestClient(runtime);
            var assetClient = new ExternalLazerAssetClient(runtimeClient);
            var ppClient = new PpWhatIfClient(runtimeClient);
            string? hash = string.IsNullOrWhiteSpace(beatmapHash) ? null : beatmapHash;

            await using ExternalLazerAssetStagingLease? lease = hash is null
                ? null
                : await assetClient.ResolveToPrivateStagingAsync(
                    libraryRoot,
                    new[] { hash },
                    Array.Empty<Guid>(),
                    cancellationToken).ConfigureAwait(false);
            string? beatmapPath = lease?.Result.Files.FirstOrDefault(file =>
                string.Equals(file.Kind, "Beatmap", StringComparison.Ordinal)
                && string.Equals(file.OwnerId, hash, StringComparison.OrdinalIgnoreCase))?.StagedPath;
            string? downloadedPath = null;

            if (beatmapPath is null && difficultyClient is not null)
            {
                OfficialBeatmapDifficultyDownloadResult download = await difficultyClient.DownloadDifficultyAsync(
                    beatmapId,
                    difficultyDownloadDirectory,
                    cancellationToken).ConfigureAwait(false);
                if (download.Status == OfficialBeatmapRequestStatus.Success)
                    beatmapPath = downloadedPath = download.BeatmapPath;
            }
            if (beatmapPath is null)
                throw new InvalidOperationException($"Beatmap difficulty {beatmapId} could not be resolved for PP calculation.");

            try
            {
                string stagingDirectory = Path.GetDirectoryName(beatmapPath)!;
                IReadOnlyList<string> normalisedMods = PpTargetMods.Normalise(mods);
                PpWhatIfResult ceiling = await ppClient.CalculateAsync(new PpWhatIfRequest(
                    stagingDirectory, beatmapPath, normalisedMods, 1, 0, null), cancellationToken).ConfigureAwait(false);

                foreach ((int accuracy, PpTargetExactRequest request) in missing)
                {
                    PpWhatIfResult expected = accuracy == 100
                        ? ceiling
                        : await ppClient.CalculateAsync(new PpWhatIfRequest(
                            stagingDirectory, beatmapPath, normalisedMods, request.ExpectedAccuracy, 0, ceiling.MaxCombo), cancellationToken).ConfigureAwait(false);
                    PpTargetEstimate estimate = createEstimate(request, expected, ceiling);
                    cache[cacheKey(request)] = new CacheEntry(cacheKey(request), DateTimeOffset.UtcNow, estimate);
                    completed[accuracy] = accuracy == 100 ? estimate.RealisticMaximumPp : estimate.ExpectedPp;
                }

                if (missing.Count > 0)
                    await trySaveCacheAsync().ConfigureAwait(false);
            }
            finally
            {
                if (downloadedPath is not null)
                    deleteIfPresent(downloadedPath);
            }

            return completed;
        }
        finally
        {
            calculationGate.Release();
        }
    }

    internal static (int Misses, int Combo) ExpectedScoreShape(double attainability, int maximumCombo)
    {
        double fit = Math.Clamp(attainability, 0, 1);
        int misses = fit switch
        {
            >= 0.85 => 0,
            >= 0.60 => 1,
            >= 0.35 => 2,
            _ => 3,
        };
        double comboRatio = misses == 0 ? 1 : Math.Clamp(1 - 0.16 * misses, 0.5, 0.84);
        return (misses, Math.Clamp((int)Math.Round(maximumCombo * comboRatio), 0, maximumCombo));
    }

    internal static (int Misses, int Combo) ExpectedScoreShape(double attainability, int maximumCombo, int objectCount, double? expectedMissRate)
    {
        if (measuredFraction(expectedMissRate) is not { } rate)
            return ExpectedScoreShape(attainability, maximumCombo);
        int count = Math.Max(0, objectCount);
        int misses = Math.Clamp((int)Math.Round(rate * count, MidpointRounding.AwayFromZero), 0, count);
        if (misses == 0) return (0, Math.Max(0, maximumCombo));
        // Miss counts are measured; the distribution of combo breaks is not.
        double comboRatio = misses == count ? 0 : Math.Clamp(1 - 0.16 * misses, 0.5, 0.84);
        return (misses, Math.Clamp((int)Math.Round(maximumCombo * comboRatio), 0, maximumCombo));
    }

    internal static double FeasibleAccuracy(double accuracy, int misses, int objectCount) => objectCount <= 0
        ? 0
        : Math.Clamp(accuracy, 0, 1 - Math.Clamp(misses, 0, objectCount) / (double)objectCount);

    private static PpTargetEstimate createEstimate(PpTargetExactRequest request, PpWhatIfResult expected, PpWhatIfResult ceiling)
    {
        if (!double.IsFinite(expected.PerformancePoints) || expected.PerformancePoints < 0
            || !double.IsFinite(ceiling.PerformancePoints) || ceiling.PerformancePoints < 0)
            throw new InvalidOperationException($"Official PP calculation returned an invalid value for beatmap difficulty {request.BeatmapId}.");

        double expectedPp = expected.PerformancePoints;
        double maximumPp = ceiling.PerformancePoints;
        double spread = 0.18 + 0.16 * (1 - Math.Clamp(request.Attainability, 0, 1));
        return new PpTargetEstimate(
            expectedPp,
            maximumPp,
            new PpTargetRange(Math.Max(0, expectedPp * (1 - spread)), Math.Min(maximumPp, expectedPp * (1 + spread))),
            1,
            PpTargetConfidence.High,
            $"Official osu! ruleset {PpCalculationProtocol.EngineVersion}: projected score and exact 100% full-combo ceiling for the selected mods.",
            request.BeatmapId,
            PpTargetMods.Normalise(request.Mods),
            request.ExpectedAccuracy,
            Math.Clamp(request.Attainability, 0, 1));
    }

    private static double? measuredFraction(double? value) => value is >= 0 and <= 1 && double.IsFinite(value.Value) ? value : null;

    private bool tryReadCached(PpTargetExactRequest request, string contentHash, IDictionary<int, PpTargetEstimate> completed)
    {
        if (!cache.TryGetValue(cacheKey(request, contentHash), out CacheEntry? entry) || !validEstimate(entry.Estimate))
            return false;
        completed[request.BeatmapId] = entry.Estimate;
        return true;
    }

    private async Task<bool> trySaveCacheAsync()
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
                await using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    await JsonSerializer.SerializeAsync(stream, new CacheDocument(cache_version, entries), json_options, CancellationToken.None).ConfigureAwait(false);
                    await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }
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
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Console.Error.WriteLine($"AimMod exact PP cache persistence failed for '{cachePath}': {error}");
            return false;
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
            return document.Entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && validEstimate(entry.Estimate))
                           .TakeLast(maximum_cache_entries)
                           .ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        }
    }

    internal static string CacheIdentity(PpTargetExactRequest request, string contentHash) => cacheKey(request, contentHash);

    private static string cacheKey(PpTargetExactRequest request, string? contentHash = null) => string.Join('|',
        cache_version,
        PpCalculationProtocol.EngineVersion,
        PpTargetBeatmapPatternReader.Version,
        PpTargetPatternModel.Version,
        contentHash ?? "accuracy-curve",
        request.BeatmapId,
        request.BeatmapHash?.ToLowerInvariant() ?? $"beatmap-{request.BeatmapId}",
        string.Join(',', PpTargetMods.Normalise(request.Mods)),
        request.PatternProfile?.Identity ?? "no-profile",
        request.ExpectedAccuracy.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        request.Attainability.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

    private static bool isValid(PpTargetExactRequest request) => request is not null
        && request.BeatmapId > 0
        && (string.IsNullOrWhiteSpace(request.BeatmapHash)
            || request.BeatmapHash is { Length: 32 or 64 }
            && request.BeatmapHash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
        && double.IsFinite(request.ExpectedAccuracy) && request.ExpectedAccuracy is >= 0 and <= 1
        && double.IsFinite(request.Attainability);

    private static bool validEstimate(PpTargetEstimate estimate) => estimate is not null
        && double.IsFinite(estimate.ExpectedPp) && estimate.ExpectedPp >= 0
        && double.IsFinite(estimate.RealisticMaximumPp) && estimate.RealisticMaximumPp >= estimate.ExpectedPp
        && estimate.ExpectedPpRange is not null
        && double.IsFinite(estimate.ExpectedPpRange.Minimum) && estimate.ExpectedPpRange.Minimum >= 0
        && double.IsFinite(estimate.ExpectedPpRange.Maximum) && estimate.ExpectedPpRange.Maximum >= estimate.ExpectedPpRange.Minimum
        && estimate.ExpectedPpRange.Maximum <= estimate.RealisticMaximumPp
        && (estimate.BeatmapId is null or > 0)
        && (estimate.ExpectedAccuracy is null || double.IsFinite(estimate.ExpectedAccuracy.Value) && estimate.ExpectedAccuracy is >= 0 and <= 1)
        && (estimate.Attainability is null || double.IsFinite(estimate.Attainability.Value) && estimate.Attainability is >= 0 and <= 1)
        && estimate.Method.Contains(PpCalculationProtocol.EngineVersion, StringComparison.Ordinal);

    private static void deleteIfPresent(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record CacheDocument(int Version, IReadOnlyList<CacheEntry> Entries);
    private sealed record CacheEntry(string Key, DateTimeOffset CalculatedAt, PpTargetEstimate Estimate);
}

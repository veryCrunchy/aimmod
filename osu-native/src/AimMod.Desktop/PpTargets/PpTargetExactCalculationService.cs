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
    PpPatternProfile? PatternProfile = null,
    string? ModsJson = null, bool LegacyScore = false,
    PpOutcomeProfile? Outcomes = null, PpTargetMapContext? Map = null);

/// <summary>Catalog metadata of the candidate difficulty, before mods.</summary>
public sealed record PpTargetMapContext(double StarRating, double Bpm, int LengthSeconds, double? OverallDifficulty, double? ApproachRate);

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
    private const int cache_version = 8;
    private const int maximum_batch_size = 200;
    private const int maximum_cache_entries = 16_384;
    private const int calibration_budget = 40;
    private static readonly JsonSerializerOptions json_options = new(JsonSerializerDefaults.Web);

    private readonly string libraryRoot;
    private readonly string cachePath;
    private readonly IOfficialBeatmapDifficultyClient? difficultyClient;
    private readonly string difficultyDownloadDirectory;
    private readonly Func<SidecarRuntimeClient> runtimeFactory;
    private readonly SemaphoreSlim calculationGate = new(1, 1);
    private readonly SemaphoreSlim saveGate = new(1, 1);
    private readonly Dictionary<string, CacheEntry> cache;
    private readonly Dictionary<string, PpWhatIfResult> performanceCache;
    private readonly Dictionary<string, double> calibrationCache;
    private readonly PpTargetBeatmapPatternReader patternReader;
    private readonly int maximumConcurrency;

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
        Func<SidecarRuntimeClient> runtimeFactory,
        int? maximumConcurrency = null)
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
        this.maximumConcurrency = maximumConcurrency ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 3);
        if (this.maximumConcurrency is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        cache = loadCache(this.cachePath);
        performanceCache = loadPerformanceCache(this.cachePath);
        calibrationCache = loadCalibrationCache(this.cachePath);
        patternReader = new PpTargetBeatmapPatternReader(Directory.Exists(this.cachePath)
            ? Path.Combine(this.cachePath, "beatmap-patterns") : this.cachePath + ".beatmaps");
    }

    /// <summary>Estimates keyed by beatmap id. When several variants of one beatmap are requested, the first requested variant wins.</summary>
    public async Task<IReadOnlyDictionary<int, PpTargetEstimate>> CalculateAsync(
        IReadOnlyList<PpTargetExactRequest> requests,
        CancellationToken cancellationToken = default,
        IProgress<PpTargetExactCalculationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(requests);
        PpTargetExactRequest[] valid = selectValid(requests);
        Dictionary<string, PpTargetEstimate> byVariant = await calculateCoreAsync(valid, cancellationToken, progress).ConfigureAwait(false);
        var byBeatmap = new Dictionary<int, PpTargetEstimate>();
        foreach (PpTargetExactRequest request in valid)
            if (byVariant.TryGetValue(VariantKey(request), out PpTargetEstimate? estimate))
                byBeatmap.TryAdd(request.BeatmapId, estimate);
        return byBeatmap;
    }

    /// <summary>Estimates keyed by <see cref="VariantKey"/>, so variants of one beatmap never collide.</summary>
    public async Task<IReadOnlyDictionary<string, PpTargetEstimate>> CalculateVariantsAsync(
        IReadOnlyList<PpTargetExactRequest> requests,
        CancellationToken cancellationToken = default,
        IProgress<PpTargetExactCalculationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(requests);
        return await calculateCoreAsync(selectValid(requests), cancellationToken, progress).ConfigureAwait(false);
    }

    public static string VariantKey(PpTargetExactRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return cacheKey(request);
    }

    private static PpTargetExactRequest[] selectValid(IReadOnlyList<PpTargetExactRequest> requests) =>
        requests.Where(isValid).DistinctBy(request => cacheKey(request)).Take(maximum_batch_size).ToArray();

    private async Task<Dictionary<string, PpTargetEstimate>> calculateCoreAsync(
        PpTargetExactRequest[] valid,
        CancellationToken cancellationToken,
        IProgress<PpTargetExactCalculationProgress>? progress)
    {
        if (valid.Length == 0)
            return new Dictionary<string, PpTargetEstimate>();

        await calculationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        int pendingCacheWrites = 0;
        var checkpointTimer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var completed = new Dictionary<string, PpTargetEstimate>();
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

            PpOutcomeObservation[] calibrating = calibrationCandidates(missing);
            string[] hashes = missing.Where(request => !retainedFiles.ContainsKey(cacheKey(request))).Select(request => request.BeatmapHash)
                                     .Concat(calibrating.Where(o => !hasCalibration(o)).Select(o => o.BeatmapHash))
                                     .Where(hash => !string.IsNullOrWhiteSpace(hash))
                                     .Cast<string>()
                                     .Distinct(StringComparer.OrdinalIgnoreCase)
                                     .ToArray();
            bool resolveLazerFiles = hashes.Length > 0 && File.Exists(Path.Combine(libraryRoot, "client.realm"));
            await using SidecarRuntimeClient? runtime = resolveLazerFiles ? runtimeFactory() : null;

            Exception? firstCalculationFailure = null;
            ExternalLazerAssetStagingLease? resolved = null;
            if (resolveLazerFiles)
            {
                try
                {
                    resolved = await new ExternalLazerAssetClient(new SidecarRuntimeRequestClient(runtime!)).ResolveToPrivateStagingAsync(
                        libraryRoot,
                        hashes,
                        Array.Empty<Guid>(),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (ExternalLazerAssetClientException error)
                {
                    // Official difficulty downloads can still cover maps the local library could not stage.
                    firstCalculationFailure = error;
                }
            }
            await using ExternalLazerAssetStagingLease? lease = resolved;
            Dictionary<string, ExternalLazerResolvedAsset> beatmaps = (lease?.Result.Files ?? [])
                .Where(file => string.Equals(file.Kind, "Beatmap", StringComparison.Ordinal))
                .GroupBy(file => file.OwnerId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<PpCalibrationSample> calibration = await calibrateAsync(calibrating, beatmaps, runtime, cancellationToken).ConfigureAwait(false);
            if (calibrating.Length > 0) pendingCacheWrites++;

            int finished = valid.Length - missing.Length;
            using var resultGate = new SemaphoreSlim(1, 1);
            using var preparationGate = new SemaphoreSlim(1, 1);
            // Variants of the same map share a lane so downloads cannot overwrite each other.
            var groups = missing.GroupBy(request => request.BeatmapId).ToArray();
            int workerCount = Math.Min(maximumConcurrency, groups.Length);
            await Task.WhenAll(Enumerable.Range(0, workerCount).Select(runWorker)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            async Task runWorker(int workerIndex)
            {
                SidecarRuntimeClient? workerRuntime = null;
                PpWhatIfClient? ppClient = null;
                PpWhatIfClient getClient() => ppClient ??= new PpWhatIfClient(new SidecarRuntimeRequestClient(
                    workerIndex == 0 && runtime is not null ? runtime : workerRuntime ??= runtimeFactory()));
                string workingDirectory = Path.Combine(difficultyDownloadDirectory, $"scan-{Guid.NewGuid():N}");
                try
                {
                    for (int groupIndex = workerIndex; groupIndex < groups.Length; groupIndex += workerCount)
                        foreach (var request in groups[groupIndex])
                            await calculateOne(request, getClient, workingDirectory).ConfigureAwait(false);
                }
                finally
                {
                    if (workerRuntime is not null) await workerRuntime.DisposeAsync().ConfigureAwait(false);
                    try
                    {
                        if (Directory.Exists(workingDirectory))
                            Directory.Delete(workingDirectory, true);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
            }

            async Task calculateOne(PpTargetExactRequest request, Func<PpWhatIfClient> ppClient, string workingDirectory)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? downloadedPath = null;
                try
                {
                    string? beatmapPath;
                    await preparationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var retained = await patternReader.TryGetCachedFileAsync(request.BeatmapId, request.BeatmapHash, cancellationToken).ConfigureAwait(false);
                        beatmapPath = retained?.Path ?? (request.BeatmapHash is { Length: > 0 } hash && beatmaps.TryGetValue(hash, out ExternalLazerResolvedAsset? localBeatmap)
                            ? localBeatmap.StagedPath : null);
                        if (beatmapPath is not null)
                        {
                            // Pin inputs before another lane can trim the pattern cache.
                            Directory.CreateDirectory(workingDirectory);
                            string privatePath = Path.Combine(workingDirectory, "calculation.osu");
                            File.Copy(beatmapPath, privatePath, true);
                            beatmapPath = privatePath;
                        }
                    }
                    finally { preparationGate.Release(); }

                    if (beatmapPath is null && difficultyClient is not null)
                    {
                        OfficialBeatmapDifficultyDownloadResult download = await difficultyClient.DownloadDifficultyAsync(
                            request.BeatmapId, workingDirectory, cancellationToken).ConfigureAwait(false);
                        if (download.Status == OfficialBeatmapRequestStatus.Success)
                            beatmapPath = downloadedPath = download.BeatmapPath;
                        else
                            throw new InvalidOperationException($"Beatmap difficulty {request.BeatmapId} download failed with status {download.Status}.");
                    }
                    if (beatmapPath is null)
                        return;

                    IReadOnlyList<string> mods = PpTargetMods.Normalise(request.Mods);
                    PpTargetBeatmapFile file;
                    PpTargetBeatmapPatternGeometry geometry;
                    await preparationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        file = await PpTargetBeatmapPatternReader.IdentifyAsync(beatmapPath, request.BeatmapHash, cancellationToken).ConfigureAwait(false);
                        geometry = await patternReader.ReadAsync(file, mods, cancellationToken, request.ModsJson, request.LegacyScore).ConfigureAwait(false);
                        await patternReader.RetainAsync(file, request.BeatmapId, request.BeatmapHash, cancellationToken).ConfigureAwait(false);
                    }
                    finally { preparationGate.Release(); }
                    string stagingDirectory = workingDirectory;
                    double? clockRate = geometry.ClockRate ?? PpTargetDifficulty.ClockRate(mods, request.ModsJson);
                    var features = PpTargetPatternModel.ExtractFeatures(geometry.Points, geometry.HitRadius, geometry.ClockRate) with
                    {
                        StarRating = request.Map is { StarRating: > 0 } map ? map.StarRating : null,
                        OverallDifficulty = PpTargetDifficulty.OverallDifficulty(request.Map?.OverallDifficulty, mods, clockRate),
                        ApproachRate = PpTargetDifficulty.ApproachRate(request.Map?.ApproachRate, mods, clockRate),
                    };
                    PpPatternPrediction? prediction = request.PatternProfile is { } profile
                        ? PpTargetPatternModel.Predict(features, profile, mods, request.ModsJson, request.LegacyScore)
                        : null;
                    double attainability = measuredFraction(prediction?.Fit) ?? request.Attainability;
                    PpWhatIfResult ceiling = await calculatePerformanceAsync(ppClient, file.ContentHash, new PpWhatIfRequest(
                        stagingDirectory, beatmapPath, mods, 1, 0, null, ModsJson: request.ModsJson, LegacyScore: request.LegacyScore), cancellationToken).ConfigureAwait(false);
                    int objects = ceiling.ObjectCount;
                    string setup = PpTargetOutcomeModel.Setup(mods, request.ModsJson);
                    double stars = request.Map is { StarRating: > 0 } context ? context.StarRating : ceiling.StarRating;
                    var target = new PpOutcomeTarget(stars, request.Map?.Bpm, request.Map?.LengthSeconds ?? 0, features.OverallDifficulty, features.ApproachRate,
                        objects, ceiling.MaxCombo, setup, request.LegacyScore, $"online:{request.BeatmapId}", features);
                    var prior = PpTargetOutcomeModel.Prior(prediction, request.ExpectedAccuracy, attainability, objects,
                        PpTargetOutcomeModel.HeadAccuracyOffset(request.Outcomes, request.PatternProfile));
                    PpOutcomeDistribution distribution = PpTargetOutcomeModel.Fit(request.Outcomes, target, prior, request.PatternProfile);
                    // One official calculation per miss count; accuracy spread uses a local slope around the no-miss score.
                    var scenarios = new List<PpScenario>();
                    foreach (int misses in PpTargetOutcomeModel.ScenarioMisses(distribution, objects))
                    {
                        double accuracy = FeasibleAccuracy(distribution.ScenarioAccuracy(misses, objects), misses, objects);
                        int combo = distribution.ScenarioCombo(misses, objects, ceiling.MaxCombo);
                        PpWhatIfResult result = await calculatePerformanceAsync(ppClient, file.ContentHash, new PpWhatIfRequest(
                            stagingDirectory, beatmapPath, mods, accuracy, misses, combo, ModsJson: request.ModsJson, LegacyScore: request.LegacyScore), cancellationToken).ConfigureAwait(false);
                        scenarios.Add(new PpScenario(misses, result.Accuracy, combo, result.PerformancePoints));
                    }
                    PpScenario baseline = scenarios[0];
                    double step = baseline.Accuracy + .01 <= 1 ? .01 : -.01;
                    PpWhatIfResult shifted = await calculatePerformanceAsync(ppClient, file.ContentHash, new PpWhatIfRequest(
                        stagingDirectory, beatmapPath, mods, Math.Clamp(baseline.Accuracy + step, 0, 1), 0, baseline.Combo, ModsJson: request.ModsJson, LegacyScore: request.LegacyScore), cancellationToken).ConfigureAwait(false);
                    double slope = baseline.Pp > 0 && Math.Abs(shifted.Accuracy - baseline.Accuracy) > 1e-6
                        ? (shifted.PerformancePoints / baseline.Pp - 1) / (shifted.Accuracy - baseline.Accuracy) : 0;
                    (double factor, int calibrated) = PpTargetOutcomeModel.Calibration(calibration, setup, request.LegacyScore, stars,
                        request.Outcomes?.AsOf ?? DateTimeOffset.UtcNow);
                    var atoms = PpTargetOutcomeModel.Integrate(distribution, scenarios, objects, slope, ceiling.PerformancePoints, factor);
                    double expectedPp = PpTargetOutcomeModel.Mean(atoms);
                    // Keep the original request fields as the ranker's estimate identity.
                    PpTargetEstimate estimate = createEstimate(request, expectedPp, ceiling) with
                    {
                        PatternPrediction = prediction,
                        Features = features,
                        PatternProfileIdentity = request.PatternProfile?.Identity,
                        ModsJson = request.ModsJson,
                        LegacyScore = request.LegacyScore,
                        Outcome = new PpOutcomeEstimate(distribution, scenarios, atoms, slope, factor, calibrated),
                        SampleCount = distribution.Passes,
                        Confidence = distribution.EffectiveSamples >= 12 && distribution.Maps >= 5 ? PpTargetConfidence.High
                            : distribution.EffectiveSamples >= 4 && distribution.Maps >= 2 ? PpTargetConfidence.Medium : PpTargetConfidence.Low,
                        ExpectedPpRange = new(PpTargetOutcomeModel.Quantile(atoms, .2), Math.Min(ceiling.PerformancePoints, PpTargetOutcomeModel.Quantile(atoms, .8))),
                    };
                    string key = cacheKey(request, file.ContentHash);
                    CacheEntry[]? checkpoint = null;
                    await resultGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        cache[key] = new CacheEntry(key, DateTimeOffset.UtcNow, estimate);
                        completed[cacheKey(request)] = estimate;
                        // Checkpoint periodically; the outer finally also flushes on cancellation.
                        if (++pendingCacheWrites >= 25 && checkpointTimer.Elapsed >= TimeSpan.FromSeconds(15))
                        {
                            checkpoint = snapshotEntries();
                            pendingCacheWrites = 0;
                            checkpointTimer.Restart();
                        }
                    }
                    finally { resultGate.Release(); }
                    // Serialise outside the result gate so other lanes keep calculating.
                    if (checkpoint is not null)
                        await trySaveCacheAsync(checkpoint).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Interlocked.CompareExchange(ref firstCalculationFailure, error, null);
                }
                finally
                {
                    if (downloadedPath is not null)
                        deleteIfPresent(downloadedPath);
                    await resultGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try { progress?.Report(new PpTargetExactCalculationProgress(++finished, valid.Length)); }
                    finally { resultGate.Release(); }
                }
            }

            if (completed.Count == 0 && firstCalculationFailure is not null)
                throw new InvalidOperationException("Official PP calculation failed for every requested beatmap difficulty.", firstCalculationFailure);

            return completed;
        }
        finally
        {
            try { if (pendingCacheWrites > 0) await trySaveCacheAsync().ConfigureAwait(false); }
            finally { calculationGate.Release(); }
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
                    PpTargetEstimate estimate = createEstimate(request, expected.PerformancePoints, ceiling);
                    cache[cacheKey(request)] = new CacheEntry(cacheKey(request), DateTimeOffset.UtcNow, estimate);
                    completed[accuracy] = accuracy == 100 ? estimate.RealisticMaximumPp : estimate.ExpectedPp;
                }
            }
            finally
            {
                if (completed.Count > 0)
                    await trySaveCacheAsync().ConfigureAwait(false);
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
        // Approximate the longest successful segment for uniformly dispersed breaks.
        // Unlike the old 50% floor this falls as misses accumulate. Clustering and
        // slider breaks remain unmeasured, so this is not a guaranteed combo.
        int segments = misses + 1;
        double harmonic = 0;
        for (int i = 1; i <= segments; i++) harmonic += 1d / i;
        double successfulObjects = count - misses;
        double longestSegment = Math.Min(successfulObjects, successfulObjects * harmonic / segments);
        double comboRatio = count == 0 ? 0 : longestSegment / count;
        return (misses, Math.Clamp((int)Math.Round(maximumCombo * comboRatio), 0, maximumCombo));
    }

    internal static double FeasibleAccuracy(double accuracy, int misses, int objectCount) => objectCount <= 0
        ? 0
        : Math.Clamp(accuracy, 0, 1 - Math.Clamp(misses, 0, objectCount) / (double)objectCount);

    private static PpTargetEstimate createEstimate(PpTargetExactRequest request, double expectedPp, PpWhatIfResult ceiling)
    {
        if (!double.IsFinite(expectedPp) || expectedPp < 0
            || !double.IsFinite(ceiling.PerformancePoints) || ceiling.PerformancePoints < 0)
            throw new InvalidOperationException($"Official PP calculation returned an invalid value for beatmap difficulty {request.BeatmapId}.");

        double maximumPp = ceiling.PerformancePoints;
        expectedPp = Math.Min(expectedPp, maximumPp);
        double spread = 0.18 + 0.16 * (1 - Math.Clamp(request.Attainability, 0, 1));
        return new PpTargetEstimate(
            expectedPp,
            maximumPp,
            new PpTargetRange(Math.Max(0, expectedPp * (1 - spread)), Math.Min(maximumPp, expectedPp * (1 + spread))),
            1,
            PpTargetConfidence.High,
            $"Official osu! ruleset {PpCalculationProtocol.EngineVersion}: PP if passed, averaged over official scenarios for your fitted miss, accuracy and combo distribution, and the exact 100% full-combo ceiling for the selected mods.",
            request.BeatmapId,
            PpTargetMods.Normalise(request.Mods),
            request.ExpectedAccuracy,
            Math.Clamp(request.Attainability, 0, 1));
    }

    private static double? measuredFraction(double? value) => value is >= 0 and <= 1 && double.IsFinite(value.Value) ? value : null;

    private bool tryReadCached(PpTargetExactRequest request, string contentHash, IDictionary<string, PpTargetEstimate> completed)
    {
        if (!cache.TryGetValue(cacheKey(request, contentHash), out CacheEntry? entry) || !validEstimate(entry.Estimate))
            return false;
        completed[cacheKey(request)] = entry.Estimate;
        return true;
    }

    private CacheEntry[] snapshotEntries() => cache.Values.OrderBy(entry => entry.CalculatedAt).TakeLast(maximum_cache_entries).ToArray();

    private async Task<bool> trySaveCacheAsync(CacheEntry[]? snapshot = null)
    {
        await saveGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            string? directory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            CacheEntry[] entries = snapshot ?? snapshotEntries();
            string temporaryPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    Dictionary<string, PpWhatIfResult> performance;
                    lock (performanceCache) performance = performanceCache.TakeLast(maximum_cache_entries).ToDictionary(p => p.Key, p => p.Value);
                    Dictionary<string, double> calibration;
                    lock (calibrationCache) calibration = calibrationCache.TakeLast(maximum_cache_entries).ToDictionary(p => p.Key, p => p.Value);
                    await JsonSerializer.SerializeAsync(stream, new CacheDocument(cache_version, entries, performance, calibration), json_options, CancellationToken.None).ConfigureAwait(false);
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
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            Console.Error.WriteLine($"AimMod exact PP cache persistence failed for '{cachePath}': {error}");
            return false;
        }
        finally { saveGate.Release(); }
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
            var entries = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
            foreach (CacheEntry entry in document.Entries.TakeLast(maximum_cache_entries))
                if (entry is not null && !string.IsNullOrWhiteSpace(entry.Key) && validEstimate(entry.Estimate))
                    entries[entry.Key] = entry;
            return entries;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
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
        PpTargetOutcomeModel.Version,
        request.Outcomes?.Identity ?? "no-outcomes",
        request.Map is { } map ? string.Join(',', new double?[] { map.StarRating, map.Bpm, map.LengthSeconds, map.OverallDifficulty, map.ApproachRate }
            .Select(value => value?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "-")) : "no-map",
        contentHash ?? "accuracy-curve",
        request.BeatmapId,
        request.BeatmapHash?.ToLowerInvariant() ?? $"beatmap-{request.BeatmapId}",
        string.Join(',', PpTargetMods.Normalise(request.Mods)),
        request.ModsJson ?? "",
        request.LegacyScore,
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

    private sealed record CacheDocument(int Version, IReadOnlyList<CacheEntry> Entries, Dictionary<string, PpWhatIfResult>? Performance = null,
        Dictionary<string, double>? Calibration = null);

    private PpOutcomeObservation[] calibrationCandidates(IEnumerable<PpTargetExactRequest> requests)
    {
        PpTargetExactRequest[] withOutcomes = requests.Where(r => r.Outcomes is not null).ToArray();
        if (withOutcomes.Length == 0) return [];
        var setups = withOutcomes.Select(r => PpTargetOutcomeModel.Setup(r.Mods, r.ModsJson)).ToHashSet(StringComparer.Ordinal);
        return withOutcomes.Select(r => r.Outcomes!).DistinctBy(o => o.Identity).SelectMany(o => o.Observations)
            .Where(o => o.Passed && o.Pp is > 0 && o.Combo is > 0 && setups.Contains(o.Setup) && (o.BeatmapId > 0 || o.BeatmapHash is not null))
            .OrderByDescending(o => o.PlayedAt).DistinctBy(calibrationKey).Take(calibration_budget).ToArray();
    }

    private bool hasCalibration(PpOutcomeObservation observation)
    {
        lock (calibrationCache) return calibrationCache.ContainsKey(calibrationKey(observation));
    }

    private static string calibrationKey(PpOutcomeObservation o) => string.Join('|', PpCalculationProtocol.EngineVersion,
        o.BeatmapHash ?? $"beatmap-{o.BeatmapId}", string.Join(',', PpTargetMods.Normalise(o.Mods)), o.ModsJson, o.LegacyScore,
        o.Accuracy.ToString("R", System.Globalization.CultureInfo.InvariantCulture), o.Misses, o.Combo,
        o.Pp?.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

    // Recorded PP relative to the pinned calculator at the same accuracy, misses and combo. It exposes
    // scoring differences the scenario model cannot see, such as slider-tail misses or older PP versions.
    private async Task<IReadOnlyList<PpCalibrationSample>> calibrateAsync(PpOutcomeObservation[] observations,
        IReadOnlyDictionary<string, ExternalLazerResolvedAsset> staged, SidecarRuntimeClient? shared, CancellationToken cancellationToken)
    {
        var samples = new List<PpCalibrationSample>();
        if (observations.Length == 0) return samples;
        SidecarRuntimeClient? owned = null;
        PpWhatIfClient? client = null;
        string directory = Path.Combine(difficultyDownloadDirectory, $"calibration-{Guid.NewGuid():N}");
        int downloads = 0;
        try
        {
            foreach (PpOutcomeObservation observation in observations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string key = calibrationKey(observation);
                double ratio;
                bool known;
                lock (calibrationCache) known = calibrationCache.TryGetValue(key, out ratio);
                if (!known)
                {
                    try
                    {
                        string? path = (await patternReader.TryGetCachedFileAsync(observation.BeatmapId, observation.BeatmapHash, cancellationToken).ConfigureAwait(false))?.Path
                            ?? (observation.BeatmapHash is { } hash && staged.TryGetValue(hash, out var asset) ? asset.StagedPath : null);
                        Directory.CreateDirectory(directory);
                        if (path is null && difficultyClient is not null && observation.BeatmapId > 0 && downloads++ < 10)
                        {
                            var download = await difficultyClient.DownloadDifficultyAsync(observation.BeatmapId, directory, cancellationToken).ConfigureAwait(false);
                            if (download.Status == OfficialBeatmapRequestStatus.Success) path = download.BeatmapPath;
                        }
                        if (path is null) continue;
                        string file = Path.Combine(directory, "calibration.osu");
                        if (!string.Equals(Path.GetFullPath(path), file, StringComparison.OrdinalIgnoreCase)) File.Copy(path, file, true);
                        client ??= new PpWhatIfClient(new SidecarRuntimeRequestClient(shared ?? (owned ??= runtimeFactory())));
                        PpWhatIfResult result = await client.CalculateAsync(new PpWhatIfRequest(directory, file, PpTargetMods.Normalise(observation.Mods),
                            observation.Accuracy, observation.Misses, observation.Combo, ModsJson: observation.ModsJson.Length == 0 ? null : observation.ModsJson,
                            LegacyScore: observation.LegacyScore), cancellationToken).ConfigureAwait(false);
                        ratio = result.PerformancePoints > 1 ? observation.Pp!.Value / result.PerformancePoints : -1;
                        // A different beatmap revision or PP version makes a ratio meaningless rather than informative.
                        if (ratio is not (>= .6 and <= 1.6)) ratio = -1;
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        ratio = -1;
                    }
                    lock (calibrationCache) calibrationCache[key] = ratio;
                }
                if (ratio > 0) samples.Add(new PpCalibrationSample(observation.Setup, observation.LegacyScore, observation.Stars, observation.PlayedAt, ratio));
            }
        }
        finally
        {
            if (owned is not null) await owned.DisposeAsync().ConfigureAwait(false);
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return samples;
    }

    private static Dictionary<string, double> loadCalibrationCache(string path)
    {
        try
        {
            if (!File.Exists(path)) return new(StringComparer.Ordinal);
            using var stream = File.OpenRead(path);
            var document = JsonSerializer.Deserialize<CacheDocument>(stream, json_options);
            return document?.Version == cache_version && document.Calibration is { } values
                ? values.Where(p => double.IsFinite(p.Value)).TakeLast(maximum_cache_entries).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return new(StringComparer.Ordinal); }
    }

    internal static string PerformanceIdentity(string contentHash, PpWhatIfRequest request) =>
        JsonSerializer.Serialize(new { Engine = PpCalculationProtocol.EngineVersion, contentHash,
            Mods = PpTargetMods.Normalise(request.Mods), request.ModsJson, request.Accuracy, request.MissCount,
            request.MaxCombo, request.Statistics, request.LegacyScore, request.LegacyTotalScore, request.RulesetId, request.Passed });

    private async Task<PpWhatIfResult> calculatePerformanceAsync(Func<PpWhatIfClient> client, string contentHash, PpWhatIfRequest request, CancellationToken token)
    {
        string key = PerformanceIdentity(contentHash, request);
        lock (performanceCache) if (performanceCache.TryGetValue(key, out var saved)) return saved;
        token.ThrowIfCancellationRequested();
        var result = await client().CalculateAsync(request, token).ConfigureAwait(false);
        if (double.IsFinite(result.PerformancePoints) && result.PerformancePoints >= 0)
            lock (performanceCache)
            {
                if (performanceCache.Count >= maximum_cache_entries) performanceCache.Remove(performanceCache.Keys.First());
                performanceCache[key] = result;
            }
        return result;
    }

    private static Dictionary<string, PpWhatIfResult> loadPerformanceCache(string path)
    {
        try
        {
            if (!File.Exists(path)) return new(StringComparer.Ordinal);
            using var stream = File.OpenRead(path);
            var document = JsonSerializer.Deserialize<CacheDocument>(stream, json_options);
            return document?.Version == cache_version && document.Performance is { } results
                ? results.Where(p => p.Value is not null && p.Value.EngineVersion == PpCalculationProtocol.EngineVersion
                    && double.IsFinite(p.Value.PerformancePoints) && p.Value.PerformancePoints >= 0
                    && p.Value.MaxCombo > 0 && p.Value.ObjectCount > 0 && double.IsFinite(p.Value.Accuracy) && p.Value.Accuracy is >= 0 and <= 1)
                    .TakeLast(maximum_cache_entries).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return new(StringComparer.Ordinal); }
    }
    private sealed record CacheEntry(string Key, DateTimeOffset CalculatedAt, PpTargetEstimate Estimate);
}

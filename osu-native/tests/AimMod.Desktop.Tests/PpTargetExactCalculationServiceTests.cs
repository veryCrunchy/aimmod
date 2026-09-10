using AimMod.Desktop.PpTargets;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class PpTargetExactCalculationServiceTests
{
    private string temporaryDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"aimmod-exact-pp-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(temporaryDirectory))
            Directory.Delete(temporaryDirectory, true);
    }

    [Test]
    public async Task StableOnlyCalculationWithKnownHashDownloadsWithoutResolvingLazerDatabase()
    {
        const int id = 459;
        string beatmap = createBeatmap(id);
        string hash = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(beatmap))).ToLowerInvariant();
        var download = new StubDifficultyClient(id, beatmap);
        var service = new PpTargetExactCalculationService(temporaryDirectory, Path.Combine(temporaryDirectory, "stable.json"),
            download, Path.Combine(temporaryDirectory, "downloads"), () => SidecarRuntimeClient.Start(desktopExecutablePath()));
        Assert.That(File.Exists(Path.Combine(temporaryDirectory, "client.realm")), Is.False);
        var results = await service.CalculateAsync([new(id, hash, [], .96, .7, LegacyScore: true)]);
        Assert.That(results[id].RealisticMaximumPp, Is.GreaterThan(0));
        Assert.That(results[id].Features, Is.Not.Null);
        Assert.That(download.RequestedBeatmapIds, Is.EqualTo(new[] { id }));
    }

    [Test]
    public void PerformanceCacheTracksScoreInputsButNotStagingPaths()
    {
        var request = new PpWhatIfRequest("stage", "map.osu", ["HD"], .98);
        string key = PpTargetExactCalculationService.PerformanceIdentity("content", request);
        Assert.That(PpTargetExactCalculationService.PerformanceIdentity("content", request with { StagingDirectory = "other", BeatmapPath = "other.osu" }), Is.EqualTo(key));
        foreach (var changed in new[] { request with { Accuracy = .97 }, request with { MissCount = 1 },
                     request with { MaxCombo = 10 }, request with { Mods = ["DT"] }, request with { Passed = false },
                     request with { LegacyScore = true }, request with { RulesetId = 1 },
                     request with { Statistics = new(100, 1, 0, 0, 0, 0) }, request with { ModsJson = "custom" } })
            Assert.That(PpTargetExactCalculationService.PerformanceIdentity("content", changed), Is.Not.EqualTo(key));
        Assert.That(PpTargetExactCalculationService.PerformanceIdentity("changed-content", request), Is.Not.EqualTo(key));
    }

    [Test]
    public async Task ChangedProfileReusesPersistedPerformanceWithoutStartingWorkers()
    {
        const int id = 456;
        string path = Path.Combine(temporaryDirectory, "profile-change.json");
        var service = new PpTargetExactCalculationService(temporaryDirectory, path,
            new StubDifficultyClient(id, createBeatmap(id)), Path.Combine(temporaryDirectory, "downloads"),
            () => SidecarRuntimeClient.Start(desktopExecutablePath()));
        var request = new PpTargetExactRequest(id, null, [], .94, .5);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var cold = await service.CalculateAsync([request]);
        long coldMs = watch.ElapsedMilliseconds;
        var reopened = new PpTargetExactCalculationService(temporaryDirectory, path,
            new FailingDifficultyClient(), Path.Combine(temporaryDirectory, "downloads"),
            () => throw new AssertionException("An unchanged score shape must reuse raw performance after a profile update."));
        watch.Restart();
        var warm = await reopened.CalculateAsync([request with { PatternProfile = new("updated-player", DateTimeOffset.UtcNow, 30, []) }]);
        TestContext.WriteLine($"Exact map cold: {coldMs}ms; changed-profile cached refresh: {watch.ElapsedMilliseconds}ms (no worker or download).");
        Assert.That(warm[id].ExpectedPp, Is.EqualTo(cold[id].ExpectedPp));
        Assert.That(warm[id].PatternProfileIdentity, Is.EqualTo("updated-player"));
        Assert.That(warm[id].Features, Is.Not.Null);
    }

    [Test]
    public void ConfiguredModsHaveSeparateCalculationCaches() {
        var request=new PpTargetExactRequest(42,null,["DT"],.98,.5);
        var configured=request with { ModsJson="[{\"acronym\":\"DT\",\"settings\":{\"speed_change\":1.2}}]" };
        Assert.That(PpTargetExactCalculationService.CacheIdentity(configured,"synthetic"),
            Is.Not.EqualTo(PpTargetExactCalculationService.CacheIdentity(request,"synthetic")));
    }

    [Test]
    public void FrequentMissesDoNotRetainHalfTheMaximumCombo()
    {
        var one = PpTargetExactCalculationService.ExpectedScoreShape(.5, 1000, 1000, .001);
        var ten = PpTargetExactCalculationService.ExpectedScoreShape(.5, 1000, 1000, .01);
        var many = PpTargetExactCalculationService.ExpectedScoreShape(.5, 1000, 1000, .1);
        Assert.That(one.Combo, Is.GreaterThan(ten.Combo));
        Assert.That(ten.Combo, Is.LessThan(500));
        Assert.That(many.Combo, Is.LessThan(ten.Combo));
        Assert.That(PpTargetExactCalculationService.ExpectedScoreShape(.5, 1000, 1000, 1).Combo, Is.Zero);
    }

    [Test]
    public async Task InterruptedBatchPreservesCompletedDifficultyAcrossRestart()
    {
        const int id = 456;
        string path = Path.Combine(temporaryDirectory, "interrupted.json");
        var first = new PpTargetExactCalculationService(temporaryDirectory, path,
            new StubDifficultyClient(id, createBeatmap(id)), Path.Combine(temporaryDirectory, "downloads"),
            () => SidecarRuntimeClient.Start(desktopExecutablePath()), maximumConcurrency: 1);
        using var cancellation = new CancellationTokenSource();
        var request = new PpTargetExactRequest(id, null, [], .94, .5);
        Assert.ThrowsAsync<OperationCanceledException>(async () => await first.CalculateAsync(
            [request, request with { BeatmapId = id + 1 }], cancellation.Token,
            new CancelAfterFirst(cancellation)));
        var reopened = new PpTargetExactCalculationService(temporaryDirectory, path,
            new FailingDifficultyClient(), Path.Combine(temporaryDirectory, "downloads"),
            () => throw new AssertionException("Completed PP must survive an interrupted batch."));
        Assert.That((await reopened.CalculateAsync([request])).ContainsKey(id), Is.True);
    }

    private sealed class CancelAfterFirst(CancellationTokenSource cancellation) : IProgress<PpTargetExactCalculationProgress>
    {
        public void Report(PpTargetExactCalculationProgress value)
        {
            if (value.Completed >= 1) cancellation.Cancel();
        }
    }

    [Test]
    public async Task ParallelScanMatchesSequentialResultsAndReusesCache()
    {
        var requests = Enumerable.Range(800, 6).Select(id => new PpTargetExactRequest(id, null, ["HD"], .94, .5)).ToArray();
        var sequentialDownloads = new ConcurrentDifficultyClient();
        var parallelDownloads = new ConcurrentDifficultyClient();
        string cachePath = Path.Combine(temporaryDirectory, "parallel.json");
        var sequential = new PpTargetExactCalculationService(temporaryDirectory, Path.Combine(temporaryDirectory, "sequential.json"),
            sequentialDownloads, Path.Combine(temporaryDirectory, "serial-downloads"),
            () => SidecarRuntimeClient.Start(desktopExecutablePath()), 1);
        var parallel = new PpTargetExactCalculationService(temporaryDirectory, cachePath,
            parallelDownloads, Path.Combine(temporaryDirectory, "parallel-downloads"),
            () => SidecarRuntimeClient.Start(desktopExecutablePath()), 3);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var expected = await sequential.CalculateAsync(requests);
        var serialTime = timer.Elapsed;
        timer.Restart();
        var progress = new CollectedProgress();
        var actual = await parallel.CalculateAsync(requests, progress: progress);
        TestContext.WriteLine($"Six exact maps: sequential {serialTime.TotalMilliseconds:F0}ms; parallel {timer.Elapsed.TotalMilliseconds:F0}ms");
        Assert.That(parallelDownloads.MaximumActive, Is.InRange(2, 3));
        Assert.That(sequentialDownloads.MaximumActive, Is.EqualTo(1));
        Assert.That(progress.Values, Is.EqualTo(Enumerable.Range(0, 7)));
        foreach (var request in requests)
        {
            Assert.That(actual[request.BeatmapId].ExpectedPp, Is.EqualTo(expected[request.BeatmapId].ExpectedPp));
            Assert.That(actual[request.BeatmapId].RealisticMaximumPp, Is.EqualTo(expected[request.BeatmapId].RealisticMaximumPp));
        }
        var reopened = new PpTargetExactCalculationService(temporaryDirectory, cachePath, new FailingDifficultyClient(),
            Path.Combine(temporaryDirectory, "reopened"), () => throw new AssertionException("Cached scan started a worker."));
        Assert.That((await reopened.CalculateAsync(requests)).Count, Is.EqualTo(6));
        Assert.That(Directory.GetDirectories(Path.Combine(temporaryDirectory, "parallel-downloads")), Is.Empty);
    }

    [Test]
    public async Task ParallelScanRetainsSuccessfulMapsWhenOneDownloadFails()
    {
        var downloads = new ConcurrentDifficultyClient(800);
        var service = new PpTargetExactCalculationService(temporaryDirectory, Path.Combine(temporaryDirectory, "partial.json"),
            downloads, Path.Combine(temporaryDirectory, "partial"), () => SidecarRuntimeClient.Start(desktopExecutablePath()), 3);
        var requests = Enumerable.Range(800, 6).Select(id => new PpTargetExactRequest(id, null, [], .94, .5)).ToArray();
        var progress = new CollectedProgress();
        var result = await service.CalculateAsync(requests, progress: progress);
        Assert.That(result.Keys, Is.EquivalentTo(Enumerable.Range(801, 5)));
        Assert.That(progress.Values, Is.EqualTo(Enumerable.Range(0, 7)));
        Assert.That(downloads.Active, Is.Zero);
    }

    [Test]
    public async Task CancellingParallelScanJoinsWorkersAndPreservesCompletedResults()
    {
        string cachePath = Path.Combine(temporaryDirectory, "cancelled.json");
        string downloadsPath = Path.Combine(temporaryDirectory, "cancelled");
        var downloads = new ConcurrentDifficultyClient();
        var service = new PpTargetExactCalculationService(temporaryDirectory, cachePath,
            downloads, downloadsPath, () => SidecarRuntimeClient.Start(desktopExecutablePath()), 3);
        var requests = Enumerable.Range(800, 12).Select(id => new PpTargetExactRequest(id, null, [], .94, .5)).ToArray();
        using var cancellation = new CancellationTokenSource();
        Assert.CatchAsync<OperationCanceledException>(async () => await service.CalculateAsync(requests,
            cancellation.Token, new CancelAfterFirst(cancellation)));
        Assert.That(downloads.Active, Is.Zero);
        Assert.That(Directory.GetDirectories(downloadsPath), Is.Empty);
        using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(cachePath));
        var ids = document.RootElement.GetProperty("entries").EnumerateArray()
            .Select(entry => entry.GetProperty("estimate").GetProperty("beatmapId").GetInt32()).ToHashSet();
        Assert.That(ids, Is.Not.Empty);
        var reopened = new PpTargetExactCalculationService(temporaryDirectory, cachePath, new FailingDifficultyClient(),
            downloadsPath, () => throw new AssertionException("Cancelled scan lost completed results."));
        Assert.That((await reopened.CalculateAsync(requests.Where(request => ids.Contains(request.BeatmapId)).ToArray())).Count,
            Is.EqualTo(ids.Count));
    }

    private sealed class CollectedProgress : IProgress<PpTargetExactCalculationProgress>
    {
        public List<int> Values { get; } = [];
        public void Report(PpTargetExactCalculationProgress value) => Values.Add(value.Completed);
    }

    private sealed class ConcurrentDifficultyClient(int? failedBeatmapId = null) : IOfficialBeatmapDifficultyClient
    {
        private int active;
        private readonly object gate = new();
        public int Active => active;
        public int MaximumActive { get; private set; }
        public async Task<OfficialBeatmapDifficultyDownloadResult> DownloadDifficultyAsync(int beatmapId,
            string destinationDirectory, CancellationToken cancellationToken = default)
        {
            lock (gate) MaximumActive = Math.Max(MaximumActive, ++active);
            try
            {
                await Task.Delay(150, cancellationToken);
                if (beatmapId == failedBeatmapId) throw new HttpRequestException("Synthetic download failure");
                Directory.CreateDirectory(destinationDirectory);
                string path = Path.Combine(destinationDirectory, $"{beatmapId}.osu");
                await File.WriteAllTextAsync(path, createBeatmap(beatmapId), cancellationToken);
                return new(OfficialBeatmapRequestStatus.Success, beatmapId, path, new FileInfo(path).Length);
            }
            finally { lock (gate) active--; }
        }
    }

    [Test]
    public async Task ConfiguredGeometryAndScoringModeReachTheWorkerAndCache()
    {
        const int id = 456;
        var download = new StubDifficultyClient(id, createBeatmap(id) + "\n256,192,24000,2,0,L|384:192,1,128\n");
        var service = new PpTargetExactCalculationService(temporaryDirectory, Path.Combine(temporaryDirectory, "configured.json"),
            download, Path.Combine(temporaryDirectory, "downloads"), () => SidecarRuntimeClient.Start(desktopExecutablePath()));
        var request = new PpTargetExactRequest(id, null, ["DT"], .96, .7,
            ModsJson: "[{\"acronym\":\"DT\",\"settings\":{\"speed_change\":1.2}}]");
        var lazer = (await service.CalculateAsync([request]))[id];
        var stable = (await service.CalculateAsync([request with { LegacyScore = true }]))[id];
        Assert.That(lazer.Features!.ClockRate, Is.EqualTo(1.2).Within(.0001));
        Assert.That(stable.LegacyScore, Is.True);
        Assert.That(lazer.LegacyScore, Is.False);
        Assert.That(stable.ExpectedPp, Is.Not.EqualTo(lazer.ExpectedPp));
        Assert.That(PpTargetExactCalculationService.CacheIdentity(request, "content"),
            Is.Not.EqualTo(PpTargetExactCalculationService.CacheIdentity(request with { LegacyScore = true }, "content")));
        Assert.That((await service.CalculateAsync([request]))[id], Is.EqualTo(lazer));
    }

    [Test]
    public async Task RemoteDifficultyUsesOfficialCalculatorForExpectedAndFullComboPp()
    {
        const int beatmapId = 456;
        var difficultyClient = new StubDifficultyClient(beatmapId, createBeatmap(beatmapId));
        var service = new PpTargetExactCalculationService(
            temporaryDirectory,
            Path.Combine(temporaryDirectory, "cache.json"),
            difficultyClient,
            Path.Combine(temporaryDirectory, "downloads"),
            () => SidecarRuntimeClient.Start(desktopExecutablePath()));

        IReadOnlyDictionary<int, PpTargetEstimate> result = await service.CalculateAsync([
            new PpTargetExactRequest(beatmapId, null, [], 0.94, 0.5),
        ]);

        PpTargetEstimate estimate = result[beatmapId];
        Assert.Multiple(() =>
        {
            Assert.That(difficultyClient.RequestedBeatmapIds, Is.EqualTo(new[] { beatmapId }));
            Assert.That(File.Exists(Path.Combine(temporaryDirectory, "cache.json")), Is.True);
            Assert.That(estimate.ExpectedPp, Is.GreaterThan(0));
            Assert.That(estimate.RealisticMaximumPp, Is.GreaterThan(estimate.ExpectedPp));
            Assert.That(estimate.ExpectedPpRange.Maximum, Is.LessThanOrEqualTo(estimate.RealisticMaximumPp));
            Assert.That(estimate.BeatmapId, Is.EqualTo(beatmapId));
            Assert.That(estimate.Mods, Is.Empty);
            Assert.That(estimate.ExpectedAccuracy, Is.EqualTo(0.94));
            Assert.That(estimate.Attainability, Is.EqualTo(0.5));
            Assert.That(estimate.Method, Does.Contain("exact 100% full-combo ceiling"));
        });
    }

    [Test]
    public async Task AccuracyCurveIsPersistedAndReusedByANewServiceInstance()
    {
        const int beatmapId = 789;
        string cachePath = Path.Combine(temporaryDirectory, "curve-cache.json");
        var difficultyClient = new StubDifficultyClient(beatmapId, createBeatmap(beatmapId));
        var service = new PpTargetExactCalculationService(
            temporaryDirectory,
            cachePath,
            difficultyClient,
            Path.Combine(temporaryDirectory, "downloads"),
            () => SidecarRuntimeClient.Start(desktopExecutablePath()));

        IReadOnlyDictionary<int, double> calculated = await service.CalculateAccuracyCurveAsync(
            beatmapId,
            null,
            ["HD"],
            [95, 98, 100]);
        var reopened = new PpTargetExactCalculationService(
            temporaryDirectory,
            cachePath,
            new FailingDifficultyClient(),
            Path.Combine(temporaryDirectory, "downloads-reopened"),
            () => throw new AssertionException("The runtime must not start when every accuracy point is cached."));
        IReadOnlyDictionary<int, double> cached = await reopened.CalculateAccuracyCurveAsync(
            beatmapId,
            null,
            ["HD"],
            [95, 98, 100]);

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(cachePath), Is.True);
            Assert.That(difficultyClient.RequestedBeatmapIds, Is.EqualTo(new[] { beatmapId }));
            Assert.That(calculated.Keys, Is.EquivalentTo(new[] { 95, 98, 100 }));
            Assert.That(cached, Is.EquivalentTo(calculated));
        });
    }

    [Test]
    public async Task PatternEvidenceChangesProjectedScoreButPreservesRequestIdentityAndOfficialCeiling()
    {
        const int id = 654;
        string cachePath = Path.Combine(temporaryDirectory, "pattern-pp.json");
        var download = new StubDifficultyClient(id, createBeatmap(id));
        var service = new PpTargetExactCalculationService(temporaryDirectory, cachePath, download,
            Path.Combine(temporaryDirectory, "downloads"), () => SidecarRuntimeClient.Start(desktopExecutablePath()));
        var request = new PpTargetExactRequest(id, null, [], 0.98, 0.95);
        PpTargetEstimate baseline = (await service.CalculateAsync([request]))[id];
        PpPatternProfile weakProfile = createPatternProfile(id, "weak-evidence", 0.82, 0.15);
        PpTargetEstimate weak = (await service.CalculateAsync([request with { PatternProfile = weakProfile }]))[id];
        PpPatternProfile strongProfile = createPatternProfile(id, "strong-evidence", 0.99, 0);
        PpTargetEstimate strong = (await service.CalculateAsync([request with { PatternProfile = strongProfile }]))[id];
        PpPatternPrediction prediction = weak.PatternPrediction!;
        string exactPath = Path.Combine(temporaryDirectory, "profile-map.osu");
        await using SidecarRuntimeClient runtime = SidecarRuntimeClient.Start(desktopExecutablePath());
        var calculator = new PpWhatIfClient(new SidecarRuntimeRequestClient(runtime));
        PpWhatIfResult ceiling = await calculator.CalculateAsync(new PpWhatIfRequest(temporaryDirectory, exactPath, [], 1, 0, null));
        (int misses, int combo) = PpTargetExactCalculationService.ExpectedScoreShape(prediction.Fit!.Value, ceiling.MaxCombo, ceiling.ObjectCount, prediction.ExpectedMissRate);
        PpWhatIfResult expectedScenario = await calculator.CalculateAsync(new PpWhatIfRequest(temporaryDirectory, exactPath, [], prediction.ExpectedAccuracy!.Value, misses, combo));
        Assert.Multiple(() =>
        {
            Assert.That(prediction.ExpectedAccuracy, Is.EqualTo(expectedScenario.Accuracy));
            Assert.That(prediction.ExpectedAccuracy, Is.EqualTo(0.82).Within(0.006));
            Assert.That(prediction.ExpectedMissRate, Is.EqualTo(0.15).Within(0.000001));
            Assert.That(misses, Is.EqualTo(18));
            Assert.That(weak.ExpectedPp, Is.EqualTo(expectedScenario.PerformancePoints));
            Assert.That(weak.ExpectedPp, Is.LessThan(strong.ExpectedPp));
            Assert.That(weak.RealisticMaximumPp, Is.EqualTo(baseline.RealisticMaximumPp));
            Assert.That(weak.RealisticMaximumPp, Is.EqualTo(ceiling.PerformancePoints));
            Assert.That(strong.RealisticMaximumPp, Is.EqualTo(baseline.RealisticMaximumPp));
            Assert.That(weak.ExpectedAccuracy, Is.EqualTo(request.ExpectedAccuracy));
            Assert.That(weak.Attainability, Is.EqualTo(request.Attainability));
            Assert.That(weak.PatternProfileIdentity, Is.EqualTo(weakProfile.Identity));
            Assert.That(download.RequestedBeatmapIds, Has.Count.EqualTo(1), "Changing profiles must reuse the exact cached beatmap.");
        });

        var reopened = new PpTargetExactCalculationService(temporaryDirectory, cachePath, new FailingDifficultyClient(),
            Path.Combine(temporaryDirectory, "other-downloads"), () => throw new AssertionException("Identical evidence must reuse persisted PP."));
        PpTargetEstimate cached = (await reopened.CalculateAsync([
            request with { PatternProfile = weakProfile with { ReferenceTime = weakProfile.ReferenceTime.AddMinutes(1) } },
        ]))[id];
        Assert.That(cached.ExpectedPp, Is.EqualTo(weak.ExpectedPp));
        Assert.That(cached.PatternProfileIdentity, Is.EqualTo(weakProfile.Identity));
    }

    [Test]
    public async Task SparsePatternEvidenceRetainsLegacyProjectedScoreAndExplicitlyUnknownPrediction()
    {
        const int id = 655;
        var download = new StubDifficultyClient(id, createBeatmap(id));
        var service = new PpTargetExactCalculationService(temporaryDirectory, Path.Combine(temporaryDirectory, "sparse.json"), download,
            Path.Combine(temporaryDirectory, "downloads"), () => SidecarRuntimeClient.Start(desktopExecutablePath()));
        var request = new PpTargetExactRequest(id, null, [], 0.95, 0.6);
        PpTargetEstimate legacy = (await service.CalculateAsync([request]))[id];
        var empty = new PpPatternProfile("empty", DateTimeOffset.UtcNow, 30, []);
        PpTargetEstimate sparse = (await service.CalculateAsync([request with { PatternProfile = empty }]))[id];
        Assert.Multiple(() =>
        {
            Assert.That(sparse.ExpectedPp, Is.EqualTo(legacy.ExpectedPp));
            Assert.That(sparse.RealisticMaximumPp, Is.EqualTo(legacy.RealisticMaximumPp));
            Assert.That(sparse.PatternPrediction, Is.Not.Null);
            Assert.That(sparse.PatternPrediction!.ExpectedAccuracy, Is.Null);
            Assert.That(sparse.PatternPrediction.Fit, Is.Null);
        });
    }

    [Test]
    public void CacheIdentityIncludesContentModsModelAndProfileIdentityButNotReferenceTime()
    {
        var profile = new PpPatternProfile("evidence-v1", DateTimeOffset.UtcNow, 30, []);
        var request = new PpTargetExactRequest(123, null, ["HD"], 0.95, 0.8, profile);
        string key = PpTargetExactCalculationService.CacheIdentity(request, "content-a");
        Assert.Multiple(() =>
        {
            Assert.That(key, Does.Contain(PpTargetPatternModel.Version));
            Assert.That(key, Does.Contain(PpTargetBeatmapPatternReader.Version));
            Assert.That(PpTargetExactCalculationService.CacheIdentity(request, "content-b"), Is.Not.EqualTo(key));
            Assert.That(PpTargetExactCalculationService.CacheIdentity(request with { Mods = ["HR"] }, "content-a"), Is.Not.EqualTo(key));
            Assert.That(PpTargetExactCalculationService.CacheIdentity(request with { PatternProfile = profile with { Identity = "evidence-v2" } }, "content-a"), Is.Not.EqualTo(key));
            Assert.That(PpTargetExactCalculationService.CacheIdentity(request with { PatternProfile = profile with { ReferenceTime = profile.ReferenceTime.AddMinutes(5) } }, "content-a"), Is.EqualTo(key));
        });
    }

    [Test]
    public void MeasuredMissRateSetsObjectCountBasedMissesWithExplicitComboHeuristic()
    {
        Assert.Multiple(() =>
        {
            int dispersedCombo = (int)Math.Round(1500 * .98 * Enumerable.Range(1, 21).Sum(i => 1d / i) / 21);
            Assert.That(PpTargetExactCalculationService.ExpectedScoreShape(0.99, 1500, 1000, 0.02), Is.EqualTo((20, dispersedCombo)));
            Assert.That(PpTargetExactCalculationService.ExpectedScoreShape(0.1, 1500, 1000, 0), Is.EqualTo((0, 1500)));
            Assert.That(PpTargetExactCalculationService.ExpectedScoreShape(0.99, 1500, 1000, 1), Is.EqualTo((1000, 0)));
            Assert.That(PpTargetExactCalculationService.ExpectedScoreShape(0.7, 100, 100, null), Is.EqualTo(PpTargetExactCalculationService.ExpectedScoreShape(0.7, 100)));
            Assert.That(PpTargetExactCalculationService.ExpectedScoreShape(0.7, 100, 100, double.NaN), Is.EqualTo(PpTargetExactCalculationService.ExpectedScoreShape(0.7, 100)));
        });
    }

    [Test]
    public async Task ConflictingMeasuredAccuracyAndMissRateProduceFeasibleOfficialScenario()
    {
        const int id = 656;
        var service = new PpTargetExactCalculationService(temporaryDirectory, Path.Combine(temporaryDirectory, "conflicting.json"),
            new StubDifficultyClient(id, createBeatmap(id)), Path.Combine(temporaryDirectory, "downloads"),
            () => SidecarRuntimeClient.Start(desktopExecutablePath()));
        var request = new PpTargetExactRequest(id, null, [], 0.98, 0.95, createPatternProfile(id, "conflicting-evidence", 0.99, 0.1));
        PpTargetEstimate estimate = (await service.CalculateAsync([request]))[id];
        Assert.Multiple(() =>
        {
            Assert.That(PpTargetExactCalculationService.FeasibleAccuracy(0.99, 12, 120), Is.EqualTo(0.9));
            Assert.That(estimate.PatternPrediction!.ExpectedAccuracy, Is.EqualTo(0.9).Within(0.000001));
            Assert.That(estimate.PatternPrediction.ExpectedMissRate, Is.EqualTo(0.1).Within(0.000001));
            Assert.That(estimate.ExpectedAccuracy, Is.EqualTo(0.98), "Original request remains the matching identity.");
            Assert.That(estimate.RealisticMaximumPp, Is.GreaterThan(estimate.ExpectedPp));
        });
    }

    private PpPatternProfile createPatternProfile(int id, string identity, double accuracy, double missRate)
    {
        string path = Path.Combine(temporaryDirectory, "profile-map.osu");
        File.WriteAllText(path, createBeatmap(id));
        PpTargetBeatmapPatternGeometry geometry = PpTargetBeatmapPatternReader.Read(path, []);
        PpPatternFeatures features = PpTargetPatternModel.ExtractFeatures(geometry.Points, geometry.HitRadius, geometry.ClockRate);
        var outcomes = new[] { "Overall", "Jumps", "Speed", "Direction changes", "Bursts", "Streams" }
            .ToDictionary(pattern => pattern, _ => new PpPatternOutcome(120, accuracy, missRate, new Dictionary<ReplayMissReason, int>()));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new PpPatternProfile(identity, now, 30, [
            new PpPatternEvidence(Guid.NewGuid(), "map-a", "", now, features, 1, outcomes),
            new PpPatternEvidence(Guid.NewGuid(), "map-b", "", now, features, 1, outcomes),
        ]);
    }

    [Test]
    [NonParallelizable]
    public async Task PersistenceFailureIsWrittenToStandardError()
    {
        const int beatmapId = 987;
        TextWriter originalError = Console.Error;
        using var error = new StringWriter();
        Console.SetError(error);
        try
        {
            var service = new PpTargetExactCalculationService(
                temporaryDirectory,
                temporaryDirectory,
                new StubDifficultyClient(beatmapId, createBeatmap(beatmapId)),
                Path.Combine(temporaryDirectory, "downloads"),
                () => SidecarRuntimeClient.Start(desktopExecutablePath()));

            await service.CalculateAsync([
                new PpTargetExactRequest(beatmapId, null, [], 0.95, 0.8),
            ]);
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.That(error.ToString(), Does.Contain("exact PP cache persistence failed"));
    }

    private static string desktopExecutablePath() => Path.Combine(
        AppContext.BaseDirectory,
        OperatingSystem.IsWindows() ? "AimMod.exe" : "AimMod");

    private static string createBeatmap(int beatmapId)
    {
        string objects = string.Join('\n', Enumerable.Range(0, 120).Select(index =>
        {
            int x = index % 2 == 0 ? 64 : 448;
            int y = index % 4 < 2 ? 64 : 320;
            return $"{x},{y},{1000 + index * 180},1,0,0:0:0:0:";
        }));
        return $$"""
            osu file format v14

            [General]
            AudioFilename: audio.mp3
            Mode: 0

            [Metadata]
            Title:Exact PP Test
            Artist:AimMod
            Creator:AimMod
            Version:Difficulty
            BeatmapID:{{beatmapId}}
            BeatmapSetID:123

            [Difficulty]
            HPDrainRate:6
            CircleSize:4
            OverallDifficulty:8
            ApproachRate:9
            SliderMultiplier:1.4
            SliderTickRate:1

            [TimingPoints]
            0,500,4,2,1,50,1,0

            [HitObjects]
            {{objects}}
            """;
    }

    private sealed class StubDifficultyClient(int expectedBeatmapId, string beatmap) : IOfficialBeatmapDifficultyClient
    {
        public List<int> RequestedBeatmapIds { get; } = [];

        public async Task<OfficialBeatmapDifficultyDownloadResult> DownloadDifficultyAsync(
            int beatmapId,
            string destinationDirectory,
            CancellationToken cancellationToken = default)
        {
            Assert.That(beatmapId, Is.EqualTo(expectedBeatmapId));
            RequestedBeatmapIds.Add(beatmapId);
            Directory.CreateDirectory(destinationDirectory);
            string path = Path.Combine(destinationDirectory, $"{beatmapId}.osu");
            await File.WriteAllTextAsync(path, beatmap, cancellationToken);
            return new OfficialBeatmapDifficultyDownloadResult(
                OfficialBeatmapRequestStatus.Success,
                beatmapId,
                path,
                new FileInfo(path).Length);
        }
    }

    private sealed class FailingDifficultyClient : IOfficialBeatmapDifficultyClient
    {
        public Task<OfficialBeatmapDifficultyDownloadResult> DownloadDifficultyAsync(
            int beatmapId,
            string destinationDirectory,
            CancellationToken cancellationToken = default) =>
            throw new AssertionException("A cached accuracy curve must not download its beatmap again.");
    }
}

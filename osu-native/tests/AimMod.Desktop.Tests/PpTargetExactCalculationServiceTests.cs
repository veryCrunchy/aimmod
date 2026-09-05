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
            Assert.That(PpTargetExactCalculationService.ExpectedScoreShape(0.99, 1500, 1000, 0.02), Is.EqualTo((20, 750)));
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

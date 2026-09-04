using AimMod.Desktop.PpTargets;
using AimMod.Osu.Runtime;
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
            () => SidecarRuntimeClient.Start(Path.Combine(AppContext.BaseDirectory, "AimMod.exe")));

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
            () => SidecarRuntimeClient.Start(Path.Combine(AppContext.BaseDirectory, "AimMod.exe")));

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
                () => SidecarRuntimeClient.Start(Path.Combine(AppContext.BaseDirectory, "AimMod.exe")));

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

using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.PpTargets;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class LocalScorePpHydrationServiceTests
{
    private string temporaryDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"aimmod-local-pp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(temporaryDirectory))
            Directory.Delete(temporaryDirectory, true);
    }

    [TestCase("taiko", 1)]
    [TestCase("fruits", 2)]
    [TestCase("mania", 3)]
    public void ModeAndFullJudgementsReachCalculator(string mode, int id) {
        var run = validRun(1) with { RulesetShortName = mode, Passed = false, LegacyScore = true,
            HitStatistics = new(1,2,3,4,0,1,Perfect:5,Good:6,LargeTickHit:7,SmallTickHit:8,SmallTickMiss:9) };
        var request = LocalScorePpHydrationService.CreateCalculationRequest(run, Path.Combine(temporaryDirectory,"map.osu"));
        Assert.That(request.RulesetId, Is.EqualTo(id));
        Assert.That(request.Passed, Is.False);
        Assert.That(request.LegacyScore, Is.True);
        Assert.That(request.Statistics, Is.EqualTo(run.HitStatistics));
    }

    [Test]
    public void StableCalculationUsesLegacyScoringAndLocalStaging()
    {
        string map = Path.Combine(temporaryDirectory, "map.osu");
        var stable = validRun(1) with { Origin = LocalLibraryOrigin.Stable, Mods = ["HD"] };
        var request = LocalScorePpHydrationService.CreateCalculationRequest(stable, map);
        Assert.That(request.LegacyScore, Is.True);
        Assert.That(request.LegacyTotalScore, Is.EqualTo(stable.TotalScore));
        Assert.That(LocalScorePpHydrationService.CreateCalculationRequest(stable with { Origin = LocalLibraryOrigin.Lazer }, map).LegacyTotalScore, Is.Null);
        Assert.That(request.BeatmapPath, Is.EqualTo(map));
        Assert.That(request.Mods, Is.EqualTo(new[] { "HD" }));
        Assert.That(LocalScorePpHydrationService.CreateCalculationRequest(stable with { Origin = LocalLibraryOrigin.Lazer }, map).LegacyScore, Is.False);
    }

    [Test]
    public async Task ChangedLegacyRawScoreInvalidatesHydratedPp()
    {
        string cachePath = Path.Combine(temporaryDirectory, "legacy-score.json");
        var stable = validRun(1) with { Origin = LocalLibraryOrigin.Stable };
        await new LocalScorePpHydrationService(temporaryDirectory, cachePath, (_, _) => Task.FromResult<double?>(123)).HydrateAsync([stable]);
        var result = await new LocalScorePpHydrationService(temporaryDirectory, cachePath, (_, _) => Task.FromResult<double?>(456))
            .HydrateAsync([stable with { TotalScore = stable.TotalScore + 1000 }]);
        Assert.That(result.CalculatedCount, Is.EqualTo(1));
        Assert.That(result.Runs.Single().PerformancePoints, Is.EqualTo(456));
    }

    [Test]
    public async Task StableAndLazerScoresDoNotReuseEachOthersPpCache()
    {
        string cachePath = Path.Combine(temporaryDirectory, "cache.json");
        var stable = validRun(1) with { Origin = LocalLibraryOrigin.Stable };
        await new LocalScorePpHydrationService(temporaryDirectory, cachePath, (_, _) => Task.FromResult<double?>(123)).HydrateAsync([stable]);
        var result = await new LocalScorePpHydrationService(temporaryDirectory, cachePath, (_, _) => Task.FromResult<double?>(456))
            .HydrateAsync([stable with { Origin = LocalLibraryOrigin.Lazer }]);
        Assert.That(result.CalculatedCount, Is.EqualTo(1));
        Assert.That(result.Runs.Single().PerformancePoints, Is.EqualTo(456));
    }

    [Test]
    public async Task PreservesStoredPpAndReportsIncompleteScoresWithoutEstimating()
    {
        var service = new LocalScorePpHydrationService(temporaryDirectory, Path.Combine(temporaryDirectory, "cache.json"));
        LocalReplay stored = run(1) with { PerformancePoints = 250 };
        LocalReplay incomplete = run(2);

        LocalScorePpHydrationResult result = await service.HydrateAsync([stored, incomplete]);

        Assert.Multiple(() =>
        {
            Assert.That(result.StoredCount, Is.EqualTo(1));
            Assert.That(result.CachedCount, Is.Zero);
            Assert.That(result.CalculatedCount, Is.Zero);
            Assert.That(result.UnavailableCount, Is.EqualTo(1));
            Assert.That(result.Runs.Single(item => item.ScoreId == incomplete.ScoreId).PerformancePoints, Is.Null);
        });
    }

    [Test]
    public async Task CalculatedPpIsPersistedAndUsedByANewServiceInstance()
    {
        string cachePath = Path.Combine(temporaryDirectory, "cache.json");
        LocalReplay replay = validRun(1);
        var service = new LocalScorePpHydrationService(
            temporaryDirectory,
            cachePath,
            (_, _) => Task.FromResult<double?>(321.5));

        LocalScorePpHydrationResult calculated = await service.HydrateAsync([replay]);
        int recalculations = 0;
        var reopened = new LocalScorePpHydrationService(
            temporaryDirectory,
            cachePath,
            (_, _) =>
            {
                recalculations++;
                return Task.FromResult<double?>(999);
            });
        LocalScorePpHydrationResult cached = await reopened.HydrateAsync([replay]);

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(cachePath), Is.True);
            Assert.That(calculated.CalculatedCount, Is.EqualTo(1));
            Assert.That(cached.CachedCount, Is.EqualTo(1));
            Assert.That(cached.Runs.Single().PerformancePoints, Is.EqualTo(321.5));
            Assert.That(recalculations, Is.Zero);
        });
    }

    [Test]
    public async Task CancellationCheckpointsAlreadyCalculatedScores()
    {
        string cachePath = Path.Combine(temporaryDirectory, "cache.json");
        using var cancellation = new CancellationTokenSource();
        LocalReplay first = validRun(1);
        LocalReplay second = validRun(2);
        int calls = 0;
        var service = new LocalScorePpHydrationService(
            temporaryDirectory,
            cachePath,
            (_, token) =>
            {
                calls++;
                if (calls == 1)
                    return Task.FromResult<double?>(250);
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult<double?>(null);
            });

        Assert.That(
            async () => await service.HydrateAsync([first, second], cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());

        int reopenedCalculations = 0;
        var reopened = new LocalScorePpHydrationService(
            temporaryDirectory,
            cachePath,
            (_, _) =>
            {
                reopenedCalculations++;
                return Task.FromResult<double?>(275);
            });
        LocalScorePpHydrationResult result = await reopened.HydrateAsync([first, second]);

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(cachePath), Is.True);
            Assert.That(result.CachedCount, Is.EqualTo(1));
            Assert.That(result.CalculatedCount, Is.EqualTo(1));
            Assert.That(reopenedCalculations, Is.EqualTo(1));
            Assert.That(result.Runs.Single(item => item.ScoreId == first.ScoreId).PerformancePoints, Is.EqualTo(250));
        });
    }

    [Test]
    [NonParallelizable]
    public async Task PersistenceFailureIsWrittenToStandardError()
    {
        TextWriter originalError = Console.Error;
        using var error = new StringWriter();
        Console.SetError(error);
        try
        {
            var service = new LocalScorePpHydrationService(
                temporaryDirectory,
                temporaryDirectory,
                (_, _) => Task.FromResult<double?>(100));

            await service.HydrateAsync([validRun(1)]);
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.That(error.ToString(), Does.Contain("local score PP cache persistence failed"));
    }

    private static LocalReplay run(int value) => new(
        new Guid(value, 0, 0, new byte[8]), Guid.NewGuid(), Guid.NewGuid(), "Song", "Artist", "Insane", "osu", "Player",
        DateTimeOffset.UtcNow, 5, 0.95, 1_000_000, 500, 1, null, [], true);

    private static LocalReplay validRun(int value) => run(value) with
    {
        Accuracy = 0.90 + value / 100d,
        BeatmapHash = value.ToString("x32"),
        HitStatistics = new PpScoreStatistics(900 - value, 50, 10, 1, 0, 0),
        ModsJson = "[]",
    };
}

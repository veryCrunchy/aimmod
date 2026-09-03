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

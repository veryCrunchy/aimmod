using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.PpTargets;
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

    private static LocalReplay run(int value) => new(
        new Guid(value, 0, 0, new byte[8]), Guid.NewGuid(), Guid.NewGuid(), "Song", "Artist", "Insane", "osu", "Player",
        DateTimeOffset.UtcNow, 5, 0.95, 1_000_000, 500, 1, null, [], true);
}

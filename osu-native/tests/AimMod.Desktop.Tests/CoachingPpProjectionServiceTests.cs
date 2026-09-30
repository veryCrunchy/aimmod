using System.Text.Json;
using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class CoachingPpProjectionServiceTests
{
    [Test]
    public void TargetComboRewardsMissRecoveryWithoutAssumingFullCombo()
    {
        CoachingPpProjectionRequest request = createRequest(currentCombo: 200, currentMisses: 8, targetMisses: 1, currentAccuracy: 0.91, targetAccuracy: 0.96);

        int target = CoachingPpProjectionService.EstimateTargetCombo(request, 1_000);

        Assert.That(target, Is.GreaterThan(200));
        Assert.That(target, Is.LessThan(1_000));
    }

    [Test]
    public void TargetComboStaysConservativeWithoutMissRecovery()
    {
        CoachingPpProjectionRequest request = createRequest(currentCombo: 400, currentMisses: 2, targetMisses: 2, currentAccuracy: 0.95, targetAccuracy: 0.96);

        int target = CoachingPpProjectionService.EstimateTargetCombo(request, 1_000);

        Assert.That(target, Is.InRange(500, 600));
    }

    [Test]
    public void TargetComboIsClampedToBeatmapMaximum()
    {
        CoachingPpProjectionRequest request = createRequest(currentCombo: 1_200, currentMisses: 4, targetMisses: 0, currentAccuracy: 0.92, targetAccuracy: 0.99);

        int target = CoachingPpProjectionService.EstimateTargetCombo(request, 1_000);

        Assert.That(target, Is.EqualTo(1_000));
    }

    [Test]
    public void ProfileGainReordersScoresUsingOsuWeighting()
    {
        Guid firstMap = Guid.NewGuid();
        Guid secondMap = Guid.NewGuid();
        LocalReplay[] history =
        {
            ppRun(firstMap, 100),
            ppRun(secondMap, 90),
        };

        double gain = CoachingPpWeighting.CalculateProfileGain(history, secondMap, 110);

        Assert.That(gain, Is.EqualTo(19.5).Within(0.0001));
    }

    [Test]
    public void ProfileGainUsesOnlyTheBestScorePerBeatmap()
    {
        Guid map = Guid.NewGuid();
        LocalReplay[] history =
        {
            ppRun(map, 120),
            ppRun(map, 80),
        };

        double gain = CoachingPpWeighting.CalculateProfileGain(history, map, 100);

        Assert.That(gain, Is.Zero);
    }

    [Test]
    public void TargetKeysIncludeEveryComboEstimateInputAndNeverCollideWithCurrentKeys()
    {
        CoachingPpProjectionRequest request = createRequest(currentCombo: 400, currentMisses: 2, targetMisses: 2, currentAccuracy: 0.95, targetAccuracy: 0.95);
        string key = CoachingPpProjectionService.TargetCacheKey(request);

        Assert.Multiple(() =>
        {
            Assert.That(CoachingPpProjectionService.TargetCacheKey(request with { Run = request.Run with { Accuracy = 0.93 } }), Is.Not.EqualTo(key));
            Assert.That(CoachingPpProjectionService.TargetCacheKey(request with { Run = request.Run with { MissCount = 5 } }), Is.Not.EqualTo(key));
            Assert.That(CoachingPpProjectionService.TargetCacheKey(request with { Run = request.Run with { MaxCombo = 300 } }), Is.Not.EqualTo(key));
            Assert.That(CoachingPpProjectionService.TargetCacheKey(request with { Opportunity = request.Opportunity with { TargetMissCount = 1 } }), Is.Not.EqualTo(key));
            Assert.That(CoachingPpProjectionService.TargetCacheKey(request with { Run = request.Run with { ScoreId = Guid.NewGuid() } }), Is.EqualTo(key));
            Assert.That(CoachingPpProjectionService.CurrentCacheKey(request.Run), Is.Not.EqualTo(key),
                "a target equal to the recorded play still uses an estimated combo");
            Assert.That(CoachingPpProjectionService.CurrentCacheKey(request.Run with { HitStatistics = new(100, 5, 1, 2, 10, 0) }),
                Is.Not.EqualTo(CoachingPpProjectionService.CurrentCacheKey(request.Run)));
        });
    }

    [Test]
    public async Task CacheWithRepeatedKeysLoadsTheNewestEntry()
    {
        string root = Path.Combine(Path.GetTempPath(), "aimmod-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CoachingPpProjectionRequest request = createRequest(currentCombo: 400, currentMisses: 2, targetMisses: 1, currentAccuracy: 0.95, targetAccuracy: 0.97);
            string key = CoachingPpProjectionService.TargetCacheKey(request);
            object entry(double pp, int minutes) => new
            {
                key,
                calculatedAt = new DateTimeOffset(2026, 1, 1, 0, minutes, 0, TimeSpan.Zero),
                result = new PpWhatIfResult(PpCalculationProtocol.EngineVersion, 1, 5.2, 900, 600, 580, 15, 3, 1, 0.97, pp, null, null, null, null, null, null),
            };
            string cachePath = Path.Combine(root, "projection.json");
            await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(
                new { version = 3, entries = new[] { entry(180, 5), entry(150, 1) } },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            var service = new CoachingPpProjectionService(root, cachePath);
            IReadOnlyDictionary<Guid, CoachingExactPpProjection> projections = await service.CalculateAsync([request, request]);

            Assert.That(projections, Has.Count.EqualTo(1));
            Assert.That(projections[request.Run.ScoreId].ProjectedPp, Is.EqualTo(180));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static CoachingPpProjectionRequest createRequest(
        int currentCombo,
        int currentMisses,
        int targetMisses,
        double currentAccuracy,
        double targetAccuracy)
    {
        var run = new LocalReplay(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Map",
            "Artist",
            "Difficulty",
            "osu",
            "Player",
            DateTimeOffset.UtcNow,
            5.2,
            currentAccuracy,
            500_000,
            currentCombo,
            currentMisses,
            120,
            Array.Empty<string>(),
            true,
            new string('a', 64));
        var opportunity = new CoachingPpOpportunity(
            1,
            run.BeatmapId,
            run.ScoreId,
            run.Title,
            run.Difficulty,
            run.StarRating,
            run.PerformancePoints!.Value,
            150,
            30,
            targetAccuracy,
            targetMisses,
            CoachingConfidence.Medium,
            4,
            10,
            "Target");
        return new CoachingPpProjectionRequest(run, opportunity);
    }

    private static LocalReplay ppRun(Guid beatmapId, double pp) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        beatmapId,
        "Map",
        "Artist",
        "Difficulty",
        "osu",
        "Player",
        DateTimeOffset.UtcNow,
        5,
        0.97,
        1_000_000,
        800,
        0,
        pp,
        Array.Empty<string>(),
        false);
}

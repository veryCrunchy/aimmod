using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
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

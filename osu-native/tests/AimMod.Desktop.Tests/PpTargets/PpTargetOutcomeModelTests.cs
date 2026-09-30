using AimMod.Desktop.PpTargets;
using NUnit.Framework;

namespace AimMod.Desktop.Tests.PpTargets;

[TestFixture]
public sealed class PpTargetOutcomeModelTests
{
    private static readonly DateTimeOffset now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly PpOutcomeTarget target = new(5, 180, 240, 8, 9.3, 800, 1_100, "", false, "online:999");
    private static readonly PpOutcomePrior prior = new(1, .95);

    private static PpOutcomeObservation play(int map, double stars, bool passed, int misses, double accuracy = .96, bool legacy = false,
        PpOutcomeSource source = PpOutcomeSource.Local, int days = 1, double? pp = null) =>
        new(Guid.NewGuid(), $"online:{map}", map, null, now.AddDays(-days), stars, 180, 240, 8, 9.3, [], "", "", legacy, passed, source,
            accuracy, misses, passed ? 600 : 100, passed ? 800 : 300, 1_100, pp);

    private static PpOutcomeProfile profile(params PpOutcomeObservation[] observations) => new("synthetic", now, observations);

    [Test]
    public void FailedAttemptsDoNotEnterThePassedScoreDistribution()
    {
        var passes = Enumerable.Range(1, 8).Select(i => play(i, 5, true, i % 2)).ToArray();
        var fails = Enumerable.Range(1, 8).Select(i => play(i, 5, false, 15)).ToArray();
        var withFails = PpTargetOutcomeModel.Fit(profile([.. passes, .. fails]), target, prior);
        var passesOnly = PpTargetOutcomeModel.Fit(profile(passes), target, prior);
        Assert.That(withFails.MissMean, Is.EqualTo(passesOnly.MissMean));
        Assert.That(withFails.MissMean, Is.LessThan(1));
        Assert.That(withFails.Passes, Is.EqualTo(8));
    }

    [Test]
    public void HarderNeighboursAreAdjustedDownToTheCandidateDifficulty()
    {
        var harder = Enumerable.Range(1, 10).Select(i => play(i, 5.8, true, 3, .95)).ToArray();
        var fitted = PpTargetOutcomeModel.Fit(profile(harder), target, prior);
        var same = PpTargetOutcomeModel.Fit(profile(harder.Select(p => p with { Stars = 5 }).ToArray()), target, prior);
        Assert.That(fitted.MissMean, Is.LessThan(same.MissMean));
        Assert.That(fitted.HitAccuracy, Is.GreaterThan(same.HitAccuracy));
    }

    [Test]
    public void ThinEvidenceFallsBackToTheOtherScoringModeAndBestScores()
    {
        var stable = Enumerable.Range(1, 6).Select(i => play(i, 5, true, 1, legacy: true)).ToArray();
        var crossed = PpTargetOutcomeModel.Fit(profile(stable), target, prior);
        Assert.That(crossed.CrossMode, Is.True);
        Assert.That(crossed.Passes, Is.EqualTo(6));

        var best = Enumerable.Range(1, 3).Select(i => play(i, 5, true, 0, source: PpOutcomeSource.OnlineBest)).ToArray();
        Assert.That(PpTargetOutcomeModel.Fit(profile(best), target, prior).UsesBestScores, Is.True);
        var plenty = Enumerable.Range(1, 12).Select(i => play(i, 5, true, 1)).ToArray();
        var rich = PpTargetOutcomeModel.Fit(profile([.. plenty, .. best, .. stable]), target, prior);
        Assert.That(rich.UsesBestScores, Is.False, "Selected best scores only fill a thin history.");
        Assert.That(rich.CrossMode, Is.False);
    }

    [Test]
    public void IntegrationAveragesPpOverMissesInsteadOfPluggingInTheAverage()
    {
        var distribution = new PpOutcomeDistribution(2, PpOutcomeDistribution.PoissonShape, .97, .0001, 1, 1, 10, 5, 10, false, false, .9, -.012);
        int[] misses = [.. PpTargetOutcomeModel.ScenarioMisses(distribution, 800)];
        var scenarios = misses.Select(m => new PpScenario(m, .97, 1_000, 100 * Math.Pow(.8, m))).ToArray();
        var atoms = PpTargetOutcomeModel.Integrate(distribution, scenarios, 800, 0, 1_000);
        double expected = 100 * Math.Exp(-2 * (1 - .8));
        Assert.That(misses, Does.Contain(0).And.Contain(8));
        Assert.That(PpTargetOutcomeModel.Mean(atoms), Is.EqualTo(expected).Within(1));
        Assert.That(PpTargetOutcomeModel.Mean(atoms), Is.GreaterThan(100 * Math.Pow(.8, 2)), "Convexity: E[PP(m)] exceeds PP(E[m]).");
    }

    [Test]
    public void TargetIsTheBestOfSeveralTriesReachedWithSixtyPercentChance()
    {
        PpOutcomeAtom[] atoms = [.. Enumerable.Range(1, 10).Select(i => new PpOutcomeAtom(i * 10, .1))];
        var single = PpTargetOutcomeModel.BestOf(atoms, 1, 1, .6)!.Value;
        Assert.That(single.Pp, Is.EqualTo(50));
        Assert.That(single.Probability, Is.EqualTo(.6).Within(1e-9));
        var three = PpTargetOutcomeModel.BestOf(atoms, 1, 3, .6)!.Value;
        Assert.That(three.Pp, Is.GreaterThan(single.Pp));
        Assert.That(PpTargetOutcomeModel.BestOf(atoms, .1, 3, .6), Is.Null, "Three tries rarely pass at 10%.");
        Assert.That(PpTargetForecastModel.Tries(.95), Is.EqualTo(3));
        Assert.That(PpTargetForecastModel.Tries(.3), Is.EqualTo(5));
    }

    [Test]
    public void CalibrationShrinksTowardsOneAndIgnoresOtherSetups()
    {
        var samples = Enumerable.Range(0, 10).Select(i => new PpCalibrationSample("", false, 5, now.AddDays(-1), .9)).ToArray();
        var (factor, count) = PpTargetOutcomeModel.Calibration(samples, "", false, 5, now);
        Assert.That(count, Is.EqualTo(10));
        Assert.That(factor, Is.LessThan(1).And.GreaterThan(.9));
        Assert.That(PpTargetOutcomeModel.Calibration(samples, "HD", false, 5, now), Is.EqualTo((1d, 0)));
    }

    [Test]
    public void ProfileIdentityTracksEveryObservationButNotTheClockWithinADay()
    {
        var observation = play(1, 5, true, 1);
        var a = PpTargetOutcomeModel.Before(profile(observation), now);
        var b = PpTargetOutcomeModel.Before(profile(observation), now.AddHours(-1));
        var changed = PpTargetOutcomeModel.Before(profile(observation with { Misses = 2 }), now);
        Assert.That(b.Identity, Is.EqualTo(a.Identity));
        Assert.That(changed.Identity, Is.Not.EqualTo(a.Identity));
        Assert.That(PpTargetOutcomeModel.Before(profile(observation), now.AddDays(-1)).Observations, Is.Empty, "Strictly earlier plays only.");
    }
}

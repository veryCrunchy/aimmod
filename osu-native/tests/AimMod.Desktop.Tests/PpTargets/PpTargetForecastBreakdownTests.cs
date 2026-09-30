using AimMod.Desktop.PpTargets;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests.PpTargets;

[TestFixture]
public sealed class PpTargetForecastBreakdownTests
{
    private const int objects = 800;

    private static PpOutcomeDistribution poisson(double misses, double hit = .97, double spread = .0001) =>
        new(misses, PpOutcomeDistribution.PoissonShape, hit, spread, 1, 1, 10, 5, 10, false, false, .9, -.012);

    private static PpTargetEstimate estimate(PpOutcomeDistribution distribution, double factor = 1, PpPatternPrediction? prediction = null,
        int objectCount = objects, int maximumCombo = 1_000)
    {
        int[] misses = [.. PpTargetOutcomeModel.ScenarioMisses(distribution, objects)];
        var scenarios = misses.Select(m => new PpScenario(m, distribution.ScenarioAccuracy(m, objects), distribution.ScenarioCombo(m, objects, 1_000),
            100 * Math.Pow(.8, m))).ToArray();
        var atoms = PpTargetOutcomeModel.Integrate(distribution, scenarios, objects, 0, 150, factor);
        return new PpTargetEstimate(PpTargetOutcomeModel.Mean(atoms), 150, new(80, 120), 10, PpTargetConfidence.High, "synthetic",
            PatternPrediction: prediction,
            Outcome: new PpOutcomeEstimate(distribution, scenarios, atoms, 0, factor, 3, objectCount, maximumCombo));
    }

    [Test]
    public void MissesFollowTheFittedDistributionWithAFullComboChance()
    {
        var breakdown = PpTargetForecastBreakdownModel.Create(estimate(poisson(2)))!;
        var misses = breakdown.Misses;
        Assert.Multiple(() =>
        {
            Assert.That(misses.FullComboChance, Is.EqualTo(Math.Exp(-2)).Within(1e-3));
            Assert.That(misses.Buckets, Has.Count.EqualTo(PpForecastMisses.BucketCount));
            Assert.That(misses.Buckets.Sum(), Is.EqualTo(1).Within(1e-9));
            Assert.That(misses.Buckets[2], Is.EqualTo(2 * Math.Exp(-2)).Within(1e-3));
            Assert.That(misses.Buckets[5], Is.EqualTo(1 - Enumerable.Range(0, 5).Sum(m => Math.Exp(-2) * Math.Pow(2, m) / factorial(m))).Within(1e-3));
            Assert.That(misses.MostLikely, Is.EqualTo(1), "Poisson(2) ties 1 and 2; the lower count is reported.");
            Assert.That(misses.Median, Is.EqualTo(2));
            Assert.That((misses.Low, misses.High), Is.EqualTo((1, 3)));
            Assert.That(misses.Expected, Is.EqualTo(2).Within(.01));
        });
    }

    [Test]
    public void ScoreAccuracyCombinesHitAccuracyWithMisses()
    {
        var clean = PpTargetForecastBreakdownModel.Create(estimate(poisson(1e-9, .97, .004)))!.Accuracy;
        Assert.That(clean.Median, Is.EqualTo(.97).Within(1e-4));
        Assert.That(clean.Low, Is.EqualTo(.97 - .6745 * .004).Within(2e-4), "The quartiles of a normal hit accuracy.");
        Assert.That(clean.High, Is.EqualTo(.97 + .6745 * .004).Within(2e-4));

        var missed = PpTargetForecastBreakdownModel.Create(estimate(poisson(4, .97, .004)))!.Accuracy;
        Assert.Multiple(() =>
        {
            Assert.That(missed.Expected, Is.EqualTo(.97 * (1 - 4d / objects)).Within(1e-4));
            Assert.That(missed.Low, Is.LessThan(missed.Median));
            Assert.That(missed.Median, Is.LessThan(missed.High));
            Assert.That(missed.Median, Is.LessThan(clean.Median), "Misses lower score accuracy.");
        });
    }

    [Test]
    public void TypicalStatsChainToPpThroughCalibrationAndTheRetryTrend()
    {
        var breakdown = PpTargetForecastBreakdownModel.Create(estimate(poisson(2), factor: .9), learningAdjustment: 1.1)!;
        Assert.Multiple(() =>
        {
            Assert.That(breakdown.TypicalMisses, Is.EqualTo(2));
            Assert.That(breakdown.TypicalAccuracy, Is.EqualTo(.97 * (1 - 2d / objects)).Within(1e-9));
            Assert.That(breakdown.CalculatorPpAtTypicalStats, Is.EqualTo(64).Within(1e-9));
            Assert.That(breakdown.PpAtTypicalStats, Is.EqualTo(64 * .9 * 1.1).Within(1e-9));
            Assert.That(breakdown.TypicalCombo, Is.LessThan(breakdown.Combo.Maximum));
            Assert.That(breakdown.Combo.Maximum, Is.EqualTo(1_000));
            Assert.That(breakdown.Combo.ExpectedFraction, Is.InRange(.1, 1));
        });
    }

    [Test]
    public void PpPercentilesAreOrderedCappedAndMatchTheForecast()
    {
        var target = estimate(poisson(2, .97, .01));
        var pass = new PpTargetPassEstimate(.8, .7, .9, 20, 5, Confidence: PpTargetConfidence.High);
        var forecast = PpTargetForecastModel.Forecast(target, pass, null)!;
        var pp = forecast.Breakdown!.Pp;
        Assert.Multiple(() =>
        {
            Assert.That(new[] { pp.P10, pp.P25, pp.Median, pp.P75, pp.P90 }, Is.Ordered);
            Assert.That(pp.P90, Is.LessThanOrEqualTo(pp.FullComboCeiling));
            Assert.That(pp.FullComboCeiling, Is.EqualTo(150));
            Assert.That(pp.Mean, Is.EqualTo(forecast.PpIfPass).Within(1e-9));
            Assert.That(pp.P10, Is.LessThan(pp.P90));
        });
    }

    [Test]
    public void TimingIsMeasuredInRealTimeAndExcludesMisses()
    {
        ReplayObjectJudgement judgement(int i, string result, double offset, double? rate) =>
            new(i, null, "HitCircle", i * 100, i * 100, result, "Great", i * 100 + offset, offset, rate, new(0, 0), new(0, 0), 0, 0);
        // Double time: 15 gameplay ms early is 10 real ms early.
        var hits = Enumerable.Range(0, 20).Select(i => judgement(i, "Great", i % 2 == 0 ? -12 : -18, 1.5))
            .Append(judgement(20, "Miss", 180, 1.5)).ToArray();
        var (mean, deviation) = PpTargetPatternModel.HitError(hits, null);
        Assert.That(mean, Is.EqualTo(-10).Within(1e-9));
        Assert.That(deviation, Is.EqualTo(2 * Math.Sqrt(20d / 19)).Within(1e-9), "Sample deviation of +/-3 gameplay ms at 1.5x.");
        var fallback = hits.Select(j => j with { GameplayRate = null }).ToArray();
        Assert.That(PpTargetPatternModel.HitError(fallback, 1.5).Mean, Is.EqualTo(-10).Within(1e-9), "The clock rate applies without per-judgement rates.");
        Assert.That(PpTargetPatternModel.HitError(hits.Take(3), null), Is.EqualTo(((double?)null, (double?)null)), "Too few hits to measure timing.");
    }

    [Test]
    public void PooledTimingKeepsPlayBiasOutOfTheUnstableRate()
    {
        var early = new PpPatternOutcome(100, .97, 0, new Dictionary<ReplayMissReason, int>(), -10, 8);
        var late = early with { HitErrorMeanMs = 10 };
        var untimed = early with { HitErrorMeanMs = null, HitErrorDeviationMs = null };
        var pooled = PpTargetPatternModel.PoolTiming([(1, early), (1, late), (5, untimed)])!.Value;
        Assert.That(pooled.Mean, Is.EqualTo(0).Within(1e-9));
        Assert.That(pooled.UnstableRate, Is.EqualTo(80).Within(1e-9));
        Assert.That(pooled.Plays, Is.EqualTo(2));
        Assert.That(PpTargetPatternModel.PoolTiming([(3, early), (1, late)])!.Value.Mean, Is.EqualTo(-5).Within(1e-9));
        Assert.That(PpTargetPatternModel.PoolTiming([(1, untimed)]), Is.Null);
        Assert.That(early.UnstableRate, Is.EqualTo(80));
    }

    [Test]
    public void BreakdownCarriesThePredictedTimingOfTheOverallPattern()
    {
        var prediction = new PpPatternPrediction(.8, .97, .5, [], [], [
            new("Overall", .8, .97, .5, 6, .01, -6.5, 112, 14), new("Jumps", .7, .96, .4, 4, .02, -2, 140, 8)]);
        var timing = PpTargetForecastBreakdownModel.Create(estimate(poisson(1), prediction: prediction))!.Timing!;
        Assert.That((timing.MeanOffsetMs, timing.UnstableRate, timing.Plays, timing.Maps), Is.EqualTo((-6.5, 112d, 14, 6)));
        Assert.That(PpTargetForecastBreakdownModel.Create(estimate(poisson(1)))!.Timing, Is.Null, "Timing is absent without replay offsets.");
    }

    [Test]
    public void OlderEstimatesInferTheObjectCountFromAMissedScenario()
    {
        var old = estimate(poisson(2), objectCount: 0, maximumCombo: 0);
        Assert.That(PpTargetForecastBreakdownModel.ObjectCount(old.Outcome!), Is.EqualTo(objects).Within(1));
        Assert.That(PpTargetForecastBreakdownModel.MaximumCombo(old.Outcome!, objects), Is.EqualTo(1_000));
    }

    private static double factorial(int n) => n <= 1 ? 1 : n * factorial(n - 1);
}

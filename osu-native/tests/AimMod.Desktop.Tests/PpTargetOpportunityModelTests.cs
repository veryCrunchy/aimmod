using AimMod.Desktop.PpTargets;
using AimMod.Desktop.ScoreHistory;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public class PpTargetOpportunityModelTests
{
    private static readonly DateTimeOffset reference = new(2026, 1, 30, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public void LongerMapsUseConservativeDurationAdjustmentWhenSimilarOutcomesExist()
    {
        var profile = PpTargetOpportunityModel.Build(Enumerable.Range(1, 12).Select(i => entry(i, i <= 9)), reference);
        var shortMap = PpTargetOpportunityModel.EstimatePass(profile, 5, 180, 120, [])!;
        var longMap = PpTargetOpportunityModel.EstimatePass(profile, 5, 180, 363, []);
        Assert.That(longMap, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(longMap!.DurationAdjusted, Is.True);
            Assert.That(longMap.Confidence, Is.EqualTo(PpTargetConfidence.Low));
            Assert.That(longMap.Probability, Is.LessThan(shortMap.Probability));
            Assert.That(longMap.Lower, Is.LessThan(longMap.Probability));
            Assert.That(longMap.Upper, Is.GreaterThan(longMap.Probability));
            Assert.That(longMap.Upper - longMap.Lower, Is.GreaterThan(shortMap.Upper - shortMap.Lower));
        });
        Assert.That(PpTargetOpportunityModel.EstimatePass(profile, 5, 180, 600, []), Is.Null);
        Assert.That(PpTargetOpportunityModel.EstimatePass(profile, 5, 280, 363, []), Is.Null);
        Assert.That(PpTargetOpportunityModel.EstimatePass(profile, 5, 180, 363, ["HR"]), Is.Null);
    }

    [Test]
    public void DurationFallbackDoesNotTreatRetriesOfOneMapAsBroadEvidence()
    {
        var profile = PpTargetOpportunityModel.Build(Enumerable.Range(1, 20)
            .Select(i => entry(i, true) with { OnlineBeatmapId = i % 3 + 1 }), reference);
        Assert.That(PpTargetOpportunityModel.EstimatePass(profile, 5, 180, 363, []), Is.Null);
    }

    [Test]
    public void BroaderComparisonUsesObservedOutcomesAndReportsLowConfidence()
    {
        var scores = Enumerable.Range(1, 10).Select(i => entry(i, i <= 6) with { StarRating = 5.8, Bpm = 200, LengthSeconds = 160 });
        var profile = PpTargetOpportunityModel.Build(scores, reference);
        var estimate = PpTargetOpportunityModel.EstimatePass(profile, 5, 180, 120, []);
        Assert.That(estimate, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(estimate!.BroaderComparison, Is.True);
            Assert.That(estimate.Confidence, Is.EqualTo(PpTargetConfidence.Low));
            Assert.That(estimate.Lower, Is.LessThan(estimate.Probability));
            Assert.That(estimate.Upper, Is.GreaterThan(estimate.Probability));
            Assert.That(estimate.Probability, Is.InRange(.4, .7));
        });
    }

    [Test]
    public void MissingMetadataDiscountsSupportInsteadOfDiscardingEveryOutcome()
    {
        var scores = Enumerable.Range(1, 10).Select(i => entry(i, i <= 6));
        var full = PpTargetOpportunityModel.EstimatePass(PpTargetOpportunityModel.Build(scores, reference), 5, 180, 120, [])!;
        var sparse = PpTargetOpportunityModel.EstimatePass(PpTargetOpportunityModel.Build(scores.Select(s => s with { Bpm = null, LengthSeconds = null }), reference), 5, 180, 120, [])!;
        Assert.That(sparse, Is.Not.Null);
        Assert.That(sparse.BroaderComparison, Is.True);
        Assert.That(sparse.Upper - sparse.Lower, Is.GreaterThan(full.Upper - full.Lower));
    }

    [Test]
    public void ExplicitLocalOutcomesAreIncludedWithoutInventingUnknownPasses()
    {
        var local = Enumerable.Range(1, 8).Select(i => entry(i, i <= 4, ScoreHistoryProvenance.Local) with
        {
            OnlineScoreId = 0, OnlineBeatmapId = 0, LocalBeatmapId = new Guid(i, 0, 0, new byte[8]),
        }).ToArray();
        var profile = PpTargetOpportunityModel.Build(local, reference);
        Assert.That(profile.RecentAttempts, Has.Count.EqualTo(8));
        Assert.That(PpTargetOpportunityModel.EstimatePass(profile, 5, 180, 120, [])!.Maps, Is.EqualTo(8));
        Assert.That(PpTargetOpportunityModel.Build(local.Select(s => s with { Passed = null }), reference).RecentAttempts, Is.Empty);
    }

    [Test]
    public void AccountGainReweightsDisplacedPlaysAndReplacesSameDifficulty()
    {
        var profile = new PpTargetOpportunityProfile(reference, [new(1, 100), new(2, 80)], []);
        Assert.That(PpTargetOpportunityModel.AccountGain(profile, 1, 100), Is.Zero);
        Assert.That(PpTargetOpportunityModel.AccountGain(profile, 1, 120), Is.EqualTo(20).Within(0.0001));
        Assert.That(PpTargetOpportunityModel.AccountGain(profile, 3, 120), Is.EqualTo(120 + 100 * .95 + 80 * .95 * .95 - 176).Within(0.0001));
        Assert.That(PpTargetOpportunityModel.AccountGain(null, 1, 120), Is.Null);
        Assert.That(PpTargetOpportunityModel.AccountGain(profile, 1, double.NaN), Is.Null);
    }

    [Test]
    public void BestOnlySuccessesAndUnknownOutcomesCannotTrainPassProbability()
    {
        var scores = Enumerable.Range(1, 8).Select(i => entry(i, true, ScoreHistoryProvenance.OnlineBest));
        var bestOnly = PpTargetOpportunityModel.Build(scores, reference);
        Assert.That(bestOnly.BestPlays, Has.Count.EqualTo(8));
        Assert.That(bestOnly.RecentAttempts, Is.Empty);
        Assert.That(PpTargetOpportunityModel.Build([entry(1, null)], reference).RecentAttempts, Is.Empty);
    }

    [Test]
    public void RecentFailuresLowerEstimateAndOldOrFutureScoresDoNotTrainIt()
    {
        var profile = PpTargetOpportunityModel.Build(Enumerable.Range(1, 8).Select(i => entry(i, true)), reference);
        var good = PpTargetOpportunityModel.EstimatePass(profile, 5, 180, 120, [])!;
        var mixed = PpTargetOpportunityModel.Build(Enumerable.Range(1, 8).Select(i => entry(i, i <= 4)), reference);
        var lower = PpTargetOpportunityModel.EstimatePass(mixed, 5, 180, 120, [])!;
        Assert.That(good.Probability, Is.GreaterThan(lower.Probability));
        Assert.That(good.Upper, Is.LessThan(1));
        Assert.That(good.Lower, Is.LessThan(good.Probability));
        Assert.That(good.Maps, Is.EqualTo(8));
        var stale = PpTargetOpportunityModel.Build([entry(1, false) with { PlayedAt = reference.AddDays(-31) },
            entry(2, true) with { PlayedAt = reference.AddDays(1) }], reference);
        Assert.That(stale.RecentAttempts, Is.Empty);
    }

    [Test]
    public void RepeatedSingleMapSparseCoverageAndIncompatibleModsStayUnknown()
    {
        var repeats = PpTargetOpportunityModel.Build(Enumerable.Range(1, 100).Select(i => entry(i, true) with { OnlineBeatmapId = 1 }), reference);
        Assert.That(PpTargetOpportunityModel.EstimatePass(repeats, 5, 180, 120, []), Is.Null);
        var profile = PpTargetOpportunityModel.Build(Enumerable.Range(1, 8).Select(i => entry(i, true)), reference);
        Assert.That(PpTargetOpportunityModel.EstimatePass(profile, 5, 180, 120, ["NF"]), Is.Null);
        Assert.That(PpTargetOpportunityModel.EstimatePass(profile, 5, 180, 120, ["HR"]), Is.Null);
        Assert.That(PpTargetOpportunityModel.EstimatePass(profile, 7, 180, 120, []), Is.Null);
        Assert.That(PpTargetOpportunityModel.EstimatePass(profile, 5, 280, 500, []), Is.Null);
    }

    private static ScoreHistoryEntry entry(int id, bool? passed,
        ScoreHistoryProvenance provenance = ScoreHistoryProvenance.OnlineRecent) => new(
            $"synthetic:{id}", id, id, 1, null, null, "Map", "Artist", "Difficulty", reference.AddDays(-1),
            5, .95, 100, 100000, 100, 1, [], provenance, false, passed, 180, 120);
}

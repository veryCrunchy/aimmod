using AimMod.Desktop.PpTargets;
using AimMod.Desktop.ScoreHistory;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public class PpTargetOpportunityModelTests
{
    private static readonly DateTimeOffset reference = new(2026, 1, 30, 12, 0, 0, TimeSpan.Zero);

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

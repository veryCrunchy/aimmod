using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.PpTargets;
using NUnit.Framework;

namespace AimMod.Desktop.Tests.PpTargets;

[TestFixture]
public sealed class PpTargetScoringModeTests
{
    private static readonly DateTimeOffset now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static LocalReplay run(int i, bool stable, DateTimeOffset at) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Synthetic", "Artist", "Diff", "osu",
        "Synthetic player", at, 5, .97, 1_000_000, 500, 1, 120, ["HD"], true, $"{i:x32}", Origin: stable ? LocalLibraryOrigin.Stable : LocalLibraryOrigin.Lazer, LegacyScore: stable);

    [Test]
    public void AutomaticModeFollowsRecentLazerPlaysWhenOldestPlaysAreStable()
    {
        var oldStable = Enumerable.Range(0, 40).Select(i => run(i, true, now.AddDays(-200 + i))).ToArray();
        var recentLazer = Enumerable.Range(0, 10).Select(i => run(100 + i, false, now.AddDays(-i))).ToArray();
        // Oldest first, as the score history arrives.
        var history = oldStable.Concat(recentLazer).OrderBy(r => r.PlayedAt).ToArray();
        var profile = PpTargetPreferenceProfile.Empty with { PreferredModSetup = ["HD"], LegacyScore = true };

        var resolved = NativePpTargetsWorkspace.ResolveModProfile(profile, "Automatic", history, null);
        Assert.Multiple(() =>
        {
            Assert.That(resolved.LegacyScore, Is.False, "The oldest play must not decide the scoring system.");
            Assert.That(resolved.PreferredModSetup, Is.EqualTo(new[] { "HD" }));
            Assert.That(resolved.OtherScoringModeRuns, Is.Zero);
            Assert.That(PpTargetPreferenceProfiler.Build(history).LegacyScore, Is.False, "Recent plays outweigh an old stable majority.");
        });
    }

    [Test]
    public void RecentPlaysInTheOtherClientAreSurfacedAndStillPredictPasses()
    {
        var lazer = Enumerable.Range(0, 10).Select(i => run(i, false, now.AddDays(-i))).ToArray();
        var stable = Enumerable.Range(0, 3).Select(i => run(100 + i, true, now.AddDays(-2 - i))).ToArray();
        var profile = PpTargetPreferenceProfile.Empty with { PreferredModSetup = ["HD"] };
        var resolved = NativePpTargetsWorkspace.ResolveModProfile(profile, "Automatic", lazer.Concat(stable).ToArray(), null);
        Assert.That(resolved.LegacyScore, Is.False);
        Assert.That(resolved.OtherScoringModeRuns, Is.EqualTo(3), "Recent plays in the other client are surfaced rather than silently ignored.");

        // Only stable pass/fail history: a lazer target still gets a (low-confidence) pass chance.
        var attempts = Enumerable.Range(0, 12).Select(i => new PpTargetPassSample(i % 4 + 1, now.AddDays(-i), 5, 180, 120, "HD", i % 3 != 0,
            Accuracy: .96, LegacyScore: true)).ToArray();
        var opportunities = new PpTargetOpportunityProfile(now, [], attempts);
        var pass = PpTargetOpportunityModel.EstimatePass(opportunities, 5, 180, 120, ["HD"], legacyScore: false);
        Assert.That(pass, Is.Not.Null);
        Assert.That(pass!.CrossMode, Is.True);
        Assert.That(pass.Confidence, Is.EqualTo(PpTargetConfidence.Low));
        Assert.That(PpTargetOpportunityModel.EstimatePass(opportunities, 5, 180, 120, ["HD"], legacyScore: true)!.CrossMode, Is.False);
    }
}

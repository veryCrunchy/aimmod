using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.PpTargets;
using AimMod.Osu.Runtime;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class PpScoreHistoryMergerTests
{
    [Test]
    public void SubmittedScoreReplacesCalculatedPpWithoutBeingDuplicated()
    {
        LocalReplay local = localRun() with { PerformancePoints = 200, OnlineScoreId = 99 };
        OsuBestScore online = onlineScore(99, 321);

        IReadOnlyList<LocalReplay> merged = PpScoreHistoryMerger.Merge([local], [online], []);

        Assert.Multiple(() =>
        {
            Assert.That(merged, Has.Count.EqualTo(1));
            Assert.That(merged[0].ScoreId, Is.EqualTo(local.ScoreId));
            Assert.That(merged[0].PerformancePoints, Is.EqualTo(321));
        });
    }

    [Test]
    public void MissingApiPpDoesNotEraseAnExactLocalCalculation()
    {
        LocalReplay local = localRun() with { PerformancePoints = 200, OnlineScoreId = 99 };

        LocalReplay merged = PpScoreHistoryMerger.Merge([local], [onlineScore(99, null)], []).Single();

        Assert.That(merged.PerformancePoints, Is.EqualTo(200));
    }

    [Test]
    public void AddsOnlineBestScoreUsingMatchingLocalBeatmapIdentity()
    {
        Guid setId = Guid.NewGuid();
        Guid beatmapId = Guid.NewGuid();
        var localSet = new LocalBeatmapSet(
            setId, 567, "Local", "Artist", "Mapper", "", DateTimeOffset.UtcNow, null,
            [new LocalBeatmapDifficulty(beatmapId, 1234, "Insane", "osu", 6.4, 180, 120_000, 4, 9, 8, 6, 0)], 0);

        LocalReplay merged = PpScoreHistoryMerger.Merge([], [onlineScore(99, 321)], [localSet]).Single();

        Assert.Multiple(() =>
        {
            Assert.That(merged.SetId, Is.EqualTo(setId));
            Assert.That(merged.BeatmapId, Is.EqualTo(beatmapId));
            Assert.That(merged.PerformancePoints, Is.EqualTo(321));
            Assert.That(merged.OnlineScoreId, Is.EqualTo(99));
            Assert.That(merged.IsLocallyStored, Is.False);
            Assert.That(merged.HitStatistics?.Great, Is.EqualTo(600));
            Assert.That(merged.ModsJson, Does.Contain("HD"));
        });
    }

    private static LocalReplay localRun() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Song", "Artist", "Insane", "osu", "Player",
        DateTimeOffset.UtcNow, 6.4, 0.98, 1_000_000, 700, 2, null, ["HD"], true);

    private static OsuBestScore onlineScore(long scoreId, double? pp) => new(
        scoreId, 42, "Player", pp, 0.98, 1_000_000, 700,
        new OsuScoreStatistics(2, 600, 20, 3), ["HD"], "[{\"acronym\":\"HD\"}]",
        new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), null,
        new OsuScoreBeatmap(1234, "abcdef", "Insane", 6.4, 900, 180, 120),
        new OsuScoreBeatmapSet(567, "Song", null, "Artist", null, "Mapper", "", "ranked", null));
}

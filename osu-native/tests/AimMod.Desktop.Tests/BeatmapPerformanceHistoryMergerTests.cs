using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.ScoreHistory;
using AimMod.Osu.Runtime;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class BeatmapPerformanceHistoryMergerTests
{
    [Test]
    public void MergesMatchingSubmittedScoreWithoutDuplicatingLocalPlay()
    {
        LocalReplay local = localScore(onlineScoreId: 44);
        OsuUserBeatmapScore submitted = onlineScore(44, 0.99, 222);

        IReadOnlyList<ScoreHistoryEntry> result = ScoreHistoryMerger.Merge([local], [onlineEntry(submitted)]);

        Assert.Multiple(() =>
        {
            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].IsLocal, Is.True);
            Assert.That(result[0].IsSubmitted, Is.True);
            Assert.That(result[0].Accuracy, Is.EqualTo(0.99));
            Assert.That(result[0].PerformancePoints, Is.EqualTo(222));
        });
    }

    [Test]
    public void KeepsAllValidSubmittedModVariantsForExactDifficulty()
    {
        IReadOnlyList<ScoreHistoryEntry> result = ScoreHistoryMerger.Merge(
            [],
            [onlineEntry(onlineScore(44, 0.95, 120)), onlineEntry(onlineScore(45, 0.98, 180)), onlineEntry(onlineScore(46, 1, 230))]);

        Assert.Multiple(() =>
        {
            Assert.That(result, Has.Count.EqualTo(3));
            Assert.That(result.All(play => !play.IsLocal && play.IsSubmitted), Is.True);
            Assert.That(result.Select(play => play.PerformancePoints), Is.EqualTo(new double?[] { 120, 180, 230 }));
        });
    }

    [Test]
    public void AccountHistoryCombinesBestAndRecentProvenanceByOnlineScoreId()
    {
        OsuBestScore shared = accountScore(80);
        IReadOnlyList<ScoreHistoryEntry> result = ScoreHistoryMerger.MergeOnline(
            [shared, accountScore(81)],
            [shared, accountScore(82)]);

        ScoreHistoryEntry merged = result.Single(score => score.OnlineScoreId == 80);
        Assert.Multiple(() =>
        {
            Assert.That(result, Has.Count.EqualTo(3));
            Assert.That(merged.Provenance.HasFlag(ScoreHistoryProvenance.OnlineBest), Is.True);
            Assert.That(merged.Provenance.HasFlag(ScoreHistoryProvenance.OnlineRecent), Is.True);
            Assert.That(merged.OnlineBeatmapId, Is.EqualTo(1234));
            Assert.That(merged.StarRating, Is.EqualTo(5.5));
            Assert.That(ScoreHistoryMerger.MergeAsLocalReplays([], result).Single(score => score.OnlineScoreId == 80).StarRating,
                Is.EqualTo(5.5));
        });
    }

    [Test]
    public void LocalStorageProvenanceSurvivesSubmissionMerge()
    {
        LocalReplay local = localScore(44);
        LocalReplay mergedLocal = ScoreHistoryMerger.MergeAsLocalReplays(
            [local],
            [onlineEntry(onlineScore(44, 0.99, 222))]).Single();
        LocalReplay onlineOnly = ScoreHistoryMerger.MergeAsLocalReplays(
            [],
            [onlineEntry(onlineScore(45, 0.98, 180))]).Single();

        Assert.Multiple(() =>
        {
            Assert.That(mergedLocal.OnlineScoreId, Is.EqualTo(44));
            Assert.That(mergedLocal.IsLocallyStored, Is.True);
            Assert.That(onlineOnly.OnlineScoreId, Is.EqualTo(45));
            Assert.That(onlineOnly.IsLocallyStored, Is.False);
        });
    }

    private static LocalReplay localScore(long onlineScoreId) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Title", "Artist", "Insane", "osu", "player",
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 5, 0.9, 1_000_000, 500, 2, 100,
        ["HD"], true, OnlineScoreId: onlineScoreId);

    private static OsuUserBeatmapScore onlineScore(long id, double accuracy, double pp) => new(
        id, 42, pp, accuracy, 1_000_000 + id, 600, new OsuScoreStatistics(1, 500, 10, 0),
        ["HD"], "[\"HD\"]", new DateTimeOffset(2026, 1, (int)(id - 43), 0, 0, 0, TimeSpan.Zero), null);

    private static ScoreHistoryEntry onlineEntry(OsuUserBeatmapScore score) => new(
        $"osu:{score.ScoreId}", score.ScoreId, 1234, 0, null, null, string.Empty, string.Empty, string.Empty,
        score.EndedAt ?? DateTimeOffset.UnixEpoch, double.NaN, score.Accuracy, score.PerformancePoints, score.TotalScore,
        score.MaximumCombo, score.Statistics.Misses, score.Mods, ScoreHistoryProvenance.OnlineBeatmap, false);

    private static OsuBestScore accountScore(long id) => new(
        id, 42, "player", 200, 0.98, 1_000_000, 600, new OsuScoreStatistics(1, 500, 10, 0),
        ["HD"], "[\"HD\"]", new DateTimeOffset(2026, 2, (int)(id - 79), 0, 0, 0, TimeSpan.Zero), null,
        new OsuScoreBeatmap(1234, "hash", "Insane", 5.5, 700, 180, 120),
        new OsuScoreBeatmapSet(567, "Title", null, "Artist", null, "Mapper", null, "ranked", null));
}

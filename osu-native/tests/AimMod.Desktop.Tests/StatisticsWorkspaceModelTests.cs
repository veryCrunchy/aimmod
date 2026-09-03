using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.ScoreHistory;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class StatisticsWorkspaceModelTests
{
    [Test]
    public void FiltersRealScoreFieldsAndBuildsAllGraphSeries()
    {
        DateTimeOffset now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        LocalReplay[] runs =
        {
            run(1, now.AddDays(-4), 5.2, 0.97, 0, 180, ["HD"], 1001),
            run(2, now.AddDays(-14), 5.6, 0.95, 2, 150, ["HD", "HR"]),
            run(3, now.AddDays(-120), 6.8, 0.91, 4, null, ["DT"]),
            run(4, now.AddDays(-2), 3.4, 0.99, 0, 90, []),
        };

        StatisticsWorkspaceModel model = StatisticsWorkspaceModel.Build(runs, new StatisticsRunQuery(
            TimeRange: StatisticsTimeRange.Days30,
            ModFilter: StatisticsModFilter.Hidden,
            Source: StatisticsScoreSource.All,
            MinimumStars: 5,
            MaximumStars: 6), now);

        Assert.Multiple(() =>
        {
            Assert.That(model.Runs.Select(item => item.ScoreId), Is.EqualTo(new[] { runs[0].ScoreId, runs[1].ScoreId }));
            Assert.That(model.CachedOnlineRunCount, Is.EqualTo(1));
            Assert.That(model.AverageAccuracy, Is.EqualTo(0.96).Within(0.0001));
            Assert.That(model.MedianPerformancePoints, Is.EqualTo(165).Within(0.001));
            Assert.That(model.Series.Select(series => series.Key), Is.EquivalentTo(new[]
            {
                "statisticsAccuracy", "statisticsPp", "statisticsStars", "statisticsMisses",
            }));
            Assert.That(model.Series.Single(series => series.Key == "statisticsPp").Points, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public void SourceFilterSeparatesOnlineAndLocalWithoutChangingDefaultUnifiedView()
    {
        LocalReplay online = run(1, DateTimeOffset.Now, 5, 0.96, 0, 170, [], 1234);
        LocalReplay local = run(2, DateTimeOffset.Now.AddMinutes(-1), 5, 0.95, 1, 150, []);

        StatisticsWorkspaceModel unified = StatisticsWorkspaceModel.Build([online, local], new StatisticsRunQuery());
        StatisticsWorkspaceModel onlineOnly = StatisticsWorkspaceModel.Build(
            [online, local],
            new StatisticsRunQuery(Source: StatisticsScoreSource.Online));
        StatisticsWorkspaceModel localOnly = StatisticsWorkspaceModel.Build(
            [online, local],
            new StatisticsRunQuery(Source: StatisticsScoreSource.Local));

        Assert.Multiple(() =>
        {
            Assert.That(unified.Runs, Has.Count.EqualTo(2));
            Assert.That(onlineOnly.Runs.Single().ScoreId, Is.EqualTo(online.ScoreId));
            Assert.That(localOnly.Runs.Single().ScoreId, Is.EqualTo(local.ScoreId));
        });
    }

    [Test]
    public void SelectedDifficultySummaryUsesExactBeatmapOnly()
    {
        Guid beatmap = Guid.NewGuid();
        LocalReplay first = run(1, DateTimeOffset.Parse("2026-01-01"), 5, 0.91, 3, 100, []) with { BeatmapId = beatmap, MaxCombo = 400 };
        LocalReplay latest = run(2, DateTimeOffset.Parse("2026-01-03"), 5, 0.96, 0, 160, []) with { BeatmapId = beatmap, MaxCombo = 650 };
        LocalReplay otherDifficulty = run(3, DateTimeOffset.Parse("2026-01-04"), 7, 0.99, 0, 300, []);

        StatisticsMapSummary summary = StatisticsWorkspaceModel.BuildMapSummary([first, latest, otherDifficulty], beatmap);

        Assert.Multiple(() =>
        {
            Assert.That(summary.PlayCount, Is.EqualTo(2));
            Assert.That(summary.AverageAccuracy, Is.EqualTo(0.935).Within(0.0001));
            Assert.That(summary.AccuracyChange, Is.EqualTo(0.05).Within(0.0001));
            Assert.That(summary.BestPerformancePoints, Is.EqualTo(160));
            Assert.That(summary.MissFreeRate, Is.EqualTo(0.5));
            Assert.That(summary.BestCombo, Is.EqualTo(650));
        });
    }

    [Test]
    public void MissingPpRemainsMissingInsteadOfInventingValues()
    {
        StatisticsWorkspaceModel model = StatisticsWorkspaceModel.Build(
            [run(1, DateTimeOffset.Now, 5, 0.95, 0, null, [])],
            new StatisticsRunQuery());

        Assert.Multiple(() =>
        {
            Assert.That(model.MedianPerformancePoints, Is.Null);
            Assert.That(model.PerformancePointRunCount, Is.Zero);
            Assert.That(model.Series.Single(series => series.Key == "statisticsPp").Points, Is.Empty);
        });
    }

    [Test]
    public void UnifiedAdapterDeduplicatesSubmittedLocalScoreAndPreservesReplay()
    {
        LocalReplay local = run(1, DateTimeOffset.Parse("2026-01-01"), 5.4, 0.94, 1, null, [], 9876);
        var online = new ScoreHistoryEntry(
            "osu:9876", 9876, 123, 456, null, null, local.Title, local.Artist, local.Difficulty,
            local.PlayedAt, 5.4, 0.955, 177, 1_200_000, 600, 0, ["HD"], ScoreHistoryProvenance.OnlineBest, false);

        LocalReplay merged = StatisticsUnifiedScoreAdapter.Merge([local], [online]).Single();

        Assert.Multiple(() =>
        {
            Assert.That(merged.ScoreId, Is.EqualTo(local.ScoreId));
            Assert.That(merged.HasReplayFile, Is.True);
            Assert.That(merged.StarRating, Is.EqualTo(5.4));
            Assert.That(merged.Accuracy, Is.EqualTo(0.955));
            Assert.That(merged.PerformancePoints, Is.EqualTo(177));
            Assert.That(merged.Mods, Is.EqualTo(new[] { "HD" }));
        });
    }

    [Test]
    public void OnlineOnlyScoreKeepsUnknownStarsExplicit()
    {
        var online = new ScoreHistoryEntry(
            "osu:100", 100, 123, 456, null, null, "Online map", "Artist", "Expert",
            DateTimeOffset.Parse("2026-01-01"), double.NaN, 0.97, 220, 1_000_000, 700, 0, [], ScoreHistoryProvenance.OnlineRecent, false);

        LocalReplay converted = StatisticsUnifiedScoreAdapter.Merge([], [online]).Single();
        StatisticsWorkspaceModel model = StatisticsWorkspaceModel.Build([converted], new StatisticsRunQuery());

        Assert.Multiple(() =>
        {
            Assert.That(double.IsNaN(converted.StarRating), Is.True);
            Assert.That(model.Runs, Has.Count.EqualTo(1));
            Assert.That(model.AverageStarRating, Is.Null);
            Assert.That(model.Series.Single(series => series.Key == "statisticsStars").Points, Is.Empty);
        });
    }

    private static LocalReplay run(
        int id,
        DateTimeOffset playedAt,
        double stars,
        double accuracy,
        int misses,
        double? pp,
        IReadOnlyList<string> mods,
        long onlineScoreId = 0) => new(
            new Guid(id, 0, 0, new byte[8]),
            Guid.NewGuid(),
            Guid.NewGuid(),
            $"Map {id}",
            "Artist",
            "Insane",
            "osu",
            "Player",
            playedAt,
            stars,
            accuracy,
            1_000_000 + id,
            500,
            misses,
            pp,
            mods,
            true,
            OnlineScoreId: onlineScoreId);
}

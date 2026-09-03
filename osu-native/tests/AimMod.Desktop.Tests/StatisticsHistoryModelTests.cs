using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class StatisticsHistoryModelTests
{
    [Test]
    public void BuildsChronologicalAccumulatedScoreAndRunSeries()
    {
        LocalReplay[] runs =
        {
            run(3, 0.94, 1, 300),
            run(1, 0.90, 3, 100),
            run(2, 0.92, 0, 200),
        };

        StatisticsHistoryModel model = StatisticsHistoryModel.Build(runs);

        Assert.Multiple(() =>
        {
            Assert.That(model.LoadedRunCount, Is.EqualTo(3));
            Assert.That(model.AccumulatedScore, Is.EqualTo(600));
            Assert.That(series(model, "historyCumulativeScore"), Is.EqualTo(new[] { 100d, 300d, 600d }));
            Assert.That(series(model, "historyCumulativeRuns"), Is.EqualTo(new[] { 1d, 2d, 3d }));
            Assert.That(model.StartedAt, Is.EqualTo(runs[1].PlayedAt));
            Assert.That(model.EndedAt, Is.EqualTo(runs[0].PlayedAt));
            Assert.That(model.TimeAxis.Start, Is.EqualTo("01 Jan 12:00"));
            Assert.That(model.TimeAxis.Middle, Is.EqualTo("02 Jan 12:00"));
            Assert.That(model.TimeAxis.End, Is.EqualTo("03 Jan 12:00"));
        });
    }

    [Test]
    public void RollingMetricsCompareEqualRecentAndPreviousWindows()
    {
        LocalReplay[] runs = Enumerable.Range(1, 40)
                                       .Select(day => run(
                                           day,
                                           day <= 20 ? 0.90 + (day % 2 == 0 ? 0.02 : -0.02) : 0.95,
                                           day <= 20 || day % 2 == 0 ? 1 : 0,
                                           100))
                                       .ToArray();

        StatisticsHistoryModel model = StatisticsHistoryModel.Build(runs);

        Assert.Multiple(() =>
        {
            Assert.That(model.RollingWindowSize, Is.EqualTo(10));
            Assert.That(model.RollingAccuracy, Is.EqualTo(0.95).Within(0.0001));
            Assert.That(model.AccuracyChange, Is.EqualTo(0).Within(0.0001));
            Assert.That(model.RollingAccuracySpread, Is.Zero.Within(0.0001));
            Assert.That(model.AccuracySpreadChange, Is.Zero.Within(0.0001));
            Assert.That(model.RollingMissFreeRate, Is.EqualTo(0.5).Within(0.0001));
            Assert.That(model.MissFreeRateChange, Is.Zero.Within(0.0001));
            Assert.That(series(model, "historyRollingAccuracy").Last(), Is.EqualTo(95).Within(0.0001));
        });
    }

    [Test]
    public void RollingChangesReflectImprovementAndTighterResults()
    {
        LocalReplay[] runs = Enumerable.Range(1, 20)
                                       .Select(day => run(
                                           day,
                                           day <= 10 ? (day % 2 == 0 ? 0.86 : 0.94) : (day % 2 == 0 ? 0.95 : 0.96),
                                           day <= 10 ? 2 : 0,
                                           100))
                                       .ToArray();

        StatisticsHistoryModel model = StatisticsHistoryModel.Build(runs);

        Assert.Multiple(() =>
        {
            Assert.That(model.RollingWindowSize, Is.EqualTo(10));
            Assert.That(model.AccuracyChange, Is.EqualTo(0.055).Within(0.0001));
            Assert.That(model.AccuracySpreadChange, Is.LessThan(-0.03));
            Assert.That(model.MissFreeRateChange, Is.EqualTo(1).Within(0.0001));
        });
    }

    [Test]
    public void EmptyAndPartialHistoryRemainExplicit()
    {
        StatisticsHistoryModel empty = StatisticsHistoryModel.Build(Array.Empty<LocalReplay>());
        StatisticsHistoryModel partial = StatisticsHistoryModel.Build(new[] { run(1, 0.9, 1, -100) }, 50);

        Assert.Multiple(() =>
        {
            Assert.That(empty.LoadedRunCount, Is.Zero);
            Assert.That(empty.RollingAccuracy, Is.Null);
            Assert.That(empty.Series.All(item => item.Points.Count == 0), Is.True);
            Assert.That(partial.IsComplete, Is.False);
            Assert.That(partial.TotalAvailableRunCount, Is.EqualTo(50));
            Assert.That(partial.AccumulatedScore, Is.Zero);
            Assert.That(partial.AccuracyChange, Is.Null);
        });
    }

    [Test]
    public void TimeAxisIncludesYearsWhenHistoryCrossesAYearBoundary()
    {
        LocalReplay first = run(1, 0.90, 1, 100) with
        {
            PlayedAt = new DateTimeOffset(2025, 12, 20, 12, 0, 0, TimeSpan.Zero),
        };
        LocalReplay last = run(2, 0.95, 0, 100) with
        {
            PlayedAt = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero),
        };

        StatisticsHistoryModel model = StatisticsHistoryModel.Build(new[] { first, last });

        Assert.Multiple(() =>
        {
            Assert.That(model.TimeAxis.Start, Is.EqualTo("Dec 2025"));
            Assert.That(model.TimeAxis.End, Is.EqualTo("Jan 2026"));
        });
    }

    [Test]
    public async Task LoaderPagesThroughTheRealSourceContract()
    {
        LocalReplay[] sourceRuns = Enumerable.Range(1, 450)
                                             .Select(index => runAt(index, 0.95, 0, 100))
                                             .OrderByDescending(item => item.PlayedAt)
                                             .ToArray();
        var source = new PagingSource(sourceRuns);

        StatisticsHistoryLoadResult result = await StatisticsHistoryLoader.LoadAsync(source);

        Assert.Multiple(() =>
        {
            Assert.That(result.Runs, Has.Count.EqualTo(450));
            Assert.That(result.TotalAvailableRunCount, Is.EqualTo(450));
            Assert.That(result.IsComplete, Is.True);
            Assert.That(source.Offsets, Is.EqualTo(new[] { 0, 200, 400 }));
        });
    }

    private static IEnumerable<double> series(StatisticsHistoryModel model, string key) =>
        model.Series.Single(item => item.Key == key).Points.Select(point => point.Value);

    private static LocalReplay run(int day, double accuracy, int misses, long score) =>
        runAt(day, accuracy, misses, score);

    private static LocalReplay runAt(int index, double accuracy, int misses, long score)
    {
        DateTimeOffset date = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        return new LocalReplay(
            new Guid(index, 0, 0, new byte[8]),
            Guid.NewGuid(),
            Guid.NewGuid(),
            $"Map {index}",
            "Artist",
            "Hard",
            "osu",
            "Player",
            date.AddDays(index - 1),
            5,
            accuracy,
            score,
            500,
            misses,
            200,
            Array.Empty<string>(),
            true);
    }

    private sealed class PagingSource(IReadOnlyList<LocalReplay> runs) : ILocalLibrarySource
    {
        public List<int> Offsets { get; } = new();

        public ValueTask<LocalLibraryPage<LocalBeatmapSet>> SearchBeatmapSetsAsync(
            LocalLibraryQuery query,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LocalLibraryPage<LocalBeatmapSet>(Array.Empty<LocalBeatmapSet>(), 0, 0, query.Limit));

        public ValueTask<LocalLibraryPage<LocalReplay>> SearchReplaysAsync(
            LocalLibraryQuery query,
            CancellationToken cancellationToken = default)
        {
            Offsets.Add(query.Offset);
            LocalReplay[] page = runs.Skip(query.Offset).Take(query.Limit).ToArray();
            return ValueTask.FromResult(new LocalLibraryPage<LocalReplay>(page, runs.Count, query.Offset, query.Limit));
        }

        public void Invalidate()
        {
        }
    }
}

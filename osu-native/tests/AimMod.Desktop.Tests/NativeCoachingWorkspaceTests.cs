using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class NativeCoachingWorkspaceTests
{
    [Test]
    public void ConstructsWithoutConflictingLayoutAxes()
    {
        var source = new InMemoryLocalLibrarySource(Array.Empty<LocalBeatmapSet>(), Array.Empty<LocalReplay>());

        Assert.DoesNotThrow(() => _ = new NativeCoachingWorkspace(
            source,
            new Dictionary<Guid, ReplayAnalysisResult>(),
            _ => { }));
    }

    [Test]
    public void SelectsTheSessionContainingTheChosenRun()
    {
        DateTimeOffset start = new(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);
        LocalReplay previousSession = run(start.AddHours(-3), 0.91, 3);
        LocalReplay selected = run(start, 0.94, 1);
        LocalReplay sameSession = run(start.AddMinutes(20), 0.96, 0);
        LocalReplay laterSession = run(start.AddHours(2), 0.93, 2);

        NativeCoachingWorkspaceModel model = NativeCoachingWorkspaceModel.Build(
            new[] { laterSession, sameSession, selected, previousSession },
            new Dictionary<Guid, ReplayAnalysisResult>(),
            selected.ScoreId);

        Assert.Multiple(() =>
        {
            Assert.That(model.SelectedRun?.ScoreId, Is.EqualTo(selected.ScoreId));
            Assert.That(model.SessionRuns.Select(item => item.ScoreId), Is.EqualTo(new[] { selected.ScoreId, sameSession.ScoreId }));
            Assert.That(model.Session?.PlayCount, Is.EqualTo(2));
            Assert.That(model.Session?.Duration, Is.EqualTo(TimeSpan.FromMinutes(20)));
            Assert.That(model.Session?.MedianAccuracy, Is.EqualTo(0.95).Within(0.0001));
        });
    }

    [Test]
    public void BoundsHistoryAndTrendSeries()
    {
        DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        LocalReplay[] runs = Enumerable.Range(0, CoachingLimits.MaximumRuns + 20)
                                       .Select(index => run(start.AddMinutes(index), 0.9 + index % 10 / 100.0, index % 3))
                                       .ToArray();

        NativeCoachingWorkspaceModel model = NativeCoachingWorkspaceModel.Build(
            runs,
            new Dictionary<Guid, ReplayAnalysisResult>());

        Assert.Multiple(() =>
        {
            Assert.That(model.History, Has.Count.EqualTo(CoachingLimits.MaximumRuns));
            Assert.That(model.TrendRuns, Has.Count.EqualTo(NativeCoachingWorkspaceModel.MaximumTrendRuns));
            Assert.That(model.TrendRuns, Is.Ordered.By(nameof(LocalReplay.PlayedAt)));
            Assert.That(model.SelectedRun?.PlayedAt, Is.EqualTo(runs[^1].PlayedAt));
        });
    }

    [Test]
    public void LeavesAnHonestEmptyWorkspace()
    {
        NativeCoachingWorkspaceModel model = NativeCoachingWorkspaceModel.Build(
            Array.Empty<LocalReplay>(),
            new Dictionary<Guid, ReplayAnalysisResult>());

        Assert.Multiple(() =>
        {
            Assert.That(model.SelectedRun, Is.Null);
            Assert.That(model.Session, Is.Null);
            Assert.That(model.SessionRuns, Is.Empty);
            Assert.That(model.TrendRuns, Is.Empty);
            Assert.That(model.Report.Intelligence.Recommendations, Is.Empty);
        });
    }

    private static LocalReplay run(DateTimeOffset playedAt, double accuracy, int misses) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        $"Map {playedAt:HHmm}",
        "Fixture Artist",
        "Insane",
        "osu",
        "Player",
        playedAt,
        5.2,
        accuracy,
        1_000_000,
        500,
        misses,
        200,
        Array.Empty<string>(),
        true);
}

using AimMod.Desktop;
using AimMod.Desktop.LocalLibrary;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class ReplayAnalysisBatchServiceTests
{
    [Test]
    public void SelectsNewestSavedUnanalysedRunsWithinHardLimit()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        LocalReplay existing = replay(now.AddMinutes(-1), hasReplay: true);
        LocalReplay missingFile = replay(now, hasReplay: false);
        LocalReplay[] candidates = Enumerable.Range(0, 8)
                                             .Select(index => replay(now.AddMinutes(-index - 2), hasReplay: true))
                                             .Append(existing)
                                             .Append(missingFile)
                                             .ToArray();

        LocalReplay[] selected = ReplayAnalysisBatchService.SelectPending(
            candidates,
            new[] { existing.ScoreId },
            99);

        Assert.Multiple(() =>
        {
            Assert.That(selected, Has.Length.EqualTo(ReplayAnalysisBatchService.MaximumBatchSize));
            Assert.That(selected.Select(item => item.ScoreId), Does.Not.Contain(existing.ScoreId));
            Assert.That(selected.Select(item => item.ScoreId), Does.Not.Contain(missingFile.ScoreId));
            Assert.That(selected.Select(item => item.PlayedAt), Is.Ordered.Descending);
        });
    }

    [Test]
    public void ZeroLimitDoesNotSelectRuns()
    {
        Assert.That(
            ReplayAnalysisBatchService.SelectPending(new[] { replay(DateTimeOffset.UtcNow, true) }, Array.Empty<Guid>(), 0),
            Is.Empty);
    }

    [Test]
    public void BreadthFirstSelectionTakesNewestAttemptFromEachExactMapFirst()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid firstMap = Guid.NewGuid();
        Guid secondMap = Guid.NewGuid();
        LocalReplay firstNewest = replay(now, true) with { BeatmapId = firstMap, Difficulty = "Insane" };
        LocalReplay firstOlder = replay(now.AddMinutes(-1), true) with { BeatmapId = firstMap, Difficulty = "Insane" };
        LocalReplay secondNewest = replay(now.AddMinutes(-2), true) with { BeatmapId = secondMap, Difficulty = "Hard" };
        LocalReplay secondOlder = replay(now.AddMinutes(-3), true) with { BeatmapId = secondMap, Difficulty = "Hard" };

        LocalReplay[] selected = ReplayAnalysisBatchService.SelectPendingBreadthFirst(
            new[] { firstOlder, secondOlder, firstNewest, secondNewest },
            Array.Empty<Guid>(),
            4);

        Assert.That(selected.Select(run => run.ScoreId), Is.EqualTo(new[]
        {
            firstNewest.ScoreId,
            secondNewest.ScoreId,
            firstOlder.ScoreId,
            secondOlder.ScoreId,
        }));
    }

    [Test]
    public void BreadthFirstSelectionSkipsCachedAndUnavailableAttempts()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid firstMap = Guid.NewGuid();
        LocalReplay cached = replay(now, true) with { BeatmapId = firstMap };
        LocalReplay nextAttempt = replay(now.AddMinutes(-1), true) with { BeatmapId = firstMap };
        LocalReplay unavailable = replay(now.AddMinutes(1), false);

        LocalReplay[] selected = ReplayAnalysisBatchService.SelectPendingBreadthFirst(
            new[] { cached, nextAttempt, unavailable },
            new[] { cached.ScoreId },
            ReplayAnalysisBatchService.MaximumBatchSize);

        Assert.That(selected.Select(run => run.ScoreId), Is.EqualTo(new[] { nextAttempt.ScoreId }));
    }

    [Test]
    public void BreadthFirstFallbackSeparatesDifficultiesWithoutBeatmapIdentity()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid setId = Guid.NewGuid();
        LocalReplay insane = replay(now, true) with { BeatmapId = Guid.Empty, SetId = setId, BeatmapHash = string.Empty, Difficulty = "Insane" };
        LocalReplay hard = replay(now.AddMinutes(-1), true) with { BeatmapId = Guid.Empty, SetId = setId, BeatmapHash = string.Empty, Difficulty = "Hard" };
        LocalReplay olderInsane = replay(now.AddMinutes(-2), true) with { BeatmapId = Guid.Empty, SetId = setId, BeatmapHash = string.Empty, Difficulty = "Insane" };

        LocalReplay[] ordered = ReplayAnalysisBatchService.OrderBreadthFirst(new[] { olderInsane, hard, insane });

        Assert.That(ordered.Select(run => run.ScoreId), Is.EqualTo(new[] { insane.ScoreId, hard.ScoreId, olderInsane.ScoreId }));
    }

    [Test]
    public void FailureLogIsBoundedAndCannotInjectLines()
    {
        LocalReplay selected = replay(DateTimeOffset.UtcNow, true) with
        {
            Title = $"Map\nwith\tcontrols {new string('x', 100)}",
        };
        var error = new ExternalLazerReplayOpenException("replay_unavailable", "sensitive path should not be copied");

        string message = ReplayAnalysisBatchService.DescribeFailure(selected, error);

        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("[replay_unavailable]"));
            Assert.That(message, Does.Contain(selected.ScoreId.ToString("D")));
            Assert.That(message, Does.Not.Contain("\n"));
            Assert.That(message, Does.Not.Contain("\t"));
            Assert.That(message, Does.Not.Contain("sensitive path"));
            Assert.That(message.Length, Is.LessThan(220));
        });
    }

    [Test]
    public void CumulativeAccountingCountsOnlyAvailableWorkingSetEntries()
    {
        LocalReplay cached = replay(DateTimeOffset.UtcNow, true);
        LocalReplay failed = replay(DateTimeOffset.UtcNow.AddMinutes(-1), true);
        LocalReplay pending = replay(DateTimeOffset.UtcNow.AddMinutes(-2), true);
        LocalReplay unavailable = replay(DateTimeOffset.UtcNow.AddMinutes(-3), false);

        ReplayAnalysisCumulativeAccounting accounting = ReplayAnalysisCumulativeAccounting.Create(
            new[] { cached, failed, pending, unavailable },
            new[] { cached.ScoreId, unavailable.ScoreId, Guid.NewGuid() },
            new[] { cached.ScoreId, failed.ScoreId, Guid.NewGuid() });

        Assert.Multiple(() =>
        {
            Assert.That(accounting.Total, Is.EqualTo(3));
            Assert.That(accounting.Cached, Is.EqualTo(1));
            Assert.That(accounting.PreviouslyFailed, Is.EqualTo(1));
            Assert.That(accounting.Processed, Is.EqualTo(2));
            Assert.That(accounting.Remaining, Is.EqualTo(1));
        });
    }

    [Test]
    public void BatchProgressMapsToCumulativeCompletedAndExposesCachedAndRemainingCounts()
    {
        LocalReplay[] runs = Enumerable.Range(0, 12)
                                       .Select(index => replay(DateTimeOffset.UtcNow.AddMinutes(-index), true))
                                       .ToArray();
        ReplayAnalysisCumulativeAccounting accounting = ReplayAnalysisCumulativeAccounting.Create(
            runs,
            runs.Take(3).Select(run => run.ScoreId),
            Array.Empty<Guid>());

        ReplayAnalysisBatchProgress firstBatch = accounting.MapBatchProgress(new ReplayAnalysisBatchProgress(4, 5, "First map"));
        var firstResult = new ReplayAnalysisBatchResult(
            runs.Skip(3).Take(4).ToDictionary(run => run.ScoreId, _ => (AimMod.Osu.Runtime.Contracts.ReplayAnalysisResult)null!),
            new[] { runs[7].ScoreId });
        accounting = accounting.Add(firstResult);
        ReplayAnalysisBatchProgress secondBatch = accounting.MapBatchProgress(new ReplayAnalysisBatchProgress(2, 4, "Second map"));

        Assert.Multiple(() =>
        {
            Assert.That(firstBatch.Completed, Is.EqualTo(7));
            Assert.That(firstBatch.Total, Is.EqualTo(12));
            Assert.That(firstBatch.CurrentTitle, Does.Contain("3 cached"));
            Assert.That(firstBatch.CurrentTitle, Does.Contain("5 remaining"));
            Assert.That(secondBatch.Completed, Is.EqualTo(10));
            Assert.That(secondBatch.Total, Is.EqualTo(12));
            Assert.That(secondBatch.CurrentTitle, Does.Contain("2 remaining"));
        });
    }

    [Test]
    public void FinishedAccountingRemainsAtFullCumulativeProgress()
    {
        LocalReplay[] runs = Enumerable.Range(0, 7)
                                       .Select(index => replay(DateTimeOffset.UtcNow.AddMinutes(-index), true))
                                       .ToArray();
        ReplayAnalysisCumulativeAccounting accounting = ReplayAnalysisCumulativeAccounting.Create(
            runs,
            runs.Take(2).Select(run => run.ScoreId),
            Array.Empty<Guid>());
        var result = new ReplayAnalysisBatchResult(
            runs.Skip(2).Take(4).ToDictionary(run => run.ScoreId, _ => (AimMod.Osu.Runtime.Contracts.ReplayAnalysisResult)null!),
            new[] { runs[^1].ScoreId });

        ReplayAnalysisBatchProgress finished = accounting.Add(result)
                                                         .MapBatchProgress(new ReplayAnalysisBatchProgress(0, 0, string.Empty));

        Assert.Multiple(() =>
        {
            Assert.That(finished.Completed, Is.EqualTo(7));
            Assert.That(finished.Total, Is.EqualTo(7));
            Assert.That(finished.CurrentTitle, Does.Contain("2 cached"));
            Assert.That(finished.CurrentTitle, Does.Contain("0 remaining"));
        });
    }

    private static LocalReplay replay(DateTimeOffset playedAt, bool hasReplay) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        "Map",
        "Artist",
        "Insane",
        "osu",
        "Player",
        playedAt,
        5.2,
        0.95,
        1_000_000,
        500,
        1,
        200,
        Array.Empty<string>(),
        hasReplay,
        new string('a', 32));
}

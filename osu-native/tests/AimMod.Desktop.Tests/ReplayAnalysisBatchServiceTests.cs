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

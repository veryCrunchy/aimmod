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

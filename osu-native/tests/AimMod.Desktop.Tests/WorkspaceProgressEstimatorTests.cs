using AimMod.Desktop.Coaching;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class WorkspaceProgressEstimatorTests
{
    [Test]
    public void EstimatesFromObservedThroughputOnlyAfterEnoughEvidence()
    {
        DateTimeOffset now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var estimator = new WorkspaceProgressEstimator(() => now);

        estimator.Report(0);
        Assert.That(estimator.Remaining(0, 100), Is.Null, "no completed work yet");

        now += TimeSpan.FromSeconds(1);
        Assert.That(estimator.Remaining(2, 100), Is.Null, "too early for a stable estimate");

        now += TimeSpan.FromSeconds(9);
        TimeSpan? remaining = estimator.Remaining(10, 110);

        Assert.That(remaining, Is.Not.Null);
        Assert.That(remaining!.Value.TotalSeconds, Is.EqualTo(100).Within(0.5));
    }

    [Test]
    public void StartsCountingFromTheFirstReportedProgressAndResets()
    {
        DateTimeOffset now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var estimator = new WorkspaceProgressEstimator(() => now);
        estimator.Report(50);

        now += TimeSpan.FromSeconds(10);

        Assert.That(estimator.Remaining(60, 100)!.Value.TotalSeconds, Is.EqualTo(40).Within(0.5));
        Assert.That(estimator.Remaining(100, 100), Is.Null, "a finished pass has nothing remaining");

        estimator.Reset();
        Assert.That(estimator.Remaining(70, 100), Is.Null);
    }

    [TestCase(10, "less than a minute remaining")]
    [TestCase(44, "less than a minute remaining")]
    [TestCase(90, "about 2 min remaining")]
    [TestCase(1_800, "about 30 min remaining")]
    [TestCase(10_800, "about 3 h remaining")]
    public void FormatsRemainingTimeInFriendlyUnits(int seconds, string expected) =>
        Assert.That(WorkspaceProgressEstimator.FormatRemaining(TimeSpan.FromSeconds(seconds)), Is.EqualTo(expected));
}

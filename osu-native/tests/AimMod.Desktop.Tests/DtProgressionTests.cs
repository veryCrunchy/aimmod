using AimMod.Desktop.Trainers;
using NUnit.Framework;
using osu.Game.Rulesets.Osu.Mods;

namespace AimMod.Desktop.Tests;

public class DtProgressionTests
{
    private static TrainerResult run(double acc = 98.5, int misses = 0) => new(Guid.NewGuid(), DateTimeOffset.UtcNow, new(), 300, 300 - misses,
        200, 0, 0, 0, 12, 0, Accuracy: acc, JudgementMisses: misses);

    [Test]
    public void StartsAtNormalAndRequiresTwoCleanRunsAtEachSpeed()
    {
        var first = DtProgression.Apply(new("map"), 100, run());
        Assert.That(first.Speed, Is.EqualTo(100)); Assert.That(first.CleanRuns, Is.EqualTo(1));
        var second = DtProgression.Apply(first, 100, run());
        Assert.That(second.Speed, Is.EqualTo(102)); Assert.That(second.CleanRuns, Is.Zero);
        Assert.That(DtProgression.Apply(second, 102, run()).Speed, Is.EqualTo(102));
    }

    [Test]
    public void TwoExcellentRunsTakeASlightlyLargerStepAndClampAtDt()
    {
        var first = DtProgression.Apply(new("map"), 100, run(99.8));
        Assert.That(DtProgression.Apply(first, 100, run(99.7)).Speed, Is.EqualTo(103));
        var atTop = DtProgression.Apply(new("map", 149), 149, run());
        Assert.That(DtProgression.Apply(atTop, 149, run()).Speed, Is.EqualTo(150));
        var dt = DtProgression.Apply(new("map", 150), 150, run());
        Assert.That(dt.Completed, Is.False);
        var completed = DtProgression.Apply(dt, 150, run());
        Assert.That(completed.Speed, Is.EqualTo(150)); Assert.That(completed.Completed, Is.True);
    }

    [TestCase(94, 0, 117)]
    [TestCase(88, 0, 115)]
    [TestCase(98, 4, 117)]
    [TestCase(96, 1, 120)]
    [TestCase(99, 1, 120)]
    public void AccuracyAndMissesControlHoldAndRecovery(double acc, int misses, int expected)
    {
        var result = DtProgression.Apply(new("map", 120, 1), 120, run(acc, misses));
        Assert.That(result.Speed, Is.EqualTo(expected)); Assert.That(result.CleanRuns, Is.Zero);
        Assert.That(DtProgression.Apply(new("map"), 100, run(80, 30)).Speed, Is.EqualTo(100));
    }

    [Test]
    public void AbortAssistInvalidAndDuplicateResultsDoNotMoveSpeed()
    {
        var state = new DtProgress("map"); var result = run();
        Assert.That(DtProgression.Apply(state, 100, null), Is.SameAs(state));
        Assert.That(DtProgression.Apply(state, 100, result with { Assisted = true }), Is.SameAs(state));
        Assert.That(DtProgression.Apply(state, 100, result with { Accuracy = double.NaN }), Is.SameAs(state));
        Assert.That(DtProgression.Apply(state, 100, result with { Notes = 10 }), Is.SameAs(state));
        Assert.That(DtProgression.Apply(state, 105, result), Is.SameAs(state));
        var once = DtProgression.Apply(state, 100, result);
        Assert.That(DtProgression.Apply(once, 100, result), Is.SameAs(once));
    }

    [Test]
    public async Task ProgressPersistsPerDifficultyAndStorageAccount()
    {
        string root = Path.Combine(Path.GetTempPath(), "aimmod-dt-tests-" + Guid.NewGuid());
        try
        {
            var first = new DtProgressStore(Path.Combine(root, "account-a.json"));
            var second = new DtProgressStore(Path.Combine(root, "account-b.json"));
            await first.SaveAsync(new("one", 123));
            Assert.That((await new DtProgressStore(Path.Combine(root, "account-a.json")).LoadAsync("one")).Speed, Is.EqualTo(123));
            Assert.That((await first.LoadAsync("two")).Speed, Is.EqualTo(100));
            Assert.That((await second.LoadAsync("one")).Speed, Is.EqualTo(100));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public void UsesActualDtRateAndLeavesNormalSpeedUnmodified()
    {
        Assert.That(DtProgression.Mods(100).OfType<OsuModDoubleTime>(), Is.Empty);
        Assert.That(DtProgression.Mods(102).OfType<OsuModDoubleTime>().Single().SpeedChange.Value, Is.EqualTo(1.02));
        Assert.That(DtProgression.Mods(150).OfType<OsuModDoubleTime>().Single().SpeedChange.Value, Is.EqualTo(1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => DtProgression.Mods(151));
    }
}

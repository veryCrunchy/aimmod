using AimMod.Desktop.Trainers;
using NUnit.Framework;
using osu.Game.Rulesets.Osu.Mods;

namespace AimMod.Desktop.Tests;

public class DtProgressionTests
{
    private static TrainerResult run(double acc = 98.5, int misses = 0) => new(Guid.NewGuid(), DateTimeOffset.UtcNow, new(), 300, 300 - misses,
        200, 0, 0, 0, 12, 0, Accuracy: acc, JudgementMisses: misses);

    [TestCase(82, 101)]
    [TestCase(88, 101)]
    [TestCase(92, 102)]
    [TestCase(97, 103)]
    public void FirstRunSetsPersonalTargetAndEarnsProportionalSpeed(double accuracy, int speed)
    {
        var first = DtProgression.Apply(new("map"), 100, run(accuracy));
        Assert.That(first.Speed, Is.EqualTo(speed));
        Assert.That(DtProgression.AccuracyTarget(first), Is.EqualTo(accuracy - 2));
        Assert.That(first.AccuracyReference, Is.EqualTo(accuracy));
    }

    [Test]
    public void ClampsAtDtAndTwoPersonalTargetRunsConfirmTheGoal()
    {
        var atTop = DtProgression.Apply(new("map", 149), 149, run());
        Assert.That(atTop.Speed, Is.EqualTo(150));
        var dt = DtProgression.Apply(new("map", 150, AccuracyReference: 88), 150, run(87));
        Assert.That(dt.Completed, Is.False);
        var completed = DtProgression.Apply(dt, 150, run(87));
        Assert.That(completed.Speed, Is.EqualTo(150)); Assert.That(completed.Completed, Is.True);
    }

    [TestCase(95, 0, 119)]
    [TestCase(92, 0, 118)]
    [TestCase(86, 0, 115)]
    [TestCase(98, 4, 120)]
    [TestCase(98, 10, 117)]
    [TestCase(98, 19, 115)]
    [TestCase(96, 1, 121)]
    [TestCase(99, 1, 121)]
    public void AccuracyAndMissesControlHoldAndRecovery(double acc, int misses, int expected)
    {
        var result = DtProgression.Apply(new("map", 120, 1, AccuracyReference: 98), 120, run(acc, misses));
        Assert.That(result.Speed, Is.EqualTo(expected)); Assert.That(result.CleanRuns, Is.Zero);
        Assert.That(DtProgression.Apply(new("map"), 100, run(80, 30)).Speed, Is.EqualTo(100));
    }

    [Test]
    public void TargetUsesPersonalHistoryAndDoesNotFallAfterBadRuns()
    {
        var state = new DtProgress("map", 120, AccuracyReference: 88);
        Assert.That(DtProgression.Apply(state, 120, run(87)).Speed, Is.EqualTo(121));
        Assert.That(DtProgression.Apply(state, 120, run(92)).Speed, Is.EqualTo(122));
        Assert.That(DtProgression.Apply(state, 120, run(85)).Speed, Is.EqualTo(119));
        for (int i = 0; i < 6; i++) state = DtProgression.Apply(state, state.Speed, run(82, 10));
        Assert.That(DtProgression.AccuracyTarget(state), Is.EqualTo(86));
        Assert.That(DtProgression.Apply(new("map"), 100, run(70)).AccuracyReference, Is.Null);
        Assert.That(DtProgression.Apply(new("map"), 100, run(98, 40)).AccuracyReference, Is.Null);
    }

    [Test]
    public void SeveralBetterRunsRaiseReferenceButOneSpikeDoesNot()
    {
        var state = new DtProgress("map", AccuracyReference: 88);
        foreach (double accuracy in new[] { 88d, 88, 100 }) state = DtProgression.Apply(state, state.Speed, run(accuracy));
        Assert.That(state.AccuracyReference, Is.EqualTo(88));
        foreach (double accuracy in new[] { 94d, 94, 94 }) state = DtProgression.Apply(state, state.Speed, run(accuracy));
        Assert.That(state.AccuracyReference, Is.EqualTo(94));
    }

    [Test]
    public void ExistingHistoryMigratesFromLowestSupportedSpeed()
    {
        var state = new DtProgress("map", 120, Attempts: [
            new(Guid.NewGuid(), DateTimeOffset.UtcNow, 100, 89, 0, 300, 102, ""),
            new(Guid.NewGuid(), DateTimeOffset.UtcNow, 100, 87, 0, 300, 102, ""),
            new(Guid.NewGuid(), DateTimeOffset.UtcNow, 120, 82, 0, 300, 120, "")]);
        Assert.That(DtProgression.AccuracyTarget(state), Is.EqualTo(86));
        Assert.That(DtProgression.Apply(state, 120, run(88)).Speed, Is.EqualTo(121));
    }

    [Test]
    public void AbortAssistInvalidAndDuplicateResultsDoNotMoveSpeed()
    {
        var state = new DtProgress("map"); var result = run();
        Assert.That(DtProgression.Apply(state, 100, null), Is.SameAs(state));
        Assert.That(DtProgression.Apply(state, 100, result with { Assisted = true }), Is.SameAs(state));
        Assert.That(DtProgression.Apply(state, 100, result with { Accuracy = double.NaN }), Is.SameAs(state));
        Assert.That(DtProgression.Apply(state, 100, result with { Notes = 10 }), Is.SameAs(state));
        Assert.That(DtProgression.Apply(state, 100, result with { JudgementMisses = 301 }), Is.SameAs(state));
        Assert.That(DtProgression.Apply(state, 100, result with { Accuracy = -1 }), Is.SameAs(state));
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
            await first.SaveAsync(new("one", 123, AccuracyReference: 88));
            Assert.That((await new DtProgressStore(Path.Combine(root, "account-a.json")).LoadAsync("one")).Speed, Is.EqualTo(123));
            Assert.That((await first.LoadAsync("one")).AccuracyReference, Is.EqualTo(88));
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

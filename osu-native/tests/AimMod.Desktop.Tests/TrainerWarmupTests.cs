using AimMod.Desktop.Trainers;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

public sealed class TrainerWarmupTests
{
    private static TrainerWarmup create(int minutes = 2, IEnumerable<TrainerResult>? runs = null) =>
        TrainerWarmup.Create(minutes, new(Keys: "D / F", OffsetMs: -25), runs ?? [], [], DateTimeOffset.UtcNow);

    internal static TrainerResult Result(TrainerSettings s, double acc = 94, int misses = 0) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(-1), s, 100, 100 - misses, 90, 0, 0, 0, 12, 0,
            Engine: TrainerResult.EngineFor(s), Accuracy: acc, PlayedSeconds: s.Seconds,
            JudgementMisses: misses, Demand: new(5, 140, 400, 12));

    [TestCase(2)] [TestCase(4)] [TestCase(8)]
    public void PlanHasFourPlayableDrillsAndPreservesControls(int minutes)
    {
        var plan = create(minutes);
        Assert.That(plan.Steps.Sum(s => s.Settings.Seconds), Is.EqualTo(minutes * 60));
        Assert.That(plan.Steps.Select(s => s.Settings.Kind).Distinct().Count(), Is.EqualTo(4));
        Assert.That(plan.Steps.Select(s => s.Settings.Music).Distinct().Count(), Is.EqualTo(4));
        for (int i = 0; i < plan.Steps.Count; i++)
        {
            var s = plan.CurrentSettings();
            s.Validate();
            Assert.That(s.Keys, Is.EqualTo("D / F")); Assert.That(s.OffsetMs, Is.EqualTo(-25));
            Assert.That(s.SkillLimits!.MaxNps, Is.LessThanOrEqualTo(2.5));
            if (i > 0) Assert.That(s.Music, Is.Not.EqualTo(plan.Steps[i - 1].Settings.Music));
            var beatmap = TrainerBeatmap.Create(s);
            Assert.That(beatmap.HitObjects.Count, Is.GreaterThanOrEqualTo(12));
            var demand = TrainerSkillProfile.Measure(beatmap);
            Assert.That(demand.PeakNps, Is.LessThanOrEqualTo(s.SkillLimits.MaxNps + .01));
            Assert.That(plan.Record(Result(s)), Is.True);
        }
        Assert.That(plan.Finished, Is.True);
        Assert.That(plan.Results.Select(r => r.WarmupRun!.Step), Is.EqualTo(new[] { 0, 1, 2, 3 }));
        Assert.That(plan.Results.All(r => r.WarmupRun!.SessionId == plan.Id), Is.True);
    }

    [Test]
    public void BadRunEasesNextDrillWithoutRaisingTheCeiling()
    {
        var plan = create();
        Assert.That(plan.Record(Result(plan.CurrentSettings(), 70, 15)), Is.True);
        Assert.That(plan.Pace, Is.EqualTo(.85));
        Assert.That(plan.CurrentSettings().SkillLimits!.MaxNps, Is.LessThan(plan.Steps[1].Settings.SkillLimits!.MaxNps));
        Assert.That(plan.CurrentSettings().OverallDifficulty, Is.LessThan(plan.Steps[1].Settings.OverallDifficulty));
        plan.Record(Result(plan.CurrentSettings(), 100));
        Assert.That(plan.Pace, Is.EqualTo(.9).Within(.001));
        for (int i = 0; i < 20; i++) plan.Ease();
        Assert.That(plan.Pace, Is.EqualTo(.6));
        plan.CurrentSettings().Validate();
    }

    [Test]
    public void AssistedIncompleteMismatchedAndDuplicateResultsDoNotAdvance()
    {
        var plan = create(); var result = Result(plan.CurrentSettings());
        Assert.That(plan.Record(result with { Assisted = true }), Is.False);
        Assert.That(plan.Record(result with { PlayedSeconds = 5 }), Is.False);
        Assert.That(plan.Record(result with { Settings = result.Settings with { PatternSeed = 42 } }), Is.False);
        Assert.That(plan.Record(result with { Accuracy = double.NaN }), Is.False);
        Assert.That(plan.Step, Is.Zero);
        Assert.That(plan.Record(result), Is.True);
        Assert.That(plan.Record(result), Is.False);
        Assert.That(plan.Step, Is.EqualTo(1));
    }

    [Test]
    public void PersonalisesFromRepeatedPlaysWithoutRequiringNinetyEightPercent()
    {
        var runs = Enumerable.Range(0, 3).Select(_ => Result(new(), 88)).ToArray();
        var plan = create(runs: runs);
        Assert.That(plan.Steps[0].AccuracyReference, Is.EqualTo(88));
        Assert.That(plan.CurrentSettings().SkillLimits!.MaxNps, Is.EqualTo(3.5).Within(.01));
        plan.Record(Result(plan.CurrentSettings(), 87));
        Assert.That(plan.Pace, Is.EqualTo(1));
        var warmups = runs.Select(r => r with { WarmupRun = new(Guid.NewGuid(), 0) });
        Assert.That(create(runs: warmups).CurrentSettings().SkillLimits!.MaxNps, Is.EqualTo(1.75).Within(.01));
    }

    [Test]
    public void OldFutureAndAssistedHistoryCannotSetWarmupDemand()
    {
        var run = Result(new());
        var plan = create(runs: [run with { CompletedAt = DateTimeOffset.UtcNow.AddDays(-31) },
            run with { Id = Guid.NewGuid(), CompletedAt = DateTimeOffset.UtcNow.AddDays(1) },
            run with { Id = Guid.NewGuid(), Assisted = true }]);
        Assert.That(plan.CurrentSettings().SkillLimits!.MaxNps, Is.EqualTo(1.75).Within(.01));
    }

    [Test]
    public void CompletedAllMissRunStillEasesOffWhenNoTimingSpreadCanBeMeasured()
    {
        var plan = create();
        var result = Result(plan.CurrentSettings(), 0, 100) with { SpreadMs = null, MeanMs = null };
        Assert.That(plan.Record(result), Is.True);
        Assert.That(plan.Step, Is.EqualTo(1));
        Assert.That(plan.Pace, Is.LessThan(1));
    }
}

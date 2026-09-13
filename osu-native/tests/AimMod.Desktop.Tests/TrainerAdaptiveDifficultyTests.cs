using AimMod.Desktop.Trainers;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

public class TrainerAdaptiveDifficultyTests
{
    private readonly DateTimeOffset now = DateTimeOffset.UtcNow;
    private TrainerResult run(TrainerSettings settings, int age, double accuracy = 88) =>
        TrainerWarmupTests.Result(settings, accuracy) with { CompletedAt = now.AddMinutes(-age), Demand = new(6, 160, 600, 24) };

    [Test]
    public void ConsistentLowerAccuracyCanBuildSkillWithoutNinetyFivePercent()
    {
        var settings = new TrainerSettings(TrainerKind.Alternating, AdaptiveDifficulty: true);
        var runs = Enumerable.Range(1, 3).Select(i => run(settings, i)).ToArray();
        var next = TrainerAdaptiveDifficulty.Apply(settings, runs, [], now);
        Assert.That(next.SkillLimits!.MaxNps, Is.GreaterThan(6));
        Assert.That(next.OverallDifficulty, Is.EqualTo(5.25));
        Assert.That(TrainerBeatmap.Create(next).Difficulty.OverallDifficulty, Is.EqualTo(5.25f));
        Assert.That(next.Keys, Is.EqualTo(settings.Keys));
        Assert.That(next.Music, Is.EqualTo(settings.Music));
        var rough = runs.Concat([run(settings, 0, 70), run(settings, 0, 70)]);
        Assert.That(TrainerAdaptiveDifficulty.Apply(settings, rough, [], now).SkillLimits!.MaxNps, Is.LessThan(next.SkillLimits.MaxNps));
    }

    [TestCase(TrainerKind.Steady)] [TestCase(TrainerKind.Alternating)] [TestCase(TrainerKind.Bursts)]
    [TestCase(TrainerKind.Rhythm)] [TestCase(TrainerKind.Aim)] [TestCase(TrainerKind.Reading)]
    public void FixedPatternsAlsoRespectPersonalLimits(TrainerKind kind)
    {
        var settings = new TrainerSettings(kind, Bpm: 210, AdaptiveDifficulty: true, AimSpacing: 140,
            Sliders: TrainerSliderStyle.Mixed, NoteSpeed: TrainerNoteSpeed.FourPerBeat, ApproachRate: 10);
        var next = TrainerAdaptiveDifficulty.Apply(settings, [], [], now);
        var map = TrainerBeatmap.Create(next);
        foreach (var obj in map.HitObjects) obj.ApplyDefaults(map.ControlPointInfo, map.Difficulty);
        var demand = TrainerSkillProfile.Measure(map);
        Assert.That(demand.PeakNps, Is.LessThanOrEqualTo(next.SkillLimits!.MaxNps + .001));
        Assert.That(demand.AimVelocity, Is.LessThanOrEqualTo(next.SkillLimits.MaxAimVelocity + .001));
        Assert.That(next.Pattern, Is.EqualTo(settings.Pattern));
        Assert.That(next.RandomizePatterns, Is.False);
    }

    [Test]
    public void ManualSettingsArePreservedAndIncompleteOrCorruptRunsCannotRaiseDifficulty()
    {
        var settings = new TrainerSettings(TrainerKind.Aim, AdaptiveDifficulty: true);
        var baseline = TrainerAdaptiveDifficulty.Apply(settings, [], [], now);
        var invalid = Enumerable.Range(1, 3).Select(i => run(settings, i, 99) with { Hits = 200 });
        Assert.That(TrainerAdaptiveDifficulty.Apply(settings, invalid, [], now), Is.EqualTo(baseline));
        invalid = Enumerable.Range(1, 3).Select(i => run(settings, i, 99) with { PlayedSeconds = 5 });
        Assert.That(TrainerAdaptiveDifficulty.Apply(settings, invalid, [], now), Is.EqualTo(baseline));
        invalid = Enumerable.Range(1, 3).Select(i => run(settings, i, 99) with { Assisted = true });
        Assert.That(TrainerAdaptiveDifficulty.Apply(settings, invalid, [], now), Is.EqualTo(baseline));
        var manual = settings with { AdaptiveDifficulty = false };
        Assert.That(TrainerAdaptiveDifficulty.Apply(manual, [], [], now), Is.EqualTo(manual));
    }

    [Test]
    public void ReactionAdjustsOnlyAfterRepeatedRunsAtTheSameWindowAndMode()
    {
        var settings = new TrainerSettings(TrainerKind.Reaction, AdaptiveDifficulty: true);
        var summary = new ReactionSummary(10, 0, 0, 0, 0, 0, 250, 300, 250, 250, []);
        var runs = Enumerable.Range(1, 3).Select(i => run(settings, i) with
            { Engine = "reaction-v2", Notes = 10, Hits = 10, Reaction = summary }).ToArray();
        Assert.That(TrainerAdaptiveDifficulty.Apply(settings, runs.Take(1), [], now).ReactionWindowMs, Is.EqualTo(1200));
        Assert.That(TrainerAdaptiveDifficulty.Apply(settings, runs, [], now).ReactionWindowMs, Is.EqualTo(1000));
        Assert.That(TrainerAdaptiveDifficulty.Apply(settings, runs.Select(r => r with { Extras = 4 }), [], now).ReactionWindowMs, Is.EqualTo(1500));
        Assert.That(TrainerAdaptiveDifficulty.Apply(settings with { ReactionMode = ReactionMode.Choice }, runs, [], now).ReactionWindowMs, Is.EqualTo(1200));
    }

    [Test]
    public void SpinnerCountsShortCompletedSessionsWithoutTapSpread()
    {
        var settings = new TrainerSettings(TrainerKind.Spinner, AdaptiveDifficulty: true);
        var runs = Enumerable.Range(1, 3).Select(i => run(settings, i, 99) with
            { Notes = 4, Hits = 4, SpreadMs = null, SpinnerPractice = new(4, 300, 10, 99, 0) }).ToArray();
        Assert.That(runs.All(r => TrainerPerformance.IsCompleted(r, now)), Is.True);
        Assert.That(TrainerAdaptiveDifficulty.Apply(settings, runs, [], now).SpinnerSeconds, Is.EqualTo(6));
        Assert.That(TrainerAdaptiveDifficulty.Apply(settings, runs.Select(r => r with { Accuracy = 70 }), [], now).SpinnerSeconds, Is.EqualTo(2));
    }

    [Test]
    public void AccuracyWindowOnlyChangesAfterThreeMatchingRunsAndHasBoundaries()
    {
        var settings = new TrainerSettings(AdaptiveDifficulty: true);
        var runs = Enumerable.Range(1, 3).Select(i => run(settings, i, 98)).ToArray();
        var next = TrainerAdaptiveDifficulty.Apply(settings, runs, [], now);
        var oneAtNext = run(next, 0, 98);
        Assert.That(TrainerAdaptiveDifficulty.Apply(next, runs.Prepend(oneAtNext), [], now).OverallDifficulty, Is.EqualTo(next.OverallDifficulty));
        Assert.That(TrainerAdaptiveDifficulty.Apply(settings, runs.Select(r => r with { Accuracy = 60 }), [], now).OverallDifficulty, Is.EqualTo(4.5));
        Assert.That(TrainerAdaptiveDifficulty.Apply(settings, runs.Select(r => r with { Settings = settings with { OverallDifficulty = 8 } }), [], now).OverallDifficulty, Is.EqualTo(8));
    }

    [Test]
    public void SparseCompletedRunsCountWithoutUnlockingDifficultyFromTooFewTaps()
    {
        var settings = new TrainerSettings(TrainerKind.Aim, AdaptiveDifficulty: true);
        var plan = TrainerGuidedPractice.Create(settings, TrainerGuidedFocus.Spacing);
        var runs = Enumerable.Range(1, 3).Select(i => run(plan.Current, i, 99) with {
            Notes = 2, Hits = 2, SpreadMs = null, GuidedRun = TrainerGuidedPractice.Stamp(plan) }).ToArray();
        Assert.That(runs.All(TrainerGuidedPractice.IsUsable), Is.True);
        Assert.That(TrainerGuidedPractice.Advise(plan, runs).MatchingRuns, Is.EqualTo(3));
        Assert.That(TrainerGuidedPractice.Advise(plan, runs).Next, Is.Null);
        Assert.That(TrainerAdaptiveDifficulty.Apply(settings, runs, [], now).OverallDifficulty, Is.EqualTo(5));
    }

    [Test]
    public void GuidedPracticeAcceptsCompletedAllMissRunsAndRetainsMissingSpread()
    {
        var settings = new TrainerSettings(TrainerKind.Aim);
        var plan = TrainerGuidedPractice.Create(settings, TrainerGuidedFocus.Spacing);
        var runs = Enumerable.Range(1, 3).Select(i => run(plan.Current, i, 0) with
            { Hits = 0, JudgementMisses = 100, SpreadMs = null, GuidedRun = TrainerGuidedPractice.Stamp(plan) }).ToArray();
        Assert.That(runs.All(TrainerGuidedPractice.IsUsable), Is.True);
        Assert.That(TrainerGuidedPractice.Advise(plan, runs).Next!.AimSpacing, Is.LessThan(settings.AimSpacing));
    }

    [Test]
    public void GuidedPracticeProgressesFromPersonalBaselineAndIgnoresOneSpike()
    {
        var plan = TrainerGuidedPractice.Create(new(TrainerKind.Aim), TrainerGuidedFocus.Spacing);
        var runs = Enumerable.Range(1, 3).Select(i => run(plan.Current, i) with { GuidedRun = TrainerGuidedPractice.Stamp(plan) }).ToArray();
        Assert.That(TrainerGuidedPractice.Advise(plan, runs).Next!.AimSpacing, Is.GreaterThan(plan.Current.AimSpacing));
        Assert.That(TrainerGuidedPractice.Advise(plan, [runs[0], runs[1], runs[2] with { Accuracy = 100 }]).Next, Is.Null);
    }
}

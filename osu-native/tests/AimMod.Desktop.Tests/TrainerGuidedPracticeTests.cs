using AimMod.Desktop.Trainers;
using NUnit.Framework;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Objects.Types;
using osuTK;

namespace AimMod.Desktop.Tests;

[TestFixture]
public class TrainerGuidedPracticeTests
{
    [TestCase(TrainerKind.Steady, TrainerSliderStyle.None)]
    [TestCase(TrainerKind.Aim, TrainerSliderStyle.Mixed)]
    [TestCase(TrainerKind.Alternating, TrainerSliderStyle.BackAndForth)]
    [TestCase(TrainerKind.Reading, TrainerSliderStyle.None)]
    public void CompactComparisonPreservesRhythmShapeAndSliderDuration(TrainerKind kind, TrainerSliderStyle sliders)
    {
        var plan = TrainerGuidedPractice.Create(new(kind, PatternSeed: 123, Sliders: sliders, OffsetMs: 31), TrainerGuidedFocus.MovementComparison);
        var original = TrainerBeatmap.Create(TrainerGuidedPractice.SettingsFor(plan));
        var compact = TrainerBeatmap.Create(TrainerGuidedPractice.SettingsFor(plan with { Step = 1 }));
        Assert.That(compact.HitObjects.Count, Is.EqualTo(original.HitObjects.Count));
        foreach (var (a, b) in original.HitObjects.Zip(compact.HitObjects))
        {
            a.ApplyDefaults(original.ControlPointInfo, original.Difficulty);
            b.ApplyDefaults(compact.ControlPointInfo, compact.Difficulty);
            Assert.That(b.StartTime, Is.EqualTo(a.StartTime));
            Assert.That(b.GetType(), Is.EqualTo(a.GetType()));
            var expected = new Vector2(256, 192) + (a.Position - new Vector2(256, 192)) * .55f;
            Assert.That(Vector2.Distance(b.Position, expected), Is.LessThan(.001));
            if (a is IHasDuration da && b is IHasDuration db) Assert.That(db.Duration, Is.EqualTo(da.Duration).Within(.01));
        }
        Assert.That(compact.Difficulty.OverallDifficulty, Is.EqualTo(original.Difficulty.OverallDifficulty));
        Assert.That(compact.Difficulty.ApproachRate, Is.EqualTo(original.Difficulty.ApproachRate));
    }

    [TestCase(TrainerGuidedFocus.Spacing)]
    [TestCase(TrainerGuidedFocus.Endurance)]
    [TestCase(TrainerGuidedFocus.GroupLength)]
    public void AdvancementRequiresThreeCleanRunsAndChangesOnlyChosenDemand(TrainerGuidedFocus focus)
    {
        var plan = TrainerGuidedPractice.Create(new(TrainerKind.Bursts, PatternSeed: 123, OffsetMs: 31, Keys: "D / F"), focus);
        var runs = Enumerable.Range(0, 3).Select(i => result(plan, i)).ToArray();
        Assert.That(TrainerGuidedPractice.Advise(plan, runs.Take(2)).Next, Is.Null);
        var advice = TrainerGuidedPractice.Advise(plan, runs);
        Assert.That(advice.Next, Is.Not.Null);
        var normalised = focus switch
        {
            TrainerGuidedFocus.Spacing => advice.Next! with { AimSpacing = plan.Current.AimSpacing },
            TrainerGuidedFocus.Endurance => advice.Next! with { Seconds = plan.Current.Seconds },
            _ => advice.Next! with { Pattern = plan.Current.Pattern },
        };
        Assert.That(normalised, Is.EqualTo(plan.Current));
        Assert.That(TrainerGuidedPractice.Advise(plan, [runs[0], runs[0], runs[0]]).Next, Is.Null);
    }

    [Test]
    public void MixedSettingsAssistanceAndTruncatedSongsDoNotAdvance()
    {
        var plan = TrainerGuidedPractice.Create(new(PatternSeed: 123), TrainerGuidedFocus.Endurance);
        var valid = result(plan, 0);
        var runs = new[] { valid, result(plan, 1) with { Assisted = true }, result(plan, 2) with { PlayedSeconds = 12 },
            result(plan, 3) with { Settings = plan.Current with { OffsetMs = 31 } },
            result(plan, 4) with { Settings = plan.Current with { PatternSeed = 124 } } };
        var advice = TrainerGuidedPractice.Advise(plan, runs);
        Assert.That(advice.MatchingRuns, Is.EqualTo(1));
        Assert.That(advice.Next, Is.Null);
    }

    [Test]
    public void MovementComparisonNeedsThreePerConditionAndKeepsConditionsSeparate()
    {
        var plan = TrainerGuidedPractice.Create(new(PatternSeed: 123), TrainerGuidedFocus.MovementComparison);
        var runs = Enumerable.Range(0, 6).Select(i => result(plan with { Step = i }, i) with
            { SpreadMs = i % 2 == 0 ? 30 : 15, Accuracy = i % 2 == 0 ? 92 : 98 }).ToArray();
        var partial = TrainerGuidedPractice.CompareMovement(plan.Id, runs.Take(4));
        Assert.That(partial.Observation, Does.Contain("Complete 3"));
        var comparison = TrainerGuidedPractice.CompareMovement(plan.Id, runs);
        Assert.That(comparison.OriginalRuns, Is.EqualTo(3));
        Assert.That(comparison.CompactRuns, Is.EqualTo(3));
        Assert.That(comparison.OriginalSpreadMs, Is.EqualTo(30));
        Assert.That(comparison.CompactSpreadMs, Is.EqualTo(15));
        Assert.That(comparison.Observation, Does.Contain("does not prove"));
        var contaminated = runs.Select((r, i) => i == 5 ? r with { Settings = r.Settings with { Bpm = 140 } } : r);
        Assert.That(TrainerGuidedPractice.CompareMovement(plan.Id, contaminated).CompactRuns, Is.EqualTo(2));
    }

    [Test]
    public void RepeatedDifficultRunsSuggestOnlyOneEasierDemand()
    {
        var plan = TrainerGuidedPractice.Create(new(AimSpacing: 100, PatternSeed: 123), TrainerGuidedFocus.Spacing);
        var runs = Enumerable.Range(0, 3).Select(i => result(plan, i) with { Accuracy = 85, Hits = 40 }).ToArray();
        var advice = TrainerGuidedPractice.Advise(plan, runs);
        Assert.That(advice.Next?.AimSpacing, Is.EqualTo(85));
        Assert.That(advice.Next! with { AimSpacing = 100 }, Is.EqualTo(plan.Current));
    }

    [Test]
    public void GuidedRandomizerFreezesItsLimitsWithoutMakingTheDrillHarder()
    {
        var selected = new TrainerSettings(TrainerKind.Alternating, RandomizePatterns: true, PatternSeed: 55,
            SkillLimits: new(MaxNps: 2.5, MaxChain: 8));
        var plan = TrainerGuidedPractice.Create(selected, TrainerGuidedFocus.MovementComparison);
        Assert.That(plan.Baseline.RandomizePatterns, Is.True);
        Assert.That(plan.Baseline.SkillLimits, Is.EqualTo(selected.SkillLimits));
        Assert.That(new TrainerSession(plan.Baseline).Notes, Is.EqualTo(new TrainerSession(selected).Notes));
        Assert.That(new TrainerSession(TrainerGuidedPractice.SettingsFor(plan with { Step = 1 })).Notes,
            Is.EqualTo(new TrainerSession(plan.Baseline).Notes));
        Assert.Throws<ArgumentException>(() => TrainerGuidedPractice.Create(selected, TrainerGuidedFocus.GroupLength));
    }

    [Test]
    public void GuidedPlansAndRunMetadataSurviveHistoryReload()
    {
        string directory = Path.Combine(Path.GetTempPath(), "aimmod-guided-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TrainerHistoryStore(Path.Combine(directory, "history.json"));
            var plan = TrainerGuidedPractice.Create(new(PatternSeed: 123), TrainerGuidedFocus.MovementComparison) with { Step = 3 };
            store.SaveGuidedPlan(plan);
            store.Add(result(plan, 0));
            Assert.That(store.LoadGuidedPlan(), Is.EqualTo(plan));
            Assert.That(store.Load().Single().GuidedRun, Is.EqualTo(TrainerGuidedPractice.Stamp(plan)));
            store.SaveGuidedPlan(null);
            Assert.That(store.LoadGuidedPlan(), Is.Null);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static TrainerResult result(TrainerGuidedPlan plan, int order) => new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(order - 10),
        TrainerGuidedPractice.SettingsFor(plan), 100, 100, 98, 0, 0, 0, 10, 0,
        Engine: "osu", Accuracy: 98, PlayedSeconds: plan.Current.Seconds, GuidedRun: TrainerGuidedPractice.Stamp(plan));
}

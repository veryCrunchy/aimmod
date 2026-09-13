using AimMod.Desktop.Trainers;
using NUnit.Framework;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Osu.Objects;

namespace AimMod.Desktop.Tests;

public sealed class NativeTrainerResultTests
{
    private static JudgementResult[] SuccessfulJudgements(IBeatmap map)
    {
        foreach (var obj in map.HitObjects) obj.ApplyDefaults(map.ControlPointInfo, map.Difficulty);
        return map.HitObjects.SelectMany(flatten).Select(obj => new JudgementResult(obj, obj.CreateJudgement())
            { Type = obj.CreateJudgement().MaxResult }).ToArray();
    }

    private static IEnumerable<HitObject> flatten(HitObject obj) =>
        new[] { obj }.Concat(obj.NestedHitObjects.SelectMany(flatten));

    [Test]
    public void SuccessfulSliderWarmupAdvancesAndReplacesPreviousAccuracy()
    {
        var plan = TrainerWarmup.Create(2, new(), [], [], DateTimeOffset.UtcNow);
        Assert.That(plan.Record(TrainerWarmupTests.Result(plan.CurrentSettings(), 86.7)), Is.True);
        var settings = plan.CurrentSettings();
        var map = TrainerBeatmap.Create(settings);
        var judgements = SuccessfulJudgements(map);
        Assert.That(judgements.Count(j => j.IsHit && j.HitObject is SliderEndCircle), Is.GreaterThan(0));
        // The old classification counted slider tails as additional hit circles.
        Assert.That(judgements.Count(j => j.IsHit && j.HitObject is HitCircle), Is.GreaterThan(map.HitObjects.Count));
        var result = NativeTrainerPlayer.CreateResult(settings, map, judgements, 98);
        Assert.That(result.Hits, Is.EqualTo(result.Notes));
        Assert.That(result.Accuracy, Is.EqualTo(98));
        Assert.That(plan.Record(result), Is.True);
        Assert.That(plan.Step, Is.EqualTo(2));
        Assert.That(plan.Results[^1].Accuracy, Is.EqualTo(98));
        Assert.That(plan.Results[0].Accuracy, Is.EqualTo(86.7));
    }

    [TestCase(TrainerKind.Steady)] [TestCase(TrainerKind.Alternating)] [TestCase(TrainerKind.Bursts)]
    [TestCase(TrainerKind.Aim)] [TestCase(TrainerKind.Rhythm)] [TestCase(TrainerKind.Reading)] [TestCase(TrainerKind.Spinner)]
    public void EveryOsuTrainerCountsEachObjectOnceWithSlidersAndSpinners(TrainerKind kind)
    {
        var settings = new TrainerSettings(kind, Seconds: 60, Sliders: TrainerSliderStyle.BackAndForth,
            Spinners: TrainerSpinnerFrequency.Occasional);
        var map = TrainerBeatmap.Create(settings);
        var result = NativeTrainerPlayer.CreateResult(settings, map, SuccessfulJudgements(map), 98.25);
        Assert.That(result.Hits, Is.EqualTo(result.Notes));
        Assert.That(result.Within25, Is.EqualTo(result.TapTargets));
        Assert.That(result.Accuracy, Is.EqualTo(98.25));
        if (kind == TrainerKind.Spinner) Assert.That(result.SpreadMs, Is.Null);
    }

    [Test]
    public void ReadingCountsSliderHeadsOnceIncludingRepeatSliders()
    {
        var settings = new TrainerSettings(Kind: TrainerKind.Reading, Seconds: 30) with
            { Sliders = TrainerSliderStyle.BackAndForth };
        var map = TrainerBeatmap.Create(settings);
        var judgements = SuccessfulJudgements(map);
        Assert.That(judgements.Any(j => j.IsHit && j.HitObject is SliderRepeat), Is.True);
        var result = NativeTrainerPlayer.CreateResult(settings, map, judgements, 97.25);
        Assert.That(result.Hits, Is.EqualTo(result.Notes));
        Assert.That(result.ReadingWindows!.Sum(w => w.Circles), Is.EqualTo(result.Notes));
        Assert.That(result.Accuracy, Is.EqualTo(97.25));
    }
}

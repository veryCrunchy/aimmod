using AimMod.Desktop.Trainers;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

public class TrainerStarTargetTests
{
    [TestCase("aurora-circuit", 2.8, 4.9)]
    [TestCase("sidechain-city", 4.5, 4.6)]
    public void AdaptiveStreamTargetsCanStart(string song, double minimum, double maximum)
    {
        foreach (int seconds in new[] {30, 60, 120})
        foreach (int seed in new[] {1, 45, 123})
        {
            var settings = TrainerAdaptiveDifficulty.Apply(new(TrainerKind.Alternating, Music: song,
                Seconds: seconds, PatternSeed: seed, MinimumStars: minimum, MaximumStars: maximum, AdaptiveDifficulty: true), [], [], DateTimeOffset.UtcNow);
            var plan = TrainerStarTarget.Fit(settings);
            Assert.That(plan.InRange, Is.True, $"{song}/{seconds}s/seed {seed}: {plan.Stars:0.000}");
            Assert.That(TrainerStarTarget.RequireTarget(plan), Is.SameAs(plan));
            Assert.That(plan.Stars, Is.EqualTo(TrainerStarTarget.Measure(plan.Map, plan.Settings)).Within(.00001));
            Assert.That(plan.Settings.Kind, Is.EqualTo(TrainerKind.Alternating));
            Assert.That(plan.Settings.SkillLimits, Is.EqualTo(settings.SkillLimits));
            Assert.That(plan.Settings.PathStyle, Is.Not.EqualTo(TrainerPathStyle.Random));
            var arrangement = TrainerSongArrangement.For(plan.Settings)!;
            Assert.That(plan.Map.HitObjects.All(o => arrangement.Events.Any(e => Math.Abs(arrangement.TimeAt(e.Beat)-o.StartTime)<.001)), Is.True);
            var demand = TrainerSkillProfile.Measure(plan.Map);
            Assert.That(demand.PeakNps, Is.LessThanOrEqualTo(settings.SkillLimits!.MaxNps+.001));
            Assert.That(demand.AimVelocity, Is.LessThanOrEqualTo(settings.SkillLimits.MaxAimVelocity+.01));
        }
    }

    [Test]
    public void FitsAReachableTargetUsingActualOsuDifficultyWithoutChangingManualRhythm()
    {
        var settings = new TrainerSettings(TrainerKind.Aim, Seconds:30, AimSpacing:70, PatternSeed:45, NoteSpeed:TrainerNoteSpeed.TwoPerBeat);
        var target = TrainerStarTarget.Measure(TrainerBeatmap.Create(settings with { AimSpacing=140 }),settings);
        var plan = TrainerStarTarget.Fit(settings with { MinimumStars=target-.02, MaximumStars=target+.02 });
        Assert.That(plan.InRange,Is.True);
        Assert.That(plan.Stars,Is.EqualTo(TrainerStarTarget.Measure(plan.Map,plan.Settings)).Within(.00001));
        Assert.That(plan.Settings.NoteSpeed,Is.EqualTo(settings.NoteSpeed));
        Assert.That(plan.Settings.Pattern,Is.EqualTo(settings.Pattern));
        Assert.That(plan.Settings.Keys,Is.EqualTo(settings.Keys));
        Assert.That(plan.Settings.MeasuredStars,Is.EqualTo(plan.Stars));
    }

    [Test]
    public void UnreachableTargetNeverBypassesAdaptiveSkillCaps()
    {
        var settings = new TrainerSettings(TrainerKind.Aim, Music:"signal-bloom", MinimumStars:9, MaximumStars:10, AdaptiveDifficulty:true, SkillLimits:new());
        var plan = TrainerStarTarget.Fit(settings);
        Assert.That(plan.InRange,Is.False);
        foreach(var obj in plan.Map.HitObjects)obj.ApplyDefaults(plan.Map.ControlPointInfo,plan.Map.Difficulty);
        var demand=TrainerSkillProfile.Measure(plan.Map);
        Assert.That(demand.PeakNps,Is.LessThanOrEqualTo(settings.SkillLimits!.MaxNps+.001));
        Assert.That(demand.AimVelocity,Is.LessThanOrEqualTo(settings.SkillLimits.MaxAimVelocity+.01));
        var error = Assert.Throws<InvalidOperationException>(() => TrainerStarTarget.RequireTarget(plan));
        Assert.That(error!.Message,Does.Contain("Practice has not started").And.Contain("9.0–10.0").And.Contain("Closest result"));
    }

    [TestCase(TrainerKind.Aim)]
    public void SelectedFourStarRangeDoesNotUseBeginnerDefaults(TrainerKind kind)
    {
        var settings = TrainerAdaptiveDifficulty.Apply(new(kind, Music:"night-drive", MinimumStars:4.1, MaximumStars:5.1, AdaptiveDifficulty:true),[],[],DateTimeOffset.UtcNow);
        Assert.That(settings.SkillLimits!.MaxNps, Is.GreaterThan(6));
        var plan = TrainerStarTarget.Fit(settings);
        TestContext.WriteLine($"{kind}: {plan.Stars:0.00} stars, {plan.Settings.Bpm} BPM, {plan.Settings.NoteSpeed}, {plan.Settings.AimSpacing}");
        Assert.That(plan.InRange, Is.True);
        Assert.That(TrainerStarTarget.RequireTarget(plan), Is.SameAs(plan));
        Assert.That(plan.Stars, Is.EqualTo(TrainerStarTarget.Measure(plan.Map, plan.Settings)).Within(.00001));
        Assert.That(plan.Settings.SkillLimits, Is.EqualTo(settings.SkillLimits));
        Assert.That(plan.Settings.Music, Is.EqualTo(settings.Music));
    }

    [Test]
    public void EasyReplaysDoNotVetoAnExplicitTargetButStillGuideUntargetedPractice()
    {
        var evidence = Enumerable.Range(0,3).Select(_ => new TrainerSkillEvidence(Guid.NewGuid(),new TrainerDemand(3,100,250,8)));
        var settings = TrainerAdaptiveDifficulty.Apply(new(TrainerKind.Aim, MinimumStars:4.1,MaximumStars:5.1,AdaptiveDifficulty:true),[],evidence,DateTimeOffset.UtcNow);
        Assert.That(settings.SkillLimits!.MaxNps,Is.GreaterThanOrEqualTo(TrainerStarTarget.StartingLimits(settings)!.MaxNps));
        var automatic = TrainerAdaptiveDifficulty.Apply(settings with {MinimumStars=null,MaximumStars=null},[],evidence,DateTimeOffset.UtcNow);
        Assert.That(automatic.SkillLimits!.MaxNps,Is.EqualTo(2.55).Within(.001));
    }

    private static IEnumerable<string> songs => TrainerMusicCatalog.Songs.Keys;

    [TestCaseSource(nameof(songs))]
    public void SteadyTargetWithSuccessfulEasyHistoryReachesFourStars(string song)
    {
        var easy = new TrainerSettings(TrainerKind.Steady, AdaptiveDifficulty:true);
        var history = Enumerable.Range(0, 5).Select(i => TrainerWarmupTests.Result(easy,96) with {
            CompletedAt=DateTimeOffset.UtcNow.AddMinutes(-i-1), Demand=new(2,45,95,12) }).ToArray();
        var settings = TrainerAdaptiveDifficulty.Apply(easy with {Music=song,MinimumStars=4.1,MaximumStars=5.1},history,[],DateTimeOffset.UtcNow);
        var plan = TrainerStarTarget.Fit(settings);
        TestContext.WriteLine($"{song}: {plan.Stars:0.00} stars, {plan.Settings.Bpm} BPM, {plan.Settings.PathStyle}");
        Assert.That(plan.InRange, Is.True);
        Assert.That(TrainerStarTarget.RequireTarget(plan),Is.SameAs(plan));
        Assert.That(plan.Stars, Is.InRange(4.1-TrainerStarTarget.Tolerance,5.1+TrainerStarTarget.Tolerance));
        Assert.That(plan.Settings.Kind, Is.EqualTo(TrainerKind.Steady));
        Assert.That(plan.Settings.Pattern, Is.EqualTo(settings.Pattern));
        Assert.That(plan.Settings.PathStyle, Is.Not.EqualTo(TrainerPathStyle.Random));
        var arrangement=TrainerSongArrangement.For(plan.Settings)!;
        Assert.That(plan.Map.HitObjects.All(o=>arrangement.Events.Any(e=>Math.Abs(arrangement.TimeAt(e.Beat)-o.StartTime)<.001)),Is.True);
    }

    [Test]
    public void SuccessfulEasyPracticeNeverReducesDefaultOrReplayAbility()
    {
        var settings = new TrainerSettings(AdaptiveDifficulty:true);
        var history = Enumerable.Range(0,5).Select(_=>TrainerWarmupTests.Result(settings,97) with {Demand=new(2,45,95,12)}).ToArray();
        var baseline=TrainerSkillProfile.Build(settings.Kind,[],[],DateTimeOffset.UtcNow);
        var withHistory=TrainerSkillProfile.Build(settings.Kind,history,[],DateTimeOffset.UtcNow);
        Assert.That(withHistory.MaxNps,Is.GreaterThanOrEqualTo(baseline.MaxNps));
        Assert.That(withHistory.MaxJumpDistance,Is.GreaterThanOrEqualTo(baseline.MaxJumpDistance));
        Assert.That(withHistory.MaxAimVelocity,Is.GreaterThanOrEqualTo(baseline.MaxAimVelocity));
        var replay=Enumerable.Range(0,3).Select(_=>new TrainerSkillEvidence(Guid.NewGuid(),new(8,240,1000,32))).ToArray();
        var demonstrated=TrainerSkillProfile.Build(settings.Kind,[],replay,DateTimeOffset.UtcNow);
        Assert.That(TrainerSkillProfile.Build(settings.Kind,history,replay,DateTimeOffset.UtcNow).MaxNps,Is.GreaterThanOrEqualTo(demonstrated.MaxNps));
    }

    [Test]
    public void FailedPreparationRejectsBeforeLoadingAnyAudio()
    {
        var settings = new TrainerSettings(TrainerKind.Aim, MinimumStars:9, MaximumStars:10, NoteSpeed:TrainerNoteSpeed.OnePerBeat);
        Assert.Throws<InvalidOperationException>(() => TrainerBeatmap.Prepare(settings,null!,1));
        var source = TrainerBeatmap.Create(settings);
        Assert.Throws<InvalidOperationException>(() => TrainerBeatmap.FromSong(settings with {Music="song"},source,[],".ogg",null!,1));
    }

    [Test]
    public void DisabledTargetPreservesWarmupAndGuidedSessionIdentity()
    {
        var settings = new TrainerSettings(AdaptiveDifficulty:true, SkillLimits:new());
        Assert.That(TrainerStarTarget.Fit(settings).Settings,Is.EqualTo(settings));
    }

    [Test]
    public void RejectsInvalidRangesAndKeepsMeasuredRatingOutOfComparisonIdentity()
    {
        foreach(var range in new[] {(double.NaN,4d),(5d,4d),(-1d,3d),(3d,11d)})
            Assert.Throws<ArgumentOutOfRangeException>(() => new TrainerSettings(MinimumStars:range.Item1,MaximumStars:range.Item2).Validate());
        var settings=new TrainerSettings(MinimumStars:3,MaximumStars:4);
        Assert.That(settings.ComparisonKey(),Is.EqualTo((settings with {MeasuredStars=3.7}).ComparisonKey()));
    }
}

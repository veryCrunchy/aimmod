using AimMod.Desktop.Trainers;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

public class TrainerDifficultyParametersTests
{
    private static TrainerSettings resolve(TrainerKind kind, double stars) => TrainerAdaptiveDifficulty.Apply(
        new(kind, MinimumStars:stars, MaximumStars:stars+.5, AdaptiveDifficulty:true), [], [], DateTimeOffset.UtcNow);

    [TestCase(TrainerKind.Steady)] [TestCase(TrainerKind.Alternating)] [TestCase(TrainerKind.Bursts)]
    [TestCase(TrainerKind.Rhythm)] [TestCase(TrainerKind.Aim)] [TestCase(TrainerKind.Reading)]
    public void DifficultyScalesVisibilityAndTimingAndReachesTheActualBeatmap(TrainerKind kind)
    {
        var easy = resolve(kind,1);
        var harder = resolve(kind,6);
        Assert.That(harder.ApproachRate, Is.GreaterThan(easy.ApproachRate));
        Assert.That(harder.OverallDifficulty, Is.GreaterThan(easy.OverallDifficulty));
        var map = TrainerBeatmap.Create(harder);
        Assert.That(map.Difficulty.ApproachRate,Is.EqualTo(harder.ApproachRate));
        Assert.That(map.Difficulty.CircleSize,Is.EqualTo(harder.CircleSize));
        Assert.That(map.Difficulty.OverallDifficulty,Is.EqualTo(harder.OverallDifficulty));
        Assert.That(easy.CircleSize,Is.EqualTo(3));
    }

    [Test]
    public void ReadingHasMorePreviewAndAimUsesPrecisionWithoutShrinkingTappingTargets()
    {
        var reading = resolve(TrainerKind.Reading,6);
        var aim = resolve(TrainerKind.Aim,6);
        var tapping = resolve(TrainerKind.Steady,6);
        Assert.That(reading.ApproachRate,Is.LessThan(aim.ApproachRate));
        Assert.That(reading.OverallDifficulty,Is.LessThan(tapping.OverallDifficulty));
        Assert.That(aim.CircleSize,Is.EqualTo(5));
        Assert.That(tapping.CircleSize,Is.EqualTo(4));
    }

    [Test]
    public void ChangingStarRangeDoesNotInheritAnUnrelatedTimingWindow()
    {
        var easy = resolve(TrainerKind.Steady,1);
        var history = Enumerable.Range(0,3).Select(_=>TrainerWarmupTests.Result(easy,98)).ToArray();
        var hard = easy with {MinimumStars=6,MaximumStars=6.5};
        var next = TrainerAdaptiveDifficulty.Apply(hard,history,[],DateTimeOffset.UtcNow);
        Assert.That(next.OverallDifficulty,Is.EqualTo(resolve(TrainerKind.Steady,6).OverallDifficulty));
        Assert.That(next.OverallDifficulty,Is.GreaterThan(easy.OverallDifficulty));
        var wronglyLabelled = history.Select(r=>r with {Settings=r.Settings with {MinimumStars=6,MaximumStars=6.5,MeasuredStars=.9}});
        Assert.That(TrainerAdaptiveDifficulty.Apply(hard,wronglyLabelled,[],DateTimeOffset.UtcNow).OverallDifficulty,Is.EqualTo(next.OverallDifficulty));
    }

    [Test]
    public void ManualAndReactionSettingsAreNotOverwrittenWithOsuDifficultyDefaults()
    {
        var manual = new TrainerSettings(TrainerKind.Aim,ApproachRate:4,CircleSize:6,OverallDifficulty:3,
            MinimumStars:5,MaximumStars:6);
        Assert.That(TrainerAdaptiveDifficulty.Apply(manual,[],[],DateTimeOffset.UtcNow),Is.EqualTo(manual));
        var reaction = manual with { Kind=TrainerKind.Reaction, AdaptiveDifficulty=true };
        var result = TrainerAdaptiveDifficulty.Apply(reaction,[],[],DateTimeOffset.UtcNow);
        Assert.That(result.ApproachRate,Is.EqualTo(reaction.ApproachRate));
        Assert.That(result.CircleSize,Is.EqualTo(reaction.CircleSize));
        Assert.That(result.ReactionWindowMs,Is.EqualTo(reaction.ReactionWindowMs));
    }

    [Test]
    public void DenseTappingGetsFasterApproachWhileReadingKeepsItsPreview()
    {
        var limits = new TrainerSkillLimits(MaxNps:12);
        var sparse = new TrainerSettings(TrainerKind.Steady,Bpm:90,NoteSpeed:TrainerNoteSpeed.OnePerBeat);
        var dense = sparse with {Bpm=180,NoteSpeed=TrainerNoteSpeed.FourPerBeat};
        // Fix the challenge band to isolate the note-density adjustment.
        sparse = sparse with {MinimumStars=2,MaximumStars=3}; dense = dense with {MinimumStars=2,MaximumStars=3};
        Assert.That(TrainerDifficultyParameters.For(dense,limits).ApproachRate,Is.GreaterThan(TrainerDifficultyParameters.For(sparse,limits).ApproachRate));
        Assert.That(TrainerDifficultyParameters.For(dense with {Kind=TrainerKind.Reading},limits).ApproachRate,Is.LessThan(9));
    }
}

using AimMod.Desktop.Trainers;
using NUnit.Framework;
using osu.Game.Rulesets.Osu.Objects;
using osuTK;

namespace AimMod.Desktop.Tests;

public class TrainerAdvancedObjectsTests
{
    [Test]
    public void CurvedPathsRemainInsideThePlayfieldAndRespectTravelLimits()
    {
        foreach (var shape in Enum.GetValues<TrainerSliderShape>())
        foreach (var start in new[] { new Vector2(64,64), new Vector2(448,320), new Vector2(256,192) })
        {
            var settings = new TrainerSettings(SliderShape: shape, PatternSeed: 7);
            var path = TrainerAdvancedObjects.Slider(settings, start, 1, 120);
            Assert.That(path.Distance, Is.InRange(1, 120.01), shape.ToString());
            for (int i = 0; i <= 100; i++)
            {
                var point = start + path.PositionAt(i / 100d);
                Assert.That(point.X, Is.InRange(31.99,480.01));
                Assert.That(point.Y, Is.InRange(31.99,352.01));
            }
        }
    }

    [Test]
    public void SliderShapesActuallyDifferAndPreserveMusicalDuration()
    {
        var settings = new TrainerSettings(TrainerKind.Aim, Music: "copper-sky", Seconds:60, Sliders: TrainerSliderStyle.BackAndForth, SliderBeats:2);
        var middles = new List<Vector2>();
        foreach (var shape in new[] { TrainerSliderShape.Straight, TrainerSliderShape.Arc, TrainerSliderShape.SCurve, TrainerSliderShape.Angular })
        {
            var map = TrainerBeatmap.Create(settings with { SliderShape = shape });
            foreach (var obj in map.HitObjects) obj.ApplyDefaults(map.ControlPointInfo, map.Difficulty);
            var slider = map.HitObjects.OfType<Slider>().First();
            middles.Add(slider.Path.PositionAt(.25));
            Assert.That(slider.RepeatCount, Is.EqualTo(3));
            Assert.That(slider.Duration, Is.InRange(275,1000.01));
            Assert.That(map.HitObjects.Where(o => o.StartTime > slider.StartTime).First().StartTime, Is.GreaterThan(slider.EndTime));
        }
        Assert.That(middles.Distinct().Count(), Is.EqualTo(4));
    }

    [TestCase(TrainerSpinnerPattern.BuildUp)] [TestCase(TrainerSpinnerPattern.MixedLengths)]
    public void SpinDurationsVaryWithRecoveryAndStayWithinSelectedLength(TrainerSpinnerPattern pattern)
    {
        var settings = new TrainerSettings(TrainerKind.Spinner, Seconds:60, SpinnerSeconds:6, SpinnerPattern:pattern);
        var spins = TrainerBeatmap.Create(settings).HitObjects.OfType<Spinner>().ToArray();
        Assert.That(spins.Select(s => s.Duration).Distinct().Count(), Is.GreaterThan(1));
        Assert.That(spins.All(s => s.Duration is >=2000 and <=6000), Is.True);
        for(int i=1;i<spins.Length;i++) Assert.That(spins[i].StartTime-spins[i-1].EndTime, Is.GreaterThanOrEqualTo(1500));
    }

    [TestCase(TrainerAimStyle.Triangles)] [TestCase(TrainerAimStyle.Boxes)] [TestCase(TrainerAimStyle.CrossScreen)]
    public void DeliberateJumpPatternsAreRepeatableAndDistinct(TrainerAimStyle style)
    {
        var settings = new TrainerSettings(TrainerKind.Aim, AimStyle:style, PatternSeed:12);
        var points = TrainerAimPatterns.Create(settings,24);
        Assert.That(points, Is.EqualTo(TrainerAimPatterns.Create(settings,24)));
        Assert.That(points, Is.Not.EqualTo(TrainerAimPatterns.Create(settings with { AimStyle=TrainerAimStyle.Balanced },24)));
        Assert.That(points.All(p => p.X is >=32 and <=480 && p.Y is >=32 and <=352),Is.True);
    }
}

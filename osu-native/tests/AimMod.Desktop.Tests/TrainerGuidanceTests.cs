using AimMod.Desktop.Trainers;
using NUnit.Framework;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.ControlPoints;
using osu.Game.Rulesets.Osu.Objects;

namespace AimMod.Desktop.Tests;

public class TrainerGuidanceTests
{
    [TestCase(2)] [TestCase(4)] [TestCase(6)]
    public void SpinnerDrillsHaveRecoveryAndFitTheSong(int seconds)
    {
        var settings = new TrainerSettings(TrainerKind.Spinner, Seconds: 30, SpinnerSeconds: seconds);
        var map = TrainerBeatmap.Create(settings);
        Assert.That(map.HitObjects, Is.Not.Empty);
        Assert.That(map.HitObjects.All(o => o is Spinner), Is.True);
        foreach (var spin in map.HitObjects.Cast<Spinner>())
            Assert.That(spin.Duration, Is.EqualTo(seconds * 1000));
        Assert.That(map.HitObjects.Zip(map.HitObjects.Skip(1), (a, b) => b.StartTime - TrainerSpinners.End(a)).All(g => g >= 1500), Is.True);
        Assert.That(map.HitObjects.Max(TrainerSpinners.End), Is.LessThanOrEqualTo(new TrainerSession(settings).EndMs));
    }

    [Test]
    public void OccasionalSpinnersPreserveSlidersAndLeaveAReadableGap()
    {
        foreach (var kind in Enum.GetValues<TrainerKind>().Where(k => k is not (TrainerKind.Reaction or TrainerKind.Spinner)))
        {
            var s = new TrainerSettings(kind, Seconds: 60, Sliders: TrainerSliderStyle.Mixed,
                Spinners: TrainerSpinnerFrequency.Occasional, PatternSeed: 21);
            var map = TrainerBeatmap.Create(s);
            foreach (var obj in map.HitObjects) obj.ApplyDefaults(map.ControlPointInfo, map.Difficulty);
            Assert.That(map.HitObjects.OfType<Spinner>().Count(), Is.InRange(1, 4), kind.ToString());
            Assert.That(map.HitObjects.OfType<Slider>(), Is.Not.Empty, kind.ToString());
            for (int i = 1; i < map.HitObjects.Count; i++)
            {
                var a = map.HitObjects[i - 1]; var b = map.HitObjects[i];
                Assert.That(b.StartTime, Is.GreaterThan(TrainerSpinners.End(a)), kind.ToString());
                if (a is Spinner || b is Spinner)
                    Assert.That(b.StartTime - TrainerSpinners.End(a), Is.GreaterThanOrEqualTo(1000), kind.ToString());
            }
        }
    }

    [Test]
    public void SpinnerTimingAlsoWorksWithAnInstalledSongAndTempoChange()
    {
        var source = new Beatmap<OsuHitObject>();
        source.ControlPointInfo.Add(0, new TimingControlPoint { BeatLength = 500 });
        source.ControlPointInfo.Add(17000, new TimingControlPoint { BeatLength = 400 });
        source.HitObjects.Add(new HitCircle { StartTime = 1000 });
        source.HitObjects.Add(new HitCircle { StartTime = 61000 });
        var settings = new TrainerSettings(TrainerKind.Spinner, Music: "song", SongStartSeconds: 10);
        var notes = TrainerBeatmap.SongNotes(settings, source);
        var map = TrainerBeatmap.Create(settings, notes, source.ControlPointInfo);
        Assert.That(map.HitObjects, Is.Not.Empty);
        Assert.That(map.HitObjects.All(s => notes.Any(n => n.TimeMs == s.StartTime)), Is.True);
        Assert.That(map.HitObjects.Max(TrainerSpinners.End), Is.LessThanOrEqualTo(41000));
    }

    [Test]
    public void GuidanceFollowsHoldsAndFadesForIndependentFinish()
    {
        var settings = new TrainerSettings(TrainerKind.Reading, GuidedCues: true);
        OsuHitObject[] objects = [new HitCircle { StartTime = 1000 }, new Spinner { StartTime = 4000, Duration = 3000 }, new HitCircle { StartTime = 16000 }];
        Assert.That(TrainerPracticeGuide.At(settings, objects, 0).Stage, Is.EqualTo("GET READY"));
        Assert.That(TrainerPracticeGuide.At(settings, objects, 4500).Stage, Is.EqualTo("SPIN"));
        Assert.That(TrainerPracticeGuide.At(settings, objects, 13000).Stage, Is.EqualTo("YOUR TURN"));
        Assert.That(TrainerPracticeGuide.At(settings, objects, 15500).Visible, Is.False);
        Assert.That(TrainerPracticeGuide.At(settings with { GuidedCues = false }, objects, 4500).Visible, Is.False);
        var map = TrainerBeatmap.Create(settings with { Sliders = TrainerSliderStyle.BackAndForth });
        foreach (var obj in map.HitObjects) obj.ApplyDefaults(map.ControlPointInfo, map.Difficulty);
        var slider = map.HitObjects.OfType<Slider>().First();
        Assert.That(TrainerPracticeGuide.At(settings, map.HitObjects, slider.StartTime + 50).Stage, Is.EqualTo("FOLLOW THE REPEAT"));
    }

    [Test]
    public void GuidanceAndSpinnerSettingsDoNotMixProgressCohorts()
    {
        var original = new TrainerSettings();
        Assert.That(original.ComparisonKey(), Is.Not.EqualTo((original with { GuidedCues = true }).ComparisonKey()));
        Assert.That(original.ComparisonKey(), Is.EqualTo((original with { SpinnerSeconds = 6 }).ComparisonKey()));
        Assert.That(original.ComparisonKey(), Is.Not.EqualTo((original with { Spinners = TrainerSpinnerFrequency.Occasional }).ComparisonKey()));
    }

    [Test]
    public void SpinnerMetricsWeightTimeAndIgnoreSmallDirectionJitter()
    {
        var metrics = new SpinnerPracticeMetrics(); metrics.Begin();
        for (int i = 0; i < 100; i++) metrics.Sample(10, 300, true, 18);
        for (int i = 0; i < 10; i++) { metrics.Sample(10, 300, true, -.1); metrics.Sample(10, 300, true, .1); }
        for (int i = 0; i < 10; i++) metrics.Sample(10, 300, true, -18);
        metrics.Sample(100, 0, false, 0);
        var r = metrics.Result();
        Assert.That(r.MeanRpm, Is.EqualTo(300).Within(.01));
        Assert.That(r.DirectionChanges, Is.EqualTo(1));
        Assert.That(r.HeldPercent, Is.EqualTo(1300.0 / 1400 * 100).Within(.01));
        Assert.That(r.SpeedVariationPercent, Is.EqualTo(0).Within(.01));
    }
}

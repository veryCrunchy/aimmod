using AimMod.Desktop.Trainers;
using NUnit.Framework;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.ControlPoints;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Types;
using osu.Game.Rulesets.Osu.Objects;
using osuTK;

namespace AimMod.Desktop.Tests;

public class TrainerGenerationInvariantTests
{
    private static readonly TrainerKind[] kinds = Enum.GetValues<TrainerKind>().Where(k => k != TrainerKind.Reaction).ToArray();

    private static TrainerSettings randomSettings(Random random)
    {
        T pick<T>(IReadOnlyList<T> values) => values[random.Next(values.Count)];
        var kind = pick(kinds);
        bool song = random.Next(3) == 0;
        int[] tempos = song ? TrainerMusicCatalog.Tempos : [60, 90, 120, 150, 180, 210, 240];
        bool randomize = random.Next(3) == 0, adaptive = random.Next(3) == 0;
        TrainerSkillLimits? limits = randomize || adaptive
            ? new(MaxNps: 1 + random.NextDouble() * 15, MaxJumpDistance: 40 + random.NextDouble() * 310, MaxAimVelocity: 100 + random.NextDouble() * 1700,
                MaxChain: random.Next(3, 257), MaxBurst: pick(new[] { 3, 5, 7, 9 }), Complexity: random.Next(3), MaxApproachRate: random.Next(3, 11))
            : null;
        return new TrainerSettings(kind, pick(tempos), pick(new[] { 15, 30 }), Music: song ? pick(TrainerMusicCatalog.Songs.Keys.ToArray()) : "cues",
            AimStyle: pick(Enum.GetValues<TrainerAimStyle>()), AimSpacing: pick(new[] { 70, 85, 100, 120, 140 }), CircleSize: random.Next(3, 7),
            PatternSeed: random.Next(int.MinValue, int.MaxValue), Pattern: pick(TrainerPatterns.Choices(kind).Values.ToArray()),
            NoteSpeed: pick(Enum.GetValues<TrainerNoteSpeed>()), Sliders: pick(Enum.GetValues<TrainerSliderStyle>()), SliderBeats: random.Next(1, 5),
            PathStyle: pick(Enum.GetValues<TrainerPathStyle>()), RandomizePatterns: randomize, ApproachRate: random.Next(3, 11), SkillLimits: limits,
            MovementScale: pick(new[] { .4, .7, 1 }), ReadingGroupSize: pick(new[] { 4, 6, 8, 12 }), ReadingHidden: random.Next(2) == 0,
            ReadingComplexity: random.Next(3), Spinners: pick(Enum.GetValues<TrainerSpinnerFrequency>()), SpinnerSeconds: pick(new[] { 2, 4, 6 }),
            AdaptiveDifficulty: adaptive, SliderShape: pick(Enum.GetValues<TrainerSliderShape>()), SpinnerPattern: pick(Enum.GetValues<TrainerSpinnerPattern>()));
    }

    private static void assertPlayable(Beatmap<OsuHitObject> map, string label)
    {
        foreach (var obj in map.HitObjects) obj.ApplyDefaults(map.ControlPointInfo, map.Difficulty);
        if (map.HitObjects.Count == 0)
        {
            Assert.Throws<InvalidOperationException>(() => TrainerBeatmap.RequirePlayable(map), label);
            return;
        }
        Assert.DoesNotThrow(() => TrainerBeatmap.RequirePlayable(map), label);
        static bool inside(Vector2 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && p.X is >= 0 and <= 512 && p.Y is >= 0 and <= 384;
        OsuHitObject? previous = null;
        foreach (var obj in map.HitObjects)
        {
            Assert.That(double.IsFinite(obj.StartTime) && obj.StartTime >= 0, Is.True, $"{label}: start {obj.StartTime}");
            Assert.That(inside(obj.Position), Is.True, $"{label}: {obj.GetType().Name} at {obj.StartTime} is outside the playfield: {obj.Position}");
            if (obj is Slider slider)
            {
                Assert.That(double.IsFinite(slider.Duration) && slider.Duration > 0, Is.True, $"{label}: slider duration {slider.Duration}");
                Assert.That(double.IsFinite(slider.Velocity) && slider.Velocity > 0, Is.True, $"{label}: slider velocity {slider.Velocity}");
                var points = new List<Vector2>();
                slider.Path.GetPathToProgress(points, 0, 1);
                Assert.That(points.All(p => inside(slider.Position + p)), Is.True, $"{label}: slider at {slider.StartTime} leaves the playfield");
            }
            if (obj is Spinner spinner) Assert.That(double.IsFinite(spinner.Duration) && spinner.Duration > 0, Is.True, label);
            if (previous is not null)
            {
                Assert.That(obj.StartTime, Is.GreaterThan(previous.StartTime), $"{label}: two objects start at {obj.StartTime}");
                if (previous is IHasDuration held)
                    Assert.That(obj.StartTime, Is.GreaterThan(held.EndTime), $"{label}: object at {obj.StartTime} starts during a hold ending {held.EndTime}");
            }
            previous = obj;
        }
    }

    [Test]
    public void GeneratedDrillsAreAlwaysPlayable()
    {
        var random = new Random(20260930);
        int checkedMaps = 0;
        for (int run = 0; run < 1500; run++)
        {
            var settings = randomSettings(random);
            Beatmap<OsuHitObject> map;
            try { map = TrainerBeatmap.Create(settings); }
            catch (ArgumentOutOfRangeException) { continue; }
            assertPlayable(map, $"run {run}: {settings}");
            checkedMaps++;
        }
        Assert.That(checkedMaps, Is.GreaterThan(1200));
    }

    [Test]
    public void GeneratedSongSectionsAreAlwaysPlayable()
    {
        var source = new Beatmap<OsuHitObject>();
        source.ControlPointInfo.Add(0, new TimingControlPoint { BeatLength = 500 });
        source.ControlPointInfo.Add(12000, new TimingControlPoint { BeatLength = 333.333 });
        for (double t = 1000; t < 60000; t += 250)
            source.HitObjects.Add(t % 2000 == 0 ? new Slider { StartTime = t, Path = new SliderPath(PathType.LINEAR, [Vector2.Zero, new Vector2(80, 0)]) } : new HitCircle { StartTime = t });
        foreach (var obj in source.HitObjects) obj.ApplyDefaults(source.ControlPointInfo, new BeatmapDifficulty());
        var random = new Random(4242);
        int checkedMaps = 0;
        for (int run = 0; run < 400; run++)
        {
            var settings = randomSettings(random) with { Music = "song", Bpm = 120 };
            IReadOnlyList<TrainerNote> notes;
            try { notes = TrainerBeatmap.SongNotes(settings, source); }
            catch (ArgumentOutOfRangeException) { continue; }
            if (notes.Count < 4) continue;
            assertPlayable(TrainerBeatmap.Create(settings, notes, source.ControlPointInfo), $"song run {run}: {settings}");
            checkedMaps++;
        }
        Assert.That(checkedMaps, Is.GreaterThan(300));
    }
}

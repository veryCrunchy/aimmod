using AimMod.Desktop.Trainers;
using NUnit.Framework;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.ControlPoints;

namespace AimMod.Desktop.Tests;

public class TrainerMusicalPhrasesTests
{
    [TestCase(TrainerKind.Alternating)] [TestCase(TrainerKind.Bursts)]
    public void FasterPlayablePhraseIsNotReplacedByALongerSlowOne(TrainerKind kind)
    {
        var music = new TrainerSongArrangement(2, "fixture", 120, "", 0, 16, [new(0,16,1,false)],
            new[] {0d,.25,.5,1,2,3,4,5,6,7}.Select(b=>new TrainerMusicEvent(b,1,60)).ToArray(), []);
        var settings = new TrainerSettings(kind, AdaptiveDifficulty:true, NoteSpeed:TrainerNoteSpeed.FourPerBeat,
            SkillLimits:new(MaxNps:8,MaxChain:8,MaxBurst:7));
        var notes = TrainerMusicalPhrases.Select(music, settings, 0, 8000);
        Assert.That(notes.Take(3).Select(n=>n.TimeMs), Is.EqualTo(new[] {0d,125,250}));
        var slower = TrainerMusicalPhrases.Select(music, settings with {NoteSpeed=TrainerNoteSpeed.OnePerBeat},0,8000);
        Assert.That(slower.Take(3).Select(n=>n.TimeMs), Is.EqualTo(new[] {0d,500,1000}));
    }

    [Test]
    public void ShortAutomaticSessionsContainPlayableExercises()
    {
        foreach (string song in TrainerMusicCatalog.Songs.Keys)
        foreach (var kind in new[] { TrainerKind.Steady, TrainerKind.Bursts, TrainerKind.Alternating, TrainerKind.Rhythm, TrainerKind.Aim, TrainerKind.Reading })
        {
            var plan = TrainerAdaptiveDifficulty.Apply(new(kind, Music: song, Seconds: 15, AdaptiveDifficulty: true), [], [], DateTimeOffset.UtcNow);
            var map = TrainerBeatmap.Create(plan);
            Assert.That(map.HitObjects.Count, Is.GreaterThanOrEqualTo(3), $"{song}/{kind}");
        }
    }

    [TestCase(TrainerKind.Steady)] [TestCase(TrainerKind.Bursts)] [TestCase(TrainerKind.Alternating)]
    [TestCase(TrainerKind.Rhythm)] [TestCase(TrainerKind.Aim)] [TestCase(TrainerKind.Reading)]
    public void AdaptiveSessionsUsePerformedAttacksAtEverySongAndTempo(TrainerKind kind)
    {
        foreach (string song in TrainerMusicCatalog.Songs.Keys)
        foreach (int bpm in TrainerMusicCatalog.Tempos)
        {
            var settings = new TrainerSettings(kind, Music: song, Bpm: bpm, Seconds: 60, AdaptiveDifficulty: true,
                SkillLimits: new(MaxNps: 4, MaxChain: 8, MaxBurst: 3), Sliders: TrainerSliderStyle.Mixed);
            var timeline = new TrainerSession(settings);
            var profile = TrainerSongArrangement.For(settings)!;
            Assert.That(timeline.Notes.Count, Is.GreaterThanOrEqualTo(12), $"{song}/{bpm}/{kind}");
            foreach (var note in timeline.Notes)
                Assert.That(profile.Events.Any(e => Math.Abs(profile.TimeAt(e.Beat) - note.TimeMs) < .02), Is.True, $"{song}/{bpm}/{note.TimeMs}");
            var map = TrainerBeatmap.Create(settings);
            foreach (var obj in map.HitObjects) obj.ApplyDefaults(map.ControlPointInfo, map.Difficulty);
            Assert.That(TrainerSkillProfile.Measure(map).PeakNps, Is.LessThanOrEqualTo(4.001));
            Assert.That(timeline.Notes, Is.EqualTo(new TrainerSession(settings).Notes));
        }
    }

    [Test]
    public void MelodyWinsOverAnEarlierAccompanimentAndHoldsKeepPerformedTiming()
    {
        var music = new TrainerSongArrangement(2, "fixture", 120, "", 500, 16,
            [new(0, 16, 1, false)], [new(4, .5, 40), new(4.1, 1, 69), new(6.9, 1, 65)],
            [new(6.9, .9, false, true)]);
        var settings = new TrainerSettings(TrainerKind.Aim, AdaptiveDifficulty: true, SkillLimits: new(MaxNps: 4), Sliders: TrainerSliderStyle.Mixed);
        var notes = music.Arrange(settings, [], 2500, 8500);
        Assert.That(notes.Select(n => n.TimeMs), Is.EqualTo(new[] { 2550d, 3950d }).Within(.001));
        Assert.That(notes[^1].MusicHoldBeats, Is.EqualTo(.9).Within(.001));
    }

    [TestCase(TrainerKind.Bursts)] [TestCase(TrainerKind.Alternating)]
    public void GroupsLeaveRestsAndNeverFillMissingMusicalAttacks(TrainerKind kind)
    {
        var music = new TrainerSongArrangement(2, "fixture", 120, "", 0, 32, [new(0, 32, 1, false)],
            new[] { 0d, .5, 1, 2.1, 4, 4.5, 5, 5.5, 8, 8.5, 9 }.Select(b => new TrainerMusicEvent(b, 1, 60)).ToArray(), []);
        var settings = new TrainerSettings(kind, AdaptiveDifficulty: true, SkillLimits: new(MaxNps: 4, MaxChain: 4, MaxBurst: 3));
        var notes = TrainerMusicalPhrases.Select(music, settings, 0, 16000);
        Assert.That(notes, Is.Not.Empty);
        foreach (var group in notes.GroupBy(n => n.Phrase))
        {
            Assert.That(group.Count(), Is.InRange(3, kind == TrainerKind.Bursts ? 3 : 4));
            var times = group.Select(n => n.TimeMs).ToArray();
            Assert.That(times.Zip(times.Skip(1), (a,b) => b-a).Distinct().Count(), Is.EqualTo(1));
        }
        Assert.That(notes.Any(n => Math.Abs(n.TimeMs-1050) < .01), Is.False);
    }

    [Test]
    public void AutomaticObjectChoicesFollowTheSkillAndManualOptionsSurvive()
    {
        var manual = new TrainerSettings(TrainerKind.Aim, Music: "copper-sky", Bpm: 210, Sliders: TrainerSliderStyle.None,
            NoteSpeed: TrainerNoteSpeed.FourPerBeat, AimSpacing: 140, ApproachRate: 10, CircleSize: 6, Keys: "D / F", OffsetMs: 31);
        var next = TrainerAdaptiveDifficulty.Apply(manual with { AdaptiveDifficulty = true }, [], [], DateTimeOffset.UtcNow);
        Assert.That(next.Bpm, Is.LessThan(210));
        Assert.That(next.Sliders, Is.EqualTo(TrainerSliderStyle.Mixed));
        Assert.That(next.NoteSpeed, Is.EqualTo(TrainerNoteSpeed.Default));
        Assert.That(next.Keys, Is.EqualTo(manual.Keys)); Assert.That(next.OffsetMs, Is.EqualTo(31));
        Assert.That(TrainerAdaptiveDifficulty.Apply(manual, [], [], DateTimeOffset.UtcNow), Is.EqualTo(manual));
        var burst = TrainerAdaptiveDifficulty.Apply(manual with { Kind = TrainerKind.Bursts, AdaptiveDifficulty = true }, [], [], DateTimeOffset.UtcNow);
        Assert.That(burst.Sliders, Is.EqualTo(TrainerSliderStyle.None));
        var map = TrainerBeatmap.Create(next);
        Assert.That(map.HitObjects.OfType<Slider>(), Is.Not.Empty);
    }

    [Test]
    public void InstalledSongUsesAuthoredAttacksAcrossTempoChanges()
    {
        var source = new Beatmap<OsuHitObject>();
        source.ControlPointInfo.Add(0, new TimingControlPoint { BeatLength = 500 });
        source.ControlPointInfo.Add(4500, new TimingControlPoint { BeatLength = 400 });
        foreach (double time in new[] { 900d, 1650, 2600, 3750, 4500, 4900, 5750, 6500, 7700 })
            source.HitObjects.Add(new HitCircle { StartTime = time });
        var settings = new TrainerSettings(TrainerKind.Aim, Music: "song", AdaptiveDifficulty: true, SkillLimits: new(MaxNps: 4));
        var notes = TrainerBeatmap.SongNotes(settings, source);
        Assert.That(notes.Count, Is.GreaterThan(4));
        Assert.That(notes.All(n => source.HitObjects.Any(o => Math.Abs(o.StartTime - n.TimeMs) < .001)), Is.True);
    }
}

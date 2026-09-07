using AimMod.Desktop.Trainers;
using NUnit.Framework;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.ControlPoints;
using osu.Game.Rulesets.Osu.Objects;

namespace AimMod.Desktop.Tests;

[TestFixture]
public class TrainerMusicTests
{
    [Test]
    public void SongDrillFollowsTimingChangesAndSectionBounds()
    {
        var source = new Beatmap<OsuHitObject>();
        source.ControlPointInfo.Add(125, new TimingControlPoint { BeatLength = 500 });
        source.ControlPointInfo.Add(20125, new TimingControlPoint { BeatLength = 400 });
        source.HitObjects.Add(new HitCircle { StartTime = 5125 });
        source.HitObjects.Add(new HitCircle { StartTime = 90125 });
        var settings = new TrainerSettings(TrainerKind.Alternating, Seconds: 30, Music: "song");
        var notes = TrainerBeatmap.SongNotes(settings, source);
        Assert.That(notes[0].TimeMs, Is.EqualTo(5125));
        Assert.That(notes[^1].TimeMs, Is.LessThan(35125));
        var before = notes.Where(n => n.TimeMs < 20125).ToArray();
        var after = notes.Where(n => n.TimeMs >= 20125).ToArray();
        Assert.That(before.Zip(before.Skip(1), (a,b) => b.TimeMs-a.TimeMs), Is.All.EqualTo(125));
        Assert.That(after.Zip(after.Skip(1), (a,b) => b.TimeMs-a.TimeMs), Is.All.EqualTo(100));
        Assert.That(notes.Select(n => n.TimeMs).Distinct().Count(), Is.EqualTo(notes.Count));
        var section = TrainerBeatmap.SongNotes(settings with { SongStartSeconds = 60, Seconds = 60 }, source);
        Assert.That(section[0].TimeMs, Is.EqualTo(65125));
        Assert.That(section[^1].TimeMs, Is.LessThanOrEqualTo(90125));
        Assert.That(TrainerBeatmap.SongNotes(settings with { SongStartSeconds = 180 }, source), Is.Empty);
    }

    [Test]
    public void SongBurstsRetainThreeNotesAndBreathingRoomAcrossTempoChanges()
    {
        var source = new Beatmap<OsuHitObject>();
        source.ControlPointInfo.Add(0, new TimingControlPoint { BeatLength = 500 });
        source.ControlPointInfo.Add(8000, new TimingControlPoint { BeatLength = 400 });
        source.HitObjects.Add(new HitCircle { StartTime = 0 });
        source.HitObjects.Add(new HitCircle { StartTime = 30000 });
        var notes = TrainerBeatmap.SongNotes(new(TrainerKind.Bursts, Seconds: 15, Music: "song"), source);
        Assert.That(notes.GroupBy(n => n.Phrase).Select(g => g.Count()), Is.All.EqualTo(3));
        var map = TrainerBeatmap.Create(new(TrainerKind.Bursts, Seconds: 15, Music: "song"), notes);
        Assert.That(map.HitObjects.Select(n => n.StartTime), Is.EqualTo(notes.Select(n => n.TimeMs)));
        Assert.That(map.HitObjects.Select(n => n.Position).Distinct().Count(), Is.GreaterThan(10));
    }

    [TestCase("pulse")]
    [TestCase("glass")]
    [TestCase("snap")]
    public void CuesAreAudibleAndFastPatternsStayWithinPcmHeadroom(string cue)
    {
        var samples = TrainerAudio.ReadCue(cue);
        double peak = samples.Max(x => Math.Abs((int)x)) / 32768.0;
        double rms = Math.Sqrt(samples.Take(2205).Average(x => Math.Pow(x / 32768.0, 2)));
        Assert.That(peak, Is.InRange(.8, .9));
        Assert.That(rms, Is.GreaterThan(.07), "Cues need body, not an inaudibly short transient.");
        var wave = TrainerAudio.Render(new(new(TrainerKind.Alternating, 240, 15, Cue: cue)));
        using var reader = new BinaryReader(new MemoryStream(wave)); reader.BaseStream.Position = 44;
        int clipped = 0;
        while (reader.BaseStream.Position < reader.BaseStream.Length)
            if (Math.Abs((int)reader.ReadInt16()) >= 32767) clipped++;
        Assert.That(clipped, Is.Zero);
    }

    [TestCase("midnight-pulse", 120)]
    [TestCase("mint-current", 150)]
    [TestCase("afterglow", 180)]
    public void MusicIsEmbeddedAndTimelineFitsFullThreeMinuteTrack(string name, int bpm)
    {
        var bytes = TrainerAudio.Asset($"training-{name}.ogg");
        Assert.That(System.Text.Encoding.ASCII.GetString(bytes, 0, 4), Is.EqualTo("OggS"));
        var session = new TrainerSession(new(Bpm: bpm, Seconds: 180, Music: name));
        Assert.That(session.EndMs + 600, Is.LessThan(184000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TrainerSettings(Bpm: bpm + 10, Music: name).Validate());
    }

    [Test]
    public void ComparisonsKeepSongsAndSectionsSeparateWithoutUsingIrrelevantTempoOrCue()
    {
        var song = new TrainerSettings(Music: "song", SongIdentity: "synthetic-song-a");
        Assert.That(song.ComparisonKey(), Is.EqualTo((song with { Bpm = 180, Cue = "snap" }).ComparisonKey()));
        Assert.That(song.ComparisonKey(), Is.Not.EqualTo((song with { SongStartSeconds = 30 }).ComparisonKey()));
        Assert.That(song.ComparisonKey(), Is.Not.EqualTo((song with { SongIdentity = "synthetic-song-b" }).ComparisonKey()));
    }
}

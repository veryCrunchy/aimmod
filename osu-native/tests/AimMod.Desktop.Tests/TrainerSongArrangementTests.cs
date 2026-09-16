using AimMod.Desktop.Trainers;
using NUnit.Framework;
using osu.Game.Rulesets.Osu.Objects;
using System.Security.Cryptography;

namespace AimMod.Desktop.Tests;

public class TrainerSongArrangementTests
{
    [Test]
    public void EverySelectableTempoHasAnArrangementForTheExactAudio()
    {
        foreach (var song in TrainerMusicCatalog.Songs.Keys)
        foreach (int bpm in TrainerMusicCatalog.Tempos)
        {
            var settings = new TrainerSettings(Music: song, Bpm: bpm);
            var arrangement = TrainerSongArrangement.For(settings);
            Assert.That(arrangement, Is.Not.Null, $"{song}/{bpm}");
            var bytes = TrainerAudio.Asset(TrainerMusicCatalog.Asset(song,bpm));
            Assert.That(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), Is.EqualTo(arrangement!.AudioSha256));
            Assert.That(arrangement.SignatureSourceSha256,
                Is.EqualTo(Convert.ToHexString(SHA256.HashData(TrainerAudio.Asset("startup.ogg"))).ToLowerInvariant()));
            Assert.That(arrangement.Events, Is.Not.Empty);
            Assert.That(arrangement.Holds, Is.Not.Empty);
            Assert.That(arrangement.TimeAt(arrangement.EndBeat), Is.GreaterThan(new TrainerSession(settings with {Seconds=180}).EndMs));
        }
    }

    [Test]
    public void SongsHaveDifferentAuthoredAimRhythmsButRepeatExactly()
    {
        var a = new TrainerSettings(TrainerKind.Aim,Music:"copper-sky",Seconds:60);
        var b = a with {Music="signal-bloom"};
        var first = new TrainerSession(a).Notes;
        Assert.That(first,Is.EqualTo(new TrainerSession(a).Notes));
        Assert.That(first.Select(n=>n.TimeMs),Is.Not.EqualTo(new TrainerSession(b).Notes.Select(n=>n.TimeMs)));
        var profile=TrainerSongArrangement.For(a)!;
        Assert.That(first.All(n=>profile.Events.Any(e=>Math.Abs(profile.TimeAt(e.Beat)-n.TimeMs)<.01)),Is.True);
    }

    [TestCase(TrainerKind.Steady,TrainerPattern.Standard)]
    [TestCase(TrainerKind.Bursts,TrainerPattern.SevenNotes)]
    [TestCase(TrainerKind.Alternating,TrainerPattern.Standard)]
    [TestCase(TrainerKind.Aim,TrainerPattern.JumpFill)]
    public void ExplicitDrillRhythmsRemainIntact(TrainerKind kind,TrainerPattern pattern)
    {
        var settings=new TrainerSettings(kind,Pattern:pattern,Seconds:60);
        var cues=new TrainerSession(settings).Notes;
        var music=new TrainerSession(settings with {Music="night-drive"}).Notes;
        Assert.That(music.Select(n=>(n.TimeMs,n.Phrase)),Is.EqualTo(cues.Select(n=>(n.TimeMs,n.Phrase))));
    }

    [TestCase(TrainerSliderStyle.Mixed)]
    [TestCase(TrainerSliderStyle.BackAndForth)]
    [TestCase(TrainerSliderStyle.SlidersOnly)]
    public void SlidersFollowHeldNotesAndDoNotOverlapTaps(TrainerSliderStyle style)
    {
        var settings=new TrainerSettings(TrainerKind.Aim,Music:"copper-sky",Seconds:60,Sliders:style,SliderBeats:4);
        var profile=TrainerSongArrangement.For(settings)!;
        var map=TrainerBeatmap.Create(settings);
        foreach(var obj in map.HitObjects)obj.ApplyDefaults(map.ControlPointInfo,map.Difficulty);
        var sliders=map.HitObjects.OfType<Slider>().ToArray();
        Assert.That(sliders,Is.Not.Empty);
        if(style==TrainerSliderStyle.SlidersOnly)Assert.That(map.HitObjects.All(o=>o is Slider),Is.True);
        foreach(var slider in sliders)
        {
            var holds=profile.Holds.Where(h=>Math.Abs(profile.TimeAt(h.Beat)-slider.StartTime)<.02).ToArray();
            Assert.That(holds,Is.Not.Empty);
            Assert.That(slider.Duration,Is.LessThanOrEqualTo(Math.Min(4,holds.Max(h=>h.Beats))*500+.02));
            Assert.That(slider.RepeatCount,Is.EqualTo(style==TrainerSliderStyle.BackAndForth?3:0));
            var next=map.HitObjects.FirstOrDefault(o=>o.StartTime>slider.StartTime);
            if(next is not null)Assert.That(next.StartTime,Is.GreaterThan(slider.EndTime));
        }
        Assert.That(TrainerBeatmap.Create(settings with {Sliders=TrainerSliderStyle.None}).HitObjects.All(o=>o is HitCircle),Is.True);
    }

    [Test]
    public void MusicalGeometryStillRespectsPlayerLimits()
    {
        var settings=new TrainerSettings(TrainerKind.Aim,Music:"signal-bloom",Seconds:60,AdaptiveDifficulty:true,
            SkillLimits:new(MaxNps:3,MaxJumpDistance:130,MaxAimVelocity:280));
        var map=TrainerBeatmap.Create(settings);
        foreach(var obj in map.HitObjects)obj.ApplyDefaults(map.ControlPointInfo,map.Difficulty);
        for(int i=1;i<map.HitObjects.Count;i++)
        {
            var previous=map.HitObjects[i-1];var current=map.HitObjects[i];
            double distance=osuTK.Vector2.Distance(previous.EndPosition,current.Position);
            Assert.That(distance,Is.LessThanOrEqualTo(130.01));
            Assert.That(distance/((current.StartTime-TrainerSpinners.End(previous))/1000),Is.LessThanOrEqualTo(280.01));
        }
    }

    [TestCase(TrainerNoteSpeed.OnePerBeat)]
    [TestCase(TrainerNoteSpeed.TwoPerBeat)]
    [TestCase(TrainerNoteSpeed.FourPerBeat)]
    public void AnExplicitNoteRateKeepsItsGrid(TrainerNoteSpeed speed)
    {
        var settings=new TrainerSettings(TrainerKind.Aim,NoteSpeed:speed,Music:"signal-bloom");
        Assert.That(new TrainerSession(settings).Notes.Select(n=>n.TimeMs),
            Is.EqualTo(new TrainerSession(settings with {Music="cues"}).Notes.Select(n=>n.TimeMs)));
    }

    [Test]
    public void OccasionalSpinnersStayInsideMusicalBreaks()
    {
        var settings=new TrainerSettings(TrainerKind.Aim,Music:"copper-sky",Seconds:180,Spinners:TrainerSpinnerFrequency.Occasional);
        var profile=TrainerSongArrangement.For(settings)!;
        var map=TrainerBeatmap.Create(settings);
        Assert.That(map.HitObjects.OfType<Spinner>(),Is.Not.Empty);
        foreach(var spinner in map.HitObjects.OfType<Spinner>())
        {
            var section=profile.SectionAt(profile.BeatAt(spinner.StartTime));
            Assert.That(section?.Quiet,Is.True);
            Assert.That(spinner.EndTime,Is.LessThanOrEqualTo(profile.TimeAt(section!.EndBeat)));
        }
    }
}

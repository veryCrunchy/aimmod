using AimMod.Desktop.Trainers;
using NUnit.Framework;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.ControlPoints;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Objects.Types;

namespace AimMod.Desktop.Tests;

public class TrainerPatternTests
{
    [TestCase(TrainerPattern.Standard, 3)] [TestCase(TrainerPattern.FiveNotes, 5)]
    [TestCase(TrainerPattern.SevenNotes, 7)] [TestCase(TrainerPattern.NineNotes, 9)]
    public void BurstsFinishTheirGroupAcrossBarBoundaries(TrainerPattern pattern, int length)
    {
        var notes = new TrainerSession(new(TrainerKind.Bursts, Seconds:60, Pattern:pattern)).Notes;
        var groups = notes.GroupBy(n => n.Phrase).ToArray();
        Assert.That(groups.SkipLast(1).All(g => g.Count()==length), Is.True);
        Assert.That(notes.Zip(notes.Skip(1), (a,b) => b.TimeMs-a.TimeMs).Min(), Is.EqualTo(125).Within(.001));
    }
    [Test]
    public void MixedBurstsContainAllLengthsAndRandomizationIsRepeatable()
    {
        var settings = new TrainerSettings(TrainerKind.Bursts, Seconds:60, Pattern:TrainerPattern.MixedBursts);
        var groups = new TrainerSession(settings).Notes.GroupBy(n => n.Phrase).SkipLast(1).Select(g=>g.Count()).Distinct();
        Assert.That(groups, Is.EquivalentTo(new[]{3,5,7,9}));
        var random = settings with {RandomizePatterns=true,PatternSeed=45,SkillLimits=new(MaxNps:12,MaxChain:64,MaxBurst:9,Complexity:2)};
        Assert.That(new TrainerSession(random).Notes, Is.EqualTo(new TrainerSession(random).Notes));
        Assert.That(new TrainerSession(random).Notes, Is.Not.EqualTo(new TrainerSession(random with {PatternSeed=46}).Notes));
    }
    [Test]
    public void ContinuousStreamsHaveNoRestsAndPartialStreamsDo()
    {
        var full = new TrainerSession(new(TrainerKind.Alternating));
        var partial = new TrainerSession(new(TrainerKind.Alternating,Pattern:TrainerPattern.PartialStreams));
        Assert.That(full.Notes.Zip(full.Notes.Skip(1),(a,b)=>b.TimeMs-a.TimeMs).Max(),Is.EqualTo(125));
        Assert.That(partial.Notes.Zip(partial.Notes.Skip(1),(a,b)=>b.TimeMs-a.TimeMs).Max(),Is.GreaterThan(1000));
    }
    [Test]
    public void JumpSpeedAndConnectingNotesChangeTiming()
    {
        var baseSettings = new TrainerSettings(TrainerKind.Aim);
        int regular = new TrainerSession(baseSettings).Notes.Count;
        Assert.That(new TrainerSession(baseSettings with {NoteSpeed=TrainerNoteSpeed.TwoPerBeat}).Notes.Count, Is.EqualTo(regular*2));
        Assert.That(new TrainerSession(baseSettings with {Pattern=TrainerPattern.JumpFill}).Notes.Count, Is.EqualTo(regular*2));
        Assert.That(new TrainerSession(baseSettings with {Pattern=TrainerPattern.JumpTriples}).Notes.Count, Is.EqualTo(regular*3));
    }
    [TestCase(TrainerSliderStyle.Mixed)] [TestCase(TrainerSliderStyle.SlidersOnly)] [TestCase(TrainerSliderStyle.BackAndForth)]
    public void SlidersHaveRealDurationAndLeaveRoomForTheNextTap(TrainerSliderStyle style)
    {
        var map = TrainerBeatmap.Create(new(TrainerKind.Alternating,Sliders:style,SliderBeats:4));
        foreach (var obj in map.HitObjects) obj.ApplyDefaults(map.ControlPointInfo,map.Difficulty);
        var sliders = map.HitObjects.OfType<Slider>().ToArray();
        Assert.That(sliders,Is.Not.Empty);
        foreach (var slider in sliders)
        {
            Assert.That(slider.Duration,Is.EqualTo(2000).Within(.01));
            Assert.That(slider.RepeatCount,Is.EqualTo(style==TrainerSliderStyle.BackAndForth?3:0));
            var next=map.HitObjects.FirstOrDefault(o=>o.StartTime>slider.StartTime);
            Assert.That(next!.StartTime,Is.GreaterThan(slider.EndTime));
            Assert.That(slider.EndPosition.X,Is.InRange(20,492));
            Assert.That(slider.EndPosition.Y,Is.InRange(20,364));
        }
    }
    [Test]
    public void EveryPatternWorksOnSongTimingAcrossTempoChanges()
    {
        var source = new Beatmap<OsuHitObject>();
        source.ControlPointInfo.Add(0,new TimingControlPoint {BeatLength=500});
        source.ControlPointInfo.Add(10000,new TimingControlPoint {BeatLength=400});
        source.HitObjects.Add(new HitCircle {StartTime=0}); source.HitObjects.Add(new HitCircle {StartTime=30000});
        foreach (var kind in Enum.GetValues<TrainerKind>().Where(k=>k!=TrainerKind.Reaction))
            foreach (var pattern in TrainerPatterns.Choices(kind).Values)
            {
                var settings=new TrainerSettings(kind,Music:"song",Pattern:pattern);
                var notes=TrainerBeatmap.SongNotes(settings,source);
                Assert.That(notes,Is.Not.Empty);
                Assert.That(notes.Zip(notes.Skip(1),(a,b)=>b.TimeMs>a.TimeMs).All(x=>x),Is.True,$"{kind}/{pattern}");
                Assert.That(notes.All(n=>n.TimeMs>=0 && n.TimeMs<30000),Is.True);
            }
    }
    [Test]
    public void ShuffleAndRandomizerPreferencesPersistSeparatelyFromHistory()
    {
        string root=Directory.CreateTempSubdirectory("aimmod-preferences-").FullName;
        try {
            var store=new TrainerHistoryStore(Path.Combine(root,"history.json"));
            store.SavePreferences(new(false,true,false));
            Assert.That(new TrainerHistoryStore(Path.Combine(root,"history.json")).LoadPreferences(),Is.EqualTo(new TrainerWorkspacePreferences(false,true,false)));
            Assert.That(store.Load(),Is.Empty);
        } finally {Directory.Delete(root,true);}
    }
}

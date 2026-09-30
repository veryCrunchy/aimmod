using AimMod.Desktop.Trainers;
using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.ControlPoints;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Objects.Types;
using osuTK;

namespace AimMod.Desktop.Tests;

public class TrainerSkillTests
{
    private static readonly DateTimeOffset now=DateTimeOffset.Parse("2026-09-01T12:00:00Z");
    private static TrainerResult result(int index, double accuracy=98, TrainerKind kind=TrainerKind.Alternating) =>
        new(Guid.NewGuid(),now.AddMinutes(-index),new(kind),100,accuracy<90?75:98,95,0,0,0,12,0,Engine:"osu-adaptive-v4",Accuracy:accuracy,PlayedSeconds:30,Demand:new(6,160,600,24));

    [Test]
    public void SingleGoodRunOrOldRunsCannotUnlockHarderPatterns()
    {
        var baseline=TrainerSkillProfile.Build(TrainerKind.Alternating,[],[],now);
        Assert.That(TrainerSkillProfile.Build(TrainerKind.Alternating,[result(1)],[],now),Is.EqualTo(baseline));
        Assert.That(TrainerSkillProfile.Build(TrainerKind.Alternating,Enumerable.Range(1,3).Select(i=>result(i) with {CompletedAt=now.AddDays(-40)}),[],now),Is.EqualTo(baseline));
        Assert.That(TrainerSkillProfile.Build(TrainerKind.Alternating,Enumerable.Range(1,3).Select(i=>result(i,kind:TrainerKind.Bursts)),[],now),Is.EqualTo(baseline));
    }
    [Test]
    public void RepeatedSuccessRaisesLimitsAndRecentStrugglesLowerThem()
    {
        var clean=Enumerable.Range(3,3).Select(i=>result(i)).ToArray();
        var raised=TrainerSkillProfile.Build(TrainerKind.Alternating,clean,[],now);
        Assert.That(raised.MaxNps,Is.EqualTo(6.3).Within(.001));
        Assert.That(raised.Complexity,Is.EqualTo(1));
        var reduced=TrainerSkillProfile.Build(TrainerKind.Alternating,clean.Concat([result(1,80),result(2,80)]),[],now);
        Assert.That(reduced.MaxNps,Is.LessThan(raised.MaxNps)); Assert.That(reduced.MaxChain,Is.LessThan(raised.MaxChain));
        Assert.That(reduced.Complexity,Is.Zero);
        var one=clean[0]; Assert.That(TrainerSkillProfile.Build(TrainerKind.Alternating,[one,one,one],[],now).EvidenceCount,Is.Zero);
    }
    [TestCase(TrainerKind.Aim)] [TestCase(TrainerKind.Alternating)] [TestCase(TrainerKind.Bursts)]
    [TestCase(TrainerKind.Steady)] [TestCase(TrainerKind.Rhythm)] [TestCase(TrainerKind.Reading)]
    public void AggressiveUserSettingsStillStayInsideColdStartLimits(TrainerKind kind)
    {
        for(int seed=0;seed<8;seed++)
        {
            var settings=new TrainerSettings(kind,Bpm:210,NoteSpeed:TrainerNoteSpeed.FourPerBeat,RandomizePatterns:true,PatternSeed:seed,
                AimStyle:TrainerAimStyle.WideJumps,AimSpacing:140,ApproachRate:10,CircleSize:6,Sliders:TrainerSliderStyle.BackAndForth,SliderBeats:4);
            var map=TrainerBeatmap.Create(settings);
            foreach(var obj in map.HitObjects) obj.ApplyDefaults(map.ControlPointInfo,map.Difficulty);
            Assert.That(map.Difficulty.ApproachRate,Is.LessThanOrEqualTo(6)); Assert.That(map.Difficulty.CircleSize,Is.LessThanOrEqualTo(4));
            var demand=TrainerSkillProfile.Measure(map);
            Assert.That(demand.PeakNps,Is.LessThanOrEqualTo(2.5+.001));
            Assert.That(demand.JumpDistance,Is.LessThanOrEqualTo(120+.001));
            Assert.That(demand.AimVelocity,Is.LessThanOrEqualTo(300+.001));
            foreach(var slider in map.HitObjects.OfType<Slider>()) Assert.That(slider.Velocity*1000,Is.LessThanOrEqualTo(300+.001));
        }
    }
    [Test]
    public void FastSongTimingCannotBypassRateAndEnduranceCaps()
    {
        var source=new Beatmap<OsuHitObject>();
        source.ControlPointInfo.Add(0,new TimingControlPoint{BeatLength=500});
        source.ControlPointInfo.Add(5000,new TimingControlPoint{BeatLength=200});
        source.HitObjects.Add(new HitCircle{StartTime=0});source.HitObjects.Add(new HitCircle{StartTime=30000});
        var settings=new TrainerSettings(TrainerKind.Alternating,Music:"song",RandomizePatterns:true);
        var notes=TrainerBeatmap.SongNotes(settings,source);
        Assert.That(notes.Zip(notes.Skip(1),(a,b)=>b.TimeMs-a.TimeMs).Min(),Is.GreaterThanOrEqualTo(400-.001));
        int chain=1;
        for(int i=1;i<notes.Count;i++) { chain=notes[i].TimeMs-notes[i-1].TimeMs>=1200 ? 1:chain+1; Assert.That(chain,Is.LessThanOrEqualTo(8)); }
    }
    [Test]
    public void TurningRandomizerOffPreservesManualDifficulty()
    {
        var manual=new TrainerSettings(TrainerKind.Aim,Bpm:210,ApproachRate:10,CircleSize:6,NoteSpeed:TrainerNoteSpeed.FourPerBeat);
        Assert.That(TrainerSkillProfile.Apply(manual,new()),Is.EqualTo(manual));
        Assert.That(TrainerBeatmap.Create(manual).Difficulty.ApproachRate,Is.EqualTo(10));
    }
    [Test]
    public void ReplayEvidenceRequiresTheSamePlayerManualPlayAndCorrectRate()
    {
        var run=new LocalReplay(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"Synthetic","Artist","Difficulty","osu","Player",now.AddDays(-1),5,.98,1,100,0,null,[],true);
        var notes=Enumerable.Range(0,32).Select(i=>new ReplayObjectJudgement(i,null,"HitCircle",i*125,i*125,"Great","Great",i*125,0,1.5,new ReplayPoint(i%2*100,100),null,i,i+1)).ToArray();
        var analysis=new ReplayAnalysisResult(ReplayAnalysisProtocol.EngineVersion,"beatmap",true,1000,[],notes,new(32,0,0,0,0,0));
        var sample=TrainerSkillProfile.FromReplay(run,analysis,"Player",now);
        Assert.That(sample,Is.Not.Null); Assert.That(sample!.Demand.PeakNps,Is.EqualTo(12).Within(.001));
        Assert.That(TrainerSkillProfile.FromReplay(run,analysis,"Another player",now),Is.Null);
        Assert.That(TrainerSkillProfile.FromReplay(run with {Mods=["AT"]},analysis,"Player",now),Is.Null);
        Assert.That(TrainerSkillProfile.FromReplay(run with {Passed=false},analysis,"Player",now),Is.Null);
        var poor=analysis with {Judgements=notes.Select(n=>n with {Result="Meh"}).ToArray()};
        Assert.That(TrainerSkillProfile.FromReplay(run,poor,"Player",now),Is.Null);
    }
}

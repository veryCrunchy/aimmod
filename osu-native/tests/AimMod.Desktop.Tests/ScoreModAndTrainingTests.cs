using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.ScoreHistory;
using NUnit.Framework;
namespace AimMod.Desktop.Tests;

[TestFixture]
public class ScoreModAndTrainingTests {
    private static LocalReplay run(int n, string[]? mods=null,string json="") => new(
        new Guid(n,0,0,new byte[8]),Guid.Empty,new Guid(42,0,0,new byte[8]),"Example","Example","Test","osu","Example",
        DateTimeOffset.UtcNow.AddDays(-1).AddMinutes(n),5,.97,100000,200,3,null,mods??[],true,ModsJson:json);
    [Test]
    public void FiltersIncludeEveryObservedModAndExactSettings() {
        var a=run(1,["DT","HD"],"[{\"acronym\":\"DT\",\"settings\":{\"speed_change\":1.2}},{\"acronym\":\"HD\"}]");
        var b=run(2,["WU","MR","DC"]);
        var choices=ScoreMods.Choices([a,b]);
        foreach(var name in new[]{"DT","HD","WU","MR","DC"})Assert.That(choices.Any(c=>c.Key=="mod:"+name),Is.True);
        Assert.That(ScoreMods.Matches(a,"mod:HD"),Is.True);
        Assert.That(ScoreMods.Matches(a,"NM"),Is.False);
        Assert.That(ScoreMods.Matches(a,"setup:"+ScoreMods.Configuration(a)),Is.True);
        Assert.That(ScoreMods.Display(a),Does.Contain("1.2×"));
        Assert.That(ScoreMods.SetupKey(a),Is.Not.EqualTo(ScoreMods.SetupKey(a with { ModsJson="" })));
        Assert.That(ScoreMods.SetupKey(a),Is.Not.EqualTo(ScoreMods.SetupKey(a with { LegacyScore=true })));
    }
    [Test]
    public void ReorderedSettingsMatchAndMalformedSettingsDoNotCrash() {
        var a=run(1,["DA"],"[{\"acronym\":\"DA\",\"settings\":{\"approach_rate\":9,\"circle_size\":4}}]");
        var b=a with { ModsJson="[{\"acronym\":\"DA\",\"settings\":{\"circle_size\":4,\"approach_rate\":9}}]" };
        Assert.That(ScoreMods.Configuration(a),Is.EqualTo(ScoreMods.Configuration(b)));
        Assert.DoesNotThrow(()=>ScoreMods.Choices([a with { ModsJson="invalid" }]));
    }
    [Test]
    public void OnlineSettingsSurviveHistoryMergeAndFilter() {
        var local=run(1) with { OnlineScoreId=99 };
        var online=new ScoreHistoryEntry("osu:99",99,42,4,null,null,"Example","Example","Test",local.PlayedAt,5,.97,100,10000,200,0,["WU"],ScoreHistoryProvenance.OnlineRecent,false,
            ModsJson:"[{\"acronym\":\"WU\",\"settings\":{\"initial_rate\":1.1}}]");
        var merged=ScoreHistoryMerger.MergeAsLocalReplays([local],[online]).Single();
        Assert.That(merged.ModsJson,Is.EqualTo(online.ModsJson));
        var model=StatisticsWorkspaceModel.Build([merged],new StatisticsRunQuery(ModSelection:"mod:WU"));
        Assert.That(model.Runs.Count,Is.EqualTo(1));
    }
    [Test]
    public void TrainingRequiresNewComparableRepeatedResults() {
        var history=new[]{run(1),run(2),run(3)};
        var plan=CoachingTrainingPlanner.Build(NativeCoachingWorkspaceModel.Build(history,new Dictionary<Guid,AimMod.Osu.Runtime.Contracts.ReplayAnalysisResult>()))!;
        Assert.That(plan.BaselineCount,Is.EqualTo(3));
        Assert.That(plan.TargetMisses,Is.EqualTo(2));
        plan=plan with { StartedAt=DateTimeOffset.UtcNow };
        var good=run(4) with { PlayedAt=DateTimeOffset.UtcNow.AddMinutes(1),MissCount=2 };
        Assert.That(CoachingTrainingPlanner.Review(plan,history).Attempts,Is.Zero);
        Assert.That(CoachingTrainingPlanner.Review(plan,[good,good]).Complete,Is.False);
        Assert.That(CoachingTrainingPlanner.Review(plan,[good,good with { ScoreId=Guid.NewGuid(),Mods=["DT"] }]).Complete,Is.False);
        Assert.That(CoachingTrainingPlanner.Review(plan,[good,good with { ScoreId=Guid.NewGuid(),Passed=false }]).Complete,Is.False);
        Assert.That(CoachingTrainingPlanner.Review(plan,[good,good with { ScoreId=Guid.NewGuid() }]).Complete,Is.True);
        Assert.That(CoachingTrainingPlanner.Review(plan,[good with { Player="Someone else" }]).Attempts,Is.Zero);
    }
    [Test]
    public void SparseHistoryCollectsBaselineAndAssistedPlaysCannotSetGoals() {
        var history=new[]{run(1)};
        var plan=CoachingTrainingPlanner.Build(NativeCoachingWorkspaceModel.Build(history,new Dictionary<Guid,AimMod.Osu.Runtime.Contracts.ReplayAnalysisResult>()))!;
        Assert.That(plan.Focus,Is.EqualTo("Set a useful baseline"));
        var assisted=new[]{run(1,["RX"])};
        Assert.That(CoachingTrainingPlanner.Build(NativeCoachingWorkspaceModel.Build(assisted,new Dictionary<Guid,AimMod.Osu.Runtime.Contracts.ReplayAnalysisResult>())),Is.Null);
    }
    [Test]
    public void ActiveSessionSurvivesRestart() {
        string dir=Directory.CreateTempSubdirectory("aimmod-training-test-").FullName;
        try {
            string path=Path.Combine(dir,"session.json");
            var plan=CoachingTrainingPlanner.Build(NativeCoachingWorkspaceModel.Build([run(1)],new Dictionary<Guid,AimMod.Osu.Runtime.Contracts.ReplayAnalysisResult>()))! with { StartedAt=DateTimeOffset.UtcNow };
            new CoachingTrainingStore(path).Save(plan);
            Assert.That(new CoachingTrainingStore(path).Load(),Is.EqualTo(plan));
            new CoachingTrainingStore(path).Save(null);
            Assert.That(new CoachingTrainingStore(path).Load(),Is.Null);
        } finally { Directory.Delete(dir,true); }
    }
}

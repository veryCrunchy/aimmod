using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Practice;
using NUnit.Framework;
namespace AimMod.Desktop.Tests.Practice;
[TestFixture]
public class PracticeProgressTests
{
    private static readonly DateTimeOffset epoch=DateTimeOffset.UtcNow.AddDays(-2);
    private static LocalReplay play(int minute,string hash="practice-sha",string player="Practice Player",double accuracy=.95,bool passed=true) => new(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"Song","Artist","Drill","osu",player,epoch.AddMinutes(minute),4,accuracy,1000,100,passed?0:5,null,[],true,BeatmapHash:hash,Passed:passed);
    private static SavedPracticeMap map() => new(Guid.NewGuid().ToString("N"),"Song","Hard",PracticeDrillType.Streams,epoch,0,5000,60000,6,100,Tracking:new("Practice Player",123,100,"source",Guid.NewGuid(),"",false,[],[new("Timing",PracticeDrillType.Streams,"practice-sha","practice-md5",0,5000,true)]));
    [Test]
    public void MatchesHashesAndRejectsOtherPlayersAutomationAndOlderScores()
    {
        var set=map(); var good=play(1); var stable=play(2,"practice-md5") with { Origin=LocalLibraryOrigin.Stable };
        var rows=new[]{good,stable,play(3,player:"Another Player"),play(-1),play(4,"unrelated"),play(5) with { Mods=new[]{"AT"} }};
        var progress=PracticeProgressTracker.Reconcile(set,PracticeProgress.Empty,rows,123);
        Assert.That(progress.Attempts,Has.Count.EqualTo(2));
        Assert.That(progress.Attempts.Select(a=>a.Setup).Distinct().Count(),Is.EqualTo(2));
        Assert.That(PracticeProgressTracker.Reconcile(set,PracticeProgress.Empty,rows,999).Attempts,Is.Empty);
    }
    [Test]
    public void HistorySurvivesRefreshAndOriginalRetestsRequireMatchingSetup()
    {
        var set=map(); var practice=play(1); var original=play(2,"source");
        var progress=PracticeProgressTracker.Reconcile(set,PracticeProgress.Empty,[practice,original,play(3,"source") with { Mods=new[]{"DT"} },play(4,"source") with { LegacyScore=true }],123);
        Assert.That(progress.Attempts,Has.Count.EqualTo(2));
        Assert.That(progress.Attempts.Count(a=>a.Original),Is.EqualTo(1));
        var refreshed=PracticeProgressTracker.Reconcile(set,progress,[practice],123);
        Assert.That(refreshed.Attempts,Is.EqualTo(progress.Attempts));
    }
    [Test]
    public void FailedAttemptsAreRetainedAndPracticeComparisonNeedsSixCompletedPlays()
    {
        var set=map(); var rows=Enumerable.Range(1,6).Select(i=>play(i,accuracy:i<=3?.9:.98)).Append(play(7,passed:false)).ToArray();
        var progress=PracticeProgressTracker.Reconcile(set,PracticeProgress.Empty,rows,123);
        string text=PracticeProgressTracker.DescribePractice(progress.Attempts);
        Assert.That(text,Does.Contain("7 recorded attempts / 6 completed"));
        Assert.That(text,Does.Contain("Median accuracy"));
        Assert.That(PracticeProgressTracker.DescribePractice(progress.Attempts.Take(2)),Does.Contain("Complete six"));
    }
    [Test]
    public void TransferExcludesOriginalPlaysBeforePractice()
    {
        var set=map(); var tracking=set.Tracking!;
        var baseline=Enumerable.Range(-5,3).Select(i=>play(i,"source",accuracy:.9)).ToArray();
        set=set with { Tracking=tracking with { Baseline=PracticeProgressTracker.Baseline(baseline,tracking,epoch) } };
        var rows=new[]{play(1,"source",accuracy:.99),play(2),play(3,"source",accuracy:.95),play(4,"source",accuracy:.96),play(5,"source",accuracy:.97)};
        var progress=PracticeProgressTracker.Reconcile(set,PracticeProgress.Empty,rows,123);
        string text=PracticeProgressTracker.DescribeTransfer(new(set,progress));
        Assert.That(text,Does.Contain("Higher accuracy on the original map"));
        Assert.That(text,Does.Contain("3 recent completed retests"));
    }
    [Test]
    public void PersistsPracticeAcrossLibraryInstances()
    {
        string root=Directory.CreateTempSubdirectory("aimmod-practice-progress-").FullName;
        try
        {
            var set=map(); string folder=Path.Combine(root,set.Id); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder,"AimMod practice.osz"),"package");
            var library=new PracticeMapLibrary(root); library.Save(set);
            var score=play(1); library.RefreshProgress([score],123);
            var reopened=new PracticeMapLibrary(root); var restored=reopened.RefreshProgress([],123).Single();
            Assert.That(restored.Progress.Attempts.Single().ScoreId,Is.EqualTo(score.ScoreId));
            Assert.That(restored.Map.Tracking!.Difficulties.Single().Sha256,Is.EqualTo("practice-sha"));
        }
        finally { Directory.Delete(root,true); }
    }
}

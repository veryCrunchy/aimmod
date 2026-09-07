using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Practice;
using NUnit.Framework;

namespace AimMod.Desktop.Tests.Practice;

[TestFixture]
public class AutomaticPracticeTests
{
    [Test]
    public void SettingsDefaultOffAndPersist()
    {
        string root=Path.Combine(Path.GetTempPath(),"aimmod-auto-test-"+Guid.NewGuid().ToString("N"));
        try {
            var store=new AutomaticPracticeStore(root);
            Assert.That(store.Load().Enabled,Is.False);
            store.Save(new(Enabled:true,Cleanup:false));
            Assert.That(new AutomaticPracticeStore(root).Load(),Is.EqualTo(new AutomaticPracticeSettings(true,false)));
        } finally { if(Directory.Exists(root))Directory.Delete(root,true); }
    }

    [Test]
    public void CleanupKeepsHistoryAndNeverRemovesManualOrFavouritePayloads()
    {
        string root=Path.Combine(Path.GetTempPath(),"aimmod-auto-test-"+Guid.NewGuid().ToString("N"));
        try {
            var library=new PracticeMapLibrary(root);
            SavedPracticeMap create(bool automatic,bool favourite) {
                string id=Guid.NewGuid().ToString("N");Directory.CreateDirectory(Path.Combine(root,id,"map"));
                File.WriteAllText(Path.Combine(root,id,"map","audio.ogg"),"generated");
                File.WriteAllText(library.ArchivePath(id),"archive");
                var map=new SavedPracticeMap(id,"Practice","Normal",PracticeDrillType.Mixed,DateTimeOffset.UtcNow,0,1000,60000,6,100,
                    Favourite:favourite,Automatic:automatic);library.Save(map);return map;
            }
            var manual=create(false,false);var favourite=create(true,true);var automatic=create(true,false);
            foreach(var map in new[]{manual,favourite,automatic})library.RetireAutomatic(map,DateTimeOffset.UtcNow);
            foreach(var map in library.List())library.PruneRetiredPayload(map);
            Assert.Multiple(()=> {
                Assert.That(File.Exists(library.ArchivePath(manual.Id)),Is.True);
                Assert.That(File.Exists(library.ArchivePath(favourite.Id)),Is.True);
                Assert.That(File.Exists(library.ArchivePath(automatic.Id)),Is.False);
                Assert.That(library.List().Single(m=>m.Id==automatic.Id).PayloadRemoved,Is.True);
                Assert.That(File.Exists(Path.Combine(root,automatic.Id,"practice.json")),Is.True);
                Assert.That(library.LoadProgress(automatic.Id),Is.EqualTo(PracticeProgress.Empty));
            });
        } finally { if(Directory.Exists(root))Directory.Delete(root,true); }
    }

    [Test]
    public void MasteryNeedsThreeMatchingCleanPlaysAndRevisionNeedsFreshEvidence()
    {
        var now=DateTimeOffset.UtcNow;
        var run=new LocalReplay(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"Song","Artist","Normal","osu","Player",now,3,.99,90000,100,0,null,[],true);
        var tracking=new PracticeTracking("Player",42,0,"",run.BeatmapId,"",false,[],[]);
        var map=new SavedPracticeMap(Guid.NewGuid().ToString("N"),"Song","Normal",PracticeDrillType.Mixed,now.AddDays(-1),0,1000,60000,6,100,Tracking:tracking,Automatic:true);
        var runs=Enumerable.Range(0,3).Select(i=>run with {ScoreId=Guid.NewGuid(),PlayedAt=now.AddMinutes(-i)}).ToArray();
        Assert.That(AutomaticPracticePolicy.IsMastered(map,runs.Take(2)),Is.False);
        Assert.That(AutomaticPracticePolicy.IsMastered(map,runs),Is.True);
        Assert.That(AutomaticPracticePolicy.IsMastered(map,runs.Select(r=>r with {Player="Other"})),Is.False);
        Assert.That(AutomaticPracticePolicy.IsMastered(map,runs.Select(r=>r with {MissCount=1})),Is.False);
        Assert.That(AutomaticPracticePolicy.NeedsRevision(map,runs[0],runs,now),Is.True);
        Assert.That(AutomaticPracticePolicy.NeedsRevision(map with {Favourite=true},runs[0],runs,now),Is.False);
        Assert.That(AutomaticPracticePolicy.NeedsRevision(map with {CreatedAt=now.AddHours(-1)},runs[0],runs,now),Is.False);
    }
}

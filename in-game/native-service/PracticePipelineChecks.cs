using System.Text.Json;
namespace AimMod.InGame;
static class PracticePipelineChecks
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "practice-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        void Check(bool value, string name) { if(!value) throw new Exception("Practice check: " + name); }
        try
        {
            var catalog = new ReplayCatalog(root); bool live = true;
            void Save(string id, string date) => File.WriteAllText(Path.Combine(root,"replays",id+".amreplay"),
                JsonSerializer.Serialize(new {kind="header",version=1,id,scenario="Synthetic",recordedAt=date,coordinates="unreal-centimeters"})+"\n"+
                "{\"kind\":\"frame\",\"t\":0,\"camera\":[0,0,0,0,0,0,90],\"actors\":[[1,100,0,0,10,10]]}\n"+
                "{\"kind\":\"frame\",\"t\":0.1,\"camera\":[0,0,0,0,1,0,90],\"actors\":[[1,100,1,0,10,10]]}\n"+
                "{\"kind\":\"end\",\"reason\":\"completed\",\"frames\":2,\"inputEvents\":0}\n");
            Save("old","2026-01-01T00:00:00Z"); Save("new","2026-01-01T00:01:00Z");
            var now = DateTimeOffset.UtcNow;
            using(var pipeline = new PracticePipeline(root,catalog,()=>live,false))
            {
                Check(!pipeline.Tick(now)&&pipeline.Snapshot().Length==0,"live run blocks all work"); live=false;
                Check(pipeline.Tick(now)&&pipeline.Snapshot()[0].Id=="new","newest first");
                Check(!pipeline.Tick(now.AddSeconds(1)),"backfill is throttled");
                Check(pipeline.Tick(now.AddSeconds(11))&&pipeline.Snapshot().Length==2,"one older replay per interval");
                var sample = pipeline.Snapshot()[0];
                Check(PracticeGuidance.DrillTarget([]) is null, "no replay means no drill target");
                var target = PracticeGuidance.DrillTarget([
                    sample with { Id="steady-replay", Tracking=sample.Tracking with { TrackedSeconds=10, MeanAbsErrorDeg=.5 } },
                    sample with { Id="weak-replay", Tracking=sample.Tracking with { TrackedSeconds=10, MeanAbsErrorDeg=2 } }]);
                Check(target?.ReplayId == "weak-replay", "drill target keeps the measured replay identity");
                var repeats = Enumerable.Range(0,6).Select(i => sample with { Id="repeat"+i, RecordedAt=now.AddMinutes(i-6), Flicks=sample.Flicks with { Count=20, MeanCorrections=i<3?1:2, MeanVerificationMs=i<3?100:150, MeanEfficiency=.8 } }).Reverse().ToArray();
                Check(JsonSerializer.Serialize(PracticeGuidance.Build(repeats,now),PracticePipeline.Json).Contains("Take a short break"),"sustained same-scenario fatigue");
                Check(!JsonSerializer.Serialize(PracticeGuidance.Build(repeats.Select((r,i)=>r with { Scenario="Scenario"+i }).ToArray(),now),PracticePipeline.Json).Contains("Take a short break"),"unrelated scenarios are not fatigue evidence");
                Check(!JsonSerializer.Serialize(PracticeGuidance.Build(repeats,now.AddHours(2)),PracticePipeline.Json).Contains("Take a short break"),"session gap resets fatigue");
                Check(JsonSerializer.Serialize(pipeline.Read("native:new"),PracticePipeline.Json).Contains("ready"),"native run identity");
                Check(JsonSerializer.Serialize(pipeline.Read("../new"),PracticePipeline.Json).Contains("unavailable"),"invalid identity");
            }
            using(var pipeline = new PracticePipeline(root,catalog,()=>false,false))
            {
                Check(!pipeline.Tick(now)&&pipeline.Snapshot().Length==2,"cache survives restart without recomputation");
                File.WriteAllText(Path.Combine(root,"practice-cache","new.json"),"broken");
            }
            using(var pipeline = new PracticePipeline(root,catalog,()=>false,false))
                Check(pipeline.Tick(now)&&pipeline.Snapshot().Any(r=>r.Id=="new"),"corrupt cache rebuilt");
            using(var pipeline = new PracticePipeline(root,catalog,()=>false,false))
            {
                for(int i=0;i<140;i++) File.WriteAllText(Path.Combine(root,"practice-cache","extra"+i+".json"),"{}");
                pipeline.Trim(); Check(Directory.GetFiles(Path.Combine(root,"practice-cache"),"*.json").Length<=PracticePipeline.MaxRuns,"cache entry cap");
                using(var oversized=File.Create(Path.Combine(root,"practice-cache","oversized.json"))) oversized.SetLength(PracticePipeline.MaxCacheBytes+1);
                pipeline.Trim(); Check(new DirectoryInfo(Path.Combine(root,"practice-cache")).GetFiles("*.json").Sum(f=>f.Length)<=PracticePipeline.MaxCacheBytes,"cache byte cap");
            }
            int probes=0;
            using(var pipeline = new PracticePipeline(root,catalog,()=>++probes>3,false))
            {
                bool cancelled=false;try { pipeline.Tick(now); } catch(OperationCanceledException) {cancelled=true;}
                Check(cancelled,"live transition interrupts work");
            }
            Console.WriteLine("Practice pipeline checks passed (14).");
        }
        finally { if(Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(root).StartsWith("practice-check-")) Directory.Delete(root,true); }
    }
}

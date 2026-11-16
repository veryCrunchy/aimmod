using System.Diagnostics;
using System.Text.Json;

namespace AimMod.InGame;

static class NativeMechanicsChecks
{
    internal static void Run()
    {
        int checks=0;
        void Check(bool value,string name) { checks++; if(!value) throw new Exception("Native mechanics check: "+name); }
        var frames=new List<ReplayFrame>(); var inputs=new List<ReplayInput>();
        for(int i=0;i<=600;i++)
        {
            var t=i/60.0;
            frames.Add(new(t,[0,0,0,0,t*10,0,90],[[1,1000,0,0,20,20]],new(0,Math.Floor(t),Math.Floor(t/2),Math.Floor(t/2),t*5,t)));
            if(i>0 && i%60==0) { inputs.Add(new(t-.05,"FirePressed",1)); inputs.Add(new(t-.01,"FireReleased",0)); }
        }
        var replay=new NativeReplay(1,"synthetic","Synthetic","2026-01-01T00:00:00Z","completed",10,frames,inputs);
        var watch=Stopwatch.StartNew(); var result=NativeMechanics.Build(replay); watch.Stop();
        Check(result.Smoothness is >99.9 and <=100 && result.Jitter is <.001 && result.Efficiency is >.999,"straight movement");
        Check(result.Correction==0 && result.Measurements.SpeedVariation<.001 && result.Measurements.Overshoot==0,"steady velocity");
        Check(result.Measurements.AverageSpeed is null && result.Measurements.DirectionalBias is null,"unsupported DPI and insufficient reversals stay unknown");
        Check(result.Measurements.ClickTimingVariation<.001 && result.Measurements.AverageShotsToHit==2,"rhythm and shots per hit");
        Check(result.Measurements.KillsPerSecond==.5 && result.Damage==50,"counter units");
        Check(result.Measurements.AverageTimeToKillMs is >=1980 and <=2020 && result.Measurements.TimeToKillSpreadMs<20,"target lifetime milliseconds");
        var blank=NativeMechanics.Build(replay with { Frames=frames.Select(f=>f with { Camera=[0,0,0,0,0,0,90],Actors=[],Stats=null }).ToArray(),Inputs=[] });
        Check(blank.Smoothness is null && blank.Jitter is null && blank.Measurements.KillsPerSecond is null && blank.Measurements.AverageTimeToKillMs is null && blank.Measurements.ClickTimingVariation is null,"absent movement and counters stay unknown");
        bool interrupted=false; try { NativeMechanics.Build(replay,()=>throw new OperationCanceledException()); } catch(OperationCanceledException) { interrupted=true; }
        Check(interrupted,"idle checkpoint cancels analysis");
        var motion=new Motion(frames.Select(f=>f.T).ToArray(),frames.Select(f=>0.0).ToArray(),frames.Select(f=>f.T*10).ToArray(),frames.Select(f=>0.0).ToArray(),[0,10],[[0,0,0,90],[0,0,0,90]],[]);
        var compact=NativeMechanics.Build(replay with { Version=2,Motion=motion });
        Check(Math.Abs(compact.Smoothness!.Value-result.Smoothness!.Value)<.001,"format 2 engine-frame motion");
        var rough=NativeMechanics.Build(replay with { Frames=frames.Select((f,i)=>f with { Camera=[0,0,0,Math.Sin(i*1.7)*2,f.T*10,0,90] }).ToArray() });
        Check(rough.Jitter>result.Jitter && rough.Smoothness<result.Smoothness,"oscillating motion reduces control");
        var root=Path.Combine(Path.GetTempPath(),"mechanics-check-"+Guid.NewGuid().ToString("N"));
        try
        {
            var catalog=new ReplayCatalog(root); bool live=true;
            void Save(string id,string date)=>File.WriteAllText(Path.Combine(root,"replays",id+".amreplay"),
                JsonSerializer.Serialize(new {kind="header",version=1,id,scenario="Synthetic",recordedAt=date,coordinates="unreal-centimeters"})+"\n"+
                "{\"kind\":\"frame\",\"t\":0,\"camera\":[0,0,0,0,0,0,90],\"actors\":[]}\n"+
                "{\"kind\":\"frame\",\"t\":1,\"camera\":[0,0,0,0,10,0,90],\"actors\":[]}\n"+
                "{\"kind\":\"end\",\"reason\":\"completed\",\"frames\":2,\"inputEvents\":0}\n");
            Save("old","2026-01-01T00:00:00Z"); Save("new","2026-01-01T00:01:00Z");
            var now=DateTimeOffset.UtcNow;
            Run Run(string id)=>new("native:"+id,"Synthetic",1,null,1,0,0,"2026-01-01T00:00:00Z",null,null,null,null,false);
            using(var pipeline=new MechanicsPipeline(root,catalog,()=>live,false))
            {
                Check(!pipeline.Tick(now),"active gameplay prevents work"); live=false;
                Check(pipeline.Tick(now) && File.Exists(Path.Combine(root,"mechanics-cache","new.json")) && !File.Exists(Path.Combine(root,"mechanics-cache","old.json")),"newest first");
                Check(!pipeline.Tick(now.AddSeconds(1)),"backfill throttled");
                var measurements=new Dictionary<string,HistoricalMeasurement>();
                var run=pipeline.Join([Run("new")],measurements).Single();
                Check(run.Replay && run.Smoothness>99 && measurements.ContainsKey(run.Id),"native run and supplementary join");
                using(var hub=new Hub(root))
                using(var payload=JsonDocument.Parse(WorkspaceData.Build([run],hub,measurements:measurements)))
                {
                    var data=payload.RootElement;
                    Check(data.GetProperty("history")[0].GetProperty("Replay").GetBoolean() && data.GetProperty("history")[0].GetProperty("RecordedDamage").ValueKind==JsonValueKind.Null,"history availability and nullable damage payload");
                    Check(data.GetProperty("mechanics").GetArrayLength()==1 && data.GetProperty("coachingHistory")[0].GetProperty("smoothness").GetProperty("composite").GetDouble()>99,"mechanics and coaching payload");
                }
                var desktop=new HistoricalMeasurement(run.Id,Overshoot:.123); measurements[run.Id]=desktop;
                Check(pipeline.Join([run with { Smoothness=42 }],measurements)[0].Smoothness==42 && measurements[run.Id]==desktop,"existing measurements retain precedence");
                Check(pipeline.Tick(now.AddSeconds(11)),"older replay backfill");
            }
            using(var pipeline=new MechanicsPipeline(root,catalog,()=>false,false)) Check(!pipeline.Tick(now),"persistent cache hit");
            File.WriteAllText(Path.Combine(root,"mechanics-cache","new.json"),"broken");
            using(var pipeline=new MechanicsPipeline(root,catalog,()=>false,false)) Check(pipeline.Tick(now),"corrupt cache rebuilt");
            File.SetLastWriteTimeUtc(Path.Combine(root,"replays","new.amreplay"),DateTime.UtcNow.AddSeconds(2));
            using(var pipeline=new MechanicsPipeline(root,catalog,()=>false,false)) Check(pipeline.Tick(now),"source stamp invalidation");
            using(var pipeline=new MechanicsPipeline(root,catalog,()=>false,false))
            {
                for(int i=0;i<140;i++) File.WriteAllText(Path.Combine(root,"mechanics-cache","extra"+i+".json"),"{}");
                pipeline.Trim(); Check(Directory.GetFiles(Path.Combine(root,"mechanics-cache"),"*.json").Length<=MechanicsPipeline.MaxRuns,"bounded cache");
            }
        }
        finally { if(Directory.Exists(root)) Directory.Delete(root,true); }
        Console.WriteLine($"Native mechanics checks passed ({checks}); synthetic 10 s replay: {watch.Elapsed.TotalMilliseconds:0.0} ms.");
    }
}

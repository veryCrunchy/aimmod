using AimMod.InGame.Analysis;
using AimMod.InGame.Multiplayer;
namespace AimMod.InGame;
static class PracticeDrillChecks
{
    public static void Run()
    {
        var count=0;
        void Check(bool ok,string label){if(!ok)throw new Exception("Practice check: "+label);count++;}
        const string source="Name=Synthetic\nMapName=Synthetic\nMapScale=1.0\nPlayerProfile=Player\nAddedBots=Target.bot\n\n[Bot Profile]\nName=Target\nCharacterProfile=Target\n\n[Character Profile]\nName=Player\n\n[Character Profile]\nName=Target\n\n[Dodge Profile]\nName=Dodge\n\n[Map Data]\nsynthetic-map\n";
        foreach(var template in new[]{"static","micro","tracking","reactive"}) {
            var spec=new DrillSpec(template,12,1,1,30,"Synthetic");
            var generated=MatchScenario.GenerateDrill(source,spec);
            Check(MatchScenario.Validate(source,generated).Count==0,"valid scenario and unchanged map");
            Check(generated.StartsWith("Name=AimMod Drill - ") && generated.Contains("IsChallenge=false") && generated.Contains("DisableScoring=true"),"freeplay marker and no scoring");
            Check(!spec.RankedEligible && spec.Mode=="freeplay","ranked exclusion");
            Check(MatchScenario.SafeMode(PracticeDrills.Name(spec,source),"challenge")=="freeplay","safe start mode");
        }
        Check(!PracticeDrills.Valid(new("static",double.NaN,0,1,0,"")),"reject nonfinite geometry");
        var folder=Path.Combine(Path.GetTempPath(),"aimmod-drill-check-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        try {
            var commands=new GameCommands(folder);
            Check(commands.Send(new("start-scenario","AimMod Drill - static - 12345678","challenge",null,null,null,null,null)).Error=="drills-freeplay-only","reject drill challenge");
            Check(commands.Send(new("start-scenario","AimMod Drill - static - 12345678","freeplay",null,null,null,null,null,Seed:1)).Error=="drills-freeplay-only","reject seeded drill");
            Check(commands.Send(new("start-scenario","AimMod Match - Synthetic","challenge",null,null,null,null,null,Seed:1)).Error=="seed-not-allowed","match seed exception stays removed");
            var store=new MatchScenarioStore(folder,Path.Combine(folder,"manifest.json"),drills:true);
            var spec=new DrillSpec("static",12,1,1,0,"Synthetic");var name=PracticeDrills.Name(spec,source);
            Check(store.Write(name,MatchScenario.GenerateDrill(source,spec),1).Ok,"install drill");
            Check(store.Clean(null)==1,"cleanup owned drill");
            store.Write(name,MatchScenario.GenerateDrill(source,spec),2);
            File.AppendAllText(Path.Combine(folder,name+".sce"),"user edit");
            Check(store.Clean(null)==0,"preserve edited drill");
            var replay=new NativeReplay(1,"synthetic","Synthetic","2026-01-01","completed",1,
                [new(0,[0,0,0,0,0,0,90],[]),new(1,[0,0,0,0,0,0,90],[])],[]);
            var report=ReplayAnalysis.Analyze(replay);
            Check(PracticeDrills.Recommend(report)==null,"insufficient measurements give no recommendation");
            var tracking=report with {Kind=ScenarioKind.Tracking,Tracking=report.Tracking with {TrackedSeconds=10,MeanTargetSpeed=30,Reversals=5,MeanReversalReactionMs=150}};
            Check(PracticeDrills.Recommend(tracking)?.Template=="reactive","slow reversals recommend reactive tracking");
            Check(PracticeDrills.Recommend(tracking with {Tracking=tracking.Tracking with {Reversals=0}})?.Template=="tracking","steady tracking recommendation");
            Check(PracticeReplay.Build(replay).Length==0,"empty flick data stays empty");
            var moving=replay with { Frames=Enumerable.Range(0,251).Select(i=>{
                double time=i/250.0,yaw=10*MinimumJerk.Position((time-.2)/.2);
                return new ReplayFrame(time,[0,0,0,0,yaw,0,90],[[1,1000,176.327,0,10,10]]);
            }).ToArray(), Inputs=[new(.5,"FirePressed",1),new(.51,"FireReleased",0)] };
            var paths=PracticeReplay.Build(moving);
            Check(paths.Length==1 && paths[0].Recorded.Length<=65 && paths[0].Ideal.Length==33,"bounded nonempty flick paths");
            Check(Math.Abs(paths[0].Ideal[^1][0]-paths[0].Recorded[^1][0])<.02,"ideal ends at target direction");
            var flickReport=ReplayAnalysis.Analyze(moving);
            var sample=flickReport.Flicks.Flicks.Single();
            var weak=flickReport with {Flicks=flickReport.Flicks with {Flicks=Enumerable.Repeat(sample with {Sector=3,Distance=20,OvershootRatio=.3},5).Concat(Enumerable.Repeat(sample with {Sector=0,Distance=10,OvershootRatio=0},5)).ToArray()}};
            Check(PracticeDrills.Recommend(weak) is {Sector:3,Template:"static"},"overshoot map selects measured weak cell");

        } finally {Directory.Delete(folder,true);}
        Console.WriteLine($"{count} drill and viewer checks passed.");
    }
}

using AimMod.InGame.Analysis;
using AimMod.InGame.Multiplayer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AimMod.InGame;

sealed class PracticeDrillHost(ContentLibrary library, IGameControl game, string output)
{
    readonly object gate = new();
    ScenarioLoader? loader;
    MatchScenarioStore? store;
    long? refresh, started;
    string? name;
    string message = "";
    long since;
    public void Tick()
    {
        lock (gate) {
            if (name is null || started is not null) return;
            if (Environment.TickCount64 - since > 60000) { message="Drill start timed out.";name=null;return; }
            if (refresh is long sequence) {
                var result=game.ResultFor(sequence);
                if (result?.State == "error") {message="Scenario refresh failed.";name=null;return;}
                if (result?.State != "done") return;
                refresh=null;
                loader=new(game,()=>Environment.TickCount64,library) {Subject="the practice drill",LogLabel="Drill load"};
                loader.Load(name);
            }
            var status=loader?.Tick();
            message=status?.Message ?? message;
            if (status?.Phase == ScenarioLoadPhase.Loaded) {
                started=game.Start(name,"freeplay");
                message=started is null ? "Freeplay start unavailable." : "Freeplay start requested.";
                name=null;
            } else if (status?.Phase == ScenarioLoadPhase.Failed) name=null;
        }
    }
    public void Map(WebApplication app,string prefix,ReplayCatalog replays)
    {
        app.MapGet(prefix+"/practice-drills/{id}",(string id)=> {
            if(game.ChallengeRunning == true)return Results.Conflict();
            var replay=replays.Read(id);
            if (replay is null) return Results.NotFound();
            var spec=PracticeDrills.Recommend(ReplayAnalysis.Analyze(replay));
            return Results.Json(new {recommendation=spec,mode="freeplay",rankedEligible=false});
        });
        app.MapGet(prefix+"/practice-drills",()=> {lock(gate)return Results.Json(new {message,pending=name is not null,sequence=started,mode="freeplay",rankedEligible=false});});
        app.MapPost(prefix+"/practice-drills/{id}",(HttpContext context,string id)=> {
            if(context.Request.Headers["X-AimMod-UI"]!="1")return Results.StatusCode(403);
            lock(gate) {
                if(name is not null || game.ChallengeRunning != false || !game.Capabilities.Contains("start"))return Results.Conflict(new {error="game-unavailable"});
                var replay=replays.Read(id);
                if(replay is null)return Results.NotFound();
                var spec=PracticeDrills.Recommend(ReplayAnalysis.Analyze(replay));
                if(spec is null)return Results.UnprocessableEntity();
                var source=library.PathOf("scenario",replay.Scenario);
                if(source is null || library.ScenarioFolder is not {} folder)return Results.UnprocessableEntity();
                try {
                    var baseText=File.ReadAllText(source);
                    var text=MatchScenario.GenerateDrill(baseText,spec);
                    var drill=PracticeDrills.Name(spec,baseText);
                    store ??=new(folder,Path.Combine(output,"practice-drills.json"),drills:true);
                    var write=store.Write(drill,text,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    if(!write.Ok)return Results.Conflict(new {error=write.Error});
                    refresh=game.Refresh();
                    if(refresh is null)return Results.Conflict(new {error="refresh-unavailable"});
                    name=drill;started=null;since=Environment.TickCount64;message="Preparing freeplay drill.";
                    return Results.Json(new {name,mode="freeplay",rankedEligible=false},statusCode:202);
                } catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException) {
                    return Results.UnprocessableEntity();
                }
            }
        });
    }
}

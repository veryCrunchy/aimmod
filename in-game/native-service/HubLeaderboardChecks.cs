using System.Net;
using System.Text;
using System.Text.Json;
namespace AimMod.InGame;
static class HubLeaderboardChecks
{
 sealed class Handler:HttpMessageHandler{
  public int Count;
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){
   if(request.RequestUri!.AbsolutePath!="/aimmod.hub.v1.HubService/GetLeaderboard")throw new Exception("Unexpected endpoint");Count++;
   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"records\":[],\"topScores\":[]}",Encoding.UTF8,"application/json")});
  }
 }
 public static async Task Run(){int count=0;void Check(bool ok,string message){if(!ok)throw new Exception(message);count++;}
  using var doc=JsonDocument.Parse("""{"topScores":[{"runId":"synthetic","scenarioName":"Synthetic","userHandle":"tester","userDisplayName":"Test player","durationMs":"60000","playedAtIso":"2026-01-01T00:00:00Z"},{"runId":"invalid"}]}""");
  var rows=Hub.LeaderboardEntries(doc.RootElement,"topScores",100);Check(rows is[{Score:0,Duration:60,Player:"Test player"}],"Validated protobuf default score and rejected invalid row");
  Check(Hub.LeaderboardEntries(doc.RootElement,"records",100).Length==0,"Empty list valid");
  var folder=Path.Combine(Path.GetTempPath(),"aimmod-hub-leaderboard-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
  try{var handler=new Handler();using var hub=new Hub(folder,handler);Check(await hub.Leaderboard("",default) is{Cached:false},"Public leaderboard requires no linked account");Check(await hub.Leaderboard("",default) is{Cached:true}&&handler.Count==1,"Repeated request uses cached feed");Check(await hub.Leaderboard("../arbitrary",default) is null&&handler.Count==1,"Invalid filter never requested");}finally{Directory.Delete(folder,true);}
  Console.WriteLine(count+" Hub leaderboard checks passed.");
 }
}

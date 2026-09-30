using System.Net;
using System.Text;
using System.Text.Json;
namespace AimMod.InGame;
static class HubPaginationChecks
{
 sealed class Clock:TimeProvider{public DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now;}
 sealed class Handler(bool legacy):HttpMessageHandler{
  public int Pages,Rejected;
  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken token){
   var body=await r.Content!.ReadAsStringAsync(token);var path=r.RequestUri!.AbsolutePath;string json;
   if(path.EndsWith("GetProfile"))json="""{"userHandle":"synthetic","userExternalId":"test-id","runCount":2,"topScenarios":[{"scenarioSlug":"scenario","runCount":2}]}""";
   else if(path.EndsWith("GetPlayerScenarioHistory")){
    if(legacy&&body.Contains("limit")){Rejected++;return new(HttpStatusCode.BadRequest);}
    Pages++;var next=body.Contains("page-two");
    json="{\"runs\":[{\"runId\":\""+(next?"second":"first")+"\",\"userHandle\":\"synthetic\",\"scenarioName\":\"Scenario\",\"score\":1,\"durationMs\":\"60000\",\"playedAtIso\":\"2026-01-01T00:00:00Z\"}]"+(legacy?"}":",\"paginationSupported\":true"+(next?"}":",\"nextCursor\":\"page-two\"}"));
   }else throw new Exception("Unexpected pagination route");
   return new(HttpStatusCode.OK){Content=new StringContent(json,Encoding.UTF8,"application/json")};
  }
 }
 public static void Run(){int count=0;void Check(bool v){if(!v)throw new Exception("History pagination check failed");count++;}
 using var old=JsonDocument.Parse("{}");var pages=new HubPagination();Check(!pages.Advance(old.RootElement)&&!pages.Supported);
 using var first=JsonDocument.Parse("{\"paginationSupported\":true,\"nextCursor\":\"first\"}");Check(pages.Advance(first.RootElement)&&pages.Cursor=="first");
 try{pages.Advance(first.RootElement);throw new Exception("Repeated cursor accepted");}catch(IOException){count++;}
 using var end=JsonDocument.Parse("{\"paginationSupported\":true}");Check(!pages.Advance(end.RootElement)&&pages.Supported);
 foreach(var legacy in new[]{false,true}){
  var folder=Path.Combine(Path.GetTempPath(),"aimmod-pagination-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
  try{AccountVault.Save(Path.Combine(folder,"account.bin"),new HubAccount("synthetic","Synthetic","test-id","test-token"));var handler=new Handler(legacy);var clock=new Clock();using var hub=new Hub(folder,handler,clock);hub.Tick(default).GetAwaiter().GetResult();hub.Tick(default).GetAwaiter().GetResult();clock.Now=clock.Now.AddSeconds(3);hub.Tick(default).GetAwaiter().GetResult();Check(hub.Runs.Count==(legacy?1:2));Check(handler.Pages==(legacy?1:2)&&handler.Rejected==(legacy?1:0));}
  finally{Directory.Delete(folder,true);}
 }
 Console.WriteLine(count+" Hub pagination checks passed.");
 }
}


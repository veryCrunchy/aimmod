using System.Text.Json;
namespace AimMod.InGame;
record HubLeaderboardEntry(string RunId,string Scenario,string ScenarioType,string Player,string Handle,double Score,double? Accuracy,double Duration,string Timestamp);
record HubLeaderboardPage(string ScenarioType,HubLeaderboardEntry[] Records,HubLeaderboardEntry[] TopScores,DateTimeOffset RetrievedAt,bool Cached=false);
sealed partial class Hub
{
 readonly SemaphoreSlim leaderboardGate=new(1,1);
 readonly Dictionary<string,HubLeaderboardPage> leaderboardPages=new(StringComparer.Ordinal);
 // Verified HubService.GetLeaderboard returns per-scenario records and top 100
 // scores. Neither this RPC nor GetPlayerScenarioHistory supports pagination.
 public async Task<HubLeaderboardPage?> Leaderboard(string scenarioType,CancellationToken token)
 {
  if(scenarioType.Length>64||scenarioType.Any(c=>!(char.IsAsciiLetterOrDigit(c)||c is ' ' or '-' or '_')))return null;
  await leaderboardGate.WaitAsync(token);
  try{
   var now=clock.GetUtcNow();
   if(leaderboardPages.TryGetValue(scenarioType,out var saved)&&now-saved.RetrievedAt<TimeSpan.FromMinutes(5))return saved with{Cached=true};
   // Honor a Hub Retry-After for UI-driven requests too; serve stale data if any.
   if(now<retryAfter){if(saved is not null)return saved with{Cached=true};throw new HttpRequestException("Hub requested a retry delay.");}
   try{
    using var doc=await Rpc("GetLeaderboard",new{scenarioType},token);
    var page=new HubLeaderboardPage(scenarioType,LeaderboardEntries(doc.RootElement,"records",4096),LeaderboardEntries(doc.RootElement,"topScores",100),now);
    if(leaderboardPages.Count>=16)leaderboardPages.Remove(leaderboardPages.MinBy(p=>p.Value.RetrievedAt).Key);
    leaderboardPages[scenarioType]=page;return page;
   }catch(Exception e)when(e is HttpRequestException or IOException or JsonException){if(saved is not null)return saved with{Cached=true};throw;}
  }finally{leaderboardGate.Release();}
 }
 internal static HubLeaderboardEntry[] LeaderboardEntries(JsonElement root,string key,int limit)
 {
  if(!root.TryGetProperty(key,out var array)||array.ValueKind!=JsonValueKind.Array)return [];
  var rows=new List<HubLeaderboardEntry>();
  foreach(var e in array.EnumerateArray().Take(limit)){
   if(e.ValueKind!=JsonValueKind.Object)continue;
   var handle=HubHistory.Text(e,"userHandle");var run=HubHistory.Parse(e,handle);
   if(run is null||handle.Length is 0 or >256||run.Scenario.Length>512||run.Id.Length>512||Math.Abs(run.Score)>=1e12)continue;
   var player=HubHistory.Text(e,"userDisplayName");if(player.Length>256)player=player[..256];
   var type=HubHistory.Text(e,"scenarioType");if(type.Length>64)type=type[..64];
   rows.Add(new(run.Id,run.Scenario,type,player.Length>0?player:handle,handle,run.Score,run.Accuracy,run.Duration,run.Timestamp));
  }
  return rows.DistinctBy(r=>r.RunId).ToArray();
 }
}

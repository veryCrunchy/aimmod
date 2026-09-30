using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace AimMod.InGame;
record ObservedOpponent(string Key,string Name,double Score,int Rank,string Scenario,string Source,string? SteamId=null,DateTime ObservedAtUtc=default,bool Cached=false);
record OpponentOptions(string SelectedKey,IReadOnlyList<ObservedOpponent> Rows);
sealed class OpponentData
{
 readonly string output; string selected=""; readonly object gate=new();
 readonly Dictionary<string,ObservedOpponent> cache=new(StringComparer.Ordinal);
 DateTime lastRead,lastSaved; const int MaximumRows=2048;
 static readonly JsonSerializerOptions Json=new(){PropertyNameCaseInsensitive=true};
 static string Identity(string name,string? steamId)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(steamId is null?"name:"+name:"steam:"+steamId)));
 static string Slot(ObservedOpponent row)=>row.Key+"\n"+row.Scenario;
 static bool Valid(ObservedOpponent row)=>!string.IsNullOrWhiteSpace(row.Name)&&row.Name.Length<=256&&!string.IsNullOrWhiteSpace(row.Scenario)&&row.Scenario.Length<=512&&double.IsFinite(row.Score)&&Math.Abs(row.Score)<1e12&&row.Rank>0&&row.Source is "friends" or "leaderboard";
 static string? Steam(string? value)=>value is {Length:17}&&value.All(char.IsAsciiDigit)&&value!="00000000000000000"?value:null;
 public OpponentData(string output)
 {
  this.output=output;
  try {
   var p=Path.Combine(output,"selected-opponent.txt");if(File.Exists(p)&&new FileInfo(p).Length<=64)selected=File.ReadAllText(p).Trim();
   p=Path.Combine(output,"opponent-cache.json");
   if(File.Exists(p)&&new FileInfo(p).Length<=2097152)
    foreach(var row in (JsonSerializer.Deserialize<ObservedOpponent[]>(File.ReadAllText(p),Json)??[]).Take(MaximumRows))
     if(Valid(row)&&row.Key==Identity(row.Name,Steam(row.SteamId))&&row.ObservedAtUtc<=DateTime.UtcNow.AddSeconds(1)&&row.ObservedAtUtc>DateTime.UtcNow.AddDays(-30))cache[Slot(row)]=row with{Cached=true};
  }catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException){}
 }
 public OpponentOptions Read()
 {
  lock(gate){
   try {
    var f=new FileInfo(Path.Combine(output,"live-opponents.json"));
    if(f.Exists&&f.Length<=65536&&f.LastWriteTimeUtc!=lastRead&&DateTime.UtcNow-f.LastWriteTimeUtc<=TimeSpan.FromSeconds(5)&&f.LastWriteTimeUtc<=DateTime.UtcNow.AddSeconds(1)){
     using var doc=JsonDocument.Parse(File.ReadAllText(f.FullName));var r=doc.RootElement;
     var scenario=r.GetProperty("scenario").GetString()??"";var source=r.GetProperty("source").GetString()??"";
     var changed=false;
     foreach(var item in r.GetProperty("rows").EnumerateArray().Take(128)){
      var name=item.GetProperty("name").GetString()??"";var score=item.GetProperty("score").GetDouble();var rank=item.GetProperty("rank").GetInt32();
      var steam=Steam(item.TryGetProperty("steamId",out var id)&&id.ValueKind==JsonValueKind.String?id.GetString():null);
      var row=new ObservedOpponent(Identity(name,steam),name,score,rank,scenario,source,steam,f.LastWriteTimeUtc);
      if(!Valid(row))continue;
      // Upgrade the previous rank-dependent selection without guessing an identity.
      var old=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scenario+"\n"+name+"\n"+rank)));
      if(selected==old)SaveSelection(row.Key);
      var slot=Slot(row);
      if(!cache.TryGetValue(slot,out var previous)||previous.Name!=row.Name||previous.Score!=row.Score||previous.Rank!=row.Rank||previous.Source!=row.Source)changed=true;
      cache[slot]=row;
     }
     lastRead=f.LastWriteTimeUtc;
     foreach(var key in cache.OrderByDescending(x=>x.Value.ObservedAtUtc).Skip(MaximumRows).Select(x=>x.Key).ToArray())cache.Remove(key);
     if(changed||DateTime.UtcNow-lastSaved>TimeSpan.FromMinutes(1)){SaveCache();lastSaved=DateTime.UtcNow;}
    }
   }catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException){}
   var now=DateTime.UtcNow;
   return new(selected,cache.Values.Where(r=>now-r.ObservedAtUtc<TimeSpan.FromDays(30)).OrderByDescending(r=>r.ObservedAtUtc).Select(r=>r with{Cached=now-r.ObservedAtUtc>TimeSpan.FromSeconds(5)}).ToArray());
  }
 }
 void SaveCache(){try{var p=Path.Combine(output,"opponent-cache.json");File.WriteAllText(p+".next",JsonSerializer.Serialize(cache.Values));File.Move(p+".next",p,true);}catch(Exception e)when(e is IOException or UnauthorizedAccessException){}}
 bool SaveSelection(string key){try{var p=Path.Combine(output,"selected-opponent.txt");File.WriteAllText(p+".next",key);File.Move(p+".next",p,true);selected=key;return true;}catch(Exception e)when(e is IOException or UnauthorizedAccessException){return false;}}
 public bool Select(string key){lock(gate){return (key==""||Read().Rows.Any(r=>r.Key==key))&&SaveSelection(key);}}
 public LiveOverlaySnapshot Apply(LiveOverlaySnapshot live){var options=Read();if(options.SelectedKey=="")return live;var row=options.Rows.FirstOrDefault(r=>r.Key==options.SelectedKey&&r.Scenario.Equals(live.Scenario,StringComparison.Ordinal));return live with{OpponentScore=row?.Score,ProjectedDelta=row is null?null:live.ProjectedScore-row.Score,OpponentName=row?.Name??"Selected opponent",OpponentSource=row is null?"unavailable":row.Cached?"cached-"+row.Source:row.Source};}
}

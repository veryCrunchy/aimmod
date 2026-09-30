using System.Text.Json;
namespace AimMod.InGame;
sealed class HubPagination
{
 readonly HashSet<string> seen=new(StringComparer.Ordinal);int pages;
 public string Cursor{get;private set;}="";
 public bool Supported{get;private set;}
 public bool Advance(JsonElement root){
  Supported=root.TryGetProperty("paginationSupported",out var supported)&&supported.ValueKind==JsonValueKind.True;
  var next=HubHistory.Text(root,"nextCursor");
  if(!Supported||next.Length==0){Cursor="";return false;}
  if(next.Length>2048||++pages>2000||!seen.Add(next))throw new IOException("Invalid repeating history page.");
  Cursor=next;return true;
 }
}

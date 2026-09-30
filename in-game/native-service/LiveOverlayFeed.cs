namespace AimMod.InGame;
// Brief transport/phase gaps may reuse a same-attempt value, never extend it.
sealed class LiveOverlayFeed
{
 readonly object gate=new();LiveOverlaySnapshot? last;DateTime lastGood;
 public LiveOverlaySnapshot Read(string output,IEnumerable<Run> history)=>Accept(LiveOverlayState.Read(output,history),DateTime.UtcNow);
 internal LiveOverlaySnapshot Accept(LiveOverlaySnapshot value,DateTime now){lock(gate){
 if(value.Replay||value.Paused||(value.Available&&!value.Active)){last=null;return value;}
 if(!value.Available||value.Transient){if(last is not null&&now>=lastGood&&now-lastGood<=TimeSpan.FromMilliseconds(200)&&(!value.Transient||value.AttemptId==last.AttemptId))return last;last=null;return value with{Active=false};}
 if(value.Active){last=value;lastGood=now;}return value;
 }}
}

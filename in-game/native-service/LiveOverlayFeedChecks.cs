namespace AimMod.InGame;
static class LiveOverlayFeedChecks
{
 public static void Run(){int checks=0;void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
 var good=new LiveOverlaySnapshot(true,true,false,"Synthetic",100,10,20,10,0,.1,50,null,null,null,null,null,AttemptId:"one");var now=DateTime.UtcNow;var feed=new LiveOverlayFeed();
 feed.Accept(good,now);Check(feed.Accept(good with{Transient=true,Score=null},now.AddMilliseconds(100)).Score==100,"Same attempt bridges transient sample");
 Check(!feed.Accept(good with{Transient=true},now.AddMilliseconds(201)).Active,"Repeated gaps do not renew grace");
 feed.Accept(good,now);Check(feed.Accept(good with{Available=false,Active=false},now.AddMilliseconds(100)).Score==100,"Brief file replacement gap retains state");
 foreach(var stop in new[]{good with{Replay=true,Active=false,Available=false},good with{Paused=true},good with{Active=false}}){feed.Accept(good,now);Check(feed.Accept(stop,now.AddMilliseconds(1))==stop,"Explicit state hides immediately");Check(!feed.Accept(good with{Transient=true},now.AddMilliseconds(2)).Active,"Stop clears retained state");}
 feed.Accept(good,now);Check(!feed.Accept(good with{Transient=true,AttemptId="two"},now.AddMilliseconds(1)).Active,"Cannot carry score into another attempt");
 feed.Accept(good,now);Check(!feed.Accept(good with{Transient=true},now.AddMilliseconds(-1)).Active,"Clock reversal expires retention");
 Console.WriteLine($"PASS {checks} live feed checks"); }
}

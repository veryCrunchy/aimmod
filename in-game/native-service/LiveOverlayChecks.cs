namespace AimMod.InGame;
static class LiveOverlayChecks
{
 public static void Run(){var folder=Path.Combine(Path.GetTempPath(),"aimmod-live-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);var path=Path.Combine(folder,"live-overlay.json");int checks=0;void Check(bool b,string message){checks++;if(!b)throw new Exception(message);}try{
 var runs=new[]{new Run("pb","Synthetic",500,80,60,3,1,"2026-01-01",null,null,null,null,false),new Run("other","Other",9000,80,60,3,1,"2026-01-01",null,null,null,null,false)};
 Check(!LiveOverlayState.Read(folder,runs).Available,"Missing live file remains unavailable");
 File.WriteAllText(path,"{\"version\":1,\"active\":true,\"paused\":false,\"scenario\":\"Synthetic\",\"score\":100,\"seconds\":10,\"shots\":20,\"hits\":10}");
 var state=LiveOverlayState.Read(folder,runs);Check(state.Active&&state.Accuracy==50,"Measured live values preserve percentage accuracy");Check(state.PersonalBest==500&&state.ProjectedScore==600&&state.ProjectedDelta==100&&state.ProjectionEstimated,"PB projection uses exact scenario and actual recorded duration");
 Check(LiveOverlayState.Read(folder,[]).ProjectedScore is null,"No guessed duration when opponent missing");
 File.WriteAllText(path,"{\"version\":1,\"active\":true,\"scenario\":\"Synthetic\",\"score\":100,\"seconds\":10,\"kills\":2,\"remainingSeconds\":60,\"lastTimeToKillSeconds\":3}");
 var measured=LiveOverlayState.Read(folder,runs);Check(measured is {ScorePerMinute:600,KillsPerSecond:.2,RemainingSeconds:60,DurationSeconds:70,LastTimeToKillSeconds:3},"Derived pace and authoritative remaining and last TTK stay distinct");
 Check(measured.ProjectedScore==700,"Native current duration takes priority over previous run duration");
 File.WriteAllText(path,"{\"version\":1,\"active\":true,\"scenario\":\"Synthetic\",\"score\":0,\"seconds\":0,\"kills\":0,\"lastTimeToKillSeconds\":3}");
 Check(LiveOverlayState.Read(folder,runs) is {ScorePerMinute:null,KillsPerSecond:null,RemainingSeconds:null,LastTimeToKillSeconds:null},"No pace division by zero or stale previous TTK without a current kill");
 File.SetLastWriteTimeUtc(path,DateTime.UtcNow.AddSeconds(-4));Check(!LiveOverlayState.Read(folder,runs).Active,"Stale live state hidden");File.SetLastWriteTimeUtc(path,DateTime.UtcNow);
 File.WriteAllText(Path.Combine(folder,"replay-frame.tsv"),"AIMMOD_REPLAY_3\t1\t1\n");Check(!LiveOverlayState.Read(folder,runs).Active,"Replay suppresses live competition state");File.Delete(Path.Combine(folder,"replay-frame.tsv"));
 File.WriteAllText(path,"{\"version\":1,\"active\":true,\"scenario\":\"Synthetic\",\"seconds\":10}");Check(LiveOverlayState.Read(folder,runs).ProjectedScore is null,"Missing score never synthesized from hit count");
 File.WriteAllText(path,"{\"version\":1,\"active\":false}");Check(LiveOverlayState.Read(folder,runs) is {Available:true,Active:false},"Inactive snapshot remains available without display");
 File.WriteAllText(path,"{");Check(!LiveOverlayState.Read(folder,runs).Available,"Torn snapshot fails closed");
 var opponentFile=Path.Combine(folder,"live-opponents.json");File.WriteAllText(opponentFile,"{\"scenario\":\"Synthetic\",\"source\":\"friends\",\"rows\":[{\"name\":\"Synthetic friend\",\"score\":700,\"rank\":2}]}");
 var opponents=new OpponentData(folder);var row=opponents.Read().Rows.Single();Check(opponents.Select(row.Key),"Only observed opponent can be selected");Check(opponents.Apply(state) is {OpponentScore:700,ProjectedDelta:-100,OpponentSource:"friends"},"Selected observed friend score replaces PB comparison");
 Check(opponents.Apply(state with{Scenario="Other"}).OpponentScore is null,"Wrong scenario never falls back to PB opponent");
 File.SetLastWriteTimeUtc(opponentFile,DateTime.UtcNow.AddSeconds(-10));Check(opponents.Apply(state).OpponentScore==700,"Observed score survives absent live leaderboard");Check(!opponents.Select("invented"),"Reject unobserved opponent identity");Check(opponents.Select("")&&opponents.Apply(state).OpponentScore==500,"Explicit PB selection restores PB comparison");
 File.WriteAllText(opponentFile,"{\"scenario\":\"Synthetic\",\"source\":\"friends\",\"rows\":[{\"name\":\"Synthetic friend\",\"score\":750,\"rank\":8}]}");
 Check(opponents.Read().Rows.Single().Key==row.Key,"Name identity survives rank change");Check(opponents.Select(row.Key)&&opponents.Apply(state).OpponentScore==750,"Changed rank updates selected score");
 File.Delete(opponentFile);var restored=new OpponentData(folder);Check(restored.Apply(state).OpponentScore==750,"Persisted selection and actual score survive restart");
 Check(restored.Apply(state with{Scenario="synthetic"}).OpponentScore is null,"Scenario identity is exact");
 File.WriteAllText(opponentFile,"{\"scenario\":\"Synthetic\",\"source\":\"friends\",\"rows\":[{\"name\":\"Synthetic renamed\",\"steamId\":\"76561198000000001\",\"score\":800,\"rank\":1}]}");
 var steam=restored.Read().Rows.Single(r=>r.SteamId!=null);restored.Select(steam.Key);
 File.WriteAllText(opponentFile,"{\"scenario\":\"Synthetic\",\"source\":\"friends\",\"rows\":[{\"name\":\"Synthetic second name\",\"steamId\":\"76561198000000001\",\"score\":850,\"rank\":3}]}");File.SetLastWriteTimeUtc(opponentFile,DateTime.UtcNow.AddMilliseconds(100));
 Check(restored.Apply(state) is{OpponentScore:850,OpponentName:"Synthetic second name"},"Steam identity survives renamed player and changed rank");
 Console.WriteLine(checks+" live overlay checks passed.");
 }finally{Directory.Delete(folder,true);}}
}


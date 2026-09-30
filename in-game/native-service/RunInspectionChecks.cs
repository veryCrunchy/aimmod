using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace AimMod.InGame;
static class RunInspectionChecks
{
    [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)]string path,out nint db,int flags,nint vfs);
    [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_close_v2(nint db);
    [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_exec(nint db,[MarshalAs(UnmanagedType.LPUTF8Str)]string sql,nint callback,nint context,out nint error);
    [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)]static extern void sqlite3_free(nint pointer);
    public static void Run()
    {
        var path=Path.Combine(Path.GetTempPath(),"aimmod-inspection-check-"+Guid.NewGuid().ToString("N")+".sqlite3");
        int checks=0;void Check(bool c,string reason){checks++;if(!c)throw new Exception(reason);}
        nint db=0;
        try
        {
            Check(sqlite3_open_v2(path,out db,6,0)==0,"Create synthetic fixture");
            void Exec(string sql){var code=sqlite3_exec(db,sql,0,0,out var error);if(error!=0)sqlite3_free(error);Check(code==0,"Initialize synthetic schema");}
            Exec("""
                CREATE TABLE sessions(id TEXT PRIMARY KEY,scenario TEXT,score REAL,accuracy REAL,duration_secs REAL,timestamp TEXT);
                INSERT INTO sessions VALUES('a','Synthetic scenario',42,0,60,'2026-01-01T00:00:00Z');
                INSERT INTO sessions VALUES('b','Other scenario',999,100,60,'2026-01-01T00:00:00Z');
                CREATE TABLE session_shot_events(session_id TEXT,shot_seq_idx INTEGER,event_kind TEXT,ts_ms INTEGER,count INTEGER,total INTEGER);
                CREATE TABLE session_shot_targets(session_id TEXT,shot_seq_idx INTEGER,target_idx INTEGER,profile TEXT,distance_3d REAL,yaw_error_deg REAL,pitch_error_deg REAL,is_nearest INTEGER);
                INSERT INTO session_shot_targets VALUES('a',0,0,'Other target',200,12,8,0);
                INSERT INTO session_shot_targets VALUES('a',0,10,'Nearest target',100,2,1,1);
                INSERT INTO session_shot_targets VALUES('b',0,0,'Foreign target',1,0,0,1);
                CREATE TABLE session_target_response_episodes(session_id TEXT,episode_idx INTEGER,kind TEXT,start_ms INTEGER,end_ms INTEGER,target_label TEXT,trigger_magnitude_deg REAL,peak_yaw_error_deg REAL,reaction_time_ms REAL,pre_slowdown_reaction_ms REAL,recovery_time_ms REAL,stable_response INTEGER);
                INSERT INTO session_target_response_episodes VALUES('a',0,'path_change',100,500,'Synthetic target',20,8,130,NULL,240,1);
                INSERT INTO session_target_response_episodes VALUES('a',1,'target_switch',500,400,'Invalid interval',20,8,130,NULL,240,1);
                CREATE TABLE session_replay_context_windows(session_id TEXT,window_idx INTEGER,context_kind TEXT,label TEXT,phase TEXT,start_ms INTEGER,end_ms INTEGER,fired_count INTEGER,hit_count INTEGER,accuracy_pct REAL,primary_target_label TEXT,primary_target_share REAL,avg_nearest_distance REAL,avg_nearest_yaw_error_deg REAL,avg_nearest_pitch_error_deg REAL,avg_score_per_minute REAL,avg_kills_per_second REAL);
                INSERT INTO session_replay_context_windows VALUES('a',0,'metric_shift','Opening','opening',0,5000,5,0,0,'Synthetic target',1,100,2,1,42,0);
                """);
            var inserts=new System.Text.StringBuilder();
            for(int i=0;i<101;i++)inserts.Append($"INSERT INTO session_shot_events VALUES('a',{i},'shot_fired',{10000+i*10},1,{i+1});");
            inserts.Append("INSERT INTO session_shot_events VALUES('b',0,'shot_hit',1,100,100);");Exec(inserts.ToString());
            sqlite3_close_v2(db);db=0;
            var before=SHA256.HashData(File.ReadAllBytes(path));
            var first=RunInspection.Read(path,"a");
            Check(first.Run?.Score==42&&first.Run.Accuracy==0,"Exact selected run and legitimate zero accuracy");
            Check(first.ShotCount==101&&first.Shots.Length==100&&first.ShotPages==2,"Shot pagination includes exact total");
            Check(first.Shots[0].Targets.Count==2&&first.Shots[0].Targets[0].Label=="Nearest target","Nearest target retained even beyond first eight target indices");
            Check(first.Shots.All(s=>s.Targets.All(t=>t.Label!="Foreign target")),"No cross-run target leakage");
            Check(first.EpisodeCount==2&&first.Episodes.Length==1&&first.Episodes[0].PreSlowdownReactionMs is null,"Reject negative response interval and retain unknown metric");
            Check(first.Windows.Length==1&&first.Windows[0].Accuracy==0,"Preserved analysis windows are readable without any replay tables");
            Check(first.Response is null&&first.UnavailableTables.Contains("session_target_response_summaries"),"Older missing tables fail independently");
            var last=RunInspection.Read(path,"a",999999);
            Check(last.ShotPage==1&&last.Shots.Length==1&&last.Shots[0].Sequence==100&&last.FirstShotTimestampMs==10000,"Clamped last page preserves global time origin");
            Check(RunInspection.Read(path,"a' OR 1=1 --").Run is null,"Run IDs are bound parameters");
            Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))),"Analysis leaves source database bytes unchanged");
            Console.WriteLine($"{checks} run inspection checks passed.");
        }
        finally{if(db!=0)sqlite3_close_v2(db);File.Delete(path);}
    }
}

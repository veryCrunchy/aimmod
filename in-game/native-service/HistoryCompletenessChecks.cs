using System.Runtime.InteropServices;
namespace AimMod.InGame;
static class HistoryCompletenessChecks
{
 [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)]string path,out nint db,int flags,nint vfs);
 [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_close_v2(nint db);
 [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_exec(nint db,[MarshalAs(UnmanagedType.LPUTF8Str)]string sql,nint callback,nint context,nint error);
 public static void Run(){var folder=Path.Combine(Path.GetTempPath(),"aimmod-history-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);var path=Path.Combine(folder,"history.sqlite3");nint db=0;try{
 if(sqlite3_open_v2(path,out db,6,0)!=0)throw new Exception("Synthetic database open failed");
 var sql="""
 CREATE TABLE sessions(id TEXT,scenario TEXT,score REAL,accuracy REAL,duration_secs REAL,kills REAL,damage_done REAL,timestamp TEXT,smoothness_json TEXT,has_replay INTEGER);
 WITH RECURSIVE n(i) AS(SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<5105)
 INSERT INTO sessions SELECT printf('run-%06d',i),'Synthetic',i,80,60,1,1,printf('2026-01-%02d',1+(i%28)),'',0 FROM n;
 CREATE TABLE session_stats_panels(session_id TEXT,avg_kps REAL,avg_ttk_ms REAL,best_ttk_ms REAL,ttk_std_ms REAL,accuracy_trend REAL);
 INSERT INTO session_stats_panels VALUES('run-000028',2,500,400,20,0);
 """;
 if(sqlite3_exec(db,sql,0,0,0)!=0)throw new Exception("Synthetic database seed failed");sqlite3_close_v2(db);db=0;
 var before=System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path));var runs=History.Read(path);
 if(runs.Count!=5105||!runs.Any(r=>r.Id=="run-000028"))throw new Exception("Older history truncated");
 var measurements=HistoricalMeasurements.Read(path,runs.Select(r=>r.Id).ToHashSet(StringComparer.Ordinal));
 if(!measurements.TryGetValue("run-000028",out var metric)||metric.AverageTimeToKillMs!=500)throw new Exception("Older measurements truncated");
 if(!before.SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))))throw new Exception("Read changed database");
 Console.WriteLine("3 complete history checks passed.");
 }finally{if(db!=0)sqlite3_close_v2(db);Directory.Delete(folder,true);}}
}

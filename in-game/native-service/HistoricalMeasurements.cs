using System.Runtime.InteropServices;

namespace AimMod.InGame;

record HistoricalMeasurement(string RunId, double? Overshoot = null, double? SpeedVariation = null,
    double? AverageSpeed = null, double? ClickTimingVariation = null, double? DirectionalBias = null,
    double? KillsPerSecond = null, double? AverageTimeToKillMs = null, double? BestTimeToKillMs = null,
    double? TimeToKillSpreadMs = null, double? AccuracyTrend = null,
    double? AverageFireToHitMs = null, double? P90FireToHitMs = null, double? AverageShotsToHit = null, double? CorrectiveShotRatio = null);

// Supplementary desktop measurements, joined by exact local run ID. This keeps
// TTK distinct from fire-to-hit latency and preserves absent values as unknown.
static class HistoricalMeasurements
{
    [DllImport("winsqlite3", CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path,out nint db,int flags,nint vfs);
    [DllImport("winsqlite3", CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_close_v2(nint db);
    [DllImport("winsqlite3", CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_busy_timeout(nint db,int milliseconds);
    [DllImport("winsqlite3", CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_prepare_v2(nint db,[MarshalAs(UnmanagedType.LPUTF8Str)] string sql,int bytes,out nint statement,nint tail);
    [DllImport("winsqlite3", CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_step(nint statement);
    [DllImport("winsqlite3", CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_finalize(nint statement);
    [DllImport("winsqlite3", CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_column_type(nint statement,int column);
    [DllImport("winsqlite3", CallingConvention=CallingConvention.Cdecl)] static extern nint sqlite3_column_text(nint statement,int column);
    [DllImport("winsqlite3", CallingConvention=CallingConvention.Cdecl)] static extern double sqlite3_column_double(nint statement,int column);
    [DllImport("winsqlite3", CallingConvention=CallingConvention.Cdecl)] static extern nint sqlite3_errmsg(nint db);
    public static IReadOnlyDictionary<string,HistoricalMeasurement> Read(string path,IReadOnlySet<string> runIds)
    {
        var result=new Dictionary<string,HistoricalMeasurement>(StringComparer.Ordinal);
        if(runIds.Count==0 || !File.Exists(path))return result;
        nint db=0;
        try
        {
            if(sqlite3_open_v2(path,out db,1,0)!=0)throw new IOException("Historical measurements unavailable.");
            sqlite3_busy_timeout(db,750);
            Query("session_smoothness", "m.overshoot_rate,m.velocity_std,m.avg_speed,m.click_timing_cv,m.directional_bias", (s,m)=>m with {
                Overshoot=Ratio(s,1),SpeedVariation=Positive(s,2),AverageSpeed=Positive(s,3),ClickTimingVariation=Positive(s,4),DirectionalBias=Ratio(s,5) });
            Query("session_stats_panels", "m.avg_kps,m.avg_ttk_ms,m.best_ttk_ms,m.ttk_std_ms,m.accuracy_trend", (s,m)=>m with {
                KillsPerSecond=Positive(s,1),AverageTimeToKillMs=Positive(s,2),BestTimeToKillMs=Positive(s,3),TimeToKillSpreadMs=Positive(s,4),AccuracyTrend=Number(s,5) });
            Query("session_shot_timings", "m.avg_fire_to_hit_ms,m.p90_fire_to_hit_ms,m.avg_shots_to_hit,m.corrective_shot_ratio", (s,m)=>m with {
                AverageFireToHitMs=Positive(s,1),P90FireToHitMs=Positive(s,2),AverageShotsToHit=Positive(s,3),CorrectiveShotRatio=Ratio(s,4) });
            return result;
            void Query(string table,string fields,Func<nint,HistoricalMeasurement,HistoricalMeasurement> convert)
            {
                nint statement=0;
                try
                {
                    // Same bounded local history window as History.Read; the
                    // index orders sessions before optional scalar-table lookup.
                    var sql=$"SELECT s.id,{fields} FROM sessions s JOIN {table} m ON m.session_id=s.id";
                    var prepared=sqlite3_prepare_v2(db,sql,-1,out statement,0);
                    if(prepared!=0)
                    {
                        var error=Marshal.PtrToStringUTF8(sqlite3_errmsg(db))??"";
                        if(error.StartsWith("no such table:",StringComparison.Ordinal)||error.StartsWith("no such column:",StringComparison.Ordinal))return;
                        throw new IOException("Historical measurements query unavailable.");
                    }
                    int step;
                    while((step=sqlite3_step(statement))==100)
                    {
                        var id=Marshal.PtrToStringUTF8(sqlite3_column_text(statement,0))??"";
                        if(runIds.Contains(id))result[id]=convert(statement,result.GetValueOrDefault(id)??new HistoricalMeasurement(id));
                    }
                    if(step!=101)throw new IOException("Historical measurements query incomplete.");
                }
                finally{if(statement!=0)sqlite3_finalize(statement);}
            }
        }
        finally{if(db!=0)sqlite3_close_v2(db);}
    }
    static double? Number(nint s,int column)
    {
        if(sqlite3_column_type(s,column) is not (1 or 2))return null;
        var n=sqlite3_column_double(s,column);return double.IsFinite(n)&&Math.Abs(n)<=1e12?n:null;
    }
    static double? Positive(nint s,int column)=>Number(s,column) is double n && n>=0?n:null;
    static double? Ratio(nint s,int column)=>Number(s,column) is double n && n>=0&&n<=1?n:null;
}

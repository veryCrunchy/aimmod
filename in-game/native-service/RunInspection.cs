using System.Runtime.InteropServices;

namespace AimMod.InGame;

record InspectedRun(string Id,string Scenario,double? Score,double? Accuracy,double? Duration,string Timestamp);
record ResponseSummary(double? Episodes,double? PathChanges,double? TargetSwitches,double? CoveragePercent,
    double? ReactionMs,double? ReactionP90Ms,double? PreSlowdownReactionMs,double? RecoveryMs,double? RecoveryP90Ms,
    double? PathChangeReactionMs,double? TargetSwitchReactionMs,double? TriggerDegrees,double? PeakYawDegrees,double? StableRatio);
record ResponseEpisode(string Kind,double StartMs,double EndMs,string Target,double? TriggerDegrees,double? PeakYawDegrees,
    double? ReactionMs,double? PreSlowdownReactionMs,double? RecoveryMs,bool Stable);
record AnalysisWindow(string Kind,string Label,string Phase,double StartMs,double EndMs,double? Fired,double? Hits,double? Accuracy,
    string Target,double? TargetShare,double? Distance,double? YawErrorDegrees,double? PitchErrorDegrees,double? ScorePerMinute,double? KillsPerSecond);
record ShotTarget(string Label,double? Distance,double? YawErrorDegrees,double? PitchErrorDegrees,bool Nearest);
record InspectedShot(int Sequence,string Kind,double TimestampMs,double? Count,double? Total,List<ShotTarget> Targets);
record InspectionResult(InspectedRun? Run,RunDetails Details,ResponseSummary? Response,ResponseEpisode[] Episodes,int EpisodeCount,
    AnalysisWindow[] Windows,int WindowCount,InspectedShot[] Shots,int ShotCount,int ShotPage,int ShotPages,double? FirstShotTimestampMs,string[] UnavailableTables);

// Analysis-only reader. No replay frames, tick streams, positions or payloads.
// Every selected ID is bound. Lists are bounded and shot detail is paginated.
static class RunInspection
{
    public static InspectionResult Read(string path,string id,int shotPage=0)
    {
        var details=RunMetrics.Read(path,id);
        InspectedRun? run=null;ResponseSummary? summary=null;
        var episodes=new List<ResponseEpisode>();var windows=new List<AnalysisWindow>();var shots=new List<InspectedShot>();
        var unavailable=new List<string>();int episodeCount=0,windowCount=0,shotCount=0;double? firstShot=null;
        shotPage=Math.Clamp(shotPage,0,100000);
        if(!File.Exists(path)||string.IsNullOrWhiteSpace(id))return new(null,details,null,[],0,[],0,[],0,0,1,null,[]);
        using var db=new InspectionDatabase(path);
        void Query(string table,string sql,Action<InspectionDatabase.Row> row)
        { if(!db.Query(sql,id,row)&&!unavailable.Contains(table))unavailable.Add(table); }
        Query("sessions","SELECT id,scenario,score,accuracy,duration_secs,timestamp FROM sessions WHERE id=?1",r=>
            run=new(r.Text(0),r.Text(1),r.Number(2),r.Number(3),r.Number(4),r.Text(5)));
        if(run is null)return new(null,details,null,[],0,[],0,[],0,0,1,null,unavailable.ToArray());
        Query("session_target_response_summaries","SELECT episode_count,path_change_count,target_switch_count,response_coverage_pct,avg_reaction_time_ms,p90_reaction_time_ms,avg_pre_slowdown_reaction_ms,avg_recovery_time_ms,p90_recovery_time_ms,avg_path_change_reaction_ms,avg_target_switch_reaction_ms,avg_trigger_magnitude_deg,avg_peak_yaw_error_deg,stable_response_ratio FROM session_target_response_summaries WHERE session_id=?1",r=>
            summary=new(r.Number(0),r.Number(1),r.Number(2),r.Number(3),r.Number(4),r.Number(5),r.Number(6),r.Number(7),r.Number(8),r.Number(9),r.Number(10),r.Number(11),r.Number(12),r.Number(13)));
        Query("session_target_response_episodes","SELECT COUNT(*) FROM session_target_response_episodes WHERE session_id=?1",r=>episodeCount=r.Count(0));
        Query("session_target_response_episodes","SELECT kind,start_ms,end_ms,target_label,trigger_magnitude_deg,peak_yaw_error_deg,reaction_time_ms,pre_slowdown_reaction_ms,recovery_time_ms,stable_response FROM session_target_response_episodes WHERE session_id=?1 ORDER BY start_ms,episode_idx LIMIT 1000",r=>{
            if(r.Number(1) is double start&&r.Number(2) is double end&&start>=0&&end>=start)
                episodes.Add(new(r.Text(0),start,end,r.Text(3),r.Number(4),r.Number(5),r.Number(6),r.Number(7),r.Number(8),r.Number(9)==1));
        });
        Query("session_replay_context_windows","SELECT COUNT(*) FROM session_replay_context_windows WHERE session_id=?1",r=>windowCount=r.Count(0));
        Query("session_replay_context_windows","SELECT context_kind,label,phase,start_ms,end_ms,fired_count,hit_count,accuracy_pct,primary_target_label,primary_target_share,avg_nearest_distance,avg_nearest_yaw_error_deg,avg_nearest_pitch_error_deg,avg_score_per_minute,avg_kills_per_second FROM session_replay_context_windows WHERE session_id=?1 ORDER BY start_ms,window_idx LIMIT 500",r=>{
            if(r.Number(3) is double start&&r.Number(4) is double end&&start>=0&&end>=start)
                windows.Add(new(r.Text(0),r.Text(1),r.Text(2),start,end,r.Number(5),r.Number(6),r.Number(7),r.Text(8),r.Number(9),r.Number(10),r.Number(11),r.Number(12),r.Number(13),r.Number(14)));
        });
        Query("session_shot_events","SELECT COUNT(*),MIN(ts_ms) FROM session_shot_events WHERE session_id=?1",r=>{shotCount=r.Count(0);firstShot=r.Number(1);});
        var pages=Math.Max(1,(int)Math.Ceiling(shotCount/100.0));shotPage=Math.Min(shotPage,pages-1);
        var offset=shotPage*100;
        Query("session_shot_events",$"SELECT shot_seq_idx,event_kind,ts_ms,count,total FROM session_shot_events WHERE session_id=?1 ORDER BY ts_ms,shot_seq_idx LIMIT 100 OFFSET {offset}",r=>{
            if(r.Number(2) is double stamp&&stamp>=0)shots.Add(new(r.Count(0),r.Text(1),stamp,r.Number(3),r.Number(4),[]));
        });
        var shotMap=shots.ToDictionary(s=>s.Sequence);
        if(shots.Count>0)
            Query("session_shot_targets",$"WITH selected AS (SELECT shot_seq_idx FROM session_shot_events WHERE session_id=?1 ORDER BY ts_ms,shot_seq_idx LIMIT 100 OFFSET {offset}) SELECT t.shot_seq_idx,t.profile,t.distance_3d,t.yaw_error_deg,t.pitch_error_deg,t.is_nearest FROM session_shot_targets t JOIN selected s ON s.shot_seq_idx=t.shot_seq_idx WHERE t.session_id=?1 AND (t.target_idx<8 OR t.is_nearest=1) ORDER BY t.shot_seq_idx,t.is_nearest DESC,t.target_idx LIMIT 900",r=>{
                if(shotMap.TryGetValue(r.Count(0),out var shot)&&shot.Targets.Count<8)shot.Targets.Add(new(r.Text(1),r.Number(2),r.Number(3),r.Number(4),r.Number(5)==1));
            });
        return new(run,details,summary,episodes.ToArray(),episodeCount,windows.ToArray(),windowCount,shots.ToArray(),shotCount,shotPage,pages,firstShot,unavailable.ToArray());
    }
}

sealed class InspectionDatabase : IDisposable
{
    nint db;
    [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)]string path,out nint db,int flags,nint vfs);
    [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_close_v2(nint db);
    [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_busy_timeout(nint db,int ms);
    [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_prepare_v2(nint db,[MarshalAs(UnmanagedType.LPUTF8Str)]string sql,int bytes,out nint s,nint tail);
    [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_bind_text(nint s,int index,[MarshalAs(UnmanagedType.LPUTF8Str)]string value,int bytes,nint destructor);
    [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_step(nint s);
    [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_finalize(nint s);
    [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)] static extern nint sqlite3_errmsg(nint db);
    [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)] static extern nint sqlite3_column_text(nint s,int column);
    [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_column_type(nint s,int column);
    [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)] static extern double sqlite3_column_double(nint s,int column);
    public InspectionDatabase(string path)
    {
        if(sqlite3_open_v2(path,out db,1,0)!=0){Dispose();throw new IOException("Run analysis unavailable.");}
        sqlite3_busy_timeout(db,750);
    }
    public sealed record Row(nint Statement)
    {
        public string Text(int column){var value=Marshal.PtrToStringUTF8(sqlite3_column_text(Statement,column))??"";return value.Length<=512?value:value[..512];}
        public double? Number(int column)
        {if(sqlite3_column_type(Statement,column) is not (1 or 2))return null;var n=sqlite3_column_double(Statement,column);return double.IsFinite(n)&&Math.Abs(n)<1e15?n:null;}
        public int Count(int column)=>(int)Math.Clamp(Number(column)??0,0,int.MaxValue);
    }
    public bool Query(string sql,string id,Action<Row> callback)
    {
        nint s=0;
        try
        {
            if(sqlite3_prepare_v2(db,sql,-1,out s,0)!=0)
            {
                var error=Marshal.PtrToStringUTF8(sqlite3_errmsg(db))??"";
                if(error.StartsWith("no such table:",StringComparison.Ordinal)||error.StartsWith("no such column:",StringComparison.Ordinal))return false;
                throw new IOException("Run analysis query unavailable.");
            }
            if(sqlite3_bind_text(s,1,id,-1,-1)!=0)throw new IOException("Run selection unavailable.");
            int step;while((step=sqlite3_step(s))==100)callback(new(s));
            if(step!=101)throw new IOException("Run analysis query incomplete.");return true;
        }
        finally{if(s!=0)sqlite3_finalize(s);}
    }
    public void Dispose(){if(db!=0){sqlite3_close_v2(db);db=0;}}
}

using System.Text.Json;
namespace AimMod.InGame;
record LiveOverlaySnapshot(bool Available,bool Active,bool Paused,string? Scenario,double? Score,double? Seconds,double? Shots,double? Hits,double? Kills,double? Damage,double? Accuracy,double? PersonalBest,double? OpponentScore,double? OpponentDuration,double? ProjectedScore,double? ProjectedDelta,bool ProjectionEstimated=true,string OpponentName="Personal best",string OpponentSource="personal-best",double? ScorePerMinute=null,double? KillsPerSecond=null,double? RemainingSeconds=null,double? DurationSeconds=null,double? LastTimeToKillSeconds=null,bool Transient=false,string? AttemptId=null,bool Replay=false);
static class LiveOverlayState
{
    static readonly LiveOverlaySnapshot Empty=new(false,false,false,null,null,null,null,null,null,null,null,null,null,null,null,null);
    static bool Eligible(Run r)=>!string.IsNullOrEmpty(r.Scenario)&&double.IsFinite(r.Score)&&r.Duration>0&&double.IsFinite(r.Duration);
    // Highest score per exact scenario (case-insensitive); ties keep the first run, as before.
    internal static IReadOnlyDictionary<string,Run> PersonalBests(IEnumerable<Run> history)
    {
        var best=new Dictionary<string,Run>(StringComparer.OrdinalIgnoreCase);
        foreach(var r in history)if(Eligible(r)&&(!best.TryGetValue(r.Scenario,out var saved)||r.Score>saved.Score))best[r.Scenario]=r;
        return best;
    }
    public static LiveOverlaySnapshot Read(string output,IEnumerable<Run> history,DateTime? utcNow=null)
    {
        var best=PersonalBests(history);
        return Read(output,s=>best.GetValueOrDefault(s),null,utcNow);
    }
    // replayActive is the worker's in-memory playback state. The workspace passes
    // it so overlay polls never open the frame file, which would make the
    // playback pump's replace fail (Windows refuses to rename over an open file).
    // When null (standalone use) the published frame header is inspected.
    public static LiveOverlaySnapshot Read(string output,Func<string,Run?> personalBest,bool? replayActive,DateTime? utcNow=null)
    {
        var now=utcNow??DateTime.UtcNow;
        try {
            if(replayActive==true)return Empty with{Replay=true};
            var replay=Path.Combine(output,"replay-frame.tsv");
            if(replayActive is null&&File.Exists(replay)){using var stream=new FileStream(replay,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var reader=new StreamReader(stream);var header=(reader.ReadLine()??"").Split('\t');if(header.Length==3&&header[2]=="1")return Empty with{Replay=true};}
            var path=Path.Combine(output,"live-overlay.json");var file=new FileInfo(path);
            if(!file.Exists||file.Length>8192||now-file.LastWriteTimeUtc>TimeSpan.FromSeconds(2)||file.LastWriteTimeUtc-now>TimeSpan.FromSeconds(1))return Empty;
            using var doc=JsonDocument.Parse(File.ReadAllText(path));var root=doc.RootElement;
            if(root.GetProperty("version").GetInt32()!=1)return Empty;
            bool Flag(string key)=>root.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.True;
            double? Number(string key)=>root.TryGetProperty(key,out var v)&&v.TryGetDouble(out var n)&&double.IsFinite(n)&&Math.Abs(n)<1e12?n:null;
            var active=Flag("active");if(!active)return Empty with{Available=true};
            var scenario=root.GetProperty("scenario").GetString();if(string.IsNullOrWhiteSpace(scenario)||scenario.Length>512)return Empty;
            var score=Number("score");var elapsed=Number("seconds");if(elapsed<0)elapsed=null;
            var shots=Number("shots");var hits=Number("hits");double? accuracy=shots>0&&hits>=0&&hits<=shots?hits/shots*100:null;
            var kills=Number("kills");if(kills<0)kills=null;
            var remaining=Number("remainingSeconds");if(remaining<0)remaining=null;
            double? duration=elapsed.HasValue&&remaining.HasValue?elapsed+remaining:null;
            double? spm=score.HasValue&&elapsed>0?score/elapsed*60:null;
            double? kps=kills.HasValue&&elapsed>0?kills/elapsed:null;
            if(spm.HasValue&&!double.IsFinite(spm.Value))spm=null;
            if(kps.HasValue&&!double.IsFinite(kps.Value))kps=null;
            var ttk=Number("lastTimeToKillSeconds");if(!(kills>0&&ttk>0&&ttk<=elapsed))ttk=null;
            var best=personalBest(scenario);
            var projectionDuration=duration??best?.Duration;
            double? projection=score.HasValue&&elapsed>=1&&projectionDuration>0&&elapsed<=projectionDuration?score/elapsed*projectionDuration:null;
            if(projection.HasValue&&!double.IsFinite(projection.Value))projection=null;
            return new(true,true,Flag("paused"),scenario,score,elapsed,shots,hits,kills,Number("damage"),accuracy,best?.Score,best?.Score,projectionDuration,projection,projection-best?.Score,ScorePerMinute:spm,KillsPerSecond:kps,RemainingSeconds:remaining,DurationSeconds:duration,LastTimeToKillSeconds:ttk,Transient:Flag("transient"),AttemptId:root.TryGetProperty("id",out var attempt)&&attempt.ValueKind==JsonValueKind.String?attempt.GetString():null);
        } catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException){return Empty;}
    }
}

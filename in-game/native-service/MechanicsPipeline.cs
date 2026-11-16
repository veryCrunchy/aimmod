using System.Text.Json;

namespace AimMod.InGame;

sealed record MechanicsEntry(int Version, string Id, long SourceSize, long SourceStamp, NativeMechanicsResult Result);

// Requests only consume snapshots. One idle, low-priority worker owns bounded backfill and writes.
sealed class MechanicsPipeline : IDisposable
{
    internal const int Version=1, MaxRuns=128, MaxEntryBytes=16384;
    readonly ReplayCatalog catalog;
    readonly string folder;
    readonly Func<bool> live;
    readonly Thread worker;
    readonly ManualResetEventSlim stop=new();
    readonly object gate=new();
    readonly Dictionary<string,MechanicsEntry> cache=new();
    readonly Dictionary<string,DateTimeOffset> failures=new();
    HashSet<string> available=[];
    long revision;
    DateTimeOffset next;
    internal long Revision { get { lock(gate) return revision; } }
    internal MechanicsPipeline(string output, ReplayCatalog catalog, Func<bool> live, bool start=true)
    {
        this.catalog=catalog; this.live=live; folder=Path.Combine(output,"mechanics-cache"); Directory.CreateDirectory(folder);
        worker=new Thread(Loop) { IsBackground=true,Name="Mechanics analysis",Priority=ThreadPriority.Lowest };
        if(start) worker.Start();
    }
    void CheckIdle() { if(stop.IsSet || live()) throw new OperationCanceledException(); }
    void Loop()
    {
        while(!stop.IsSet)
        {
            try { Tick(DateTimeOffset.UtcNow); }
            catch(Exception e) when(e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or OperationCanceledException) { }
            stop.Wait(1000);
        }
    }
    internal bool Tick(DateTimeOffset now)
    {
        if(live() || now<next) return false;
        next=now.AddSeconds(10);
        var newest=catalog.List(CheckIdle).OrderByDescending(r=>r.RecordedAt,StringComparer.Ordinal).Take(MaxRuns).ToArray();
        var keep=newest.Select(r=>r.Id).ToHashSet();
        lock(gate)
        {
            if(!available.SetEquals(keep)) { available=keep; revision++; }
            foreach(var id in cache.Keys.Where(id=>!keep.Contains(id)).ToArray()) { cache.Remove(id); revision++; }
            foreach(var id in failures.Keys.Where(id=>!keep.Contains(id)).ToArray()) failures.Remove(id);
        }
        Trim();
        foreach(var item in newest)
        {
            CheckIdle(); var source=catalog.Resolve(item.Id); if(source is null) continue;
            var info=new FileInfo(source);
            bool Valid(MechanicsEntry? e)=>e is not null && e.Version==Version && e.Id==item.Id && e.Result?.Measurements is not null && e.Result.Measurements.RunId=="native:"+item.Id && e.SourceSize==info.Length && e.SourceStamp==info.LastWriteTimeUtc.Ticks && ValidResult(e.Result);
            lock(gate) { if(cache.TryGetValue(item.Id,out var existing) && Valid(existing)) continue; if(cache.Remove(item.Id)) revision++; }
            var path=Path.Combine(folder,item.Id+".json"); MechanicsEntry? saved=null;
            try { if(File.Exists(path) && new FileInfo(path).Length<=MaxEntryBytes) saved=JsonSerializer.Deserialize<MechanicsEntry>(File.ReadAllText(path)); }
            catch(Exception e) when(e is JsonException or IOException) { }
            if(Valid(saved)) { lock(gate) { cache[item.Id]=saved!; revision++; } continue; }
            lock(gate) if(failures.TryGetValue(item.Id,out var retry) && now<retry) continue;
            try
            {
                var replay=catalog.Read(item.Id); CheckIdle(); if(replay is null) throw new InvalidDataException();
                var result=NativeMechanics.Build(replay,CheckIdle); CheckIdle();
                var after=new FileInfo(source); if(after.Length!=info.Length || after.LastWriteTimeUtc.Ticks!=info.LastWriteTimeUtc.Ticks) throw new IOException();
                saved=new(Version,item.Id,info.Length,info.LastWriteTimeUtc.Ticks,result);
                var bytes=JsonSerializer.SerializeToUtf8Bytes(saved); if(bytes.Length>MaxEntryBytes) throw new InvalidDataException();
                File.WriteAllBytes(path+".tmp",bytes); CheckIdle(); File.Move(path+".tmp",path,true);
                lock(gate) { cache[item.Id]=saved; failures.Remove(item.Id); revision++; }
                Trim(); return true;
            }
            catch(OperationCanceledException) { throw; }
            catch(Exception e) when(e is IOException or InvalidDataException or JsonException or ArgumentException) { lock(gate) failures[item.Id]=now.AddMinutes(5); }
            finally { if(File.Exists(path+".tmp")) File.Delete(path+".tmp"); }
        }
        return false;
    }
    static bool ValidResult(NativeMechanicsResult r)
    {
        bool Range(double? value,double max)=>value is null || double.IsFinite(value.Value) && value>=0 && value<=max;
        if(!Range(r.Smoothness,100) || !Range(r.Jitter,1) || !Range(r.Efficiency,1) || !Range(r.Correction,1)) return false;
        var m=r.Measurements;
        return Range(m.Overshoot,1) && Range(m.SpeedVariation,1) && Range(m.DirectionalBias,1)
            && typeof(HistoricalMeasurement).GetProperties().Where(p=>p.PropertyType==typeof(double?)).All(p=>Range((double?)p.GetValue(m),1e12))
            && Range(r.Kills,1e12) && Range(r.Damage,1e12);
    }
    internal void Trim()
    {
        foreach(var partial in new DirectoryInfo(folder).GetFiles("*.tmp")) partial.Delete();
        int count=0; long bytes=0;
        foreach(var f in new DirectoryInfo(folder).GetFiles("*.json").OrderByDescending(f=>f.LastWriteTimeUtc))
        {
            if(f.Length>MaxEntryBytes || count>=MaxRuns || bytes+f.Length>MaxRuns*MaxEntryBytes) f.Delete();
            else { count++; bytes+=f.Length; }
        }
    }
    internal Run[] Join(IEnumerable<Run> runs, IDictionary<string,HistoricalMeasurement> measurements)
    {
        lock(gate) return runs.Select(r=>
        {
            if(!r.Id.StartsWith("native:",StringComparison.Ordinal)) return r;
            var id=r.Id[7..];
            r=r with { Replay=catalog.Resolve(id) is not null };
            if(!cache.TryGetValue(id,out var entry)) return r;
            var m=entry.Result;
            // Exact desktop entries retain precedence; native fields only fill their own run IDs.
            if(!measurements.ContainsKey(r.Id)) measurements[r.Id]=m.Measurements;
            return r with { Smoothness=r.Smoothness??m.Smoothness,Jitter=r.Jitter??m.Jitter,Efficiency=r.Efficiency??m.Efficiency,Correction=r.Correction??m.Correction,
                RecordedDamage=m.Damage??r.RecordedDamage };
        }).ToArray();
    }
    public void Dispose() { stop.Set(); if(worker.IsAlive) worker.Join(); stop.Dispose(); }
}

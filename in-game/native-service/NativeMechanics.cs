using AimMod.InGame.Analysis;

namespace AimMod.InGame;

sealed record NativeMechanicsResult(double? Smoothness, double? Jitter, double? Efficiency, double? Correction,
    HistoricalMeasurement Measurements, double? Kills, double? Damage);

static class NativeMechanics
{
    static double Std(double[] values) { var mean = values.Average(); return Math.Sqrt(values.Average(v => (v - mean) * (v - mean))); }
    static (double X, double Y) Axis((double X, double Y)[] v)
    {
        var x = v.Average(p => p.X); var y = v.Average(p => p.Y);
        var xx = v.Average(p => (p.X-x)*(p.X-x)); var yy = v.Average(p => (p.Y-y)*(p.Y-y));
        var xy = v.Average(p => (p.X-x)*(p.Y-y)); var d = xx-yy;
        var ex = d + Math.Sqrt(d*d+4*xy*xy); var ey = 2*xy; var length = Math.Sqrt(ex*ex+ey*ey);
        return length < 1e-9 ? (1,0) : (ex/length,ey/length);
    }
    internal static NativeMechanicsResult Build(NativeReplay replay, Action? checkpoint = null)
    {
        if (replay.Duration is <= 0 or > 600 || replay.Frames.Count < 2) throw new InvalidDataException();
        var samples = new List<ViewSample>();
        if (replay.Motion is { } motion)
            foreach (var t in motion.Times) { if(samples.Count % 1024 == 0) checkpoint?.Invoke(); var (p,y,_) = motion.Rotation(t); samples.Add(new(t,y,p)); }
        else foreach(var f in replay.Frames) samples.Add(new(f.T,f.Camera[4],f.Camera[3]));
        var yaw = samples.Select(s=>s.Yaw).ToArray(); Angles.Unwrap(yaw);
        for(int i=0;i<samples.Count;i++) samples[i]=samples[i] with { Yaw=yaw[i] };
        // The desktop uses roughly 60 Hz mouse windows. Use the same 10/15-sample
        // windows and dimensionless formulas on angular motion, independent of engine FPS.
        // DPI is absent from the recording: 800-DPI counts/s cannot be recovered.
        var grid = ViewPath.Resample(samples,replay.Duration);
        var points = Enumerable.Range(0,grid.Count).Where(i=>i%4==0).Select(i=>(X:grid.Yaw[i],Y:grid.Pitch[i])).ToArray();
        var velocities = new List<(double X,double Y)>();
        for(int i=1;i<points.Length;i++)
        {
            if(i%1024==0) checkpoint?.Invoke();
            var x=(points[i].X-points[i-1].X)/.016; var y=(points[i].Y-points[i-1].Y)/.016;
            if(Math.Sqrt(x*x+y*y)>.01) velocities.Add((x,y));
        }
        var v=velocities.ToArray(); var speeds=v.Select(p=>Math.Sqrt(p.X*p.X+p.Y*p.Y)).ToArray();
        double? jitter=null,efficiency=null,correction=null,smooth=null,variation=null,overshoot=null,bias=null;
        if(v.Length>=10)
        {
            var mean=speeds.Average(); var jitters=new List<double>();
            for(int i=0;i+10<=v.Length;i+=5)
            {
                if(i%500==0) checkpoint?.Invoke();
                var chunk=v.Skip(i).Take(10).ToArray(); var axis=Axis(chunk);
                jitters.Add(Math.Sqrt(chunk.Average(p=>Math.Pow(-axis.Y*p.X+axis.X*p.Y,2)))/chunk.Average(p=>Math.Sqrt(p.X*p.X+p.Y*p.Y)));
            }
            jitter=Math.Clamp(jitters.Average(),0,1); variation=Math.Clamp((Std(speeds)/mean-.4)/.6,0,1);
            correction=speeds.Count(s=>s<mean*.4)/(double)speeds.Length;
            int qualified=0,reversed=0; var signs=new List<double>(); var global=Axis(v);
            for(int i=0;i+1<v.Length;i++)
            {
                if(i%1024==0) checkpoint?.Invoke();
                if(speeds[i]>mean*.25) { qualified++; if(speeds[i+1]>mean*.25 && (v[i].X*v[i+1].X+v[i].Y*v[i+1].Y)/(speeds[i]*speeds[i+1])<-.5) reversed++; }
                var a=v[i].X*global.X+v[i].Y*global.Y; var b=v[i+1].X*global.X+v[i+1].Y*global.Y;
                if(Math.Abs(a)>mean*.25 && Math.Abs(b)>mean*.25 && a*b<0) signs.Add(Math.Sign(a));
            }
            overshoot=qualified>0?reversed/(double)qualified:null;
            bias=signs.Count>=3?Math.Abs(signs.Average()):null;
            var efficiencies=new List<double>();
            for(int i=0;i+15<=points.Length;i+=7)
            {
                if(i%700==0) checkpoint?.Invoke();
                double length=0; for(int j=i+1;j<i+15;j++) length+=Math.Sqrt(Math.Pow(points[j].X-points[j-1].X,2)+Math.Pow(points[j].Y-points[j-1].Y,2));
                if(length>.01) efficiencies.Add(Math.Min(1,Math.Sqrt(Math.Pow(points[i+14].X-points[i].X,2)+Math.Pow(points[i+14].Y-points[i].Y,2))/length));
            }
            if(efficiencies.Count>0) efficiency=efficiencies.Average();
            // Unknown scenario classification uses the desktop's balanced weight profile.
            if(efficiency.HasValue && overshoot.HasValue) smooth=30*(1-jitter)+15*efficiency+25*(1-variation)+30*(1-overshoot);
        }
        var presses=replay.Inputs.Where(i=>i.Action=="FirePressed").Select(i=>i.T).Distinct().Order().ToArray();
        var intervals=presses.Zip(presses.Skip(1),(a,b)=>(b-a)*1000).Where(t=>t>0).ToArray();
        double? click=intervals.Length>=3?Std(intervals)/intervals.Average():null;
        double? Last(Func<ReplayStats,double?> field)=>replay.Frames.Where(f=>f.Stats is not null).Select(f=>field(f.Stats!)).LastOrDefault(n=>n is >=0 && double.IsFinite(n.Value));
        var kills=Last(s=>s.Kills); var damage=Last(s=>s.Damage); var shots=Last(s=>s.Shots); var hits=Last(s=>s.Hits);
        // TTK is target lifetime from observed spawn to a single confirmed kill,
        // never reciprocal KPS or time between unrelated kills. Ambiguous batches stay unknown.
        var spawned=new Dictionary<double,double>(); var ttk=new List<double>(); double? previousKills=null;
        HashSet<double> previousActors=[];
        int frameIndex=0;
        foreach(var f in replay.Frames)
        {
            if(frameIndex++%256==0) checkpoint?.Invoke();
            foreach(var a in f.Actors) if(!spawned.ContainsKey(a[0])) spawned[a[0]]=f.T;
            if(f.Stats?.Kills is double k)
            {
                if(previousKills is double pk && k-pk==1)
                {
                    double? target=f.Stats.HitTarget;
                    if(target is null && previousActors.Count==1) target=previousActors.Single();
                    if(target is double id && spawned.TryGetValue(id,out var start) && f.T>start) { ttk.Add((f.T-start)*1000); spawned.Remove(id); }
                }
                previousKills=k;
            }
            var actors=f.Actors.Select(a=>a[0]).ToHashSet();
            foreach(var id in previousActors.Where(id=>!actors.Contains(id))) spawned.Remove(id);
            previousActors=actors;
        }
        var times=ttk.ToArray();
        var measurements=new HistoricalMeasurement("native:"+replay.Id, Overshoot:overshoot,SpeedVariation:variation,ClickTimingVariation:click,DirectionalBias:bias,
            KillsPerSecond:kills/replay.Duration,AverageTimeToKillMs:times.Length>0?times.Average():null,BestTimeToKillMs:times.Length>0?times.Min():null,
            TimeToKillSpreadMs:times.Length>=2?Std(times):null,AverageShotsToHit:shots is >=0 && hits is >0 && shots>=hits?shots/hits:null);
        checkpoint?.Invoke();
        return new(smooth,jitter,efficiency,correction,measurements,kills,damage);
    }
}

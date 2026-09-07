using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime.Contracts;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Objects.Types;
using osuTK;

namespace AimMod.Desktop.Trainers;

public sealed record TrainerDemand(double PeakNps, double JumpDistance, double AimVelocity, int LongestChain);
public sealed record TrainerSkillEvidence(Guid ScoreId, TrainerDemand Demand);
public sealed record TrainerSkillLimits(double MaxNps = 2.5, double MaxJumpDistance = 120, double MaxAimVelocity = 300,
    int MaxChain = 8, int MaxBurst = 3, int Complexity = 0, int MaxApproachRate = 6, int EvidenceCount = 0)
{
    public void Validate()
    {
        if (!double.IsFinite(MaxNps) || MaxNps is < 1 or > 16 || !double.IsFinite(MaxJumpDistance) || MaxJumpDistance is < 40 or > 350
            || !double.IsFinite(MaxAimVelocity) || MaxAimVelocity is < 100 or > 1800 || MaxChain is < 3 or > 256
            || MaxBurst is not (3 or 5 or 7 or 9) || Complexity is < 0 or > 2 || MaxApproachRate is < 3 or > 10)
            throw new ArgumentOutOfRangeException(nameof(TrainerSkillLimits));
    }
}

public static class TrainerSkillProfile
{
    public static TrainerSkillLimits Build(TrainerKind kind, IEnumerable<TrainerResult> history, IEnumerable<TrainerSkillEvidence> replays, DateTimeOffset now)
    {
        var recent = history.Where(r => r.Settings.Kind == kind && r.UsesOsuJudgements && r.CompletedAt >= now.AddDays(-30) && r.CompletedAt <= now
            && r.Notes >= 12 && r.Hits >= 0 && r.Hits <= r.Notes && r.PlayedSeconds >= 12)
            .DistinctBy(r => r.Id).OrderByDescending(r => r.CompletedAt).Take(12).ToArray();
        var clean = recent.Where(r => r.Accuracy is >= 95 and <= 100 && r.Hits >= r.Notes*.95 && r.SpreadMs is >= 0 and <= 25)
            .Select(demandForResult).OfType<TrainerDemand>().Where(valid).ToArray();
        // One replay contributes at most one sample; a long map cannot outweigh several plays.
        var replay = replays.DistinctBy(r => r.ScoreId).Select(r => r.Demand).Where(valid)
            .Where(d => kind is not (TrainerKind.Alternating or TrainerKind.Rhythm) || d.LongestChain >= 16).Take(40).ToArray();
        var limits = new TrainerSkillLimits();
        int count = 0;
        if (replay.Length >= 3)
        {
            limits = limits with { MaxNps = quantile(replay.Select(d=>d.PeakNps))*.85,
                MaxJumpDistance = quantile(replay.Select(d=>d.JumpDistance))*.85, MaxAimVelocity = quantile(replay.Select(d=>d.AimVelocity))*.85,
                MaxChain = (int)quantile(replay.Select(d=>(double)d.LongestChain)), MaxBurst = 5 };
            count = replay.Length;
        }
        if (clean.Length >= 3)
        {
            // Modest progression only after repeated clean runs in this exact practice mode.
            limits = limits with { MaxNps = quantile(clean.Select(d=>d.PeakNps))*1.05,
                MaxJumpDistance = quantile(clean.Select(d=>d.JumpDistance))*1.05, MaxAimVelocity = quantile(clean.Select(d=>d.AimVelocity))*1.05,
                MaxChain = (int)quantile(clean.Select(d=>(double)d.LongestChain))+4,
                MaxBurst = clean.Length >= 9 ? 9 : clean.Length >= 6 ? 7 : 5,
                Complexity = clean.Length >= 6 ? 2 : 1, MaxApproachRate = Math.Min(9,recent.Where(r=>r.Accuracy>=95).Min(r=>r.Settings.ApproachRate)+1) };
            count += clean.Length;
        }
        if (recent.Take(3).Count(r => r.Accuracy < 90 || r.Hits < r.Notes*.9) >= 2)
            limits = limits with { MaxNps = limits.MaxNps*.8, MaxJumpDistance = limits.MaxJumpDistance*.8,
                MaxAimVelocity = limits.MaxAimVelocity*.8, MaxChain = Math.Max(3,limits.MaxChain/2), MaxBurst = 3, Complexity = 0 };
        return limits with { MaxNps = Math.Clamp(limits.MaxNps,1,16), MaxJumpDistance = Math.Clamp(limits.MaxJumpDistance,40,350),
            MaxAimVelocity = Math.Clamp(limits.MaxAimVelocity,100,1800), MaxChain = Math.Clamp(limits.MaxChain,3,256), EvidenceCount = count };
    }

    private static bool valid(TrainerDemand d) => double.IsFinite(d.PeakNps) && d.PeakNps is >= .5 and <= 30
        && double.IsFinite(d.JumpDistance) && d.JumpDistance is >= 0 and <= 600
        && double.IsFinite(d.AimVelocity) && d.AimVelocity is >= 0 and <= 10000 && d.LongestChain >= 3;
    private static double quantile(IEnumerable<double> values) { var a=values.Order().ToArray(); return a[(a.Length-1)/4]; }
    private static TrainerDemand? demandForResult(TrainerResult result)
    {
        if(result.Demand is {} measured) return measured;
        // Older fixed drills can be reconstructed; song-tempo and old random runs cannot.
        if(result.Settings.Music=="song" || result.Settings.RandomizePatterns) return null;
        try
        {
            var map=TrainerBeatmap.Create(result.Settings);
            if(map.HitObjects.Count!=result.Notes) return null;
            foreach(var obj in map.HitObjects) obj.ApplyDefaults(map.ControlPointInfo,map.Difficulty);
            return Measure(map);
        }
        catch(ArgumentException) {return null;}
    }

    public static TrainerSettings Apply(TrainerSettings s, TrainerSkillLimits limits)
    {
        if (!s.RandomizePatterns || s.Kind == TrainerKind.Reaction) return s with { SkillLimits = null };
        limits.Validate();
        return s with { SkillLimits = limits, ApproachRate = Math.Min(s.ApproachRate,limits.MaxApproachRate), CircleSize = Math.Min(s.CircleSize,4),
            Sliders = limits.Complexity == 0 && s.Sliders == TrainerSliderStyle.BackAndForth ? TrainerSliderStyle.Mixed : s.Sliders,
            SliderBeats = limits.Complexity == 0 ? 1 : Math.Min(s.SliderBeats,2) };
    }

    public static bool Allows(TrainerPattern pattern, TrainerSkillLimits limits) => pattern switch
    {
        TrainerPattern.FiveNotes => limits.MaxBurst>=5, TrainerPattern.SevenNotes => limits.MaxBurst>=7,
        TrainerPattern.NineNotes => limits.MaxBurst>=9, TrainerPattern.MixedBursts => limits.MaxBurst>=5,
        TrainerPattern.LongStreams => limits.MaxChain>=24, TrainerPattern.BuildUp => limits.Complexity>=1,
        TrainerPattern.JumpFill or TrainerPattern.Scattered or TrainerPattern.Offbeat => limits.Complexity>=1,
        TrainerPattern.JumpTriples or TrainerPattern.SpeedSwitch or TrainerPattern.Syncopated or TrainerPattern.Triplets or TrainerPattern.Overlaps => limits.Complexity>=2,
        _ => true,
    };

    public static IReadOnlyList<TrainerNote> ConstrainNotes(TrainerSettings s, IReadOnlyList<TrainerNote> source)
    {
        if (!s.RandomizePatterns || s.SkillLimits is not {} limits) return source;
        var result = new List<TrainerNote>();
        double minGap = 1000/limits.MaxNps;
        int chain = 0;
        foreach (var note in source)
        {
            double gap = result.Count==0 ? double.PositiveInfinity : note.TimeMs-result[^1].TimeMs;
            if (gap < minGap-.001) continue;
            if(s.Kind==TrainerKind.Bursts && result.Count>0 && note.Phrase!=result[^1].Phrase) chain=0;
            bool endurance = s.Kind is TrainerKind.Alternating or TrainerKind.Rhythm or TrainerKind.Bursts;
            if (endurance && chain>=limits.MaxChain && gap<Math.Max(600,minGap*3)) continue;
            chain = gap >= Math.Max(600,minGap*3) ? 1 : chain+1;
            result.Add(note);
        }
        return result;
    }

    public static TrainerDemand Measure(IBeatmap map)
    {
        var notes=map.HitObjects.OfType<OsuHitObject>().OrderBy(o=>o.StartTime).ToArray();
        double rate=0, spacing=0, velocity=0; int chain=1,longest=1;
        for(int i=1;i<notes.Length;i++)
        {
            double end = notes[i-1] is IHasDuration d ? d.EndTime : notes[i-1].StartTime;
            double gap=notes[i].StartTime-end;
            if(gap<=0) continue;
            double distance=Vector2.Distance(notes[i-1].EndPosition,notes[i].Position);
            rate=Math.Max(rate,1000/(notes[i].StartTime-notes[i-1].StartTime)); spacing=Math.Max(spacing,distance); velocity=Math.Max(velocity,distance*1000/gap);
            chain=gap<600 ? chain+1:1; longest=Math.Max(longest,chain);
        }
        return new(rate,spacing,velocity,longest);
    }

    public static TrainerSkillEvidence? FromReplay(LocalReplay run, ReplayAnalysisResult analysis, string player, DateTimeOffset now)
    {
        if (!string.Equals(run.Player,player,StringComparison.OrdinalIgnoreCase) || !run.Passed || !double.IsFinite(run.Accuracy) || run.Accuracy is < .95 or > 1
            || run.PlayedAt<now.AddDays(-30) || run.PlayedAt>now || run.RulesetShortName!="osu" || !ScoreMods.IsManualPlay(run)
            || analysis.EngineVersion!=ReplayAnalysisProtocol.EngineVersion) return null;
        var notes=analysis.Judgements.Where(n=>string.IsNullOrEmpty(n.NestedPath)).OrderBy(n=>n.StartTimeMs).ToArray();
        var samples=new List<TrainerDemand>();
        int chain=0;
        for(int i=0;i<notes.Length;i++)
        {
            var n=notes[i];
            bool clean=n.ObjectIndex is not null && n.ObjectType=="HitCircle" && n.Result=="Great" && n.ObjectPosition is not null && n.GameplayRate is >= .5 and <= 2
                && double.IsFinite(n.StartTimeMs);
            if(!clean) {chain=0;continue;}
            if(i>0 && chain>0)
            {
                var prev=notes[i-1]; double gap=(n.StartTimeMs-prev.StartTimeMs)/n.GameplayRate!.Value;
                if(n.ObjectIndex!=prev.ObjectIndex+1 || n.GameplayRate!=prev.GameplayRate || gap is < 40 or >= 600) chain=0;
            }
            chain++;
            if(chain<8 || chain%8!=0) continue;
            var window=notes.Skip(i-7).Take(8).ToArray();
            var gaps=window.Zip(window.Skip(1),(a,b)=>(b.StartTimeMs-a.StartTimeMs)/b.GameplayRate!.Value).ToArray();
            // Slowest adjacent interval bounds the demonstrated rate, rather than one fast double.
            var distances=window.Zip(window.Skip(1),(a,b)=>Math.Sqrt(Math.Pow(b.ObjectPosition!.X-a.ObjectPosition!.X,2)+Math.Pow(b.ObjectPosition.Y-a.ObjectPosition.Y,2))).ToArray();
            samples.Add(new(1000/gaps.Max(),quantile(distances),quantile(distances.Zip(gaps,(d,g)=>d*1000/g)),chain));
        }
        if(samples.Count==0) return null;
        return new(run.ScoreId,new(quantile(samples.Select(s=>s.PeakNps)),quantile(samples.Select(s=>s.JumpDistance)),quantile(samples.Select(s=>s.AimVelocity)),samples.Max(s=>s.LongestChain)));
    }
}

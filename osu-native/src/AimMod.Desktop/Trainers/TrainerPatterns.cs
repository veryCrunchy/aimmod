namespace AimMod.Desktop.Trainers;

public enum TrainerPattern { Standard, Offbeat, Doubles, Gaps, FiveNotes, SevenNotes, NineNotes, MixedBursts,
    PartialStreams, LongStreams, BuildUp, Syncopated, Triplets, JumpFill, JumpTriples, SpeedSwitch, Scattered, Overlaps }
public enum TrainerNoteSpeed { Default, OnePerBeat, TwoPerBeat, FourPerBeat }
public enum TrainerSliderStyle { None, Mixed, SlidersOnly, BackAndForth }
public enum TrainerPathStyle { FigureEight, Arc, Zigzag, Random }
public enum TrainerReactionDelay { Standard, Short, Long, Wide }

public static class TrainerPatterns
{
    public static IReadOnlyDictionary<string, TrainerPattern> Choices(TrainerKind kind) => kind switch
    {
        TrainerKind.Steady => new Dictionary<string, TrainerPattern> { ["Even taps"] = TrainerPattern.Standard, ["Offbeat taps"] = TrainerPattern.Offbeat, ["Doubles"] = TrainerPattern.Doubles, ["Rest and re-enter"] = TrainerPattern.Gaps },
        TrainerKind.Alternating => new Dictionary<string, TrainerPattern> { ["Full streams"] = TrainerPattern.Standard, ["Short streams · 8 notes"] = TrainerPattern.PartialStreams, ["Long streams · 24 notes"] = TrainerPattern.LongStreams, ["Build into streams"] = TrainerPattern.BuildUp },
        TrainerKind.Bursts => new Dictionary<string, TrainerPattern> { ["3-note bursts"] = TrainerPattern.Standard, ["5-note bursts"] = TrainerPattern.FiveNotes, ["7-note bursts"] = TrainerPattern.SevenNotes, ["9-note bursts"] = TrainerPattern.NineNotes, ["Mixed · 3 / 5 / 7 / 9"] = TrainerPattern.MixedBursts },
        TrainerKind.Rhythm => new Dictionary<string, TrainerPattern> { ["Half / quarter switches"] = TrainerPattern.Standard, ["Syncopation"] = TrainerPattern.Syncopated, ["Triplet transitions"] = TrainerPattern.Triplets, ["Stop and restart"] = TrainerPattern.Gaps },
        TrainerKind.Aim => new Dictionary<string, TrainerPattern> { ["Even jumps"] = TrainerPattern.Standard, ["Jumps + connecting notes"] = TrainerPattern.JumpFill, ["Jumps + triples"] = TrainerPattern.JumpTriples, ["Slow / fast jumps"] = TrainerPattern.SpeedSwitch },
        TrainerKind.Reading => new Dictionary<string, TrainerPattern> { ["Ordered rows"] = TrainerPattern.Standard, ["Scattered patterns"] = TrainerPattern.Scattered, ["Overlapping patterns"] = TrainerPattern.Overlaps, ["Rhythm switches"] = TrainerPattern.SpeedSwitch },
        _ => new Dictionary<string, TrainerPattern> { ["Standard"] = TrainerPattern.Standard },
    };

    public static double Step(TrainerSettings s)
    {
        double step = s.NoteSpeed switch {
        TrainerNoteSpeed.OnePerBeat => 1, TrainerNoteSpeed.TwoPerBeat => .5, TrainerNoteSpeed.FourPerBeat => .25,
        _ => s.Kind is TrainerKind.Aim or TrainerKind.Reading ? 1 : s.Kind == TrainerKind.Steady ? .5 : .25,
        };
        if (s.RandomizePatterns)
        {
            var limits=s.SkillLimits ?? new();
            double factor=s.Kind is TrainerKind.Aim or TrainerKind.Reading && limits.Complexity>=2 ? 4 : 1;
            while (s.Bpm/(60*step)*factor>limits.MaxNps && step<16) step*=2;
        }
        return step;
    }

    public static IReadOnlyList<(double Beat, int Phrase)> Bar(TrainerSettings s, int bar)
    {
        var pattern = PatternAt(s, bar);
        double step = Step(s);
        var result = new List<(double Beat, int Phrase)>();
        void add(double beat, int phrase = 0) { if (beat >= 0 && beat < 4 - .00001) result.Add((beat, phrase)); }
        if (s.Kind == TrainerKind.Bursts)
        {
            bool mixed = pattern == TrainerPattern.MixedBursts || s.RandomizePatterns;
            var burstRandom = new Random(s.PatternSeed);
            double cursor = 0;
            for (int phrase = 0; cursor < (bar+1)*4; phrase++)
            {
                int[] lengths = new[] {3,5,7,9}.Where(n=>!s.RandomizePatterns || n<=(s.SkillLimits??new()).MaxBurst).ToArray();
                int length = mixed ? lengths[s.RandomizePatterns ? burstRandom.Next(lengths.Length) : phrase%lengths.Length]
                    : pattern switch { TrainerPattern.FiveNotes => 5, TrainerPattern.SevenNotes => 7, TrainerPattern.NineNotes => 9, _ => 3 };
                for (int i = 0; i < length; i++)
                {
                    double at = cursor+i*step-bar*4;
                    if (at >= 0 && at < 4) add(at, phrase);
                }
                cursor += !mixed && length==3 && step==.25 ? 2 : length*step+1;
            }
        }
        else if (pattern is TrainerPattern.JumpFill or TrainerPattern.JumpTriples)
        {
            for (double b = 0; b < 4; b += step)
            {
                add(b);
                if (pattern == TrainerPattern.JumpFill) add(b + step*.5);
                else { add(b + step*.25); add(b + step*.5); }
            }
        }
        else if (pattern == TrainerPattern.Triplets)
        {
            double spacing = (bar % 2 == 0 ? .5 : 1.0/3) * step/.25;
            for (double b = 0; b < 4-.00001; b += spacing) add(b);
        }
        else if (pattern == TrainerPattern.Syncopated)
            foreach (double b in new double[] {0,.75,1,1.5,2.25,2.5,3.25,3.5}) add(b*step/.25);
        else
        {
            if (pattern == TrainerPattern.SpeedSwitch) step = bar % 2 == 0 ? step : step/2;
            if (s.Kind == TrainerKind.Rhythm && pattern == TrainerPattern.Standard) step = (bar % 2 == 0 ? .5 : .25) * step/.25;
            if (pattern == TrainerPattern.BuildUp) step = new[] {1.0,.5,.25,.25}[bar%4]*step/.25;
            for (int i = 0; i*step < 4-.00001; i++)
            {
                double b = i*step;
                if (pattern == TrainerPattern.PartialStreams && (bar*(int)Math.Round(4/step)+i)%16 >= 8) continue;
                if (pattern == TrainerPattern.LongStreams && (bar*(int)Math.Round(4/step)+i)%32 >= 24) continue;
                if (pattern == TrainerPattern.Gaps && b >= 2.5) continue;
                if (pattern == TrainerPattern.Doubles && i%4 >= 2) continue;
                add(b + (pattern == TrainerPattern.Offbeat ? step*.5 : 0), bar*2+(b>=2 ? 1:0));
            }
        }
        return result.OrderBy(n => n.Beat).ToArray();
    }
    public static TrainerPattern PatternAt(TrainerSettings s, int bar)
    {
        var choices=Choices(s.Kind).Values.Where(p=>!s.RandomizePatterns || TrainerSkillProfile.Allows(p,s.SkillLimits??new())).ToArray();
        return s.RandomizePatterns ? choices[new Random(unchecked(s.PatternSeed*397 ^ bar*7919)).Next(choices.Length)] : s.Pattern;
    }
}

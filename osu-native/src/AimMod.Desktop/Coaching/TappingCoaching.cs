using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop.Coaching;

public sealed record TappingLesson(string Pattern, string Observation, string Cue, string Practice,
    int FirstObjectIndex, double StartTimeMs, double EndTimeMs, double MeanOffsetMs, double DriftMs);

/// <summary>Local observations from evenly spaced, consecutive hit circles; not a diagnosis of the player.</summary>
public static class TappingCoaching
{
    public static TappingLesson? Build(ReplayAnalysisResult? analysis)
    {
        if (analysis is null || analysis.EngineVersion != ReplayAnalysisProtocol.EngineVersion) return null;
        var notes = analysis.Judgements.OrderBy(j => j.StartTimeMs).ToArray();
        TappingLesson? best = null;
        double strongest = 0;
        for (int start = 0; start + 8 <= notes.Length; start++)
        {
            var phrase = notes.Skip(start).Take(8).ToArray();
            if (phrase.Any(j => j.ObjectIndex is null || !string.IsNullOrEmpty(j.NestedPath)
                || j.ObjectType != "HitCircle" || j.Result is not ("Great" or "Ok" or "Meh")
                || !double.IsFinite(j.TimeOffsetMs) || !double.IsFinite(j.StartTimeMs))) continue;
            if (phrase.Skip(1).Where((j, i) => j.ObjectIndex != phrase[i].ObjectIndex + 1).Any()) continue;
            double gap = (phrase[7].StartTimeMs - phrase[0].StartTimeMs) / 7;
            if (gap < 60 || gap > 500 || phrase.Skip(1).Where((j, i) => Math.Abs(j.StartTimeMs - phrase[i].StartTimeMs - gap) > gap * .05).Any()) continue;
            double[] offsets = phrase.Select(j => j.TimeOffsetMs).ToArray();
            double mean = offsets.Average();
            double drift = offsets[7] - offsets[0];
            double slope = offsets.Select((v, i) => (i - 3.5) * (v - mean)).Sum() / 42;
            double residual = Math.Sqrt(offsets.Select((v, i) => Math.Pow(v - mean - slope * (i - 3.5), 2)).Average());
            double spread = Math.Sqrt(offsets.Select(v => Math.Pow(v - mean, 2)).Average());
            double gapError = Math.Sqrt(offsets.Skip(1).Select((v, i) => Math.Pow(v - offsets[i], 2)).Average());
            string pattern, observation, cue, practice;
            double strength;
            if (Math.Abs(drift) >= 25 && Math.Abs(slope) >= 3 && residual <= Math.Abs(drift) * .22)
            {
                bool rushing = slope < 0;
                pattern = rushing ? "Rushing through the phrase" : "Falling behind through the phrase";
                observation = $"Across these 8 notes, the last tap lands {Math.Abs(drift):0} ms {(rushing ? "earlier" : "later")} relative to the beat than the first.";
                cue = rushing ? "Listen to the spacing between taps. Keep the last few taps as evenly spaced as the first; do not accelerate to finish the burst."
                    : "Listen through the end of the phrase. Keep the tapping motion comfortable; lower the practice speed if the last notes feel forced.";
                practice = "Start at 90% speed. Try three focused repetitions, then return to 100%. Compare the first and last taps before increasing the speed.";
                strength = Math.Abs(drift);
            }
            else if (spread <= 10 && Math.Abs(mean) >= 15)
            {
                pattern = mean < 0 ? "Even taps, early start" : "Even taps, late start";
                observation = $"These 8 taps average {Math.Abs(mean):0} ms {(mean < 0 ? "early" : "late")}, with only {spread:0} ms of spread.";
                cue = "Keep the spacing. Listen to the lead-in and line up the first tap with the beat, then let the rest follow. Check another familiar map before changing your offset.";
                practice = "Keep 100% speed and a clear lead-in. Try three repetitions, moving the whole phrase onto the beat without speeding up or slowing down.";
                strength = Math.Abs(mean);
            }
            else if (gapError >= 20 && spread >= 12)
            {
                pattern = "Uneven tap spacing";
                observation = $"The notes are evenly spaced, but the tap gaps vary. Timing spread is {spread:0} ms across these 8 notes.";
                cue = "Listen for alternating short and long gaps. Use small, relaxed taps and keep each gap even; watch the timing bar after the attempt.";
                practice = "Start at 90% speed. Try three focused repetitions, then retest at 100%. Check a different layout at the same rhythm afterwards.";
                strength = gapError;
            }
            else continue;
            if (strength <= strongest) continue;
            strongest = strength;
            best = new(pattern, observation, cue, practice, phrase[0].ObjectIndex!.Value,
                phrase[0].StartTimeMs, phrase[7].StartTimeMs, mean, drift);
        }
        return best;
    }
}

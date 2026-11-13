using AimMod.InGame.Analysis;
using AimMod.InGame.Multiplayer;

namespace AimMod.InGame;

sealed record DrillSpec(string Template, double Amplitude, int Sector, double Radius, double Speed, string Reason)
{
    public string Mode => "freeplay";
    public bool RankedEligible => false;
}
static class PracticeDrills
{
    public const string Prefix = "AimMod Drill - ";
    public const string Marker = "AimMod temporary freeplay drill. ";
    public static bool IsDrill(string? name) => name?.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) == true;
    public static bool Valid(DrillSpec s) => s.Template is "static" or "micro" or "tracking" or "reactive"
        && double.IsFinite(s.Amplitude) && s.Amplitude is >= 1 and <= 60 && s.Sector is >= 0 and < 8
        && double.IsFinite(s.Radius) && s.Radius is >= .1 and <= 10
        && double.IsFinite(s.Speed) && s.Speed is >= 0 and <= 180;
    public static DrillSpec? Recommend(ReplayAnalysisResult analysis)
    {
        var t = analysis.Tracking;
        if (analysis.Kind == ScenarioKind.Tracking && t.TrackedSeconds >= 5 && double.IsFinite(t.MeanTargetSpeed) && t.MeanTargetSpeed >= 1) {
            bool reactive = t.Reversals >= 3 && double.IsFinite(t.MeanReversalReactionMs) && t.MeanReversalReactionMs > 100;
            return new(reactive ? "reactive" : "tracking", 15, 0, 1, Math.Clamp(t.MeanTargetSpeed,1,180),
                reactive ? "Direction changes take longer to follow." : "Practise the measured target speed.");
        }
        var cell = analysis.Flicks.Flicks.Where(f => double.IsFinite(f.Efficiency) && double.IsFinite(f.TargetRadius))
            .GroupBy(f => (f.Sector, Band: f.Distance < 5 ? 0 : f.Distance < 15 ? 1 : f.Distance < 30 ? 2 : 3))
            .Where(g => g.Count() >= 5)
            .OrderByDescending(g => g.Average(f => Math.Max(0, f.OvershootRatio) + Math.Abs(1-f.PrimaryEndRatio) + f.Corrections*.1))
            .FirstOrDefault();
        if (cell is null) return null;
        var amplitude = Math.Clamp(cell.Average(f => f.Distance),1,60);
        return new(amplitude < 5 ? "micro" : "static", amplitude, cell.Key.Sector,
            Math.Clamp(cell.Average(f => f.TargetRadius),.1,10), 0, "Practise the direction and distance with the most endpoint correction.");
    }
    public static string Name(DrillSpec spec, string baseText) => Prefix + spec.Template + " - " +
        MatchScenario.Hash(System.Text.Json.JsonSerializer.Serialize(spec) + MatchScenario.Hash(baseText))[..8];
}

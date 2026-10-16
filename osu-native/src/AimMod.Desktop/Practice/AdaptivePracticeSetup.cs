using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.PpTargets;

namespace AimMod.Desktop.Practice;

/// <summary>
/// What an adaptive session practises for a play: the same map, the play's difficulty mods, and its speed as
/// the full-speed target. Rate mods become the target speed; assist and automation mods are never carried over.
/// </summary>
public sealed record AdaptivePracticeSetup(string Key, double TargetRate, IReadOnlyList<string> Mods, double Leniency = AdaptivePracticeCoach.DefaultLeniency)
{
    private static readonly string[] kept = ["HR", "HD", "EZ", "FL"];

    public static AdaptivePracticeSetup For(LocalReplay run)
    {
        var mods = ScoreMods.Acronyms(run).Select(m => m.ToUpperInvariant()).Where(kept.Contains).Distinct().Order(StringComparer.Ordinal).ToArray();
        double rate = PpTargetDifficulty.ClockRate(run.Mods, run.ModsJson) is { } clock && double.IsFinite(clock) ? Math.Clamp(clock, .5, 2) : 1;
        string map = run.BeatmapHash.Length > 0 ? run.BeatmapHash.ToLowerInvariant()
            : run.OnlineBeatmapId > 0 ? "online:" + run.OnlineBeatmapId : "local:" + run.BeatmapId.ToString("N");
        var hits = run.HitStatistics;
        double leniency = AdaptivePracticeCoach.LeniencyFrom(hits?.Great ?? 0, hits?.Ok ?? 0, hits?.Meh ?? 0, run.Accuracy);
        return new($"{map}|{string.Join('+', mods)}|{rate.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}", rate, mods, leniency);
    }

    public string Label => (Mods.Count == 0 ? "No mods" : string.Join("", Mods))
        + (Math.Abs(TargetRate - 1) < .001 ? "" : $" · {TargetRate.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}×");
}

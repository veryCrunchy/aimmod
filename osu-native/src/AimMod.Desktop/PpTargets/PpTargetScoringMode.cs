using AimMod.Desktop.LocalLibrary;

namespace AimMod.Desktop.PpTargets;

public sealed record PpTargetScoringChoice(LocalReplay Run, bool LegacyScore, int OtherModeRecentRuns);

public static class PpTargetScoringMode
{
    public static bool IsLegacy(LocalReplay run) => run.LegacyScore || run.Origin == LocalLibraryOrigin.Stable;

    /// <summary>
    /// The configuration and scoring system (stable or lazer) the player uses now: the setup group
    /// with the highest recency-weighted play count, represented by its most recent play.
    /// </summary>
    public static PpTargetScoringChoice? Select(IEnumerable<LocalReplay> history, IReadOnlyList<string> mods)
    {
        ArgumentNullException.ThrowIfNull(history);
        IReadOnlyList<string> setup = PpTargetMods.Normalise(mods);
        LocalReplay[] runs = history.Where(ScoreMods.IsManualPlay).Where(r => PpTargetMods.Normalise(r.Mods).SequenceEqual(setup)).ToArray();
        if (runs.Length == 0) return null;
        DateTimeOffset newest = runs.Max(r => r.PlayedAt);
        double weight(LocalReplay run) => Math.Pow(.5, Math.Max(0, (newest - run.PlayedAt).TotalDays) / 14);
        var chosen = runs.GroupBy(r => (Configuration: ScoreMods.Configuration(r), Legacy: IsLegacy(r)))
            .OrderByDescending(g => g.Sum(weight)).ThenByDescending(g => g.Max(r => r.PlayedAt)).First();
        LocalReplay latest = chosen.OrderByDescending(r => r.PlayedAt).ThenBy(r => r.ScoreId).First();
        int other = runs.Count(r => IsLegacy(r) != chosen.Key.Legacy && (newest - r.PlayedAt).TotalDays <= 30);
        return new PpTargetScoringChoice(latest, chosen.Key.Legacy, other);
    }
}

using AimMod.Osu.Runtime;

namespace AimMod.Desktop.PpTargets;

public static class PpTargetScanPlanner
{
    public static IReadOnlyList<PpTargetCandidate> Select(PpTargetPreferenceProfile profile,
        IEnumerable<OfficialBeatmapSet> catalog, PpTargetFilters filters, int limit = 500)
    {
        var ranked = PpTargetRanker.Rank(profile, catalog, filters with { Limit = 10_000 });
        // Cover the selected range before spending the entire budget on one easy band.
        var bands = ranked.Candidates.GroupBy(candidate => (int)Math.Floor(candidate.StarRating * 2))
            .OrderBy(group => group.Key).Select(group => new Queue<PpTargetCandidate>(group)).ToArray();
        int budget = Math.Clamp(limit, 1, 1_000);
        var selected = new List<PpTargetCandidate>();
        while (selected.Count < budget)
        {
            bool added = false;
            foreach (var band in bands)
            {
                if (band.TryDequeue(out var candidate))
                {
                    selected.Add(candidate);
                    added = true;
                }
                if (selected.Count >= budget)
                    break;
            }
            if (!added)
                break;
        }
        return selected;
    }
}

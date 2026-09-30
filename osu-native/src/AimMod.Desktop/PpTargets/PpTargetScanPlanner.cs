using AimMod.Osu.Runtime;

namespace AimMod.Desktop.PpTargets;

public static class PpTargetScanPlanner
{
    public const double StretchShare = .25;

    public static IReadOnlyList<PpTargetCandidate> Select(PpTargetPreferenceProfile profile,
        IEnumerable<OfficialBeatmapSet> catalog, PpTargetFilters filters, int limit = 500,
        IReadOnlyDictionary<int, PpTargetEstimate>? estimates = null)
    {
        var ranked = PpTargetRanker.Rank(profile, catalog, filters with { Limit = 50_000 }, estimates);
        int budget = Math.Clamp(limit, 1, 5_000);
        // Reserve exploration for different tempos and durations. Most calculations
        // go to promising, supported maps rather than evenly funding impossible stars.
        var ordered = ranked.Candidates.OrderByDescending(c => c.PassEstimate is { Probability: >= .5 })
            .ThenByDescending(c => c.RankScore).ThenBy(c => c.BeatmapId).ToArray();
        var bands = ordered.GroupBy(c => ((int)Math.Floor(c.StarRating * 2), (int)(c.Bpm / 30), c.TotalLengthSeconds / 90))
            .OrderByDescending(group => group.Max(c => c.RankScore)).Select(group => new Queue<PpTargetCandidate>(group)).ToArray();
        var selected = new List<PpTargetCandidate>();
        int exploration = Math.Min(budget, Math.Max(3, budget / 5));
        while (selected.Count < exploration)
        {
            bool added = false;
            foreach (var band in bands)
            {
                if (band.TryDequeue(out var candidate))
                {
                    selected.Add(candidate);
                    added = true;
                }
                if (selected.Count >= exploration)
                    break;
            }
            if (!added)
                break;
        }
        var selectedIds = selected.Select(c => c.BeatmapId).ToHashSet();
        // Comfortable maps otherwise take the whole budget. Harder maps with the highest
        // expected earned PP keep a share even when their skill fit is lower.
        int stretch = Math.Min(budget - selected.Count, (int)Math.Round(budget * StretchShare));
        double[] typical = ordered.Take(budget).Select(c => c.StarRating).Order().ToArray();
        double pivot = typical.Length == 0 ? 0 : typical[typical.Length / 2];
        foreach (var candidate in ordered.Where(c => !selectedIds.Contains(c.BeatmapId) && c.StarRating > pivot)
                     .OrderByDescending(EarnedPotential).ThenBy(c => c.BeatmapId).Take(stretch))
        {
            selected.Add(candidate);
            selectedIds.Add(candidate.BeatmapId);
        }
        selected.AddRange(ordered.Where(c => !selectedIds.Contains(c.BeatmapId)).Take(budget - selected.Count));
        // Finish the strongest candidates first so incremental results are useful.
        return selected.OrderByDescending(c => c.PassEstimate is { Probability: >= .5 })
            .ThenByDescending(c => c.RankScore).ThenBy(c => c.BeatmapId).ToArray();
    }

    /// <summary>Expected earned PP, or before any calculation a star-based PP proxy times the pass chance.</summary>
    public static double EarnedPotential(PpTargetCandidate candidate) => candidate.ExpectedEarnedPp
        ?? (candidate.Estimate?.ExpectedPp ?? 2 * Math.Pow(Math.Max(0, candidate.StarRating), 2.8))
        * (candidate.PassEstimate?.Probability ?? candidate.Attainability);
}

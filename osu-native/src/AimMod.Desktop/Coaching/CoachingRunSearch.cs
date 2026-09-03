using AimMod.Desktop.LocalLibrary;

namespace AimMod.Desktop.Coaching;

public static class CoachingRunSearch
{
    public static CoachingRunPage Search(
        IReadOnlyList<LocalReplay> runs,
        CoachingRunQuery query)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(query);
        query = query.Normalised();

        string[] terms = query.SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        IEnumerable<LocalReplay> filtered = runs.Where(run =>
            (query.RulesetShortName.Length == 0
             || string.Equals(run.RulesetShortName, query.RulesetShortName, StringComparison.OrdinalIgnoreCase))
            && (!query.RequireReplayFile || run.HasReplayFile)
            && terms.All(term => searchableValues(run).Any(value => value.Contains(term, StringComparison.OrdinalIgnoreCase))));

        IOrderedEnumerable<LocalReplay> ordered = query.Sort switch
        {
            CoachingRunSort.Accuracy => filtered.OrderByDescending(run => finite(run.Accuracy)).ThenByDescending(run => run.PlayedAt),
            CoachingRunSort.StarRating => filtered.OrderByDescending(run => finite(run.StarRating)).ThenByDescending(run => run.PlayedAt),
            CoachingRunSort.Misses => filtered.OrderBy(run => Math.Max(0, run.MissCount)).ThenByDescending(run => run.Accuracy),
            _ => filtered.OrderByDescending(run => run.PlayedAt),
        };

        LocalReplay[] matching = ordered.ToArray();
        CoachingRecentRun[] page = matching.Skip(query.Offset)
                                                .Take(query.Limit)
                                                .Select(toRecentRun)
                                                .ToArray();
        return new CoachingRunPage(page, matching.Length, query.Offset, query.Limit);
    }

    internal static CoachingRecentRun ToRecentRun(LocalReplay run) => toRecentRun(run);

    private static CoachingRecentRun toRecentRun(LocalReplay run) => new(
        run.ScoreId,
        run.BeatmapId,
        run.Title,
        run.Artist,
        run.Difficulty,
        run.RulesetShortName,
        run.PlayedAt,
        finite(run.StarRating),
        finite(run.Accuracy),
        Math.Max(0, run.MissCount),
        run.PerformancePoints is { } pp && double.IsFinite(pp) ? pp : null,
        run.Mods ?? Array.Empty<string>(),
        run.HasReplayFile);

    private static IEnumerable<string> searchableValues(LocalReplay run)
    {
        yield return run.Title ?? string.Empty;
        yield return run.Artist ?? string.Empty;
        yield return run.Difficulty ?? string.Empty;
        yield return run.Player ?? string.Empty;
        yield return run.RulesetShortName ?? string.Empty;
        foreach (string mod in run.Mods ?? Array.Empty<string>())
            yield return mod;
    }

    private static double finite(double value) => double.IsFinite(value) ? value : 0;
}

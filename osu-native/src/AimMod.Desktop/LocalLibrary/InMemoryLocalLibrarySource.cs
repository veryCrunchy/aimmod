namespace AimMod.Desktop.LocalLibrary;

public sealed class InMemoryLocalLibrarySource : ILocalLibrarySource, ILocalReplayMetadataSource
{
    private readonly IReadOnlyList<LocalBeatmapSet> beatmapSets;
    private readonly IReadOnlyList<LocalReplay> replays;

    public InMemoryLocalLibrarySource(IEnumerable<LocalBeatmapSet> beatmapSets, IEnumerable<LocalReplay> replays)
    {
        this.beatmapSets = beatmapSets.ToArray();
        this.replays = replays.ToArray();
    }

    public ValueTask<LocalLibraryPage<LocalBeatmapSet>> SearchBeatmapSetsAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LocalLibraryQuery normalised = query.Normalised();
        string[] terms = splitTerms(normalised.SearchText);

        IEnumerable<LocalBeatmapSet> result = beatmapSets.Select(set => new
        {
            Set = set,
            Difficulties = matchingBeatmapDifficulties(set, normalised).ToArray(),
        }).Where(candidate =>
            candidate.Difficulties.Length > 0
            && matchesTerms(terms, candidate.Set.Title, candidate.Set.Artist, candidate.Set.Creator, candidate.Set.Source,
                string.Join(' ', candidate.Difficulties.Select(difficulty => difficulty.Name))))
          .Select(candidate => candidate.Set with { Difficulties = candidate.Difficulties });

        result = normalised.Sort switch
        {
            LocalLibrarySort.Title => result.OrderBy(set => set.Title, StringComparer.OrdinalIgnoreCase)
                                                    .ThenBy(set => set.Artist, StringComparer.OrdinalIgnoreCase),
            LocalLibrarySort.StarRating => result.OrderByDescending(set => matchingBeatmapDifficulties(set, normalised).Max(difficulty => difficulty.StarRating))
                                                         .ThenBy(set => set.Title, StringComparer.OrdinalIgnoreCase),
            LocalLibrarySort.RecentlyPlayed => result.OrderByDescending(set => set.LastPlayed ?? DateTimeOffset.MinValue)
                                                           .ThenBy(set => set.Title, StringComparer.OrdinalIgnoreCase),
            _ => result.OrderByDescending(set => set.DateAdded)
                       .ThenBy(set => set.Title, StringComparer.OrdinalIgnoreCase),
        };

        return ValueTask.FromResult(page(result, normalised));
    }

    public ValueTask<LocalLibraryPage<LocalReplay>> SearchReplaysAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LocalLibraryQuery normalised = query.Normalised();
        string[] terms = splitTerms(normalised.SearchText);

        IEnumerable<LocalReplay> result = replays.Where(replay =>
            matchesRuleset(replay.RulesetShortName, normalised.RulesetShortName)
            && matchesStars(replay.StarRating, normalised)
            && matchesTerms(terms, replay.Title, replay.Artist, replay.Difficulty, replay.Player, string.Join(' ', replay.Mods)));

        result = normalised.Sort switch
        {
            LocalLibrarySort.Title => result.OrderBy(replay => replay.Title, StringComparer.OrdinalIgnoreCase)
                                                    .ThenByDescending(replay => replay.PlayedAt),
            LocalLibrarySort.StarRating => result.OrderByDescending(replay => replay.StarRating)
                                                         .ThenByDescending(replay => replay.PlayedAt),
            LocalLibrarySort.Score => result.OrderByDescending(replay => replay.TotalScore)
                                                  .ThenByDescending(replay => replay.PlayedAt),
            LocalLibrarySort.Accuracy => result.OrderByDescending(replay => replay.Accuracy)
                                                     .ThenByDescending(replay => replay.PlayedAt),
            _ => result.OrderByDescending(replay => replay.PlayedAt),
        };

        return ValueTask.FromResult(page(result, normalised));
    }

    public void Invalidate()
    {
        // A fixed in-memory source has no backing snapshot to rebuild.
    }

    private static IEnumerable<LocalBeatmapDifficulty> matchingBeatmapDifficulties(LocalBeatmapSet set, LocalLibraryQuery query) =>
        set.Difficulties.Where(difficulty =>
            matchesRuleset(difficulty.RulesetShortName, query.RulesetShortName)
            && matchesStars(difficulty.StarRating, query));

    private static bool matchesRuleset(string value, string query) =>
        string.IsNullOrWhiteSpace(query) || string.Equals(value, query, StringComparison.OrdinalIgnoreCase);

    private static bool matchesStars(double value, LocalLibraryQuery query) =>
        (query.MinimumStars is null || value >= query.MinimumStars)
        && (query.MaximumStars is null || value <= query.MaximumStars);

    private static bool matchesTerms(IReadOnlyList<string> terms, params string[] fields)
    {
        if (terms.Count == 0)
            return true;

        string searchable = string.Join('\n', fields);
        return terms.All(term => searchable.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string[] splitTerms(string searchText) => searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static LocalLibraryPage<T> page<T>(IEnumerable<T> values, LocalLibraryQuery query)
    {
        T[] materialised = values.ToArray();
        T[] items = materialised.Skip(query.Offset).Take(query.Limit).ToArray();
        return new LocalLibraryPage<T>(items, materialised.Length, query.Offset, query.Limit);
    }
}

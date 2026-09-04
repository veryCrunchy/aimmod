namespace AimMod.Desktop.LocalLibrary;

public sealed class CompositeLocalLibrarySource : ILocalLibrarySource
{
    private const int source_page_size = 200;
    private readonly IReadOnlyList<ILocalLibrarySource> sources;

    public CompositeLocalLibrarySource(IEnumerable<ILocalLibrarySource> sources)
    {
        this.sources = sources?.Where(source => source is not null).Distinct().ToArray()
                       ?? throw new ArgumentNullException(nameof(sources));
        if (this.sources.Count == 0)
            throw new ArgumentException("At least one local library source is required.", nameof(sources));
    }

    public async ValueTask<LocalLibraryPage<LocalBeatmapSet>> SearchBeatmapSetsAsync(
        LocalLibraryQuery query,
        CancellationToken cancellationToken = default)
    {
        LocalLibraryQuery normalised = query.Normalised();
        SourceRows<LocalBeatmapSet>[] rows = await Task.WhenAll(sources.Select(source =>
            readPrefix(source.SearchBeatmapSetsAsync, normalised, cancellationToken))).ConfigureAwait(false);
        LocalBeatmapSet[] raw = rows.SelectMany(row => row.Items).ToArray();
        LocalBeatmapSet[] merged = raw
            .GroupBy(mapKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => mergeSets(group.ToArray()))
            .ToArray();
        LocalLibraryPage<LocalBeatmapSet> page = await new InMemoryLocalLibrarySource(merged, [])
            .SearchBeatmapSetsAsync(normalised, cancellationToken).ConfigureAwait(false);
        int total = Math.Max(page.Offset + page.Items.Count, rows.Sum(row => row.Total) - (raw.Length - merged.Length));
        return page with { Total = total };
    }

    public async ValueTask<LocalLibraryPage<LocalReplay>> SearchReplaysAsync(
        LocalLibraryQuery query,
        CancellationToken cancellationToken = default)
    {
        LocalLibraryQuery normalised = query.Normalised();
        SourceRows<LocalReplay>[] rows = await Task.WhenAll(sources.Select(source =>
            readPrefix(source.SearchReplaysAsync, normalised, cancellationToken))).ConfigureAwait(false);
        LocalReplay[] raw = rows.SelectMany(row => row.Items).ToArray();
        LocalReplay[] merged = raw
            .GroupBy(replayKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(replayQuality).First())
            .ToArray();
        LocalLibraryPage<LocalReplay> page = await new InMemoryLocalLibrarySource([], merged)
            .SearchReplaysAsync(normalised, cancellationToken).ConfigureAwait(false);
        int total = Math.Max(page.Offset + page.Items.Count, rows.Sum(row => row.Total) - (raw.Length - merged.Length));
        return page with { Total = total };
    }

    public void Invalidate()
    {
        foreach (ILocalLibrarySource source in sources)
            source.Invalidate();
    }

    private static async Task<SourceRows<T>> readPrefix<T>(
        Func<LocalLibraryQuery, CancellationToken, ValueTask<LocalLibraryPage<T>>> search,
        LocalLibraryQuery query,
        CancellationToken cancellationToken)
    {
        int wanted = query.Offset + query.Limit;
        var rows = new List<T>(wanted);
        int total = 0;
        while (rows.Count < wanted)
        {
            LocalLibraryPage<T> page = await search(
                query with { Offset = rows.Count, Limit = Math.Min(source_page_size, wanted - rows.Count) },
                cancellationToken).ConfigureAwait(false);
            rows.AddRange(page.Items);
            total = page.Total;
            if (!page.HasMore || page.Items.Count == 0)
                break;
        }
        return new SourceRows<T>(rows.ToArray(), total);
    }

    private static string mapKey(LocalBeatmapSet set)
    {
        if (set.OnlineId > 0)
            return $"online:{set.OnlineId}";
        string hashes = string.Join(':', set.Difficulties.Select(difficulty => difficulty.BeatmapHash)
            .Where(hash => hash.Length > 0)
            .Order(StringComparer.OrdinalIgnoreCase));
        return hashes.Length > 0 ? "hash:" + hashes : $"local:{set.Artist}:{set.Title}:{set.Creator}";
    }

    private static LocalBeatmapSet mergeSets(IReadOnlyList<LocalBeatmapSet> sets)
    {
        LocalBeatmapSet preferred = sets.OrderByDescending(set => set.Difficulties.Count(difficulty => difficulty.StarRating > 0))
            .ThenByDescending(set => set.BackgroundPath.Length > 0)
            .First();
        LocalBeatmapDifficulty[] difficulties = sets.SelectMany(set => set.Difficulties)
            .GroupBy(difficulty => difficulty.OnlineId > 0 ? $"online:{difficulty.OnlineId}" : $"hash:{difficulty.BeatmapHash}")
            .Select(group => group.OrderByDescending(difficulty => difficulty.StarRating > 0).First())
            .OrderBy(difficulty => difficulty.StarRating)
            .ToArray();
        return preferred with
        {
            DateAdded = sets.Min(set => set.DateAdded),
            LastPlayed = sets.Where(set => set.LastPlayed is not null).Max(set => set.LastPlayed),
            Difficulties = difficulties,
            LocalReplayCount = sets.Where(set => set.LocalReplayCount is not null).Sum(set => set.LocalReplayCount),
            BackgroundPath = sets.Select(set => set.BackgroundPath).FirstOrDefault(path => path.Length > 0) ?? string.Empty,
        };
    }

    private static string replayKey(LocalReplay replay) => replay.OnlineScoreId > 0
        ? $"online:{replay.OnlineScoreId}"
        : $"local:{replay.BeatmapHash}:{replay.Player}:{replay.PlayedAt.UtcTicks}:{replay.TotalScore}:{string.Join(',', replay.Mods.Order(StringComparer.OrdinalIgnoreCase))}";

    private static int replayQuality(LocalReplay replay) =>
        (replay.HasReplayFile ? 4 : 0)
        + (replay.PerformancePoints is not null ? 2 : 0)
        + (replay.IsLocallyStored ? 1 : 0);

    private sealed record SourceRows<T>(T[] Items, int Total);
}

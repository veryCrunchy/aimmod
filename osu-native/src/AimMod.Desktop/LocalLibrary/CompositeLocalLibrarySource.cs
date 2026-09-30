namespace AimMod.Desktop.LocalLibrary;

public sealed class CompositeLocalLibrarySource : ILocalLibrarySource, ILocalLibraryProgressSource
{
    private const int source_page_size = 200;
    private static readonly TimeSpan prefix_lifetime = TimeSpan.FromSeconds(10);
    private readonly IReadOnlyList<ILocalLibrarySource> sources;
    private readonly TimeSpan sourceTimeout;
    private readonly PrefixCache<LocalBeatmapSet> setPrefixes = new();
    private readonly PrefixCache<LocalReplay> replayPrefixes = new();
    public LocalLibraryProgress? Progress => sources.OfType<ILocalLibraryProgressSource>()
        .Select(source => source.Progress).FirstOrDefault(progress => progress is not null);

    public CompositeLocalLibrarySource(IEnumerable<ILocalLibrarySource> sources, TimeSpan? sourceTimeout = null)
    {
        this.sources = sources?.Where(source => source is not null)
                           .SelectMany(source => source is CompositeLocalLibrarySource composite ? composite.sources : new[] { source })
                           .Distinct().ToArray()
                       ?? throw new ArgumentNullException(nameof(sources));
        this.sourceTimeout = sourceTimeout ?? TimeSpan.FromSeconds(45);
        if (this.sources.Count == 0)
            throw new ArgumentException("At least one local library source is required.", nameof(sources));
    }

    public async ValueTask<LocalLibraryPage<LocalBeatmapSet>> SearchBeatmapSetsAsync(
        LocalLibraryQuery query,
        CancellationToken cancellationToken = default)
    {
        LocalLibraryQuery normalised = query.Normalised();
        SourceRows<LocalBeatmapSet>[] rows = await readAllAsync(setPrefixes, normalised,
            source => source.SearchBeatmapSetsAsync, cancellationToken).ConfigureAwait(false);
        ensureAvailable(rows);
        LocalBeatmapSet[] raw = rows.SelectMany(row => row.Items).ToArray();
        LocalBeatmapSet[] merged = raw
            .GroupBy(mapKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => mergeSets(group.ToArray()))
            .ToArray();
        LocalLibraryPage<LocalBeatmapSet> page = await new InMemoryLocalLibrarySource(merged, [])
            .SearchBeatmapSetsAsync(normalised, cancellationToken).ConfigureAwait(false);
        int total = Math.Max(page.Offset + page.Items.Count, rows.Sum(row => row.Total) - (raw.Length - merged.Length));
        return page with { Total = total, Warning = warning(rows) };
    }

    public async ValueTask<LocalLibraryPage<LocalReplay>> SearchReplaysAsync(
        LocalLibraryQuery query,
        CancellationToken cancellationToken = default)
    {
        LocalLibraryQuery normalised = query.Normalised();
        SourceRows<LocalReplay>[] rows = await readAllAsync(replayPrefixes, normalised,
            source => source.SearchReplaysAsync, cancellationToken).ConfigureAwait(false);
        ensureAvailable(rows);
        LocalReplay[] raw = rows.SelectMany(row => row.Items).ToArray();
        LocalReplay[] merged = raw
            .GroupBy(replayKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(replayQuality).First())
            .ToArray();
        LocalLibraryPage<LocalReplay> page = await new InMemoryLocalLibrarySource([], merged)
            .SearchReplaysAsync(normalised, cancellationToken).ConfigureAwait(false);
        int total = Math.Max(page.Offset + page.Items.Count, rows.Sum(row => row.Total) - (raw.Length - merged.Length));
        return page with { Total = total, Warning = warning(rows) };
    }

    public void Invalidate()
    {
        setPrefixes.Clear();
        replayPrefixes.Clear();
        foreach (ILocalLibrarySource source in sources)
            source.Invalidate();
    }

    // Later pages of a query reuse the rows already read from each source, so paging
    // deeper only fetches new rows. A first-page request always starts fresh.
    private async Task<SourceRows<T>[]> readAllAsync<T>(
        PrefixCache<T> cache,
        LocalLibraryQuery query,
        Func<ILocalLibrarySource, Func<LocalLibraryQuery, CancellationToken, ValueTask<LocalLibraryPage<T>>>> searchOf,
        CancellationToken cancellationToken)
    {
        LocalLibraryQuery key = query with { Offset = 0, Limit = 1 };
        SourcePrefix<T>[] prefixes = cache.Acquire(key, sources.Count, prefix_lifetime, reuse: query.Offset > 0);
        SourceRows<T>[] rows = await Task.WhenAll(prefixes.Select((prefix, index) =>
            readSafely(prefix, searchOf(sources[index]), query, cancellationToken))).ConfigureAwait(false);
        if (rows.Any(row => row.Error is not null || row.Warning is not null))
            cache.Remove(key, prefixes);
        return rows;
    }

    private async Task<SourceRows<T>> readSafely<T>(
        SourcePrefix<T> prefix,
        Func<LocalLibraryQuery, CancellationToken, ValueTask<LocalLibraryPage<T>>> search,
        LocalLibraryQuery query, CancellationToken cancellationToken)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            return await readPrefix(prefix, search, query, request.Token)
                .WaitAsync(sourceTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            request.Cancel();
            return new([], 0, error);
        }
    }

    private static void ensureAvailable<T>(SourceRows<T>[] rows)
    {
        if (rows.All(row => row.Error is not null))
            throw new InvalidOperationException("No local osu! library responded. Check the installation locations and retry.", rows[0].Error);
    }

    private static string? warning<T>(SourceRows<T>[] rows) => rows.Any(row => row.Error is not null)
        ? "Partial library: an osu! installation is unavailable. Retry to include it."
        : rows.Select(row => row.Warning).FirstOrDefault(message => message is not null);

    private static async Task<SourceRows<T>> readPrefix<T>(
        SourcePrefix<T> prefix,
        Func<LocalLibraryQuery, CancellationToken, ValueTask<LocalLibraryPage<T>>> search,
        LocalLibraryQuery query,
        CancellationToken cancellationToken)
    {
        int wanted = query.Offset + query.Limit;
        await prefix.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (prefix.Rows.Count < wanted && !prefix.Exhausted)
            {
                LocalLibraryPage<T> page = await search(
                    query with { Offset = prefix.Rows.Count, Limit = source_page_size },
                    cancellationToken).ConfigureAwait(false);
                prefix.Rows.AddRange(page.Items);
                prefix.Total = page.Total;
                prefix.Warning ??= page.Warning;
                prefix.Exhausted = !page.HasMore || page.Items.Count == 0;
            }
            return new SourceRows<T>(prefix.Rows.Take(wanted).ToArray(), prefix.Total, Warning: prefix.Warning);
        }
        finally { prefix.Gate.Release(); }
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

    private sealed record SourceRows<T>(T[] Items, int Total, Exception? Error = null, string? Warning = null);

    private sealed class SourcePrefix<T>
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public List<T> Rows { get; } = [];
        public int Total { get; set; }
        public string? Warning { get; set; }
        public bool Exhausted { get; set; }
    }

    private sealed class PrefixCache<T>
    {
        private const int maximum_entries = 8;
        private readonly object gate = new();
        private readonly Dictionary<LocalLibraryQuery, (SourcePrefix<T>[] Prefixes, DateTime ExpiresAt)> entries = new();

        public SourcePrefix<T>[] Acquire(LocalLibraryQuery key, int sourceCount, TimeSpan lifetime, bool reuse)
        {
            lock (gate)
            {
                DateTime now = DateTime.UtcNow;
                if (reuse && entries.TryGetValue(key, out var existing) && existing.ExpiresAt > now)
                {
                    entries[key] = (existing.Prefixes, now + lifetime);
                    return existing.Prefixes;
                }
                foreach (LocalLibraryQuery expired in entries.Where(entry => entry.Value.ExpiresAt <= now).Select(entry => entry.Key).ToArray())
                    entries.Remove(expired);
                if (entries.Count >= maximum_entries)
                    entries.Remove(entries.MinBy(entry => entry.Value.ExpiresAt).Key);
                SourcePrefix<T>[] created = Enumerable.Range(0, sourceCount).Select(_ => new SourcePrefix<T>()).ToArray();
                entries[key] = (created, now + lifetime);
                return created;
            }
        }

        public void Remove(LocalLibraryQuery key, SourcePrefix<T>[] prefixes)
        {
            lock (gate)
            {
                if (entries.TryGetValue(key, out var existing) && ReferenceEquals(existing.Prefixes, prefixes))
                    entries.Remove(key);
            }
        }

        public void Clear()
        {
            lock (gate)
                entries.Clear();
        }
    }
}

using AimMod.Osu.Runtime;

namespace AimMod.Desktop.PpTargets;

public enum PpTargetCatalogScanStopReason
{
    Completed,
    PageLimit,
    SetLimit,
    RepeatedCursor,
    RequestFailed,
}

public sealed record PpTargetCatalogScanProgress(int Pages, int Sets, int Difficulties);

public sealed record PpTargetCatalogScanResult(
    OfficialBeatmapRequestStatus Status,
    IReadOnlyList<OfficialBeatmapSet> BeatmapSets,
    int Pages,
    PpTargetCatalogScanStopReason StopReason)
{
    public int SetCount => BeatmapSets.Count;
    public int DifficultyCount => BeatmapSets.Sum(set => set.Difficulties.Count);
    public bool IsPartial => StopReason != PpTargetCatalogScanStopReason.Completed;
}

public sealed class PpTargetCatalogScanner
{
    private readonly IOfficialBeatmapDiscoveryClient client;
    private readonly int maximumPages;
    private readonly int maximumSets;

    public PpTargetCatalogScanner(IOfficialBeatmapDiscoveryClient client, int maximumPages = 24, int maximumSets = 1200)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        if (maximumPages is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maximumPages));
        if (maximumSets is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(maximumSets));
        this.maximumPages = maximumPages;
        this.maximumSets = maximumSets;
    }

    public async Task<PpTargetCatalogScanResult> ScanAsync(OfficialBeatmapSearchQuery query,
        CancellationToken cancellationToken = default, IProgress<PpTargetCatalogScanProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        OfficialBeatmapSearchQuery captured = query.Normalised() with { Limit = 50 };
        var streams = new[] { captured.Sort, OfficialBeatmapSort.Rating, OfficialBeatmapSort.Plays, OfficialBeatmapSort.Favourites }
            .Distinct().Select(sort => new ScanStream(sort, sort == captured.Sort ? captured.Cursor : null)).ToArray();
        var sets = new Dictionary<int, OfficialBeatmapSet>();
        int pages = 0;
        bool repeatedCursor = false;
        progress?.Report(new(0, 0, 0));
        while (streams.Any(stream => !stream.Complete))
        {
            foreach (ScanStream stream in streams.Where(stream => !stream.Complete))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pages >= maximumPages) return result(OfficialBeatmapRequestStatus.Success, PpTargetCatalogScanStopReason.PageLimit);
                OfficialBeatmapSearchResult page;
                try
                {
                    page = await client.SearchAsync(captured with { Sort = stream.Sort, Cursor = stream.Cursor }, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is HttpRequestException or IOException or TaskCanceledException)
                {
                    page = OfficialBeatmapSearchResult.Empty(OfficialBeatmapRequestStatus.NetworkError);
                }
                cancellationToken.ThrowIfCancellationRequested();
                pages++;
                foreach (OfficialBeatmapSet set in page.BeatmapSets)
                {
                    if (set.BeatmapSetId <= 0) continue;
                    if (sets.TryGetValue(set.BeatmapSetId, out OfficialBeatmapSet? existing))
                    {
                        OfficialBeatmapDifficulty[] merged = existing.Difficulties.Concat(set.Difficulties)
                            .Where(difficulty => difficulty.BeatmapId > 0).GroupBy(difficulty => difficulty.BeatmapId)
                            .Select(group => group.Last()).ToArray();
                        sets[set.BeatmapSetId] = set with { Difficulties = Array.AsReadOnly(merged) };
                    }
                    else if (sets.Count < maximumSets)
                    {
                        sets.Add(set.BeatmapSetId, set with { Difficulties = Array.AsReadOnly(set.Difficulties
                            .Where(difficulty => difficulty.BeatmapId > 0).DistinctBy(difficulty => difficulty.BeatmapId).ToArray()) });
                    }
                }
                progress?.Report(new(pages, sets.Count, sets.Values.Sum(set => set.Difficulties.Count)));
                if (page.Status != OfficialBeatmapRequestStatus.Success)
                    return result(page.Status, PpTargetCatalogScanStopReason.RequestFailed);
                if (sets.Count >= maximumSets) return result(page.Status, PpTargetCatalogScanStopReason.SetLimit);
                if (string.IsNullOrEmpty(page.NextCursor)) stream.Complete = true;
                else if (!stream.Seen.Add(page.NextCursor))
                {
                    stream.Complete = true;
                    repeatedCursor = true;
                }
                else stream.Cursor = page.NextCursor;
            }
        }
        return result(OfficialBeatmapRequestStatus.Success, repeatedCursor ? PpTargetCatalogScanStopReason.RepeatedCursor : PpTargetCatalogScanStopReason.Completed);

        PpTargetCatalogScanResult result(OfficialBeatmapRequestStatus status, PpTargetCatalogScanStopReason reason) =>
            new(status, Array.AsReadOnly(sets.Values.ToArray()), pages, reason);
    }

    private sealed class ScanStream(OfficialBeatmapSort sort, string? cursor)
    {
        public OfficialBeatmapSort Sort { get; } = sort;
        public string? Cursor { get; set; } = cursor;
        public bool Complete { get; set; }
        public HashSet<string> Seen { get; } = string.IsNullOrEmpty(cursor) ? new(StringComparer.Ordinal) : new([cursor], StringComparer.Ordinal);
    }
}

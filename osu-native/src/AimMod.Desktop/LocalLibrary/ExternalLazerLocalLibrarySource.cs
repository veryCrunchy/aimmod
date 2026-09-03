using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop.LocalLibrary;

public sealed class ExternalLazerLocalLibrarySource : ILocalLibrarySource
{
    private readonly string libraryRoot;
    private readonly Func<ExternalLazerCatalogSearchRequest, CancellationToken, Task<ExternalLazerCatalogSearchResult>> search;
    private readonly SemaphoreSlim queryGate = new(1, 1);

    public ExternalLazerLocalLibrarySource(string libraryRoot)
        : this(libraryRoot, searchWithPrivateWorker)
    {
    }

    internal ExternalLazerLocalLibrarySource(
        string libraryRoot,
        Func<ExternalLazerCatalogSearchRequest, CancellationToken, Task<ExternalLazerCatalogSearchResult>> search)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        if (!Path.IsPathFullyQualified(libraryRoot))
            throw new ArgumentException("The external lazer library root must be absolute.", nameof(libraryRoot));

        this.libraryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
        this.search = search ?? throw new ArgumentNullException(nameof(search));
    }

    public async ValueTask<LocalLibraryPage<LocalBeatmapSet>> SearchBeatmapSetsAsync(
        LocalLibraryQuery query,
        CancellationToken cancellationToken = default)
    {
        LocalLibraryQuery normalised = query.Normalised();
        ExternalLazerCatalogSearchResult result = await searchWithoutAbandoningSnapshot(
            toRequest(normalised, ExternalLazerCatalogEntryKind.BeatmapSets),
            cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, string> artworkPaths = resolveArtworkPaths(
            result.BeatmapSets.Select(set => (set.SetId.ToString("D"), set.BackgroundHash)));

        LocalBeatmapSet[] sets = result.BeatmapSets.Select(set => new LocalBeatmapSet(
            set.SetId,
            set.OnlineId,
            set.Title,
            set.Artist,
            set.Creator,
            set.Source,
            set.DateAdded,
            set.LastPlayed,
            set.Difficulties.Select(difficulty => new LocalBeatmapDifficulty(
                difficulty.BeatmapId,
                difficulty.OnlineId,
                difficulty.Name,
                difficulty.RulesetShortName,
                difficulty.StarRating,
                difficulty.Bpm,
                difficulty.LengthMilliseconds,
                difficulty.CircleSize,
                difficulty.ApproachRate,
                difficulty.OverallDifficulty,
                difficulty.DrainRate,
                difficulty.LocalScoreCount)).ToArray(),
            set.LocalReplayCount,
            artworkPaths.GetValueOrDefault(set.SetId.ToString("D"), string.Empty))).ToArray();

        return new LocalLibraryPage<LocalBeatmapSet>(sets, result.Total, result.Offset, result.Limit);
    }

    public async ValueTask<LocalLibraryPage<LocalReplay>> SearchReplaysAsync(
        LocalLibraryQuery query,
        CancellationToken cancellationToken = default)
    {
        LocalLibraryQuery normalised = query.Normalised();
        ExternalLazerCatalogSearchResult result = await searchWithoutAbandoningSnapshot(
            toRequest(normalised, ExternalLazerCatalogEntryKind.Replays),
            cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, string> artworkPaths = resolveArtworkPaths(
            result.Replays.Select(replay => (replay.ScoreId.ToString("D"), replay.BackgroundHash)));

        LocalReplay[] replays = result.Replays.Select(replay => new LocalReplay(
            replay.ScoreId,
            replay.SetId,
            replay.BeatmapId,
            replay.Title,
            replay.Artist,
            replay.Difficulty,
            replay.RulesetShortName,
            replay.Player,
            replay.PlayedAt,
            replay.StarRating,
            replay.Accuracy,
            replay.TotalScore,
            replay.MaxCombo,
            replay.MissCount,
            replay.PerformancePoints,
            replay.Mods,
            replay.HasReplayFile,
            replay.BeatmapHash,
            artworkPaths.GetValueOrDefault(replay.ScoreId.ToString("D"), string.Empty))).ToArray();

        return new LocalLibraryPage<LocalReplay>(replays, result.Total, result.Offset, result.Limit);
    }

    public void Invalidate()
    {
        // Every query gets a fresh, transactionally consistent lazer snapshot.
    }

    private async Task<ExternalLazerCatalogSearchResult> searchWithoutAbandoningSnapshot(
        ExternalLazerCatalogSearchRequest request,
        CancellationToken cancellationToken)
    {
        await queryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Once dispatched, let the dedicated worker finish and delete its
            // private Realm snapshot. The controller's revision check discards
            // this result if a newer query superseded it.
            ExternalLazerCatalogSearchResult result = await search(request, CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            queryGate.Release();
        }
    }

    private ExternalLazerCatalogSearchRequest toRequest(LocalLibraryQuery query, ExternalLazerCatalogEntryKind kind) => new(
        libraryRoot,
        kind,
        query.SearchText,
        query.RulesetShortName,
        query.MinimumStars,
        query.MaximumStars,
        query.Sort switch
        {
            LocalLibrarySort.RecentlyPlayed => ExternalLazerCatalogSort.RecentlyPlayed,
            LocalLibrarySort.Title => ExternalLazerCatalogSort.Title,
            LocalLibrarySort.StarRating => ExternalLazerCatalogSort.StarRating,
            LocalLibrarySort.Score => ExternalLazerCatalogSort.Score,
            LocalLibrarySort.Accuracy => ExternalLazerCatalogSort.Accuracy,
            _ => ExternalLazerCatalogSort.RecentlyAdded,
        },
        query.Offset,
        query.Limit);

    private IReadOnlyDictionary<string, string> resolveArtworkPaths(IEnumerable<(string OwnerId, string Hash)> entries)
    {
        LazerStoredFileReference[] references = entries
            .Where(entry => entry.Hash.Length == 64)
            .DistinctBy(entry => entry.OwnerId)
            .Select(entry => new LazerStoredFileReference(
                LazerLibraryAssetKind.Background,
                entry.OwnerId,
                "background",
                entry.Hash))
            .ToArray();
        if (references.Length == 0)
            return new Dictionary<string, string>();

        try
        {
            return new LazerHashedFileResolver()
                   .Resolve(Path.Combine(libraryRoot, "files"), references)
                   .Where(file => file.SourcePath is not null)
                   .ToDictionary(file => file.Reference.OwnerId, file => file.SourcePath!, StringComparer.OrdinalIgnoreCase);
        }
        catch (ExternalLazerLibraryException)
        {
            // Catalog metadata is still useful if an artwork file disappeared
            // between the snapshot query and the file-store lookup.
            return new Dictionary<string, string>();
        }
    }

    private static async Task<ExternalLazerCatalogSearchResult> searchWithPrivateWorker(
        ExternalLazerCatalogSearchRequest request,
        CancellationToken cancellationToken)
    {
        await using SidecarRuntimeClient runtime = SidecarRuntimeClient.Start();
        var client = new ExternalLazerCatalogClient(new SidecarRuntimeRequestClient(runtime));
        return await client.SearchAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

public interface ILocalLibrarySourceChanged
{
    event Action? SourceChanged;
}

public sealed class SwitchableLocalLibrarySource : ILocalLibrarySource, ILocalLibrarySourceChanged
{
    private ILocalLibrarySource current;

    public SwitchableLocalLibrarySource(ILocalLibrarySource initialSource)
    {
        current = initialSource ?? throw new ArgumentNullException(nameof(initialSource));
    }

    public event Action? SourceChanged;

    public ILocalLibrarySource Current => Volatile.Read(ref current);

    public void SwitchTo(ILocalLibrarySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ILocalLibrarySource previous = Interlocked.Exchange(ref current, source);
        if (ReferenceEquals(previous, source))
            return;

        source.Invalidate();
        SourceChanged?.Invoke();
    }

    public ValueTask<LocalLibraryPage<LocalBeatmapSet>> SearchBeatmapSetsAsync(
        LocalLibraryQuery query,
        CancellationToken cancellationToken = default) =>
        Current.SearchBeatmapSetsAsync(query, cancellationToken);

    public ValueTask<LocalLibraryPage<LocalReplay>> SearchReplaysAsync(
        LocalLibraryQuery query,
        CancellationToken cancellationToken = default) =>
        Current.SearchReplaysAsync(query, cancellationToken);

    public void Invalidate() => Current.Invalidate();
}

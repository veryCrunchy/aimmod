using osu.Game.Beatmaps;
using osu.Game.Database;
using osu.Game.Scoring;

namespace AimMod.Desktop.LocalLibrary;

public interface ILocalReplayResolver
{
    ValueTask<Score?> LoadReplayAsync(Guid scoreId, CancellationToken cancellationToken = default);
}

public interface ILocalReplayMetadataSource
{
    ValueTask<LocalLibraryPage<LocalReplay>> SearchReplaysAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default);

    void Invalidate();
}

public sealed class EmptyLocalReplayMetadataSource : ILocalReplayMetadataSource
{
    public ValueTask<LocalLibraryPage<LocalReplay>> SearchReplaysAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LocalLibraryQuery normalised = query.Normalised();
        return ValueTask.FromResult(new LocalLibraryPage<LocalReplay>(Array.Empty<LocalReplay>(), 0, normalised.Offset, normalised.Limit));
    }

    public void Invalidate()
    {
    }
}

public sealed class OsuManagerLocalLibrarySource : ILocalLibrarySource, ILocalReplayResolver
{
    private readonly BeatmapStore beatmapStore;
    private readonly ScoreManager scoreManager;
    private readonly ILocalReplayMetadataSource replayMetadataSource;
    private readonly object snapshotLock = new();
    private Task<InMemoryLocalLibrarySource>? beatmapIndexTask;

    public OsuManagerLocalLibrarySource(
        BeatmapStore beatmapStore,
        ScoreManager scoreManager,
        ILocalReplayMetadataSource? replayMetadataSource = null)
    {
        this.beatmapStore = beatmapStore;
        this.scoreManager = scoreManager;
        this.replayMetadataSource = replayMetadataSource ?? new EmptyLocalReplayMetadataSource();
    }

    public async ValueTask<LocalLibraryPage<LocalBeatmapSet>> SearchBeatmapSetsAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default)
    {
        InMemoryLocalLibrarySource index = await getBeatmapIndex(cancellationToken).ConfigureAwait(false);
        return await index.SearchBeatmapSetsAsync(query, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<LocalLibraryPage<LocalReplay>> SearchReplaysAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default) =>
        replayMetadataSource.SearchReplaysAsync(query, cancellationToken);

    public async ValueTask<Score?> LoadReplayAsync(Guid scoreId, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScoreInfo? scoreInfo = scoreManager.Query(score => score.ID == scoreId && !score.DeletePending);
            cancellationToken.ThrowIfCancellationRequested();
            return scoreInfo is null ? null : scoreManager.GetScore(scoreInfo);
        }, cancellationToken).ConfigureAwait(false);
    }

    public void Invalidate()
    {
        lock (snapshotLock)
            beatmapIndexTask = null;

        replayMetadataSource.Invalidate();
    }

    private Task<InMemoryLocalLibrarySource> getBeatmapIndex(CancellationToken cancellationToken)
    {
        Task<InMemoryLocalLibrarySource> index;

        lock (snapshotLock)
            index = beatmapIndexTask ??= Task.Run(() => buildBeatmapIndex(CancellationToken.None), CancellationToken.None);

        return index.WaitAsync(cancellationToken);
    }

    private InMemoryLocalLibrarySource buildBeatmapIndex(CancellationToken cancellationToken)
    {
        IReadOnlyList<BeatmapSetInfo> detachedSets = beatmapStore.GetBeatmapSets(cancellationToken).ToArray();
        var beatmapSets = new List<LocalBeatmapSet>(detachedSets.Count);

        foreach (BeatmapSetInfo detachedSet in detachedSets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeatmapInfo[] visibleDifficulties = detachedSet.Beatmaps.Where(beatmap => !beatmap.Hidden).ToArray();
            if (visibleDifficulties.Length == 0)
                continue;

            LocalBeatmapDifficulty[] difficulties = visibleDifficulties.Select(beatmap => new LocalBeatmapDifficulty(
                beatmap.ID,
                beatmap.OnlineID,
                beatmap.DifficultyName,
                beatmap.Ruleset.ShortName,
                beatmap.StarRating,
                beatmap.BPM,
                beatmap.Length,
                beatmap.Difficulty.CircleSize,
                beatmap.Difficulty.ApproachRate,
                beatmap.Difficulty.OverallDifficulty,
                beatmap.Difficulty.DrainRate,
                null)).OrderBy(difficulty => difficulty.StarRating).ToArray();

            BeatmapMetadata metadata = visibleDifficulties[0].Metadata;
            beatmapSets.Add(new LocalBeatmapSet(
                detachedSet.ID,
                detachedSet.OnlineID,
                metadata.Title,
                metadata.Artist,
                metadata.Author.Username,
                metadata.Source,
                detachedSet.DateAdded,
                visibleDifficulties.Max(beatmap => beatmap.LastPlayed),
                difficulties,
                null));
        }

        return new InMemoryLocalLibrarySource(beatmapSets, Array.Empty<LocalReplay>());
    }
}

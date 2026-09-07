namespace AimMod.Desktop.LocalLibrary;

internal static class ReplayBeatmapResolver
{
    public static async Task<LocalReplay> ResolveSourceAsync(ILocalLibrarySource source, LocalReplay replay, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (replay.IsLocallyStored && (replay.BeatmapHash.Length > 0 || replay.BeatmapPath.Length > 0)) return replay;
        int offset = 0;
        do
        {
            var page = await source.SearchBeatmapSetsAsync(new LocalLibraryQuery(RulesetShortName: replay.RulesetShortName,
                Offset: offset, Limit: 200), token).ConfigureAwait(false);
            foreach (var set in page.Items)
                foreach (var difficulty in set.Difficulties)
                    if (difficulty.RulesetShortName == replay.RulesetShortName &&
                        ((replay.OnlineBeatmapId > 0 && difficulty.OnlineId == replay.OnlineBeatmapId)
                         || (replay.BeatmapHash.Length > 0 && replay.BeatmapHash.Equals(difficulty.BeatmapHash, StringComparison.OrdinalIgnoreCase))))
                        return replay with { SetId = set.SetId, BeatmapId = difficulty.BeatmapId,
                            BeatmapHash = difficulty.BeatmapHash, BeatmapPath = difficulty.BeatmapPath,
                            Origin = difficulty.Origin };
            if (!page.HasMore || page.Items.Count == 0) break;
            offset += page.Items.Count;
        } while (true);
        throw new ExternalLazerReplayOpenException("beatmap_missing", "This difficulty was not found in the connected osu! libraries.");
    }

    public static async Task<int> ResolveAsync(ILocalLibrarySource source, LocalReplay replay, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (replay.OnlineBeatmapId > 0) return replay.OnlineBeatmapId;
        int offset = 0;
        while (true)
        {
            var page = await source.SearchBeatmapSetsAsync(new LocalLibraryQuery(RulesetShortName: replay.RulesetShortName, Offset: offset, Limit: 200), token).ConfigureAwait(false);
            foreach (var map in page.Items)
                foreach (var difficulty in map.Difficulties)
                    if (difficulty.OnlineId > 0 &&
                        ((replay.BeatmapId != Guid.Empty && difficulty.BeatmapId == replay.BeatmapId)
                         || (!string.IsNullOrWhiteSpace(replay.BeatmapHash) && replay.BeatmapHash.Equals(difficulty.BeatmapHash, StringComparison.OrdinalIgnoreCase))))
                        return difficulty.OnlineId;
            if (!page.HasMore || page.Items.Count == 0) break;
            offset += page.Items.Count;
        }
        throw new InvalidOperationException("This difficulty has no known osu! ID. Unsubmitted maps cannot be opened with an osu! link.");
    }
}

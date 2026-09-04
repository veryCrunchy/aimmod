using AimMod.Desktop.LocalLibrary;

namespace AimMod.Desktop;

internal sealed record ReplayBrowserMapGroup(
    string Key,
    string Title,
    string Artist,
    string Difficulty,
    DateTimeOffset LastPlayed,
    IReadOnlyList<LocalReplay> Attempts);

internal sealed record ReplayBrowserSnapshot(
    IReadOnlyList<ReplayBrowserMapGroup> Maps,
    int TotalMapCount,
    int TotalReplayCount)
{
    public static ReplayBrowserSnapshot Empty { get; } = new([], 0, 0);
}

internal static class ReplayBrowserModel
{
    public const int DefaultMapLimit = 100;
    public const int PageSize = 200;

    public static async Task<ReplayBrowserSnapshot> LoadAsync(
        ILocalLibrarySource source,
        string search,
        int mapLimit = DefaultMapLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        int offset = 0;
        int totalReplays = 0;
        var replays = new List<LocalReplay>();
        var scoreIds = new HashSet<Guid>();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LocalLibraryPage<LocalReplay> page = await source.SearchReplaysAsync(new LocalLibraryQuery(
                SearchText: search,
                RulesetShortName: "osu",
                Sort: LocalLibrarySort.RecentlyPlayed,
                Offset: offset,
                Limit: PageSize), cancellationToken).ConfigureAwait(false);

            totalReplays = page.Total;
            foreach (LocalReplay replay in page.Items)
            {
                if (replay.ScoreId == Guid.Empty || scoreIds.Add(replay.ScoreId))
                    replays.Add(replay);
            }

            if (!page.HasMore || page.Items.Count == 0)
                break;

            int nextOffset = page.Offset + page.Items.Count;
            if (nextOffset <= offset)
                break;

            offset = nextOffset;
        }

        ReplayBrowserMapGroup[] allMaps = replays
            .GroupBy(MapKeyFor)
            .Select(group =>
            {
                LocalReplay[] attempts = group.OrderByDescending(replay => replay.PlayedAt).ToArray();
                LocalReplay representative = attempts[0];
                return new ReplayBrowserMapGroup(
                    group.Key,
                    representative.Title,
                    representative.Artist,
                    representative.Difficulty,
                    representative.PlayedAt,
                    attempts);
            })
            .OrderByDescending(group => group.LastPlayed)
            .ThenBy(group => group.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Difficulty, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ReplayBrowserSnapshot(
            allMaps.Take(Math.Max(0, mapLimit)).ToArray(),
            allMaps.Length,
            Math.Max(totalReplays, replays.Count));
    }

    public static string MapKeyFor(LocalReplay replay)
    {
        if (replay.BeatmapId != Guid.Empty)
            return $"id:{replay.BeatmapId:N}";

        if (!string.IsNullOrWhiteSpace(replay.BeatmapHash))
            return $"hash:{replay.BeatmapHash.Trim().ToLowerInvariant()}";

        return string.Join(':',
            "fallback",
            replay.SetId.ToString("N"),
            normalise(replay.RulesetShortName),
            normalise(replay.Title),
            normalise(replay.Artist),
            normalise(replay.Difficulty));
    }

    private static string normalise(string value) => value.Trim().ToLowerInvariant();
}

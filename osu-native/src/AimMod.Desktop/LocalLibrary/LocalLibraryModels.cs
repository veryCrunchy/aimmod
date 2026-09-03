namespace AimMod.Desktop.LocalLibrary;

public enum LocalLibrarySort
{
    RecentlyAdded,
    RecentlyPlayed,
    Title,
    StarRating,
    Score,
    Accuracy,
}

public sealed record LocalLibraryQuery(
    string SearchText = "",
    string RulesetShortName = "osu",
    double? MinimumStars = null,
    double? MaximumStars = null,
    LocalLibrarySort Sort = LocalLibrarySort.RecentlyAdded,
    int Offset = 0,
    int Limit = 60)
{
    public LocalLibraryQuery Normalised() => this with
    {
        SearchText = SearchText.Trim(),
        RulesetShortName = RulesetShortName.Trim(),
        MinimumStars = MinimumStars is >= 0 ? MinimumStars : null,
        MaximumStars = MaximumStars is >= 0 ? MaximumStars : null,
        Offset = Math.Max(0, Offset),
        Limit = Math.Clamp(Limit, 1, 200),
    };
}

public sealed record LocalLibraryPage<T>(IReadOnlyList<T> Items, int Total, int Offset, int Limit)
{
    public bool HasMore => Offset + Items.Count < Total;
}

public sealed record LocalBeatmapDifficulty(
    Guid BeatmapId,
    int OnlineId,
    string Name,
    string RulesetShortName,
    double StarRating,
    double Bpm,
    double LengthMilliseconds,
    float CircleSize,
    float ApproachRate,
    float OverallDifficulty,
    float DrainRate,
    int? LocalScoreCount);

public sealed record LocalBeatmapSet(
    Guid SetId,
    int OnlineId,
    string Title,
    string Artist,
    string Creator,
    string Source,
    DateTimeOffset DateAdded,
    DateTimeOffset? LastPlayed,
    IReadOnlyList<LocalBeatmapDifficulty> Difficulties,
    int? LocalReplayCount,
    string BackgroundPath = "");

public sealed record LocalReplay(
    Guid ScoreId,
    Guid SetId,
    Guid BeatmapId,
    string Title,
    string Artist,
    string Difficulty,
    string RulesetShortName,
    string Player,
    DateTimeOffset PlayedAt,
    double StarRating,
    double Accuracy,
    long TotalScore,
    int MaxCombo,
    int MissCount,
    double? PerformancePoints,
    IReadOnlyList<string> Mods,
    bool HasReplayFile,
    string BeatmapHash = "",
    string BackgroundPath = "");

public interface ILocalLibrarySource
{
    ValueTask<LocalLibraryPage<LocalBeatmapSet>> SearchBeatmapSetsAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default);

    ValueTask<LocalLibraryPage<LocalReplay>> SearchReplaysAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default);

    void Invalidate();
}

namespace AimMod.Osu.Runtime;

public enum OfficialBeatmapRequestStatus
{
    Success,
    SessionUnavailable,
    SignedOut,
    TokenExpired,
    Unauthorized,
    SessionChanged,
    NetworkError,
    InvalidResponse,
    ServerError,
}

public enum OfficialBeatmapCategory
{
    Any,
    Leaderboard,
    Ranked,
    Qualified,
    Loved,
    Pending,
    Wip,
    Graveyard,
}

public enum OfficialBeatmapSort
{
    Relevance,
    Updated,
    Ranked,
    Rating,
    Plays,
    Favourites,
    Artist,
    Title,
    Difficulty,
}

public sealed record OfficialBeatmapSearchQuery(
    string SearchText = "",
    double? MinimumStars = null,
    double? MaximumStars = null,
    OfficialBeatmapCategory Category = OfficialBeatmapCategory.Any,
    OfficialBeatmapSort Sort = OfficialBeatmapSort.Relevance,
    bool IncludeExplicitContent = false,
    int Limit = 24)
{
    public OfficialBeatmapSearchQuery Normalised()
    {
        string searchText = (SearchText ?? string.Empty).Trim();
        double? minimum = MinimumStars is >= 0 and <= 20 ? MinimumStars : null;
        double? maximum = MaximumStars is >= 0 and <= 20 ? MaximumStars : null;
        if (minimum is not null && maximum is not null && minimum > maximum)
            (minimum, maximum) = (maximum, minimum);

        return this with
        {
            SearchText = searchText[..Math.Min(searchText.Length, 256)],
            MinimumStars = minimum,
            MaximumStars = maximum,
            Limit = Math.Clamp(Limit, 1, 50),
        };
    }
}

public sealed record OfficialBeatmapDifficulty(
    int BeatmapId,
    string Name,
    string RulesetShortName,
    double StarRating,
    double Bpm,
    int TotalLengthSeconds,
    float CircleSize,
    float ApproachRate,
    float OverallDifficulty,
    float DrainRate,
    int PlayCount,
    int PassCount,
    int? MaximumCombo);

public sealed record OfficialBeatmapSet(
    int BeatmapSetId,
    string Title,
    string TitleUnicode,
    string Artist,
    string ArtistUnicode,
    string Creator,
    string Source,
    string Status,
    DateTimeOffset? RankedAt,
    DateTimeOffset? LastUpdatedAt,
    int PlayCount,
    int FavouriteCount,
    bool HasExplicitContent,
    bool DownloadDisabled,
    Uri? CoverUrl,
    Uri? CardUrl,
    Uri? ListUrl,
    Uri? PreviewAudioUrl,
    IReadOnlyList<OfficialBeatmapDifficulty> Difficulties);

public sealed record OfficialBeatmapSearchResult(
    OfficialBeatmapRequestStatus Status,
    IReadOnlyList<OfficialBeatmapSet> BeatmapSets,
    int ServerTotal = 0,
    bool IsTruncated = false)
{
    public static OfficialBeatmapSearchResult Empty(OfficialBeatmapRequestStatus status) => new(status, []);
}

public sealed record OfficialBeatmapDownloadResult(
    OfficialBeatmapRequestStatus Status,
    string? ArchivePath = null,
    long ArchiveBytes = 0);

public interface IOfficialBeatmapDiscoveryClient
{
    Task<OfficialBeatmapSearchResult> SearchAsync(
        OfficialBeatmapSearchQuery query,
        CancellationToken cancellationToken = default);

    Task<OfficialBeatmapDownloadResult> DownloadAsync(
        int beatmapSetId,
        string destinationDirectory,
        bool noVideo = false,
        CancellationToken cancellationToken = default);
}

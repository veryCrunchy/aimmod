namespace AimMod.Osu.Runtime;

public sealed record OsuProfile(
    int UserId,
    string Username,
    string? CountryCode,
    Uri? AvatarUrl,
    OsuProfileStatistics? Statistics);

public sealed record OsuProfileStatistics(
    int? GlobalRank,
    int? CountryRank,
    double? PerformancePoints,
    double? HitAccuracy,
    int PlayCount,
    int PlayTimeSeconds,
    long RankedScore,
    long TotalScore,
    long TotalHits,
    int MaximumCombo);

public enum OsuProfileFetchStatus
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

public sealed record OsuProfileFetchResult(OsuProfileFetchStatus Status, OsuProfile? Profile = null);

public sealed record OsuBestScore(
    long ScoreId,
    int UserId,
    string Username,
    double? PerformancePoints,
    double Accuracy,
    long TotalScore,
    int MaximumCombo,
    OsuScoreStatistics Statistics,
    IReadOnlyList<string> Mods,
    string ModsJson,
    DateTimeOffset? EndedAt,
    DateTimeOffset? CreatedAt,
    OsuScoreBeatmap Beatmap,
    OsuScoreBeatmapSet BeatmapSet);

public sealed record OsuScoreStatistics(
    int Misses,
    int Greats,
    int Oks,
    int Mehs);

public sealed record OsuScoreBeatmap(
    int BeatmapId,
    string? Checksum,
    string DifficultyName,
    double StarRating,
    int? MaximumCombo,
    double Bpm,
    int TotalLengthSeconds);

public sealed record OsuScoreBeatmapSet(
    int BeatmapSetId,
    string Title,
    string? TitleUnicode,
    string Artist,
    string? ArtistUnicode,
    string Creator,
    string? Source,
    string? Status,
    Uri? CoverUrl);

public enum OsuBestScoresFetchStatus
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

public sealed record OsuBestScoresFetchResult(
    OsuBestScoresFetchStatus Status,
    IReadOnlyList<OsuBestScore>? Scores = null,
    bool IsFromCache = false,
    DateTimeOffset? FetchedAt = null);

internal sealed record OsuBestScoresCacheDocument(
    int SchemaVersion,
    string ApiVersion,
    int UserId,
    DateTimeOffset FetchedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<OsuBestScore> Scores);

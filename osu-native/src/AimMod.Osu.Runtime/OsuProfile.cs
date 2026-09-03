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

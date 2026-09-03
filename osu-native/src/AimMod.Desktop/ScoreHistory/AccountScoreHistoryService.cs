using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime;

namespace AimMod.Desktop.ScoreHistory;

[Flags]
public enum ScoreHistoryProvenance
{
    None = 0,
    Local = 1,
    OnlineBest = 2,
    OnlineRecent = 4,
    OnlineBeatmap = 8,
}

public sealed record ScoreHistoryEntry(
    string Identity,
    long OnlineScoreId,
    int OnlineBeatmapId,
    int OnlineBeatmapSetId,
    Guid? LocalScoreId,
    Guid? LocalBeatmapId,
    string Title,
    string Artist,
    string Difficulty,
    DateTimeOffset PlayedAt,
    double StarRating,
    double Accuracy,
    double? PerformancePoints,
    long TotalScore,
    int MaximumCombo,
    int MissCount,
    IReadOnlyList<string> Mods,
    ScoreHistoryProvenance Provenance,
    bool HasReplay)
{
    public bool IsLocal => Provenance.HasFlag(ScoreHistoryProvenance.Local);
    public bool IsSubmitted => (Provenance & ~ScoreHistoryProvenance.Local) != 0 || OnlineScoreId > 0;
}

public sealed record OnlineScoreCoverage(
    OsuBestScoresFetchStatus Status,
    bool IsFromCache,
    DateTimeOffset? FetchedAt,
    string Scope,
    int? ApiWindowLimit,
    bool IsExhaustive)
{
    public bool IsSuccess => Status == OsuBestScoresFetchStatus.Success;
}

public sealed record OnlineAccountScoreHistoryResult(
    OsuProfile? Profile,
    IReadOnlyList<ScoreHistoryEntry> Scores,
    OnlineScoreCoverage BestCoverage,
    OnlineScoreCoverage RecentCoverage);

public sealed record OnlineBeatmapScoreHistoryResult(
    int BeatmapId,
    IReadOnlyList<ScoreHistoryEntry> Scores,
    OnlineScoreCoverage Coverage)
{
    public bool IsSuccess => Coverage.IsSuccess;
}

public interface IAccountScoreHistoryService
{
    Task<OnlineAccountScoreHistoryResult> FetchAccountAsync(CancellationToken cancellationToken = default);

    Task<OnlineBeatmapScoreHistoryResult> FetchBeatmapAsync(int beatmapId, CancellationToken cancellationToken = default);
}

public sealed class OfficialAccountScoreHistoryService : IAccountScoreHistoryService
{
    private static readonly TimeSpan profile_lifetime = TimeSpan.FromMinutes(5);
    private readonly Func<OfficialOsuApiClient?> api;
    private readonly SemaphoreSlim profileLock = new(1, 1);
    private OsuProfile? cachedProfile;
    private DateTimeOffset profileFetchedAt;

    public OfficialAccountScoreHistoryService(Func<OfficialOsuApiClient?> api)
    {
        this.api = api ?? throw new ArgumentNullException(nameof(api));
    }

    public async Task<OnlineAccountScoreHistoryResult> FetchAccountAsync(CancellationToken cancellationToken = default)
    {
        (OfficialOsuApiClient? client, OsuProfile? profile, OsuBestScoresFetchStatus status) = await getProfileAsync(cancellationToken).ConfigureAwait(false);
        if (client is null || profile is null)
        {
            OnlineScoreCoverage unavailable = coverage(status, false, null, "account feed", 100, false);
            return new OnlineAccountScoreHistoryResult(null, [], unavailable with { Scope = "best scores" }, unavailable with { Scope = "recent scores" });
        }

        Task<OsuBestScoresFetchResult> bestTask = client.FetchBestScoresAsync(profile, cancellationToken);
        Task<OsuBestScoresFetchResult> recentTask = client.FetchRecentScoresAsync(profile, cancellationToken);
        await Task.WhenAll(bestTask, recentTask).ConfigureAwait(false);
        OsuBestScoresFetchResult best = await bestTask.ConfigureAwait(false);
        OsuBestScoresFetchResult recent = await recentTask.ConfigureAwait(false);

        IReadOnlyList<ScoreHistoryEntry> scores = ScoreHistoryMerger.MergeOnline(
            best.Status == OsuBestScoresFetchStatus.Success ? best.Scores ?? [] : [],
            recent.Status == OsuBestScoresFetchStatus.Success ? recent.Scores ?? [] : []);
        return new OnlineAccountScoreHistoryResult(
            profile,
            scores,
            coverage(best.Status, best.IsFromCache, best.FetchedAt, "best scores", 100, false),
            coverage(recent.Status, recent.IsFromCache, recent.FetchedAt, "recent scores", 100, false));
    }

    public async Task<OnlineBeatmapScoreHistoryResult> FetchBeatmapAsync(int beatmapId, CancellationToken cancellationToken = default)
    {
        if (beatmapId <= 0)
            return new OnlineBeatmapScoreHistoryResult(beatmapId, [], coverage(OsuBestScoresFetchStatus.InvalidResponse, false, null, "exact beatmap", null, false));
        (OfficialOsuApiClient? client, OsuProfile? profile, OsuBestScoresFetchStatus status) = await getProfileAsync(cancellationToken).ConfigureAwait(false);
        if (client is null || profile is null)
            return new OnlineBeatmapScoreHistoryResult(beatmapId, [], coverage(status, false, null, "exact beatmap", null, false));

        OsuUserBeatmapScoresFetchResult result = await client.FetchUserBeatmapScoresAsync(profile, beatmapId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ScoreHistoryEntry> scores = result.Status == OsuBestScoresFetchStatus.Success
            ? (result.Scores ?? []).Select(score => fromExact(score, beatmapId)).ToArray()
            : [];
        return new OnlineBeatmapScoreHistoryResult(
            beatmapId,
            scores,
            coverage(result.Status, result.IsFromCache, result.FetchedAt, "exact beatmap submissions", null, true));
    }

    private async Task<(OfficialOsuApiClient?, OsuProfile?, OsuBestScoresFetchStatus)> getProfileAsync(CancellationToken cancellationToken)
    {
        OfficialOsuApiClient? client = api();
        if (client is null)
            return (null, null, OsuBestScoresFetchStatus.SessionUnavailable);
        if (cachedProfile is not null && DateTimeOffset.UtcNow - profileFetchedAt < profile_lifetime)
            return (client, cachedProfile, OsuBestScoresFetchStatus.Success);

        await profileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cachedProfile is not null && DateTimeOffset.UtcNow - profileFetchedAt < profile_lifetime)
                return (client, cachedProfile, OsuBestScoresFetchStatus.Success);
            OsuProfileFetchResult result = await client.FetchCurrentProfileAsync(cancellationToken).ConfigureAwait(false);
            if (result.Status != OsuProfileFetchStatus.Success || result.Profile is null)
                return (client, null, mapProfileStatus(result.Status));
            cachedProfile = result.Profile;
            profileFetchedAt = DateTimeOffset.UtcNow;
            return (client, cachedProfile, OsuBestScoresFetchStatus.Success);
        }
        finally
        {
            profileLock.Release();
        }
    }

    private static ScoreHistoryEntry fromExact(OsuUserBeatmapScore score, int beatmapId) => new(
        $"osu:{score.ScoreId}", score.ScoreId, beatmapId, 0, null, null, string.Empty, string.Empty, string.Empty,
        score.EndedAt ?? score.CreatedAt ?? DateTimeOffset.UnixEpoch, double.NaN, score.Accuracy, score.PerformancePoints,
        score.TotalScore, score.MaximumCombo, score.Statistics.Misses, score.Mods, ScoreHistoryProvenance.OnlineBeatmap, false);

    private static OnlineScoreCoverage coverage(OsuBestScoresFetchStatus status, bool cached, DateTimeOffset? fetchedAt, string scope, int? limit, bool exhaustive) =>
        new(status, cached, fetchedAt, scope, limit, exhaustive);

    private static OsuBestScoresFetchStatus mapProfileStatus(OsuProfileFetchStatus status) => status switch
    {
        OsuProfileFetchStatus.SignedOut => OsuBestScoresFetchStatus.SignedOut,
        OsuProfileFetchStatus.TokenExpired => OsuBestScoresFetchStatus.TokenExpired,
        OsuProfileFetchStatus.Unauthorized => OsuBestScoresFetchStatus.Unauthorized,
        OsuProfileFetchStatus.SessionChanged => OsuBestScoresFetchStatus.SessionChanged,
        OsuProfileFetchStatus.NetworkError => OsuBestScoresFetchStatus.NetworkError,
        OsuProfileFetchStatus.InvalidResponse => OsuBestScoresFetchStatus.InvalidResponse,
        OsuProfileFetchStatus.ServerError => OsuBestScoresFetchStatus.ServerError,
        _ => OsuBestScoresFetchStatus.SessionUnavailable,
    };
}

public static class ScoreHistoryMerger
{
    public static IReadOnlyList<ScoreHistoryEntry> MergeOnline(
        IReadOnlyList<OsuBestScore> bestScores,
        IReadOnlyList<OsuBestScore> recentScores)
    {
        ArgumentNullException.ThrowIfNull(bestScores);
        ArgumentNullException.ThrowIfNull(recentScores);
        var scores = new Dictionary<long, ScoreHistoryEntry>();
        foreach (OsuBestScore score in bestScores.Where(validOnlineScore).DistinctBy(score => score.ScoreId))
            scores[score.ScoreId] = fromAccount(score, ScoreHistoryProvenance.OnlineBest);
        foreach (OsuBestScore score in recentScores.Where(validOnlineScore).DistinctBy(score => score.ScoreId))
        {
            if (scores.TryGetValue(score.ScoreId, out ScoreHistoryEntry? existing))
                scores[score.ScoreId] = existing with { Provenance = existing.Provenance | ScoreHistoryProvenance.OnlineRecent };
            else
                scores[score.ScoreId] = fromAccount(score, ScoreHistoryProvenance.OnlineRecent);
        }
        return scores.Values.OrderByDescending(score => score.PlayedAt).ThenBy(score => score.Identity, StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<ScoreHistoryEntry> Merge(
        IReadOnlyList<LocalReplay> localScores,
        IReadOnlyList<ScoreHistoryEntry> onlineScores)
    {
        ArgumentNullException.ThrowIfNull(localScores);
        ArgumentNullException.ThrowIfNull(onlineScores);
        var onlineById = onlineScores.Where(score => score.OnlineScoreId > 0)
                                    .GroupBy(score => score.OnlineScoreId)
                                    .ToDictionary(group => group.Key, group => group.First());
        var merged = new List<ScoreHistoryEntry>(localScores.Count + onlineScores.Count);
        foreach (LocalReplay local in localScores.GroupBy(score => score.ScoreId).Select(group => group.First()))
        {
            ScoreHistoryEntry? online = local.OnlineScoreId > 0 ? onlineById.GetValueOrDefault(local.OnlineScoreId) : null;
            if (online is not null)
                onlineById.Remove(online.OnlineScoreId);
            merged.Add(new ScoreHistoryEntry(
                $"local:{local.ScoreId:N}", local.OnlineScoreId, online?.OnlineBeatmapId ?? 0, online?.OnlineBeatmapSetId ?? 0,
                local.ScoreId, local.BeatmapId, local.Title, local.Artist, local.Difficulty,
                online?.PlayedAt ?? local.PlayedAt,
                online is { StarRating: var onlineStars } && double.IsFinite(onlineStars) ? onlineStars : local.StarRating,
                online?.Accuracy ?? local.Accuracy,
                online?.PerformancePoints ?? local.PerformancePoints, online?.TotalScore ?? local.TotalScore,
                online?.MaximumCombo ?? local.MaxCombo, online?.MissCount ?? local.MissCount, online?.Mods ?? local.Mods,
                ScoreHistoryProvenance.Local | (online?.Provenance ?? ScoreHistoryProvenance.None), local.HasReplayFile));
        }
        merged.AddRange(onlineById.Values);
        return merged.OrderBy(score => score.PlayedAt).ThenBy(score => score.Identity, StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<LocalReplay> MergeAsLocalReplays(
        IReadOnlyList<LocalReplay> localScores,
        IReadOnlyList<ScoreHistoryEntry> onlineScores)
    {
        IReadOnlyList<ScoreHistoryEntry> merged = Merge(localScores, onlineScores);
        Dictionary<Guid, LocalReplay> localById = localScores.GroupBy(score => score.ScoreId)
                                                               .ToDictionary(group => group.Key, group => group.First());

        return merged.Select(entry =>
        {
            if (entry.LocalScoreId is { } localId && localById.TryGetValue(localId, out LocalReplay? local))
            {
                return local with
                {
                    PlayedAt = entry.PlayedAt,
                    StarRating = double.IsFinite(entry.StarRating) ? entry.StarRating : local.StarRating,
                    Accuracy = entry.Accuracy,
                    PerformancePoints = entry.PerformancePoints,
                    TotalScore = entry.TotalScore,
                    MaxCombo = entry.MaximumCombo,
                    MissCount = entry.MissCount,
                    Mods = entry.Mods,
                    OnlineScoreId = entry.OnlineScoreId,
                };
            }

            return new LocalReplay(
                stableGuid(entry.OnlineScoreId, 1),
                stableGuid(entry.OnlineBeatmapSetId, 2),
                stableGuid(entry.OnlineBeatmapId, 3),
                entry.Title,
                entry.Artist,
                entry.Difficulty,
                "osu",
                string.Empty,
                entry.PlayedAt,
                entry.StarRating,
                entry.Accuracy,
                entry.TotalScore,
                entry.MaximumCombo,
                entry.MissCount,
                entry.PerformancePoints,
                entry.Mods,
                false,
                OnlineScoreId: entry.OnlineScoreId);
        }).ToArray();
    }

    private static Guid stableGuid(long value, byte discriminator)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, value);
        bytes[15] = discriminator;
        return new Guid(bytes);
    }

    private static bool validOnlineScore(OsuBestScore score) => score is not null && score.ScoreId > 0 &&
        score.Beatmap.BeatmapId > 0 && score.BeatmapSet.BeatmapSetId > 0 &&
        double.IsFinite(score.Accuracy) && score.Accuracy is >= 0 and <= 1;

    private static ScoreHistoryEntry fromAccount(OsuBestScore score, ScoreHistoryProvenance provenance) => new(
        $"osu:{score.ScoreId}", score.ScoreId, score.Beatmap.BeatmapId, score.BeatmapSet.BeatmapSetId, null, null,
        score.BeatmapSet.Title, score.BeatmapSet.Artist, score.Beatmap.DifficultyName,
        score.EndedAt ?? score.CreatedAt ?? DateTimeOffset.UnixEpoch, score.Beatmap.StarRating, score.Accuracy, score.PerformancePoints,
        score.TotalScore, score.MaximumCombo, score.Statistics.Misses, score.Mods, provenance, false);
}

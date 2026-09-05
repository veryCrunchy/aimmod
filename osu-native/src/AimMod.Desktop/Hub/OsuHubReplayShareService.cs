using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop.Hub;

public sealed record HubReplayShareSelection(
    LocalReplay Replay,
    OsuHubVisibility Visibility,
    bool UploadReplayFile,
    bool UploadAnalysis);

public sealed class OsuHubReplayShareService
{
    private readonly ILocalLibrarySource localLibrary;
    private readonly Func<OsuProfile?> profileProvider;
    private readonly IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses;
    private readonly IOsuHubUploadQueue queue;

    public OsuHubReplayShareService(
        ILocalLibrarySource localLibrary,
        Func<OsuProfile?> profileProvider,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses,
        IOsuHubUploadQueue queue)
    {
        this.localLibrary = localLibrary ?? throw new ArgumentNullException(nameof(localLibrary));
        this.profileProvider = profileProvider ?? throw new ArgumentNullException(nameof(profileProvider));
        this.analyses = analyses ?? throw new ArgumentNullException(nameof(analyses));
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    public async Task<HubUploadQueueItem> QueueAsync(HubReplayShareSelection selection, CancellationToken cancellationToken = default)
    {
        OsuHubSyncRequest request = await prepareAsync(selection, cancellationToken).ConfigureAwait(false);
        return await queue.EnqueueAsync(request, selection.UploadReplayFile ? selection.Replay.ReplayPath : null,
            title(selection.Replay), cancellationToken).ConfigureAwait(false);
    }

    public void SetAutomaticUploadPermission(Func<HubUploadQueueItem, bool> permission) => queue.SetAutomaticUploadPermission(permission);

    public async Task<HubUploadQueueItem?> QueueAutomaticAsync(LocalReplay replay, HubSharingPreferences preferences,
        long expectedOsuUserId, string accountScope, string deduplicationKey, Func<bool> stillAllowed,
        CancellationToken cancellationToken = default)
    {
        if (profileProvider()?.UserId != expectedOsuUserId || !stillAllowed())
            return null;
        var selection = new HubReplayShareSelection(replay, preferences.Visibility,
            preferences.UploadReplayFile && replay.HasReplayFile && File.Exists(replay.ReplayPath),
            preferences.UploadAnalysis && analyses.ContainsKey(replay.ScoreId));
        OsuHubSyncRequest request;
        try { request = await prepareAsync(selection, cancellationToken).ConfigureAwait(false); }
        catch (FileNotFoundException) when (selection.UploadReplayFile)
        {
            selection = selection with { UploadReplayFile = false };
            request = await prepareAsync(selection, cancellationToken).ConfigureAwait(false);
        }
        if (profileProvider()?.UserId != expectedOsuUserId || request.Profile.OsuUserId != expectedOsuUserId || !stillAllowed())
            return null;
        return await queue.TryEnqueueAutomaticAsync(request, selection.UploadReplayFile ? replay.ReplayPath : null,
            title(replay), deduplicationKey, accountScope, preferences.AutomaticSharingGeneration, cancellationToken).ConfigureAwait(false);
    }

    private static string title(LocalReplay replay) => $"{replay.Artist} - {replay.Title} [{replay.Difficulty}]";

    private async Task<OsuHubSyncRequest> prepareAsync(HubReplayShareSelection selection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        OsuProfile profile = profileProvider()
                             ?? throw new InvalidOperationException("Sign in to osu!lazer so AimMod can verify which osu! profile owns this score.");
        (LocalBeatmapSet set, LocalBeatmapDifficulty difficulty) = await resolveBeatmapAsync(selection.Replay, cancellationToken).ConfigureAwait(false);
        analyses.TryGetValue(selection.Replay.ScoreId, out ReplayAnalysisResult? analysis);
        if (selection.UploadAnalysis && analysis is null)
            throw new InvalidOperationException("Exact replay analysis must finish before its coaching data can be shared.");

        var input = new OsuHubSyncInput(
            selection.Replay,
            set,
            difficulty,
            new OsuHubProfile(
                profile.UserId,
                profile.Username,
                profile.CountryCode ?? "",
                profile.AvatarUrl?.AbsoluteUri ?? "",
                profile.Statistics?.GlobalRank,
                profile.Statistics?.PerformancePoints,
                profile.Statistics?.PlayCount ?? 0,
                profile.Statistics?.PlayTimeSeconds ?? 0),
            analysis,
            selection.Visibility,
            selection.UploadReplayFile,
            selection.UploadAnalysis);
        return await OsuHubContractFactory.CreateAsync(input, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(LocalBeatmapSet Set, LocalBeatmapDifficulty Difficulty)> resolveBeatmapAsync(
        LocalReplay replay,
        CancellationToken cancellationToken)
    {
        LocalLibraryPage<LocalBeatmapSet> page = await localLibrary.SearchBeatmapSetsAsync(new LocalLibraryQuery(
            SearchText: replay.Title,
            RulesetShortName: replay.RulesetShortName,
            Limit: 200), cancellationToken).ConfigureAwait(false);
        LocalBeatmapSet? set = page.Items.FirstOrDefault(candidate => candidate.SetId == replay.SetId)
                               ?? page.Items.FirstOrDefault(candidate => candidate.Difficulties.Any(difficulty => difficulty.BeatmapId == replay.BeatmapId));
        LocalBeatmapDifficulty? difficulty = set?.Difficulties.FirstOrDefault(candidate => candidate.BeatmapId == replay.BeatmapId);
        if (set is not null && difficulty is not null)
            return (set, difficulty);

        var fallbackDifficulty = new LocalBeatmapDifficulty(
            replay.BeatmapId,
            0,
            replay.Difficulty,
            replay.RulesetShortName,
            replay.StarRating,
            0,
            0,
            0,
            0,
            0,
            0,
            null,
            replay.BeatmapHash);
        var fallbackSet = new LocalBeatmapSet(
            replay.SetId,
            0,
            replay.Title,
            replay.Artist,
            "",
            "",
            replay.PlayedAt,
            replay.PlayedAt,
            [fallbackDifficulty],
            1,
            replay.BackgroundPath);
        return (fallbackSet, fallbackDifficulty);
    }
}

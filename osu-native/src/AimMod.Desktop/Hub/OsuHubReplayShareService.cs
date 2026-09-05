using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using System.Security.Cryptography;

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
    private readonly Func<ILocalReplayOpenService?> replayOpenServiceProvider;
    private readonly string? uploadSpoolPath;

    public OsuHubReplayShareService(
        ILocalLibrarySource localLibrary,
        Func<OsuProfile?> profileProvider,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses,
        IOsuHubUploadQueue queue,
        Func<ILocalReplayOpenService?>? replayOpenServiceProvider = null,
        string? uploadSpoolPath = null)
    {
        this.localLibrary = localLibrary ?? throw new ArgumentNullException(nameof(localLibrary));
        this.profileProvider = profileProvider ?? throw new ArgumentNullException(nameof(profileProvider));
        this.analyses = analyses ?? throw new ArgumentNullException(nameof(analyses));
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.replayOpenServiceProvider = replayOpenServiceProvider ?? (() => null);
        if (uploadSpoolPath is not null && !Path.IsPathFullyQualified(uploadSpoolPath))
            throw new ArgumentException("The Hub upload spool path must be absolute.", nameof(uploadSpoolPath));
        this.uploadSpoolPath = uploadSpoolPath;
    }

    public async Task<HubUploadQueueItem> QueueAsync(HubReplayShareSelection selection, CancellationToken cancellationToken = default)
    {
        PreparedShare prepared = await prepareAsync(selection, cancellationToken).ConfigureAwait(false);
        return await queue.EnqueueAsync(prepared.Request, prepared.ReplayPath,
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
            preferences.UploadReplayFile && replay.HasReplayFile,
            preferences.UploadAnalysis && analyses.ContainsKey(replay.ScoreId));
        PreparedShare prepared = await prepareAsync(selection, cancellationToken).ConfigureAwait(false);
        if (profileProvider()?.UserId != expectedOsuUserId || prepared.Request.Profile.OsuUserId != expectedOsuUserId || !stillAllowed())
            return null;
        return await queue.TryEnqueueAutomaticAsync(prepared.Request, prepared.ReplayPath,
            title(replay), deduplicationKey, accountScope, preferences.AutomaticSharingGeneration, cancellationToken).ConfigureAwait(false);
    }

    private static string title(LocalReplay replay) => $"{replay.Artist} - {replay.Title} [{replay.Difficulty}]";

    private async Task<PreparedShare> prepareAsync(HubReplayShareSelection selection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        OsuProfile profile = profileProvider()
                             ?? throw new InvalidOperationException("Sign in to osu!lazer so AimMod can verify which osu! profile owns this score.");
        (LocalBeatmapSet set, LocalBeatmapDifficulty difficulty) = await resolveBeatmapAsync(selection.Replay, cancellationToken).ConfigureAwait(false);
        analyses.TryGetValue(selection.Replay.ScoreId, out ReplayAnalysisResult? analysis);
        if (selection.UploadAnalysis && analysis is null)
            throw new InvalidOperationException("Exact replay analysis must finish before its coaching data can be shared.");

        LocalReplay resolved = selection.Replay;
        if (selection.UploadReplayFile)
            resolved = resolved with { ReplayPath = await stageReplayAsync(resolved, cancellationToken).ConfigureAwait(false) };

        var input = new OsuHubSyncInput(
            resolved,
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
        OsuHubSyncRequest request = await OsuHubContractFactory.CreateAsync(input, cancellationToken).ConfigureAwait(false);
        return new PreparedShare(request, selection.UploadReplayFile ? resolved.ReplayPath : null);
    }

    private sealed record PreparedShare(OsuHubSyncRequest Request, string? ReplayPath);

    private async Task<string> stageReplayAsync(LocalReplay replay, CancellationToken cancellationToken)
    {
        if (!replay.HasReplayFile)
            throw new FileNotFoundException("The selected score does not contain a replay file.");
        if (uploadSpoolPath is null)
            throw new InvalidOperationException("The Hub replay upload storage is not configured.");
        if (Path.IsPathFullyQualified(replay.ReplayPath) && File.Exists(replay.ReplayPath))
            return await copyToSpoolAsync(replay.ReplayPath, cancellationToken).ConfigureAwait(false);
        ILocalReplayOpenService resolver = replayOpenServiceProvider()
            ?? throw new InvalidOperationException("The replay library is not connected. Reconnect osu! before sharing the replay file.");
        await using IReplayFileLease lease = await resolver.OpenReplayFileAsync(replay, cancellationToken).ConfigureAwait(false);
        // The resolver owns temporary staging. Queue entries must never reference its files.
        return await copyToSpoolAsync(lease.ReplayPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> copyToSpoolAsync(string sourcePath, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("The resolved replay file is no longer available.");
        Directory.CreateDirectory(uploadSpoolPath!);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(uploadSpoolPath!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string temporary = Path.Combine(uploadSpoolPath!, $"{Guid.NewGuid():N}.pending");
        try
        {
            string hash;
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                if (source.Length is <= 0 or > 64L * 1024 * 1024)
                    throw new InvalidDataException("The replay file is empty or exceeds the Hub 64 MiB upload limit.");
                long expectedLength = source.Length;
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                if (destination.Length != expectedLength)
                    throw new IOException("The replay changed while preparing its upload.");
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
                destination.Position = 0;
                hash = Convert.ToHexString(await SHA256.HashDataAsync(destination, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            string target = Path.Combine(uploadSpoolPath!, hash + ".osr");
            try { File.Move(temporary, target); }
            catch (IOException) when (File.Exists(target))
            {
                await using var existing = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
                string existingHash = Convert.ToHexString(await SHA256.HashDataAsync(existing, cancellationToken).ConfigureAwait(false));
                if (!string.Equals(hash, existingHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The stored Hub replay failed its integrity check.");
            }
            return target;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
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

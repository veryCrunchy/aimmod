using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop;

public sealed record ReplayAnalysisBatchProgress(int Completed, int Total, string CurrentTitle);

public sealed record ReplayAnalysisBatchResult(
    IReadOnlyDictionary<Guid, ReplayAnalysisResult> Completed,
    IReadOnlyList<Guid> Failed);

internal sealed record ReplayAnalysisCumulativeAccounting(
    int Total,
    int Cached,
    int PreviouslyFailed,
    int Completed,
    int Failed)
{
    public int Processed => Math.Min(Total, Cached + PreviouslyFailed + Completed + Failed);

    public int Remaining => Math.Max(0, Total - Processed);

    public static ReplayAnalysisCumulativeAccounting Create(
        IEnumerable<LocalReplay> replays,
        IEnumerable<Guid> cachedScoreIds,
        IEnumerable<Guid> failedScoreIds)
    {
        ArgumentNullException.ThrowIfNull(replays);
        ArgumentNullException.ThrowIfNull(cachedScoreIds);
        ArgumentNullException.ThrowIfNull(failedScoreIds);

        HashSet<Guid> available = replays.Where(replay => replay.HasReplayFile)
                                         .Select(replay => replay.ScoreId)
                                         .ToHashSet();
        HashSet<Guid> cached = cachedScoreIds.Where(available.Contains).ToHashSet();
        int previouslyFailed = failedScoreIds.Where(scoreId => available.Contains(scoreId) && !cached.Contains(scoreId))
                                             .Distinct()
                                             .Count();
        return new ReplayAnalysisCumulativeAccounting(available.Count, cached.Count, previouslyFailed, 0, 0);
    }

    public ReplayAnalysisCumulativeAccounting Add(ReplayAnalysisBatchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return this with
        {
            Completed = Completed + result.Completed.Count,
            Failed = Failed + result.Failed.Count,
        };
    }

    public ReplayAnalysisBatchProgress MapBatchProgress(ReplayAnalysisBatchProgress batchProgress)
    {
        ArgumentNullException.ThrowIfNull(batchProgress);
        int cumulativeCompleted = Math.Clamp(Processed + batchProgress.Completed, 0, Total);
        int remaining = Math.Max(0, Total - cumulativeCompleted);
        string counts = $"{Cached:N0} cached | {remaining:N0} remaining";
        string title = string.IsNullOrWhiteSpace(batchProgress.CurrentTitle)
            ? counts
            : $"{batchProgress.CurrentTitle} | {counts}";
        return new ReplayAnalysisBatchProgress(cumulativeCompleted, Total, title);
    }
}

/// <summary>
/// Analyses a small replay batch sequentially in one muted worker. This is used to
/// enrich coaching without constructing replay players or starting audio tracks.
/// </summary>
public sealed class ReplayAnalysisBatchService
{
    public const int MaximumBatchSize = 5;

    private readonly ILocalReplayOpenService replayOpenService;
    private readonly Action<string> log;

    public ReplayAnalysisBatchService(
        ILocalReplayOpenService replayOpenService,
        Action<string>? log = null)
    {
        this.replayOpenService = replayOpenService ?? throw new ArgumentNullException(nameof(replayOpenService));
        this.log = log ?? Console.Error.WriteLine;
    }

    public async Task<ReplayAnalysisBatchResult> AnalyseRecentAsync(
        IEnumerable<LocalReplay> replays,
        IEnumerable<Guid> completedScoreIds,
        int limit,
        IProgress<ReplayAnalysisBatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => await analyseAsync(
            SelectPending(replays, completedScoreIds, limit),
            progress,
            cancellationToken).ConfigureAwait(false);

    public async Task<ReplayAnalysisBatchResult> AnalyseBreadthFirstAsync(
        IEnumerable<LocalReplay> replays,
        IEnumerable<Guid> completedScoreIds,
        int limit,
        IProgress<ReplayAnalysisBatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => await analyseAsync(
            SelectPendingBreadthFirst(replays, completedScoreIds, limit),
            progress,
            cancellationToken).ConfigureAwait(false);

    private async Task<ReplayAnalysisBatchResult> analyseAsync(
        LocalReplay[] pending,
        IProgress<ReplayAnalysisBatchProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (pending.Length == 0)
            return new ReplayAnalysisBatchResult(new Dictionary<Guid, ReplayAnalysisResult>(), Array.Empty<Guid>());

        var completed = new Dictionary<Guid, ReplayAnalysisResult>();
        var failed = new List<Guid>();
        await using SidecarRuntimeClient runtime = SidecarRuntimeClient.Start();
        var analysisClient = new ReplayAnalysisClient(new SidecarRuntimeRequestClient(runtime));

        for (int index = 0; index < pending.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LocalReplay replay = pending[index];
            progress?.Report(new ReplayAnalysisBatchProgress(index, pending.Length, replay.Title));

            try
            {
                await using IPlayableReplayBundle bundle = await replayOpenService.OpenAsync(replay, cancellationToken).ConfigureAwait(false);
                await using ReplayAnalysisStaging staging = await ReplayAnalysisStaging.CreateAsync(
                    bundle.BeatmapPath,
                    bundle.ReplayPath,
                    cancellationToken).ConfigureAwait(false);
                ReplayAnalysisResult result = await analysisClient.AnalyseAsync(
                    new ReplayAnalysisRequest(staging.DirectoryPath, staging.BeatmapPath, staging.ReplayPath),
                    cancellationToken).ConfigureAwait(false);
                completed[replay.ScoreId] = result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (error is ExternalLazerReplayOpenException or ReplayAnalysisClientException or IOException or UnauthorizedAccessException)
            {
                failed.Add(replay.ScoreId);
                log(DescribeFailure(replay, error));
            }
        }

        progress?.Report(new ReplayAnalysisBatchProgress(pending.Length, pending.Length, string.Empty));
        return new ReplayAnalysisBatchResult(completed, failed);
    }

    internal static LocalReplay[] OrderBreadthFirst(IEnumerable<LocalReplay> replays)
    {
        ArgumentNullException.ThrowIfNull(replays);

        LocalReplay[][] groups = replays.Where(replay => replay.HasReplayFile)
                                        .GroupBy(exactMapKey)
                                        .Select(group => group.OrderByDescending(replay => replay.PlayedAt).ToArray())
                                        .OrderByDescending(group => group[0].PlayedAt)
                                        .ToArray();
        if (groups.Length == 0)
            return Array.Empty<LocalReplay>();

        int maximumAttempts = groups.Max(group => group.Length);
        var ordered = new List<LocalReplay>(groups.Sum(group => group.Length));
        for (int attempt = 0; attempt < maximumAttempts; attempt++)
        {
            foreach (LocalReplay[] group in groups)
            {
                if (attempt < group.Length)
                    ordered.Add(group[attempt]);
            }
        }

        return ordered.ToArray();
    }

    internal static LocalReplay[] SelectPendingBreadthFirst(
        IEnumerable<LocalReplay> replays,
        IEnumerable<Guid> completedScoreIds,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(replays);
        ArgumentNullException.ThrowIfNull(completedScoreIds);

        int boundedLimit = Math.Clamp(limit, 0, MaximumBatchSize);
        if (boundedLimit == 0)
            return Array.Empty<LocalReplay>();

        var completed = completedScoreIds.ToHashSet();
        return OrderBreadthFirst(replays).Where(replay => !completed.Contains(replay.ScoreId))
                                         .Take(boundedLimit)
                                         .ToArray();
    }

    internal static LocalReplay[] SelectPending(
        IEnumerable<LocalReplay> replays,
        IEnumerable<Guid> completedScoreIds,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(replays);
        ArgumentNullException.ThrowIfNull(completedScoreIds);

        int boundedLimit = Math.Clamp(limit, 0, MaximumBatchSize);
        if (boundedLimit == 0)
            return Array.Empty<LocalReplay>();

        var completed = completedScoreIds.ToHashSet();
        return replays.Where(replay => replay.HasReplayFile && !completed.Contains(replay.ScoreId))
                      .OrderByDescending(replay => replay.PlayedAt)
                      .Take(boundedLimit)
                      .ToArray();
    }

    internal static string DescribeFailure(LocalReplay replay, Exception error)
    {
        ArgumentNullException.ThrowIfNull(replay);
        ArgumentNullException.ThrowIfNull(error);

        string title = new(replay.Title
                                 .Where(character => !char.IsControl(character))
                                 .Take(80)
                                 .ToArray());
        string code = error switch
        {
            ExternalLazerReplayOpenException replayError => replayError.Code,
            ReplayAnalysisClientException analysisError => analysisError.Code,
            UnauthorizedAccessException => "access_denied",
            IOException => "io_error",
            _ => "unexpected_error",
        };
        return $"[AimMod] Background replay analysis skipped {replay.ScoreId:D} [{code}] {title}: {error.GetType().Name}";
    }

    private static string exactMapKey(LocalReplay replay)
    {
        if (replay.BeatmapId != Guid.Empty)
            return $"id:{replay.BeatmapId:D}";
        if (!string.IsNullOrWhiteSpace(replay.BeatmapHash))
            return $"hash:{replay.BeatmapHash.Trim().ToUpperInvariant()}";

        return $"fallback:{replay.SetId:D}:{replay.RulesetShortName.Trim().ToUpperInvariant()}:{replay.Difficulty.Trim().ToUpperInvariant()}";
    }
}

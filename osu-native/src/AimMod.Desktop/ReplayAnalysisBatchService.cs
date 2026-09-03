using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop;

public sealed record ReplayAnalysisBatchProgress(int Completed, int Total, string CurrentTitle);

public sealed record ReplayAnalysisBatchResult(
    IReadOnlyDictionary<Guid, ReplayAnalysisResult> Completed,
    IReadOnlyList<Guid> Failed);

/// <summary>
/// Analyses a small replay batch sequentially in one muted worker. This is used to
/// enrich coaching without constructing replay players or starting audio tracks.
/// </summary>
public sealed class ReplayAnalysisBatchService
{
    public const int MaximumBatchSize = 5;

    private readonly ExternalLazerReplayOpenService replayOpenService;
    private readonly Action<string> log;

    public ReplayAnalysisBatchService(
        ExternalLazerReplayOpenService replayOpenService,
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
    {
        LocalReplay[] pending = SelectPending(replays, completedScoreIds, limit);
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
                await using ExternalLazerPlayableReplayBundle bundle = await replayOpenService.OpenAsync(replay, cancellationToken).ConfigureAwait(false);
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
}

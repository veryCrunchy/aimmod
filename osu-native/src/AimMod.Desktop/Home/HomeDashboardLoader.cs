using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.PpTargets;
using AimMod.Desktop.ScoreHistory;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop.Home;

/// <summary>Collects the dashboard inputs from the shared score history and small local caches.</summary>
internal static class HomeDashboardLoader
{
    private const int hydration_limit = 200;

    public static async Task<HomeDashboardData> LoadAsync(HomeDashboardSources sources, CancellationToken token)
    {
        ScoreHistorySession history = ScoreHistorySession.For(sources.Library);
        IAccountScoreHistoryService? service = sources.AccountHistory();
        Task<StatisticsHistoryLoadResult> localTask = history.GetLocalAsync(cancellationToken: token);
        Task<OnlineAccountScoreHistoryResult?> onlineTask = loadOnlineAsync(history, service, token);
        await Task.WhenAll(localTask, onlineTask).ConfigureAwait(false);
        StatisticsHistoryLoadResult local = await localTask.ConfigureAwait(false);
        OnlineAccountScoreHistoryResult? online = await onlineTask.ConfigureAwait(false);
        IReadOnlyList<LocalReplay> runs = history.Merge(local.Runs, online?.Scores ?? []);
        token.ThrowIfCancellationRequested();

        DateTimeOffset now = sources.Clock();
        runs = await hydrateRecentAsync(runs, sources.PpHydration(), now, token).ConfigureAwait(false);

        OsuProfile? profile = sources.Profile();
        // A public stable profile carries statistics; a remembered local profile may not.
        if (profile?.Statistics is null && online?.Profile is { Statistics: not null } fetched && (profile is null || fetched.UserId == profile.UserId))
            profile = fetched;
        HomeProfileChange? change = profile is null ? null : safely(() => sources.RecordProfile(profile));

        PpTargetWorkspaceSnapshot? targets = safely(sources.PpTargets);
        var inputs = new HomeDashboardInputs(runs, profile, change,
            safely(sources.CoachingPlan), safely(sources.GuidedPlan), safely(sources.TrainerHistory) ?? [], targets)
        {
            Patterns = patterns(runs, profile, targets, sources, now),
        };
        token.ThrowIfCancellationRequested();
        return HomeDashboardBuilder.Build(inputs, now);
    }

    /// <summary>
    /// The player's last 30 days of analysed plays, measured like PP targets: circle sizes from the cached local maps and
    /// earlier measurements from the cached profile, so plays without an analysis in memory still count.
    /// </summary>
    private static PpPatternProfile? patterns(IReadOnlyList<LocalReplay> runs, OsuProfile? profile, PpTargetWorkspaceSnapshot? targets,
        HomeDashboardSources sources, DateTimeOffset now)
    {
        string? player = HomeDashboardBuilder.SelectPlayer(runs, profile);
        var own = runs.Where(run => player is null || !run.IsLocallyStored || string.Equals(run.Player, player, StringComparison.OrdinalIgnoreCase)).ToArray();
        var analyses = sources.ReplayAnalyses?.Invoke() ?? new Dictionary<Guid, ReplayAnalysisResult>();
        var cached = targets?.Profile.PatternProfile ?? targets?.PendingPatternProfile;
        try
        {
            return PpTargetPatternModel.BuildProfile(own, analyses, now: now, localSets: targets?.LocalSets, cachedProfile: cached);
        }
        catch (ArgumentException)
        {
            return cached;
        }
    }

    private static async Task<OnlineAccountScoreHistoryResult?> loadOnlineAsync(ScoreHistorySession history, IAccountScoreHistoryService? service, CancellationToken token)
    {
        try
        {
            return await history.GetOnlineAsync(service, cancellationToken: token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Local plays are still worth showing when the account feed is unavailable.
            return null;
        }
    }

    /// <summary>Fills in PP for this month's local plays, which the osu! database does not always store.</summary>
    private static async Task<IReadOnlyList<LocalReplay>> hydrateRecentAsync(IReadOnlyList<LocalReplay> runs, ILocalScorePpHydrationService? hydrator,
        DateTimeOffset now, CancellationToken token)
    {
        if (hydrator is null)
            return runs;
        LocalReplay[] missing = runs.Where(run => run.IsLocallyStored && run.PerformancePoints is null && run.PlayedAt >= now.AddDays(-60))
            .OrderByDescending(run => run.PlayedAt).Take(hydration_limit).ToArray();
        if (missing.Length == 0)
            return runs;
        try
        {
            var hydrated = (await hydrator.HydrateAsync(missing, token).ConfigureAwait(false)).Runs
                .GroupBy(run => run.ScoreId).ToDictionary(group => group.Key, group => group.First());
            return runs.Select(run => hydrated.TryGetValue(run.ScoreId, out LocalReplay? filled) ? filled : run).ToArray();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Home could not calculate local PP: {error.Message}");
            return runs;
        }
    }

    private static T? safely<T>(Func<T?> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

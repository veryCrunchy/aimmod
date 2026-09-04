using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Visuals;
using AimMod.Desktop.ScoreHistory;
using AimMod.Desktop.Practice;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using osu.Framework.Threading;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;

namespace AimMod.Desktop.Coaching;

/// <summary>
/// An account-wide coaching workspace backed by merged score history and completed replay analyses.
/// </summary>
public partial class NativeCoachingWorkspace : CompositeDrawable
{
    private const int visible_run_limit = 24;
    private const int practice_candidate_pool_limit = 500;
    private const int practice_candidate_display_limit = 100;
    internal const double PracticeFilterDebounceMilliseconds = 180;

    private readonly ILocalLibrarySource source;
    private readonly ILocalLibrarySourceChanged? sourceChanges;
    private readonly IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses;
    private readonly Action<LocalReplay> openReplay;
    private readonly Func<IAccountScoreHistoryService?> accountHistory;
    private readonly Func<PracticeMapGenerationRequest, CancellationToken, Task<PracticeMapGenerationResult>>? generatePracticeMap;
    private readonly Action<string>? openPracticeFolder;
    private readonly Func<LazerBeatmapArchive, CancellationToken, Task<LazerBeatmapInstallResult>>? installPracticeMap;

    private readonly Container headerArtwork;
    private readonly OsuSpriteText sessionTitle;
    private readonly OsuSpriteText sessionPlays;
    private readonly OsuSpriteText sessionDuration;
    private readonly OsuSpriteText sessionAccuracy;
    private readonly OsuSpriteText sessionTrend;
    private readonly AnalysisProgressBanner analysisBanner;
    private readonly CoachingTrendChart trendChart;
    private readonly FillFlowContainer<Drawable> selectedRunHost;
    private readonly FillFlowContainer<Drawable> exactAnalysisHost;
    private readonly FillFlowContainer<Drawable> changesHost;
    private readonly FillFlowContainer<Drawable> recommendationHost;
    private readonly FillFlowContainer<Drawable> practiceHost;
    private readonly SectionLine practiceSectionLine;
    private readonly OsuTextBox practiceSearch;
    private readonly Bindable<PracticeCandidateSort> practiceSort = new(PracticeCandidateSort.WeakestFirst);
    private readonly Bindable<PracticeEvidenceFilter> practiceEvidence = new(PracticeEvidenceFilter.AnyEvidence);
    private readonly BindableDouble practiceMinimumStars = new(0) { MinValue = 0, MaxValue = 10, Default = 0 };
    private readonly BindableDouble practiceMaximumStars = new(10) { MinValue = 0, MaxValue = 10, Default = 10 };
    private readonly PracticeCandidatePoolCache practiceCandidatePool = new(practice_candidate_pool_limit);
    private readonly OsuTextBox search;
    private readonly FillFlowContainer<Drawable> runList;
    private readonly AimModLoadingOverlay loadingOverlay;

    private CancellationTokenSource? loading;
    private CancellationTokenSource? practiceGeneration;
    private CancellationTokenSource? practiceLaunch;
    private ScheduledDelegate? scheduledPracticeRefresh;
    private IReadOnlyList<LocalReplay> replays = Array.Empty<LocalReplay>();
    private PracticeCandidatePage? renderedPracticePage;
    private PracticeDisplayState renderedPracticeState;
    private NativeCoachingWorkspaceModel? workspace;
    private bool acceptingAnalysisProgress;
    private bool creatingPracticeMap;
    private bool openingPracticeMap;
    private bool practiceSucceeded;
    private int renderedAnalysisCount = -1;
    private string practiceMessage = string.Empty;
    private string? practiceDirectory;
    private LazerBeatmapArchive? practiceLazerArchive;

    public NativeCoachingWorkspace(
        ILocalLibrarySource source,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses,
        Action<LocalReplay> openReplay,
        Func<IAccountScoreHistoryService?>? accountHistory = null,
        Func<PracticeMapGenerationRequest, CancellationToken, Task<PracticeMapGenerationResult>>? generatePracticeMap = null,
        Action<string>? openPracticeFolder = null,
        Func<LazerBeatmapArchive, CancellationToken, Task<LazerBeatmapInstallResult>>? installPracticeMap = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.analyses = analyses ?? throw new ArgumentNullException(nameof(analyses));
        this.openReplay = openReplay ?? throw new ArgumentNullException(nameof(openReplay));
        this.accountHistory = accountHistory ?? (() => null);
        this.generatePracticeMap = generatePracticeMap;
        this.openPracticeFolder = openPracticeFolder;
        this.installPracticeMap = installPracticeMap;
        sourceChanges = source as ILocalLibrarySourceChanged;
        if (sourceChanges is not null)
            sourceChanges.SourceChanged += sourceChanged;

        RelativeSizeAxes = Axes.Both;

        var content = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new(AimModVisualStyle.SectionSpacing),
            Padding = new MarginPadding { Right = 8, Bottom = 40 },
        };

        content.Add(createSessionHeader(
            out headerArtwork,
            out sessionTitle,
            out sessionPlays,
            out sessionDuration,
            out sessionAccuracy,
            out sessionTrend));
        content.Add(analysisBanner = new AnalysisProgressBanner());
        content.Add(new GridContainer
        {
            RelativeSizeAxes = Axes.X,
            Height = 540,
            ColumnDimensions = new[]
            {
                new Dimension(GridSizeMode.Relative, 0.59f),
                new Dimension(GridSizeMode.Relative, 0.41f),
            },
            Content = new[]
            {
                new Drawable[]
                {
                    createPerformancePanel(out trendChart, out selectedRunHost, out exactAnalysisHost),
                    createPracticePanel(
                        out practiceHost,
                        out practiceSectionLine,
                        out practiceSearch,
                        practiceSort,
                        practiceEvidence,
                        practiceMinimumStars,
                        practiceMaximumStars),
                },
            },
        });
        content.Add(new AimModSubsectionHeader(
            "Coaching plan",
            "Priorities and next plays from your account-wide profile"));
        content.Add(createCoachPanel(out changesHost, out recommendationHost).With(panel => panel.Height = 410));
        content.Add(new AimModSubsectionHeader(
            "Beatmap drill-down",
            "Inspect a play or open its replay"));
        content.Add(search = new OsuTextBox
        {
            RelativeSizeAxes = Axes.X,
            Height = AimModVisualStyle.ControlHeight,
            PlaceholderText = "Search beatmaps, difficulties, artists, players, or mods",
        });
        content.Add(runList = new FillFlowContainer<Drawable>
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new(AimModVisualStyle.RelatedSpacing),
        });

        InternalChildren = new Drawable[]
        {
            new AimModScrollContainer
            {
                RelativeSizeAxes = Axes.Both,
                Depth = 10,
                Child = content,
            },
            loadingOverlay = new AimModLoadingOverlay(),
        };

        practiceSearch.Current.BindValueChanged(_ => updatePracticeMapsImmediately());
        practiceSort.BindValueChanged(_ => updatePracticeMapsImmediately());
        practiceEvidence.BindValueChanged(_ => updatePracticeMapsImmediately());
        practiceMinimumStars.BindValueChanged(_ => schedulePracticeMapRefresh());
        practiceMaximumStars.BindValueChanged(_ => schedulePracticeMapRefresh());
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        search.OnCommit += (_, _) => refreshRunList();
        load();
    }

    private void load()
    {
        loading?.Cancel();
        loading?.Dispose();
        loading = new CancellationTokenSource();
        analysisBanner.ShowHistoryLoading();
        loadingOverlay.ShowLoading("Preparing coaching", "Merging submitted and local osu!standard scores");
        _ = loadAsync(loading.Token);
    }

    private async Task loadAsync(CancellationToken cancellationToken)
    {
        try
        {
            StatisticsHistoryLoadResult history = await StatisticsHistoryLoader.LoadAsync(source, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<LocalReplay> local = ScoreHistoryMerger.MergeAsLocalReplays(history.Runs, []);
            NativeCoachingWorkspaceModel localModel = NativeCoachingWorkspaceModel.Build(local, analyses);
            if (!IsDisposed)
                Schedule(() => apply(local, Math.Max(history.TotalAvailableRunCount, local.Count), localModel, null));

            IAccountScoreHistoryService? service = accountHistory();
            if (service is null)
                return;

            OnlineAccountScoreHistoryResult online;
            try
            {
                online = await service.FetchAccountAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                if (!IsDisposed)
                    Schedule(() => analysisBanner.ShowWarning(
                        "Submitted scores could not be refreshed",
                        "Your local plays and replay evidence remain available."));
                return;
            }

            IReadOnlyList<LocalReplay> merged = ScoreHistoryMerger.MergeAsLocalReplays(history.Runs, online.Scores);
            NativeCoachingWorkspaceModel mergedModel = NativeCoachingWorkspaceModel.Build(merged, analyses);
            if (!IsDisposed)
                Schedule(() => apply(merged, Math.Max(history.TotalAvailableRunCount, merged.Count), mergedModel, online));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (!IsDisposed)
                Schedule(() =>
                {
                    loadingOverlay.HideLoading();
                    analysisBanner.ShowError(
                        "Score history could not be loaded",
                        "Check the local osu! data source and reopen Coaching.");
                });
        }
    }

    private void apply(
        IReadOnlyList<LocalReplay> nextReplays,
        int total,
        NativeCoachingWorkspaceModel next,
        OnlineAccountScoreHistoryResult? online)
    {
        replays = nextReplays;
        workspace = next;
        renderedAnalysisCount = analyses.Count;
        invalidatePracticeCandidates();
        int submitted = online?.Scores.Count ?? 0;
        loadingOverlay.HideLoading();
        updateWorkspace();
        if (!acceptingAnalysisProgress)
            analysisBanner.ShowReady(next.GlobalProfile, nextReplays.Count, submitted, total);
    }

    private void selectRun(Guid scoreId)
    {
        workspace = NativeCoachingWorkspaceModel.Build(replays, analyses, scoreId);
        updateWorkspace();
    }

    private void showGlobalOverview()
    {
        workspace = NativeCoachingWorkspaceModel.Build(replays, analyses);
        updateWorkspace();
    }

    public void SetAnalysisProgress(int completed, int total, string currentTitle)
    {
        if (!acceptingAnalysisProgress || total <= 0)
            return;

        if (workspace is not null && renderedAnalysisCount != analyses.Count)
        {
            Guid? selectedScoreId = workspace.SelectedRun?.ScoreId;
            workspace = NativeCoachingWorkspaceModel.Build(replays, analyses, selectedScoreId);
            renderedAnalysisCount = analyses.Count;
            invalidatePracticeCandidates();
            updateWorkspace();
        }

        analysisBanner.ShowAnalysing(
            completed,
            total,
            currentTitle,
            workspace?.GlobalProfile.Coverage.AnalysedRunCount ?? analyses.Count);
        if (workspace is null)
        {
            loadingOverlay.SetProgress(
                completed >= total ? "Updating your coaching report" : currentTitle,
                completed,
                total);
        }
        else
        {
            loadingOverlay.HideLoading();
        }
    }

    public void BeginAnalysisProgress()
    {
        acceptingAnalysisProgress = true;
        analysisBanner.ShowStarting(workspace?.GlobalProfile.Coverage.AnalysedRunCount ?? analyses.Count);
        if (workspace is null)
            loadingOverlay.ShowLoading("Preparing coaching", "Loading score history before replay analysis");
        else
            loadingOverlay.HideLoading();
    }

    public void ApplyNewAnalyses(int completed, int failed)
    {
        acceptingAnalysisProgress = false;
        Guid? selectedScoreId = workspace?.SelectedRun?.ScoreId;
        workspace = NativeCoachingWorkspaceModel.Build(replays, analyses, selectedScoreId);
        renderedAnalysisCount = analyses.Count;
        invalidatePracticeCandidates();
        updateWorkspace();

        analysisBanner.ShowComplete(workspace.GlobalProfile, completed, failed);
        loadingOverlay.HideLoading();
    }

    public void SetAnalysisError()
    {
        acceptingAnalysisProgress = false;
        loadingOverlay.HideLoading();
        analysisBanner.ShowError(
            "Replay analysis paused",
            "Your existing coaching profile and generated drills are still available.");
    }

    private void updateWorkspace()
    {
        NativeCoachingWorkspaceModel model = workspace ?? NativeCoachingWorkspaceModel.Build(Array.Empty<LocalReplay>(), analyses);
        CoachingReport report = model.Report;
        LocalReplay? selected = model.SelectedRun;

        updateSessionHeader(model);
        trendChart.SetRuns(model.TrendRuns, selected?.ScoreId, selectRun);
        updateSelectedRun(model, report.Intelligence.SelectedRunPrediction);
        updateExactAnalysis(selected, report.Intelligence.Mechanics);
        updateChanges(report.Intelligence, model.GlobalProfile, selected is null);
        updateRecommendation(report.Intelligence.Recommendations);
        updatePracticeMapsImmediately();
        refreshRunList();
    }

    private void schedulePracticeMapRefresh()
    {
        scheduledPracticeRefresh?.Cancel();
        scheduledPracticeRefresh = Scheduler.AddDelayed(() =>
        {
            scheduledPracticeRefresh = null;
            updatePracticeMaps();
        }, PracticeFilterDebounceMilliseconds);
    }

    private void updatePracticeMapsImmediately()
    {
        scheduledPracticeRefresh?.Cancel();
        scheduledPracticeRefresh = null;
        updatePracticeMaps();
    }

    private void updatePracticeMaps()
    {
        IReadOnlyList<PracticeMapCandidate> available = practiceCandidatePool.Get(replays, analyses);
        PracticeCandidatePage candidates = PracticeMapCandidateSearch.Search(
            available,
            new PracticeCandidateQuery(
                practiceSearch.Current.Value,
                practiceSort.Value,
                practiceEvidence.Value,
                practiceMinimumStars.Value,
                practiceMaximumStars.Value),
            practice_candidate_display_limit);
        var displayState = new PracticeDisplayState(
            acceptingAnalysisProgress,
            creatingPracticeMap,
            openingPracticeMap,
            practiceSucceeded,
            practiceMessage,
            practiceDirectory,
            practiceLazerArchive is not null);
        if (SamePracticeCandidatePage(renderedPracticePage, candidates) && renderedPracticeState == displayState)
            return;

        renderedPracticePage = candidates;
        renderedPracticeState = displayState;
        practiceHost.Clear();
        practiceSectionLine.SetDetail(PracticeCandidateDetail(candidates));

        if (creatingPracticeMap)
        {
            practiceHost.Add(new PracticeStatusRow(
                "Creating your drill",
                practiceMessage,
                AimModPalette.Cyan,
                FontAwesome.Solid.CircleNotch));
            return;
        }

        if (openingPracticeMap)
        {
            practiceHost.Add(new PracticeStatusRow(
                "Opening in osu!lazer",
                practiceMessage,
                AimModPalette.Cyan,
                FontAwesome.Solid.CircleNotch));
            return;
        }

        if (!string.IsNullOrWhiteSpace(practiceMessage))
        {
            string? actionLabel = null;
            Action? action = null;
            if (practiceLazerArchive is not null && installPracticeMap is not null)
            {
                actionLabel = "Open in osu!";
                action = beginPracticeLaunch;
            }
            else if (practiceDirectory is not null && openPracticeFolder is not null)
            {
                actionLabel = "Open folder";
                action = () => openPracticeFolder(practiceDirectory);
            }

            practiceHost.Add(new PracticeStatusRow(
                practiceSucceeded ? "Practice map ready" : practiceDirectory is null ? "Practice map not created" : "Practice map exported",
                practiceMessage,
                practiceSucceeded ? AimModPalette.Success : practiceDirectory is null ? AimModPalette.Pink : AimModPalette.Yellow,
                practiceSucceeded ? FontAwesome.Solid.CheckCircle : FontAwesome.Solid.ExclamationCircle,
                actionLabel,
                action));
        }

        if (available.Count == 0)
        {
            practiceHost.Add(new PracticeEmptyState(
                acceptingAnalysisProgress ? "Finding your weakest patterns" : "More replay evidence needed",
                acceptingAnalysisProgress
                    ? "Practice candidates will appear as repeated misses are confirmed."
                    : "Play or import saved replays to identify repeatable jump and stream sections."));
            return;
        }

        if (candidates.Total == 0)
        {
            practiceHost.Add(new PracticeEmptyState(
                "No practice maps match",
                "Adjust the search, evidence, or star filters to widen the ranked pool."));
            return;
        }

        foreach (PracticeMapCandidate candidate in candidates.Items)
            practiceHost.Add(new PracticeCandidateRow(candidate, beginPracticeMap));
    }

    private void beginPracticeMap(PracticeMapCandidate candidate, PracticeDrillType drillType)
    {
        if (generatePracticeMap is null || creatingPracticeMap)
            return;
        practiceGeneration?.Cancel();
        practiceGeneration?.Dispose();
        practiceLaunch?.Cancel();
        practiceLaunch?.Dispose();
        practiceLaunch = null;
        practiceGeneration = new CancellationTokenSource();
        creatingPracticeMap = true;
        openingPracticeMap = false;
        practiceSucceeded = false;
        practiceDirectory = null;
        practiceLazerArchive = null;
        practiceMessage = $"Preparing {candidate.SourceReplay.Title} [{candidate.SourceReplay.Difficulty}]";
        updatePracticeMapsImmediately();
        _ = generatePracticeMapAsync(new PracticeMapGenerationRequest(candidate, drillType), practiceGeneration.Token);
    }

    private async Task generatePracticeMapAsync(PracticeMapGenerationRequest request, CancellationToken cancellationToken)
    {
        PracticeMapGenerationResult result;
        try
        {
            result = await generatePracticeMap!(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            result = new PracticeMapGenerationResult(false, "The practice map could not be created. Try another section.");
        }

        if (!IsDisposed && !cancellationToken.IsCancellationRequested)
        {
            Schedule(() =>
            {
                creatingPracticeMap = false;
                practiceSucceeded = result.Success;
                practiceMessage = result.Message;
                practiceDirectory = result.DirectoryPath;
                practiceLazerArchive = result.LazerArchive;
                updatePracticeMapsImmediately();
            });
        }
    }

    private void beginPracticeLaunch()
    {
        if (practiceLazerArchive is null || installPracticeMap is null || openingPracticeMap)
            return;

        practiceLaunch?.Cancel();
        practiceLaunch?.Dispose();
        practiceLaunch = new CancellationTokenSource();
        openingPracticeMap = true;
        practiceMessage = "Sending the generated .osz to your osu!lazer installation";
        updatePracticeMapsImmediately();
        _ = launchPracticeMapAsync(practiceLazerArchive, practiceLaunch.Token);
    }

    private async Task launchPracticeMapAsync(LazerBeatmapArchive archive, CancellationToken cancellationToken)
    {
        LazerBeatmapInstallResult result;
        try
        {
            result = await installPracticeMap!(archive, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            result = new LazerBeatmapInstallResult(LazerBeatmapInstallStatus.LaunchFailed);
        }

        if (!IsDisposed && !cancellationToken.IsCancellationRequested)
        {
            Schedule(() =>
            {
                openingPracticeMap = false;
                practiceMessage = PracticeLaunchMessage(result.Status);
                if (!PracticeLaunchSucceeded(result.Status))
                    practiceSucceeded = false;
                practiceLazerArchive = null;
                updatePracticeMapsImmediately();
            });
        }
    }

    private void invalidatePracticeCandidates()
    {
        practiceCandidatePool.Invalidate();
        renderedPracticePage = null;
    }

    internal static bool SamePracticeCandidatePage(PracticeCandidatePage? previous, PracticeCandidatePage current)
    {
        if (previous is null || previous.Total != current.Total || previous.Available != current.Available || previous.Items.Count != current.Items.Count)
            return false;

        for (int i = 0; i < previous.Items.Count; i++)
        {
            PracticeMapCandidate left = previous.Items[i];
            PracticeMapCandidate right = current.Items[i];
            if (left.SourceReplay.ScoreId != right.SourceReplay.ScoreId
                || !left.AnalysisScoreIds.SequenceEqual(right.AnalysisScoreIds)
                || left.AnalysedAttempts != right.AnalysedAttempts
                || left.MissCount != right.MissCount
                || left.WeaknessScore != right.WeaknessScore
                || left.AttemptsWithMisses != right.AttemptsWithMisses
                || left.AverageMissConfidence != right.AverageMissConfidence)
                return false;
        }

        return true;
    }

    private void updateSessionHeader(NativeCoachingWorkspaceModel model)
    {
        LocalReplay? selected = model.SelectedRun;
        CoachingSessionSummary? session = model.Session;
        GlobalCoachingSummary global = model.Global;
        headerArtwork.Clear();
        if (!string.IsNullOrWhiteSpace(selected?.BackgroundPath))
            headerArtwork.Add(new AimModLocalArtwork(selected.BackgroundPath));

        sessionTitle.Text = selected is null
            ? "Global coaching profile"
            : $"Selected map: {selected.Title} [{selected.Difficulty}]";
        sessionPlays.Text = selected is null
            ? $"{global.RunCount:N0} merged {(global.RunCount == 1 ? "play" : "plays")}"
            : session is null ? "Selected play" : $"{session.PlayCount:N0} session {(session.PlayCount == 1 ? "play" : "plays")}";
        sessionDuration.Text = selected is null
            ? global.FirstPlayAt is { } first && global.LastPlayAt is { } last
                ? $"{first:MMM yyyy} - {last:MMM yyyy}"
                : "No history yet"
            : session is null ? $"{selected.PlayedAt:MMM d, yyyy}" : formatDuration(session.Duration);
        sessionAccuracy.Text = (selected is null ? global.MedianAccuracy : session?.MedianAccuracy) is { } median ? $"{median:P1}" : "-";

        CoachingPerformanceTrend trend = model.Report.Intelligence.Trend;
        sessionTrend.Text = trend.MatchedAccuracyChange is { } matched
            ? $"{matched * 100:+0.0;-0.0;0.0} pts matched"
            : trend.RecentAccuracyChange is { } recent
                ? $"{recent * 100:+0.0;-0.0;0.0} pts recent"
                : "More plays needed";
        sessionTrend.Colour = (trend.MatchedAccuracyChange ?? trend.RecentAccuracyChange) switch
        {
            > 0 => AimModPalette.Success,
            < 0 => AimModPalette.Pink,
            _ => AimModPalette.Muted,
        };
    }

    private void updateSelectedRun(NativeCoachingWorkspaceModel model, CoachingAccuracyPrediction? prediction)
    {
        selectedRunHost.Clear();
        LocalReplay? run = model.SelectedRun;
        if (run is null)
        {
            GlobalCoachingSummary global = model.Global;
            selectedRunHost.Add(new GridContainer
            {
                RelativeSizeAxes = Axes.X,
                Height = 48,
                ColumnDimensions = new[]
                {
                    new Dimension(GridSizeMode.Relative, 0.25f),
                    new Dimension(GridSizeMode.Relative, 0.25f),
                    new Dimension(GridSizeMode.Relative, 0.25f),
                    new Dimension(GridSizeMode.Relative, 0.25f),
                },
                Content = new[]
                {
                    new Drawable[]
                    {
                        miniMetric("Local", global.LocalRunCount.ToString("N0"), AimModPalette.Cyan),
                        miniMetric("Submitted", global.SubmittedRunCount.ToString("N0"), AimModPalette.Pink),
                        miniMetric("Beatmaps", global.DistinctBeatmapCount.ToString("N0"), AimModPalette.Yellow),
                        miniMetric("Exact replays", global.ExactAnalysisRunCount.ToString("N0"), AimModPalette.Success),
                    },
                },
            });
            return;
        }

        selectedRunHost.Add(new SelectedRunCard(
            run,
            prediction,
            showGlobalOverview,
            run.HasReplayFile ? () => openReplay(run) : null));
    }

    private void updateExactAnalysis(LocalReplay? run, CoachingMechanicsProfile mechanics)
    {
        exactAnalysisHost.Clear();
        if (run is null)
        {
            GlobalCoachingProfile profile = workspace?.GlobalProfile ?? GlobalCoachingProfile.Empty;
            exactAnalysisHost.Add(new GlobalSkillProfileGrid(profile));
            return;
        }

        if (!analyses.TryGetValue(run.ScoreId, out ReplayAnalysisResult? result)
            || result.Summary is null
            || result.Judgements is null)
        {
            exactAnalysisHost.Add(flow(
                run.HasReplayFile
                    ? "Open this replay to calculate exact hit timing, miss locations, slider breaks, and cursor error."
                    : "This score has no saved replay, so object-level timing and miss locations are unavailable.",
                13,
                AimModPalette.Muted));
            return;
        }

        ReplayAnalysisPresentation presentation = ReplayAnalysisPresenter.Present(result);
        ReplayObjectJudgement[] timing = result.Judgements.Where(judgement =>
            !string.Equals(judgement.Result, "Miss", StringComparison.OrdinalIgnoreCase)
            && double.IsFinite(judgement.TimeOffsetMs)
            && string.Equals(judgement.MaximumResult, "Great", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        double? mean = timing.Length == 0 ? null : timing.Average(judgement => judgement.TimeOffsetMs);
        double? spread = timing.Length == 0 ? null : standardDeviation(timing.Select(judgement => judgement.TimeOffsetMs));

        exactAnalysisHost.Add(new GridContainer
        {
            RelativeSizeAxes = Axes.X,
            Height = 58,
            ColumnDimensions = new[]
            {
                new Dimension(GridSizeMode.Relative, 0.25f),
                new Dimension(GridSizeMode.Relative, 0.25f),
                new Dimension(GridSizeMode.Relative, 0.25f),
                new Dimension(GridSizeMode.Relative, 0.25f),
            },
            Content = new[]
            {
                new Drawable[]
                {
                    miniMetric("Great", result.Summary.Great.ToString("N0"), AimModPalette.Success),
                    miniMetric("Lower hits", (result.Summary.Ok + result.Summary.Meh).ToString("N0"), AimModPalette.Yellow),
                    miniMetric("Misses", result.Summary.Miss.ToString("N0"), AimModPalette.Pink),
                    miniMetric("Hit spread", spread is { } value ? $"{value:0.0} ms" : "-", AimModPalette.Cyan),
                },
            },
        });
        exactAnalysisHost.Add(flow(
            mean is { } offset
                ? $"Average hit offset {formatSignedMilliseconds(offset)}. {presentation.NotableMoments}"
                : presentation.NotableMoments,
            12,
            AimModPalette.Muted));
    }

    private void updateChanges(CoachingIntelligence intelligence, GlobalCoachingProfile profile, bool isGlobal)
    {
        changesHost.Clear();

        if (isGlobal)
        {
            Colour4[] accents = { AimModPalette.Pink, AimModPalette.Cyan, AimModPalette.Success, AimModPalette.Yellow };
            foreach ((GlobalCoachingPriority priority, int index) in profile.Priorities.Select((priority, index) => (priority, index)))
            {
                changesHost.Add(new InsightRow(
                    priority.Title,
                    priority.Detail,
                    priority.Value,
                    accents[index % accents.Length]));
            }

            if (profile.Priorities.Count == 0)
            {
                changesHost.Add(new InsightRow(
                    "Build replay evidence",
                    "Replay review will populate timing, aim, recurring weaknesses, and practice priorities here.",
                    "Waiting",
                    AimModPalette.Cyan));
            }
            return;
        }

        changesHost.Add(new InsightRow(
            "Performance trend",
            trendDetail(intelligence.Trend),
            trendValue(intelligence.Trend),
            AimModPalette.Pink));
        changesHost.Add(new InsightRow(
            "Difficulty fit",
            intelligence.DifficultyFit.Summary,
            intelligence.DifficultyFit.BestFit is { } band ? $"{band.MinimumStars:0.0}-{band.MaximumStars:0.0}*" : "Not measured",
            AimModPalette.Cyan));
        changesHost.Add(new InsightRow(
            "Session drift",
            intelligence.SessionDrift.Summary,
            intelligence.SessionDrift.AccuracyChange is { } drift ? $"{drift * 100:+0.0;-0.0;0.0} pts" : "Not measured",
            Colour4.FromHex("FF9C55")));
        changesHost.Add(new InsightRow(
            "Mechanics",
            MechanicsDetail(intelligence.Mechanics),
            MechanicsValue(intelligence.Mechanics),
            AimModPalette.Success));
    }

    private void updateRecommendation(IReadOnlyList<CoachingRecommendation> recommendations)
    {
        recommendationHost.Clear();
        CoachingRecommendation? recommendation = recommendations.FirstOrDefault();
        if (recommendation is null)
        {
            recommendationHost.Add(flow(
                "Play more comparable maps. Saved local replays add exact mechanics, while submitted scores build the broader performance model.",
                13,
                AimModPalette.Muted));
            return;
        }

        LocalReplay? run = replays.FirstOrDefault(candidate => candidate.ScoreId == recommendation.ScoreId);
        recommendationHost.Add(new RecommendationCard(
            recommendation,
            run is { HasReplayFile: true } ? () => openReplay(run) : null));
    }

    private void refreshRunList()
    {
        runList.Clear();
        CoachingRunPage page = CoachingRunSearch.Search(replays, new CoachingRunQuery(
            SearchText: search.Current.Value,
            Sort: CoachingRunSort.Recent,
            Limit: visible_run_limit));
        if (page.Items.Count == 0)
        {
            runList.Add(flow("No account scores match this search.", 14, AimModPalette.Muted).With(text => text.Padding = new MarginPadding(18)));
            return;
        }

        Guid? selectedId = workspace?.SelectedRun?.ScoreId;
        foreach (CoachingRecentRun run in page.Items)
            runList.Add(new RunPickerRow(run, run.ScoreId == selectedId, () => selectRun(run.ScoreId)));

        if (page.HasMore)
        {
            runList.Add(label(
                $"Showing the newest {page.Items.Count:N0} of {page.Total:N0} matching runs. Refine the search to find older plays.",
                11,
                AimModPalette.Muted).With(text => text.Padding = new MarginPadding(12)));
        }
    }

    private static Container createSessionHeader(
        out Container artwork,
        out OsuSpriteText title,
        out OsuSpriteText plays,
        out OsuSpriteText duration,
        out OsuSpriteText accuracy,
        out OsuSpriteText trend)
    {
        var header = new Container
        {
            RelativeSizeAxes = Axes.X,
            Height = 96,
            Masking = true,
            CornerRadius = AimModVisualStyle.CardRadius,
        };

        artwork = new Container { RelativeSizeAxes = Axes.Both };
        title = label("Global coaching overview", 22, AimModPalette.Text, "Bold");
        plays = label("No plays", 13, AimModPalette.Text, "SemiBold");
        duration = label("No session yet", 13, AimModPalette.Text, "SemiBold");
        accuracy = label("-", 22, AimModPalette.Text, "Bold");
        trend = label("More plays needed", 14, AimModPalette.Muted, "Bold");

        header.Children = new Drawable[]
        {
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = ColourInfo.GradientHorizontal(AimModPalette.Panel, AimModPalette.CyanDark),
            },
            artwork,
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = ColourInfo.GradientHorizontal(AimModPalette.Canvas, AimModPalette.Canvas.Opacity(0.48f)),
            },
            new Box
            {
                Anchor = Anchor.TopRight,
                Origin = Anchor.TopRight,
                RelativeSizeAxes = Axes.Y,
                Width = 210,
                X = 55,
                Shear = new(-0.18f, 0),
                Colour = AimModPalette.Pink,
                Alpha = 0.13f,
            },
            new FillFlowContainer
            {
                AutoSizeAxes = Axes.Both,
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                Margin = new MarginPadding { Left = 20 },
                Direction = FillDirection.Vertical,
                Spacing = new(AimModVisualStyle.RelatedSpacing),
                Children = new Drawable[]
                {
                    title,
                    new FillFlowContainer
                    {
                        AutoSizeAxes = Axes.Both,
                        Direction = FillDirection.Horizontal,
                        Spacing = new(20),
                        Children = new Drawable[]
                        {
                            headerMetric(FontAwesome.Regular.PlayCircle, plays),
                            headerMetric(FontAwesome.Regular.Clock, duration),
                            headerMetric(FontAwesome.Solid.Bullseye, label("Median accuracy", 11, AimModPalette.Muted), accuracy),
                            headerMetric(FontAwesome.Solid.ChartLine, label("Trend", 11, AimModPalette.Muted), trend),
                        },
                    },
                },
            },
        };
        return header;
    }

    private static Drawable headerMetric(IconUsage icon, params Drawable[] content) => new FillFlowContainer
    {
        AutoSizeAxes = Axes.Both,
        Direction = FillDirection.Horizontal,
        Spacing = new(9),
        Children = new Drawable[]
        {
            new SpriteIcon
            {
                Icon = icon,
                Size = new(18),
                Colour = AimModPalette.Text,
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
            },
            new FillFlowContainer
            {
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Vertical,
                Spacing = new(1),
                Children = content,
            },
        },
    };

    private static Container createPerformancePanel(
        out CoachingTrendChart chart,
        out FillFlowContainer<Drawable> selectedHost,
        out FillFlowContainer<Drawable> analysisHost)
    {
        var panel = new WorkspacePanel(new MarginPadding { Left = 16, Right = 12, Vertical = 14 });
        var body = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new(AimModVisualStyle.RelatedSpacing),
        };
        panel.Child = body;
        body.Add(sectionLine("Global skill profile", "All analysed maps"));
        body.Add(selectedHost = new FillFlowContainer<Drawable>
        {
            RelativeSizeAxes = Axes.X,
            Height = 48,
            Direction = FillDirection.Vertical,
        });
        body.Add(analysisHost = new FillFlowContainer<Drawable>
        {
            RelativeSizeAxes = Axes.X,
            Height = 126,
            Direction = FillDirection.Vertical,
            Spacing = new(8),
        });
        body.Add(sectionLine("Recent performance", "Select a point to inspect"));
        body.Add(chart = new CoachingTrendChart
        {
            RelativeSizeAxes = Axes.X,
            Height = 220,
        });
        return panel;
    }

    private static Container createPracticePanel(
        out FillFlowContainer<Drawable> practice,
        out SectionLine section,
        out OsuTextBox search,
        Bindable<PracticeCandidateSort> sort,
        Bindable<PracticeEvidenceFilter> evidence,
        BindableDouble minimumStars,
        BindableDouble maximumStars)
    {
        Container outer = new Container
        {
            RelativeSizeAxes = Axes.Both,
            Padding = new MarginPadding { Left = 8 },
        };
        var panel = new WorkspacePanel(new MarginPadding { Left = 16, Right = 14, Vertical = 14 });
        outer.Child = panel;
        var body = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new(7),
        };
        panel.Child = body;
        body.Add(section = new SectionLine("Practice map builder", "Finding maps"));
        body.Add(flow(
            "Generate a focused drill from the map sections where your misses repeat.",
            11,
            AimModPalette.Muted));
        body.Add(search = new OsuTextBox
        {
            RelativeSizeAxes = Axes.X,
            Height = AimModVisualStyle.CompactControlHeight,
            PlaceholderText = "Search maps, difficulties, players, or mods",
            Depth = -20,
        });
        body.Add(new GridContainer
        {
            RelativeSizeAxes = Axes.X,
            Height = AimModVisualStyle.CompactControlHeight,
            Depth = -20,
            ColumnDimensions = new[]
            {
                new Dimension(GridSizeMode.Relative, 0.5f),
                new Dimension(GridSizeMode.Relative, 0.5f),
            },
            Content = new[]
            {
                new Drawable[]
                {
                    new PracticeDropdown<PracticeCandidateSort>(PracticeSortLabel)
                    {
                        RelativeSizeAxes = Axes.X,
                        Width = 0.97f,
                        Items = Enum.GetValues<PracticeCandidateSort>(),
                        Current = sort,
                    },
                    new PracticeDropdown<PracticeEvidenceFilter>(PracticeEvidenceLabel)
                    {
                        Anchor = Anchor.TopRight,
                        Origin = Anchor.TopRight,
                        RelativeSizeAxes = Axes.X,
                        Width = 0.97f,
                        Items = Enum.GetValues<PracticeEvidenceFilter>(),
                        Current = evidence,
                    },
                },
            },
        });
        body.Add(new RangeSlider
        {
            RelativeSizeAxes = Axes.X,
            Height = 58,
            Label = "Stars",
            LowerBound = minimumStars,
            UpperBound = maximumStars,
            DefaultStringLowerBound = "0",
            DefaultStringUpperBound = "10+",
            TooltipSuffix = "stars",
            NubWidth = 24,
            Depth = -10,
        });
        body.Add(new AimModScrollContainer
        {
            RelativeSizeAxes = Axes.X,
            Height = 282,
            Depth = 10,
            Child = practice = new FillFlowContainer<Drawable>
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new(AimModVisualStyle.RelatedSpacing),
                Padding = new MarginPadding { Bottom = 12 },
            },
        });
        return outer;
    }

    private static Container createCoachPanel(
        out FillFlowContainer<Drawable> changes,
        out FillFlowContainer<Drawable> recommendation)
    {
        Container outer = new Container { RelativeSizeAxes = Axes.X };
        var panel = new WorkspacePanel(new MarginPadding(16));
        outer.Child = panel;
        var body = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new(AimModVisualStyle.RowSpacing),
        };
        panel.Child = body;
        body.Add(new GridContainer
        {
            RelativeSizeAxes = Axes.X,
            Height = 372,
            ColumnDimensions = new[]
            {
                new Dimension(GridSizeMode.Relative, 0.62f),
                new Dimension(GridSizeMode.Relative, 0.38f),
            },
            Content = new[]
            {
                new Drawable[]
                {
                    new FillFlowContainer<Drawable>
                    {
                        RelativeSizeAxes = Axes.Both,
                        Padding = new MarginPadding { Right = 12 },
                        Direction = FillDirection.Vertical,
                        Spacing = new(AimModVisualStyle.RelatedSpacing),
                        Children = new Drawable[]
                        {
                            sectionLine("Priorities", "Strongest evidence first"),
                            changes = new FillFlowContainer<Drawable>
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Direction = FillDirection.Vertical,
                                Spacing = new(2),
                            },
                        },
                    },
                    new FillFlowContainer<Drawable>
                    {
                        RelativeSizeAxes = Axes.Both,
                        Padding = new MarginPadding { Left = 12 },
                        Direction = FillDirection.Vertical,
                        Spacing = new(AimModVisualStyle.RelatedSpacing),
                        Children = new Drawable[]
                        {
                            sectionLine("Play next", "Best comparable map"),
                            recommendation = new FillFlowContainer<Drawable>
                            {
                                RelativeSizeAxes = Axes.X,
                                Height = 240,
                                Direction = FillDirection.Vertical,
                            },
                        },
                    },
                },
            },
        });
        return outer;
    }

    private static Drawable sectionLine(string title, string detail) => new SectionLine(title, detail);

    private static Drawable miniMetric(string title, string value, Colour4 colour) => new FillFlowContainer
    {
        RelativeSizeAxes = Axes.Both,
        Direction = FillDirection.Vertical,
        Spacing = new(3),
        Children = new Drawable[]
        {
            label(title, 10, AimModPalette.Muted, "SemiBold"),
            label(value, 17, colour, "Bold"),
        },
    };

    private static string trendDetail(CoachingPerformanceTrend trend)
    {
        if (trend.MatchedAccuracyChange is { } matched)
            return $"{trend.MatchedComparisonCount:N0} matched comparisons changed by {matched * 100:+0.0;-0.0;0.0} accuracy points.";
        if (trend.RecentAccuracyChange is { } recent)
            return $"The newer half of {trend.WindowSize:N0} plays changed by {recent * 100:+0.0;-0.0;0.0} accuracy points.";
        return "More comparable local plays are needed before a trend can be measured.";
    }

    private static string trendValue(CoachingPerformanceTrend trend) =>
        trend.MatchedAccuracyChange is { } matched
            ? $"{matched * 100:+0.0;-0.0;0.0} pts"
            : trend.RecentAccuracyChange is { } recent
                ? $"{recent * 100:+0.0;-0.0;0.0} pts"
                : trend.Direction;

    internal static string MechanicsDetail(CoachingMechanicsProfile mechanics)
    {
        if (mechanics.ExactAnalysisRunCount == 0)
            return "Open saved replays to add exact hit timing and cursor measurements.";

        var details = new List<string>();
        if (mechanics.MeanTimingOffsetMilliseconds is { } offset)
            details.Add($"hits average {formatSignedMilliseconds(offset)}");
        if (mechanics.TimingStandardDeviationMilliseconds is { } spread)
            details.Add($"timing spread {spread:0.0} ms");
        if (mechanics.MeanCursorDistancePlayfieldUnits is { } distance)
            details.Add($"cursor error {distance:0.0} px");
        if (mechanics.ExactMissCount > 0)
            details.Add($"{mechanics.ExactMissCount:N0} exact misses");
        if (mechanics.DominantMissReason is { } dominant)
        {
            int count = mechanics.MissReasonCounts?.GetValueOrDefault(dominant) ?? 0;
            details.Add($"most common cause {ReplayMissInsightPresenter.Label(dominant)} ({count:N0})");
        }
        return details.Count == 0 ? $"{mechanics.JudgementCount:N0} exact judgements measured." : string.Join(", ", details) + ".";
    }

    internal static string MechanicsValue(CoachingMechanicsProfile mechanics) =>
        mechanics.DominantMissReason is { } dominant
            ? ReplayMissInsightPresenter.Label(dominant)
            : mechanics.WeakestMapSegment ?? $"{mechanics.ExactAnalysisRunCount:N0} exact runs";

    internal static string ProfileCoverageValue(GlobalCoachingProfile profile) => $"{profile.Coverage.ReplayCoverage:P0}";

    internal static string ProfileEvidenceSummary(GlobalCoachingProfile profile) => profile.MissReasons.Count == 0
        ? "No classified misses in analysed replays yet"
        : string.Join("  /  ", profile.MissReasons.Take(4)
            .Select(reason => $"{ReplayMissInsightPresenter.Label(reason.Reason)} {reason.Share:P0}"));

    internal static string ProfileTendencySummary(GlobalCoachingProfile profile) =>
        $"Timing: {profile.TimingTendency}  /  Aim: {profile.AimTendency}";

    internal static string ConfidenceLabel(CoachingConfidence confidence) => confidence switch
    {
        CoachingConfidence.High => "High",
        CoachingConfidence.Medium => "Medium",
        CoachingConfidence.Low => "Low",
        _ => "Building",
    };

    private static string formatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours}h {duration.Minutes:N0}m"
        : $"{Math.Max(1, (int)Math.Ceiling(duration.TotalMinutes)):N0} min";

    private static string formatSignedMilliseconds(double value) => $"{value:+0.0;-0.0;0.0} ms";

    private static double standardDeviation(IEnumerable<double> values)
    {
        double[] samples = values.Where(double.IsFinite).ToArray();
        if (samples.Length == 0)
            return 0;
        double mean = samples.Average();
        return Math.Sqrt(samples.Average(value => Math.Pow(value - mean, 2)));
    }

    private void sourceChanged()
    {
        if (!IsDisposed)
            Schedule(load);
    }

    protected override void Dispose(bool isDisposing)
    {
        loading?.Cancel();
        loading?.Dispose();
        practiceGeneration?.Cancel();
        practiceGeneration?.Dispose();
        practiceLaunch?.Cancel();
        practiceLaunch?.Dispose();
        scheduledPracticeRefresh?.Cancel();
        scheduledPracticeRefresh = null;
        if (sourceChanges is not null)
            sourceChanges.SourceChanged -= sourceChanged;
        base.Dispose(isDisposing);
    }

    private readonly record struct PracticeDisplayState(
        bool AcceptingAnalysisProgress,
        bool CreatingPracticeMap,
        bool OpeningPracticeMap,
        bool PracticeSucceeded,
        string Message,
        string? Directory,
        bool HasLazerArchive);

    private static OsuSpriteText label(string text, float size, Colour4 colour, string weight = "Regular") => new()
    {
        Text = text,
        Font = new FontUsage(size: size, weight: weight),
        Colour = colour,
    };

    private static TruncatingSpriteText truncatingLabel(string text, float size, Colour4 colour, float maxWidth, string weight = "Regular") => new()
    {
        Text = text,
        Font = new FontUsage(size: size, weight: weight),
        Colour = colour,
        MaxWidth = maxWidth,
    };

    private static OsuTextFlowContainer flow(string text, float size, Colour4 colour, string weight = "Regular") => new(sprite =>
    {
        sprite.Font = new FontUsage(size: size, weight: weight);
        sprite.Colour = colour;
    })
    {
        RelativeSizeAxes = Axes.X,
        AutoSizeAxes = Axes.Y,
        Text = text,
    };

    internal static string AnalysisProgressDetail(int completed, int total, int cached) =>
        $"{Math.Clamp(completed, 0, Math.Max(0, total)):N0} of {Math.Max(0, total):N0} in this pass  //  {Math.Max(0, cached):N0} already analysed";

    internal static string AnalysisCompletionDetail(int cached, int failed) => failed > 0
        ? $"{Math.Max(0, cached):N0} analysed  //  {failed:N0} could not be read"
        : $"{Math.Max(0, cached):N0} analysed  //  coaching profile updated";

    internal static string PracticeEvidenceSummary(PracticeMapCandidate candidate)
    {
        string attempts = candidate.AttemptsWithMisses > 0
            ? $"{candidate.AttemptsWithMisses:N0}/{candidate.AnalysedAttempts:N0} miss attempts"
            : $"{candidate.AnalysedAttempts:N0} analysed {(candidate.AnalysedAttempts == 1 ? "attempt" : "attempts")}";
        string confidence = candidate.AverageMissConfidence > 0 ? $"  //  {candidate.AverageMissConfidence:P0} confidence" : string.Empty;
        return $"{candidate.SourceReplay.StarRating:0.00}*  //  {candidate.MissCount:N0} exact {(candidate.MissCount == 1 ? "miss" : "misses")}  //  {attempts}{confidence}";
    }

    internal static string PracticeSourceSummary(PracticeMapCandidate candidate) =>
        $"Source difficulty: {candidate.SourceReplay.Difficulty}  //  last played {candidate.SourceReplay.PlayedAt.ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture)}";

    internal static string PracticeSortLabel(PracticeCandidateSort value) => value switch
    {
        PracticeCandidateSort.MostRepeated => "Most repeated",
        PracticeCandidateSort.MostExactMisses => "Most exact misses",
        PracticeCandidateSort.RecentlyPlayed => "Recently played",
        PracticeCandidateSort.HardestFirst => "Hardest first",
        PracticeCandidateSort.EasiestFirst => "Easiest first",
        PracticeCandidateSort.Title => "Title",
        _ => "Weakest first",
    };

    internal static string PracticeEvidenceLabel(PracticeEvidenceFilter value) => value switch
    {
        PracticeEvidenceFilter.RepeatedAcrossAttempts => "Repeated misses",
        PracticeEvidenceFilter.HighConfidence => "High confidence",
        PracticeEvidenceFilter.ThreePlusMisses => "3+ exact misses",
        PracticeEvidenceFilter.FivePlusMisses => "5+ exact misses",
        _ => "Any evidence",
    };

    internal static string PracticeCandidateDetail(PracticeCandidatePage page) => page switch
    {
        { Available: <= 0 } => "No maps ready",
        { Total: 0 } => $"0 of {page.Available:N0} maps",
        { Total: 1, Available: 1 } => "1 practice map ready",
        _ when page.Total < page.Available => $"{page.Items.Count:N0} of {page.Total:N0} matching",
        _ when page.Items.Count < page.Total => $"Top {page.Items.Count:N0} of {page.Total:N0} maps",
        _ => $"{page.Total:N0} practice maps ready",
    };

    internal static bool PracticeLaunchSucceeded(LazerBeatmapInstallStatus status) =>
        status is LazerBeatmapInstallStatus.Sent or LazerBeatmapInstallStatus.LazerStarted;

    internal static string PracticeLaunchMessage(LazerBeatmapInstallStatus status) => status switch
    {
        LazerBeatmapInstallStatus.Sent => "The drill was sent to osu!lazer. It will appear after import completes.",
        LazerBeatmapInstallStatus.LazerStarted => "osu!lazer opened and is importing the drill.",
        LazerBeatmapInstallStatus.ArchiveUnavailable => "The preserved .osz is unavailable. Open the export folder to import it manually.",
        LazerBeatmapInstallStatus.LazerNotFound => "osu!lazer was not found. Open the export folder to import the .osz manually.",
        LazerBeatmapInstallStatus.LazerRejected => "osu!lazer did not accept the drill. Open the export folder to import it manually.",
        _ => "AimMod could not open osu!lazer. Open the export folder to import the .osz manually.",
    };

    private partial class AnalysisProgressBanner : CompositeDrawable
    {
        private readonly Box accent;
        private readonly Box progressFill;
        private readonly SpriteIcon icon;
        private readonly OsuSpriteText phase;
        private readonly TruncatingSpriteText title;
        private readonly TruncatingSpriteText detail;

        public AnalysisProgressBanner()
        {
            RelativeSizeAxes = Axes.X;
            Height = 64;
            Masking = true;
            CornerRadius = AimModVisualStyle.ControlRadius;
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                accent = new Box { RelativeSizeAxes = Axes.Y, Width = 3, Colour = AimModPalette.Cyan },
                icon = new SpriteIcon
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Position = new(16, -1),
                    Size = new(16),
                    Icon = FontAwesome.Solid.ChartLine,
                    Colour = AimModPalette.Cyan,
                },
                new FillFlowContainer
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    AutoSizeAxes = Axes.Y,
                    RelativeSizeAxes = Axes.X,
                    Width = 0.56f,
                    Margin = new MarginPadding { Left = 42 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(2),
                    Children = new Drawable[]
                    {
                        phase = label("LOADING HISTORY", 9, AimModPalette.Cyan, "Bold"),
                        title = truncatingLabel("Building your global profile", 14, AimModPalette.Text, 520, "SemiBold"),
                    },
                },
                detail = truncatingLabel("Reading local and submitted plays", 11, AimModPalette.Muted, 460).With(text =>
                {
                    text.Anchor = Anchor.CentreRight;
                    text.Origin = Anchor.CentreRight;
                    text.Margin = new MarginPadding { Right = 18 };
                }),
                new Container
                {
                    Anchor = Anchor.BottomLeft,
                    Origin = Anchor.BottomLeft,
                    RelativeSizeAxes = Axes.X,
                    Height = 3,
                    Children = new Drawable[]
                    {
                        new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Border },
                        progressFill = new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Width = 0.12f,
                            Colour = AimModPalette.Cyan,
                        },
                    },
                },
            };
        }

        public void ShowHistoryLoading() => set(
            "LOADING HISTORY",
            "Building your global profile",
            "Reading local and submitted plays",
            0.12f,
            AimModPalette.Cyan,
            FontAwesome.Solid.ChartLine);

        public void ShowStarting(int cached) => set(
            "ANALYSING REPLAYS",
            "Preparing the next replay",
            $"{Math.Max(0, cached):N0} replay analyses already available",
            0.04f,
            AimModPalette.Cyan,
            FontAwesome.Solid.CircleNotch);

        public void ShowAnalysing(int completed, int total, string currentTitle, int cached)
        {
            float progress = total <= 0 ? 0 : Math.Clamp(completed / (float)total, 0, 1);
            set(
                "ANALYSING REPLAYS",
                string.IsNullOrWhiteSpace(currentTitle) ? "Reading replay judgements" : currentTitle,
                AnalysisProgressDetail(completed, total, cached),
                progress,
                AimModPalette.Cyan,
                FontAwesome.Solid.CircleNotch);
        }

        public void ShowReady(GlobalCoachingProfile profile, int merged, int submitted, int total)
        {
            if (merged == 0)
            {
                set(
                    "NO PLAY HISTORY",
                    "No osu!standard plays found",
                    "Play a map or connect an osu! account to begin coaching.",
                    0,
                    AimModPalette.Pink,
                    FontAwesome.Solid.ExclamationCircle);
                return;
            }

            int cached = profile.Coverage.AnalysedRunCount;
            set(
                cached > 0 ? "GLOBAL PROFILE READY" : "REPLAY ANALYSIS READY",
                cached > 0
                    ? $"{cached:N0} replays analysed across {profile.Coverage.AnalysedMapCount:N0} maps"
                    : $"{merged:N0} plays loaded for coaching",
                cached > 0
                    ? $"{ConfidenceLabel(profile.Coverage.Confidence)} confidence  //  {ProfileCoverageValue(profile)} replay coverage"
                    : $"{profile.Coverage.ReplayAvailableRunCount:N0} saved replays  //  {submitted:N0} submitted scores  //  {total:N0} records",
                cached > 0 ? 1 : 0,
                cached > 0 ? AimModPalette.Success : AimModPalette.Yellow,
                cached > 0 ? FontAwesome.Solid.CheckCircle : FontAwesome.Solid.Clock);
        }

        public void ShowComplete(GlobalCoachingProfile profile, int completed, int failed) => set(
            "GLOBAL PROFILE UPDATED",
            completed > 0
                ? $"Added {completed:N0} new replay {(completed == 1 ? "analysis" : "analyses")}"
                : "Replay analysis is up to date",
            AnalysisCompletionDetail(profile.Coverage.AnalysedRunCount, failed),
            1,
            failed > 0 && completed == 0 ? AimModPalette.Yellow : AimModPalette.Success,
            failed > 0 && completed == 0 ? FontAwesome.Solid.ExclamationCircle : FontAwesome.Solid.CheckCircle);

        public void ShowWarning(string titleText, string detailText) => set(
            "LIMITED DATA",
            titleText,
            detailText,
            1,
            AimModPalette.Yellow,
            FontAwesome.Solid.ExclamationCircle);

        public void ShowError(string titleText, string detailText) => set(
            "ANALYSIS PAUSED",
            titleText,
            detailText,
            1,
            AimModPalette.Pink,
            FontAwesome.Solid.ExclamationCircle);

        private void set(string phaseText, string titleText, string detailText, float progress, Colour4 colour, IconUsage iconUsage)
        {
            phase.Text = phaseText;
            phase.Colour = colour;
            title.Text = titleText;
            detail.Text = detailText;
            accent.Colour = colour;
            progressFill.Colour = colour;
            progressFill.ResizeWidthTo(Math.Clamp(progress, 0, 1), 180, Easing.OutQuint);
            icon.Icon = iconUsage;
            icon.Colour = colour;
        }

        protected override void Update()
        {
            base.Update();
            title.MaxWidth = Math.Max(180, DrawWidth * 0.5f - 64);
            detail.MaxWidth = Math.Max(160, DrawWidth * 0.4f - 28);
        }
    }

    private partial class GlobalSkillProfileGrid : CompositeDrawable
    {
        public GlobalSkillProfileGrid(GlobalCoachingProfile profile)
        {
            RelativeSizeAxes = Axes.X;
            Height = 126;
            GlobalMissReasonShare? miss = profile.MissReasons.FirstOrDefault();
            GlobalSkillAreaEvidence? focusArea = profile.MeasuredSkillAreas.FirstOrDefault();
            InternalChild = new GridContainer
            {
                RelativeSizeAxes = Axes.Both,
                RowDimensions = new[]
                {
                    new Dimension(GridSizeMode.Relative, 0.5f),
                    new Dimension(GridSizeMode.Relative, 0.5f),
                },
                ColumnDimensions = new[]
                {
                    new Dimension(GridSizeMode.Relative, 0.333f),
                    new Dimension(GridSizeMode.Relative, 0.334f),
                    new Dimension(GridSizeMode.Relative, 0.333f),
                },
                Content = new[]
                {
                    new Drawable[]
                    {
                        new ProfileMetric("TIMING", profile.TimingTendency, profile.TimingDetail, AimModPalette.Cyan),
                        new ProfileMetric("AIM", profile.AimTendency, profile.AimDetail, AimModPalette.Pink),
                        new ProfileMetric(
                            "MISS CAUSE",
                            miss is null ? "Collecting" : ReplayMissInsightPresenter.Label(miss.Reason),
                            miss is null
                                ? "No classified misses yet"
                                : $"{miss.Count:N0} classified  //  {miss.Share:P0}  //  {ConfidenceLabel(miss.Confidence)} confidence",
                            AimModPalette.Yellow),
                    },
                    new Drawable[]
                    {
                        new ProfileMetric(
                            "COVERAGE",
                            ProfileCoverageValue(profile),
                            $"{profile.Coverage.AnalysedRunCount:N0} of {profile.Coverage.ReplayAvailableRunCount:N0} saved replays",
                            AimModPalette.Success),
                        new ProfileMetric(
                            "CONFIDENCE",
                            ConfidenceLabel(profile.Coverage.Confidence),
                            $"{profile.Coverage.AnalysedMapCount:N0} analysed maps",
                            AimModPalette.Yellow),
                        new ProfileMetric(
                            "FOCUS AREA",
                            focusArea?.Label ?? "Collecting",
                            focusArea is null
                                ? $"{profile.Coverage.JudgementCount:N0} exact judgements"
                                : $"{focusArea.EvidenceCount:N0} misses  //  {focusArea.MapCount:N0} maps  //  {ConfidenceLabel(focusArea.Confidence)}",
                            AimModPalette.Cyan),
                    },
                },
            };
        }
    }

    private partial class ProfileMetric : CompositeDrawable
    {
        private readonly TruncatingSpriteText value;
        private readonly TruncatingSpriteText detail;

        public ProfileMetric(string titleText, string valueText, string detailText, Colour4 accentColour)
        {
            RelativeSizeAxes = Axes.Both;
            Padding = new MarginPadding(3);
            InternalChild = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Masking = true,
                CornerRadius = AimModVisualStyle.ControlRadius,
                Children = new Drawable[]
                {
                    new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                    new Box { RelativeSizeAxes = Axes.Y, Width = 3, Colour = accentColour },
                    label(titleText, 8, AimModPalette.Muted, "Bold").With(text => text.Position = new(11, 6)),
                    value = truncatingLabel(valueText, 13, accentColour, 160, "Bold").With(text => text.Position = new(11, 20)),
                    detail = truncatingLabel(detailText, 10, AimModPalette.Muted, 160).With(text => text.Position = new(11, 39)),
                },
            };
        }

        protected override void Update()
        {
            base.Update();
            value.MaxWidth = detail.MaxWidth = Math.Max(60, DrawWidth - 28);
        }
    }

    private partial class InsightRow : CompositeDrawable
    {
        public InsightRow(string title, string detail, string value, Colour4 accent)
        {
            RelativeSizeAxes = Axes.X;
            Height = 73;
            InternalChildren = new Drawable[]
            {
                new Box
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Size = new(3, 48),
                    Colour = accent,
                },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding { Left = 14, Right = 102, Top = 7 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(4),
                    Children = new Drawable[]
                    {
                        label(title, 13, AimModPalette.Text, "SemiBold"),
                        flow(detail, 10, AimModPalette.Muted),
                    },
                },
                label(value, 13, accent, "Bold").With(text =>
                {
                    text.Anchor = Anchor.TopRight;
                    text.Origin = Anchor.TopRight;
                    text.Margin = new MarginPadding { Top = 8, Right = 2 };
                }),
                new Box
                {
                    Anchor = Anchor.BottomLeft,
                    Origin = Anchor.BottomLeft,
                    RelativeSizeAxes = Axes.X,
                    Height = 1,
                    Colour = AimModPalette.Border,
                    Alpha = 0.65f,
                },
            };
        }
    }

    private partial class SelectedRunCard : CompositeDrawable
    {
        public SelectedRunCard(LocalReplay run, CoachingAccuracyPrediction? prediction, Action showGlobal, Action? open)
        {
            RelativeSizeAxes = Axes.X;
            Height = 48;
            Masking = true;
            CornerRadius = AimModVisualStyle.ControlRadius;
            InternalChildren = new Drawable[]
            {
                new AimModLocalArtwork(run.BackgroundPath),
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = ColourInfo.GradientHorizontal(AimModPalette.Canvas.Opacity(0.92f), AimModPalette.Panel.Opacity(0.82f)),
                },
                new Box
                {
                    RelativeSizeAxes = Axes.Y,
                    Width = 5,
                    Colour = AimModVisualStyle.DifficultyColour(run.StarRating),
                },
                new FillFlowContainer
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Width = 1,
                    Margin = new MarginPadding { Left = 14 },
                    Padding = new MarginPadding { Right = 260 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(2),
                    Children = new Drawable[]
                    {
                        truncatingLabel($"{run.Title} [{run.Difficulty}]", 13, AimModPalette.Text, 520, "Bold"),
                        truncatingLabel($"{run.Accuracy:P2}  //  {run.MissCount:N0} misses  //  {run.PlayedAt:MMM d, yyyy}{formatPpSuffix(run.PerformancePoints)}", 10, AimModPalette.Cyan, 520, "SemiBold"),
                    },
                },
                new FillFlowContainer
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    AutoSizeAxes = Axes.Both,
                    Margin = new MarginPadding { Right = 9 },
                    Direction = FillDirection.Horizontal,
                    Spacing = new(6),
                    Children = new Drawable[]
                    {
                        new AimModDifficultyPill(run.StarRating),
                        new ActionButton("Global", showGlobal),
                        new ActionButton(open is null ? "No replay" : "Replay", open),
                    },
                },
            };
        }
    }

    private partial class GlobalEvidenceStrip : CompositeDrawable
    {
        public GlobalEvidenceStrip(GlobalCoachingProfile profile)
        {
            RelativeSizeAxes = Axes.X;
            Height = 66;

            GlobalMissReasonShare[] reasons = profile.MissReasons.Take(4).ToArray();
            var bar = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                Height = 8,
                Direction = FillDirection.Horizontal,
                Spacing = new(2),
            };
            if (reasons.Length == 0)
            {
                bar.Add(new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Border });
            }
            else
            {
                Colour4[] colours = { AimModPalette.Pink, AimModPalette.Cyan, AimModPalette.Yellow, AimModPalette.Success };
                double visibleTotal = reasons.Sum(item => item.Share);
                for (int i = 0; i < reasons.Length; i++)
                {
                    bar.Add(new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Width = visibleTotal <= 0 ? 1f / reasons.Length : (float)(reasons[i].Share / visibleTotal),
                        Colour = colours[i % colours.Length],
                    });
                }
            }

            string missSummary = ProfileEvidenceSummary(profile);
            InternalChildren = new Drawable[]
            {
                bar,
                truncatingLabel(missSummary, 10, AimModPalette.Muted, 760).With(text => text.Y = 14),
                truncatingLabel(ProfileTendencySummary(profile), 11, AimModPalette.Text, 760, "SemiBold")
                    .With(text => text.Y = 38),
            };
        }
    }

    private partial class SectionLine : AimModSubsectionHeader
    {
        public SectionLine(string titleText, string detailText)
            : base(titleText, detailText)
        {
        }

        public void SetDetail(string value) => Detail = value;
    }

    private sealed partial class PracticeDropdown<T> : OsuDropdown<T>
        where T : struct, Enum
    {
        private readonly Func<T, string> formatter;

        public PracticeDropdown(Func<T, string> formatter)
        {
            this.formatter = formatter;
        }

        protected override LocalisableString GenerateItemText(T item) => formatter(item);
    }

    private static string formatPpSuffix(double? pp) =>
        pp is { } value && double.IsFinite(value) && value > 0 ? $"  //  {value:0.0}pp" : string.Empty;

    private partial class RecommendationCard : CompositeDrawable
    {
        public RecommendationCard(CoachingRecommendation recommendation, Action? open)
        {
            RelativeSizeAxes = Axes.X;
            Height = 146;
            Masking = true;
            CornerRadius = AimModVisualStyle.CardRadius;
            InternalChildren = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = ColourInfo.GradientHorizontal(AimModPalette.PanelRaised, AimModPalette.PinkDark),
                },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding(14),
                    Direction = FillDirection.Vertical,
                    Spacing = new(5),
                    Children = new Drawable[]
                    {
                        label(recommendation.Intent.ToUpperInvariant(), 9, AimModPalette.Pink, "Bold"),
                        truncatingLabel($"{recommendation.Title} [{recommendation.Difficulty}]", 15, AimModPalette.Text, 440, "Bold"),
                        flow(recommendation.Reason, 10, AimModPalette.Muted),
                    },
                },
                new FillFlowContainer
                {
                    Anchor = Anchor.BottomRight,
                    Origin = Anchor.BottomRight,
                    AutoSizeAxes = Axes.Both,
                    Margin = new MarginPadding { Right = 12, Bottom = 11 },
                    Direction = FillDirection.Horizontal,
                    Spacing = new(9),
                    Children = new Drawable[]
                    {
                        label(recommendation.ExpectedAccuracy is { } expected ? $"Expected {expected:P1}" : confidenceText(recommendation.Confidence), 10, AimModPalette.Cyan, "SemiBold"),
                        new ActionButton(open is null ? "Run unavailable" : "Open run", open),
                    },
                },
            };
        }

        private static string confidenceText(CoachingConfidence confidence) => confidence switch
        {
            CoachingConfidence.High => "High confidence",
            CoachingConfidence.Medium => "Medium confidence",
            CoachingConfidence.Low => "Low confidence",
            _ => "More plays needed",
        };
    }

    private partial class RunPickerRow : ClickableContainer
    {
        private readonly Action select;
        private readonly Box background;
        private readonly Colour4 restingColour;

        public RunPickerRow(CoachingRecentRun run, bool selected, Action select)
        {
            this.select = select;
            restingColour = selected ? AimModPalette.PanelRaised : AimModPalette.Panel;
            RelativeSizeAxes = Axes.X;
            Height = 64;
            Masking = true;
            CornerRadius = AimModVisualStyle.ControlRadius;
            Children = new Drawable[]
            {
                background = new Box { RelativeSizeAxes = Axes.Both, Colour = restingColour },
                new Box
                {
                    RelativeSizeAxes = Axes.Y,
                    Width = selected ? 4 : 3,
                    Colour = AimModVisualStyle.DifficultyColour(run.StarRating),
                },
                new FillFlowContainer
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Width = 1,
                    Margin = new MarginPadding { Left = 17 },
                    Padding = new MarginPadding { Right = 280 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(2),
                    Children = new Drawable[]
                    {
                        truncatingLabel($"{run.Title} [{run.Difficulty}]", 14, AimModPalette.Text, 520, "SemiBold"),
                        truncatingLabel($"{run.Artist}  //  {run.PlayedAt:MMM d, HH:mm}  //  {formatMods(run.Mods)}", 10, AimModPalette.Muted, 520),
                    },
                },
                new FillFlowContainer
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    AutoSizeAxes = Axes.Both,
                    Margin = new MarginPadding { Right = 16 },
                    Direction = FillDirection.Horizontal,
                    Spacing = new(12),
                    Children = new Drawable[]
                    {
                        label($"{run.Accuracy:P2}", 13, AimModPalette.Cyan, "Bold"),
                        label($"{run.MissCount:N0} miss", 11, run.MissCount == 0 ? AimModPalette.Success : AimModPalette.Muted),
                        new AimModDifficultyPill(run.StarRating),
                        label(selected ? "Selected" : "Inspect", 11, selected ? AimModPalette.Pink : AimModPalette.Muted, "SemiBold"),
                    },
                },
            };
        }

        protected override bool OnClick(ClickEvent e)
        {
            select();
            return true;
        }

        protected override bool OnHover(HoverEvent e)
        {
            background.FadeColour(AimModPalette.PanelHover, 100);
            return true;
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            background.FadeColour(restingColour, AimModVisualStyle.HoverTransition);
            base.OnHoverLost(e);
        }

        private static string formatMods(IReadOnlyList<string> mods) => mods.Count == 0 ? "No Mod" : string.Join(' ', mods);
    }

    private partial class ActionButton : ClickableContainer
    {
        private readonly Action? action;
        private readonly Box background;

        public ActionButton(string text, Action? action)
        {
            this.action = action;
            AutoSizeAxes = Axes.Both;
            Masking = true;
            CornerRadius = AimModVisualStyle.ControlRadius;
            Alpha = action is null ? 0.5f : 1;
            Children = new Drawable[]
            {
                background = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = action is null ? AimModPalette.PanelHover : AimModPalette.PinkDark,
                },
                new FillFlowContainer
                {
                    AutoSizeAxes = Axes.Both,
                    Padding = new MarginPadding { Horizontal = 11, Vertical = 6 },
                    Child = label(text, 10, AimModPalette.Text, "SemiBold"),
                },
            };
        }

        protected override bool OnClick(ClickEvent e)
        {
            action?.Invoke();
            return action is not null;
        }

        protected override bool OnHover(HoverEvent e)
        {
            if (action is not null)
                background.FadeColour(AimModPalette.Pink, 90);
            return action is not null;
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            background.FadeColour(action is null ? AimModPalette.PanelHover : AimModPalette.PinkDark, 90);
            base.OnHoverLost(e);
        }
    }

    private partial class PracticeCandidateRow : CompositeDrawable
    {
        private readonly TruncatingSpriteText title;
        private readonly TruncatingSpriteText evidence;
        private readonly TruncatingSpriteText source;

        public PracticeCandidateRow(PracticeMapCandidate candidate, Action<PracticeMapCandidate, PracticeDrillType> create)
        {
            RelativeSizeAxes = Axes.X;
            Height = 124;
            Masking = true;
            CornerRadius = AimModVisualStyle.ControlRadius;
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                new Box
                {
                    RelativeSizeAxes = Axes.Y,
                    Width = 3,
                    Colour = AimModPalette.Pink,
                },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding { Left = 13, Right = 72, Top = 9 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(3),
                    Children = new Drawable[]
                    {
                        title = truncatingLabel($"{candidate.SourceReplay.Title} [{candidate.SourceReplay.Difficulty}]", 13, AimModPalette.Text, 520, "SemiBold"),
                        evidence = truncatingLabel(
                            PracticeEvidenceSummary(candidate),
                            11,
                            AimModPalette.Cyan,
                            520,
                            "SemiBold"),
                        source = truncatingLabel(
                            PracticeSourceSummary(candidate),
                            11,
                            AimModPalette.Muted,
                            520),
                    },
                },
                new AimModDifficultyPill(candidate.SourceReplay.StarRating)
                {
                    Anchor = Anchor.TopRight,
                    Origin = Anchor.TopRight,
                    Margin = new MarginPadding { Top = 9, Right = 9 },
                },
                new FillFlowContainer
                {
                    Anchor = Anchor.BottomLeft,
                    Origin = Anchor.BottomLeft,
                    AutoSizeAxes = Axes.Both,
                    Margin = new MarginPadding { Left = 13, Bottom = 9 },
                    Direction = FillDirection.Horizontal,
                    Spacing = new(6),
                    Children = new Drawable[]
                    {
                        new ActionButton("Jumps", () => create(candidate, PracticeDrillType.LongJumps)),
                        new ActionButton("Streams", () => create(candidate, PracticeDrillType.Streams)),
                        new ActionButton("Mixed", () => create(candidate, PracticeDrillType.Mixed)),
                    },
                },
            };
        }

        protected override void Update()
        {
            base.Update();
            title.MaxWidth = Math.Max(120, DrawWidth - 98);
            evidence.MaxWidth = source.MaxWidth = Math.Max(120, DrawWidth - 30);
        }
    }

    private partial class PracticeStatusRow : CompositeDrawable
    {
        public PracticeStatusRow(
            string titleText,
            string detailText,
            Colour4 accentColour,
            IconUsage iconUsage,
            string? actionLabel = null,
            Action? action = null)
        {
            RelativeSizeAxes = Axes.X;
            Height = 64;
            Masking = true;
            CornerRadius = AimModVisualStyle.ControlRadius;
            var children = new List<Drawable>
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                new Box { RelativeSizeAxes = Axes.Y, Width = 3, Colour = accentColour },
                new SpriteIcon
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Position = new(15, 0),
                    Size = new(15),
                    Icon = iconUsage,
                    Colour = accentColour,
                },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding { Left = 40, Right = action is null ? 14 : 118, Top = 8 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(3),
                    Children = new Drawable[]
                    {
                        label(titleText, 12, AimModPalette.Text, "SemiBold"),
                        truncatingLabel(detailText, 11, AimModPalette.Muted, 680),
                    },
                },
            };
            if (action is not null && actionLabel is not null)
            {
                children.Add(new ActionButton(actionLabel, action)
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    Margin = new MarginPadding { Right = 12 },
                });
            }

            InternalChildren = children.ToArray();
        }
    }

    private partial class PracticeEmptyState : CompositeDrawable
    {
        public PracticeEmptyState(string titleText, string detailText)
        {
            RelativeSizeAxes = Axes.X;
            Height = 132;
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas, Alpha = 0.25f },
                new FillFlowContainer
                {
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.TopCentre,
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Y = 42,
                    Padding = new MarginPadding { Horizontal = 24 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(4),
                    Children = new Drawable[]
                    {
                        label(titleText, 13, AimModPalette.Text, "SemiBold").With(text =>
                        {
                            text.Anchor = Anchor.TopCentre;
                            text.Origin = Anchor.TopCentre;
                        }),
                        flow(detailText, 11, AimModPalette.Muted).With(text => text.TextAnchor = Anchor.TopCentre),
                    },
                },
            };
        }
    }

    private partial class CoachingTrendChart : CompositeDrawable
    {
        private readonly LineGraph graph;
        private readonly Container markers;
        private readonly Container missBars;
        private readonly OsuSpriteText upperLabel;
        private readonly OsuSpriteText lowerLabel;
        private readonly OsuSpriteText timeRange;

        public CoachingTrendChart()
        {
            RelativeSizeAxes = Axes.X;
            Height = 88;
            Masking = true;
            CornerRadius = AimModVisualStyle.ControlRadius;
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas, Alpha = 0.38f },
                new Box
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 1,
                    Y = 18,
                    Colour = AimModPalette.Border,
                },
                new Box
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 1,
                    Y = 48,
                    Colour = AimModPalette.Border,
                },
                new Box
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 1,
                    Y = 72,
                    Colour = AimModPalette.Border,
                },
                graph = new LineGraph
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 70,
                    Padding = new MarginPadding { Top = 9, Bottom = 8, Left = 34, Right = 12 },
                    LineColour = AimModPalette.Pink,
                    DefaultValueCount = NativeCoachingWorkspaceModel.MaximumTrendRuns,
                },
                markers = new Container
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 52,
                    Position = new(34, 11),
                    Width = -46,
                },
                upperLabel = label("100%", 9, AimModPalette.Muted).With(text => text.Position = new(4, 8)),
                lowerLabel = label("80%", 9, AimModPalette.Muted).With(text => text.Position = new(4, 55)),
                label("MISS", 8, AimModPalette.Muted, "Bold").With(text => text.Position = new(4, 70)),
                missBars = new Container
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 12,
                    Position = new(34, 70),
                    Width = -46,
                },
                timeRange = label("No recent plays", 9, AimModPalette.Muted).With(text =>
                {
                    text.Anchor = Anchor.BottomRight;
                    text.Origin = Anchor.BottomRight;
                    text.Margin = new MarginPadding { Right = 7, Bottom = 3 };
                }),
            };
        }

        public void SetRuns(IReadOnlyList<LocalReplay> runs, Guid? selectedScoreId, Action<Guid> select)
        {
            LocalReplay[] chronological = runs.Where(run => double.IsFinite(run.Accuracy))
                                              .OrderBy(run => run.PlayedAt)
                                              .TakeLast(NativeCoachingWorkspaceModel.MaximumTrendRuns)
                                              .ToArray();
            markers.Clear();
            missBars.Clear();
            if (chronological.Length == 0)
            {
                graph.Alpha = 0;
                timeRange.Text = "No recent plays";
                return;
            }

            double minimum = Math.Max(0, Math.Floor((chronological.Min(run => run.Accuracy * 100) - 2) / 5) * 5);
            double maximum = Math.Min(100, Math.Ceiling((chronological.Max(run => run.Accuracy * 100) + 1) / 5) * 5);
            if (maximum - minimum < 5)
                minimum = Math.Max(0, maximum - 5);

            graph.MinValue = (float)minimum;
            graph.MaxValue = (float)maximum;
            graph.Values = chronological.Select(run => (float)(run.Accuracy * 100)).ToArray();
            graph.FadeIn(120);
            upperLabel.Text = $"{maximum:0}%";
            lowerLabel.Text = $"{minimum:0}%";
            timeRange.Text = chronological.Length == 1
                ? chronological[0].PlayedAt.ToString("MMM d, HH:mm")
                : $"{chronological[0].PlayedAt:MMM d} to {chronological[^1].PlayedAt:MMM d}";

            int maximumMisses = Math.Max(1, chronological.Max(run => run.MissCount));
            for (int i = 0; i < chronological.Length; i++)
            {
                LocalReplay run = chronological[i];
                float x = chronological.Length == 1 ? 0.5f : 0.02f + 0.96f * i / (chronological.Length - 1);
                float y = (float)(1 - (run.Accuracy * 100 - minimum) / (maximum - minimum));
                markers.Add(new TrendPoint(
                    run.ScoreId == selectedScoreId,
                    AimModVisualStyle.DifficultyColour(run.StarRating),
                    () => select(run.ScoreId))
                {
                    RelativePositionAxes = Axes.Both,
                    Position = new(x, Math.Clamp(y, 0.02f, 0.98f)),
                    Anchor = Anchor.TopLeft,
                    Origin = Anchor.Centre,
                });

                float barX = chronological.Length == 1 ? 0.5f : (float)i / (chronological.Length - 1);
                missBars.Add(new Box
                {
                    RelativePositionAxes = Axes.X,
                    X = barX,
                    Anchor = Anchor.BottomLeft,
                    Origin = Anchor.BottomCentre,
                    Width = Math.Clamp(150f / chronological.Length, 3, 8),
                    Height = run.MissCount == 0 ? 1 : 2 + 9f * run.MissCount / maximumMisses,
                    Colour = run.MissCount == 0 ? AimModPalette.Success : AimModPalette.Pink,
                    Alpha = run.ScoreId == selectedScoreId ? 1 : 0.62f,
                });
            }
        }
    }

    private partial class WorkspacePanel : Container
    {
        private readonly Container content;

        protected override Container<Drawable> Content => content;

        public WorkspacePanel(MarginPadding padding)
        {
            RelativeSizeAxes = Axes.Both;
            Masking = true;
            CornerRadius = AimModVisualStyle.CardRadius;
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                content = new Container { RelativeSizeAxes = Axes.Both, Padding = padding },
            };
        }
    }

    private partial class TrendPoint : ClickableContainer
    {
        private readonly Action action;
        private readonly CircularContainer circle;

        public TrendPoint(bool selected, Colour4 colour, Action action)
        {
            this.action = action;
            Size = new(selected ? 15 : 10);
            circle = new CircularContainer
            {
                RelativeSizeAxes = Axes.Both,
                Masking = true,
                BorderThickness = selected ? 2 : 0,
                BorderColour = AimModPalette.Text,
                Child = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = colour,
                },
            };
            Child = circle;
        }

        protected override bool OnClick(ClickEvent e)
        {
            action();
            return true;
        }

        protected override bool OnHover(HoverEvent e)
        {
            circle.ScaleTo(1.35f, 80);
            return true;
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            circle.ScaleTo(1, 80);
            base.OnHoverLost(e);
        }
    }
}

internal sealed class PracticeCandidatePoolCache
{
    private readonly int limit;
    private readonly Func<IEnumerable<LocalReplay>, IReadOnlyDictionary<Guid, ReplayAnalysisResult>, int, IReadOnlyList<PracticeMapCandidate>> build;
    private IReadOnlyList<PracticeMapCandidate>? cached;

    public PracticeCandidatePoolCache(
        int limit,
        Func<IEnumerable<LocalReplay>, IReadOnlyDictionary<Guid, ReplayAnalysisResult>, int, IReadOnlyList<PracticeMapCandidate>>? build = null)
    {
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit));

        this.limit = limit;
        this.build = build ?? PracticeMapCandidateBuilder.Build;
    }

    public IReadOnlyList<PracticeMapCandidate> Get(
        IReadOnlyList<LocalReplay> replays,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses) => cached ??= build(replays, analyses, limit);

    public void Invalidate() => cached = null;
}

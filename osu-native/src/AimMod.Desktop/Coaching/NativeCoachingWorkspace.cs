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
using osuTK.Input;

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
    private const float minimum_text_size = 11;

    private readonly ILocalLibrarySource source;
    private readonly ILocalLibrarySourceChanged? sourceChanges;
    private readonly IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses;
    private readonly Action<LocalReplay> openReplay;
    private readonly Action<LocalReplay, double>? openReplayMoment;
    private readonly Action? compareMovement;
    private readonly Action? openTrainers;
    private readonly Func<IAccountScoreHistoryService?> accountHistory;
    private readonly Func<PracticeMapGenerationRequest, CancellationToken, Task<PracticeMapGenerationResult>>? generatePracticeMap;
    private readonly Func<LazerBeatmapArchive, CancellationToken, Task<LazerBeatmapInstallResult>>? installPracticeMap;

    private readonly FillFlowContainer<Drawable> trainingHost;
    private readonly Bindable<string> modSelection = new(ScoreMods.Any);
    private readonly ScoreModFilterDropdown modDropdown;
    private readonly CoachingTrainingStore? trainingStore;
    private CoachingTrainingPlan? activeTraining;
    private bool trainingSaveFailed;
    private readonly Container headerArtwork;
    private readonly OsuSpriteText sessionTitle;
    private readonly OsuSpriteText sessionPlays;
    private readonly OsuSpriteText sessionDuration;
    private readonly OsuSpriteText sessionAccuracy;
    private readonly OsuSpriteText sessionTrend;
    private readonly AnalysisProgressBanner analysisBanner;
    private readonly CoachingTrendChart trendChart;
    private readonly SectionLine globalProfileSectionLine;
    private readonly FillFlowContainer<Drawable> selectedRunHost;
    private readonly FillFlowContainer<Drawable> exactAnalysisHost;
    private readonly FillFlowContainer<Drawable> changesHost;
    private readonly FillFlowContainer<Drawable> recommendationHost;
    private readonly FillFlowContainer<Drawable> practiceHost;
    private readonly SectionLine practiceSectionLine;
    private readonly OsuTextBox practiceSearch;
    private readonly Bindable<CoachingTimeRange> coachingTimeRange = new(CoachingTimeRange.Days30);
    private readonly Bindable<PracticeCandidateSort> practiceSort = new(PracticeCandidateSort.WeakestFirst);
    private readonly Bindable<PracticeEvidenceFilter> practiceEvidence = new(PracticeEvidenceFilter.AnyEvidence);
    private readonly BindableDouble practiceMinimumStars = new(0) { MinValue = 0, MaxValue = 10, Default = 0 };
    private readonly BindableDouble practiceMaximumStars = new(10) { MinValue = 0, MaxValue = 10, Default = 10 };
    private const double analysis_refresh_interval = 1_500;
    private readonly ScoreHistorySession history;
    private readonly CoachingModelBuilder modelBuilder = new();
    private readonly LatestBackgroundQuery<CoachingModelResult> modelQuery = new();
    private readonly WorkspaceProgressEstimator analysisEstimate = new();
    private readonly object modChoiceGate = new();
    private IReadOnlyList<LocalReplay>? modChoiceSource;
    private IReadOnlyList<ScoreModChoice>? modChoices;
    private IReadOnlyList<LocalReplay>? filteredSource;
    private string? filteredMods;
    private IReadOnlyList<LocalReplay> filtered = Array.Empty<LocalReplay>();
    private IReadOnlyList<PracticeMapCandidate> practicePool = Array.Empty<PracticeMapCandidate>();
    private int modelGeneration;
    private bool buildingModel;
    private (int Completed, int Failed)? pendingCompletion;
    private ScheduledDelegate? scheduledRunListRefresh;
    private readonly List<CoachingMapRow> runRows = new();
    private int focusedRun = -1;
    private Action? cancelAnalysisAction;
    private readonly OsuTextBox search;
    private readonly FillFlowContainer<Drawable> runList;
    private readonly AimModLoadingOverlay loadingOverlay;

    private CancellationTokenSource? loading;
    private Task historyLoadTask = Task.CompletedTask;
    private IReadOnlyList<ScoreHistoryEntry> submittedScores = [];
    private IAccountScoreHistoryService? submittedScoresService;
    private CancellationTokenSource? practiceGeneration;
    private CancellationTokenSource? practiceLaunch;
    private ScheduledDelegate? scheduledPracticeRefresh;
    private ScheduledDelegate? scheduledAnalysisRefresh;
    public IReadOnlyList<LocalReplay> PracticeSourceHistory => allReplays;
    private IReadOnlyList<LocalReplay> allReplays = Array.Empty<LocalReplay>();
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
    private LazerBeatmapArchive? practiceLazerArchive;
    private readonly NativePracticeWorkspace? practiceWorkspace;
    private readonly Func<LocalReplay, CancellationToken, Task>? openBeatmap;

    public void ClosePractice() { if (practiceWorkspace is not null) { practiceWorkspace.Alpha = 0; if (practiceWorkspace.IsShowingSavedPractice) showCoachingPage(coachingMapId is null ? 3 : 4); } refreshPracticeProgress(); }

    public void FocusPracticeMap(string title)
    {
        practiceMinimumStars.SetDefault();
        practiceMaximumStars.SetDefault();
        practiceEvidence.Value = PracticeEvidenceFilter.AnyEvidence;
        practiceSearch.Current.Value = title;
        updatePracticeMapsImmediately();
        practiceWorkspace?.Open(title);
    }

    public NativeCoachingWorkspace(
        ILocalLibrarySource source,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses,
        Action<LocalReplay> openReplay,
        Func<IAccountScoreHistoryService?>? accountHistory = null,
        Func<PracticeMapGenerationRequest, CancellationToken, Task<PracticeMapGenerationResult>>? generatePracticeMap = null,
        Func<LazerBeatmapArchive, CancellationToken, Task<LazerBeatmapInstallResult>>? installPracticeMap = null,
        NativePracticeWorkspace? practiceWorkspace = null,
        Func<LocalReplay, CancellationToken, Task>? openBeatmap = null, CoachingTrainingStore? trainingStore = null, PracticeMapLibrary? practiceLibrary = null, Func<int>? accountId = null,
        Action<LocalReplay, double>? openReplayMoment = null, Action? compareMovement = null, Action? openTrainers = null)
    {
        this.practiceLibrary = practiceLibrary;
        this.practiceAccountId = accountId ?? (() => 0);
        this.trainingStore = trainingStore;
        activeTraining = trainingStore?.Load();
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        history = ScoreHistorySession.For(source);
        this.analyses = analyses ?? throw new ArgumentNullException(nameof(analyses));
        this.openReplay = openReplay ?? throw new ArgumentNullException(nameof(openReplay));
        this.openReplayMoment = openReplayMoment;
        this.compareMovement = compareMovement;
        this.openTrainers = openTrainers;
        this.accountHistory = accountHistory ?? (() => null);
        this.generatePracticeMap = generatePracticeMap;
        this.installPracticeMap = installPracticeMap;
        this.practiceWorkspace = practiceWorkspace;
        this.openBeatmap = openBeatmap;
        sourceChanges = source as ILocalLibrarySourceChanged;
        if (sourceChanges is not null)
            sourceChanges.SourceChanged += sourceChanged;

        RelativeSizeAxes = Axes.Both;

        var sessionContent = pageFlow();
        sessionContent.Add(trainingHost = pageFlow());
        var progressContent = pageFlow();
        progressContent.Add(reviewHost = pageFlow());
        coachingDetails = pageFlow();
        CoachingButton? detailToggle = null;
        detailToggle = new CoachingButton("Show detailed statistics", () => {
            detailsOpen = !detailsOpen; coachingDetails.Alpha = detailsOpen ? 1 : 0;
            detailToggle!.SetCaption(detailsOpen ? "Hide detailed statistics" : "Show detailed statistics");
        });
        progressContent.Add(detailToggle);
        coachingDetails.Alpha = 0;
        progressContent.Add(coachingDetails);
        coachingDetails.Add(createSessionHeader(out headerArtwork, out sessionTitle, out sessionPlays,
            out sessionDuration, out sessionAccuracy, out sessionTrend, coachingTimeRange));
        coachingDetails.Add(analysisBanner = new AnalysisProgressBanner());
        coachingDetails.Add(new CoachingColumns(
            createPerformancePanel(out trendChart, out globalProfileSectionLine, out selectedRunHost, out exactAnalysisHost)
                .With(panel => { panel.RelativeSizeAxes = Axes.X; panel.Height = 540; }),
            createPracticePanel(out practiceHost, out practiceSectionLine, out practiceSearch,
                practiceSort, practiceEvidence, practiceMinimumStars, practiceMaximumStars)
                .With(panel => { panel.RelativeSizeAxes = Axes.X; panel.Height = 540; })));
        coachingDetails.Add(createCoachPanel(out changesHost, out recommendationHost).With(panel => panel.Height = 410));
        var playsContent = pageFlow();
        playsContent.Add(flow("Which map do you want to improve?", 19, AimModPalette.Text));
        playsContent.Add(flow("Pick a recent play to find difficult sections, practise aim and tapping separately, and check if it helps on the full map.", 14, AimModPalette.Muted));

        playsContent.Add(search = new AimModTextBox {
            RelativeSizeAxes = Axes.X, Height = AimModVisualStyle.ControlHeight, PlaceholderText = "Search maps, difficulties, artists or mods",
        });
        runList = pageFlow();
        var savedContent = pageFlow();

        savedContent.Add(savedPracticeSearch = new AimModTextBox {
            RelativeSizeAxes = Axes.X, Height = AimModVisualStyle.ControlHeight, PlaceholderText = "Search saved maps and practice difficulties",
        });
        var savedActions = new FillFlowContainer<Drawable> {
            RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Full, Spacing = new(8),
            Children = [
                new CoachingButton("Create a practice set", () => showCoachingPage(2), true, true),
                new CoachingButton("Refresh results", reloadHistory, compact:true),
                new AimModResetButton(() => savedPracticeSearch.Current.Value = string.Empty, "Clear search"),
            ],
        };
        savedContent.Add(savedActions);
        CoachingButton? archiveToggle = null;
        archiveToggle = new CoachingButton("Show archived sets", () => {
            showArchivedPractice = !showArchivedPractice;
            archiveToggle!.SetCaption(showArchivedPractice ? "Show active sets" : "Show archived sets");
            renderPracticeHistory();
        }, compact: true);
        savedActions.Add(archiveToggle);
        savedContent.Add(practiceHistoryHost = pageFlow());
        savedPracticeSearch.Current.BindValueChanged(_ => renderPracticeHistory());
        coachingPages = new[] { sessionContent, progressContent, playsContent, savedContent, mapDetailHost = pageFlow() }
            .Select(body => new AimModScrollContainer { RelativeSizeAxes = Axes.Both, Child = body, Alpha = 0 }).ToArray();
        pageNavigation = new FillFlowContainer<Drawable> {
            AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(8), Y = 72, Anchor = Anchor.TopLeft, Origin = Anchor.TopLeft,
        };
        navigationButtons = new[] { "Practice plan", "Review results", "Find a map", "My coaching", "Beatmap" }
            .Select((name, index) => new CoachingButton(name, () => showCoachingPage(index), compact: true)).ToArray();
        foreach (int index in new[] { 2, 3 }) pageNavigation.Add(navigationButtons[index]);
        coachingFilters = new GridContainer {
            Width = 350, Height = 40, Depth = -20,
            ColumnDimensions = [new Dimension(GridSizeMode.Relative, .52f), new Dimension(GridSizeMode.Relative, .48f)],
            Content = new[] { new Drawable[] {
                new StatisticsFilterBar { RelativeSizeAxes = Axes.Both,
                    Child = modDropdown = new ScoreModFilterDropdown(modSelection) },
                new TimeRangeDropdown { RelativeSizeAxes = Axes.X, Width = .95f,
                    Items = Enum.GetValues<CoachingTimeRange>(), Current = coachingTimeRange }
            } },
        };
        playsContent.Add(coachingFilters);
        playsContent.Add(runList);
        renderPracticeHistory();
        InternalChildren = [
            new AimModSectionHeader("Coaching", "Build a practice plan and track your progress.") { Width = 1 },
            new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Top = 120 },
                Children = coachingPages },
            pageNavigation,
            loadingOverlay = new AimModLoadingOverlay(),
        ];
        if (practiceWorkspace is not null) AddInternal(practiceWorkspace);
        showCoachingPage(2);

        practiceSearch.Current.BindValueChanged(_ => schedulePracticeMapRefresh());
        practiceSort.BindValueChanged(_ => updatePracticeMapsImmediately());
        practiceEvidence.BindValueChanged(_ => updatePracticeMapsImmediately());
        practiceMinimumStars.BindValueChanged(_ => schedulePracticeMapRefresh());
        practiceMaximumStars.BindValueChanged(_ => schedulePracticeMapRefresh());
        coachingTimeRange.BindValueChanged(_ => changeTimeRange());
        modSelection.BindValueChanged(_ => changeTimeRange());
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        search.OnCommit += (_, _) => refreshRunList();
        search.Current.BindValueChanged(_ => scheduleRunListRefresh());
        load();
        _ = watchPracticeScores(practiceTrackingLifetime.Token);
    }

    /// <summary>Builds models on the calling thread; used by tests that drive the workspace without a game host.</summary>
    internal bool SynchronousModelBuilds { get; set; }

    /// <summary>Raised when the player cancels the running replay analysis pass from the progress banner.</summary>
    public Action? CancelAnalysisRequested { get; set; }

    /// <summary>Reloads score history, reusing cached results when nothing changed.</summary>
    public void RefreshHistory()
    {
        if (IsLoaded && !IsDisposed && historyLoadTask.IsCompleted)
            load(refresh: true);
    }

    private void reloadHistory()
    {
        source.Invalidate();
        history.Invalidate();
        load();
    }

    private void load(bool refresh = false)
    {
        loading?.Cancel();
        loading?.Dispose();
        loading = new CancellationTokenSource();
        if (workspace is null)
        {
            analysisBanner.ShowHistoryLoading();
            loadingOverlay.ShowLoading("Preparing coaching", "Loading your recent plays");
        }
        var service = accountHistory();
        if (!ReferenceEquals(service, submittedScoresService))
        {
            submittedScores = [];
            submittedScoresService = service;
        }
        var retainedSubmittedScores = submittedScores;
        var token = loading.Token;
        int account = practiceAccountId();
        historyLoadTask = Task.Run(() => loadAsync(service, retainedSubmittedScores, account, refresh, token));
    }

    private async Task loadAsync(IAccountScoreHistoryService? service, IReadOnlyList<ScoreHistoryEntry> retainedSubmittedScores,
        int account, bool refresh, CancellationToken cancellationToken)
    {
        try
        {
            StatisticsHistoryLoadResult local = await history.GetLocalAsync(refresh, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<LocalReplay> merged = history.Merge(local.Runs, retainedSubmittedScores);
            IReadOnlyList<ScoreModChoice> choices = modChoicesFor(merged);
            scheduleHistoryUpdate(() => apply(merged, choices), account, cancellationToken);

            if (service is null)
                return;

            OnlineAccountScoreHistoryResult? online;
            try
            {
                online = await history.GetOnlineAsync(service, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                scheduleHistoryUpdate(() => analysisBanner.ShowWarning(
                        "Submitted scores could not be refreshed",
                        "Your saved plays are still shown. Retry to include your latest submitted scores.",
                        "Retry", reloadHistory), account, cancellationToken);
                return;
            }

            if (online is null)
                return;

            var refreshedSubmittedScores = online.BestCoverage.IsSuccess && online.RecentCoverage.IsSuccess
                ? online.Scores
                : online.Scores.Concat(retainedSubmittedScores).DistinctBy(score => score.Identity).ToArray();
            merged = history.Merge(local.Runs, refreshedSubmittedScores);
            choices = modChoicesFor(merged);
            bool unavailable = !online.BestCoverage.IsSuccess && !online.RecentCoverage.IsSuccess;
            scheduleHistoryUpdate(() =>
            {
                submittedScores = refreshedSubmittedScores;
                apply(merged, choices);
                if (unavailable)
                {
                    analysisBanner.ShowWarning(
                        "Submitted scores are unavailable",
                        "Sign in to osu! in Settings to include submitted scores. Your saved plays are still used.",
                        "Retry", reloadHistory);
                }
            }, account, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            scheduleHistoryUpdate(() =>
                {
                    loadingOverlay.HideLoading();
                    analysisBanner.ShowError(
                        "Score history could not be loaded",
                        "AimMod could not read your osu! plays. Check the osu! installation in Settings, then retry.",
                        "Retry", reloadHistory);
                }, account, cancellationToken);
        }
    }

    private IReadOnlyList<ScoreModChoice> modChoicesFor(IReadOnlyList<LocalReplay> runs)
    {
        lock (modChoiceGate)
        {
            if (ReferenceEquals(modChoiceSource, runs) && modChoices is { } cached)
                return cached;
        }

        IReadOnlyList<ScoreModChoice> choices = ScoreMods.Choices(runs);
        lock (modChoiceGate)
        {
            modChoiceSource = runs;
            modChoices = choices;
        }
        return choices;
    }

    private void scheduleHistoryUpdate(Action update, int account, CancellationToken token)
    {
        if (IsDisposed || token.IsCancellationRequested) return;
        Schedule(() =>
        {
            if (!IsDisposed && !token.IsCancellationRequested && account == practiceAccountId()) update();
        });
    }

    private void apply(IReadOnlyList<LocalReplay> nextReplays, IReadOnlyList<ScoreModChoice> choices)
    {
        if (workspace is not null && ReferenceEquals(allReplays, nextReplays) && renderedAnalysisCount == analyses.Count)
        {
            refreshPracticeProgress();
            loadingOverlay.HideLoading();
            return;
        }
        if (!ReferenceEquals(allReplays, nextReplays))
        {
            allReplays = nextReplays;
            if (coachingMapRun is { } selectedMapRun)
                coachingMapRun = nextReplays.FirstOrDefault(r => r.ScoreId == selectedMapRun.ScoreId) ?? selectedMapRun;
            practiceWorkspace?.SetSourceHistory(nextReplays);
            modDropdown.SetChoices(choices);
            trainingPlanCache = null;
        }
        refreshPracticeProgress();
        requestModel(workspace?.SelectedRun?.ScoreId);
    }

    /// <summary>
    /// Builds the model for the current filters off the update thread. Cached scopes apply immediately;
    /// otherwise the visible content stays in place with an updating indicator until the build completes.
    /// </summary>
    private void requestModel(Guid? selectedScoreId)
    {
        scheduledAnalysisRefresh?.Cancel();
        scheduledAnalysisRefresh = null;
        IReadOnlyList<LocalReplay> runs = filteredRuns();
        CoachingTimeRange range = coachingTimeRange.Value;
        int analysisCount = analyses.Count;
        int generation = ++modelGeneration;
        if (modelBuilder.TryGetCached(runs, analyses, selectedScoreId, range, out NativeCoachingWorkspaceModel? cached)
            && modelBuilder.TryGetPracticePool(cached, out IReadOnlyList<PracticeMapCandidate>? cachedPool))
        {
            applyModel(cached, cachedPool, analysisCount);
            return;
        }

        CoachingModelResult build(CancellationToken token)
        {
            NativeCoachingWorkspaceModel model = modelBuilder.Build(runs, analyses, selectedScoreId, range);
            token.ThrowIfCancellationRequested();
            IReadOnlyList<PracticeMapCandidate> pool = modelBuilder.GetPracticePool(model, analyses, practice_candidate_pool_limit);
            return new CoachingModelResult(model, pool, analysisCount);
        }

        if (SynchronousModelBuilds)
        {
            CoachingModelResult result = build(CancellationToken.None);
            applyModel(result.Model, result.PracticePool, result.AnalysisCount);
            return;
        }

        buildingModel = true;
        if (workspace is null)
        {
            if (!acceptingAnalysisProgress)
                loadingOverlay.ShowLoading("Preparing coaching", "Building your coaching report");
        }
        else if (!acceptingAnalysisProgress)
            analysisBanner.ShowUpdating();

        modelQuery.Submit(build, result =>
        {
            if (!IsDisposed) Schedule(() =>
            {
                if (!IsDisposed && generation == modelGeneration)
                    applyModel(result.Model, result.PracticePool, result.AnalysisCount);
            });
        }, error =>
        {
            Console.Error.WriteLine($"Coaching report could not be built: {error}");
            if (!IsDisposed) Schedule(() =>
            {
                if (IsDisposed || generation != modelGeneration) return;
                buildingModel = false;
                loadingOverlay.HideLoading();
                analysisBanner.ShowError("Coaching report could not be prepared",
                    "Your plays are still saved. Retry to rebuild the report.", "Retry", () => requestModel(selectedScoreId));
            });
        });
    }

    private void applyModel(NativeCoachingWorkspaceModel model, IReadOnlyList<PracticeMapCandidate> pool, int analysisCount)
    {
        buildingModel = false;
        workspace = model;
        replays = model.History;
        renderedAnalysisCount = analysisCount;
        if (!ReferenceEquals(practicePool, pool))
        {
            practicePool = pool;
            renderedPracticePage = null;
        }
        loadingOverlay.HideLoading();
        updateWorkspace();
        if (pendingCompletion is not null)
            showCompletion();
        else if (!acceptingAnalysisProgress)
            analysisBanner.ShowReady(model.GlobalProfile, replays.Count, scopedSubmittedRunCount(), TimeRangeLabel(coachingTimeRange.Value), allReplays.Count);
    }

    private IReadOnlyList<LocalReplay> filteredRuns()
    {
        if (ReferenceEquals(filteredSource, allReplays) && filteredMods == modSelection.Value)
            return filtered;
        filteredSource = allReplays;
        filteredMods = modSelection.Value;
        return filtered = modSelection.Value == ScoreMods.Any
            ? allReplays
            : allReplays.Where(r => CoachingRunKeys.Matches(r, modSelection.Value)).ToArray();
    }

    private void selectRun(Guid scoreId)
    {
        requestModel(scoreId);
        showCoachingPage(1);
    }

    private void showGlobalOverview() => requestModel(null);

    private void changeTimeRange()
    {
        if (workspace is null && allReplays.Count == 0)
            return;

        requestModel(workspace?.SelectedRun?.ScoreId);
    }

    private int scopedSubmittedRunCount() => replays.Count(run => run.OnlineScoreId > 0);

    public void SetAnalysisProgress(int completed, int total, string currentTitle)
    {
        if (!acceptingAnalysisProgress || total <= 0)
            return;

        if (workspace is not null && renderedAnalysisCount != analyses.Count && !buildingModel)
        {
            scheduledAnalysisRefresh ??= Scheduler.AddDelayed(() =>
            {
                scheduledAnalysisRefresh = null;
                if (IsDisposed || !acceptingAnalysisProgress) return;
                requestModel(workspace?.SelectedRun?.ScoreId);
            }, analysis_refresh_interval);
        }

        analysisEstimate.Report(completed);
        TimeSpan? remaining = analysisEstimate.Remaining(completed, total);
        analysisBanner.ShowAnalysing(
            completed,
            total,
            currentTitle,
            workspace?.GlobalProfile.Coverage.AnalysedRunCount ?? analyses.Count,
            remaining is { } eta ? WorkspaceProgressEstimator.FormatRemaining(eta) : null,
            CancelAnalysisRequested is null ? null : cancelAnalysisAction ??= cancelAnalysis);
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

    private void cancelAnalysis()
    {
        analysisBanner.ShowCancelling();
        CancelAnalysisRequested?.Invoke();
    }

    public void BeginAnalysisProgress()
    {
        acceptingAnalysisProgress = true;
        analysisEstimate.Reset();
        analysisBanner.ShowStarting(workspace?.GlobalProfile.Coverage.AnalysedRunCount ?? analyses.Count);
        if (workspace is null)
            loadingOverlay.ShowLoading("Preparing coaching", "Loading score history before replay analysis");
        else
            loadingOverlay.HideLoading();
    }

    public void ApplyNewAnalyses(int completed, int failed)
    {
        scheduledAnalysisRefresh?.Cancel();
        scheduledAnalysisRefresh = null;
        acceptingAnalysisProgress = false;
        pendingCompletion = (completed, failed);
        requestModel(workspace?.SelectedRun?.ScoreId);
    }

    public void SetAnalysisError()
    {
        acceptingAnalysisProgress = false;
        pendingCompletion = null;
        loadingOverlay.HideLoading();
        analysisBanner.ShowError(
            "Replay analysis paused",
            "Your existing coaching profile and practice maps are still available.");
    }

    private void showCompletion()
    {
        if (pendingCompletion is not { } result || workspace is null)
            return;
        pendingCompletion = null;
        analysisBanner.ShowComplete(workspace.GlobalProfile, result.Completed, result.Failed);
    }

    private sealed record CoachingModelResult(NativeCoachingWorkspaceModel Model, IReadOnlyList<PracticeMapCandidate> PracticePool, int AnalysisCount);

    private void updateWorkspace()
    {
        if (workspace is not { } model)
            return;
        CoachingReport report = model.Report;
        LocalReplay? selected = model.SelectedRun;

        renderSession();
        updateSessionHeader(model);
        globalProfileSectionLine.SetDetail(TimeRangeLabel(coachingTimeRange.Value));
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
        IReadOnlyList<PracticeMapCandidate> available = practicePool;
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
            practiceLazerArchive is not null);
        if (SamePracticeCandidatePage(renderedPracticePage, candidates) && renderedPracticeState == displayState)
            return;

        renderedPracticePage = candidates;
        practiceWorkspace?.SetCandidates(available);
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
            bool canRetryLazer = practiceLazerArchive is not null && installPracticeMap is not null;
            if (canRetryLazer)
            {
                actionLabel = "Open in osu!";
                action = beginPracticeLaunch;
            }

            practiceHost.Add(new PracticeStatusRow(
                practiceSucceeded ? "Practice map ready" : canRetryLazer ? "Could not open osu!lazer" : "Practice map not created",
                practiceMessage,
                practiceSucceeded ? AimModPalette.Success : canRetryLazer ? AimModPalette.Yellow : AimModPalette.Pink,
                practiceSucceeded ? FontAwesome.Solid.CheckCircle : FontAwesome.Solid.ExclamationCircle,
                actionLabel,
                action));
        }

        if (available.Count == 0)
        {
            practiceHost.Add(new PracticeEmptyState(
                acceptingAnalysisProgress ? "Finding your weakest patterns" : "More replay evidence needed",
                acceptingAnalysisProgress
                    ? "Practice maps will appear as timing patterns and missed sections are found."
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
        if (practiceWorkspace is not null) { practiceWorkspace.OpenCandidate(candidate); return; }
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
                updatePracticeMapsImmediately();
            });
        }
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
            ? "Recent results"
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
        selectedRunHost.Add(new OpenBeatmapButton(() => run, openBeatmap));
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

    private void saveTraining() {
        try { trainingStore?.Save(activeTraining); trainingSaveFailed=false; }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException) {
            trainingSaveFailed=true;
        }
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

    private void scheduleRunListRefresh()
    {
        scheduledRunListRefresh?.Cancel();
        scheduledRunListRefresh = Scheduler.AddDelayed(refreshRunList, PracticeFilterDebounceMilliseconds);
    }

    private void refreshRunList()
    {
        scheduledRunListRefresh?.Cancel();
        scheduledRunListRefresh = null;
        runList.Clear();
        runRows.Clear();
        focusedRun = -1;
        LocalReplay[] eligible = replays.Where(eligibleForCoaching).ToArray();
        CoachingRunPage page = CoachingRunSearch.Search(eligible, new CoachingRunQuery(
            SearchText: search.Current.Value,
            Sort: CoachingRunSort.Recent,
            Limit: visible_run_limit));
        bool filtered = search.Current.Value.Length > 0 || modSelection.Value != ScoreMods.Any || coachingTimeRange.Value != CoachingTimeRange.All;
        void resetFilters()
        {
            search.Current.Value = string.Empty;
            modSelection.Value = ScoreMods.Any;
            coachingTimeRange.Value = CoachingTimeRange.All;
        }
        runList.Add(new Container { RelativeSizeAxes = Axes.X, Height = 32, Children = [
            flow(page.Total > page.Items.Count ? $"{page.Items.Count} of {page.Total:N0} matching plays shown" : $"{page.Items.Count} recent plays shown", 12, AimModPalette.Muted),
            new AimModResetButton(resetFilters) { Anchor = Anchor.TopRight, Origin = Anchor.TopRight },
        ] });
        if (workspace is null)
        {
            runList.Add(new WorkspaceSkeleton(4, 78));
            return;
        }

        if (page.Items.Count == 0)
        {
            var empty = new WorkspaceStateCard();
            runList.Add(empty);
            if (allReplays.Count == 0)
            {
                empty.Show(FontAwesome.Solid.Music, "No plays yet",
                    "Play a map in osu!, or connect your osu! installation and account in Settings. Your plays will appear here.");
            }
            else if (filtered)
            {
                empty.Show(FontAwesome.Solid.Filter, "No plays match these filters",
                    "Try a wider date range, another mod filter or a different search.", actionLabel: "Reset filters", action: resetFilters);
            }
            else
            {
                empty.Show(FontAwesome.Solid.InfoCircle, "No completed plays to coach yet",
                    "Coaching uses passed plays with at least 70% accuracy. Finish a map you are comfortable with to begin.");
            }
            if (openTrainers is not null)
                runList.Add(visualEntry("Train a skill", "Practise now, even without saved plays.", PracticeSketchKind.Timing, openTrainers));
            return;
        }

        var byScore = eligible.GroupBy(run => run.ScoreId).ToDictionary(group => group.Key, group => group.First());
        foreach (CoachingRecentRun item in page.Items)
        {
            if (!byScore.TryGetValue(item.ScoreId, out LocalReplay? replay)) continue;
            var row = new CoachingMapRow(replay.Title, replay.Difficulty,
                $"{replay.Accuracy:P2} accuracy  ·  {replay.MissCount} misses  ·  {ScoreMods.Display(replay)}  ·  {replay.PlayedAt.ToLocalTime():dd MMM, HH:mm}",
                "Open coaching", () => openCoachingRun(replay));
            runRows.Add(row);
            runList.Add(row);
        }
    }

    protected override bool OnKeyDown(KeyDownEvent e)
    {
        if (e.ControlPressed && e.Key == Key.F && selectedCoachingPage is 2 or 3)
        {
            GetContainingFocusManager()?.ChangeFocus(selectedCoachingPage == 2 ? search : savedPracticeSearch);
            return true;
        }

        if (selectedCoachingPage == 2 && runRows.Count > 0 && !e.ControlPressed && !e.AltPressed)
        {
            switch (e.Key)
            {
                case Key.Down:
                    focusRun(focusedRun + 1);
                    return true;
                case Key.Up:
                    focusRun(focusedRun - 1);
                    return true;
                case Key.Enter or Key.KeypadEnter when focusedRun >= 0:
                    runRows[focusedRun].TriggerClick();
                    return true;
            }
        }

        if (e.Key == Key.Escape && !e.Repeat)
        {
            if (selectedCoachingPage == 2 && search.Current.Value.Length > 0)
            {
                search.Current.Value = string.Empty;
                return true;
            }
            if (selectedCoachingPage == 4)
            {
                showCoachingPage(3);
                return true;
            }
            if (selectedCoachingPage is 0 or 1)
            {
                showCoachingPage(2);
                return true;
            }
        }

        return base.OnKeyDown(e);
    }

    private void focusRun(int index)
    {
        if (focusedRun >= 0 && focusedRun < runRows.Count)
            runRows[focusedRun].SetFocused(false);
        focusedRun = Math.Clamp(index, 0, runRows.Count - 1);
        CoachingMapRow row = runRows[focusedRun];
        row.SetFocused(true);
        coachingPages[2].ScrollIntoView(row);
    }

    private static Container createSessionHeader(
        out Container artwork,
        out OsuSpriteText title,
        out OsuSpriteText plays,
        out OsuSpriteText duration,
        out OsuSpriteText accuracy,
        out OsuSpriteText trend,
        Bindable<CoachingTimeRange> timeRange)
    {
        var header = new Container
        {
            RelativeSizeAxes = Axes.X,
            Height = 96,
            Depth = -30,
        };

        artwork = new Container { RelativeSizeAxes = Axes.Both };
        title = label("Global coaching overview", 22, AimModPalette.Text, "Bold");
        plays = label("No plays", 13, AimModPalette.Text, "SemiBold");
        duration = label("No session yet", 13, AimModPalette.Text, "SemiBold");
        accuracy = label("-", 22, AimModPalette.Text, "Bold");
        trend = label("More plays needed", 14, AimModPalette.Muted, "Bold");

        header.Children = new Drawable[]
        {
            new Container
            {
                RelativeSizeAxes = Axes.Both,
                Masking = true,
                CornerRadius = AimModVisualStyle.CardRadius,
                Children = new Drawable[]
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
                },
            },
            new FillFlowContainer
            {
                Anchor = Anchor.CentreRight,
                Origin = Anchor.CentreRight,
                AutoSizeAxes = Axes.Y,
                Width = 156,
                Margin = new MarginPadding { Right = 18 },
                Direction = FillDirection.Vertical,
                Spacing = new(3),
                Depth = -20,
                Children = new Drawable[]
                {
                    label("PROFILE PERIOD", 8, AimModPalette.Cyan, "Bold"),
                    new TimeRangeDropdown
                    {
                        RelativeSizeAxes = Axes.X,
                        Items = Enum.GetValues<CoachingTimeRange>(),
                        Current = timeRange,
                    },
                },
            },
        };
        header.Remove(header.Children.Last(), true); // The shared toolbar owns the time-range filter.
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
        out SectionLine profileSection,
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
        body.Add(profileSection = new SectionLine("Global skill profile", "Last 30 days"));
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
            "Practise a section with timing trouble or repeated misses.",
            11,
            AimModPalette.Muted));
        body.Add(search = new AimModTextBox
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
        body.Add(new AimModStarRatingFilter
        {
            RelativeSizeAxes = Axes.X,
            Height = 30,
            LowerBound = minimumStars,
            UpperBound = maximumStars,
            DefaultStringLowerBound = "0",
            DefaultStringUpperBound = "10+",
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

    internal static string ProfileCoverageValue(GlobalCoachingProfile profile) => $"{profile.Coverage.ReplayCoverage * 100:0}%";

    internal static string ProfileEvidenceSummary(GlobalCoachingProfile profile) => profile.MissReasons.Count == 0
        ? "No classified misses in analysed replays yet"
        : string.Join("  /  ", profile.MissReasons.Take(4)
            .Select(reason => $"{ReplayMissInsightPresenter.Label(reason.Reason)} {reason.Share * 100:0}%"));

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

    private static double standardDeviation(IEnumerable<double> values) => ReplayJudgementClassifier.StandardDeviation(values);

    private void sourceChanged()
    {
        if (!IsDisposed)
            Schedule(() => load());
    }

    protected override void Dispose(bool isDisposing)
    {
        practiceTrackingLifetime.Cancel();
        practiceTrackingLifetime.Dispose();
        scheduledAnalysisRefresh?.Cancel();
        loading?.Cancel();
        loading?.Dispose();
        practiceGeneration?.Cancel();
        practiceGeneration?.Dispose();
        practiceLaunch?.Cancel();
        practiceLaunch?.Dispose();
        scheduledPracticeRefresh?.Cancel();
        scheduledPracticeRefresh = null;
        scheduledRunListRefresh?.Cancel();
        modelQuery.Dispose();
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
        bool HasLazerArchive);

    private static OsuSpriteText label(string text, float size, Colour4 colour, string weight = "Regular") => new()
    {
        Text = text,
        Font = new FontUsage(size: Math.Max(minimum_text_size, size), weight: weight),
        Colour = colour,
    };

    private static TruncatingSpriteText truncatingLabel(string text, float size, Colour4 colour, float maxWidth, string weight = "Regular") => new()
    {
        Text = text,
        Font = new FontUsage(size: Math.Max(minimum_text_size, size), weight: weight),
        Colour = colour,
        MaxWidth = maxWidth,
    };

    private static OsuTextFlowContainer flow(string text, float size, Colour4 colour, string weight = "Regular") => new(sprite =>
    {
        sprite.Font = new FontUsage(size: Math.Max(minimum_text_size, size), weight: weight);
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
        LazerBeatmapInstallStatus.ArchiveUnavailable => "The generated drill is no longer available. Create it again.",
        LazerBeatmapInstallStatus.LazerNotFound => "osu!lazer was not found. Check the installation and try again.",
        LazerBeatmapInstallStatus.LazerRejected => "osu!lazer did not accept the drill. Try creating it again.",
        _ => "AimMod could not open osu!lazer. Check the installation and try again.",
    };
}

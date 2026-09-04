using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.UserInterface;
using osu.Game.Screens;

namespace AimMod.Desktop;

/// <summary>
/// Native workspace around osu!'s official replay player. Score, artwork and
/// analysis values all originate in the local lazer library.
/// </summary>
public partial class NativeReplayRouteView : Container
{
    private const float browser_width = 320;
    private const float inspector_width = 340;
    private const float transport_height = 228;

    public OsuScreenStack ScreenStack { get; } = new() { RelativeSizeAxes = Axes.Both };

    private readonly ILocalLibrarySource? source;
    private readonly IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses;
    private readonly Action<LocalReplay>? openReplay;
    private readonly OsuTextBox searchBox;
    private readonly SpriteText replayCount;
    private readonly FillFlowContainer<Drawable> replayList;
    private readonly Container statusLayer;
    private readonly SpriteIcon statusIcon;
    private readonly TruncatingSpriteText statusTitle;
    private readonly TruncatingSpriteText statusDetail;
    private readonly SpriteText summaryAccuracy;
    private readonly SpriteText summaryPerformance;
    private readonly SpriteText summaryMisses;
    private readonly SpriteText summaryCombo;
    private readonly SpriteText analysisTitle;
    private readonly WrappedLabel analysisSummary;
    private readonly WrappedLabel analysisNextPlay;
    private readonly FillFlowContainer<Drawable> notableRows;
    private readonly FillFlowContainer<Drawable> mapPatternRows;
    private readonly FillFlowContainer<Drawable> momentButtons;
    private readonly ReplayJudgementTimeline judgementTimeline;
    private readonly SpriteText currentTimeText;
    private readonly SpriteText durationText;
    private readonly SpriteText pauseLabel;
    private readonly SpriteText speedLabel;
    private readonly Container analysisCard;
    private readonly AimModLoadingOverlay loadingOverlay;
    private IBindable<double>? currentTime;
    private IBindable<double>? duration;
    private IBindable<bool>? paused;
    private NativeReplayPlayer? player;
    private LocalReplay? selectedReplay;
    private CancellationTokenSource? loading;
    private CancellationTokenSource? mapPatternLoading;
    private ReplayBrowserSnapshot replayBrowser = ReplayBrowserSnapshot.Empty;
    private readonly HashSet<string> expandedReplayMaps = new(StringComparer.Ordinal);
    private long analysisRevision;
    private double playbackSpeed = 1;
    private bool analysisInProgress;
    private bool analysisHasResult;

    public NativeReplayRouteView(
        ILocalLibrarySource? source = null,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult>? analyses = null,
        Action<LocalReplay>? openReplay = null)
    {
        this.source = source;
        this.analyses = analyses ?? new Dictionary<Guid, ReplayAnalysisResult>();
        this.openReplay = openReplay;
        RelativeSizeAxes = Axes.Both;

        Children = new Drawable[]
        {
            makePanel(new Container
            {
                RelativeSizeAxes = Axes.Y,
                Width = browser_width,
                Padding = new MarginPadding(12),
                Children = new Drawable[]
                {
                    place(section("REPLAY LIBRARY"), y: 2),
                    replayCount = place(makeText("Loading local runs...", 10, AimModPalette.Muted, "SemiBold"), y: 22),
                    searchBox = new OsuTextBox
                    {
                        RelativeSizeAxes = Axes.X,
                        Height = AimModVisualStyle.ControlHeight,
                        Y = 44,
                        PlaceholderText = "Search replays",
                    },
                    new OsuScrollContainer
                    {
                        RelativeSizeAxes = Axes.Both,
                        Padding = new MarginPadding { Top = 94 },
                        Child = replayList = new FillFlowContainer<Drawable>
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Padding = new MarginPadding { Right = 8, Bottom = 10 },
                            Direction = FillDirection.Vertical,
                            Spacing = new(AimModVisualStyle.RelatedSpacing),
                        },
                    },
                },
            }, Anchor.TopLeft, Anchor.TopLeft, null, browser_width),
            new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Left = browser_width + AimModVisualStyle.RowSpacing, Right = inspector_width + AimModVisualStyle.RowSpacing },
                Children = new Drawable[]
                {
                    new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Padding = new MarginPadding { Bottom = transport_height + AimModVisualStyle.RowSpacing },
                        Masking = true,
                        Children = new Drawable[]
                        {
                            ScreenStack,
                            statusLayer = new Container
                            {
                                RelativeSizeAxes = Axes.Both,
                                Children = new Drawable[]
                                {
                                    new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas },
                                    new FillFlowContainer
                                    {
                                        Anchor = Anchor.Centre,
                                        Origin = Anchor.Centre,
                                        RelativeSizeAxes = Axes.X,
                                        AutoSizeAxes = Axes.Y,
                                        Width = 1,
                                        Padding = new MarginPadding { Horizontal = 40 },
                                        Direction = FillDirection.Vertical,
                                        Spacing = new(10),
                                        Children = new Drawable[]
                                        {
                                            new Container
                                            {
                                                RelativeSizeAxes = Axes.X,
                                                Height = 44,
                                                Child = statusIcon = new SpriteIcon
                                                {
                                                    Anchor = Anchor.Centre,
                                                    Origin = Anchor.Centre,
                                                    Size = new(34),
                                                    Icon = FontAwesome.Solid.PlayCircle,
                                                    Colour = AimModPalette.Cyan,
                                                },
                                            },
                                            statusTitle = truncatingText("Choose a replay", 25, AimModPalette.Text, 540, "Bold", Anchor.TopCentre),
                                            statusDetail = truncatingText("Expand a map on the left, then choose an attempt to inspect.", 13, AimModPalette.Muted, 540, anchor: Anchor.TopCentre),
                                        },
                                    },
                                },
                            },
                        },
                    },
                    makePanel(new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Padding = new MarginPadding(14),
                        Children = new Drawable[]
                        {
                            new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                Height = 36,
                                Direction = FillDirection.Horizontal,
                                Spacing = new(AimModVisualStyle.RelatedSpacing),
                                Children = new Drawable[]
                                {
                                    new TransportButton("-5s", () => player?.SeekTo((currentTime?.Value ?? 0) - 5000)),
                                    new TransportButton("Play", () => player?.TogglePause(), pauseLabel = makeText("Play", 12, AimModPalette.Text, "Bold")),
                                    new TransportButton("+5s", () => player?.SeekTo((currentTime?.Value ?? 0) + 5000)),
                                    new TransportButton("1.00x", cycleSpeed, speedLabel = makeText("1.00x", 12, AimModPalette.Text, "Bold")),
                                },
                            },
                            currentTimeText = place(makeText("0:00.000", 12, AimModPalette.Text, "SemiBold"), y: 44),
                            durationText = place(makeText("0:00.000", 12, AimModPalette.Muted, "SemiBold"), y: 44, anchor: Anchor.TopRight, origin: Anchor.TopRight),
                            new ReplayScrubber(
                                () => currentTime?.Value ?? 0,
                                () => duration?.Value ?? 0,
                                time => player?.SeekTo(time))
                            {
                                RelativeSizeAxes = Axes.X,
                                Height = 24,
                                Y = 58,
                            },
                            place(makeText("JUDGEMENT TIMELINE", 11, AimModPalette.Muted, "Bold"), y: 91),
                            createJudgementLegend(),
                            judgementTimeline = new ReplayJudgementTimeline { Y = 116 },
                            momentButtons = new FillFlowContainer<Drawable>
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Y = 166,
                                Direction = FillDirection.Horizontal,
                                Spacing = new(AimModVisualStyle.RelatedSpacing),
                            },
                        },
                    }, Anchor.BottomLeft, Anchor.BottomLeft, transport_height),
                },
            },
            makePanel(new OsuScrollContainer
            {
                RelativeSizeAxes = Axes.Both,
                Child = new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding { Left = 16, Top = 14, Right = 24, Bottom = 28 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(AimModVisualStyle.RowSpacing),
                    Children = new Drawable[]
                    {
                        new AimModSubsectionHeader("Run summary", "selected attempt"),
                        new GridContainer
                        {
                            RelativeSizeAxes = Axes.X,
                            Height = 104,
                            ColumnDimensions = new[]
                            {
                                new Dimension(GridSizeMode.Relative, 0.5f),
                                new Dimension(GridSizeMode.Relative, 0.5f),
                            },
                            RowDimensions = new[]
                            {
                                new Dimension(GridSizeMode.Relative, 0.5f),
                                new Dimension(GridSizeMode.Relative, 0.5f),
                            },
                            Content = new[]
                            {
                                new Drawable[]
                                {
                                    summaryMetric("ACCURACY", "--", AimModPalette.Text, out summaryAccuracy),
                                    summaryMetric("PERFORMANCE", "--", AimModPalette.Cyan, out summaryPerformance),
                                },
                                new Drawable[]
                                {
                                    summaryMetric("MISSES", "--", AimModPalette.Text, out summaryMisses),
                                    summaryMetric("MAX COMBO", "--", AimModPalette.Text, out summaryCombo),
                                },
                            },
                        },
                        new AimModSubsectionHeader("Analysis summary", "exact judgements"),
                        analysisCard = new Container
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Masking = true,
                            CornerRadius = AimModVisualStyle.CardRadius,
                            Alpha = 0,
                            Children = new Drawable[]
                            {
                                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                                new Box { RelativeSizeAxes = Axes.Y, Width = 3, Colour = AimModPalette.Cyan },
                                new FillFlowContainer
                                {
                                    RelativeSizeAxes = Axes.X,
                                    AutoSizeAxes = Axes.Y,
                                    Padding = new MarginPadding { Left = 14, Top = 12, Right = 12, Bottom = 12 },
                                    Direction = FillDirection.Vertical,
                                    Spacing = new(AimModVisualStyle.RelatedSpacing),
                                    Children = new Drawable[]
                                    {
                                        analysisTitle = makeText("Analysing exact judgements...", 13, AimModPalette.Text, "Bold"),
                                        analysisSummary = new WrappedLabel("Preparing replay details", 11, AimModPalette.Muted),
                                    },
                                },
                            },
                        },
                        new AimModSubsectionHeader("Notable moments", "click to review"),
                        notableRows = new FillFlowContainer<Drawable>
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Direction = FillDirection.Vertical,
                            Spacing = new(AimModVisualStyle.RelatedSpacing),
                        },
                        new AimModSubsectionHeader("Across attempts", "same difficulty"),
                        mapPatternRows = new FillFlowContainer<Drawable>
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Direction = FillDirection.Vertical,
                            Spacing = new(5),
                        },
                        new AimModSubsectionHeader("Focus for your next play"),
                        new Container
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Masking = true,
                            CornerRadius = AimModVisualStyle.ControlRadius,
                            Children = new Drawable[]
                            {
                                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                                new Box { RelativeSizeAxes = Axes.Y, Width = 3, Colour = AimModPalette.Success },
                                new Container
                                {
                                    Padding = new MarginPadding { Left = 14, Top = 12, Right = 12, Bottom = 12 },
                                    RelativeSizeAxes = Axes.X,
                                    AutoSizeAxes = Axes.Y,
                                    Child = analysisNextPlay = new WrappedLabel("Select a run to get a measured next step.", 12, AimModPalette.Text, "SemiBold"),
                                },
                            },
                        },
                    },
                },
            }, Anchor.TopRight, Anchor.TopRight, null, inspector_width),
            loadingOverlay = new AimModLoadingOverlay(),
        };

        analysisTitle.Text = "No replay selected";
        analysisSummary.Text = "Choose an attempt to calculate exact judgements and coaching evidence.";
        analysisCard.Alpha = 1;
        showNotableState("Select a run to see misses, slider breaks, and timing errors.", AimModPalette.Muted);
        showMapPatternState("Choose a map with multiple attempts to compare repeated mistakes.");
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        searchBox.OnCommit += (_, _) => loadReplayBrowser();
        if (source is not null)
            loadReplayBrowser();
    }

    protected override void Update()
    {
        base.Update();
        currentTimeText.Text = formatTime(currentTime?.Value ?? 0);
        durationText.Text = formatTime(duration?.Value ?? 0);
        pauseLabel.Text = paused?.Value != false ? "Play" : "Pause";
        statusTitle.MaxWidth = statusDetail.MaxWidth = Math.Max(120, statusLayer.DrawWidth - 80);
    }

    public void SetReplaySummary(LocalReplay replay)
    {
        selectedReplay = replay;
        expandedReplayMaps.Add(ReplayBrowserModel.MapKeyFor(replay));
        analysisRevision = -1;
        analysisInProgress = false;
        analysisHasResult = false;
        summaryAccuracy.Text = formatAccuracy(replay.Accuracy);
        summaryPerformance.Text = replay.PerformancePoints is { } pp ? $"{pp:0.#}pp" : $"{replay.TotalScore:N0}";
        summaryMisses.Text = replay.MissCount.ToString("N0");
        summaryCombo.Text = $"{replay.MaxCombo:N0}x";
        statusIcon.Icon = FontAwesome.Solid.PlayCircle;
        statusIcon.Colour = AimModPalette.Cyan;
        statusTitle.Text = replay.Title;
        statusTitle.Colour = AimModPalette.Text;
        statusDetail.Text = $"{replay.Difficulty}  //  {formatAccuracy(replay.Accuracy)}  //  {replay.PlayedAt.LocalDateTime:g}";
        statusLayer.FadeIn(80);

        if (analyses.TryGetValue(replay.ScoreId, out ReplayAnalysisResult? cachedAnalysis))
            showCompletedAnalysis(cachedAnalysis);
        else
            showPendingAnalysis();

        loadReplayBrowser();
    }

    public void AttachPlayer(NativeReplayPlayer replayPlayer)
    {
        player?.SuspendPlayback();
        player = replayPlayer;
        currentTime = replayPlayer.CurrentTime;
        duration = replayPlayer.Duration;
        paused = replayPlayer.IsPaused;
    }

    public void SuspendPlayback() => player?.SuspendPlayback();

    public void ShowReady() => statusLayer.FadeOut(180);

    public void ShowError(string message)
    {
        analysisInProgress = false;
        loadingOverlay.HideLoading();
        statusIcon.Icon = FontAwesome.Solid.ExclamationTriangle;
        statusIcon.Colour = AimModPalette.Pink;
        statusTitle.Text = "Replay could not be opened";
        statusTitle.Colour = AimModPalette.Pink;
        statusDetail.Text = message;
        statusLayer.FadeIn(120);

        if (!analysisHasResult)
            showAnalysisFailure("Replay analysis could not start because the replay did not open.", "Resolve the replay loading error, then select the run again.");
    }

    public void ShowAnalysisState(ReplayAnalysisState state)
    {
        if (state.Revision < analysisRevision)
            return;

        analysisRevision = state.Revision;
        switch (state.Status)
        {
            case ReplayAnalysisStatus.Running:
                analysisInProgress = true;
                analysisHasResult = false;
                loadingOverlay.HideLoading();
                analysisTitle.Text = "Analysing exact judgements...";
                analysisSummary.Text = "Running accelerated official ruleset playback";
                showNotableState("Exact judgement analysis is in progress.", AimModPalette.Muted);
                momentButtons.Clear();
                judgementTimeline.ClearResult();
                analysisNextPlay.Text = "A measured focus will appear when exact judgement analysis completes.";
                analysisCard.FadeIn(150);
                break;

            case ReplayAnalysisStatus.Completed when state.Result is not null:
                showCompletedAnalysis(state.Result);
                break;

            case ReplayAnalysisStatus.Failed:
                analysisInProgress = false;
                analysisHasResult = false;
                loadingOverlay.HideLoading();
                showAnalysisFailure(
                    state.Error?.Message ?? "AimMod could not analyse this replay.",
                    "Exact coaching focus is unavailable for this run. Replay playback is still available.");
                break;

            case ReplayAnalysisStatus.Cancelled:
                analysisInProgress = false;
                analysisHasResult = false;
                loadingOverlay.HideLoading();
                showAnalysisFailure(
                    "Exact replay analysis was cancelled.",
                    "Select the run again to calculate its coaching focus.");
                break;

            case ReplayAnalysisStatus.Idle:
                analysisInProgress = false;
                analysisHasResult = false;
                loadingOverlay.HideLoading();
                showAnalysisFailure(
                    "Exact replay analysis has not started.",
                    "Open this run to calculate notable moments and a measured coaching focus.");
                break;
        }
    }

    public void ShowAnalysisError(string message)
    {
        analysisInProgress = false;
        analysisHasResult = false;
        loadingOverlay.HideLoading();
        showAnalysisFailure(message, "Exact coaching focus is unavailable for this run. Replay playback is still available.");
    }

    public void ShowMapAnalysisProgress(int completed, int total, string currentTitle)
    {
        if (total <= 0)
            return;

        showMapPatternState(completed >= total
            ? "Updating repeated-pattern analysis..."
            : $"Analysing matching attempt {completed + 1:N0}/{total:N0}: {currentTitle}");
    }

    public void RefreshMapPattern() => loadMapPattern();

    private void showPendingAnalysis()
    {
        analysisTitle.Text = "Waiting for exact replay analysis";
        analysisSummary.Text = "Opening the replay and preparing exact judgement data.";
        showNotableState("Notable moments will appear when analysis completes.", AimModPalette.Muted);
        momentButtons.Clear();
        judgementTimeline.ClearResult();
        analysisNextPlay.Text = "A measured focus will appear when exact judgement analysis completes.";
        showMapPatternState("Analyse this replay to compare it with other attempts.");
        analysisCard.FadeIn(150);
    }

    private void showCompletedAnalysis(ReplayAnalysisResult result)
    {
        analysisInProgress = false;
        analysisHasResult = true;
        loadingOverlay.HideLoading();
        ReplayAnalysisPresentation presentation = ReplayAnalysisPresenter.Present(result);
        analysisTitle.Text = "Exact replay analysis";
        analysisSummary.Text = wrap(presentation.Summary, 37);
        analysisNextPlay.Text = wrap(measuredNextPlay(result, presentation.NextPlay), 35);
        judgementTimeline.SetResult(result);
        showMomentButtons(result);
        showNotableRows(result);
        loadMapPattern();
        analysisCard.FadeIn(150);
    }

    private void showAnalysisFailure(string message, string nextPlay)
    {
        analysisTitle.Text = "Replay analysis unavailable";
        analysisSummary.Text = message;
        showNotableState(message, AimModPalette.Pink);
        momentButtons.Clear();
        judgementTimeline.ClearResult();
        analysisNextPlay.Text = nextPlay;
        analysisCard.FadeIn(150);
    }

    private void showNotableState(string message, Colour4 colour)
    {
        notableRows.Clear();
        notableRows.Add(new InspectorStateRow(message, colour));
    }

    private void loadReplayBrowser()
    {
        if (source is null)
            return;

        loading?.Cancel();
        loading?.Dispose();
        loading = new CancellationTokenSource();
        CancellationToken cancellationToken = loading.Token;
        if (selectedReplay is null && !analysisInProgress)
            loadingOverlay.ShowLoading("Loading replays", "Reading your local osu!lazer play history");
        _ = loadReplayBrowserAsync(searchBox.Current.Value, cancellationToken);
    }

    private async Task loadReplayBrowserAsync(string search, CancellationToken cancellationToken)
    {
        try
        {
            ILocalLibrarySource availableSource = source ?? throw new InvalidOperationException("The local replay library is not available.");
            ReplayBrowserSnapshot page = await ReplayBrowserModel.LoadAsync(
                availableSource,
                search,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!IsDisposed && !cancellationToken.IsCancellationRequested)
                Schedule(() => applyReplayBrowser(page));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!IsDisposed)
                Schedule(() =>
                {
                    if (!analysisInProgress)
                        loadingOverlay.HideLoading();
                    replayCount.Text = $"Local replays unavailable: {error.Message}";
                });
        }
    }

    private void applyReplayBrowser(ReplayBrowserSnapshot snapshot)
    {
        replayBrowser = snapshot;
        if (selectedReplay is not null)
            expandedReplayMaps.Add(ReplayBrowserModel.MapKeyFor(selectedReplay));

        renderReplayBrowser();
        if (!analysisInProgress)
            loadingOverlay.HideLoading();
    }

    private void renderReplayBrowser()
    {
        int shownMaps = replayBrowser.Maps.Count;
        string shownMapLabel = shownMaps == 1 ? "map" : "maps";
        replayCount.Text = replayBrowser.TotalMapCount > shownMaps
            ? $"Newest {shownMaps:N0} {shownMapLabel} of {replayBrowser.TotalMapCount:N0}  //  {replayBrowser.TotalReplayCount:N0} runs"
            : $"{shownMaps:N0} {shownMapLabel}  //  {replayBrowser.TotalReplayCount:N0} runs";
        replayList.Clear();

        foreach (ReplayBrowserMapGroup group in replayBrowser.Maps)
        {
            bool expanded = expandedReplayMaps.Contains(group.Key);
            replayList.Add(new ReplayGroupHeader(group, expanded, () => toggleReplayMap(group.Key)));
            if (!expanded)
                continue;

            foreach (LocalReplay replay in group.Attempts)
                replayList.Add(new ReplayBrowserRow(replay, replay.ScoreId == selectedReplay?.ScoreId, () => openReplay?.Invoke(replay)));
        }

        if (replayBrowser.Maps.Count == 0)
            replayList.Add(new ReplayBrowserEmptyState(
                string.IsNullOrWhiteSpace(searchBox.Current.Value) ? "No saved replays" : "No matching replays",
                string.IsNullOrWhiteSpace(searchBox.Current.Value)
                    ? "Play an osu!standard map with replay recording enabled, then return here."
                    : "Try a title, artist, difficulty, player, or mod."));
    }

    private void toggleReplayMap(string key)
    {
        if (!expandedReplayMaps.Add(key))
            expandedReplayMaps.Remove(key);

        renderReplayBrowser();
    }

    private void showMomentButtons(ReplayAnalysisResult result)
    {
        momentButtons.Clear();
        foreach (ReplayObjectJudgement judgement in ReplayAnalysisPresenter.SelectNotableJudgements(result))
        {
            double seekTime = Math.Max(0, judgement.StartTimeMs - 1500);
            string label = formatTime(judgement.StartTimeMs);
            momentButtons.Add(new MomentButton(label, () =>
            {
                Console.Error.WriteLine($"[AimMod] Jumping to notable replay moment {label}.");
                player?.Seek(seekTime);
            }));
        }
    }

    private void showNotableRows(ReplayAnalysisResult result)
    {
        notableRows.Clear();
        IReadOnlyList<ReplayObjectJudgement> judgements = ReplayAnalysisPresenter.SelectNotableJudgements(result);
        foreach (ReplayObjectJudgement judgement in judgements)
        {
            string objectLabel = judgement.ObjectIndex is { } index ? $"Object {index + 1:N0}" : judgement.ObjectType;
            notableRows.Add(new NotableMomentRow(
                formatTime(judgement.StartTimeMs),
                objectLabel,
                judgement.Result,
                ReplayMissInsightPresenter.Describe(judgement),
                () =>
            {
                string label = formatTime(judgement.StartTimeMs);
                Console.Error.WriteLine($"[AimMod] Jumping to notable replay moment {label}.");
                player?.Seek(Math.Max(0, judgement.StartTimeMs - 1500));
            }));
        }

        if (judgements.Count == 0)
            notableRows.Add(new WrappedLabel("No misses or slider breaks were found.", 12, AimModPalette.Success, "SemiBold"));
    }

    private void loadMapPattern()
    {
        mapPatternLoading?.Cancel();
        mapPatternLoading?.Dispose();
        mapPatternLoading = new CancellationTokenSource();
        CancellationToken cancellationToken = mapPatternLoading.Token;
        LocalReplay? replay = selectedReplay;
        if (replay is null)
            return;

        showMapPatternState("Checking this difficulty across saved attempts...");
        if (source is null)
        {
            applyMapPattern(replay, new[] { replay });
            return;
        }

        _ = loadMapPatternAsync(replay, cancellationToken);
    }

    private async Task loadMapPatternAsync(LocalReplay replay, CancellationToken cancellationToken)
    {
        try
        {
            LocalLibraryPage<LocalReplay> page = await source!.SearchReplaysAsync(new LocalLibraryQuery(
                SearchText: replay.Title,
                RulesetShortName: "osu",
                Sort: LocalLibrarySort.RecentlyPlayed,
                Limit: 200), cancellationToken).ConfigureAwait(false);
            if (!IsDisposed && !cancellationToken.IsCancellationRequested)
                Schedule(() => applyMapPattern(replay, page.Items));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            if (!IsDisposed)
                Schedule(() => applyMapPattern(replay, new[] { replay }));
        }
    }

    private void applyMapPattern(LocalReplay replay, IReadOnlyList<LocalReplay> history)
    {
        if (selectedReplay?.ScoreId != replay.ScoreId)
            return;

        ReplayMapPatternReport report = ReplayMapPatternAnalyzer.Build(replay, history, analyses);
        mapPatternRows.Clear();
        mapPatternRows.Add(new WrappedLabel(
            $"{report.AnalysedAttempts:N0} of {report.TotalAttempts:N0} saved attempts have exact analysis.",
            11,
            AimModPalette.Muted));

        foreach (ReplayRecurringMiss pattern in report.RecurringMisses.Take(4))
        {
            string reason = pattern.DominantReason is { } value ? ReplayMissInsightPresenter.Label(value) : "misses";
            mapPatternRows.Add(new WrappedLabel(
                $"{formatTime(pattern.StartTimeMs)} · object {pattern.ObjectIndex + 1:N0} missed in {pattern.MissedAttempts:N0}/{pattern.AnalysedAttempts:N0} attempts · {reason}",
                11,
                AimModPalette.Pink,
                "SemiBold"));
        }

        if (report.RecurringMisses.Count == 0)
        {
            string message = report.AnalysedAttempts < 2
                ? "At least two analysed attempts are needed to identify repeated mistakes."
                : "No object was missed in more than one analysed attempt.";
            mapPatternRows.Add(new WrappedLabel(message, 11, AimModPalette.Text));
        }

        if (report.MissReasons.Count > 0)
        {
            string dominant = report.MissReasons.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).First() is var pair
                ? $"Most common: {ReplayMissInsightPresenter.Label(pair.Key)} ({pair.Value:N0})."
                : string.Empty;
            mapPatternRows.Add(new WrappedLabel(dominant, 11, AimModPalette.Cyan, "SemiBold"));
        }
    }

    private void showMapPatternState(string message)
    {
        mapPatternRows.Clear();
        mapPatternRows.Add(new InspectorStateRow(message, AimModPalette.Muted));
    }

    private string measuredNextPlay(ReplayAnalysisResult result, string fallback)
    {
        if (selectedReplay is null)
            return fallback;

        try
        {
            Dictionary<Guid, ReplayAnalysisResult> available = new(analyses) { [selectedReplay.ScoreId] = result };
            CoachingReport report = CoachingReportBuilder.Build(new[] { selectedReplay }, available, selectedReplay.ScoreId);
            CoachingRecommendation? recommendation = report.Intelligence.Recommendations.FirstOrDefault();
            return recommendation is null ? fallback : $"{recommendation.Intent}: {recommendation.Reason}";
        }
        catch
        {
            return fallback;
        }
    }

    private void cycleSpeed()
    {
        playbackSpeed = playbackSpeed switch
        {
            < 0.75 => 0.75,
            < 1 => 1,
            < 1.25 => 1.25,
            < 1.5 => 1.5,
            _ => 0.5,
        };
        if (player?.SetPlaybackRate(playbackSpeed) == true)
            speedLabel.Text = $"{playbackSpeed:0.00}x";
    }

    protected override void Dispose(bool isDisposing)
    {
        SuspendPlayback();
        loading?.Cancel();
        loading?.Dispose();
        mapPatternLoading?.Cancel();
        mapPatternLoading?.Dispose();
        base.Dispose(isDisposing);
    }

    private static Container makePanel(Drawable child, Anchor anchor, Anchor origin, float? height = null, float? width = null) => new()
    {
        Anchor = anchor,
        Origin = origin,
        RelativeSizeAxes = height is null ? Axes.Y : Axes.X,
        Width = width ?? 1,
        Height = height ?? 1,
        Masking = true,
        CornerRadius = AimModVisualStyle.CardRadius,
        Children = new Drawable[]
        {
            new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
            child,
        },
    };

    private static SpriteText section(string value) => makeText(value, 11, AimModPalette.Muted, "Bold");

    private static FillFlowContainer<Drawable> createJudgementLegend()
    {
        var legend = new FillFlowContainer<Drawable>
        {
            Anchor = Anchor.TopRight,
            Origin = Anchor.TopRight,
            AutoSizeAxes = Axes.Both,
            Y = 89,
            Direction = FillDirection.Horizontal,
            Spacing = new(12, 0),
        };
        legend.AddRange(new[]
        {
            legendItem("300", ReplayTimelineTone.Great),
            legendItem("100", ReplayTimelineTone.Ok),
            legendItem("50", ReplayTimelineTone.Meh),
            legendItem("miss", ReplayTimelineTone.Miss),
        });
        return legend;
    }

    private static FillFlowContainer<Drawable> legendItem(string label, ReplayTimelineTone tone) => new()
    {
        AutoSizeAxes = Axes.Both,
        Direction = FillDirection.Horizontal,
        Spacing = new(4, 0),
        Children = new Drawable[]
        {
            new CircularContainer
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                Size = new(8),
                Masking = true,
                Child = new Box { RelativeSizeAxes = Axes.Both, Colour = ReplayJudgementTimeline.ColourFor(tone) },
            },
            makeText(label, 10, AimModPalette.Muted, "SemiBold"),
        },
    };

    private static FillFlowContainer<Drawable> summaryMetric(
        string labelValue,
        string value,
        Colour4 colour,
        out SpriteText valueText)
    {
        valueText = makeText(value, 22, colour, "Bold");
        return new FillFlowContainer<Drawable>
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new(3),
            Children = new Drawable[]
            {
                makeText(labelValue, 9, AimModPalette.Muted, "Bold"),
                valueText,
            },
        };
    }

    private static string formatAccuracy(double value) => double.IsFinite(value) ? $"{value * 100:0.00}%" : "--";

    private static string wrap(string value, int width)
    {
        var lines = new List<string>();
        var current = new List<string>();
        int length = 0;
        foreach (string word in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (length > 0 && length + word.Length + 1 > width)
            {
                lines.Add(string.Join(' ', current));
                current.Clear();
                length = 0;
            }

            current.Add(word);
            length += word.Length + (length == 0 ? 0 : 1);
        }

        if (current.Count > 0)
            lines.Add(string.Join(' ', current));
        return string.Join('\n', lines.Take(4));
    }

    private static string formatTime(double milliseconds)
    {
        TimeSpan time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds:000}";
    }

    private static SpriteText makeText(string value, float size, Colour4 colour, string weight = "Regular") => new()
    {
        Text = value,
        Font = new FontUsage(size: size, weight: weight),
        Colour = colour,
    };

    private static TruncatingSpriteText truncatingText(
        string value,
        float size,
        Colour4 colour,
        float maxWidth,
        string weight = "Regular",
        Anchor anchor = Anchor.TopLeft) => new()
    {
        Text = value,
        Font = new FontUsage(size: size, weight: weight),
        Colour = colour,
        MaxWidth = maxWidth,
        Anchor = anchor,
        Origin = anchor,
    };

    private static SpriteText place(SpriteText drawable, float x = 0, float y = 0, Anchor anchor = Anchor.TopLeft, Anchor origin = Anchor.TopLeft)
    {
        drawable.Position = new(x, y);
        drawable.Anchor = anchor;
        drawable.Origin = origin;
        return drawable;
    }

    private partial class ReplayBrowserEmptyState : CompositeDrawable
    {
        public ReplayBrowserEmptyState(string title, string detail)
        {
            RelativeSizeAxes = Axes.X;
            Height = 150;
            Masking = true;
            CornerRadius = AimModVisualStyle.CardRadius;
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas, Alpha = 0.5f },
                new SpriteIcon
                {
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.TopCentre,
                    Y = 25,
                    Size = new(24),
                    Icon = FontAwesome.Solid.PlayCircle,
                    Colour = AimModPalette.Cyan,
                },
                new FillFlowContainer
                {
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.TopCentre,
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Y = 62,
                    Padding = new MarginPadding { Horizontal = 24 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(AimModVisualStyle.RelatedSpacing),
                    Children = new Drawable[]
                    {
                        truncatingText(title, 15, AimModPalette.Text, 240, "SemiBold", Anchor.TopCentre),
                        new WrappedLabel(detail, 11, AimModPalette.Muted),
                    },
                },
            };
        }
    }

    private partial class InspectorStateRow : CompositeDrawable
    {
        public InspectorStateRow(string message, Colour4 colour)
        {
            RelativeSizeAxes = Axes.X;
            Height = 52;
            Masking = true;
            CornerRadius = AimModVisualStyle.ControlRadius;
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                new Box { RelativeSizeAxes = Axes.Y, Width = 4, Colour = colour },
                new SpriteIcon
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Position = new(14, 0),
                    Size = new(12),
                    Icon = FontAwesome.Solid.InfoCircle,
                    Colour = colour,
                },
                new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding { Left = 36, Right = 12, Top = 11, Bottom = 9 },
                    Child = new WrappedLabel(message, 10, colour, "SemiBold"),
                },
            };
        }
    }

    private partial class ReplayGroupHeader : AimModInteractiveSurface
    {
        private readonly TruncatingSpriteText title;
        private readonly TruncatingSpriteText detail;

        public ReplayGroupHeader(ReplayBrowserMapGroup group, bool expanded, Action action)
        {
            RelativeSizeAxes = Axes.X;
            Height = 62;
            Action = action;
            CornerRadius = AimModVisualStyle.CardRadius;
            BackgroundColour = expanded ? AimModPalette.PanelRaised : AimModPalette.Panel;
            LocalReplay latest = group.Attempts[0];
            Children = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Y,
                    Width = 3,
                    Colour = AimModVisualStyle.DifficultyColour(latest.StarRating),
                },
                title = new TruncatingSpriteText
                {
                    Text = group.Title,
                    Position = new(12, 7),
                    Font = new FontUsage(size: 13, weight: "Bold"),
                    Colour = AimModPalette.Text,
                    MaxWidth = 240,
                },
                detail = new TruncatingSpriteText
                {
                    Text = $"{group.Artist}  //  {group.Difficulty}",
                    Position = new(12, 28),
                    Font = new FontUsage(size: 10, weight: "Regular"),
                    Colour = AimModPalette.Cyan,
                    MaxWidth = 240,
                },
                place(makeText(
                    $"{group.Attempts.Count:N0} {(group.Attempts.Count == 1 ? "attempt" : "attempts")}  //  best {group.Attempts.Max(replay => replay.Accuracy) * 100:0.00}%",
                    9,
                    AimModPalette.Muted,
                    "SemiBold"), 12, 46),
                new SpriteIcon
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    Position = new(-12, 0),
                    Size = new(11),
                    Icon = expanded ? FontAwesome.Solid.ChevronUp : FontAwesome.Solid.ChevronDown,
                    Colour = AimModPalette.Muted,
                },
            };
        }

        protected override void Update()
        {
            base.Update();
            title.MaxWidth = detail.MaxWidth = Math.Max(100, DrawWidth - 48);
        }
    }

    private partial class ReplayBrowserRow : AimModInteractiveSurface
    {
        public ReplayBrowserRow(LocalReplay replay, bool selected, Action action)
        {
            RelativeSizeAxes = Axes.X;
            Height = 60;
            CornerRadius = AimModVisualStyle.ControlRadius;
            BackgroundColour = selected ? AimModPalette.PanelHover : AimModPalette.PanelRaised;
            Action = action;
            Children = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Y,
                    Width = 3,
                    Colour = selected ? AimModPalette.Pink : AimModVisualStyle.DifficultyColour(replay.StarRating),
                },
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Pink, Alpha = selected ? 0.08f : 0 },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding { Left = 12, Top = 8, Right = 10 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(4),
                    Children = new Drawable[]
                    {
                        new FillFlowContainer
                        {
                            AutoSizeAxes = Axes.Both,
                            Direction = FillDirection.Horizontal,
                            Spacing = new(7),
                            Children = new Drawable[]
                            {
                                new AimModDifficultyPill(replay.StarRating),
                                makeText(formatAccuracy(replay.Accuracy), 15, AimModPalette.Text, "Bold"),
                                makeText($"{replay.MissCount:N0} {(replay.MissCount == 1 ? "miss" : "misses")}", 9, replay.MissCount > 0 ? AimModPalette.Pink : AimModPalette.Success, "SemiBold"),
                            },
                        },
                        makeText(
                            $"{replay.PlayedAt.LocalDateTime:g}  //  {(replay.Mods.Count == 0 ? "No Mod" : string.Join(' ', replay.Mods))}",
                            9,
                            AimModPalette.Muted),
                    },
                },
            };
        }

    }

    private partial class NotableMomentRow : AimModInteractiveSurface
    {
        public NotableMomentRow(string time, string objectLabel, string result, string detail, Action action)
        {
            RelativeSizeAxes = Axes.X;
            Height = 72;
            Action = action;
            CornerRadius = AimModVisualStyle.ControlRadius;
            BackgroundColour = AimModPalette.PanelRaised;
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Y, Width = 3, Colour = result == "Miss" ? ReplayJudgementTimeline.ColourFor(ReplayTimelineTone.Miss) : AimModPalette.Cyan },
                place(makeText(time, 11, AimModPalette.Pink, "Bold"), 12, 9),
                place(makeText(objectLabel, 10, AimModPalette.Text, "SemiBold"), 82, 10),
                place(makeText(result.ToUpperInvariant(), 9, result == "Miss" ? ReplayJudgementTimeline.ColourFor(ReplayTimelineTone.Miss) : AimModPalette.Muted, "Bold"), -10, 10, Anchor.TopRight, Anchor.TopRight),
                new Container
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 34,
                    Y = 32,
                    Padding = new MarginPadding { Left = 12, Right = 10 },
                    Child = new WrappedLabel(detail, 10, AimModPalette.Muted),
                },
            };
        }
    }

    private partial class TransportButton : AimModInteractiveSurface
    {
        public TransportButton(string label, Action action, SpriteText? suppliedLabel = null)
        {
            Width = label.Length > 4 ? 70 : 54;
            Height = AimModVisualStyle.CompactControlHeight;
            Action = action;
            CornerRadius = AimModVisualStyle.ControlRadius;
            BackgroundColour = AimModPalette.PanelRaised;
            Children = new Drawable[]
            {
                suppliedLabel ?? place(makeText(label, 12, AimModPalette.Text, "Bold"), anchor: Anchor.Centre, origin: Anchor.Centre),
            };
            if (suppliedLabel is not null)
            {
                suppliedLabel.Anchor = Anchor.Centre;
                suppliedLabel.Origin = Anchor.Centre;
            }
        }
    }

    private partial class ReplayScrubber : ClickableContainer
    {
        private readonly Func<double> currentTime;
        private readonly Func<double> duration;
        private readonly Action<double> seek;
        private readonly Box progress;
        private readonly CircularContainer handle;

        public ReplayScrubber(Func<double> currentTime, Func<double> duration, Action<double> seek)
        {
            this.currentTime = currentTime;
            this.duration = duration;
            this.seek = seek;

            Children = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 4,
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Colour = AimModPalette.Border,
                },
                progress = new Box
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 4,
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Colour = AimModPalette.Pink,
                },
                handle = new CircularContainer
                {
                    RelativePositionAxes = Axes.X,
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.Centre,
                    Size = new(14),
                    Masking = true,
                    Child = new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = AimModPalette.Text,
                    },
                },
            };
        }

        protected override void Update()
        {
            base.Update();
            double total = duration();
            float position = total > 0 ? (float)Math.Clamp(currentTime() / total, 0, 1) : 0;
            progress.Width = position;
            handle.X = position;
        }

        protected override bool OnClick(ClickEvent e)
        {
            seekTo(e.ScreenSpaceMousePosition);
            return true;
        }

        protected override bool OnDragStart(DragStartEvent e) => duration() > 0;

        protected override void OnDrag(DragEvent e) => seekTo(e.ScreenSpaceMousePosition);

        private void seekTo(osuTK.Vector2 screenPosition)
        {
            double total = duration();
            if (total <= 0 || DrawWidth <= 0)
                return;

            float localX = ToLocalSpace(screenPosition).X;
            seek(Math.Clamp(localX / DrawWidth, 0, 1) * total);
        }
    }

    private partial class MomentButton : AimModInteractiveSurface
    {
        public MomentButton(string time, Action action)
        {
            Size = new(92, 30);
            Action = action;
            CornerRadius = AimModVisualStyle.ControlRadius;
            BackgroundColour = AimModPalette.PanelRaised;
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Cyan, Alpha = 0.18f },
                padded(makeText($"Jump {time}", 11, AimModPalette.Cyan, "SemiBold"), new MarginPadding { Horizontal = 10, Vertical = 6 }),
            };
        }
    }

    private partial class WrappedLabel : CompositeDrawable
    {
        private readonly TextFlowContainer flow;
        private string text = string.Empty;

        public WrappedLabel(string value, float size, Colour4 colour, string weight = "Regular")
        {
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;
            InternalChild = flow = new TextFlowContainer(sprite =>
            {
                sprite.Font = new FontUsage(size: size, weight: weight);
                sprite.Colour = colour;
            })
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
            };
            Text = value;
        }

        public string Text
        {
            get => text;
            set
            {
                text = value ?? string.Empty;
                flow.Clear();
                flow.AddText(text);
            }
        }
    }

    private static SpriteText padded(SpriteText drawable, MarginPadding padding)
    {
        drawable.Padding = padding;
        return drawable;
    }
}

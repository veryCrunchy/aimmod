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
    private const float browser_width = 280;
    private const float inspector_width = 270;

    public OsuScreenStack ScreenStack { get; } = new() { RelativeSizeAxes = Axes.Both };

    private readonly ILocalLibrarySource? source;
    private readonly IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses;
    private readonly Action<LocalReplay>? openReplay;
    private readonly OsuTextBox searchBox;
    private readonly SpriteText replayCount;
    private readonly FillFlowContainer<Drawable> replayList;
    private readonly Container statusLayer;
    private readonly SpriteText statusTitle;
    private readonly SpriteText statusDetail;
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
                Padding = new MarginPadding(14),
                Children = new Drawable[]
                {
                    searchBox = new OsuTextBox
                    {
                        RelativeSizeAxes = Axes.X,
                        Height = 42,
                        PlaceholderText = "Search replays",
                    },
                    replayCount = place(makeText("Loading local runs...", 12, AimModPalette.Muted, "SemiBold"), y: 53),
                    new OsuScrollContainer
                    {
                        RelativeSizeAxes = Axes.Both,
                        Padding = new MarginPadding { Top = 79 },
                        Child = replayList = new FillFlowContainer<Drawable>
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Direction = FillDirection.Vertical,
                            Spacing = new(6),
                        },
                    },
                },
            }, Anchor.TopLeft, Anchor.TopLeft, null, browser_width),
            new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Left = browser_width + 10, Right = inspector_width + 10 },
                Children = new Drawable[]
                {
                    new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Padding = new MarginPadding { Bottom = 212 },
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
                                        AutoSizeAxes = Axes.Y,
                                        Width = 620,
                                        Direction = FillDirection.Vertical,
                                        Spacing = new(10),
                                        Children = new Drawable[]
                                        {
                                            statusTitle = place(makeText("Choose a replay", 25, AimModPalette.Text, "Bold"), anchor: Anchor.TopCentre, origin: Anchor.TopCentre),
                                            statusDetail = place(makeText("Playback uses osu!'s audio, colours and active skin.", 13, AimModPalette.Muted), anchor: Anchor.TopCentre, origin: Anchor.TopCentre),
                                        },
                                    },
                                },
                            },
                        },
                    },
                    makePanel(new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Padding = new MarginPadding(15),
                        Children = new Drawable[]
                        {
                            new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                Height = 36,
                                Direction = FillDirection.Horizontal,
                                Spacing = new(8),
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
                            place(makeText("Exact judgement timeline", 12, AimModPalette.Muted, "Bold"), y: 91),
                            createJudgementLegend(),
                            judgementTimeline = new ReplayJudgementTimeline { Y = 110 },
                            momentButtons = new FillFlowContainer<Drawable>
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Y = 158,
                                Direction = FillDirection.Horizontal,
                                Spacing = new(7),
                            },
                        },
                    }, Anchor.BottomLeft, Anchor.BottomLeft, 202),
                },
            },
            makePanel(new OsuScrollContainer
            {
                RelativeSizeAxes = Axes.Both,
                Child = new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding(18),
                    Direction = FillDirection.Vertical,
                    Spacing = new(12),
                    Children = new Drawable[]
                    {
                        section("RUN SUMMARY"),
                        new GridContainer
                        {
                            RelativeSizeAxes = Axes.X,
                            Height = 74,
                            ColumnDimensions = new[]
                            {
                                new Dimension(GridSizeMode.Relative, 0.55f),
                                new Dimension(GridSizeMode.Relative, 0.45f),
                            },
                            Content = new[]
                            {
                                new Drawable[]
                                {
                                    summaryAccuracy = makeText("--", 28, AimModPalette.Text, "Bold"),
                                    summaryPerformance = makeText("--", 24, AimModPalette.Cyan, "Bold"),
                                },
                            },
                        },
                        divider(),
                        new GridContainer
                        {
                            RelativeSizeAxes = Axes.X,
                            Height = 56,
                            ColumnDimensions = new[]
                            {
                                new Dimension(GridSizeMode.Relative, 0.5f),
                                new Dimension(GridSizeMode.Relative, 0.5f),
                            },
                            Content = new[]
                            {
                                new Drawable[]
                                {
                                    metric("--", "misses", out summaryMisses),
                                    metric("--", "max combo", out summaryCombo),
                                },
                            },
                        },
                        divider(),
                        section("NOTABLE MOMENTS"),
                        notableRows = new FillFlowContainer<Drawable>
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Direction = FillDirection.Vertical,
                            Spacing = new(5),
                        },
                        analysisCard = new Container
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Masking = true,
                            CornerRadius = 9,
                            Alpha = 0,
                            Children = new Drawable[]
                            {
                                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                                new FillFlowContainer
                                {
                                    RelativeSizeAxes = Axes.X,
                                    AutoSizeAxes = Axes.Y,
                                    Padding = new MarginPadding(13),
                                    Direction = FillDirection.Vertical,
                                    Spacing = new(6),
                                    Children = new Drawable[]
                                    {
                                        analysisTitle = makeText("Analysing exact judgements...", 14, AimModPalette.Text, "Bold"),
                                        analysisSummary = new WrappedLabel("Preparing replay details", 12, AimModPalette.Muted),
                                    },
                                },
                            },
                        },
                        divider(),
                        section("ACROSS ATTEMPTS"),
                        mapPatternRows = new FillFlowContainer<Drawable>
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Direction = FillDirection.Vertical,
                            Spacing = new(5),
                        },
                        divider(),
                        section("FOCUS FOR YOUR NEXT PLAY"),
                        analysisNextPlay = new WrappedLabel("Select a run to get a measured next step.", 13, AimModPalette.Text, "SemiBold"),
                    },
                },
            }, Anchor.TopRight, Anchor.TopRight, null, inspector_width),
            loadingOverlay = new AimModLoadingOverlay(),
        };
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
        notableRows.Add(new WrappedLabel(message, 12, colour, "SemiBold"));
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
            replayList.Add(makeText("No local replays match this search.", 13, AimModPalette.Muted));
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
        mapPatternRows.Add(new WrappedLabel(message, 11, AimModPalette.Muted));
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
        CornerRadius = 10,
        BorderThickness = 1,
        BorderColour = AimModPalette.Border,
        Children = new Drawable[]
        {
            new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
            child,
        },
    };

    private static Drawable divider() => new Box { RelativeSizeAxes = Axes.X, Height = 1, Colour = AimModPalette.Border };
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
            Spacing = new(8, 0),
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
                Size = new(6),
                Masking = true,
                Child = new Box { RelativeSizeAxes = Axes.Both, Colour = ReplayJudgementTimeline.ColourFor(tone) },
            },
            makeText(label, 9, AimModPalette.Muted, "SemiBold"),
        },
    };

    private static FillFlowContainer metric(string value, string labelValue, out SpriteText valueText)
    {
        valueText = makeText(value, 20, AimModPalette.Text, "Bold");
        return new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new(2),
            Children = new Drawable[]
            {
                valueText,
                makeText(labelValue, 11, AimModPalette.Muted),
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

    private static SpriteText place(SpriteText drawable, float x = 0, float y = 0, Anchor anchor = Anchor.TopLeft, Anchor origin = Anchor.TopLeft)
    {
        drawable.Position = new(x, y);
        drawable.Anchor = anchor;
        drawable.Origin = origin;
        return drawable;
    }

    private partial class ReplayGroupHeader : ClickableContainer
    {
        public ReplayGroupHeader(ReplayBrowserMapGroup group, bool expanded, Action action)
        {
            RelativeSizeAxes = Axes.X;
            Height = 62;
            Action = action;
            Masking = true;
            CornerRadius = 6;
            BorderThickness = 1;
            BorderColour = AimModPalette.Border;
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = expanded ? AimModPalette.PanelRaised : AimModPalette.Panel },
                new TruncatingSpriteText
                {
                    Text = group.Title,
                    Position = new(10, 8),
                    Font = new FontUsage(size: 13, weight: "Bold"),
                    Colour = AimModPalette.Text,
                    MaxWidth = 218,
                },
                new TruncatingSpriteText
                {
                    Text = group.Difficulty,
                    Position = new(10, 29),
                    Font = new FontUsage(size: 10, weight: "SemiBold"),
                    Colour = AimModPalette.Cyan,
                    MaxWidth = 218,
                },
                place(makeText($"{group.Artist}  //  {group.Attempts.Count:N0} {(group.Attempts.Count == 1 ? "run" : "runs")}", 9, AimModPalette.Muted), 10, 45),
                new SpriteIcon
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    Position = new(-10, 0),
                    Size = new(11),
                    Icon = expanded ? FontAwesome.Solid.ChevronUp : FontAwesome.Solid.ChevronDown,
                    Colour = AimModPalette.Muted,
                },
            };
        }
    }

    private partial class ReplayBrowserRow : ClickableContainer
    {
        private readonly Box hover;

        public ReplayBrowserRow(LocalReplay replay, bool selected, Action action)
        {
            RelativeSizeAxes = Axes.X;
            Height = 78;
            Masking = true;
            CornerRadius = 7;
            BorderThickness = selected ? 2 : 1;
            BorderColour = selected ? AimModPalette.Pink : AimModPalette.Border;
            Action = action;
            Children = new Drawable[]
            {
                new AimModLocalArtwork(replay.BackgroundPath) { RelativeSizeAxes = Axes.Both },
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas, Alpha = 0.63f },
                hover = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Pink, Alpha = selected ? 0.12f : 0 },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding(10),
                    Direction = FillDirection.Vertical,
                    Spacing = new(4),
                    Children = new Drawable[]
                    {
                        new FillFlowContainer
                        {
                            AutoSizeAxes = Axes.Both,
                            Direction = FillDirection.Horizontal,
                            Spacing = new(9),
                            Children = new Drawable[]
                            {
                                new AimModDifficultyPill(replay.StarRating),
                                makeText(formatAccuracy(replay.Accuracy), 17, AimModPalette.Text, "Bold"),
                                makeText($"{replay.MissCount:N0} miss", 11, replay.MissCount > 0 ? AimModPalette.Pink : AimModPalette.Success, "SemiBold"),
                            },
                        },
                        makeText($"{replay.Difficulty}  //  {replay.PlayedAt.LocalDateTime:g}", 10, AimModPalette.Muted),
                    },
                },
            };
        }

        protected override bool OnHover(HoverEvent e)
        {
            hover.FadeTo(0.16f, 100);
            return base.OnHover(e);
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            hover.FadeTo(BorderThickness > 1 ? 0.12f : 0, 100);
            base.OnHoverLost(e);
        }
    }

    private partial class NotableMomentRow : ClickableContainer
    {
        public NotableMomentRow(string time, string objectLabel, string result, string detail, Action action)
        {
            RelativeSizeAxes = Axes.X;
            Height = 70;
            Action = action;
            BorderThickness = 1;
            BorderColour = AimModPalette.Border;
            Masking = true;
            CornerRadius = 6;
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                place(makeText(time, 11, AimModPalette.Pink, "Bold"), 10, 9),
                place(makeText(objectLabel, 11, AimModPalette.Text, "SemiBold"), 78, 9),
                place(makeText(result, 10, AimModPalette.Muted), -10, 10, Anchor.TopRight, Anchor.TopRight),
                new Container
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 34,
                    Y = 32,
                    Padding = new MarginPadding { Left = 10, Right = 10 },
                    Child = new WrappedLabel(detail, 10, AimModPalette.Muted),
                },
            };
        }
    }

    private partial class TransportButton : ClickableContainer
    {
        public TransportButton(string label, Action action, SpriteText? suppliedLabel = null)
        {
            Width = label.Length > 4 ? 70 : 54;
            Height = 34;
            Action = action;
            Masking = true;
            CornerRadius = 7;
            BorderThickness = 1;
            BorderColour = AimModPalette.Border;
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
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

    private partial class MomentButton : ClickableContainer
    {
        public MomentButton(string time, Action action)
        {
            AutoSizeAxes = Axes.Both;
            Action = action;
            Masking = true;
            CornerRadius = 7;
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

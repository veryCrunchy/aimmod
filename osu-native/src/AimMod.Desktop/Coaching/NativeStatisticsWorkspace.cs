using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.ScoreHistory;
using AimMod.Desktop.Visuals;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Framework.Threading;
using osu.Game.Graphics.Sprites;
using osuTK.Input;

namespace AimMod.Desktop.Coaching;

public partial class NativeStatisticsWorkspace : CompositeDrawable
{
    private const float header_height = 156;
    private const float toolbar_y = 72;
    private const float chip_row_y = 118;
    private const float wide_chart_width = 900;
    private const float inspector_overlay_width = 860;
    private const int visible_row_limit = 100;
    private const double filter_debounce = 60;

    private readonly ILocalLibrarySource source;
    private readonly ILocalLibrarySourceChanged? sourceChanges;
    private readonly Action<LocalReplay> openReplay;
    private readonly Func<LocalReplay, CancellationToken, Task>? openBeatmap;
    private readonly Func<IAccountScoreHistoryService?> accountHistory;
    private readonly ScoreHistorySession history;
    private readonly LatestBackgroundQuery<StatisticsWorkspaceModel> statisticsQuery = new();

    private readonly Bindable<StatisticsTimeRange> timeRange = new(StatisticsTimeRange.All);
    private readonly Bindable<string> modFilter = new(ScoreMods.Any);
    private readonly Bindable<StatisticsRunSort> sort = new(StatisticsRunSort.Recent);
    private readonly Bindable<StatisticsScoreSource> scoreSource = new(StatisticsScoreSource.All);
    private readonly BindableDouble minimumStars = new(0) { MinValue = 0, MaxValue = 10, Default = 0 };
    private readonly BindableDouble maximumStars = new(10) { MinValue = 0, MaxValue = 10, Default = 10 };
    private readonly Bindable<StatisticsResultFilter> resultFilter = new(StatisticsResultFilter.All);
    private readonly Bindable<StatisticsMetric> chartMetric = new(StatisticsMetric.Accuracy);

    private readonly AimModSearchBox search;
    private readonly StatisticsSegmentedControl<StatisticsTimeRange> periodControl;
    private readonly AimModButton filtersButton;
    private readonly FillFlowContainer<Drawable> chipRow;
    private readonly OsuSpriteText countText;
    private readonly StatisticsInfoIcon sourceInfo;
    private readonly FillFlowContainer<Drawable> chips;
    private readonly AimModResetButton clearButton;
    private readonly Container filterLayer;
    private readonly Container filterPopover;
    private readonly ScoreModFilterDropdown modDropdown;

    private readonly Container contentViewport;
    private readonly Container mainColumn;
    private readonly AimModScrollContainer runScroll;
    private readonly WorkspaceStateCard stateCard;
    private readonly FillFlowContainer<Drawable> kpiFlow;
    private readonly Dictionary<StatisticsMetric, StatisticsKpiCard> kpiCards = [];
    private readonly Container chartSection;
    private readonly Container trendCard;
    private readonly OsuSpriteText trendTitle;
    private readonly FillFlowContainer trendLegend;
    private readonly StatisticsSegmentedControl<StatisticsMetric> metricControl;
    private readonly StatisticsTrendChart trendChart;
    private readonly AimModSubsectionHeader playsHeader;
    private readonly StatisticsPlaysHeader tableHeader;
    private readonly FillFlowContainer<Drawable> runList;
    private readonly OsuSpriteText listFooter;

    private readonly Container inspectorColumn;
    private readonly FillFlowContainer<Drawable> inspectorContent;
    private readonly AimModLoadingOverlay loadingOverlay;

    private readonly Dictionary<Guid, StatisticsPlayRow> runRows = new();
    private readonly List<StatisticsPlayRow> visibleRows = new();
    private readonly Dictionary<Guid, LocalReplay> runsById = new();
    private StatisticsEmptyState? emptyState;
    private StatisticsMapIndex mapIndex = StatisticsMapIndex.Empty;
    private StatisticsWorkspaceModel? lastModel;
    private ScheduledDelegate? searchRefresh;
    private CancellationTokenSource? loading;
    private IReadOnlyList<LocalReplay> allRuns = Array.Empty<LocalReplay>();
    private LocalReplay? selected;
    private OnlineAccountScoreHistoryResult? onlineHistory;
    private bool loadedOnce;
    private int renderRevision;
    private bool filtersOpen;
    private bool inspectorOverlay;
    private bool inspectorOverlayOpen;
    private float layoutWidth = -1;
    private float mainLayoutWidth = -1;
    private bool hasHistory = true;

    public NativeStatisticsWorkspace(
        ILocalLibrarySource source,
        Action<LocalReplay> openReplay,
        Func<IAccountScoreHistoryService?>? accountHistory = null,
        Func<LocalReplay, CancellationToken, Task>? openBeatmap = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        history = ScoreHistorySession.For(source);
        this.openReplay = openReplay ?? throw new ArgumentNullException(nameof(openReplay));
        this.openBeatmap = openBeatmap;
        this.accountHistory = accountHistory ?? (() => null);
        sourceChanges = source as ILocalLibrarySourceChanged;
        if (sourceChanges is not null)
            sourceChanges.SourceChanged += sourceChanged;

        RelativeSizeAxes = Axes.Both;

        modDropdown = new ScoreModFilterDropdown(modFilter);
        filterPopover = createFilterPopover();

        InternalChildren = new Drawable[]
        {
            contentViewport = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Top = header_height },
                Children = new Drawable[]
                {
                    mainColumn = new Container
                    {
                        RelativeSizeAxes = Axes.Y,
                        Child = runScroll = new AimModScrollContainer
                        {
                            RelativeSizeAxes = Axes.Both,
                            Padding = new MarginPadding { Right = 4 },
                            Child = new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Direction = FillDirection.Vertical,
                                Spacing = new(AimModVisualStyle.SectionSpacing),
                                Padding = new MarginPadding { Bottom = 28, Right = 12 },
                                Children = new Drawable[]
                                {
                                    stateCard = new WorkspaceStateCard(),
                                    kpiFlow = new FillFlowContainer<Drawable>
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        AutoSizeAxes = Axes.Y,
                                        Direction = FillDirection.Full,
                                        Margin = new MarginPadding { Bottom = -AimModVisualStyle.RelatedSpacing },
                                    },
                                    chartSection = new Container
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        Child = trendCard = chartCard(),
                                    },
                                    new FillFlowContainer
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        AutoSizeAxes = Axes.Y,
                                        Direction = FillDirection.Vertical,
                                        Spacing = new(AimModVisualStyle.RelatedSpacing),
                                        Children = new Drawable[]
                                        {
                                            playsHeader = new AimModSubsectionHeader("Plays"),
                                            tableHeader = new StatisticsPlaysHeader(sort),
                                            runList = new FillFlowContainer<Drawable>
                                            {
                                                RelativeSizeAxes = Axes.X,
                                                AutoSizeAxes = Axes.Y,
                                                Direction = FillDirection.Vertical,
                                                Spacing = new(4),
                                            },
                                            listFooter = StatisticsChartFormat.Text(string.Empty, 12, AimModPalette.Muted),
                                        },
                                    },
                                },
                            },
                        },
                    },
                    inspectorColumn = new Container
                    {
                        Anchor = Anchor.TopRight,
                        Origin = Anchor.TopRight,
                        RelativeSizeAxes = Axes.Y,
                        Masking = true,
                        CornerRadius = AimModVisualStyle.CardRadius,
                        Children = new Drawable[]
                        {
                            new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                            new AimModScrollContainer
                            {
                                RelativeSizeAxes = Axes.Both,
                                Padding = new MarginPadding { Right = 4, Vertical = 4 },
                                Child = inspectorContent = new FillFlowContainer<Drawable>
                                {
                                    RelativeSizeAxes = Axes.X,
                                    AutoSizeAxes = Axes.Y,
                                    Direction = FillDirection.Vertical,
                                    Spacing = new(AimModVisualStyle.RelatedSpacing),
                                    Padding = new MarginPadding { Left = 16, Top = 12, Bottom = 16, Right = 12 },
                                },
                            },
                        },
                    },
                },
            },
            // The toolbar and its popover are drawn above the scrolling content.
            new Container
            {
                RelativeSizeAxes = Axes.X,
                Height = header_height,
                Depth = -10,
                Children = new Drawable[]
                {
                    new AimModSectionHeader("Statistics", "Your accuracy, PP and misses over time."),
                    new Container
                    {
                        RelativeSizeAxes = Axes.X,
                        Y = toolbar_y,
                        Height = AimModVisualStyle.ControlHeight,
                        Children = new Drawable[]
                        {
                            search = new AimModSearchBox { PlaceholderText = "Search title, artist, difficulty or mod" },
                            periodControl = new StatisticsSegmentedControl<StatisticsTimeRange>(timeRange,
                            [
                                (StatisticsTimeRange.Days30, "30 days"), (StatisticsTimeRange.Days90, "90 days"),
                                (StatisticsTimeRange.Year, "Year"), (StatisticsTimeRange.All, "All time"),
                            ]),
                            filtersButton = new AimModButton("Filters", toggleFilters) { Anchor = Anchor.TopRight, Origin = Anchor.TopRight },
                        },
                    },
                    chipRow = new FillFlowContainer<Drawable>
                    {
                        RelativeSizeAxes = Axes.X,
                        Y = chip_row_y,
                        Height = 28,
                        Direction = FillDirection.Horizontal,
                        Spacing = new(AimModVisualStyle.RelatedSpacing),
                        Children = new Drawable[]
                        {
                            countText = StatisticsChartFormat.Text("Loading plays...", 13, AimModPalette.Text, "SemiBold").With(t => { t.Anchor = Anchor.CentreLeft; t.Origin = Anchor.CentreLeft; }),
                            sourceInfo = new StatisticsInfoIcon { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Margin = new MarginPadding { Right = 8 } },
                            chips = new FillFlowContainer<Drawable>
                            {
                                AutoSizeAxes = Axes.Both,
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.CentreLeft,
                                Direction = FillDirection.Horizontal,
                                Spacing = new(6),
                            },
                            clearButton = new AimModResetButton(resetFilters, "Clear all") { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Height = 28, Width = 84, Alpha = 0 },
                        },
                    },
                },
            },
            filterLayer = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Depth = -20,
                Alpha = 0,
                Children = new Drawable[]
                {
                    new ClickableContainer { RelativeSizeAxes = Axes.Both, Action = closeFilters },
                    filterPopover,
                },
            },
            loadingOverlay = new AimModLoadingOverlay(),
        };

        trendCard.AddRange(new Drawable[]
        {
            trendTitle = StatisticsChartFormat.Text("Accuracy over time", 16, AimModPalette.Text, "SemiBold").With(t => t.Position = new(16, 14)),
            trendLegend = new FillFlowContainer
            {
                Position = new(16, 38),
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Horizontal,
                Spacing = new(14),
            },
            metricControl = new StatisticsSegmentedControl<StatisticsMetric>(chartMetric,
            [
                (StatisticsMetric.Accuracy, "Accuracy"), (StatisticsMetric.Performance, "PP"),
                (StatisticsMetric.Misses, "Misses"), (StatisticsMetric.Stars, "Stars"),
            ], AimModVisualStyle.CompactControlHeight)
            {
                Anchor = Anchor.TopRight,
                Origin = Anchor.TopRight,
                Position = new(-12, 12),
            },
            new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Top = 64, Left = 8, Right = 12, Bottom = 10 },
                Child = trendChart = new StatisticsTrendChart(),
            },
        });

        foreach (StatisticsMetric metric in Enum.GetValues<StatisticsMetric>())
        {
            StatisticsMetric value = metric;
            var card = new StatisticsKpiCard(metric, () => chartMetric.Value = value);
            kpiCards[metric] = card;
            kpiFlow.Add(new Container
            {
                RelativeSizeAxes = Axes.X,
                Height = 104,
                Padding = new MarginPadding { Right = AimModVisualStyle.RelatedSpacing, Bottom = AimModVisualStyle.RelatedSpacing },
                Child = card,
            });
        }

        trendChart.ResolvePlay = id => runsById.GetValueOrDefault(id);
        trendChart.PlaySelected += id =>
        {
            if (runsById.TryGetValue(id, out LocalReplay? replay))
                select(replay, openDetails: true);
        };

        search.Current.BindValueChanged(_ => scheduleRender(150));
        timeRange.BindValueChanged(_ => scheduleRender(filter_debounce));
        modFilter.BindValueChanged(_ => scheduleRender(filter_debounce));
        sort.BindValueChanged(_ => scheduleRender(filter_debounce));
        scoreSource.BindValueChanged(_ => scheduleRender(filter_debounce));
        minimumStars.BindValueChanged(_ => scheduleRender(150));
        maximumStars.BindValueChanged(_ => scheduleRender(150));
        resultFilter.BindValueChanged(_ => scheduleRender(filter_debounce));
        chartMetric.BindValueChanged(_ => applyChartMetric());
        showEmptyInspector();
        updateChips();
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        search.PlaceholderText = "Search title, artist, difficulty or mod";
        load();
    }

    protected override void Update()
    {
        base.Update();
        layoutToolbar(DrawWidth);
        if (contentViewport.DrawWidth != layoutWidth)
        {
            layoutWidth = contentViewport.DrawWidth;
            layoutColumns(Math.Max(1, layoutWidth));
        }

        float mainWidth = runScroll.ChildSize.X;
        if (mainWidth != mainLayoutWidth && mainWidth > 1)
        {
            mainLayoutWidth = mainWidth;
            layoutMain(mainWidth);
        }
    }

    private void layoutColumns(float available)
    {
        bool overlay = available < inspector_overlay_width;
        inspectorOverlay = overlay;
        if (!hasHistory)
        {
            // Nothing to inspect yet: give the empty state the whole width.
            inspectorColumn.Alpha = 0;
            mainColumn.Alpha = 1;
            mainColumn.Width = available;
            return;
        }

        if (overlay)
        {
            inspectorColumn.Width = available;
            mainColumn.Width = available;
            inspectorColumn.Alpha = inspectorOverlayOpen ? 1 : 0;
            mainColumn.Alpha = inspectorOverlayOpen ? 0 : 1;
        }
        else
        {
            inspectorOverlayOpen = false;
            float inspectorWidth = Math.Clamp(available * 0.27f, 280, 380);
            inspectorColumn.Width = inspectorWidth;
            inspectorColumn.Alpha = 1;
            mainColumn.Alpha = 1;
            mainColumn.Width = Math.Max(0, available - inspectorWidth - AimModVisualStyle.SectionSpacing);
        }

        if (selected is not null)
            showInspector(selected);
    }

    private void layoutToolbar(float width)
    {
        float buttons = filtersButton.DrawWidth + AimModVisualStyle.RelatedSpacing;
        float period = periodControl.DrawWidth + AimModVisualStyle.RelatedSpacing;
        float searchWidth = Math.Max(160, width - buttons - period);
        if (search.Width != searchWidth)
            search.Width = searchWidth;
        periodControl.X = searchWidth + AimModVisualStyle.RelatedSpacing;
    }

    private void layoutMain(float width)
    {
        bool twoByTwo = width < 760;
        foreach (Drawable cell in kpiFlow)
            cell.Width = twoByTwo ? 0.5f : 0.25f;

        float trendHeight = width >= wide_chart_width ? 380 : 340;
        trendCard.Width = width - AimModVisualStyle.RelatedSpacing;
        trendCard.Height = trendHeight;
        chartSection.Height = trendHeight;

        // Hide the chart legend before it collides with the metric selector.
        trendLegend.Alpha = trendCard.Width - metricControl.DrawWidth - 40 > trendLegend.DrawWidth ? 1 : 0;
        trendTitle.Y = trendLegend.Alpha > 0 ? 14 : 20;
    }

    /// <summary>Re-reads the local history, reusing cached results when nothing changed.</summary>
    public void RefreshHistory()
    {
        if (IsLoaded && !IsDisposed)
            load(refresh: true);
    }

    private void reload()
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
        CancellationToken token = loading.Token;
        stateCard.Dismiss();
        if (!loadedOnce)
            loadingOverlay.ShowLoading("Loading statistics", "Reading your plays");
        else
            countText.Text = "Refreshing plays...";
        var service = accountHistory();
        _ = Task.Run(() => loadAsync(service, refresh, token));
    }

    private async Task loadAsync(IAccountScoreHistoryService? service, bool refresh, CancellationToken token)
    {
        try
        {
            Task<StatisticsHistoryLoadResult> localTask = history.GetLocalAsync(refresh, token);
            Task<OnlineAccountScoreHistoryResult?> onlineTask = loadOnlineAsync(service, token);
            await Task.WhenAll(localTask, onlineTask).ConfigureAwait(false);
            StatisticsHistoryLoadResult result = await localTask.ConfigureAwait(false);
            OnlineAccountScoreHistoryResult? online = await onlineTask.ConfigureAwait(false);
            IReadOnlyList<LocalReplay> merged = history.Merge(result.Runs, online?.Scores ?? []);
            IReadOnlyList<ScoreModChoice> choices = ReferenceEquals(merged, allRuns) ? [] : ScoreMods.Choices(merged);
            StatisticsMapIndex index = ReferenceEquals(merged, allRuns) ? mapIndex : new StatisticsMapIndex(merged);
            if (!IsDisposed)
                Schedule(() => { if (!IsDisposed && !token.IsCancellationRequested) applyLoaded(merged, online, choices, index); });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Statistics could not be loaded: {error}");
            if (!IsDisposed)
                Schedule(() =>
                {
                    if (IsDisposed || token.IsCancellationRequested) return;
                    loadingOverlay.HideLoading();
                    countText.Text = "Plays could not be loaded";
                    stateCard.Show(FontAwesome.Solid.ExclamationTriangle, "Statistics could not be loaded",
                        "AimMod could not read your osu! plays. Check the osu! installation in Settings, then retry.",
                        AimModPalette.Danger, "Retry", reload);
                });
        }
    }

    private async Task<OnlineAccountScoreHistoryResult?> loadOnlineAsync(
        IAccountScoreHistoryService? service,
        CancellationToken cancellationToken)
    {
        try
        {
            return await history.GetOnlineAsync(service, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private void applyLoaded(IReadOnlyList<LocalReplay> runs, OnlineAccountScoreHistoryResult? online, IReadOnlyList<ScoreModChoice> choices, StatisticsMapIndex index)
    {
        loadedOnce = true;
        onlineHistory = online;
        loadingOverlay.HideLoading();
        if (ReferenceEquals(allRuns, runs))
        {
            if (lastModel is not null)
                applyModel(lastModel);
            else
                render();
            return;
        }

        allRuns = runs;
        mapIndex = index;
        runsById.Clear();
        foreach (LocalReplay run in runs)
            runsById.TryAdd(run.ScoreId, run);
        modDropdown.SetChoices(choices);
        render();
    }

    private void scheduleRender(double delay)
    {
        updateChips();
        searchRefresh?.Cancel();
        searchRefresh = Scheduler.AddDelayed(render, delay);
    }

    private void render()
    {
        searchRefresh?.Cancel();
        searchRefresh = null;
        var runs = allRuns;
        int revision = ++renderRevision;
        var query = new StatisticsRunQuery(
            search.Current.Value,
            timeRange.Value,
            StatisticsModFilter.Any,
            sort.Value,
            scoreSource.Value,
            minimumStars.Value,
            maximumStars.Value >= maximumStars.MaxValue ? 100 : maximumStars.Value,
            resultFilter.Value == StatisticsResultFilter.MissFree,
            modFilter.Value);
        statisticsQuery.Submit(token =>
        {
            token.ThrowIfCancellationRequested();
            return StatisticsWorkspaceModel.Build(runs, query);
        }, model =>
        {
            if (!IsDisposed) Schedule(() =>
            {
                if (!IsDisposed && revision == renderRevision) applyModel(model);
            });
        }, error =>
        {
            Console.Error.WriteLine($"Statistics filtering failed: {error}");
            if (!IsDisposed) Schedule(() =>
            {
                if (IsDisposed || revision != renderRevision) return;
                stateCard.Show(FontAwesome.Solid.ExclamationTriangle, "These filters could not be applied",
                    "Try again, or clear the filters.", AimModPalette.Danger, "Retry", render);
            });
        });
    }

    private bool filtersActive => search.Current.Value.Length > 0 || timeRange.Value != StatisticsTimeRange.All || popoverFilterCount > 0;

    private int popoverFilterCount => (modFilter.Value != ScoreMods.Any ? 1 : 0) + (scoreSource.Value != StatisticsScoreSource.All ? 1 : 0)
                                      + (!minimumStars.IsDefault || !maximumStars.IsDefault ? 1 : 0)
                                      + (resultFilter.Value != StatisticsResultFilter.All ? 1 : 0);

    private void resetFilters()
    {
        search.Current.Value = string.Empty;
        timeRange.Value = StatisticsTimeRange.All;
        modFilter.Value = ScoreMods.Any;
        sort.Value = StatisticsRunSort.Recent;
        scoreSource.Value = StatisticsScoreSource.All;
        minimumStars.SetDefault();
        maximumStars.SetDefault();
        resultFilter.Value = StatisticsResultFilter.All;
    }

    private void applyModel(StatisticsWorkspaceModel model)
    {
        lastModel = model;
        stateCard.Dismiss();
        StatisticsInsights insights = model.Insights;
        string count = model.Runs.Count == allRuns.Count || allRuns.Count == 0
            ? $"{model.Runs.Count:N0} {(model.Runs.Count == 1 ? "play" : "plays")}"
            : $"{model.Runs.Count:N0} of {allRuns.Count:N0} plays";
        countText.Text = allRuns.Count == 0 ? string.Empty : model.Runs.Count < 2 ? count : $"{count}  ·  {dateRange(model.Runs)}";
        if (hasHistory != allRuns.Count > 0)
        {
            hasHistory = allRuns.Count > 0;
            layoutWidth = -1;
        }

        kpiFlow.Alpha = chartSection.Alpha = playsHeader.Alpha = tableHeader.Alpha = sourceInfo.Alpha = hasHistory ? 1 : 0;
        kpiFlow.AutoSizeAxes = hasHistory ? Axes.Y : Axes.None;
        if (!hasHistory)
        {
            kpiFlow.Height = 0;
            chartSection.Height = 0;
        }
        else
        {
            mainLayoutWidth = -1;
        }
        sourceInfo.TooltipText = sourceSummary(insights);
        playsHeader.Detail = model.Runs.Count > visible_row_limit ? $"Newest {visible_row_limit} shown" : null;
        if (sort.Value != StatisticsRunSort.Recent && model.Runs.Count > visible_row_limit)
            playsHeader.Detail = $"Top {visible_row_limit} shown";

        foreach ((StatisticsMetric metric, StatisticsKpiCard card) in kpiCards)
            card.Set(insights.For(metric), kpiDetail(metric, model, insights));

        applyChartMetric();

        if (selected is null || !model.Runs.Any(run => run.ScoreId == selected.ScoreId))
            selected = model.Runs.FirstOrDefault();

        renderRuns(model.Runs);
        listFooter.Text = model.Runs.Count > visible_row_limit
            ? $"{model.Runs.Count - visible_row_limit:N0} more plays match. Search or filter to find a specific play."
            : string.Empty;
        listFooter.Alpha = model.Runs.Count > visible_row_limit ? 1 : 0;
        if (selected is null)
            showEmptyInspector();
        else
            showInspector(selected);
    }

    private static string dateRange(IReadOnlyList<LocalReplay> runs)
    {
        DateTimeOffset first = runs.Min(run => run.PlayedAt).ToLocalTime();
        DateTimeOffset last = runs.Max(run => run.PlayedAt).ToLocalTime();
        string format = first.Year == last.Year ? "d MMM" : "d MMM yyyy";
        return $"{first.ToString(format, System.Globalization.CultureInfo.InvariantCulture)} – {last.ToString(format, System.Globalization.CultureInfo.InvariantCulture)}";
    }

    private static string kpiDetail(StatisticsMetric metric, StatisticsWorkspaceModel model, StatisticsInsights insights) => metric switch
    {
        StatisticsMetric.Accuracy => model.BestAccuracy is { } best ? $"best {best * 100:0.00}%" : string.Empty,
        StatisticsMetric.Performance => insights.MissingPpCount > 0 ? $"{insights.MissingPpCount:N0} without PP" : string.Empty,
        StatisticsMetric.Misses => insights.MissFreeRate is { } rate ? $"{rate:0}% miss-free" : string.Empty,
        _ => string.Empty,
    };

    private string sourceSummary(StatisticsInsights insights)
    {
        string online = onlineHistory is null
            ? "Online scores are unavailable. Sign in from Settings to include them."
            : "Online scores include only your best and recent plays.";
        return $"On this PC: {insights.LocalCount:N0} plays\nSubmitted online: {insights.SubmittedCount:N0} ({insights.OnlineOnlyCount:N0} only online)\n{online}";
    }

    private void applyChartMetric()
    {
        StatisticsMetric metric = chartMetric.Value;
        foreach ((StatisticsMetric key, StatisticsKpiCard card) in kpiCards)
            card.SetSelected(key == metric);
        StatisticsMetricView view = lastModel?.Insights.For(metric) ?? StatisticsMetricView.Empty(metric);
        trendTitle.Text = $"{StatisticsChartFormat.Name(metric)} over time";
        trendChart.SetView(view, selected?.ScoreId);
        updateLegend(view);
    }

    private void updateLegend(StatisticsMetricView view)
    {
        trendLegend.Clear();
        if (view.Plays.Count == 0)
            return;
        trendLegend.Add(legendItem(new CircularContainer { Size = new(6), Masking = true, Child = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Cyan.Opacity(0.55f) } }, "Each play"));
        if (view.RollingWindow > 1)
            trendLegend.Add(legendItem(new Container
            {
                Size = new(14, 8),
                Children = new Drawable[]
                {
                    new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Cyan.Opacity(0.22f) },
                    new Box { RelativeSizeAxes = Axes.X, Height = 1.6f, Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Colour = AimModPalette.Cyan },
                },
            }, $"{view.RollingWindow}-play average"));
        trendLegend.Add(legendItem(new FillFlowContainer
        {
            AutoSizeAxes = Axes.Both,
            Direction = FillDirection.Horizontal,
            Spacing = new(2),
            Children = Enumerable.Range(0, 3).Select(_ => (Drawable)new Box { Size = new(4, 1.5f), Colour = AimModPalette.Text.Opacity(0.6f) }).ToArray(),
        }, $"{(view.Metric == StatisticsMetric.Performance ? "Median" : "Average")} {StatisticsChartFormat.Value(view.Metric, StatisticsTrendChart.ReferenceValue(view))}"));
        mainLayoutWidth = -1;
    }

    private static Drawable legendItem(Drawable mark, string label) => new FillFlowContainer
    {
        AutoSizeAxes = Axes.Both,
        Direction = FillDirection.Horizontal,
        Spacing = new(6),
        Children = new[]
        {
            mark.With(d => { d.Anchor = Anchor.CentreLeft; d.Origin = Anchor.CentreLeft; }),
            StatisticsChartFormat.Text(label, 11, AimModPalette.Muted).With(d => { d.Anchor = Anchor.CentreLeft; d.Origin = Anchor.CentreLeft; }),
        },
    };

    /// <summary>Updates the visible rows in place: unchanged plays keep their row and only its order changes.</summary>
    private void renderRuns(IReadOnlyList<LocalReplay> runs)
    {
        var previous = new Dictionary<Guid, StatisticsPlayRow>(runRows);
        runRows.Clear();
        visibleRows.Clear();
        if (emptyState is not null)
        {
            runList.Remove(emptyState, true);
            emptyState = null;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        int position = 0;
        foreach (LocalReplay replay in runs.Take(visible_row_limit))
        {
            if (!previous.Remove(replay.ScoreId, out StatisticsPlayRow? row) || !ReferenceEquals(row.Replay, replay))
            {
                if (row is not null)
                    runList.Remove(row, true);
                LocalReplay current = replay;
                row = new StatisticsPlayRow(replay, () => select(current, openDetails: true), now);
                runList.Add(row);
            }

            row.SetSelected(selected?.ScoreId == replay.ScoreId);
            runList.SetLayoutPosition(row, position++);
            runRows[replay.ScoreId] = row;
            visibleRows.Add(row);
        }

        foreach (StatisticsPlayRow unused in previous.Values)
            runList.Remove(unused, true);

        if (runs.Count == 0)
        {
            bool noHistory = allRuns.Count == 0;
            runList.Add(emptyState = new StatisticsEmptyState(
                noHistory ? "No plays yet" : "No plays match these filters",
                noHistory
                    ? "Play a map in osu!, or connect your osu! installation and account in Settings."
                    : "Try a longer period or fewer filters.",
                !noHistory && filtersActive ? resetFilters : null));
        }
    }

    private void select(LocalReplay? replay, bool openDetails = false)
    {
        if (openDetails && inspectorOverlay && replay is not null)
            setInspectorOverlay(true);
        if (selected == replay) return;
        if (selected is not null && runRows.TryGetValue(selected.ScoreId, out StatisticsPlayRow? previousRow))
            previousRow.SetSelected(false);
        selected = replay;
        if (replay is not null && runRows.TryGetValue(replay.ScoreId, out StatisticsPlayRow? row))
            row.SetSelected(true);
        trendChart.SetView(trendChart.View, replay?.ScoreId);
        if (replay is null)
            showEmptyInspector();
        else
            showInspector(replay);
    }

    private void setInspectorOverlay(bool open)
    {
        inspectorOverlayOpen = open && inspectorOverlay;
        if (!inspectorOverlay)
            return;
        inspectorColumn.FadeTo(inspectorOverlayOpen ? 1 : 0, AimModVisualStyle.FastTransition);
        mainColumn.FadeTo(inspectorOverlayOpen ? 0 : 1, AimModVisualStyle.FastTransition);
        if (selected is not null)
            showInspector(selected);
    }

    protected override bool OnKeyDown(KeyDownEvent e)
    {
        if (e.ControlPressed && e.Key == Key.F)
        {
            GetContainingFocusManager()?.ChangeFocus(search);
            return true;
        }

        if (e.Key == Key.Escape && filtersOpen)
        {
            closeFilters();
            return true;
        }

        if (e.Key == Key.Escape && inspectorOverlayOpen)
        {
            setInspectorOverlay(false);
            return true;
        }

        if (e.Key is Key.Up or Key.Down && visibleRows.Count > 0 && !e.ControlPressed && !e.AltPressed)
        {
            int index = selected is null ? -1 : visibleRows.FindIndex(row => row.Replay.ScoreId == selected.ScoreId);
            index = Math.Clamp(index + (e.Key == Key.Down ? 1 : -1), 0, visibleRows.Count - 1);
            select(visibleRows[index].Replay);
            runScroll.ScrollIntoView(visibleRows[index]);
            return true;
        }

        if (e.Key is Key.Enter or Key.KeypadEnter && selected is { HasReplayFile: true } replay)
        {
            openReplay(replay);
            return true;
        }

        if (e.Key == Key.Escape && search.Current.Value.Length > 0)
        {
            search.Current.Value = string.Empty;
            return true;
        }

        return base.OnKeyDown(e);
    }

    private void sourceChanged()
    {
        if (!IsDisposed)
            Schedule(() => load());
    }

    protected override void Dispose(bool isDisposing)
    {
        statisticsQuery.Dispose();
        searchRefresh?.Cancel();
        loading?.Cancel();
        loading?.Dispose();
        if (sourceChanges is not null)
            sourceChanges.SourceChanged -= sourceChanged;
        base.Dispose(isDisposing);
    }

    private static Container chartCard() => new()
    {
        Masking = true,
        CornerRadius = AimModVisualStyle.CardRadius,
        BorderThickness = 1,
        BorderColour = AimModPalette.Border,
        Child = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
    };

    /// <summary>Puts the page into a named state for off-screen visual checks.</summary>
    internal void ApplyCaptureState(string state)
    {
        switch (state)
        {
            case "filters":
                openFilters();
                break;
            case "filters-menu":
                openFilters();
                modDropdown.SetChoices(new[] { new ScoreModChoice(ScoreMods.Any, "All mods") }
                                       .Concat(Enumerable.Range(1, 40).Select(i => new ScoreModChoice("capture:" + i, $"Exact: HD + DT (setup {i})"))).ToArray());
                Scheduler.AddDelayed(() => ((osu.Framework.Graphics.UserInterface.Menu)typeof(osu.Framework.Graphics.UserInterface.Dropdown<string>)
                                                .GetField("Menu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                                                .GetValue(modDropdown)!).Open(), 200);
                break;
            case "hover":
                Scheduler.AddDelayed(() => trendChart.ShowHoverAt(0.7f), 100);
                break;
            case "metric-pp":
                chartMetric.Value = StatisticsMetric.Performance;
                break;
            case "metric-misses":
                chartMetric.Value = StatisticsMetric.Misses;
                break;
            case "period-30":
                timeRange.Value = StatisticsTimeRange.Days30;
                scoreSource.Value = StatisticsScoreSource.Local;
                minimumStars.Value = 4;
                break;
            case "details":
                if (visibleRows.Count > 3)
                    select(visibleRows[3].Replay, openDetails: true);
                break;
            case "plays":
                runScroll.ScrollTo(playsHeader, false);
                break;
        }
    }

    private enum StatisticsResultFilter
    {
        All,
        MissFree,
    }

    private sealed partial class StatisticsEmptyState : CompositeDrawable
    {
        public StatisticsEmptyState(string heading, string detail, Action? reset = null)
        {
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;
            var card = new WorkspaceStateCard();
            InternalChild = card;
            card.Show(reset is null ? FontAwesome.Solid.Music : FontAwesome.Solid.Filter, heading, detail,
                actionLabel: reset is null ? null : "Clear filters", action: reset);
        }
    }
}

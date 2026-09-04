using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.ScoreHistory;
using AimMod.Desktop.Visuals;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;

namespace AimMod.Desktop.Coaching;

public partial class NativeStatisticsWorkspace : CompositeDrawable
{
    private readonly ILocalLibrarySource source;
    private readonly ILocalLibrarySourceChanged? sourceChanges;
    private readonly Action<LocalReplay> openReplay;
    private readonly Func<IAccountScoreHistoryService?> accountHistory;
    private readonly ShearedFilterTextBox search;
    private readonly Bindable<StatisticsTimeRange> timeRange = new(StatisticsTimeRange.All);
    private readonly Bindable<StatisticsModFilter> modFilter = new(StatisticsModFilter.Any);
    private readonly Bindable<StatisticsRunSort> sort = new(StatisticsRunSort.Recent);
    private readonly Bindable<StatisticsScoreSource> scoreSource = new(StatisticsScoreSource.All);
    private readonly Bindable<StatisticsStarBand> starBand = new(StatisticsStarBand.Any);
    private readonly Bindable<StatisticsResultFilter> resultFilter = new(StatisticsResultFilter.All);
    private readonly SpriteText scopeText;
    private readonly SpriteText resultText;
    private readonly Container filterBar;
    private readonly GridContainer metricGrid;
    private readonly MetricCard averageAccuracy;
    private readonly MetricCard medianPp;
    private readonly MetricCard missFree;
    private readonly MetricCard averageStars;
    private readonly StatisticsGraphCard accuracyGraph;
    private readonly StatisticsGraphCard ppGraph;
    private readonly StatisticsGraphCard starsGraph;
    private readonly StatisticsGraphCard missesGraph;
    private readonly FillFlowContainer<Drawable> runList;
    private readonly Container contentViewport;
    private readonly Container mainColumn;
    private readonly Container inspectorColumn;
    private readonly FillFlowContainer<Drawable> inspectorContent;
    private readonly AimModLoadingOverlay loadingOverlay;

    private CancellationTokenSource? loading;
    private IReadOnlyList<LocalReplay> allRuns = Array.Empty<LocalReplay>();
    private IReadOnlyList<LocalReplay> visibleRuns = Array.Empty<LocalReplay>();
    private LocalReplay? selected;
    private OnlineAccountScoreHistoryResult? onlineHistory;

    public NativeStatisticsWorkspace(
        ILocalLibrarySource source,
        Action<LocalReplay> openReplay,
        Func<IAccountScoreHistoryService?>? accountHistory = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.openReplay = openReplay ?? throw new ArgumentNullException(nameof(openReplay));
        this.accountHistory = accountHistory ?? (() => null);
        sourceChanges = source as ILocalLibrarySourceChanged;
        if (sourceChanges is not null)
            sourceChanges.SourceChanged += sourceChanged;

        RelativeSizeAxes = Axes.Both;

        InternalChildren = new Drawable[]
        {
            new Container
            {
                RelativeSizeAxes = Axes.X,
                Height = 242,
                Depth = -10,
                Children = new Drawable[]
                {
                    new AimModSectionHeader(
                        "Statistics",
                        "Explore the unified osu!standard score dataset, combining online best/recent records with replay-rich local attempts.",
                        "performance history"),
                    scopeText = text("Loading score scope...", 10, AimModPalette.Muted).With(drawable => drawable.Y = 62),
                    filterBar = new StatisticsFilterBar
                    {
                        RelativeSizeAxes = Axes.X,
                        Position = new(0, 82),
                        Height = 144,
                        Padding = new MarginPadding { Horizontal = 10 },
                        Children = new Drawable[]
                        {
                            search = new ShearedFilterTextBox
                            {
                                RelativeSizeAxes = Axes.X,
                                PlaceholderText = "Title, artist, difficulty",
                            },
                            new GridContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                Y = 66,
                                Height = 78,
                                ColumnDimensions = Enumerable.Repeat(new Dimension(GridSizeMode.Relative, 1f / 3), 3).ToArray(),
                                RowDimensions = twoEqualRows(),
                                Content = new[]
                                {
                                    new Drawable[]
                                    {
                                        filterField("Period", timeRange, -3),
                                        filterField("Source", scoreSource, -3),
                                        filterField("Sort", sort, -3),
                                    },
                                    new Drawable[]
                                    {
                                        filterField("Mods", modFilter, -2),
                                        filterField("Stars", starBand, -2),
                                        filterField("Result", resultFilter, -2),
                                    },
                                },
                            },
                        },
                    },
                },
            },
            contentViewport = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Top = 242 },
                Children = new Drawable[]
                {
                    mainColumn = new Container
                    {
                        RelativeSizeAxes = Axes.Y,
                        Child = new AimModScrollContainer
                        {
                            RelativeSizeAxes = Axes.Both,
                            Padding = new MarginPadding { Right = 4 },
                            Child = new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Direction = FillDirection.Vertical,
                                Spacing = new(AimModVisualStyle.RowSpacing),
                                Padding = new MarginPadding { Bottom = 28, Right = 12 },
                                Children = new Drawable[]
                                {
                                    new AimModSubsectionHeader("Overview", "Filtered performance at a glance"),
                                    metricGrid = new GridContainer
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        Height = 96,
                                        ColumnDimensions = fourEqualColumns(),
                                        Content = new[]
                                        {
                                            new Drawable[]
                                            {
                                                (averageAccuracy = new MetricCard("Average accuracy", AimModPalette.Cyan, FontAwesome.Solid.Crosshairs)).WithPadding(),
                                                (medianPp = new MetricCard("Median PP", AimModPalette.Pink, FontAwesome.Solid.ChartLine)).WithPadding(),
                                                (missFree = new MetricCard("Miss-free rate", AimModPalette.Success, FontAwesome.Solid.CheckCircle)).WithPadding(),
                                                (averageStars = new MetricCard("Average difficulty", AimModPalette.Yellow, FontAwesome.Solid.Star)).WithPadding(),
                                            },
                                        },
                                    },
                                    new AimModSubsectionHeader("Performance trends", "Hover to inspect an exact play"),
                                    new GridContainer
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        Height = 210,
                                        ColumnDimensions = twoEqualColumns(),
                                        Content = new[] { new Drawable[]
                                        {
                                            (accuracyGraph = new StatisticsGraphCard("Accuracy", AimModPalette.Cyan, value => $"{value:0.00}%")).WithPadding(),
                                            (ppGraph = new StatisticsGraphCard("Performance points", AimModPalette.Pink, value => $"{value:0.#}pp")).WithPadding(),
                                        } },
                                    },
                                    new GridContainer
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        Height = 210,
                                        ColumnDimensions = twoEqualColumns(),
                                        Content = new[] { new Drawable[]
                                        {
                                            (starsGraph = new StatisticsGraphCard("Difficulty played", AimModPalette.Yellow, value => $"{value:0.00} stars")).WithPadding(),
                                            (missesGraph = new StatisticsGraphCard("Misses per play", AimModPalette.Success, value => $"{value:0} misses")).WithPadding(),
                                        } },
                                    },
                                    new Container
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        Height = 40,
                                        Children = new Drawable[]
                                        {
                                            new AimModSubsectionHeader("Plays in view"),
                                            resultText = text(string.Empty, 11, AimModPalette.Muted, "SemiBold").With(label =>
                                            {
                                                label.Anchor = Anchor.CentreRight;
                                                label.Origin = Anchor.CentreRight;
                                            }),
                                        },
                                    },
                                    runList = new FillFlowContainer<Drawable>
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        AutoSizeAxes = Axes.Y,
                                        Direction = FillDirection.Vertical,
                                        Spacing = new(AimModVisualStyle.RelatedSpacing),
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
                            new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                            new AimModScrollContainer
                            {
                                RelativeSizeAxes = Axes.Both,
                                Padding = new MarginPadding { Right = 4, Vertical = 4 },
                                Child = inspectorContent = new FillFlowContainer<Drawable>
                                {
                                    RelativeSizeAxes = Axes.X,
                                    AutoSizeAxes = Axes.Y,
                                    Direction = FillDirection.Vertical,
                                    Spacing = new(AimModVisualStyle.RowSpacing),
                                    Padding = new MarginPadding { Left = 16, Top = 12, Bottom = 16, Right = 16 },
                                },
                            },
                        },
                    },
                },
            },
            loadingOverlay = new AimModLoadingOverlay(),
        };

        search.Current.BindValueChanged(_ => render());
        timeRange.BindValueChanged(_ => render());
        modFilter.BindValueChanged(_ => render());
        sort.BindValueChanged(_ => render());
        scoreSource.BindValueChanged(_ => render());
        starBand.BindValueChanged(_ => render());
        resultFilter.BindValueChanged(_ => render());
        showEmptyInspector();
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        search.PlaceholderText = "Title, artist, difficulty";
        load();
    }

    protected override void Update()
    {
        base.Update();
        float available = Math.Max(720, contentViewport.DrawWidth);
        float inspectorWidth = Math.Clamp(available * 0.285f, 306, 400);
        inspectorColumn.Width = inspectorWidth;
        mainColumn.Width = Math.Max(420, available - inspectorWidth - AimModVisualStyle.SectionSpacing);
    }

    private void load()
    {
        loading?.Cancel();
        loading?.Dispose();
        loading = new CancellationTokenSource();
        CancellationToken token = loading.Token;
        loadingOverlay.ShowLoading("Loading statistics", "Reading local history and cached online score records");
        _ = loadAsync(token);
    }

    private async Task loadAsync(CancellationToken token)
    {
        try
        {
            Task<StatisticsHistoryLoadResult> localTask = StatisticsHistoryLoader.LoadAsync(source, token).AsTask();
            IAccountScoreHistoryService? service = accountHistory();
            Task<OnlineAccountScoreHistoryResult?> onlineTask = service is null
                ? Task.FromResult<OnlineAccountScoreHistoryResult?>(null)
                : loadOnlineAsync(service, token);
            await Task.WhenAll(localTask, onlineTask).ConfigureAwait(false);
            StatisticsHistoryLoadResult result = await localTask.ConfigureAwait(false);
            OnlineAccountScoreHistoryResult? online = await onlineTask.ConfigureAwait(false);
            if (!IsDisposed)
                Schedule(() => applyLoaded(result, online));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (!IsDisposed)
                Schedule(() =>
                {
                    loadingOverlay.HideLoading();
                    scopeText.Text = "Statistics could not be loaded. Reopen this workspace to try again.";
                });
        }
    }

    private static async Task<OnlineAccountScoreHistoryResult?> loadOnlineAsync(
        IAccountScoreHistoryService service,
        CancellationToken cancellationToken)
    {
        try
        {
            return await service.FetchAccountAsync(cancellationToken).ConfigureAwait(false);
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

    private void applyLoaded(StatisticsHistoryLoadResult result, OnlineAccountScoreHistoryResult? online)
    {
        onlineHistory = online;
        allRuns = StatisticsUnifiedScoreAdapter.Merge(result.Runs, online?.Scores ?? []);
        loadingOverlay.HideLoading();
        render();
    }

    private void render()
    {
        if (allRuns.Count == 0)
        {
            scopeText.Text = "No local osu!standard history or cached online scores are available.";
        }

        (double minStars, double maxStars) = starBand.Value switch
        {
            StatisticsStarBand.BelowFour => (0, 4),
            StatisticsStarBand.FourToFive => (4, 5),
            StatisticsStarBand.FiveToSix => (5, 6),
            StatisticsStarBand.SixToSeven => (6, 7),
            StatisticsStarBand.SevenPlus => (7, 100),
            _ => (0, 100),
        };
        StatisticsWorkspaceModel model = StatisticsWorkspaceModel.Build(allRuns, new StatisticsRunQuery(
            search.Current.Value,
            timeRange.Value,
            modFilter.Value,
            sort.Value,
            scoreSource.Value,
            minStars,
            maxStars,
            resultFilter.Value == StatisticsResultFilter.MissFree));

        int localCount = model.UnfilteredRunCount - model.CachedOnlineRunCount;
        scopeText.Text = model.CachedOnlineRunCount > 0
            ? $"Unified scope: {model.CachedOnlineRunCount:N0} online best/recent and {localCount:N0} local records. Online windows are limited, not complete history."
            : onlineHistory is null
                ? $"Unified scope currently contains {localCount:N0} local records; online score service is unavailable."
                : $"Unified scope currently contains {localCount:N0} local records; online best/recent returned no scores.";
        resultText.Text = model.Runs.Count == 1 ? "1 matching play" : $"{model.Runs.Count:N0} matching plays";
        search.StatusText = resultText.Text;
        if (model.Runs.Count == 0)
        {
            averageAccuracy.Set("-", "No plays in this view");
            medianPp.Set("-", "No PP values in this view");
            missFree.Set("-", "No results in this view");
            averageStars.Set("-", "No difficulties in this view");
        }
        else
        {
            averageAccuracy.Set(formatPercent(model.AverageAccuracy), model.BestAccuracy is { } best ? $"Best {best:P2}" : "No accuracy data");
            medianPp.Set(model.MedianPerformancePoints is { } pp ? $"{pp:0.#}pp" : "-", $"{model.PerformancePointRunCount:N0} plays with PP");
            missFree.Set($"{model.MissFreeRate:P0}", $"Best combo {model.BestCombo:N0}x");
            averageStars.Set(model.AverageStarRating is { } stars ? $"{stars:0.00} stars" : "-", compactScore(model.TotalScore));
        }
        updateGraph(accuracyGraph, model.Series.Single(series => series.Key == "statisticsAccuracy"));
        updateGraph(ppGraph, model.Series.Single(series => series.Key == "statisticsPp"));
        updateGraph(starsGraph, model.Series.Single(series => series.Key == "statisticsStars"));
        updateGraph(missesGraph, model.Series.Single(series => series.Key == "statisticsMisses"));
        visibleRuns = model.Runs;
        if (selected is null || !model.Runs.Any(run => run.ScoreId == selected.ScoreId))
            selected = model.Runs.FirstOrDefault();

        renderRuns(model.Runs);
        if (selected is null)
            showEmptyInspector();
        else
            showInspector(selected);
    }

    private void renderRuns(IReadOnlyList<LocalReplay> runs)
    {
        runList.Clear();
        if (runs.Count == 0)
        {
            runList.Add(new StatisticsEmptyState(
                allRuns.Count == 0 ? "No score history yet" : "No plays match these filters",
                allRuns.Count == 0
                    ? "Local and online osu!standard plays will appear here when available."
                    : "Try widening the period, difficulty, result, or source filters."));
            return;
        }

        foreach (LocalReplay replay in runs.Take(100))
            runList.Add(new StatisticsRunRow(replay, selected?.ScoreId == replay.ScoreId, () => select(replay)));
    }

    private void select(LocalReplay? replay)
    {
        selected = replay;
        renderRuns(visibleRuns);
        if (replay is null)
            showEmptyInspector();
        else
            showInspector(replay);
    }

    private void showEmptyInspector()
    {
        inspectorContent.Clear();
        inspectorContent.Add(new StatisticsInspectorEmptyState());
    }

    private void showInspector(LocalReplay replay)
    {
        StatisticsMapSummary map = StatisticsWorkspaceModel.BuildMapSummary(allRuns, replay.BeatmapId);
        inspectorContent.Clear();
        inspectorContent.AddRange(new Drawable[]
        {
            new Box
            {
                RelativeSizeAxes = Axes.X,
                Height = 4,
                Colour = AimModVisualStyle.DifficultyColour(replay.StarRating),
                Margin = new MarginPadding { Bottom = 2 },
            },
            sectionLabel("SELECTED PLAY", AimModPalette.Cyan),
            truncatingText(replay.Title, 20, AimModPalette.Text, Math.Max(220, inspectorColumn.DrawWidth - 40), "Bold"),
            truncatingText($"{replay.Artist}  /  [{replay.Difficulty}]", 12, AimModPalette.Muted, Math.Max(220, inspectorColumn.DrawWidth - 40)),
            new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Horizontal,
                Spacing = new(8),
                Children = pills(replay),
            },
            divider(),
            sectionLabel("RUN SUMMARY", AimModPalette.Muted),
            new GridContainer
            {
                RelativeSizeAxes = Axes.X,
                Height = 104,
                ColumnDimensions = twoEqualColumns(),
                RowDimensions = twoEqualRows(),
                Content = new[]
                {
                    new Drawable[]
                    {
                        inspectorMetric("ACCURACY", replay.Accuracy.ToString("P2"), AimModPalette.Cyan),
                        inspectorMetric("PERFORMANCE", replay.PerformancePoints is { } pp ? $"{pp:0.##}pp" : "Not stored", AimModPalette.Pink),
                    },
                    new Drawable[]
                    {
                        inspectorMetric("SCORE", compactNumber(replay.TotalScore), AimModPalette.Text),
                        inspectorMetric("COMBO / MISSES", $"{replay.MaxCombo:N0}x  /  {replay.MissCount:N0}", AimModPalette.Text),
                    },
                },
            },
            divider(),
            sectionLabel("PLAY DETAILS", AimModPalette.Muted),
            detail("PLAYED", replay.PlayedAt.ToString("dd MMM yyyy  HH:mm")),
            detail("SOURCE", replay.IsLocallyStored
                ? replay.OnlineScoreId > 0 ? "Local + cached online" : "Local score history"
                : "Cached online best/recent"),
            divider(),
            sectionLabel("DIFFICULTY HISTORY", AimModPalette.Pink),
            detail("PLAYS", map.PlayCount.ToString("N0")),
            detail("AVERAGE / BEST", $"{formatPercent(map.AverageAccuracy)}  /  {formatPercent(map.BestAccuracy)}"),
            detail("CHANGE", map.AccuracyChange is { } change ? $"{change * 100:+0.00;-0.00;0.00} points first to latest" : "More plays needed"),
            detail("BEST PP", map.BestPerformancePoints is { } bestPp ? $"{bestPp:0.##}pp" : "Not stored"),
            detail("MISS-FREE", map.MissFreeRate.ToString("P0")),
            detail("BEST COMBO", $"{map.BestCombo:N0}x"),
            replay.HasReplayFile ? actionButton("Open replay", () => openReplay(replay)) : text("Replay file is unavailable for this score.", 11, AimModPalette.Muted),
        });
    }

    private static Drawable[] pills(LocalReplay replay)
    {
        var result = new List<Drawable>();
        if (double.IsFinite(replay.StarRating))
            result.Add(new AimModDifficultyPill(replay.StarRating));
        else
            result.Add(new AimModPill("Stars unknown"));
        if (replay.Mods.Count == 0)
            result.Add(new AimModPill("No Mod"));
        else
            result.AddRange(replay.Mods.Take(4).Select(mod => new AimModPill(mod, AimModPillTone.Accent)));
        return result.ToArray();
    }

    private static Drawable inspectorMetric(string heading, string value, Colour4 colour) => new Container
    {
        RelativeSizeAxes = Axes.Both,
        Padding = new MarginPadding { Right = 10, Bottom = 8 },
        Child = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new(3),
            Children = new Drawable[]
            {
                text(heading, 9, AimModPalette.Muted, "Bold"),
                text(value, 17, colour, "Bold"),
            },
        },
    };

    private static Drawable detail(string heading, string value) => new FillFlowContainer
    {
        RelativeSizeAxes = Axes.X,
        AutoSizeAxes = Axes.Y,
        Direction = FillDirection.Vertical,
        Spacing = new(3),
        Children = new Drawable[]
        {
            text(heading, 9, AimModPalette.Muted, "Bold"),
            text(value, 14, AimModPalette.Text, "SemiBold"),
        },
    };

    private static Drawable divider() => new Box
    {
        RelativeSizeAxes = Axes.X,
        Height = 1,
        Colour = AimModPalette.Border,
        Margin = new MarginPadding { Vertical = 3 },
    };

    private static Drawable actionButton(string label, Action action) => new ClickableContainer
    {
        RelativeSizeAxes = Axes.X,
        Height = AimModVisualStyle.ControlHeight,
        Action = action,
        Masking = true,
        CornerRadius = AimModVisualStyle.ControlRadius,
        Children = new Drawable[]
        {
            new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Pink },
            text(label, 12, AimModPalette.Canvas, "Bold").With(text =>
            {
                text.Anchor = Anchor.Centre;
                text.Origin = Anchor.Centre;
            }),
        },
    };

    private static void updateGraph(StatisticsGraphCard graph, CoachingChartSeries series) => graph.SetSeries(series.Points);

    private void sourceChanged()
    {
        if (!IsDisposed)
            Schedule(load);
    }

    protected override void Dispose(bool isDisposing)
    {
        loading?.Cancel();
        loading?.Dispose();
        if (sourceChanges is not null)
            sourceChanges.SourceChanged -= sourceChanged;
        base.Dispose(isDisposing);
    }

    private static Drawable filterField<T>(string heading, Bindable<T> current, float depth)
        where T : struct, Enum => new Container
    {
        RelativeSizeAxes = Axes.Both,
        // Menus in the upper row must draw and receive input above the lower row.
        Depth = depth,
        Padding = new MarginPadding { Right = 16 },
        Child = new StatisticsFilterDropdown<T>(heading, current),
    };

    private static SpriteText sectionLabel(string value, Colour4 colour) => text(value, 10, colour, "Bold");

    private static Dimension[] fourEqualColumns() => Enumerable.Repeat(new Dimension(GridSizeMode.Relative, 0.25f), 4).ToArray();
    private static Dimension[] twoEqualColumns() => Enumerable.Repeat(new Dimension(GridSizeMode.Relative, 0.5f), 2).ToArray();
    private static Dimension[] twoEqualRows() => Enumerable.Repeat(new Dimension(GridSizeMode.Relative, 0.5f), 2).ToArray();
    private static string formatPercent(double? value) => value is { } number ? number.ToString("P2") : "-";
    private static string compactNumber(long value) => value switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000d:0.##}B",
        >= 1_000_000 => $"{value / 1_000_000d:0.##}M",
        >= 1_000 => $"{value / 1_000d:0.##}K",
        _ => value.ToString("N0"),
    };
    private static string compactScore(long value) => value switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000d:0.##}B total score",
        >= 1_000_000 => $"{value / 1_000_000d:0.##}M total score",
        >= 1_000 => $"{value / 1_000d:0.##}K total score",
        _ => $"{value:N0} total score",
    };

    private static OsuSpriteText text(string value, float size, Colour4 colour, string weight = "Regular") => new()
    {
        Text = value,
        Font = new FontUsage(size: size, weight: weight),
        Colour = colour,
    };

    private static TruncatingSpriteText truncatingText(string value, float size, Colour4 colour, float maxWidth, string weight = "Regular") => new()
    {
        Text = value,
        Font = new FontUsage(size: size, weight: weight),
        Colour = colour,
        MaxWidth = maxWidth,
    };

    private enum StatisticsStarBand
    {
        Any,
        BelowFour,
        FourToFive,
        FiveToSix,
        SixToSeven,
        SevenPlus,
    }

    private enum StatisticsResultFilter
    {
        All,
        MissFree,
    }

    private sealed partial class MetricCard : CompositeDrawable
    {
        private readonly SpriteText value;
        private readonly SpriteText detailText;

        public MetricCard(string heading, Colour4 accent, IconUsage icon)
        {
            RelativeSizeAxes = Axes.Both;
            Masking = true;
            CornerRadius = AimModVisualStyle.ControlRadius;
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                new Box { RelativeSizeAxes = Axes.Y, Width = 3, Colour = accent },
                new Container
                {
                    Anchor = Anchor.TopRight,
                    Origin = Anchor.TopRight,
                    Position = new(-14, 14),
                    Size = new(24),
                    Child = new SpriteIcon
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Size = new(13),
                        Icon = icon,
                        Colour = accent,
                    },
                },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding { Top = 11, Bottom = 10, Left = 14, Right = 42 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(5),
                    Children = new Drawable[]
                    {
                        text(heading.ToUpperInvariant(), 9, AimModPalette.Muted, "Bold"),
                        value = text("-", 20, AimModPalette.Text, "Bold"),
                        detailText = text(string.Empty, 9, AimModPalette.Muted),
                    },
                },
            };
        }

        public void Set(string nextValue, string detail)
        {
            value.Text = nextValue;
            detailText.Text = detail;
        }
    }

    private sealed partial class StatisticsGraphCard : CompositeDrawable
    {
        private readonly Func<double, string> formatter;
        private readonly Colour4 colour;
        private readonly Container plotViewport;
        private readonly Container plotArea;
        private readonly LineGraph graph;
        private readonly Container pointLayer;
        private readonly SpriteText range;
        private readonly SpriteText chartMeta;
        private readonly Container emptyState;
        private readonly Container hoverLine;
        private readonly CircularContainer hoverPoint;
        private readonly Container tooltip;
        private readonly SpriteText tooltipValue;
        private readonly SpriteText tooltipDate;
        private CoachingChartPoint[] points = Array.Empty<CoachingChartPoint>();
        private float minimumValue;
        private float maximumValue = 1;

        public StatisticsGraphCard(string heading, Colour4 colour, Func<double, string> formatter)
        {
            this.formatter = formatter;
            this.colour = colour;
            RelativeSizeAxes = Axes.Both;
            Masking = true;
            CornerRadius = AimModVisualStyle.ControlRadius;
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                new Box { RelativeSizeAxes = Axes.X, Height = 2, Colour = colour, Alpha = 0.8f },
                text(heading, 14, AimModPalette.Text, "SemiBold").With(text => text.Position = new(14, 12)),
                range = text("No data", 10, AimModPalette.Muted).With(text =>
                {
                    text.Anchor = Anchor.TopRight;
                    text.Origin = Anchor.TopRight;
                    text.Position = new(-14, 12);
                }),
                chartMeta = text("No plays in current view", 9, AimModPalette.Muted).With(text =>
                {
                    text.Anchor = Anchor.TopRight;
                    text.Origin = Anchor.TopRight;
                    text.Position = new(-14, 29);
                }),
                plotViewport = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding { Top = 52, Bottom = 16, Left = 14, Right = 14 },
                    Child = plotArea = new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Masking = true,
                        Children = new Drawable[]
                        {
                            gridLine(0.25f),
                            gridLine(0.5f),
                            gridLine(0.75f),
                            graph = new LineGraph
                            {
                                RelativeSizeAxes = Axes.Both,
                                LineColour = colour,
                                DefaultValueCount = 80,
                            },
                            pointLayer = new Container { RelativeSizeAxes = Axes.Both },
                            hoverLine = new Container
                            {
                                RelativeSizeAxes = Axes.Y,
                                Height = 1,
                                Width = 1,
                                Alpha = 0,
                                Child = new Box { RelativeSizeAxes = Axes.Both, Colour = Colour4.White.Opacity(0.5f) },
                            },
                            hoverPoint = new CircularContainer
                            {
                                Size = new(9),
                                Origin = Anchor.Centre,
                                Masking = true,
                                Alpha = 0,
                                BorderThickness = 2,
                                BorderColour = AimModPalette.Text,
                                Child = new Box { RelativeSizeAxes = Axes.Both, Colour = colour },
                            },
                        },
                    },
                },
                emptyState = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding { Top = 48 },
                    Child = text("No plays in this view", 11, AimModPalette.Muted).With(label =>
                    {
                        label.Anchor = Anchor.Centre;
                        label.Origin = Anchor.Centre;
                        label.Alpha = 0.72f;
                    }),
                },
                tooltip = new Container
                {
                    Position = new(15, 51),
                    Size = new(142, 50),
                    Alpha = 0,
                    Masking = true,
                    CornerRadius = 5,
                    Children = new Drawable[]
                    {
                        new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas.Opacity(0.96f) },
                        tooltipValue = text(string.Empty, 12, AimModPalette.Text, "Bold").With(text => text.Position = new(9, 7)),
                        tooltipDate = text(string.Empty, 9, AimModPalette.Muted).With(text => text.Position = new(9, 27)),
                    },
                },
            };
        }

        public void SetSeries(IReadOnlyList<CoachingChartPoint> source)
        {
            points = downsample(source, 120);
            if (points.Length == 0)
            {
                graph.Alpha = 0;
                pointLayer.Clear();
                range.Text = "No data";
                range.Colour = AimModPalette.Muted;
                chartMeta.Text = "No plays in current view";
                plotViewport.FadeOut(80);
                hoverLine.FadeOut(80);
                hoverPoint.FadeOut(80);
                tooltip.FadeOut(80);
                emptyState.FadeIn(100);
                return;
            }

            double minimum = points.Min(point => point.Value);
            double maximum = points.Max(point => point.Value);
            double padding = Math.Max(0.01, (maximum - minimum) * 0.1);
            minimumValue = (float)Math.Max(0, minimum - padding);
            maximumValue = (float)Math.Max(minimumValue + 0.01, maximum + padding);
            graph.MinValue = minimumValue;
            graph.MaxValue = maximumValue;
            graph.DefaultValueCount = Math.Max(2, points.Length);
            graph.Values = points.Select(point => (float)point.Value).ToArray();
            rebuildPoints();
            graph.FadeIn(120);
            plotViewport.FadeIn(120);
            emptyState.FadeOut(80);
            range.Text = formatter(points[^1].Value);
            range.Colour = colour;
            chartMeta.Text = $"{points.Length:N0} plays  /  {formatDateRange(points)}";
        }

        protected override bool OnHover(HoverEvent e)
        {
            updateHover(ToLocalSpace(e.ScreenSpaceMousePosition).X);
            return true;
        }

        protected override bool OnMouseMove(MouseMoveEvent e)
        {
            updateHover(ToLocalSpace(e.ScreenSpaceMousePosition).X);
            return true;
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            hoverLine.FadeOut(80);
            hoverPoint.FadeOut(80);
            tooltip.FadeOut(80);
            base.OnHoverLost(e);
        }

        private void updateHover(float localX)
        {
            if (points.Length == 0)
                return;
            float graphWidth = Math.Max(1, plotArea.DrawWidth);
            float x = Math.Clamp(localX - plotViewport.Padding.Left, 0, graphWidth);
            int index = points.Length == 1 ? 0 : (int)Math.Round(x / graphWidth * (points.Length - 1));
            CoachingChartPoint point = points[Math.Clamp(index, 0, points.Length - 1)];
            float y = valueY(point.Value, Math.Max(1, plotArea.DrawHeight));
            hoverLine.X = x;
            hoverPoint.Position = new(x, y);
            tooltip.X = Math.Clamp(15 + x + 7, 6, Math.Max(6, DrawWidth - tooltip.Width - 6));
            tooltipValue.Text = formatter(point.Value);
            tooltipDate.Text = point.PlayedAt.ToString("dd MMM yyyy  HH:mm");
            hoverLine.FadeIn(60);
            hoverPoint.FadeIn(60);
            tooltip.FadeIn(60);
        }

        private void rebuildPoints()
        {
            pointLayer.Clear();
            if (points.Length > 36)
                return;

            for (int index = 0; index < points.Length; index++)
            {
                float x = points.Length == 1 ? 0.5f : index / (points.Length - 1f);
                float y = normalisedY(points[index].Value);
                pointLayer.Add(new CircularContainer
                {
                    RelativePositionAxes = Axes.Both,
                    Position = new(Math.Clamp(x, 0.012f, 0.988f), Math.Clamp(y, 0.025f, 0.975f)),
                    Origin = Anchor.Centre,
                    Size = new(6),
                    Masking = true,
                    BorderThickness = 1.5f,
                    BorderColour = AimModPalette.Panel,
                    Child = new Box { RelativeSizeAxes = Axes.Both, Colour = colour },
                });
            }
        }

        private float normalisedY(double value) => 1 - Math.Clamp(((float)value - minimumValue) / Math.Max(0.0001f, maximumValue - minimumValue), 0, 1);

        private float valueY(double value, float height) => Math.Clamp(normalisedY(value) * height, 4, Math.Max(4, height - 4));

        private static Box gridLine(float y) => new()
        {
            RelativePositionAxes = Axes.Y,
            RelativeSizeAxes = Axes.X,
            Y = y,
            Height = 1,
            Colour = AimModPalette.Border,
            Alpha = 0.58f,
        };

        private static string formatDateRange(IReadOnlyList<CoachingChartPoint> source)
        {
            string first = source[0].PlayedAt.ToString("dd MMM");
            string last = source[^1].PlayedAt.ToString("dd MMM");
            return first == last ? first : $"{first} - {last}";
        }

        private static CoachingChartPoint[] downsample(IReadOnlyList<CoachingChartPoint> source, int limit)
        {
            if (source.Count <= limit)
                return source.ToArray();
            return Enumerable.Range(0, limit)
                             .Select(index => source[(int)Math.Round(index * (source.Count - 1d) / (limit - 1))])
                             .ToArray();
        }
    }

    private sealed partial class StatisticsRunRow : ClickableContainer
    {
        private readonly Box background;
        private readonly Box selectionLayer;
        private readonly TruncatingSpriteText title;
        private readonly TruncatingSpriteText subtitle;
        private readonly bool selected;

        public StatisticsRunRow(LocalReplay replay, bool selected, Action action)
        {
            this.selected = selected;
            Action = action;
            RelativeSizeAxes = Axes.X;
            Height = 72;
            Masking = true;
            CornerRadius = AimModVisualStyle.ControlRadius;
            string mods = replay.Mods.Count == 0 ? "NM" : string.Join(" ", replay.Mods);
            Children = new Drawable[]
            {
                background = new Box { RelativeSizeAxes = Axes.Both, Colour = selected ? AimModPalette.PanelRaised : AimModPalette.Panel },
                selectionLayer = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = AimModPalette.Pink,
                    Alpha = selected ? 0.08f : 0,
                },
                new Box { RelativeSizeAxes = Axes.Y, Width = selected ? 4 : 3, Colour = AimModVisualStyle.DifficultyColour(replay.StarRating) },
                new FillFlowContainer
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Width = 0.62f,
                    Margin = new MarginPadding { Left = 16 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(4),
                    Children = new Drawable[]
                    {
                        title = truncatingText($"{replay.Title} [{replay.Difficulty}]", 13, AimModPalette.Text, 500, "SemiBold"),
                        subtitle = truncatingText($"{replay.Artist}  /  {replay.PlayedAt:dd MMM yyyy HH:mm}  /  {mods}", 10, AimModPalette.Muted, 500),
                    },
                },
                new FillFlowContainer
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    AutoSizeAxes = Axes.Both,
                    Margin = new MarginPadding { Right = 14 },
                    Direction = FillDirection.Horizontal,
                    Spacing = new(14),
                    Children = new Drawable[]
                    {
                        text($"{replay.Accuracy:P2}", 12, AimModPalette.Cyan, "Bold"),
                        text(replay.PerformancePoints is { } pp ? $"{pp:0.#}pp" : "PP -", 12, AimModPalette.Pink, "Bold"),
                        double.IsFinite(replay.StarRating)
                            ? new AimModDifficultyPill(replay.StarRating)
                            : new AimModPill("Stars unknown"),
                    },
                },
            };
        }

        protected override void Update()
        {
            base.Update();
            float available = Math.Max(140, DrawWidth - 314);
            title.MaxWidth = available;
            subtitle.MaxWidth = available;
        }

        protected override bool OnHover(HoverEvent e)
        {
            background.FadeColour(AimModPalette.PanelHover, 90);
            selectionLayer.FadeTo(selected ? 0.14f : 0.055f, 90);
            return true;
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            background.FadeColour(selected ? AimModPalette.PanelRaised : AimModPalette.Panel, 90);
            selectionLayer.FadeTo(selected ? 0.08f : 0, 90);
            base.OnHoverLost(e);
        }
    }

    private sealed partial class StatisticsEmptyState : CompositeDrawable
    {
        public StatisticsEmptyState(string heading, string detail)
        {
            RelativeSizeAxes = Axes.X;
            Height = 92;
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel, Alpha = 0.45f },
                new FillFlowContainer
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    AutoSizeAxes = Axes.Both,
                    Direction = FillDirection.Vertical,
                    Spacing = new(4),
                    Children = new Drawable[]
                    {
                        text(heading, 13, AimModPalette.Text, "SemiBold"),
                        text(detail, 10, AimModPalette.Muted),
                    },
                },
            };
        }
    }

    private sealed partial class StatisticsInspectorEmptyState : CompositeDrawable
    {
        public StatisticsInspectorEmptyState()
        {
            RelativeSizeAxes = Axes.X;
            Height = 250;
            InternalChild = new FillFlowContainer
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Vertical,
                Spacing = new(8),
                Children = new Drawable[]
                {
                    new SpriteIcon
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Size = new(24),
                        Icon = FontAwesome.Solid.MousePointer,
                        Colour = AimModPalette.Cyan,
                    },
                    text("Select a play", 16, AimModPalette.Text, "SemiBold"),
                    text("Choose a row to inspect its score and difficulty history.", 11, AimModPalette.Muted),
                },
            };
        }
    }
}

internal static class StatisticsDrawableExtensions
{
    public static Container WithPadding(this Drawable drawable) => new()
    {
        RelativeSizeAxes = Axes.Both,
        Padding = new MarginPadding { Right = 8 },
        Child = drawable,
    };
}

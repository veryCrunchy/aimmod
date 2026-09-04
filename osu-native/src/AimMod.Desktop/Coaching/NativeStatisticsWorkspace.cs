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
    private readonly OsuTextBox search;
    private readonly Bindable<StatisticsTimeRange> timeRange = new(StatisticsTimeRange.All);
    private readonly Bindable<StatisticsModFilter> modFilter = new(StatisticsModFilter.Any);
    private readonly Bindable<StatisticsRunSort> sort = new(StatisticsRunSort.Recent);
    private readonly Bindable<StatisticsScoreSource> scoreSource = new(StatisticsScoreSource.All);
    private readonly Bindable<StatisticsStarBand> starBand = new(StatisticsStarBand.Any);
    private readonly Bindable<StatisticsResultFilter> resultFilter = new(StatisticsResultFilter.All);
    private readonly SpriteText scopeText;
    private readonly SpriteText resultText;
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
                Height = 164,
                Depth = -10,
                Children = new Drawable[]
                {
                    new AimModSectionHeader(
                        "Statistics",
                        "Explore the unified osu!standard score dataset, combining online best/recent records with replay-rich local attempts.",
                        "performance history"),
                    scopeText = text("Loading score scope...", 11, AimModPalette.Muted).With(drawable => drawable.Y = 65),
                    filterLabel("SEARCH", 0),
                    filterLabel("PERIOD", 220),
                    filterLabel("SOURCE", 340),
                    filterLabel("MODS", 460),
                    filterLabel("DIFFICULTY", 575),
                    filterLabel("RESULT", 690),
                    filterLabel("SORT", 805),
                    search = new OsuTextBox
                    {
                        Position = new(0, 102),
                        Size = new(210, 42),
                        PlaceholderText = "Search plays",
                    },
                    dropdown(timeRange, 220, 102, 110),
                    dropdown(scoreSource, 340, 102, 110),
                    dropdown(modFilter, 460, 102, 105),
                    dropdown(starBand, 575, 102, 105),
                    dropdown(resultFilter, 690, 102, 105),
                    dropdown(sort, 805, 102, 133),
                },
            },
            contentViewport = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Top = 164 },
                Children = new Drawable[]
                {
                    mainColumn = new Container
                    {
                        RelativeSizeAxes = Axes.Y,
                        Child = new OsuScrollContainer
                        {
                            RelativeSizeAxes = Axes.Both,
                            Child = new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Direction = FillDirection.Vertical,
                                Spacing = new(12),
                                Padding = new MarginPadding { Bottom = 28, Right = 4 },
                                Children = new Drawable[]
                                {
                                    metricGrid = new GridContainer
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        Height = 100,
                                        ColumnDimensions = fourEqualColumns(),
                                        Content = new[]
                                        {
                                            new Drawable[]
                                            {
                                                (averageAccuracy = new MetricCard("Average accuracy", AimModPalette.Cyan)).WithPadding(),
                                                (medianPp = new MetricCard("Median PP", AimModPalette.Pink)).WithPadding(),
                                                (missFree = new MetricCard("Miss-free rate", AimModPalette.Success)).WithPadding(),
                                                (averageStars = new MetricCard("Average difficulty", Colour4.FromHex("FFD45A"))).WithPadding(),
                                            },
                                        },
                                    },
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
                                            (starsGraph = new StatisticsGraphCard("Difficulty played", Colour4.FromHex("FFD45A"), value => $"{value:0.00} stars")).WithPadding(),
                                            (missesGraph = new StatisticsGraphCard("Misses per play", AimModPalette.Success, value => $"{value:0} misses")).WithPadding(),
                                        } },
                                    },
                                    new FillFlowContainer
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        AutoSizeAxes = Axes.Y,
                                        Direction = FillDirection.Horizontal,
                                        Children = new Drawable[]
                                        {
                                            text("PLAYS IN VIEW", 11, AimModPalette.Cyan, "Bold"),
                                            resultText = text(string.Empty, 11, AimModPalette.Muted, "SemiBold").With(drawable => drawable.Margin = new MarginPadding { Left = 12 }),
                                        },
                                    },
                                    runList = new FillFlowContainer<Drawable>
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        AutoSizeAxes = Axes.Y,
                                        Direction = FillDirection.Vertical,
                                        Spacing = new(7),
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
                        CornerRadius = 8,
                        BorderThickness = 1,
                        BorderColour = AimModPalette.Border,
                        Children = new Drawable[]
                        {
                            new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                            new OsuScrollContainer
                            {
                                RelativeSizeAxes = Axes.Both,
                                Child = inspectorContent = new FillFlowContainer<Drawable>
                                {
                                    RelativeSizeAxes = Axes.X,
                                    AutoSizeAxes = Axes.Y,
                                    Direction = FillDirection.Vertical,
                                    Spacing = new(12),
                                    Padding = new MarginPadding(18),
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
        load();
    }

    protected override void Update()
    {
        base.Update();
        float available = Math.Max(720, contentViewport.DrawWidth);
        float inspectorWidth = Math.Clamp(available * 0.29f, 290, 370);
        inspectorColumn.Width = inspectorWidth;
        mainColumn.Width = Math.Max(420, available - inspectorWidth - 14);
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
        catch (Exception error)
        {
            if (!IsDisposed)
                Schedule(() =>
                {
                    loadingOverlay.HideLoading();
                    scopeText.Text = $"Statistics could not be loaded. {error.Message}";
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
        resultText.Text = $"{model.Runs.Count:N0} matching";
        averageAccuracy.Set(formatPercent(model.AverageAccuracy), model.BestAccuracy is { } best ? $"Best {best:P2}" : "No accuracy data");
        medianPp.Set(model.MedianPerformancePoints is { } pp ? $"{pp:0.#}pp" : "-", $"{model.PerformancePointRunCount:N0} plays with PP");
        missFree.Set($"{model.MissFreeRate:P0}", $"Best combo {model.BestCombo:N0}x");
        averageStars.Set(model.AverageStarRating is { } stars ? $"{stars:0.00} stars" : "-", compactScore(model.TotalScore));
        updateGraph(accuracyGraph, model.Series.Single(series => series.Key == "statisticsAccuracy"));
        updateGraph(ppGraph, model.Series.Single(series => series.Key == "statisticsPp"));
        updateGraph(starsGraph, model.Series.Single(series => series.Key == "statisticsStars"));
        updateGraph(missesGraph, model.Series.Single(series => series.Key == "statisticsMisses"));
        renderRuns(model.Runs);

        if (selected is null || !model.Runs.Any(run => run.ScoreId == selected.ScoreId))
            select(model.Runs.FirstOrDefault());
        else
            showInspector(selected);
    }

    private void renderRuns(IReadOnlyList<LocalReplay> runs)
    {
        runList.Clear();
        if (runs.Count == 0)
        {
            runList.Add(text("No plays match the active filters.", 14, AimModPalette.Muted).With(drawable => drawable.Padding = new MarginPadding(18)));
            return;
        }

        foreach (LocalReplay replay in runs.Take(100))
            runList.Add(new StatisticsRunRow(replay, selected?.ScoreId == replay.ScoreId, () => select(replay)));
    }

    private void select(LocalReplay? replay)
    {
        selected = replay;
        if (replay is null)
            showEmptyInspector();
        else
            showInspector(replay);
    }

    private void showEmptyInspector()
    {
        inspectorContent.Clear();
        inspectorContent.AddRange(new Drawable[]
        {
            text("PLAY DETAIL", 11, AimModPalette.Cyan, "Bold"),
            text("Select a play to inspect its exact score and difficulty history.", 14, AimModPalette.Muted).With(text => text.RelativeSizeAxes = Axes.X),
        });
    }

    private void showInspector(LocalReplay replay)
    {
        StatisticsMapSummary map = StatisticsWorkspaceModel.BuildMapSummary(allRuns, replay.BeatmapId);
        inspectorContent.Clear();
        inspectorContent.AddRange(new Drawable[]
        {
            text("SELECTED DIFFICULTY", 11, AimModPalette.Cyan, "Bold"),
            text(replay.Title, 19, AimModPalette.Text, "Bold").With(text => text.RelativeSizeAxes = Axes.X),
            text($"{replay.Artist}  /  [{replay.Difficulty}]", 12, AimModPalette.Muted).With(text => text.RelativeSizeAxes = Axes.X),
            new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Horizontal,
                Spacing = new(8),
                Children = pills(replay),
            },
            divider(),
            detail("PLAYED", replay.PlayedAt.ToString("dd MMM yyyy  HH:mm")),
            detail("SCORE", replay.TotalScore.ToString("N0")),
            detail("ACCURACY", replay.Accuracy.ToString("P2")),
            detail("PP", replay.PerformancePoints is { } pp ? $"{pp:0.##}pp" : "Not stored"),
            detail("COMBO / MISSES", $"{replay.MaxCombo:N0}x  /  {replay.MissCount:N0}"),
            detail("SOURCE", replay.IsLocallyStored
                ? replay.OnlineScoreId > 0 ? "Local + cached online" : "Local score history"
                : "Cached online best/recent"),
            divider(),
            text("THIS DIFFICULTY", 11, AimModPalette.Pink, "Bold"),
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
        Height = 42,
        Action = action,
        Masking = true,
        CornerRadius = 6,
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

    private static OsuEnumDropdown<T> dropdown<T>(Bindable<T> bindable, float x, float y, float width)
        where T : struct, Enum => new()
        {
            Position = new(x, y),
            Width = width,
            Current = bindable,
        };

    private static SpriteText filterLabel(string value, float x) => text(value, 8, AimModPalette.Cyan, "Bold").With(text => text.Position = new(x, 89));

    private static Dimension[] fourEqualColumns() => Enumerable.Repeat(new Dimension(GridSizeMode.Relative, 0.25f), 4).ToArray();
    private static Dimension[] twoEqualColumns() => Enumerable.Repeat(new Dimension(GridSizeMode.Relative, 0.5f), 2).ToArray();
    private static string formatPercent(double? value) => value is { } number ? number.ToString("P2") : "-";
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

        public MetricCard(string heading, Colour4 accent)
        {
            RelativeSizeAxes = Axes.Both;
            Masking = true;
            CornerRadius = 7;
            BorderThickness = 1;
            BorderColour = AimModPalette.Border;
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                new Box { RelativeSizeAxes = Axes.X, Height = 3, Colour = accent },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding(14),
                    Direction = FillDirection.Vertical,
                    Spacing = new(4),
                    Children = new Drawable[]
                    {
                        text(heading.ToUpperInvariant(), 9, AimModPalette.Muted, "Bold"),
                        value = text("-", 21, AimModPalette.Text, "Bold"),
                        detailText = text(string.Empty, 10, AimModPalette.Muted),
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
        private readonly Container plotViewport;
        private readonly LineGraph graph;
        private readonly SpriteText range;
        private readonly Container hoverLine;
        private readonly Container tooltip;
        private readonly SpriteText tooltipValue;
        private readonly SpriteText tooltipDate;
        private CoachingChartPoint[] points = Array.Empty<CoachingChartPoint>();

        public StatisticsGraphCard(string heading, Colour4 colour, Func<double, string> formatter)
        {
            this.formatter = formatter;
            RelativeSizeAxes = Axes.Both;
            Masking = true;
            CornerRadius = 7;
            BorderThickness = 1;
            BorderColour = AimModPalette.Border;
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                text(heading, 14, AimModPalette.Text, "SemiBold").With(text => text.Position = new(15, 13)),
                range = text("No data", 10, AimModPalette.Muted).With(text =>
                {
                    text.Anchor = Anchor.TopRight;
                    text.Origin = Anchor.TopRight;
                    text.Position = new(-15, 16);
                }),
                plotViewport = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding { Top = 52, Bottom = 22, Left = 15, Right = 15 },
                    Child = new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Masking = true,
                        Child = graph = new LineGraph
                        {
                            RelativeSizeAxes = Axes.Both,
                            LineColour = colour,
                            DefaultValueCount = 80,
                        },
                    },
                },
                hoverLine = new Container
                {
                    Position = new(15, 51),
                    RelativeSizeAxes = Axes.Y,
                    Height = 0.64f,
                    Width = 1,
                    Alpha = 0,
                    Child = new Box { RelativeSizeAxes = Axes.Both, Colour = Colour4.White.Opacity(0.55f) },
                },
                tooltip = new Container
                {
                    Position = new(15, 48),
                    Size = new(132, 48),
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
                range.Text = "No matching data";
                return;
            }

            double minimum = points.Min(point => point.Value);
            double maximum = points.Max(point => point.Value);
            double padding = Math.Max(0.01, (maximum - minimum) * 0.1);
            float minimumValue = (float)Math.Max(0, minimum - padding);
            graph.MinValue = minimumValue;
            graph.MaxValue = (float)Math.Max(minimumValue + 0.01, maximum + padding);
            graph.DefaultValueCount = Math.Max(2, points.Length);
            graph.Values = points.Select(point => (float)point.Value).ToArray();
            graph.FadeIn(120);
            range.Text = $"{formatter(points[^1].Value)}  /  {points.Length:N0} points";
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
            tooltip.FadeOut(80);
            base.OnHoverLost(e);
        }

        private void updateHover(float localX)
        {
            if (points.Length == 0)
                return;
            float graphWidth = Math.Max(1, plotViewport.Child.DrawWidth);
            float x = Math.Clamp(localX - plotViewport.Padding.Left, 0, graphWidth);
            int index = points.Length == 1 ? 0 : (int)Math.Round(x / graphWidth * (points.Length - 1));
            CoachingChartPoint point = points[Math.Clamp(index, 0, points.Length - 1)];
            hoverLine.X = 15 + x;
            tooltip.X = Math.Clamp(15 + x + 7, 6, Math.Max(6, DrawWidth - tooltip.Width - 6));
            tooltipValue.Text = formatter(point.Value);
            tooltipDate.Text = point.PlayedAt.ToString("dd MMM yyyy  HH:mm");
            hoverLine.FadeIn(60);
            tooltip.FadeIn(60);
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

        public StatisticsRunRow(LocalReplay replay, bool selected, Action action)
        {
            Action = action;
            RelativeSizeAxes = Axes.X;
            Height = 68;
            Masking = true;
            CornerRadius = 6;
            BorderThickness = selected ? 2 : 1;
            BorderColour = selected ? AimModPalette.Pink : AimModPalette.Border;
            string mods = replay.Mods.Count == 0 ? "NM" : string.Join(" ", replay.Mods);
            Children = new Drawable[]
            {
                background = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                new Box { RelativeSizeAxes = Axes.Y, Width = 4, Colour = AimModVisualStyle.DifficultyColour(replay.StarRating) },
                new FillFlowContainer
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    AutoSizeAxes = Axes.Both,
                    Margin = new MarginPadding { Left = 16 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(3),
                    Children = new Drawable[]
                    {
                        text($"{replay.Title} [{replay.Difficulty}]", 13, AimModPalette.Text, "SemiBold"),
                        text($"{replay.Artist}  /  {replay.PlayedAt:dd MMM yyyy HH:mm}  /  {mods}", 10, AimModPalette.Muted),
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

        protected override bool OnHover(HoverEvent e)
        {
            background.FadeColour(AimModPalette.PanelHover, 90);
            return true;
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            background.FadeColour(AimModPalette.Panel, 90);
            base.OnHoverLost(e);
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

using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime.Contracts;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;

namespace AimMod.Desktop.Coaching;

public enum ReplayHistoryScreenMode
{
    Statistics,
    Coaching,
}

public partial class ReplayHistoryScreen : CompositeDrawable
{
    private const int history_limit = CoachingLimits.MaximumRuns;

    private readonly ILocalLibrarySource source;
    private readonly ILocalLibrarySourceChanged? sourceChanges;
    private readonly ReplayHistoryScreenMode mode;
    private readonly IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses;
    private readonly Action<LocalReplay> openReplay;
    private readonly SpriteText status;
    private readonly SpriteText averageAccuracy;
    private readonly SpriteText accuracyChange;
    private readonly SpriteText missAverage;
    private readonly SpriteText cleanRuns;
    private readonly SpriteText timingBias;
    private readonly SpriteText analysedRuns;
    private readonly SpriteText runCount;
    private readonly SpriteText historyWindow;
    private readonly SpriteText adviceTitle;
    private readonly SpriteText adviceDetail;
    private readonly Container adviceCard;
    private readonly GraphCard primaryGraph;
    private readonly GraphCard secondaryGraph;
    private readonly GraphCard rollingAccuracyGraph;
    private readonly GraphCard missFreeGraph;
    private readonly GridContainer statisticsTrendGraphs;
    private readonly OsuTextBox search;
    private readonly FillFlowContainer<Drawable> runList;
    private readonly AimModLoadingOverlay loadingOverlay;

    private CancellationTokenSource? loading;
    private IReadOnlyList<LocalReplay> replays = Array.Empty<LocalReplay>();
    private CoachingReport? report;
    private readonly HashSet<string> expandedRunMaps = new(StringComparer.Ordinal);
    private bool initialRunExpansionApplied;

    public ReplayHistoryScreen(
        ILocalLibrarySource source,
        ReplayHistoryScreenMode mode,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses,
        Action<LocalReplay> openReplay)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.mode = mode;
        this.analyses = analyses ?? throw new ArgumentNullException(nameof(analyses));
        this.openReplay = openReplay ?? throw new ArgumentNullException(nameof(openReplay));
        sourceChanges = source as ILocalLibrarySourceChanged;
        if (sourceChanges is not null)
            sourceChanges.SourceChanged += sourceChanged;

        RelativeSizeAxes = Axes.Both;

        primaryGraph = graphCard(
            mode == ReplayHistoryScreenMode.Statistics ? "Accumulated score" : "Accuracy over time",
            AimModPalette.Pink);
        secondaryGraph = graphCard(
            mode == ReplayHistoryScreenMode.Statistics ? "Accumulated plays" : "Misses over time",
            AimModPalette.Cyan);
        rollingAccuracyGraph = graphCard("Rolling accuracy", AimModPalette.Success);
        missFreeGraph = graphCard("Rolling miss-free rate", Colour4.FromHex("FFD45A"));

        InternalChildren = new Drawable[]
        {
            new AimModScrollContainer
            {
                RelativeSizeAxes = Axes.Both,
                Child = new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Spacing = new(AimModVisualStyle.SectionSpacing),
                    Padding = new MarginPadding { Right = 8, Bottom = 36 },
                    Children = new Drawable[]
                    {
                    new AimModSectionHeader(
                        mode == ReplayHistoryScreenMode.Statistics ? "Statistics" : "Coaching",
                        mode == ReplayHistoryScreenMode.Statistics
                            ? "Score, accuracy and consistency across your local osu!standard history."
                            : "Start with the whole pattern, then inspect the runs that changed it.",
                        mode == ReplayHistoryScreenMode.Statistics ? "play history" : "your coach"),
                    status = label("Loading your local play history...", 13, AimModPalette.Muted),
                    adviceCard = createAdviceCard(out adviceTitle, out adviceDetail),
                    new GridContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        Height = 104,
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
                                metricCard(mode == ReplayHistoryScreenMode.Statistics ? "Score in local history" : "Average accuracy", out averageAccuracy, out accuracyChange),
                                metricCard(mode == ReplayHistoryScreenMode.Statistics ? "Recorded plays" : "Misses per play", out missAverage, out cleanRuns),
                                metricCard(mode == ReplayHistoryScreenMode.Statistics ? "Rolling accuracy" : "Average hit timing", out timingBias, out analysedRuns),
                                metricCard(mode == ReplayHistoryScreenMode.Statistics ? "Miss-free rate" : "Runs in view", out runCount, out historyWindow),
                            },
                        },
                    },
                    new GridContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        Height = 252,
                        ColumnDimensions = new[]
                        {
                            new Dimension(GridSizeMode.Relative, 0.5f),
                            new Dimension(GridSizeMode.Relative, 0.5f),
                        },
                        Content = new[]
                        {
                            new Drawable[]
                            {
                                primaryGraph.Drawable,
                                secondaryGraph.Drawable,
                            },
                        },
                    },
                    statisticsTrendGraphs = new GridContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        Height = 252,
                        ColumnDimensions = new[]
                        {
                            new Dimension(GridSizeMode.Relative, 0.5f),
                            new Dimension(GridSizeMode.Relative, 0.5f),
                        },
                        Content = new[]
                        {
                            new Drawable[]
                            {
                                rollingAccuracyGraph.Drawable,
                                missFreeGraph.Drawable,
                            },
                        },
                    },
                    new AimModSubsectionHeader(
                        "Replay history",
                        "Grouped by difficulty"),
                    search = new OsuTextBox
                    {
                        RelativeSizeAxes = Axes.X,
                        Height = AimModVisualStyle.ControlHeight,
                        PlaceholderText = "Search title, artist, difficulty, player, or mod",
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
            loadingOverlay = new AimModLoadingOverlay(),
        };

        adviceCard.Alpha = mode == ReplayHistoryScreenMode.Coaching ? 1 : 0;
        adviceCard.Height = mode == ReplayHistoryScreenMode.Coaching ? 112 : 0;
        statisticsTrendGraphs.Alpha = mode == ReplayHistoryScreenMode.Statistics ? 1 : 0;
        statisticsTrendGraphs.Height = mode == ReplayHistoryScreenMode.Statistics ? 252 : 0;

        runCount.Text = "0";
        historyWindow.Text = "Waiting for local scores";
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        search.OnCommit += (_, _) => refreshRunList();
        search.Current.BindValueChanged(_ => refreshRunList());
        load();
    }

    private void load()
    {
        loading?.Cancel();
        loading?.Dispose();
        loading = new CancellationTokenSource();
        CancellationToken cancellationToken = loading.Token;
        status.Text = "Loading your local play history...";
        loadingOverlay.ShowLoading(
            mode == ReplayHistoryScreenMode.Statistics ? "Loading statistics" : "Preparing coaching",
            "Reading your local osu!standard play history");
        _ = loadAsync(cancellationToken);
    }

    private async Task loadAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<LocalReplay> items;
            int total;
            if (mode == ReplayHistoryScreenMode.Statistics)
            {
                StatisticsHistoryLoadResult history = await StatisticsHistoryLoader.LoadAsync(source, cancellationToken).ConfigureAwait(false);
                items = history.Runs;
                total = history.TotalAvailableRunCount;
            }
            else
            {
                LocalLibraryPage<LocalReplay> page = await source.SearchReplaysAsync(new LocalLibraryQuery(
                    RulesetShortName: "osu",
                    Sort: LocalLibrarySort.RecentlyPlayed,
                    Limit: history_limit), cancellationToken).ConfigureAwait(false);
                items = page.Items;
                total = page.Total;
            }

            CoachingReport nextReport = CoachingReportBuilder.Build(items, analyses);
            StatisticsHistoryModel statistics = StatisticsHistoryModel.Build(items, total);
            if (!IsDisposed)
                Schedule(() => apply(items, total, nextReport, statistics));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!IsDisposed)
                Schedule(() =>
                {
                    loadingOverlay.HideLoading();
                    status.Text = $"Your play history could not be loaded. {error.Message}";
                });
        }
    }

    private void apply(
        IReadOnlyList<LocalReplay> nextReplays,
        int total,
        CoachingReport nextReport,
        StatisticsHistoryModel statistics)
    {
        replays = nextReplays;
        report = nextReport;
        loadingOverlay.HideLoading();

        if (mode == ReplayHistoryScreenMode.Statistics)
        {
            applyStatistics(statistics);
            refreshRunList();
            return;
        }

        status.Text = total > nextReplays.Count
            ? $"Showing the latest {nextReplays.Count:N0} of {total:N0} local osu!standard plays"
            : $"{nextReplays.Count:N0} local osu!standard plays";

        averageAccuracy.Text = formatPercent(nextReport.Accuracy.Average);
        accuracyChange.Text = nextReport.Accuracy.RecentChange is { } change
            ? $"{formatSignedPoints(change)} between the older and newer half"
            : "More plays are needed for a trend";
        accuracyChange.Colour = nextReport.Accuracy.RecentChange is > 0 ? AimModPalette.Success : AimModPalette.Muted;

        missAverage.Text = nextReport.Misses.Average is { } average ? $"{average:0.00}" : "-";
        cleanRuns.Text = $"{nextReport.Misses.RunsWithoutMisses:N0} miss-free runs";

        timingBias.Text = nextReport.Timing.MeanOffsetMilliseconds is { } offset
            ? $"{Math.Abs(offset):0.0} ms {(offset < 0 ? "early" : "late")}"
            : "Analyse a replay";
        analysedRuns.Text = nextReport.Timing.AnalysedRunCount == 1
            ? "1 replay with exact timing"
            : $"{nextReport.Timing.AnalysedRunCount:N0} replays with exact timing";

        runCount.Text = nextReplays.Count.ToString("N0");
        historyWindow.Text = nextReplays.Count == 0 ? "No local scores yet" : "Latest local plays";

        adviceTitle.Text = nextReport.NextPlay.Title;
        adviceDetail.Text = nextReport.NextPlay.Detail;

        updateGraph(
            primaryGraph,
            nextReport.Series.Single(series => series.Key == "accuracy"),
            0,
            100,
            value => $"{value:0.##}%");

        CoachingChartSeries secondarySeries = nextReport.Series.Single(series =>
            series.Key == (mode == ReplayHistoryScreenMode.Statistics ? "cumulativeScore" : "misses"));
        double secondaryMaximum = Math.Max(1, secondarySeries.Points.Count == 0 ? 1 : secondarySeries.Points.Max(point => point.Value));
        updateGraph(
            secondaryGraph,
            secondarySeries,
            0,
            secondaryMaximum,
            mode == ReplayHistoryScreenMode.Statistics ? compactNumber : value => $"{value:0} misses");
        refreshRunList();
    }

    private void applyStatistics(StatisticsHistoryModel statistics)
    {
        status.Text = statistics.LoadedRunCount == 0
            ? "No local osu!standard scores were found."
            : statistics.IsComplete
                ? $"{statistics.LoadedRunCount:N0} local plays from {statistics.TimeAxis.Start} to {statistics.TimeAxis.End}"
                : $"Loaded {statistics.LoadedRunCount:N0} of {statistics.TotalAvailableRunCount:N0} local plays";

        averageAccuracy.Text = compactScore(statistics.AccumulatedScore);
        accuracyChange.Text = statistics.LoadedRunCount == 0
            ? "No stored score yet"
            : $"{compactScore(statistics.RecentScore)} across the latest {statistics.RollingWindowSize:N0} plays";
        accuracyChange.Colour = AimModPalette.Muted;

        missAverage.Text = statistics.LoadedRunCount.ToString("N0");
        cleanRuns.Text = statistics.IsComplete
            ? "Complete local score history"
            : $"{statistics.TotalAvailableRunCount:N0} plays reported by lazer";

        timingBias.Text = statistics.RollingAccuracy is { } rollingAccuracyValue ? $"{rollingAccuracyValue:P2}" : "-";
        analysedRuns.Text = statistics.AccuracyChange is { } accuracyDelta
            ? $"{formatSignedPoints(accuracyDelta)} versus the previous {statistics.RollingWindowSize:N0}"
            : $"Latest {statistics.RollingWindowSize:N0}-play window";
        analysedRuns.Colour = statistics.AccuracyChange switch
        {
            > 0 => AimModPalette.Success,
            < 0 => AimModPalette.Pink,
            _ => AimModPalette.Muted,
        };

        runCount.Text = statistics.RollingMissFreeRate is { } missFree ? $"{missFree:P0}" : "-";
        string missFreeChange = statistics.MissFreeRateChange is { } change
            ? $"{change * 100:+0;-0;0} points"
            : "No prior window";
        string spread = statistics.RollingAccuracySpread is { } accuracySpread
            ? $"{accuracySpread * 100:0.00} point accuracy spread"
            : "spread not measured";
        historyWindow.Text = $"{missFreeChange}; {spread}";

        primaryGraph.Heading.Text = "Accumulated score";
        secondaryGraph.Heading.Text = "Accumulated plays";
        rollingAccuracyGraph.Heading.Text = $"Rolling {statistics.RollingWindowSize:N0}-play accuracy";
        missFreeGraph.Heading.Text = $"Rolling {statistics.RollingWindowSize:N0}-play miss-free rate";

        CoachingChartSeries accumulatedScore = statistics.Series.Single(series => series.Key == "historyCumulativeScore");
        CoachingChartSeries accumulatedRuns = statistics.Series.Single(series => series.Key == "historyCumulativeRuns");
        CoachingChartSeries rollingAccuracy = statistics.Series.Single(series => series.Key == "historyRollingAccuracy");
        CoachingChartSeries rollingMissFree = statistics.Series.Single(series => series.Key == "historyRollingMissFree");
        updateGraph(primaryGraph, accumulatedScore, 0, maximum(accumulatedScore), compactScore);
        updateGraph(secondaryGraph, accumulatedRuns, 0, maximum(accumulatedRuns), compactPlays);
        updateGraphWithMeasuredRange(rollingAccuracyGraph, rollingAccuracy, value => $"{value:0.##}%");
        updateGraphWithMeasuredRange(missFreeGraph, rollingMissFree, value => $"{value:0.#}%");
    }

    private void refreshRunList()
    {
        runList.Clear();
        CoachingRunPage page = CoachingRunSearch.Search(replays, new CoachingRunQuery(
            SearchText: search.Current.Value,
            Sort: CoachingRunSort.Recent,
            Limit: 40));
        IReadOnlyDictionary<Guid, LocalReplay> byId = replays.ToDictionary(replay => replay.ScoreId);

        if (page.Items.Count == 0)
        {
            runList.Add(new ReplayHistoryEmptyState(
                replays.Count == 0 ? "No saved runs yet" : "No matching runs",
                replays.Count == 0
                    ? "Play an osu!standard map with replay recording enabled to start building history."
                    : "Try a title, artist, difficulty, player, or mod."));
            return;
        }

        CoachingRecentRun[][] groups = page.Items
            .GroupBy(historyMapKey)
            .Select(group => group.ToArray())
            .ToArray();
        if (!initialRunExpansionApplied && groups.Length > 0)
        {
            expandedRunMaps.Add(historyMapKey(groups[0][0]));
            initialRunExpansionApplied = true;
        }

        foreach (CoachingRecentRun[] group in groups)
        {
            string key = historyMapKey(group[0]);
            bool expanded = expandedRunMaps.Contains(key);
            runList.Add(new RecentMapGroupRow(group, expanded, () =>
            {
                if (!expandedRunMaps.Add(key))
                    expandedRunMaps.Remove(key);
                refreshRunList();
            }));
            if (!expanded)
                continue;

            foreach (CoachingRecentRun run in group)
            {
                if (byId.TryGetValue(run.ScoreId, out LocalReplay? replay))
                    runList.Add(new RecentRunRow(run, run.CanAnalyse ? () => openReplay(replay) : null));
            }
        }
    }

    private static string historyMapKey(CoachingRecentRun run) => run.BeatmapId != Guid.Empty
        ? $"id:{run.BeatmapId:N}"
        : $"fallback:{run.Title.Trim().ToLowerInvariant()}:{run.Artist.Trim().ToLowerInvariant()}:{run.Difficulty.Trim().ToLowerInvariant()}";

    private static Container createAdviceCard(out SpriteText title, out SpriteText detail)
    {
        var card = new Container
        {
            RelativeSizeAxes = Axes.X,
            Height = 112,
            Masking = true,
            CornerRadius = AimModVisualStyle.CardRadius,
        };
        card.Children = new Drawable[]
        {
            new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
            new Box { RelativeSizeAxes = Axes.Y, Width = 3, Colour = AimModPalette.Success },
            new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Padding = new MarginPadding { Left = 16, Top = 14, Right = 16 },
                Direction = FillDirection.Vertical,
                Spacing = new(AimModVisualStyle.RelatedSpacing),
                Children = new Drawable[]
                {
                    label("FOCUS FOR YOUR NEXT PLAY", 10, AimModPalette.Success, "Bold"),
                    title = label("Loading your recent runs...", 18, AimModPalette.Text, "Bold"),
                    detail = label(string.Empty, 12, AimModPalette.Muted),
                },
            },
        };
        return card;
    }

    private static Container metricCard(string heading, out SpriteText value, out SpriteText detail)
    {
        var card = new Container
        {
            RelativeSizeAxes = Axes.Both,
            Padding = new MarginPadding { Right = AimModVisualStyle.RelatedSpacing },
        };
        card.Child = new Container
        {
            RelativeSizeAxes = Axes.Both,
            Masking = true,
            CornerRadius = AimModVisualStyle.CardRadius,
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding(14),
                    Direction = FillDirection.Vertical,
                    Spacing = new(AimModVisualStyle.RelatedSpacing),
                    Children = new Drawable[]
                    {
                        label(heading, 10, AimModPalette.Muted, "Bold"),
                        value = label("-", 22, AimModPalette.Text, "Bold"),
                        detail = label(string.Empty, 10, AimModPalette.Muted),
                    },
                },
            },
        };
        return card;
    }

    private static GraphCard graphCard(string headingText, Colour4 colour)
    {
        var card = new Container
        {
            RelativeSizeAxes = Axes.Both,
            Padding = new MarginPadding { Right = AimModVisualStyle.RelatedSpacing },
        };
        card.Child = new Container
        {
            RelativeSizeAxes = Axes.Both,
            Masking = true,
            CornerRadius = AimModVisualStyle.CardRadius,
        };
        OsuSpriteText heading = label(headingText, 15, AimModPalette.Text, "SemiBold");
        heading.Position = new(16, 14);
        OsuSpriteText range = label("Waiting for plays", 11, AimModPalette.Muted);
        range.Anchor = Anchor.TopRight;
        range.Origin = Anchor.TopRight;
        range.Margin = new MarginPadding { Top = 17, Right = 16 };
        var graph = new LineGraph
        {
            RelativeSizeAxes = Axes.Both,
            Padding = new MarginPadding { Top = 54, Bottom = 38, Left = 16, Right = 16 },
            LineColour = colour,
            DefaultValueCount = 60,
        };
        OsuSpriteText start = timeLabel(Anchor.BottomLeft);
        OsuSpriteText middle = timeLabel(Anchor.BottomCentre);
        OsuSpriteText end = timeLabel(Anchor.BottomRight);
        card.Children = new Drawable[]
        {
            new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
            heading,
            range,
            graph,
            start,
            middle,
            end,
        };
        return new GraphCard(card, heading, graph, range, start, middle, end);
    }

    private static void updateGraph(
        GraphCard card,
        CoachingChartSeries series,
        double minimum,
        double maximum,
        Func<double, string> formatValue)
    {
        CoachingChartPoint[] points = series.Key is "historyCumulativeScore" or "historyCumulativeRuns"
            ? StatisticsGraphSampler.SampleCumulative(series.Points, 80)
            : downsample(series.Points, 80);
        if (points.Length == 0)
        {
            card.Graph.Alpha = 0;
            card.Range.Text = "No plays to graph";
            card.Start.Text = string.Empty;
            card.Middle.Text = string.Empty;
            card.End.Text = string.Empty;
            return;
        }

        card.Graph.MinValue = (float)minimum;
        card.Graph.MaxValue = (float)Math.Max(minimum + 0.001, maximum);
        card.Graph.Values = points.Select(point => (float)point.Value).ToArray();
        card.Graph.FadeIn(160);
        card.Range.Text = points.Length == 1
            ? formatValue(points[0].Value)
            : $"{formatValue(points[0].Value)} to {formatValue(points[^1].Value)}";
        StatisticsTimeAxis axis = timeAxis(points);
        card.Start.Text = axis.Start;
        card.Middle.Text = axis.Middle;
        card.End.Text = axis.End;
    }

    private static void updateGraphWithMeasuredRange(
        GraphCard card,
        CoachingChartSeries series,
        Func<double, string> formatValue)
    {
        double minimum = series.Points.Count == 0 ? 0 : series.Points.Min(point => point.Value);
        double maximum = series.Points.Count == 0 ? 100 : series.Points.Max(point => point.Value);
        double padding = Math.Max(1, (maximum - minimum) * 0.12);
        updateGraph(card, series, Math.Max(0, Math.Floor(minimum - padding)), Math.Min(100, Math.Ceiling(maximum + padding)), formatValue);
    }

    private static CoachingChartPoint[] downsample(IReadOnlyList<CoachingChartPoint> points, int limit)
    {
        if (points.Count <= limit)
            return points.ToArray();

        var result = new CoachingChartPoint[limit];
        for (int index = 0; index < limit; index++)
        {
            int sourceIndex = (int)Math.Round(index * (points.Count - 1d) / (limit - 1));
            result[index] = points[sourceIndex];
        }

        return result;
    }

    private static StatisticsTimeAxis timeAxis(IReadOnlyList<CoachingChartPoint> points)
    {
        if (points.Count == 0)
            return new StatisticsTimeAxis(string.Empty, string.Empty, string.Empty);

        DateTimeOffset first = points[0].PlayedAt;
        DateTimeOffset last = points[^1].PlayedAt;
        string format = last - first <= TimeSpan.FromDays(2)
            ? "dd MMM HH:mm"
            : first.Year == last.Year
                ? "dd MMM"
                : "MMM yyyy";
        return new StatisticsTimeAxis(
            first.ToString(format),
            points[points.Count / 2].PlayedAt.ToString(format),
            last.ToString(format));
    }

    private static OsuSpriteText timeLabel(Anchor anchor) => label(string.Empty, 10, AimModPalette.Muted).With(text =>
    {
        text.Anchor = anchor;
        text.Origin = anchor;
        text.Margin = anchor switch
        {
            Anchor.BottomLeft => new MarginPadding { Bottom = 10, Left = 18 },
            Anchor.BottomRight => new MarginPadding { Bottom = 10, Right = 18 },
            _ => new MarginPadding { Bottom = 10 },
        };
    });

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

    private static string formatPercent(double? value) => value is { } number ? $"{number:P2}" : "-";

    private static string formatSignedPoints(double change) => $"{change * 100:+0.00;-0.00;0.00} points";

    private static string compactNumber(double value) => value switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000:0.##}B score",
        >= 1_000_000 => $"{value / 1_000_000:0.##}M score",
        >= 1_000 => $"{value / 1_000:0.##}K score",
        _ => $"{value:0} score",
    };

    private static string compactScore(double value) => compactNumber(value);

    private static string compactPlays(double value) => $"{value:N0} plays";

    private static double maximum(CoachingChartSeries series) =>
        Math.Max(1, series.Points.Count == 0 ? 1 : series.Points.Max(point => point.Value));

    private static OsuSpriteText label(string text, float size, Colour4 colour, string weight = "Regular") => new()
    {
        Text = text,
        Font = new FontUsage(size: size, weight: weight),
        Colour = colour,
    };

    private sealed record GraphCard(
        Container Drawable,
        OsuSpriteText Heading,
        LineGraph Graph,
        OsuSpriteText Range,
        OsuSpriteText Start,
        OsuSpriteText Middle,
        OsuSpriteText End);

    private partial class ReplayHistoryEmptyState : CompositeDrawable
    {
        public ReplayHistoryEmptyState(string title, string detail)
        {
            RelativeSizeAxes = Axes.X;
            Height = 154;
            Masking = true;
            CornerRadius = AimModVisualStyle.CardRadius;
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                new SpriteIcon
                {
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.TopCentre,
                    Y = 28,
                    Size = new(24),
                    Icon = FontAwesome.Solid.PlayCircle,
                    Colour = AimModPalette.Cyan,
                },
                new FillFlowContainer
                {
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.TopCentre,
                    AutoSizeAxes = Axes.Y,
                    RelativeSizeAxes = Axes.X,
                    Y = 66,
                    Padding = new MarginPadding { Horizontal = 40 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(AimModVisualStyle.RelatedSpacing),
                    Children = new Drawable[]
                    {
                        label(title, 15, AimModPalette.Text, "SemiBold").With(text =>
                        {
                            text.Anchor = Anchor.TopCentre;
                            text.Origin = Anchor.TopCentre;
                        }),
                        new TextFlowContainer(sprite =>
                        {
                            sprite.Font = new FontUsage(size: 11);
                            sprite.Colour = AimModPalette.Muted;
                        })
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            TextAnchor = Anchor.TopCentre,
                        }.With(flow => flow.AddText(detail)),
                    },
                },
            };
        }
    }

    private partial class RecentMapGroupRow : AimModInteractiveSurface
    {
        private readonly TruncatingSpriteText title;
        private readonly TruncatingSpriteText artist;

        public RecentMapGroupRow(IReadOnlyList<CoachingRecentRun> runs, bool expanded, Action action)
        {
            CoachingRecentRun latest = runs[0];
            RelativeSizeAxes = Axes.X;
            Height = 62;
            Action = action;
            CornerRadius = AimModVisualStyle.CardRadius;
            BackgroundColour = expanded ? AimModPalette.PanelRaised : AimModPalette.Panel;
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
                    Text = $"{latest.Title} [{latest.Difficulty}]",
                    Position = new(14, 8),
                    Font = new FontUsage(size: 13, weight: "SemiBold"),
                    Colour = AimModPalette.Text,
                    MaxWidth = 600,
                },
                artist = new TruncatingSpriteText
                {
                    Text = latest.Artist,
                    Position = new(14, 30),
                    Font = new FontUsage(size: 10),
                    Colour = AimModPalette.Muted,
                    MaxWidth = 600,
                },
                label(
                    $"{runs.Count:N0} {(runs.Count == 1 ? "attempt" : "attempts")}  //  best {runs.Max(run => run.Accuracy):P2}",
                    10,
                    AimModPalette.Cyan,
                    "SemiBold").With(text =>
                {
                    text.Anchor = Anchor.CentreRight;
                    text.Origin = Anchor.CentreRight;
                    text.Margin = new MarginPadding { Right = 42 };
                }),
                new SpriteIcon
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    Position = new(-14, 0),
                    Size = new(11),
                    Icon = expanded ? FontAwesome.Solid.ChevronUp : FontAwesome.Solid.ChevronDown,
                    Colour = AimModPalette.Muted,
                },
            };
        }

        protected override void Update()
        {
            base.Update();
            title.MaxWidth = artist.MaxWidth = Math.Max(160, DrawWidth - 350);
        }
    }

    private partial class RecentRunRow : AimModInteractiveSurface
    {
        public RecentRunRow(CoachingRecentRun run, Action? action)
        {
            RelativeSizeAxes = Axes.X;
            Height = 52;
            CornerRadius = AimModVisualStyle.ControlRadius;
            BackgroundColour = AimModPalette.PanelRaised;
            Action = action;

            string mods = run.Mods.Count == 0 ? "No Mod" : string.Join(" ", run.Mods);
            Children = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Y,
                    Width = 3,
                    Colour = action is null ? AimModPalette.Muted : AimModPalette.Cyan,
                },
                new FillFlowContainer
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Width = 1,
                    Margin = new MarginPadding { Left = 14 },
                    Padding = new MarginPadding { Right = 330 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(3),
                    Children = new Drawable[]
                    {
                        label($"{run.PlayedAt:yyyy-MM-dd HH:mm}  //  {mods}", 11, AimModPalette.Text, "SemiBold"),
                        label(run.PerformancePoints is { } pp ? $"{pp:0.#}pp recorded" : "Performance points unavailable", 9, AimModPalette.Muted),
                    },
                },
                new FillFlowContainer
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    AutoSizeAxes = Axes.Both,
                    Margin = new MarginPadding { Right = 14 },
                    Direction = FillDirection.Horizontal,
                    Spacing = new(8),
                    Children = new Drawable[]
                    {
                        label($"{run.Accuracy:P2}", 12, AimModPalette.Cyan, "Bold"),
                        label($"{run.MissCount:N0} {(run.MissCount == 1 ? "miss" : "misses")}", 10,
                            run.MissCount == 0 ? AimModPalette.Success : AimModPalette.Muted),
                        new AimModDifficultyPill(run.StarRating),
                        label(run.CanAnalyse ? "Inspect" : "Unavailable", 10,
                            run.CanAnalyse ? AimModPalette.Pink : AimModPalette.Muted, "SemiBold"),
                    },
                },
            };
            Alpha = run.CanAnalyse ? 1 : 0.62f;
        }
    }
}

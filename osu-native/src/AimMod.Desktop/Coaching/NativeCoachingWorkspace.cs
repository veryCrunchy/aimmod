using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime.Contracts;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;

namespace AimMod.Desktop.Coaching;

/// <summary>
/// A session-first coaching workspace backed by local lazer scores and completed replay analyses.
/// </summary>
public partial class NativeCoachingWorkspace : CompositeDrawable
{
    private const int visible_run_limit = 24;

    private readonly ILocalLibrarySource source;
    private readonly ILocalLibrarySourceChanged? sourceChanges;
    private readonly IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses;
    private readonly Action<LocalReplay> openReplay;

    private readonly Container headerArtwork;
    private readonly OsuSpriteText sessionTitle;
    private readonly OsuSpriteText sessionPlays;
    private readonly OsuSpriteText sessionDuration;
    private readonly OsuSpriteText sessionAccuracy;
    private readonly OsuSpriteText sessionTrend;
    private readonly OsuSpriteText status;
    private readonly CoachingTrendChart trendChart;
    private readonly FillFlowContainer<Drawable> selectedRunHost;
    private readonly FillFlowContainer<Drawable> exactAnalysisHost;
    private readonly FillFlowContainer<Drawable> changesHost;
    private readonly FillFlowContainer<Drawable> recommendationHost;
    private readonly OsuTextBox search;
    private readonly FillFlowContainer<Drawable> runList;
    private readonly AimModLoadingOverlay loadingOverlay;

    private CancellationTokenSource? loading;
    private IReadOnlyList<LocalReplay> replays = Array.Empty<LocalReplay>();
    private NativeCoachingWorkspaceModel? workspace;
    private bool acceptingAnalysisProgress;

    public NativeCoachingWorkspace(
        ILocalLibrarySource source,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses,
        Action<LocalReplay> openReplay)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.analyses = analyses ?? throw new ArgumentNullException(nameof(analyses));
        this.openReplay = openReplay ?? throw new ArgumentNullException(nameof(openReplay));
        sourceChanges = source as ILocalLibrarySourceChanged;
        if (sourceChanges is not null)
            sourceChanges.SourceChanged += sourceChanged;

        RelativeSizeAxes = Axes.Both;

        var content = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new(16),
            Padding = new MarginPadding { Bottom = 40 },
        };

        content.Add(createSessionHeader(
            out headerArtwork,
            out sessionTitle,
            out sessionPlays,
            out sessionDuration,
            out sessionAccuracy,
            out sessionTrend));
        content.Add(status = label("Loading your local play history...", 12, AimModPalette.Muted));
        content.Add(new GridContainer
        {
            RelativeSizeAxes = Axes.X,
            Height = 700,
            ColumnDimensions = new[]
            {
                new Dimension(GridSizeMode.Relative, 0.63f),
                new Dimension(GridSizeMode.Relative, 0.37f),
            },
            Content = new[]
            {
                new Drawable[]
                {
                    createPerformancePanel(out trendChart, out selectedRunHost, out exactAnalysisHost),
                    createCoachPanel(out changesHost, out recommendationHost),
                },
            },
        });
        content.Add(new AimModSectionHeader(
            "Choose a run",
            "Search your local osu!standard history. Selecting a run updates every panel above.",
            "local history"));
        content.Add(search = new OsuTextBox
        {
            RelativeSizeAxes = Axes.X,
            Height = 46,
            PlaceholderText = "Search beatmaps, difficulties, artists, players, or mods",
        });
        content.Add(runList = new FillFlowContainer<Drawable>
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new(7),
        });

        InternalChildren = new Drawable[]
        {
            new OsuScrollContainer
            {
                RelativeSizeAxes = Axes.Both,
                Depth = 10,
                Child = content,
            },
            loadingOverlay = new AimModLoadingOverlay(),
        };
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
        status.Text = "Loading your local play history...";
        loadingOverlay.ShowLoading("Preparing coaching", "Reading your local osu!standard history");
        _ = loadAsync(loading.Token);
    }

    private async Task loadAsync(CancellationToken cancellationToken)
    {
        try
        {
            StatisticsHistoryLoadResult history = await StatisticsHistoryLoader.LoadAsync(source, cancellationToken).ConfigureAwait(false);
            NativeCoachingWorkspaceModel next = NativeCoachingWorkspaceModel.Build(history.Runs, analyses);
            if (!IsDisposed)
                Schedule(() => apply(history.Runs, history.TotalAvailableRunCount, next));
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
                    status.Text = $"Your local play history could not be loaded. {error.Message}";
                });
        }
    }

    private void apply(IReadOnlyList<LocalReplay> nextReplays, int total, NativeCoachingWorkspaceModel next)
    {
        replays = nextReplays;
        workspace = next;
        status.Text = total > nextReplays.Count
            ? $"Coaching uses your latest {nextReplays.Count:N0} of {total:N0} local osu!standard plays"
            : $"Coaching uses {nextReplays.Count:N0} local osu!standard plays";
        loadingOverlay.HideLoading();
        updateWorkspace();
    }

    private void selectRun(Guid scoreId)
    {
        workspace = NativeCoachingWorkspaceModel.Build(replays, analyses, scoreId);
        updateWorkspace();
    }

    public void SetAnalysisProgress(int completed, int total, string currentTitle)
    {
        if (!acceptingAnalysisProgress || total <= 0)
            return;

        status.Text = completed >= total
            ? $"Analysed {total:N0} recent saved {(total == 1 ? "replay" : "replays")}. Updating coaching..."
            : $"Analysing recent saved replays {completed + 1:N0}/{total:N0}: {currentTitle}";
        loadingOverlay.SetProgress(
            completed >= total ? "Updating your coaching report" : currentTitle,
            completed,
            total);
    }

    public void BeginAnalysisProgress()
    {
        acceptingAnalysisProgress = true;
        loadingOverlay.ShowLoading("Analysing recent replays", "Preparing saved replay analysis");
    }

    public void ApplyNewAnalyses(int completed, int failed)
    {
        acceptingAnalysisProgress = false;
        Guid? selectedScoreId = workspace?.SelectedRun?.ScoreId;
        workspace = NativeCoachingWorkspaceModel.Build(replays, analyses, selectedScoreId);
        updateWorkspace();

        status.Text = completed switch
        {
            > 0 when failed > 0 => $"Added exact analysis for {completed:N0} recent saved {(completed == 1 ? "replay" : "replays")}; {failed:N0} could not be read.",
            > 0 => $"Added exact timing and miss locations from {completed:N0} recent saved {(completed == 1 ? "replay" : "replays")}.",
            _ when failed > 0 => "Recent saved replays could not be analysed. You can still open any available replay below.",
            _ => status.Text,
        };
        loadingOverlay.HideLoading();
    }

    public void SetAnalysisError()
    {
        acceptingAnalysisProgress = false;
        loadingOverlay.HideLoading();
        status.Text = "Recent replay analysis stopped. You can still open any available replay below.";
    }

    private void updateWorkspace()
    {
        NativeCoachingWorkspaceModel model = workspace ?? NativeCoachingWorkspaceModel.Build(Array.Empty<LocalReplay>(), analyses);
        CoachingReport report = model.Report;
        LocalReplay? selected = model.SelectedRun;

        updateSessionHeader(model);
        trendChart.SetRuns(model.TrendRuns, selected?.ScoreId, selectRun);
        updateSelectedRun(selected, report.Intelligence.SelectedRunPrediction);
        updateExactAnalysis(selected);
        updateChanges(report.Intelligence);
        updateRecommendation(report.Intelligence.Recommendations);
        refreshRunList();
    }

    private void updateSessionHeader(NativeCoachingWorkspaceModel model)
    {
        LocalReplay? selected = model.SelectedRun;
        CoachingSessionSummary? session = model.Session;
        headerArtwork.Clear();
        if (!string.IsNullOrWhiteSpace(selected?.BackgroundPath))
            headerArtwork.Add(new AimModLocalArtwork(selected.BackgroundPath));

        sessionTitle.Text = session is null ? "Your coaching workspace" : $"{session.StartedAt:MMMM d} session";
        sessionPlays.Text = session is null ? "No plays" : $"{session.PlayCount:N0} {(session.PlayCount == 1 ? "play" : "plays")}";
        sessionDuration.Text = session is null ? "No session yet" : formatDuration(session.Duration);
        sessionAccuracy.Text = session?.MedianAccuracy is { } median ? $"{median:P1}" : "-";

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

    private void updateSelectedRun(LocalReplay? run, CoachingAccuracyPrediction? prediction)
    {
        selectedRunHost.Clear();
        if (run is null)
        {
            selectedRunHost.Add(flow("Play a local osu!standard map to begin a coaching session.", 14, AimModPalette.Muted));
            return;
        }

        selectedRunHost.Add(new SelectedRunCard(run, prediction, run.HasReplayFile ? () => openReplay(run) : null));
    }

    private void updateExactAnalysis(LocalReplay? run)
    {
        exactAnalysisHost.Clear();
        if (run is null)
        {
            exactAnalysisHost.Add(flow("No run selected.", 13, AimModPalette.Muted));
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
                    miniMetric("Lower hits", (result.Summary.Ok + result.Summary.Meh).ToString("N0"), Colour4.FromHex("FFD45A")),
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

    private void updateChanges(CoachingIntelligence intelligence)
    {
        changesHost.Clear();

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
            mechanicsDetail(intelligence.Mechanics),
            intelligence.Mechanics.WeakestMapSegment ?? $"{intelligence.Mechanics.ExactAnalysisRunCount:N0} exact runs",
            AimModPalette.Success));
    }

    private void updateRecommendation(IReadOnlyList<CoachingRecommendation> recommendations)
    {
        recommendationHost.Clear();
        CoachingRecommendation? recommendation = recommendations.FirstOrDefault();
        if (recommendation is null)
        {
            recommendationHost.Add(flow(
                "Play more comparable local maps with saved replays. AimMod will recommend a focused practice target once the history is large enough.",
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
            runList.Add(flow("No local runs match this search.", 14, AimModPalette.Muted).With(text => text.Padding = new MarginPadding(18)));
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
            Height = 156,
            Masking = true,
            CornerRadius = 14,
            BorderThickness = 1,
            BorderColour = AimModPalette.Border,
        };

        artwork = new Container { RelativeSizeAxes = Axes.Both };
        title = label("Your coaching workspace", 27, AimModPalette.Text, "Bold");
        plays = label("No plays", 14, AimModPalette.Text, "SemiBold");
        duration = label("No session yet", 14, AimModPalette.Text, "SemiBold");
        accuracy = label("-", 25, AimModPalette.Text, "Bold");
        trend = label("More plays needed", 16, AimModPalette.Muted, "Bold");

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
                Margin = new MarginPadding { Left = 28 },
                Direction = FillDirection.Vertical,
                Spacing = new(13),
                Children = new Drawable[]
                {
                    title,
                    new FillFlowContainer
                    {
                        AutoSizeAxes = Axes.Both,
                        Direction = FillDirection.Horizontal,
                        Spacing = new(28),
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
        var panel = new WorkspacePanel(new MarginPadding { Left = 18, Right = 16, Vertical = 18 });
        var body = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new(10),
        };
        panel.Child = body;
        body.Add(sectionLine("Accuracy and misses over recent plays", "Select any point to inspect that run"));
        body.Add(chart = new CoachingTrendChart
        {
            RelativeSizeAxes = Axes.X,
            Height = 250,
        });
        body.Add(label("SELECTED RUN", 10, AimModPalette.Pink, "Bold"));
        body.Add(selectedHost = new FillFlowContainer<Drawable>
        {
            RelativeSizeAxes = Axes.X,
            Height = 104,
            Direction = FillDirection.Vertical,
        });
        body.Add(label("EXACT REPLAY ANALYSIS", 10, AimModPalette.Cyan, "Bold"));
        body.Add(analysisHost = new FillFlowContainer<Drawable>
        {
            RelativeSizeAxes = Axes.X,
            Height = 135,
            Direction = FillDirection.Vertical,
            Spacing = new(8),
        });
        return panel;
    }

    private static Container createCoachPanel(
        out FillFlowContainer<Drawable> changes,
        out FillFlowContainer<Drawable> recommendation)
    {
        Container outer = new Container
        {
            RelativeSizeAxes = Axes.Both,
            Padding = new MarginPadding { Left = 4 },
        };
        var panel = new WorkspacePanel(new MarginPadding(18));
        outer.Child = panel;
        var body = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new(10),
        };
        panel.Child = body;
        body.Add(sectionLine("What changed", "Compared across your measured local history"));
        body.Add(changes = new FillFlowContainer<Drawable>
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new(3),
        });
        body.Add(new Box
        {
            RelativeSizeAxes = Axes.X,
            Height = 1,
            Colour = AimModPalette.Border,
            Margin = new MarginPadding { Vertical = 4 },
        });
        body.Add(sectionLine("Practice next", "Chosen from comparable personal plays"));
        body.Add(recommendation = new FillFlowContainer<Drawable>
        {
            RelativeSizeAxes = Axes.X,
            Height = 154,
            Direction = FillDirection.Vertical,
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

    private static string mechanicsDetail(CoachingMechanicsProfile mechanics)
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
        return details.Count == 0 ? $"{mechanics.JudgementCount:N0} exact judgements measured." : string.Join(", ", details) + ".";
    }

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
        if (sourceChanges is not null)
            sourceChanges.SourceChanged -= sourceChanged;
        base.Dispose(isDisposing);
    }

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
        public SelectedRunCard(LocalReplay run, CoachingAccuracyPrediction? prediction, Action? open)
        {
            RelativeSizeAxes = Axes.X;
            Height = 100;
            Masking = true;
            CornerRadius = 10;
            BorderThickness = 1;
            BorderColour = AimModPalette.Border;
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
                    Margin = new MarginPadding { Left = 18 },
                    Padding = new MarginPadding { Right = 190 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(3),
                    Children = new Drawable[]
                    {
                        truncatingLabel($"{run.Title} [{run.Difficulty}]", 16, AimModPalette.Text, 520, "Bold"),
                        truncatingLabel($"{run.Artist}  //  {run.PlayedAt:yyyy-MM-dd HH:mm}", 11, AimModPalette.Muted, 520),
                        truncatingLabel($"{run.Accuracy:P2}  //  {run.MissCount:N0} misses  //  {run.MaxCombo:N0}x{formatPpSuffix(run.PerformancePoints)}", 12, AimModPalette.Cyan, 520, "SemiBold"),
                    },
                },
                new FillFlowContainer
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    AutoSizeAxes = Axes.Both,
                    Margin = new MarginPadding { Right = 14 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(8),
                    Children = new Drawable[]
                    {
                        new AimModDifficultyPill(run.StarRating),
                        prediction is null
                            ? label("No prediction", 10, AimModPalette.Muted)
                            : label($"Expected {prediction.ExpectedAccuracy:P1}", 10, AimModPalette.Success, "SemiBold"),
                        new ActionButton(open is null ? "Replay unavailable" : "Open replay", open),
                    },
                },
            };
        }
    }

    private partial class SectionLine : CompositeDrawable
    {
        private readonly TruncatingSpriteText title;
        private readonly TruncatingSpriteText detail;

        public SectionLine(string titleText, string detailText)
        {
            RelativeSizeAxes = Axes.X;
            Height = 34;
            InternalChildren = new Drawable[]
            {
                title = truncatingLabel(titleText, 17, AimModPalette.Text, 200, "SemiBold"),
                detail = truncatingLabel(detailText, 10, AimModPalette.Muted, 160).With(text =>
                {
                    text.Anchor = Anchor.CentreRight;
                    text.Origin = Anchor.CentreRight;
                }),
            };
        }

        protected override void Update()
        {
            base.Update();
            const float gap = 16;
            float titleWidth = Math.Max(80, DrawWidth * 0.58f - gap);
            float detailWidth = Math.Max(60, DrawWidth - titleWidth - gap);
            title.MaxWidth = titleWidth;
            detail.MaxWidth = detailWidth;
        }
    }

    private static string formatPpSuffix(double? pp) =>
        pp is { } value && double.IsFinite(value) && value > 0 ? $"  //  {value:0.0}pp" : string.Empty;

    private partial class RecommendationCard : CompositeDrawable
    {
        public RecommendationCard(CoachingRecommendation recommendation, Action? open)
        {
            RelativeSizeAxes = Axes.X;
            Height = 152;
            Masking = true;
            CornerRadius = 10;
            BorderThickness = 1;
            BorderColour = AimModPalette.PinkDark;
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

        public RunPickerRow(CoachingRecentRun run, bool selected, Action select)
        {
            this.select = select;
            RelativeSizeAxes = Axes.X;
            Height = 68;
            Masking = true;
            CornerRadius = 9;
            BorderThickness = selected ? 1 : 0;
            BorderColour = AimModPalette.Pink;
            Children = new Drawable[]
            {
                background = new Box { RelativeSizeAxes = Axes.Both, Colour = selected ? AimModPalette.PanelRaised : AimModPalette.Panel },
                new Box
                {
                    RelativeSizeAxes = Axes.Y,
                    Width = selected ? 5 : 3,
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
            background.FadeColour(AimModPalette.Panel, 100);
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
            CornerRadius = 7;
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
                    Padding = new MarginPadding { Horizontal = 11, Vertical = 5 },
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
            Height = 250;
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas, Alpha = 0.38f },
                new Box
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 1,
                    Y = 30,
                    Colour = AimModPalette.Border,
                },
                new Box
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 1,
                    Y = 105,
                    Colour = AimModPalette.Border,
                },
                new Box
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 1,
                    Y = 180,
                    Colour = AimModPalette.Border,
                },
                graph = new LineGraph
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 184,
                    Padding = new MarginPadding { Top = 22, Bottom = 12, Left = 38, Right = 14 },
                    LineColour = AimModPalette.Pink,
                    DefaultValueCount = NativeCoachingWorkspaceModel.MaximumTrendRuns,
                },
                markers = new Container
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 150,
                    Position = new(38, 27),
                    Width = -52,
                },
                upperLabel = label("100%", 9, AimModPalette.Muted).With(text => text.Position = new(4, 22)),
                lowerLabel = label("80%", 9, AimModPalette.Muted).With(text => text.Position = new(4, 168)),
                label("MISSES", 8, AimModPalette.Muted, "Bold").With(text => text.Position = new(4, 198)),
                missBars = new Container
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 34,
                    Position = new(42, 194),
                    Width = -58,
                },
                timeRange = label("No plays to graph", 9, AimModPalette.Muted).With(text =>
                {
                    text.Anchor = Anchor.BottomRight;
                    text.Origin = Anchor.BottomRight;
                    text.Margin = new MarginPadding { Right = 8, Bottom = 4 };
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
                timeRange.Text = "No plays to graph";
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
                    Height = run.MissCount == 0 ? 2 : 4 + 28f * run.MissCount / maximumMisses,
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
            CornerRadius = 13;
            BorderThickness = 1;
            BorderColour = AimModPalette.Border;
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
                BorderThickness = selected ? 3 : 1,
                BorderColour = selected ? AimModPalette.Text : colour,
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

using AimMod.Desktop.Coaching;
using AimMod.Desktop.Hub;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Replays;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;

namespace AimMod.Desktop;

public partial class NativeReplayRouteView
{
    private SpriteText summaryAccuracy = null!;
    private SpriteText summaryPerformance = null!;
    private SpriteText summaryMisses = null!;
    private SpriteText summaryCombo = null!;
    private SpriteText analysisTitle = null!;
    private WrappedLabel analysisSummary = null!;
    private WrappedLabel analysisNextPlay = null!;
    private ReplayJudgementBar judgementCounts = null!;
    private FillFlowContainer<Drawable> notableRows = null!;
    private FillFlowContainer<Drawable> mapPatternRows = null!;
    private Container analysisCard = null!;
    private NativeHubReplaySharePanel hubSharePanel = null!;
    private AimModButton practiceButton = null!;
    private WrappedLabel analysisMismatch = null!;
    private AimModSubsectionHeader mistakesHeader = null!;
    private CancellationTokenSource? mapPatternLoading;
    private long analysisRevision;
    private bool analysisHasResult;

    private static AimModScrollContainer tabScroll(params Drawable[] children) => new()
    {
        RelativeSizeAxes = Axes.Both,
        Child = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Padding = new MarginPadding { Left = 14, Top = 8, Right = 12, Bottom = 24 },
            Direction = FillDirection.Vertical,
            Spacing = new(AimModVisualStyle.RowSpacing),
            Children = children,
        },
    };

    private Drawable createSummaryContent(
        OsuHubReplayShareService? hubShareService,
        IHubCredentialStore? hubCredentialStore,
        IOsuHubUploadQueue? hubUploadQueue,
        IHubSharingPreferenceStore? hubPreferenceStore,
        Action<Uri>? openUrl,
        Action<string>? copyText) => tabScroll(
        new AimModSubsectionHeader("Run summary", "selected attempt"),
        new GridContainer
        {
            RelativeSizeAxes = Axes.X,
            Height = 128,
            ColumnDimensions = new[]
            {
                new Dimension(),
                new Dimension(GridSizeMode.Absolute, AimModVisualStyle.RelatedSpacing),
                new Dimension(),
            },
            RowDimensions = new[]
            {
                new Dimension(),
                new Dimension(GridSizeMode.Absolute, AimModVisualStyle.RelatedSpacing),
                new Dimension(),
            },
            Content = new[]
            {
                new Drawable[]
                {
                    new MetricTile("Accuracy", AimModPalette.Text, out summaryAccuracy),
                    Empty(),
                    new MetricTile("Performance", AimModPalette.Cyan, out summaryPerformance),
                },
                new[] { Empty(), Empty(), Empty() },
                new Drawable[]
                {
                    new MetricTile("Misses", AimModPalette.Text, out summaryMisses),
                    Empty(),
                    new MetricTile("Max combo", AimModPalette.Text, out summaryCombo),
                },
            },
        },
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
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding { Horizontal = 14, Top = 12, Bottom = 12 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(AimModVisualStyle.RelatedSpacing),
                    Children = new Drawable[]
                    {
                        analysisTitle = makeText("Analysing exact judgements...", 14, AimModPalette.Text, "Bold"),
                        judgementCounts = new ReplayJudgementBar { Alpha = 0 },
                        analysisSummary = new WrappedLabel("Preparing replay details", 12, AimModPalette.Muted),
                        analysisMismatch = new WrappedLabel(string.Empty, 12, AimModPalette.Yellow, "SemiBold") { Alpha = 0 },
                    },
                },
            },
        },
        new AimModSubsectionHeader("Focus for your next play"),
        new Container
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Masking = true,
            CornerRadius = AimModVisualStyle.CardRadius,
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                new Box { RelativeSizeAxes = Axes.Y, Width = 3, Colour = AimModPalette.Accent },
                new Container
                {
                    Padding = new MarginPadding { Left = 14, Top = 12, Right = 12, Bottom = 12 },
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Child = analysisNextPlay = new WrappedLabel("Select a run to get a measured next step.", 12, AimModPalette.Text, "SemiBold"),
                },
            },
        },
        new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Full,
            Spacing = new(AimModVisualStyle.RelatedSpacing),
            Children = new Drawable[]
            {
                practiceButton = new AimModButton("Practice this map", () =>
                {
                    if (selectedReplay is not null) openPractice?.Invoke(selectedReplay.Title);
                }, primary: true)
                {
                    Enabled = { Value = false },
                },
                new OpenBeatmapButton(() => selectedReplay, openBeatmap) { Size = new(132, AimModVisualStyle.ControlHeight) },
            },
        },
        new AimModSubsectionHeader("AimMod Hub", "manual sharing"),
        // osu!'s switches and dropdowns in the share panel take their accent from this scope.
        new ReplayOverlayColourScope
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Child = hubSharePanel = new NativeHubReplaySharePanel(
                hubShareService,
                hubCredentialStore,
                hubUploadQueue,
                hubPreferenceStore,
                openUrl,
                copyText),
        });

    private Drawable createMistakesContent() => tabScroll(
        mistakesHeader = new AimModSubsectionHeader("Mistakes", "click to review"),
        notableRows = new FillFlowContainer<Drawable>
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new(6),
        },
        new AimModSubsectionHeader("Across attempts", "same difficulty"),
        mapPatternRows = new FillFlowContainer<Drawable>
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new(5),
        });

    private void showRunSummary(LocalReplay replay)
    {
        summaryAccuracy.Text = formatAccuracy(replay.Accuracy);
        summaryPerformance.Text = replay.PerformancePoints is { } pp ? $"{pp:0.#}pp" : $"{replay.TotalScore:N0}";
        summaryMisses.Text = replay.MissCount.ToString("N0");
        summaryMisses.Colour = replay.MissCount > 0 ? AimModPalette.Pink : AimModPalette.Success;
        summaryCombo.Text = $"{replay.MaxCombo:N0}x";
    }

    public void ShowAnalysisState(ReplayAnalysisState state)
    {
        if (state.Revision < analysisRevision)
            return;

        analysisRevision = state.Revision;
        switch (state.Status)
        {
            case ReplayAnalysisStatus.Running:
                analysisHasResult = false;
                analysisTitle.Text = "Analysing exact judgements...";
                analysisSummary.Text = "Running accelerated official ruleset playback";
                judgementCounts.Alpha = 0;
                showNotableState("Exact judgement analysis is in progress.", AimModPalette.Muted);
                clearMoments();
                analysisNextPlay.Text = "A measured focus will appear when exact judgement analysis completes.";
                analysisCard.FadeIn(150);
                break;

            case ReplayAnalysisStatus.Completed when state.Result is not null:
                showCompletedAnalysis(state.Result);
                break;

            case ReplayAnalysisStatus.Failed:
                analysisHasResult = false;
                showAnalysisFailure(
                    state.Error?.Message ?? "AimMod could not analyse this replay.",
                    "Exact coaching focus is unavailable for this run. Replay playback is still available.");
                break;

            case ReplayAnalysisStatus.Cancelled:
                analysisHasResult = false;
                showAnalysisFailure(
                    "Exact replay analysis was cancelled.",
                    "Select the run again to calculate its coaching focus.");
                break;

            case ReplayAnalysisStatus.Idle:
                analysisHasResult = false;
                showAnalysisFailure(
                    "Exact replay analysis has not started.",
                    "Open this run to calculate notable moments and a measured coaching focus.");
                break;
        }
    }

    public void ShowAnalysisError(string message)
    {
        analysisHasResult = false;
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
        judgementCounts.Alpha = 0;
        showNotableState("Mistakes will appear when analysis completes.", AimModPalette.Muted);
        clearMoments();
        analysisNextPlay.Text = "A measured focus will appear when exact judgement analysis completes.";
        showMapPatternState("Analyse this replay to compare it with other attempts.");
        analysisCard.FadeIn(150);
    }

    private void showCompletedAnalysis(ReplayAnalysisResult result)
    {
        analysisHasResult = true;
        ReplayAnalysisPresentation presentation = ReplayAnalysisPresenter.Present(result);
        analysisTitle.Text = "Exact replay analysis";
        analysisSummary.Text = DescribeMistakes(result.Summary, transportMomentsAfter(result));
        showScoreMismatch(selectedReplay, result.Summary);
        showJudgementCounts(result.Summary);
        analysisNextPlay.Text = measuredNextPlay(result, presentation.NextPlay);
        transportBar.SetAnalysis(result);
        showNotableRows();
        hubSharePanel.SetAnalysisAvailable(true);
        loadMapPattern();
        analysisCard.FadeIn(150);
    }

    /// <summary>
    /// The saved score and AimMod's re-simulation should agree. When they do not (for example a replay
    /// recorded on a different version of the beatmap), say so instead of silently showing both.
    /// </summary>
    internal static string? DescribeScoreMismatch(LocalReplay? replay, ReplayJudgementSummary summary)
    {
        if (replay is null || replay.MissCount == summary.Miss)
            return null;

        static string misses(int count) => $"{count:N0} {(count == 1 ? "miss" : "misses")}";
        return $"Score: {misses(replay.MissCount)} · analysis found {misses(summary.Miss)}. The replay may not match this version of the beatmap.";
    }

    private void showScoreMismatch(LocalReplay? replay, ReplayJudgementSummary summary)
    {
        string? mismatch = DescribeScoreMismatch(replay, summary);
        analysisMismatch.Text = mismatch ?? string.Empty;
        analysisMismatch.Alpha = mismatch is null ? 0 : 1;
    }

    private IReadOnlyList<ReplayMoment> transportMomentsAfter(ReplayAnalysisResult result)
    {
        transport.SetAnalysis(result);
        return transport.Moments;
    }

    /// <summary>One sentence for the analysis card; the individual moments are listed below it.</summary>
    internal static string DescribeMistakes(ReplayJudgementSummary summary, IReadOnlyList<ReplayMoment> moments)
    {
        int judged = summary.Great + summary.Ok + summary.Meh + summary.Miss;
        if (summary.Miss == 0 && summary.SliderBreaks == 0)
            return judged > 0 ? $"Clean run: no misses or slider breaks across {judged:N0} judged objects." : "No judged objects were recorded.";

        var parts = new List<string>();
        if (summary.Miss > 0)
            parts.Add($"{summary.Miss:N0} {(summary.Miss == 1 ? "miss" : "misses")}");
        if (summary.SliderBreaks > 0)
            parts.Add($"{summary.SliderBreaks:N0} slider {(summary.SliderBreaks == 1 ? "break" : "breaks")}");
        string first = moments.Count > 0 ? $" First at {ReplayTimeFormat.Precise(moments[0].TimeMs)}." : string.Empty;
        return $"{string.Join(" and ", parts)} across {judged:N0} judged objects.{first}";
    }

    private void showAnalysisFailure(string message, string nextPlay)
    {
        analysisTitle.Text = "Replay analysis unavailable";
        analysisSummary.Text = message;
        judgementCounts.Alpha = 0;
        showNotableState(message, AimModPalette.Danger);
        analysisMismatch.Alpha = 0;
        clearMoments();
        analysisNextPlay.Text = nextPlay;
        analysisCard.FadeIn(150);
    }

    private void clearMoments()
    {
        transport.SetAnalysis(null);
        transportBar.SetAnalysis(null);
        mistakesHeader.Detail = "click to review";
    }

    private void showJudgementCounts(ReplayJudgementSummary summary)
    {
        judgementCounts.SetSummary(summary);
        judgementCounts.Alpha = 1;
    }

    private void showNotableState(string message, Colour4 colour)
    {
        notableRows.Clear();
        notableRows.Add(new InspectorStateRow(message, colour));
    }

    private void showNotableRows()
    {
        notableRows.Clear();
        IReadOnlyList<ReplayMoment> moments = transport.Moments;
        foreach (ReplayMoment moment in moments)
            notableRows.Add(new NotableMomentRow(moment, () => jumpToMoment(moment)));

        int misses = moments.Count(moment => moment.Severity == ReplayMomentSeverity.Miss);
        int breaks = moments.Count - misses;
        var parts = new List<string>();
        if (misses > 0)
            parts.Add($"{misses:N0} {(misses == 1 ? "miss" : "misses")}");
        if (breaks > 0)
            parts.Add($"{breaks:N0} slider {(breaks == 1 ? "break" : "breaks")}");
        mistakesHeader.Detail = parts.Count == 0 ? "clean run" : string.Join(" · ", parts);

        if (moments.Count == 0)
            notableRows.Add(new InspectorStateRow("No misses or slider breaks were found in this run.", AimModPalette.Success));
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
            mapPatternRows.Add(new WrappedLabel(message, 12, AimModPalette.Text));
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

    private static string formatAccuracy(double value) => double.IsFinite(value) ? $"{value * 100:0.00}%" : "--";

    private static string formatTime(double milliseconds) => ReplayTimeFormat.Precise(milliseconds);

    /// <summary>Transport clock at tenth-of-a-second precision so labels change at most ten times a second.</summary>
    internal static string formatClock(double milliseconds) => ReplayTimeFormat.Clock(milliseconds);

    private static SpriteText makeText(string value, float size, Colour4 colour, string weight = "Regular") => new OsuSpriteText
    {
        Text = value,
        Font = new FontUsage(size: AimModVisualStyle.Readable(size), weight: weight),
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
        Font = new FontUsage(size: AimModVisualStyle.Readable(size), weight: weight),
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

    private partial class MetricTile : CompositeDrawable
    {
        public MetricTile(string label, Colour4 colour, out SpriteText value)
        {
            RelativeSizeAxes = Axes.Both;
            Masking = true;
            CornerRadius = AimModVisualStyle.CardRadius;
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                new FillFlowContainer
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    AutoSizeAxes = Axes.Both,
                    Padding = new MarginPadding { Horizontal = 12 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(0, 2),
                    Children = new Drawable[]
                    {
                        makeText(label, 11, AimModPalette.Muted, "SemiBold"),
                        value = makeText("--", 20, colour, "Bold"),
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
            AutoSizeAxes = Axes.Y;
            Masking = true;
            CornerRadius = AimModVisualStyle.ControlRadius;
            InternalChildren = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                new Box { RelativeSizeAxes = Axes.Y, Width = 3, Colour = colour },
                new SpriteIcon
                {
                    Position = new(14, 13),
                    Size = new(12),
                    Icon = FontAwesome.Solid.InfoCircle,
                    Colour = colour,
                },
                new Container
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding { Left = 36, Right = 12, Top = 11, Bottom = 11 },
                    Child = new WrappedLabel(message, 12, colour == AimModPalette.Muted ? AimModPalette.Muted : AimModPalette.Text, "SemiBold"),
                },
            };
        }
    }

    private partial class NotableMomentRow : AimModInteractiveSurface
    {
        private readonly FillFlowContainer content;

        public NotableMomentRow(ReplayMoment moment, Action action)
        {
            RelativeSizeAxes = Axes.X;
            Action = action;
            CornerRadius = AimModVisualStyle.ControlRadius;
            BackgroundColour = AimModPalette.PanelRaised;
            Colour4 colour = ReplayMomentColours.For(moment.Severity);
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Y, Width = 3, Colour = colour },
                content = new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding { Left = 14, Right = 12, Top = 10, Bottom = 10 },
                    Direction = FillDirection.Vertical,
                    Spacing = new(0, 4),
                    Children = new Drawable[]
                    {
                        new Container
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Children = new Drawable[]
                            {
                                new FillFlowContainer
                                {
                                    AutoSizeAxes = Axes.Both,
                                    Direction = FillDirection.Horizontal,
                                    Spacing = new(8, 0),
                                    Children = new Drawable[]
                                    {
                                        new OsuSpriteText
                                        {
                                            Text = ReplayTimeFormat.Precise(moment.TimeMs),
                                            Font = new FontUsage(size: 13, weight: "Bold", fixedWidth: true),
                                            Colour = AimModPalette.Text,
                                        },
                                        makeText(moment.ObjectLabel, 12, AimModPalette.Muted, "SemiBold"),
                                    },
                                },
                                new OsuSpriteText
                                {
                                    Anchor = Anchor.TopRight,
                                    Origin = Anchor.TopRight,
                                    Text = moment.SeverityLabel.ToUpperInvariant(),
                                    Font = AimModVisualStyle.LabelFont,
                                    Colour = colour,
                                },
                            },
                        },
                        new WrappedLabel(moment.Detail, 11, AimModPalette.Muted),
                    },
                },
            };
        }

        protected override void Update()
        {
            base.Update();
            float height = content.DrawHeight;
            if (height > 0 && Math.Abs(Height - height) > 0.5f)
                Height = height;
        }
    }

    private partial class WrappedLabel : CompositeDrawable
    {
        private readonly TextFlowContainer flow;
        private string text = string.Empty;
        private bool initialised;

        public WrappedLabel(string value, float size, Colour4 colour, string weight = "Regular", bool centred = false)
        {
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;
            InternalChild = flow = new TextFlowContainer(sprite =>
            {
                sprite.Font = new FontUsage(size: AimModVisualStyle.Readable(size), weight: weight);
                sprite.Colour = colour;
            })
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                TextAnchor = centred ? Anchor.TopCentre : Anchor.TopLeft,
            };
            Text = value;
        }

        public string Text
        {
            get => text;
            set
            {
                if (initialised && text == (value ?? string.Empty))
                    return;
                text = value ?? string.Empty;
                flow.Clear();
                flow.AddText(text);
                initialised = true;
            }
        }
    }
}

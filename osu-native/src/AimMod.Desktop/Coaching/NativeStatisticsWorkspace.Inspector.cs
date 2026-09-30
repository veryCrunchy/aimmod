using osu.Game.Graphics.Sprites;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;

namespace AimMod.Desktop.Coaching;

public partial class NativeStatisticsWorkspace
{
    private void showEmptyInspector()
    {
        inspectorContent.Clear();
        inspectorContent.Add(new Container
        {
            RelativeSizeAxes = Axes.X,
            Height = 240,
            Child = new FillFlowContainer
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Vertical,
                Spacing = new(8),
                Children = new Drawable[]
                {
                    new SpriteIcon { Anchor = Anchor.TopCentre, Origin = Anchor.TopCentre, Size = new(22), Icon = FontAwesome.Solid.MousePointer, Colour = AimModPalette.Muted },
                    StatisticsChartFormat.Text("Select a play", 16, AimModPalette.Text, "SemiBold").With(t => { t.Anchor = Anchor.TopCentre; t.Origin = Anchor.TopCentre; }),
                    StatisticsChartFormat.Text("Pick a row or a chart point.", 12, AimModPalette.Muted).With(t => { t.Anchor = Anchor.TopCentre; t.Origin = Anchor.TopCentre; }),
                },
            },
        });
    }

    private void showInspector(LocalReplay replay)
    {
        StatisticsMapSummary map = mapIndex.Summarise(replay);
        IReadOnlyList<LocalReplay> attempts = mapIndex.Attempts(replay);
        StatisticsTypicalResult typical = mapIndex.Typical(replay.StarRating);
        float width = Math.Max(200, inspectorColumn.DrawWidth - 36);
        double? accuracy = StatisticsInsightsBuilder.Value(replay, StatisticsMetric.Accuracy);
        double? mapAverage = map.AverageAccuracy * 100;
        double? mapBestPp = map.BestPerformancePoints;

        inspectorContent.Clear();
        if (inspectorOverlay)
            inspectorContent.Add(new AimModButton("Back to plays", () => setInspectorOverlay(false)) { Margin = new MarginPadding { Bottom = 4 } });

        inspectorContent.AddRange(new Drawable[]
        {
            new Box { RelativeSizeAxes = Axes.X, Height = 3, Colour = AimModVisualStyle.DifficultyColour(replay.StarRating) },
            new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new(3),
                Children = new Drawable[]
                {
                    StatisticsChartFormat.Truncating(replay.Title, 18, AimModPalette.Text, "Bold").With(t => t.MaxWidth = width),
                    StatisticsChartFormat.Truncating($"{replay.Artist}  ·  [{replay.Difficulty}]", 12, AimModPalette.Muted).With(t => t.MaxWidth = width),
                },
            },
            new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Horizontal,
                Spacing = new(6),
                Children = pills(replay),
            },
            new GridContainer
            {
                RelativeSizeAxes = Axes.X,
                Height = 144,
                Margin = new MarginPadding { Top = 4 },
                ColumnDimensions = [new Dimension(GridSizeMode.Relative, 0.5f), new Dimension(GridSizeMode.Relative, 0.5f)],
                RowDimensions = [new Dimension(GridSizeMode.Relative, 0.5f), new Dimension(GridSizeMode.Relative, 0.5f)],
                Content = new[]
                {
                    new[]
                    {
                        tile("Accuracy", StatisticsChartFormat.Value(StatisticsMetric.Accuracy, accuracy),
                            accuracyContext(accuracy, attempts.Count > 1 ? mapAverage : typical.AccuracyPercent, attempts.Count > 1 ? "avg here" : "avg at this level")),
                        tile("PP", replay.PerformancePoints is { } pp && pp >= 0 ? $"{pp:0}pp" : StatisticsChartFormat.Missing,
                            replay.PerformancePoints is not >= 0
                                ? ("No PP: unranked or failed", AimModPalette.Muted)
                                : attempts.Count > 1 && mapBestPp is { } best
                                    ? (best <= replay.PerformancePoints + 0.01 ? "Your best here" : $"Best here {best:0}pp", AimModPalette.Muted)
                                    : typical.MedianPerformancePoints is { } usual ? ($"Usually {usual:0}pp", AimModPalette.Muted) : (string.Empty, AimModPalette.Muted)),
                    },
                    new[]
                    {
                        tile("Misses", Math.Max(0, replay.MissCount).ToString("N0"),
                            attempts.Count > 1 ? ($"Miss-free {map.MissFreeRate * 100:0}% here", AimModPalette.Muted) : (replay.MissCount == 0 ? "Full combo" : string.Empty, AimModPalette.Success)),
                        tile("Combo", $"{Math.Max(0, replay.MaxCombo):N0}x",
                            attempts.Count > 1 && map.BestCombo > replay.MaxCombo ? ($"Best {map.BestCombo:N0}x", AimModPalette.Muted) : ("Your best here", AimModPalette.Muted)),
                    },
                },
            },
            separator(),
            attemptsSection(replay, attempts, map, width),
            separator(),
            actions(replay),
            detailRow("Played", StatisticsChartFormat.DateTime(replay.PlayedAt)),
            detailRow("Saved", replay.IsLocallyStored
                ? replay.OnlineScoreId > 0 ? "On this PC, submitted online" : "On this PC"
                : "Online only"),
        });
    }

    private static (string Text, Colour4 Colour) accuracyContext(double? value, double? reference, string label)
    {
        if (value is not { } current || reference is not { } average)
            return (string.Empty, AimModPalette.Muted);
        double change = current - average;
        (Colour4 colour, _) = StatisticsChartFormat.ChangeStyle(StatisticsMetric.Accuracy, change, true);
        string sign = Math.Abs(change) < StatisticsChartFormat.ChangeThreshold(StatisticsMetric.Accuracy) ? "=" : change > 0 ? "+" : "−";
        return ($"{sign}{Math.Abs(change):0.00} vs {label}", colour);
    }

    private Drawable attemptsSection(LocalReplay replay, IReadOnlyList<LocalReplay> attempts, StatisticsMapSummary map, float width)
    {
        var section = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new(6),
        };
        section.Add(new Container
        {
            RelativeSizeAxes = Axes.X,
            Height = 18,
            Children = new Drawable[]
            {
                StatisticsChartFormat.Text("Your attempts here", 13, AimModPalette.Text, "SemiBold"),
                StatisticsChartFormat.Text(attempts.Count == 1 ? "1 play" : $"{attempts.Count:N0} plays", 12, AimModPalette.Muted).With(t =>
                {
                    t.Anchor = Anchor.TopRight;
                    t.Origin = Anchor.TopRight;
                }),
            },
        });

        if (attempts.Count < 2)
        {
            section.Add(StatisticsChartFormat.Text("First recorded play of this difficulty.", 12, AimModPalette.Muted));
            return section;
        }

        CoachingChartPoint[] points = attempts.Where(run => StatisticsInsightsBuilder.Value(run, StatisticsMetric.Accuracy) is not null)
                                             .Select(run => new CoachingChartPoint(run.ScoreId, run.PlayedAt, run.Accuracy * 100))
                                             .ToArray();
        var chart = new StatisticsTrendChart(compact: true) { ResolvePlay = id => runsById.GetValueOrDefault(id) };
        chart.SetView(new StatisticsMetricView(StatisticsMetric.Accuracy, points, [], 0, [],
            points.Length == 0 ? null : points.Average(point => point.Value), null, string.Empty, [], [], 0), replay.ScoreId);
        chart.PlaySelected += id =>
        {
            if (runsById.TryGetValue(id, out LocalReplay? attempt))
                select(attempt, openDetails: true);
        };
        section.Add(new Container { RelativeSizeAxes = Axes.X, Height = 124, Child = chart });

        string change = map.AccuracyChange is { } delta
            ? $"  ·  first to latest {(delta >= 0 ? "+" : "−")}{Math.Abs(delta) * 100:0.00}"
            : string.Empty;
        section.Add(StatisticsChartFormat.Truncating(
            $"Avg {StatisticsChartFormat.Value(StatisticsMetric.Accuracy, map.AverageAccuracy * 100)}  ·  best {StatisticsChartFormat.Value(StatisticsMetric.Accuracy, map.BestAccuracy * 100)}{change}",
            12, AimModPalette.Muted).With(t => t.MaxWidth = width));
        return section;
    }

    private Drawable actions(LocalReplay replay)
    {
        var open = new OpenBeatmapButton(() => replay, openBeatmap)
        {
            RelativeSizeAxes = Axes.X,
            Width = 1,
            Height = AimModVisualStyle.ControlHeight,
            BackgroundColour = AimModPalette.Panel,
        };
        if (!replay.HasReplayFile)
        {
            return new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new(6),
                Children = new Drawable[]
                {
                    open,
                    StatisticsChartFormat.Text("No replay file saved for this play.", 12, AimModPalette.Muted),
                },
            };
        }

        var watch = new AimModButton("Watch replay", () => openReplay(replay), primary: true)
        {
            AutoSizeAxes = Axes.None,
            RelativeSizeAxes = Axes.X,
        };
        return new GridContainer
        {
            RelativeSizeAxes = Axes.X,
            Height = AimModVisualStyle.ControlHeight,
            ColumnDimensions = [new Dimension(), new Dimension(GridSizeMode.Absolute, AimModVisualStyle.RelatedSpacing), new Dimension()],
            Content = new[] { new Drawable[] { watch, Empty(), open } },
        };
    }

    private static Drawable tile(string label, string value, (string Text, Colour4 Colour) context) => new Container
    {
        RelativeSizeAxes = Axes.Both,
        Padding = new MarginPadding { Right = 8, Bottom = 8 },
        Child = new Container
        {
            RelativeSizeAxes = Axes.Both,
            Masking = true,
            CornerRadius = AimModVisualStyle.ControlRadius,
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Spacing = new(2),
                    Padding = new MarginPadding { Horizontal = 10, Vertical = 8 },
                    Children = new Drawable[]
                    {
                        StatisticsChartFormat.Text(label, 11, AimModPalette.Muted, "SemiBold"),
                        value == StatisticsChartFormat.Missing
                            ? new StatisticsHintText(value, StatisticsPlayRow.MissingPpTooltip, 20, AimModPalette.Muted, "Bold")
                            : StatisticsChartFormat.Text(value, 20, AimModPalette.Text, "Bold"),
                        StatisticsChartFormat.Truncating(context.Text, 11, context.Colour).With(t => { t.RelativeSizeAxes = Axes.X; }),
                    },
                },
            },
        },
    };

    private static Drawable detailRow(string label, string value) => new Container
    {
        RelativeSizeAxes = Axes.X,
        Height = 20,
        Children = new Drawable[]
        {
            StatisticsChartFormat.Text(label, 12, AimModPalette.Muted),
            StatisticsChartFormat.Text(value, 12, AimModPalette.Text, "SemiBold").With(t => { t.Anchor = Anchor.TopRight; t.Origin = Anchor.TopRight; }),
        },
    };

    private static Drawable separator() => new Box { RelativeSizeAxes = Axes.X, Height = 1, Colour = AimModPalette.Border, Margin = new MarginPadding { Vertical = 4 } };

    private static Drawable[] pills(LocalReplay replay)
    {
        var result = new List<Drawable>
        {
            double.IsFinite(replay.StarRating) ? new AimModDifficultyPill(replay.StarRating) : new AimModPill("Stars unknown"),
        };
        if (replay.Mods.Count == 0)
            result.Add(new AimModPill("No mod"));
        else
            result.AddRange(replay.Mods.Take(4).Select(mod => new AimModPill(mod, AimModPillTone.Accent)));
        if (!replay.Passed)
            result.Add(new AimModPill("Failed"));
        return result.ToArray();
    }
}

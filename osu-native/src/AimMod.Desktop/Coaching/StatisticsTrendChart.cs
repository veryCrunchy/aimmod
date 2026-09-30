using osu.Game.Graphics.Sprites;
using AimMod.Desktop.LocalLibrary;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osuTK;

namespace AimMod.Desktop.Coaching;

/// <summary>
/// Time-proportional chart: faint per-play dots, a rolling average line and the period average, with
/// labelled value and date axes. A histogram on the right shares the value axis, so the spread of results
/// reads next to the trend. Hovering shows the nearest play or bin; clicking a play selects it.
/// </summary>
public sealed partial class StatisticsTrendChart : CompositeDrawable
{
    private const float axis_width = 44;
    private const float axis_height = 22;
    private const float margin_width = 64;
    private const float margin_gap = 14;
    private const float minimum_width_for_distribution = 520;

    private readonly Container plot;
    private readonly StatisticsChartCanvas canvas;
    private readonly Container marginalHost;
    private readonly StatisticsChartCanvas marginal;
    private readonly Container yLabels;
    private readonly Container xLabels;
    private readonly SpriteText emptyText;
    private readonly Box hoverGuide;
    private readonly CircularContainer hoverDot;
    private readonly Container tooltip;
    private readonly SpriteText tooltipValue;
    private readonly TruncatingSpriteText tooltipTitle;
    private readonly SpriteText tooltipDate;
    private readonly bool compact;

    private StatisticsMetricView view = StatisticsMetricView.Empty(StatisticsMetric.Accuracy);
    private Guid? highlighted;
    private Vector2 laidOutSize = new(-1);
    private double minimum;
    private double maximum = 1;
    private long startTicks;
    private long endTicks = 1;
    private Vector2[] positions = [];
    private (float Top, float Bottom, StatisticsHistogramBin Bin)[] marginBins = [];
    private bool showDistribution;

    public event Action<Guid>? PlaySelected;

    /// <summary>Resolves a play for the hover tooltip; optional.</summary>
    public Func<Guid, LocalReplay?>? ResolvePlay { get; set; }

    public StatisticsTrendChart(bool compact = false)
    {
        this.compact = compact;
        RelativeSizeAxes = Axes.Both;
        InternalChildren = new Drawable[]
        {
            yLabels = new Container { RelativeSizeAxes = Axes.Y, Width = axis_width - 8, Padding = new MarginPadding { Bottom = axis_height } },
            xLabels = new Container
            {
                Anchor = Anchor.BottomLeft,
                Origin = Anchor.BottomLeft,
                RelativeSizeAxes = Axes.X,
                Height = axis_height,
                Padding = new MarginPadding { Left = axis_width },
            },
            plot = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Left = axis_width, Bottom = axis_height, Top = 8, Right = 6 },
                Children = new Drawable[]
                {
                    canvas = new StatisticsChartCanvas(),
                    hoverGuide = new Box { RelativeSizeAxes = Axes.Y, Width = 1, Colour = AimModPalette.Text, Alpha = 0 },
                    hoverDot = new CircularContainer
                    {
                        Size = new(10),
                        Origin = Anchor.Centre,
                        Masking = true,
                        Alpha = 0,
                        BorderThickness = 2,
                        BorderColour = AimModPalette.Text,
                        Child = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Cyan },
                    },
                },
            },
            marginalHost = new Container
            {
                Anchor = Anchor.TopRight,
                Origin = Anchor.TopRight,
                RelativeSizeAxes = Axes.Y,
                Width = margin_width,
                Alpha = 0,
                Padding = new MarginPadding { Top = 8, Bottom = axis_height },
                Children = new Drawable[]
                {
                    marginal = new StatisticsChartCanvas(),
                    StatisticsChartFormat.Text("Spread", 11, AimModPalette.Muted).With(label =>
                    {
                        label.Anchor = Anchor.BottomLeft;
                        label.Origin = Anchor.TopLeft;
                        label.Y = 6;
                    }),
                },
            },
            emptyText = StatisticsChartFormat.Text("No plays in this view", 13, AimModPalette.Muted).With(label =>
            {
                label.Anchor = Anchor.Centre;
                label.Origin = Anchor.Centre;
                label.Alpha = 0;
            }),
            tooltip = new Container
            {
                Depth = -10,
                AutoSizeAxes = Axes.Both,
                Alpha = 0,
                Masking = true,
                CornerRadius = Visuals.AimModVisualStyle.ControlRadius,
                BorderThickness = 1,
                BorderColour = AimModPalette.Border,
                Children = new Drawable[]
                {
                    new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas },
                    new FillFlowContainer
                    {
                        AutoSizeAxes = Axes.Both,
                        Direction = FillDirection.Vertical,
                        Padding = new MarginPadding { Horizontal = 10, Vertical = 8 },
                        Spacing = new(3),
                        Children = new Drawable[]
                        {
                            tooltipValue = StatisticsChartFormat.Text(string.Empty, 15, AimModPalette.Text, "Bold"),
                            tooltipTitle = StatisticsChartFormat.Truncating(string.Empty, 12, AimModPalette.Text).With(text => text.MaxWidth = 260),
                            tooltipDate = StatisticsChartFormat.Text(string.Empty, 11, AimModPalette.Muted),
                        },
                    },
                },
            },
        };
    }

    public StatisticsMetricView View => view;

    public int PlottedPointCount => positions.Length;

    public int YAxisLabelCount => yLabels.Count;

    public int XAxisLabelCount => xLabels.Count;

    public int DistributionBarCount => marginal.BarCount;

    public void SetView(StatisticsMetricView next, Guid? highlight = null)
    {
        view = next;
        highlighted = highlight;
        laidOutSize = new(-1);
        hideHover();
    }

    protected override void Update()
    {
        base.Update();
        bool distribution = !compact && DrawWidth >= minimum_width_for_distribution;
        if (distribution != showDistribution)
        {
            showDistribution = distribution;
            plot.Padding = plot.Padding with { Right = distribution ? margin_width + margin_gap : 6 };
            xLabels.Padding = xLabels.Padding with { Right = distribution ? margin_width + margin_gap : 0 };
            marginalHost.Alpha = distribution ? 1 : 0;
            laidOutSize = new(-1);
            return;
        }

        Vector2 size = plot.ChildSize;
        if (size == laidOutSize || size.X <= 1 || size.Y <= 1)
            return;
        laidOutSize = size;
        layout(size);
    }

    private void layout(Vector2 size)
    {
        yLabels.Clear();
        xLabels.Clear();
        IReadOnlyList<CoachingChartPoint> points = view.Plays;
        emptyText.Alpha = points.Count == 0 ? 1 : 0;
        if (points.Count == 0)
        {
            positions = [];
            marginBins = [];
            canvas.Set();
            marginal.Set();
            return;
        }

        double low = points.Min(point => point.Value);
        double high = points.Max(point => point.Value);
        if (view.Metric == StatisticsMetric.Misses)
            low = 0;
        double[] ticks = StatisticsChartFormat.NiceTicks(low, high, compact ? 3 : 4);
        if (view.Metric == StatisticsMetric.Accuracy)
            ticks = ticks.Where(tick => tick <= 100).DefaultIfEmpty(100).ToArray();
        minimum = Math.Min(low, ticks[0]);
        maximum = Math.Max(high, ticks[^1]);
        if (maximum - minimum < 1e-6)
            maximum = minimum + 1;

        startTicks = points[0].PlayedAt.UtcTicks;
        endTicks = Math.Max(startTicks + 1, points[^1].PlayedAt.UtcTicks);

        var grid = new List<StatisticsChartCanvas.Line>();
        foreach (double tick in ticks)
        {
            float y = yFor(tick, size.Y);
            grid.Add(new([new(0, y), new(size.X, y)], 1, AimModPalette.Border.Opacity(0.7f)));
            yLabels.Add(StatisticsChartFormat.Text(StatisticsChartFormat.Axis(view.Metric, tick), 11, AimModPalette.Muted).With(label =>
            {
                label.Anchor = Anchor.TopRight;
                label.Origin = Anchor.CentreRight;
                label.Y = plot.Padding.Top + y;
            }));
        }

        DateTimeOffset start = points[0].PlayedAt;
        DateTimeOffset end = points[^1].PlayedAt;
        foreach ((DateTimeOffset time, string text) in StatisticsChartFormat.TimeTicks(start, end, Math.Max(2, (int)(size.X / 84))))
        {
            float x = xFor(time.UtcTicks, size.X);
            if (x < 18 || x > size.X - 18)
                continue;
            grid.Add(new([new(x, size.Y), new(x, size.Y + 4)], 1, AimModPalette.Border));
            xLabels.Add(StatisticsChartFormat.Text(text, 11, AimModPalette.Muted).With(label =>
            {
                label.Origin = Anchor.TopCentre;
                label.X = x;
                label.Y = 6;
            }));
        }

        positions = points.Select(point => new Vector2(xFor(point.PlayedAt.UtcTicks, size.X), yFor(point.Value, size.Y))).ToArray();
        bool dense = positions.Length > 400;
        float radius = compact ? 3.2f : dense ? 1.6f : 2.6f;
        Colour4 dotColour = AimModPalette.Cyan.Opacity(compact ? 0.75f : dense ? 0.24f : 0.45f);
        var dots = positions.Select(position => new StatisticsChartCanvas.Dot(position, radius, dotColour)).ToList();

        var lines = new List<StatisticsChartCanvas.Line>(grid);
        var bands = new List<StatisticsChartCanvas.Band>();
        if (view.Rolling.Count > 1 && view.RollingWindow > 1)
        {
            foreach ((int first, int last) in segments(view.Rolling))
            {
                Vector2[] line = new Vector2[last - first];
                Vector2[] upper = new Vector2[last - first];
                Vector2[] lower = new Vector2[last - first];
                for (int index = first; index < last; index++)
                {
                    CoachingChartPoint point = view.Rolling[index];
                    float x = xFor(point.PlayedAt.UtcTicks, size.X);
                    double spread = index < view.RollingBand.Count ? view.RollingBand[index] : 0;
                    line[index - first] = new Vector2(x, yFor(point.Value, size.Y));
                    upper[index - first] = new Vector2(x, yFor(Math.Min(maximum, point.Value + spread), size.Y));
                    lower[index - first] = new Vector2(x, yFor(Math.Max(minimum, point.Value - spread), size.Y));
                }

                bands.Add(new(upper, lower, AimModPalette.Cyan.Opacity(0.16f)));
                lines.Add(new(line, 1.6f, AimModPalette.Cyan));
            }
        }
        else if (compact && positions.Length > 1)
        {
            lines.Add(new(positions, 1.5f, AimModPalette.Cyan.Opacity(0.6f)));
        }

        // The reference line's value is named in the chart legend, so no label sits on the plotted data.
        float averageY = yFor(ReferenceValue(view) ?? points.Average(point => point.Value), size.Y);
        lines.Add(new([new(0, averageY), new(size.X, averageY)], 1.2f, AimModPalette.Text.Opacity(0.55f), Dash: 5));

        if (highlighted is { } id)
        {
            int index = indexOf(id);
            if (index >= 0)
            {
                dots.Add(new(positions[index], compact ? 6 : 5.5f, AimModPalette.Text));
                dots.Add(new(positions[index], compact ? 4.2f : 3.8f, AimModPalette.Accent));
            }
        }

        canvas.Set(dots: dots, lines: lines, bands: bands);
        layoutDistribution(size.Y);
    }

    /// <summary>Horizontal bars beside the plot, aligned with the value axis.</summary>
    private void layoutDistribution(float height)
    {
        IReadOnlyList<StatisticsHistogramBin> bins = view.Distribution;
        if (!showDistribution || bins.Count == 0)
        {
            marginBins = [];
            marginal.Set();
            return;
        }

        int peak = Math.Max(1, bins.Max(bin => bin.Count));
        int top = bins.Select((bin, index) => (bin, index)).MaxBy(item => item.bin.Count).index;
        var bars = new List<StatisticsChartCanvas.Bar>();
        var slots = new List<(float, float, StatisticsHistogramBin)>();
        for (int index = 0; index < bins.Count; index++)
        {
            StatisticsHistogramBin bin = bins[index];
            // Discrete bins (such as "3 misses") cover the half-step either side of their value.
            double low = bin.Start == bin.End ? bin.Start - 0.5 : bin.Start;
            double high = bin.Start == bin.End ? bin.End + 0.5 : Math.Min(bin.End, maximum);
            float yTop = Math.Clamp(yFor(Math.Min(high, maximum), height), 0, height);
            float yBottom = Math.Clamp(yFor(Math.Max(low, minimum), height), 0, height);
            if (yBottom - yTop < 1)
                continue;
            slots.Add((yTop, yBottom, bin));
            if (bin.Count == 0)
                continue;
            float gap = Math.Min(2, (yBottom - yTop) * 0.2f);
            float length = Math.Max(2, bin.Count / (float)peak * margin_width);
            bars.Add(new(new osu.Framework.Graphics.Primitives.RectangleF(0, yTop + gap / 2, length, Math.Max(1, yBottom - yTop - gap)),
                index == top ? AimModPalette.Cyan.Opacity(0.85f) : AimModPalette.Cyan.Opacity(0.35f)));
        }

        marginBins = slots.ToArray();
        marginal.Set(bars: bars, lines: [new([new(0, 0), new(0, height)], 1, AimModPalette.Border)]);
    }

    /// <summary>Index ranges of the average line, split where you did not play for a while instead of bridging the gap.</summary>
    private IEnumerable<(int Start, int End)> segments(IReadOnlyList<CoachingChartPoint> points)
    {
        long gap = Math.Max(TimeSpan.FromDays(3).Ticks, (endTicks - startTicks) / 40);
        int start = 0;
        for (int index = 1; index <= points.Count; index++)
        {
            if (index < points.Count && points[index].PlayedAt.UtcTicks - points[index - 1].PlayedAt.UtcTicks <= gap)
                continue;
            // Skip the warm-up at the start of each segment, where the window is still filling.
            int first = Math.Min(index - 1, start + Math.Min(view.RollingWindow / 4, (index - start) / 3));
            if (index - first > 1)
                yield return (first, index);
            start = index;
        }
    }

    /// <summary>The dashed reference line: median PP, otherwise the mean of the plotted plays.</summary>
    public static double? ReferenceValue(StatisticsMetricView view) =>
        view.Metric == StatisticsMetric.Performance && view.Headline is { } median ? median
        : view.Plays.Count == 0 ? null
        : view.Plays.Average(point => point.Value);

    private int indexOf(Guid id)
    {
        for (int index = 0; index < view.Plays.Count; index++)
            if (view.Plays[index].ScoreId == id)
                return index;
        return -1;
    }

    private float xFor(long ticks, float width) => (float)((ticks - startTicks) / (double)(endTicks - startTicks)) * width;

    private float yFor(double value, float height) => (float)(1 - (value - minimum) / (maximum - minimum)) * height;

    protected override bool OnHover(HoverEvent e)
    {
        showHover(plot.ToLocalSpace(e.ScreenSpaceMousePosition) - new Vector2(plot.Padding.Left, plot.Padding.Top));
        return true;
    }

    protected override bool OnMouseMove(MouseMoveEvent e)
    {
        if (showDistribution && marginalHost.ReceivePositionalInputAt(e.ScreenSpaceMousePosition))
            showBin(marginalHost.ToLocalSpace(e.ScreenSpaceMousePosition).Y - marginalHost.Padding.Top);
        else
            showHover(plot.ToLocalSpace(e.ScreenSpaceMousePosition) - new Vector2(plot.Padding.Left, plot.Padding.Top));
        return base.OnMouseMove(e);
    }

    private void showBin(float y)
    {
        var slot = marginBins.FirstOrDefault(item => y >= item.Top && y <= item.Bottom);
        if (slot.Bin is null)
        {
            hideHover();
            return;
        }

        int total = Math.Max(1, view.Distribution.Sum(bin => bin.Count));
        hoverGuide.Alpha = 0;
        hoverDot.Alpha = 0;
        tooltipValue.Text = slot.Bin.Count == 1 ? "1 play" : $"{slot.Bin.Count:N0} plays";
        tooltipTitle.Text = StatisticsChartFormat.BinRange(view.Metric, slot.Bin);
        tooltipTitle.Alpha = 1;
        tooltipDate.Text = $"{slot.Bin.Count * 100.0 / total:0}% of plays in view";
        tooltip.X = Math.Max(4, DrawWidth - margin_width - tooltip.DrawWidth - 12);
        tooltip.Y = Math.Clamp(marginalHost.Padding.Top + (slot.Top + slot.Bottom) / 2 - 30, 2, Math.Max(2, DrawHeight - axis_height - 74));
        tooltip.Alpha = 1;
    }

    protected override void OnHoverLost(HoverLostEvent e)
    {
        hideHover();
        base.OnHoverLost(e);
    }

    protected override bool OnClick(ClickEvent e)
    {
        int index = nearest(plot.ToLocalSpace(e.ScreenSpaceMousePosition) - new Vector2(plot.Padding.Left, plot.Padding.Top));
        if (index < 0)
            return false;
        PlaySelected?.Invoke(view.Plays[index].ScoreId);
        return true;
    }

    /// <summary>Shows the tooltip for the play nearest a horizontal position (0-1). Used by visual checks.</summary>
    public void ShowHoverAt(float fraction)
    {
        if (positions.Length == 0)
            return;
        float x = Math.Clamp(fraction, 0, 1) * laidOutSize.X;
        int index = nearest(new Vector2(x, laidOutSize.Y / 2));
        if (index >= 0)
            showHover(positions[index]);
    }

    private void showHover(Vector2 local)
    {
        int index = nearest(local);
        if (index < 0)
        {
            hideHover();
            return;
        }

        CoachingChartPoint point = view.Plays[index];
        Vector2 position = positions[index];
        hoverGuide.X = position.X;
        hoverDot.Position = position;
        hoverGuide.Alpha = 0.18f;
        hoverDot.Alpha = 1;

        tooltipValue.Text = StatisticsChartFormat.Value(view.Metric, point.Value);
        LocalReplay? play = ResolvePlay?.Invoke(point.ScoreId);
        tooltipTitle.Text = play is null ? string.Empty : $"{play.Title} [{play.Difficulty}]";
        tooltipTitle.Alpha = play is null ? 0 : 1;
        tooltipDate.Text = StatisticsChartFormat.DateTime(point.PlayedAt) + (compact ? string.Empty : "  ·  click to open");
        tooltipTitle.MaxWidth = compact ? 176 : 260;

        float width = Math.Max(120, tooltip.DrawWidth);
        float plotX = plot.Padding.Left + position.X;
        float plotY = plot.Padding.Top + position.Y;
        float x = plotX + 14;
        if (x + width > DrawWidth - 4)
            x = plotX - width - 14;
        tooltip.X = Math.Max(4, x);
        tooltip.Y = Math.Clamp(plotY - 30, 2, Math.Max(2, DrawHeight - axis_height - 74));
        tooltip.Alpha = 1;
    }

    private void hideHover()
    {
        hoverGuide.Alpha = 0;
        hoverDot.Alpha = 0;
        tooltip.Alpha = 0;
    }

    private int nearest(Vector2 local)
    {
        if (positions.Length == 0)
            return -1;
        int low = 0;
        int high = positions.Length - 1;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (positions[middle].X < local.X) low = middle + 1;
            else high = middle;
        }

        int best = -1;
        float bestDistance = float.MaxValue;
        for (int index = Math.Max(0, low - 60); index < Math.Min(positions.Length, low + 60); index++)
        {
            Vector2 delta = positions[index] - local;
            // Favour horizontal proximity: people scan charts along time.
            float distance = delta.X * delta.X * 4 + delta.Y * delta.Y;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = index;
            }
        }

        return best;
    }
}

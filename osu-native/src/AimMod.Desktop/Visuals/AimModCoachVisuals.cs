using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Game.Graphics.Sprites;
using osuTK;

namespace AimMod.Desktop.Visuals;

// Compact, data-driven coaching visuals. Every drawable here plots the player's own measurements.

/// <summary>Typical-to-best accuracy on a shared scale: the filled segment is the room to improve.</summary>
public partial class AimModCoachRangeBar : CompositeDrawable
{
    public const double ScaleMinimum = 0.85;

    public AimModCoachRangeBar(double typical, double best, double minimum = ScaleMinimum, double maximum = 1)
    {
        RelativeSizeAxes = Axes.X;
        Height = 10;
        float position(double value) => (float)Math.Clamp((value - minimum) / (maximum - minimum), 0, 1);
        float low = position(Math.Min(typical, best)), high = position(Math.Max(typical, best));
        InternalChildren =
        [
            new Container
            {
                RelativeSizeAxes = Axes.X, Height = 4, Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft,
                Masking = true, CornerRadius = 2,
                Child = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Border },
            },
            new Box
            {
                RelativePositionAxes = Axes.X, RelativeSizeAxes = Axes.X, X = low, Width = Math.Max(0.004f, high - low), Height = 4,
                Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Colour = AimModPalette.Accent.Opacity(0.5f),
            },
            new Circle
            {
                RelativePositionAxes = Axes.X, X = position(typical), Size = new(8), Anchor = Anchor.CentreLeft, Origin = Anchor.Centre,
                Colour = AimModPalette.Text,
            },
            new Circle
            {
                RelativePositionAxes = Axes.X, X = position(best), Size = new(10), Anchor = Anchor.CentreLeft, Origin = Anchor.Centre,
                Colour = AimModPalette.Accent,
            },
        ];
    }
}

/// <summary>Short issue label with the colour used for the same issue in timelines and profile bars.</summary>
public partial class AimModCoachIssueChip : CompositeDrawable
{
    public AimModCoachIssueChip(string text, Colour4 colour)
    {
        AutoSizeAxes = Axes.Both;
        InternalChild = new CircularContainer
        {
            AutoSizeAxes = Axes.Both,
            Masking = true,
            Children =
            [
                new Box { RelativeSizeAxes = Axes.Both, Colour = colour.Opacity(0.16f) },
                new FillFlowContainer
                {
                    AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(6),
                    Padding = new MarginPadding { Horizontal = 10, Vertical = 4 },
                    Children =
                    [
                        new Circle { Size = new(7), Colour = colour, Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft },
                        new OsuSpriteText
                        {
                            Text = text, Font = new FontUsage(size: 12, weight: "SemiBold"), Colour = AimModPalette.Text,
                            Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft,
                        },
                    ],
                },
            ],
        };
    }
}

/// <summary>A small colour key entry: dot and label.</summary>
public partial class AimModCoachLegendItem : FillFlowContainer
{
    public AimModCoachLegendItem(string text, Colour4 colour)
    {
        AutoSizeAxes = Axes.Both;
        Direction = FillDirection.Horizontal;
        Spacing = new(5);
        Children =
        [
            new Circle { Size = new(8), Colour = colour, Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft },
            new OsuSpriteText { Text = text, Font = AimModVisualStyle.CaptionFont, Colour = AimModPalette.Muted, Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft },
        ];
    }
}

public readonly record struct AimModCoachTimelineMark(double TimeMs, int Series);

public readonly record struct AimModCoachTimelineBand(double StartMs, double EndMs, Colour4 Colour, string Label);

/// <summary>
/// Full map length with stacked problems per play in each time slice, shaded practice sections and a hover tooltip.
/// Clicking a slice opens that moment.
/// </summary>
public partial class AimModCoachTimeline : CompositeDrawable
{
    private const float chart_height = 64, gutter = 40, top = 18;
    private readonly double lengthMs;
    private readonly AimModCoachTimelineMark[] marks;
    private readonly AimModCoachTimelineBand[] bands;
    private readonly Colour4[] colours;
    private readonly string[] names;
    private readonly int plays;
    private readonly Action<double>? select;
    private readonly Container chart;
    private readonly Container bins;
    private readonly Container bandLayer;
    private readonly OsuSpriteText maximumLabel;
    private readonly Container tooltip;
    private readonly OsuSpriteText tooltipText;
    private float renderedWidth = -1;

    private readonly double startMs;

    /// <param name="lengthMs">End of the plotted range.</param>
    /// <param name="startMs">Start of the plotted range; a later start zooms into one section of the map.</param>
    public AimModCoachTimeline(double lengthMs, IReadOnlyList<AimModCoachTimelineMark> marks, IReadOnlyList<Colour4> colours,
        IReadOnlyList<string> names, int plays, IReadOnlyList<AimModCoachTimelineBand>? bands, Action<double>? select, double startMs = 0)
    {
        this.startMs = Math.Max(0, startMs);
        this.lengthMs = Math.Max(1_000, lengthMs - this.startMs);
        this.marks = marks.ToArray();
        this.colours = colours.ToArray();
        this.names = names.ToArray();
        this.plays = Math.Max(1, plays);
        this.bands = bands?.ToArray() ?? [];
        this.select = select;
        RelativeSizeAxes = Axes.X;
        Height = top + chart_height + 22;
        InternalChildren =
        [
            new OsuSpriteText { Text = "misses per play", Font = AimModVisualStyle.CaptionFont, Colour = AimModPalette.Muted },
            maximumLabel = axisLabel(string.Empty, top),
            axisLabel("0", top + chart_height - 13),
            chart = new Container
            {
                RelativeSizeAxes = Axes.X, Height = chart_height, Y = top, Padding = new MarginPadding { Left = gutter },
                Children =
                [
                    bandLayer = new Container { RelativeSizeAxes = Axes.Both },
                    new Box { RelativeSizeAxes = Axes.X, Height = 1, Colour = AimModPalette.Border.Opacity(0.5f) },
                    new Box { RelativeSizeAxes = Axes.X, Height = 1, Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft, Colour = AimModPalette.Border },
                    bins = new Container { RelativeSizeAxes = Axes.Both },
                ],
            },
            new Container
            {
                RelativeSizeAxes = Axes.X, Height = 16, Y = top + chart_height + 6, Padding = new MarginPadding { Left = gutter },
                Children =
                [
                    timeLabel(format(this.startMs), Anchor.TopLeft),
                    timeLabel(format(this.startMs + this.lengthMs / 2), Anchor.TopCentre),
                    timeLabel(format(this.startMs + this.lengthMs), Anchor.TopRight),
                ],
            },
            tooltip = new Container
            {
                AutoSizeAxes = Axes.Both, Masking = true, CornerRadius = AimModVisualStyle.ControlRadius, Alpha = 0,
                BorderThickness = 1, BorderColour = AimModPalette.Border, Origin = Anchor.BottomCentre,
                Children =
                [
                    new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
                    tooltipText = new OsuSpriteText
                    {
                        Font = AimModVisualStyle.CaptionStrongFont, Colour = AimModPalette.Text, Margin = new MarginPadding { Horizontal = 8, Vertical = 5 },
                    },
                ],
            },
        ];
    }

    private static OsuSpriteText axisLabel(string text, float y) => new()
    {
        Text = text, Y = y, Width = gutter - 8, Font = AimModVisualStyle.CaptionFont, Colour = AimModPalette.Muted,
    };

    private static OsuSpriteText timeLabel(string text, Anchor anchor) => new()
    {
        Text = text, Anchor = anchor, Origin = anchor, Font = AimModVisualStyle.CaptionFont, Colour = AimModPalette.Muted,
    };

    internal static string format(double ms) => TimeSpan.FromMilliseconds(Math.Max(0, ms)).ToString(@"m\:ss");

    protected override void Update()
    {
        base.Update();
        float width = DrawWidth - gutter;
        if (Math.Abs(width - renderedWidth) < 2 || width < 40) return;
        renderedWidth = width;
        bins.Clear();
        bandLayer.Clear();
        foreach (var band in bands)
        {
            double from = Math.Max(band.StartMs, startMs) - startMs, to = Math.Min(band.EndMs, startMs + lengthMs) - startMs;
            if (to <= 0 || from >= lengthMs) continue;
            float bandWidth = Math.Max(12, (float)((to - from) / lengthMs * width));
            float x = Math.Clamp((float)(from / lengthMs * width) - (bandWidth > (to - from) / lengthMs * width ? 4 : 0), 0, width - bandWidth);
            bandLayer.Add(new Container
            {
                X = x, Width = bandWidth, RelativeSizeAxes = Axes.Y, Masking = true, CornerRadius = 2,
                Children =
                [
                    new Box { RelativeSizeAxes = Axes.Both, Colour = band.Colour.Opacity(0.28f) },
                    new Box { RelativeSizeAxes = Axes.X, Height = 3, Colour = band.Colour },
                ],
            });
        }

        int count = Math.Clamp((int)(width / 7), 12, 160);
        double slice = lengthMs / count;
        var counts = new int[count, colours.Length];
        foreach (var mark in marks)
        {
            double at = mark.TimeMs - startMs;
            if (mark.Series < 0 || mark.Series >= colours.Length || at < 0 || at > lengthMs) continue;
            counts[Math.Clamp((int)(at / slice), 0, count - 1), mark.Series]++;
        }
        int maximum = 1;
        for (int i = 0; i < count; i++)
        {
            int total = 0;
            for (int s = 0; s < colours.Length; s++) total += counts[i, s];
            maximum = Math.Max(maximum, total);
        }
        // A round axis maximum keeps bar heights honest and readable against the label.
        double perPlayMaximum = niceCeiling((double)maximum / plays);
        maximumLabel.Text = perPlayMaximum >= 1 ? $"{perPlayMaximum:0.#}" : $"{perPlayMaximum:0.0#}";
        float binWidth = width / count;
        for (int i = 0; i < count; i++)
        {
            var values = Enumerable.Range(0, colours.Length).Select(s => counts[i, s]).ToArray();
            if (values.Sum() == 0) continue;
            double start = startMs + i * slice;
            bins.Add(new TimelineBin(values, perPlayMaximum * plays, colours, start, select, showTooltip(values, start, start + slice))
            {
                X = i * binWidth, Width = Math.Max(2, binWidth - 1.5f), RelativeSizeAxes = Axes.Y,
            });
        }
    }

    private static double niceCeiling(double value)
    {
        foreach (double step in new[] { 0.05, 0.1, 0.2, 0.25, 0.5, 1, 2, 2.5, 5, 10, 20 })
            if (value <= step) return step;
        return Math.Ceiling(value / 10) * 10;
    }

    private Action<bool, float> showTooltip(int[] values, double start, double end) => (visible, x) =>
    {
        if (!visible) { tooltip.FadeOut(100); return; }
        tooltipText.Text = $"{format(start)}–{format(end)}  ·  " + string.Join("  ·  ", values.Select((v, s) => (v, s)).Where(p => p.v > 0)
            .Select(p => $"{(double)p.v / plays:0.##} {names.ElementAtOrDefault(p.s) ?? string.Empty} per play".Trim()));
        float half = Math.Max(60, tooltip.DrawWidth / 2);
        tooltip.Position = new(Math.Clamp(gutter + x, half, DrawWidth - half), top - 2);
        tooltip.FadeIn(60);
    };

    private partial class TimelineBin : ClickableContainer
    {
        private readonly Action<bool, float> hover;
        private readonly Box highlight;

        public TimelineBin(int[] values, double maximum, Colour4[] colours, double startMs, Action<double>? select, Action<bool, float> hover)
        {
            this.hover = hover;
            int total = values.Sum();
            Action = select is null ? null : () => select(startMs);
            var stack = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.Both, Direction = FillDirection.Vertical, Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft,
                Height = (float)Math.Clamp(total / maximum, 0.05, 1),
            };
            for (int s = values.Length - 1; s >= 0; s--)
                if (values[s] > 0)
                    stack.Add(new Box { RelativeSizeAxes = Axes.Both, Height = (float)values[s] / total, Colour = colours[s] });
            Children =
            [
                highlight = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Text, Alpha = 0 },
                new Container { RelativeSizeAxes = Axes.Both, Masking = true, CornerRadius = 1.5f, Child = stack },
            ];
        }

        protected override bool OnHover(HoverEvent e)
        {
            highlight.FadeTo(0.12f, 60);
            hover(true, X + DrawWidth / 2);
            return true;
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            highlight.FadeOut(120);
            hover(false, 0);
            base.OnHoverLost(e);
        }
    }
}

// <summary>Per-note tap offset: bars above the line are early, below are late.</summary>
public partial class AimModCoachTapDrift : CompositeDrawable
{
    public AimModCoachTapDrift(IReadOnlyList<double> offsetsMs, Colour4 colour)
    {
        Size = new(128, 52);
        double scale = Math.Max(40, offsetsMs.Where(double.IsFinite).Select(Math.Abs).DefaultIfEmpty(0).Max());
        var children = new List<Drawable>
        {
            new Box { RelativeSizeAxes = Axes.X, Height = 1, Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Colour = AimModPalette.Muted.Opacity(0.5f) },
            caption("early", Anchor.TopRight),
            caption("late", Anchor.BottomRight),
        };
        int count = offsetsMs.Count;
        float slot = 92f / Math.Max(1, count);
        for (int i = 0; i < count; i++)
        {
            double value = double.IsFinite(offsetsMs[i]) ? offsetsMs[i] : 0;
            float height = (float)(Math.Abs(value) / scale * 24);
            children.Add(new Box
            {
                X = i * slot + 2, Width = Math.Max(3, slot - 3), Height = Math.Max(1.5f, height),
                Anchor = Anchor.CentreLeft, Origin = value < 0 ? Anchor.BottomLeft : Anchor.TopLeft,
                Colour = colour.Opacity(0.35f + 0.65f * i / Math.Max(1, count - 1)),
            });
        }
        InternalChildren = children.ToArray();
    }

    private static OsuSpriteText caption(string text, Anchor anchor) => new()
    {
        Text = text, Anchor = anchor, Origin = anchor, Font = AimModVisualStyle.CaptionFont, Colour = AimModPalette.Muted,
    };
}

/// <summary>Where presses landed relative to the circle (unit radius), for aim misses.</summary>
public partial class AimModCoachPressScatter : CompositeDrawable
{
    public AimModCoachPressScatter(IReadOnlyList<(float X, float Y)> points, Colour4 colour)
    {
        Size = new(60);
        const float radius = 13; // circle radius in pixels; the frame spans a little over two radii each way
        var children = new List<Drawable>
        {
            new CircularContainer
            {
                Size = new(radius * 2), Anchor = Anchor.Centre, Origin = Anchor.Centre, Masking = true,
                BorderThickness = 2, BorderColour = AimModPalette.Muted,
                Child = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
            },
            new Circle { Size = new(4), Anchor = Anchor.Centre, Origin = Anchor.Centre, Colour = AimModPalette.Muted },
        };
        foreach (var (x, y) in points)
        {
            var offset = new Vector2(x, y) * radius;
            if (offset.Length > 28) offset = offset.Normalized() * 28;
            children.Add(new Circle { Size = new(6), Anchor = Anchor.Centre, Origin = Anchor.Centre, Position = offset, Colour = colour.Opacity(0.85f) });
        }
        InternalChildren = children.ToArray();
    }
}

/// <summary>Press offsets on an early-to-late axis around the centre of the timing window.</summary>
public partial class AimModCoachTimingAxis : CompositeDrawable
{
    public AimModCoachTimingAxis(IReadOnlyList<double> offsetsMs, Colour4 colour, double rangeMs = 200)
    {
        Size = new(128, 52);
        var children = new List<Drawable>
        {
            new Box { RelativeSizeAxes = Axes.X, Height = 2, Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Colour = AimModPalette.Border },
            new Box { RelativeSizeAxes = Axes.X, Width = 0.3f, Height = 10, Anchor = Anchor.Centre, Origin = Anchor.Centre, Colour = AimModPalette.Accent.Opacity(0.3f) },
            new Box { Width = 2, Height = 14, Anchor = Anchor.Centre, Origin = Anchor.Centre, Colour = AimModPalette.Accent },
            caption("early", Anchor.BottomLeft),
            caption("late", Anchor.BottomRight),
        };
        foreach (double offset in offsetsMs.Where(double.IsFinite))
            children.Add(new Circle
            {
                RelativePositionAxes = Axes.X, X = (float)Math.Clamp(offset / rangeMs / 2, -0.48, 0.48), Size = new(7),
                Anchor = Anchor.Centre, Origin = Anchor.Centre, Colour = colour.Opacity(0.8f),
            });
        InternalChildren = children.ToArray();
    }

    private static OsuSpriteText caption(string text, Anchor anchor) => new()
    {
        Text = text, Anchor = anchor, Origin = anchor, Font = AimModVisualStyle.CaptionFont, Colour = AimModPalette.Muted,
    };
}

/// <summary>A labelled share bar with an optional change marker; lower is better for problem counts.</summary>
public partial class AimModCoachSkillBar : CompositeDrawable
{
    public AimModCoachSkillBar(string title, string value, float fraction, Colour4 colour, double? change = null)
    {
        RelativeSizeAxes = Axes.X;
        Height = 36;
        var header = new FillFlowContainer
        {
            AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(6), Anchor = Anchor.TopRight, Origin = Anchor.TopRight,
            Child = new OsuSpriteText { Text = value, Font = AimModVisualStyle.CaptionStrongFont, Colour = AimModPalette.Text },
        };
        if (change is { } delta && Math.Abs(delta) >= 0.05)
        {
            bool better = delta < 0;
            header.Add(new FillFlowContainer
            {
                AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(2),
                Children =
                [
                    new SpriteIcon
                    {
                        Icon = better ? FontAwesome.Solid.ArrowDown : FontAwesome.Solid.ArrowUp, Size = new(9),
                        Colour = better ? AimModPalette.Success : AimModPalette.Yellow, Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft,
                    },
                    new OsuSpriteText
                    {
                        Text = $"{Math.Abs(delta) * 100:0}%", Font = AimModVisualStyle.CaptionStrongFont,
                        Colour = better ? AimModPalette.Success : AimModPalette.Yellow,
                    },
                ],
            });
        }
        InternalChildren =
        [
            new OsuSpriteText { Text = title, Font = AimModVisualStyle.BodyFont, Colour = AimModPalette.Text },
            header,
            new Container
            {
                RelativeSizeAxes = Axes.X, Height = 6, Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft, Y = -4,
                Masking = true, CornerRadius = 3,
                Children =
                [
                    new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Border },
                    new Box { RelativeSizeAxes = Axes.Both, Width = Math.Clamp(fraction, 0.01f, 1), Colour = colour },
                ],
            },
        ];
    }
}

/// <summary>Average tap offset as a dot inside its spread band, on a symmetric early/late axis.</summary>
public partial class AimModCoachOffsetGauge : CompositeDrawable
{
    public AimModCoachOffsetGauge(double meanMs, double spreadMs, double rangeMs = 40)
    {
        RelativeSizeAxes = Axes.X;
        Height = 30;
        float at(double value) => (float)Math.Clamp(value / rangeMs / 2, -0.5, 0.5);
        InternalChildren =
        [
            new Box { RelativeSizeAxes = Axes.X, Height = 2, Y = 8, Anchor = Anchor.TopCentre, Origin = Anchor.Centre, Colour = AimModPalette.Border },
            new Box
            {
                RelativeSizeAxes = Axes.X, RelativePositionAxes = Axes.X, X = at(meanMs), Width = Math.Clamp((float)(spreadMs / rangeMs), 0.02f, 1), Height = 8, Y = 8,
                Anchor = Anchor.TopCentre, Origin = Anchor.Centre, Colour = AimModPalette.Cyan.Opacity(0.3f),
            },
            new Box { Width = 2, Height = 12, Y = 8, Anchor = Anchor.TopCentre, Origin = Anchor.Centre, Colour = AimModPalette.Muted },
            new Circle
            {
                RelativePositionAxes = Axes.X, X = at(meanMs), Size = new(9), Y = 8, Anchor = Anchor.TopCentre, Origin = Anchor.Centre, Colour = AimModPalette.Cyan,
            },
            new OsuSpriteText { Text = "early", Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft, Font = AimModVisualStyle.CaptionFont, Colour = AimModPalette.Muted },
            new OsuSpriteText { Text = "on beat", Anchor = Anchor.BottomCentre, Origin = Anchor.BottomCentre, Font = AimModVisualStyle.CaptionFont, Colour = AimModPalette.Muted },
            new OsuSpriteText { Text = "late", Anchor = Anchor.BottomRight, Origin = Anchor.BottomRight, Font = AimModVisualStyle.CaptionFont, Colour = AimModPalette.Muted },
        ];
    }
}

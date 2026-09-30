using osu.Game.Graphics.Sprites;
using AimMod.Desktop.Visuals;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using osuTK;

namespace AimMod.Desktop.Coaching;

/// <summary>Joined buttons for a short list of exclusive choices, such as the period or chart metric.</summary>
public sealed partial class StatisticsSegmentedControl<T> : CompositeDrawable
    where T : struct, Enum
{
    private readonly Bindable<T> current = new();
    private readonly List<(T Value, Segment Segment)> segments = [];

    public Bindable<T> Current => current;

    public StatisticsSegmentedControl(Bindable<T> source, IEnumerable<(T Value, string Label)> items, float height = AimModVisualStyle.ControlHeight)
    {
        current.BindTo(source);
        AutoSizeAxes = Axes.X;
        Height = height;
        Masking = true;
        CornerRadius = AimModVisualStyle.ControlRadius;
        BorderThickness = 1;
        BorderColour = AimModPalette.Border;
        var flow = new FillFlowContainer { AutoSizeAxes = Axes.X, RelativeSizeAxes = Axes.Y, Direction = FillDirection.Horizontal };
        InternalChildren = [new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel }, flow];
        foreach ((T value, string label) in items)
        {
            T option = value;
            var segment = new Segment(label, () => current.Value = option);
            segments.Add((option, segment));
            flow.Add(segment);
        }

        current.BindValueChanged(change =>
        {
            foreach ((T value, Segment segment) in segments)
                segment.SetSelected(EqualityComparer<T>.Default.Equals(value, change.NewValue));
        }, true);
    }

    public IReadOnlyList<string> Labels => segments.Select(segment => segment.Segment.Label).ToArray();

    private sealed partial class Segment : ClickableContainer
    {
        private readonly Box background;
        private readonly SpriteText text;
        private bool selected;

        public string Label { get; }

        public Segment(string label, Action action)
        {
            Label = label;
            Action = action;
            AutoSizeAxes = Axes.X;
            RelativeSizeAxes = Axes.Y;
            Children =
            [
                background = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
                new Container
                {
                    AutoSizeAxes = Axes.X,
                    RelativeSizeAxes = Axes.Y,
                    Padding = new MarginPadding { Horizontal = 12 },
                    Child = text = StatisticsChartFormat.Text(label, 13, AimModPalette.Muted, "SemiBold").With(t =>
                    {
                        t.Anchor = Anchor.CentreLeft;
                        t.Origin = Anchor.CentreLeft;
                    }),
                },
            ];
        }

        public void SetSelected(bool value)
        {
            selected = value;
            background.FadeColour(selected ? AimModPalette.AccentMuted : IsHovered ? AimModPalette.PanelHover : AimModPalette.Panel, AimModVisualStyle.FastTransition);
            text.FadeColour(selected ? AimModPalette.Accent : IsHovered ? AimModPalette.Text : AimModPalette.Muted, AimModVisualStyle.FastTransition);
        }

        protected override bool OnHover(HoverEvent e)
        {
            SetSelected(selected);
            return true;
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            base.OnHoverLost(e);
            SetSelected(selected);
        }
    }
}

/// <summary>One active filter that can be removed with a click.</summary>
public sealed partial class StatisticsFilterChip : ClickableContainer, IHasTooltip
{
    private readonly Box background;

    public string Label { get; }

    public LocalisableString TooltipText => "Remove this filter";

    public StatisticsFilterChip(string label, Action remove)
    {
        Label = label;
        Action = remove;
        AutoSizeAxes = Axes.X;
        Height = 26;
        Masking = true;
        CornerRadius = 13;
        Children =
        [
            background = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.AccentMuted },
            new FillFlowContainer
            {
                AutoSizeAxes = Axes.X,
                RelativeSizeAxes = Axes.Y,
                Direction = FillDirection.Horizontal,
                Spacing = new(6),
                Padding = new MarginPadding { Left = 10, Right = 9 },
                Children =
                [
                    StatisticsChartFormat.Text(label, 12, AimModPalette.Accent, "SemiBold").With(t => { t.Anchor = Anchor.CentreLeft; t.Origin = Anchor.CentreLeft; }),
                    new SpriteIcon { Icon = FontAwesome.Solid.Times, Size = new(9), Colour = AimModPalette.Accent, Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft },
                ],
            },
        ];
    }

    protected override bool OnHover(HoverEvent e)
    {
        background.FadeColour(AimModPalette.PanelHover, AimModVisualStyle.FastTransition);
        return true;
    }

    protected override void OnHoverLost(HoverLostEvent e)
    {
        base.OnHoverLost(e);
        background.FadeColour(AimModPalette.AccentMuted, AimModVisualStyle.FastTransition);
    }
}

/// <summary>A small info glyph whose tooltip carries detail that would otherwise clutter the page.</summary>
public sealed partial class StatisticsInfoIcon : CompositeDrawable, IHasTooltip
{
    public LocalisableString TooltipText { get; set; }

    public StatisticsInfoIcon(string tooltip = "")
    {
        TooltipText = tooltip;
        Size = new(16);
        InternalChild = new SpriteIcon
        {
            RelativeSizeAxes = Axes.Both,
            Icon = FontAwesome.Solid.InfoCircle,
            Colour = AimModPalette.Muted,
        };
    }

    protected override bool OnHover(HoverEvent e)
    {
        InternalChild.FadeColour(AimModPalette.Text, AimModVisualStyle.FastTransition);
        return true;
    }

    protected override void OnHoverLost(HoverLostEvent e)
    {
        base.OnHoverLost(e);
        InternalChild.FadeColour(AimModPalette.Muted, AimModVisualStyle.FastTransition);
    }
}

/// <summary>Text with an explanatory tooltip, used for missing values and relative dates.</summary>
public sealed partial class StatisticsHintText : CompositeDrawable, IHasTooltip
{
    public LocalisableString TooltipText { get; }

    public StatisticsHintText(string text, string tooltip, float size, Colour4 colour, string weight = "Regular")
    {
        TooltipText = tooltip;
        AutoSizeAxes = Axes.Both;
        InternalChild = StatisticsChartFormat.Text(text, size, colour, weight);
    }
}

/// <summary>
/// Headline number for one metric with its change against the comparison window and a small trend line.
/// Clicking the card charts that metric.
/// </summary>
public sealed partial class StatisticsKpiCard : ClickableContainer
{
    private readonly Box background;
    private readonly SpriteText value;
    private readonly SpriteText detail;
    private readonly SpriteIcon changeIcon;
    private readonly SpriteText changeText;
    private readonly TruncatingSpriteText comparison;
    private readonly StatisticsChartCanvas sparkline;
    private readonly Container sparklineHost;
    private IReadOnlyList<double> sparklineValues = [];
    private Vector2 sparklineSize = new(-1);
    private bool selected;

    public StatisticsMetric Metric { get; }

    public string ValueText => value.Text.ToString();

    public string ChangeText => changeText.Text.ToString();

    public StatisticsKpiCard(StatisticsMetric metric, Action select)
    {
        Metric = metric;
        Action = select;
        RelativeSizeAxes = Axes.Both;
        Masking = true;
        CornerRadius = AimModVisualStyle.CardRadius;
        BorderThickness = 1;
        BorderColour = AimModPalette.Border;
        Children =
        [
            background = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
            new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Horizontal = 14, Vertical = 12 },
                Children =
                [
                    StatisticsChartFormat.Text(StatisticsChartFormat.HeadlineName(metric), 12, AimModPalette.Muted, "SemiBold"),
                    detail = StatisticsChartFormat.Text(string.Empty, 11, AimModPalette.Muted).With(t => { t.Anchor = Anchor.TopRight; t.Origin = Anchor.TopRight; }),
                    value = StatisticsChartFormat.Text(StatisticsChartFormat.Missing, 26, AimModPalette.Text, "Bold").With(t => t.Y = 20),
                    new FillFlowContainer
                    {
                        Anchor = Anchor.BottomLeft,
                        Origin = Anchor.BottomLeft,
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Horizontal,
                        Spacing = new(5),
                        Children =
                        [
                            changeIcon = new SpriteIcon { Size = new(10), Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Colour = AimModPalette.Muted },
                            changeText = StatisticsChartFormat.Text(string.Empty, 12, AimModPalette.Muted, "Bold").With(t => { t.Anchor = Anchor.CentreLeft; t.Origin = Anchor.CentreLeft; }),
                            comparison = StatisticsChartFormat.Truncating(string.Empty, 11, AimModPalette.Muted).With(t => { t.Anchor = Anchor.CentreLeft; t.Origin = Anchor.CentreLeft; }),
                        ],
                    },
                    sparklineHost = new Container
                    {
                        Anchor = Anchor.TopRight,
                        Origin = Anchor.TopRight,
                        RelativeSizeAxes = Axes.X,
                        Width = 0.4f,
                        Height = 30,
                        Y = 24,
                        Child = sparkline = new StatisticsChartCanvas(),
                    },
                ],
            },
        ];
    }

    public void Set(StatisticsMetricView view, string detailText)
    {
        value.Text = StatisticsChartFormat.Value(view.Metric, view.Headline);
        detail.Text = detailText;
        sparklineValues = view.Sparkline;
        sparklineSize = new(-1);
        if (view.Change is { } change)
        {
            (Colour4 colour, IconUsage icon) = StatisticsChartFormat.ChangeStyle(view.Metric, change, view.HigherIsBetter);
            changeIcon.Icon = icon;
            changeIcon.Colour = colour;
            changeIcon.Alpha = 1;
            changeText.Text = Math.Abs(change) < StatisticsChartFormat.ChangeThreshold(view.Metric) ? "No change" : StatisticsChartFormat.Change(view.Metric, change);
            changeText.Colour = colour;
            comparison.Text = view.ComparisonLabel;
        }
        else
        {
            changeIcon.Alpha = 0;
            changeText.Text = string.Empty;
            comparison.Text = view.Headline is null ? "No plays in this view" : "Not enough earlier plays to compare";
        }
    }

    public void SetSelected(bool value)
    {
        selected = value;
        BorderColour = selected ? AimModPalette.Accent.Opacity(0.7f) : AimModPalette.Border;
        background.Colour = selected ? AimModPalette.PanelRaised : IsHovered ? AimModPalette.PanelHover : AimModPalette.Panel;
    }

    protected override void Update()
    {
        base.Update();
        comparison.MaxWidth = Math.Max(40, DrawWidth - 28 - changeText.DrawWidth - 22);
        sparklineHost.Alpha = DrawWidth < 190 ? 0 : 1;
        Vector2 size = sparkline.DrawSize;
        if (size == sparklineSize || size.X <= 1)
            return;
        sparklineSize = size;
        if (sparklineValues.Count < 2)
        {
            sparkline.Set();
            return;
        }

        double low = sparklineValues.Min();
        double high = sparklineValues.Max();
        double range = Math.Max(1e-6, high - low);
        Vector2[] points = sparklineValues.Select((item, index) => new Vector2(
            index / (float)(sparklineValues.Count - 1) * (size.X - 4) + 2,
            (float)(1 - (item - low) / range) * (size.Y - 6) + 3)).ToArray();
        sparkline.Set(
            dots: [new(points[^1], 2.6f, AimModPalette.Cyan)],
            lines: [new(points, 1.6f, AimModPalette.Cyan.Opacity(0.85f))]);
    }

    protected override bool OnHover(HoverEvent e)
    {
        if (!selected) background.FadeColour(AimModPalette.PanelHover, AimModVisualStyle.FastTransition);
        return true;
    }

    protected override void OnHoverLost(HoverLostEvent e)
    {
        base.OnHoverLost(e);
        background.FadeColour(selected ? AimModPalette.PanelRaised : AimModPalette.Panel, AimModVisualStyle.FastTransition);
    }
}

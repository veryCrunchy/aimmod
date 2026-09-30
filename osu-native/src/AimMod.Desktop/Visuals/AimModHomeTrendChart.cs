using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Localisation;
using osu.Game.Graphics.Sprites;
using osuTK;

namespace AimMod.Desktop.Visuals;

/// <summary>One day of a trend chart. A null value is a day without a measured result.</summary>
public readonly record struct AimModHomeTrendPoint(DateOnly Day, int Count, double? Value);

/// <summary>
/// Daily accuracy as a line over daily play counts as bars, with a labelled period average, a value axis and dates.
/// Hovering a day shows its exact numbers.
/// </summary>
public partial class AimModHomeTrendChart : CompositeDrawable
{
    private const float axis_width = 44;
    private const float label_width = 72;
    private const float date_height = 18;
    private const float bar_share = .32f;

    private readonly Container plot;
    private readonly Container axis;
    private readonly Container dates;
    private readonly OsuSpriteText emptyText;
    private IReadOnlyList<AimModHomeTrendPoint> points = [];
    private double? average;
    private AimModLayout.ChangeTracker<Vector2> sizeTracker;

    public AimModHomeTrendChart()
    {
        RelativeSizeAxes = Axes.X;
        Height = 150;
        InternalChildren = new Drawable[]
        {
            axis = new Container { RelativeSizeAxes = Axes.Y, Width = axis_width, Padding = new MarginPadding { Bottom = date_height } },
            plot = new Container { RelativeSizeAxes = Axes.Both, Padding = new MarginPadding { Left = axis_width, Right = label_width, Bottom = date_height } },
            dates = new Container
            {
                Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft, RelativeSizeAxes = Axes.X, Height = date_height,
                Padding = new MarginPadding { Left = axis_width, Right = label_width },
            },
            emptyText = new OsuSpriteText
            {
                Anchor = Anchor.Centre, Origin = Anchor.Centre, Font = AimModVisualStyle.BodyFont, Colour = AimModPalette.Muted, Alpha = 0,
                Text = "No finished plays in this period",
            },
        };
    }

    /// <summary>Formats a value for the axis, average label and tooltips.</summary>
    public Func<double, string> FormatValue { get; set; } = value => $"{value:P1}";

    public string CountLabel { get; set; } = "plays";

    public void SetData(IReadOnlyList<AimModHomeTrendPoint> data, double? periodAverage)
    {
        points = data;
        average = periodAverage;
        message = null;
        sizeTracker.Reset();
    }

    /// <summary>Replaces the chart with a short centred message, for loading or missing data.</summary>
    public void ShowMessage(string text)
    {
        message = text;
        sizeTracker.Reset();
    }

    private string? message;

    protected override void Update()
    {
        base.Update();
        if (sizeTracker.Update(DrawSize))
            rebuild();
    }

    /// <summary>Value range padded to whole steps, never narrower than two percentage points.</summary>
    internal static (double Minimum, double Maximum) ValueRange(IEnumerable<double> values, double minimumSpan = .02, double step = .01)
    {
        double[] finite = values.Where(double.IsFinite).ToArray();
        if (finite.Length == 0)
            return (0, 1);
        double low = Math.Floor(finite.Min() / step) * step;
        double high = Math.Ceiling(finite.Max() / step) * step;
        if (high - low < minimumSpan)
        {
            double middle = (high + low) / 2;
            low = middle - minimumSpan / 2;
            high = middle + minimumSpan / 2;
        }
        if (high > 1 && finite.Max() <= 1)
        {
            low -= high - 1;
            high = 1;
        }
        return (Math.Max(0, low), high);
    }

    private void rebuild()
    {
        plot.Clear();
        axis.Clear();
        dates.Clear();
        float width = DrawWidth - axis_width - label_width;
        float height = DrawHeight - date_height;
        double[] values = points.Where(p => p.Value is not null).Select(p => p.Value!.Value).ToArray();
        emptyText.Text = message ?? "No finished plays in this period";
        emptyText.Alpha = message is not null || points.Count > 0 && values.Length == 0 ? 1 : 0;
        if (width <= 0 || height <= 0 || points.Count == 0 || message is not null)
            return;

        (double minimum, double maximum) = ValueRange(values.Append(average ?? double.NaN));
        int maxCount = Math.Max(1, points.Max(p => p.Count));
        float column = width / points.Count;
        float barArea = height * bar_share;
        float lineTop = 8;
        float lineBottom = height - 6;
        float y(double value) => (float)(lineBottom - (value - minimum) / Math.Max(1e-9, maximum - minimum) * (lineBottom - lineTop));

        // Grid lines and value axis at the range ends and middle.
        foreach (double tick in new[] { minimum, (minimum + maximum) / 2, maximum })
        {
            plot.Add(new Box { RelativeSizeAxes = Axes.X, Height = 1, Y = y(tick), Colour = AimModPalette.Border, Alpha = .55f });
            axis.Add(new OsuSpriteText
            {
                Anchor = Anchor.TopRight, Origin = Anchor.CentreRight, X = -8, Y = y(tick),
                Text = FormatValue(tick), Font = AimModVisualStyle.CaptionFont, Colour = AimModPalette.Muted,
            });
        }

        for (int i = 0; i < points.Count; i++)
        {
            AimModHomeTrendPoint point = points[i];
            float barHeight = point.Count == 0 ? 0 : Math.Max(2, barArea * point.Count / maxCount);
            plot.Add(new DayColumn(tooltip(point))
            {
                X = i * column, Width = column, RelativeSizeAxes = Axes.Y,
                Child = new Box
                {
                    Anchor = Anchor.BottomCentre, Origin = Anchor.BottomCentre,
                    Width = Math.Clamp(column * .56f, 2, 22), Height = barHeight, Colour = AimModPalette.CyanDark, Alpha = .45f,
                },
            });
        }

        // Label the busiest day so the bars have a scale.
        int busiest = points.Select((p, i) => (p.Count, i)).MaxBy(p => p.Count).i;
        if (points[busiest].Count > 0)
            plot.Add(new OsuSpriteText
            {
                Anchor = Anchor.BottomLeft, Origin = Anchor.BottomCentre, X = busiest * column + column / 2,
                Y = -Math.Max(2, barArea) - 2, Text = $"{points[busiest].Count}", Font = AimModVisualStyle.CaptionFont, Colour = AimModPalette.Cyan,
            });

        if (average is { } mean && values.Length > 0)
        {
            float averageY = y(mean);
            for (float x = 0; x < width; x += 10)
                plot.Add(new Box { X = x, Y = averageY, Width = Math.Min(5, width - x), Height = 1, Colour = AimModPalette.Muted, Alpha = .9f });
            // The average is labelled beyond the last day, so it never covers a data point.
            plot.Add(new OsuSpriteText
            {
                Anchor = Anchor.TopRight, Origin = Anchor.CentreLeft, Y = averageY, X = 8,
                Text = $"avg {FormatValue(mean)}", Font = AimModVisualStyle.CaptionStrongFont, Colour = AimModPalette.Text,
            });
        }

        Vector2? previous = null;
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i].Value is not { } value)
                continue;
            var current = new Vector2(i * column + column / 2, y(value));
            if (previous is { } start)
                plot.Add(segment(start, current));
            previous = current;
        }
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i].Value is not { } value)
                continue;
            bool better = average is { } mean2 && value >= mean2;
            plot.Add(new Circle
            {
                Origin = Anchor.Centre, Position = new(i * column + column / 2, y(value)), Size = new(values.Length > 20 ? 6 : 8),
                Colour = better ? AimModPalette.Accent : AimModPalette.Yellow,
            });
        }

        // Dates: first, middle and the last day ("Today").
        int[] labelled = points.Count <= 1 ? [0] : [0, points.Count / 2, points.Count - 1];
        foreach (int index in labelled.Distinct())
        {
            bool last = index == points.Count - 1;
            bool first = index == 0;
            dates.Add(new OsuSpriteText
            {
                X = first ? 0 : last ? width : index * column + column / 2,
                Anchor = Anchor.CentreLeft,
                Origin = first ? Anchor.CentreLeft : last ? Anchor.CentreRight : Anchor.Centre,
                Text = last && points[index].Day == DateOnly.FromDateTime(DateTime.Now) ? "Today" : points[index].Day.ToString("d MMM"),
                Font = AimModVisualStyle.CaptionFont, Colour = AimModPalette.Muted,
            });
        }
    }

    private string tooltip(AimModHomeTrendPoint point) =>
        $"{point.Day:ddd d MMM} · {point.Count} {(point.Count == 1 ? CountLabel.TrimEnd('s') : CountLabel)}"
        + (point.Value is { } value ? $" · {FormatValue(value)}" : string.Empty);

    private static Drawable segment(Vector2 start, Vector2 end)
    {
        Vector2 delta = end - start;
        return new Box
        {
            Position = start, Origin = Anchor.CentreLeft, Width = delta.Length, Height = 2,
            Rotation = MathF.Atan2(delta.Y, delta.X) * 180 / MathF.PI, Colour = AimModPalette.Accent, Alpha = .85f,
        };
    }

    private partial class DayColumn : Container, IHasTooltip
    {
        public DayColumn(string text) => TooltipText = text;

        public LocalisableString TooltipText { get; }
    }
}

using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Visuals;

/// <summary>A shaded span of a <see cref="AimModRangeStrip"/>, in the strip's own value units.</summary>
public sealed record AimModStripBand(double From, double To, Colour4 Colour, float Alpha = 1);

/// <summary>A value mark on a <see cref="AimModRangeStrip"/>: a full-height marker, or a thin tick when <paramref name="Tick"/> is set.</summary>
public sealed record AimModStripMarker(double Value, Colour4 Colour, bool Tick = false);

/// <summary>
/// A compact horizontal value strip for ranges, meters and gauges: a neutral track, shaded bands and markers.
/// Values outside the domain are clamped to its ends. Labels belong to the caller, so they can avoid collisions.
/// </summary>
public partial class AimModRangeStrip : CompositeDrawable
{
    public const float DefaultTrackHeight = 8;

    public double Minimum { get; }
    public double Maximum { get; }

    public AimModRangeStrip(double minimum, double maximum, IEnumerable<AimModStripBand>? bands = null,
        IEnumerable<AimModStripMarker>? markers = null, float trackHeight = DefaultTrackHeight)
    {
        Minimum = double.IsFinite(minimum) ? minimum : 0;
        Maximum = double.IsFinite(maximum) && maximum > Minimum ? maximum : Minimum + 1;
        RelativeSizeAxes = Axes.X;
        Height = trackHeight + 6;
        var track = new Container
        {
            Anchor = Anchor.CentreLeft,
            Origin = Anchor.CentreLeft,
            RelativeSizeAxes = Axes.X,
            Height = trackHeight,
            Masking = true,
            CornerRadius = trackHeight / 2,
            Child = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelHover },
        };
        foreach (var band in bands ?? [])
        {
            float from = PositionOf(Math.Min(band.From, band.To)), to = PositionOf(Math.Max(band.From, band.To));
            track.Add(new Box
            {
                RelativeSizeAxes = Axes.Both,
                RelativePositionAxes = Axes.X,
                X = from,
                Width = Math.Max(0.004f, to - from),
                Colour = band.Colour,
                Alpha = band.Alpha,
            });
        }
        var marks = new Container { RelativeSizeAxes = Axes.Both };
        foreach (var marker in markers ?? [])
            marks.Add(new Box
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.Centre,
                RelativePositionAxes = Axes.X,
                X = PositionOf(marker.Value),
                Size = marker.Tick ? new(2, trackHeight + 6) : new(4, trackHeight + 6),
                Colour = marker.Colour,
            });
        InternalChildren = [track, marks];
    }

    /// <summary>Relative horizontal position (0-1) of a value on this strip.</summary>
    public float PositionOf(double value) => (float)Math.Clamp((value - Minimum) / (Maximum - Minimum), 0, 1);
}

/// <summary>
/// A small labelled probability histogram: one column per category with its chance above the bar and its
/// label below. The highlighted column (normally the most likely one) uses the full colour.
/// </summary>
public partial class AimModProbabilityBars : CompositeDrawable
{
    public const float DefaultBarHeight = 30;

    public AimModProbabilityBars(IReadOnlyList<(string Label, double Probability)> columns, Colour4 colour, int highlighted = -1,
        float barHeight = DefaultBarHeight)
    {
        RelativeSizeAxes = Axes.X;
        const float valueRow = 14, labelRow = 15;
        Height = valueRow + barHeight + labelRow;
        double peak = Math.Max(1e-9, columns.Count == 0 ? 1 : columns.Max(c => double.IsFinite(c.Probability) ? c.Probability : 0));
        var content = new Container { RelativeSizeAxes = Axes.Both };
        for (int i = 0; i < columns.Count; i++)
        {
            (string label, double probability) = columns[i];
            probability = double.IsFinite(probability) ? Math.Clamp(probability, 0, 1) : 0;
            bool strong = i == highlighted;
            float fill = (float)(probability / peak);
            content.Add(new Container
            {
                RelativeSizeAxes = Axes.Both,
                RelativePositionAxes = Axes.X,
                Width = 1f / columns.Count,
                X = (float)i / columns.Count,
                Padding = new MarginPadding { Horizontal = 2 },
                Children = new Drawable[]
                {
                    new OsuSpriteText
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Text = probability >= .995 ? ">99%" : probability < .005 ? "<1%" : $"{probability:P0}",
                        Font = new FontUsage(size: AimModVisualStyle.MinReadableFontSize, weight: strong ? "Bold" : "Regular"),
                        Colour = strong ? AimModPalette.Text : AimModPalette.Muted,
                    },
                    new Container
                    {
                        RelativeSizeAxes = Axes.X,
                        Height = barHeight,
                        Y = valueRow,
                        Children = new Drawable[]
                        {
                            new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelHover, Alpha = .45f },
                            new Box
                            {
                                Anchor = Anchor.BottomLeft,
                                Origin = Anchor.BottomLeft,
                                RelativeSizeAxes = Axes.Both,
                                Height = Math.Max(fill, probability > 0 ? .04f : 0),
                                Colour = colour,
                                Alpha = strong ? 1 : .45f,
                            },
                        },
                    },
                    new OsuSpriteText
                    {
                        Anchor = Anchor.BottomCentre,
                        Origin = Anchor.BottomCentre,
                        Text = label,
                        Font = new FontUsage(size: AimModVisualStyle.MinReadableFontSize, weight: strong ? "Bold" : "SemiBold"),
                        Colour = strong ? colour : AimModPalette.Muted,
                    },
                },
            });
        }
        InternalChild = content;
    }
}

/// <summary>A small rounded label chip with an optional leading icon, for badges such as confidence or calibration.</summary>
public partial class AimModChip : CompositeDrawable
{
    public AimModChip(string text, Colour4 colour, IconUsage? icon = null, float backgroundAlpha = .14f)
    {
        AutoSizeAxes = Axes.Both;
        Masking = true;
        CornerRadius = 4;
        var flow = new FillFlowContainer
        {
            AutoSizeAxes = Axes.Both,
            Direction = FillDirection.Horizontal,
            Spacing = new(4, 0),
            Padding = new MarginPadding { Horizontal = 6, Vertical = 2 },
        };
        if (icon is { } glyph)
            flow.Add(new SpriteIcon
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                Icon = glyph,
                Size = new(10),
                Colour = colour,
            });
        flow.Add(new OsuSpriteText
        {
            Anchor = Anchor.CentreLeft,
            Origin = Anchor.CentreLeft,
            Text = text,
            Font = new FontUsage(size: AimModVisualStyle.MinReadableFontSize, weight: "SemiBold"),
            Colour = colour,
        });
        InternalChildren =
        [
            new Box { RelativeSizeAxes = Axes.Both, Colour = colour, Alpha = backgroundAlpha },
            flow,
        ];
    }
}

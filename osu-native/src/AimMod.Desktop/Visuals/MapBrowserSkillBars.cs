using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;

namespace AimMod.Desktop.Visuals;

/// <summary>One skill on a 0–10 scale, optionally with the player's usual level on the same scale.</summary>
public sealed record MapBrowserSkillValue(string Label, double Value, double? Usual = null);

/// <summary>
/// Labelled horizontal bars on a shared 0–10 axis. A thin marker shows the player's usual
/// level, and the right column states the difference so the bar reads without a legend.
/// </summary>
public partial class MapBrowserSkillBars : FillFlowContainer
{
    public const float RowHeight = 22;
    private const float label_width = 72;
    private const float value_width = 92;

    public MapBrowserSkillBars(IReadOnlyList<MapBrowserSkillValue> values)
    {
        RelativeSizeAxes = Axes.X;
        AutoSizeAxes = Axes.Y;
        Direction = FillDirection.Vertical;
        Spacing = new(0, 4);
        foreach (MapBrowserSkillValue value in values)
            Add(row(value));
        Add(scale());
    }

    /// <summary>Wording for the right column, exposed for tests.</summary>
    public static (string Text, Colour4 Colour) Comparison(MapBrowserSkillValue value)
    {
        if (value.Usual is not { } usual)
            return ($"{value.Value:0.0}", AimModPalette.Text);
        double delta = value.Value - usual;
        return delta switch
        {
            >= 1 => ($"{value.Value:0.0} · +{delta:0.0}", AimModPalette.Yellow),
            <= -1 => ($"{value.Value:0.0} · {delta:0.0}", AimModPalette.Muted),
            _ => ($"{value.Value:0.0} · usual", AimModPalette.Accent),
        };
    }

    private static Drawable row(MapBrowserSkillValue value)
    {
        float fill = (float)Math.Clamp(value.Value / 10, 0, 1);
        (string text, Colour4 colour) = Comparison(value);
        var track = new Container
        {
            RelativeSizeAxes = Axes.Both,
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.X, Height = 8, Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Colour = AimModPalette.PanelHover },
                new Box { RelativePositionAxes = Axes.X, X = 0.5f, Width = 1, Height = 12, Anchor = Anchor.CentreLeft, Origin = Anchor.Centre, Colour = AimModPalette.Border },
                new Container
                {
                    RelativeSizeAxes = Axes.X,
                    Width = fill,
                    Height = 8,
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Masking = true,
                    CornerRadius = 2,
                    Child = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Cyan },
                },
            },
        };
        if (value.Usual is { } usual)
        {
            track.Add(new Box
            {
                RelativePositionAxes = Axes.X,
                X = (float)Math.Clamp(usual / 10, 0, 1),
                Width = 2,
                Height = 16,
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.Centre,
                Colour = AimModPalette.Text,
            });
        }

        return new Container
        {
            RelativeSizeAxes = Axes.X,
            Height = RowHeight,
            Children = new Drawable[]
            {
                new SpriteText
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Text = value.Label,
                    Font = AimModVisualStyle.CaptionStrongFont,
                    Colour = AimModPalette.Text,
                },
                new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding { Left = label_width, Right = value_width },
                    Child = track,
                },
                new SpriteText
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    Text = text,
                    Font = new FontUsage(size: 12, weight: "Bold"),
                    Colour = colour,
                },
            },
        };
    }

    private static Drawable scale() => new Container
    {
        RelativeSizeAxes = Axes.X,
        Height = 14,
        Padding = new MarginPadding { Left = label_width, Right = value_width },
        Children = new Drawable[]
        {
            tick("0", 0, Anchor.TopLeft),
            tick("5", 0.5f, Anchor.TopCentre),
            tick("10", 1, Anchor.TopRight),
        },
    };

    private static Drawable tick(string text, float x, Anchor origin) => new SpriteText
    {
        RelativePositionAxes = Axes.X,
        X = x,
        Origin = origin,
        Text = text,
        Font = AimModVisualStyle.CaptionFont,
        Colour = AimModPalette.Muted,
    };
}

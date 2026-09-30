using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;

namespace AimMod.Desktop.Visuals;

/// <summary>
/// A set's difficulties at a glance: one osu!-coloured dot per difficulty in star order,
/// followed by the star range. Replaces rows of cut-off difficulty chips.
/// </summary>
public partial class MapBrowserDifficultySpread : FillFlowContainer
{
    public const int MaximumDots = 12;

    public MapBrowserDifficultySpread(IReadOnlyCollection<double> starRatings, bool showRange = true)
    {
        AutoSizeAxes = Axes.Both;
        Direction = FillDirection.Horizontal;
        Spacing = new(3, 0);
        double[] ordered = starRatings.Where(double.IsFinite).Order().ToArray();
        foreach (double stars in ordered.Take(MaximumDots))
        {
            Add(Dot(stars));
        }
        if (ordered.Length > MaximumDots)
            Add(text($"+{ordered.Length - MaximumDots}", AimModPalette.Muted, 3));
        if (!showRange || ordered.Length == 0)
            return;
        Add(new SpriteIcon
        {
            Anchor = Anchor.CentreLeft,
            Origin = Anchor.CentreLeft,
            Icon = FontAwesome.Solid.Star,
            Size = new(9),
            Margin = new MarginPadding { Left = 7 },
            Colour = AimModPalette.Muted,
        });
        Add(text(MapBrowserFormat.StarRange(ordered), AimModPalette.Text, 3));
    }

    /// <summary>A difficulty-coloured dot with a faint ring so osu!'s darkest colours stay visible.</summary>
    public static Drawable Dot(double stars, float size = 8) => new CircularContainer
    {
        Anchor = Anchor.CentreLeft,
        Origin = Anchor.CentreLeft,
        Size = new(size),
        Masking = true,
        BorderThickness = 1.5f,
        BorderColour = Colour4.White.Opacity(0.22f),
        Child = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModVisualStyle.DifficultyColour(stars) },
    };

    private static SpriteText text(string value, Colour4 colour, float left) => new()
    {
        Anchor = Anchor.CentreLeft,
        Origin = Anchor.CentreLeft,
        Text = value,
        Margin = new MarginPadding { Left = left },
        Font = AimModVisualStyle.CaptionStrongFont,
        Colour = colour,
    };
}

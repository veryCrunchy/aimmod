using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Visuals;

/// <summary>Whether a change is an improvement, a regression or too small to matter.</summary>
public enum AimModHomeTrend
{
    None,
    Better,
    Worse,
    Flat,
    /// <summary>A real change that is neither good nor bad, such as the number of plays.</summary>
    Neutral,
}

/// <summary>
/// A key number with its context: a short label, the value, and a coloured change against a named reference
/// ("▲ 0.8% vs 96.1% previous 7 days"). Missing values show a dash rather than zero.
/// </summary>
public partial class AimModHomeMetric : CompositeDrawable
{
    private readonly OsuSpriteText label;
    private readonly TruncatingSpriteText value;
    private readonly OsuSpriteText unit;
    private readonly SpriteIcon trendIcon;
    private readonly OsuSpriteText trendText;
    private readonly TruncatingSpriteText context;
    private readonly FillFlowContainer trendRow;

    public AimModHomeMetric(string title, float valueSize = 24)
    {
        // Width is set by the owning layout; the tile grows to fit its text vertically.
        Width = 160;
        AutoSizeAxes = Axes.Y;
        InternalChild = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new(4),
            Children = new Drawable[]
            {
                label = new OsuSpriteText { Text = title, Font = AimModVisualStyle.CaptionStrongFont, Colour = AimModPalette.Muted },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Horizontal,
                    Spacing = new(4),
                    Children = new Drawable[]
                    {
                        value = new TruncatingSpriteText { Font = new FontUsage(size: valueSize, weight: "SemiBold"), Colour = AimModPalette.Text },
                        unit = new OsuSpriteText
                        {
                            Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft, Margin = new MarginPadding { Bottom = valueSize * .12f },
                            Font = new FontUsage(size: 13, weight: "SemiBold"), Colour = AimModPalette.Muted,
                        },
                    },
                },
                // The comparison wraps below the change when the tile is narrow, instead of being cut off.
                trendRow = new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Full,
                    Spacing = new(5, 2),
                    Children = new Drawable[]
                    {
                        trendIcon = new SpriteIcon { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Size = new(9) },
                        trendText = new OsuSpriteText { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Font = AimModVisualStyle.CaptionStrongFont },
                        context = new TruncatingSpriteText { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Font = AimModVisualStyle.CaptionFont, Colour = AimModPalette.Muted },
                    },
                },
            },
        };
        SetValue(null);
    }

    public string Title { get => label.Text.ToString(); set => label.Text = value; }

    /// <summary>Sets the headline. Null shows a dash, so a missing value never reads as zero.</summary>
    public void SetValue(string? text, string? unitText = null)
    {
        value.Text = string.IsNullOrWhiteSpace(text) ? "—" : text;
        value.Colour = string.IsNullOrWhiteSpace(text) ? AimModPalette.Muted : AimModPalette.Text;
        unit.Text = unitText ?? string.Empty;
    }

    /// <summary>Shows the change and what it is compared with. <paramref name="change"/> may be empty for context only.</summary>
    public void SetTrend(AimModHomeTrend trend, double direction, string change, string comparison)
    {
        (IconUsage icon, Colour4 colour) = TrendStyle(trend, direction);
        trendIcon.Icon = icon;
        trendIcon.Colour = trendText.Colour = colour;
        trendIcon.Alpha = trend is AimModHomeTrend.None || string.IsNullOrEmpty(change) ? 0 : 1;
        trendText.Text = change;
        context.Text = comparison;
    }

    /// <summary>The arrow follows the value's direction; the colour says whether that direction is good.</summary>
    public static (IconUsage Icon, Colour4 Colour) TrendStyle(AimModHomeTrend trend, double direction)
    {
        IconUsage icon = trend is AimModHomeTrend.Flat or AimModHomeTrend.None || direction == 0
            ? FontAwesome.Solid.Minus
            : direction > 0 ? FontAwesome.Solid.ArrowUp : FontAwesome.Solid.ArrowDown;
        Colour4 colour = trend switch
        {
            AimModHomeTrend.Better => AimModPalette.Accent,
            AimModHomeTrend.Worse => AimModPalette.Yellow,
            _ => AimModPalette.Muted,
        };
        return (icon, colour);
    }

    /// <summary>
    /// Classifies a change. <paramref name="higherIsBetter"/> flips the arrow meaning for misses and ranks;
    /// the arrow always follows the direction of the value, while the colour says whether that is good.
    /// </summary>
    public static AimModHomeTrend Classify(double? current, double? previous, double threshold, bool higherIsBetter = true)
    {
        if (current is not { } now || previous is not { } before || !double.IsFinite(now) || !double.IsFinite(before))
            return AimModHomeTrend.None;
        double delta = now - before;
        if (Math.Abs(delta) < threshold)
            return AimModHomeTrend.Flat;
        return delta > 0 == higherIsBetter ? AimModHomeTrend.Better : AimModHomeTrend.Worse;
    }

    protected override void Update()
    {
        base.Update();
        float width = DrawWidth;
        value.MaxWidth = Math.Max(20, width - unit.DrawWidth - 4);
        context.MaxWidth = Math.Max(0, width);
        trendRow.Alpha = string.IsNullOrEmpty(trendText.Text.ToString()) && string.IsNullOrEmpty(context.Text.ToString()) ? 0 : 1;
    }
}

using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Visuals;

/// <summary>A planned value with a small bar showing where it sits in its practical range.</summary>
public partial class AimModTrainerGauge : CompositeDrawable, IHasTooltip
{
    private readonly OsuSpriteText value;
    private readonly Box fill, usualMark;
    public LocalisableString TooltipText { get; set; }

    /// <param name="usual">Where the player's recent runs usually sit on the same scale, shown as a tick.</param>
    public AimModTrainerGauge(string label, string value, double fraction, string? tooltip = null, double? usual = null)
    {
        Height = 54; Masking = true; CornerRadius = AimModVisualStyle.ControlRadius;
        TooltipText = tooltip ?? string.Empty;
        InternalChildren = [
            new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.PanelRaised },
            new OsuSpriteText { X = 10, Y = 7, Text = label, Font = AimModVisualStyle.CaptionFont, Colour = AimModPalette.Muted },
            this.value = new OsuSpriteText { X = 10, Y = 21, Font = new FontUsage(size: 17, weight: "SemiBold"), Colour = AimModPalette.Text },
            new Container { RelativeSizeAxes = Axes.X, Height = 3, Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft,
                Padding = new MarginPadding { Horizontal = 10 }, Y = -7, Children = [
                    new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Border },
                    fill = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Accent },
                    usualMark = new Box { RelativePositionAxes = Axes.X, Origin = Anchor.Centre, Anchor = Anchor.CentreLeft, Width = 2, Height = 9,
                        Colour = AimModPalette.Text, Alpha = 0 },
                ] },
        ];
        Set(value, fraction, usual);
    }

    public void Set(string text, double fraction, double? usual = null)
    {
        value.Text = text;
        fill.Width = (float)Math.Clamp(double.IsFinite(fraction) ? fraction : 0, 0.03, 1);
        usualMark.Alpha = usual is { } u && double.IsFinite(u) ? 1 : 0;
        if (usual is { } at && double.IsFinite(at)) usualMark.X = (float)Math.Clamp(at, 0, 1);
    }
}

public enum AimModTrainerChipTone { Neutral, Accent, Info }

/// <summary>A compact fact such as a key binding, offset or object type. Not interactive.</summary>
public partial class AimModTrainerChip : CompositeDrawable, IHasTooltip
{
    public LocalisableString TooltipText { get; set; }

    public AimModTrainerChip(string text, IconUsage? icon = null, AimModTrainerChipTone tone = AimModTrainerChipTone.Neutral, string? tooltip = null)
    {
        AutoSizeAxes = Axes.Both; Masking = true; CornerRadius = AimModVisualStyle.ControlRadius;
        TooltipText = tooltip ?? string.Empty;
        var (background, foreground) = tone switch
        {
            AimModTrainerChipTone.Accent => (AimModPalette.AccentMuted, AimModPalette.Accent),
            AimModTrainerChipTone.Info => (AimModPalette.PanelRaised, AimModPalette.Cyan),
            _ => (AimModPalette.PanelRaised, AimModPalette.Text),
        };
        var row = new FillFlowContainer<Drawable> { AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(6),
            Padding = new MarginPadding { Horizontal = 9, Vertical = 5 } };
        if (icon is { } symbol)
            row.Add(new SpriteIcon { Icon = symbol, Size = new(11), Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Colour = tone == AimModTrainerChipTone.Neutral ? AimModPalette.Muted : foreground });
        row.Add(new OsuSpriteText { Text = text, Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Font = new FontUsage(size: 12, weight: "SemiBold"), Colour = foreground });
        InternalChildren = [new Box { RelativeSizeAxes = Axes.Both, Colour = background }, row];
    }
}

/// <summary>Direction in which a metric improves. Used to colour changes against a previous value.</summary>
public enum AimModTrainerTrend { HigherIsBetter, LowerIsBetter, Neutral }

/// <summary>A headline result with its change from the previous comparable run.</summary>
public partial class AimModTrainerKpi : CompositeDrawable, IHasTooltip
{
    public LocalisableString TooltipText { get; set; }

    public AimModTrainerKpi(string label, string value, string? change = null, double? delta = null,
        AimModTrainerTrend trend = AimModTrainerTrend.Neutral, bool best = false, string? tooltip = null)
    {
        Width = 156; Height = 88; Masking = true; CornerRadius = AimModVisualStyle.CardRadius;
        TooltipText = tooltip ?? string.Empty;
        bool better = delta is { } d && Math.Abs(d) > 1e-9 && (trend == AimModTrainerTrend.HigherIsBetter ? d > 0 : trend == AimModTrainerTrend.LowerIsBetter && d < 0);
        bool worse = delta is { } w && Math.Abs(w) > 1e-9 && (trend == AimModTrainerTrend.HigherIsBetter ? w < 0 : trend == AimModTrainerTrend.LowerIsBetter && w > 0);
        Colour4 changeColour = better ? AimModPalette.Accent : worse ? AimModPalette.Yellow : AimModPalette.Muted;
        var children = new List<Drawable>
        {
            new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel },
            new OsuSpriteText { X = 12, Y = 10, Text = label, Font = AimModVisualStyle.CaptionStrongFont, Colour = AimModPalette.Muted },
            new OsuSpriteText { X = 12, Y = 27, Text = value, Font = new FontUsage(size: 24, weight: "SemiBold"), Colour = AimModPalette.Text },
        };
        if (change is not null)
        {
            var line = new FillFlowContainer<Drawable> { X = 12, Y = 62, AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal, Spacing = new(4) };
            if (delta is { } arrow && Math.Abs(arrow) > 1e-9)
                line.Add(new SpriteIcon { Icon = arrow > 0 ? FontAwesome.Solid.ArrowUp : FontAwesome.Solid.ArrowDown, Size = new(9),
                    Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft, Colour = changeColour });
            line.Add(new OsuSpriteText { Text = change, Font = AimModVisualStyle.CaptionFont, Colour = changeColour, Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft });
            children.Add(line);
        }
        if (best)
            children.Add(new AimModPill("Best", AimModPillTone.Accent) { Anchor = Anchor.TopRight, Origin = Anchor.TopRight, X = -8, Y = 8 });
        InternalChildren = children.ToArray();
    }
}

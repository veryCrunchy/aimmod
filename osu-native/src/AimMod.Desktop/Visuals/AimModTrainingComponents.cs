using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;

namespace AimMod.Desktop.Visuals;

/// <summary>A compact, wrapping group of related choices with shared surface styling.</summary>
public partial class AimModChoiceGroup : CompositeDrawable
{
    public FillFlowContainer<Drawable> Choices { get; }
    public AimModChoiceGroup(string title, string subtitle)
    {
        RelativeSizeAxes = Axes.X; AutoSizeAxes = Axes.Y;
        InternalChildren = [
            new Container { RelativeSizeAxes = Axes.Both, Masking = true, CornerRadius = AimModVisualStyle.CardRadius,
                Child = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel } },
            new FillFlowContainer<Drawable> { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical, Spacing = new(8), Padding = new MarginPadding(12), Children = [
                    new TextFlowContainer(t => { t.Font = new FontUsage(size: 16, weight: "SemiBold"); t.Colour = AimModPalette.Text; })
                        { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Text = title },
                    new TextFlowContainer(t => { t.Font = new FontUsage(size: 12); t.Colour = AimModPalette.Muted; })
                        { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Text = subtitle },
                    Choices = new FillFlowContainer<Drawable> { RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Full, Spacing = new(6) }
                ] }
        ];
    }
}

/// <summary>A labelled value for compact practice and progress summaries.</summary>
public partial class AimModStatTile : CompositeDrawable
{
    public AimModStatTile(string label, string value, string hint)
    {
        Width = 132; Height = 84;
        InternalChildren = [
            new Container { RelativeSizeAxes = Axes.Both, Masking = true, CornerRadius = AimModVisualStyle.CardRadius,
                Child = new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Panel } },
            new SpriteText { X = 12, Y = 10, Text = label, Font = new FontUsage(size: 11), Colour = AimModPalette.Muted },
            new SpriteText { X = 12, Y = 28, Text = value, Font = new FontUsage(size: 25, weight: "SemiBold"), Colour = AimModPalette.Accent },
            new SpriteText { X = 12, Y = 60, Text = hint, Font = new FontUsage(size: 11), Colour = AimModPalette.Muted }
        ];
    }
}

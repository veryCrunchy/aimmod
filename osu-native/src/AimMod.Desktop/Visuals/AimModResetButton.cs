using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Visuals;

public partial class AimModResetButton : AimModInteractiveSurface
{
    private readonly OsuSpriteText label;
    private readonly string caption;

    public AimModResetButton(Action reset, string caption = "Clear filters")
    {
        this.caption = caption;
        Width = 104;
        Height = 32;
        Action = reset;
        BackgroundColour = AimModPalette.Panel;
        BorderThickness = 1; BorderColour = AimModPalette.Border;
        Child = label = new OsuSpriteText {
            Anchor = Anchor.Centre, Origin = Anchor.Centre,
            Text = caption, Font = new FontUsage(size: 13), Colour = AimModPalette.Text,
        };
    }

    /// <summary>Disables the button and shows progress while the action it started is running.</summary>
    public void SetLoading(bool loading)
    {
        Enabled.Value = !loading;
        label.Text = loading ? "Loading..." : caption;
        Alpha = loading ? 0.6f : 1;
    }
}

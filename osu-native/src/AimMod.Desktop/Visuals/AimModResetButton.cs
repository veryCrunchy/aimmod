using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Visuals;

public partial class AimModResetButton : AimModInteractiveSurface
{
    public AimModResetButton(Action reset, string caption = "Clear filters")
    {
        Width = 104;
        Height = 32;
        Action = reset;
        BackgroundColour = AimModPalette.Panel;
        BorderThickness = 1; BorderColour = AimModPalette.Border;
        Child = new OsuSpriteText {
            Anchor = Anchor.Centre, Origin = Anchor.Centre,
            Text = caption, Font = new FontUsage(size: 13), Colour = AimModPalette.Text,
        };
    }
}

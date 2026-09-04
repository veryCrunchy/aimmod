using osu.Framework.Allocation;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;

namespace AimMod.Desktop.Visuals;

public partial class AimModBrandMark : Sprite
{
    [BackgroundDependencyLoader]
    private void load(IRenderer renderer)
    {
        using Stream stream = typeof(AimModGame).Assembly.GetManifestResourceStream("AimMod.Resources.Brand.mark-mint.png")
            ?? throw new InvalidOperationException("The AimMod brand resource is missing.");
        Texture = Texture.FromStream(renderer, stream);
    }
}

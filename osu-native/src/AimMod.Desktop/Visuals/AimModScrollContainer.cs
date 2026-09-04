using osu.Framework.Graphics;
using osu.Game.Graphics.Containers;

namespace AimMod.Desktop.Visuals;

/// <summary>
/// Keeps osu!'s scrollbar styling while reserving a real gutter for the handle.
/// </summary>
public partial class AimModScrollContainer : OsuScrollContainer
{
    public AimModScrollContainer(Direction direction = Direction.Vertical)
        : base(direction)
    {
        ScrollbarOverlapsContent = false;
    }
}

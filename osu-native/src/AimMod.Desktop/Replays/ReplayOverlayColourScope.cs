using osu.Framework.Allocation;
using osu.Framework.Graphics.Containers;
using osu.Game.Overlays;

namespace AimMod.Desktop.Replays;

/// <summary>
/// Gives native osu! controls (switches, slider nubs) AimMod's mint accent instead of their pink
/// default by providing the aquamarine overlay colour scheme to everything inside it.
/// </summary>
public partial class ReplayOverlayColourScope : Container
{
    [Cached]
    private readonly OverlayColourProvider colours = new(OverlayColourScheme.Aquamarine);
}

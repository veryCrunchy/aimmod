using osu.Framework.Graphics.UserInterface;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using osu.Game.Graphics.UserInterface;

namespace AimMod.Desktop.Visuals;

public partial class AimModDropdown<T> : OsuDropdown<T>
{
    protected override DropdownHeader CreateHeader() => new AimModDropdownHeader<T>();
    protected override DropdownMenu CreateMenu() => new AimModDropdownMenu<T>();
    [osu.Framework.Allocation.Resolved] private osu.Framework.Platform.GameHost popupHost { get; set; } = null!;
    private AimModPopupLayer? popupLayer;
    public AimModDropdown()
    {
        Menu.MaxHeight = 240;
        Menu.StateChanged += state =>
        {
            popupLayer?.Dispose(); popupLayer = null;
            if (state == MenuState.Open) popupLayer = new AimModPopupLayer(this, action => popupHost.UpdateThread.Scheduler.Add(action));
        };
    }
    protected override void Dispose(bool isDisposing)
    { popupLayer?.Dispose(); popupLayer = null; base.Dispose(isDisposing); }
    protected override void Update()
    {
        base.Update();
        Drawable viewport = this;
        while (viewport.Parent is { } parent) viewport = parent;
        float bottom = ToLocalSpace(viewport.ToScreenSpace(new osuTK.Vector2(0, viewport.DrawHeight))).Y;
        Menu.MaxHeight = Math.Clamp(bottom - DrawHeight - 24, 1, 240);
    }
}

public partial class AimModDropdownHeader<T> : OsuDropdown<T>.OsuDropdownHeader
{
    private LocalisableString value;
    public string Prefix { get; set; } = string.Empty;
    protected override LocalisableString Label
    {
        get => value;
        set { this.value = value; Text.Text = string.IsNullOrEmpty(Prefix) ? value : $"{Prefix}  ·  {value}"; }
    }
    public AimModDropdownHeader()
    {
        Height = AimModVisualStyle.ControlHeight;
        Margin = new MarginPadding(); CornerRadius = AimModVisualStyle.ControlRadius;
        BorderThickness = 1; BorderColour = AimModPalette.Border;
        Foreground.Padding = new MarginPadding { Horizontal = 12, Vertical = 9 };
        Text.Font = new FontUsage(size:14);
    }
    private void style()
    {
        Background.Colour = IsHovered ? AimModPalette.PanelHover : AimModPalette.Panel;
        Text.Colour = AimModPalette.Text; Chevron.Colour = AimModPalette.Muted;
    }
    protected override void LoadComplete() { base.LoadComplete(); style(); }
    protected override bool OnHover(HoverEvent e) { base.OnHover(e); style(); return true; }
    protected override void OnHoverLost(HoverLostEvent e) { base.OnHoverLost(e); style(); }
}

public partial class AimModDropdownMenu<T> : OsuDropdown<T>.OsuDropdownMenu
{
    protected override void LoadComplete()
    {
        base.LoadComplete(); BackgroundColour = AimModPalette.PanelRaised;
        HoverColour = AimModPalette.PanelHover; SelectionColour = AimModPalette.AccentMuted;
    }
}

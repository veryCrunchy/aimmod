using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;

namespace AimMod.Desktop.Visuals;

/// <summary>Shared text field for search, settings and practice forms.</summary>
public partial class AimModTextBox : OsuTextBox
{
    public AimModTextBox() { Height = AimModVisualStyle.ControlHeight; CornerRadius = AimModVisualStyle.ControlRadius; }
    protected override void LoadComplete()
    {
        base.LoadComplete();
        BackgroundUnfocused = AimModPalette.Panel;
        BackgroundFocused = AimModPalette.PanelRaised;
        BackgroundCommit = AimModPalette.AccentMuted;
        BorderColour = AimModPalette.Accent;
        Placeholder.Colour = AimModPalette.Muted;
        Placeholder.Font = new FontUsage(size: 14);
        TextContainer.Height = .42f;
    }
}

/// <summary>Mint primary actions and outlined secondary actions and local tabs.</summary>
public partial class AimModButton : ClickableContainer
{
    private readonly Box background;
    private readonly OsuSpriteText caption;
    private readonly Container captionContainer;
    private readonly bool primary;
    private bool selected;
    public AimModButton(string text, Action action, bool primary = false)
    {
        this.primary = primary; Action = action; AutoSizeAxes = Axes.X;
        Height = AimModVisualStyle.ControlHeight; Masking = true;
        CornerRadius = AimModVisualStyle.ControlRadius; BorderThickness = 1;
        Children = [background = new Box { RelativeSizeAxes = Axes.Both },
            captionContainer = new Container { AutoSizeAxes = Axes.Both, Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft,
                Padding = new MarginPadding { Horizontal = 14 },
                Child = caption = new OsuSpriteText { Text = text, Font = new FontUsage(size:14, weight:"SemiBold") } }];
        SetSelected(false);
    }
    public void SetCaption(string value) => caption.Text = value;
    public void SetVisualContent(Drawable content, float height)
    {
        captionContainer.Hide();
        AutoSizeAxes = Axes.None;
        Height = height;
        Add(content);
    }
    public void SetSelected(bool value)
    {
        selected = value;
        background.Colour = primary ? AimModPalette.Accent : selected ? AimModPalette.AccentMuted : AimModPalette.Panel;
        caption.Colour = primary ? AimModPalette.Canvas : selected ? AimModPalette.Accent : AimModPalette.Text;
        BorderColour = primary || selected ? AimModPalette.Accent.Opacity(.45f) : AimModPalette.Border;
    }
    protected override bool OnHover(HoverEvent e)
    { background.FadeColour(primary ? Colour4.FromHex("74E9C8") : AimModPalette.PanelHover, 100); return true; }
    protected override void OnHoverLost(HoverLostEvent e) { SetSelected(selected); base.OnHoverLost(e); }
}

public partial class AimModTabControl<T> : FillFlowContainer<Drawable> where T : struct, Enum
{
    public Bindable<T> Current { get; set; } = new();
    public AimModTabControl()
    { AutoSizeAxes = Axes.X; Height = AimModVisualStyle.ControlHeight; Direction = FillDirection.Horizontal; Spacing = new(8); }
    protected override void LoadComplete()
    {
        base.LoadComplete();
        foreach (T option in Enum.GetValues<T>())
        {
            var button = new AimModButton(option.ToString(), () => Current.Value = option);
            Add(button);
            Current.BindValueChanged(v => button.SetSelected(EqualityComparer<T>.Default.Equals(v.NewValue, option)), true);
        }
    }
}

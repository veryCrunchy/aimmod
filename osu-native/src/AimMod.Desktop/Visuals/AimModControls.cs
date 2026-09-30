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
    private QueryDebouncer? debouncer;
    private Action<string>? queryChanged;

    public AimModTextBox() { Height = AimModVisualStyle.ControlHeight; CornerRadius = AimModVisualStyle.ControlRadius; }

    /// <summary>Raised once per edit burst after a short pause, or immediately (once) on Enter.</summary>
    public event Action<string>? QueryChanged
    {
        add
        {
            queryChanged += value;
            if (debouncer is not null) return;
            debouncer = new QueryDebouncer(text => queryChanged?.Invoke(text));
            OnCommit += (_, _) => debouncer.Commit(Current.Value);
            Current.BindValueChanged(text => debouncer.Submit(text.NewValue, IsLoaded ? Time.Current : 0));
        }
        remove => queryChanged -= value;
    }

    /// <summary>Raised when Down is pressed while typing so a page can move focus into its results.</summary>
    public event Action? MoveToResults;

    /// <summary>Opt in to Ctrl+F focusing this box while it is visible.</summary>
    public bool FocusOnSearchShortcut { get; set; }

    public override bool HandleNonPositionalInput => base.HandleNonPositionalInput || FocusOnSearchShortcut;

    /// <summary>Gives the text box keyboard focus once it is part of a loaded, visible hierarchy.</summary>
    public void FocusSearch() => Schedule(() =>
    {
        if (IsLoaded && IsPresent)
            GetContainingFocusManager()?.ChangeFocus(this);
    });

    protected override void Update()
    {
        base.Update();
        if (debouncer?.IsPending == true)
            debouncer.Update(Time.Current);
    }

    protected override bool OnKeyDown(osu.Framework.Input.Events.KeyDownEvent e)
    {
        if (FocusOnSearchShortcut && !HasFocus && e.ControlPressed && e.Key == osuTK.Input.Key.F && isVisibleOnScreen())
        {
            FocusSearch();
            return true;
        }

        if (HasFocus && e.Key == osuTK.Input.Key.Down && MoveToResults is not null && !e.ControlPressed && !e.AltPressed)
        {
            MoveToResults.Invoke();
            return true;
        }

        return base.OnKeyDown(e);
    }

    private bool isVisibleOnScreen()
    {
        for (Drawable? drawable = this; drawable is not null; drawable = drawable.Parent)
        {
            if (!drawable.IsPresent || drawable.Alpha <= 0)
                return false;
        }
        return true;
    }

    protected override void Dispose(bool isDisposing)
    {
        debouncer?.Cancel();
        base.Dispose(isDisposing);
    }

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

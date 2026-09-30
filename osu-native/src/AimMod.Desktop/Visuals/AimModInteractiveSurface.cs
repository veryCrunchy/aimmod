using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Game.Graphics.Sprites;
using osuTK.Input;

namespace AimMod.Desktop.Visuals;

public partial class AimModInteractiveSurface : ClickableContainer
{
    private readonly Container content;
    private readonly Box background;
    private readonly Box hoverLayer;
    private readonly Box flashLayer;
    private Colour4 restingColour = AimModPalette.Panel;

    protected override Container<Drawable> Content => content;

    public AimModInteractiveSurface()
    {
        Masking = true;
        CornerRadius = AimModVisualStyle.ControlRadius;
        BorderThickness = 1; BorderColour = AimModPalette.Border;

        InternalChildren = new Drawable[]
        {
            background = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = restingColour,
            },
            hoverLayer = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Colour4.White,
                Blending = BlendingParameters.Additive,
                Alpha = 0,
            },
            flashLayer = new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Colour4.White,
                Blending = BlendingParameters.Additive,
                Alpha = 0,
            },
            content = new Container { RelativeSizeAxes = Axes.Both },
        };
    }

    public Colour4 BackgroundColour
    {
        get => restingColour;
        set
        {
            restingColour = value;
            background.Colour = value;
        }
    }

    /// <summary>When false the surface never takes keyboard focus.</summary>
    public bool KeyboardFocusable { get; set; } = true;

    public override bool AcceptsFocus => KeyboardFocusable && Enabled.Value && Action is not null && IsPresent;

    private bool pointerActivated;
    private bool keyboardFocused;
    private float restingBorderThickness;
    private osu.Framework.Graphics.Colour.ColourInfo restingBorderColour;

    /// <summary>Moves keyboard focus here and shows the focus ring.</summary>
    public void FocusFromKeyboard()
    {
        if (!AcceptsFocus)
            return;
        pointerActivated = false;
        GetContainingFocusManager()?.ChangeFocus(this);
    }

    protected override void OnFocus(FocusEvent e)
    {
        // Pointer clicks also focus the surface. Only keyboard focus shows a ring and handles keys,
        // so Space still reaches page shortcuts after a click.
        keyboardFocused = !pointerActivated;
        pointerActivated = false;
        if (keyboardFocused)
        {
            restingBorderThickness = BorderThickness;
            restingBorderColour = BorderColour;
            BorderColour = AimModPalette.Accent;
            BorderThickness = 2;
        }
        base.OnFocus(e);
    }

    protected override void OnFocusLost(FocusLostEvent e)
    {
        if (keyboardFocused)
        {
            BorderThickness = restingBorderThickness;
            BorderColour = restingBorderColour;
        }
        keyboardFocused = false;
        base.OnFocusLost(e);
    }

    protected override bool OnKeyDown(KeyDownEvent e)
    {
        if (!HasFocus || !keyboardFocused || e.ControlPressed || e.AltPressed)
            return base.OnKeyDown(e);

        switch (e.Key)
        {
            case Key.Enter or Key.KeypadEnter or Key.Space:
                if (e.Repeat || Action is null || !Enabled.Value)
                    return false;
                flashLayer.FadeOutFromOne(500, Easing.OutQuint);
                TriggerClick();
                return true;

            case Key.Down or Key.Up:
                return MoveFocus(e.Key == Key.Down ? 1 : -1);

            case Key.Escape:
                GetContainingFocusManager()?.ChangeFocus(null);
                return true;
        }

        return base.OnKeyDown(e);
    }

    /// <summary>Moves keyboard focus to the previous or next visible surface in the same scroll region.</summary>
    public bool MoveFocus(int direction)
    {
        Drawable? scope = this.FindClosestParent<ScrollContainer<Drawable>>() ?? (Drawable?)Parent;
        if (scope is not CompositeDrawable root)
            return false;

        List<AimModInteractiveSurface> candidates = FocusCandidates(root);
        int index = candidates.IndexOf(this);
        if (index < 0 || index + direction < 0 || index + direction >= candidates.Count)
            return false;

        AimModInteractiveSurface target = candidates[index + direction];
        target.FocusFromKeyboard();
        this.FindClosestParent<ScrollContainer<Drawable>>()?.ScrollIntoView(target);
        return true;
    }

    /// <summary>Visible, focusable surfaces in reading order.</summary>
    public static List<AimModInteractiveSurface> FocusCandidates(CompositeDrawable root)
    {
        var candidates = new List<AimModInteractiveSurface>();
        collect(root, candidates);
        candidates.Sort((left, right) =>
        {
            int vertical = left.ScreenSpaceDrawQuad.TopLeft.Y.CompareTo(right.ScreenSpaceDrawQuad.TopLeft.Y);
            return vertical != 0 ? vertical : left.ScreenSpaceDrawQuad.TopLeft.X.CompareTo(right.ScreenSpaceDrawQuad.TopLeft.X);
        });
        return candidates;
    }

    /// <summary>Focuses the first visible surface below <paramref name="root"/>, for search-box Down arrows.</summary>
    public static bool FocusFirst(CompositeDrawable root)
    {
        List<AimModInteractiveSurface> candidates = FocusCandidates(root);
        if (candidates.Count == 0)
            return false;
        candidates[0].FocusFromKeyboard();
        root.FindClosestParent<ScrollContainer<Drawable>>()?.ScrollIntoView(candidates[0]);
        return true;
    }

    private static void collect(Drawable drawable, List<AimModInteractiveSurface> result)
    {
        if (!drawable.IsPresent || drawable.Alpha <= 0)
            return;
        if (drawable is AimModInteractiveSurface { AcceptsFocus: true } surface)
        {
            result.Add(surface);
            return;
        }
        if (drawable is IContainerEnumerable<Drawable> container)
        {
            foreach (Drawable child in container.Children)
                collect(child, result);
        }
        else if (drawable is CompositeDrawable composite)
        {
            foreach (AimModInteractiveSurface nested in osu.Framework.Testing.TestingExtensions.ChildrenOfType<AimModInteractiveSurface>(composite))
            {
                if (nested.AcceptsFocus && isVisible(nested, composite))
                    result.Add(nested);
            }
        }
    }

    private static bool isVisible(Drawable drawable, Drawable root)
    {
        for (Drawable? current = drawable; current is not null && current != root; current = current.Parent)
        {
            if (!current.IsPresent || current.Alpha <= 0)
                return false;
        }
        return true;
    }

    protected override bool OnHover(HoverEvent e)
    {
        hoverLayer.FadeTo(0.2f, 40, Easing.OutQuint)
                  .Then()
                  .FadeTo(0.1f, AimModVisualStyle.SettleTransition, Easing.OutQuint);
        return base.OnHover(e);
    }

    protected override void OnHoverLost(HoverLostEvent e)
    {
        hoverLayer.FadeOut(AimModVisualStyle.SettleTransition, Easing.OutQuint);
        base.OnHoverLost(e);
    }

    protected override bool OnClick(ClickEvent e)
    {
        flashLayer.FadeOutFromOne(500, Easing.OutQuint);
        return base.OnClick(e);
    }

    protected override bool OnMouseDown(MouseDownEvent e)
    {
        pointerActivated = true;
        content.ScaleTo(0.995f, 80, Easing.OutQuint);
        return base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseUpEvent e)
    {
        content.ScaleTo(1, 100, Easing.OutQuint);
        base.OnMouseUp(e);
    }
}

public partial class AimModSubsectionHeader : CompositeDrawable
{
    private readonly OsuSpriteText titleText;
    private readonly OsuSpriteText detailText;

    public AimModSubsectionHeader(string title, string? detail = null)
    {
        RelativeSizeAxes = Axes.X;
        Height = 40;

        InternalChildren = new Drawable[]
        {
            new Box
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                RelativeSizeAxes = Axes.Y,
                Height = 0.55f,
                Width = 3,
                Colour = AimModPalette.Accent,
            },
            titleText = new OsuSpriteText
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                X = 13,
                Font = new FontUsage(size: 14, weight: "Bold"),
                Colour = AimModPalette.Text,
            },
            detailText = new OsuSpriteText
            {
                Anchor = Anchor.CentreRight,
                Origin = Anchor.CentreRight,
                Margin = new MarginPadding { Right = 4 },
                Font = new FontUsage(size: 11, weight: "SemiBold"),
                Colour = AimModPalette.Muted,
            },
        };

        Title = title;
        Detail = detail;
    }

    public string Title
    {
        get => titleText.Text.ToString();
        set => titleText.Text = value ?? string.Empty;
    }

    public string? Detail
    {
        get => detailText.Text.ToString();
        set
        {
            detailText.Text = value ?? string.Empty;
            detailText.Alpha = string.IsNullOrWhiteSpace(value) ? 0 : 1;
        }
    }
}

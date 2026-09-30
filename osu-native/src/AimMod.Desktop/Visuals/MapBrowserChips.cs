using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;

namespace AimMod.Desktop.Visuals;

/// <summary>
/// Auto-sized pill-shaped button with hover feedback. Unlike <see cref="AimModInteractiveSurface"/>
/// its size follows its content, so it suits chips and difficulty choices.
/// </summary>
public partial class MapBrowserPillButton : ClickableContainer
{
    private readonly Box background;
    private readonly Box hover;
    private readonly FillFlowContainer flow;
    private Colour4 restingColour;

    public MapBrowserPillButton(float height = 26, float cornerRadius = 13)
    {
        AutoSizeAxes = Axes.X;
        Height = height;
        Masking = true;
        CornerRadius = cornerRadius;
        BorderThickness = 1;
        InternalChildren = new Drawable[]
        {
            background = new Box { RelativeSizeAxes = Axes.Both },
            hover = new Box { RelativeSizeAxes = Axes.Both, Colour = Colour4.White, Blending = BlendingParameters.Additive, Alpha = 0 },
            flow = new FillFlowContainer
            {
                AutoSizeAxes = Axes.X,
                RelativeSizeAxes = Axes.Y,
                Direction = FillDirection.Horizontal,
                Spacing = new(6, 0),
                Padding = new MarginPadding { Horizontal = 10 },
            },
        };
        SetColours(AimModPalette.Panel, AimModPalette.Border);
    }

    public void SetColours(Colour4 fill, Colour4 border)
    {
        restingColour = fill;
        background.Colour = fill;
        BorderColour = border;
    }

    public void AddContent(Drawable drawable)
    {
        drawable.Anchor = Anchor.CentreLeft;
        drawable.Origin = Anchor.CentreLeft;
        flow.Add(drawable);
    }

    public MarginPadding ContentPadding { set => flow.Padding = value; }

    protected override bool OnHover(osu.Framework.Input.Events.HoverEvent e)
    {
        hover.FadeTo(0.08f, AimModVisualStyle.FastTransition);
        return base.OnHover(e);
    }

    protected override void OnHoverLost(osu.Framework.Input.Events.HoverLostEvent e)
    {
        hover.FadeOut(AimModVisualStyle.SettleTransition);
        base.OnHoverLost(e);
    }
}

/// <summary>An applied filter shown as a removable chip below the toolbar.</summary>
public partial class MapBrowserFilterChip : MapBrowserPillButton
{
    public string Text { get; }

    public MapBrowserFilterChip(string text, Action remove)
    {
        Text = text;
        Action = remove;
        SetColours(AimModPalette.AccentMuted, AimModPalette.Accent.Opacity(0.35f));
        ContentPadding = new MarginPadding { Left = 11, Right = 9 };
        AddContent(new SpriteText { Text = text, Font = AimModVisualStyle.CaptionStrongFont, Colour = AimModPalette.Accent });
        AddContent(new SpriteIcon { Icon = FontAwesome.Solid.Times, Size = new(9), Colour = AimModPalette.Accent });
    }
}

/// <summary>A labelled single-choice row of compact buttons, bound to an enum value.</summary>
public partial class MapBrowserChoiceGroup<T> : FillFlowContainer where T : struct, Enum
{
    public MapBrowserChoiceGroup(string label, Bindable<T> current, Func<T, string> format, float labelWidth = 64)
    {
        RelativeSizeAxes = Axes.X;
        AutoSizeAxes = Axes.Y;
        Direction = FillDirection.Horizontal;
        Spacing = new(AimModVisualStyle.RelatedSpacing);
        Add(new Container
        {
            Width = labelWidth,
            Height = AimModVisualStyle.CompactControlHeight,
            Child = new SpriteText
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                Text = label,
                Font = AimModVisualStyle.BodyStrongFont,
                Colour = AimModPalette.Muted,
            },
        });
        foreach (T option in Enum.GetValues<T>())
        {
            var button = new AimModButton(format(option), () => current.Value = option) { Height = AimModVisualStyle.CompactControlHeight };
            Add(button);
            current.BindValueChanged(value => button.SetSelected(EqualityComparer<T>.Default.Equals(value.NewValue, option)), true);
        }
    }
}

/// <summary>osu! ranking status as a compact coloured badge.</summary>
public partial class MapBrowserStatusBadge : CompositeDrawable
{
    public MapBrowserStatusBadge(string status)
    {
        AutoSizeAxes = Axes.Both;
        (Colour4 background, Colour4 text) = status.ToLowerInvariant() switch
        {
            "ranked" or "approved" => (AimModPalette.AccentMuted, AimModPalette.Accent),
            "qualified" => (AimModPalette.CyanDark.Opacity(0.45f), AimModPalette.Cyan),
            // osu! marks loved maps with pink; keep that semantic cue.
            "loved" => (AimModPalette.PinkDark.Opacity(0.35f), AimModPalette.Pink),
            _ => (AimModPalette.PanelHover, AimModPalette.Muted),
        };
        InternalChild = new CircularContainer
        {
            AutoSizeAxes = Axes.Both,
            Masking = true,
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = background },
                new SpriteText
                {
                    Text = MapBrowserFormat.Status(status),
                    Padding = new MarginPadding { Horizontal = 8, Vertical = 3 },
                    Font = AimModVisualStyle.LabelFont,
                    Colour = text,
                },
            },
        };
    }
}

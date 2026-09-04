using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Game.Graphics.Sprites;

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
        content.ScaleTo(0.985f, 160, Easing.OutQuint);
        return base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseUpEvent e)
    {
        content.ScaleTo(1, 500, Easing.OutElastic);
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
                Colour = AimModPalette.Pink,
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

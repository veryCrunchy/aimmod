using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Visuals;

public partial class AimModPill : CompositeDrawable
{
    private readonly Box background;
    private readonly OsuSpriteText label;
    private AimModPillTone tone;

    public AimModPill(string text, AimModPillTone tone = AimModPillTone.Neutral)
    {
        AutoSizeAxes = Axes.Both;

        InternalChild = new CircularContainer
        {
            AutoSizeAxes = Axes.Both,
            Masking = true,
            Children = new Drawable[]
            {
                background = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                },
                new FillFlowContainer
                {
                    AutoSizeAxes = Axes.Both,
                    Padding = new MarginPadding { Horizontal = 9, Vertical = 4 },
                    Child = label = new OsuSpriteText
                    {
                        Font = new FontUsage(size: 11, weight: "SemiBold"),
                    },
                },
            },
        };

        Text = text;
        Tone = tone;
    }

    public string Text
    {
        get => label.Text.ToString();
        set => label.Text = value ?? string.Empty;
    }

    public AimModPillTone Tone
    {
        get => tone;
        set
        {
            tone = value;
            (background.Colour, label.Colour) = value switch
            {
                AimModPillTone.Accent => (AimModPalette.PinkDark, AimModPalette.Text),
                AimModPillTone.Info => (AimModPalette.CyanDark, AimModPalette.Text),
                AimModPillTone.Success => (AimModPalette.Success, AimModPalette.Canvas),
                _ => (AimModPalette.PanelHover, AimModPalette.Muted),
            };
        }
    }
}

public partial class AimModDifficultyPill : CompositeDrawable
{
    private readonly Box background;
    private readonly SpriteIcon star;
    private readonly OsuSpriteText label;
    private double starRating;

    public AimModDifficultyPill(double starRating)
    {
        AutoSizeAxes = Axes.Both;

        InternalChild = new CircularContainer
        {
            AutoSizeAxes = Axes.Both,
            Masking = true,
            Children = new Drawable[]
            {
                background = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Alpha = 0.92f,
                },
                new FillFlowContainer
                {
                    AutoSizeAxes = Axes.Both,
                    Direction = FillDirection.Horizontal,
                    Spacing = new(5),
                    Padding = new MarginPadding { Horizontal = 9, Vertical = 4 },
                    Children = new Drawable[]
                    {
                        star = new SpriteIcon
                        {
                            Icon = FontAwesome.Solid.Star,
                            Size = new(10),
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                        },
                        label = new OsuSpriteText
                        {
                            Font = new FontUsage(size: 11, weight: "Bold"),
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                        },
                    },
                },
            },
        };

        StarRating = starRating;
    }

    public double StarRating
    {
        get => starRating;
        set
        {
            starRating = AimModVisualStyle.NormaliseStarRating(value);
            Colour4 difficultyColour = AimModVisualStyle.DifficultyColour(starRating);
            Colour4 textColour = AimModVisualStyle.DifficultyTextColour(starRating);
            background.Colour = difficultyColour;
            star.Colour = textColour;
            label.Colour = textColour;
            label.Text = AimModVisualStyle.FormatStarRating(starRating);
        }
    }
}

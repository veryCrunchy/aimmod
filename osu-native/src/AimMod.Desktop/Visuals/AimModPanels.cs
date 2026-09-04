using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Visuals;

public partial class AimModSlantedAccentPanel : Container
{
    private readonly Container content;
    private readonly Box accent;
    private readonly Box accentEdge;
    private Colour4 accentColour;

    protected override Container<Drawable> Content => content;

    public AimModSlantedAccentPanel()
    {
        Masking = true;
        CornerRadius = AimModVisualStyle.CardRadius;

        InternalChildren = new Drawable[]
        {
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = AimModPalette.Panel,
            },
            accent = new Box
            {
                RelativeSizeAxes = Axes.Y,
                Width = 18,
                X = -9,
                Shear = new(-0.18f, 0),
            },
            accentEdge = new Box
            {
                RelativeSizeAxes = Axes.Y,
                Width = 3,
                X = 7,
                Shear = new(-0.18f, 0),
                Alpha = 0.55f,
            },
            content = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding { Left = 24, Right = 18, Vertical = 14 },
            },
        };

        AccentColour = AimModPalette.Pink;
    }

    public Colour4 AccentColour
    {
        get => accentColour;
        set
        {
            accentColour = value;
            accent.Colour = value;
            accentEdge.Colour = value;
        }
    }
}

public partial class AimModSectionHeader : CompositeDrawable
{
    private readonly OsuSpriteText eyebrowText;
    private readonly OsuSpriteText titleText;
    private readonly OsuSpriteText subtitleText;
    private readonly Box accent;

    public AimModSectionHeader(string title, string? subtitle = null, string? eyebrow = null)
    {
        RelativeSizeAxes = Axes.X;
        AutoSizeAxes = Axes.Y;

        InternalChild = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new(AimModVisualStyle.RelatedSpacing),
            Children = new Drawable[]
            {
                new FillFlowContainer
                {
                    AutoSizeAxes = Axes.Both,
                    Direction = FillDirection.Horizontal,
                    Spacing = new(9),
                    Children = new Drawable[]
                    {
                        accent = new Box
                        {
                            Size = new(28, 2),
                            Shear = new(-0.35f, 0),
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                        },
                        eyebrowText = new OsuSpriteText
                        {
                            Font = new FontUsage(size: 10, weight: "Bold"),
                            Colour = AimModPalette.Cyan,
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                        },
                    },
                },
                titleText = new OsuSpriteText
                {
                    Font = new FontUsage(size: 24, weight: "Bold"),
                    Colour = AimModPalette.Text,
                },
                subtitleText = new OsuSpriteText
                {
                    Font = new FontUsage(size: 12),
                    Colour = AimModPalette.Muted,
                },
            },
        };

        AccentColour = AimModPalette.Pink;
        Title = title;
        Subtitle = subtitle;
        Eyebrow = eyebrow;
    }

    public Colour4 AccentColour
    {
        get => accent.Colour;
        set => accent.Colour = value;
    }

    public string Title
    {
        get => titleText.Text.ToString();
        set => titleText.Text = value ?? string.Empty;
    }

    public string? Subtitle
    {
        get => subtitleText.Text.ToString();
        set
        {
            subtitleText.Text = value ?? string.Empty;
            subtitleText.Alpha = string.IsNullOrWhiteSpace(value) ? 0 : 1;
        }
    }

    public string? Eyebrow
    {
        get => eyebrowText.Text.ToString();
        set
        {
            eyebrowText.Text = value?.ToUpperInvariant() ?? string.Empty;
            eyebrowText.Alpha = string.IsNullOrWhiteSpace(value) ? 0 : 1;
        }
    }
}

public partial class AimModBeatmapBanner : CompositeDrawable
{
    private readonly TruncatingSpriteText titleText;
    private readonly TruncatingSpriteText artistText;
    private readonly TruncatingSpriteText difficultyText;

    public AimModBeatmapBanner(AimModBeatmapBannerModel model, Drawable? artwork = null)
    {
        ArgumentNullException.ThrowIfNull(model);

        Model = normalise(model);
        RelativeSizeAxes = Axes.X;
        Height = 172;
        Masking = true;
        CornerRadius = AimModVisualStyle.CardRadius;

        if (artwork is not null)
            artwork.RelativeSizeAxes = Axes.Both;

        InternalChildren = new Drawable[]
        {
            artwork ?? new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = ColourInfo.GradientHorizontal(AimModPalette.PanelRaised, AimModPalette.CyanDark),
            },
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = AimModPalette.Canvas,
                Alpha = artwork is null ? 0.32f : 0.62f,
            },
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = ColourInfo.GradientVertical(Colour4.Transparent, AimModPalette.Canvas),
                Alpha = 0.92f,
            },
            new Box
            {
                Anchor = Anchor.TopRight,
                Origin = Anchor.TopRight,
                RelativeSizeAxes = Axes.Y,
                Width = 120,
                X = 42,
                Shear = new(-0.22f, 0),
                Colour = AimModPalette.Pink,
                Alpha = 0.14f,
            },
            new FillFlowContainer
            {
                Anchor = Anchor.BottomLeft,
                Origin = Anchor.BottomLeft,
                AutoSizeAxes = Axes.Both,
                Margin = new MarginPadding { Left = 22, Bottom = 20 },
                Direction = FillDirection.Vertical,
                Spacing = new(3),
                Children = new Drawable[]
                {
                    titleText = new TruncatingSpriteText
                    {
                        Font = new FontUsage(size: 22, weight: "Bold"),
                        Colour = AimModPalette.Text,
                        MaxWidth = 720,
                    },
                    artistText = new TruncatingSpriteText
                    {
                        Font = new FontUsage(size: 13, weight: "SemiBold"),
                        Colour = AimModPalette.Muted,
                        MaxWidth = 720,
                    },
                    difficultyText = new TruncatingSpriteText
                    {
                        Font = new FontUsage(size: 12),
                        Colour = AimModPalette.Cyan,
                        MaxWidth = 720,
                    },
                },
            },
            new AimModDifficultyPill(Model.StarRating)
            {
                Anchor = Anchor.TopRight,
                Origin = Anchor.TopRight,
                Margin = new MarginPadding { Top = 14, Right = 16 },
            },
        };

        titleText.Text = Model.Title;
        artistText.Text = Model.Artist;
        difficultyText.Text = formatDetail(Model);
    }

    public AimModBeatmapBannerModel Model { get; }

    private static AimModBeatmapBannerModel normalise(AimModBeatmapBannerModel model) => model with
    {
        Title = fallback(model.Title, "Untitled beatmap"),
        Artist = fallback(model.Artist, "Unknown artist"),
        Difficulty = fallback(model.Difficulty, "Unknown difficulty"),
        StarRating = AimModVisualStyle.NormaliseStarRating(model.StarRating),
        Creator = string.IsNullOrWhiteSpace(model.Creator) ? null : model.Creator.Trim(),
        Ruleset = string.IsNullOrWhiteSpace(model.Ruleset) ? null : model.Ruleset.Trim(),
    };

    private static string formatDetail(AimModBeatmapBannerModel model)
    {
        string detail = model.Difficulty;
        if (model.Creator is not null)
            detail += $"  //  mapped by {model.Creator}";
        if (model.Ruleset is not null)
            detail += $"  //  {model.Ruleset}";
        return detail;
    }

    private static string fallback(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Skins;

/// <summary>
/// Installed skin as a grid card or a compact list row: a thumbnail drawn from the skin's own
/// elements, its readable name (team tag split out), creator and library, and state badges.
/// </summary>
public partial class SkinCardView : AimModInteractiveSurface, IHasTooltip
{
    public const float ThumbnailHeight = 104;
    public const float CardHeight = ThumbnailHeight + 54;
    public const float RowHeight = 60;
    private const float row_thumbnail_width = 108;

    private readonly Container thumbnail;
    private readonly FillFlowContainer badges;
    private readonly FillFlowContainer titleRow;
    private readonly AimModPill? tag;
    private readonly TruncatingSpriteText name;
    private readonly TruncatingSpriteText meta;
    private readonly Box selectionBar;
    private readonly string storedName;
    private bool compact;
    private AimModLayout.ChangeTracker<(float, bool, float, float)> widthTracker;

    public Guid SkinId { get; }

    public SkinCardView(InstalledLazerSkin skin, bool selected, bool activeInLazer, bool inUse, SkinTextureCache? textures, Action action)
    {
        SkinId = skin.SkinId;
        storedName = skin.Name;
        Height = CardHeight;
        Width = 240;
        CornerRadius = AimModVisualStyle.CardRadius;
        Action = action;

        badges = new FillFlowContainer
        {
            AutoSizeAxes = Axes.Both,
            Direction = FillDirection.Horizontal,
            Spacing = new(6),
            Margin = new MarginPadding(8),
        };
        // State badges say what the skin is doing; where it lives is in the text below.
        if (inUse)
            badges.Add(new AimModPill("In use", AimModPillTone.Success));
        if (activeInLazer)
            badges.Add(new AimModPill("Active in lazer", AimModPillTone.Info));

        titleRow = new FillFlowContainer
        {
            AutoSizeAxes = Axes.Both,
            Direction = FillDirection.Horizontal,
            Spacing = new(6),
        };
        if (skin.DisplayTag is { } tagText)
            titleRow.Add(tag = new AimModPill(tagText) { Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft });
        titleRow.Add(name = new TruncatingSpriteText
        {
            Anchor = Anchor.CentreLeft,
            Origin = Anchor.CentreLeft,
            Text = skin.DisplayName,
            Font = new FontUsage(size: 14, weight: "SemiBold"),
            Colour = AimModPalette.Text,
        });

        Children = new Drawable[]
        {
            thumbnail = new Container
            {
                RelativeSizeAxes = Axes.X,
                Height = ThumbnailHeight,
                Masking = true,
                Children = new Drawable[]
                {
                    new Box { RelativeSizeAxes = Axes.Both, Colour = AimModPalette.Canvas },
                    // Only cards scrolled into view read and upload their elements.
                    new DelayedLoadWrapper(() => new SkinThumbnailView(skin, textures), 0) { RelativeSizeAxes = Axes.Both },
                },
            },
            badges,
            titleRow,
            meta = new TruncatingSpriteText
            {
                Text = $"{CreatorLabel(skin)}  ·  {SourceLabel(skin)}",
                Font = AimModVisualStyle.CaptionFont,
                Colour = AimModPalette.Muted,
            },
            selectionBar = new Box
            {
                Anchor = Anchor.BottomLeft,
                Origin = Anchor.BottomLeft,
                RelativeSizeAxes = Axes.X,
                Height = 3,
                Colour = AimModPalette.Accent,
            },
        };
        SetSelected(selected);
        SetCompact(false);
    }

    public LocalisableString TooltipText => SkinDisplayName.Differs(storedName) ? storedName.Trim() : string.Empty;

    public static string SourceLabel(InstalledLazerSkin skin) =>
        skin.IsBuiltIn ? "built into lazer" : skin.Origin == InstalledSkinOrigin.Stable ? "stable" : "lazer";

    public static string CreatorLabel(InstalledLazerSkin skin) =>
        skin.Creator.Length == 0 || string.Equals(skin.Creator, "Unknown creator", StringComparison.OrdinalIgnoreCase) ? "Unknown creator" : skin.Creator;

    public void SetSelected(bool selected)
    {
        BackgroundColour = selected ? AimModPalette.PanelHover : AimModPalette.PanelRaised;
        BorderColour = selected ? AimModPalette.Accent : AimModPalette.Border;
        BorderThickness = selected ? 2 : 1;
        selectionBar.Alpha = selected ? 1 : 0;
    }

    /// <summary>Switches between the thumbnail card and a one-line row for scanning long libraries.</summary>
    public void SetCompact(bool value)
    {
        compact = value;
        if (compact)
        {
            Height = RowHeight;
            thumbnail.RelativeSizeAxes = Axes.Y;
            thumbnail.Width = row_thumbnail_width;
            thumbnail.Height = 1;
            titleRow.Position = new(row_thumbnail_width + 14, 10);
            meta.Position = new(row_thumbnail_width + 14, 34);
            badges.Anchor = badges.Origin = Anchor.CentreRight;
        }
        else
        {
            Height = CardHeight;
            thumbnail.RelativeSizeAxes = Axes.X;
            thumbnail.Width = 1;
            thumbnail.Height = ThumbnailHeight;
            titleRow.Position = new(12, ThumbnailHeight + 8);
            meta.Position = new(12, ThumbnailHeight + 31);
            badges.Anchor = badges.Origin = Anchor.TopLeft;
        }
        widthTracker.Reset();
    }

    protected override void Update()
    {
        base.Update();
        if (!widthTracker.Update((DrawWidth, compact, tag?.DrawWidth ?? 0, badges.DrawWidth)))
            return;
        float left = compact ? row_thumbnail_width + 14 : 12;
        float right = compact ? badges.DrawWidth + 16 : 12;
        float textWidth = Math.Max(40, DrawWidth - left - right);
        name.MaxWidth = Math.Max(30, textWidth - (tag is null ? 0 : tag.DrawWidth + titleRow.Spacing.X));
        meta.MaxWidth = textWidth;
    }
}

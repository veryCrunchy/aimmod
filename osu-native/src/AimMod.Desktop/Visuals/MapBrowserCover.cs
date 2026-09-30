using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;

namespace AimMod.Desktop.Visuals;

/// <summary>
/// Beatmap cover that renders a difficulty-tinted placeholder immediately and replaces it
/// with the local background or online cover once that loads off the update thread.
/// Inside a rounded parent, pass the parent's radius: a nested mask replaces the outer
/// rounded mask, so a square inner mask would expose the parent's corners.
/// </summary>
public partial class MapBrowserCover : CompositeDrawable
{
    private readonly string? localPath;
    private readonly Uri? onlineUri;
    private readonly bool fullResolution;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SpriteIcon placeholderIcon;

    public MapBrowserCover(string? localPath, Uri? onlineUri, double starRating, bool fullResolution = false, bool showPlaceholderIcon = true, float cornerRadius = AimModVisualStyle.ControlRadius)
    {
        this.localPath = localPath;
        this.onlineUri = onlineUri;
        this.fullResolution = fullResolution;
        Masking = true;
        CornerRadius = cornerRadius;
        Colour4 tint = AimModVisualStyle.DifficultyColour(starRating);
        InternalChildren = new Drawable[]
        {
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = ColourInfo.GradientVertical(tint.Darken(1.4f), AimModPalette.PanelRaised),
            },
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = ColourInfo.GradientHorizontal(tint.Opacity(0.35f), tint.Opacity(0)),
            },
            placeholderIcon = new SpriteIcon
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Icon = FontAwesome.Solid.Music,
                Size = new(14),
                Colour = AimModPalette.Text,
                Alpha = showPlaceholderIcon ? 0.35f : 0,
            },
        };
    }

    /// <summary>True once a real cover texture replaced the placeholder.</summary>
    public bool HasArtwork { get; private set; }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        if (!string.IsNullOrWhiteSpace(localPath) && Path.IsPathFullyQualified(localPath))
        {
            LoadComponentAsync(new AimModLocalArtwork(localPath, !fullResolution), artwork =>
            {
                if (lifetime.IsCancellationRequested || artwork.Texture is null)
                    return;
                show(artwork);
            }, lifetime.Token);
        }
        else if (onlineUri is not null)
        {
            LoadComponentAsync(new AimModOnlineArtwork(onlineUri), artwork =>
            {
                if (lifetime.IsCancellationRequested || artwork.Texture is null)
                    return;
                show(artwork);
            }, lifetime.Token);
        }
    }

    private void show(Sprite artwork)
    {
        HasArtwork = true;
        placeholderIcon.Hide();
        AddInternal(artwork);
        artwork.FadeInFromZero(AimModVisualStyle.HoverTransition);
    }

    protected override void Dispose(bool isDisposing)
    {
        lifetime.Cancel();
        lifetime.Dispose();
        base.Dispose(isDisposing);
    }
}

using AimMod.Desktop.Skins.Online;
using AimMod.Desktop.Visuals;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Skins;

public partial class SkinScreenshot(SkinScreenshotCache? cache, Uri uri, bool fit = false) : CompositeDrawable
{
    private readonly CancellationTokenSource lifetime = new();
    protected override void LoadComplete()
    {
        base.LoadComplete();
        InternalChild = label("Loading image...");
        _ = loadImage();
    }

    private async Task loadImage()
    {
        try
        {
            if (cache is null) throw new InvalidOperationException();
            string path = await cache.GetAsync(uri, lifetime.Token).ConfigureAwait(false);
            if (lifetime.IsCancellationRequested) return;
            Schedule(() =>
            {
                if (IsDisposed || lifetime.IsCancellationRequested) return;
                LoadComponentAsync(new AimModLocalArtwork(path, cropForPanel: false)
                {
                    FillMode = fit ? FillMode.Fit : FillMode.Fill,
                }, artwork =>
                {
                    if (!IsDisposed && !lifetime.IsCancellationRequested)
                        InternalChild = artwork.Texture is null ? label("Image unavailable") : artwork;
                }, lifetime.Token);
            });
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!IsDisposed) Schedule(() => { if (!IsDisposed) InternalChild = label("Image unavailable"); });
        }
    }

    private static TruncatingSpriteText label(string text) => new()
    {
        Text = text, Anchor = Anchor.Centre, Origin = Anchor.Centre,
        Font = new FontUsage(size: 11), Colour = AimModPalette.Muted, MaxWidth = 130,
    };

    protected override void Update()
    {
        base.Update();
        if (InternalChild is TruncatingSpriteText status)
            status.MaxWidth = Math.Max(1, DrawWidth - 8);
    }

    protected override void Dispose(bool isDisposing)
    {
        lifetime.Cancel();
        // In-flight work may still observe the token while decoding or scheduling.
        base.Dispose(isDisposing);
    }
}

using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;

namespace AimMod.Desktop.Visuals;

[LongRunningLoad]
public partial class AimModOnlineArtwork : Sprite
{
    private readonly Uri? uri;

    public AimModOnlineArtwork(Uri? uri)
    {
        this.uri = uri;
        RelativeSizeAxes = Axes.Both;
        FillMode = FillMode.Fill;
        Anchor = Anchor.Centre;
        Origin = Anchor.Centre;
        Alpha = 0;
    }

    [BackgroundDependencyLoader]
    private void load(LargeTextureStore textures)
    {
        if (uri is null || uri.Scheme != Uri.UriSchemeHttps)
            return;

        Texture = textures.Get(uri.AbsoluteUri);
        Alpha = Texture is null ? 0 : 1;
    }
}

/// <summary>
/// Lets the surrounding card render immediately while its remote art is loaded
/// independently on osu's long-running loader.
/// </summary>
public partial class AimModOnlineArtworkHost : CompositeDrawable
{
    private readonly Uri? uri;
    private readonly CancellationTokenSource lifetime = new();

    public AimModOnlineArtworkHost(Uri? uri)
    {
        this.uri = uri;
        RelativeSizeAxes = Axes.Both;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        if (uri is null)
            return;

        LoadComponentAsync(new AimModOnlineArtwork(uri), artwork =>
        {
            if (!lifetime.IsCancellationRequested)
                InternalChild = artwork;
        }, lifetime.Token);
    }

    protected override void Dispose(bool isDisposing)
    {
        lifetime.Cancel();
        lifetime.Dispose();
        base.Dispose(isDisposing);
    }
}

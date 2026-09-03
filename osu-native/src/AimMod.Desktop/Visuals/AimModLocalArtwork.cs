using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Framework.Platform;
using osu.Game.Beatmaps;

namespace AimMod.Desktop.Visuals;

/// <summary>
/// Loads a single verified file from lazer's hashed store and applies the same
/// narrow-panel crop used by osu!lazer's beatmap cards.
/// </summary>
public partial class AimModLocalArtwork : Sprite
{
    private readonly string path;
    private TextureStore? textures;

    public AimModLocalArtwork(string path)
    {
        this.path = path;
        RelativeSizeAxes = Axes.Both;
        FillMode = FillMode.Fill;
        Anchor = Anchor.Centre;
        Origin = Anchor.Centre;
        Alpha = 0;
    }

    [BackgroundDependencyLoader]
    private void load(GameHost host)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path))
            return;

        var fileStore = new SingleFileResourceStore(path);
        textures = new TextureStore(
            host.Renderer,
            new BeatmapPanelBackgroundTextureLoaderStore(host.CreateTextureLoaderStore(fileStore)),
            useAtlas: false);
        Texture = textures.Get(path);
        Alpha = Texture is null ? 0 : 1;
    }

    protected override void Dispose(bool isDisposing)
    {
        textures?.Dispose();
        base.Dispose(isDisposing);
    }

    private sealed class SingleFileResourceStore(string permittedPath) : IResourceStore<byte[]>
    {
        private readonly string permittedPath = Path.GetFullPath(permittedPath);

        public byte[] Get(string name)
        {
            using Stream? stream = GetStream(name);
            if (stream is null)
                return null!;

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }

        public async Task<byte[]> GetAsync(string name, CancellationToken cancellationToken = default)
        {
            await using Stream? stream = GetStream(name);
            if (stream is null)
                return null!;

            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer.ToArray();
        }

        public Stream? GetStream(string name)
        {
            if (!string.Equals(Path.GetFullPath(name), permittedPath, StringComparison.Ordinal))
                return null;

            return File.Open(permittedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        public IEnumerable<string> GetAvailableResources()
        {
            yield return permittedPath;
        }

        public void Dispose()
        {
        }
    }
}

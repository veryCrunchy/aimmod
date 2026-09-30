using System.Collections.Concurrent;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Framework.Platform;

namespace AimMod.Desktop.Skins;

/// <summary>
/// Shares decoded skin element textures between skin cards and the preview, so each file is read
/// and uploaded once per screen. Only files registered from a skin's verified element list can be read.
/// </summary>
public sealed class SkinTextureCache : IDisposable
{
    private const long maximum_element_bytes = 8 * 1024 * 1024;
    private readonly AllowListStore files = new();
    private readonly TextureStore textures;

    public SkinTextureCache(GameHost host)
        : this(host.Renderer, host.CreateTextureLoaderStore)
    {
    }

    private SkinTextureCache(IRenderer renderer, Func<IResourceStore<byte[]>, IResourceStore<TextureUpload>> loaders)
    {
        textures = new TextureStore(renderer, loaders(files), useAtlas: false);
    }

    /// <summary>Loads an element texture. Safe off the update thread; missing or oversized files return null.</summary>
    public Texture? Get(SkinElementFile? element)
    {
        if (element is null || !files.Permit(element.Path))
            return null;
        try
        {
            return textures.Get(element.Path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    public void Dispose() => textures.Dispose();

    private sealed class AllowListStore : IResourceStore<byte[]>
    {
        private readonly ConcurrentDictionary<string, bool> permitted = new(StringComparer.OrdinalIgnoreCase);

        public bool Permit(string path)
        {
            if (!Path.IsPathFullyQualified(path))
                return false;
            return permitted.GetOrAdd(Path.GetFullPath(path), full =>
            {
                try
                {
                    var info = new FileInfo(full);
                    return info.Exists && info.Length is > 0 and <= maximum_element_bytes && (info.Attributes & FileAttributes.ReparsePoint) == 0;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    return false;
                }
            });
        }

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
            if (!Path.IsPathFullyQualified(name) || !permitted.TryGetValue(Path.GetFullPath(name), out bool allowed) || !allowed)
                return null;
            try
            {
                return File.Open(name, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        public IEnumerable<string> GetAvailableResources() => permitted.Where(entry => entry.Value).Select(entry => entry.Key);

        public void Dispose()
        {
        }
    }
}

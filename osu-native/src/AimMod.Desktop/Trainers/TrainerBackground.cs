using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Textures;
using SixLabors.ImageSharp;

namespace AimMod.Desktop.Trainers;

internal sealed class TrainerBackground(byte[] bytes, IRenderer renderer) : IDisposable
{
    private readonly Lazy<Texture?> texture = new(() =>
    {
        try { using var stream = new MemoryStream(bytes, false); return Texture.FromStream(renderer, stream); }
        catch (Exception error) when (error is UnknownImageFormatException or InvalidImageContentException or NotSupportedException) { return null; }
    });
    public Texture? Texture => texture.Value;
    public void Dispose() { if (texture.IsValueCreated) texture.Value?.Dispose(); }

    public static byte[]? Read(string directory, string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return null;
        try
        {
            string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(Path.Combine(root, filename));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path) || new FileInfo(path).Length > 16 * 1024 * 1024) return null;
            byte[] data = File.ReadAllBytes(path);
            using var stream = new MemoryStream(data, false);
            var info = Image.Identify(stream);
            return info.Width <= 8192 && info.Height <= 8192 && (long)info.Width * info.Height <= 32_000_000 ? data : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or UnknownImageFormatException or InvalidImageContentException or NotSupportedException) { return null; }
    }
}

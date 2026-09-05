using System.Security.Cryptography;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Processing;

namespace AimMod.Desktop.Skins.Online;

public sealed class SkinScreenshotCache(ISecureSkinHttpClient http, string directory)
{
    private static readonly SkinHttpFetchOptions options = new(
        ["cdn.osuskins.net", "skins.osuck.net", "i.imgur.com"],
        ["image/webp", "image/png", "image/jpeg"], 12 * 1024 * 1024, TimeSpan.FromSeconds(20));
    private readonly SemaphoreSlim downloads = new(4, 4);

    public async Task<string> GetAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length > 0
            || !options.AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsupported screenshot location.");
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
        string path = Path.Combine(directory, key + ".png");
        await downloads.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromDays(30)) return path;
            SkinHttpPayload payload = await http.GetBytesAsync(uri, options, cancellationToken).ConfigureAwait(false);
            ImageInfo info = Image.Identify(payload.Bytes);
            if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > 32_000_000)
                throw new InvalidDataException("Screenshot dimensions are too large.");
            using Image image = Image.Load(new DecoderOptions { MaxFrames = 1 }, payload.Bytes);
            if (image.Width > 1920 || image.Height > 1080)
                image.Mutate(context => context.Resize(new ResizeOptions { Size = new Size(1920, 1080), Mode = ResizeMode.Max }));
            Directory.CreateDirectory(directory);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await image.SaveAsPngAsync(temporary, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, path, true);
                foreach (FileInfo old in new DirectoryInfo(directory).EnumerateFiles("*.png").OrderByDescending(file => file.LastWriteTimeUtc).Skip(192))
                    old.Delete();
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return path;
        }
        finally { downloads.Release(); }
    }
}

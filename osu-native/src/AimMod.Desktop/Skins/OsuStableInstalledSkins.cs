using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AimMod.Osu.Runtime.Contracts;
using osu.Game.Database;
using osu.Game.Skinning;

namespace AimMod.Desktop.Skins;

public sealed class OsuStableInstalledSkinSource : IInstalledSkinSource
{
    private readonly string skinsRoot;

    public OsuStableInstalledSkinSource(string skinsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skinsRoot);
        if (!Path.IsPathFullyQualified(skinsRoot))
            throw new ArgumentException("The osu!stable skins path must be absolute.", nameof(skinsRoot));
        this.skinsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(skinsRoot));
    }

    public Task<InstalledLazerSkinPage> SearchAsync(
        string searchText = "",
        int offset = 0,
        int limit = 60,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 100);
        InstalledLazerSkin[] skins = readSkins(cancellationToken)
            .Where(skin => string.IsNullOrWhiteSpace(searchText)
                           || skin.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                           || skin.Creator.Contains(searchText, StringComparison.OrdinalIgnoreCase))
            .OrderBy(skin => skin.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Task.FromResult(new InstalledLazerSkinPage(skins.Skip(offset).Take(limit).ToArray(), skins.Length, offset, limit));
    }

    public Task<InstalledLazerSkin?> GetAsync(Guid skinId, CancellationToken cancellationToken = default) =>
        Task.FromResult(readSkins(cancellationToken).FirstOrDefault(skin => skin.SkinId == skinId));

    private IEnumerable<InstalledLazerSkin> readSkins(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(skinsRoot))
            yield break;

        foreach (string directory in Directory.EnumerateDirectories(skinsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                continue;
            string iniPath = Path.Combine(directory, "skin.ini");
            if (!File.Exists(iniPath))
                continue;

            IReadOnlyDictionary<string, string> metadata = readGeneralMetadata(iniPath);
            string folderName = Path.GetFileName(directory);
            string name = metadata.GetValueOrDefault("Name") ?? folderName;
            string creator = metadata.GetValueOrDefault("Author") ?? "Unknown creator";
            string preview = findPreview(directory);
            string contentIdentity = $"{folderName}:{File.GetLastWriteTimeUtc(iniPath).Ticks}:{new FileInfo(iniPath).Length}";
            Guid id = stableGuid(contentIdentity);
            int fileCount = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Take(8_193).Count();
            var summary = new ExternalLazerSkinSummary(id, name, creator, contentIdentity, false, fileCount);
            yield return new InstalledLazerSkin(summary, preview, InstalledSkinOrigin.Stable, directory);
        }
    }

    private static IReadOnlyDictionary<string, string> readGeneralMetadata(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool general = false;
        foreach (string raw in File.ReadLines(path).Take(2_000))
        {
            string line = raw.Trim();
            if (line.StartsWith('['))
            {
                general = string.Equals(line, "[General]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!general || line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) || !line.Contains(':'))
                continue;
            string[] pair = line.Split(':', 2);
            values[pair[0].Trim()] = pair[1].Trim();
        }
        return values;
    }

    private static string findPreview(string directory)
    {
        string[] preferred = ["menu-background.jpg", "menu-background.png", "ranking-panel.jpg", "ranking-panel.png", "hitcircle.png"];
        return preferred.Select(name => Path.Combine(directory, name)).FirstOrDefault(File.Exists) ?? string.Empty;
    }

    private static Guid stableGuid(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("osu-stable-skin:" + value.ToLowerInvariant()));
        return new Guid(hash.AsSpan(0, 16));
    }
}

public sealed class CompositeInstalledSkinSource : IInstalledSkinSource
{
    private readonly IReadOnlyList<IInstalledSkinSource> sources;

    public CompositeInstalledSkinSource(params IInstalledSkinSource[] sources)
    {
        this.sources = sources.Where(source => source is not null).Distinct().ToArray();
    }

    public async Task<InstalledLazerSkinPage> SearchAsync(string searchText = "", int offset = 0, int limit = 60, CancellationToken cancellationToken = default)
    {
        InstalledLazerSkinPage[] pages = await Task.WhenAll(sources.Select(source =>
            source.SearchAsync(searchText, 0, 100, cancellationToken))).ConfigureAwait(false);
        InstalledLazerSkin[] skins = pages.SelectMany(page => page.Items)
            .GroupBy(skin => $"{skin.Name}\n{skin.Creator}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(skin => skin.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 100);
        return new InstalledLazerSkinPage(skins.Skip(offset).Take(limit).ToArray(), skins.Length, offset, limit);
    }

    public async Task<InstalledLazerSkin?> GetAsync(Guid skinId, CancellationToken cancellationToken = default)
    {
        foreach (IInstalledSkinSource source in sources)
        {
            InstalledLazerSkin? skin = await source.GetAsync(skinId, cancellationToken).ConfigureAwait(false);
            if (skin is not null)
                return skin;
        }
        return null;
    }
}

public sealed class OsuStableSkinApplyService
{
    private readonly SkinManager skinManager;

    public OsuStableSkinApplyService(SkinManager skinManager)
    {
        this.skinManager = skinManager ?? throw new ArgumentNullException(nameof(skinManager));
    }

    public async Task<Guid> PrepareAsync(InstalledLazerSkin skin, CancellationToken cancellationToken = default)
    {
        if (skin.Origin != InstalledSkinOrigin.Stable || !Path.IsPathFullyQualified(skin.SourcePath) || !Directory.Exists(skin.SourcePath))
            throw new ExternalLazerSkinApplyException("stable_skin_unavailable", "This osu!stable skin is no longer available.");

        string temp = Path.Combine(Path.GetTempPath(), $"aimmod-stable-skin-{Guid.NewGuid():N}.osk");
        try
        {
            await Task.Run(() => ZipFile.CreateFromDirectory(skin.SourcePath, temp, CompressionLevel.Fastest, includeBaseDirectory: false), cancellationToken).ConfigureAwait(false);
            Live<SkinInfo> imported = await skinManager.Import(
                new ImportTask(temp),
                new ImportParameters { ImportImmediately = true },
                cancellationToken).ConfigureAwait(false);
            skinManager.Rename(imported, skin.Name);
            return imported.ID;
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

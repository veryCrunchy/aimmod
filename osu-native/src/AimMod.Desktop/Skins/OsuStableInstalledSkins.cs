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

    // Scanning every skin folder is disk-bound; keep it off the caller's (often the UI) thread.
    public Task<InstalledLazerSkinPage> SearchAsync(
        string searchText = "",
        int offset = 0,
        int limit = 60,
        CancellationToken cancellationToken = default) => Task.Run(() =>
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
        return new InstalledLazerSkinPage(skins.Skip(offset).Take(limit).ToArray(), skins.Length, offset, limit);
    }, cancellationToken);

    public Task<InstalledLazerSkin?> GetAsync(Guid skinId, CancellationToken cancellationToken = default) =>
        Task.Run(() => readSkins(cancellationToken).FirstOrDefault(skin => skin.SkinId == skinId), cancellationToken);

    private IEnumerable<InstalledLazerSkin> readSkins(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(skinsRoot))
            yield break;

        foreach (string directory in Directory.EnumerateDirectories(skinsRoot, "*", new EnumerationOptions { IgnoreInaccessible = true }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tryReadSkin(directory) is { } skin)
                yield return skin;
        }
    }

    // One locked skin.ini or protected folder must not hide every other skin.
    private static InstalledLazerSkin? tryReadSkin(string directory)
    {
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                return null;
            string iniPath = Path.Combine(directory, "skin.ini");
            if (!File.Exists(iniPath))
                return null;

            IReadOnlyDictionary<string, string> metadata = readGeneralMetadata(iniPath);
            string folderName = Path.GetFileName(directory);
            string name = metadata.GetValueOrDefault("Name") ?? folderName;
            string creator = metadata.GetValueOrDefault("Author") ?? "Unknown creator";
            string preview = findPreview(directory);
            string contentIdentity = $"{folderName}:{File.GetLastWriteTimeUtc(iniPath).Ticks}:{new FileInfo(iniPath).Length}";
            Guid id = stableGuid(contentIdentity);
            int fileCount = 0;
            var elements = new List<(int Priority, string Name, string Path)>();
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint };
            foreach (string file in Directory.EnumerateFiles(directory, "*", options).Take(8_193))
            {
                fileCount++;
                string logicalName = Path.GetRelativePath(directory, file).Replace('\\', '/');
                int priority = ExternalLazerSkinProtocol.PreviewElementPriority(logicalName);
                if (priority >= 0)
                    elements.Add((priority, logicalName, file));
            }
            var files = elements
                .OrderBy(element => element.Priority)
                .ThenBy(element => element.Name, StringComparer.OrdinalIgnoreCase)
                .Take(ExternalLazerSkinProtocol.MaximumPreviewFilesPerSkin)
                .GroupBy(element => element.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Path, StringComparer.OrdinalIgnoreCase);
            var summary = new ExternalLazerSkinSummary(id, name, creator, contentIdentity, false, fileCount);
            return new InstalledLazerSkin(summary, preview, InstalledSkinOrigin.Stable, directory)
            {
                ElementFiles = files,
                AddedAt = Directory.GetCreationTimeUtc(directory),
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
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

    private const int source_page_size = 100;
    private const int maximum_skins_per_source = 5_000;

    public async Task<InstalledLazerSkinPage> SearchAsync(string searchText = "", int offset = 0, int limit = 60, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<InstalledLazerSkin>[] perSource = await Task.WhenAll(sources.Select(source =>
            readAllAsync(source, searchText, cancellationToken))).ConfigureAwait(false);
        InstalledLazerSkin[] skins = perSource.SelectMany(items => items)
            .GroupBy(skin => $"{skin.Name}\n{skin.Creator}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(skin => skin.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 100);
        return new InstalledLazerSkinPage(skins.Skip(offset).Take(limit).ToArray(), skins.Length, offset, limit);
    }

    // Sources cap each page, so merging only their first page would hide skins beyond it.
    private static async Task<IReadOnlyList<InstalledLazerSkin>> readAllAsync(IInstalledSkinSource source, string searchText, CancellationToken cancellationToken)
    {
        var items = new List<InstalledLazerSkin>();
        while (items.Count < maximum_skins_per_source)
        {
            InstalledLazerSkinPage page = await source.SearchAsync(searchText, items.Count, source_page_size, cancellationToken).ConfigureAwait(false);
            items.AddRange(page.Items);
            if (page.Items.Count == 0 || !page.HasMore)
                break;
        }
        return items;
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
            await Task.Run(() => createArchive(skin.SourcePath, temp, cancellationToken), cancellationToken).ConfigureAwait(false);
            Live<SkinInfo>? imported = await skinManager.Import(
                new ImportTask(temp),
                new ImportParameters { ImportImmediately = true },
                cancellationToken).ConfigureAwait(false);
            if (imported is null)
                throw new ExternalLazerSkinApplyException("skin_import_failed", "AimMod could not import this osu!stable skin into its embedded player.");
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

    // Unlike ZipFile.CreateFromDirectory this skips linked files, is bounded and can be cancelled.
    // Directory enumeration already does not recurse into junctions or symbolic links.
    private static void createArchive(string sourceDirectory, string archivePath, CancellationToken cancellationToken)
    {
        const long maximum_bytes = 1024L * 1024 * 1024;
        long total = 0;
        using ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (string file in Directory.EnumerateFiles(sourceDirectory, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += new FileInfo(file).Length;
            if (total > maximum_bytes)
                throw new ExternalLazerSkinApplyException("stable_skin_too_large", "This osu!stable skin is too large to prepare.");
            archive.CreateEntryFromFile(file, Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/'), CompressionLevel.Fastest);
        }
    }
}

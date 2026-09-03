using System.IO.Compression;
using System.Text.Json;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using osu.Game.Database;
using osu.Game.Skinning;

namespace AimMod.Desktop.Skins;

public sealed record InstalledLazerSkin(ExternalLazerSkinSummary Summary, string PreviewPath)
{
    public Guid SkinId => Summary.SkinId;
    public string Name => Summary.Name;
    public string Creator => Summary.Creator;
    public bool IsBuiltIn => Summary.IsBuiltIn;
}

public sealed record InstalledLazerSkinPage(IReadOnlyList<InstalledLazerSkin> Items, int Total, int Offset, int Limit)
{
    public bool HasMore => Offset + Items.Count < Total;
}

public sealed class ExternalLazerInstalledSkinSource
{
    private readonly string libraryRoot;
    private readonly Func<ExternalLazerSkinCatalogSearchRequest, CancellationToken, Task<ExternalLazerSkinCatalogSearchResult>> search;

    public ExternalLazerInstalledSkinSource(string libraryRoot)
        : this(libraryRoot, searchWithPrivateWorker)
    {
    }

    internal ExternalLazerInstalledSkinSource(
        string libraryRoot,
        Func<ExternalLazerSkinCatalogSearchRequest, CancellationToken, Task<ExternalLazerSkinCatalogSearchResult>> search)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        if (!Path.IsPathFullyQualified(libraryRoot))
            throw new ArgumentException("The lazer library root must be absolute.", nameof(libraryRoot));

        this.libraryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
        this.search = search ?? throw new ArgumentNullException(nameof(search));
    }

    public async Task<InstalledLazerSkinPage> SearchAsync(
        string searchText = "",
        int offset = 0,
        int limit = 60,
        CancellationToken cancellationToken = default)
    {
        var request = new ExternalLazerSkinCatalogSearchRequest(libraryRoot, searchText, offset, limit);
        ExternalLazerSkinCatalogSearchResult result = await search(request, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<Guid, string> previews = resolvePreviewPaths(result.Skins);
        return new InstalledLazerSkinPage(
            result.Skins.Select(skin => new InstalledLazerSkin(skin, previews.GetValueOrDefault(skin.SkinId) ?? string.Empty)).ToArray(),
            result.Total,
            result.Offset,
            result.Limit);
    }

    public async Task<InstalledLazerSkin?> GetAsync(Guid skinId, CancellationToken cancellationToken = default)
    {
        if (skinId == Guid.Empty)
            return null;
        var request = new ExternalLazerSkinCatalogSearchRequest(libraryRoot, Limit: 1, SkinId: skinId);
        ExternalLazerSkinCatalogSearchResult result = await search(request, cancellationToken).ConfigureAwait(false);
        ExternalLazerSkinSummary? skin = result.Skins.SingleOrDefault();
        if (skin is null)
            return null;
        string preview = resolvePreviewPaths(new[] { skin }).GetValueOrDefault(skin.SkinId) ?? string.Empty;
        return new InstalledLazerSkin(skin, preview);
    }

    private IReadOnlyDictionary<Guid, string> resolvePreviewPaths(IEnumerable<ExternalLazerSkinSummary> skins)
    {
        LazerStoredFileReference[] references = skins
            .Where(skin => skin.PreviewHash.Length == 64)
            .Select(skin => new LazerStoredFileReference(
                LazerLibraryAssetKind.Skin,
                skin.SkinId.ToString("D"),
                skin.PreviewLogicalName,
                skin.PreviewHash))
            .ToArray();
        if (references.Length == 0)
            return new Dictionary<Guid, string>();

        try
        {
            return new LazerHashedFileResolver()
                   .Resolve(Path.Combine(libraryRoot, "files"), references)
                   .Where(file => file.SourcePath is not null && Guid.TryParse(file.Reference.OwnerId, out _))
                   .ToDictionary(file => Guid.Parse(file.Reference.OwnerId), file => file.SourcePath!);
        }
        catch (ExternalLazerLibraryException)
        {
            return new Dictionary<Guid, string>();
        }
    }

    private static async Task<ExternalLazerSkinCatalogSearchResult> searchWithPrivateWorker(
        ExternalLazerSkinCatalogSearchRequest request,
        CancellationToken cancellationToken)
    {
        await using SidecarRuntimeClient runtime = SidecarRuntimeClient.Start();
        var client = new ExternalLazerSkinCatalogClient(new SidecarRuntimeRequestClient(runtime));
        return await client.SearchAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class ExternalLazerSkinApplyService
{
    private const string package_prefix = "aimmod-skin-package-";

    private readonly string libraryRoot;
    private readonly SkinManager skinManager;
    private readonly ExternalSkinMappingStore mappings;
    private readonly Func<string, IReadOnlyList<string>, IReadOnlyList<Guid>, IReadOnlyList<Guid>, CancellationToken, Task<ExternalLazerAssetStagingLease>> stageAssets;

    public ExternalLazerSkinApplyService(string libraryRoot, SkinManager skinManager, string mappingPath)
        : this(libraryRoot, skinManager, new ExternalSkinMappingStore(mappingPath), stageWithPrivateWorker)
    {
    }

    internal ExternalLazerSkinApplyService(
        string libraryRoot,
        SkinManager skinManager,
        ExternalSkinMappingStore mappings,
        Func<string, IReadOnlyList<string>, IReadOnlyList<Guid>, IReadOnlyList<Guid>, CancellationToken, Task<ExternalLazerAssetStagingLease>> stageAssets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        if (!Path.IsPathFullyQualified(libraryRoot))
            throw new ArgumentException("The lazer library root must be absolute.", nameof(libraryRoot));

        this.libraryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
        this.skinManager = skinManager ?? throw new ArgumentNullException(nameof(skinManager));
        this.mappings = mappings ?? throw new ArgumentNullException(nameof(mappings));
        this.stageAssets = stageAssets ?? throw new ArgumentNullException(nameof(stageAssets));
    }

    public async Task<Guid> PrepareAsync(InstalledLazerSkin selected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ExternalLazerSkinSummary skin = selected.Summary;

        if (skin.IsBuiltIn)
        {
            if (skinManager.Query(candidate => candidate.ID == skin.SkinId) is null)
                throw new ExternalLazerSkinApplyException("skin_unavailable", "This built-in lazer skin is not available in AimMod's pinned osu runtime.");
            return skin.SkinId;
        }

        ExternalSkinMapping? mapping = mappings.Load().GetValueOrDefault(skin.SkinId);
        if (mapping is not null
            && string.Equals(mapping.ContentHash, skin.ContentHash, StringComparison.OrdinalIgnoreCase)
            && skinManager.Query(candidate => candidate.ID == mapping.LocalSkinId) is not null)
        {
            return mapping.LocalSkinId;
        }

        await using ExternalLazerAssetStagingLease lease = await stageAssets(
            libraryRoot,
            Array.Empty<string>(),
            Array.Empty<Guid>(),
            new[] { skin.SkinId },
            cancellationToken).ConfigureAwait(false);

        IReadOnlyList<Guid> missingSkins = lease.Result.MissingSkins ?? Array.Empty<Guid>();
        if (missingSkins.Contains(skin.SkinId))
            throw new ExternalLazerSkinApplyException("skin_missing", "The selected skin is no longer present in lazer's library.");
        if (lease.Result.MissingFiles.Any(file => file.Kind == "Skin" && Guid.TryParse(file.OwnerId, out Guid owner) && owner == skin.SkinId))
            throw new ExternalLazerSkinApplyException("skin_file_missing", "One or more files belonging to this skin are missing from lazer storage.");

        ExternalLazerResolvedAsset[] files = lease.Result.Files
            .Where(file => file.Kind == "Skin" && Guid.TryParse(file.OwnerId, out Guid owner) && owner == skin.SkinId)
            .ToArray();
        if (files.Length == 0 || files.Length != skin.FileCount)
            throw new ExternalLazerSkinApplyException("skin_files_incomplete", "AimMod could not stage every file belonging to this skin.");

        string packageDirectory = Directory.CreateTempSubdirectory(package_prefix).FullName;
        string archivePath = Path.Combine(packageDirectory, "skin.osk");
        try
        {
            await createArchiveAsync(archivePath, files, cancellationToken).ConfigureAwait(false);
            Live<SkinInfo> imported = await skinManager.Import(
                new ImportTask(archivePath),
                new ImportParameters { ImportImmediately = true },
                cancellationToken).ConfigureAwait(false);
            skinManager.Rename(imported, skin.Name);

            var next = new ExternalSkinMapping(skin.SkinId, skin.ContentHash, imported.ID);
            await mappings.SaveAsync(next, cancellationToken).ConfigureAwait(false);
            return imported.ID;
        }
        catch (ExternalLazerSkinApplyException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new ExternalLazerSkinApplyException("skin_import_failed", "AimMod could not prepare this lazer skin for its embedded player.", exception);
        }
        finally
        {
            deleteOwnedPackageDirectory(packageDirectory);
        }
    }

    private static async Task createArchiveAsync(
        string archivePath,
        IReadOnlyList<ExternalLazerResolvedAsset> files,
        CancellationToken cancellationToken)
    {
        await using var archiveStream = new FileStream(
            archivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true);
        var logicalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ExternalLazerResolvedAsset file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string logicalName = normaliseLogicalName(file.LogicalName);
            if (!logicalNames.Add(logicalName))
                throw new ExternalLazerSkinApplyException("skin_file_duplicate", "The selected skin contains duplicate logical filenames.");

            ZipArchiveEntry entry = archive.CreateEntry(logicalName, CompressionLevel.Fastest);
            await using Stream destination = entry.Open();
            await using var source = new FileStream(
                file.StagedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string normaliseLogicalName(string name)
    {
        string normalised = name.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalised)
            || Path.IsPathFullyQualified(normalised)
            || normalised.Split('/').Any(component => component.Length == 0 || component is "." or ".."))
        {
            throw new ExternalLazerSkinApplyException("skin_filename_invalid", "The selected skin contains an unsafe filename.");
        }

        return normalised;
    }

    private static void deleteOwnedPackageDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Path.GetFileName(fullPath).StartsWith(package_prefix, StringComparison.Ordinal)
            || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ExternalLazerSkinApplyException("skin_cleanup_failed", "AimMod refused to clean an unrecognised skin package directory.");
        }

        foreach (string file in Directory.EnumerateFiles(fullPath))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new ExternalLazerSkinApplyException("skin_cleanup_failed", "AimMod refused to clean a linked skin package file.");
            File.Delete(file);
        }
        Directory.Delete(fullPath, recursive: false);
    }

    private static async Task<ExternalLazerAssetStagingLease> stageWithPrivateWorker(
        string libraryRoot,
        IReadOnlyList<string> beatmaps,
        IReadOnlyList<Guid> scores,
        IReadOnlyList<Guid> skins,
        CancellationToken cancellationToken)
    {
        await using SidecarRuntimeClient runtime = SidecarRuntimeClient.Start();
        var client = new ExternalLazerAssetClient(new SidecarRuntimeRequestClient(runtime));
        return await client.ResolveToPrivateStagingAsync(libraryRoot, beatmaps, scores, skins, cancellationToken).ConfigureAwait(false);
    }
}

public sealed record ExternalSkinMapping(Guid ExternalSkinId, string ContentHash, Guid LocalSkinId);

public sealed class ExternalSkinMappingStore
{
    private const int maximum_mappings = 256;
    private readonly string path;

    public ExternalSkinMappingStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("The external skin mapping path must be absolute.", nameof(path));
        this.path = Path.GetFullPath(path);
    }

    public IReadOnlyDictionary<Guid, ExternalSkinMapping> Load()
    {
        if (!File.Exists(path))
            return new Dictionary<Guid, ExternalSkinMapping>();

        try
        {
            using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            ExternalSkinMapping[] entries = JsonSerializer.Deserialize<ExternalSkinMapping[]>(stream, RuntimeProtocol.JsonOptions) ?? [];
            return entries
                   .Where(valid)
                   .Take(maximum_mappings)
                   .GroupBy(entry => entry.ExternalSkinId)
                   .ToDictionary(group => group.Key, group => group.Last());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<Guid, ExternalSkinMapping>();
        }
    }

    public async Task SaveAsync(ExternalSkinMapping mapping, CancellationToken cancellationToken = default)
    {
        if (!valid(mapping))
            throw new ArgumentException("The external skin mapping is invalid.", nameof(mapping));

        var entries = Load().Values.Where(entry => entry.ExternalSkinId != mapping.ExternalSkinId).Append(mapping).TakeLast(maximum_mappings).ToArray();
        string? directory = Path.GetDirectoryName(path);
        if (directory is not null)
            Directory.CreateDirectory(directory);

        string temporary = path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, entries, RuntimeProtocol.JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static bool valid(ExternalSkinMapping mapping) =>
        mapping.ExternalSkinId != Guid.Empty
        && mapping.LocalSkinId != Guid.Empty
        && mapping.ContentHash is { } hash
        && (hash.Length == 0 || hash.Length == 64 && hash.All(Uri.IsHexDigit));
}

public sealed class ExternalLazerSkinApplyException(string code, string message, Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
}

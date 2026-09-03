using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Osu.Runtime;

public enum LazerLibraryAssetKind
{
    Beatmap,
    Audio,
    Background,
    Replay,
    Skin,
}

public sealed record ExternalLazerLibraryLocation(string LibraryRoot, string SnapshotDirectory);

public sealed record ValidatedExternalLazerLibraryLocation(
    string LibraryRoot,
    string DatabasePath,
    string FilesRoot,
    string SnapshotDirectory);

public sealed record LazerLibrarySnapshot(
    Guid SnapshotId,
    string DatabasePath,
    string FilesRoot,
    DateTimeOffset CreatedAt);

public sealed record LazerLibraryAssetQuery(
    IReadOnlyList<string> BeatmapHashes,
    IReadOnlyList<Guid> ScoreIds,
    IReadOnlyList<Guid>? SkinIds = null);

public sealed record LazerStoredFileReference(
    LazerLibraryAssetKind Kind,
    string OwnerId,
    string LogicalName,
    string Sha256Hash);

public sealed record LazerLibraryAssetManifest(
    IReadOnlyList<LazerStoredFileReference> Files,
    IReadOnlyList<string> MissingBeatmaps,
    IReadOnlyList<Guid> MissingScores,
    IReadOnlyList<Guid>? MissingSkins = null);

public sealed record ResolvedLazerStoredFile(
    LazerStoredFileReference Reference,
    string? SourcePath,
    long? Length)
{
    public bool Exists => SourcePath is not null;
}

public sealed record ResolvedLazerLibraryAssets(
    LazerLibrarySnapshot Snapshot,
    IReadOnlyList<ResolvedLazerStoredFile> Files,
    IReadOnlyList<string> MissingBeatmaps,
    IReadOnlyList<Guid> MissingScores,
    IReadOnlyList<Guid>? MissingSkins = null);

/// <summary>
/// Owns the private Realm snapshot backing a resolved asset manifest. Callers
/// must keep the lease alive while copying assets and dispose it afterwards.
/// Disposal is idempotent and removes the snapshot.
/// </summary>
public sealed class ExternalLazerLibraryAssetLease : IAsyncDisposable
{
    private ILazerLibrarySnapshotFactory? snapshotFactory;

    internal ExternalLazerLibraryAssetLease(
        ResolvedLazerLibraryAssets assets,
        ILazerLibrarySnapshotFactory snapshotFactory)
    {
        Assets = assets;
        this.snapshotFactory = snapshotFactory;
    }

    public ResolvedLazerLibraryAssets Assets { get; }

    public async ValueTask DisposeAsync()
    {
        ILazerLibrarySnapshotFactory? owner = Interlocked.Exchange(ref snapshotFactory, null);
        if (owner is null)
            return;

        try
        {
            await owner.DeleteSnapshotAsync(Assets.Snapshot).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.CompareExchange(ref snapshotFactory, owner, null);
            throw;
        }
    }
}

public interface ILazerLibrarySnapshotFactory
{
    Task<LazerLibrarySnapshot> CreateSnapshotAsync(
        ValidatedExternalLazerLibraryLocation location,
        CancellationToken cancellationToken = default);

    ValueTask DeleteSnapshotAsync(LazerLibrarySnapshot snapshot);
}

public interface ILazerLibraryManifestReader
{
    Task<LazerLibraryAssetManifest> ReadManifestAsync(
        LazerLibrarySnapshot snapshot,
        LazerLibraryAssetQuery query,
        CancellationToken cancellationToken = default);
}

public interface ILazerLibraryCatalogReader
{
    Task<ExternalLazerCatalogSearchResult> ReadCatalogAsync(
        LazerLibrarySnapshot snapshot,
        ExternalLazerCatalogSearchRequest query,
        CancellationToken cancellationToken = default);
}

public interface ILazerSkinCatalogReader
{
    Task<ExternalLazerSkinCatalogSearchResult> ReadCatalogAsync(
        LazerLibrarySnapshot snapshot,
        ExternalLazerSkinCatalogSearchRequest query,
        CancellationToken cancellationToken = default);
}

public sealed class ExternalLazerLibraryValidator
{
    public ValidatedExternalLazerLibraryLocation Validate(ExternalLazerLibraryLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);

        string libraryRoot = validateDirectory(location.LibraryRoot, "The lazer library root must be an existing absolute directory.");
        string snapshotDirectory = validateDirectory(location.SnapshotDirectory, "The snapshot directory must be an existing absolute directory.");
        string databasePath = Path.Combine(libraryRoot, "client.realm");
        string filesRoot = Path.Combine(libraryRoot, "files");

        if (!File.Exists(databasePath))
            throw new ExternalLazerLibraryException("database_not_found", "The detected lazer library does not contain client.realm.");
        if (!Directory.Exists(filesRoot))
            throw new ExternalLazerLibraryException("file_store_not_found", "The detected lazer library does not contain its files directory.");
        if (isInside(snapshotDirectory, libraryRoot))
            throw new ExternalLazerLibraryException("snapshot_location_invalid", "AimMod snapshots must be stored outside the lazer library.");

        rejectReparsePoint(databasePath, "The lazer database cannot be a symbolic link.");
        rejectReparsePoint(filesRoot, "The lazer file store cannot be a symbolic link.");
        rejectReparsePoint(snapshotDirectory, "The snapshot directory cannot be a symbolic link.");

        return new ValidatedExternalLazerLibraryLocation(libraryRoot, databasePath, filesRoot, snapshotDirectory);
    }

    private static string validateDirectory(string path, string message)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ExternalLazerLibraryException("library_path_invalid", message);

        string fullPath;
        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ExternalLazerLibraryException("library_path_invalid", message);
        }

        if (!Directory.Exists(fullPath))
            throw new ExternalLazerLibraryException("library_path_invalid", message);

        return fullPath;
    }

    private static bool isInside(string candidate, string parent)
    {
        string relative = Path.GetRelativePath(parent, candidate);
        return relative == "."
               || (!Path.IsPathRooted(relative)
                   && relative != ".."
                   && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static void rejectReparsePoint(string path, string message)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ExternalLazerLibraryException("library_path_invalid", message);
    }
}

public sealed class LazerHashedFileResolver
{
    public const int MaximumReferencesPerRequest = 8_192;

    public IReadOnlyList<ResolvedLazerStoredFile> Resolve(
        string filesRoot,
        IReadOnlyList<LazerStoredFileReference> references)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filesRoot);
        ArgumentNullException.ThrowIfNull(references);

        if (!Path.IsPathFullyQualified(filesRoot) || !Directory.Exists(filesRoot))
            throw new ExternalLazerLibraryException("file_store_not_found", "The lazer file store is unavailable.");
        if (references.Count > MaximumReferencesPerRequest)
            throw new ExternalLazerLibraryException("asset_request_too_large", $"A file-resolution request cannot exceed {MaximumReferencesPerRequest} assets.");

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(filesRoot));
        var resolvedByHash = new Dictionary<string, (string? Path, long? Length)>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ResolvedLazerStoredFile>(references.Count);

        foreach (LazerStoredFileReference reference in references)
        {
            string hash = normaliseHash(reference.Sha256Hash);
            if (!resolvedByHash.TryGetValue(hash, out (string? Path, long? Length) resolved))
            {
                resolved = resolveHash(root, hash);
                resolvedByHash.Add(hash, resolved);
            }

            if (resolved.Length == 0 && reference.Kind != LazerLibraryAssetKind.Skin)
                throw new ExternalLazerLibraryException("asset_size_invalid", "A resolved lazer asset is empty.");
            if (resolved.Length is { } length && length > maximumLength(reference.Kind))
                throw new ExternalLazerLibraryException("asset_size_invalid", "A resolved lazer asset exceeds its import limit.");

            result.Add(new ResolvedLazerStoredFile(reference with { Sha256Hash = hash }, resolved.Path, resolved.Length));
        }

        return result;
    }

    private static string normaliseHash(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash)
            || hash.Length != 64
            || hash.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')))
        {
            throw new ExternalLazerLibraryException("asset_hash_invalid", "Lazer file references must contain a 64-character SHA-256 hash.");
        }

        return hash.ToLowerInvariant();
    }

    private static (string? Path, long? Length) resolveHash(string root, string hash)
    {
        string firstDirectory = Path.Combine(root, hash[..1]);
        string secondDirectory = Path.Combine(firstDirectory, hash[..2]);
        string path = Path.Combine(secondDirectory, hash);

        if (!File.Exists(path))
            return (null, null);
        if (isReparsePoint(firstDirectory) || isReparsePoint(secondDirectory) || isReparsePoint(path))
            throw new ExternalLazerLibraryException("asset_path_invalid", "A lazer file-store path contains a symbolic link.");

        return (path, new FileInfo(path).Length);
    }

    private static long maximumLength(LazerLibraryAssetKind kind) => kind switch
    {
        LazerLibraryAssetKind.Beatmap => 16 * 1024 * 1024,
        LazerLibraryAssetKind.Replay => 64 * 1024 * 1024,
        LazerLibraryAssetKind.Background => 128 * 1024 * 1024,
        LazerLibraryAssetKind.Skin => 256 * 1024 * 1024,
        _ => 1024L * 1024 * 1024,
    };

    private static bool isReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}

public sealed class ExternalLazerLibraryImportBridge(
    ILazerLibrarySnapshotFactory snapshotFactory,
    ILazerLibraryManifestReader manifestReader,
    ExternalLazerLibraryValidator validator,
    LazerHashedFileResolver fileResolver)
{
    public async Task<ExternalLazerLibraryAssetLease> ResolveAssetsAsync(
        ExternalLazerLibraryLocation location,
        LazerLibraryAssetQuery query,
        CancellationToken cancellationToken = default)
    {
        ValidatedExternalLazerLibraryLocation validated = validator.Validate(location);
        LazerLibrarySnapshot snapshot = await snapshotFactory.CreateSnapshotAsync(validated, cancellationToken).ConfigureAwait(false);

        try
        {
            LazerLibraryAssetManifest manifest = await manifestReader.ReadManifestAsync(snapshot, query, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ResolvedLazerStoredFile> files = fileResolver.Resolve(snapshot.FilesRoot, manifest.Files);
            var assets = new ResolvedLazerLibraryAssets(snapshot, files, manifest.MissingBeatmaps, manifest.MissingScores, manifest.MissingSkins);
            return new ExternalLazerLibraryAssetLease(assets, snapshotFactory);
        }
        catch
        {
            await snapshotFactory.DeleteSnapshotAsync(snapshot).ConfigureAwait(false);
            throw;
        }
    }

}

public sealed class ExternalLazerLibraryException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

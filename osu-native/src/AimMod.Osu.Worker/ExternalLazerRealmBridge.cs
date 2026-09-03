using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using Realms;

namespace AimMod.Osu.Worker;

/// <summary>
/// Opens the detected lazer Realm read-only and asks Realm to produce a
/// transactionally consistent private copy. AimMod only queries that copy.
/// </summary>
public sealed class RealmLazerLibrarySnapshotFactory : ILazerLibrarySnapshotFactory
{
    // ppy.osu.Game 2026.730.0 pins RealmAccess.schema_version to 51.
    internal const ulong SupportedSchemaVersion = 51;

    private readonly object ownershipLock = new();
    private readonly Dictionary<Guid, OwnedSnapshot> ownedSnapshots = new();

    public Task<LazerLibrarySnapshot> CreateSnapshotAsync(
        ValidatedExternalLazerLibraryLocation location,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => createSnapshot(location, cancellationToken), cancellationToken);

    public ValueTask DeleteSnapshotAsync(LazerLibrarySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (ownershipLock)
        {
            if (!ownedSnapshots.TryGetValue(snapshot.SnapshotId, out OwnedSnapshot? owned)
                || !pathsEqual(snapshot.DatabasePath, owned.DatabasePath))
            {
                throw new ExternalLazerLibraryException("snapshot_path_invalid", "AimMod refused to delete a snapshot not created by this factory.");
            }

            deleteSnapshotArtifacts(snapshot.SnapshotId, owned.DatabasePath, owned.StorageDirectory);
            ownedSnapshots.Remove(snapshot.SnapshotId);
        }

        return ValueTask.CompletedTask;
    }

    private LazerLibrarySnapshot createSnapshot(
        ValidatedExternalLazerLibraryLocation location,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        cancellationToken.ThrowIfCancellationRequested();

        string storageDirectory = canonicalDirectory(location.SnapshotDirectory);
        rejectReparsePoint(storageDirectory);
        setPrivateDirectoryPermissions(storageDirectory);

        Guid snapshotId = Guid.NewGuid();
        string destinationPath = Path.Combine(storageDirectory, $"lazer-{snapshotId:N}.realm");
        if (File.Exists(destinationPath))
            throw new ExternalLazerLibraryException("snapshot_exists", "The generated snapshot path already exists.");

        var sourceConfiguration = new RealmConfiguration(location.DatabasePath)
        {
            IsDynamic = true,
            IsReadOnly = true,
            SchemaVersion = SupportedSchemaVersion,
        };
        var destinationConfiguration = new RealmConfiguration(destinationPath)
        {
            IsDynamic = true,
            SchemaVersion = SupportedSchemaVersion,
        };

        try
        {
            using Realm source = Realm.GetInstance(sourceConfiguration);
            cancellationToken.ThrowIfCancellationRequested();
            source.WriteCopy(destinationConfiguration);
            File.SetAttributes(destinationPath, File.GetAttributes(destinationPath) | FileAttributes.ReadOnly);
            setPrivateFilePermissions(destinationPath, readOnly: true);
            cancellationToken.ThrowIfCancellationRequested();

            var snapshot = new LazerLibrarySnapshot(snapshotId, destinationPath, location.FilesRoot, DateTimeOffset.UtcNow);
            lock (ownershipLock)
                ownedSnapshots.Add(snapshotId, new OwnedSnapshot(destinationPath, storageDirectory));

            return snapshot;
        }
        catch (Exception creationException)
        {
            try
            {
                deleteSnapshotArtifacts(snapshotId, destinationPath, storageDirectory);
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException("Creating the private lazer snapshot failed and its artifacts could not be removed.", creationException, cleanupException);
            }

            throw;
        }
    }

    private static void deleteSnapshotArtifacts(Guid snapshotId, string databasePath, string storageDirectory)
    {
        string path = canonicalFile(databasePath);
        string root = canonicalDirectory(storageDirectory);
        string expectedName = $"lazer-{snapshotId:N}.realm";
        if (!string.Equals(Path.GetFileName(path), expectedName, StringComparison.Ordinal)
            || !pathsEqual(Path.GetDirectoryName(path), root))
        {
            throw new ExternalLazerLibraryException("snapshot_path_invalid", "AimMod refused to delete an unrecognised snapshot path.");
        }

        rejectReparsePoint(root);
        string lockPath = $"{path}.lock";
        string notePath = $"{path}.note";
        string managementDirectory = $"{path}.management";
        rejectReparsePoint(path);
        rejectReparsePoint(lockPath);
        rejectReparsePoint(notePath);
        rejectReparsePoint(managementDirectory);

        bool hasArtifacts = File.Exists(path)
                            || File.Exists(lockPath)
                            || File.Exists(notePath)
                            || Directory.Exists(managementDirectory);
        if (!hasArtifacts)
            return;

        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            setPrivateFilePermissions(path, readOnly: false);
        }

        // Realm owns the snapshot's sidecar layout. Its deletion API removes
        // those exact associated files without AimMod recursively traversing a
        // directory supplied by another process.
        Realm.DeleteRealm(new RealmConfiguration(path)
        {
            IsDynamic = true,
            SchemaVersion = SupportedSchemaVersion,
        });

        // Realm intentionally leaves its exact lock file behind.
        deleteFile(lockPath);
        deleteFile(notePath);
        deleteFile(path);

        // Delete only the now-empty, exact Realm sidecar directory. If Realm
        // left unexpected content behind, fail closed instead of recursing.
        if (Directory.Exists(managementDirectory))
            Directory.Delete(managementDirectory, recursive: false);
    }

    private static void rejectReparsePoint(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path))
            && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ExternalLazerLibraryException("snapshot_path_invalid", "AimMod refused to delete a symbolic-link snapshot artifact.");
        }
    }

    private static void deleteFile(string path)
    {
        if (!File.Exists(path))
            return;

        File.SetAttributes(path, FileAttributes.Normal);
        setPrivateFilePermissions(path, readOnly: false);
        File.Delete(path);
    }

    private static string canonicalDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path))
            throw new ExternalLazerLibraryException("snapshot_path_invalid", "The snapshot storage directory is unavailable.");

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string canonicalFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ExternalLazerLibraryException("snapshot_path_invalid", "AimMod refused to delete an unrecognised snapshot path.");

        return Path.GetFullPath(path);
    }

    private static bool pathsEqual(string? left, string right) =>
        left is not null && string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void setPrivateDirectoryPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void setPrivateFilePermissions(string path, bool readOnly)
    {
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(path, readOnly
            ? UnixFileMode.UserRead
            : UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private sealed record OwnedSnapshot(string DatabasePath, string StorageDirectory);
}

/// <summary>
/// Resolves a bounded selection with one beatmap-table pass and one score-table
/// pass. It never opens or enumerates the hashed file store.
/// </summary>
public sealed class DynamicRealmLazerLibraryManifestReader : ILazerLibraryManifestReader
{
    public Task<LazerLibraryAssetManifest> ReadManifestAsync(
        LazerLibrarySnapshot snapshot,
        LazerLibraryAssetQuery query,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => readManifest(snapshot, query, cancellationToken), cancellationToken);

    private static LazerLibraryAssetManifest readManifest(
        LazerLibrarySnapshot snapshot,
        LazerLibraryAssetQuery query,
        CancellationToken cancellationToken)
    {
        validateSnapshot(snapshot);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.BeatmapHashes);
        ArgumentNullException.ThrowIfNull(query.ScoreIds);
        IReadOnlyList<Guid> skinIds = query.SkinIds ?? Array.Empty<Guid>();

        if (query.BeatmapHashes.Count > ExternalLazerAssetProtocol.MaximumBeatmapSelections
            || query.ScoreIds.Count > ExternalLazerAssetProtocol.MaximumScoreSelections
            || skinIds.Count > ExternalLazerAssetProtocol.MaximumSkinSelections)
            throw new ExternalLazerLibraryException("manifest_request_too_large", "A manifest request exceeds the supported beatmap, score, or skin selection limit.");

        var requestedBeatmaps = query.BeatmapHashes.Select(normaliseBeatmapHash).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingBeatmaps = requestedBeatmaps.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requestedScores = query.ScoreIds.ToHashSet();
        var missingScores = requestedScores.ToHashSet();
        var requestedSkins = skinIds.ToHashSet();
        var missingSkins = requestedSkins.ToHashSet();
        var files = new List<LazerStoredFileReference>();
        var uniqueFiles = new HashSet<(LazerLibraryAssetKind Kind, string OwnerId, string LogicalName, string Hash)>();
        var namedFilesBySet = new Dictionary<Guid, NamedFileIndex>();

        var configuration = new RealmConfiguration(snapshot.DatabasePath)
        {
            IsDynamic = true,
            IsReadOnly = true,
            SchemaVersion = RealmLazerLibrarySnapshotFactory.SupportedSchemaVersion,
        };

        using Realm realm = Realm.GetInstance(configuration);

        if (requestedBeatmaps.Count > 0)
        {
            foreach (IRealmObject beatmap in realm.DynamicApi.All("Beatmap"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string sha256 = get<string>(beatmap, "Hash") ?? string.Empty;
                string md5 = get<string>(beatmap, "MD5Hash") ?? string.Empty;
                string? matchedHash = requestedBeatmaps.Contains(sha256) ? sha256
                    : requestedBeatmaps.Contains(md5) ? md5
                    : null;
                if (matchedHash is null)
                    continue;

                IRealmObjectBase? set = getObject(beatmap, "BeatmapSet");
                if (set is null || get<bool>(set, "DeletePending"))
                    continue;

                string ownerId = matchedHash;
                IRealmObjectBase? metadata = getObject(beatmap, "Metadata");
                Guid setId = get<Guid>(set, "ID");
                if (!namedFilesBySet.TryGetValue(setId, out NamedFileIndex? namedFiles))
                {
                    namedFiles = readNamedFiles(set);
                    namedFilesBySet.Add(setId, namedFiles);
                }

                if (addFile(files, uniqueFiles, LazerLibraryAssetKind.Beatmap, ownerId, namedFiles.LogicalNameByHash.GetValueOrDefault(sha256) ?? "beatmap.osu", sha256))
                    missingBeatmaps.Remove(matchedHash);

                if (metadata is not null)
                {
                    addNamedFile(files, uniqueFiles, namedFiles, LazerLibraryAssetKind.Audio, ownerId, get<string>(metadata, "AudioFile"));
                    addNamedFile(files, uniqueFiles, namedFiles, LazerLibraryAssetKind.Background, ownerId, get<string>(metadata, "BackgroundFile"));
                }

                if (files.Count > LazerHashedFileResolver.MaximumReferencesPerRequest)
                    throw new ExternalLazerLibraryException("manifest_too_large", "The selected lazer assets exceed the manifest limit.");
            }
        }

        if (requestedScores.Count > 0)
        {
            foreach (IRealmObject score in realm.DynamicApi.All("Score"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Guid scoreId = get<Guid>(score, "ID");
                if (!requestedScores.Contains(scoreId) || get<bool>(score, "DeletePending"))
                    continue;

                string ownerId = scoreId.ToString("D");
                bool replayAdded = false;
                foreach (IEmbeddedObject usage in score.DynamicApi.GetList<IEmbeddedObject>("Files"))
                {
                    string logicalName = get<string>(usage, "Filename") ?? string.Empty;
                    if (!logicalName.EndsWith(".osr", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string hash = getObject(usage, "File") is { } stored ? get<string>(stored, "Hash") ?? string.Empty : string.Empty;
                    replayAdded |= addFile(files, uniqueFiles, LazerLibraryAssetKind.Replay, ownerId, logicalName, hash);
                }

                if (replayAdded)
                    missingScores.Remove(scoreId);
            }
        }

        if (requestedSkins.Count > 0)
        {
            foreach (IRealmObject skin in realm.DynamicApi.All("Skin"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Guid skinId = get<Guid>(skin, "ID");
                if (!requestedSkins.Contains(skinId) || get<bool>(skin, "DeletePending"))
                    continue;

                int fileCount = 0;
                foreach (IEmbeddedObject usage in skin.DynamicApi.GetList<IEmbeddedObject>("Files"))
                {
                    if (++fileCount > ExternalLazerSkinProtocol.MaximumFilesPerSkin)
                        throw new ExternalLazerLibraryException("skin_too_large", "The selected skin contains too many files to import safely.");

                    string logicalName = get<string>(usage, "Filename") ?? string.Empty;
                    string hash = getObject(usage, "File") is { } stored ? get<string>(stored, "Hash") ?? string.Empty : string.Empty;
                    addFile(files, uniqueFiles, LazerLibraryAssetKind.Skin, skinId.ToString("D"), logicalName, hash);
                }

                missingSkins.Remove(skinId);
            }
        }

        return new LazerLibraryAssetManifest(
            files,
            missingBeatmaps.Order().ToArray(),
            missingScores.Order().ToArray(),
            missingSkins.Order().ToArray());
    }

    private static void validateSnapshot(LazerLibrarySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Path.IsPathFullyQualified(snapshot.DatabasePath)
            || !File.Exists(snapshot.DatabasePath)
            || !string.Equals(Path.GetExtension(snapshot.DatabasePath), ".realm", StringComparison.OrdinalIgnoreCase))
        {
            throw new ExternalLazerLibraryException("snapshot_invalid", "The private lazer Realm snapshot is unavailable.");
        }

        if ((File.GetAttributes(snapshot.DatabasePath) & FileAttributes.ReparsePoint) != 0)
            throw new ExternalLazerLibraryException("snapshot_invalid", "The private lazer Realm snapshot cannot be a symbolic link.");
    }

    private static string normaliseBeatmapHash(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash)
            || hash.Length is not (32 or 64)
            || hash.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')))
        {
            throw new ExternalLazerLibraryException("beatmap_hash_invalid", "Beatmap selection hashes must be hexadecimal MD5 or SHA-256 values.");
        }

        return hash.ToLowerInvariant();
    }

    private static void addNamedFile(
        List<LazerStoredFileReference> result,
        HashSet<(LazerLibraryAssetKind Kind, string OwnerId, string LogicalName, string Hash)> unique,
        NamedFileIndex namedFiles,
        LazerLibraryAssetKind kind,
        string ownerId,
        string? logicalName)
    {
        if (string.IsNullOrWhiteSpace(logicalName))
            return;

        if (namedFiles.HashByLogicalName.TryGetValue(logicalName, out string? hash))
            addFile(result, unique, kind, ownerId, logicalName, hash);
    }

    private static NamedFileIndex readNamedFiles(IRealmObjectBase owner)
    {
        var hashByLogicalName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var logicalNameByHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (IEmbeddedObject usage in owner.DynamicApi.GetList<IEmbeddedObject>("Files"))
        {
            string logicalName = get<string>(usage, "Filename") ?? string.Empty;
            string storedHash = getObject(usage, "File") is { } stored ? get<string>(stored, "Hash") ?? string.Empty : string.Empty;
            if (logicalName.Length == 0 || storedHash.Length != 64)
                continue;
            if (logicalName.Length > ExternalLazerAssetProtocol.MaximumLogicalNameCharacters)
                throw new ExternalLazerLibraryException("asset_name_invalid", "A lazer asset contains an overlong logical filename.");

            hashByLogicalName.TryAdd(logicalName, storedHash);
            logicalNameByHash.TryAdd(storedHash, logicalName);
        }

        return new NamedFileIndex(hashByLogicalName, logicalNameByHash);
    }

    private static bool addFile(
        List<LazerStoredFileReference> result,
        HashSet<(LazerLibraryAssetKind Kind, string OwnerId, string LogicalName, string Hash)> unique,
        LazerLibraryAssetKind kind,
        string ownerId,
        string logicalName,
        string hash)
    {
        if (!validLogicalName(logicalName))
        {
            throw new ExternalLazerLibraryException("asset_name_invalid", "A lazer asset contains an invalid logical filename.");
        }

        if (hash.Length != 64)
            return false;

        hash = hash.ToLowerInvariant();
        if (unique.Add((kind, ownerId, logicalName, hash)))
        {
            result.Add(new LazerStoredFileReference(kind, ownerId, logicalName, hash));
            return true;
        }

        return false;
    }

    private static bool validLogicalName(string logicalName)
    {
        if (string.IsNullOrWhiteSpace(logicalName)
            || logicalName.Length > ExternalLazerAssetProtocol.MaximumLogicalNameCharacters
            || Path.IsPathFullyQualified(logicalName))
            return false;

        string normalised = logicalName.Replace('\\', '/');
        return normalised.Split('/').All(component => component.Length > 0 && component is not "." and not "..");
    }

    private static T get<T>(IRealmObjectBase value, string property) => value.DynamicApi.Get<T>(property);

    private static IRealmObjectBase? getObject(IRealmObjectBase value, string property)
        => value.DynamicApi.Get<IRealmObjectBase?>(property);

    private sealed record NamedFileIndex(
        IReadOnlyDictionary<string, string> HashByLogicalName,
        IReadOnlyDictionary<string, string> LogicalNameByHash);
}

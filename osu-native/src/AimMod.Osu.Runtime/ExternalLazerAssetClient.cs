using System.Text.Json;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Osu.Runtime;

public sealed class ExternalLazerAssetClient(IRuntimeRequestClient runtimeClient)
{
    public async Task<ExternalLazerAssetStagingLease> ResolveToPrivateStagingAsync(
        string libraryRoot,
        IReadOnlyList<string> beatmapHashes,
        IReadOnlyList<Guid> scoreIds,
        CancellationToken cancellationToken = default)
        => await ResolveToPrivateStagingAsync(
            libraryRoot,
            beatmapHashes,
            scoreIds,
            Array.Empty<Guid>(),
            cancellationToken).ConfigureAwait(false);

    public async Task<ExternalLazerAssetStagingLease> ResolveToPrivateStagingAsync(
        string libraryRoot,
        IReadOnlyList<string> beatmapHashes,
        IReadOnlyList<Guid> scoreIds,
        IReadOnlyList<Guid> skinIds,
        CancellationToken cancellationToken = default)
    {
        validateSelections(libraryRoot, beatmapHashes, scoreIds, skinIds);

        string stagingDirectory = Directory.CreateTempSubdirectory("aimmod-lazer-assets-").FullName;
        setPrivateDirectoryPermissions(stagingDirectory);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExternalLazerAssetResolveResult result = await ResolveAsync(
                new ExternalLazerAssetResolveRequest(libraryRoot, stagingDirectory, beatmapHashes, scoreIds, skinIds),
                CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new ExternalLazerAssetStagingLease(stagingDirectory, result);
        }
        catch
        {
            deleteOwnedStagingDirectory(stagingDirectory, null);
            throw;
        }
    }

    public async Task<ExternalLazerAssetResolveResult> ResolveAsync(
        ExternalLazerAssetResolveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        RuntimeRequest runtimeRequest = RuntimeProtocol.CreateRequest(RuntimeCommands.ResolveExternalLazerAssets, request);
        RuntimeResponse response = await runtimeClient.SendAsync(
            runtimeRequest,
            cancellationToken).ConfigureAwait(false);

        if (response.Id != runtimeRequest.Id || response.ProtocolVersion != RuntimeProtocol.CurrentVersion)
            throw invalidResponse();

        if (!response.Success)
        {
            if (response.Payload is not null || response.Error is null)
                throw invalidResponse();

            RuntimeError error = response.Error ?? new RuntimeError("worker_error", "External lazer asset resolution failed.");
            throw new ExternalLazerAssetClientException(error.Code, error.Message);
        }

        if (response.Error is not null || response.Payload is null)
            throw invalidResponse();

        try
        {
            ExternalLazerAssetResolveResult result = response.Payload.Value.Deserialize<ExternalLazerAssetResolveResult>(RuntimeProtocol.JsonOptions)
                                                     ?? throw invalidResponse();
            string stagingRoot = canonicalDirectory(request.StagingDirectory);
            var requestedBeatmaps = request.BeatmapHashes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var requestedScores = request.ScoreIds.ToHashSet();
            var requestedSkins = (request.SkinIds ?? Array.Empty<Guid>()).ToHashSet();
            IReadOnlyList<Guid> missingSkins = result.MissingSkins ?? Array.Empty<Guid>();
            if (result.Files is null || result.MissingFiles is null || result.MissingBeatmaps is null || result.MissingScores is null
                || result.Files.Count > ExternalLazerAssetProtocol.MaximumFiles
                || result.MissingFiles.Count > ExternalLazerAssetProtocol.MaximumFiles
                || result.MissingBeatmaps.Count > ExternalLazerAssetProtocol.MaximumBeatmapSelections
                || result.MissingScores.Count > ExternalLazerAssetProtocol.MaximumScoreSelections
                || missingSkins.Count > ExternalLazerAssetProtocol.MaximumSkinSelections
                || result.Files.Any(file => file is null
                                            || !validKind(file.Kind)
                                            || !validLogicalName(file.LogicalName)
                                            || !validSha256(file.Sha256Hash)
                                            || !ownerMatches(file.Kind, file.OwnerId, requestedBeatmaps, requestedScores, requestedSkins)
                                            || !isDirectChild(file.StagedPath, stagingRoot)
                                            || file.Length < 0
                                            || file.Length == 0 && file.Kind != "Skin"
                                            || file.Length > ExternalLazerAssetProtocol.MaximumTotalBytes)
                || result.MissingFiles.Any(file => file is null
                                                   || !validKind(file.Kind)
                                                   || !validLogicalName(file.LogicalName)
                                                   || !validSha256(file.Sha256Hash)
                                                   || !ownerMatches(file.Kind, file.OwnerId, requestedBeatmaps, requestedScores, requestedSkins)
                                                   || file.Code is not "file_missing")
                || result.MissingBeatmaps.Any(hash => !requestedBeatmaps.Contains(hash))
                || result.MissingScores.Any(score => !requestedScores.Contains(score))
                || missingSkins.Any(skin => !requestedSkins.Contains(skin))
                || !withinTotalLimit(result.Files))
            {
                throw invalidResponse();
            }

            return result;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw invalidResponse();
        }
    }

    private static ExternalLazerAssetClientException invalidResponse() =>
        new("invalid_worker_response", "The osu runtime worker returned an invalid external-library result.");

    private static string canonicalDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw invalidResponse();

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool isDirectChild(string path, string parent)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return false;

        string fullPath = Path.GetFullPath(path);
        return string.Equals(Path.GetDirectoryName(fullPath), parent, StringComparison.Ordinal);
    }

    private static bool validKind(string kind) => kind is "Beatmap" or "Audio" or "Background" or "Replay" or "Skin";

    private static bool validLogicalName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Length > ExternalLazerAssetProtocol.MaximumLogicalNameCharacters
            || Path.IsPathFullyQualified(name))
            return false;

        string normalised = name.Replace('\\', '/');
        return normalised.Split('/').All(component => component.Length > 0 && component is not "." and not "..");
    }

    private static bool validSha256(string hash) =>
        hash is { Length: 64 }
        && hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ownerMatches(
        string kind,
        string ownerId,
        ISet<string> requestedBeatmaps,
        ISet<Guid> requestedScores,
        ISet<Guid> requestedSkins) => kind switch
        {
            "Replay" => Guid.TryParse(ownerId, out Guid scoreId) && requestedScores.Contains(scoreId),
            "Skin" => Guid.TryParse(ownerId, out Guid skinId) && requestedSkins.Contains(skinId),
            _ => requestedBeatmaps.Contains(ownerId),
        };

    private static bool withinTotalLimit(IEnumerable<ExternalLazerResolvedAsset> files)
    {
        long remaining = ExternalLazerAssetProtocol.MaximumTotalBytes;
        foreach (ExternalLazerResolvedAsset file in files)
        {
            if (file.Length > remaining)
                return false;
            remaining -= file.Length;
        }

        return true;
    }

    private static void validateSelections(
        string libraryRoot,
        IReadOnlyList<string> beatmapHashes,
        IReadOnlyList<Guid> scoreIds,
        IReadOnlyList<Guid> skinIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentNullException.ThrowIfNull(beatmapHashes);
        ArgumentNullException.ThrowIfNull(scoreIds);
        ArgumentNullException.ThrowIfNull(skinIds);
        if (!Path.IsPathFullyQualified(libraryRoot))
            throw new ArgumentException("The lazer library root must be absolute.", nameof(libraryRoot));
        if (beatmapHashes.Count > ExternalLazerAssetProtocol.MaximumBeatmapSelections)
            throw new ArgumentOutOfRangeException(nameof(beatmapHashes));
        if (scoreIds.Count > ExternalLazerAssetProtocol.MaximumScoreSelections)
            throw new ArgumentOutOfRangeException(nameof(scoreIds));
        if (skinIds.Count > ExternalLazerAssetProtocol.MaximumSkinSelections || skinIds.Any(id => id == Guid.Empty))
            throw new ArgumentOutOfRangeException(nameof(skinIds));
        if (beatmapHashes.Any(hash => hash is null
                                      || hash.Length is not (32 or 64)
                                      || hash.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))))
        {
            throw new ArgumentException("Beatmap selections must contain hexadecimal MD5 or SHA-256 hashes.", nameof(beatmapHashes));
        }
    }

    internal static void deleteOwnedStagingDirectory(string directory, IReadOnlyList<string>? expectedFiles)
    {
        if (!Directory.Exists(directory))
            return;

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!Path.GetFileName(root).StartsWith("aimmod-lazer-assets-", StringComparison.Ordinal)
            || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ExternalLazerAssetClientException("staging_cleanup_failed", "AimMod refused to clean an unrecognised asset staging directory.");
        }

        IReadOnlyList<string> files = expectedFiles ?? Directory.EnumerateFiles(root).ToArray();
        if (Directory.EnumerateDirectories(root).Any())
            throw new ExternalLazerAssetClientException("staging_cleanup_failed", "AimMod found an unexpected directory in asset staging.");

        foreach (string path in files)
        {
            string fullPath = Path.GetFullPath(path);
            if (!string.Equals(Path.GetDirectoryName(fullPath), root, StringComparison.Ordinal))
                throw new ExternalLazerAssetClientException("staging_cleanup_failed", "AimMod refused to clean a file outside its asset staging directory.");
            if (File.Exists(fullPath))
            {
                if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                    throw new ExternalLazerAssetClientException("staging_cleanup_failed", "AimMod refused to clean a symbolic link from asset staging.");
                File.Delete(fullPath);
            }
        }

        try
        {
            Directory.Delete(root, recursive: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ExternalLazerAssetClientException("staging_cleanup_failed", "AimMod could not remove its asset staging directory.");
        }
    }

    private static void setPrivateDirectoryPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

public sealed class ExternalLazerAssetStagingLease : IAsyncDisposable
{
    private string? stagingDirectory;

    internal ExternalLazerAssetStagingLease(string stagingDirectory, ExternalLazerAssetResolveResult result)
    {
        this.stagingDirectory = stagingDirectory;
        Result = result;
    }

    public ExternalLazerAssetResolveResult Result { get; }

    public ValueTask DisposeAsync()
    {
        string? directory = Interlocked.Exchange(ref stagingDirectory, null);
        if (directory is null)
            return ValueTask.CompletedTask;

        try
        {
            ExternalLazerAssetClient.deleteOwnedStagingDirectory(directory, Result.Files.Select(file => file.StagedPath).ToArray());
        }
        catch
        {
            Interlocked.CompareExchange(ref stagingDirectory, directory, null);
            throw;
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class ExternalLazerAssetClientException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

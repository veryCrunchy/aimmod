using System.Text.Json;
using System.Security.Cryptography;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Osu.Worker;

internal sealed class ReplayAnalysisBackend : IRuntimeBackend
{
    private readonly IReplayAnalysisEngine engine;
    private readonly ReplayInputValidator inputValidator;
    private readonly TimeSpan timeout;
    private readonly IExternalLazerAssetBackend externalLibrary;
    private readonly IExternalLazerCatalogBackend externalCatalog;
    private readonly IExternalLazerSkinCatalogBackend externalSkins;

    public ReplayAnalysisBackend()
        : this(
            new OfficialReplayAnalysisEngine(),
            new ReplayInputValidator(),
            TimeSpan.FromMilliseconds(ReplayAnalysisProtocol.WallClockTimeoutMs),
            new ExternalLazerAssetBackend(),
            new ExternalLazerCatalogBackend(),
            new ExternalLazerSkinCatalogBackend())
    {
    }

    internal ReplayAnalysisBackend(IReplayAnalysisEngine engine, ReplayInputValidator inputValidator, TimeSpan timeout)
        : this(engine, inputValidator, timeout, new ExternalLazerAssetBackend(), new ExternalLazerCatalogBackend(), new ExternalLazerSkinCatalogBackend())
    {
    }

    internal ReplayAnalysisBackend(
        IReplayAnalysisEngine engine,
        ReplayInputValidator inputValidator,
        TimeSpan timeout,
        IExternalLazerAssetBackend externalLibrary)
        : this(engine, inputValidator, timeout, externalLibrary, new ExternalLazerCatalogBackend(), new ExternalLazerSkinCatalogBackend())
    {
    }

    internal ReplayAnalysisBackend(
        IReplayAnalysisEngine engine,
        ReplayInputValidator inputValidator,
        TimeSpan timeout,
        IExternalLazerAssetBackend externalLibrary,
        IExternalLazerCatalogBackend externalCatalog,
        IExternalLazerSkinCatalogBackend? externalSkins = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        this.engine = engine;
        this.inputValidator = inputValidator;
        this.timeout = timeout;
        this.externalLibrary = externalLibrary;
        this.externalCatalog = externalCatalog;
        this.externalSkins = externalSkins ?? new ExternalLazerSkinCatalogBackend();
    }

    public RuntimeHello Describe() => new(
        "AimMod.Osu.Worker",
        typeof(ReplayAnalysisBackend).Assembly.GetName().Version?.ToString() ?? "0.0.0",
        new[]
        {
            RuntimeCapabilities.ReplayAnalysis,
            RuntimeCapabilities.ExternalLibraryCatalog,
            RuntimeCapabilities.ExternalLibraryAssets,
            RuntimeCapabilities.SkinRead,
        });

    public async ValueTask<JsonElement?> ExecuteAsync(string command, JsonElement? payload, CancellationToken cancellationToken)
    {
        if (command == RuntimeCommands.ResolveExternalLazerAssets)
        {
            ExternalLazerAssetResolveRequest assetRequest = deserializeAssetRequest(payload);
            ExternalLazerAssetResolveResult assets = await externalLibrary.ResolveAsync(assetRequest, cancellationToken);
            return JsonSerializer.SerializeToElement(assets, RuntimeProtocol.JsonOptions);
        }

        if (command == RuntimeCommands.SearchExternalLazerCatalog)
        {
            ExternalLazerCatalogSearchRequest catalogRequest = deserializeCatalogRequest(payload);
            ExternalLazerCatalogSearchResult result = await externalCatalog.SearchAsync(catalogRequest, cancellationToken);
            return JsonSerializer.SerializeToElement(result, RuntimeProtocol.JsonOptions);
        }

        if (command == RuntimeCommands.SearchExternalLazerSkins)
        {
            ExternalLazerSkinCatalogSearchRequest skinRequest = deserializeSkinCatalogRequest(payload);
            ExternalLazerSkinCatalogSearchResult result = await externalSkins.SearchAsync(skinRequest, cancellationToken);
            return JsonSerializer.SerializeToElement(result, RuntimeProtocol.JsonOptions);
        }

        if (command != RuntimeCommands.AnalyseReplay)
            throw new RuntimeCommandException("unsupported_command", $"The worker does not implement '{command}'.");

        ReplayAnalysisRequest request = deserializeRequest(payload);
        ValidatedReplayInput input = inputValidator.Validate(request);

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);

        try
        {
            ReplayAnalysisResult result = await engine.AnalyseAsync(input, timeoutCancellation.Token);
            return JsonSerializer.SerializeToElement(result, RuntimeProtocol.JsonOptions);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCancellation.IsCancellationRequested)
        {
            throw new RuntimeCommandException("analysis_timeout", $"Replay analysis exceeded the {timeout.TotalSeconds:0.#} second wall-clock limit.");
        }
        catch (ReplayAnalysisException exception)
        {
            throw new RuntimeCommandException(exception.Code, exception.Message);
        }
    }

    private static ExternalLazerAssetResolveRequest deserializeAssetRequest(JsonElement? payload)
    {
        if (payload is null)
            throw new RuntimeCommandException("invalid_payload", "External library resolution requires a payload.");

        try
        {
            ExternalLazerAssetResolveRequest request = payload.Value.Deserialize<ExternalLazerAssetResolveRequest>(RuntimeProtocol.JsonOptions)
                                                       ?? throw new RuntimeCommandException("invalid_payload", "External library resolution requires a library root and selections.");
            if (string.IsNullOrWhiteSpace(request.LibraryRoot)
                || string.IsNullOrWhiteSpace(request.StagingDirectory)
                || request.BeatmapHashes is null
                || request.ScoreIds is null)
            {
                throw new RuntimeCommandException("invalid_payload", "External library resolution requires a library root and selection lists.");
            }

            return request;
        }
        catch (JsonException)
        {
            throw new RuntimeCommandException("invalid_payload", "External library resolution requires a library root and selections.");
        }
    }

    private static ExternalLazerCatalogSearchRequest deserializeCatalogRequest(JsonElement? payload)
    {
        if (payload is null)
            throw new RuntimeCommandException("invalid_payload", "External library catalog search requires a payload.");

        try
        {
            ExternalLazerCatalogSearchRequest request = payload.Value.Deserialize<ExternalLazerCatalogSearchRequest>(RuntimeProtocol.JsonOptions)
                                                        ?? throw new RuntimeCommandException("invalid_payload", "External library catalog search requires a library root and query.");
            if (string.IsNullOrWhiteSpace(request.LibraryRoot))
                throw new RuntimeCommandException("invalid_payload", "External library catalog search requires a library root.");
            return request;
        }
        catch (JsonException)
        {
            throw new RuntimeCommandException("invalid_payload", "External library catalog search requires a library root and query.");
        }
    }

    private static ExternalLazerSkinCatalogSearchRequest deserializeSkinCatalogRequest(JsonElement? payload)
    {
        if (payload is null)
            throw new RuntimeCommandException("invalid_payload", "Installed-skin search requires a payload.");

        try
        {
            ExternalLazerSkinCatalogSearchRequest request = payload.Value.Deserialize<ExternalLazerSkinCatalogSearchRequest>(RuntimeProtocol.JsonOptions)
                                                           ?? throw new RuntimeCommandException("invalid_payload", "Installed-skin search requires a library root and query.");
            if (string.IsNullOrWhiteSpace(request.LibraryRoot))
                throw new RuntimeCommandException("invalid_payload", "Installed-skin search requires a library root.");
            return request;
        }
        catch (JsonException)
        {
            throw new RuntimeCommandException("invalid_payload", "Installed-skin search requires a library root and query.");
        }
    }

    private static ReplayAnalysisRequest deserializeRequest(JsonElement? payload)
    {
        if (payload is null)
            throw new RuntimeCommandException("invalid_payload", "Replay analysis requires a payload.");

        try
        {
            return payload.Value.Deserialize<ReplayAnalysisRequest>(RuntimeProtocol.JsonOptions)
                   ?? throw new RuntimeCommandException("invalid_payload", "Replay analysis requires staged beatmap and replay paths.");
        }
        catch (JsonException)
        {
            throw new RuntimeCommandException("invalid_payload", "Replay analysis requires staged beatmap and replay paths.");
        }
    }
}

internal interface IReplayAnalysisEngine
{
    ValueTask<ReplayAnalysisResult> AnalyseAsync(ValidatedReplayInput input, CancellationToken cancellationToken);
}

internal sealed class ReplayAnalysisException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal interface IExternalLazerAssetBackend
{
    ValueTask<ExternalLazerAssetResolveResult> ResolveAsync(
        ExternalLazerAssetResolveRequest request,
        CancellationToken cancellationToken);
}

internal sealed class ExternalLazerAssetBackend : IExternalLazerAssetBackend
{
    private readonly ExternalLazerLibraryImportBridge bridge;

    public ExternalLazerAssetBackend()
        : this(new ExternalLazerLibraryImportBridge(
            new RealmLazerLibrarySnapshotFactory(),
            new DynamicRealmLazerLibraryManifestReader(),
            new ExternalLazerLibraryValidator(),
            new LazerHashedFileResolver()))
    {
    }

    internal ExternalLazerAssetBackend(ExternalLazerLibraryImportBridge bridge)
    {
        this.bridge = bridge;
    }

    public async ValueTask<ExternalLazerAssetResolveResult> ResolveAsync(
        ExternalLazerAssetResolveRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        string stagingDirectory = validateStagingDirectory(request.LibraryRoot, request.StagingDirectory);
        string snapshotDirectory = Directory.CreateTempSubdirectory("aimmod-lazer-snapshot-").FullName;
        var stagedPaths = new List<string>();
        try
        {
            await using ExternalLazerLibraryAssetLease lease = await bridge.ResolveAssetsAsync(
                new ExternalLazerLibraryLocation(request.LibraryRoot, snapshotDirectory),
                new LazerLibraryAssetQuery(request.BeatmapHashes, request.ScoreIds, request.SkinIds),
                cancellationToken).ConfigureAwait(false);

            var missingBeatmaps = lease.Assets.MissingBeatmaps.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missingScores = lease.Assets.MissingScores.ToHashSet();
            var missingSkins = (lease.Assets.MissingSkins ?? Array.Empty<Guid>()).ToHashSet();
            var files = new List<ExternalLazerResolvedAsset>();
            var missingFiles = new List<ExternalLazerMissingAsset>();
            long totalBytes = 0;

            foreach (ResolvedLazerStoredFile file in lease.Assets.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!file.Exists || file.Length is null)
                {
                    missingFiles.Add(new ExternalLazerMissingAsset(
                        file.Reference.Kind.ToString(),
                        file.Reference.OwnerId,
                        file.Reference.LogicalName,
                        file.Reference.Sha256Hash,
                        "file_missing"));
                    markRequiredSelectionMissing(file.Reference, missingBeatmaps, missingScores);
                    if (file.Reference.Kind == LazerLibraryAssetKind.Skin && Guid.TryParse(file.Reference.OwnerId, out Guid missingSkin))
                        missingSkins.Add(missingSkin);
                    continue;
                }

                if (file.Length.Value > ExternalLazerAssetProtocol.MaximumTotalBytes - totalBytes)
                    throw new RuntimeCommandException("asset_request_too_large", "The selected lazer assets exceed AimMod's staging limit.");

                string stagedPath = createStagedPath(stagingDirectory, files.Count, file.Reference);
                stagedPaths.Add(stagedPath);
                await copyAndVerifyAsync(file.SourcePath!, stagedPath, file.Length.Value, file.Reference.Sha256Hash, cancellationToken).ConfigureAwait(false);
                totalBytes += file.Length.Value;
                files.Add(new ExternalLazerResolvedAsset(
                    file.Reference.Kind.ToString(),
                    file.Reference.OwnerId,
                    file.Reference.LogicalName,
                    file.Reference.Sha256Hash,
                    stagedPath,
                    file.Length.Value));
            }

            return new ExternalLazerAssetResolveResult(
                files,
                missingFiles,
                missingBeatmaps.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                missingScores.Order().ToArray(),
                missingSkins.Order().ToArray());
        }
        catch (ExternalLazerLibraryException exception)
        {
            deleteStagedFiles(stagedPaths);
            throw new RuntimeCommandException(exception.Code, exception.Message);
        }
        catch (RuntimeCommandException)
        {
            deleteStagedFiles(stagedPaths);
            throw;
        }
        catch (IOException)
        {
            deleteStagedFiles(stagedPaths);
            throw new RuntimeCommandException("asset_copy_failed", "AimMod could not stage a selected lazer asset.");
        }
        catch (UnauthorizedAccessException)
        {
            deleteStagedFiles(stagedPaths);
            throw new RuntimeCommandException("asset_copy_failed", "AimMod could not stage a selected lazer asset.");
        }
        catch (OperationCanceledException)
        {
            deleteStagedFiles(stagedPaths);
            throw;
        }
        finally
        {
            deleteOwnedSnapshotDirectory(snapshotDirectory);
        }
    }

    private static string validateStagingDirectory(string libraryRoot, string stagingDirectory)
    {
        if (string.IsNullOrWhiteSpace(stagingDirectory)
            || !Path.IsPathFullyQualified(stagingDirectory)
            || !Directory.Exists(stagingDirectory))
        {
            throw new RuntimeCommandException("staging_path_invalid", "Asset staging requires an existing absolute directory.");
        }

        string stagingRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingDirectory));
        string liveRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
        rejectReparsePointAncestors(stagingRoot);
        string relative = Path.GetRelativePath(liveRoot, stagingRoot);
        if (relative == "."
            || (!Path.IsPathRooted(relative)
                && relative != ".."
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            || (File.GetAttributes(stagingRoot) & FileAttributes.ReparsePoint) != 0
            || Directory.EnumerateFileSystemEntries(stagingRoot).Any())
        {
            throw new RuntimeCommandException("staging_path_invalid", "Asset staging must be an empty real directory outside the lazer data root.");
        }

        return stagingRoot;
    }

    private static void rejectReparsePointAncestors(string path)
    {
        for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new RuntimeCommandException("staging_path_invalid", "Asset staging cannot use symbolic-link or junction path components.");
        }
    }

    private static string createStagedPath(string stagingDirectory, int index, LazerStoredFileReference reference)
    {
        string extension = Path.GetExtension(reference.LogicalName);
        if (extension.Length is < 2 or > 16 || extension.Skip(1).Any(character => !char.IsAsciiLetterOrDigit(character)))
            extension = ".bin";

        return Path.Combine(stagingDirectory, $"{index:D4}-{reference.Kind.ToString().ToLowerInvariant()}-{reference.Sha256Hash}{extension.ToLowerInvariant()}");
    }

    private static async Task copyAndVerifyAsync(
        string sourcePath,
        string stagedPath,
        long expectedLength,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            stagedPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        byte[] buffer = new byte[81_920];
        long copied = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            copied += read;
            if (copied > expectedLength)
                throw new RuntimeCommandException("asset_changed", "A selected lazer asset changed while AimMod was staging it.");

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        string actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (copied != expectedLength || !string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
            throw new RuntimeCommandException("asset_changed", "A selected lazer asset changed or failed its content hash while AimMod was staging it.");
    }

    private static void markRequiredSelectionMissing(
        LazerStoredFileReference reference,
        ISet<string> missingBeatmaps,
        ISet<Guid> missingScores)
    {
        if (reference.Kind == LazerLibraryAssetKind.Beatmap)
            missingBeatmaps.Add(reference.OwnerId);
        else if (reference.Kind == LazerLibraryAssetKind.Replay && Guid.TryParse(reference.OwnerId, out Guid scoreId))
            missingScores.Add(scoreId);
    }

    private static void deleteStagedFiles(IEnumerable<string> stagedPaths)
    {
        foreach (string path in stagedPaths)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void deleteOwnedSnapshotDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            || !Path.GetFileName(path).StartsWith("aimmod-lazer-snapshot-", StringComparison.Ordinal))
        {
            throw new RuntimeCommandException("snapshot_cleanup_failed", "AimMod refused to clean an unrecognised snapshot directory.");
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new RuntimeCommandException("snapshot_cleanup_failed", "AimMod could not remove its private lazer snapshot.");
        }
    }
}

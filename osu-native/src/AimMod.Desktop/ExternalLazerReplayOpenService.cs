using System.Security.Cryptography;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using AimMod.Desktop.LocalLibrary;

namespace AimMod.Desktop;

public sealed class ExternalLazerReplayOpenService
{
    private const string bundle_prefix = "aimmod-replay-open-";

    private readonly string libraryRoot;
    private readonly Func<
        string,
        IReadOnlyList<string>,
        IReadOnlyList<Guid>,
        CancellationToken,
        Task<ExternalLazerAssetStagingLease>> stageAssets;

    public ExternalLazerReplayOpenService(string libraryRoot)
        : this(libraryRoot, stageWithPrivateWorker)
    {
    }

    public ExternalLazerReplayOpenService(string libraryRoot, ExternalLazerAssetClient assetClient)
        : this(
            libraryRoot,
            (assetClient ?? throw new ArgumentNullException(nameof(assetClient))).ResolveToPrivateStagingAsync)
    {
    }

    internal ExternalLazerReplayOpenService(
        string libraryRoot,
        Func<
            string,
            IReadOnlyList<string>,
            IReadOnlyList<Guid>,
            CancellationToken,
            Task<ExternalLazerAssetStagingLease>> stageAssets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        if (!Path.IsPathFullyQualified(libraryRoot))
            throw new ArgumentException("The external lazer library root must be absolute.", nameof(libraryRoot));

        this.libraryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
        this.stageAssets = stageAssets ?? throw new ArgumentNullException(nameof(stageAssets));
    }

    public async Task<ExternalLazerPlayableReplayBundle> OpenAsync(
        ExternalLazerReplaySummary replay,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replay);
        validateReplay(replay);

        ExternalLazerAssetStagingLease assetLease;
        try
        {
            assetLease = await stageAssets(
                libraryRoot,
                new[] { replay.BeatmapHash },
                new[] { replay.ScoreId },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ExternalLazerAssetClientException error)
        {
            throw new ExternalLazerReplayOpenException(error.Code, error.Message, error);
        }

        ExternalLazerPlayableReplayBundle? bundle = null;

        try
        {
            SelectedReplayAssets selected = selectAssets(replay, assetLease.Result);
            bundle = await materialiseBundle(selected, cancellationToken).ConfigureAwait(false);
            await assetLease.DisposeAsync().ConfigureAwait(false);
            return bundle;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await cleanAfterFailure(bundle, assetLease).ConfigureAwait(false);
            throw;
        }
        catch (ExternalLazerReplayOpenException)
        {
            await cleanAfterFailure(bundle, assetLease).ConfigureAwait(false);
            throw;
        }
        catch (ExternalLazerAssetClientException error)
        {
            await cleanBundle(bundle).ConfigureAwait(false);
            throw new ExternalLazerReplayOpenException(error.Code, error.Message, error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            await cleanAfterFailure(bundle, assetLease).ConfigureAwait(false);
            throw new ExternalLazerReplayOpenException(
                "replay_staging_failed",
                "AimMod could not prepare the selected lazer replay for playback.",
                error);
        }
    }

    public Task<ExternalLazerPlayableReplayBundle> OpenAsync(
        LocalReplay replay,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replay);
        return OpenAsync(new ExternalLazerReplaySummary(
            replay.ScoreId,
            replay.SetId,
            replay.BeatmapId,
            replay.BeatmapHash,
            replay.Title,
            replay.Artist,
            replay.Difficulty,
            replay.RulesetShortName,
            replay.Player,
            replay.PlayedAt,
            replay.StarRating,
            replay.Accuracy,
            replay.TotalScore,
            replay.MaxCombo,
            replay.MissCount,
            replay.PerformancePoints,
            replay.Mods,
            replay.HasReplayFile), cancellationToken);
    }

    private static void validateReplay(ExternalLazerReplaySummary replay)
    {
        if (!string.Equals(replay.RulesetShortName, "osu", StringComparison.Ordinal))
        {
            throw new ExternalLazerReplayOpenException(
                "ruleset_unsupported",
                "AimMod currently opens osu!standard replays only.");
        }

        if (!replay.HasReplayFile)
        {
            throw new ExternalLazerReplayOpenException(
                "replay_unavailable",
                "The selected score does not contain a replay file.");
        }

        if (!validBeatmapHash(replay.BeatmapHash))
        {
            throw new ExternalLazerReplayOpenException(
                "beatmap_hash_invalid",
                "The selected replay does not identify a valid lazer beatmap.");
        }
    }

    private static SelectedReplayAssets selectAssets(
        ExternalLazerReplaySummary replay,
        ExternalLazerAssetResolveResult result)
    {
        ExternalLazerMissingAsset? missing = result.MissingFiles.FirstOrDefault(file =>
            ownerMatches(file, replay) && (file.Kind is "Replay" or "Beatmap" or "Audio" or "Background"));
        if (missing is not null)
            throw missingAssetError(missing.Kind);

        if (result.MissingScores.Contains(replay.ScoreId))
        {
            throw new ExternalLazerReplayOpenException(
                "replay_missing",
                "The selected replay is no longer present in the lazer library.");
        }

        if (result.MissingBeatmaps.Contains(replay.BeatmapHash, StringComparer.OrdinalIgnoreCase))
        {
            throw new ExternalLazerReplayOpenException(
                "beatmap_missing",
                "The beatmap for the selected replay is no longer present in the lazer library.");
        }

        ExternalLazerResolvedAsset replayFile = singleRequiredAsset(result.Files, "Replay", replay.ScoreId.ToString(), StringComparison.OrdinalIgnoreCase);
        ExternalLazerResolvedAsset beatmapFile = singleRequiredAsset(result.Files, "Beatmap", replay.BeatmapHash, StringComparison.OrdinalIgnoreCase);
        ExternalLazerResolvedAsset audioFile = singleRequiredAsset(result.Files, "Audio", replay.BeatmapHash, StringComparison.OrdinalIgnoreCase);
        ExternalLazerResolvedAsset[] backgrounds = matchingAssets(result.Files, "Background", replay.BeatmapHash, StringComparison.OrdinalIgnoreCase);

        return new SelectedReplayAssets(replayFile, beatmapFile, audioFile, backgrounds);
    }

    private static ExternalLazerResolvedAsset singleRequiredAsset(
        IReadOnlyList<ExternalLazerResolvedAsset> files,
        string kind,
        string ownerId,
        StringComparison ownerComparison)
    {
        ExternalLazerResolvedAsset[] matches = matchingAssets(files, kind, ownerId, ownerComparison);
        if (matches.Length == 0)
            throw missingAssetError(kind);
        if (matches.Length > 1)
        {
            throw new ExternalLazerReplayOpenException(
                "asset_result_invalid",
                $"The worker returned more than one {kind.ToLowerInvariant()} file for the selected replay.");
        }

        return matches[0];
    }

    private static ExternalLazerResolvedAsset[] matchingAssets(
        IReadOnlyList<ExternalLazerResolvedAsset> files,
        string kind,
        string ownerId,
        StringComparison ownerComparison) =>
        files.Where(file =>
                 string.Equals(file.Kind, kind, StringComparison.Ordinal)
                 && string.Equals(file.OwnerId, ownerId, ownerComparison))
             .ToArray();

    private static bool ownerMatches(ExternalLazerMissingAsset file, ExternalLazerReplaySummary replay) =>
        file.Kind == "Replay"
            ? string.Equals(file.OwnerId, replay.ScoreId.ToString(), StringComparison.OrdinalIgnoreCase)
            : string.Equals(file.OwnerId, replay.BeatmapHash, StringComparison.OrdinalIgnoreCase);

    private static ExternalLazerReplayOpenException missingAssetError(string kind) => kind switch
    {
        "Replay" => new ExternalLazerReplayOpenException("replay_file_missing", "The selected replay file is missing from lazer storage."),
        "Beatmap" => new ExternalLazerReplayOpenException("beatmap_file_missing", "The selected difficulty file is missing from lazer storage."),
        "Audio" => new ExternalLazerReplayOpenException("audio_file_missing", "The selected beatmap audio is missing from lazer storage."),
        "Background" => new ExternalLazerReplayOpenException("background_file_missing", "The selected beatmap background is missing from lazer storage."),
        _ => new ExternalLazerReplayOpenException("asset_file_missing", "A file required for replay playback is missing from lazer storage."),
    };

    private static async Task<ExternalLazerPlayableReplayBundle> materialiseBundle(
        SelectedReplayAssets selected,
        CancellationToken cancellationToken)
    {
        string directory = Directory.CreateTempSubdirectory(bundle_prefix).FullName;
        setPrivateDirectoryPermissions(directory);
        var bundle = new ExternalLazerPlayableReplayBundle(directory);

        try
        {
            await copyAsset(selected.Beatmap, bundle.BeatmapPath, cancellationToken).ConfigureAwait(false);
            await copyAsset(selected.Replay, bundle.ReplayPath, cancellationToken).ConfigureAwait(false);
            bundle.AudioPath = await copyLogicalAsset(selected.Audio, directory, cancellationToken).ConfigureAwait(false);

            var backgroundPaths = new List<string>(selected.Backgrounds.Count);
            foreach (ExternalLazerResolvedAsset background in selected.Backgrounds)
                backgroundPaths.Add(await copyLogicalAsset(background, directory, cancellationToken).ConfigureAwait(false));

            bundle.BackgroundPaths = backgroundPaths;
            return bundle;
        }
        catch
        {
            await bundle.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<string> copyLogicalAsset(
        ExternalLazerResolvedAsset asset,
        string directory,
        CancellationToken cancellationToken)
    {
        string relativePath = asset.LogicalName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        string destination = Path.GetFullPath(Path.Combine(directory, relativePath));
        string directoryPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(directoryPrefix, StringComparison.Ordinal))
        {
            throw new ExternalLazerReplayOpenException(
                "asset_result_invalid",
                "The worker returned an invalid replay asset name.");
        }

        string? parent = Path.GetDirectoryName(destination);
        if (parent is null)
            throw new ExternalLazerReplayOpenException("asset_result_invalid", "The worker returned an invalid replay asset name.");

        Directory.CreateDirectory(parent);
        setPrivateDirectoryPermissions(parent);
        await copyAsset(asset, destination, cancellationToken).ConfigureAwait(false);
        return destination;
    }

    private static async Task copyAsset(
        ExternalLazerResolvedAsset asset,
        string destination,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(asset.StagedPath))
        {
            throw new ExternalLazerReplayOpenException(
                "staged_asset_missing",
                "The worker did not leave a staged replay asset for AimMod to open.");
        }

        if ((File.GetAttributes(asset.StagedPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ExternalLazerReplayOpenException(
                "staged_asset_invalid",
                "AimMod refused to open a symbolic link from replay staging.");
        }

        if (File.Exists(destination))
        {
            throw new ExternalLazerReplayOpenException(
                "asset_name_conflict",
                "Two replay assets use the same file name.");
        }

        await using var source = new FileStream(
            asset.StagedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length != asset.Length)
        {
            throw new ExternalLazerReplayOpenException(
                "staged_asset_changed",
                "A staged replay asset changed before AimMod could open it.");
        }

        await using var destinationStream = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long copied = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            copied += read;
            hash.AppendData(buffer, 0, read);
            await destinationStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        await destinationStream.FlushAsync(cancellationToken).ConfigureAwait(false);

        string actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (copied != asset.Length || !string.Equals(actualHash, asset.Sha256Hash, StringComparison.Ordinal))
        {
            throw new ExternalLazerReplayOpenException(
                "staged_asset_changed",
                "A staged replay asset changed before AimMod could open it.");
        }

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static async Task cleanAfterFailure(
        ExternalLazerPlayableReplayBundle? bundle,
        ExternalLazerAssetStagingLease assetLease)
    {
        await cleanBundle(bundle).ConfigureAwait(false);
        try
        {
            await assetLease.DisposeAsync().ConfigureAwait(false);
        }
        catch (ExternalLazerAssetClientException error)
        {
            throw new ExternalLazerReplayOpenException(error.Code, error.Message, error);
        }
    }

    private static async Task cleanBundle(ExternalLazerPlayableReplayBundle? bundle)
    {
        if (bundle is not null)
            await bundle.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task<ExternalLazerAssetStagingLease> stageWithPrivateWorker(
        string libraryRoot,
        IReadOnlyList<string> beatmapHashes,
        IReadOnlyList<Guid> scoreIds,
        CancellationToken cancellationToken)
    {
        await using SidecarRuntimeClient runtime = SidecarRuntimeClient.Start();
        var client = new ExternalLazerAssetClient(new SidecarRuntimeRequestClient(runtime));
        return await client.ResolveToPrivateStagingAsync(
            libraryRoot,
            beatmapHashes,
            scoreIds,
            cancellationToken).ConfigureAwait(false);
    }

    private static bool validBeatmapHash(string hash) =>
        hash is { Length: 32 or 64 }
        && hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static void setPrivateDirectoryPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private sealed record SelectedReplayAssets(
        ExternalLazerResolvedAsset Replay,
        ExternalLazerResolvedAsset Beatmap,
        ExternalLazerResolvedAsset Audio,
        IReadOnlyList<ExternalLazerResolvedAsset> Backgrounds);
}

public sealed class ExternalLazerPlayableReplayBundle : IAsyncDisposable
{
    private const string bundle_prefix = "aimmod-replay-open-";
    private string? directoryPath;

    internal ExternalLazerPlayableReplayBundle(string directoryPath)
    {
        this.directoryPath = directoryPath;
        DirectoryPath = directoryPath;
        BeatmapPath = Path.Combine(directoryPath, "beatmap.osu");
        ReplayPath = Path.Combine(directoryPath, "replay.osr");
        OpenRequest = new ReplayOpenRequest(BeatmapPath, ReplayPath);
    }

    public string DirectoryPath { get; }

    public string BeatmapPath { get; }

    public string ReplayPath { get; }

    public string AudioPath { get; internal set; } = string.Empty;

    public IReadOnlyList<string> BackgroundPaths { get; internal set; } = Array.Empty<string>();

    public ReplayOpenRequest OpenRequest { get; }

    public ValueTask DisposeAsync()
    {
        string? directory = Interlocked.Exchange(ref directoryPath, null);
        if (directory is null || !Directory.Exists(directory))
            return ValueTask.CompletedTask;

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!Path.GetFileName(root).StartsWith(bundle_prefix, StringComparison.Ordinal)
            || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            Interlocked.CompareExchange(ref directoryPath, directory, null);
            throw new ExternalLazerReplayOpenException(
                "staging_cleanup_failed",
                "AimMod refused to clean an unrecognised replay staging directory.");
        }

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Interlocked.CompareExchange(ref directoryPath, directory, null);
            throw new ExternalLazerReplayOpenException(
                "staging_cleanup_failed",
                "AimMod could not remove its replay staging directory.",
                error);
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class ExternalLazerReplayOpenException : Exception
{
    public ExternalLazerReplayOpenException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

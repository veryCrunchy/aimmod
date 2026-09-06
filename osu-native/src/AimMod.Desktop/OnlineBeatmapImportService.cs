using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime;
using osu.Game.Beatmaps;
using osu.Game.Database;

namespace AimMod.Desktop;

public enum OnlineBeatmapImportStatus
{
    Success,
    DownloadDisabled,
    SessionUnavailable,
    SignedOut,
    TokenExpired,
    Unauthorized,
    SessionChanged,
    NetworkError,
    InvalidDownload,
    ServerError,
    ImportFailed,
    OsuInstallFailed,
}

public sealed record OnlineBeatmapImportResult(
    OnlineBeatmapImportStatus Status,
    int BeatmapSetId,
    LazerBeatmapArchive? LazerArchive = null);

public sealed class OnlineBeatmapImportService
{
    private readonly IOfficialBeatmapDiscoveryClient client;
    private readonly string stagingDirectory;
    private readonly Func<string, CancellationToken, Task<bool>> importArchive;
    private readonly Action imported;
    private readonly ILazerBeatmapInstallService? lazerInstall;
    private readonly SemaphoreSlim importGate = new(1, 1);
    private readonly Dictionary<int, LazerBeatmapArchive> pendingInstalls = new();

    public OnlineBeatmapImportService(
        IOfficialBeatmapDiscoveryClient client,
        BeatmapManager beatmapManager,
        string stagingDirectory,
        ILocalLibrarySource localLibrary,
        ILazerBeatmapInstallService lazerInstall)
        : this(
            client,
            stagingDirectory,
            async (path, cancellationToken) =>
                await beatmapManager.Import(new ImportTask(path), cancellationToken: cancellationToken).ConfigureAwait(false) is not null,
            localLibrary.Invalidate,
            lazerInstall)
    {
        ArgumentNullException.ThrowIfNull(beatmapManager);
        ArgumentNullException.ThrowIfNull(localLibrary);
    }

    internal OnlineBeatmapImportService(
        IOfficialBeatmapDiscoveryClient client,
        string stagingDirectory,
        Func<string, CancellationToken, Task<bool>> importArchive,
        Action imported,
        ILazerBeatmapInstallService? lazerInstall = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        if (!Path.IsPathFullyQualified(stagingDirectory))
            throw new ArgumentException("The beatmap staging directory must be absolute.", nameof(stagingDirectory));
        this.stagingDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingDirectory));
        this.importArchive = importArchive ?? throw new ArgumentNullException(nameof(importArchive));
        this.imported = imported ?? throw new ArgumentNullException(nameof(imported));
        this.lazerInstall = lazerInstall;
    }

    public async Task<OnlineBeatmapImportResult> ImportAsync(
        OfficialBeatmapSet beatmapSet,
        bool noVideo = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(beatmapSet);
        if (beatmapSet.DownloadDisabled)
            return new OnlineBeatmapImportResult(OnlineBeatmapImportStatus.DownloadDisabled, beatmapSet.BeatmapSetId);

        await importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? downloadedPath = null;
        LazerBeatmapArchive? lazerArchive = null;
        try
        {
            if (pendingInstalls.TryGetValue(beatmapSet.BeatmapSetId, out var pending))
                return await installSavedArchive(beatmapSet.BeatmapSetId, pending, cancellationToken).ConfigureAwait(false);
            OfficialBeatmapDownloadResult download = await client.DownloadAsync(
                beatmapSet.BeatmapSetId,
                stagingDirectory,
                noVideo,
                cancellationToken).ConfigureAwait(false);
            downloadedPath = download.ArchivePath;
            if (download.Status != OfficialBeatmapRequestStatus.Success || downloadedPath is null)
                return new OnlineBeatmapImportResult(mapDownloadStatus(download.Status), beatmapSet.BeatmapSetId);

            if (lazerInstall is not null)
            {
                try
                {
                    // AimMod's ppy importer deletes a successful ImportTask source.
                    // Preserve the handoff before importing locally, then install in the preferred client.
                    lazerArchive = await lazerInstall.PreserveAsync(
                        downloadedPath,
                        beatmapSet.BeatmapSetId,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
                {
                    lazerArchive = null;
                }
            }

            bool success = await importArchive(downloadedPath, cancellationToken).ConfigureAwait(false);
            if (!success)
            {
                if (lazerArchive is not null)
                    lazerInstall?.Discard(lazerArchive);
                return new OnlineBeatmapImportResult(OnlineBeatmapImportStatus.ImportFailed, beatmapSet.BeatmapSetId);
            }

            imported();
            if (lazerInstall is not null)
            {
                if (lazerArchive is null)
                    return new OnlineBeatmapImportResult(OnlineBeatmapImportStatus.OsuInstallFailed, beatmapSet.BeatmapSetId);
                pendingInstalls[beatmapSet.BeatmapSetId] = lazerArchive;
                return await installSavedArchive(beatmapSet.BeatmapSetId, lazerArchive, cancellationToken).ConfigureAwait(false);
            }
            return new OnlineBeatmapImportResult(OnlineBeatmapImportStatus.Success, beatmapSet.BeatmapSetId, lazerArchive);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (lazerArchive is not null && !pendingInstalls.ContainsKey(beatmapSet.BeatmapSetId))
                lazerInstall?.Discard(lazerArchive);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or NotSupportedException)
        {
            if (lazerArchive is not null)
                lazerInstall?.Discard(lazerArchive);
            return new OnlineBeatmapImportResult(OnlineBeatmapImportStatus.ImportFailed, beatmapSet.BeatmapSetId);
        }
        finally
        {
            if (downloadedPath is not null)
                deleteIfPresent(downloadedPath);
            importGate.Release();
        }
    }

    private async Task<OnlineBeatmapImportResult> installSavedArchive(int setId, LazerBeatmapArchive archive, CancellationToken token)
    {
        try
        {
            LazerBeatmapInstallResult result = await InstallInLazerAsync(archive, token).ConfigureAwait(false);
            if (result.Status is LazerBeatmapInstallStatus.Sent or LazerBeatmapInstallStatus.LazerStarted)
            {
                pendingInstalls.Remove(setId);
                return new OnlineBeatmapImportResult(OnlineBeatmapImportStatus.Success, setId, archive);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException) { }
        return new OnlineBeatmapImportResult(OnlineBeatmapImportStatus.OsuInstallFailed, setId, archive);
    }

    public Task<LazerBeatmapInstallResult> InstallInLazerAsync(
        LazerBeatmapArchive archive,
        CancellationToken cancellationToken = default) =>
        lazerInstall?.InstallAsync(archive, cancellationToken)
        ?? Task.FromResult(new LazerBeatmapInstallResult(LazerBeatmapInstallStatus.LazerNotFound));

    private static OnlineBeatmapImportStatus mapDownloadStatus(OfficialBeatmapRequestStatus status) => status switch
    {
        OfficialBeatmapRequestStatus.SessionUnavailable => OnlineBeatmapImportStatus.SessionUnavailable,
        OfficialBeatmapRequestStatus.SignedOut => OnlineBeatmapImportStatus.SignedOut,
        OfficialBeatmapRequestStatus.TokenExpired => OnlineBeatmapImportStatus.TokenExpired,
        OfficialBeatmapRequestStatus.Unauthorized => OnlineBeatmapImportStatus.Unauthorized,
        OfficialBeatmapRequestStatus.SessionChanged => OnlineBeatmapImportStatus.SessionChanged,
        OfficialBeatmapRequestStatus.NetworkError => OnlineBeatmapImportStatus.NetworkError,
        OfficialBeatmapRequestStatus.ServerError => OnlineBeatmapImportStatus.ServerError,
        _ => OnlineBeatmapImportStatus.InvalidDownload,
    };

    private static void deleteIfPresent(string path)
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

using AimMod.Osu.Runtime.Contracts;
using osu.Game.Beatmaps;

namespace AimMod.Desktop;

public sealed class ReplayAnalysisStaging : IAsyncDisposable
{
    public string DirectoryPath { get; }
    public string BeatmapPath { get; }
    public string ReplayPath { get; }

    private ReplayAnalysisStaging(string directoryPath)
    {
        DirectoryPath = directoryPath;
        BeatmapPath = Path.Combine(directoryPath, "beatmap.osu");
        ReplayPath = Path.Combine(directoryPath, "replay.osr");
    }

    public static async Task<ReplayAnalysisStaging> CreateAsync(
        string beatmapPath,
        string replayPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(beatmapPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(replayPath);

        await using FileStream beatmapStream = File.OpenRead(beatmapPath);
        await using FileStream replayStream = File.OpenRead(replayPath);
        return await CreateAsync(beatmapStream, replayStream, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ReplayAnalysisStaging> CreateAsync(
        WorkingBeatmap beatmap,
        string replayPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(beatmap);
        ArgumentException.ThrowIfNullOrWhiteSpace(replayPath);

        Stream beatmapStream = beatmap.GetStream(beatmap.BeatmapInfo.Path)
                               ?? throw new InvalidOperationException("AimMod could not read the selected difficulty data.");

        await using (beatmapStream)
        await using (FileStream replayStream = File.OpenRead(replayPath))
            return await CreateAsync(beatmapStream, replayStream, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<ReplayAnalysisStaging> CreateAsync(
        Stream beatmapStream,
        Stream replayStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(beatmapStream);
        ArgumentNullException.ThrowIfNull(replayStream);

        string directoryPath = Directory.CreateTempSubdirectory("aimmod-replay-").FullName;
        var staging = new ReplayAnalysisStaging(directoryPath);

        try
        {
            await copyBoundedAsync(
                beatmapStream,
                staging.BeatmapPath,
                ReplayAnalysisProtocol.MaximumBeatmapBytes,
                "beatmap",
                cancellationToken).ConfigureAwait(false);
            await copyBoundedAsync(
                replayStream,
                staging.ReplayPath,
                ReplayAnalysisProtocol.MaximumReplayBytes,
                "replay",
                cancellationToken).ConfigureAwait(false);
            return staging;
        }
        catch
        {
            await staging.DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!Directory.Exists(DirectoryPath))
            return ValueTask.CompletedTask;

        try
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
        catch (IOException)
        {
            // The worker may still be releasing a file during application shutdown.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return ValueTask.CompletedTask;
    }

    private static async Task copyBoundedAsync(
        Stream source,
        string destinationPath,
        long maximumBytes,
        string label,
        CancellationToken cancellationToken)
    {
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[81920];
        long copied = 0;

        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            copied += read;
            if (copied > maximumBytes)
                throw new InvalidOperationException($"The selected {label} exceeds AimMod's analysis limit.");

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        if (copied == 0)
            throw new InvalidOperationException($"The selected {label} is empty.");
    }
}

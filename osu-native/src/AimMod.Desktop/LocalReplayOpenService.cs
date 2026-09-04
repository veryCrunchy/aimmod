using AimMod.Desktop.LocalLibrary;

namespace AimMod.Desktop;

public interface IPlayableReplayBundle : IAsyncDisposable
{
    string BeatmapPath { get; }
    string ReplayPath { get; }
    ReplayOpenRequest OpenRequest { get; }
}

public interface ILocalReplayOpenService
{
    Task<IPlayableReplayBundle> OpenAsync(LocalReplay replay, CancellationToken cancellationToken = default);
}

public sealed class CompositeLocalReplayOpenService : ILocalReplayOpenService
{
    private readonly ExternalLazerReplayOpenService? lazer;

    public CompositeLocalReplayOpenService(ExternalLazerReplayOpenService? lazer = null)
    {
        this.lazer = lazer;
    }

    public async Task<IPlayableReplayBundle> OpenAsync(LocalReplay replay, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replay);
        cancellationToken.ThrowIfCancellationRequested();
        if (replay.Origin == LocalLibraryOrigin.Stable)
        {
            if (!Path.IsPathFullyQualified(replay.BeatmapPath) || !File.Exists(replay.BeatmapPath))
                throw new ExternalLazerReplayOpenException("beatmap_missing", "The osu!stable beatmap file is no longer available.");
            if (!Path.IsPathFullyQualified(replay.ReplayPath) || !File.Exists(replay.ReplayPath))
                throw new ExternalLazerReplayOpenException("replay_missing", "The osu!stable replay file is no longer available.");
            return new StablePlayableReplayBundle(replay.BeatmapPath, replay.ReplayPath);
        }

        if (lazer is null)
            throw new ExternalLazerReplayOpenException("lazer_library_unavailable", "The osu!lazer replay library is not connected.");
        return await lazer.OpenAsync(replay, cancellationToken).ConfigureAwait(false);
    }

    private sealed class StablePlayableReplayBundle : IPlayableReplayBundle
    {
        public StablePlayableReplayBundle(string beatmapPath, string replayPath)
        {
            BeatmapPath = Path.GetFullPath(beatmapPath);
            ReplayPath = Path.GetFullPath(replayPath);
            OpenRequest = new ReplayOpenRequest(BeatmapPath, ReplayPath);
        }

        public string BeatmapPath { get; }
        public string ReplayPath { get; }
        public ReplayOpenRequest OpenRequest { get; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

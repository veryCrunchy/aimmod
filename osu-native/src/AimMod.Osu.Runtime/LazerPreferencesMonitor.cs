using System.Globalization;
using System.Text;

namespace AimMod.Osu.Runtime;

/// <summary>
/// Follows the small, non-sensitive subset of osu!lazer preferences that affects
/// AimMod playback and replay presentation. The external files are never written.
/// </summary>
public sealed class LazerPreferencesMonitor : IAsyncDisposable
{
    private const int maximum_file_bytes = 1024 * 1024;
    private static readonly TimeSpan reconciliation_interval = TimeSpan.FromSeconds(5);

    private readonly string dataRoot;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object stateLock = new();
    private readonly FileSystemWatcher? watcher;
    private readonly Task reconciliationTask;
    private LazerPreferencesState current = LazerPreferencesState.Unavailable;
    private bool disposed;

    private LazerPreferencesMonitor(string dataRoot)
    {
        this.dataRoot = Path.GetFullPath(dataRoot);

        if (Directory.Exists(this.dataRoot))
        {
            watcher = new FileSystemWatcher(this.dataRoot)
            {
                Filter = "*.ini",
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
            };
            watcher.Changed += onFileChanged;
            watcher.Created += onFileChanged;
            watcher.Deleted += onFileChanged;
            watcher.Renamed += onFileChanged;
            watcher.Error += onWatcherError;
            watcher.EnableRaisingEvents = true;
        }

        reconciliationTask = reconcileAsync(lifetime.Token);
    }

    public LazerPreferencesState Current
    {
        get
        {
            lock (stateLock)
                return current;
        }
    }

    public event Action<LazerPreferencesState>? StateChanged;

    public static async Task<LazerPreferencesMonitor> CreateAsync(string dataRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var monitor = new LazerPreferencesMonitor(dataRoot);

        try
        {
            await monitor.RefreshAsync(cancellationToken);
            return monitor;
        }
        catch
        {
            await monitor.DisposeAsync();
            throw;
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        throwIfDisposed();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await refreshGate.WaitAsync(linkedCancellation.Token);

        try
        {
            LazerPreferencesSnapshot game = await readIniAsync(Path.Combine(dataRoot, "game.ini"), linkedCancellation.Token);
            LazerPreferencesSnapshot framework = await readIniAsync(Path.Combine(dataRoot, "framework.ini"), linkedCancellation.Token);
            apply(new LazerPreferencesState(
                game.SkinId,
                game.BeatmapSkins,
                game.BeatmapColours,
                game.BeatmapHitsounds,
                game.AudioOffset,
                game.PositionalHitsoundsLevel,
                framework.VolumeUniversal,
                framework.VolumeMusic,
                framework.VolumeEffect,
                0));
        }
        finally
        {
            refreshGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (stateLock)
        {
            if (disposed)
                return;

            disposed = true;
            lifetime.Cancel();
            watcher?.Dispose();
        }

        try
        {
            await reconciliationTask;
        }
        catch (OperationCanceledException)
        {
        }

        await refreshGate.WaitAsync();
        refreshGate.Release();
        refreshGate.Dispose();
        lifetime.Dispose();
    }

    private async Task<LazerPreferencesSnapshot> readIniAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > maximum_file_bytes)
                return new LazerPreferencesSnapshot();

            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            string contents = await reader.ReadToEndAsync(cancellationToken);
            if (Encoding.UTF8.GetByteCount(contents) > maximum_file_bytes)
                return new LazerPreferencesSnapshot();

            return parse(contents);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return new LazerPreferencesSnapshot();
        }
    }

    private static LazerPreferencesSnapshot parse(string contents)
    {
        Guid? skinId = null;
        bool? beatmapSkins = null;
        bool? beatmapColours = null;
        bool? beatmapHitsounds = null;
        double? audioOffset = null;
        float? positionalHitsoundsLevel = null;
        double? volumeUniversal = null;
        double? volumeMusic = null;
        double? volumeEffect = null;

        foreach (string rawLine in contents.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] is '#' or ';' || line[0] == '[' && line[^1] == ']')
                continue;

            int separator = line.IndexOf('=');
            if (separator < 1)
                continue;

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();

            if (key.Equals("Skin", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(value, out Guid parsedSkin))
                skinId = parsedSkin;
            else if (key.Equals("BeatmapSkins", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out bool parsedBeatmapSkins))
                beatmapSkins = parsedBeatmapSkins;
            else if (key.Equals("BeatmapColours", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out bool parsedBeatmapColours))
                beatmapColours = parsedBeatmapColours;
            else if (key.Equals("BeatmapHitsounds", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out bool parsedBeatmapHitsounds))
                beatmapHitsounds = parsedBeatmapHitsounds;
            else if (key.Equals("AudioOffset", StringComparison.OrdinalIgnoreCase) && tryParseBoundedDouble(value, -500, 500, out double parsedAudioOffset))
                audioOffset = parsedAudioOffset;
            else if (key.Equals("PositionalHitsoundsLevel", StringComparison.OrdinalIgnoreCase) && tryParseBoundedDouble(value, 0, 1, out double parsedPositional))
                positionalHitsoundsLevel = (float)parsedPositional;
            else if (key.Equals("VolumeUniversal", StringComparison.OrdinalIgnoreCase) && tryParseBoundedDouble(value, 0, 1, out double parsedUniversal))
                volumeUniversal = parsedUniversal;
            else if (key.Equals("VolumeMusic", StringComparison.OrdinalIgnoreCase) && tryParseBoundedDouble(value, 0, 1, out double parsedMusic))
                volumeMusic = parsedMusic;
            else if (key.Equals("VolumeEffect", StringComparison.OrdinalIgnoreCase) && tryParseBoundedDouble(value, 0, 1, out double parsedEffect))
                volumeEffect = parsedEffect;
        }

        return new LazerPreferencesSnapshot(
            skinId,
            beatmapSkins,
            beatmapColours,
            beatmapHitsounds,
            audioOffset,
            positionalHitsoundsLevel,
            volumeUniversal,
            volumeMusic,
            volumeEffect);
    }

    private static bool tryParseBoundedDouble(string value, double minimum, double maximum, out double result)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) &&
            double.IsFinite(result) && result >= minimum && result <= maximum)
            return true;

        result = default;
        return false;
    }

    private void apply(LazerPreferencesState next)
    {
        LazerPreferencesState? changed = null;

        lock (stateLock)
        {
            if (disposed)
                return;

            LazerPreferencesState comparable = next with { Revision = current.Revision };
            if (current == comparable)
                return;

            current = next with { Revision = current.Revision + 1 };
            changed = current;
        }

        foreach (Action<LazerPreferencesState> subscriber in StateChanged?.GetInvocationList().Cast<Action<LazerPreferencesState>>() ?? [])
        {
            try
            {
                subscriber(changed);
            }
            catch
            {
                // Preference monitoring must not fail because a UI observer failed.
            }
        }
    }

    private async Task reconcileAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(reconciliation_interval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
            await RefreshAsync(cancellationToken);
    }

    private void onFileChanged(object sender, FileSystemEventArgs eventArgs)
    {
        string fileName = Path.GetFileName(eventArgs.FullPath);
        if (fileName.Equals("game.ini", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("framework.ini", StringComparison.OrdinalIgnoreCase))
            _ = refreshFromWatcherAsync();
    }

    private void onWatcherError(object sender, ErrorEventArgs eventArgs) => _ = refreshFromWatcherAsync();

    private async Task refreshFromWatcherAsync()
    {
        try
        {
            await Task.Delay(100, lifetime.Token);
            await RefreshAsync(lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            // Periodic reconciliation will retry transient read failures.
        }
    }

    private void throwIfDisposed()
    {
        lock (stateLock)
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(LazerPreferencesMonitor));
        }
    }

    private sealed record LazerPreferencesSnapshot(
        Guid? SkinId = null,
        bool? BeatmapSkins = null,
        bool? BeatmapColours = null,
        bool? BeatmapHitsounds = null,
        double? AudioOffset = null,
        float? PositionalHitsoundsLevel = null,
        double? VolumeUniversal = null,
        double? VolumeMusic = null,
        double? VolumeEffect = null);
}

public sealed record LazerPreferencesState(
    Guid? SkinId,
    bool? BeatmapSkins,
    bool? BeatmapColours,
    bool? BeatmapHitsounds,
    double? AudioOffset,
    float? PositionalHitsoundsLevel,
    double? VolumeUniversal,
    double? VolumeMusic,
    double? VolumeEffect,
    long Revision)
{
    public static LazerPreferencesState Unavailable { get; } = new(null, null, null, null, null, null, null, null, null, 0);

    public bool HasValues => SkinId is not null || BeatmapSkins is not null || BeatmapColours is not null || BeatmapHitsounds is not null ||
                             AudioOffset is not null || PositionalHitsoundsLevel is not null || VolumeUniversal is not null ||
                             VolumeMusic is not null || VolumeEffect is not null;
}

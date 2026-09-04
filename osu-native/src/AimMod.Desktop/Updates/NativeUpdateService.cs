using Velopack;

namespace AimMod.Desktop.Updates;

public enum NativeUpdateChannel
{
    Stable,
    Preview,
}

public enum NativeUpdateStage
{
    Idle,
    Checking,
    Current,
    Available,
    Downloading,
    ReadyToRestart,
    Unavailable,
    Failed,
}

public enum NativeUpdatePlatform
{
    Windows,
    Linux,
    Unsupported,
}

public sealed record NativeUpdateState(
    NativeUpdateStage Stage,
    NativeUpdateChannel Channel,
    string Title,
    string Detail,
    string? Version = null,
    int Progress = 0)
{
    public static NativeUpdateState Initial(NativeUpdateChannel channel) =>
        new(NativeUpdateStage.Idle, channel, "App updates", "Ready to check for updates.");
}

public sealed record NativeUpdateRelease(string Version, object Handle);

public static class NativeUpdateFeeds
{
    public const string Stable = "https://github.com/veryCrunchy/aimmod/releases/download/aimmod-osu-stable";
    public const string Preview = "https://github.com/veryCrunchy/aimmod/releases/download/aimmod-osu-preview";

    public static string FeedFor(NativeUpdateChannel channel) => channel == NativeUpdateChannel.Preview ? Preview : Stable;

    public static string ChannelFor(NativeUpdatePlatform platform, NativeUpdateChannel channel) => (platform, channel) switch
    {
        (NativeUpdatePlatform.Windows, NativeUpdateChannel.Stable) => "win-stable",
        (NativeUpdatePlatform.Windows, NativeUpdateChannel.Preview) => "win-preview",
        (NativeUpdatePlatform.Linux, NativeUpdateChannel.Stable) => "linux-stable",
        (NativeUpdatePlatform.Linux, NativeUpdateChannel.Preview) => "linux-preview",
        _ => throw new PlatformNotSupportedException("Native updates are only supported on Windows and Linux."),
    };

    public static NativeUpdatePlatform CurrentPlatform() =>
        OperatingSystem.IsWindows() ? NativeUpdatePlatform.Windows :
        OperatingSystem.IsLinux() ? NativeUpdatePlatform.Linux :
        NativeUpdatePlatform.Unsupported;
}

public interface INativeUpdatePreferenceStore
{
    NativeUpdateChannel Load();

    void Save(NativeUpdateChannel channel);
}

public sealed class FileNativeUpdatePreferenceStore : INativeUpdatePreferenceStore
{
    private readonly string path;

    public FileNativeUpdatePreferenceStore(string path)
    {
        this.path = path;
    }

    public NativeUpdateChannel Load()
    {
        try
        {
            return string.Equals(File.ReadAllText(path).Trim(), "preview", StringComparison.OrdinalIgnoreCase)
                ? NativeUpdateChannel.Preview
                : NativeUpdateChannel.Stable;
        }
        catch (IOException)
        {
            return NativeUpdateChannel.Stable;
        }
        catch (UnauthorizedAccessException)
        {
            return NativeUpdateChannel.Stable;
        }
    }

    public void Save(NativeUpdateChannel channel)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, channel == NativeUpdateChannel.Preview ? "preview" : "stable");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public interface INativeUpdateBackend
{
    bool IsInstalled { get; }

    string? CurrentVersion { get; }

    Task<NativeUpdateRelease?> CheckForUpdatesAsync();

    Task DownloadAsync(NativeUpdateRelease release, Action<int> progress, CancellationToken cancellationToken);

    void ApplyAndRestart(NativeUpdateRelease release);
}

public interface INativeUpdateBackendFactory
{
    INativeUpdateBackend Create(NativeUpdateChannel channel);
}

public sealed class VelopackUpdateBackendFactory : INativeUpdateBackendFactory
{
    private readonly NativeUpdatePlatform platform;

    public VelopackUpdateBackendFactory()
        : this(NativeUpdateFeeds.CurrentPlatform())
    {
    }

    internal VelopackUpdateBackendFactory(NativeUpdatePlatform platform)
    {
        this.platform = platform;
    }

    public INativeUpdateBackend Create(NativeUpdateChannel channel)
    {
        if (platform == NativeUpdatePlatform.Unsupported)
            return UnsupportedUpdateBackend.Instance;

        var manager = new UpdateManager(
            NativeUpdateFeeds.FeedFor(channel),
            new UpdateOptions
            {
                ExplicitChannel = NativeUpdateFeeds.ChannelFor(platform, channel),
                AllowVersionDowngrade = true,
            });
        return new VelopackUpdateBackend(manager);
    }

    private sealed class VelopackUpdateBackend : INativeUpdateBackend
    {
        private readonly UpdateManager manager;

        public VelopackUpdateBackend(UpdateManager manager)
        {
            this.manager = manager;
        }

        public bool IsInstalled => manager.IsInstalled;

        public string? CurrentVersion => manager.CurrentVersion?.ToString();

        public async Task<NativeUpdateRelease?> CheckForUpdatesAsync()
        {
            UpdateInfo? update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            return update is null
                ? null
                : new NativeUpdateRelease(update.TargetFullRelease.Version.ToString(), update);
        }

        public Task DownloadAsync(NativeUpdateRelease release, Action<int> progress, CancellationToken cancellationToken) =>
            manager.DownloadUpdatesAsync(getHandle(release), progress, cancellationToken);

        public void ApplyAndRestart(NativeUpdateRelease release) => manager.ApplyUpdatesAndRestart(getHandle(release));

        private static UpdateInfo getHandle(NativeUpdateRelease release) =>
            release.Handle as UpdateInfo ?? throw new InvalidOperationException("The update was not created by Velopack.");
    }

    private sealed class UnsupportedUpdateBackend : INativeUpdateBackend
    {
        public static UnsupportedUpdateBackend Instance { get; } = new();

        public bool IsInstalled => false;

        public string? CurrentVersion => null;

        public Task<NativeUpdateRelease?> CheckForUpdatesAsync() => Task.FromResult<NativeUpdateRelease?>(null);

        public Task DownloadAsync(NativeUpdateRelease release, Action<int> progress, CancellationToken cancellationToken) => Task.CompletedTask;

        public void ApplyAndRestart(NativeUpdateRelease release)
        {
        }
    }
}

public interface INativeUpdateService : IDisposable
{
    NativeUpdateState State { get; }

    event Action<NativeUpdateState>? StateChanged;

    Task CheckAsync();

    Task SelectChannelAsync(NativeUpdateChannel channel);

    Task DownloadAsync();

    void ApplyAndRestart();
}

public sealed class NativeUpdateService : INativeUpdateService
{
    private readonly INativeUpdatePreferenceStore preferenceStore;
    private readonly INativeUpdateBackendFactory backendFactory;
    private readonly object sync = new();
    private CancellationTokenSource operationCancellation = new();
    private INativeUpdateBackend? backend;
    private NativeUpdateRelease? availableRelease;
    private int revision;
    private bool disposed;

    public NativeUpdateService(INativeUpdatePreferenceStore preferenceStore, INativeUpdateBackendFactory backendFactory)
    {
        this.preferenceStore = preferenceStore;
        this.backendFactory = backendFactory;
        State = NativeUpdateState.Initial(preferenceStore.Load());
    }

    public NativeUpdateState State { get; private set; }

    public event Action<NativeUpdateState>? StateChanged;

    public async Task CheckAsync()
    {
        Operation operation = beginOperation();
        setState(operation.Revision, new NativeUpdateState(
            NativeUpdateStage.Checking,
            operation.Channel,
            "Checking for updates",
            $"Looking for {channelLabel(operation.Channel)} releases."));

        try
        {
            INativeUpdateBackend nextBackend = backendFactory.Create(operation.Channel);
            if (!nextBackend.IsInstalled)
            {
                setState(operation.Revision, new NativeUpdateState(
                    NativeUpdateStage.Unavailable,
                    operation.Channel,
                    "Updates unavailable",
                    "Install AimMod to receive updates automatically."));
                return;
            }

            NativeUpdateRelease? release = await nextBackend.CheckForUpdatesAsync().ConfigureAwait(false);
            if (!isCurrent(operation.Revision))
                return;

            backend = nextBackend;
            availableRelease = release;
            setState(operation.Revision, release is null
                ? new NativeUpdateState(
                    NativeUpdateStage.Current,
                    operation.Channel,
                    "AimMod is up to date",
                    currentVersionDetail(nextBackend.CurrentVersion))
                : new NativeUpdateState(
                    NativeUpdateStage.Available,
                    operation.Channel,
                    $"AimMod {release.Version} is ready",
                    "Download the update while you keep using AimMod.",
                    release.Version));
        }
        catch (Exception)
        {
            if (isCurrent(operation.Revision))
                setState(operation.Revision, failedState(operation.Channel, "Could not check for updates."));
        }
    }

    public Task SelectChannelAsync(NativeUpdateChannel channel)
    {
        if (State.Channel == channel && State.Stage != NativeUpdateStage.Failed)
            return Task.CompletedTask;

        preferenceStore.Save(channel);
        lock (sync)
            State = NativeUpdateState.Initial(channel);
        StateChanged?.Invoke(State);
        return CheckAsync();
    }

    public async Task DownloadAsync()
    {
        INativeUpdateBackend selectedBackend;
        NativeUpdateRelease release;
        Operation operation;

        lock (sync)
        {
            if (disposed || backend is null || availableRelease is null || State.Stage != NativeUpdateStage.Available)
                return;

            selectedBackend = backend;
            release = availableRelease;
            operation = beginOperationLocked(State.Channel);
        }

        setState(operation.Revision, new NativeUpdateState(
            NativeUpdateStage.Downloading,
            operation.Channel,
            $"Downloading AimMod {release.Version}",
            "You can continue using AimMod.",
            release.Version));

        try
        {
            await selectedBackend.DownloadAsync(
                release,
                progress => setState(operation.Revision, new NativeUpdateState(
                    NativeUpdateStage.Downloading,
                    operation.Channel,
                    $"Downloading AimMod {release.Version}",
                    $"{Math.Clamp(progress, 0, 100)}% complete",
                    release.Version,
                    Math.Clamp(progress, 0, 100))),
                operation.CancellationToken).ConfigureAwait(false);

            setState(operation.Revision, new NativeUpdateState(
                NativeUpdateStage.ReadyToRestart,
                operation.Channel,
                $"AimMod {release.Version} is ready",
                "Restart to finish updating.",
                release.Version,
                100));
        }
        catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (isCurrent(operation.Revision))
                setState(operation.Revision, failedState(operation.Channel, "Could not download the update."));
        }
    }

    public void ApplyAndRestart()
    {
        INativeUpdateBackend? selectedBackend;
        NativeUpdateRelease? release;

        lock (sync)
        {
            if (disposed || State.Stage != NativeUpdateStage.ReadyToRestart)
                return;
            selectedBackend = backend;
            release = availableRelease;
        }

        if (selectedBackend is null || release is null)
            return;

        try
        {
            selectedBackend.ApplyAndRestart(release);
        }
        catch (Exception)
        {
            int currentRevision;
            lock (sync)
                currentRevision = revision;
            setState(currentRevision, failedState(State.Channel, "Could not restart to apply the update."));
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            operationCancellation.Cancel();
            operationCancellation.Dispose();
        }
    }

    private Operation beginOperation()
    {
        lock (sync)
            return beginOperationLocked(State.Channel);
    }

    private Operation beginOperationLocked(NativeUpdateChannel channel)
    {
        operationCancellation.Cancel();
        operationCancellation.Dispose();
        operationCancellation = new CancellationTokenSource();
        return new Operation(++revision, channel, operationCancellation.Token);
    }

    private void setState(int operationRevision, NativeUpdateState state)
    {
        lock (sync)
        {
            if (disposed || operationRevision != revision)
                return;
            State = state;
        }

        StateChanged?.Invoke(state);
    }

    private bool isCurrent(int operationRevision)
    {
        lock (sync)
            return !disposed && operationRevision == revision;
    }

    private static string channelLabel(NativeUpdateChannel channel) => channel == NativeUpdateChannel.Preview ? "preview" : "stable";

    private static string currentVersionDetail(string? version) => string.IsNullOrWhiteSpace(version)
        ? "You have the latest release."
        : $"Version {version} - no update available";

    private static NativeUpdateState failedState(NativeUpdateChannel channel, string detail) =>
        new(NativeUpdateStage.Failed, channel, "Update interrupted", detail);

    private readonly record struct Operation(int Revision, NativeUpdateChannel Channel, CancellationToken CancellationToken);
}

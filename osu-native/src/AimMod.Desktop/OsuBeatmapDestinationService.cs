using System.Diagnostics;
using AimMod.Osu.Runtime;

namespace AimMod.Desktop;

public enum OsuClientDestination
{
    Auto,
    Stable,
    Lazer,
}

public interface IOsuClientDestinationPreferenceStore
{
    OsuClientDestination Load();
    void Save(OsuClientDestination destination);
}

public sealed class FileOsuClientDestinationPreferenceStore : IOsuClientDestinationPreferenceStore
{
    private readonly string path;

    public FileOsuClientDestinationPreferenceStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("The destination preference path must be absolute.", nameof(path));
        this.path = Path.GetFullPath(path);
    }

    public OsuClientDestination Load()
    {
        try
        {
            string value = File.ReadAllText(path).Trim();
            return Enum.TryParse(value, ignoreCase: true, out OsuClientDestination destination)
                ? destination
                : OsuClientDestination.Auto;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return OsuClientDestination.Auto;
        }
    }

    public void Save(OsuClientDestination destination)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is not null)
            Directory.CreateDirectory(directory);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, destination.ToString());
        File.Move(temporary, path, overwrite: true);
    }
}

public interface IOsuBeatmapDestinationService : ILazerBeatmapInstallService
{
    OsuClientDestination Destination { get; set; }
    event Action<OsuClientDestination>? DestinationChanged;
    Task<LazerBeatmapInstallResult> OpenBeatmapAsync(int beatmapId, CancellationToken cancellationToken = default);
}

public sealed class OsuBeatmapDestinationService : IOsuBeatmapDestinationService
{
    private static readonly TimeSpan launch_observation_time = TimeSpan.FromSeconds(4);

    private readonly ILazerBeatmapInstallService lazer;
    private readonly IOsuClientDestinationPreferenceStore preferences;
    private readonly string? stableExecutable;
    private readonly string handoffDirectory;
    private readonly LazerExecutableLocator lazerLocator = new();
    private readonly Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<DestinationLaunchOutcome>> launch;
    private OsuClientDestination destination;

    public OsuBeatmapDestinationService(
        ILazerBeatmapInstallService lazer,
        IOsuClientDestinationPreferenceStore preferences,
        string handoffDirectory,
        string? stableExecutable = null)
        : this(lazer, preferences, handoffDirectory, stableExecutable, observeLaunchAsync)
    {
    }

    internal OsuBeatmapDestinationService(
        ILazerBeatmapInstallService lazer,
        IOsuClientDestinationPreferenceStore preferences,
        string handoffDirectory,
        string? stableExecutable,
        Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<DestinationLaunchOutcome>> launch)
    {
        this.lazer = lazer ?? throw new ArgumentNullException(nameof(lazer));
        this.preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        ArgumentException.ThrowIfNullOrWhiteSpace(handoffDirectory);
        if (!Path.IsPathFullyQualified(handoffDirectory))
            throw new ArgumentException("The handoff directory must be absolute.", nameof(handoffDirectory));
        this.handoffDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(handoffDirectory));
        this.stableExecutable = string.IsNullOrWhiteSpace(stableExecutable) ? null : Path.GetFullPath(stableExecutable);
        this.launch = launch ?? throw new ArgumentNullException(nameof(launch));
        destination = preferences.Load();
    }

    public event Action<OsuClientDestination>? DestinationChanged;

    public OsuClientDestination Destination
    {
        get => destination;
        set
        {
            if (!Enum.IsDefined(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            if (destination == value)
                return;
            preferences.Save(value);
            destination = value;
            DestinationChanged?.Invoke(value);
        }
    }

    public Task<LazerBeatmapArchive> PreserveAsync(
        string sourceArchive,
        int beatmapSetId,
        CancellationToken cancellationToken = default) =>
        lazer.PreserveAsync(sourceArchive, beatmapSetId, cancellationToken);

    public async Task<LazerBeatmapInstallResult> InstallAsync(
        LazerBeatmapArchive archive,
        CancellationToken cancellationToken = default)
    {
        return Destination switch
        {
            OsuClientDestination.Stable => await installStable(archive, cancellationToken).ConfigureAwait(false),
            OsuClientDestination.Lazer => await lazer.InstallAsync(archive, cancellationToken).ConfigureAwait(false),
            _ => await installAuto(archive, cancellationToken).ConfigureAwait(false),
        };
    }

    public void Discard(LazerBeatmapArchive archive) => lazer.Discard(archive);

    public async Task<LazerBeatmapInstallResult> OpenBeatmapAsync(int beatmapId, CancellationToken cancellationToken = default)
    {
        if (beatmapId <= 0)
            throw new ArgumentOutOfRangeException(nameof(beatmapId));
        string uri = $"osu://b/{beatmapId}";
        if (Destination == OsuClientDestination.Stable)
            return await launchStable(stableExecutable, uri, cancellationToken).ConfigureAwait(false);

        // osu!lazer registers the official osu:// handler. Auto uses it first,
        // then falls back to an explicitly discovered stable executable.
        LazerBeatmapInstallResult lazerResult = await launchUri(uri, cancellationToken).ConfigureAwait(false);
        return Destination == OsuClientDestination.Auto && lazerResult.Status is LazerBeatmapInstallStatus.LaunchFailed or LazerBeatmapInstallStatus.LazerNotFound
            ? await launchStable(stableExecutable, uri, cancellationToken).ConfigureAwait(false)
            : lazerResult;
    }

    private async Task<LazerBeatmapInstallResult> installAuto(LazerBeatmapArchive archive, CancellationToken cancellationToken)
    {
        LazerBeatmapInstallResult result = await lazer.InstallAsync(archive, cancellationToken).ConfigureAwait(false);
        return result.Status == LazerBeatmapInstallStatus.LazerNotFound
            ? await installStable(archive, cancellationToken).ConfigureAwait(false)
            : result;
    }

    private async Task<LazerBeatmapInstallResult> installStable(LazerBeatmapArchive archive, CancellationToken cancellationToken)
    {
        if (stableExecutable is null || !File.Exists(stableExecutable))
            return new LazerBeatmapInstallResult(LazerBeatmapInstallStatus.LazerNotFound, "osu!stable");

        string archivePath = Path.Combine(
            handoffDirectory,
            archive.BeatmapSetId == 0
                ? $"practice-{archive.Id:N}.osz"
                : $"beatmapset-{archive.BeatmapSetId}-{archive.Id:N}.osz");
        if (!File.Exists(archivePath))
            return new LazerBeatmapInstallResult(LazerBeatmapInstallStatus.ArchiveUnavailable, "osu!stable");

        var startInfo = new ProcessStartInfo(stableExecutable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(stableExecutable) ?? string.Empty,
        };
        startInfo.ArgumentList.Add(archivePath);

        try
        {
            DestinationLaunchOutcome outcome = await launch(startInfo, launch_observation_time, cancellationToken).ConfigureAwait(false);
            if (!outcome.Started)
                return new LazerBeatmapInstallResult(LazerBeatmapInstallStatus.LaunchFailed, "osu!stable");
            if (outcome.Exited && outcome.ExitCode != 0)
                return new LazerBeatmapInstallResult(LazerBeatmapInstallStatus.LazerRejected, "osu!stable");
            return new LazerBeatmapInstallResult(
                outcome.Exited ? LazerBeatmapInstallStatus.Sent : LazerBeatmapInstallStatus.LazerStarted,
                "osu!stable");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            return new LazerBeatmapInstallResult(LazerBeatmapInstallStatus.LaunchFailed, "osu!stable");
        }
    }

    private async Task<LazerBeatmapInstallResult> launchUri(string uri, CancellationToken cancellationToken)
    {
        LazerLaunchCommand? command = lazerLocator.Find();
        if (command is null)
            return new LazerBeatmapInstallResult(LazerBeatmapInstallStatus.LazerNotFound, "osu!lazer");
        var startInfo = new ProcessStartInfo(command.ExecutablePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(command.ExecutablePath) ?? string.Empty,
        };
        foreach (string argument in command.ArgumentsBeforeArchive)
            startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add(uri);
        foreach (string argument in command.ArgumentsAfterArchive)
            startInfo.ArgumentList.Add(argument);
        try
        {
            DestinationLaunchOutcome outcome = await launch(startInfo, launch_observation_time, cancellationToken).ConfigureAwait(false);
            return outcome.Started
                ? new LazerBeatmapInstallResult(LazerBeatmapInstallStatus.LazerStarted, command.Source)
                : new LazerBeatmapInstallResult(LazerBeatmapInstallStatus.LaunchFailed, command.Source);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new LazerBeatmapInstallResult(LazerBeatmapInstallStatus.LaunchFailed, command.Source);
        }
    }

    private async Task<LazerBeatmapInstallResult> launchStable(string? executable, string argument, CancellationToken cancellationToken)
    {
        if (executable is null || !File.Exists(executable))
            return new LazerBeatmapInstallResult(LazerBeatmapInstallStatus.LazerNotFound, "osu!stable");
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? string.Empty,
        };
        startInfo.ArgumentList.Add(argument);
        try
        {
            DestinationLaunchOutcome outcome = await launch(startInfo, launch_observation_time, cancellationToken).ConfigureAwait(false);
            return outcome.Started
                ? new LazerBeatmapInstallResult(LazerBeatmapInstallStatus.LazerStarted, "osu!stable")
                : new LazerBeatmapInstallResult(LazerBeatmapInstallStatus.LaunchFailed, "osu!stable");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            return new LazerBeatmapInstallResult(LazerBeatmapInstallStatus.LaunchFailed, "osu!stable");
        }
    }

    private static async Task<DestinationLaunchOutcome> observeLaunchAsync(
        ProcessStartInfo startInfo,
        TimeSpan observationTime,
        CancellationToken cancellationToken)
    {
        using Process? process = Process.Start(startInfo);
        if (process is null)
            return new DestinationLaunchOutcome(false, false, 0);
        Task exit = process.WaitForExitAsync(CancellationToken.None);
        Task observed = await Task.WhenAny(exit, Task.Delay(observationTime, cancellationToken)).ConfigureAwait(false);
        return observed == exit
            ? new DestinationLaunchOutcome(true, true, process.ExitCode)
            : new DestinationLaunchOutcome(true, false, 0);
    }

    internal sealed record DestinationLaunchOutcome(bool Started, bool Exited, int ExitCode);
}

using System.Diagnostics;
using AimMod.Osu.Runtime;

namespace AimMod.Desktop.Skins.Online;

public sealed class OsuSkinArchiveDestinationService : IOnlineSkinArchiveDestination
{
    private static readonly TimeSpan launch_observation_time = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan retained_archive_age = TimeSpan.FromDays(2);
    private const int maximum_retained_archives = 12;

    private readonly Func<OsuClientDestination> destination;
    private readonly string handoffRoot;
    private readonly string? stableExecutable;
    private readonly LazerExecutableLocator lazerLocator;
    private readonly Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<LaunchOutcome>> launch;
    private readonly SemaphoreSlim gate = new(1, 1);

    public OsuSkinArchiveDestinationService(
        Func<OsuClientDestination> destination,
        string handoffRoot,
        string? stableExecutable = null)
        : this(destination, handoffRoot, stableExecutable, new LazerExecutableLocator(), observeLaunchAsync)
    {
    }

    internal OsuSkinArchiveDestinationService(
        Func<OsuClientDestination> destination,
        string handoffRoot,
        string? stableExecutable,
        LazerExecutableLocator lazerLocator,
        Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<LaunchOutcome>> launch)
    {
        this.destination = destination ?? throw new ArgumentNullException(nameof(destination));
        ArgumentException.ThrowIfNullOrWhiteSpace(handoffRoot);
        if (!Path.IsPathFullyQualified(handoffRoot))
            throw new ArgumentException("The skin handoff directory must be absolute.", nameof(handoffRoot));
        this.handoffRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(handoffRoot));
        this.stableExecutable = string.IsNullOrWhiteSpace(stableExecutable) ? null : Path.GetFullPath(stableExecutable);
        this.lazerLocator = lazerLocator ?? throw new ArgumentNullException(nameof(lazerLocator));
        this.launch = launch ?? throw new ArgumentNullException(nameof(launch));
    }

    public async Task<OnlineSkinImportResult> ImportAsync(string validatedOskPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(validatedOskPath);
        if (!Path.IsPathFullyQualified(validatedOskPath) || !File.Exists(validatedOskPath))
            return new OnlineSkinImportResult(false, "The temporary .osk archive is no longer available.");

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(handoffRoot);
            if ((File.GetAttributes(handoffRoot) & FileAttributes.ReparsePoint) != 0)
                return new OnlineSkinImportResult(false, "The skin handoff directory is not safe to use.");
            trimHandoffs();
            string handoff = Path.Combine(handoffRoot, $"skin-{Guid.NewGuid():N}.osk");
            await copyAsync(validatedOskPath, handoff, cancellationToken).ConfigureAwait(false);

            ProcessStartInfo? command = createCommand(destination(), handoff);
            if (command is null)
            {
                File.Delete(handoff);
                return new OnlineSkinImportResult(false, "The selected osu! client could not be found.");
            }
            try
            {
                LaunchOutcome outcome = await launch(command, launch_observation_time, cancellationToken).ConfigureAwait(false);
                if (!outcome.Started || outcome.Exited && outcome.ExitCode != 0)
                {
                    File.Delete(handoff);
                    return new OnlineSkinImportResult(false, "osu! did not accept the skin archive.");
                }
                File.SetLastWriteTimeUtc(handoff, DateTime.UtcNow);
                return new OnlineSkinImportResult(true, "Skin sent to osu!.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                File.Delete(handoff);
                return new OnlineSkinImportResult(false, $"osu! could not be launched: {error.Message}");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private ProcessStartInfo? createCommand(OsuClientDestination target, string archive)
    {
        if (target == OsuClientDestination.Stable)
            return stableCommand(archive);
        if (target == OsuClientDestination.Lazer)
            return lazerCommand(archive);
        return lazerCommand(archive) ?? stableCommand(archive) ?? shellCommand(archive);
    }

    private ProcessStartInfo? stableCommand(string archive)
    {
        if (stableExecutable is null || !File.Exists(stableExecutable))
            return null;
        var command = new ProcessStartInfo(stableExecutable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(stableExecutable) ?? string.Empty,
        };
        command.ArgumentList.Add(archive);
        return command;
    }

    private ProcessStartInfo? lazerCommand(string archive)
    {
        LazerLaunchCommand? lazer = lazerLocator.Find();
        if (lazer is null)
            return null;
        var command = new ProcessStartInfo(lazer.ExecutablePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(lazer.ExecutablePath) ?? string.Empty,
        };
        foreach (string argument in lazer.ArgumentsBeforeArchive)
            command.ArgumentList.Add(argument);
        command.ArgumentList.Add(archive);
        foreach (string argument in lazer.ArgumentsAfterArchive)
            command.ArgumentList.Add(argument);
        return command;
    }

    private static ProcessStartInfo shellCommand(string archive) => new(archive) { UseShellExecute = true };

    private static async Task copyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private void trimHandoffs()
    {
        FileInfo[] files = Directory.EnumerateFiles(handoffRoot, "skin-*.osk", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .Where(file => (file.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();
        DateTime cutoff = DateTime.UtcNow - retained_archive_age;
        for (int index = 0; index < files.Length; index++)
        {
            if (index >= maximum_retained_archives || files[index].LastWriteTimeUtc < cutoff)
            {
                try
                {
                    files[index].Delete();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static async Task<LaunchOutcome> observeLaunchAsync(ProcessStartInfo command, TimeSpan observation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using Process? process = Process.Start(command);
        if (process is null)
            return new LaunchOutcome(false, false, 0);
        Task exit = process.WaitForExitAsync(CancellationToken.None);
        Task observed = await Task.WhenAny(exit, Task.Delay(observation, CancellationToken.None)).ConfigureAwait(false);
        return observed == exit
            ? new LaunchOutcome(true, true, process.ExitCode)
            : new LaunchOutcome(true, false, 0);
    }

    internal sealed record LaunchOutcome(bool Started, bool Exited, int ExitCode);
}

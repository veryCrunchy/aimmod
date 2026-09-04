using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace AimMod.Osu.Runtime;

public enum LazerBeatmapInstallStatus
{
    Sent,
    LazerStarted,
    ArchiveUnavailable,
    LazerNotFound,
    LaunchFailed,
    LazerRejected,
}

/// <param name="BeatmapSetId">The online set ID, or zero for a locally generated archive.</param>
public sealed record LazerBeatmapArchive(int BeatmapSetId, Guid Id);

public sealed record LazerBeatmapInstallResult(LazerBeatmapInstallStatus Status, string? LauncherSource = null);

public interface ILazerBeatmapInstallService
{
    Task<LazerBeatmapArchive> PreserveAsync(
        string sourceArchive,
        int beatmapSetId,
        CancellationToken cancellationToken = default);

    Task<LazerBeatmapInstallResult> InstallAsync(
        LazerBeatmapArchive archive,
        CancellationToken cancellationToken = default);

    void Discard(LazerBeatmapArchive archive);
}

/// <summary>
/// Keeps a bounded copy of downloaded beatmaps and hands a selected archive to
/// osu!lazer through its supported command-line import flow. This never opens
/// or writes lazer's Realm database.
/// </summary>
public sealed class LazerBeatmapInstallService : ILazerBeatmapInstallService
{
    private const long maximum_archive_bytes = 512L * 1024 * 1024;
    private const long maximum_cache_bytes = 2L * 1024 * 1024 * 1024;
    private const int maximum_cached_archives = 8;
    private const int maximum_zip_entries = 100_000;
    private static readonly TimeSpan launcher_observation_time = TimeSpan.FromSeconds(4);

    private readonly string archiveDirectory;
    private readonly LazerExecutableLocator locator;
    private readonly Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<LazerLaunchOutcome>> launch;
    private readonly SemaphoreSlim gate = new(1, 1);

    public LazerBeatmapInstallService(string archiveDirectory)
        : this(archiveDirectory, new LazerExecutableLocator(), observeLaunchAsync)
    {
    }

    internal LazerBeatmapInstallService(
        string archiveDirectory,
        LazerExecutableLocator locator,
        Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<LazerLaunchOutcome>> launch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveDirectory);
        if (!Path.IsPathFullyQualified(archiveDirectory))
            throw new ArgumentException("The lazer handoff directory must be absolute.", nameof(archiveDirectory));

        this.archiveDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(archiveDirectory));
        this.locator = locator ?? throw new ArgumentNullException(nameof(locator));
        this.launch = launch ?? throw new ArgumentNullException(nameof(launch));
    }

    public async Task<LazerBeatmapArchive> PreserveAsync(
        string sourceArchive,
        int beatmapSetId,
        CancellationToken cancellationToken = default)
    {
        validateArchiveIdentity(beatmapSetId);
        string source = validateSourceArchive(sourceArchive);
        validateArchive(source);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        string? committedPath = null;
        try
        {
            ensureArchiveDirectory();
            var archive = new LazerBeatmapArchive(beatmapSetId, Guid.NewGuid());
            string destination = getArchivePath(archive);
            temporaryPath = Path.Combine(archiveDirectory, $".aimmod-handoff-{Guid.NewGuid():N}.partial");

            await using (var input = new FileStream(
                             source,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             1024 * 128,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             1024 * 128,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await copyBoundedAsync(input, output, cancellationToken).ConfigureAwait(false);
            }

            validateArchive(temporaryPath, requireOszExtension: false);
            File.Move(temporaryPath, destination);
            temporaryPath = null;
            committedPath = destination;
            File.SetLastWriteTimeUtc(destination, DateTime.UtcNow);
            trimCache(destination);
            committedPath = null;
            return archive;
        }
        catch
        {
            if (committedPath is not null)
                deleteFile(committedPath);
            throw;
        }
        finally
        {
            if (temporaryPath is not null)
                deleteFile(temporaryPath);
            gate.Release();
        }
    }

    public async Task<LazerBeatmapInstallResult> InstallAsync(
        LazerBeatmapArchive archive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        validateArchiveIdentity(archive.BeatmapSetId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            string path;
            try
            {
                path = getArchivePath(archive);
                validateCachedArchive(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return new LazerBeatmapInstallResult(LazerBeatmapInstallStatus.ArchiveUnavailable);
            }

            LazerLaunchCommand? command = locator.Find();
            if (command is null)
                return new LazerBeatmapInstallResult(LazerBeatmapInstallStatus.LazerNotFound);

            // The secondary osu! process acknowledges IPC before the primary process opens the archive.
            // Do not mutate the handoff file after launch or its ZIP probe can race the metadata write.
            touch(path);
            ProcessStartInfo startInfo = CreateStartInfo(command, path);
            try
            {
                LazerLaunchOutcome outcome = await launch(startInfo, launcher_observation_time, cancellationToken).ConfigureAwait(false);
                if (!outcome.Started)
                    return new LazerBeatmapInstallResult(LazerBeatmapInstallStatus.LaunchFailed, command.Source);
                if (outcome.Exited && outcome.ExitCode != 0)
                    return new LazerBeatmapInstallResult(LazerBeatmapInstallStatus.LazerRejected, command.Source);

                return new LazerBeatmapInstallResult(
                    outcome.Exited ? LazerBeatmapInstallStatus.Sent : LazerBeatmapInstallStatus.LazerStarted,
                    command.Source);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
            {
                return new LazerBeatmapInstallResult(LazerBeatmapInstallStatus.LaunchFailed, command.Source);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public void Discard(LazerBeatmapArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        validateArchiveIdentity(archive.BeatmapSetId);
        deleteFile(getArchivePath(archive));
    }

    internal static ProcessStartInfo CreateStartInfo(LazerLaunchCommand command, string archivePath)
    {
        var startInfo = new ProcessStartInfo(command.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = Path.GetDirectoryName(command.ExecutablePath) ?? string.Empty,
        };

        foreach (string argument in command.ArgumentsBeforeArchive)
            startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add(archivePath);
        foreach (string argument in command.ArgumentsAfterArchive)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static async Task<LazerLaunchOutcome> observeLaunchAsync(
        ProcessStartInfo startInfo,
        TimeSpan observationTime,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using Process? process = Process.Start(startInfo);
        if (process is null)
            return new LazerLaunchOutcome(false, false, 0);

        Task exit = process.WaitForExitAsync(CancellationToken.None);
        Task observed = await Task.WhenAny(exit, Task.Delay(observationTime, CancellationToken.None)).ConfigureAwait(false);
        return observed == exit
            ? new LazerLaunchOutcome(true, true, process.ExitCode)
            : new LazerLaunchOutcome(true, false, 0);
    }

    private void ensureArchiveDirectory()
    {
        Directory.CreateDirectory(archiveDirectory);
        if ((File.GetAttributes(archiveDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The lazer handoff directory cannot be a symbolic link.");
    }

    private string getArchivePath(LazerBeatmapArchive archive)
    {
        string filename = archive.BeatmapSetId == 0
            ? $"practice-{archive.Id:N}.osz"
            : $"beatmapset-{archive.BeatmapSetId}-{archive.Id:N}.osz";
        string path = Path.GetFullPath(Path.Combine(archiveDirectory, filename));
        if (!string.Equals(Path.GetDirectoryName(path), archiveDirectory, StringComparison.Ordinal))
            throw new IOException("The lazer handoff archive path is invalid.");
        return path;
    }

    private void validateCachedArchive(string path)
    {
        if (!Directory.Exists(archiveDirectory)
            || (File.GetAttributes(archiveDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The lazer handoff directory is unavailable.");
        validateArchive(path);
    }

    private static string validateSourceArchive(string sourceArchive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceArchive);
        if (!Path.IsPathFullyQualified(sourceArchive))
            throw new ArgumentException("The downloaded beatmap archive must be absolute.", nameof(sourceArchive));
        return Path.GetFullPath(sourceArchive);
    }

    private static void validateArchive(string path, bool requireOszExtension = true)
    {
        if (requireOszExtension && !string.Equals(Path.GetExtension(path), ".osz", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The lazer handoff only accepts .osz beatmap archives.");
        if (!File.Exists(path))
            throw new FileNotFoundException("The beatmap archive is unavailable.", path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The beatmap archive cannot be a symbolic link.");

        var info = new FileInfo(path);
        if (info.Length is <= 0 or > maximum_archive_bytes)
            throw new InvalidDataException("The beatmap archive has an invalid size.");

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            if (zip.Entries.Count > maximum_zip_entries
                || !zip.Entries.Any(entry => entry.FullName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The beatmap archive does not contain an osu! beatmap.");
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("The beatmap archive could not be read.", exception);
        }
    }

    private static async Task copyBoundedAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1024 * 128];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maximum_archive_bytes)
                throw new InvalidDataException("The beatmap archive exceeds AimMod's handoff limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private void trimCache(string protectedPath)
    {
        FileInfo[] archives = Directory.EnumerateFiles(archiveDirectory, "*.osz", SearchOption.TopDirectoryOnly)
                                       .Select(path => new FileInfo(path))
                                       .Where(file => (file.Attributes & FileAttributes.ReparsePoint) == 0)
                                       .OrderByDescending(file => file.LastWriteTimeUtc)
                                       .ToArray();
        long totalBytes = archives.Sum(file => file.Length);
        int remaining = archives.Length;
        for (int index = archives.Length - 1; index >= 0; index--)
        {
            FileInfo candidate = archives[index];
            if (remaining <= maximum_cached_archives && totalBytes <= maximum_cache_bytes)
                break;
            if (string.Equals(candidate.FullName, protectedPath, StringComparison.Ordinal))
                continue;
            if (deleteFile(candidate.FullName))
            {
                totalBytes -= candidate.Length;
                remaining--;
            }
        }

        if (remaining > maximum_cached_archives || totalBytes > maximum_cache_bytes)
            throw new IOException("AimMod could not keep the lazer handoff cache within its storage limit.");
    }

    private static void validateArchiveIdentity(int beatmapSetId)
    {
        if (beatmapSetId < 0)
            throw new ArgumentOutOfRangeException(nameof(beatmapSetId));
    }

    private static bool deleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void touch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed record LazerLaunchOutcome(bool Started, bool Exited, int ExitCode);

internal sealed record LazerLaunchCommand(
    string ExecutablePath,
    IReadOnlyList<string> ArgumentsBeforeArchive,
    IReadOnlyList<string> ArgumentsAfterArchive,
    string Source);

internal sealed class LazerExecutableLocator
{
    private const int maximum_desktop_entry_bytes = 64 * 1024;
    private const int maximum_desktop_entries = 512;
    private const string beatmap_archive_mime = "application/x-osu-beatmap-archive";
    private static readonly HashSet<string> known_linux_desktop_ids = new(StringComparer.Ordinal)
    {
        "osu.desktop",
        "osu!.desktop",
        "osu-lazer.desktop",
        "sh.ppy.osu.desktop",
    };

    private readonly string homeDirectory;
    private readonly string? xdgDataHome;
    private readonly string? xdgDataDirectories;
    private readonly string? pathVariable;
    private readonly OSPlatform platform;

    public LazerExecutableLocator()
        : this(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
            Environment.GetEnvironmentVariable("XDG_DATA_DIRS"),
            Environment.GetEnvironmentVariable("PATH"),
            currentPlatform())
    {
    }

    internal LazerExecutableLocator(
        string homeDirectory,
        string? xdgDataHome,
        string? xdgDataDirectories,
        string? pathVariable,
        OSPlatform platform)
    {
        this.homeDirectory = homeDirectory;
        this.xdgDataHome = xdgDataHome;
        this.xdgDataDirectories = xdgDataDirectories;
        this.pathVariable = pathVariable;
        this.platform = platform;
    }

    public LazerLaunchCommand? Find()
    {
        if (platform == OSPlatform.Linux)
        {
            LazerLaunchCommand? desktop = findLinuxDesktopEntry();
            if (desktop is not null)
                return desktop;

            foreach (string path in linuxFallbacks())
            {
                string? executable = resolveExecutable(path);
                if (executable is not null)
                    return new LazerLaunchCommand(executable, [], [], "lazer executable");
            }
        }
        else if (platform == OSPlatform.OSX)
        {
            string path = "/Applications/osu!.app/Contents/MacOS/osu!";
            if (isExecutable(path))
                return new LazerLaunchCommand(path, [], [], "osu! application");
        }
        else if (platform == OSPlatform.Windows)
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string path = Path.Combine(localAppData, "osulazer", "current", "osu!.exe");
            if (isExecutable(path))
                return new LazerLaunchCommand(path, [], [], "osu! installation");
        }

        return null;
    }

    private LazerLaunchCommand? findLinuxDesktopEntry()
    {
        int inspected = 0;
        foreach (string directory in desktopEntryDirectories().Distinct(StringComparer.Ordinal))
        {
            if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
                continue;

            string[] entries;
            try
            {
                entries = Directory.GetFiles(directory, "*.desktop", SearchOption.TopDirectoryOnly);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string path in entries)
            {
                if (++inspected > maximum_desktop_entries)
                    return null;
                if (!known_linux_desktop_ids.Contains(Path.GetFileName(path)))
                    continue;
                LazerLaunchCommand? command = parseDesktopEntry(path);
                if (command is not null)
                    return command;
            }
        }
        return null;
    }

    private LazerLaunchCommand? parseDesktopEntry(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > maximum_desktop_entry_bytes
                || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                return null;

            string? name = null;
            string? exec = null;
            string? type = null;
            string? mimeTypes = null;
            bool inDesktopEntry = false;
            foreach (string rawLine in File.ReadLines(path))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    inDesktopEntry = string.Equals(line, "[Desktop Entry]", StringComparison.Ordinal);
                    continue;
                }
                if (!inDesktopEntry)
                    continue;
                if (line.StartsWith("Name=", StringComparison.Ordinal))
                    name = line[5..].Trim();
                else if (line.StartsWith("Exec=", StringComparison.Ordinal))
                    exec = line[5..].Trim();
                else if (line.StartsWith("Type=", StringComparison.Ordinal))
                    type = line[5..].Trim();
                else if (line.StartsWith("MimeType=", StringComparison.Ordinal))
                    mimeTypes = line[9..].Trim();
            }

            bool handlesBeatmapArchives = mimeTypes?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                                        .Contains(beatmap_archive_mime, StringComparer.Ordinal) == true;
            if (name is not ("osu!" or "osu!lazer")
                || !string.Equals(type, "Application", StringComparison.Ordinal)
                || !handlesBeatmapArchives
                || string.IsNullOrWhiteSpace(exec))
                return null;

            List<string> tokens = tokenizeDesktopExec(exec);
            if (tokens.Count == 0 || tokens.Count > 32)
                return null;
            string? executable = resolveExecutable(tokens[0]);
            if (executable is null)
                return null;

            var before = new List<string>();
            var after = new List<string>();
            List<string> destination = before;
            bool foundArchiveSlot = false;
            foreach (string token in tokens.Skip(1))
            {
                if (token is "%u" or "%U" or "%f" or "%F")
                {
                    if (foundArchiveSlot)
                        return null;
                    foundArchiveSlot = true;
                    destination = after;
                    continue;
                }
                if (token is "%i" or "%c" or "%k")
                    continue;
                if (token.Contains('%', StringComparison.Ordinal))
                    return null;
                destination.Add(token.Replace("%%", "%", StringComparison.Ordinal));
            }

            return new LazerLaunchCommand(executable, before, after, $"desktop entry {Path.GetFileName(path)}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    private IEnumerable<string> desktopEntryDirectories()
    {
        string dataHome = string.IsNullOrWhiteSpace(xdgDataHome)
            ? Path.Combine(homeDirectory, ".local", "share")
            : xdgDataHome;
        yield return Path.Combine(dataHome, "applications");

        string directories = string.IsNullOrWhiteSpace(xdgDataDirectories)
            ? "/usr/local/share:/usr/share"
            : xdgDataDirectories;
        foreach (string directory in directories.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return Path.Combine(directory, "applications");
    }

    private IEnumerable<string> linuxFallbacks()
    {
        yield return Path.Combine(homeDirectory, ".local", "bin", "osu-native-pen");
        yield return Path.Combine(homeDirectory, ".local", "bin", "osu.AppImage");
        yield return Path.Combine(homeDirectory, "Applications", "osu.AppImage");
        yield return Path.Combine(homeDirectory, "Applications", "osu!.AppImage");
        yield return "osu-lazer";
        yield return "osu";
    }

    private string? resolveExecutable(string candidate)
    {
        if (Path.IsPathFullyQualified(candidate))
            return isExecutable(candidate) ? Path.GetFullPath(candidate) : null;
        if (candidate.Contains(Path.DirectorySeparatorChar) || candidate.Contains(Path.AltDirectorySeparatorChar))
            return null;

        foreach (string directory in (pathVariable ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string path = Path.Combine(directory, candidate);
            if (isExecutable(path))
                return Path.GetFullPath(path);
        }
        return null;
    }

    private bool isExecutable(string path)
    {
        try
        {
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.Directory) != 0)
                return false;
            if (OperatingSystem.IsWindows())
                return true;
            UnixFileMode mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static List<string> tokenizeDesktopExec(string command)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        bool escaped = false;
        foreach (char character in command)
        {
            if (escaped)
            {
                current.Append(character);
                escaped = false;
                continue;
            }
            if (character == '\\')
            {
                escaped = true;
                continue;
            }
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(character);
        }

        if (escaped || quoted)
            throw new FormatException("The desktop entry command is incomplete.");
        if (current.Length > 0)
            tokens.Add(current.ToString());
        return tokens;
    }

    private static OSPlatform currentPlatform()
    {
        if (OperatingSystem.IsWindows()) return OSPlatform.Windows;
        if (OperatingSystem.IsMacOS()) return OSPlatform.OSX;
        return OSPlatform.Linux;
    }
}

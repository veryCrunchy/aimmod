using System.Diagnostics;
using System.Globalization;

namespace AimMod.Desktop.Creator;

internal static class LocalFootagePlayer
{
    internal static void ValidateFile(string path)
    {
        if (!FootageIndex.IsSupportedLocation(path) || FootageIndex.TryVideoUri(path, out _))
            throw new InvalidOperationException("Choose a local video file.");
        if (!File.Exists(path))
            throw new InvalidOperationException("This video has moved or is unavailable. Update its file path in Adjust recording.");
    }

    internal static string? FindPlayer()
    {
        foreach (string name in new[] { "vlc", "mpv", "ffplay" })
        {
            string file = name + (OperatingSystem.IsWindows() ? ".exe" : "");
            var directories = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).AsEnumerable();
            if (name == "vlc" && OperatingSystem.IsWindows())
                directories = directories.Concat(new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
                    .Select(f => Path.Combine(Environment.GetFolderPath(f), "VideoLAN", "VLC")));
            foreach (string directory in directories)
            {
                try
                {
                    string candidate = Path.GetFullPath(Path.Combine(directory.Trim().Trim('"'), file));
                    if (File.Exists(candidate)) return candidate;
                }
                catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException) { }
            }
        }
        return null;
    }

    internal static ProcessStartInfo StartInfo(string executable, string path, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > FootageIndex.MaximumDurationSeconds)
            throw new ArgumentOutOfRangeException(nameof(seconds));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        string time = seconds.ToString("0.###", CultureInfo.InvariantCulture);
        string name = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
        string[] options = name switch
        {
            "vlc" => ["--no-one-instance", "--no-video-title-show", "--start-time=" + time, "--", path],
            "mpv" => ["--start=" + time, "--", path],
            "ffplay" => ["-ss", time, "-autoexit", "-i", path],
            _ => throw new ArgumentException("Unsupported video player.", nameof(executable)),
        };
        foreach (string option in options) start.ArgumentList.Add(option);
        return start;
    }

    // Default file associations have no portable seek argument. Keep the time
    // on the clipboard and report this fallback instead of claiming a seek.
    internal static bool Open(string path, double seconds)
    {
        ValidateFile(path);
        string? player = FindPlayer();
        using var process = Process.Start(player is null
            ? new ProcessStartInfo(path) { UseShellExecute = true }
            : StartInfo(player, path, seconds));
        if (process is null) throw new InvalidOperationException("Your video player could not open this recording.");
        return player is not null;
    }
}

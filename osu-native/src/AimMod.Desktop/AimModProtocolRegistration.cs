using System.Diagnostics;
using Microsoft.Win32;

namespace AimMod.Desktop;

internal static class AimModProtocolRegistration
{
    internal static string WindowsCommand(string executable) => $"\"{executable}\" \"%1\"";

    internal static string LinuxDesktopEntry(string executable)
    {
        string escaped = executable.Replace("\\", "\\\\\\\\").Replace("\"", "\\\\\"")
                                   .Replace("`", "\\\\`").Replace("$", "\\\\$").Replace("%", "%%");
        return "[Desktop Entry]\nType=Application\nName=AimMod for osu!\nNoDisplay=true\nTerminal=false\n"
               + $"Exec=\"{escaped}\" %u\nMimeType=x-scheme-handler/aimmod-osu;\n";
    }

    public static void Refresh()
    {
        string? executable = Environment.ProcessPath;
        if (executable is null || !string.Equals(Path.GetFileNameWithoutExtension(executable), "AimMod", StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\aimmod-osu");
                key.SetValue("", "URL:AimMod osu link");
                key.SetValue("URL Protocol", "");
                using RegistryKey command = key.CreateSubKey(@"shell\open\command");
                command.SetValue("", WindowsCommand(executable));
            }
            else if (OperatingSystem.IsLinux())
            {
                // AppImage mounts are temporary; register the persistent outer image instead.
                executable = Environment.GetEnvironmentVariable("APPIMAGE") ?? executable;
                if (!Path.IsPathFullyQualified(executable) || executable.Any(char.IsControl))
                    return;
                string? dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                if (string.IsNullOrEmpty(dataHome) || !Path.IsPathFullyQualified(dataHome))
                    dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
                string applications = Path.Combine(dataHome, "applications");
                Directory.CreateDirectory(applications);
                File.WriteAllText(Path.Combine(applications, "aimmod-osu.desktop"), LinuxDesktopEntry(executable));
                var start = new ProcessStartInfo("xdg-mime") { UseShellExecute = false, CreateNoWindow = true };
                foreach (string argument in new[] { "default", "aimmod-osu.desktop", "x-scheme-handler/aimmod-osu" })
                    start.ArgumentList.Add(argument);
                using Process? process = Process.Start(start);
                if (process is not null && !process.WaitForExit(3000))
                    process.Kill();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"Could not register AimMod links: {error.Message}");
        }
    }
}

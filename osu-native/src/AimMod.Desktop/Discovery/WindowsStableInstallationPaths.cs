using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Security;

namespace AimMod.Desktop.Discovery;

internal static class WindowsStableInstallationPaths
{
    public static IReadOnlyList<string> Read() => OperatingSystem.IsWindows() ? ReadWindows() : [];

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> ReadWindows()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var registry = RegistryKey.OpenBaseKey(hive, view);
                foreach (string application in new[] { "osu", "osu!", "osu!play", "osu!replay" })
                {
                    using var command = registry.OpenSubKey($@"Software\Classes\{application}\shell\open\command");
                    if (RootFromCommand(command?.GetValue(null) as string) is { } root) roots.Add(root);
                }
                using var uninstall = registry.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\osu!");
                if (uninstall?.GetValue("InstallLocation") is string location && Path.IsPathFullyQualified(location)) roots.Add(location.Trim('"'));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { }
        }
        // These are untrusted hints. OsuStableDiscoveryService verifies the database,
        // configured Songs directory and canonical paths before accepting any root.
        return roots.ToArray();
    }

    internal static string? RootFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string text = command.Trim();
        string executable;
        if (text.StartsWith('"'))
        {
            int end = text.IndexOf('"', 1);
            if (end < 0) return null;
            executable = text[1..end];
        }
        else
        {
            int end = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (end < 0 || end + 4 < text.Length && !char.IsWhiteSpace(text[end + 4])) return null;
            executable = text[..(end + 4)];
        }
        executable = executable.Replace('/', '\\');
        int slash = executable.LastIndexOf('\\');
        if (slash < 0 || !executable[(slash + 1)..].Equals("osu!.exe", StringComparison.OrdinalIgnoreCase) ||
            !OsuDiscoveryPath.IsAbsolute(OsuHostPlatform.Windows, executable)) return null;
        return executable[..slash];
    }
}

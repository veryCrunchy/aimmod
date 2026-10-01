using System.Diagnostics;
using Microsoft.Win32;

namespace AimMod.Setup;

// The per-user "Apps & features" entry (HKCU, no admin) and the copy of the
// installer it points to (%LOCALAPPDATA%\AimMod\Setup\AimMod-Setup.exe), so
// AimMod can be updated, repaired or removed from Windows Settings after the
// downloaded installer is gone. Both are removed by Uninstall.
sealed class AppRegistration(string? keyPath = null, string? setupFolder = null)
{
    public const string DefaultKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AimMod.KovaaKs";
    public const string DisplayName = "AimMod for KovaaK's";
    public const string ExeName = "AimMod-Setup.exe";
    public string KeyPath { get; } = keyPath ?? DefaultKey;
    public string SetupFolder { get; } = setupFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AimMod", "Setup");
    public string SetupExe => Path.Combine(SetupFolder, ExeName);

    static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    // Keeps the newest installer in the Setup folder: an older download that
    // runs later never replaces a newer copy.
    public string Register(string win64, string version, long sizeBytes, string? runningExe, AimMod.InGame.SemanticVersion runningVersion)
    {
        Directory.CreateDirectory(SetupFolder);
        if (runningExe is not null && File.Exists(runningExe) && !Same(runningExe, SetupExe) && !CopyIsNewer(runningVersion))
        {
            try { File.Copy(runningExe, SetupExe + ".new", true); File.Move(SetupExe + ".new", SetupExe, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var exe = File.Exists(SetupExe) ? SetupExe : runningExe ?? SetupExe;
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
        key.SetValue("DisplayName", DisplayName);
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "AimMod");
        key.SetValue("DisplayIcon", $"\"{exe}\",0");
        key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{exe}\" --uninstall");
        key.SetValue("ModifyPath", $"\"{exe}\"");
        key.SetValue("InstallLocation", win64);
        key.SetValue("URLInfoAbout", FeedCheck.Repository);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("EstimatedSize", (int)Math.Clamp(sizeBytes / 1024, 0, int.MaxValue), RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        return exe;
    }
    bool CopyIsNewer(AimMod.InGame.SemanticVersion running)
    {
        try
        {
            var text = File.Exists(SetupExe) ? FileVersionInfo.GetVersionInfo(SetupExe).ProductVersion : null;
            return text is not null && AimMod.InGame.SemanticVersion.TryParse(text.Split('+')[0], out var copy) && copy > running;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException) { return false; }
    }

    public bool IsRegistered { get { using var key = Registry.CurrentUser.OpenSubKey(KeyPath); return key is not null; } }
    public string? GameFolder
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(KeyPath); return key?.GetValue("InstallLocation") as string; }
    }

    // Removes the entry and the installer copy. Returns true when the copy is
    // the running process: call DeleteCopyAfterExit as this process exits.
    public bool Unregister(string? runningExe)
    {
        Registry.CurrentUser.DeleteSubKeyTree(KeyPath, false);
        if (!Directory.Exists(SetupFolder)) return false;
        if (runningExe is not null && Same(Path.GetDirectoryName(runningExe)!, SetupFolder)) return true;
        try { Directory.Delete(SetupFolder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return false;
    }
    // A hidden cmd.exe waits for this process to end, then removes the Setup folder.
    public void DeleteCopyAfterExit()
    {
        var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        info.Arguments = $"/d /c for /l %i in (1,1,30) do (ping -n 2 127.0.0.1 >nul & rmdir /s /q \"{SetupFolder}\" 2>nul & if not exist \"{SetupFolder}\" exit /b 0)";
        try { Process.Start(info)?.Dispose(); } catch (System.ComponentModel.Win32Exception) { }
    }
}

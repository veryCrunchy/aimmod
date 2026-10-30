using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace AimMod.Desktop.PpTargets;

/// <summary>What the player chose about the Defender exclusion. Kept locally; Windows does not let a normal user read exclusions back.</summary>
public sealed record DefenderExclusionState(bool Added = false, DateTimeOffset? ChangedAt = null, bool Dismissed = false);

public enum DefenderExclusionOutcome { Done, Cancelled, Failed }

public sealed record DefenderExclusionResult(DefenderExclusionOutcome Outcome, string Message);

/// <summary>
/// Microsoft Defender checks every beatmap file a PP target scan writes, which can take longer than the calculation.
/// On request, and only after Windows asks the player for permission, this adds AimMod's own PP target cache folders to
/// Defender's exclusions with one visible PowerShell command, and can remove them again. Nothing else is excluded.
/// </summary>
public sealed class DefenderScanExclusion
{
    private readonly string statePath;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<int>> run;

    public DefenderScanExclusion(IReadOnlyList<string> folders, string statePath, Func<ProcessStartInfo, CancellationToken, Task<int>>? run = null)
    {
        if (folders.Count == 0 || folders.Any(folder => !Path.IsPathFullyQualified(folder) || folder.Contains('"')))
            throw new ArgumentException("Exclusions need absolute folder paths.", nameof(folders));
        Folders = folders.Select(folder => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder))).ToArray();
        this.statePath = statePath;
        this.run = run ?? runElevated;
    }

    public IReadOnlyList<string> Folders { get; }

    public static bool Supported => OperatingSystem.IsWindows();

    /// <summary>Whether Microsoft Defender's engine is running. Unknown counts as running, so the offer is not hidden by mistake.</summary>
    public static bool DefenderRunning()
    {
        if (!Supported) return false;
        try
        {
            Process[] engines = Process.GetProcessesByName("MsMpEng");
            foreach (Process engine in engines) engine.Dispose();
            return engines.Length > 0;
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception) { return true; }
    }

    public DefenderExclusionState Load()
    {
        try { return File.Exists(statePath) ? JsonSerializer.Deserialize<DefenderExclusionState>(File.ReadAllText(statePath)) ?? new() : new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save(DefenderExclusionState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statePath))!);
        File.WriteAllText(statePath + ".tmp", JsonSerializer.Serialize(state));
        File.Move(statePath + ".tmp", statePath, true);
    }

    public Task<DefenderExclusionResult> AddAsync(CancellationToken cancellationToken = default) => changeAsync(true, cancellationToken);

    public Task<DefenderExclusionResult> RemoveAsync(CancellationToken cancellationToken = default) => changeAsync(false, cancellationToken);

    private async Task<DefenderExclusionResult> changeAsync(bool add, CancellationToken cancellationToken)
    {
        if (!Supported) return new(DefenderExclusionOutcome.Failed, "Microsoft Defender exclusions are only available on Windows.");
        int exit;
        try
        {
            exit = await run(new ProcessStartInfo("powershell.exe", Arguments(add, Folders))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Normal,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            return new(DefenderExclusionOutcome.Cancelled, "Windows did not get permission, so nothing changed.");
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        {
            return new(DefenderExclusionOutcome.Failed, $"PowerShell could not start: {error.Message}");
        }
        if (exit != 0)
            return new(DefenderExclusionOutcome.Failed, add
                ? "Microsoft Defender did not accept the exclusions. Your organisation may manage Defender on this PC."
                : "Microsoft Defender did not remove the exclusions. They can be removed in Windows Security.");
        try { Save(Load() with { Added = add, ChangedAt = DateTimeOffset.Now, Dismissed = add || Load().Dismissed }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return new(DefenderExclusionOutcome.Done, add
            ? "Done. Scans no longer wait for Defender to check each beatmap file."
            : "Removed. Defender checks these folders again.");
    }

    /// <summary>
    /// The visible PowerShell command: it prints what it changes, runs one Defender cmdlet for exactly these folders
    /// and exits 0 on success. Plain text, never an encoded command, so anyone can read what runs.
    /// </summary>
    public static string Arguments(bool add, IReadOnlyList<string> folders)
    {
        string list = string.Join(",", folders.Select(folder => "'" + folder.Replace("'", "''") + "'"));
        string echo = string.Join("; ", folders.Select(folder => "Write-Host '  " + folder.Replace("'", "''") + "'"));
        string script = add
            ? "Write-Host 'AimMod is adding its PP target cache folders to Microsoft Defender exclusions:'; " + echo + "; "
              + "try { Add-MpPreference -ExclusionPath " + list + " -ErrorAction Stop; Write-Host 'Done. You can remove them again in AimMod Settings.'; Start-Sleep -Seconds 3; exit 0 } "
              + "catch { Write-Host ('Could not add the exclusions: ' + $_.Exception.Message); Start-Sleep -Seconds 8; exit 1 }"
            : "Write-Host 'AimMod is removing its PP target cache folders from Microsoft Defender exclusions:'; " + echo + "; "
              + "try { Remove-MpPreference -ExclusionPath " + list + " -ErrorAction Stop; Write-Host 'Removed.'; Start-Sleep -Seconds 3; exit 0 } "
              + "catch { Write-Host ('Could not remove the exclusions: ' + $_.Exception.Message); Start-Sleep -Seconds 8; exit 1 }";
        return "-NoProfile -Command \"" + script + "\"";
    }

    private static async Task<int> runElevated(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell did not start.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }
}

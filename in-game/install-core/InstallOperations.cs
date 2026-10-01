using System.Text.Json;
using System.Text.Json.Serialization;

namespace AimMod.InGame;

sealed record LifecycleRequests(
    [property: JsonPropertyName("repair")] bool Repair = false,
    [property: JsonPropertyName("install")] bool Install = false,
    [property: JsonPropertyName("skipVersion")] string? SkipVersion = null);
sealed record LifecycleResult(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("at")] string At,
    [property: JsonPropertyName("seen")] bool Seen = false);

// The local install state under the data folder (default
// %LOCALAPPDATA%\AimMod\KovaaksNative), shared by the service, its command
// line and the installer (AimMod-Setup.exe):
//   package\current, package\previous   verified copies for repair and rollback
//   updates\requests.json, last-result.json, pending-confirmation.json
//   ..\Repair-AimMod.cmd                 placed by every install
static class InstallState
{
    // Held by every process that changes the game folder (service command
    // line, post-exit applier, installer), so two installs never overlap.
    public const string MutexName = "Local\\AimMod.KovaaksNative.Installer";
    public static string DefaultOutput => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AimMod", "KovaaksNative");
    public static string UpdatesRoot(string output) => Path.Combine(output, "updates");
    public static string PackageCache(string output) => Path.Combine(output, "package", "current");
    public static string PreviousPackageCache(string output) => Path.Combine(output, "package", "previous");
    public static string RequestsPath(string updatesRoot) => Path.Combine(updatesRoot, "requests.json");
    public static string ResultPath(string updatesRoot) => Path.Combine(updatesRoot, "last-result.json");
    public static string ConfirmPath(string updatesRoot) => Path.Combine(updatesRoot, "pending-confirmation.json");
    // Next to the data folder: %LOCALAPPDATA%\AimMod\Repair-AimMod.cmd by default.
    public static string RepairShortcut(string output) => Path.Combine(Path.GetDirectoryName(output.TrimEnd('\\'))!, "Repair-AimMod.cmd");
    public static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

    public static T? ReadJson<T>(string path) where T : class
    {
        try { return File.Exists(path) && new FileInfo(path).Length <= 64 * 1024 ? JsonSerializer.Deserialize<T>(File.ReadAllBytes(path)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public static void WriteJson<T>(string path, T value) => AtomicFile.WriteBytes(path, JsonSerializer.SerializeToUtf8Bytes(value), durable: true);
    public static void Record(string updatesRoot, LifecycleResult result) => WriteJson(ResultPath(updatesRoot), result);
    public static void UpdateRequests(string updatesRoot, Func<LifecycleRequests, LifecycleRequests> change) =>
        WriteJson(RequestsPath(updatesRoot), change(ReadJson<LifecycleRequests>(RequestsPath(updatesRoot)) ?? new()));

    public static ReleaseManifest? CachedRelease(string output)
    {
        try
        {
            var path = Path.Combine(PackageCache(output), InstallLayout.PackageManifest);
            return File.Exists(path) ? ReleaseManifest.Parse(File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ReleaseFormatException) { return null; }
    }

    // A folder the user picked: Binaries\Win64 itself, the FPSAimTrainer game
    // folder, or the Steam "FPSAimTrainer" folder above it.
    public static string ResolveGameDir(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var candidate in new[] { full, Path.Combine(full, "FPSAimTrainer", "Binaries", "Win64"), Path.Combine(full, "Binaries", "Win64") })
            if (InstallLayout.IsWin64(candidate)) return candidate;
        return full;
    }
}

// The steps around a PackageApplier transaction that every install, update
// and repair shares, whoever runs it (the service's command line, its
// post-exit hand-off or the installer).
static class InstallOperations
{
    // Applies a verified package, then keeps it as package\current (the old
    // copy becomes package\previous), places Repair-AimMod.cmd and drops a
    // staged update that is not newer than what is now installed, so it is
    // never applied over a newer install.
    public static PackageApplier.Result Apply(string win64, string output, VerifiedPackage package, string kind, Func<string, bool>? gameRunning = null)
    {
        var updatesRoot = InstallState.UpdatesRoot(output);
        Directory.CreateDirectory(updatesRoot);
        var result = new PackageApplier(updatesRoot, gameRunning).Apply(win64, package, kind);
        CachePackage(output, package);
        InstallRepairShortcut(output, package);
        var updater = new Updater(updatesRoot);
        if (updater.Staged() is { } staged && SemanticVersion.Parse(staged.Version) <= package.Manifest.SemVer) updater.ClearStaged();
        return result;
    }

    // Removes what AimMod placed in the game (PackageApplier.Uninstall) and
    // its package copies and staged updates. History, replays and settings in
    // the data folder are kept.
    public static int Uninstall(string win64, string output, bool force = false, Func<string, bool>? gameRunning = null)
    {
        var kept = new PackageApplier(InstallState.UpdatesRoot(output), gameRunning).Uninstall(win64, force);
        foreach (var dir in new[] { Path.Combine(output, "package"), Path.Combine(output, "updates", "staged") })
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { }
        foreach (var file in new[] { Path.Combine(output, "updates", "staged.json"), InstallState.RepairShortcut(output) })
            try { File.Delete(file); } catch (IOException) { }
        return kept;
    }

    // package\current is the verified copy used for repairs; package\previous
    // is kept for a rollback.
    public static void CachePackage(string output, VerifiedPackage package)
    {
        var current = InstallState.PackageCache(output);
        if (string.Equals(Path.GetFullPath(package.Root).TrimEnd('\\'), Path.GetFullPath(current).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
        var previous = InstallState.PreviousPackageCache(output);
        var incoming = current + ".incoming";
        if (Directory.Exists(incoming)) Directory.Delete(incoming, true);
        CopyTree(package.Root, incoming);
        if (Directory.Exists(current))
        {
            if (Directory.Exists(previous)) Directory.Delete(previous, true);
            Directory.Move(current, previous);
        }
        Directory.Move(incoming, current);
    }
    // After a rollback package\previous becomes package\current again.
    public static void RestorePreviousCache(string output)
    {
        var current = InstallState.PackageCache(output); var previous = InstallState.PreviousPackageCache(output);
        if (!Directory.Exists(previous)) return;
        if (Directory.Exists(current)) Directory.Delete(current, true);
        Directory.Move(previous, current);
    }
    static void CopyTree(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), true);
    }
    // %LOCALAPPDATA%\AimMod\Repair-AimMod.cmd: works after a game update even
    // when the mod no longer loads (it runs the cached package).
    static void InstallRepairShortcut(string output, VerifiedPackage package)
    {
        var source = Path.Combine(package.Root, "Repair-AimMod.cmd");
        try { if (File.Exists(source)) File.Copy(source, InstallState.RepairShortcut(output), true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

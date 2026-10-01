using System.Text.Json;
using System.Text.Json.Serialization;

namespace AimMod.InGame;

// A package folder (an unzipped release or a staged update):
//   aimmod-release.json, aimmod-release.json.sig, files\<Win64-relative path>
// Opening it checks the signature when one is present (and requires it when
// asked) and checks every file's size and SHA-256 against the manifest.
sealed record VerifiedPackage(string Root, ReleaseManifest Manifest, string ManifestSha256, bool Signed)
{
    public string FilePath(ReleaseFile file) => ReleasePaths.Combine(Path.Combine(Root, "files"), file.Path);

    public static VerifiedPackage Open(string root, ReleaseTrust trust, bool requireSignature, string? expectedManifestSha256 = null)
    {
        var manifestPath = Path.Combine(root, InstallLayout.PackageManifest);
        if (!File.Exists(manifestPath)) throw new ReleaseFormatException("The package has no release manifest.");
        if (new FileInfo(manifestPath).Length > ReleaseManifest.MaximumBytes) throw new ReleaseFormatException("Release manifest has an invalid size.");
        var bytes = File.ReadAllBytes(manifestPath);
        var sha = Sha256Hex.Of(bytes);
        if (expectedManifestSha256 is not null && !Sha256Hex.Same(sha, expectedManifestSha256)) throw new ReleaseFormatException("The release manifest does not match the update feed.");
        var signaturePath = manifestPath + ".sig";
        var signed = false;
        if (File.Exists(signaturePath) && new FileInfo(signaturePath).Length <= 512)
        {
            if (trust.Configured)
            {
                if (!trust.Verify(bytes, File.ReadAllBytes(signaturePath))) throw new ReleaseFormatException("The release signature is not valid.");
                signed = true;
            }
        }
        if (requireSignature && !signed) throw new ReleaseFormatException("The release is not signed by a trusted AimMod key.");
        var manifest = ReleaseManifest.Parse(bytes);
        var package = new VerifiedPackage(Path.GetFullPath(root), manifest, sha, signed);
        foreach (var file in manifest.Files)
        {
            var path = package.FilePath(file);
            var info = new FileInfo(path);
            if (!info.Exists) throw new ReleaseFormatException($"The package is missing {file.Path}.");
            if (info.Length != file.Size || !Sha256Hex.Same(Sha256Hex.OfFile(path), file.Sha256)) throw new ReleaseFormatException($"{file.Path} does not match the release manifest.");
        }
        return package;
    }
}

sealed class InstallException(string message) : Exception(message);
// Test hook only: escapes the applier's rollback like a power loss would.
sealed class SimulatedCrashException : Exception;

// Applies a verified package to FPSAimTrainer\Binaries\Win64 as one
// transaction. Every file that will change is copied to a backup folder and
// listed in a journal before anything is touched; any failure (including a
// crash, recovered on the next run) restores exactly the previous state.
sealed class PackageApplier(string stateRoot, Func<string, bool>? gameRunning = null)
{
    readonly Func<string, bool> gameRunning = gameRunning ?? InstallLayout.GameRunning;
    public string BackupRoot => Path.Combine(stateRoot, "backups");
    // Test hook: called after each placed file, may throw to simulate failure.
    internal Action<string>? AfterFile;

    sealed record JournalEntry(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("existed")] bool Existed);
    sealed record Journal(
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("win64")] string Win64,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("previousVersion")] string? PreviousVersion,
        [property: JsonPropertyName("createdAt")] string CreatedAt,
        [property: JsonPropertyName("entries")] JournalEntry[] Entries,
        [property: JsonPropertyName("createdDirectories")] string[] CreatedDirectories);

    static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
    static string Win(string releasePath) => ReleasePaths.ToWindows(releasePath);
    static string Under(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new InstallException("Path escapes the game folder.");
        return full;
    }
    void RequireGameClosed(string win64)
    {
        if (gameRunning(win64)) throw new InstallException("KovaaK's is running. Close the game first.");
    }

    public sealed record Result(string Version, string? PreviousVersion, int Changed, string BackupFolder);

    public Result Apply(string win64, VerifiedPackage package, string mode)
    {
        win64 = Path.GetFullPath(win64);
        if (!InstallLayout.IsWin64(win64)) throw new InstallException("KovaaK's was not found in this folder.");
        RequireGameClosed(win64);
        RecoverInterrupted();
        var previous = InstallManifest.Read(win64);
        var manifest = package.Manifest;
        var owned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (previous is not null) foreach (var (path, sha) in previous.Files) owned[path] = sha;
        var backups = previous?.Backups is { } b ? new Dictionary<string, string>(b, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);

        // Plan every change first.
        var placements = new List<(string Relative, string Source, string Sha)>();
        var foreignBackups = new List<string>();
        foreach (var file in manifest.Files)
        {
            var relative = Win(file.Path);
            var dest = Under(win64, relative);
            if (File.Exists(dest) && Sha256Hex.Same(Sha256Hex.OfFile(dest), file.Sha256)) continue;
            // Keep anything AimMod did not place before replacing it (restored by uninstall).
            if (File.Exists(dest) && !owned.ContainsKey(relative) && !backups.ContainsKey(relative)) foreignBackups.Add(relative);
            placements.Add((relative, package.FilePath(file), file.Sha256));
        }
        var shipped = new HashSet<string>(manifest.Files.Select(f => Win(f.Path)), StringComparer.OrdinalIgnoreCase);
        var obsolete = owned.Where(kv => !shipped.Contains(kv.Key) && File.Exists(Under(win64, kv.Key)) && Sha256Hex.Same(Sha256Hex.OfFile(Under(win64, kv.Key)), kv.Value)).Select(kv => kv.Key).ToList();
        var modLists = new[] { @"ue4ss\Mods\mods.txt", @"ue4ss\Mods\mods.json" };
        var touched = placements.Select(p => p.Relative).Concat(foreignBackups.Select(f => f + ".aimmod-backup")).Concat(obsolete)
            .Concat(modLists).Concat(previous is null ? modLists.Select(l => l + ".aimmod-backup") : []).Append(@"ue4ss\" + InstallLayout.ManifestName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Back up and journal before the first change.
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + manifest.Version;
        var backupFolder = Path.Combine(BackupRoot, stamp);
        Directory.CreateDirectory(Path.Combine(backupFolder, "files"));
        var entries = new List<JournalEntry>();
        foreach (var relative in touched)
        {
            var dest = Under(win64, relative);
            var existed = File.Exists(dest);
            if (existed)
            {
                var copy = Under(Path.Combine(backupFolder, "files"), relative);
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(dest, copy, true);
            }
            entries.Add(new(relative, existed));
        }
        var createdDirs = new List<string>();
        var journal = new Journal("applying", win64, manifest.Version, previous?.Version, Now(), entries.ToArray(), []);
        WriteJournal(backupFolder, journal);
        try
        {
            RequireGameClosed(win64);
            foreach (var relative in foreignBackups)
                File.Copy(Under(win64, relative), Under(win64, relative + ".aimmod-backup"), true);
            foreach (var relative in foreignBackups) backups[relative] = relative + ".aimmod-backup";
            foreach (var (relative, source, sha) in placements)
            {
                var dest = Under(win64, relative);
                EnsureDirectory(win64, Path.GetDirectoryName(dest)!, createdDirs);
                WriteJournal(backupFolder, journal with { CreatedDirectories = createdDirs.ToArray() });
                var temporary = dest + ".aimmod-new";
                File.Copy(source, temporary, true);
                File.Move(temporary, dest, true);
                AfterFile?.Invoke(relative);
            }
            foreach (var relative in obsolete) File.Delete(Under(win64, relative));

            var modsDir = Path.Combine(win64, "ue4ss", "Mods");
            var createdLists = previous?.CreatedModLists.ToList() ?? [];
            if (previous is null)
                foreach (var list in modLists)
                {
                    var dest = Under(win64, list);
                    if (File.Exists(dest)) { File.Copy(dest, dest + ".aimmod-backup", true); backups[list] = list + ".aimmod-backup"; }
                    else createdLists.Add(list);
                }
            var removedMods = (previous?.Mods ?? []).Where(m => !manifest.Mods.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
            ModList.Write(modsDir, manifest.Mods, removedMods);

            var record = new InstallManifest
            {
                Product = "AimModCore", InstalledAt = Now(), Ue4ss = manifest.Ue4ss.Version, Ue4ssZipSha256 = manifest.Ue4ss.ZipSha256.ToUpperInvariant(),
                LuaUi = manifest.Mods.Contains("AimModNativeUI", StringComparer.OrdinalIgnoreCase),
                Files = manifest.Files.Select(f => (Win(f.Path), f.Sha256.ToUpperInvariant())).ToList(),
                Backups = backups,
                CreatedDirectories = (previous?.CreatedDirectories ?? []).Concat(createdDirs).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                CreatedModLists = createdLists.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Version = manifest.Version, Channel = manifest.Channel, ReleaseManifestSha256 = package.ManifestSha256, Mods = manifest.Mods.ToList(),
            };
            AtomicFile.WriteText(InstallLayout.ManifestPath(win64), record.ToJson());

            foreach (var file in manifest.Files)
                if (!Sha256Hex.Same(Sha256Hex.OfFile(Under(win64, Win(file.Path))), file.Sha256))
                    throw new InstallException($"{file.Path} did not install correctly.");
            WriteJournal(backupFolder, journal with { State = "applied", CreatedDirectories = createdDirs.ToArray() });
        }
        catch (Exception failure) when (failure is not SimulatedCrashException)
        {
            try { Restore(backupFolder, journal with { CreatedDirectories = createdDirs.ToArray() }, "failed"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new InstallException($"{Describe(failure)} Restoring the previous install also failed ({ex.Message}); run Repair-AimMod again with the game closed."); }
            throw new InstallException(Describe(failure) + " The previous install was restored.");
        }
        Prune();
        return new(manifest.Version, previous?.Version, placements.Count, backupFolder);
    }
    static string Describe(Exception ex) => ex is InstallException or ReleaseFormatException ? ex.Message : $"Install failed ({ex.GetType().Name}: {ex.Message}).";

    static void EnsureDirectory(string win64, string folder, List<string> created)
    {
        var missing = new Stack<string>();
        for (var walk = folder; !Directory.Exists(walk); walk = Path.GetDirectoryName(walk)!) missing.Push(walk);
        while (missing.Count > 0)
        {
            var dir = missing.Pop();
            Directory.CreateDirectory(dir);
            created.Add(Path.GetRelativePath(win64, dir));
        }
    }
    static void WriteJournal(string backupFolder, Journal journal) =>
        AtomicFile.WriteBytes(Path.Combine(backupFolder, "journal.json"), JsonSerializer.SerializeToUtf8Bytes(journal, new JsonSerializerOptions { WriteIndented = true }), durable: true);
    static Journal? ReadJournal(string backupFolder)
    {
        var path = Path.Combine(backupFolder, "journal.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(path)); } catch (JsonException) { return null; }
    }
    void Restore(string backupFolder, Journal journal, string finalState)
    {
        foreach (var entry in journal.Entries.Reverse())
        {
            var dest = Under(journal.Win64, entry.Path);
            if (entry.Existed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(Under(Path.Combine(backupFolder, "files"), entry.Path), dest, true);
            }
            else if (File.Exists(dest)) File.Delete(dest);
            var temporary = dest + ".aimmod-new";
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        foreach (var dir in journal.CreatedDirectories.OrderByDescending(d => d.Length))
        {
            var full = Under(journal.Win64, dir);
            if (Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any()) Directory.Delete(full);
        }
        WriteJournal(backupFolder, journal with { State = finalState });
    }

    IEnumerable<(string Folder, Journal Journal)> Journals() =>
        Directory.Exists(BackupRoot)
            ? Directory.GetDirectories(BackupRoot).OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                .Select(d => (d, ReadJournal(d))).Where(x => x.Item2 is not null).Select(x => (x.d, x.Item2!))
            : [];

    // A crash between journal and "applied" leaves state "applying".
    public bool HasInterrupted => Journals().Any(j => j.Journal.State == "applying");
    public bool RecoverInterrupted()
    {
        var recovered = false;
        foreach (var (folder, journal) in Journals().Where(j => j.Journal.State == "applying").ToList())
        {
            RequireGameClosed(journal.Win64);
            Restore(folder, journal, "failed");
            recovered = true;
        }
        return recovered;
    }
    // The newest applied transaction that changed the version (repairs that
    // followed it re-placed the same files, so undoing it is still exact).
    static bool ChangedVersion(Journal j) => j.State == "applied" && j.PreviousVersion != j.Version;
    public string? RollbackTarget
    {
        get
        {
            var j = Journals().Where(x => x.Journal.State is "applied" or "rolled-back").Select(x => x.Journal).FirstOrDefault(x => x.State == "rolled-back" || ChangedVersion(x));
            return j is not null && ChangedVersion(j) ? (j.PreviousVersion ?? "the previous install") : null;
        }
    }
    public string RollbackLast()
    {
        var all = Journals().ToList();
        var index = all.FindIndex(x => x.Journal.State == "rolled-back" || ChangedVersion(x.Journal));
        if (index < 0 || !ChangedVersion(all[index].Journal)) throw new InstallException("There is no update to roll back.");
        var (folder, journal) = all[index];
        RequireGameClosed(journal.Win64);
        Restore(folder, journal, "rolled-back");
        foreach (var (newer, later) in all.Take(index))
            if (later.State == "applied") WriteJournal(newer, later with { State = "superseded" });
        return journal.PreviousVersion ?? "the previous install";
    }
    void Prune()
    {
        // Keep the two newest transactions and the newest version change.
        var all = Journals().ToList();
        var update = all.FirstOrDefault(x => ChangedVersion(x.Journal)).Folder;
        foreach (var (folder, _) in all.Skip(2).Where(x => x.Folder != update).ToList())
            try { Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Mirrors Uninstall-AimModCore.ps1: remove unchanged placed files, restore
    // backups, drop created mod lists and empty created folders.
    public int Uninstall(string win64, bool force = false)
    {
        win64 = Path.GetFullPath(win64);
        RequireGameClosed(win64);
        var manifest = InstallManifest.Read(win64) ?? throw new InstallException("AimMod is not installed here.");
        var kept = 0;
        foreach (var (path, sha) in manifest.Files)
        {
            var full = Under(win64, path);
            if (!File.Exists(full)) continue;
            if (!force && !Sha256Hex.Same(Sha256Hex.OfFile(full), sha)) { kept++; continue; }
            File.Delete(full);
        }
        var restored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (target, backup) in manifest.Backups)
        {
            var source = Under(win64, backup);
            if (File.Exists(source)) { File.Move(source, Under(win64, target), true); restored.Add(target); }
        }
        foreach (var list in manifest.CreatedModLists)
            if (!restored.Contains(list)) { var full = Under(win64, list); if (File.Exists(full)) File.Delete(full); }
        File.Delete(InstallLayout.ManifestPath(win64));
        if (manifest.CreatedDirectories.Contains("ue4ss", StringComparer.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(win64, "ue4ss")))
            foreach (var file in Directory.GetFiles(Path.Combine(win64, "ue4ss")))
                if (Path.GetFileName(file).Equals("UE4SS.log", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase)) File.Delete(file);
        foreach (var dir in manifest.CreatedDirectories.OrderByDescending(d => d.Length))
        {
            var full = Under(win64, dir);
            if (Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any()) Directory.Delete(full);
        }
        return kept;
    }
}

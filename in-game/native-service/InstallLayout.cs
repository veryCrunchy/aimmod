using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AimMod.InGame;

// Where AimMod lives in the game and in %LOCALAPPDATA%, and the install
// manifest (ue4ss\aimmod-install.json). The manifest keeps the fields
// Install-AimModCore.ps1 writes, so Uninstall-AimModCore.ps1 keeps working
// on installs made from a release package, and adds the release fields.
static class InstallLayout
{
    public const string GameExe = "FPSAimTrainer-Win64-Shipping";
    public const string ManifestName = "aimmod-install.json";
    public const string PackageManifest = "aimmod-release.json";

    public static string ManifestPath(string win64) => Path.Combine(win64, "ue4ss", ManifestName);

    // Install-record paths are relative to Binaries\Win64, except AimMod
    // cosmetics paks: recorded as paks\~AimMod\<name> and placed in the game's
    // own FPSAimTrainer\Content\Paks\~AimMod. Nothing else outside Win64.
    public const string GamePak = "FPSAimTrainer-WindowsNoEditor.pak";
    const string PakRecord = @"paks\", PakDir = "~AimMod";
    public static string ContentPaks(string win64) => Path.GetFullPath(Path.Combine(win64, "..", "..", "Content", "Paks"));
    public static bool IsPakRecord(string relative) => relative.StartsWith(PakRecord, StringComparison.OrdinalIgnoreCase);
    static string Inside(string root, string relative, bool allowRoot)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.GetFullPath(root).TrimEnd('\\');
        if (allowRoot && full.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return full;
        if (!full.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase)) throw new InstallException("Path escapes the game folder.");
        return full;
    }
    public static string Resolve(string win64, string relative)
    {
        if (!IsPakRecord(relative)) return Inside(win64, relative, false);
        var rest = relative[PakRecord.Length..];
        string sub;
        if (rest.Equals(PakDir, StringComparison.OrdinalIgnoreCase)) sub = "";
        else if (rest.StartsWith(PakDir + "\\", StringComparison.OrdinalIgnoreCase)) sub = rest[(PakDir.Length + 1)..];
        else throw new InstallException("Path outside the AimMod pak folder.");
        var paks = ContentPaks(win64);
        // The game's own pak must be there: this is the right game folder.
        if (!File.Exists(Path.Combine(paks, GamePak))) throw new InstallException(@"The game's Content\Paks folder was not found next to Binaries\Win64.");
        return Inside(Path.Combine(paks, PakDir), sub, true);
    }
    // The install-record path for a full path that Resolve maps back to it.
    public static string Record(string win64, string full)
    {
        var aimmod = Path.Combine(ContentPaks(win64), PakDir);
        full = Path.GetFullPath(full);
        if (full.Equals(aimmod, StringComparison.OrdinalIgnoreCase)) return PakRecord + PakDir;
        if (full.StartsWith(aimmod + "\\", StringComparison.OrdinalIgnoreCase)) return PakRecord + PakDir + "\\" + full[(aimmod.Length + 1)..];
        return Path.GetRelativePath(win64, full);
    }
    public static bool IsWin64(string? folder) => folder is not null && File.Exists(Path.Combine(folder, GameExe + ".exe"));

    // The service ships as <Win64>\ue4ss\Mods\AimModCore\service\AimMod.InGame.exe.
    public static string? FindWin64FromService(string? serviceFolder)
    {
        var folder = serviceFolder;
        for (int i = 0; i < 6 && folder is not null; i++)
        {
            if (IsWin64(folder)) return Path.GetFullPath(folder);
            folder = Path.GetDirectoryName(folder.TrimEnd('\\', '/'));
        }
        return null;
    }
    // A package root holds aimmod-release.json next to files\.
    public static string? FindPackageRoot(string? start)
    {
        var folder = start;
        for (int i = 0; i < 8 && folder is not null; i++)
        {
            if (File.Exists(Path.Combine(folder, PackageManifest)) && Directory.Exists(Path.Combine(folder, "files"))) return Path.GetFullPath(folder);
            folder = Path.GetDirectoryName(folder.TrimEnd('\\', '/'));
        }
        return null;
    }
    // Steam libraries, as Install-AimModCore.ps1 searches them.
    public static string? FindWin64FromSteam()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var libraries = new List<string>();
        foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey(hive == Microsoft.Win32.Registry.CurrentUser ? @"Software\Valve\Steam" : @"SOFTWARE\WOW6432Node\Valve\Steam");
                var path = (key?.GetValue("SteamPath") ?? key?.GetValue("InstallPath")) as string;
                if (!string.IsNullOrEmpty(path)) { libraries.Add(path.Replace('/', '\\')); break; }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        if (libraries.Count > 0)
        {
            var vdf = Path.Combine(libraries[0], "steamapps", "libraryfolders.vdf");
            try
            {
                if (File.Exists(vdf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            }
            catch (IOException) { }
        }
        foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var win64 = Path.Combine(library, "steamapps", "common", "FPSAimTrainer", "FPSAimTrainer", "Binaries", "Win64");
            if (IsWin64(win64)) return Path.GetFullPath(win64);
        }
        return null;
    }

    // Only this installation's game process matters. A process whose path
    // cannot be read is treated as this game (fail closed).
    public static bool GameRunning(string win64)
    {
        var processes = System.Diagnostics.Process.GetProcessesByName(GameExe);
        try
        {
            foreach (var process in processes)
            {
                string? path = null;
                try { path = process.MainModule?.FileName; } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
                if (path is null || string.Equals(Path.GetDirectoryName(path), win64.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    // <library>\steamapps\common\FPSAimTrainer\FPSAimTrainer\Binaries\Win64
    // -> <library>\steamapps\appmanifest_824270.acf "buildid".
    public static long? SteamBuildId(string win64)
    {
        var folder = win64;
        for (int i = 0; i < 5 && folder is not null; i++) folder = Path.GetDirectoryName(folder.TrimEnd('\\'));
        if (folder is null) return null;
        var acf = Path.Combine(folder, $"appmanifest_{ReleaseManifest.KovaaksAppId}.acf");
        try
        {
            if (!File.Exists(acf) || new FileInfo(acf).Length > 1 << 20) return null;
            var match = Regex.Match(File.ReadAllText(acf), "\"buildid\"\\s+\"(\\d{1,12})\"");
            return match.Success ? long.Parse(match.Groups[1].Value) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}

sealed class InstallManifest
{
    public string Product = "AimModCore";
    public string InstalledAt = "";
    public string Ue4ss = "";
    public string Ue4ssZipSha256 = "";
    public bool LuaUi;
    public List<(string Path, string Sha256)> Files = [];
    public Dictionary<string, string> Backups = new(StringComparer.OrdinalIgnoreCase);
    public List<string> CreatedDirectories = [];
    public List<string> CreatedModLists = [];
    // Release fields; absent on developer installs from Install-AimModCore.ps1.
    public string? Version;
    public string? Channel;
    public string? ReleaseManifestSha256;
    public List<string> Mods = [];
    public bool Managed => Version is not null && SemanticVersion.TryParse(Version, out _);

    static string Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    static IEnumerable<string> Strings(JsonNode? node) =>
        node is JsonArray array ? array.Select(Text).Where(s => s.Length > 0) : node is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? [s] : [];

    public static InstallManifest? Read(string win64)
    {
        var path = InstallLayout.ManifestPath(win64);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 4 << 20) throw new InvalidDataException("Install manifest is too large.");
        return Parse(File.ReadAllText(path));
    }
    public static InstallManifest Parse(string json)
    {
        JsonNode? root;
        // PowerShell writes UTF-8 with a BOM.
        try { root = JsonNode.Parse(json.TrimStart('\uFEFF')); } catch (JsonException) { throw new InvalidDataException("Install manifest is not valid JSON."); }
        if (root is not JsonObject o) throw new InvalidDataException("Install manifest is not an object.");
        var m = new InstallManifest
        {
            Product = Text(o["product"]), InstalledAt = Text(o["installedAt"]), Ue4ss = Text(o["ue4ss"]), Ue4ssZipSha256 = Text(o["ue4ssZipSha256"]),
            LuaUi = o["luaUi"] is JsonValue lua && lua.TryGetValue<bool>(out var b) && b,
        };
        // ConvertTo-Json writes a one-element list as a bare object.
        var files = o["files"] is JsonArray fa ? fa.ToList() : o["files"] is JsonObject single ? [single] : [];
        foreach (var file in files)
            if (file is JsonObject f && Text(f["path"]) is { Length: > 0 } p) m.Files.Add((p, Text(f["sha256"])));
        if (o["backups"] is JsonObject backups)
            foreach (var (key, value) in backups) if (Text(value) is { Length: > 0 } v) m.Backups[key] = v;
        m.CreatedDirectories.AddRange(Strings(o["createdDirectories"]));
        m.CreatedModLists.AddRange(Strings(o["createdModLists"]));
        if (Text(o["version"]) is { Length: > 0 } version) m.Version = version;
        if (Text(o["channel"]) is { Length: > 0 } channel) m.Channel = channel;
        if (Text(o["releaseManifestSha256"]) is { Length: > 0 } sha) m.ReleaseManifestSha256 = sha;
        m.Mods.AddRange(Strings(o["mods"]));
        if (m.Mods.Count == 0) { m.Mods.Add("AimModCore"); if (m.LuaUi) m.Mods.Add("AimModNativeUI"); }
        return m;
    }
    public string ToJson()
    {
        var o = new JsonObject
        {
            ["product"] = Product, ["installedAt"] = InstalledAt, ["ue4ss"] = Ue4ss, ["ue4ssZipSha256"] = Ue4ssZipSha256, ["luaUi"] = LuaUi,
            ["files"] = new JsonArray(Files.Select(f => (JsonNode)new JsonObject { ["path"] = f.Path, ["sha256"] = f.Sha256 }).ToArray()),
            ["backups"] = new JsonObject(Backups.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["createdDirectories"] = new JsonArray(CreatedDirectories.Select(d => (JsonNode)d).ToArray()),
            ["createdModLists"] = new JsonArray(CreatedModLists.Select(d => (JsonNode)d).ToArray()),
            ["mods"] = new JsonArray(Mods.Select(d => (JsonNode)d).ToArray()),
        };
        if (Version is not null) o["version"] = Version;
        if (Channel is not null) o["channel"] = Channel;
        if (ReleaseManifestSha256 is not null) o["releaseManifestSha256"] = ReleaseManifestSha256;
        return o.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}

// mods.txt / mods.json: AimMod entries first and enabled; other entries kept
// only while their folder exists (same rules as AimModInstall.psm1).
static class ModList
{
    static readonly Regex Line = new(@"^\s*([^;:\s][^:]*?)\s*:\s*([01])\s*$", RegexOptions.CultureInvariant);
    public static IReadOnlyDictionary<string, bool> Read(string modsDir)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var txt = Path.Combine(modsDir, "mods.txt");
        if (!File.Exists(txt)) return result;
        foreach (var line in File.ReadAllLines(txt))
        {
            var m = Line.Match(line);
            if (m.Success && !result.ContainsKey(m.Groups[1].Value)) result[m.Groups[1].Value] = m.Groups[2].Value == "1";
        }
        return result;
    }
    public static void Write(string modsDir, IReadOnlyList<string> ours, IReadOnlyCollection<string> remove)
    {
        var entries = new List<(string Name, bool Enabled)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in ours) if (names.Add(name)) entries.Add((name, true));
        foreach (var (name, enabled) in Read(modsDir))
        {
            if (names.Contains(name) || remove.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (Directory.Exists(Path.Combine(modsDir, name)) && names.Add(name)) entries.Add((name, enabled));
        }
        Directory.CreateDirectory(modsDir);
        AtomicFile.WriteText(Path.Combine(modsDir, "mods.txt"), string.Join("\r\n", entries.Select(e => $"{e.Name} : {(e.Enabled ? 1 : 0)}")) + "\r\n");
        var json = new JsonObject { ["mods"] = new JsonArray(entries.Select(e => (JsonNode)new JsonObject { ["mod_name"] = e.Name, ["mod_enabled"] = e.Enabled }).ToArray()) };
        AtomicFile.WriteText(Path.Combine(modsDir, "mods.json"), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}

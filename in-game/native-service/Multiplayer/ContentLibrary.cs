using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AimMod.InGame.Multiplayer;

sealed record ScenarioInfo(string Name, string Hash, string Map, string MapHash, string MapSource, double TimeLimit, string? DefaultWeapon, string? DefaultCharacter, bool Ported, string? WorkshopId = null, double MapScale = 1);
sealed record MapInfo(string Name, string Hash, string Source);

// Read-only view of the player's KovaaK's library, used to pick lobby content
// and to check that everyone has the same scenario and map. Scenarios and
// profiles live in <game>/FPSAimTrainer/Saved/SaveGames/<Type>, custom and
// ported maps in <game>/FPSAimTrainer/maps. Nothing here writes to the game.
sealed partial class ContentLibrary : IContentResolver
{
    const long MaxScenarioBytes = 32L << 20, MaxMapBytes = 64L << 20, MaxProfileBytes = 1L << 20;
    const int MaxItems = 5000;
    readonly string? root;
    readonly Func<DateTime> clock;
    readonly Dictionary<string, (long Length, DateTime Stamp, string Hash)> hashes = new(StringComparer.OrdinalIgnoreCase);
    readonly object gate = new();
    DateTime scanned = DateTime.MinValue;
    IReadOnlyList<ScenarioInfo> scenarios = [];
    IReadOnlyList<MapInfo> maps = [];
    IReadOnlyList<LibraryItem> weapons = [], characters = [];
    Dictionary<string, string> paths = new(StringComparer.OrdinalIgnoreCase);

    public ContentLibrary(string? root, Func<DateTime>? clock = null) { this.root = root; this.clock = clock ?? (() => DateTime.UtcNow); }
    public bool Available => root is not null && Directory.Exists(root);

    public IReadOnlyList<ScenarioInfo> Scenarios { get { Refresh(); lock (gate) return scenarios; } }
    public IReadOnlyList<MapInfo> Maps { get { Refresh(); lock (gate) return maps; } }
    public IReadOnlyList<LibraryItem> Weapons { get { Refresh(); lock (gate) return weapons; } }
    public IReadOnlyList<LibraryItem> Characters { get { Refresh(); lock (gate) return characters; } }

    public ScenarioChoice? Scenario(string name) =>
        Scenarios.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } s ? new ScenarioChoice(s.Name, s.Hash, s.Map, s.MapHash, s.TimeLimit, s.WorkshopId, CsMapProblem(s.Map)) : null;

    // Why a map can't host CS competitive (null: it can): its CS map spec in
    // <game>/maps/<map>.aimmod.json. Cached per file and write time.
    readonly Dictionary<string, (DateTime Stamp, string? Problem)> csMaps = new(StringComparer.OrdinalIgnoreCase);
    public string? CsMapProblem(string mapName)
    {
        if (root is null || string.IsNullOrWhiteSpace(mapName) || mapName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return MapObjectives.NoCsData;
        var path = Path.Combine(root, "maps", MapObjectives.FileFor(mapName));
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 4 << 20) return MapObjectives.NoCsData;
            lock (csMaps)
            {
                if (csMaps.TryGetValue(path, out var known) && known.Stamp == info.LastWriteTimeUtc) return known.Problem;
                var problem = MapObjectives.Parse(File.ReadAllText(path)) is { } parsed ? parsed.CsProblem : MapObjectives.NoCsData;
                csMaps[path] = (info.LastWriteTimeUtc, problem);
                return problem;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return MapObjectives.NoCsData; }
    }
    public MapChoice? Map(string name) =>
        Maps.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } m ? new MapChoice(m.Name, m.Hash, m.Source) : null;
    public LibraryItem? Weapon(string name) => Weapons.FirstOrDefault(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    public LibraryItem? Character(string name) => Characters.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    // What this machine has for the lobby's content: ok, missing or a different version.
    public (string Scenario, string Map, string Profiles) Check(LobbySettings settings)
    {
        if (settings.Scenario is not { } wanted) return (ContentStates.None, ContentStates.None, ContentStates.None);
        var local = Scenarios.FirstOrDefault(s => s.Name.Equals(wanted.Name, StringComparison.OrdinalIgnoreCase));
        var scenario = local is null ? ContentStates.Missing : local.Hash == wanted.Hash ? ContentStates.Ok : ContentStates.Mismatch;
        string map;
        if (settings.MapOverride is { } over)
        {
            var m = Maps.FirstOrDefault(x => x.Name.Equals(over.Name, StringComparison.OrdinalIgnoreCase));
            map = m is null ? ContentStates.Missing : m.Hash == over.Hash ? ContentStates.Ok : ContentStates.Mismatch;
        }
        else
        {
            // The scenario's own map: a file in maps/, or a built-in map known by name.
            var m = Maps.FirstOrDefault(x => x.Name.Equals(wanted.Map, StringComparison.OrdinalIgnoreCase));
            map = m is not null ? (m.Hash == wanted.MapHash ? ContentStates.Ok : ContentStates.Mismatch)
                : wanted.MapHash == TextHash("builtin-map:" + wanted.Map.ToLowerInvariant()) ? ContentStates.Ok : ContentStates.Missing;
        }
        // Custom weapon and character profiles come from the host's library and must match too.
        var profiles = ContentStates.None;
        foreach (var (choice, lookup) in new[] { (settings.WeaponProfile, (Func<string, LibraryItem?>)Weapon), (settings.CharacterProfile, Character) })
        {
            if (choice.Preset != ProfilePresets.Custom || choice.Custom is null) continue;
            var have = lookup(choice.Custom);
            var state = have is null ? ContentStates.Missing : have.Hash == choice.Hash ? ContentStates.Ok : ContentStates.Mismatch;
            if (profiles is ContentStates.None or ContentStates.Ok) profiles = state;
        }
        return (scenario, map, profiles);
    }

    // Local file for a library item, for the match scenario generator. Never sent to the UI.
    internal string? PathOf(string kind, string name) { Refresh(); lock (gate) return paths.GetValueOrDefault(kind + ":" + name); }
    internal string? ScenarioFolder => root is null ? null : Path.Combine(root, "Saved", "SaveGames", "Scenarios");
    internal string? Root => root;
    // <library>/steamapps/common/FPSAimTrainer/FPSAimTrainer -> <library>/steamapps/workshop/content/824270
    string? WorkshopFolder => root is null ? null : Path.GetFullPath(Path.Combine(root, "..", "..", "..", "workshop", "content", "824270"));
    IEnumerable<(string File, string? Workshop)> WorkshopFiles()
    {
        if (WorkshopFolder is not { } folder || !Directory.Exists(folder)) yield break;
        foreach (var item in Directory.EnumerateDirectories(folder).Take(MaxItems))
        {
            var id = Path.GetFileName(item);
            if (id.Length is 0 or > 20 || !id.All(char.IsAsciiDigit)) continue;
            foreach (var file in Files(item, "*.sce").Take(8)) yield return (file, id);
        }
    }

    // The files that make up a lobby's content on this machine, for the host to offer:
    // the scenario, a custom or ported map, ability files the scenario names and custom profiles.
    internal IReadOnlyList<(string Kind, string Path)> ContentFiles(LobbySettings s)
    {
        var list = new List<(string, string)>();
        if (s.Scenario is null) return list;
        var info = Scenarios.FirstOrDefault(x => x.Hash == s.Scenario.Hash);
        if (info is null || PathOf("scenario", info.Name) is not { } scenarioPath) return list;
        list.Add(("scenario", scenarioPath));
        var map = s.MapOverride?.Name ?? (info.MapSource == "game" ? null : info.Map);
        if (map is not null && PathOf("map", map) is { } mapPath) list.Add(("map", mapPath));
        foreach (var ability in AbilityNames(scenarioPath)) if (PathOf("ability", ability) is { } abilityPath) list.Add(("ability", abilityPath));
        if (s.WeaponProfile is { Preset: ProfilePresets.Custom, Custom: { } w } && PathOf("weapon", w) is { } weaponPath) list.Add(("weapon", weaponPath));
        if (s.CharacterProfile is { Preset: ProfilePresets.Custom, Custom: { } c } && PathOf("character", c) is { } characterPath) list.Add(("character", characterPath));
        return list;
    }
    internal static IEnumerable<string> AbilityNames(string scenarioPath)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var line in File.ReadLines(scenarioPath).Take(200_000))
            {
                if (line == "[Map Data]") break;
                if (!line.StartsWith("AbilityProfileNames=", StringComparison.Ordinal)) continue;
                foreach (var n in line["AbilityProfileNames=".Length..].Split(';')) if (n.Trim().Length is > 0 and <= 128) names.Add(n.Trim());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return names;
    }

    public void Refresh(bool force = false)
    {
        lock (gate)
        {
            if (!force && clock() - scanned < TimeSpan.FromSeconds(30)) return;
            scanned = clock();
            if (!Available) return;
            try
            {
                var games = Path.Combine(root!, "Saved", "SaveGames");
                var mapFolder = Path.Combine(root!, "maps");
                var found = new List<(ScenarioInfo Info, string File)>();
                var portedMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var nextPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                // Local scenarios first, then Workshop items (steamapps/workshop/content/824270/<id>/*.sce).
                var sources = Files(Path.Combine(games, "Scenarios"), "*.sce").Select(f => (File: f, Workshop: (string?)null)).Concat(WorkshopFiles());
                foreach (var (file, workshop) in sources)
                {
                    // Match scenarios AimMod generated are not offered as lobby content.
                    if (Path.GetFileName(file).StartsWith(MatchScenario.Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    var header = ReadScenarioHeader(file);
                    if (header is null) continue;
                    var hash = Hash(file, MaxScenarioBytes);
                    if (hash is null) continue;
                    var name = header.GetValueOrDefault("Name") is { Length: > 0 and <= 128 } n ? n : Path.GetFileNameWithoutExtension(file);
                    var mapName = header.GetValueOrDefault("MapName") ?? "";
                    var limit = double.TryParse(header.GetValueOrDefault("Timelimit"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) && t is > 0 and <= 3600 ? t : 60;
                    var ported = (header.GetValueOrDefault("Description") ?? "").StartsWith("Ported Source map", StringComparison.OrdinalIgnoreCase);
                    if (ported && mapName.Length > 0) portedMaps.Add(Path.GetFileNameWithoutExtension(mapName));
                    found.Add((new ScenarioInfo(name, hash, Path.GetFileNameWithoutExtension(mapName), "", "game", limit, header.GetValueOrDefault("~weapon"), header.GetValueOrDefault("PlayerProfile"), ported, workshop,
                        double.TryParse(header.GetValueOrDefault("MapScale"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var scale) && scale is > 0 and < 100 ? scale : 1), file));
                }
                var mapList = new List<MapInfo>();
                foreach (var file in Files(mapFolder, "*.map").Concat(Files(mapFolder, "*.json")))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (Hash(file, MaxMapBytes) is not { } hash) continue;
                    if (nextPaths.TryAdd("map:" + name, file)) mapList.Add(new MapInfo(name, hash, portedMaps.Contains(name) ? "ported" : "custom"));
                }
                var mapByName = mapList.ToDictionary(m => m.Name, m => m, StringComparer.OrdinalIgnoreCase);
                var list = new List<ScenarioInfo>();
                foreach (var (info, file) in found)
                {
                    if (!nextPaths.TryAdd("scenario:" + info.Name, file)) continue;
                    // Built-in maps ship inside the game's paks; their identity is the name.
                    list.Add(mapByName.TryGetValue(info.Map, out var m) ? info with { MapHash = m.Hash, MapSource = m.Source }
                        : info with { MapHash = TextHash("builtin-map:" + info.Map.ToLowerInvariant()), MapSource = "game" });
                }
                scenarios = list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                maps = mapList.OrderBy(m => m.Source == "ported" ? 0 : 1).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                weapons = Profiles(Path.Combine(games, "Weapons"), "*.wep", "weapon", nextPaths);
                characters = Profiles(Path.Combine(games, "Characters"), "*.chr", "character", nextPaths);
                foreach (var ext in ContentRules.AbilityExtensions)
                    foreach (var file in Files(Path.Combine(games, "Abilities"), "*" + ext)) nextPaths.TryAdd("ability:" + Path.GetFileNameWithoutExtension(file), file);
                paths = nextPaths;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    IReadOnlyList<LibraryItem> Profiles(string folder, string pattern, string kind, Dictionary<string, string> into)
    {
        var items = new List<LibraryItem>();
        foreach (var file in Files(folder, pattern))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (Hash(file, MaxProfileBytes) is { } hash && into.TryAdd(kind + ":" + name, file)) items.Add(new LibraryItem(name, hash));
        }
        return items.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    static IEnumerable<string> Files(string folder, string pattern) =>
        Directory.Exists(folder) ? Directory.EnumerateFiles(folder, pattern, SearchOption.TopDirectoryOnly).Take(MaxItems) : [];

    // Header keys come before the first [Section]. The player's default weapon
    // is the first weapon of the [Character Profile] named by PlayerProfile.
    internal static Dictionary<string, string>? ReadScenarioHeader(string file)
    {
        try
        {
            if (new FileInfo(file).Length > MaxScenarioBytes) return null;
            var header = new Dictionary<string, string>(StringComparer.Ordinal);
            using var reader = new StreamReader(file, new UTF8Encoding(false, false));
            string? line, section = null, profileName = null;
            var lines = 0;
            while ((line = reader.ReadLine()) is not null && lines++ < 200_000)
            {
                if (line.StartsWith('[')) { section = line.Trim(); profileName = null; if (section == "[Map Data]") break; continue; }
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq]; var value = line[(eq + 1)..].Trim();
                if (section is null) { if (value.Length <= 512) header.TryAdd(key, value); continue; }
                if (section != "[Character Profile]") continue;
                if (key == "Name") profileName = value;
                else if (key == "WeaponProfileNames" && profileName is not null && header.TryGetValue("PlayerProfile", out var player) && player == profileName)
                    header.TryAdd("~weapon", value.Split(';')[0]);
            }
            return header.ContainsKey("Name") || header.ContainsKey("MapName") ? header : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    string? Hash(string file, long limit)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > limit) return null;
            if (hashes.TryGetValue(file, out var cached) && cached.Length == info.Length && cached.Stamp == info.LastWriteTimeUtc) return cached.Hash;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            hashes[file] = (info.Length, info.LastWriteTimeUtc, hash);
            return hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    internal static string TextHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    // The game folder: an explicit override, then Steam's library list. Read-only.
    public static string? Locate(string? explicitRoot = null)
    {
        foreach (var candidate in new[] { explicitRoot, Environment.GetEnvironmentVariable("AIMMOD_KOVAAKS_ROOT") })
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(Path.Combine(candidate, "Saved"))) return Path.GetFullPath(candidate);
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is not string steam) return null;
            var libraries = new List<string> { steam };
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf) && new FileInfo(vdf).Length < 1 << 20)
                foreach (Match m in LibraryPath().Matches(File.ReadAllText(vdf))) libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var game = Path.Combine(library, "steamapps", "common", "FPSAimTrainer", "FPSAimTrainer");
                if (Directory.Exists(Path.Combine(game, "Saved"))) return game;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return null;
    }
    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")] private static partial Regex LibraryPath();
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

// Movement presets in Source-style units (u/s, sv_* values). KovaaK's units are
// u x Scale: KovaaK's bundled "Counter-Striker" profile runs at 1100 = 250 u/s x 4.4.
// Field mapping follows the map-port tool's CS profile (in-game/tools/map-port,
// mapport/scenario.py): Quake/Source movement with scaled acceleration and
// continuous friction. Values are "-like" references; tune them in the live test.
sealed record MovementPreset(string Id, string Label, double Run, double Accelerate, double Friction, double StopSpeed,
    double AirAccelerate, double AirCap, double Gravity, double JumpSpeed, double Step, double CrouchMultiplier);
sealed record WeaponPreset(string Id, string Label, double TimeBetweenShots);

static class MatchPresets
{
    public const double Scale = 4.4, UnrealGravity = 980;
    public static readonly MovementPreset[] Movement =
    [
        new("cs", "CS", 250, 5.2, 4, 75, 10, 30, 800, 301, 18, 0.34),
        new("valorant", "Valorant", 265.7, 7, 6, 90, 4, 30, 760, 290, 18, 0.4),
        new("apex", "Apex", 287, 6, 4, 75, 15, 60, 750, 320, 18, 0.5),
        new("quake", "Quake", 320, 10, 6, 100, 1, 320, 800, 270, 18, 0.5),
    ];
    public static readonly WeaponPreset[] Weapons =
    [
        new("cs", "CS", 0.1),
        new("valorant", "Valorant", 0.1026),
        new("apex", "Apex", 0.0741),
        new("quake", "Quake", 0.05),
    ];
}

// Builds the match scenario: the host's chosen base scenario plus the lobby's
// movement, weapon, character, target and map settings. Every member builds it
// from the same inputs (all hash-checked in the lobby) with the same generator,
// so they get the same bytes. The result is written next to the user's
// scenarios under a reserved name and played in freeplay, so nothing reaches
// KovaaK's ranked leaderboards.
static class MatchScenario
{
    public const int GeneratorVersion = 1;
    public const string Prefix = "AimMod Match - ";
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    // Anything that changes what is played means a generated scenario.
    public static bool Needed(LobbySettings s) =>
        s.MapOverride is not null || s.TimeLimit is not null || s.TargetSpeed != 1 || s.TargetSize != 1
        || s.WeaponProfile.Preset != ProfilePresets.Default || s.MovementProfile.Preset != ProfilePresets.Default || s.CharacterProfile.Preset != ProfilePresets.Default;

    // A pure function of the lobby settings, which carry every input's content hash.
    public static string Key(LobbySettings s)
    {
        var parts = new object?[] { GeneratorVersion, s.Scenario?.Hash, s.Scenario?.MapHash, s.MapOverride?.Hash, s.TimeLimit,
            Num(s.TargetSpeed), Num(s.TargetSize), s.WeaponProfile.Preset, s.WeaponProfile.Hash, s.MovementProfile.Preset, s.CharacterProfile.Preset, s.CharacterProfile.Hash };
        return ContentLibrary.TextHash(JsonSerializer.Serialize(parts));
    }

    public static string Name(LobbySettings s)
    {
        var label = MatchPresets.Movement.FirstOrDefault(m => m.Id == s.MovementProfile.Preset)?.Label
            ?? MatchPresets.Weapons.FirstOrDefault(w => w.Id == s.WeaponProfile.Preset)?.Label
            ?? (s.CharacterProfile.Preset == ProfilePresets.Custom || s.WeaponProfile.Preset == ProfilePresets.Custom ? "Custom"
            : s.MapOverride is not null ? "Map" : s.TargetSpeed != 1 || s.TargetSize != 1 ? "Targets" : "Timed");
        var baseName = new string((s.Scenario?.Name ?? "Scenario").Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        if (baseName.Length > 60) baseName = baseName[..60].TrimEnd();
        return Prefix + baseName + " - " + label + " - " + Key(s)[..8];
    }

    static string Num(double value) => value.ToString("0.0#####", Invariant);

    sealed class Section
    {
        public required string Title; public List<string> Lines = [];
        public string? Get(string key) { foreach (var l in Lines) if (l.StartsWith(key + "=", StringComparison.Ordinal)) return l[(key.Length + 1)..]; return null; }
        public void Set(string key, string value)
        {
            for (var i = 0; i < Lines.Count; i++) if (Lines[i].StartsWith(key + "=", StringComparison.Ordinal)) { Lines[i] = key + "=" + value; return; }
            var at = Lines.Count; while (at > 0 && Lines[at - 1].Trim().Length == 0) at--;
            Lines.Insert(at, key + "=" + value);
        }
        public void Scale(string key, double factor)
        {
            if (factor == 1 || Get(key) is not { } raw || !double.TryParse(raw.Trim(), NumberStyles.Float, Invariant, out var v)) return;
            Set(key, Num(v * factor));
        }
    }

    // Splits a .sce into the header, the profile sections and the raw [Map Data].
    static (Section Header, List<Section> Sections, string? MapData, string NewLine) Parse(string text)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var header = new Section { Title = "" };
        var sections = new List<Section>();
        var current = header;
        string? map = null;
        var start = 0;
        while (start <= text.Length)
        {
            var end = text.IndexOf('\n', start);
            var line = (end < 0 ? text[start..] : text[start..end]).TrimEnd('\r');
            if (line == "[Map Data]") { map = end < 0 ? "" : text[(end + 1)..]; break; }
            if (line.StartsWith('[') && line.EndsWith(']')) { current = new Section { Title = line }; sections.Add(current); }
            else if (end >= 0 || line.Length > 0) current.Lines.Add(line);
            if (end < 0) break;
            start = end + 1;
        }
        return (header, sections, map, newline);
    }

    static IEnumerable<string> Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Where(l => l.Trim().Length > 0 && !l.StartsWith('['));

    public sealed record Inputs(string BaseText, LobbySettings Settings, string? MapFile = null, string? MapText = null, string? WeaponText = null, string? CharacterText = null);

    public static string Generate(Inputs input)
    {
        var s = input.Settings;
        var (header, sections, mapData, nl) = Parse(input.BaseText);
        var name = Name(s);
        header.Set("Name", name);
        header.Set("Description", "AimMod multiplayer match generated from " + (s.Scenario?.Name ?? "a scenario") + ". Played in freeplay; not a published scenario.");
        if (s.TimeLimit is { } limit) header.Set("Timelimit", Num(limit));
        var playerName = header.Get("PlayerProfile");
        var characters = sections.Where(x => x.Title == "[Character Profile]").ToList();
        var player = characters.FirstOrDefault(c => c.Get("Name") == playerName);

        // Character: a profile from the host's library replaces the player's body and movement.
        if (input.CharacterText is { } characterText && player is not null)
        {
            player.Lines = Lines(characterText).Where(l => !l.StartsWith("Name=", StringComparison.Ordinal)).Prepend("Name=" + playerName).ToList();
        }
        // Movement preset: Quake/Source movement fields on the player's character.
        if (MatchPresets.Movement.FirstOrDefault(m => m.Id == s.MovementProfile.Preset) is { } mv && player is not null)
        {
            var k = MatchPresets.Scale;
            player.Set("MaxSpeed", Num(mv.Run * k)); player.Set("MaxCrouchSpeed", Num(mv.Run * mv.CrouchMultiplier * k));
            player.Set("Acceleration", Num(mv.Accelerate * mv.Run * k)); player.Set("CrouchingAcceleration", Num(mv.Accelerate * mv.Run * k));
            player.Set("Friction", Num(mv.Friction)); player.Set("BrakingFrictionFactor", "1.0");
            player.Set("JumpVelocity", Num(mv.JumpSpeed * k)); player.Set("JumpVelocityMin", Num(mv.JumpSpeed * k)); player.Set("JumpVelocityMax", Num(mv.JumpSpeed * k));
            player.Set("Gravity", Num(Math.Round(mv.Gravity * k / MatchPresets.UnrealGravity, 4))); player.Set("AirControl", "1.0");
            player.Set("StepUpHeight", Num(mv.Step * k));
            player.Set("ContinuousGroundFriction", Num(mv.Friction)); player.Set("ContinuousAirFriction", "0.0");
            player.Set("ScaledGroundAcceleration", Num(mv.Accelerate)); player.Set("ScaledAirAcceleration", Num(mv.AirAccelerate));
            player.Set("MaxAirSpeed", Num(mv.AirCap * k)); player.Set("StopSpeed", Num(mv.StopSpeed * k)); player.Set("StopSpeedThreshold", Num(mv.StopSpeed * k));
            player.Set("ClampVelocityToInputSpeed", "false"); player.Set("JumpSkipsFriction", "false");
            player.Set("EnableQuakeMovement", "true"); player.Set("EnableQuakeJump", "false");
        }
        // Weapon: a library profile, or the scenario's own weapon with a preset fire rate.
        var weapons = sections.Where(x => x.Title == "[Weapon Profile]").ToList();
        Section? weapon = null;
        if (input.WeaponText is { } weaponText && s.WeaponProfile.Custom is { } customWeapon)
        {
            weapon = new Section { Title = "[Weapon Profile]", Lines = Lines(weaponText).Where(l => !l.StartsWith("Name=", StringComparison.Ordinal)).Prepend("Name=" + customWeapon).ToList() };
        }
        else if (MatchPresets.Weapons.FirstOrDefault(w => w.Id == s.WeaponProfile.Preset) is { } wp)
        {
            var current = player?.Get("WeaponProfileNames")?.Split(';')[0];
            var source = weapons.FirstOrDefault(w => w.Get("Name") == current) ?? weapons.FirstOrDefault();
            weapon = new Section { Title = "[Weapon Profile]", Lines = source is null ? ["Name=x", "Type=Hitscan", "ShotsPerClick=1", "DamagePerShot=36.0", "MaxHitscanRange=1000000.0", "HeadshotCapable=true", "CooldownType=InfiniteUse", "MagazineMax=0"] : source.Lines.ToList() };
            weapon.Set("Name", "AimMod " + wp.Label + " Weapon");
            weapon.Set("TimeBetweenShots", Num(wp.TimeBetweenShots));
            weapon.Set("Category", "FullyAuto"); weapon.Set("FullyAutomatic", "true");
        }
        if (weapon is not null && player is not null)
        {
            var weaponName = weapon.Get("Name")!;
            sections.RemoveAll(x => x.Title == "[Weapon Profile]" && x.Get("Name") == weaponName);
            var at = sections.FindLastIndex(x => x.Title == "[Weapon Profile]");
            sections.Insert(at < 0 ? sections.Count : at + 1, weapon);
            var slots = (player.Get("WeaponProfileNames") ?? ";;;;;;;").Split(';');
            slots[0] = weaponName;
            player.Set("WeaponProfileNames", string.Join(';', slots));
        }
        // Targets: speed and size multipliers on every bot body (not the player's).
        foreach (var bot in characters.Where(c => c != player))
        {
            foreach (var key in new[] { "MaxSpeed", "MaxCrouchSpeed", "Acceleration", "CrouchingAcceleration" }) bot.Scale(key, s.TargetSpeed);
            foreach (var key in new[] { "MainBBHeight", "MainBBRadius", "MainBBHeadRadius", "ProjBBHeight", "ProjBBRadius", "ProjBBHeadRadius" }) bot.Scale(key, s.TargetSize);
        }
        // Map: a map from maps/ (including map-port output) replaces the embedded copy.
        if (s.MapOverride is not null && input.MapFile is { } mapFile && input.MapText is { } mapText)
        {
            header.Set("MapName", mapFile);
            mapData = mapText.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Replace("\n", nl, StringComparison.Ordinal) + nl;
        }
        // Canonical layout: header, then each section after one blank line.
        var output = new StringBuilder();
        foreach (var line in header.Lines.Where(l => l.Trim().Length > 0)) output.Append(line).Append(nl);
        foreach (var section in sections)
        {
            output.Append(nl).Append(section.Title).Append(nl);
            foreach (var line in section.Lines.Where(l => l.Trim().Length > 0)) output.Append(line).Append(nl);
        }
        if (mapData is not null) output.Append(nl).Append("[Map Data]").Append(nl).Append(mapData);
        return output.ToString();
    }

    public static string Hash(string text) => ContentLibrary.TextHash(text);
}

// Writes match scenarios into the game's Scenarios folder. Only files AimMod
// generated (listed in its own manifest, unchanged since) are ever replaced or
// removed; a user's scenario with the same name is never overwritten.
sealed class MatchScenarioStore(string scenariosFolder, string manifestPath, int keep = 5)
{
    sealed record Entry(string File, string Hash, long Written);
    readonly object gate = new();

    List<Entry> Read()
    {
        try
        {
            if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 65536) return [];
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(manifestPath), Protocol.Json) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }
    void Save(List<Entry> entries) => AtomicFile.WriteText(manifestPath, JsonSerializer.Serialize(entries, Protocol.Json));

    public (bool Ok, string? Error) Write(string name, string text, long now)
    {
        lock (gate)
        {
            if (!name.StartsWith(MatchScenario.Prefix, StringComparison.Ordinal) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return (false, "invalid-name");
            Directory.CreateDirectory(scenariosFolder);
            var file = name + ".sce";
            var path = Path.Combine(scenariosFolder, file);
            var hash = MatchScenario.Hash(text);
            var entries = Read();
            if (File.Exists(path))
            {
                var existing = MatchScenario.Hash(File.ReadAllText(path));
                if (existing == hash) { Touch(entries, file, hash, now); return (true, null); }
                if (!entries.Any(e => e.File == file && e.Hash == existing)) return (false, "name-taken");
            }
            AtomicFile.WriteText(path, text);
            Touch(entries, file, hash, now);
            // Keep the last few; remove older ones only if they are still exactly what we wrote.
            foreach (var old in entries.OrderByDescending(e => e.Written).Skip(keep).ToArray())
            {
                var oldPath = Path.Combine(scenariosFolder, old.File);
                try { if (File.Exists(oldPath) && MatchScenario.Hash(File.ReadAllText(oldPath)) == old.Hash) File.Delete(oldPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                entries.Remove(old);
            }
            Save(entries);
            return (true, null);
        }
    }
    static void Touch(List<Entry> entries, string file, string hash, long now) { entries.RemoveAll(e => e.File == file); entries.Add(new Entry(file, hash, now)); }
    public IReadOnlyList<string> Files() { lock (gate) return Read().OrderByDescending(e => e.Written).Select(e => e.File).ToArray(); }
}

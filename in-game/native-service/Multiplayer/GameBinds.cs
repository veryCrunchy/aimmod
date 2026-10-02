using System.Globalization;
using System.Text.RegularExpressions;

namespace AimMod.InGame.Multiplayer;

// KovaaK's own binds for what a match needs, against KovaaK's 3.9.11 defaults (DefaultInput.ini
// in the game's pak). Read only: AimMod never rebinds anything, and a differing bind never blocks
// ready or start; the lobby, the load screen and one in-game notice per match just say so.
static partial class GameBinds
{
    // Action names as in Input.ini; axes are "MoveForward+" / "MoveForward-" by the sign of Scale.
    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["MoveForward+"] = "W", ["MoveForward-"] = "S", ["MoveRight-"] = "A", ["MoveRight+"] = "D",
        ["Fire"] = "LeftMouseButton", ["Jump"] = "SpaceBar", ["Crouch"] = "LeftControl", ["Reload"] = "R",
        ["Ability1"] = "LeftShift", ["Ability2"] = "E", ["Ability3"] = "Q", ["Ability4"] = "V",
        ["Weapon1"] = "One", ["Weapon2"] = "Two", ["Weapon3"] = "Three", ["Weapon4"] = "Four",
        ["Weapon5"] = "Five", ["Weapon6"] = "Six", ["Weapon7"] = "Seven", ["Weapon8"] = "Eight",
    };

    // The player's binds by action. An action Input.ini doesn't list keeps its default; one
    // listed only with Key=None has no key. Modified binds read "Ctrl+Q".
    public sealed record Bound(IReadOnlyDictionary<string, IReadOnlyList<string>> Keys)
    {
        public static readonly Bound Standard = new(new Dictionary<string, IReadOnlyList<string>>());
        public IReadOnlyList<string> Of(string action) => Keys.TryGetValue(action, out var k) ? k : Defaults.TryGetValue(action, out var d) ? [d] : [];
    }

    [GeneratedRegex(@"^[+.]?ActionMappings=\(ActionName=""([^""]{1,64})""(.*)\)\s*$")] private static partial Regex ActionLine();
    [GeneratedRegex(@"^[+.]?AxisMappings=\(AxisName=""([^""]{1,64})"",Scale=(-?[0-9.]+)(.*)\)\s*$")] private static partial Regex AxisLine();
    [GeneratedRegex(@"(?:^|,)Key=([A-Za-z0-9_]{1,40})")] private static partial Regex KeyField();

    public static Bound Parse(IEnumerable<string> lines)
    {
        var keys = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var inSection = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { inSection = line == "[/Script/Engine.InputSettings]"; continue; }
            if (!inSection) continue;
            string action; string rest;
            if (ActionLine().Match(line) is { Success: true } a) { action = a.Groups[1].Value; rest = a.Groups[2].Value; }
            else if (AxisLine().Match(line) is { Success: true } x && double.TryParse(x.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) && scale != 0)
            { action = x.Groups[1].Value + (scale > 0 ? "+" : "-"); rest = x.Groups[3].Value; }
            else continue;
            if (!keys.TryGetValue(action, out var list)) keys[action] = list = [];
            if (KeyField().Match(rest) is not { Success: true } k || k.Groups[1].Value == "None") continue;
            var mods = new[] { "bCtrl", "bAlt", "bShift", "bCmd" }.Where(m => rest.Contains(m + "=True", StringComparison.Ordinal)).Select(m => m[1..]);
            var key = string.Join('+', mods.Append(k.Groups[1].Value));
            if (!list.Contains(key)) list.Add(key);
        }
        return new(keys.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value, StringComparer.Ordinal));
    }

    // KovaaK's keeps its binds in the UE user config (LocalApplicationData\FPSAimTrainer\Saved\
    // Config\WindowsNoEditor\Input.ini); the game folder's Saved\Config is the fallback.
    public static IEnumerable<string> InputFiles(string? root)
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FPSAimTrainer", "Saved", "Config", "WindowsNoEditor", "Input.ini");
        if (root is not null) yield return Path.Combine(root, "Saved", "Config", "WindowsNoEditor", "Input.ini");
    }

    static (long At, string? Root, Bound Binds) cached = (long.MinValue, null, Bound.Standard);
    public static Bound Current(string? root)
    {
        var now = Environment.TickCount64;
        if (now - cached.At < 5000 && cached.Root == root) return cached.Binds;
        var binds = Bound.Standard;
        foreach (var path in InputFiles(root))
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 1 << 20) continue;
                binds = Parse(File.ReadLines(path)); break; // the first config found is the one the game uses
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        cached = (now, root, binds);
        return binds;
    }

    // What a match asks of the player's binds: the action and how AimMod names it.
    public sealed record Use(string Action, string Label, bool Move = false);

    // From the scenario the match plays: the player's character (crouch, jump, weapon slots,
    // abilities: a sprint profile slower than 1 is a walk) and its weapons (reload).
    public static IReadOnlyList<Use> Uses(string scenarioText, string mode)
    {
        var uses = new List<Use> { new("MoveForward+", "Forward", true), new("MoveRight-", "Left", true), new("MoveForward-", "Back", true), new("MoveRight+", "Right", true), new("Fire", "Fire") };
        var (header, sections) = Sections(scenarioText);
        var character = header.GetValueOrDefault("PlayerProfile") ?? header.GetValueOrDefault("PlayerCharacters")?.Split(';')[0];
        var player = sections.FirstOrDefault(s => s.Title == "[Character Profile]" && s.Fields.GetValueOrDefault("Name") == character)?.Fields;
        var cs = mode == LobbyModes.Cs;
        if (player is null) return uses;
        if (!(Number(player.GetValueOrDefault("JumpVelocityMax")) is { } jump && jump <= 0)) uses.Add(new("Jump", "Jump"));
        if (player.GetValueOrDefault("CanCrouch") == "true") uses.Add(new("Crouch", "Crouch"));
        var abilities = (player.GetValueOrDefault("AbilityProfileNames") ?? "").Split(';');
        for (var i = 0; i < Math.Min(4, abilities.Length); i++)
            if (abilities[i].Trim() is { Length: > 0 } file) uses.Add(new("Ability" + (i + 1), AbilityLabel(file, sections)));
        var weapons = (player.GetValueOrDefault("WeaponProfileNames") ?? "").Split(';');
        var reload = weapons.Any(w => w.Length > 0 && sections.Any(s => s.Title == "[Weapon Profile]" && s.Fields.GetValueOrDefault("Name") == w && Number(s.Fields.GetValueOrDefault("MagazineMax")) > 0));
        if (reload || cs) uses.Add(new("Reload", "Reload"));
        // CS: the primary and pistol bought into slots 1 and 2, the knife on 3, grenades on 4, the bomb on 5; other
        // scenarios switch between the slots they fill.
        if (cs) { uses.Add(new("Weapon1", "Primary")); uses.Add(new("Weapon2", "Pistol")); uses.Add(new("Weapon3", "Knife")); uses.Add(new("Weapon4", "Grenades")); uses.Add(new("Weapon5", "Bomb")); }
        else if (weapons.Count(w => w.Length > 0) >= 2)
            for (var i = 0; i < Math.Min(8, weapons.Length); i++) if (weapons[i].Length > 0) uses.Add(new("Weapon" + (i + 1), "Weapon " + (i + 1)));
        return uses;
    }

    static string AbilityLabel(string file, List<Section> sections)
    {
        var stem = Path.GetFileNameWithoutExtension(file).Trim();
        if (file.EndsWith(".abilsprint", StringComparison.OrdinalIgnoreCase))
        {
            var sprint = sections.FirstOrDefault(s => s.Title == "[Sprint Ability Profile]" && s.Fields.GetValueOrDefault("Name") == stem)?.Fields;
            if (Number(sprint?.GetValueOrDefault("SpeedModifier")) is { } speed) return speed < 1 ? "Walk" : "Sprint";
            return stem.Contains("walk", StringComparison.OrdinalIgnoreCase) ? "Walk" : "Sprint";
        }
        var clean = new string(stem.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length is > 0 and <= 24 ? clean : "Ability";
    }

    sealed record Section(string Title, Dictionary<string, string> Fields);
    static double? Number(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;

    static (Dictionary<string, string> Header, List<Section> Sections) Sections(string text)
    {
        var header = new Dictionary<string, string>(StringComparer.Ordinal);
        var sections = new List<Section>();
        var current = header;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line == "[Map Data]") break;
            if (line.StartsWith('[') && line.EndsWith(']')) { current = new(StringComparer.Ordinal); sections.Add(new(line, current)); continue; }
            var eq = line.IndexOf('=');
            if (eq > 0) current.TryAdd(line[..eq], line[(eq + 1)..].Trim());
        }
        return (header, sections);
    }

    // One row per used action: the keys it has, the usual key, and whether that's among them.
    // AimMod's own keys (CS use and buy, the scoreboard) are rows too: they never change, but
    // KovaaK's binding a used action to the same key is worth a line.
    public sealed record Row(string Label, string Keys, string Usual, bool Standard, bool Move = false, bool AimMod = false);
    public static IReadOnlyList<Row> Rows(IReadOnlyList<Use> uses, Bound binds, IReadOnlyList<(string Label, string Key)>? aimmodKeys = null)
    {
        var rows = new List<Row>();
        foreach (var u in uses)
        {
            var keys = binds.Of(u.Action);
            var usual = Defaults.GetValueOrDefault(u.Action, "");
            rows.Add(new(u.Label, string.Join(" / ", keys.Select(KeyName)), KeyName(usual), keys.Contains(usual), u.Move));
        }
        foreach (var (label, key) in aimmodKeys ?? [])
            rows.Add(new(label, KeyName(key), KeyName(key), !uses.Any(u => binds.Of(u.Action).Contains(key, StringComparer.OrdinalIgnoreCase)), AimMod: true));
        return rows;
    }

    // "Walk is on Q here (usually Shift)", "Walk has no key", "Buy and Reload are both on B".
    public static IReadOnlyList<string> Issues(IReadOnlyList<Row> rows)
    {
        var list = new List<string>();
        foreach (var r in rows.Where(r => !r.Standard))
        {
            if (!r.AimMod) { list.Add(r.Keys.Length == 0 ? r.Label + " has no key" : r.Label + " is on " + r.Keys + " here (usually " + r.Usual + ")"); continue; }
            var other = rows.FirstOrDefault(o => !o.AimMod && o.Keys.Split(" / ").Contains(r.Keys));
            if (other is not null) list.Add(r.Label + " and " + other.Label + " are both on " + r.Keys);
        }
        return list;
    }

    // UE key names as players know them.
    public static string KeyName(string key)
    {
        var plus = key.LastIndexOf('+');
        if (plus > 0) return key[..plus] + "+" + KeyName(key[(plus + 1)..]);
        return key switch
        {
            "" => "",
            "LeftMouseButton" => "Mouse 1", "RightMouseButton" => "Mouse 2", "MiddleMouseButton" => "Mouse 3",
            "ThumbMouseButton" => "Mouse 4", "ThumbMouseButton2" => "Mouse 5",
            "MouseScrollUp" => "Wheel up", "MouseScrollDown" => "Wheel down",
            "LeftShift" => "Shift", "RightShift" => "Right Shift", "LeftControl" => "Ctrl", "RightControl" => "Right Ctrl",
            "LeftAlt" => "Alt", "RightAlt" => "Right Alt", "SpaceBar" => "Space", "CapsLock" => "Caps Lock",
            "Zero" => "0", "One" => "1", "Two" => "2", "Three" => "3", "Four" => "4", "Five" => "5", "Six" => "6", "Seven" => "7", "Eight" => "8", "Nine" => "9",
            "Tilde" => "~",
            _ => key,
        };
    }
}

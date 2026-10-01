using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
static partial class MatchScenario
{
    public const int GeneratorVersion = 3;
    public const string Prefix = "AimMod Match - ";
    // Written into every generated scenario; cleanup removes only files that carry it.
    public const string Marker = "AimMod multiplayer match generated from ";
    public const string Tag = "AimMod Match";
    [GeneratedRegex(@"^AimMod Match - .{1,80} - [0-9a-f]{8}\.sce$")] private static partial Regex FilePattern();
    public static bool IsGeneratedName(string fileName) => FilePattern().IsMatch(fileName);

    // Match scenarios never run as challenges, so nothing can reach KovaaK's leaderboards.
    public static string SafeMode(string scenario, string mode) => scenario.StartsWith(Prefix, StringComparison.Ordinal) ? "freeplay" : mode;
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    // Anything that changes what is played means a generated scenario.
    public static bool Needed(LobbySettings s) =>
        s.Mode == LobbyModes.Tracking || LobbyModes.Combat(s.Mode) || s.MapOverride is not null || s.TimeLimit is not null || s.TargetSpeed != 1 || s.TargetSize != 1
        || s.WeaponProfile.Preset != ProfilePresets.Default || s.MovementProfile.Preset != ProfilePresets.Default || s.CharacterProfile.Preset != ProfilePresets.Default;

    // A pure function of the lobby settings, which carry every input's content hash.
    public static string Key(LobbySettings s)
    {
        var parts = new object?[] { GeneratorVersion, s.Mode == LobbyModes.Tracking || LobbyModes.Combat(s.Mode) ? s.Mode : null, s.Scenario?.Hash, s.Scenario?.MapHash, s.MapOverride?.Hash, s.TimeLimit,
            Num(s.TargetSpeed), Num(s.TargetSize), s.WeaponProfile.Preset, s.WeaponProfile.Hash, s.MovementProfile.Preset, s.CharacterProfile.Preset, s.CharacterProfile.Hash };
        return ContentLibrary.TextHash(JsonSerializer.Serialize(parts));
    }

    public static string Name(LobbySettings s)
    {
        var label = s.Mode switch { LobbyModes.Tracking => "Tracking duel", LobbyModes.Deathmatch => "Deathmatch", LobbyModes.Vampiric => "Vampiric 1v1", LobbyModes.Instagib => "Instagib", _ => null }
            ?? MatchPresets.Movement.FirstOrDefault(m => m.Id == s.MovementProfile.Preset)?.Label
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
        header.Set("Description", Marker + (s.Scenario?.Name ?? "a scenario") + ". Temporary: AimMod removes it after the match. Played in freeplay; not a published scenario.");
        // Only our own tag, so match scenarios stay out of the player's usual tag filters.
        header.Set("SearchTags", Tag);
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
        // Bodies for the other players (AimModSteam spawns them as inert bots): one
        // character profile per offered look, never scoring and never replacing the scenario's own.
        foreach (var look in AvatarProfiles.All)
            if (!sections.Any(x => x.Title == "[Character Profile]" && x.Get("Name") == look.ProfileName))
                sections.Add(new Section { Title = "[Character Profile]", Lines = AvatarProfiles.Lines(look).ToList() });
        if (s.Mode == LobbyModes.Tracking) TrackingDuelScenario(header, sections, s);
        else if (LobbyModes.Combat(s.Mode)) CombatArena(header, sections, s, player);
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

    // Tracking duel arena: no targets of its own, nobody can be hurt, nothing scores
    // natively (AimMod scores time on target). One invisible, inert helper bot stays,
    // because AimModSteam spawns each opponent's avatar from a bot the scenario has
    // (the avatar then loads the player's look, aimmod.char.<id>). The KovaaK's run
    // outlasts the round; the host ends the round.
    public const string HiddenBot = "AimMod Hidden Bot", HiddenBody = "AimMod Hidden";
    static void TrackingDuelScenario(Section header, List<Section> sections, LobbySettings s)
    {
        header.Set("Timelimit", Num(s.EffectiveTimeLimit + 10));
        header.Set("InvinciblePlayer", "true");
        HelperBot(header, sections);
    }

    // Combat arena (deathmatch, vampiric 1v1, instagib): the host owns health and
    // deaths, AimModCore applies them (play-state.tsv), so the player can be hurt;
    // avatars stay invulnerable; nothing heals, regenerates or scores natively. The
    // player carries exactly the mode's weapon, which the host validates claims against.
    static void CombatArena(Section header, List<Section> sections, LobbySettings s, Section? player)
    {
        header.Set("Timelimit", Num(s.EffectiveTimeLimit + 30));
        header.Set("InvinciblePlayer", "false");
        header.Set("PlayerMaxLives", "0");
        HelperBot(header, sections);
        var weapon = CombatRules.Weapon(s.Mode);
        sections.RemoveAll(x => x.Title == "[Weapon Profile]" && x.Get("Name") == weapon.Name);
        sections.Add(new Section { Title = "[Weapon Profile]", Lines = WeaponLines(weapon) });
        if (player is null) return;
        var slots = (player.Get("WeaponProfileNames") ?? ";;;;;;;").Split(';');
        if (slots.Length < 8) slots = slots.Concat(Enumerable.Repeat("", 8 - slots.Length)).ToArray();
        for (var i = 0; i < slots.Length; i++) slots[i] = i == 0 ? weapon.Name : "";
        player.Set("WeaponProfileNames", string.Join(';', slots));
        player.Set("MaxHealth", Num(CombatRules.MaxHealth));
        player.Set("LifeStealPercent", "0.0"); player.Set("HealthRegainedonkill", "0.0"); player.Set("HealthRegenPerSec", "0.0");
        player.Set("RespawnInvulnTime", "0.0");
        var respawn = Num(CombatRules.RespawnMs(s.Mode) / 1000.0);
        player.Set("MinRespawnDelay", respawn); player.Set("MaxRespawnDelay", respawn);
    }

    static List<string> WeaponLines(CombatWeapon w) =>
    [
        "Name=" + w.Name, "Type=Hitscan", "ShotsPerClick=1", "DamagePerShot=" + Num(w.Damage), "KnockbackFactor=0.0", "TimeBetweenShots=" + Num(w.TimeBetweenShots),
        "Pierces=false", "Category=" + (w.FullyAuto ? "FullyAuto" : "SemiAuto"), "BurstShotCount=1", "MaxHitscanRange=1000000.0",
        "HeadshotCapable=" + (w.HeadMultiplier > 1 ? "true" : "false"), "HeadshotMultiplier=" + Num(w.HeadMultiplier), "CooldownType=InfiniteUse", "MagazineMax=0", "AmmoPerShot=0",
        "DamageFalloffStartDistance=100000.0", "DamageFalloffStopDistance=100000.0", "DamageAtMaxRange=" + Num(w.Damage), "DelayBeforeShot=0.0",
        "VisualLifetime=" + (w.FullyAuto ? "0.05" : "0.4"), "BlockedByWorld=true", "CanAimDownSight=false",
        "SpreadSSA=0.0,0.0,0.0,0.0", "SpreadSCA=0.0,0.0,0.0,0.0", "SpreadMSA=0.0,0.0,0.0,0.0", "SpreadMCA=0.0,0.0,0.0,0.0",
        "MaxRecoilUp=0.0", "MinRecoilUp=0.0", "MinRecoilHoriz=0.0", "MaxRecoilHoriz=0.0", "FlatKnockbackVertical=" + Num(w.KnockbackVertical),
        "WeaponModel=Rifle", "WeaponSkin=Default", "FullyAutomatic=" + (w.FullyAuto ? "true" : "false"),
    ];

    // Shared by the AimMod arenas: no targets, nothing scored natively, and one invisible,
    // passable, inert helper bot that AimModSteam spawns avatars from.
    static void HelperBot(Section header, List<Section> sections)
    {
        header.Set("IsChallenge", "false");
        header.Set("BotCharacters", HiddenBot + ".bot"); header.Set("AddedBots", HiddenBot + ".bot");
        header.Set("BotMaxLives", "0"); header.Set("BotTeams", "2");
        if (header.Get("PlayerTeam") is null) header.Set("PlayerTeam", "1");
        header.Set("InvincibleBots", "true");
        header.Set("ScorePerHit", "0.0"); header.Set("ScorePerDamage", "0.0"); header.Set("ScorePerKill", "0.0");
        header.Set("TimeRefilledByKill", "0.0");
        sections.RemoveAll(x => (x.Title == "[Bot Profile]" && x.Get("Name") == HiddenBot) || (x.Title == "[Character Profile]" && x.Get("Name") == HiddenBody) || (x.Title is "[Dodge Profile]" or "[Aim Profile]" && x.Get("Name") == HiddenBot));
        sections.Add(new Section { Title = "[Bot Profile]", Lines =
        [
            "Name=" + HiddenBot, "DodgeProfileNames=" + HiddenBot, "DodgeProfileWeights=1.0", "DodgeProfileMaxChangeTime=5.0", "DodgeProfileMinChangeTime=1.0",
            "WeaponsProfileNames=;;;;;;;", "WeaponProfileWeights=1.0;1.0;1.0;1.0;1.0;1.0;1.0;1.0", "AimingProfileNames=" + string.Join(';', Enumerable.Repeat(HiddenBot, 8)),
            "WeaponSwitchTime=3.0", "UseWeapons=false", "CharacterProfile=" + HiddenBody, "SeeThroughWalls=false", "NoDodging=true", "StandStillUntilHurt=true",
            "NoAiming=true", "SpawnGroup=0", "UseMinimumRespawnTime=true", "DisableScoring=true",
        ] });
        sections.Add(new Section { Title = "[Aim Profile]", Lines = ["Name=" + HiddenBot, "MinReactionTime=0.3", "MaxReactionTime=0.4", "AimingStyle=Simple"] });
        sections.Add(new Section { Title = "[Dodge Profile]", Lines =
        [
            "Name=" + HiddenBot, "MaxTargetDistance=0.0", "MinTargetDistance=0.0", "ToggleLeftRight=false", "ToggleForwardBack=false", "JumpFrequency=0.0",
            "CrouchInAirFrequency=0.0", "CrouchOnGroundFrequency=0.0",
        ] });
        var body = AvatarProfiles.Lines(AvatarProfiles.All[0]).Where(l => !l.StartsWith("Name=", StringComparison.Ordinal) && !l.StartsWith("CharacterModel=", StringComparison.Ordinal) && !l.StartsWith("CharacterSkin=", StringComparison.Ordinal) && !l.StartsWith("MainBBHide=", StringComparison.Ordinal) && !l.StartsWith("ProjBBHide=", StringComparison.Ordinal)).ToList();
        body.InsertRange(0, ["Name=" + HiddenBody, "CharacterModel=None", "CharacterSkin=Default", "MainBBHide=true", "ProjBBHide=true"]);
        sections.Add(new Section { Title = "[Character Profile]", Lines = body });
    }
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

    // Remove every match scenario except the one the current lobby needs. A file goes only
    // if its name has the generated pattern and its header carries the generated marker,
    // so a user's own scenario is never touched. Returns how many were removed.
    public int Clean(string? keepName)
    {
        lock (gate)
        {
            if (!Directory.Exists(scenariosFolder)) return 0;
            var keep = keepName is null ? null : keepName + ".sce";
            var removed = 0;
            var written = Read();
            foreach (var path in Directory.EnumerateFiles(scenariosFolder, MatchScenario.Prefix + "*.sce").Take(500).ToArray())
            {
                var file = Path.GetFileName(path);
                if (file == keep || !MatchScenario.IsGeneratedName(file)) continue;
                // Ours: it carries the marker, or it is still exactly what this store wrote.
                if (!Marked(path) && !written.Any(e => e.File == file && Unchanged(path, e.Hash))) continue;
                try { File.Delete(path); removed++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            var entries = Read();
            if (entries.RemoveAll(e => e.File != keep && !File.Exists(Path.Combine(scenariosFolder, e.File))) > 0 || removed > 0) Save(entries);
            return removed;
        }
    }
    static bool Unchanged(string path, string hash)
    {
        try { return new FileInfo(path).Length <= 32L << 20 && MatchScenario.Hash(File.ReadAllText(path)) == hash; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    static bool Marked(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 32L << 20) return false;
            using var reader = new StreamReader(path);
            for (var i = 0; i < 400 && reader.ReadLine() is { } line; i++)
            {
                if (line.StartsWith('[')) return false;
                if (line.StartsWith("Description=" + MatchScenario.Marker, StringComparison.Ordinal)) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return false;
    }
    public IReadOnlyList<string> Files() { lock (gate) return Read().OrderByDescending(e => e.Written).Select(e => e.File).ToArray(); }
}

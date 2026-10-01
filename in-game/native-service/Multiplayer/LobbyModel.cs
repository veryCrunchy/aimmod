using System.Text.Json;
using System.Text.Json.Serialization;

namespace AimMod.InGame.Multiplayer;

// Lobby state shared by the host (authority), every client mirror and the UI.
// Records are immutable snapshots; LobbyCore owns the mutable state.
static class LobbyModes
{
    public const string Race = "score-race", Duel = "duel", Rounds = "ffa-rounds", Practice = "practice";
    public static readonly string[] All = [Race, Duel, Rounds, Practice];
    // Score race plays the scenario exactly as published so scores compare with each player's history.
    public static bool AllowsOverrides(string mode) => mode != Race;
    public static bool AllowsLateJoin(string mode) => mode is Rounds or Practice;
    public static bool Scored(string mode) => mode != Practice;
}

static class LobbyPrivacy
{
    public const string Friends = "friends", Invite = "invite", Public = "public";
    public static readonly string[] All = [Friends, Invite, Public];
}

static class ProfilePresets
{
    public const string Default = "default", Custom = "custom";
    // Reference targets for the scenario builder. The C++ bridge turns a preset into
    // [Weapon Profile] / [Character Profile] blocks; the lobby only carries the id.
    public static readonly (string Id, string Label, string Weapon, string Movement)[] All =
    [
        (Default, "Scenario default", "The scenario’s own weapon", "The scenario’s own movement"),
        ("cs", "Counter-Strike-like", "Rifle, 10 shots/s, accurate first shot", "Run 250 u/s, counter-strafe stops, low air control"),
        ("valorant", "Valorant-like", "Rifle, 9.75 shots/s, tap-accurate", "Run 6.75 m/s, fast stops, short jumps"),
        ("apex", "Apex-like", "Auto rifle, 13.5 shots/s, low recoil", "Sprint 7.4 m/s, strong air strafe"),
        ("quake", "Quake-like", "Hitscan beam, 20 ticks/s", "Run 320 u/s, strafe jumping, high air acceleration"),
        (Custom, "Custom", "A weapon profile from your library", "A character profile from your library"),
    ];
    public static bool Known(string id) => All.Any(p => p.Id == id);
}

// WorkshopId is set when the host's copy is a Steam Workshop item, so members can download it there.
sealed record ScenarioChoice(string Name, string Hash, string Map, string MapHash, double TimeLimit, string? WorkshopId = null);
sealed record MapChoice(string Name, string Hash, string Source);
sealed record ProfileChoice(string Preset, string? Custom = null, string? Hash = null)
{
    public static readonly ProfileChoice Default = new(ProfilePresets.Default);
}

sealed record LobbySettings(
    string Mode = LobbyModes.Race,
    ScenarioChoice? Scenario = null,
    MapChoice? MapOverride = null,
    int MaxPlayers = 4,
    bool Spectators = false,
    int Rounds = 1,
    int FirstTo = 3,
    int? TimeLimit = null,
    ProfileChoice? Weapon = null,
    ProfileChoice? Movement = null,
    ProfileChoice? Character = null,
    double TargetSpeed = 1,
    double TargetSize = 1,
    string Privacy = LobbyPrivacy.Friends,
    int Countdown = 5,
    bool LateJoin = false,
    bool AutoStart = false)
{
    public const int MinPlayers = 2, MaxPlayerLimit = 8, MaxSpectators = 4;
    [JsonIgnore] public ProfileChoice WeaponProfile => Weapon ?? ProfileChoice.Default;
    [JsonIgnore] public ProfileChoice MovementProfile => Movement ?? ProfileChoice.Default;
    [JsonIgnore] public ProfileChoice CharacterProfile => Character ?? ProfileChoice.Default;
    [JsonIgnore] public double EffectiveTimeLimit => TimeLimit ?? Scenario?.TimeLimit ?? 60;
    [JsonIgnore] public int? TotalRounds => Mode switch { LobbyModes.Race or LobbyModes.Rounds => Rounds, _ => null };
    // Values that change what people play. Changing any of them clears ready states.
    [JsonIgnore] public string PlayKey => string.Join('|', Mode, Scenario?.Hash, MapOverride?.Hash, Rounds, FirstTo, TimeLimit,
        WeaponProfile, MovementProfile, CharacterProfile, TargetSpeed, TargetSize);
}

static class MemberRoles { public const string Player = "player", Spectator = "spectator"; }
static class ContentStates { public const string Ok = "ok", Missing = "missing", Mismatch = "mismatch", Unknown = "unknown", None = "none"; }
static class Connections { public const string Connected = "connected", Reconnecting = "reconnecting"; }
static class MatchPhases { public const string Countdown = "countdown", Live = "live", Round = "round", Final = "final"; }
static class LineStates { public const string Waiting = "waiting", Playing = "playing", Finished = "finished", Dnf = "dnf", Left = "left"; }

// Connection: connected or reconnecting. Link: local (this machine), relay,
// direct or simulated. Profiles: whether custom weapon/character profiles are present.
sealed record LobbyMember(string Id, string Name, string Role, bool Ready, int? Ping, string Scenario, string Map, string Profiles,
    string Connection, string Link, long JoinedAt, bool Simulated, string Avatar = AvatarProfiles.Default, string? Version = null);

sealed record ScoreLine(string MemberId, double? Score, double? Seconds, double? Remaining, int Shots, int Hits, int Kills,
    string Status, bool Disputed);

sealed record Placement(string MemberId, string Name, int Place, double? Score, double? Accuracy, int Points, string Status, bool Disputed);
sealed record RoundResult(int Round, IReadOnlyList<Placement> Results, string? WinnerId);
sealed record Standing(string MemberId, string Name, int Place, int Wins, int Points, double? Best, double Total, int Played);

sealed record MatchSnapshot(string Id, string Phase, string Mode, string Scenario, double TimeLimit, int Round, int? TotalRounds,
    int? FirstTo, long? StartsAt, long? EndsAt, long? NextAt, IReadOnlyList<string> Players, IReadOnlyList<ScoreLine> Live,
    IReadOnlyList<RoundResult> Rounds, IReadOnlyList<Standing> Standings, string? WinnerId, IReadOnlyList<string> Rematch, long? RematchDeadline = null);

// Clip: a shared clip replay id (everyone in the lobby received the file).
sealed record ChatLine(long Id, string? From, string Name, string Text, long At, bool System, string? Clip = null);

// ReadyCheck: when the host last asked everyone to ready up (host clock), while it is open.
sealed record LobbySnapshot(int V, string Id, string Code, long Revision, string HostId, LobbySettings Settings,
    IReadOnlyList<LobbyMember> Members, MatchSnapshot? Match, IReadOnlyList<ChatLine> Chat, long Now, long? ReadyCheck = null, long? AutoStartAt = null);

sealed record StartBlocker(string Code, string Text);

sealed record LobbyResult(bool Ok, string? Code = null, string? Message = null)
{
    public static readonly LobbyResult Success = new(true);
    public static LobbyResult Fail(string code, string message) => new(false, code, message);
}

// Frames and results a player's own client reports during a round.
sealed record ScoreFrame(string MatchId, int Round, double Seconds, double Score, int Shots, int Hits, int Kills, double? Remaining);
sealed record RunFinish(string MatchId, int Round, double Score, double Seconds, int Shots, int Hits, int Kills, string? ReplayHash);

// Host-side validation and clamping. Every settings change, local or remote,
// goes through Apply, so a client can never push an out-of-range lobby.
static class LobbyRules
{
    public const int MaxName = 32, MaxChat = 200, MaxContentName = 128;
    static readonly HashSet<string> Keys = ["mode", "scenario", "mapOverride", "maxPlayers", "spectators", "rounds", "firstTo",
        "timeLimit", "weapon", "movement", "character", "targetSpeed", "targetSize", "privacy", "countdown", "lateJoin", "autoStart"];

    public static string CleanName(string? name, string fallback)
    {
        var text = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (text.Length > MaxName) text = text[..MaxName].TrimEnd();
        return text.Length == 0 ? fallback : text;
    }
    public static string? CleanChat(string? text)
    {
        var value = new string((text ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (value.Length == 0) return null;
        return value.Length > MaxChat ? value[..MaxChat] : value;
    }
    static double Clamp(double value, double min, double max, double step) =>
        Math.Round(Math.Clamp(Math.Round(Math.Clamp(value, min, max) / step) * step, min, max), 4);
    static bool ValidHash(string? hash) => hash is { Length: >= 8 and <= 64 } && hash.All(c => char.IsAsciiHexDigitLower(c) || char.IsAsciiDigit(c));
    static bool ValidContentName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= MaxContentName && !name.Any(char.IsControl);

    // resolve: turns a scenario / map / profile name from the patch into content the host actually has.
    public static (LobbySettings? Settings, LobbyResult Result) Apply(LobbySettings current, JsonElement patch, int players, IContentResolver resolve)
    {
        if (patch.ValueKind != JsonValueKind.Object) return (null, LobbyResult.Fail("invalid", "Settings must be an object."));
        var next = current;
        var seen = new HashSet<string>();
        foreach (var property in patch.EnumerateObject())
        {
            if (!Keys.Contains(property.Name) || !seen.Add(property.Name)) return (null, LobbyResult.Fail("invalid", "Unknown or repeated setting: " + property.Name));
            var value = property.Value;
            string? Text() => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            double? Number() => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
            bool? Flag() => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
            LobbyResult Bad(string message) => LobbyResult.Fail("invalid", message);
            switch (property.Name)
            {
                case "mode":
                    if (Text() is not { } mode || !LobbyModes.All.Contains(mode)) return (null, Bad("Unknown mode."));
                    if (mode == LobbyModes.Duel && players > 2) return (null, LobbyResult.Fail("duel-players", "A duel is one against one. Move extra players to spectators first."));
                    next = next with { Mode = mode }; break;
                case "scenario":
                    if (Text() is not { } scenarioName || !ValidContentName(scenarioName)) return (null, Bad("Choose a scenario from your library."));
                    if (resolve.Scenario(scenarioName) is not { } scenario) return (null, LobbyResult.Fail("scenario-missing", "That scenario isn’t in your library."));
                    next = next with { Scenario = scenario, MapOverride = null }; break;
                case "mapOverride":
                    if (value.ValueKind == JsonValueKind.Null) { next = next with { MapOverride = null }; break; }
                    if (Text() is not { } mapName || !ValidContentName(mapName)) return (null, Bad("Choose a map from your library."));
                    if (resolve.Map(mapName) is not { } map) return (null, LobbyResult.Fail("map-missing", "That map isn’t in your maps folder."));
                    next = next with { MapOverride = map }; break;
                case "maxPlayers":
                    if (Number() is not { } max) return (null, Bad("Players must be a number."));
                    next = next with { MaxPlayers = (int)Clamp(max, Math.Max(LobbySettings.MinPlayers, players), LobbySettings.MaxPlayerLimit, 1) }; break;
                case "spectators": if (Flag() is not { } spectators) return (null, Bad("Spectators must be on or off.")); next = next with { Spectators = spectators }; break;
                case "rounds": if (Number() is not { } rounds) return (null, Bad("Rounds must be a number.")); next = next with { Rounds = (int)Clamp(rounds, 1, 10, 1) }; break;
                case "firstTo": if (Number() is not { } firstTo) return (null, Bad("First to must be a number.")); next = next with { FirstTo = (int)Clamp(firstTo, 1, 7, 1) }; break;
                case "timeLimit":
                    if (value.ValueKind == JsonValueKind.Null) { next = next with { TimeLimit = null }; break; }
                    if (Number() is not { } limit) return (null, Bad("Time limit must be a number of seconds."));
                    next = next with { TimeLimit = (int)Clamp(limit, 10, 600, 5) }; break;
                case "weapon": case "movement": case "character":
                    var (profile, error) = Profile(property.Name, value, resolve);
                    if (profile is null) return (null, Bad(error));
                    next = property.Name switch { "weapon" => next with { Weapon = profile }, "movement" => next with { Movement = profile }, _ => next with { Character = profile } };
                    break;
                case "targetSpeed": if (Number() is not { } speed) return (null, Bad("Target speed must be a number.")); next = next with { TargetSpeed = Clamp(speed, 0.25, 3, 0.05) }; break;
                case "targetSize": if (Number() is not { } size) return (null, Bad("Target size must be a number.")); next = next with { TargetSize = Clamp(size, 0.25, 2, 0.05) }; break;
                case "privacy": if (Text() is not { } privacy || !LobbyPrivacy.All.Contains(privacy)) return (null, Bad("Unknown privacy option.")); next = next with { Privacy = privacy }; break;
                case "countdown": if (Number() is not { } countdown) return (null, Bad("Countdown must be a number.")); next = next with { Countdown = (int)Clamp(countdown, 3, 10, 1) }; break;
                case "lateJoin": if (Flag() is not { } late) return (null, Bad("Late join must be on or off.")); next = next with { LateJoin = late }; break;
                case "autoStart": if (Flag() is not { } auto) return (null, Bad("Auto start must be on or off.")); next = next with { AutoStart = auto }; break;
            }
        }
        if (seen.Count == 0) return (null, LobbyResult.Fail("invalid", "No settings supplied."));
        return (Normalize(next, players), LobbyResult.Success);
    }

    static (ProfileChoice? Profile, string Error) Profile(string kind, JsonElement value, IContentResolver resolve)
    {
        string? preset = null, custom = null;
        if (value.ValueKind == JsonValueKind.String) preset = value.GetString();
        else if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in value.EnumerateObject())
            {
                if (p.Name == "preset" && p.Value.ValueKind == JsonValueKind.String) preset = p.Value.GetString();
                else if (p.Name == "custom" && p.Value.ValueKind == JsonValueKind.String) custom = p.Value.GetString();
                else return (null, "Unknown profile field.");
            }
        }
        if (preset is null || !ProfilePresets.Known(preset)) return (null, "Unknown profile preset.");
        if (kind == "character" && preset is not (ProfilePresets.Default or ProfilePresets.Custom)) return (null, "Character profiles are the scenario default or one from your library.");
        if (preset != ProfilePresets.Custom) return (new ProfileChoice(preset), "");
        if (!ValidContentName(custom)) return (null, "Choose a profile from your library.");
        var found = kind == "weapon" ? resolve.Weapon(custom!) : resolve.Character(custom!);
        return found is null ? (null, "That profile isn’t in your library.") : (new ProfileChoice(ProfilePresets.Custom, found.Name, found.Hash), "");
    }

    // Mode rules applied after every change, so combinations stay coherent.
    public static LobbySettings Normalize(LobbySettings s, int players)
    {
        if (s.Mode == LobbyModes.Duel) s = s with { MaxPlayers = 2 };
        if (!LobbyModes.AllowsOverrides(s.Mode))
            s = s with { MapOverride = null, TimeLimit = null, Weapon = ProfileChoice.Default, Movement = ProfileChoice.Default, Character = ProfileChoice.Default, TargetSpeed = 1, TargetSize = 1 };
        if (!LobbyModes.AllowsLateJoin(s.Mode)) s = s with { LateJoin = false };
        // Picking the scenario's own length is no override, so no match scenario is generated for it.
        if (s.TimeLimit is { } limit && s.Scenario is { } scenario && Math.Abs(limit - scenario.TimeLimit) < 0.5) s = s with { TimeLimit = null };
        if (s.Mode == LobbyModes.Race) s = s with { Rounds = Math.Clamp(s.Rounds, 1, 5) };
        if (s.MaxPlayers < Math.Max(LobbySettings.MinPlayers, players)) s = s with { MaxPlayers = Math.Min(LobbySettings.MaxPlayerLimit, Math.Max(LobbySettings.MinPlayers, players)) };
        return s with { Weapon = s.WeaponProfile, Movement = s.MovementProfile, Character = s.CharacterProfile };
    }

    // Snapshots from a remote host are trusted for display only after the same bounds hold.
    public static bool Plausible(LobbySettings s) =>
        LobbyModes.All.Contains(s.Mode) && LobbyPrivacy.All.Contains(s.Privacy)
        && s.MaxPlayers is >= LobbySettings.MinPlayers and <= LobbySettings.MaxPlayerLimit && s.Rounds is >= 1 and <= 10 && s.FirstTo is >= 1 and <= 7
        && s.TimeLimit is null or (>= 10 and <= 600) && s.TargetSpeed is >= 0.25 and <= 3 && s.TargetSize is >= 0.25 and <= 2 && s.Countdown is >= 3 and <= 10
        && (s.Scenario is null || (ValidContentName(s.Scenario.Name) && ValidHash(s.Scenario.Hash) && s.Scenario.TimeLimit is > 0 and <= 3600))
        && (s.MapOverride is null || (ValidContentName(s.MapOverride.Name) && ValidHash(s.MapOverride.Hash)))
        && new[] { s.WeaponProfile, s.MovementProfile, s.CharacterProfile }.All(p => ProfilePresets.Known(p.Preset));

    public static IReadOnlyList<StartBlocker> StartBlockers(LobbySnapshot lobby)
    {
        var list = new List<StartBlocker>();
        var s = lobby.Settings;
        var players = lobby.Members.Where(m => m.Role == MemberRoles.Player).ToArray();
        if (lobby.Match is { Phase: not MatchPhases.Final }) { list.Add(new("in-match", "A match is already running.")); return list; }
        if (s.Scenario is null) list.Add(new("scenario", "Choose a scenario."));
        if (s.Mode == LobbyModes.Duel && players.Length != 2) list.Add(new("duel-players", "A duel needs exactly two players."));
        else if (players.Length < LobbySettings.MinPlayers) list.Add(new("players", "Waiting for at least one more player."));
        foreach (var m in players.Where(m => m.Connection != Connections.Connected)) list.Add(new("reconnecting", m.Name + " is reconnecting."));
        // Different AimMod builds can't see each other in the world (the pose format changed).
        var hostVersion = lobby.Members.FirstOrDefault(m => m.Id == lobby.HostId)?.Version;
        foreach (var m in lobby.Members.Where(m => m.Version is not null && hostVersion is not null && m.Version != hostVersion))
            list.Add(new("version", m.Name + " is on a different AimMod version. Everyone needs the latest AimMod."));
        var notReady = players.Where(m => m.Id != lobby.HostId && !m.Ready && m.Connection == Connections.Connected).ToArray();
        if (notReady.Length == 1) list.Add(new("ready", notReady[0].Name + " isn’t ready."));
        else if (notReady.Length > 1) list.Add(new("ready", notReady.Length + " players aren’t ready."));
        if (s.Scenario is not null)
            foreach (var m in players)
            {
                if (m.Scenario == ContentStates.Missing) list.Add(new("content", m.Name + " doesn’t have the scenario."));
                else if (m.Scenario == ContentStates.Mismatch) list.Add(new("content", m.Name + " has a different version of the scenario."));
                else if (m.Map == ContentStates.Missing) list.Add(new("content", m.Name + " doesn’t have the map."));
                else if (m.Map == ContentStates.Mismatch) list.Add(new("content", m.Name + " has a different version of the map."));
                else if (m.Profiles is ContentStates.Missing or ContentStates.Mismatch) list.Add(new("content", m.Name + " doesn’t have the custom profile."));
                else if (m.Scenario == ContentStates.Unknown) list.Add(new("content", "Checking " + m.Name + "’s content."));
            }
        return list;
    }
}

sealed record LibraryItem(string Name, string Hash);
interface IContentResolver
{
    ScenarioChoice? Scenario(string name);
    MapChoice? Map(string name);
    LibraryItem? Weapon(string name);
    LibraryItem? Character(string name);
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AimMod.InGame.Multiplayer;

// Lobby state shared by the host (authority), every client mirror and the UI.
// Records are immutable snapshots; LobbyCore owns the mutable state.
static class LobbyModes
{
    public const string Race = "score-race", Duel = "duel", Rounds = "ffa-rounds", Practice = "practice", Tracking = "tracking-duel",
        Deathmatch = "deathmatch", Vampiric = "vampiric", Instagib = "instagib", TeamDeathmatch = "team-deathmatch", Cs = "cs";
    public static readonly string[] All = [Race, Duel, Rounds, Practice, Tracking, Deathmatch, Vampiric, Instagib, TeamDeathmatch, Cs];
    // Players shoot each other with host-validated hits: the combat modes and CS.
    public static bool Shooting(string mode) => Combat(mode) || mode == Cs;
    // CS is 3v3, 4v4 or 5v5 only; everything else is up to 8 players.
    public static int MaxPlayers(string mode) => mode == Cs ? 10 : 8;
    // One against one: duel, tracking duel and vampiric 1v1.
    public static bool TwoPlayers(string mode) => mode is Duel or Tracking or Vampiric;
    // Players shoot each other; the host owns health, frags and respawns (CombatMatch).
    public static bool Combat(string mode) => mode is Deathmatch or Vampiric or Instagib or TeamDeathmatch;
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
// CsProblem: why the scenario's map can't host CS competitive (null: it can), from the host's
// library (the map's AimMod CS map spec). Only the host's value counts.
sealed record ScenarioChoice(string Name, string Hash, string Map, string MapHash, double TimeLimit, string? WorkshopId = null, string? CsProblem = null);
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
    bool AutoStart = false,
    bool Voting = true,
    int? FragLimit = null,
    int Lifesteal = 50,
    bool RequireFire = false,
    int HalfRounds = 12,
    bool Overtime = true,
    TournamentLock? Tournament = null,
    bool? FriendlyFire = null)
{
    // Team damage: on by default in CS competitive (CS2 rules), off elsewhere unless the host turns it on.
    [JsonIgnore] public bool EffectiveFriendlyFire => FriendlyFire ?? Mode == LobbyModes.Cs;
    public const int MinPlayers = 2, MaxPlayerLimit = 10, MaxSpectators = 4;
    [JsonIgnore] public ProfileChoice WeaponProfile => Weapon ?? ProfileChoice.Default;
    [JsonIgnore] public ProfileChoice MovementProfile => Movement ?? ProfileChoice.Default;
    [JsonIgnore] public ProfileChoice CharacterProfile => Character ?? ProfileChoice.Default;
    [JsonIgnore] public double EffectiveTimeLimit => Mode == LobbyModes.Tracking ? TimeLimit ?? TrackingDefaults.RoundSeconds
        : LobbyModes.Combat(Mode) ? TimeLimit ?? CombatRules.DefaultMatchSeconds : Mode == LobbyModes.Cs ? 10_800 : TimeLimit ?? Scenario?.TimeLimit ?? 60;
    [JsonIgnore] public int EffectiveFragLimit => FragLimit ?? CombatRules.DefaultFragLimit(Mode);
    // Tracking duel: Rounds is the number of rounds (both players track in each).
    [JsonIgnore] public int? TotalRounds => Mode switch { LobbyModes.Race or LobbyModes.Rounds or LobbyModes.Tracking => Rounds, LobbyModes.Deathmatch or LobbyModes.Vampiric or LobbyModes.Instagib or LobbyModes.TeamDeathmatch => 1, LobbyModes.Cs => HalfRounds * 2, _ => null };
    // Values that change what people play. Changing any of them clears ready states.
    [JsonIgnore] public string PlayKey => string.Join('|', Mode, Scenario?.Hash, MapOverride?.Hash, Rounds, FirstTo, TimeLimit,
        WeaponProfile, MovementProfile, CharacterProfile, TargetSpeed, TargetSize, FragLimit, Lifesteal, RequireFire, HalfRounds, Overtime, Tournament?.MatchId, Tournament?.Game, Tournament?.Seed);
}

// A lobby created for a tournament match (AimMod Hub). Its settings follow the
// tournament's ruleset and can't be changed in the lobby; only the match's two
// players play (Players: member ids), everyone else watches. Game is the
// 0-based game of the series and Seed its shared seed (same targets for both).
sealed record TournamentLock(string TournamentId, string MatchId, string Label, int Game, long Seed, IReadOnlyList<string> Players, string? Name = null);

static class MemberRoles { public const string Player = "player", Spectator = "spectator"; }
static class ContentStates { public const string Ok = "ok", Missing = "missing", Mismatch = "mismatch", Unknown = "unknown", None = "none"; }
static class Connections { public const string Connected = "connected", Reconnecting = "reconnecting"; }
static class MatchPhases { public const string Loading = "loading", Countdown = "countdown", Live = "live", Round = "round", Final = "final"; }
static class LineStates { public const string Waiting = "waiting", Playing = "playing", Finished = "finished", Dnf = "dnf", Left = "left", Dodger = "dodger"; }
// Tracking duel defaults (game-modes.md 6.3): short simultaneous rounds, best of three.
static class TrackingDefaults { public const int RoundSeconds = 10, MaxRoundSeconds = 60, Rounds = 3, MaxRounds = 9; }

// Connection: connected or reconnecting. Link: local (this machine), relay,
// direct or simulated. Profiles: whether custom weapon/character profiles are present.
sealed record LobbyMember(string Id, string Name, string Role, bool Ready, int? Ping, string Scenario, string Map, string Profiles,
    string Connection, string Link, long JoinedAt, bool Simulated, string Avatar = AvatarProfiles.Default, string? Version = null, bool Away = false,
    IReadOnlyList<CosmeticRef>? Cosmetics = null, int Team = 0);

sealed record ScoreLine(string MemberId, double? Score, double? Seconds, double? Remaining, int Shots, int Hits, int Kills,
    string Status, bool Disputed);

sealed record Placement(string MemberId, string Name, int Place, double? Score, double? Accuracy, int Points, string Status, bool Disputed);
sealed record RoundResult(int Round, IReadOnlyList<Placement> Results, string? WinnerId);
sealed record Standing(string MemberId, string Name, int Place, int Wins, int Points, double? Best, double Total, int Played);

sealed record MatchSnapshot(string Id, string Phase, string Mode, string Scenario, double TimeLimit, int Round, int? TotalRounds,
    int? FirstTo, long? StartsAt, long? EndsAt, long? NextAt, IReadOnlyList<string> Players, IReadOnlyList<ScoreLine> Live,
    IReadOnlyList<RoundResult> Rounds, IReadOnlyList<Standing> Standings, string? WinnerId, IReadOnlyList<string> Rematch, long? RematchDeadline = null, IReadOnlyList<string>? Loaded = null,
    string? Attacker = null, IReadOnlyList<TrackView>? Tracking = null, CombatView? Combat = null, CsView? Cs = null,
    IReadOnlyDictionary<string, string>? LoadIssues = null, bool LoadFailed = false, int LoadAttempt = 0);
// Live tracking-duel state for the HUD: each player's time on target so far (host score).
sealed record TrackView(string Member, double Percent, double Seconds, double Coverage, double LagMs, bool Disputed, string? Reason)
{
    public static TrackView Of(string member, TrackResult r) => new(member, r.Percent, r.OnTargetSeconds, r.Coverage, r.LagMs, r.Disputed, r.Reason);
}

// Clip: a shared clip replay id (everyone in the lobby received the file).
sealed record ChatLine(long Id, string? From, string Name, string Text, long At, bool System, string? Clip = null);

// ReadyCheck: when the host last asked everyone to ready up (host clock), while it is open.
sealed record LobbySnapshot(int V, string Id, string Code, long Revision, string HostId, LobbySettings Settings,
    IReadOnlyList<LobbyMember> Members, MatchSnapshot? Match, IReadOnlyList<ChatLine> Chat, long Now, long? ReadyCheck = null, long? AutoStartAt = null,
    IReadOnlyList<Suggestion>? Suggestions = null);
// A scenario a member suggested, with the members who voted for it.
sealed record Suggestion(string Scenario, string By, IReadOnlyList<string> Votes);

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
        "timeLimit", "weapon", "movement", "character", "targetSpeed", "targetSize", "privacy", "countdown", "lateJoin", "autoStart", "voting", "fragLimit", "lifesteal", "requireFire", "halfRounds", "overtime", "friendlyFire"];

    // A member id AimModCore accepts in play-state.tsv: [A-Za-z0-9_-]{1,64}.
    public static bool IsStreamSafe(string? id) => id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    // CS teams at the start: everyone keeps their pick (1 = T, 2 = CT); players on "either"
    // fill the smaller team, in join order.
    public static Dictionary<string, int> ResolveTeams(IReadOnlyList<(string Id, int Team)> players)
    {
        var teams = players.Where(p => p.Team is 1 or 2).ToDictionary(p => p.Id, p => p.Team);
        foreach (var (id, _) in players.Where(p => p.Team is not (1 or 2)))
            teams[id] = teams.Values.Count(t => t == 1) <= teams.Values.Count(t => t == 2) ? 1 : 2;
        return teams;
    }
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
        if (current.Tournament is not null) return (null, LobbyResult.Fail("tournament-locked", "This lobby follows the tournament’s ruleset, so its settings can’t change."));
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
                    if (LobbyModes.TwoPlayers(mode) && players > 2) return (null, LobbyResult.Fail("duel-players", "A duel is one against one. Move extra players to spectators first."));
                    // Switching mode starts from that mode's defaults, unless the same patch sets them.
                    next = ModeDefaults(next, mode, keepRounds: patch.TryGetProperty("rounds", out _)); break;
                case "scenario":
                    if (Text() is not { } scenarioName || !ValidContentName(scenarioName)) return (null, Bad("Choose a scenario from your library."));
                    if (resolve.Scenario(scenarioName) is not { } scenario) return (null, LobbyResult.Fail("scenario-missing", "That scenario isn’t in your library."));
                    if (next.Mode == LobbyModes.Cs && !patch.TryGetProperty("mode", out _) && scenario.CsProblem is { } csProblem)
                        return (null, LobbyResult.Fail("cs-map", "That map isn’t set up for CS: " + csProblem + "."));
                    next = next with { Scenario = scenario, MapOverride = null }; break;
                case "mapOverride":
                    if (value.ValueKind == JsonValueKind.Null) { next = next with { MapOverride = null }; break; }
                    if (Text() is not { } mapName || !ValidContentName(mapName)) return (null, Bad("Choose a map from your library."));
                    if (resolve.Map(mapName) is not { } map) return (null, LobbyResult.Fail("map-missing", "That map isn’t in your maps folder."));
                    next = next with { MapOverride = map }; break;
                case "maxPlayers":
                    if (Number() is not { } max) return (null, Bad("Players must be a number."));
                    next = next with { MaxPlayers = (int)Clamp(max, Math.Max(LobbySettings.MinPlayers, players), LobbyModes.MaxPlayers(next.Mode), 1) }; break;
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
                case "voting": if (Flag() is not { } vote) return (null, Bad("Suggestions must be on or off.")); next = next with { Voting = vote }; break;
                case "fragLimit":
                    if (value.ValueKind == JsonValueKind.Null) { next = next with { FragLimit = null }; break; }
                    if (Number() is not { } frags) return (null, Bad("Frag limit must be a number.")); next = next with { FragLimit = (int)Clamp(frags, 1, 100, 1) }; break;
                case "halfRounds": if (Number() is not { } half) return (null, Bad("Rounds per half must be a number.")); next = next with { HalfRounds = (int)Clamp(half, 6, 15, 1) }; break;
                case "overtime": if (Flag() is not { } ot) return (null, Bad("Overtime must be on or off.")); next = next with { Overtime = ot }; break;
                case "friendlyFire": if (Flag() is not { } ff) return (null, Bad("Friendly fire must be on or off.")); next = next with { FriendlyFire = ff }; break;
                case "requireFire": if (Flag() is not { } fire) return (null, Bad("Require fire must be on or off.")); next = next with { RequireFire = fire }; break;
                case "lifesteal": if (Number() is not { } steal) return (null, Bad("Lifesteal must be a percentage.")); next = next with { Lifesteal = (int)Clamp(steal, 0, 200, 5) }; break;
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

    // A mode's own defaults when a lobby switches to it (tracking duel: three attacks each).
    public static LobbySettings ModeDefaults(LobbySettings s, string mode, bool keepRounds = false)
    {
        if (mode == s.Mode) return s;
        var next = s with { Mode = mode };
        if (mode == LobbyModes.Tracking && !keepRounds) next = next with { Rounds = TrackingDefaults.Rounds };
        return next;
    }

    // Mode rules applied after every change, so combinations stay coherent.
    public static LobbySettings Normalize(LobbySettings s, int players)
    {
        if (LobbyModes.TwoPlayers(s.Mode)) s = s with { MaxPlayers = 2 };
        // A tournament may set the game's length; its games run in freeplay, never ranked.
        if (!LobbyModes.AllowsOverrides(s.Mode) && s.Tournament is null)
            s = s with { MapOverride = null, TimeLimit = null, Weapon = ProfileChoice.Default, Movement = ProfileChoice.Default, Character = ProfileChoice.Default, TargetSpeed = 1, TargetSize = 1 };
        if (!LobbyModes.AllowsLateJoin(s.Mode)) s = s with { LateJoin = false };
        // Picking the scenario's own length is no override, so no match scenario is generated for it.
        if (s.Mode != LobbyModes.Tracking && !LobbyModes.Combat(s.Mode) && s.TimeLimit is { } limit && s.Scenario is { } scenario && Math.Abs(limit - scenario.TimeLimit) < 0.5) s = s with { TimeLimit = null };
        if (s.Mode == LobbyModes.Race) s = s with { Rounds = Math.Clamp(s.Rounds, 1, 5) };
        // Combat modes: one match-long round of their own length (default 5 min), the mode's weapon, no target changes.
        if (LobbyModes.Combat(s.Mode)) s = s with { Rounds = 1, TimeLimit = Math.Clamp(s.TimeLimit ?? CombatRules.DefaultMatchSeconds, 60, 600), Weapon = ProfileChoice.Default, TargetSpeed = 1, TargetSize = 1 };
        // Tracking duel: short rounds of its own length (never the scenario's), up to five attacks each.
        if (s.Mode != LobbyModes.Tracking) s = s with { RequireFire = false };
        // CS: teams of 3, 4 or 5 (6, 8 or 10 players), CS2 halves, the bought weapons only.
        if (s.Mode == LobbyModes.Cs)
        {
            var max = Math.Clamp(s.MaxPlayers + s.MaxPlayers % 2, 6, 10);
            // CS plays the scenario's own map: its CS map spec is what makes it eligible.
            s = s with { Rounds = 1, TimeLimit = null, Weapon = ProfileChoice.Default, TargetSpeed = 1, TargetSize = 1, MaxPlayers = max, HalfRounds = Math.Clamp(s.HalfRounds, 6, 15), MapOverride = null };
        }
        else s = s with { MaxPlayers = Math.Min(s.MaxPlayers, LobbyModes.MaxPlayers(s.Mode)) };
        if (s.Mode == LobbyModes.Tracking) s = s with { Rounds = Math.Clamp(s.Rounds, 1, TrackingDefaults.MaxRounds), TimeLimit = Math.Clamp(s.TimeLimit ?? TrackingDefaults.RoundSeconds, 10, TrackingDefaults.MaxRoundSeconds) };
        if (s.MaxPlayers < Math.Max(LobbySettings.MinPlayers, players)) s = s with { MaxPlayers = Math.Min(LobbySettings.MaxPlayerLimit, Math.Max(LobbySettings.MinPlayers, players)) };
        return s with { Weapon = s.WeaponProfile, Movement = s.MovementProfile, Character = s.CharacterProfile };
    }

    // Snapshots from a remote host are trusted for display only after the same bounds hold.
    public static bool Plausible(LobbySettings s) =>
        LobbyModes.All.Contains(s.Mode) && LobbyPrivacy.All.Contains(s.Privacy)
        && s.MaxPlayers is >= LobbySettings.MinPlayers and <= LobbySettings.MaxPlayerLimit && s.MaxPlayers <= LobbyModes.MaxPlayers(s.Mode) && s.HalfRounds is >= 6 and <= 15
        && s.Rounds is >= 1 and <= 10 && s.FirstTo is >= 1 and <= 7
        && (s.Tournament is null || s.Tournament is { Seed: >= 0 and <= uint.MaxValue, Game: >= 0 and < 64, Players.Count: <= 2 } t && t.MatchId.Length is > 0 and <= 32 && t.TournamentId.Length is > 0 and <= 64)
        && s.TimeLimit is null or (>= 10 and <= 600) && s.FragLimit is null or (>= 1 and <= 100) && s.Lifesteal is >= 0 and <= 200 && s.TargetSpeed is >= 0.25 and <= 3 && s.TargetSize is >= 0.25 and <= 2 && s.Countdown is >= 3 and <= 10
        && (s.Scenario is null || (ValidContentName(s.Scenario.Name) && ValidHash(s.Scenario.Hash) && s.Scenario.TimeLimit is > 0 and <= 3600))
        && (s.MapOverride is null || (ValidContentName(s.MapOverride.Name) && ValidHash(s.MapOverride.Hash)))
        && new[] { s.WeaponProfile, s.MovementProfile, s.CharacterProfile }.All(p => ProfilePresets.Known(p.Preset));

    public static IReadOnlyList<StartBlocker> StartBlockers(LobbySnapshot lobby)
    {
        var list = new List<StartBlocker>();
        var s = lobby.Settings;
        // Away players are skipped: they never block the start and sit the match out.
        var players = lobby.Members.Where(m => m.Role == MemberRoles.Player && !m.Away).ToArray();
        if (lobby.Match is { Phase: not MatchPhases.Final }) { list.Add(new("in-match", "A match is already running.")); return list; }
        if (s.Scenario is null) list.Add(new("scenario", "Choose a scenario."));
        if (s.Mode == LobbyModes.Cs && s.Scenario?.CsProblem is { } csProblem) list.Add(new("cs-map", "This map isn’t set up for CS (" + csProblem + "). Pick a CS map."));
        if (LobbyModes.TwoPlayers(s.Mode) && players.Length != 2) list.Add(new("duel-players", "A duel needs exactly two players."));
        else if (s.Mode == LobbyModes.Cs && players.Length is not (6 or 8 or 10)) list.Add(new("cs-teams", "CS is 3v3, 4v4 or 5v5: it needs 6, 8 or 10 players (now " + players.Length + ")."));
        else if (s.Mode == LobbyModes.Cs && ResolveTeams(players.Select(p => (p.Id, p.Team)).ToList()) is { } resolved && resolved.Values.Count(t => t == 1) != resolved.Values.Count(t => t == 2))
            list.Add(new("cs-balance", "The teams are uneven: " + resolved.Values.Count(t => t == 1) + " T and " + resolved.Values.Count(t => t == 2) + " CT. Move someone or press Balance."));
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

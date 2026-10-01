using System.Globalization;
using System.Text.Json;

namespace AimMod.InGame.Tournaments;

// What the service keeps from AimMod Hub's tournament API (aimmod.tournament.v1,
// Connect JSON). Enum values arrive as their proto names ("MATCH_STATE_LIVE")
// and are kept as the short lower-case form ("live").
sealed record TEntrant(string Id, string Name, string Handle, int Seed);
sealed record TGame(int Index, string Scenario, int TimeLimit, string Seed, bool HasScore, double ScoreA, double ScoreB, int Winner, bool HostValidated);
sealed record TVeto(int Step, string Action, string Entrant, string Scenario);
sealed record TMatch(string Id, string Label, string Side, int Round, int Position, string State, int BestOf, int WinsA, int WinsB,
    string A, string B, bool EmptyA, bool EmptyB, string Winner, string Host, IReadOnlyList<string> Ready, int CurrentGame,
    IReadOnlyList<TGame> Games, IReadOnlyList<TVeto> Veto, string VetoTurn, string VetoAction, string Deadline, string ReportedBy,
    IReadOnlyList<string> Flags, string Resolution)
{
    public TGame? Current => CurrentGame >= 0 && CurrentGame < Games.Count ? Games[CurrentGame] : null;
}
sealed record TPool(string Name, int TimeLimit);
sealed record TRuleset(string GameMode, int BestOf, IReadOnlyList<TPool> Pool, int Countdown, bool Spectators, bool RequireReplays);
sealed record MyMatch(string TournamentId, string TournamentName, TMatch Match, TEntrant Self, TEntrant Opponent, TRuleset Ruleset,
    bool Host, string OpponentSteamId, string LobbyToken, bool Scheduled, string MatchToken = "");
sealed record CheckInDue(string TournamentId, string Name, string ClosesAt);
sealed record TournamentFeed(IReadOnlyList<MyMatch> Matches, IReadOnlyList<CheckInDue> CheckIn);
sealed record TournamentSummary(string Id, string Slug, string Name, string Status, string Format, int Entrants, int MaxEntrants, string StartsAt, bool Entered);
sealed record TournamentDetail(string Id, string Slug, string Name, string Status, string Format, string Champion,
    IReadOnlyList<TEntrant> Entrants, IReadOnlyList<TMatch> Matches, bool CanCheckIn, string SelfEntrant);

// Live state the match host pushes to the Hub (LiveMatch).
sealed record LivePlayerState(string Entrant, double Score, double Accuracy, double SecondsLeft, int Ping, string Connection, string Status, bool Ready);
sealed record LiveMatchState(string MatchId, int GameIndex, string Scenario, string Phase, IReadOnlyList<LivePlayerState> Players, int Spectators, string? LobbyToken);

// One finished tournament game as the lobby host reports it: the result
// record of in-game/docs/game-modes.md 8.4 with entrant ids, the seed and
// Unix millisecond times (Hub GameResult).
sealed record ResultPlayer(string Key, string Entrant, int Place, double Score, double Accuracy, string? Replay, bool Disputed);
sealed record GameResultRecord(string Match, string Mode, string? SettingsKey, string? ScenarioHash, long StartedAt, long EndedAt, string Host,
    IReadOnlyList<ResultPlayer> Players, string? Winner, string Seed);

static class TournamentJson
{
    public static string Text(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? Clean(v.GetString(), 256) : "";
    public static double Num(JsonElement e, string key)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d)) return d;
        // protojson writes 64-bit integers as strings.
        return v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && double.IsFinite(s) ? s : 0;
    }
    public static int Int(JsonElement e, string key, int fallback = 0) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out _) ? (int)Math.Clamp(Num(e, key), int.MinValue, int.MaxValue) : fallback;
    public static bool Flag(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
    public static JsonElement Obj(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;
    public static IEnumerable<JsonElement> Arr(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Take(1024) : [];
    static string Clean(string? s, int max)
    {
        var t = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return t.Length > max ? t[..max] : t;
    }
    // "MATCH_STATE_AWAITING_CONFIRMATION" -> "awaiting_confirmation"
    public static string Enum(JsonElement e, string key, string prefix)
    {
        var raw = Text(e, key);
        if (raw.Length == 0) return "";
        return (raw.StartsWith(prefix, StringComparison.Ordinal) ? raw[prefix.Length..] : raw).ToLowerInvariant();
    }

    public static TEntrant? Entrant(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || Text(e, "id").Length == 0) return null;
        var user = Obj(e, "user");
        var name = Text(user, "displayName");
        var handle = Text(user, "handle");
        return new TEntrant(Text(e, "id"), LobbyName(name.Length > 0 ? name : handle), handle, Int(e, "seed"));
    }
    static string LobbyName(string s) => Multiplayer.LobbyRules.CleanName(s, "Player");

    public static TMatch Match(JsonElement m)
    {
        var a = Obj(m, "slotA"); var b = Obj(m, "slotB");
        var games = Arr(m, "games").Select(g => new TGame(Int(g, "index"), Text(g, "scenario"), Int(g, "timeLimitSeconds"), Text(g, "seed"),
            Flag(g, "hasScore"), Num(g, "scoreA"), Num(g, "scoreB"), Int(g, "winner", -1), Flag(g, "hostValidated"))).ToArray();
        var veto = Arr(m, "veto").Select(v => new TVeto(Int(v, "step"), Enum(v, "action", "VETO_ACTION_"), Text(v, "entrantId"), Text(v, "scenario"))).ToArray();
        return new TMatch(Text(m, "id"), Text(m, "label"), Enum(m, "side", "BRACKET_SIDE_"), Int(m, "round"), Int(m, "position"),
            Enum(m, "state", "MATCH_STATE_"), Math.Max(1, Int(m, "bestOf", 1)), Int(m, "winsA"), Int(m, "winsB"),
            Text(a, "entrantId"), Text(b, "entrantId"), Flag(a, "empty"), Flag(b, "empty"), Text(m, "winnerEntrantId"), Text(m, "hostEntrantId"),
            Arr(m, "readyEntrantIds").Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray(),
            Int(m, "currentGame", -1), games, veto, Text(m, "vetoTurnEntrantId"), Enum(m, "vetoTurnAction", "VETO_ACTION_"),
            Text(m, "deadline"), Text(m, "reportedBy"), Arr(m, "flags").Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray(),
            Enum(m, "resolution", "RESOLUTION_"));
    }

    public static TRuleset Ruleset(JsonElement r) => new(Text(r, "gameMode") is { Length: > 0 } mode ? mode : "score-race", Math.Max(1, Int(r, "bestOf", 1)),
        Arr(r, "pool").Select(p => new TPool(Text(p, "name"), Int(p, "timeLimitSeconds"))).Where(p => p.Name.Length > 0).ToArray(),
        Math.Clamp(Int(r, "countdownSeconds", 5), 3, 10), Flag(r, "spectators"), Flag(r, "requireReplays"));

    public static TournamentFeed Feed(JsonElement root)
    {
        var matches = new List<MyMatch>();
        foreach (var m in Arr(root, "matches"))
        {
            var self = Entrant(Obj(m, "self")); var opp = Entrant(Obj(m, "opponent"));
            if (self is null || opp is null || Text(m, "tournamentId").Length == 0) continue;
            matches.Add(new MyMatch(Text(m, "tournamentId"), Text(m, "tournamentName"), Match(Obj(m, "match")), self, opp, Ruleset(Obj(m, "ruleset")),
                Flag(m, "host"), Text(m, "opponentSteamId"), Text(m, "lobbyToken"), Text(m, "scheduling") == "SCHEDULING_MODE_SCHEDULED", Text(m, "matchToken")));
        }
        var due = Arr(root, "checkIn").Select(c => Obj(c, "tournament")).Where(t => Text(t, "id").Length > 0)
            .Select(t => new CheckInDue(Text(t, "id"), Text(t, "name"), Text(t, "checkInClosesAt"))).ToArray();
        return new TournamentFeed(matches, due);
    }

    public static IReadOnlyList<TournamentSummary> List(JsonElement root) => Arr(root, "tournaments").Select(s =>
    {
        var t = Obj(s, "tournament");
        return new TournamentSummary(Text(t, "id"), Text(t, "slug"), Text(t, "name"), Enum(t, "status", "TOURNAMENT_STATUS_"), Enum(t, "format", "TOURNAMENT_FORMAT_"),
            Int(t, "entrantCount"), Int(t, "maxEntrants"), Text(t, "startsAt"), Obj(s, "self").ValueKind == JsonValueKind.Object);
    }).Where(s => s.Id.Length > 0).ToArray();

    public static TournamentDetail Detail(JsonElement root)
    {
        var t = Obj(root, "tournament"); var viewer = Obj(root, "viewer");
        return new TournamentDetail(Text(t, "id"), Text(t, "slug"), Text(t, "name"), Enum(t, "status", "TOURNAMENT_STATUS_"), Enum(t, "format", "TOURNAMENT_FORMAT_"),
            Text(t, "championEntrantId"), Arr(root, "entrants").Select(Entrant).Where(e => e is not null).Select(e => e!).ToArray(),
            Arr(root, "matches").Select(Match).ToArray(), Flag(viewer, "canCheckIn"), Text(viewer, "entrantId"));
    }
}

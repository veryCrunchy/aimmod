namespace AimMod.InGame.Multiplayer;

// In-match chat (game-modes.md 6.11): all chat and team chat in every mode, CS radio callouts,
// and what bots say. Every line goes through the host's lobby chat (LobbyCore "chat"); the host
// sends each member only the lines they may see:
//  - team lines reach the sender's match team only (modes without teams have all chat only);
//  - in CS, a dead player's lines reach only dead players and spectators until the match ends
//    (the lobby setting deadTalk lets them reach everyone).
// Team lines carry where the sender stands, like CS ("[team] Name @ A Site: Enemy spotted").
static class MatchChat
{
    public const string All = "all", Team = "team";
    public const int MaxPlace = 32;
    // Bots: at most one line per bot every 3 s, and the same callout once per team in 5 s.
    public const long BotGapMs = 3000, BotRepeatMs = 5000;

    // Who a member is in a snapshot's match: their team (0: none), whether they are down, and
    // whether they play in it at all (spectators and lobby members outside the match don't).
    public readonly record struct Viewer(int Team, bool Dead, bool Playing);
    public static Viewer ViewerOf(LobbySnapshot s, string id)
    {
        var m = s.Match;
        var playing = m is not null && m.Players.Contains(id);
        if (m?.Cs?.Players.FirstOrDefault(p => p.Member == id) is { } cs) return new(cs.Team, !cs.Alive, playing);
        if (m?.Combat?.Players.FirstOrDefault(p => p.Member == id) is { } c) return new(c.Team is 1 or 2 && Teams(m) ? c.Team : 0, !c.Alive, playing);
        var member = s.Members.FirstOrDefault(x => x.Id == id);
        return new(member is { Team: 1 or 2 } && Teams(m) ? member.Team : 0, false, playing);
    }
    // The match has two teams: CS and team deathmatch.
    public static bool Teams(MatchSnapshot? m) => m is not null && (m.Cs is not null || m.Mode == LobbyModes.TeamDeathmatch);
    public static bool Teams(string mode) => mode is LobbyModes.Cs or LobbyModes.TeamDeathmatch;

    // Whether a viewer may see a line.
    public static bool Visible(ChatLine line, string viewerId, Viewer viewer, LobbySnapshot s)
    {
        if (line.From == viewerId) return true;
        if (line.Scope == Team && (viewer.Team == 0 || viewer.Team != line.Team)) return false;
        if (line.Dead && !s.Settings.DeadTalk && s.Match is { Cs: not null } m && m.Phase != MatchPhases.Final && line.Match == m.Id && viewer.Playing && !viewer.Dead) return false;
        return true;
    }

    // The snapshot one member receives: the chat lines they may see (the rest is everyone's).
    public static LobbySnapshot For(LobbySnapshot s, string viewerId)
    {
        if (s.Chat.All(l => l.Scope is null && !l.Dead)) return s;
        var viewer = ViewerOf(s, viewerId);
        var lines = s.Chat.Where(l => Visible(l, viewerId, viewer, s)).ToArray();
        return lines.Length == s.Chat.Count ? s : s with { Chat = lines };
    }

    // Where someone stands for a callout: the map's own callout zone ("Long A"), else a bomb site
    // they are in or next to ("A Site"), else a side's spawn ("T Spawn"); null when none is close.
    // Positions are the pose feed's (world units, z at the eyes like the CS zones expect).
    public const double SiteNearCm = 600, SpawnNearCm = 1000;
    public static string? Location(MapObjectives? map, double x, double y, double z)
    {
        if (map is null || !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) return null;
        if (map.Callouts.FirstOrDefault(c => c.Contains(x, y, z)) is { } call) return Clip(call.Name);
        var site = map.BombSites.Select(s => (s.Name, D: Outside(s, x, y))).Where(s => s.Name.Length > 0 && s.D <= SiteNearCm).OrderBy(s => s.D).FirstOrDefault();
        if (site.Name is not null) return Clip(site.Name.Length <= 2 ? site.Name + " Site" : site.Name);
        var spawn = map.Spawns.Where(p => p.Team is "terrorist" or "counter_terrorist").Select(p => (p.Team, D: Math.Sqrt((p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y))))
            .Where(p => p.D <= SpawnNearCm).OrderBy(p => p.D).FirstOrDefault();
        return spawn.Team is null ? null : spawn.Team == "terrorist" ? "T Spawn" : "CT Spawn";
    }
    // Horizontal distance from a point to a zone's box (0 inside).
    static double Outside(ObjectiveZone z, double x, double y)
    {
        var dx = Math.Max(Math.Max(z.Min[0] - x, 0), x - z.Max[0]);
        var dy = Math.Max(Math.Max(z.Min[1] - y, 0), y - z.Max[1]);
        return Math.Sqrt(dx * dx + dy * dy);
    }
    static string? Clip(string? name)
    {
        var clean = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length == 0 ? null : clean.Length > MaxPlace ? clean[..MaxPlace] : clean;
    }

    // Radio callouts (Z, X, C like CS): each goes out as a team line with the caller's location.
    public sealed record RadioGroup(string Key, string Title, IReadOnlyList<string> Items);
    public static readonly IReadOnlyList<RadioGroup> Radio =
    [
        new("Z", "Commands", ["Going A", "Going B", "Rotate", "Hold this position", "Regroup", "Follow me", "Fall back"]),
        new("X", "Reports", ["Enemy spotted", "Need backup", "Taking fire", "Bomb spotted", "Sector clear", "In position"]),
        new("C", "Responses", ["Affirmative", "Negative", "Thanks", "Sorry", "Nice shot", "Enemy down"]),
    ];
    public static string? RadioText(int group, int item) => group >= 0 && group < Radio.Count && item >= 0 && item < Radio[group].Items.Count ? Radio[group].Items[item] : null;
}

// Rate limits for bot lines (MultiplayerService.BotSay): one line per bot every BotGapMs, and a
// callout a teammate's bot already said in the last BotRepeatMs isn't said again, so five bots
// don't all shout "Enemy spotted" (the service also skips what anyone on the team just said).
sealed class BotChatLimiter
{
    readonly Dictionary<string, long> lastBy = new(StringComparer.Ordinal);
    readonly Dictionary<string, long> lastSaid = new(StringComparer.Ordinal);
    public static string Key(int team, bool teamOnly, string text) => (teamOnly ? "t" + team : "all") + "|" + string.Join(' ', text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    // The bot hasn't spoken in the last BotGapMs (a cheap check before anything else).
    public bool Ready(string bot, long now) => !(lastBy.TryGetValue(bot, out var at) && now - at < MatchChat.BotGapMs);
    // Whether the bot may say it now (and records it if so).
    public bool Allow(string bot, int team, bool teamOnly, string text, long now)
    {
        if (!Ready(bot, now)) return false;
        var key = Key(team, teamOnly, text);
        if (lastSaid.TryGetValue(key, out var said) && now - said < MatchChat.BotRepeatMs) return false;
        lastBy[bot] = now; lastSaid[key] = now;
        if (lastSaid.Count > 256) foreach (var old in lastSaid.Where(kv => now - kv.Value >= MatchChat.BotRepeatMs).Select(kv => kv.Key).ToArray()) lastSaid.Remove(old);
        return true;
    }
}

// The chat input on this machine (Y: all chat, U: team chat), as a state machine the service steps
// every tick with what the keys did. Open, the notice layer takes the keyboard (AimModCore holds
// UI-only input like for the buy menu, so movement and fire stop) and the page types the line.
// It always closes again: Enter (the page sends the text), Escape, the game window losing focus
// for half a second, the match ending, or 2 minutes without a word.
sealed class ChatInput
{
    public const long FocusLossMs = 500, SendGraceMs = 2000, IdleMs = 120_000;
    public string? Scope { get; private set; }
    public long Session { get; private set; }
    public bool Open => Scope is not null;
    // Why it last closed: "enter", "escape", "focus", "sent", "cancel", "idle" or "match".
    public string? Closed { get; private set; }
    long closedAt = long.MinValue / 2, openedAt; long? awaySince; string? closedScope; long closedSession;

    public bool Begin(string scope, long now)
    {
        if (Open) return false;
        Scope = scope is MatchChat.Team ? MatchChat.Team : MatchChat.All;
        Session++; openedAt = now; awaySince = null; Closed = null;
        return true;
    }
    public void Close(string why, long now)
    {
        if (!Open) return;
        closedScope = Scope; closedSession = Session; closedAt = now; Closed = why; Scope = null; awaySince = null;
    }
    // One service tick: the game window in front, and Escape and Enter press edges.
    public void Step(bool foreground, bool escape, bool enter, long now)
    {
        if (!Open) return;
        if (!foreground) { awaySince ??= now; if (now - awaySince.Value >= FocusLossMs) Close("focus", now); return; }
        awaySince = null;
        if (escape) Close("escape", now);
        else if (enter) Close("enter", now);
        else if (now - openedAt > IdleMs) Close("idle", now);
    }
    // The page sends the line for its session: taken while open, or just after Enter closed it (the
    // service may see Enter before the page's request arrives). Returns the scope, or null.
    public string? Accept(long session, long now)
    {
        if (Open && session == Session) { var scope = Scope; Close("sent", now); return scope; }
        if (!Open && session == closedSession && Closed == "enter" && now - closedAt <= SendGraceMs) { Closed = "sent"; return closedScope; }
        return null;
    }
    // The page cancelled (Escape it saw itself).
    public void Cancel(long session, long now) { if (Open && session == Session) Close("cancel", now); }
}

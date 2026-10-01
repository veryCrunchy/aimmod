using System.Security.Cryptography;
using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

// The host's authoritative lobby. Every change comes in as a command from a
// member id and is validated here, whether it came from this machine, a
// simulated member or a remote peer. Clients only mirror snapshots.
sealed class LobbyCore
{
    public const int ChatLimit = 60;
    public const long RoundGraceMs = 10_000, ResultsMs = 8_000, MemberGraceMs = 30_000, HostGraceMs = 10_000, RematchMs = 20_000, AutoStartMs = 3_000;
    sealed class Member
    {
        public required string Id; public required string Name; public string Role = MemberRoles.Player;
        public bool Ready; public int? Ping; public string Scenario = ContentStates.Unknown, Map = ContentStates.Unknown, Profiles = ContentStates.None;
        public string Connection = Connections.Connected, Link = "local"; public long JoinedAt; public long? LostAt; public bool Simulated; public string Avatar = AvatarProfiles.Default; public string? Version; public bool Away; public IReadOnlyList<CosmeticRef> Cosmetics = [];
        public readonly Queue<long> ChatTimes = new();
        public LobbyMember View() => new(Id, Name, Role, Ready, Ping, Scenario, Map, Profiles, Connection, Link, JoinedAt, Simulated, Avatar, Version, Away, Cosmetics);
    }
    sealed class Line
    {
        public double? Score, Seconds, Remaining; public int Shots, Hits, Kills; public string Status = LineStates.Waiting; public bool Disputed;
        public ScoreLine View(string id) => new(id, Score, Seconds, Remaining, Shots, Hits, Kills, Status, Disputed);
    }
    sealed class Match
    {
        public required string Id; public required LobbySettings Settings; public string Phase = MatchPhases.Countdown;
        public int Round = 1; public long? StartsAt, EndsAt, NextAt; public List<string> Players = [];
        public Dictionary<string, Line> Live = new(); public List<RoundResult> Rounds = []; public HashSet<string> Rematch = [];
        public Dictionary<string, string> Names = new(); public string? WinnerId; public bool Over; public long? RematchDeadline; public HashSet<string> Loaded = [];
        // Tracking duel: the current round's samples, and the last host scores (first, second player).
        public TrackingRound? Tracking; public (TrackResult First, TrackResult Second)? TrackLast; public long TrackComputedAt;
        // Combat modes: the host's health, frags and respawns for the whole match.
        public CombatMatch? Combat; public long CombatEvents = -1;
        // CS: the round machine, economy and bomb (its CombatMatch validates the hits).
        public CsMatch? Cs; public string? CsKey;
    }
    bool Combat => match is not null && LobbyModes.Shooting(match.Settings.Mode);
    // The arena's CS objectives (bomb sites, buy zones, side spawns), set by the service.
    MapObjectives? csObjectives;
    public void SetCsObjectives(MapObjectives? objectives) => csObjectives = objectives;
    // The arena's spawn points for host-chosen respawns (set by the service from the generated scenario).
    IReadOnlyList<SpawnPoint> combatSpawns = [];
    public void SetCombatSpawns(IReadOnlyList<SpawnPoint> spawns)
    {
        combatSpawns = spawns;
        if (match?.Combat is { } c) c.Spawns = spawns;
    }
    // Tracking duel: samples may still arrive this long after the round's end.
    public const long TrackGraceMs = 1_000;
    bool Tracking => match?.Settings.Mode == LobbyModes.Tracking;

    readonly Func<long> clock;
    readonly List<Member> members = [];
    readonly List<ChatLine> chat = [];
    readonly HashSet<string> banned = [];
    Match? match;
    long chatId;
    public string Id { get; }
    public string Code { get; }
    public string HostId { get; private set; }
    public LobbySettings Settings { get; private set; }
    public long Revision { get; private set; }
    public bool Closed { get; private set; }
    // Raised once when a match reaches its final results.
    public event Action<MatchSnapshot, IReadOnlyList<LobbyMember>>? MatchFinished;

    public LobbyCore(string hostId, string hostName, LobbySettings settings, Func<long> clock, string? id = null, string? code = null, bool hostSimulated = false)
    {
        this.clock = clock;
        Id = id ?? "l-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        Code = code ?? NewCode();
        HostId = hostId;
        Settings = LobbyRules.Normalize(settings, 1);
        members.Add(new Member { Id = hostId, Name = LobbyRules.CleanName(hostName, "Host"), JoinedAt = clock(), Simulated = hostSimulated, Link = hostSimulated ? "simulated" : "local" });
        System(members[0].Name + " created the lobby.");
    }

    // Room codes avoid look-alike characters so they can be read out loud.
    public static string NewCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = RandomNumberGenerator.GetBytes(6);
        return new string(bytes.Select(b => alphabet[b % alphabet.Length]).ToArray());
    }
    public static bool ValidCode(string? code) => code is { Length: 6 } && code.All(c => "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".Contains(c));

    Member? Find(string id) => members.FirstOrDefault(m => m.Id == id);
    bool IsHost(string id) => id == HostId;
    int PlayerCount => members.Count(m => m.Role == MemberRoles.Player);
    void Changed() => Revision++;
    void System(string text) { AddChat(null, "", text, true); }
    void AddChat(string? from, string name, string text, bool system, string? clip = null)
    {
        chat.Add(new ChatLine(++chatId, from, name, text, clock(), system, clip));
        if (chat.Count > ChatLimit) chat.RemoveRange(0, chat.Count - ChatLimit);
        Changed();
    }
    // A clip a member shared, once its file has reached the host.
    public void ShareClip(string from, string clip, string? label)
    {
        if (Find(from) is not { } m || !ReplaySwap.ValidId(clip)) return;
        AddChat(m.Id, m.Name, "shared a clip" + (LobbyRules.CleanChat(label) is { } l ? ": " + l : "."), false, clip);
    }

    public IReadOnlyList<LobbyMember> Members => members.Select(m => m.View()).ToArray();
    public const long ReadyCheckMs = 30_000;
    long? readyCheck, autoStartAt;
    public LobbySnapshot Snapshot() => new(Protocol.Version, Id, Code, Revision, HostId, Settings, Members, MatchView(), chat.ToArray(), clock(), readyCheck, autoStartAt, suggestions.Select(s => new Suggestion(s.Scenario, s.By, s.Votes.ToArray())).ToArray());
    // Wait for everyone to load the scenario before the countdown (set when AimMod can load scenarios).
    public bool RequireLoading { get; set; }
    public const long LoadingMs = 20_000;
    readonly List<(string Scenario, string By, HashSet<string> Votes)> suggestions = [];

    public LobbyResult Join(string id, string name, bool simulated = false, string? version = null)
    {
        if (Closed) return LobbyResult.Fail("closed", "This lobby has closed.");
        if (banned.Contains(id)) return LobbyResult.Fail("kicked", "The host removed you from this lobby.");
        if (Find(id) is { } existing)
        {
            if (version is not null && existing.Version != version) { existing.Version = version; Changed(); }
            if (existing.Connection != Connections.Connected) { existing.Connection = Connections.Connected; existing.LostAt = null; System(existing.Name + " reconnected."); }
            return LobbyResult.Success;
        }
        var role = MemberRoles.Player;
        var inMatch = match is { Phase: not MatchPhases.Final };
        // A player of the running match who dropped out (crash, lost connection) rejoins as a player.
        var returning = match is not null && match.Players.Contains(id);
        if (!returning && (PlayerCount >= Settings.MaxPlayers || (inMatch && !Settings.LateJoin))) role = MemberRoles.Spectator;
        if (role == MemberRoles.Spectator && (!Settings.Spectators || members.Count(m => m.Role == MemberRoles.Spectator) >= LobbySettings.MaxSpectators))
            return LobbyResult.Fail(inMatch ? "in-match" : "full", inMatch ? "A match is in progress and late join is off." : "This lobby is full.");
        var member = new Member { Id = id, Name = UniqueName(LobbyRules.CleanName(name, "Player")), Role = role, JoinedAt = clock(), Simulated = simulated, Link = simulated ? "simulated" : "relay", Version = version };
        members.Add(member);
        if (match is not null) match.Names[id] = member.Name;
        if (returning && match!.Live.TryGetValue(id, out var line) && line.Status == LineStates.Left && match.Phase is MatchPhases.Countdown or MatchPhases.Live)
            line.Status = line.Score is null ? LineStates.Waiting : LineStates.Playing;
        System(member.Name + (returning ? " is back." : role == MemberRoles.Spectator ? " is watching." : " joined."));
        return LobbyResult.Success;
    }
    string UniqueName(string name)
    {
        var candidate = name; var n = 2;
        while (members.Any(m => m.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase))) candidate = name + " " + n++;
        return candidate;
    }

    public LobbyResult Leave(string id, string reason = "left")
    {
        var member = Find(id);
        if (member is null) return LobbyResult.Fail("unknown", "Not in this lobby.");
        members.Remove(member);
        if (match is not null && match.Live.TryGetValue(id, out var line) && line.Status is LineStates.Waiting or LineStates.Playing) line.Status = LineStates.Left;
        match?.Rematch.Remove(id);
        System(member.Name + (reason == "kicked" ? " was removed by the host." : reason == "timeout" ? " lost connection." : " left."));
        if (members.Count == 0) { Closed = true; Changed(); return LobbyResult.Success; }
        if (id == HostId) Migrate(member.Name);
        // Combat: a player leaving drops out; the match ends when fewer than two remain.
        if (match is { Phase: MatchPhases.Live, Cs: { } leftCs }) leftCs.Leave(id, clock());
        if (match is { Phase: MatchPhases.Live, Combat: { } combat })
        {
            combat.Leave(id);
            if (match.Players.Count(p => Find(p) is not null) < 2) { CloseRound(); return LobbyResult.Success; }
        }
        // A tracking round needs both players: it ends when either leaves.
        if (match is { Phase: MatchPhases.Live, Tracking: { } tr } && (tr.First == id || tr.Second == id)) { CloseRound(); return LobbyResult.Success; }
        if (match is { Phase: MatchPhases.Live } m && m.Live.Values.All(l => l.Status is not (LineStates.Waiting or LineStates.Playing))) CloseRound();
        return LobbyResult.Success;
    }

    // The next host is the longest-connected player, then the longest-connected spectator.
    void Migrate(string previous)
    {
        var next = members.Where(m => m.Connection == Connections.Connected).OrderBy(m => m.Role == MemberRoles.Player ? 0 : 1).ThenBy(m => m.JoinedAt).FirstOrDefault()
            ?? members.OrderBy(m => m.JoinedAt).First();
        HostId = next.Id;
        next.Ready = false;
        System(next.Name + " is now the host.");
    }

    public void Disconnected(string id)
    {
        if (Find(id) is not { } m || m.Connection != Connections.Connected) return;
        m.Connection = Connections.Reconnecting; m.LostAt = clock();
        System(m.Name + (IsHost(id) ? " (host) lost connection." : " lost connection."));
    }

    // A member's catalog items (cosmetic.look); the caller checked the structure.
    public void SetCosmetics(string id, IReadOnlyList<CosmeticRef> items)
    {
        var member = members.FirstOrDefault(m => m.Id == id);
        if (member is null) return;
        var next = items.Take(CosmeticsCatalog.MaxEquipped).ToArray();
        if (member.Cosmetics.SequenceEqual(next)) return;
        member.Cosmetics = next; Changed();
    }
    public void SetVersion(string id, string? version) { if (Find(id) is { } m && version is not null && m.Version != version) { m.Version = version; Changed(); } }
    public void SetLink(string id, string? route, int? ping)
    {
        if (Find(id) is not { } m) return;
        var clamped = ping is null ? null : (int?)Math.Clamp(ping.Value, 0, 9999);
        var link = route is "relay" or "direct" or "local" or "simulated" ? route : m.Link;
        if (m.Ping == clamped && m.Link == link) return;
        m.Ping = clamped; m.Link = link; Changed();
    }

    public LobbyResult Apply(string from, string action, JsonElement args, IContentResolver resolve)
    {
        var member = Find(from);
        if (member is null) return LobbyResult.Fail("unknown", "Not in this lobby.");
        string? Text(string key) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool? Flag(string key) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
        LobbyResult HostOnly() => LobbyResult.Fail("not-host", "Only the host can do that.");
        switch (action)
        {
            case "ready":
                if (Flag("ready") is not { } ready) return LobbyResult.Fail("invalid", "Ready must be true or false.");
                if (member.Role != MemberRoles.Player) return LobbyResult.Fail("spectator", "Spectators don’t ready up.");
                if (ready && Settings.Scenario is not null && (member.Scenario != ContentStates.Ok || member.Map != ContentStates.Ok))
                    return LobbyResult.Fail("content", "You need the scenario and map before you can ready up.");
                if (member.Ready != ready || (ready && member.Away)) { member.Ready = ready; if (ready) member.Away = false; Changed(); }
                return LobbyResult.Success;
            case "content":
                var scenario = Text("scenario"); var map = Text("map"); var profiles = Text("profiles") ?? ContentStates.None;
                string[] states = [ContentStates.Ok, ContentStates.Missing, ContentStates.Mismatch, ContentStates.Unknown, ContentStates.None];
                if (scenario is null || map is null || !states.Contains(scenario) || !states.Contains(map) || !states.Contains(profiles)) return LobbyResult.Fail("invalid", "Unknown content state.");
                if (member.Scenario != scenario || member.Map != map || member.Profiles != profiles)
                {
                    member.Scenario = scenario; member.Map = map; member.Profiles = profiles;
                    if (scenario != ContentStates.Ok || map != ContentStates.Ok || profiles is ContentStates.Missing or ContentStates.Mismatch) member.Ready = false;
                    Changed();
                }
                return LobbyResult.Success;
            case "settings":
                if (!IsHost(from)) return HostOnly();
                if (match is { Phase: not MatchPhases.Final }) return LobbyResult.Fail("in-match", "Settings are locked during a match.");
                if (!args.TryGetProperty("settings", out var patch)) return LobbyResult.Fail("invalid", "No settings supplied.");
                var (next, result) = LobbyRules.Apply(Settings, patch, PlayerCount, resolve);
                if (next is null) return result;
                if (next == Settings) return LobbyResult.Success;
                var reset = next.PlayKey != Settings.PlayKey;
                var scenarioChanged = next.Scenario?.Hash != Settings.Scenario?.Hash || next.MapOverride?.Hash != Settings.MapOverride?.Hash;
                Settings = next;
                if (reset) foreach (var m in members) m.Ready = false;
                if (scenarioChanged) foreach (var m in members.Where(m => m.Id != HostId)) { m.Scenario = ContentStates.Unknown; m.Map = ContentStates.Unknown; }
                if (scenarioChanged && Settings.Scenario is not null) System("Scenario set to " + Settings.Scenario.Name + ".");
                Changed();
                return LobbyResult.Success;
            case "kick":
                if (!IsHost(from)) return HostOnly();
                var target = Text("member");
                if (target is null || target == from || Find(target) is null) return LobbyResult.Fail("invalid", "Choose someone in the lobby.");
                banned.Add(target);
                return Leave(target, "kicked");
            case "transfer":
                if (!IsHost(from)) return HostOnly();
                var heir = Text("member") is { } heirId ? Find(heirId) : null;
                if (heir is null || heir.Id == from || heir.Connection != Connections.Connected) return LobbyResult.Fail("invalid", "Choose a connected member.");
                HostId = heir.Id; heir.Ready = false;
                System(member.Name + " made " + heir.Name + " the host.");
                return LobbyResult.Success;
            case "chat":
                var text = LobbyRules.CleanChat(Text("text"));
                if (text is null) return LobbyResult.Fail("invalid", "Type a message first.");
                var now = clock();
                while (member.ChatTimes.Count > 0 && now - member.ChatTimes.Peek() > 5000) member.ChatTimes.Dequeue();
                if (member.ChatTimes.Count >= 5) return LobbyResult.Fail("slow", "Slow down a little.");
                member.ChatTimes.Enqueue(now);
                AddChat(member.Id, member.Name, text, false);
                return LobbyResult.Success;
            case "role":
                if (Flag("spectator") is not { } spectate) return LobbyResult.Fail("invalid", "Role must be player or spectator.");
                if (match is { Phase: not MatchPhases.Final }) return LobbyResult.Fail("in-match", "Roles are locked during a match.");
                if (spectate && !Settings.Spectators) return LobbyResult.Fail("spectators-off", "Spectators are off in this lobby.");
                if (spectate && member.Role == MemberRoles.Player && members.Count(m => m.Role == MemberRoles.Spectator) >= LobbySettings.MaxSpectators) return LobbyResult.Fail("full", "Spectator slots are full.");
                if (!spectate && member.Role == MemberRoles.Spectator && PlayerCount >= Settings.MaxPlayers) return LobbyResult.Fail("full", "All player slots are taken.");
                var role = spectate ? MemberRoles.Spectator : MemberRoles.Player;
                if (member.Role != role) { member.Role = role; member.Ready = false; Changed(); }
                return LobbyResult.Success;
            case "away":
                if (Flag("away") is not { } away) return LobbyResult.Fail("invalid", "Away must be true or false.");
                if (member.Away != away) { member.Away = away; if (away) member.Ready = false; System(member.Name + (away ? " is away." : " is back.")); }
                return LobbyResult.Success;
            case "skip":
                // The host marks someone away so they don't hold up the start.
                if (!IsHost(from)) return HostOnly();
                if (Text("member") is not { } skipped || Find(skipped) is not { } sk || sk.Id == from) return LobbyResult.Fail("invalid", "Choose someone in the lobby.");
                if (!sk.Away) { sk.Away = true; sk.Ready = false; System(sk.Name + " was marked away."); }
                return LobbyResult.Success;
            case "suggest":
                if (!Settings.Voting) return LobbyResult.Fail("voting-off", "The host turned suggestions off.");
                if (match is { Phase: not MatchPhases.Final }) return LobbyResult.Fail("in-match", "Suggest after the match.");
                var suggested = Text("scenario");
                if (string.IsNullOrWhiteSpace(suggested) || suggested.Length > LobbyRules.MaxContentName || suggested.Any(char.IsControl)) return LobbyResult.Fail("invalid", "Pick a scenario to suggest.");
                var existing2 = suggestions.FirstOrDefault(s => s.Scenario.Equals(suggested, StringComparison.OrdinalIgnoreCase));
                if (existing2.Scenario is null)
                {
                    if (suggestions.Count >= 8) return LobbyResult.Fail("full", "There are enough suggestions. Vote for one.");
                    suggestions.Add((suggested.Trim(), member.Name, [from]));
                    System(member.Name + " suggests " + suggested.Trim() + ".");
                }
                else if (existing2.Votes.Add(from)) Changed();
                return LobbyResult.Success;
            case "vote":
                var choice = suggestions.FirstOrDefault(s => s.Scenario.Equals(Text("scenario"), StringComparison.OrdinalIgnoreCase));
                if (choice.Scenario is null) return LobbyResult.Fail("invalid", "That suggestion is gone.");
                foreach (var s in suggestions) s.Votes.Remove(from);
                choice.Votes.Add(from); Changed();
                return LobbyResult.Success;
            case "pick":
                if (!IsHost(from)) return HostOnly();
                var picked = suggestions.FirstOrDefault(s => s.Scenario.Equals(Text("scenario"), StringComparison.OrdinalIgnoreCase));
                if (picked.Scenario is null) return LobbyResult.Fail("invalid", "That suggestion is gone.");
                var applied = Apply(from, "settings", JsonSerializer.SerializeToElement(new { settings = new { scenario = picked.Scenario } }), resolve);
                if (!applied.Ok) return LobbyResult.Fail(applied.Code ?? "scenario-missing", applied.Code == "scenario-missing" ? "You don’t have “" + picked.Scenario + "”. Get it first, or pick another." : applied.Message ?? "Couldn’t use that scenario.");
                suggestions.Clear(); Changed();
                return LobbyResult.Success;
            case "loaded":
                if (match is { Phase: MatchPhases.Loading } lm && lm.Players.Contains(from) && lm.Loaded.Add(from)) Changed();
                return LobbyResult.Success;
            case "buy" or "use":
                return CsAction(member, action, args);
            case "avatar":
                // How this member looks in other players' games (any member, any time).
                if (AvatarProfiles.Find(Text("avatar")) is not { } look) return LobbyResult.Fail("invalid", "Unknown look.");
                if (member.Avatar != look.Id) { member.Avatar = look.Id; Changed(); }
                return LobbyResult.Success;
            case "ready-check":
                // The host wants to start: ping everyone who isn't ready (shown outside the AimMod panel too).
                if (!IsHost(from)) return HostOnly();
                if (match is { Phase: not MatchPhases.Final }) return LobbyResult.Fail("in-match", "A match is already running.");
                if (members.All(m => m.Id == HostId || m.Role != MemberRoles.Player || m.Ready)) return LobbyResult.Fail("ready", "Everyone is already ready.");
                readyCheck = clock();
                System(member.Name + " wants to start. Ready up!");
                return LobbyResult.Success;
            case "start":
                if (!IsHost(from)) return HostOnly();
                var blockers = LobbyRules.StartBlockers(Snapshot());
                if (blockers.Count > 0) return LobbyResult.Fail("blocked", blockers[0].Text);
                BeginMatch();
                return LobbyResult.Success;
            case "next":
                if (!IsHost(from)) return HostOnly();
                if (match is not { Phase: MatchPhases.Round }) return LobbyResult.Fail("invalid", "There is no round to skip.");
                match.NextAt = clock(); Tick();
                return LobbyResult.Success;
            case "end":
                if (!IsHost(from)) return HostOnly();
                if (match is null) return LobbyResult.Fail("invalid", "No match is running.");
                if (match.Phase == MatchPhases.Final || match.Rounds.Count == 0) { EndMatch(); return LobbyResult.Success; }
                FinishMatch(); return LobbyResult.Success;
            case "rematch":
                // Play again: the first vote opens a short window; everyone who confirms plays,
                // and players who don't answer in time sit the rematch out (they stay in the lobby).
                if (match is not { Phase: MatchPhases.Final }) return LobbyResult.Fail("invalid", "Rematch is available after the final results.");
                if (!match.Players.Contains(from)) return LobbyResult.Fail("spectator", "Only players from the last match can ask for a rematch.");
                if (match.Rematch.Add(from)) { match.RematchDeadline ??= clock() + RematchMs; Changed(); }
                TryRematch(timedOut: false);
                return LobbyResult.Success;            default:
                return LobbyResult.Fail("invalid", "Unknown action.");
        }
    }

    void TryRematch(bool timedOut)
    {
        if (match is not { Phase: MatchPhases.Final } m) return;
        var present = m.Players.Where(id => Find(id) is { Connection: Connections.Connected }).ToArray();
        var voters = present.Where(m.Rematch.Contains).ToArray();
        if (!(present.Length >= LobbySettings.MinPlayers && voters.Length == present.Length) && !timedOut) return;
        if (voters.Length < LobbySettings.MinPlayers)
        {
            if (timedOut) { m.RematchDeadline = null; m.Rematch.Clear(); System("Not enough players wanted a rematch."); }
            return;
        }
        foreach (var id in present.Except(voters))
            if (Find(id) is { } sitting) { sitting.Role = MemberRoles.Spectator; sitting.Ready = false; System(sitting.Name + " sat out the rematch."); }
        foreach (var mem in members) mem.Ready = mem.Role == MemberRoles.Player && voters.Contains(mem.Id);
        var blockers = LobbyRules.StartBlockers(Snapshot() with { Match = null });
        if (blockers.Count == 0) { System("Rematch!"); BeginMatch(); }
        else { EndMatch(); System("Rematch needs everyone back in the lobby: " + blockers[0].Text); }
    }

    void BeginMatch()
    {
        autoStartAt = null;
        var players = members.Where(m => m.Role == MemberRoles.Player && m.Connection == Connections.Connected && !m.Away).ToArray();
        suggestions.Clear();
        match = new Match { Id = "m-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(), Settings = Settings, Players = players.Select(p => p.Id).ToList() };
        foreach (var p in players) match.Names[p.Id] = p.Name;
        if (RequireLoading) { match.Phase = MatchPhases.Loading; match.NextAt = clock() + LoadingMs; match.Live = match.Players.ToDictionary(id => id, _ => new Line()); Changed(); }
        else StartRound();
    }
    void StartRound()
    {
        var m = match!;
        m.Phase = MatchPhases.Countdown; m.StartsAt = clock() + m.Settings.Countdown * 1000L; m.EndsAt = null; m.NextAt = null;
        m.Live = m.Players.ToDictionary(id => id, id => new Line { Status = Find(id) is null ? LineStates.Left : LineStates.Waiting });
        m.Tracking = null; m.TrackLast = null; m.Combat = null; m.CombatEvents = -1; m.Cs = null; m.CsKey = null;
        if (m.Settings.Mode == LobbyModes.Cs)
        {
            m.Cs = new CsMatch(m.Players.Where(id => Find(id) is not null).ToList(), m.StartsAt.Value, m.Settings.HalfRounds, m.Settings.Overtime, csObjectives);
            m.Combat = m.Cs.Combat;
        }
        if (LobbyModes.Combat(m.Settings.Mode))
            m.Combat = new CombatMatch(m.Settings.Mode, m.Players.Where(id => Find(id) is not null), m.Settings.EffectiveFragLimit, m.Settings.Lifesteal,
                m.StartsAt.Value, m.StartsAt.Value + (long)(m.Settings.EffectiveTimeLimit * 1000)) { Spawns = combatSpawns };
        // Both players track each other at once, every round.
        if (m.Settings.Mode == LobbyModes.Tracking && m.Players.Count >= 2)
            m.Tracking = new TrackingRound(m.Players[0], m.Players[1], m.StartsAt.Value, m.StartsAt.Value + (long)(m.Settings.EffectiveTimeLimit * 1000), m.Settings.RequireFire);
        // Late joiners in modes that allow it take part from the next round.
        if (m.Settings.LateJoin) foreach (var p in members.Where(x => x.Role == MemberRoles.Player && !m.Players.Contains(x.Id))) { m.Players.Add(p.Id); m.Names[p.Id] = p.Name; m.Live[p.Id] = new Line(); }
        Changed();
    }
    void EndMatch()
    {
        match = null;
        foreach (var m in members) m.Ready = false;
        Changed();
    }

    // Live scores from each player's own client. Unreliable frames may arrive
    // out of order, so older frames are ignored rather than rejected.
    public LobbyResult Score(string from, ScoreFrame frame)
    {
        if (Tracking) return LobbyResult.Fail("tracking", "The host scores tracking duels from the track stream.");
        if (Combat) return LobbyResult.Fail("combat", "The host scores combat modes from validated hits.");
        if (match is not { Phase: MatchPhases.Live or MatchPhases.Countdown } m || frame.MatchId != m.Id || frame.Round != m.Round) return LobbyResult.Fail("stale", "Not the current round.");
        if (!m.Live.TryGetValue(from, out var line) || line.Status is LineStates.Finished or LineStates.Left or LineStates.Dnf) return LobbyResult.Fail("not-playing", "Not playing this round.");
        if (!Sane(frame.Score, frame.Seconds, frame.Shots, frame.Hits, frame.Kills, m.Settings.EffectiveTimeLimit)) return LobbyResult.Fail("invalid", "Impossible score frame.");
        if (line.Seconds is { } seen && frame.Seconds < seen) return LobbyResult.Success;
        line.Score = frame.Score; line.Seconds = frame.Seconds; line.Remaining = frame.Remaining is { } r && double.IsFinite(r) ? Math.Max(0, r) : null;
        line.Shots = frame.Shots; line.Hits = frame.Hits; line.Kills = frame.Kills; line.Status = LineStates.Playing;
        Changed();
        return LobbyResult.Success;
    }
    static bool Sane(double score, double seconds, int shots, int hits, int kills, double limit) =>
        double.IsFinite(score) && Math.Abs(score) < 1e9 && double.IsFinite(seconds) && seconds >= 0 && seconds <= limit + 30
        && shots >= 0 && hits >= 0 && hits <= shots && kills >= 0 && kills <= 100_000;

    public LobbyResult Finish(string from, RunFinish run)
    {
        if (Tracking) return LobbyResult.Fail("tracking", "The host scores tracking duels from the track stream.");
        if (Combat) return LobbyResult.Fail("combat", "The host scores combat modes from validated hits.");
        if (match is not { Phase: MatchPhases.Live } m || run.MatchId != m.Id || run.Round != m.Round) return LobbyResult.Fail("stale", "Not the current round.");
        if (!m.Live.TryGetValue(from, out var line) || line.Status is LineStates.Finished or LineStates.Left) return LobbyResult.Fail("not-playing", "Not playing this round.");
        if (!Sane(run.Score, run.Seconds, run.Shots, run.Hits, run.Kills, m.Settings.EffectiveTimeLimit)) return LobbyResult.Fail("invalid", "Impossible result.");
        var limit = m.Settings.EffectiveTimeLimit;
        // Mutual checks from the design: the final score matches the stream and the run length matches the limit.
        var disputed = run.Seconds > limit + 5 || run.Seconds < limit - 5
            || (line.Score is { } last && line.Remaining is < 2 && Math.Abs(run.Score - last) > Math.Max(5, Math.Abs(last) * 0.1));
        line.Score = run.Score; line.Seconds = run.Seconds; line.Remaining = 0; line.Shots = run.Shots; line.Hits = run.Hits; line.Kills = run.Kills;
        line.Status = LineStates.Finished; line.Disputed = disputed;
        Changed();
        if (m.Live.Values.All(l => l.Status is not (LineStates.Waiting or LineStates.Playing))) CloseRound();
        return LobbyResult.Success;
    }

    // Tracking duel samples from a player's own client (or this machine). Only the
    // current round's two players are accepted, and only while it counts down or runs.
    public LobbyResult Track(string from, TrackBatch batch)
    {
        if (match is { Phase: MatchPhases.Live or MatchPhases.Countdown, Combat: { } combat } cm && batch.MatchId == cm.Id && batch.Round == cm.Round)
        {
            if (!cm.Players.Contains(from)) return LobbyResult.Fail("not-playing", "Not playing this match.");
            combat.Track(from, batch);
            return LobbyResult.Success;
        }
        if (match is not { Phase: MatchPhases.Live or MatchPhases.Countdown, Tracking: { } tracking } m || batch.MatchId != m.Id || batch.Round != m.Round) return LobbyResult.Fail("stale", "Not the current round.");
        if (from != tracking.First && from != tracking.Second) return LobbyResult.Fail("not-playing", "Not playing this round.");
        tracking.Add(from, batch);
        return LobbyResult.Success;
    }

    // A hit the shooter's game registered. The host validates it and applies the damage.
    public LobbyResult Claim(string from, HitClaim claim)
    {
        if (match is not { Phase: MatchPhases.Live, Combat: { } combat } m || claim.MatchId != m.Id || claim.Round != m.Round) return LobbyResult.Fail("stale", "Not the current match.");
        if (m.Cs is { Phase: not ("live" or "planted") }) return LobbyResult.Fail("round-phase", "No shooting between rounds.");
        var refused = combat.Claim(from, claim, clock(), Find(from)?.Ping);
        UpdateCombat(m, combat);
        if (m.Cs is null && combat.Leader is not null) CloseRound();
        return refused is null ? LobbyResult.Success : LobbyResult.Fail(refused, "Hit not accepted (" + refused + ").");
    }

    void UpdateCombat(Match m, CombatMatch combat)
    {
        var view = combat.View();
        var latest = view.Events.Count > 0 ? view.Events[^1].Id : 0;
        foreach (var p in view.Players)
            if (m.Live.TryGetValue(p.Member, out var line) && line.Status is LineStates.Waiting or LineStates.Playing)
            {
                var changed = line.Score != p.Frags || line.Shots != p.Claims || line.Kills != p.Frags;
                line.Score = p.Frags; line.Kills = p.Frags; line.Shots = p.Claims; line.Hits = p.Claims - p.Rejected; line.Status = LineStates.Playing;
                if (changed) Changed();
            }
        if (latest != m.CombatEvents) { m.CombatEvents = latest; Changed(); }
    }

    // One match-long round: placement by frags, then fewer deaths.
    void CloseCombatRound(Match m, CombatMatch combat)
    {
        var view = combat.View();
        // Team deathmatch: the team with more frags places first (a tie places everyone first).
        int TeamPlace(int team) => view.TeamFrags is { } tf && team is 1 or 2 ? (tf[team - 1] >= tf[2 - team] ? 1 : 2) : 0;
        var rows = view.Players.OrderBy(p => combat.Teams ? TeamPlace(p.Team) : 0).ThenByDescending(p => p.Frags).ThenBy(p => p.Deaths).ToArray();
        var results = new List<Placement>();
        foreach (var p in rows)
        {
            var present = Find(p.Member) is not null;
            if (m.Live.TryGetValue(p.Member, out var line) && line.Status is LineStates.Waiting or LineStates.Playing) line.Status = present ? LineStates.Finished : LineStates.Left;
            var place = !present ? 0 : combat.Teams ? TeamPlace(p.Team) : 1 + rows.Count(o => o.Frags > p.Frags || (o.Frags == p.Frags && o.Deaths < p.Deaths));
            double? accuracy = p.Claims > 0 ? Math.Round((p.Claims - p.Rejected) * 100.0 / p.Claims, 1) : null;
            results.Add(new Placement(p.Member, m.Names.GetValueOrDefault(p.Member, "Player"), place, p.Frags, accuracy, p.Frags, present ? LineStates.Finished : LineStates.Left,
                p.Claims >= 10 && p.Rejected > p.Claims * 0.2));
        }
        var top = results.Where(r => r.Place == 1).ToArray();
        // A team win has no single winner; the combat view names the winning team.
        m.Rounds.Add(new RoundResult(m.Round, results, !combat.Teams && top.Length == 1 ? top[0].MemberId : null));
        m.Over = true;
        FinishMatch();
    }

    // CS: the team with more rounds places first (a draw places everyone first); points are kills.
    void CloseCsMatch(Match m, CsMatch cs)
    {
        var view = cs.View();
        var winner = cs.WinnerTeam ?? (view.Score[0] == view.Score[1] ? (int?)null : view.Score[0] > view.Score[1] ? 1 : 2);
        var results = view.Players.OrderBy(p => winner is null || p.Team == winner ? 1 : 2).ThenByDescending(p => p.Kills).Select(p =>
        {
            var present = Find(p.Member) is not null;
            if (m.Live.TryGetValue(p.Member, out var line) && line.Status is LineStates.Waiting or LineStates.Playing) line.Status = present ? LineStates.Finished : LineStates.Left;
            return new Placement(p.Member, m.Names.GetValueOrDefault(p.Member, "Player"), !present ? 0 : winner is null || p.Team == winner ? 1 : 2, p.Kills, null, p.Kills,
                present ? LineStates.Finished : LineStates.Left, false);
        }).ToList();
        m.Rounds.Add(new RoundResult(m.Round, results, null));
        m.Over = true;
        FinishMatch();
    }

    // CS actions from a player's own client: buy an item, hold or release the use key.
    LobbyResult CsAction(Member member, string action, JsonElement args)
    {
        if (match is not { Phase: MatchPhases.Live, Cs: { } cs } m || !m.Players.Contains(member.Id)) return LobbyResult.Fail("invalid", "No CS round is running.");
        string? refused;
        if (action == "buy")
            refused = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.String ? cs.Buy(member.Id, item.GetString() ?? "", clock()) : "unknown-item";
        else
            refused = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("held", out var held) && held.ValueKind is JsonValueKind.True or JsonValueKind.False ? cs.Use(member.Id, held.GetBoolean(), clock()) : "invalid";
        Changed();
        return refused is null ? LobbyResult.Success : LobbyResult.Fail(refused, refused switch
        {
            "money" => "Not enough money.", "buy-time" => "Buy time is over.", "buy-zone" => "Go back to your buy zone.", "side" => "Your side can't buy that.",
            "owned" => "You already have that.", "not-in-site" => "Plant at a bomb site.", "moving" => "Stand still to plant.", "not-at-bomb" => "Get to the bomb to defuse.",
            _ => "Not now (" + refused + ").",
        });
    }

    void UpdateTracking(Match m, TrackingRound tracking, long now)
    {
        m.TrackComputedAt = now;
        var result = tracking.Compute(now, id => Find(id)?.Ping);
        if (result == m.TrackLast) return;
        m.TrackLast = result;
        foreach (var (id, r) in new[] { (tracking.First, result.First), (tracking.Second, result.Second) })
            if (m.Live.TryGetValue(id, out var line) && line.Status is LineStates.Waiting or LineStates.Playing)
            {
                line.Score = r.Percent; line.Seconds = Math.Max(0, Math.Min(now, tracking.End) - tracking.Start) / 1000.0;
                line.Remaining = Math.Max(0, tracking.End - now) / 1000.0; line.Shots = r.Samples; line.Hits = r.OnSamples; line.Kills = 0;
                if (r.Samples > 0) line.Status = LineStates.Playing;
                line.Disputed = r.Disputed;
            }
        Changed();
    }

    // Both players' time on target (percent of the round); the higher share takes the
    // round. The match goes to the most rounds won, then the higher total.
    void CloseTrackingRound(Match m, TrackingRound tracking)
    {
        var now = clock();
        UpdateTracking(m, tracking, Math.Max(now, tracking.End));
        var (first, second) = m.TrackLast!.Value;
        var complete = now >= tracking.End && Find(tracking.First) is not null && Find(tracking.Second) is not null;
        Placement Place(string id, TrackResult r, TrackResult other)
        {
            var line = m.Live.GetValueOrDefault(id);
            var present = Find(id) is not null && line is not null && line.Status != LineStates.Left;
            if (line is not null && line.Status is LineStates.Waiting or LineStates.Playing) line.Status = complete ? LineStates.Finished : present ? LineStates.Dnf : LineStates.Left;
            double? accuracy = r.Samples > 0 ? Math.Round(r.OnSamples * 100.0 / r.Samples, 1) : null;
            var place = !complete || !present ? 0 : r.Percent >= other.Percent ? 1 : 2;
            return new(id, m.Names.GetValueOrDefault(id, "Player"), place, complete && present ? r.Percent : null, accuracy, complete && r.Percent > other.Percent ? 1 : 0,
                line?.Status ?? LineStates.Left, r.Disputed);
        }
        var results = new List<Placement> { Place(tracking.First, first, second), Place(tracking.Second, second, first) }.OrderBy(p => p.Place == 0 ? 3 : p.Place).ToList();
        var winner = complete && first.Percent != second.Percent ? (first.Percent > second.Percent ? tracking.First : tracking.Second) : null;
        m.Rounds.Add(new RoundResult(m.Round, results, winner));
        m.Over = m.Round >= m.Settings.Rounds || m.Players.Count(id => Find(id) is not null) < 2;
        m.Phase = MatchPhases.Round; m.NextAt = clock() + ResultsMs;
        Changed();
    }

    public void Tick()
    {
        var now = clock();
        foreach (var m in members.Where(m => m.LostAt is not null).ToArray())
            if (now - m.LostAt!.Value >= (IsHost(m.Id) ? HostGraceMs : MemberGraceMs)) Leave(m.Id, "timeout");
        // A ready check closes once everyone is ready, after 30 s, or when the match starts.
        if (readyCheck is { } asked && (now - asked > ReadyCheckMs || match is { Phase: not MatchPhases.Final } || members.All(m => m.Id == HostId || m.Role != MemberRoles.Player || m.Ready))) { readyCheck = null; Changed(); }
        // Auto start: once nothing blocks the start for a moment, the host's lobby starts itself.
        if (Settings.AutoStart && match is null && !Closed)
        {
            if (LobbyRules.StartBlockers(Snapshot()).Count == 0) { if (autoStartAt is null) { autoStartAt = now + AutoStartMs; Changed(); } else if (now >= autoStartAt) BeginMatch(); }
            else if (autoStartAt is not null) { autoStartAt = null; Changed(); }
        }
        else if (autoStartAt is not null) { autoStartAt = null; Changed(); }
        if (match is { Phase: MatchPhases.Final, RematchDeadline: { } deadline } && now >= deadline) TryRematch(timedOut: true);
        if (match is null) return;
        // Warm-up: the countdown begins once everyone has the scenario loaded, or after 20 s.
        if (match.Phase == MatchPhases.Loading && (now >= match.NextAt || match.Players.All(id => match.Loaded.Contains(id) || Find(id) is null))) StartRound();
        if (match.Phase == MatchPhases.Countdown && now >= match.StartsAt)
        {
            match.Phase = MatchPhases.Live;
            match.EndsAt = match.StartsAt + (long)(match.Settings.EffectiveTimeLimit * 1000) + (Tracking ? TrackGraceMs : RoundGraceMs);
            Changed();
        }
        if (match.Phase == MatchPhases.Live && match.Cs is { } cs)
        {
            cs.Tick(now);
            UpdateCombat(match, cs.Combat);
            var csKey = cs.Round + "|" + cs.Phase + "|" + cs.View().Events.LastOrDefault()?.Id;
            if (csKey != match.CsKey) { match.CsKey = csKey; Changed(); }
            if (cs.Over || match.Players.Count(id => Find(id) is not null) < 2) CloseRound();
        }
        else if (match.Phase == MatchPhases.Live && match.Combat is { } combat)
        {
            combat.Tick(now);
            UpdateCombat(match, combat);
            if (now >= match.EndsAt || combat.Leader is not null || match.Players.Count(id => Find(id) is not null) < 2) CloseRound();
        }
        else if (match.Phase == MatchPhases.Live && match.Tracking is { } tracking)
        {
            // The host scores live (for the HUD) a few times a second, and closes the round at its end.
            if (now - match.TrackComputedAt >= 250) UpdateTracking(match, tracking, now);
            if (now >= match.EndsAt || match.Players.Count(id => Find(id) is not null) < 2) CloseRound();
        }
        else if (match.Phase == MatchPhases.Live)
        {
            var open = match.Live.Values.Where(l => l.Status is LineStates.Waiting or LineStates.Playing).ToArray();
            var active = match.Players.Count(id => Find(id) is not null);
            if (open.Length == 0 || now >= match.EndsAt || active == 0)
            {
                foreach (var l in open) l.Status = LineStates.Dnf;
                CloseRound();
            }
        }
        if (match is { Phase: MatchPhases.Round } && now >= match.NextAt)
        {
            if (match.Over) FinishMatch();
            else { match.Round++; StartRound(); }
        }
    }

    void CloseRound()
    {
        var m = match!;
        var s = m.Settings;
        if (m.Tracking is { } tracking) { CloseTrackingRound(m, tracking); return; }
        if (m.Cs is { } csMatch) { CloseCsMatch(m, csMatch); return; }
        if (m.Combat is { } combatMatch) { CloseCombatRound(m, combatMatch); return; }
        var ranked = m.Live.Select(kv => (Id: kv.Key, Line: kv.Value))
            .OrderBy(x => x.Line.Status == LineStates.Finished ? 0 : x.Line.Status == LineStates.Dnf ? 1 : 2)
            .ThenByDescending(x => x.Line.Score ?? double.MinValue).ToArray();
        var finished = ranked.Where(x => x.Line.Status == LineStates.Finished).ToArray();
        var results = new List<Placement>();
        string? winner = null;
        for (var i = 0; i < ranked.Length; i++)
        {
            var (id, line) = ranked[i];
            // Ties share a place.
            var place = line.Status == LineStates.Finished ? 1 + finished.Count(f => f.Line.Score > line.Score) : 0;
            var points = 0;
            if (s.Mode == LobbyModes.Rounds && place > 0) points = finished.Length - place;
            if (s.Mode == LobbyModes.Duel && place == 1 && finished.Count(f => f.Line.Score == line.Score) == 1) points = 1;
            double? accuracy = line.Shots > 0 ? Math.Round(line.Hits * 100.0 / line.Shots, 1) : null;
            results.Add(new Placement(id, m.Names.GetValueOrDefault(id, "Player"), LobbyModes.Scored(s.Mode) ? place : 0, line.Score, accuracy, points, line.Status, line.Disputed));
        }
        if (finished.Length > 0 && finished.Count(f => f.Line.Score == finished[0].Line.Score) == 1) winner = finished[0].Id;
        m.Rounds.Add(new RoundResult(m.Round, results, LobbyModes.Scored(s.Mode) ? winner : null));
        var standings = Standings(m);
        m.Over = s.Mode switch
        {
            LobbyModes.Race or LobbyModes.Rounds => m.Round >= s.Rounds,
            LobbyModes.Duel => standings.Any(x => x.Wins >= s.FirstTo) || m.Round >= s.FirstTo * 2 + 2 || m.Players.Count(id => Find(id) is not null) < 2,
            LobbyModes.Tracking => m.Round >= s.Rounds || m.Players.Count(id => Find(id) is not null) < 2,
            _ => false,
        };
        if (m.Players.Count(id => Find(id) is not null) == 0) m.Over = true;
        // A single deciding round goes straight to the final screen.
        if (m.Over && m.Rounds.Count == 1) { FinishMatch(); return; }
        m.Phase = MatchPhases.Round; m.NextAt = clock() + ResultsMs;
        Changed();
    }

    void FinishMatch()
    {
        var m = match!;
        var standings = Standings(m);
        m.WinnerId = LobbyModes.Scored(m.Settings.Mode) && m.Combat is not { Teams: true } && standings.Count(x => x.Place == 1) == 1 ? standings.First(x => x.Place == 1).MemberId : null;
        m.Phase = MatchPhases.Final; m.NextAt = null; m.StartsAt = null; m.EndsAt = null; m.Rematch.Clear();
        foreach (var member in members) member.Ready = false;
        Changed();
        MatchFinished?.Invoke(MatchView()!, Members);
    }

    static IReadOnlyList<Standing> Standings(Match m)
    {
        var mode = m.Settings.Mode;
        var rows = m.Players.Select(id =>
        {
            var mine = m.Rounds.SelectMany(r => r.Results.Where(p => p.MemberId == id).Select(p => (Round: r, P: p))).ToArray();
            var scores = mine.Where(x => x.P.Status == LineStates.Finished && x.P.Score is not null).Select(x => x.P.Score!.Value).ToArray();
            return new Standing(id, m.Names.GetValueOrDefault(id, "Player"), 0, mine.Count(x => x.Round.WinnerId == id), mine.Sum(x => x.P.Points),
                scores.Length > 0 ? scores.Max() : null, scores.Sum(), mine.Length);
        }).ToList();
        Func<Standing, (double, double)> key = mode switch
        {
            LobbyModes.Duel => s => (s.Wins, s.Total),
            LobbyModes.Tracking => s => (s.Wins, s.Total),
            var cm when LobbyModes.Shooting(cm) => s => (s.Points, s.Total),
            LobbyModes.Rounds => s => (s.Points, s.Total),
            _ => s => (s.Best ?? double.MinValue, s.Total),
        };
        var ordered = rows.OrderByDescending(key).ToList();
        if (!LobbyModes.Scored(mode)) return ordered;
        return ordered.Select(s => s with { Place = 1 + ordered.Count(o => key(o).CompareTo(key(s)) > 0) }).ToArray();
    }

    MatchSnapshot? MatchView()
    {
        if (match is not { } m) return null;
        var s = m.Settings;
        return new MatchSnapshot(m.Id, m.Phase, s.Mode, s.Scenario?.Name ?? "", s.EffectiveTimeLimit, m.Round, s.TotalRounds,
            s.Mode == LobbyModes.Duel ? s.FirstTo : null, m.StartsAt, m.EndsAt, m.NextAt, m.Players.ToArray(),
            m.Live.Select(kv => kv.Value.View(kv.Key)).ToArray(), m.Rounds.ToArray(), Standings(m), m.WinnerId, m.Rematch.ToArray(), m.RematchDeadline, m.Loaded.ToArray(),
            null, m.Tracking is { } t && m.TrackLast is { } r ? [TrackView.Of(t.First, r.First), TrackView.Of(t.Second, r.Second)] : null,
            m.Combat?.View(), m.Cs?.View());
    }

    // A client that becomes host rebuilds the authority from the last snapshot it mirrored.
    public static LobbyCore Restore(LobbySnapshot snapshot, string newHostId, Func<long> clock)
    {
        var self = snapshot.Members.FirstOrDefault(m => m.Id == newHostId) ?? throw new InvalidOperationException("New host is not a member.");
        var core = new LobbyCore(self.Id, self.Name, snapshot.Settings, clock, snapshot.Id, snapshot.Code) { Revision = snapshot.Revision + 1 };
        core.members.Clear(); core.chat.Clear();
        foreach (var m in snapshot.Members.Where(m => m.Id != snapshot.HostId || m.Id == newHostId))
            core.members.Add(new Member { Id = m.Id, Name = m.Name, Role = m.Role, Ready = m.Ready, Ping = m.Id == newHostId ? null : m.Ping, Scenario = m.Scenario, Map = m.Map, Profiles = m.Profiles,
                // Everyone else must reconnect to the new host, so they start as reconnecting.
                Connection = m.Id == newHostId ? Connections.Connected : Connections.Reconnecting, Link = m.Id == newHostId ? "local" : m.Link,
                JoinedAt = m.JoinedAt, Simulated = m.Simulated, LostAt = m.Id == newHostId ? null : clock(), Avatar = AvatarProfiles.Find(m.Avatar)?.Id ?? AvatarProfiles.Default, Version = m.Version, Away = m.Away, Cosmetics = (m.Cosmetics ?? []).Take(CosmeticsCatalog.MaxEquipped).ToArray() });
        core.chat.AddRange(snapshot.Chat); core.chatId = snapshot.Chat.Count > 0 ? snapshot.Chat.Max(c => c.Id) : 0; core.readyCheck = snapshot.ReadyCheck;
        foreach (var s in snapshot.Suggestions ?? []) core.suggestions.Add((s.Scenario, s.By, s.Votes.ToHashSet()));
        if (snapshot.Match is { } ms)
        {
            var match = new Match { Id = ms.Id, Settings = snapshot.Settings, Phase = ms.Phase, Round = ms.Round, StartsAt = ms.StartsAt, EndsAt = ms.EndsAt, NextAt = ms.NextAt,
                Players = ms.Players.ToList(), Rounds = ms.Rounds.ToList(), WinnerId = ms.WinnerId, Loaded = ms.Loaded?.ToHashSet() ?? [], RematchDeadline = ms.RematchDeadline };
            foreach (var l in ms.Live) match.Live[l.MemberId] = new Line { Score = l.Score, Seconds = l.Seconds, Remaining = l.Remaining, Shots = l.Shots, Hits = l.Hits, Kills = l.Kills, Status = l.Status, Disputed = l.Disputed };
            foreach (var st in ms.Standings) match.Names[st.MemberId] = st.Name;
            foreach (var r in ms.Rematch) match.Rematch.Add(r);
            if (snapshot.HostId != newHostId && match.Live.TryGetValue(snapshot.HostId, out var oldHost) && oldHost.Status is LineStates.Waiting or LineStates.Playing)
                oldHost.Status = LineStates.Left;
            // A combat match carries on from the scores the old host last published.
            if (LobbyModes.Combat(snapshot.Settings.Mode) && ms.StartsAt is { } combatStart && ms.Phase is MatchPhases.Countdown or MatchPhases.Live)
            {
                match.Combat = new CombatMatch(snapshot.Settings.Mode, ms.Players, snapshot.Settings.EffectiveFragLimit, snapshot.Settings.Lifesteal, combatStart, combatStart + (long)(snapshot.Settings.EffectiveTimeLimit * 1000),
                    ms.Combat?.Players.Where(p => p.Team is 1 or 2).ToDictionary(p => p.Member, p => p.Team));
                if (ms.Combat is { } cv) match.Combat.Restore(cv);
            }
            // A tracking round in progress restarts its scoring on the new host (the old host's samples are gone); the short coverage marks it disputed.
            if (snapshot.Settings.Mode == LobbyModes.Tracking && ms.StartsAt is { } startsAt && ms.Phase is MatchPhases.Countdown or MatchPhases.Live && ms.Players.Count >= 2)
                match.Tracking = new TrackingRound(ms.Players[0], ms.Players[1], startsAt, startsAt + (long)(snapshot.Settings.EffectiveTimeLimit * 1000), snapshot.Settings.RequireFire);
            core.match = match;
        }
        if (snapshot.HostId != newHostId) core.System(snapshot.Members.FirstOrDefault(m => m.Id == snapshot.HostId)?.Name + " (host) left. " + self.Name + " is now the host.");
        return core;
    }
}

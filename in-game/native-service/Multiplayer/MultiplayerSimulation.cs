using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

// Developer simulation: synthetic members who join, check content, ready up,
// chat, post score frames and vote for rematches, so every lobby and match
// screen can be used and tested before the Steam transport exists. Members it
// adds are marked Simulated and the UI labels them. Off in release builds
// unless explicitly enabled (MultiplayerService.SimulationRequested).
sealed class MultiplayerSimulation(Func<long> clock, ContentLibrary library, Func<IReadOnlyList<Run>> runs, int seed = 0)
{
    static readonly string[] Names = ["Nova", "Kestrel", "Juniper", "Orbit", "Vesper", "Talon", "Mako"];
    static readonly string[] Hellos = ["hey!", "hi all", "ready when you are", "o/"];
    static readonly string[] Chatter = ["gg", "one more?", "that was close", "nice shot!", "lag spike, sorry", "brb"];
    sealed class Bot
    {
        public required string Id; public double Skill; public long ContentAt, ReadyAt, StartAt, FrameAt, RematchAt, ChatAt;
        public bool MissingMap, Greeted, Finished; public string? Round; public int? Ping;
    }
    readonly Random random = seed == 0 ? new Random() : new Random(seed);
    readonly Dictionary<string, Bot> bots = new();
    readonly List<IncomingInvite> invites = [];
    string? lastPlayKey, lastPhase, selfRound; long hostStartAt, selfStart; double selfSkill = 1;
    int nameIndex;

    static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value, Protocol.Json);
    long Jitter(int min, int max) => clock() + random.Next(min, max);
    string NextId() => "sim-" + Guid.NewGuid().ToString("N")[..8];
    string NextName() => Names[nameIndex++ % Names.Length];

    public LobbyCore HostedLobby(string code, string selfId, string selfName)
    {
        var scenario = library.Scenarios.Count > 0 ? library.Scenarios[random.Next(Math.Min(library.Scenarios.Count, 8))] : null;
        var settings = new LobbySettings(Mode: LobbyModes.Rounds, Rounds: 3, MaxPlayers: 6, Spectators: true, Privacy: LobbyPrivacy.Public,
            Scenario: scenario is null ? null : library.Scenario(scenario.Name));
        var hostName = NextName();
        var hostId = "sim-" + hostName.ToLowerInvariant();
        var core = new LobbyCore(hostId, hostName, settings, clock, code: code, hostSimulated: true);
        bots[hostId] = NewBot(hostId);
        Add(core);
        core.Join(selfId, selfName);
        core.SetLink(selfId, "local", null);
        return core;
    }

    Bot NewBot(string id) => new() { Id = id, Skill = 0.82 + random.NextDouble() * 0.26, ContentAt = Jitter(600, 1800), ReadyAt = long.MaxValue, ChatAt = Jitter(1500, 4000), Ping = random.Next(18, 90) };

    public string? Add(LobbyCore core, bool missingMap = false, string? name = null)
    {
        // Stable ids by name, so simulated players show up as rivals across matches.
        var who = name ?? NextName();
        var id = core.Members.Any(m => m.Id == "sim-" + who.ToLowerInvariant()) ? NextId() : "sim-" + who.ToLowerInvariant();
        if (!core.Join(id, who, simulated: true).Ok) return null;
        var bot = NewBot(id); bot.MissingMap = missingMap;
        bots[id] = bot;
        core.SetLink(id, "simulated", bot.Ping);
        return id;
    }

    // Each new lobby meets the same simulated players again (Nova, Kestrel, ...), so they become rivals.
    public void NewLobby() => nameIndex = 0;

    public IReadOnlyList<IncomingInvite> TakeInvites() { var list = invites.ToArray(); invites.Clear(); return list; }

    // A friend invited from the panel accepts a few seconds later.
    readonly List<(long At, string Name)> pendingFriends = [];
    public void InvitedFriend(LobbyCore core, string friendId)
    {
        var name = Friends(clock()).FirstOrDefault(f => f.Id == friendId)?.Name ?? NextName();
        pendingFriends.Add((Jitter(1500, 3000), name));
    }

    public LobbyResult Control(LobbyCore? core, string op, string? member)
    {
        if (op == "friend-online") { FriendOnline = !FriendOnline; return LobbyResult.Success; }
        if (op is "invite" or "request" or "launch")
        {
            var from = Names[random.Next(Names.Length)];
            var summary = new LobbySummary(op == "request" ? LobbyModes.Race : LobbyModes.Duel, library.Scenarios.FirstOrDefault()?.Name, 1, op == "request" ? 4 : 2);
            invites.Add(new IncomingInvite("inv-" + Guid.NewGuid().ToString("N")[..8], from, op == "invite" ? "incoming" : op, "sim-" + LobbyCore.NewCode(), summary, clock()));
            return LobbyResult.Success;
        }
        if (core is null) return LobbyResult.Fail("no-lobby", "Create or join a lobby first.");
        var fakes = core.Members.Where(m => m.Simulated).ToArray();
        string? Pick(Func<LobbyMember, bool> filter) => member is not null && fakes.Any(m => m.Id == member && filter(m)) ? member : fakes.Where(filter).Select(m => m.Id).LastOrDefault();
        switch (op)
        {
            case "add":
                if (core.Members.Count(m => m.Role == MemberRoles.Player) >= core.Settings.MaxPlayers && !core.Settings.Spectators) return LobbyResult.Fail("full", "The lobby is full.");
                return Add(core) is null ? LobbyResult.Fail("full", "The lobby is full.") : LobbyResult.Success;
            case "add-missing":
                return Add(core, missingMap: true) is null ? LobbyResult.Fail("full", "The lobby is full.") : LobbyResult.Success;
            case "remove":
                if (Pick(m => m.Id != core.HostId) is not { } leaver) return LobbyResult.Fail("none", "No simulated member to remove.");
                bots.Remove(leaver); return core.Leave(leaver);
            case "drop":
                if (Pick(m => m.Connection == Connections.Connected) is not { } dropped) return LobbyResult.Fail("none", "No simulated member to drop.");
                core.Disconnected(dropped); return LobbyResult.Success;
            case "reconnect":
                if (Pick(m => m.Connection != Connections.Connected) is not { } back) return LobbyResult.Fail("none", "Nobody is reconnecting.");
                return core.Join(back, "");
            case "host-leave":
                if (!fakes.Any(m => m.Id == core.HostId)) return LobbyResult.Fail("not-sim-host", "The host isn’t simulated.");
                bots.Remove(core.HostId); return core.Leave(core.HostId);
            // Developer menu: make a simulated member do one thing now.
            case "chat":
                if (Pick(m => m.Connection == Connections.Connected) is not { } talker) return LobbyResult.Fail("none", "No simulated member to talk.");
                return core.Apply(talker, "chat", Args(new { text = Chatter[random.Next(Chatter.Length)] }), library);
            case "away":
                if (Pick(m => m.Role == MemberRoles.Player && m.Id != core.HostId) is not { } idler) return LobbyResult.Fail("none", "No simulated player to send away.");
                var isAway = core.Members.First(m => m.Id == idler).Away;
                if (isAway && bots.TryGetValue(idler, out var back2)) back2.ReadyAt = Jitter(800, 1600);
                return core.Apply(idler, "away", Args(new { away = !isAway }), library);
            case "unready":
                if (Pick(m => m.Ready && m.Id != core.HostId) is not { } waverer) return LobbyResult.Fail("none", "No simulated player is ready.");
                if (bots.TryGetValue(waverer, out var w)) w.ReadyAt = Jitter(4000, 7000);
                return core.Apply(waverer, "ready", Args(new { ready = false }), library);
            case "suggest":
                if (Pick(m => m.Connection == Connections.Connected) is not { } suggester) return LobbyResult.Fail("none", "No simulated member to suggest.");
                if (library.Scenarios.Count == 0) return LobbyResult.Fail("none", "Your library has no scenarios to suggest.");
                var pick = library.Scenarios[random.Next(Math.Min(library.Scenarios.Count, 20))].Name;
                var made = core.Apply(suggester, "suggest", Args(new { scenario = pick }), library);
                // Everyone else simulated votes for it.
                if (made.Ok) foreach (var voter in fakes.Where(m => m.Id != suggester)) core.Apply(voter.Id, "vote", Args(new { scenario = pick }), library);
                return made;
            default:
                return LobbyResult.Fail("invalid", "Unknown simulation step.");
        }
    }

    // Dev control: Vesper starts AimMod (or quits it again), for the friend-online toast.
    public bool FriendOnline;
    public IReadOnlyList<FriendEntry> Friends(long now)
    {
        var scenario = library.Scenarios.Count > 1 ? library.Scenarios[1].Name : "a scenario";
        return
        [
            new("sim-f1", "Juniper", "aimmod-lobby", "In a lobby · Duel", "JUNPER", true),
            new("sim-f2", "Kestrel", "aimmod", "Playing " + scenario, null, false),
            new("sim-f3", "Talon", "kovaaks", "In KovaaK’s, no AimMod", null, false),
            FriendOnline ? new("sim-f4", "Vesper", "aimmod", "Playing " + scenario, null, false, Spectatable: true) : new("sim-f4", "Vesper", "online", "Online", null, false),
        ];
    }

    public void ResetSelf() { selfRound = null; }

    // A stand-in for the local player's run when the game isn't running one.
    public LocalRun SelfRun(MatchSnapshot match, long now)
    {
        var key = match.Id + "#" + match.Round;
        if (selfRound != key) { selfRound = key; selfStart = (match.StartsAt ?? now) + random.Next(300, 900); selfSkill = 0.9 + random.NextDouble() * 0.2; }
        return Run(match, now, selfStart, selfSkill, 7);
    }

    LocalRun Run(MatchSnapshot match, long now, long start, double skill, int phase)
    {
        if (now < start) return new LocalRun(false, match.Scenario, null, null, null, 0, 0, 0, null);
        var limit = match.TimeLimit;
        var t = Math.Min(limit, (now - start) / 1000.0);
        var rate = BaseScore(match.Scenario, limit) / limit * skill;
        var score = Math.Round(rate * t * (1 + 0.035 * Math.Sin(t * 0.6 + phase)), 1);
        var shots = (int)(t * 2.4); var hits = (int)(shots * Math.Clamp(0.62 + skill * 0.18, 0, 1));
        return new LocalRun(true, match.Scenario, Math.Max(0, score), Math.Round(t, 2), Math.Round(limit - t, 2), shots, hits, hits, match.Id);
    }

    double BaseScore(string scenario, double limit)
    {
        var best = runs().Where(r => r.Scenario.Equals(scenario, StringComparison.OrdinalIgnoreCase) && double.IsFinite(r.Score) && r.Score > 0).Select(r => r.Score).DefaultIfEmpty(0).Max();
        return best > 0 ? best : 1000 * limit / 60;
    }

    public void Step(LobbyCore core, string selfId)
    {
        var now = clock();
        foreach (var friend in pendingFriends.Where(f => now >= f.At).ToArray()) { pendingFriends.Remove(friend); Add(core, name: friend.Name); }
        var snapshot = core.Snapshot();
        foreach (var id in bots.Keys.Where(id => snapshot.Members.All(m => m.Id != id)).ToArray()) bots.Remove(id);
        if (snapshot.Settings.PlayKey != lastPlayKey)
        {
            lastPlayKey = snapshot.Settings.PlayKey;
            foreach (var b in bots.Values) { b.ContentAt = Jitter(500, 1500); b.ReadyAt = long.MaxValue; }
        }
        var phase = snapshot.Match?.Phase ?? "lobby";
        if (phase != lastPhase)
        {
            if (phase == MatchPhases.Loading) foreach (var b in bots.Values) core.Apply(b.Id, "loaded", default, library);
            if (phase == MatchPhases.Countdown && random.NextDouble() < 0.6 && bots.Values.FirstOrDefault() is { } talker) core.Apply(talker.Id, "chat", Args(new { text = "glhf" }), library);
            if (phase == MatchPhases.Final) foreach (var b in bots.Values) b.RematchAt = Jitter(2500, 6000);
            if (phase == "lobby") foreach (var b in bots.Values) b.ReadyAt = Jitter(1200, 3500);
            lastPhase = phase;
        }
        foreach (var b in bots.Values)
        {
            var me = snapshot.Members.FirstOrDefault(m => m.Id == b.Id);
            if (me is null || me.Connection != Connections.Connected) continue;
            if (now % 2000 < 100) core.SetLink(b.Id, "simulated", Math.Clamp((b.Ping ?? 40) + random.Next(-4, 5), 8, 250));
            if (!b.Greeted && now >= b.ChatAt) { b.Greeted = true; if (random.NextDouble() < 0.5) core.Apply(b.Id, "chat", Args(new { text = Hellos[random.Next(Hellos.Length)] }), library); }
            if (snapshot.Settings.Scenario is not null && now >= b.ContentAt && b.ContentAt != long.MaxValue)
            {
                // A member missing the map "installs" it after a while.
                var map = b.MissingMap ? ContentStates.Missing : ContentStates.Ok;
                if (b.MissingMap && now - b.ContentAt > 9000) { b.MissingMap = false; map = ContentStates.Ok; }
                var profiles = snapshot.Settings.WeaponProfile.Preset == ProfilePresets.Custom || snapshot.Settings.CharacterProfile.Preset == ProfilePresets.Custom ? ContentStates.Ok : ContentStates.None;
                core.Apply(b.Id, "content", Args(new { scenario = ContentStates.Ok, map, profiles }), library);
                if (map == ContentStates.Ok) { b.ContentAt = long.MaxValue; if (b.ReadyAt == long.MaxValue) b.ReadyAt = Jitter(900, 3200); }
            }
            if (snapshot.Match is null && me.Role == MemberRoles.Player && !me.Ready && now >= b.ReadyAt && b.Id != snapshot.HostId)
            {
                core.Apply(b.Id, "ready", Args(new { ready = true }), library);
                b.ReadyAt = long.MaxValue;
            }
        }
        // A simulated host starts once everyone is ready.
        if (bots.ContainsKey(snapshot.HostId))
        {
            var blockers = LobbyRules.StartBlockers(snapshot);
            if (snapshot.Match is null && blockers.Count == 0) { if (hostStartAt == 0) hostStartAt = now + 2500; else if (now >= hostStartAt) { core.Apply(snapshot.HostId, "start", default, library); hostStartAt = 0; } }
            else hostStartAt = 0;
            if (snapshot.Match is { Phase: MatchPhases.Final } && snapshot.Match.Rematch.Count == 0 && now % 20000 < 100) core.Apply(snapshot.HostId, "end", default, library);
        }
        if (snapshot.Match is { } match) PlayRound(core, match, now);
    }

    void PlayRound(LobbyCore core, MatchSnapshot match, long now)
    {
        var key = match.Id + "#" + match.Round;
        foreach (var b in bots.Values)
        {
            if (!match.Players.Contains(b.Id)) continue;
            if (b.Round != key) { b.Round = key; b.Finished = false; b.StartAt = (match.StartsAt ?? now) + random.Next(300, 1600); b.FrameAt = 0; }
            if (match.Phase == MatchPhases.Final && b.RematchAt != long.MaxValue && now >= b.RematchAt && !match.Rematch.Contains(b.Id))
            { core.Apply(b.Id, "rematch", default, library); b.RematchAt = long.MaxValue; }
            if (match.Phase != MatchPhases.Live || b.Finished) continue;
            var line = match.Live.FirstOrDefault(l => l.MemberId == b.Id);
            if (line is null || line.Status is LineStates.Finished or LineStates.Left or LineStates.Dnf) continue;
            var run = Run(match, now, b.StartAt, b.Skill, b.Id.GetHashCode() & 7);
            if (!run.Active || run.Score is null || run.Seconds is null) continue;
            if (run.Remaining <= 0)
            {
                core.Finish(b.Id, new RunFinish(match.Id, match.Round, run.Score.Value, run.Seconds.Value, run.Shots, run.Hits, run.Kills, null));
                b.Finished = true;
                continue;
            }
            if (now < b.FrameAt) continue;
            b.FrameAt = now + 200;
            core.Score(b.Id, new ScoreFrame(match.Id, match.Round, run.Seconds.Value, run.Score.Value, run.Shots, run.Hits, run.Kills, run.Remaining));
        }
    }
}

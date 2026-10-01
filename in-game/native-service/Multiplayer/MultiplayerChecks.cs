using System.Text;
using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

// Synthetic identities and content only; everything runs in temporary folders.
static class MultiplayerChecks
{
    static int count;
    static void Check(bool value, string name) { count++; if (Environment.GetEnvironmentVariable("AIMMOD_CHECK_TRACE") == "1") Console.Error.WriteLine(name); if (!value) throw new Exception("Multiplayer check failed: " + name); }
    static JsonElement J(object value) => JsonSerializer.SerializeToElement(value, Protocol.Json);
    static JsonElement Patch(object settings) => J(new { settings });

    sealed class FakeContent : IContentResolver
    {
        public ScenarioChoice? Scenario(string name) => name.StartsWith("Synthetic", StringComparison.Ordinal) ? new ScenarioChoice(name, ContentLibrary.TextHash(name), "synthetic_map", ContentLibrary.TextHash("map"), 60) : null;
        public MapChoice? Map(string name) => name == "synthetic_port" ? new MapChoice(name, ContentLibrary.TextHash(name), "ported") : null;
        public LibraryItem? Weapon(string name) => name == "Synthetic Rifle" ? new LibraryItem(name, ContentLibrary.TextHash(name)) : null;
        public LibraryItem? Character(string name) => null;
    }

    public static void Run()
    {
        count = 0;
        Rules();
        Authority();
        Matches();
        ProtocolFrames();
        Peers();
        SteamPipe();
        var root = Path.Combine(Path.GetTempPath(), "aimmod-mp-test-" + Guid.NewGuid().ToString("N"));
        try { Content(root); Generator(root); Service(root); }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        Console.WriteLine($"{count} multiplayer checks passed.");
    }

    static (LobbyCore Core, Func<long> Clock, Action<long> Advance) Lobby(LobbySettings? settings = null)
    {
        long now = 1_000_000;
        Func<long> clock = () => now;
        var core = new LobbyCore("host", "Host", settings ?? new LobbySettings(Scenario: new FakeContent().Scenario("Synthetic A")), clock, code: "ABCDEF");
        return (core, clock, ms => now += ms);
    }
    static void Content(LobbyCore core, string id, string state = ContentStates.Ok) => core.Apply(id, "content", J(new { scenario = state, map = ContentStates.Ok }), new FakeContent());
    static void ReadyAll(LobbyCore core)
    {
        foreach (var m in core.Members) { Content(core, m.Id); if (m.Id != core.HostId) core.Apply(m.Id, "ready", J(new { ready = true }), new FakeContent()); }
    }

    static void Rules()
    {
        var content = new FakeContent();
        var start = new LobbySettings(Scenario: content.Scenario("Synthetic A"));
        var (clamped, ok) = LobbyRules.Apply(start, J(new { mode = "ffa-rounds", maxPlayers = 99, targetSpeed = 9, targetSize = 0.01, countdown = 1, timeLimit = 7, rounds = 5.4 }), 3, content);
        Check(ok.Ok && clamped!.MaxPlayers == 8 && clamped.TargetSpeed == 3 && clamped.TargetSize == 0.25 && clamped.Countdown == 3 && clamped.TimeLimit == 10 && clamped.Rounds == 5, "Numbers are clamped to their ranges");
        Check(LobbyRules.Apply(start, J(new { maxPlayers = 2 }), 4, content).Settings!.MaxPlayers == 4, "Max players never drops below the players present");
        Check(LobbyRules.Apply(start, J(new { mode = "practice", targetSpeed = 1.234 }), 2, content).Settings!.TargetSpeed == 1.25, "Multipliers snap to 0.05 steps");
        Check(LobbyRules.Apply(start, J(new { targetSpeed = 2 }), 2, content).Settings!.TargetSpeed == 1, "Score race keeps scenario targets");
        Check(LobbyRules.Apply(start, J(new { mode = "ffa-rounds", timeLimit = 60 }), 2, content).Settings!.TimeLimit is null && !MatchScenario.Needed(LobbyRules.Apply(start, J(new { mode = "ffa-rounds", timeLimit = 60 }), 2, content).Settings!), "Choosing the scenario's own length needs no generated scenario");
        foreach (var bad in new object[] { new { mode = "battle-royale" }, new { privacy = "everyone" }, new { unknown = 1 }, new { maxPlayers = "8" }, new { weapon = "laser" }, new { character = "cs" }, new { scenario = "Not mine" }, new { mapOverride = "../../x" } })
            Check(!LobbyRules.Apply(start, J(bad), 2, content).Result.Ok, "Invalid setting rejected: " + JsonSerializer.Serialize(bad));
        Check(!LobbyRules.Apply(start, JsonDocument.Parse("{\"mode\":\"duel\",\"mode\":\"practice\"}").RootElement, 2, content).Result.Ok, "Repeated keys rejected");
        Check(LobbyRules.Apply(start, J(new { mode = "duel" }), 3, content).Result.Code == "duel-players", "Duel with three players is refused");
        var duel = LobbyRules.Apply(start, J(new { mode = "duel", maxPlayers = 6 }), 2, content).Settings!;
        Check(duel.MaxPlayers == 2, "Duel is always two players");
        var race = LobbyRules.Apply(start, J(new { mode = "practice", mapOverride = "synthetic_port", targetSpeed = 1.5, weapon = "cs", lateJoin = true }), 2, content).Settings!;
        Check(race.MapOverride?.Source == "ported" && race.TargetSpeed == 1.5 && race.LateJoin, "Practice keeps overrides, ported maps and late join");
        var back = LobbyRules.Apply(race, J(new { mode = "score-race" }), 2, content).Settings!;
        Check(back.MapOverride is null && back.TargetSpeed == 1 && back.WeaponProfile.Preset == "default" && !back.LateJoin, "Score race plays the scenario as published");
        var custom = LobbyRules.Apply(start with { Mode = LobbyModes.Rounds }, J(new { weapon = new { preset = "custom", custom = "Synthetic Rifle" } }), 2, content).Settings!;
        Check(custom.WeaponProfile.Custom == "Synthetic Rifle" && custom.WeaponProfile.Hash is { Length: 64 }, "Custom weapon comes from the library with its hash");
        Check(LobbyRules.Plausible(custom) && !LobbyRules.Plausible(custom with { MaxPlayers = 12 }) && !LobbyRules.Plausible(custom with { Mode = "x" }), "Remote snapshots are bounds-checked");
        Check(LobbyRules.CleanName("  Ab\u0001cdefghijklmnopqrstuvwxyz0123456789  ", "x") == "Abcdefghijklmnopqrstuvwxyz012345" && LobbyRules.CleanName("   ", "Player") == "Player", "Names are cleaned and bounded");
        Check(LobbyCore.ValidCode(LobbyCore.NewCode()) && !LobbyCore.ValidCode("ABCDE0") && !LobbyCore.ValidCode("abcdef"), "Room codes avoid look-alike characters");
    }

    static void Authority()
    {
        var content = new FakeContent();
        var (core, _, advance) = Lobby();
        core.Join("p2", "Player Two"); core.Join("p3", "Player Two");
        Check(core.Members.Count(m => m.Name.StartsWith("Player Two", StringComparison.Ordinal)) == 2 && core.Members.Any(m => m.Name == "Player Two 2"), "Duplicate names get a suffix");
        Check(core.Apply("p2", "settings", Patch(new { mode = "practice" }), content).Code == "not-host", "Only the host changes settings");
        Check(core.Apply("p2", "kick", J(new { member = "p3" }), content).Code == "not-host" && core.Apply("p2", "start", default, content).Code == "not-host", "Only the host kicks and starts");
        Check(core.Apply("p2", "ready", J(new { ready = true }), content).Code == "content", "Ready needs the content first");
        ReadyAll(core);
        Check(core.Members.Where(m => m.Id != "host").All(m => m.Ready), "Members ready up once their content is checked");
        core.Apply("host", "settings", Patch(new { privacy = "public" }), content);
        Check(core.Members.First(m => m.Id == "p2").Ready, "Privacy changes keep ready states");
        core.Apply("host", "settings", Patch(new { rounds = 3, mode = "ffa-rounds" }), content);
        Check(core.Members.All(m => !m.Ready), "Play-affecting changes clear ready states");
        core.Apply("host", "settings", Patch(new { scenario = "Synthetic B" }), content);
        Check(core.Members.Where(m => m.Id != "host").All(m => m.Scenario == ContentStates.Unknown), "A new scenario needs a fresh content check");
        Check(LobbyRules.StartBlockers(core.Snapshot()).Any(b => b.Code == "content"), "Start is blocked while content is unchecked");
        Check(core.Apply("host", "kick", J(new { member = "p3" }), content).Ok && core.Join("p3", "Back").Code == "kicked", "Kicked members can't rejoin");
        Check(core.Apply("host", "transfer", J(new { member = "p2" }), content).Ok && core.HostId == "p2", "Host transfer");
        Check(core.Apply("host", "settings", Patch(new { mode = "practice" }), content).Code == "not-host", "The old host loses authority");
        for (var i = 0; i < 5; i++) core.Apply("p2", "chat", J(new { text = "hello " + i }), content);
        Check(core.Apply("p2", "chat", J(new { text = "spam" }), content).Code == "slow", "Chat is rate limited");
        advance(6000);
        Check(core.Apply("p2", "chat", J(new { text = new string('x', 500) }), content).Ok && core.Snapshot().Chat.Last().Text.Length == LobbyRules.MaxChat, "Chat is bounded");
        // Host migration: leaving, and a dropped host after the grace period.
        core.Join("p4", "Four");
        core.Leave("p2");
        Check(core.HostId == "host" && core.Snapshot().Chat.Any(c => c.System && c.Text.Contains("is now the host")), "Host leaving hands the lobby to the longest-present member");
        core.Disconnected("host");
        Check(core.Members.First(m => m.Id == "host").Connection == Connections.Reconnecting && LobbyRules.StartBlockers(core.Snapshot()).Any(b => b.Code == "reconnecting"), "A dropped member shows as reconnecting");
        advance(LobbyCore.HostGraceMs + 1); core.Tick();
        Check(core.HostId == "p4" && core.Members.All(m => m.Id != "host"), "A host who doesn't come back is replaced");
        core.Leave("p4");
        Check(core.Closed, "The last member leaving closes the lobby");
    }

    static void Matches()
    {
        var content = new FakeContent();
        // Duel, first to 2.
        var (core, clock, advance) = Lobby();
        core.Join("p2", "Two");
        core.Apply("host", "settings", Patch(new { mode = "duel", firstTo = 2, countdown = 3 }), content);
        Check(core.Apply("host", "start", default, content).Code == "blocked", "Start waits for ready players");
        ReadyAll(core);
        Check(LobbyRules.StartBlockers(core.Snapshot()).Count == 0 && core.Apply("host", "start", default, content).Ok, "Start once everyone is ready with the content");
        var match = core.Snapshot().Match!;
        Check(match.Phase == MatchPhases.Countdown && match.StartsAt == clock() + 3000, "Countdown is scheduled on the host clock");
        Check(core.Apply("host", "settings", Patch(new { rounds = 2 }), content).Code == "in-match", "Settings lock during a match");
        void Round(double hostScore, double twoScore)
        {
            advance(3001); core.Tick();
            var m = core.Snapshot().Match!;
            Check(m.Phase == MatchPhases.Live, "Round goes live after the countdown");
            Check(core.Score("p2", new ScoreFrame(m.Id, m.Round, 30, twoScore / 2, 50, 60, 10, 30)).Code == "invalid", "Hits above shots are rejected");
            core.Score("p2", new ScoreFrame(m.Id, m.Round, 30, twoScore / 2, 60, 50, 10, 30));
            core.Score("p2", new ScoreFrame(m.Id, m.Round, 20, 1, 60, 50, 10, 40));
            Check(core.Snapshot().Match!.Live.First(l => l.MemberId == "p2").Seconds == 30, "Older frames are ignored");
            advance(60_000);
            core.Finish("host", new RunFinish(m.Id, m.Round, hostScore, 60, 100, 80, 40, null));
            core.Finish("p2", new RunFinish(m.Id, m.Round, twoScore, 60, 100, 70, 35, null));
        }
        Round(1000, 900);
        Check(core.Snapshot().Match!.Phase == MatchPhases.Round && core.Snapshot().Match!.Rounds[0].WinnerId == "host", "Round result names the winner");
        advance(LobbyCore.ResultsMs); core.Tick();
        Check(core.Snapshot().Match!.Round == 2 && core.Snapshot().Match!.Phase == MatchPhases.Countdown, "Next round starts after the results");
        Round(1100, 1000);
        var final = core.Snapshot().Match!;
        Check(final.Phase == MatchPhases.Round, "The deciding round shows its result first");
        advance(LobbyCore.ResultsMs); core.Tick();
        final = core.Snapshot().Match!;
        Check(final.Phase == MatchPhases.Final && final.WinnerId == "host" && final.Standings[0].Wins == 2, "First to two wins the duel");
        MatchSnapshot? finished = null; core.MatchFinished += (m, _) => finished = m;
        core.Apply("host", "rematch", default, content);
        Check(core.Snapshot().Match!.Phase == MatchPhases.Final && core.Snapshot().Match!.Rematch.Count == 1, "Rematch waits for both players");
        core.Apply("p2", "rematch", default, content);
        Check(core.Snapshot().Match is { Phase: MatchPhases.Countdown, Round: 1 } r && r.Id != final.Id, "Rematch starts a new match");

        // Free-for-all points, a disputed result and a player leaving mid-round.
        (core, clock, advance) = Lobby();
        core.Join("p2", "Two"); core.Join("p3", "Three");
        core.Apply("host", "settings", Patch(new { mode = "ffa-rounds", rounds = 1, countdown = 3 }), content);
        ReadyAll(core); core.Apply("host", "start", default, content);
        advance(3001); core.Tick();
        var ffa = core.Snapshot().Match!;
        core.Score("p3", new ScoreFrame(ffa.Id, 1, 59, 500, 10, 5, 5, 1));
        core.Finish("p3", new RunFinish(ffa.Id, 1, 5000, 60, 10, 5, 5, null));
        core.Finish("host", new RunFinish(ffa.Id, 1, 800, 60, 10, 5, 5, null));
        core.Leave("p2");
        var ffaFinal = core.Snapshot().Match!;
        Check(ffaFinal.Phase == MatchPhases.Final, "A round closes when the remaining players finish or leave");
        var placements = ffaFinal.Rounds[0].Results;
        Check(placements.First(p => p.MemberId == "p3").Disputed && placements.First(p => p.MemberId == "p3").Points == 1 && placements.First(p => p.MemberId == "host").Points == 0, "Placement points, and a result that disagrees with its live stream is disputed");
        Check(placements.First(p => p.MemberId == "p2").Status == LineStates.Left, "A player who left is marked");

        // Missing players time out; practice has no ranking.
        (core, clock, advance) = Lobby();
        core.Join("p2", "Two");
        core.Apply("host", "settings", Patch(new { mode = "practice", countdown = 3 }), content);
        ReadyAll(core); core.Apply("host", "start", default, content);
        advance(3001); core.Tick();
        var practice = core.Snapshot().Match!;
        core.Finish("host", new RunFinish(practice.Id, 1, 700, 60, 10, 5, 5, null));
        advance(60_000 + LobbyCore.RoundGraceMs); core.Tick();
        var p = core.Snapshot().Match!;
        Check(p.Phase == MatchPhases.Round && p.Rounds[0].Results.All(x => x.Place == 0) && p.Rounds[0].Results.First(x => x.MemberId == "p2").Status == LineStates.Dnf, "Practice is unranked and late players are marked did-not-finish");
        core.Apply("host", "end", default, content);
        Check(core.Snapshot().Match!.Phase == MatchPhases.Final && core.Snapshot().Match!.WinnerId is null, "Ending practice shows a summary without a winner");
        core.Apply("host", "end", default, content);
        Check(core.Snapshot().Match is null && core.Members.All(m => !m.Ready), "Back to the lobby with ready states cleared");

        // Restore: a client rebuilds the authority from its last snapshot.
        (core, clock, advance) = Lobby();
        core.Join("p2", "Two"); core.Join("p3", "Three");
        ReadyAll(core); core.Apply("host", "start", default, content);
        var snapshot = core.Snapshot();
        var heir = LobbyCore.Restore(snapshot, "p2", clock);
        Check(heir.HostId == "p2" && heir.Members.All(m => m.Id != "host") && heir.Snapshot().Match?.Id == snapshot.Match!.Id, "Host migration keeps the lobby and the running match");
        Check(heir.Members.First(m => m.Id == "p3").Connection == Connections.Reconnecting && heir.Join("p3", "").Ok && heir.Members.First(m => m.Id == "p3").Connection == Connections.Connected, "Other members reconnect to the new host");
        Check(heir.Snapshot().Match!.Live.First(l => l.MemberId == "host").Status == LineStates.Left, "The departed host's run is marked left");
    }

    static void ProtocolFrames()
    {
        var frame = Protocol.Encode(Protocol.Create("command", "l-1", "peer-a", 7, 123, new { id = 1, action = "ready", args = new { ready = true } }));
        var decoded = Protocol.Decode(frame)!;
        Check(decoded.T == "command" && decoded.From == "peer-a" && decoded.Seq == 7 && decoded.Body.GetProperty("args").GetProperty("ready").GetBoolean(), "Envelope round trip");
        Check(Encoding.UTF8.GetString(frame).StartsWith("{\"p\":\"aimmod.mp\",\"v\":1,\"t\":\"command\"", StringComparison.Ordinal), "Stable wire field names");
        string Swap(string from, string to) => Encoding.UTF8.GetString(frame).Replace(from, to, StringComparison.Ordinal);
        foreach (var bad in new[] { Swap("aimmod.mp", "other"), Swap("\"v\":1", "\"v\":2"), Swap("\"command\"", "\"teleport\""), Swap("\"seq\":7", "\"seq\":-1"), "[]", "{", Swap("\"body\":{", "\"body\":[{").Replace("}}}", "}}]}") })
            Check(Protocol.Decode(Encoding.UTF8.GetBytes(bad)) is null, "Rejected frame: " + bad[..Math.Min(40, bad.Length)]);
        Check(Protocol.Decode(new byte[Protocol.MaxBytes + 1]) is null, "Oversized frames are rejected");
        Check(!Protocol.Reliable("score") && Protocol.Reliable("snapshot") && Protocol.Types.Length == 16, "Score frames are unreliable, state is reliable");
        var sync = new ClockSync();
        sync.Add(0, 1050, 200); sync.Add(1000, 2010, 1020); sync.Add(2000, 3100, 2300);
        Check(sync.Rtt == 20 && sync.Offset == 1000, "Clock sync uses the minimum round-trip sample");
    }

    // In-memory peers standing in for Steam relay connections.
    sealed class MemoryNetwork
    {
        public readonly Dictionary<string, MemoryTransport> Peers = new();
        public readonly Dictionary<string, string> Codes = new();
    }
    sealed class MemoryTransport(MemoryNetwork network, string id) : IMultiplayerTransport
    {
        public readonly Queue<TransportEvent> Inbox = new();
        public string Kind => "memory";
        public bool Available => true;
        public string LocalPeer => id;
        public string? LocalName => "Synthetic " + id;
        public void Advertise(LobbySnapshot lobby) => network.Codes[lobby.Code] = id;
        public void Withdraw() { foreach (var k in network.Codes.Where(kv => kv.Value == id).Select(kv => kv.Key).ToArray()) network.Codes.Remove(k); }
        public string? Resolve(string code) => network.Codes.GetValueOrDefault(code);
        public bool BeginJoin(string token) { if (network.Codes.GetValueOrDefault(token) is not { } host) return false; Inbox.Enqueue(new TransportEvent(host, TransportEvent.Connected, Host: true)); return true; }
        public void DismissJoin() { }
        public readonly List<string> HostActions = [];
        public void Kick(string peer) => HostActions.Add("kick " + peer);
        public void Transfer(string peer) => HostActions.Add("transfer " + peer);
        public string? HostHint => null;
        public bool WorkshopDownload(string item) => false;
        public void Send(string peer, byte[] frame, bool reliable) { if (network.Peers.TryGetValue(peer, out var to)) to.Inbox.Enqueue(new TransportEvent(id, TransportEvent.Message, frame)); }
        public void Close(string peer) { }
        public IReadOnlyList<TransportEvent> Drain() { var list = Inbox.ToArray(); Inbox.Clear(); return list; }
        public bool InviteOverlay(LobbySnapshot lobby) => true;
        public bool InviteFriend(string friendId, LobbySnapshot lobby) => true;
        public bool Invited(string peer) => peer == "invited";
        public IReadOnlyList<FriendEntry> Friends() => [new("f1", "Synthetic Friend", "aimmod", null, null, false)];
        public PeerLink? Link(string peer) => new("connected", "relay", 42);
        public void Dispose() { }
    }

    static void Peers()
    {
        long now = 5_000_000;
        var net = new MemoryNetwork();
        var all = new List<MultiplayerService>();
        MultiplayerService Make(string id)
        {
            var t = new MemoryTransport(net, id); net.Peers[id] = t;
            var service = new MultiplayerService(t, new ContentLibrary(null), new NoGameControl(), () => new LocalRun(false, null, null, null, null, 0, 0, 0, null), () => [], () => null, null, false, () => now, autoTick: false);
            all.Add(service); return service;
        }
        var a = Make("peer-a"); var b = Make("peer-b"); var c = Make("peer-c");
        void Pump() { for (var i = 0; i < 6; i++) { now += 50; foreach (var s in all) s.Tick(); } }
        Check(a.Act("create", J(new { mode = "practice" })).Ok, "Host creates a lobby");
        var code = net.Codes.Single().Key;
        Check(b.Act("join", J(new { code })).Ok && c.Act("join", J(new { code = code[..3] + "-" + code[3..] })).Ok, "Clients join by room code, with or without a dash");
        Pump();
        JsonElement View(MultiplayerService s) => JsonSerializer.SerializeToElement(s.View(), Protocol.Json);
        var bv = View(b).GetProperty("lobby");
        Check(bv.GetProperty("members").GetArrayLength() == 3 && !bv.GetProperty("isHost").GetBoolean() && bv.GetProperty("hostId").GetString() == "peer-a", "Clients mirror the host's lobby");
        Check(View(a).GetProperty("lobby").GetProperty("members").EnumerateArray().First(m => m.GetProperty("id").GetString() == "peer-b").GetProperty("link").GetString() == "relay", "Members show their relay link");
        b.Act("settings", Patch(new { mode = "duel" }));
        Pump();
        Check(View(b).GetProperty("notice").GetProperty("text").GetString()!.Contains("host", StringComparison.OrdinalIgnoreCase), "A client's settings change is refused by the host");
        b.Act("chat", J(new { text = "synthetic hello" }));
        Pump();
        Check(View(c).GetProperty("lobby").GetProperty("chat").EnumerateArray().Any(l => l.GetProperty("text").GetString() == "synthetic hello"), "Chat travels through the host");
        // A forged frame claiming to be the host is dropped.
        net.Peers["peer-b"].Inbox.Enqueue(new TransportEvent("peer-c", TransportEvent.Message, Protocol.Encode(Protocol.Create("snapshot", "x", "peer-a", 1, now, new { snapshot = new { } }))));
        Pump();
        Check(View(b).GetProperty("lobby").GetProperty("hostId").GetString() == "peer-a", "Frames whose sender doesn't match the peer are dropped");
        // The host leaves: the longest-present member takes over and the other reconnects.
        a.Act("leave", default);
        Pump(); Pump();
        var bAfter = View(b).GetProperty("lobby"); var cAfter = View(c).GetProperty("lobby");
        Check(bAfter.GetProperty("isHost").GetBoolean() && cAfter.GetProperty("hostId").GetString() == "peer-b", "Host migration over the transport");
        Check(cAfter.GetProperty("members").EnumerateArray().First(m => m.GetProperty("id").GetString() == "peer-c").GetProperty("connection").GetString() == Connections.Connected, "The remaining member reconnects to the new host");
        // Incoming invites are shown and can be declined.
        net.Peers["peer-c"].Inbox.Enqueue(new TransportEvent("steam", TransportEvent.InviteReceived, Invite: new IncomingInvite("i1", "Synthetic Host", "invite", "token", new LobbySummary(LobbyModes.Duel, "Synthetic A", 1, 2), now)));
        Pump();
        Check(View(c).GetProperty("invites").GetArrayLength() == 1 && c.Act("decline-invite", J(new { id = "i1" })).Ok && View(c).GetProperty("invites").GetArrayLength() == 0, "Invites can be declined");
        Check(View(b).GetProperty("friends").GetProperty("items").GetArrayLength() == 1 && b.Act("invite-friend", J(new { friend = "f1" })).Ok && !b.Act("invite-friend", J(new { friend = "steam:123" })).Ok, "Friends come from the transport; unknown ids are refused");
        // Accepting a Steam invite joins asynchronously: the host arrives as a Connected event.
        var d = Make("peer-d");
        net.Peers["peer-d"].Inbox.Enqueue(new TransportEvent("peer-b", TransportEvent.InviteReceived, Invite: new IncomingInvite("i2", "Synthetic Host", "invite", net.Codes.Single().Key, null, now)));
        Pump();
        Check(d.Act("accept-invite", J(new { id = "i2" })).Ok && View(d).GetProperty("joining").ValueKind == JsonValueKind.Object, "Accepting an invite shows the joining state");
        Pump();
        Check(View(d).GetProperty("lobby").GetProperty("members").GetArrayLength() == 3 && View(d).GetProperty("joining").ValueKind == JsonValueKind.Null, "The invited player lands in the host's lobby");
        Check(b.Act("kick", J(new { member = "peer-d" })).Ok && ((MemoryTransport)net.Peers["peer-b"]).HostActions.Contains("kick peer-d"), "Kicks are mirrored to the Steam lobby");
        Pump();
        Check(View(d).GetProperty("lobby").ValueKind == JsonValueKind.Null && View(d).GetProperty("notice").GetProperty("text").GetString()!.Contains("removed"), "A kicked player is told and leaves");
        net.Peers["peer-b"].Inbox.Enqueue(new TransportEvent("", TransportEvent.Error, Reason: "Synthetic bridge error"));
        Pump();
        Check(View(b).GetProperty("notice").GetProperty("text").GetString() == "Synthetic bridge error", "Bridge errors surface as a notice");
    }

    // A fake AimModSteam on a private pipe name checks the v1 contract both ways.
    static void SteamPipe()
    {
        var name = "aimmod-steam-test-" + Guid.NewGuid().ToString("N")[..10];
        using var server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous, 1 << 17, 1 << 17);
        using var steam = new SteamTransport(name);
        Check(server.WaitForConnectionAsync().Wait(5000), "The transport connects to the bridge pipe");
        JsonElement Read()
        {
            var header = new byte[4];
            if (!server.ReadExactlyAsync(header).AsTask().Wait(5000)) throw new Exception("Multiplayer check failed: bridge read timed out");
            var body = new byte[BitConverter.ToUInt32(header)];
            server.ReadExactlyAsync(body).AsTask().Wait(5000);
            return JsonDocument.Parse(body).RootElement.Clone();
        }
        JsonElement Expect(string cmd) { for (var i = 0; i < 6; i++) { var c = Read(); if (c.GetProperty("cmd").GetString() == cmd) return c; } throw new Exception("Multiplayer check failed: expected " + cmd); }
        void Write(object ev) { var bytes = JsonSerializer.SerializeToUtf8Bytes(ev); server.Write(BitConverter.GetBytes((uint)bytes.Length)); server.Write(bytes); server.Flush(); }
        bool Until(Func<bool> condition) { for (var i = 0; i < 100; i++) { if (condition()) return true; Thread.Sleep(20); } return false; }
        var hello = Expect("hello");
        Check(hello.GetProperty("v").GetInt32() == 1 && hello.TryGetProperty("id", out _), "hello opens the contract");
        const string self = "76561190000000001", friend = "76561190000000002", lobbyId = "109775240000000001";
        Write(new { v = 1, ev = "ready", contract = 1, wire = 1, bridge = "test", steam = true, appId = 824270, self = new { peer = self, name = "Synthetic Host", initials = "SH" }, relay = "Current" });
        Check(Until(() => steam.Available) && steam.LocalPeer == self && steam.LocalName == "Synthetic Host", "ready gives the local peer and persona name");
        var (core, _, _) = Lobby();
        steam.Advertise(core.Snapshot());
        var create = Expect("lobby.create");
        Check(create.GetProperty("privacy").GetString() == "friends" && create.GetProperty("maxMembers").GetInt32() == 4 && create.GetProperty("data").GetProperty("aimmod.code").GetString() == "ABCDEF", "Advertise creates a friends lobby with aimmod.* data");
        Write(new { v = 1, ev = "lobby.updated", lobby = lobbyId, owner = self, isHost = true, privacy = "friends", joinable = true, maxMembers = 4, members = new[] { new { peer = self, name = "Synthetic Host", initials = "SH", host = true, self = true, connected = true } }, data = new { } });
        Check(Until(() => steam.HostHint == self), "lobby.updated names the owner");
        steam.Advertise(core.Snapshot());
        Check(Expect("lobby.setData").GetProperty("data").GetProperty("aimmod.players").GetString() == "1/4" && Expect("lobby.setJoinable").GetProperty("joinable").GetBoolean() && Expect("presence.set").GetProperty("status").GetString()!.StartsWith("In an AimMod lobby", StringComparison.Ordinal), "Later adverts update data, joinability and rich presence");
        var frame = Protocol.Encode(Protocol.Create("ping", "l", self, 1, 2, new { t0 = 2 }));
        steam.Send(friend, frame, reliable: false);
        var send = Expect("p2p.send");
        Check(send.GetProperty("peer").GetString() == friend && !send.GetProperty("reliable").GetBoolean() && Convert.FromBase64String(send.GetProperty("data").GetString()!).SequenceEqual(frame) && !send.TryGetProperty("id", out _), "Frames go out as base64 p2p.send without an id");
        steam.InviteOverlay(core.Snapshot());
        Check(!Expect("lobby.invite").TryGetProperty("friend", out _), "Invite friends opens the overlay");
        Write(new { v = 1, ev = "member.joined", member = new { peer = friend, name = "Synthetic Friend", initials = "SF", host = false, self = false, connected = false } });
        Write(new { v = 1, ev = "p2p.connected", peer = friend, host = false });
        Write(new { v = 1, ev = "p2p.message", peer = friend, reliable = true, data = Convert.ToBase64String(frame) });
        Write(new { v = 1, ev = "p2p.ping", peer = friend, rtt = 37 });
        Write(new { v = 1, ev = "friends", friends = new object[] {
            new { peer = "76561190000000003", name = "Offline Friend", initials = "OF", state = "offline", playing = false, aimmod = false },
            new { peer = "76561190000000004", name = "Plain Friend", initials = "PF", state = "online", playing = false, aimmod = false },
            new { peer = friend, name = "Synthetic Friend", initials = "SF", state = "online", playing = true, aimmod = true, lobby = "109775240000000002" } } });
        Write(new { v = 1, ev = "join.requested", source = "launch-aimmodjoin", lobby = "109775240000000003", compatible = true, from = (string?)null });
        var events = new List<TransportEvent>();
        Check(Until(() => { events.AddRange(steam.Drain()); return events.Count >= 3; }), "Bridge events arrive");
        Check(events.Any(e => e.Kind == TransportEvent.Connected && e.Peer == friend && !e.Host) && events.Any(e => e.Kind == TransportEvent.Message && e.Frame!.SequenceEqual(frame)), "p2p.connected and p2p.message map to transport events");
        var launch = events.First(e => e.Kind == TransportEvent.InviteReceived).Invite!;
        Check(launch.Kind == "launch" && launch.Token == "109775240000000003" && launch.FromName == "A friend", "A launch join becomes an invite to confirm");
        Check(Until(() => steam.Link(friend)?.Ping == 37) && steam.Link(friend)!.State == "connected" && steam.Link(friend)!.Route == "relay", "Members carry relay state and ping");
        Check(Until(() => steam.Friends().Count == 2) && steam.Friends()[0].Status == "aimmod-lobby" && steam.Friends()[0].Joinable && steam.Friends()[1].Status == "online", "Online friends, AimMod players first; offline ones are hidden");
        Check(steam.Invited(friend) && !steam.Invited("76561190000000009"), "Lobby membership gates invite-only joins");
        steam.Kick(friend);
        Check(Expect("lobby.kick").GetProperty("peer").GetString() == friend, "Kicks reach the bridge");
        steam.Transfer("not-a-steam-id");
        steam.Transfer(friend);
        Check(Expect("lobby.transfer").GetProperty("peer").GetString() == friend, "Host transfer reaches the bridge, and malformed ids never do");
        Write(new { v = 1, ev = "member.left", peer = friend });
        Check(Until(() => steam.Drain().Any(e => e.Kind == TransportEvent.Left && e.Peer == friend)), "member.left means the member is gone");
        Write(new { v = 1, ev = "error", code = "rejected", message = "banned" });
        Check(Until(() => steam.Drain().Any(e => e.Kind == TransportEvent.Error && e.Reason!.Contains("refused"))), "Bridge errors are reported");
        server.Disconnect();
        Check(Until(() => !steam.Available), "A dropped pipe makes the transport unavailable");
    }

    static void WriteText(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
    const string BaseScenario = "Name=Synthetic A\nPlayerCharacters=Player\nBotCharacters=target.bot\nTimelimit=60.0\nPlayerProfile=Player\nMapName=synthetic_map.map\nMapScale=4.0\nDescription=Synthetic\n\n[Bot Profile]\nName=target\nCharacterProfile=target\n\n[Character Profile]\nName=Player\nMaxHealth=100.0\nWeaponProfileNames=Synthetic Gun;;;;;;;\nMaxSpeed=1000.0\nMainBBHeight=230.0\n\n[Character Profile]\nName=target\nMaxSpeed=800.0\nMainBBHeight=200.0\nMainBBRadius=40.0\n\n[Weapon Profile]\nName=Synthetic Gun\nType=Hitscan\nTimeBetweenShots=0.5\nCategory=SemiAuto\n\n[Map Data]\nreflex map version 8\nglobal\n";

    static void Content(string root)
    {
        var game = Path.Combine(root, "game");
        WriteText(Path.Combine(game, "Saved", "SaveGames", "Scenarios", "Synthetic A.sce"), BaseScenario);
        WriteText(Path.Combine(game, "Saved", "SaveGames", "Scenarios", "Synthetic Port.sce"), "Name=Synthetic Port\nMapName=synthetic_port.json\nTimelimit=30\nDescription=Ported Source map with Counter-Strike movement.\n\n[Map Data]\n{}\n");
        WriteText(Path.Combine(game, "Saved", "SaveGames", "Scenarios", MatchScenario.Prefix + "old.sce"), "Name=generated\n");
        WriteText(Path.Combine(game, "maps", "synthetic_port.json"), "{\"materialSets\":[]}");
        WriteText(Path.Combine(game, "Saved", "SaveGames", "Weapons", "Synthetic Rifle.wep"), "Name=Synthetic Rifle\nTimeBetweenShots=0.08\n");
        var library = new ContentLibrary(game);
        Check(library.Scenarios.Count == 2 && library.Scenarios.All(s => !s.Name.StartsWith("AimMod", StringComparison.Ordinal)), "Generated match scenarios are not offered as content");
        var a = library.Scenarios.First(s => s.Name == "Synthetic A");
        Check(a.Map == "synthetic_map" && a.MapSource == "game" && a.TimeLimit == 60 && a.DefaultWeapon == "Synthetic Gun" && a.Hash.Length == 64, "Scenario header, default weapon and hash are read");
        Check(library.Maps.Single().Source == "ported" && library.Scenarios.First(s => s.Name == "Synthetic Port").MapSource == "ported", "Map-port maps are recognised");
        var settings = new LobbySettings(Scenario: library.Scenario("Synthetic A"));
        Check(library.Check(settings) == (ContentStates.Ok, ContentStates.Ok, ContentStates.None), "Own content checks out");
        Check(library.Check(settings with { Scenario = settings.Scenario! with { Hash = ContentLibrary.TextHash("other") } }).Scenario == ContentStates.Mismatch, "A different scenario version is detected");
        Check(library.Check(settings with { Scenario = settings.Scenario! with { Name = "Synthetic Missing" } }).Scenario == ContentStates.Missing, "A missing scenario is detected");
        Check(library.Check(settings with { MapOverride = new MapChoice("synthetic_port", ContentLibrary.TextHash("x"), "ported") }).Map == ContentStates.Mismatch, "A different map version is detected");
        Check(library.Check(settings with { Weapon = new ProfileChoice("custom", "Nope", ContentLibrary.TextHash("x")) }).Profiles == ContentStates.Missing, "A missing custom profile is detected");
        Check(library.PathOf("scenario", "Synthetic A")!.EndsWith("Synthetic A.sce", StringComparison.Ordinal) && ContentLibrary.Locate(game) == Path.GetFullPath(game), "Local paths stay internal and an explicit game folder is used");
    }

    static void Generator(string root)
    {
        var content = new FakeContent();
        var s = new LobbySettings(Mode: LobbyModes.Rounds, Scenario: new ScenarioChoice("Synthetic A", ContentLibrary.TextHash(BaseScenario), "synthetic_map", ContentLibrary.TextHash("m"), 60));
        Check(!MatchScenario.Needed(s), "An unmodified scenario needs no generated copy");
        var cs = s with { Movement = new ProfileChoice("cs"), TargetSize = 1.5, TargetSpeed = 0.5, TimeLimit = 30 };
        Check(MatchScenario.Needed(cs) && MatchScenario.Name(cs).StartsWith(MatchScenario.Prefix + "Synthetic A - CS - ", StringComparison.Ordinal), "Match scenario name carries base, preset and key");
        var one = MatchScenario.Generate(new(BaseScenario, cs));
        var two = MatchScenario.Generate(new(BaseScenario.Replace("\n", "\r\n"), cs));
        Check(one == MatchScenario.Generate(new(BaseScenario, cs)) && MatchScenario.Hash(one) == MatchScenario.Hash(MatchScenario.Generate(new(BaseScenario, cs))), "Generation is deterministic");
        Check(two.Replace("\r\n", "\n") == one, "Line endings follow the base file without changing content");
        Check(MatchScenario.Key(cs) != MatchScenario.Key(cs with { Movement = new ProfileChoice("quake") }) && MatchScenario.Key(cs) != MatchScenario.Key(cs with { Scenario = cs.Scenario! with { Hash = ContentLibrary.TextHash("v2") } }), "Any input change changes the key");
        Check(one.Contains("Name=" + MatchScenario.Name(cs) + "\n") && one.Contains("Timelimit=30.0\n"), "Header gets the match name and time limit");
        var player = one[one.IndexOf("Name=Player", StringComparison.Ordinal)..one.IndexOf("Name=target\nMaxSpeed", StringComparison.Ordinal)];
        Check(player.Contains("MaxSpeed=1100.0\n") && player.Contains("EnableQuakeMovement=true\n") && player.Contains("ScaledGroundAcceleration=5.2\n"), "CS movement calibrated to KovaaK's Counter-Striker run speed");
        var bot = one[one.IndexOf("Name=target\nMaxSpeed", StringComparison.Ordinal)..];
        Check(bot.Contains("MaxSpeed=400.0\n") && bot.Contains("MainBBHeight=300.0\n") && bot.Contains("MainBBRadius=60.0\n") && player.Contains("MainBBHeight=230.0\n"), "Target speed and size change bots only");
        Check(one.EndsWith("[Map Data]\nreflex map version 8\nglobal\n", StringComparison.Ordinal), "The embedded map is kept");
        var weapon = MatchScenario.Generate(new(BaseScenario, s with { Weapon = new ProfileChoice("valorant") }));
        Check(weapon.Contains("WeaponProfileNames=AimMod Valorant Weapon;;;;;;;\n") && weapon.Contains("Name=AimMod Valorant Weapon\nType=Hitscan\nTimeBetweenShots=0.1026\nCategory=FullyAuto\n") && weapon.Contains("Name=Synthetic Gun\n"), "Weapon presets clone the scenario weapon with the preset fire rate");
        var custom = MatchScenario.Generate(new(BaseScenario, s with { Weapon = new ProfileChoice("custom", "Synthetic Rifle", "h"), MapOverride = new MapChoice("synthetic_port", "h", "ported") }, "synthetic_port.json", "{\"materialSets\":[]}\n", "Name=Other\nTimeBetweenShots=0.08\n"));
        Check(custom.Contains("MapName=synthetic_port.json\n") && custom.EndsWith("[Map Data]\n{\"materialSets\":[]}\n", StringComparison.Ordinal) && custom.Contains("Name=Synthetic Rifle\nTimeBetweenShots=0.08\n"), "Map override and a custom weapon from the library");
        // Store: writes, reuses, never overwrites a user's file, keeps the last few.
        var folder = Path.Combine(root, "store", "Scenarios"); var manifest = Path.Combine(root, "store", "manifest.json");
        var store = new MatchScenarioStore(folder, manifest, keep: 2);
        Check(store.Write(MatchScenario.Name(cs), one, 1).Ok && File.ReadAllText(Path.Combine(folder, MatchScenario.Name(cs) + ".sce")) == one, "Match scenario is written");
        Check(store.Write(MatchScenario.Name(cs), one, 2).Ok, "Writing the same scenario again is a no-op");
        var taken = MatchScenario.Prefix + "Synthetic A - Quake - 00000000";
        WriteText(Path.Combine(folder, taken + ".sce"), "user file");
        Check(store.Write(taken, "generated", 3).Error == "name-taken" && File.ReadAllText(Path.Combine(folder, taken + ".sce")) == "user file", "A user's scenario is never overwritten");
        Check(!store.Write("My Scenario", "x", 4).Ok, "Only reserved names are written");
        for (var i = 0; i < 3; i++) store.Write(MatchScenario.Prefix + "Synthetic A - CS - 1111111" + i, "g" + i, 10 + i);
        Check(store.Files().Count == 2 && !File.Exists(Path.Combine(folder, MatchScenario.Name(cs) + ".sce")) && File.Exists(Path.Combine(folder, taken + ".sce")), "Old match scenarios are cleaned up, user files kept");
    }

    sealed class FakeGame(params string[] caps) : IGameControl
    {
        public readonly List<string> Calls = [];
        public IReadOnlySet<string> Capabilities { get; } = caps.ToHashSet();
        public long? Load(string scenario) { Calls.Add("load " + scenario); return Calls.Count; }
        public long? Start(string scenario, string mode) { Calls.Add("start " + mode + " " + scenario); return Calls.Count; }
        public long? Refresh() { Calls.Add("refresh"); return Calls.Count; }
        public GameCommandResult? Result => null;
    }

    static void Service(string root)
    {
        long now = 9_000_000;
        var game = Path.Combine(root, "game");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(output);
        var control = new FakeGame("load", "start");
        var runs = new List<Run>();
        var service = new MultiplayerService(new OfflineTransport(), new ContentLibrary(game), control, () => new LocalRun(false, null, null, null, null, 0, 0, 0, null), () => runs,
            () => "Synthetic Player", output, simulation: true, () => now, autoTick: false, seed: 7);
        JsonElement View() => JsonSerializer.SerializeToElement(service.View(), Protocol.Json);
        Check(View().GetProperty("lobby").ValueKind == JsonValueKind.Null && View().GetProperty("friends").GetProperty("source").GetString() == "simulation", "Home view without a lobby; simulated friends are labelled");
        Check(!service.Act("join", J(new { code = "bad" })).Ok, "Bad room codes are refused");
        Check(service.Act("create", J(new { mode = "score-race", scenario = "Synthetic A" })).Ok && !service.Act("create", default).Ok, "One lobby at a time");
        service.Act("sim", J(new { op = "add" })); service.Act("sim", J(new { op = "add-missing" }));
        void Run(int ms) { for (var t = 0; t < ms; t += 100) { now += 100; service.Tick(); } }
        Run(4000);
        var lobby = View().GetProperty("lobby");
        Check(lobby.GetProperty("members").GetArrayLength() == 3 && lobby.GetProperty("simulated").GetBoolean(), "Simulated members join and are labelled");
        Check(lobby.GetProperty("blockers").EnumerateArray().Any(b => b.GetProperty("text").GetString()!.Contains("map")), "A member missing the map blocks the start with a reason");
        Run(9000);
        Check(View().GetProperty("lobby").GetProperty("blockers").GetArrayLength() == 0, "Simulated members get the content and ready up");
        Check(service.Act("start", default).Ok, "Host starts the simulated match");
        Run(500);
        Check(control.Calls.SequenceEqual(["load Synthetic A"]) && View().GetProperty("lobby").GetProperty("round").GetProperty("mode").GetString() == "challenge", "Unmodified scenario loads for a normal run during the countdown");
        Run(6000);
        Check(control.Calls.Contains("start challenge Synthetic A"), "The run starts at zero through AimModCore");
        Check(View().GetProperty("lobby").GetProperty("match").GetProperty("live").EnumerateArray().Count(l => l.GetProperty("status").GetString() == "playing") >= 2, "Live score frames arrive");
        Run(62_000);
        var match = View().GetProperty("lobby").GetProperty("match");
        Check(match.GetProperty("phase").GetString() == MatchPhases.Final && match.GetProperty("standings").GetArrayLength() == 3, "The simulated race reaches its final results");
        Check(View().GetProperty("recent").GetArrayLength() == 1 && File.Exists(Path.Combine(output, "multiplayer-matches.json")), "Finished matches are kept locally, apart from KovaaK's leaderboards");
        // Overrides build a match scenario and run it in freeplay.
        service.Act("end", default);
        service.Act("settings", Patch(new { mode = "ffa-rounds", rounds = 1, movement = "cs", targetSize = 1.5 }));
        Run(6000);
        var generated = View().GetProperty("lobby").GetProperty("generated");
        Check(generated.GetProperty("name").GetString()!.StartsWith(MatchScenario.Prefix, StringComparison.Ordinal), "The lobby shows the match scenario it will build");
        Check(File.Exists(Path.Combine(game, "Saved", "SaveGames", "Scenarios", generated.GetProperty("name").GetString()! + ".sce")) && generated.GetProperty("saved").GetBoolean() && control.Calls.Contains("refresh"), "The match scenario is written and refreshed before the match starts");
        Check(service.Act("start", default).Ok, "Start with overrides");
        Run(500);
        var name = generated.GetProperty("name").GetString()!;
        Check(control.Calls.Last() == "load " + name && File.Exists(Path.Combine(game, "Saved", "SaveGames", "Scenarios", name + ".sce")), "The generated scenario is written and loaded");
        Run(6000);
        Check(control.Calls.Last() == "start freeplay " + name, "Generated scenarios run in freeplay");
        // Host leaving a simulated lobby hands it over; invites and launch joins.
        service.Act("leave", default);
        Check(service.Act("join", J(new { code = "SAMPLE" })).Ok && !View().GetProperty("lobby").GetProperty("isHost").GetBoolean(), "Joining a simulated room shows a read-only lobby");
        Run(1000);
        service.Act("sim", J(new { op = "host-leave" }));
        Run(200);
        Check(View().GetProperty("lobby").GetProperty("hostId").GetString() != null && View().GetProperty("lobby").GetProperty("chat").EnumerateArray().Any(c => c.GetProperty("text").GetString()!.Contains("now the host")), "The simulated host leaving migrates the lobby");
        service.Act("sim", J(new { op = "launch" }));
        var invite = View().GetProperty("invites")[0];
        Check(invite.GetProperty("kind").GetString() == "launch" && invite.GetProperty("summary").GetProperty("mode").GetString() == LobbyModes.Duel, "A launch-from-invite join is offered with a lobby summary");
        Check(service.Act("accept-invite", J(new { id = invite.GetProperty("id").GetString() })).Ok && View().GetProperty("lobby").ValueKind == JsonValueKind.Object, "Accepting an invite joins that lobby");
        Check(MultiplayerService.SimulationRequested(["--multiplayer-sim"], output), "The simulation can be requested explicitly");
        File.WriteAllText(Path.Combine(output, "multiplayer-dev.json"), "{\"simulation\":true}");
        Check(MultiplayerService.SimulationRequested([], output), "The developer setting turns the simulation on");
        service.Dispose();
    }
}

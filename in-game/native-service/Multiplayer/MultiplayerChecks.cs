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
        try { Content(root); Generator(root); Service(root); Transfers(root); Replays(root); Maps(root); }
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
        Check(core.Apply("p2", "ready-check", default, content).Code == "not-host" && core.Apply("host", "ready-check", default, content).Ok && core.Snapshot().ReadyCheck is not null, "Only the host asks everyone to ready up");
        advance(LobbyCore.ReadyCheckMs + 1); core.Tick();
        Check(core.Snapshot().ReadyCheck is null, "A ready check expires");
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

        // Looks and builds.
        (core, clock, advance) = Lobby();
        core.Join("p2", "Two", version: "bridge-2");
        core.SetVersion("host", "bridge-1");
        Check(core.Apply("p2", "avatar", J(new { avatar = "endo" }), content).Ok && core.Members.First(m => m.Id == "p2").Avatar == "endo" && !core.Apply("p2", "avatar", J(new { avatar = "../x" }), content).Ok, "Members pick a look from the offered ones");
        Check(LobbyRules.StartBlockers(core.Snapshot()).Any(b => b.Code == "version" && b.Text.Contains("Two")), "A different AimMod build blocks the start with a clear reason");
        core.SetVersion("p2", "bridge-1");
        core.Join("p2", "Two", version: "bridge-1");
        Check(LobbyRules.StartBlockers(core.Snapshot()).All(b => b.Code != "version"), "Same builds play together");

        // Away players don't block; suggestions and votes; a warm-up until everyone has loaded.
        (core, clock, advance) = Lobby();
        core.Join("p2", "Two"); core.Join("p3", "Three");
        ReadyAll(core);
        core.Apply("p3", "ready", J(new { ready = false }), content);
        Check(LobbyRules.StartBlockers(core.Snapshot()).Any(b => b.Code == "ready"), "An unready player blocks");
        Check(core.Apply("p2", "skip", J(new { member = "p3" }), content).Code == "not-host" && core.Apply("host", "skip", J(new { member = "p3" }), content).Ok && LobbyRules.StartBlockers(core.Snapshot()).Count == 0, "The host can mark a player away, who then doesn't block");
        core.Apply("p3", "suggest", J(new { scenario = "Synthetic B" }), content);
        core.Apply("p2", "vote", J(new { scenario = "Synthetic B" }), content);
        Check(core.Snapshot().Suggestions!.Single().Votes.Count == 2, "Members suggest scenarios and vote");
        Check(core.Apply("host", "pick", J(new { scenario = "Synthetic B" }), content).Ok && core.Settings.Scenario!.Name == "Synthetic B" && core.Snapshot().Suggestions!.Count == 0, "The host picks a suggestion");
        core.Apply("host", "settings", Patch(new { voting = false }), content);
        Check(core.Apply("p2", "suggest", J(new { scenario = "Synthetic C" }), content).Code == "voting-off", "The host can turn suggestions off");
        ReadyAll(core); core.Apply("host", "skip", J(new { member = "p3" }), content);
        core.RequireLoading = true;
        core.Apply("host", "start", default, content);
        var lm = core.Snapshot().Match!;
        Check(lm.Phase == MatchPhases.Loading && !lm.Players.Contains("p3"), "The match waits for loading, without the away player");
        core.Apply("host", "loaded", J(new { }), content); core.Tick();
        Check(core.Snapshot().Match!.Phase == MatchPhases.Loading && core.Snapshot().Match!.Loaded!.Count == 1, "Loading waits for everyone");
        core.Apply("p2", "loaded", J(new { }), content); core.Tick();
        Check(core.Snapshot().Match!.Phase == MatchPhases.Countdown, "The countdown starts when everyone has loaded");

        // Auto start once nothing blocks for a moment.
        (core, clock, advance) = Lobby();
        core.Join("p2", "Two");
        core.Apply("host", "settings", Patch(new { mode = "practice", autoStart = true }), content);
        ReadyAll(core); core.Tick();
        Check(core.Snapshot().AutoStartAt is not null && core.Snapshot().Match is null, "Auto start waits a moment once everyone is ready");
        core.Apply("p2", "ready", J(new { ready = false }), content); core.Tick();
        Check(core.Snapshot().AutoStartAt is null, "Unreadying cancels the auto start");
        core.Apply("p2", "ready", J(new { ready = true }), content); core.Tick();
        advance(LobbyCore.AutoStartMs + 1); core.Tick();
        Check(core.Snapshot().Match?.Phase == MatchPhases.Countdown, "The lobby starts itself");

        // Play again: confirmers rematch; whoever doesn't answer in time sits out.
        (core, clock, advance) = Lobby();
        core.Join("p2", "Two"); core.Join("p3", "Three");
        core.Apply("host", "settings", Patch(new { mode = "practice", countdown = 3 }), content);
        ReadyAll(core); core.Apply("host", "start", default, content);
        advance(3001); core.Tick();
        var pm = core.Snapshot().Match!;
        core.Leave("p3"); core.Join("p3", "Three");
        Check(core.Members.First(m => m.Id == "p3").Role == MemberRoles.Player && core.Snapshot().Match!.Live.First(l => l.MemberId == "p3").Status == LineStates.Waiting, "A player who drops out of a running match rejoins it as a player");
        foreach (var id in new[] { "host", "p2", "p3" }) core.Finish(id, new RunFinish(pm.Id, 1, 500, 60, 10, 5, 5, null));
        core.Apply("host", "end", default, content);
        Check(core.Snapshot().Match!.Phase == MatchPhases.Final, "Practice ends on request");
        core.Apply("host", "rematch", default, content); core.Apply("p2", "rematch", default, content);
        Check(core.Snapshot().Match!.RematchDeadline is not null && core.Snapshot().Match!.Phase == MatchPhases.Final, "The first vote opens a rematch window");
        advance(LobbyCore.RematchMs + 1); core.Tick();
        Check(core.Snapshot().Match is { Phase: MatchPhases.Countdown } again && again.Players.Count == 2 && !again.Players.Contains("p3") && core.Members.First(m => m.Id == "p3").Role == MemberRoles.Spectator, "Players who don't confirm in time sit the rematch out");
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
        Check(!Protocol.Reliable("score") && Protocol.Reliable("snapshot") && Protocol.Types.Length == 18, "Score frames are unreliable, state is reliable");
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
        public string? Resolve(string code) { if (network.Codes.ContainsKey(code)) joinedCode = code; return network.Codes.GetValueOrDefault(code); }
        public bool BeginJoin(string token) { if (network.Codes.GetValueOrDefault(token) is not { } host) return false; joinedCode = token; Inbox.Enqueue(new TransportEvent(host, TransportEvent.Connected, Host: true)); return true; }
        public void DismissJoin() { }
        public readonly List<string> HostActions = [];
        public void Kick(string peer) => HostActions.Add("kick " + peer);
        public void Transfer(string peer) => HostActions.Add("transfer " + peer);
        public string? HostHint => null;
        string? joinedCode;
        public string? LobbyToken => network.Codes.FirstOrDefault(kv => kv.Value == id).Key ?? joinedCode;
        public string? BridgeVersion { get; set; }
        public RejoinPoint? LastLobby => null;
        public void SetPresencePrivacy(bool hideScenario) { }
        public bool StartSpectate(string peer, int rate) => false;
        public void StopSpectate() { }
        public bool AllowSpectate;
        public readonly List<string> SpectateLog = [];
        public bool RequestSpectate(string peer) { SpectateLog.Add("request " + peer); return AllowSpectate; }
        public void AnswerSpectate(string peer, bool allow) => SpectateLog.Add((allow ? "allow " : "deny ") + peer);
        public void SetSpectatePrivacy(string mode) { }
        public void RemoveSpectator(string peer) { }
        public bool WorkshopDownload(string item) => false;
        // Bulk lane stand-in: chunks arrive in order; DropAfter cuts a transfer short like a lost link.
        public int BulkChunkBytes { get; set; }
        public int DropAfter = -1, BulkSent;
        public readonly List<(int Transfer, int Index)> BulkLog = [];
        public readonly List<(string Peer, int Transfer, string Reason)> Cancelled = [];
        public BulkSend BulkChunk(string peer, int transfer, int index, byte[] data)
        {
            if (BulkChunkBytes == 0 || !network.Peers.TryGetValue(peer, out var to)) return BulkSend.Unavailable;
            BulkLog.Add((transfer, index)); BulkSent += data.Length;
            if (DropAfter == 0)
            {
                // The link drops: both ends hear the transfer ended; this chunk never arrives.
                DropAfter = -1;
                to.Inbox.Enqueue(new TransportEvent(id, TransportEvent.BulkEnd, Reason: "disconnected", Transfer: transfer));
                Inbox.Enqueue(new TransportEvent(peer, TransportEvent.BulkEnd, Reason: "disconnected", Transfer: transfer));
                return BulkSend.Sent;
            }
            if (DropAfter > 0) DropAfter--;
            to.Inbox.Enqueue(new TransportEvent(id, TransportEvent.BulkData, data, Transfer: transfer, Index: index));
            return BulkSend.Sent;
        }
        public void BulkCancel(string peer, int transfer, string reason) => Cancelled.Add((peer, transfer, reason));
        public void Send(string peer, byte[] frame, bool reliable) { if (network.Peers.TryGetValue(peer, out var to)) to.Inbox.Enqueue(new TransportEvent(id, TransportEvent.Message, frame)); }
        public void Close(string peer) { }
        public IReadOnlyList<TransportEvent> Drain() { var list = Inbox.ToArray(); Inbox.Clear(); return list; }
        public bool InviteOverlay(LobbySnapshot lobby) => true;
        public bool InviteFriend(string friendId, LobbySnapshot lobby) => true;
        public bool Invited(string peer) => peer == "invited";
        public IReadOnlyList<FriendEntry>? FriendList;
        public IReadOnlyList<FriendEntry> Friends() => FriendList ?? (IReadOnlyList<FriendEntry>)[new("f1", "Synthetic Friend", "aimmod", null, null, false), new("f2", "Watchable Friend", "aimmod", "Playing Synthetic A", null, false, Spectatable: true, Watchers: 1, Scenario: "Synthetic A")];
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
        net.Peers["peer-c"].Inbox.Enqueue(new TransportEvent("steam", TransportEvent.InviteReceived, Invite: new IncomingInvite("i1", "Synthetic Host", "incoming", "token", new LobbySummary(LobbyModes.Duel, "Synthetic A", 1, 2), now)));
        Pump();
        Check(View(c).GetProperty("invites").GetArrayLength() == 1 && c.Act("decline-invite", J(new { id = "i1" })).Ok && View(c).GetProperty("invites").GetArrayLength() == 0, "Invites can be declined");
        Check(View(b).GetProperty("friends").GetProperty("items").GetArrayLength() == 2 && b.Act("invite-friend", J(new { friend = "f1" })).Ok && !b.Act("invite-friend", J(new { friend = "steam:123" })).Ok, "Friends come from the transport; unknown ids are refused");
        // Spectating a friend without a lobby, and being watched.
        var bt = (MemoryTransport)net.Peers["peer-b"];
        Check(!b.Act("watch", J(new { friend = "f1" })).Ok, "Friends who don't allow spectators can't be watched");
        bt.AllowSpectate = true;
        Check(b.Act("watch", J(new { friend = "f2" })).Ok && View(b).GetProperty("watch").GetProperty("state").GetString() == "requesting", "Asking to watch a friend");
        bt.Inbox.Enqueue(new TransportEvent("f2", TransportEvent.SpectateStarted, Reason: "Watchable Friend"));
        bt.Inbox.Enqueue(new TransportEvent("f2", TransportEvent.SpectateScore, Frame: Encoding.UTF8.GetBytes("{\"active\":true,\"score\":1234,\"accuracy\":85.5,\"remaining\":20}")));
        Pump();
        var watching = View(b).GetProperty("watch");
        Check(watching.GetProperty("scenario").GetString() == "Synthetic A" && watching.GetProperty("state").GetString() == "missing" && watching.GetProperty("message").GetString()!.Contains("don’t have"), "The friend's scenario is known, and missing content is explained");
        b.Act("watch-started", default);
        Check(JsonDocument.Parse(b.NoticeText()).RootElement.GetProperty("badge").GetString() == "Watching Watchable Friend · 1,234 · 85.5% · 20 s left", "The spectator sees the friend's live stats over their view");
        bt.Inbox.Enqueue(new TransportEvent("f2", TransportEvent.SpectateEnded, Reason: "declined"));
        Pump();
        Check(View(b).GetProperty("watch").GetProperty("message").GetString()!.Contains("said no"), "An end reason is explained");
        b.Act("watch-stop", default);
        var ct = (MemoryTransport)net.Peers["peer-c"];
        ct.Inbox.Enqueue(new TransportEvent("f9", TransportEvent.SpectatorJoined, Reason: "Synthetic Watcher"));
        ct.Inbox.Enqueue(new TransportEvent("f8", TransportEvent.SpectatorJoined, Reason: "Quiet Sync", Host: true));
        ct.Inbox.Enqueue(new TransportEvent("f7", TransportEvent.SpectateAsked, Reason: "Synthetic Asker"));
        Pump();
        var cn = JsonDocument.Parse(c.NoticeText()).RootElement.GetRawText(); var cnBadge = JsonDocument.Parse(c.NoticeText()).RootElement.GetProperty("badge").GetString() ?? ""; var cnTitle = JsonDocument.Parse(c.NoticeText()).RootElement.GetProperty("title").GetString() ?? "";
        Check(cnBadge == "2 watching: Synthetic Watcher, Quiet Sync" && cnTitle == "Synthetic Asker wants to watch you" && cn.Contains("spectate-allow"), "The watched player sees who watches and can allow a request");
        Check(c.Act("spectate-allow", J(new { id = "f7" })).Ok && ct.SpectateLog.Contains("allow f7") && !c.NoticeText().Contains("wants to watch"), "Allowing answers the bridge and clears the popup");
        // A Steam join (invite accepted in Steam) joins asynchronously: the host arrives as a Connected event.
        var d = Make("peer-d");
        net.Peers["peer-d"].Inbox.Enqueue(new TransportEvent("peer-b", TransportEvent.InviteReceived, Invite: new IncomingInvite("i2", "Synthetic Host", "invite", net.Codes.Single().Key, null, now)));
        Pump();
        Check(View(d).GetProperty("lobby").ValueKind == JsonValueKind.Object && View(d).GetProperty("invites").GetArrayLength() == 0, "A join the player chose in Steam happens without another prompt");
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
        Write(new { v = 1, ev = "ready", contract = 1, wire = 1, bridge = "test", steam = true, appId = 824270, self = new { peer = self, name = "Synthetic Host", initials = "SH" }, relay = "Current", features = new[] { "lobby", "p2p", "ugc", "xfer" }, maxChunk = 32768, xferWindow = 4 });
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
        // Contract additions: Workshop downloads and the bulk file lane.
        Check(steam.BulkChunkBytes == 32768, "The bulk lane follows the bridge's maxChunk");
        Check(steam.WorkshopDownload("3333089221") && Expect("ugc.download").GetProperty("highPriority").GetBoolean() && !steam.WorkshopDownload("../1"), "Workshop downloads go to the bridge with only numeric item ids");
        Write(new { v = 1, ev = "ugc.progress", item = "3333089221", downloaded = 10, total = 40 });
        Write(new { v = 1, ev = "ugc.installed", item = "3333089221", folder = "synthetic" });
        var ugcEvents = new List<TransportEvent>();
        Check(Until(() => { ugcEvents.AddRange(steam.Drain().Where(e => e.Kind == TransportEvent.WorkshopUpdate)); return ugcEvents.Count >= 2; }) && ugcEvents[0].Workshop!.Done == 10 && ugcEvents[1].Workshop!.State == "installed", "Workshop progress and install arrive as events");
        var piece = new byte[100];
        for (var i = 0; i < 4; i++) Check(steam.BulkChunk(friend, 7, i, piece) == BulkSend.Sent && Expect("xfer.chunk").GetProperty("index").GetInt32() == i, "Bulk chunks go out as xfer.chunk");
        Check(steam.BulkChunk(friend, 7, 4, piece) == BulkSend.WindowFull, "The send window of four is respected");
        Write(new { v = 1, ev = "xfer.ack", peer = friend, transfer = 7, index = 0, credit = 1 });
        Check(Until(() => steam.BulkChunk(friend, 7, 4, piece) == BulkSend.Sent) && Expect("xfer.chunk").GetProperty("index").GetInt32() == 4, "An ack frees the window");
        Write(new { v = 1, ev = "xfer.chunk", peer = friend, transfer = 9, index = 0, data = Convert.ToBase64String(piece) });
        Write(new { v = 1, ev = "xfer.end", peer = friend, transfer = 9, reason = "disconnected", by = "peer" });
        var bulkEvents = new List<TransportEvent>();
        Check(Until(() => { bulkEvents.AddRange(steam.Drain()); return bulkEvents.Any(e => e.Kind == TransportEvent.BulkEnd); }) && bulkEvents.Any(e => e.Kind == TransportEvent.BulkData && e.Transfer == 9 && e.Frame!.Length == 100) && bulkEvents.First(e => e.Kind == TransportEvent.BulkEnd).Reason == "disconnected", "Incoming bulk chunks and transfer ends map to events");
        steam.BulkCancel(friend, 7, "complete");
        Check(Expect("xfer.cancel").GetProperty("reason").GetString() == "complete", "Finished transfers are closed as complete");
        // Spectating without a lobby (contract §6).
        Check(steam.RequestSpectate(friend) && Expect("spectate.request").GetProperty("rate").GetInt32() == 60, "Spectate requests go to the bridge");
        Write(new { v = 1, ev = "spectate.started", peer = friend, name = "Synthetic Friend", direct = true });
        Write(new { v = 1, ev = "spectate.score", active = true, paused = false, score = 10, seconds = 5, remaining = 55, shots = 4, hits = 3, kills = 2, accuracy = 75 });
        Write(new { v = 1, ev = "spectate.asked", from = "76561190000000008", fromName = "Synthetic Asker" });
        Write(new { v = 1, ev = "spectator.joined", peer = "76561190000000009", name = "Synthetic Viewer" });
        Write(new { v = 1, ev = "spectators", list = new[] { new { peer = "76561190000000009", name = "Synthetic Viewer" }, new { peer = "76561190000000010", name = "Already Watching" } } });
        Write(new { v = 1, ev = "spectator.left", peer = "76561190000000009", reason = "stopped" });
        Write(new { v = 1, ev = "spectate.ended", peer = friend, reason = "stopped" });
        var spectateEvents = new List<TransportEvent>();
        Check(Until(() => { spectateEvents.AddRange(steam.Drain()); return spectateEvents.Any(e => e.Kind == TransportEvent.SpectateEnded); }), "Spectate events arrive");
        Check(spectateEvents.Any(e => e.Kind == TransportEvent.SpectateStarted && e.Reason == "Synthetic Friend" && e.Host) && spectateEvents.Any(e => e.Kind == TransportEvent.SpectateScore)
            && spectateEvents.Any(e => e.Kind == TransportEvent.SpectateAsked && e.Peer == "76561190000000008" && e.Reason == "Synthetic Asker")
            && spectateEvents.Any(e => e.Kind == TransportEvent.SpectatorJoined && e.Peer == "76561190000000010" && e.Host) && spectateEvents.Any(e => e.Kind == TransportEvent.SpectatorLeft), "started, score, asked, joined, the quiet list sync and left all map");
        steam.AnswerSpectate("76561190000000008", true); steam.SetSpectatePrivacy("ask"); steam.RemoveSpectator("76561190000000010");
        Check(Expect("spectate.answer").GetProperty("allow").GetBoolean() && Expect("spectate.privacy").GetProperty("mode").GetString() == "ask" && Expect("spectate.remove").GetProperty("peer").GetString() == "76561190000000010", "Answers, privacy and removal reach the bridge");
        // Rich-presence friend status and the bridge build.
        Check(steam.BridgeVersion == "test", "The bridge build comes from ready");
        Write(new { v = 1, ev = "friends", friends = new object[] {
            new { peer = "76561190000000005", name = "Lobby Friend", initials = "LF", state = "online", playing = true, aimmod = true, aimmodState = "lobby", lobbySize = 2, lobbyMax = 4, lobbyJoinable = true, lobby = "109775240000000005" },
            new { peer = "76561190000000006", name = "Busy Friend", initials = "BF", state = "online", playing = true, aimmod = true, aimmodState = "playing", scenario = "Synthetic A" },
            new { peer = "76561190000000007", name = "Idle Friend", initials = "IF", state = "online", playing = true, aimmod = true, aimmodState = "idle" } } });
        Check(Until(() => steam.Friends().Count == 3) && steam.Friends()[0].Detail == "In AimMod lobby (2/4)" && steam.Friends()[0].Joinable && steam.Friends().Any(f => f.Detail == "Playing Synthetic A") && steam.Friends().Any(f => f.Detail == "Idle"), "Friends show In AimMod lobby, Playing <scenario> and Idle");
        var (avatarCore, _, _) = Lobby();
        avatarCore.Join(friend, "Synthetic Friend");
        avatarCore.Apply(friend, "avatar", J(new { avatar = "meso-tracer" }), new FakeContent());
        steam.Advertise(avatarCore.Snapshot());
        Check(Expect("lobby.setData").GetProperty("data").GetProperty("aimmod.char." + friend).GetString() == "AimMod Meso Tracer", "The host publishes each player's look as aimmod.char.<id>");
        Write(new { v = 1, ev = "member.left", peer = friend });
        Check(Until(() => steam.Drain().Any(e => e.Kind == TransportEvent.Left && e.Peer == friend)), "member.left means the member is gone");
        Write(new { v = 1, ev = "error", code = "rejected", message = "banned" });
        Check(Until(() => steam.Drain().Any(e => e.Kind == TransportEvent.Error && e.Reason!.Contains("refused"))), "Bridge errors are reported");
        Check(!steam.QueryWorkshop(MapPorts.WorkshopTag), "Without the ugc-query feature the Workshop isn't listed");
        Write(new { v = 1, ev = "ugc.items", tag = MapPorts.WorkshopTag, items = new object[] {
            new { item = "3333000001", title = "AimMod - Dust2 (CSGO) - CS Movement", bytes = 71_000_000L, updated = 1_790_000_000L, subscribed = true, installed = true, needsUpdate = true },
            new { item = "../bad", title = "AimMod - Bad (CSS) - CS Movement" } } });
        Check(Until(() => steam.WorkshopItems.Count == 1) && steam.WorkshopItems[0].NeedsUpdate && steam.WorkshopItems[0].Bytes == 71_000_000L, "Workshop listings keep valid items and Steam's update state");
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

    static void Maps(string root)
    {
        var game = Path.Combine(root, "maps-game");
        var scenarios = Path.Combine(game, "Saved", "SaveGames", "Scenarios");
        WriteText(Path.Combine(scenarios, "AimMod - Dust2 (CSGO) - CS Movement.sce"), "Name=AimMod - Dust2 (CSGO) - CS Movement\nMapName=aimmod_de_dust2_csgo.json\nMapScale=4.0\nTimelimit=600\nDescription=Ported Source map with Counter-Strike movement.\n\n[Character Profile]\nName=Player\nAbilityProfileNames=CS Walk\n\n[Map Data]\n{}\n");
        WriteText(Path.Combine(scenarios, "Synthetic Plain.sce"), "Name=Synthetic Plain\nMapName=synthetic_map.map\nTimelimit=60\n\n[Map Data]\n");
        WriteText(Path.Combine(game, "maps", "aimmod_de_dust2_csgo.json"), new string('x', 2048));
        File.WriteAllBytes(Path.Combine(game, "maps", "aimmod_de_dust2_csgo.preview.png"), [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        Check(MapPorts.Parse("AimMod - Office (CSS) - Sprint") == ("Office", "CSS", "Sprint") && MapPorts.Parse("AimMod - Office (Quake) - Sprint") is null && MapPorts.Parse("Synthetic Plain") is null, "Port names follow naming.py");
        var library = new ContentLibrary(game);
        var catalog = new[] { new WorkshopItem("3333000002", "AimMod - Mirage (CSGO) - CS Movement", 61_000_000, 0, false, false, false), new WorkshopItem("3333000003", "Not a port", 1, 0, false, false, false) };
        var ports = MapPorts.List(library, catalog);
        Check(ports.Count == 2 && ports[0].Installed && ports[0].Display == "Dust2" && ports[0].Game == "CSGO" && ports[0].Shift == "walk" && ports[0].Bytes > 2048 && ports[0].Preview is not null && ports[0].MapScale == 4, "Installed ports show map, game, size, Shift and preview");
        Check(!ports[1].Installed && ports[1].WorkshopId == "3333000002" && ports[1].Bytes == 61_000_000, "Workshop ports this machine lacks are listed for install; other items are not");
        long now = 5_000_000;
        var service = new MultiplayerService(new OfflineTransport(), library, new FakeGame("load"), () => new LocalRun(false, null, null, null, null, 0, 0, 0, null), () => [], () => null, null, simulation: true, () => now, autoTick: false);
        JsonElement Maps() => JsonSerializer.SerializeToElement(service.MapsView(), Protocol.Json);
        var view = Maps();
        var dust = view.GetProperty("ports").EnumerateArray().First(p => p.GetProperty("display").GetString() == "Dust2");
        Check(view.GetProperty("source").GetString() == "simulation" && dust.GetProperty("preview").GetBoolean() && !dust.ToString().Contains(root, StringComparison.OrdinalIgnoreCase), "The Map Library view never shows local paths");
        Check(service.MapPreview(dust.GetProperty("key").GetString())!.EndsWith(".preview.png", StringComparison.Ordinal) && service.MapPreview("../../x") is null && service.MapPreview("000000000000") is null, "Only previews the library found are served");
        Check(service.Act("map-load", J(new { key = dust.GetProperty("key").GetString() })).Ok, "Installed ports load through AimModCore");
        var mirage = Maps().GetProperty("ports").EnumerateArray().First(p => p.GetProperty("display").GetString() == "Mirage");
        Check(!mirage.GetProperty("installed").GetBoolean() && service.Act("map-install", J(new { key = mirage.GetProperty("key").GetString() })).Ok, "A simulated Workshop install starts");
        for (var i = 0; i < 10; i++) { now += 100; service.Tick(); }
        var progress = Maps().GetProperty("ports").EnumerateArray().First(p => p.GetProperty("display").GetString() == "Mirage").GetProperty("download");
        Check(progress.GetProperty("state").GetString() == "downloading" && progress.GetProperty("done").GetInt64() > 0, "Install progress is shown");
        for (var i = 0; i < 60; i++) { now += 100; service.Tick(); }
        mirage = Maps().GetProperty("ports").EnumerateArray().First(p => p.GetProperty("display").GetString() == "Mirage");
        Check(mirage.GetProperty("installed").GetBoolean() && mirage.GetProperty("simulated").GetBoolean() && mirage.GetProperty("download").ValueKind == JsonValueKind.Null && !Directory.EnumerateFiles(scenarios).Any(f => f.Contains("Mirage")), "Simulated installs finish without writing to the game");
        Check(!service.Act("map-load", J(new { key = mirage.GetProperty("key").GetString() })).Ok && !service.Act("map-install", J(new { key = "nope" })).Ok, "Simulated installs can't be loaded and unknown maps are refused");
        service.Dispose();
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
        Check(AvatarProfiles.All.All(a => one.Contains("[Character Profile]\nName=" + a.ProfileName + "\n") ) && one.Contains("CharacterModel=Meso\nCharacterSkin=McCree\n"), "Match scenarios ship a body profile for every offered look");
        var chars = Path.Combine(root, "avatars");
        WriteText(Path.Combine(chars, "AimMod Endo.chr"), "Name=AimMod Endo\nmy own tweaks\n");
        Check(AvatarFiles.Install(chars) == AvatarProfiles.All.Length - 1 && File.ReadAllText(Path.Combine(chars, "AimMod Endo.chr")) == "Name=AimMod Endo\nmy own tweaks\n" && AvatarFiles.Install(chars) == 0, "Avatar profiles are installed once and a user's same-named profile is left alone");
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

    // Synthetic format 2 replay (same fixture as CoreFormatChecks).
    const string SyntheticReplay = "QU1SUExBWTICAAAAzQEAAHsia2luZCI6ImhlYWRlciIsInZlcnNpb24iOjIsImlkIjoiMTc5MDAwMDAwMC00Mi0yIiwic2NlbmFyaW8iOiJTeW50aGV0aWMgdGFyZ2V0IHRlc3QiLCJyZWNvcmRlZEF0IjoiMjAyNi0wMS0wMVQwMDowMDowMFoiLCJjb29yZGluYXRlcyI6InVucmVhbC1jZW50aW1ldGVycyIsIm5vbWluYWxIeiI6NjAsIm1hcE5hbWUiOiJNYXBfQSIsIm1hcFNjYWxlIjoxLCJzdGFydEV2ZW50IjoibmF0aXZlIiwicmVhc29uIjoiY29tcGxldGVkIiwiZnJhbWVzIjoxNzIsImlucHV0RXZlbnRzIjoyMDUyLCJzY29yZSI6MzIxLjUsImR1cmF0aW9uIjoyLjk5ODUsImVuY29kaW5nIjp7ImtleWZyYW1lcyI6NiwicXVhbnR1bSI6MC4wNywieWF3UGVyVW5pdCI6MC4xMTQ1ODU5OTksInBpdGNoUGVyVW5pdCI6LTAuMTE0NTg2LCJrZXlmcmFtZUVycm9yTWF4Ijo3LjM3NGUtMTAsImtleWZyYW1lRXJyb3JSbXMiOjUuMTI3ZS0xMH19BQAAAM0gAAAKUeXAGADoBc0gAAAAAAAAzSAAAAAAAABMAQAAC0qy1doDBybVsUyvB3hxLFGmoQSQ1H8ePNlBXUa5KZq7gkedYSami6iPAAAAmhhwZs9VAKYP7E/AOjgQqHIDBzbYAQeYDeAHIIDV0mENsYgH4gAWRascnCQCYDoW9EMfUF/4BJAhB0oxhojIB+IBjNAygC7wTo/WkAEEdG9CAwEB187A88LHwNvXDW7ZAAaqTu9h9s8irjSBB8QvTMAqYBmAgdJTABjA4NfqSVBeRf30Vgk+CDBv63vSJAgQMKgGEAgKrCsDrgSBAIOAgwECBIECgQCFAcs2ASugwcABgQBCQYDAgAKBlRAQMGBAcCCAEFBQESgIDBAEJFj9DAQOAhAGCggEBBQGAggGEuyZ4dUk2GfS3PCJBBNuUrYAxZsGru0Z4tXUZ9LEdhaPBOhtXsKNwxcQoPUEMKsKAACAIPyJXS8EqQAGODIUCbA=";
    static void Replays(string root)
    {
        long now = 30_000_000;
        var bytes = Convert.FromBase64String(SyntheticReplay);
        string Out(string name) { var o = Path.Combine(root, "swap", name); Directory.CreateDirectory(o); return o; }
        var sender = new ReplaySwap(Out("a"), () => now); var receiver = new ReplaySwap(Out("b"), () => now);
        File.WriteAllBytes(Path.Combine(Out("a"), "replays.tmp"), []);
        Directory.CreateDirectory(Path.Combine(Out("a"), "replays"));
        File.WriteAllBytes(Path.Combine(Out("a"), "replays", "1790000000-42-2.amreplay"), bytes);
        File.WriteAllBytes(Path.Combine(Out("a"), "replays", "1790000000-42-3.amreplay.partial"), bytes);
        Check(sender.ReadOwn("1790000000-42-2")!.SequenceEqual(bytes) && sender.ReadOwn("1790000000-42-3") is null && sender.ReadOwn("../x") is null, "Only finished replays are read, by safe id");
        sender.Offer("peer-b", "m1", 1, "peer-a", "1790000000-42-2", "round", bytes);
        (byte[] Bytes, ReplaySwap.Shared Info)? done = null;
        for (var i = 0; i < 10 && done is null; i++)
            sender.Pump(false, (_, body) =>
            {
                var b = JsonSerializer.SerializeToElement(body, Protocol.Json);
                done ??= receiver.Chunk(b.GetProperty("match").GetString()!, b.GetProperty("round").GetInt32(), b.GetProperty("owner").GetString()!, b.GetProperty("id").GetString()!, b.GetProperty("kind").GetString()!, null,
                    b.GetProperty("size").GetInt32(), b.GetProperty("hash").GetString()!, b.GetProperty("offset").GetInt32(), Convert.FromBase64String(b.GetProperty("data").GetString()!));
            });
        Check(done is not null && receiver.Import(done.Value.Bytes, done.Value.Info) == "1790000000-42-2" && File.Exists(Path.Combine(Out("b"), "replays", "1790000000-42-2.amreplay")), "A round replay arrives, is verified and imported");
        Check(receiver.Received.Single().Owner == "peer-a" && receiver.Received.Single().Round == 1, "The import is remembered for run vs run");
        var tampered = (byte[])bytes.Clone(); tampered[^1] ^= 0xFF;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        Check(receiver.Chunk("m1", 2, "peer-a", "1790000000-42-9", "round", null, tampered.Length, hash, 0, tampered.Take(ReplaySwap.ChunkBytes).ToArray()) is null || tampered.Length > ReplaySwap.ChunkBytes, "A replay whose bytes don't match its hash is dropped");
        Check(receiver.Chunk("m1", 1, "peer-a", "../evil", "round", null, 100, hash, 0, new byte[10]) is null && receiver.Chunk("m1", 1, "peer-a", "x", "script", null, 100, hash, 0, new byte[10]) is null, "Bad ids and kinds are refused");
    }

    static void Transfers(string root)
    {
        // Rules: only bare names with allowed extensions in allowed folders.
        foreach (var bad in new[] { "../evil.sce", "..\\evil.sce", "sub/evil.sce", "C:evil.sce", "evil.exe", ".sce", "CON.sce", "evil.sce ", "a..b.sce" })
            Check(!ContentRules.SafeName("scenario", bad), "Unsafe name refused: " + bad);
        Check(ContentRules.SafeName("scenario", "Synthetic A.sce") && ContentRules.SafeName("ability", "CS Walk.abilsprint") && ContentRules.SafeName("map", "synthetic_port.json") && !ContentRules.SafeName("map", "x.sce"), "Allowed names and extensions per folder");
        var h = new string('a', 64);
        Check(!ContentRules.Valid(new ContentManifest("k", [new ContentFile("scenario", "../evil.sce", 10, h, 10)], null)), "A manifest with path traversal is refused");
        Check(!ContentRules.Valid(new ContentManifest("k", [new ContentFile("scenario", "big.sce", ContentRules.MaxFile + 1, h, 10)], null)), "Oversized files are refused");
        Check(!ContentRules.Valid(new ContentManifest("k", [new ContentFile("script", "x.lua", 1, h, 1)], null)), "Unknown kinds are refused");

        long now = 20_000_000;
        var net = new MemoryNetwork();
        var all = new List<MultiplayerService>();
        string Game(string name) => Path.Combine(root, "xfer", name, "game");
        // Host library: a scenario with a ported map and an ability file; the joiner has nothing.
        var hostGame = Game("host");
        var big = string.Join("\n", Enumerable.Range(0, 4000).Select(i => "{\"brush\":" + i + ",\"v\":\"" + Convert.ToHexString(BitConverter.GetBytes(i * 2654435761u)) + "\"}"));
        WriteText(Path.Combine(hostGame, "maps", "synthetic_port.json"), big);
        WriteText(Path.Combine(hostGame, "Saved", "SaveGames", "Abilities", "Synthetic Walk.abilsprint"), "Name=Synthetic Walk\n");
        WriteText(Path.Combine(hostGame, "Saved", "SaveGames", "Scenarios", "Synthetic Port.sce"), "Name=Synthetic Port\nMapName=synthetic_port.json\nTimelimit=30\n\n[Character Profile]\nName=Player\nAbilityProfileNames=Synthetic Walk;;;\n\n[Map Data]\n{}\n");
        Directory.CreateDirectory(Path.Combine(Game("join"), "Saved", "SaveGames"));
        MultiplayerService Make(string id, string game, out MemoryTransport t, LocalRun? live = null)
        {
            t = new MemoryTransport(net, id); net.Peers[id] = t;
            var output = Path.Combine(root, "xfer", id, "out"); Directory.CreateDirectory(output);
            var service = new MultiplayerService(t, new ContentLibrary(game, () => new DateTime(2026, 1, 1).AddMilliseconds(now)), new NoGameControl(), () => live ?? new LocalRun(false, null, null, null, null, 0, 0, 0, null), () => [], () => null, output, false, () => now, autoTick: false);
            all.Add(service); return service;
        }
        void Pump(int rounds = 10) { for (var i = 0; i < rounds; i++) { now += 100; foreach (var s in all) s.Tick(); } }
        JsonElement View(MultiplayerService s) => JsonSerializer.SerializeToElement(s.View(), Protocol.Json);
        var host = Make("xfer-host", hostGame, out var hostLink);
        var joiner = Make("xfer-join", Game("join"), out var joinLink);
        host.Act("create", J(new { mode = "practice", scenario = "Synthetic Port" }));
        joiner.Act("join", J(new { code = net.Codes.Single().Key }));
        Pump();
        var offer = View(joiner).GetProperty("lobby").GetProperty("download").GetProperty("view");
        Check(offer.GetProperty("state").GetString() == "ready" && offer.GetProperty("files").GetArrayLength() == 3 && offer.GetProperty("total").GetInt64() > big.Length, "A missing player is offered the scenario, map and ability with sizes");
        Check(View(joiner).GetProperty("lobby").GetProperty("blockers").EnumerateArray().Any(b => b.GetProperty("text").GetString()!.Contains("scenario")), "Missing content still blocks the start");
        // Frame path, interrupted by leaving the page alone: cancel, then retry resumes.
        Check(joiner.Act("download", default).Ok, "Download starts");
        Pump(2);
        joiner.Act("download-cancel", default);
        Pump(3);
        Check(View(joiner).GetProperty("lobby").GetProperty("download").GetProperty("view").GetProperty("state").GetString() == "cancelled", "Download can be cancelled");
        var partial = View(joiner).GetProperty("lobby").GetProperty("download").GetProperty("view").GetProperty("done").GetInt64();
        // Retry over the bulk lane, with the link dropping part-way: it resumes from what is on disk.
        hostLink.BulkChunkBytes = joinLink.BulkChunkBytes = 1024; hostLink.DropAfter = 2;
        Check(joiner.Act("download-retry", default).Ok, "Retry continues the download");
        for (var i = 0; i < 40 && View(joiner).GetProperty("lobby").GetProperty("content").GetProperty("scenario").GetString() != "ok"; i++) Pump(5);
        var installed = Path.Combine(Game("join"), "maps", "synthetic_port.json");
        Check(File.Exists(installed) && File.ReadAllText(installed) == big && File.Exists(Path.Combine(Game("join"), "Saved", "SaveGames", "Abilities", "Synthetic Walk.abilsprint")), "Files are verified and installed into the game folders");
        Check(hostLink.BulkLog.Select(b => b.Transfer).Distinct().Count() >= 2 && hostLink.BulkLog.Count(b => b.Index == 0) >= 2, "A dropped bulk transfer resumes in a new transfer");
        Check(partial > 0 && hostLink.BulkSent < offer.GetProperty("packed").GetInt64() * 2, "Resuming doesn't start over");
        Check(hostLink.Cancelled.Any(c => c.Reason == "complete"), "Completed transfers are closed as complete");
        Check(View(joiner).GetProperty("lobby").GetProperty("content").GetProperty("scenario").GetString() == "ok" && View(host).GetProperty("lobby").GetProperty("members").EnumerateArray().First(m => m.GetProperty("id").GetString() == "xfer-join").GetProperty("map").GetString() == "ok", "The member's state flips to has content");
        Pump(5);
        Check(View(host).GetProperty("lobby").GetProperty("members").EnumerateArray().First(m => m.GetProperty("id").GetString() == "xfer-join").GetProperty("ready").GetBoolean(), "The joiner readies up once the content is there (default preference)");
        // Preferences are checked and saved.
        Check(joiner.Act("prefs", J(new { prefs = new { volume = 5, hotkey = "f9", readyOnJoin = true, unknown = 1 } })).Ok && View(joiner).GetProperty("prefs").GetProperty("volume").GetDouble() == 1 && View(joiner).GetProperty("hotkey").GetString() == "F9", "Preferences are clamped and applied");
        Check(MultiplayerPrefs.Load(Path.Combine(root, "xfer", "xfer-join", "out", "multiplayer-settings.json")).ReadyOnJoin, "Preferences persist");
        var clipFile = Path.Combine(root, "xfer", "xfer-join", "out", "clip-settings.tsv");
        File.WriteAllText(clipFile, "AIMMOD_CLIPS_1\nkey\tF8\nbefore\t12\nafter\t3\n");
        Check(joiner.Act("prefs", J(new { prefs = new { clipKey = "F9" } })).Code == "conflict" && joiner.Act("prefs", J(new { prefs = new { clipKey = "Tab" } })).Code == "invalid", "The clip key can't clash with the lobby key and must be an offered key");
        Check(joiner.Act("prefs", J(new { prefs = new { clipKey = "F10" } })).Ok && File.ReadAllText(clipFile) == "AIMMOD_CLIPS_1\nkey\tF10\nbefore\t12\nafter\t3\n", "The clip key is saved in clip-settings.tsv, keeping its other values");
        Check(KeyBinds.Conflicts("F7", "F7", new HashSet<string>()).Count == 1 && KeyBinds.Conflicts("F7", "F8", new HashSet<string>(["F8"])).Single().Contains("KovaaK’s already uses F8"), "Key conflicts are reported");
        // A crashed or restarted client rejoins its lobby.
        Check(File.Exists(Path.Combine(root, "xfer", "xfer-join", "out", "multiplayer-session.json")), "The client remembers its lobby");
        all.Remove(joiner);
        var reborn = Make("xfer-join", Game("join"), out _);
        Pump(5);
        Check(View(reborn).GetProperty("lobby").ValueKind == JsonValueKind.Object && View(host).GetProperty("lobby").GetProperty("members").GetArrayLength() == 2, "After a restart the client rejoins the same lobby");
        // Spectating without the scenario: the watched player sends their current scenario's files.
        var watched = Make("xfer-watched", hostGame, out var watchedLink, new LocalRun(true, "Synthetic Port", 10, 5, 25, 3, 2, 2, "a1"));
        var viewer = Make("xfer-viewer", Game("viewer"), out var viewerLink);
        Directory.CreateDirectory(Path.Combine(Game("viewer"), "Saved", "SaveGames"));
        viewerLink.AllowSpectate = true;
        viewerLink.FriendList = [new("xfer-watched", "Watched Host", "aimmod", "Playing Synthetic Port", null, false, Spectatable: true, Scenario: "Synthetic Port")];
        Check(viewer.Act("watch", J(new { friend = "xfer-watched" })).Ok, "Spectate a friend");
        viewerLink.Inbox.Enqueue(new TransportEvent("xfer-watched", TransportEvent.SpectateStarted, Reason: "Watched Host"));
        watchedLink.Inbox.Enqueue(new TransportEvent("xfer-viewer", TransportEvent.SpectatorJoined, Reason: "Viewer"));
        Pump(2);
        Check(View(viewer).GetProperty("watch").GetProperty("state").GetString() == "missing", "A spectator without the scenario is offered the download");
        Check(viewer.Act("watch-download", default).Ok, "Download from the friend (no Workshop item)");
        for (var i = 0; i < 30 && View(viewer).GetProperty("watch").GetProperty("state").GetString() is "downloading" or "missing"; i++) Pump(3);
        Check(File.Exists(Path.Combine(Game("viewer"), "Saved", "SaveGames", "Scenarios", "Synthetic Port.sce")) && File.ReadAllText(Path.Combine(Game("viewer"), "maps", "synthetic_port.json")) == big, "The friend's scenario and map arrive verified over the spectate link");
        Check(View(viewer).GetProperty("watch").GetProperty("state").GetString() == "manual", "With the content in place, watching continues");
        var stranger = Make("xfer-stranger", Game("viewer"), out _);
        net.Peers["xfer-watched"].Inbox.Enqueue(new TransportEvent("xfer-stranger", TransportEvent.Message, Protocol.Encode(Protocol.Create("content.request", "", "xfer-stranger", 1, now, new { }))));
        Pump(2);
        Check(View(stranger).GetProperty("watch").ValueKind == JsonValueKind.Null, "Only people watching you can ask for your files");
        // The host only serves current lobby content.
        var server = new ContentServer(new ContentLibrary(hostGame), () => now);
        var settings = new LobbySettings(Scenario: new ContentLibrary(hostGame).Scenario("Synthetic Port"));
        Check(server.Request("x", settings, new string('b', 64), 0, 100) == "not-offered", "Files outside the lobby content are never served");

        // Receiver checks, against a packed file the host would send.
        var raw = System.Text.Encoding.UTF8.GetBytes("Name=Synthetic Port\n");
        var packed = ContentRules.Pack(raw);
        string Hash(byte[] b) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)).ToLowerInvariant();
        ContentDownload Fresh(string name) { var g = Path.Combine(root, "xfer", name, "game"); Directory.CreateDirectory(g); return new ContentDownload(g, Path.Combine(root, "xfer", name, "tmp"), () => now); }
        var wrong = Fresh("mismatch");
        wrong.Offer(new ContentManifest("k1", [new ContentFile("scenario", "Synthetic Port.sce", raw.Length, new string('c', 64), packed.Length)], null));
        wrong.Start(); wrong.Chunk(new string('c', 64), 0, packed.Length, packed);
        Check(wrong.State == "error" && wrong.Code == "hash" && !File.Exists(Path.Combine(root, "xfer", "mismatch", "game", "Saved", "SaveGames", "Scenarios", "Synthetic Port.sce")), "A hash mismatch is discarded, never installed");
        var traversal = Fresh("traversal");
        traversal.Offer(new ContentManifest("k2", [new ContentFile("scenario", "..\\..\\evil.sce", raw.Length, Hash(raw), packed.Length)], null));
        Check(traversal.State == "error" && traversal.Code == "invalid", "Path traversal in a manifest is refused before anything is written");
        var taken = Fresh("conflict");
        WriteText(Path.Combine(root, "xfer", "conflict", "game", "Saved", "SaveGames", "Scenarios", "Synthetic Port.sce"), "my own version");
        taken.Offer(new ContentManifest("k3", [new ContentFile("scenario", "Synthetic Port.sce", raw.Length, Hash(raw), packed.Length)], null));
        Check(!taken.Start() && taken.Code == "conflict" && File.ReadAllText(Path.Combine(root, "xfer", "conflict", "game", "Saved", "SaveGames", "Scenarios", "Synthetic Port.sce")) == "my own version", "An existing file with different content is never overwritten");
        var full = Fresh("disk"); full.FreeSpace = () => 10;
        full.Offer(new ContentManifest("k4", [new ContentFile("scenario", "Synthetic Port.sce", raw.Length, Hash(raw), packed.Length)], null));
        Check(!full.Start() && full.Code == "disk" && full.Error!.Contains("disk space"), "Not enough disk space is reported");
        var gone = Fresh("gone");
        gone.Offer(new ContentManifest("k5", [new ContentFile("scenario", "Synthetic Port.sce", raw.Length, Hash(raw), packed.Length)], null));
        gone.Start(); gone.HostGone();
        Check(gone.Code == "host-left" && gone.Error!.Contains("host left"), "The host leaving mid-transfer is explained");
    }

    sealed class FakeGame(params string[] caps) : IGameControl
    {
        public readonly List<string> Calls = [];
        public IReadOnlySet<string> Capabilities { get; } = caps.ToHashSet();
        long lastLoad, lastStart;
        public long? Load(string scenario) { Calls.Add("load " + scenario); return lastLoad = Calls.Count; }
        public long? Start(string scenario, string mode) { Calls.Add("start " + mode + " " + scenario); return lastStart = Calls.Count; }
        public long? Refresh() { Calls.Add("refresh"); return Calls.Count; }
        // Answers like AimModCore: the latest load is done, then the latest start.
        public GameCommandResult? Result => lastStart > lastLoad ? new GameCommandResult(lastStart, "done", "started", "") : lastLoad > 0 ? new GameCommandResult(lastLoad, "done", "loaded", "") : null;
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
        // Setups: save one, and the next lobby starts from the last setup.
        Check(service.Act("preset-save", J(new { name = "Synthetic setup" })).Ok && View().GetProperty("presets").EnumerateArray().Any(p => p.GetString() == "Synthetic setup"), "The host saves a setup");
        Check(service.Act("preset-load", J(new { name = "Synthetic setup" })).Ok && !service.Act("preset-load", J(new { name = "Nope" })).Ok, "Saved setups load; missing ones are refused");
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
        // Launched from a Steam invite: join straight away and ask for the panel.
        service.Act("leave", default);
        service.Act("sim", J(new { op = "launch" }));
        Run(200);
        Check(View().GetProperty("lobby").ValueKind == JsonValueKind.Object && View().GetProperty("invites").GetArrayLength() == 0 && File.ReadAllText(Path.Combine(output, "open-workspace.request")) == "multiplayer", "A Steam launch join is automatic and opens the Multiplayer page");
        // An unasked invite waits in a popup; F7 joins it.
        service.Act("leave", default);
        service.Act("sim", J(new { op = "invite" }));
        Run(200);
        var invite = View().GetProperty("invites")[0];
        Check(invite.GetProperty("kind").GetString() == "incoming" && service.Notice() is { Kind: "invite", Invite: not null } n && n.Title.Contains("invited you to a duel"), "An incoming invite shows a popup naming the mode and scenario");
        Check(File.ReadAllText(Path.Combine(output, "multiplayer-notify.json")).Contains("\"interactive\":true"), "The notice file tells the game layer the popup is clickable");
        service.Hotkey();
        Run(200);
        Check(View().GetProperty("lobby").ValueKind == JsonValueKind.Object && View().GetProperty("invites").GetArrayLength() == 0, "The hotkey joins the invite");
        // The host asks everyone to ready up; members see it outside the panel and F7 readies them.
        service.Act("sim", J(new { op = "add" }));
        Run(300);
        Check(MultiplayerHotkey.Parse("F9") == (0x78, "F9") && MultiplayerHotkey.Parse("Q") == (0x76, "F7") && MultiplayerHotkey.Parse("F13") == (0x76, "F7"), "The hotkey is F1 to F12, F7 by default");
        Check(MultiplayerService.SimulationRequested(["--multiplayer-sim"], output), "The simulation can be requested explicitly");
        File.WriteAllText(Path.Combine(output, "multiplayer-dev.json"), "{\"simulation\":true}");
        Check(MultiplayerService.SimulationRequested([], output), "The developer setting turns the simulation on");
        service.Dispose();
    }
}

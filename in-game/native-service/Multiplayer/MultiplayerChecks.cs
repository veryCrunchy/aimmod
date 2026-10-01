using System.Text;
using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

// Synthetic identities and content only; everything runs in temporary folders.
static partial class MultiplayerChecks
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
        TrackingDuel();
        CombatModes();
        TeamsAndSpawns();
        CsMode();
        ProtocolFrames();
        Peers();
        SteamPipe();
        Follow();
        DevAvatarChecks();
        Marker();
        var root = Path.Combine(Path.GetTempPath(), "aimmod-mp-test-" + Guid.NewGuid().ToString("N"));
        try { Content(root); Generator(root); Blocked(root); AutoLeave(root); Service(root); Transfers(root); Replays(root); Maps(root); Tournaments(root); }
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
        // Host migration while the last round's results show: the match is decided, no extra round.
        (core, clock, advance) = Lobby();
        core.Join("p2", "Two"); core.Join("p3", "Three");
        core.Apply("host", "settings", Patch(new { mode = "score-race", rounds = 2, countdown = 3 }), content);
        ReadyAll(core); core.Apply("host", "start", default, content);
        for (var rn = 1; rn <= 2; rn++)
        {
            if (rn > 1) { advance(LobbyCore.ResultsMs); core.Tick(); }
            advance(3001); core.Tick();
            var rm = core.Snapshot().Match!;
            advance(60_000);
            foreach (var id in new[] { "host", "p2", "p3" }) core.Finish(id, new RunFinish(rm.Id, rn, 900 - rn, 60, 10, 5, 5, null));
        }
        var decided = core.Snapshot();
        Check(decided.Match is { Phase: MatchPhases.Round, Round: 2 }, "The deciding round shows its results");
        var successor = LobbyCore.Restore(decided, "p2", clock);
        successor.Join("p3", "Three");
        advance(LobbyCore.ResultsMs); successor.Tick();
        Check(successor.Snapshot().Match is { Phase: MatchPhases.Final, Round: 2 }, "A host who takes over during the last results ends the match instead of starting an extra round");

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

    // Tracking duel (game-modes.md 6.3): geometry, host scoring with the rewind cap, rounds and the arena scenario.
    static void TrackingDuel()
    {
        var content = new FakeContent();
        // Geometry: a level ray through a capsule 10 m ahead, above it, behind the eye.
        var (fx, fy, fz) = TrackGeometry.Direction(0, 0);
        Check(TrackGeometry.HitsCapsule(0, 0, 100, fx, fy, fz, 100_000, 1000, 0, 100, 45, 115), "A ray through the capsule hits");
        Check(!TrackGeometry.HitsCapsule(0, 0, 300, fx, fy, fz, 100_000, 1000, 0, 100, 45, 115), "A ray over the head misses");
        Check(!TrackGeometry.HitsCapsule(0, 0, 100, fx, fy, fz, 100_000, -1000, 0, 100, 45, 115), "A target behind the camera is never hit");
        var (ux, uy, uz) = TrackGeometry.Direction(30, 90);
        Check(Math.Abs(ux) < 1e-9 && Math.Abs(uy - Math.Cos(Math.PI / 6)) < 1e-9 && Math.Abs(uz - 0.5) < 1e-9, "Pitch and yaw follow Unreal (yaw 90 = +Y, pitch up = +Z)");
        Check(TrackGeometry.HitsCapsule(0, 0, 100, 0, 0.6, 0.8, 100_000, 0, 600, 900, 45, 115) && !TrackGeometry.HitsCapsule(0, 0, 100, 0, 0.6, 0.8, 100_000, 0, 600, 1100, 45, 115), "Capsule ends are rounded");

        // Settings: one against one, short simultaneous rounds, best of three by default.
        var start = new LobbySettings(Scenario: content.Scenario("Synthetic A"));
        Check(LobbyRules.Apply(start, J(new { mode = "tracking-duel" }), 3, content).Result.Code == "duel-players", "Tracking duel with three players is refused");
        Check(LobbyRules.Apply(start, J(new { mode = "tracking-duel" }), 2, content).Settings! is { Rounds: 3, RequireFire: false }, "Tracking duel defaults: three rounds, fire not required");
        var duel = LobbyRules.Apply(start, J(new { mode = "tracking-duel", maxPlayers = 6, rounds = 12 }), 2, content).Settings!;
        Check(duel.MaxPlayers == 2 && duel.Rounds == 9 && duel.TimeLimit == 10 && duel.EffectiveTimeLimit == 10 && duel.TotalRounds == 9, "Tracking duel: two players, 10 s rounds by default, up to nine rounds");
        Check(LobbyRules.Apply(duel, J(new { timeLimit = 60 }), 2, content).Settings!.TimeLimit == 60 && LobbyRules.Apply(duel, J(new { timeLimit = 300 }), 2, content).Settings!.TimeLimit == 60, "Round length is its own (even the scenario's 60 s) and at most a minute");
        Check(LobbyRules.Apply(duel, J(new { requireFire = true }), 2, content).Settings!.RequireFire && !LobbyRules.Apply(duel with { RequireFire = true }, J(new { mode = "practice" }), 2, content).Settings!.RequireFire, "Require fire is a tracking-duel option");
        Check(MatchScenario.Needed(duel) && MatchScenario.Name(duel).Contains(" - Tracking duel - ", StringComparison.Ordinal) && MatchScenario.Key(duel) != MatchScenario.Key(duel with { Mode = LobbyModes.Practice }), "A tracking duel always plays its own generated arena");

        // Scoring, one direction: "d" strafes 10 m in front of "a" (eye 64 cm above the capsule
        // centre); a's game draws it 120 ms late, and a aims exactly at what it sees.
        const long t0 = 2_000_000; const long length = 10_000;
        static (double X, double Y, double Z) Body(long t) => (1000, 300 * Math.Sin((t - t0) / 1000.0 * 2 * Math.PI), 100);
        TrackSample Eye(long t) { var b = Body(t); return new TrackSample(t, b.X, b.Y, b.Z + 64, 0, 0); }
        TrackSample Aim(long t, (double X, double Y, double Z) at, bool fire = false) => new(t, 0, 0, 164, Math.Atan2(at.Z - 164, Math.Sqrt(at.X * at.X + at.Y * at.Y)) * 180 / Math.PI, Math.Atan2(at.Y, at.X) * 180 / Math.PI, fire);
        TrackingRound Play(long seenLag, long aimLag, Func<long, (double, double, double)>? claimed = null, bool evidence = true, long attackUntil = length, bool requireFire = false, string? tag = null,
            double aimOffset = 0, double seenRadius = 45, double seenHalf = 115)
        {
            var round = new TrackingRound("a", "d", t0, t0 + length, requireFire);
            for (long t = t0; t < t0 + length; t += 17)
            {
                var dodge = new List<TrackSample> { Eye(t) };
                var aim = t < t0 + attackUntil ? new List<TrackSample> { Aim(t, Body(t - aimLag) is var at ? (at.X, at.Y + aimOffset, at.Z) : default, fire: (t - t0) % 1000 < 500) } : [];
                var seen = new List<TrackSeen>();
                if (evidence && (t - t0) % 34 == 0) { var c = (claimed ?? (x => Body(x - seenLag)))(t); seen.Add(new TrackSeen(t, 7, c.Item1, c.Item2, c.Item3, seenRadius, seenHalf, tag)); seen.Add(new TrackSeen(t, 3, -500, -500, 100, 45, 115)); }
                round.Add("d", new TrackBatch("m", 1, dodge, []));
                round.Add("a", new TrackBatch("m", 1, aim, seen));
            }
            return round;
        }
        var fairRound = Play(120, 120);
        var fair = fairRound.ScoreFor("a", t0 + length, 40, 60);
        Check(fair.Percent > 97 && !fair.Disputed && Math.Abs(fair.LagMs - 120) <= 10 && fair.SeenRejected < fair.SeenRows / 10, "Tracking what the game drew 120 ms late scores fully, and the lag is measured");
        var both = fairRound.Compute(t0 + length, id => id == "a" ? 40 : 60);
        Check(both.First.Percent == fair.Percent && both.Second.Percent == 0, "Both players are scored at once; one who never looks at the other scores nothing");
        var blind = Play(0, 100, evidence: false).ScoreFor("a", t0 + length, 0, 0);
        Check(blind.Percent > 97 && blind.SeenRows == 0 && blind.LagMs == 100, "Without drawn hulls the host rewinds the target by the estimated lag (100 ms + half the round trips)");
        var lie = Play(0, 0, claimed: t => { var b = Body(t); return (b.X + 250, b.Y, b.Z); }).ScoreFor("a", t0 + length, 40, 40);
        Check(lie.Disputed && lie.Reason == "seen-mismatch", "Drawn hulls that don't match the target's own track are rejected and dispute the round");
        var late = Play(350, 350).ScoreFor("a", t0 + length, 40, 40);
        var huge = Play(120, 120, aimOffset: 300, seenRadius: 1000, seenHalf: 2000).ScoreFor("a", t0 + length, 40, 60);
        Check(huge.Percent < 5, "Drawn hulls far larger than the avatar's don't count aim 3 m beside the target as on target");
        Check(late.Disputed && late.LagMs == TrackingRound.RewindCapMs && late.Percent < 60, "The rewind is capped at 200 ms: aiming at a target older than that doesn't count");
        var partial = Play(120, 120, attackUntil: length / 2).ScoreFor("a", t0 + length, 40, 40);
        Check(partial.Disputed && partial.Reason == "coverage" && partial.Percent is > 45 and < 55, "A stream covering half the round scores half and is disputed");
        var midway = Play(120, 120).ScoreFor("a", t0 + length / 2, 40, 40);
        Check(midway.Percent is > 45 and < 55 && !midway.Disputed && midway.Coverage > 0.95, "Live scores cover the round so far");
        var fired = Play(120, 120, requireFire: true).ScoreFor("a", t0 + length, 40, 40);
        Check(fired.Percent is > 45 and < 55 && fired.Coverage > 0.95, "Require fire: time on target counts only while firing");
        var tagged = Play(120, 120, tag: "d").ScoreFor("a", t0 + length, 40, 40);
        var otherTag = Play(120, 120, tag: "someone-else").ScoreFor("a", t0 + length, 40, 40);
        Check(tagged.Percent > 97 && !tagged.Disputed && otherTag.Disputed, "Drawn rows tagged as the opponent's avatar are used; rows tagged as someone else are ignored");

        // Wire format.
        var batch = new TrackBatch("m-1", 2, [new TrackSample(t0, 1.234, 2, 3, -10, 370, true)], [new TrackSeen(t0, 5, 4, 5, 6, 40, 90, "p2")]);
        var read = TrackBatch.Read(J(batch.Body()));
        Check(read is { MatchId: "m-1", Round: 2 } && read.Samples[0].X == 1.23 && read.Samples[0].Yaw == 370 && read.Samples[0].Fire && read.Seen[0].Id == 5 && read.Seen[0].HalfHeight == 90 && read.Seen[0].Member == "p2", "Track batches round-trip with the fire flag and avatar tags");
        Check(TrackBatch.Read(J(new { match = "m", round = 1, s = new[] { new double[] { 1, 0, 0, 0, 95, 0 } } })) is null && TrackBatch.Read(J(new { match = "m", round = 1, s = new[] { new double[] { 1, 0, 0 } } })) is null
            && TrackBatch.Read(J(new { match = "m", round = 1, s = Array.Empty<double[]>(), v = new[] { new double[] { 1, 0, 0, 0, 0, 40, 90 } } })) is null
            && TrackBatch.Read(J(new { match = "m", round = 1, s = new[] { new double[] { 1, 0, 0, 0, 0, 0, 2 } } })) is null, "Impossible pitch, short rows, bad target ids and fire flags are refused");
        Check(TrackBatch.Read(J(new { match = "m", round = 1, s = new[] { new double[] { 1, 0, 0, 0, 0, 0 } } })) is { } old && !old.Samples[0].Fire, "Six-value samples from older clients still read (not firing)");

        // Client side: self-pose.tsv rows become samples on the host clock; target rows belong to the newest pose;
        // the fire row marks that publication's poses, tag rows name the avatar's player.
        Check(StreamIds.For("76561198000000000").Length == 18 && StreamIds.For("76561198000000000").StartsWith("s-", StringComparison.Ordinal) && StreamIds.For("1") != StreamIds.For("2"), "Stream ids follow the bridge's s-<16 hex>");
        var tracker = new SelfPoseTracker(Path.GetTempPath());
        tracker.Take(LivePoseFrame.Parse("AIMMOD_POSE_1\t4\nmeta\tx\ty\t1\npose\t1000\t1\t2\t3\t-5\t90\t0\t100\npose\t1016\t1\t2\t3\t-5\t91\t0\t100\ntarget\t9\t50\t60\t70\t40\t90\ntag\t9\t" + StreamIds.For("p2") + "\nfire\t1016\t12\t1\n"), 500, ["host", "p2"]);
        tracker.Take(LivePoseFrame.Parse("AIMMOD_POSE_1\t4\npose\t1032\t0\t0\t0\t0\t0\t0\t100\n"), 500);
        var drained = tracker.Drain("m", 1).ToArray();
        Check(drained.Length == 1 && drained[0].Samples.Count == 2 && drained[0].Samples[0].T == 1500 && drained[0].Samples[1].Yaw == 91 && drained[0].Samples.All(x => x.Fire)
            && drained[0].Seen.Single() is { T: 1516, Id: 9, Radius: 40, Member: "p2" }, "Self-pose rows become host-clock samples with fire and avatar tags; a repeated file sequence is skipped");

        // Rounds: both track at once, the higher share takes the round, native score frames are refused.
        var (core, clock, advance) = Lobby();
        core.Join("p2", "Two");
        core.Apply("host", "settings", Patch(new { mode = "tracking-duel", rounds = 2, countdown = 3 }), content);
        ReadyAll(core);
        Check(core.Apply("host", "start", default, content).Ok, "Tracking duel starts with two ready players");
        void Duel(double hostYaw, double twoYaw)
        {
            advance(3000); core.Tick();
            var m = core.Snapshot().Match!;
            Check(m.Phase == MatchPhases.Live && m.Live.All(l => l.Status == LineStates.Waiting) && core.Score("host", new ScoreFrame(m.Id, m.Round, 1, 1, 1, 1, 0, 9)).Code == "tracking", "Round goes live for both players and KovaaK's own score isn't used");
            var begin = clock();
            for (long t = 0; t < 10_000; t += 100)
            {
                var hostView = new List<TrackSample>(); var twoView = new List<TrackSample>();
                // host at the origin, p2 10 m along +X; yaw 0 / 180 look straight at each other.
                for (long k = 0; k < 100; k += 17) { var at = begin + t + k; hostView.Add(new TrackSample(at, 0, 0, 164, 0, hostYaw)); twoView.Add(new TrackSample(at, 1000, 0, 164, 0, twoYaw)); }
                core.Track("host", new TrackBatch(m.Id, m.Round, hostView, []));
                core.Track("p2", new TrackBatch(m.Id, m.Round, twoView, []));
                if (t == 0) Check(core.Track("nobody", new TrackBatch(m.Id, m.Round, hostView, [])).Code == "not-playing", "Only the round's players stream");
                advance(100); core.Tick();
            }
            Check(core.Snapshot().Match!.Tracking is { Count: 2 } live && live.All(v => v.Member is "host" or "p2"), "The HUD gets both players' live time on target");
            advance(LobbyCore.TrackGraceMs); core.Tick();
        }
        // p2 looks 5 degrees past the host: at 10 m that misses by 87 cm.
        Duel(0, 175);
        var r1 = core.Snapshot().Match!.Rounds[0];
        Check(r1.Results.All(r => r.Score is not null) && r1.WinnerId == "host" && r1.Results.First(r => r.MemberId == "host").Score > 95 && r1.Results.First(r => r.MemberId == "p2").Score < 5, "Round 1: both players are scored and the higher share takes the round");
        advance(LobbyCore.ResultsMs); core.Tick();
        Duel(0, 175);
        advance(LobbyCore.ResultsMs); core.Tick();
        var final = core.Snapshot().Match!;
        Check(final.Phase == MatchPhases.Final && final.WinnerId == r1.WinnerId && final.Standings[0].Wins == 2, "Most rounds won takes the duel");

        // A player leaving ends the round.
        (core, clock, advance) = Lobby();
        core.Join("p2", "Two");
        core.Apply("host", "settings", Patch(new { mode = "tracking-duel", countdown = 3 }), content);
        ReadyAll(core); core.Apply("host", "start", default, content);
        advance(3000); core.Tick(); advance(2000); core.Tick();
        core.Leave("p2");
        Check(core.Snapshot().Match!.Phase == MatchPhases.Round, "A player leaving ends the round");
        advance(LobbyCore.ResultsMs); core.Tick();
        var gone = core.Snapshot().Match!;
        Check(gone.Phase == MatchPhases.Final && gone.Rounds[0].Results.First(r => r.MemberId == "host").Score is null && gone.Rounds[0].WinnerId is null, "A player leaving ends the round unscored and the duel");

        // Arena scenario: no targets, nobody hurt, nothing scored natively, one hidden helper bot.
        var arena = MatchScenario.Generate(new(BaseScenario, duel with { Scenario = new ScenarioChoice("Synthetic A", ContentLibrary.TextHash(BaseScenario), "synthetic_map", ContentLibrary.TextHash("m"), 60) }));
        Check(arena.Contains("AddedBots=AimMod Hidden Bot.bot\n") && arena.Contains("BotCharacters=AimMod Hidden Bot.bot\n") && arena.Contains("InvinciblePlayer=true\n") && arena.Contains("ScorePerDamage=0.0\n") && arena.Contains("Timelimit=20.0\n"), "Tracking arena: hidden helper bot only, invincible, no native scoring, the run outlasts the round");
        // Every PvP arena: the scenario's own targets never spawn, only the hidden helper (avatars come from it).
        foreach (var pvp in new[] { LobbyModes.Tracking, LobbyModes.Deathmatch, LobbyModes.Vampiric, LobbyModes.Instagib, LobbyModes.TeamDeathmatch })
        {
            var text = MatchScenario.Generate(new(BaseScenario, new LobbySettings(Mode: pvp, Scenario: new ScenarioChoice("Synthetic A", ContentLibrary.TextHash(BaseScenario), "synthetic_map", ContentLibrary.TextHash("m"), 60))));
            var header = text[..text.IndexOf("\n[", StringComparison.Ordinal)];
            var spawns = header.Split('\n').Where(l => l.StartsWith("BotCharacters=", StringComparison.Ordinal) || l.StartsWith("AddedBots=", StringComparison.Ordinal)).ToArray();
            Check(spawns.Length == 2 && spawns.All(l => l.EndsWith("=AimMod Hidden Bot.bot", StringComparison.Ordinal)) && !header.Contains("target.bot"), pvp + " arena spawns no scenario targets, only the hidden helper bot");
        }
        var hidden = arena[arena.IndexOf("[Character Profile]\nName=AimMod Hidden\n", StringComparison.Ordinal)..];
        Check(hidden.Contains("CharacterModel=None\n") && hidden.Contains("MainBBHide=true\n") && hidden.Contains("DisableCharacterCollision=true\n") && arena.Contains("[Bot Profile]\nName=AimMod Hidden Bot\n") && arena.Contains("NoAiming=true\n"), "The helper bot is invisible, passable and inert");
        // Offline avatar spike: a replay's camera becomes a 30 Hz path for AimModSteam's avatar test.
        var recorded = new NativeReplay(1, "synthetic", "Synthetic Arena", "2026-01-01", "completed", 1,
            [new(0, [0, 0, 164, -5, 0, 0, 90], []), new(1, [300, 0, 164, 5, 90, 0, 90], [])], [], "arena.map", 2.5);
        var exported = AvatarPathExport.Build(recorded).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Check(exported[0] == "AIMMOD_AVATAR_PATH_1" && exported[1] == "meta\tSynthetic%20Arena\tarena.map\t2.5" && exported.Length == 2 + 31
            && exported[2] == "p\t0\t0\t0\t164\t-5\t0" && exported[^1].StartsWith("p\t1000\t300\t0\t164\t5\t", StringComparison.Ordinal), "A replay exports as a 30 Hz avatar path with escaped scenario and map");
        Check(arena == MatchScenario.Generate(new(BaseScenario, duel with { Scenario = new ScenarioChoice("Synthetic A", ContentLibrary.TextHash(BaseScenario), "synthetic_map", ContentLibrary.TextHash("m"), 60) })), "The arena is deterministic");
    }

    // Phase 2 combat modes: rules, host-validated hit claims, health, deaths, respawns, lifesteal and instagib.
    static void CombatModes()
    {
        var content = new FakeContent();
        var start = new LobbySettings(Scenario: content.Scenario("Synthetic A"));
        var dm = LobbyRules.Apply(start, J(new { mode = "deathmatch", weapon = "cs", targetSize = 2 }), 3, content).Settings!;
        Check(dm.Rounds == 1 && dm.TimeLimit == 300 && dm.EffectiveFragLimit == 20 && dm.WeaponProfile.Preset == "default" && dm.TargetSize == 1 && dm.MaxPlayers >= 3 && MatchScenario.Needed(dm),
            "Deathmatch: one 5-minute round, frag limit 20, the mode's own weapon, any number of players");
        Check(LobbyRules.Apply(start, J(new { mode = "vampiric" }), 3, content).Result.Code == "duel-players" && LobbyRules.Apply(start, J(new { mode = "vampiric" }), 2, content).Settings!.EffectiveFragLimit == 10, "Vampiric is one against one, first to 10");
        var vamp = LobbyRules.Apply(start, J(new { mode = "vampiric", lifesteal = 87, fragLimit = 400, timeLimit = 20 }), 2, content).Settings!;
        Check(vamp.Lifesteal == 85 && vamp.FragLimit == 100 && vamp.TimeLimit == 60 && LobbyRules.Plausible(vamp) && !LobbyRules.Plausible(vamp with { Lifesteal = 500 }), "Lifesteal snaps to 5 %, frag limit and match length are clamped");
        Check(LobbyRules.Apply(start, J(new { mode = "instagib" }), 2, content).Settings!.EffectiveFragLimit == 25 && CombatRules.Weapon(LobbyModes.Instagib).Damage >= CombatRules.MaxHealth, "Instagib: first to 25, every hit kills");

        // Two players 10 m apart on a level floor, eyes 64 cm above their capsule centres.
        const long t0 = 5_000_000;
        TrackSample Eye(long t, double x, double yaw, double pitch = 0) => new(t, x, 0, 164, pitch, yaw);
        CombatMatch Arena(string mode, double lifesteal = 50)
        {
            var c = new CombatMatch(mode, ["a", "b"], CombatRules.DefaultFragLimit(mode), lifesteal, t0, t0 + 60_000);
            var a = new List<TrackSample>(); var b = new List<TrackSample>();
            for (long t = t0 - 500; t < t0 + 9_000; t += 17) { a.Add(Eye(t, 0, 0)); b.Add(Eye(t, 1000, 180)); }
            for (var i = 0; i < a.Count; i += 60) { c.Track("a", new TrackBatch("m", 1, a.Skip(i).Take(60).ToList(), [])); c.Track("b", new TrackBatch("m", 1, b.Skip(i).Take(60).ToList(), [])); }
            return c;
        }
        long seq = 0;
        HitClaim Shot(long t, double yaw = 0, double pitch = 0, double ox = 0, double targetY = 0, bool drawn = true, double fromX = 0) =>
            new("m", 1, ++seq, t, fromX + ox, 0, 164, pitch, yaw, false, drawn ? 1000 - fromX : null, drawn ? targetY : null, drawn ? 100 : null, drawn ? 45 : null, drawn ? 115 : null);
        var c1 = Arena(LobbyModes.Deathmatch);
        var at = t0 + 2000;
        Check(c1.Claim("a", Shot(at), at + 50, 40) is null && c1.View().Players.First(p => p.Member == "b").Health == 80, "A body hit on the drawn, matching hull does the weapon's damage");
        Check(c1.Claim("a", Shot(at + 50), at + 100, 40) == "fire-rate", "Claims faster than the weapon fires are refused");
        Check(c1.Claim("a", Shot(at + 200, ox: 100), at + 250, 40) == "origin", "The ray must start at the shooter's own camera");
        Check(c1.Claim("a", Shot(at + 300, yaw: 10), at + 350, 40) == "aim", "The ray must look where the shooter's own track looked");
        Check(c1.Claim("a", Shot(at + 400, targetY: 300), at + 450, 40) == "target-mismatch", "A drawn target nobody was at in the last 200 ms is refused");
        Check(c1.Claim("a", Shot(at + 500, yaw: 2.9), at + 550, 40) == "ray-miss", "A ray beside the hull is refused");
        // The drawn hull is the shooter's evidence of where the victim was, not of how big it is.
        Check(c1.Claim("a", new HitClaim("m", 1, ++seq, at + 520, 0, 30, 164, 0, 2.9, false, 1000, 0, 100, 1000, 2000), at + 560, 40) == "ray-miss",
            "A claimed hull far larger than the avatar's can't turn a miss into a hit");
        Check(Arena(LobbyModes.Deathmatch).Claim("a", new HitClaim("m", 1, ++seq, at + 540, 0, 0, 195, 2.9, 0, false, 1000, 0, 155.7, 45, 115), at + 570, 40) == "target-mismatch",
            "A claimed hull raised above the victim's own track can't turn a shot over the head into a headshot");
        Check(c1.Claim("a", Shot(at + 600) with { Seq = 1 }, at + 650, 40) == "repeated" && c1.Claim("a", Shot(at - 5000), at + 700, 40) == "time", "Repeated and stale claims are refused");
        var headPitch = Math.Atan2(195 - 164, 1000) * 180 / Math.PI;
        Check(c1.Claim("a", Shot(at + 800, pitch: headPitch), at + 850, 40) is null && c1.View().Events[^1] is { Kind: "damage", Head: true, Amount: 40 }, "The host decides headshots from the ray (the top of the hull) and doubles the damage");
        for (var k = 0; k < 2; k++) c1.Claim("a", Shot(at + 1000 + k * 120), at + 1050 + k * 120, 40);
        var dead = c1.View().Players.First(p => p.Member == "b");
        Check(!dead.Alive && dead.Deaths == 1 && c1.View().Players.First(p => p.Member == "a").Frags == 1 && c1.View().Events.Any(e => e.Kind == "death" && e.Member == "b" && e.Attacker == "a"), "Health reaching zero is a death and a frag");
        Check(c1.Claim("a", Shot(at + 1500), at + 1550, 40) == "target-mismatch", "A dead player can't be hit");
        c1.Tick(at + 1240 + CombatRules.RespawnMs(LobbyModes.Deathmatch) + 10);
        var back = c1.View().Players.First(p => p.Member == "b");
        Check(back.Alive && back.Health == 100 && c1.View().Events[^1].Kind == "respawn", "Respawn after the delay with full health");
        var resp = at + 1240 + CombatRules.RespawnMs(LobbyModes.Deathmatch) + 100;
        Check(c1.Claim("a", Shot(resp), resp + 20, 40) == "spawn-protected", "Spawn protection holds for 1.5 s");
        Check(c1.Claim("a", Shot(resp + 1600, drawn: false), resp + 1620, 0) is null, "Without a drawn hull the host rewinds the victim's own track (estimated lag) and checks the ray");

        // Vampiric: damage dealt heals the dealer; decay wears both down, never to death.
        var v = Arena(LobbyModes.Vampiric, 50);
        var vt = t0 + 2000;
        HitClaim Back(long t) => new("m", 1, ++seq, t, 1000, 0, 164, 0, 180, false, 0, 0, 100, 45, 115);
        v.Claim("b", Back(vt), vt + 10, 40); v.Claim("b", Back(vt + 150), vt + 160, 40);
        Check(v.View().Players.First(p => p.Member == "a").Health == 60, "Vampiric: the other player's hits land first");
        v.Claim("a", Shot(vt + 300), vt + 310, 40);
        Check(v.View().Players.First(p => p.Member == "a").Health == 70 && v.View().Events[^1].AttackerHealth == 70, "Lifesteal 50 %: a 20 damage hit heals 10");
        var decay = Arena(LobbyModes.Vampiric);
        decay.Tick(t0 + 1000); decay.Tick(t0 + 11_000);
        Check(decay.View().Players.All(p => Math.Abs(p.Health - 78) < 0.01) && decay.View().Players.All(p => p.Alive), "Vampiric decay is 2 hp/s (11 s: 22 hp) and never kills");
        var inst = Arena(LobbyModes.Instagib);
        Check(inst.Claim("a", Shot(vt), vt + 10, 40) is null && !inst.View().Players.First(p => p.Member == "b").Alive, "Instagib: one hit kills");
        Check(inst.Claim("b", Back(vt + 500), vt + 510, 40) == "shooter-dead" && inst.Claim("a", Shot(vt + 600), vt + 610, 40) == "fire-rate", "Dead players can't shoot; the railgun fires every 1.2 s");

        // Wire formats.
        var claim = Shot(t0, pitch: 1.234);
        var read = HitClaim.Read(J(claim.Body()));
        Check(read is { MatchId: "m", Pitch: 1.23, TargetX: 1000, TargetHalfHeight: 115 } && read.Seq == claim.Seq, "Hit claims round-trip");
        Check(HitClaim.Read(J(new { match = "m", round = 1, seq = 1, t = 1, o = new[] { 0, 0, 0 }, r = new[] { 95, 0 } })) is null && HitClaim.Read(J(new { match = "m", round = 1, seq = 1, t = 1, o = new[] { 0, 0 }, r = new[] { 0, 0 } })) is null, "Impossible claims are refused");
        var feed = new ShotFeed(Path.GetTempPath());
        var shots = ShotFeed.Parse("AIMMOD_SHOTS_1\t3\nshot\t1000\t7\t1\t2\t3\t-4\t90\t0\t0\t0\nshot\t1100\t8\t1\t2\t3\t-4\t91\t0\t9\t1\n");
        var seen = new Dictionary<int, TrackSeen> { [9] = new TrackSeen(1090, 9, 500, 600, 100, 45, 115) };
        var claims = feed.Take(shots, "m", 1, 250, seen);
        Check(claims.Count == 1 && claims[0] is { Seq: 8, T: 1350, Head: true, TargetX: 500, Yaw: 91 } && feed.Take(shots, "m", 1, 250, seen).Count == 0, "Shot rows become hit claims on the host clock; misses and repeats are skipped");
        Check(ShotFeed.Parse("AIMMOD_SHOTS_1\t1\nshot\t1\t5\t0\t0\t0\t0\t0\t0\t0\t0\nshot\t2\t4\t0\t0\t0\t0\t0\t0\t0\t0\n") is null && ShotFeed.Parse("AIMMOD_SHOTS_1\t1\nshot\t1\t5\t0\t0\t0\t0\t0\t9\t0\t0\n") is null, "Shot sequences must increase and weapon slots are 0-7");
        var state = PlayState.Format(4, "AimMod Match - X", new CombatPlayerView("me", 42.5, true, 3, 1, null, 900, 10, 0), new CombatEvent(7, "damage", 800, "me", "76561198000000001", 20, true, 42.5, null, null, [0.6, 0.8, 0]), 800, 0);
        Check(state == "AIMMOD_PLAYSTATE_1\t4\nmatch\tAimMod Match - X\nhealth\t42.5\t100\nalive\t1\nrespawnAt\t0\nprotected\t1\nhit\t7\t76561198000000001\t20\t1\t0.6\t0.8\t0\n", "Play state in AimModCore's format: exact scenario, health, life, respawn, protection and the last hit with its direction");
        var down = PlayState.Format(5, "AimMod Match - X", new CombatPlayerView("me", 0, false, 3, 2, 5000, 900, 10, 0), null, 4000, 250);
        Check(down.Contains("alive\t0\n") && down.Contains("respawnAt\t4750\n") && down.Contains("protected\t0\n") && !down.Contains("hit\t"), "A dead player's respawn time is converted to the local clock");
        // AimModCore's shot rows: origin, unit direction, slot, target, headshot, gameHit; a session header; tag rows.
        var core2 = ShotFeed.Parse("AIMMOD_SHOTS_1\t9\tsess-1\nshot\t2000\t3\t0\t0\t164\t0\t1\t0\t1\t9\t0\t1\ntag\t9\ts-0011223344556677\n");
        Check(core2 is { Session: "sess-1" } c2 && c2.Shots.Single() is { Seq: 3, Weapon: 1, Target: 9 } sh && Math.Abs(sh.Yaw - 90) < 1e-9 && Math.Abs(sh.Pitch) < 1e-9, "AimModCore's shot rows (direction vectors) read as pitch and yaw");
        var feed2 = new ShotFeed(Path.GetTempPath());
        Check(feed2.Take(core2, "m", 1, 0, seen).Count == 1 && feed2.Take(ShotFeed.Parse("AIMMOD_SHOTS_1\t10\tsess-2\nshot\t2100\t1\t0\t0\t164\t1\t0\t0\t0\t9\t0\t1\n"), "m", 1, 0, seen).Count == 1, "A new AimModCore session restarts the shot sequence");

        // A deathmatch on the host: frag limit ends it, native score frames are refused.
        var (core, clock, advance) = Lobby();
        core.Join("p2", "Two");
        core.Apply("host", "settings", Patch(new { mode = "deathmatch", fragLimit = 1, countdown = 3 }), content);
        ReadyAll(core);
        Check(core.Apply("host", "start", default, content).Ok, "Deathmatch starts");
        advance(3000); core.Tick();
        var m = core.Snapshot().Match!;
        Check(m.Phase == MatchPhases.Live && m.Combat is { FragLimit: 1 } && core.Score("host", new ScoreFrame(m.Id, 1, 1, 1, 1, 1, 0, 9)).Code == "combat", "Combat matches are scored by the host only");
        var begin = clock();
        for (long t = 0; t < 2000; t += 100)
        {
            var eyesA = new List<TrackSample>(); var eyesB = new List<TrackSample>();
            for (long k = 0; k < 100; k += 17) { eyesA.Add(Eye(begin + t + k, 0, 0)); eyesB.Add(Eye(begin + t + k, 1000, 180)); }
            core.Track("host", new TrackBatch(m.Id, 1, eyesA, [])); core.Track("p2", new TrackBatch(m.Id, 1, eyesB, []));
            advance(100); core.Tick();
        }
        var now = clock();
        for (var k = 0; k < 5; k++) { core.Claim("host", new HitClaim(m.Id, 1, 100 + k, now - 480 + k * 100, 0, 0, 164, 0, 0, false, 1000, 0, 100, 45, 115)); }
        var final = core.Snapshot().Match!;
        Check(final.Phase == MatchPhases.Final && final.WinnerId == "host" && final.Rounds[0].Results[0] is { MemberId: "host", Score: 1, Place: 1 }, "Reaching the frag limit ends the match with the winner");

        // A three-player deathmatch whose leader leaves: the players still there are placed among themselves.
        (core, clock, advance) = Lobby();
        core.Join("p2", "Two"); core.Join("p3", "Three");
        core.Apply("host", "settings", Patch(new { mode = "deathmatch", fragLimit = 5, countdown = 3, timeLimit = 60 }), content);
        ReadyAll(core); core.Apply("host", "start", default, content);
        advance(3000); core.Tick();
        var three = core.Snapshot().Match!;
        var t3 = clock();
        for (long t = 0; t < 3000; t += 100)
        {
            List<TrackSample> Eyes(double x, double y, double yaw) { var l = new List<TrackSample>(); for (long k = 0; k < 100; k += 17) l.Add(new TrackSample(t3 + t + k, x, y, 164, 0, yaw)); return l; }
            core.Track("host", new TrackBatch(three.Id, 1, Eyes(0, 0, 0), [])); core.Track("p2", new TrackBatch(three.Id, 1, Eyes(1000, 0, 180), [])); core.Track("p3", new TrackBatch(three.Id, 1, Eyes(0, 3000, 0), []));
            advance(100); core.Tick();
        }
        var shotAt = clock();
        for (var k = 0; k < 5; k++) core.Claim("p2", new HitClaim(three.Id, 1, 200 + k, shotAt - 600 + k * 110, 1000, 0, 164, 0, 180, false, 0, 0, 100, 45, 115));
        Check(core.Snapshot().Match!.Combat!.Players.First(p => p.Member == "p2").Frags == 1, "The leader has a frag");
        core.Leave("p2");
        advance(60_000 + LobbyCore.RoundGraceMs); core.Tick();
        var left = core.Snapshot().Match!;
        var leftPlaces = left.Rounds[0].Results;
        Check(left.Phase == MatchPhases.Final && leftPlaces.First(p => p.MemberId == "p3").Place == 1 && leftPlaces.First(p => p.MemberId == "host").Place == 2 && leftPlaces.First(p => p.MemberId == "p2").Place == 0,
            "A leader who left doesn't push the remaining players down: equal frags, fewer deaths places first");
        Check(left.WinnerId == "p3" && left.Standings.First(s => s.MemberId == "p3").Place == 1 && left.Standings.First(s => s.MemberId == "host").Place == 2,
            "The final standings follow the match placement: the player who left doesn't win, fewer deaths breaks a frag tie");

        // Arenas: the player can be hurt and carries the mode weapon; nothing natively heals or scores.
        var arena = MatchScenario.Generate(new(BaseScenario, LobbyRules.Apply(start, J(new { mode = "instagib" }), 2, content).Settings! with { Scenario = new ScenarioChoice("Synthetic A", ContentLibrary.TextHash(BaseScenario), "synthetic_map", ContentLibrary.TextHash("m"), 60) }));
        Check(arena.Contains("InvinciblePlayer=false\n") && arena.Contains("AddedBots=AimMod Hidden Bot.bot\n") && arena.Contains("WeaponProfileNames=AimMod Railgun;;;;;;;\n") && arena.Contains("Name=AimMod Railgun\nType=Hitscan\nShotsPerClick=1\nDamagePerShot=1000.0\n")
            && arena.Contains("TimeBetweenShots=1.2\n") && arena.Contains("LifeStealPercent=0.0\n") && arena.Contains("ScorePerKill=0.0\n") && arena.Contains("MinRespawnDelay=1.0\n") && arena.Contains("Timelimit=330.0\n"), "Instagib arena: vulnerable player with the railgun, helper bot, no native heals or score");
        Check(MatchScenario.Name(dm).Contains(" - Deathmatch - ", StringComparison.Ordinal) && MatchScenario.Key(dm) != MatchScenario.Key(dm with { Mode = LobbyModes.Instagib }), "Each combat mode has its own arena");
    }

    // Team deathmatch, host-chosen spawns, pushed events and arena spawn points.
    static void TeamsAndSpawns()
    {
        var content = new FakeContent();
        var start = new LobbySettings(Scenario: content.Scenario("Synthetic A"));
        var tdm = LobbyRules.Apply(start, J(new { mode = "team-deathmatch" }), 4, content).Settings!;
        Check(tdm.EffectiveFragLimit == 50 && tdm.MaxPlayers >= 4 && LobbyModes.Combat(tdm.Mode) && CombatRules.Teams(["a", "b", "c", "d", "e"]).Values.SequenceEqual([1, 2, 1, 2, 1]), "Team deathmatch: first team to 50, teams alternate by join order");

        const long t0 = 7_000_000;
        // a (team 1) and c (team 1) at x=0 and x=0/y=300; b (team 2) at x=1000.
        var c = new CombatMatch(LobbyModes.TeamDeathmatch, ["a", "b", "c"], 2, 0, t0, t0 + 60_000);
        Check(c.TeamOf("a") == 1 && c.TeamOf("b") == 2 && c.TeamOf("c") == 1, "Teams are assigned at the start");
        void Feed(string id, double x, double y, double yaw)
        {
            var list = new List<TrackSample>();
            for (long t = t0 - 500; t < t0 + 9000; t += 17) list.Add(new TrackSample(t, x, y, 164, 0, yaw));
            for (var i = 0; i < list.Count; i += 60) c.Track(id, new TrackBatch("m", 1, list.Skip(i).Take(60).ToList(), []));
        }
        Feed("a", 0, 0, 0); Feed("b", 1000, 0, 180); Feed("c", 500, 0, 0);
        long seq = 0;
        HitClaim At(long t, double tx) => new("m", 1, ++seq, t, 0, 0, 164, 0, 0, false, tx, 0, 100, 45, 115);
        var at = t0 + 2000;
        Check(c.Claim("a", At(at, 500), at + 10, 40) == "teammate" && c.View().Players.First(p => p.Member == "c").Health == 100, "Friendly fire is off: a hit on a teammate is refused");
        for (var k = 0; k < 5; k++) c.Claim("a", At(at + 200 + k * 120, 1000), at + 210 + k * 120, 40);
        Check(c.View().TeamFrags!.SequenceEqual([1, 0]) && c.WinnerTeam is null && c.Leader is null, "Team frags add up");
        c.Spawns = [new SpawnPoint(0, 0, 100, 0, 2), new SpawnPoint(5000, 0, 100, 90, 2), new SpawnPoint(9000, 0, 100, 0, 1)];
        c.Tick(at + 210 + 4 * 120 + CombatRules.RespawnMs(LobbyModes.TeamDeathmatch) + 10);
        var respawn = c.View().Events[^1];
        Check(respawn is { Kind: "respawn", Member: "b" } && respawn.Spawn is { } sp && sp[0] == 5000 && sp[3] == 90, "Respawn picks the team's spawn farthest from the nearest living opponent");
        var t2 = at + 5000;
        for (var k = 0; k < 5; k++) c.Claim("a", At(t2 + k * 120, 1000), t2 + 10 + k * 120, 40);
        Check(c.WinnerTeam == 1 && c.Leader == "a", "The first team to the frag limit wins");

        // On the host: placements by team, no single winner, the combat view names the team.
        var (core, clock, advance) = Lobby();
        core.Join("p2", "Two"); core.Join("p3", "Three"); core.Join("p4", "Four");
        core.Apply("host", "settings", Patch(new { mode = "team-deathmatch", fragLimit = 1, countdown = 3 }), content);
        ReadyAll(core);
        core.SetCombatSpawns([new SpawnPoint(1, 2, 3, 0, 3)]);
        Check(core.Apply("host", "start", default, content).Ok, "Team deathmatch starts");
        advance(3000); core.Tick();
        var m = core.Snapshot().Match!;
        Check(m.Combat!.Players.First(p => p.Member == "host").Team == 1 && m.Combat.Players.First(p => p.Member == "p2").Team == 2 && m.Combat.TeamFrags!.Count == 2, "The snapshot carries teams and team frags");
        var begin = clock();
        for (long t = 0; t < 2000; t += 100)
        {
            var a = new List<TrackSample>(); var b = new List<TrackSample>();
            for (long k = 0; k < 100; k += 17) { a.Add(new TrackSample(begin + t + k, 0, 0, 164, 0, 0)); b.Add(new TrackSample(begin + t + k, 1000, 0, 164, 0, 180)); }
            core.Track("host", new TrackBatch(m.Id, 1, a, [])); core.Track("p2", new TrackBatch(m.Id, 1, b, []));
            advance(100); core.Tick();
        }
        var now = clock();
        for (var k = 0; k < 5; k++) core.Claim("host", new HitClaim(m.Id, 1, 100 + k, now - 480 + k * 100, 0, 0, 164, 0, 0, false, 1000, 0, 100, 45, 115));
        var final = core.Snapshot().Match!;
        var places = final.Rounds[0].Results.ToDictionary(r => r.MemberId, r => r.Place);
        Check(final.Phase == MatchPhases.Final && final.WinnerId is null && final.Combat!.WinnerTeam == 1 && places["host"] == 1 && places["p3"] == 1 && places["p2"] == 2 && places["p4"] == 2, "Team deathmatch places the winning team first, with no single winner");

        // Pushed events on top of the last snapshot.
        var view = new CombatView(20, [new("me", 100, true, 0, 0, null, null, 0, 0), new("them", 100, true, 0, 0, null, null, 0, 0)], [new(3, "respawn", 1, "me", null, 0, false, 100, null)]);
        var pushed = new[] { new CombatEvent(2, "damage", 1, "me", "them", 50, false, 10, null), new CombatEvent(4, "damage", 2, "me", "them", 40, false, 60, 100), new CombatEvent(5, "death", 3, "them", "me", 60, true, 0, 80) };
        var live = CombatOverlay.Apply(view, pushed);
        Check(live.Players[0] is { Health: 80, Frags: 1, Alive: true } && live.Players[1] is { Alive: false, Deaths: 1, Health: 0 } && live.Events.Count == 3, "Pushed events newer than the snapshot apply at once; older ones are already in it");

        // Spawn points from the arena's map: map-creator JSON and legacy Reflex.
        var json = "Name=x\nMapScale=2.0\n\n[Map Data]\n{\"objects\":[{\"location\":\"10, 20, 30\",\"name\":\"SpawnPoint\",\"properties\":[{\"name\":\"TeamMask\",\"value\":2}],\"rotation\":\"0, 0, -90\",\"type\":\"gameObject\"},{\"name\":\"Cube\",\"location\":\"1, 1, 1\"}],\"version\":\"1.0.0\"}\n";
        var fromJson = MatchScenario.Spawns(json);
        Check(fromJson.Count == 1 && fromJson[0] == new SpawnPoint(20, 40, 60, -90, 2), "JSON spawn points: location times MapScale, yaw from the rotation, team mask");
        var reflex = "Name=x\nMapScale=4.0\n\n[Map Data]\nreflex map version 8\nglobal\n\tentity\n\t\ttype WorldSpawn\n\tentity\n\t\ttype PlayerSpawn\n\t\tVector3 position 1.0 2.0 3.0\n\t\tVector3 angles 45.0 0.0 0.0\n\t\tBool8 teamB 0\n\tentity\n\t\ttype PlayerSpawn\n\t\tVector3 position 5 6 7\n";
        var fromReflex = MatchScenario.Spawns(reflex);
        Check(fromReflex.Count == 2 && fromReflex[0] == new SpawnPoint(12, 4, 8, 45, 1) && fromReflex[1] == new SpawnPoint(28, 20, 24, 0, 3), "Reflex spawn points: (a, b, c) loads as (c, a, b) times MapScale, teamB 0 keeps team 1 only");
        var state = PlayState.Round(1, "AimMod Match - X", new CombatEvent(9, "respawn", 5, "me", null, 0, false, 100, null, [1, 2, 3, 90]), ["phase\tlive\t0\t1\t123"]);
        Check(state == "AIMMOD_ROUND_1\t1\nmatch\tAimMod Match - X\nspawn\t9\t1\t2\t3\t90\nphase\tlive\t0\t1\t123\n", "round-state.tsv carries the host's spawn (and the CS round) for AimModCore");
    }

    // Phase 3: CS rules, economy, armour, buying, plant and defuse, halves, and the lobby side.
    static void CsMode()
    {
        var content = new FakeContent();
        // CS2 armour: AK-47 (77.5 % penetration) 36 body damage on full kevlar: 27.9 to health, 4.05 armour.
        var (health, armor) = CsRules.Armor(36, false, 100, false, 0.775);
        Check(Math.Abs(health - 27.9) < 1e-9 && Math.Abs(armor - 4.05) < 1e-9, "Armour: penetration share reaches health, the rest costs 0.5 armour per point");
        Check(CsRules.Armor(144, true, 100, false, 0.775) == (144, 0) && CsRules.Armor(144, true, 100, true, 0.775).Health < 144, "A headshot is reduced only with a helmet");
        var (h2, a2) = CsRules.Armor(100, false, 5, false, 0.5);
        Check(Math.Abs(h2 - 90) < 1e-9 && a2 == 5, "Thin armour absorbs what it can and the rest is health damage");
        Check(CsRules.LossBonus(0) == 1400 && CsRules.LossBonus(1) == 1900 && CsRules.LossBonus(4) == 3400 && CsRules.LossBonus(9) == 3400, "CS2 loss bonus: 1400 up to 3400 in 500 steps");
        Check(CsRules.Find("ak47") is { Price: 2700, Side: "T", KillReward: 300 } && CsRules.Find("awp") is { Price: 4750, KillReward: 100 } && CsRules.Find("mp9") is { KillReward: 600, Side: "CT" }, "CS2 prices, sides and kill rewards per class");

        // Lobby: 3v3, 4v4 or 5v5 only, up to 10 players, halves of 6-15 rounds.
        var start = new LobbySettings(Scenario: content.Scenario("Synthetic A"));
        var cs = LobbyRules.Apply(start, J(new { mode = "cs", maxPlayers = 7, halfRounds = 30 }), 2, content).Settings!;
        Check(cs.MaxPlayers == 8 && cs.HalfRounds == 15 && cs.Overtime && cs.TotalRounds == 30 && LobbyRules.Apply(cs, J(new { maxPlayers = 12 }), 2, content).Settings!.MaxPlayers == 10, "CS: even team sizes up to 5v5, halves capped at 15 rounds");
        Check(LobbyRules.Apply(start, J(new { mode = "deathmatch", maxPlayers = 10 }), 2, content).Settings!.MaxPlayers == 8, "Other modes stay at 8 players");
        Check(MatchScenario.Name(cs).Contains(" - CS competitive - ", StringComparison.Ordinal) && MatchScenario.Needed(cs), "CS plays its own arena");
        var arena = MatchScenario.Generate(new(BaseScenario, cs with { Scenario = new ScenarioChoice("Synthetic A", ContentLibrary.TextHash(BaseScenario), "synthetic_map", ContentLibrary.TextHash("m"), 60) }));
        Check(CsRules.Weapons.All(w => arena.Contains("Name=" + w.Combat.Name + "\nType=Hitscan\n")) && arena.Contains("WeaponProfileNames=AimMod CS USP-S;AimMod CS Glock-18;;;;;;\n") && arena.Contains("InvinciblePlayer=false\n") && arena.Contains("MinRespawnDelay=600.0\n"),
            "CS arena: every buyable weapon, the pistols in the first slots, no native respawn");
        Check(MultiplayerService.CsKeyClashes(new HashSet<string> { "E", "F" }).Single().Contains("E (use", StringComparison.Ordinal) && MultiplayerService.CsKeyClashes(new HashSet<string>()).Count == 0, "B and E are checked against KovaaK's binds");

        // Map-port metadata: zones and spawns times map_scale.
        var json = "{\"format\":\"aimmod.map-objectives\",\"version\":1,\"map_scale\":1,\"zones\":["
            + "{\"type\":\"buy_zone\",\"team\":\"terrorist\",\"name\":\"\",\"aabb\":{\"min\":[-100,-100,0],\"max\":[100,100,200]}},"
            + "{\"type\":\"buy_zone\",\"team\":\"counter_terrorist\",\"name\":\"\",\"aabb\":{\"min\":[900,-100,0],\"max\":[1100,100,200]}},"
            + "{\"type\":\"bomb_site\",\"team\":\"any\",\"name\":\"\",\"aabb\":{\"min\":[450,-50,0],\"max\":[550,50,200]}}],"
            + "\"spawns\":[{\"team\":\"terrorist\",\"origin\":[0,0,10],\"yaw\":0},{\"team\":\"counter_terrorist\",\"origin\":[1000,0,10],\"yaw\":180}]}";
        var objectives = MapObjectives.Parse(json)!;
        Check(objectives.BombSites.Single().Name == "A" && objectives.BuyZones(CsRules.T).Count == 1 && objectives.SpawnsFor(CsRules.CT).Single().Yaw == 180
            && MapObjectives.Parse(json.Replace("aimmod.map-objectives", "other")) is null && MapObjectives.Parse(json.Replace("\"map_scale\":1", "\"map_scale\":4"))!.BombSites[0].Min[0] == 1800, "Objective metadata: sites named A, B.. in order, zones scaled by map_scale");
        Check(MapObjectives.FileFor("aimmod_de_dust2_csgo.json") == "aimmod_de_dust2_csgo.aimmod.json", "Metadata sits next to the map as <map>.aimmod.json");

        // A 3v3: a, c, e start T (team 1); b, d, f start CT (team 2).
        const long t0 = 9_000_000;
        var ids = new[] { "a", "b", "c", "d", "e", "f" };
        var match = new CsMatch(ids, t0, 6, true, objectives);
        void Place(string id, double x, long from, long to) { var list = new List<TrackSample>(); for (var t = from; t < to; t += 17) list.Add(new TrackSample(t, x, 0, 164, 0, x < 500 ? 0 : 180)); for (var i = 0; i < list.Count; i += 60) match.Combat.Track(id, new TrackBatch("m", 1, list.Skip(i).Take(60).ToList(), [])); }
        foreach (var id in ids) Place(id, match.SideOf(id) == CsRules.T ? 0 : 1000, t0 - 200, t0 + 2000);
        Check(match.SideOf("a") == CsRules.T && match.SideOf("b") == CsRules.CT && match.View().Bomb.Carrier == "a" && match.View().Spawns!["b"][0] == 1000, "Teams by join order, a Terrorist carries the bomb, side spawns from the metadata");
        Check(match.Buy("a", "ak47", t0 + 1000) == "money" && match.Buy("a", "kevlar", t0 + 1000) is null && match.View().Players.First(p => p.Member == "a") is { Money: 150, Armor: 100 }, "Buying in freeze time: not enough money is refused, kevlar is bought");
        Check(match.Buy("b", "ak47", t0 + 1000) == "side" && match.Buy("b", "usp", t0 + 1000) == "owned" && match.Buy("b", "defuse-kit", t0 + 1000) is null, "Side rules: CT can't buy the AK; the starting pistol is owned; CT buy a kit");
        Place("d", 0, t0 + 2000, t0 + 3000);
        Check(match.Buy("d", "kevlar", t0 + 2500) == "buy-zone", "Buying outside your buy zone is refused");
        match.Tick(t0 + CsRules.FreezeMs);
        Check(match.Phase == "live" && match.Buy("c", "kevlar", t0 + CsRules.FreezeMs + CsRules.BuyMs + 10) == "buy-time", "After the freeze the round is live; buy time ends 20 s later");

        // Plant: the carrier holds E in the site for 3.2 s; then the CT defuses with a kit (5 s).
        var live = t0 + CsRules.FreezeMs;
        foreach (var id in ids) Place(id, id == "a" || id == "b" ? 500 : match.SideOf(id) == CsRules.T ? 0 : 1000, live, live + 30_000);
        Check(match.Use("c", true, live + 1000) == "nothing-to-use" && match.Use("a", true, live + 1000) is null, "Only the carrier plants, in a bomb site");
        match.Tick(live + 1000 + CsRules.PlantMs + 10);
        Check(match.Phase == "planted" && match.View().Bomb is { State: "planted", Site: "A" } && match.View().Players.First(p => p.Member == "a").Money == 450, "Planted after 3.2 s; the planter gets $300");
        Check(match.Use("b", true, live + 5000) is null, "The CT at the bomb starts defusing");
        match.Tick(live + 5000 + CsRules.KitDefuseMs + 10);
        var v = match.View();
        Check(match.Phase == "end" && v.LastWinner == 2 && v.LastReason == "defuse" && v.Score[1] == 1, "A kit defuses in 5 s: the CT take the round");
        Check(v.Players.First(p => p.Member == "b").Money == 800 - CsRules.KitPrice + CsRules.DefuseReward + CsRules.WinDefuse && v.Players.First(p => p.Member == "c").Money == 800 + 1400 + 800,
            "CS2 rewards: $3500 defuse win (+$300 defuser); Terrorists get the loss bonus plus $800 for the plant");

        // Round 2: elimination. Dead players lose their gear; the loss bonus grows.
        match.Tick(match.View().PhaseEndsAt + 1);
        Check(match.Phase == "freeze" && match.Round == 2 && match.View().Bomb.Carrier == "c", "The next round starts frozen; the bomb goes to the next Terrorist");
        match.Tick(match.View().PhaseEndsAt + 1);
        var t2 = match.View().LiveAt!.Value;
        foreach (var id in new[] { "a", "c", "e" }) match.Combat.Kill(id, t2 + 100);
        match.Tick(t2 + 200);
        Check(match.View() is { LastWinner: 2, LastReason: "elimination" } && match.View().Players.First(p => p.Member == "e").Money == 800 + 2200 + 1900, "All Terrorists down: CT win by elimination; the second loss pays $1900");

        // Rounds 3-6 the same way; then halftime: sides switch, money and gear reset.
        for (var r = 3; r <= 6; r++)
        {
            match.Tick(match.View().PhaseEndsAt + 1); match.Tick(match.View().PhaseEndsAt + 1);
            var at = match.View().LiveAt!.Value;
            foreach (var id in new[] { "a", "c", "e" }) match.Combat.Kill(id, at + 100);
            match.Tick(at + 200);
        }
        Check(match.View().Score[1] == 6, "Six CT rounds");
        match.Tick(match.View().PhaseEndsAt + 1);
        v = match.View();
        Check(v.Round == 7 && match.SideOf("a") == CsRules.CT && v.Team1Side == CsRules.CT && v.Players.All(p => p.Money == CsRules.StartMoney) && v.Players.All(p => p.Primary is null && p.Armor == 0), "Halftime: sides switch, money resets to $800, gear is gone");
        // Team 2 (now T) wins once more: first to 7 takes the match.
        match.Tick(v.PhaseEndsAt + 1);
        var t7 = match.View().LiveAt!.Value;
        foreach (var id in new[] { "a", "c", "e" }) match.Combat.Kill(id, t7 + 100);
        match.Tick(t7 + 200); match.Tick(match.View().PhaseEndsAt + 1);
        Check(match.Over && match.WinnerTeam == 2 && match.View().Events.Last().Kind == "match-end", "First to 7 (half of 6 + 1) wins the match");

        // Time runs out: CT win; Terrorists who survived get no loss bonus.
        var timeout = new CsMatch(ids, t0, 6, true, objectives);
        timeout.Tick(t0 + CsRules.FreezeMs);
        timeout.Tick(t0 + CsRules.FreezeMs + CsRules.RoundMs + 1);
        Check(timeout.View() is { LastWinner: 2, LastReason: "time" } && timeout.View().Players.Where(p => p.Side == CsRules.T).All(p => p.Money == 800), "Time out: CT win; surviving Terrorists get nothing");

        // On the host: 6 players start, freeze refuses hits, buys go through lobby commands.
        var (core, clock, advance) = Lobby();
        core.Apply("host", "settings", Patch(new { mode = "cs", maxPlayers = 6, countdown = 3 }), content);
        foreach (var id in new[] { "p2", "p3", "p4", "p5" }) core.Join(id, id);
        ReadyAll(core);
        Check(core.Apply("host", "start", default, content).Code == "blocked" && LobbyRules.StartBlockers(core.Snapshot()).Any(b => b.Code == "cs-teams"), "CS with five players is blocked: it needs 6, 8 or 10");
        core.Join("p6", "p6"); ReadyAll(core);
        core.SetCsObjectives(null);
        Check(core.Apply("host", "start", default, content).Ok, "CS starts with 3v3");
        advance(3000); core.Tick();
        var m = core.Snapshot().Match!;
        Check(m.Cs is { Phase: "freeze", Round: 1 } && core.Claim("host", new HitClaim(m.Id, 1, 1, clock(), 0, 0, 164, 0, 0, false, null, null, null, null, null, 1)).Code == "round-phase", "Freeze time: no shooting");
        Check(core.Apply("host", "buy", J(new { item = "kevlar" }), content).Ok && core.Apply("host", "buy", J(new { item = "kevlar" }), content).Code == "owned"
            && core.Snapshot().Match!.Cs!.Players.First(p => p.Member == "host").Money == 150, "Buys are lobby commands the host validates (no map metadata: buy anywhere)");
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
        Check(!Protocol.Reliable("score") && Protocol.Reliable("snapshot") && Protocol.Reliable("track") && Protocol.Reliable("hit") && Protocol.Reliable("combat") && Protocol.Reliable("cosmetic.look") && Protocol.Types.Length == 22, "Score frames are unreliable; state and tracking samples are reliable");
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
        public readonly List<string> DevAvatars = [];
        public bool DevAvatar(bool on, string mode, string? profile = null) { DevAvatars.Add((on ? "on " : "off ") + mode + (profile is null ? "" : " " + profile)); return true; }
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
        bt.Inbox.Enqueue(new TransportEvent("f2", TransportEvent.SpectateStarted, Reason: "Watchable Friend", Stream: "pose-f2"));
        bt.Inbox.Enqueue(new TransportEvent("f2", TransportEvent.SpectateScore, Frame: Encoding.UTF8.GetBytes("{\"active\":true,\"score\":1234,\"accuracy\":85.5,\"remaining\":20}")));
        Pump();
        var watching = View(b).GetProperty("watch");
        Check(watching.GetProperty("scenario").GetString() == "Synthetic A" && watching.GetProperty("state").GetString() == "missing" && watching.GetProperty("stream").GetString() == "pose-f2" && watching.GetProperty("message").GetString()!.Contains("don’t have"), "The friend's scenario is known, and missing content is explained");
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
        Write(new { v = 1, ev = "spectate.started", peer = friend, name = "Synthetic Friend", direct = true, stream = "pose-7" });
        Write(new { v = 1, ev = "spectate.score", active = true, paused = false, score = 10, seconds = 5, remaining = 55, shots = 4, hits = 3, kills = 2, accuracy = 75 });
        Write(new { v = 1, ev = "spectate.asked", from = "76561190000000008", fromName = "Synthetic Asker" });
        Write(new { v = 1, ev = "spectator.joined", peer = "76561190000000009", name = "Synthetic Viewer" });
        Write(new { v = 1, ev = "spectators", list = new[] { new { peer = "76561190000000009", name = "Synthetic Viewer" }, new { peer = "76561190000000010", name = "Already Watching" } } });
        Write(new { v = 1, ev = "spectator.left", peer = "76561190000000009", reason = "stopped" });
        Write(new { v = 1, ev = "spectate.ended", peer = friend, reason = "stopped" });
        var spectateEvents = new List<TransportEvent>();
        Check(Until(() => { spectateEvents.AddRange(steam.Drain()); return spectateEvents.Any(e => e.Kind == TransportEvent.SpectateEnded); }), "Spectate events arrive");
        Check(spectateEvents.Any(e => e.Kind == TransportEvent.SpectateStarted && e.Reason == "Synthetic Friend" && e.Host && e.Stream == "pose-7") && spectateEvents.Any(e => e.Kind == TransportEvent.SpectateScore)
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
        Check(!steam.QueryWorkshop(MapPorts.TitlePrefix), "Without the ugc-query feature the Workshop isn't listed");
        Write(new { v = 1, ev = "ugc.items", tag = MapPorts.WorkshopTag, items = new object[] {
            new { item = "3333000001", title = "AimMod - Dust2 (CSGO) - CS Movement", bytes = 71_000_000L, updated = 1_790_000_000L, subscribed = true, installed = true, needsUpdate = true },
            new { item = "../bad", title = "AimMod - Bad (CSS) - CS Movement" } } });
        Check(Until(() => steam.WorkshopItems.Count == 1) && steam.WorkshopItems[0].NeedsUpdate && steam.WorkshopItems[0].Bytes == 71_000_000L, "Workshop listings keep valid items and Steam's update state");
        Write(new { v = 1, ev = "ready", contract = 1, wire = 1, bridge = "test", steam = true, appId = 824270, self = new { peer = self, name = "Synthetic Host", initials = "SH" }, relay = "Current", features = new[] { "lobby", "p2p", "ugc", "ugc-query", "xfer" }, maxChunk = 32768, xferWindow = 4 });
        Check(Until(() => steam.QueryWorkshop(MapPorts.TitlePrefix)), "With ugc-query the Workshop is searched");
        var query = Expect("ugc.query");
        Check(query.GetProperty("text").GetString() == "AimMod - " && !query.TryGetProperty("tag", out _) && !steam.QueryWorkshop(null), "Ports are found by title text (KovaaK's uploads carry no tags); empty queries never go out");
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
        Picks(root, library);
        Developer(root, library);
        Cosmetics(root, library);
    }

    static void Follow()
    {
        LobbyMember M(string id, bool sim = false) => new(id, id, MemberRoles.Player, true, null, ContentStates.Ok, ContentStates.Ok, ContentStates.None, Connections.Connected, "relay", 1, sim);
        ScoreLine L(string id, double? score, string status = "playing") => new(id, score, 10, 50, 10, 5, 5, status, false);
        MatchSnapshot Match(params ScoreLine[] live) => new("m", MatchPhases.Live, LobbyModes.Race, "Synthetic A", 60, 1, null, null, 0, null, null, live.Select(l => l.MemberId).ToArray(), live, [], [], null, []);
        var members = new[] { M("me"), M("a"), M("b"), M("bot", sim: true) };
        Check(MultiplayerService.LeaderOf(Match(L("me", 9000), L("a", 4000), L("b", 5000), L("bot", 8000)), members, "me") == "b", "Follow the leader skips yourself and simulated players");
        Check(MultiplayerService.LeaderOf(Match(L("a", 4000), L("b", 6000, "left")), members, "me") == "a" && MultiplayerService.LeaderOf(Match(L("a", null)), members, "me") is null, "Players who left or have no score yet aren't followed");
    }

    // A player still inside a challenge run: the round waits (never "start it yourself") and loads once it ends.
    // Leaving the run for the match: 5 s notice with Stay, the lobby key leaves now, then quit-run and load.
    static void AutoLeave(string root)
    {
        long now = 4_500_000;
        var control = new FakeGame("load", "start", "quit");
        var service = new MultiplayerService(new OfflineTransport(), new ContentLibrary(Path.Combine(root, "game")), control, () => new LocalRun(false, null, null, null, null, 0, 0, 0, null), () => [], () => null, null, simulation: true, () => now, autoTick: false, seed: 6);
        JsonElement Round() => JsonSerializer.SerializeToElement(service.View(), Protocol.Json).GetProperty("lobby").GetProperty("round");
        JsonElement Notice() => JsonDocument.Parse(service.NoticeText()).RootElement;
        void Run(int ms) { for (var t = 0; t < ms; t += 100) { now += 100; service.Tick(); } }
        service.Act("create", J(new { mode = "score-race", scenario = "Synthetic A" }));
        service.Act("sim", J(new { op = "add" }));
        Run(9000);
        control.ChallengeRunning = true;
        service.Act("start", default); Run(300);
        var n = Notice();
        Check(n.GetProperty("title").GetString()!.StartsWith("Leaving your run for the match in ", StringComparison.Ordinal) && n.GetProperty("countdown").GetInt32() is > 0 and <= 5
            && n.GetProperty("actions").EnumerateArray().Any(x => x.GetProperty("action").GetString() == "leave-run-cancel"), "A run in progress gets a 5 s leave notice with Stay");
        Check(!control.Calls.Contains("quit"), "Nothing is quit before the countdown ends");
        Run(5000);
        Check(control.Calls.Count(c => c == "quit") == 1 && Round().GetProperty("message").GetString()!.StartsWith("Leaving your run", StringComparison.Ordinal), "After 5 s the run is left with quit-run");
        control.ChallengeRunning = false; Run(300);
        Check(control.Calls.Last() == "load Synthetic A", "Then the match scenario loads");
        service.Act("end", default); Run(6000);
        // Stay: cancel keeps the run and falls back to the manual message.
        control.ChallengeRunning = true;
        service.Act("start", default); Run(300);
        Check(service.Act("leave-run-cancel", J(new { id = "x" })).Ok, "Stay is accepted");
        Run(6000);
        Check(control.Calls.Count(c => c == "quit") == 1 && Round().GetProperty("message").GetString()!.Contains("Finish or quit your current run"), "Staying never quits; the manual message returns");
        control.ChallengeRunning = false; Run(300);
        service.Act("end", default); Run(6000);
        // The lobby key leaves at once.
        control.ChallengeRunning = true;
        service.Act("start", default); Run(300);
        service.Hotkey(); Run(100);
        Check(control.Calls.Count(c => c == "quit") == 2, "The lobby key leaves the run at once");
        control.ChallengeRunning = false; Run(300);
        service.Act("end", default); Run(6000);
        // Preference off, or no quit capability: the manual message, no quit.
        service.Act("prefs", J(new { prefs = new { leaveRun = false } }));
        control.ChallengeRunning = true;
        service.Act("start", default); Run(6000);
        var off = Notice();
        Check(control.Calls.Count(c => c == "quit") == 2 && !(off.GetProperty("active").GetBoolean() && off.GetProperty("title").GetString()!.StartsWith("Leaving", StringComparison.Ordinal)), "With the preference off nothing is left automatically");
        service.Dispose();
    }

    static void Blocked(string root)
    {
        long now = 4_000_000;
        var control = new FakeGame("load", "start");
        var service = new MultiplayerService(new OfflineTransport(), new ContentLibrary(Path.Combine(root, "game")), control, () => new LocalRun(false, null, null, null, null, 0, 0, 0, null), () => [], () => null, null, simulation: true, () => now, autoTick: false, seed: 5);
        JsonElement Round() => JsonSerializer.SerializeToElement(service.View(), Protocol.Json).GetProperty("lobby").GetProperty("round");
        string Phase() => JsonSerializer.SerializeToElement(service.View(), Protocol.Json).GetProperty("lobby").GetProperty("match").GetProperty("phase").GetString()!;
        void Run(int ms) { for (var t = 0; t < ms; t += 100) { now += 100; service.Tick(); } }
        service.Act("create", J(new { mode = "score-race", scenario = "Synthetic A" }));
        service.Act("sim", J(new { op = "add" }));
        Run(9000);
        control.ChallengeRunning = true;
        Check(service.Act("start", default).Ok, "The host starts while still in a challenge run");
        Run(500);
        Check(Round().GetProperty("state").GetString() == "blocked" && Round().GetProperty("message").GetString()!.Contains("Finish or quit your current run") && !control.Calls.Any(c => c.StartsWith("load", StringComparison.Ordinal)), "A running challenge holds the load with a finish-or-quit message, not start-it-yourself");
        Run(3000);
        Check(Phase() == MatchPhases.Loading, "The warm-up counts a player still in a run as not loaded yet");
        control.ChallengeRunning = false;
        Run(300);
        Check(control.Calls.Count(c => c == "load Synthetic A") == 1 && Round().GetProperty("state").GetString() is "loading" or "ready", "Once the challenge ends the scenario loads by itself");
        service.Act("end", default); Run(6000);
        // A challenge that starts between the check and the load: AimModCore answers challenge-active.
        control.RefuseNextLoad();
        Check(service.Act("start", default).Ok, "The next match starts"); Run(500);
        Check(Round().GetProperty("state").GetString() == "blocked", "challenge-active from AimModCore holds the round too");
        Check(control.Calls.Count(c => c == "load Synthetic A") == 2, "Retries wait a moment instead of hammering the game");
        control.Accept(); Run(2500);
        Check(control.Calls.Count(c => c == "load Synthetic A") == 3 && Round().GetProperty("state").GetString() is "loading" or "ready", "The load is retried once the game is free");
        service.Dispose();
    }

    static void Marker()
    {
        const long now = 1_790_000_000;
        const string name = "AimMod Match - Cata IC Long Strafes - Timed - ab93b242";
        string Text(string mode = "match", string scenario = name, string expires = "1790000060", string v = "1") => "v=" + v + "\nmode=" + mode + "\nscenario=" + scenario + "\nexpires=" + expires + "\n";
        Check(SessionMarker.Read(Text(), now) is { Mode: "match", Scenario: name, Expires: now + 60 }, "The reader accepts the example marker (expires = now + 60)");
        Check(SessionMarker.Read(SessionMarker.Format("match", name, now + SessionMarker.LifetimeSeconds), now) is { Mode: "match" } && SessionMarker.Read(SessionMarker.Format("lobby", null, now + 90), now) is { Mode: "lobby", Scenario: "" }, "What the service writes, the reader accepts");
        Check(SessionMarker.Read(Text(v: "2"), now) is null && SessionMarker.Read(Text(mode: "ranked"), now) is null && SessionMarker.Read(Text(expires: "soon"), now) is null, "Version 2, mode ranked and a non-numeric expiry are rejected");
        Check(SessionMarker.Read(Text(expires: now.ToString()), now) is null && SessionMarker.Read(Text(expires: (now - 5).ToString()), now) is null && SessionMarker.Read(Text(expires: (now + 3600).ToString()), now) is null, "Expired markers and ones too far ahead are rejected");
        Check(SessionMarker.Read(Text() + new string('x', 1100), now) is null, "Markers over 1 KiB are rejected");
        Check(SessionMarker.Read(Text(scenario: "AimMod Match - Cata IC Long Strafes - Timed - AB93B242"), now) is null && SessionMarker.Read(Text(scenario: "AimMod Match - Cata - Timed - ab93b24"), now) is null
            && SessionMarker.Read(Text(scenario: "AimMod Match - Cata - Timed - ab93b2421"), now) is null && SessionMarker.Read(Text(scenario: "Cata IC Long Strafes - Timed - ab93b242"), now) is null, "Uppercase, 7- or 9-digit keys and a missing prefix are rejected");
        Check(SessionMarker.Format("match", "AimMod Match - Bad\nmode=match - ab93b242", now) is null && SessionMarker.Format("match", "Cata IC Long Strafes", now) is null && SessionMarker.Format("ranked", name, now) is null, "Nothing is written for control characters, normal scenarios or unknown modes");
    }

    static void Cosmetics(string root, ContentLibrary library)
    {
        var folder = Path.Combine(root, "cosmetics");
        Directory.CreateDirectory(folder);
        const string catalog = """
            {"version":1,"items":[
              {"id":"meso-tint-ember","version":1,"kind":"avatar_tint","name":"Ember","models":["Meso"],"parts":["body"],"vector":{"PrimaryColor":{"R":0.85,"G":0.22,"B":0.05,"A":1}}},
              {"id":"weapon-finish-sand","version":2,"kind":"weapon_finish","name":"Sand","parts":["weapon"],"vector":{"PrimaryColor":{"R":0.76,"G":0.66,"B":0.48,"A":1}}},
              {"id":"meso-tint-draft","version":1,"kind":"avatar_tint","name":"Draft","models":["Meso"],"parts":["body"],"vector":{"PrimaryColor":{"R":1,"G":0,"B":0,"A":1}},"draft":true},
              {"id":"accessory-halo","version":1,"kind":"accessory","name":"Halo","models":["Meso"],"parts":["body"],"pak":{"file":"AimModCosmetics-1.pak"}},
              {"id":"bad-kind","version":1,"kind":"rocket","parts":["body"],"models":["Meso"],"vector":{"PrimaryColor":{"R":1,"G":1,"B":1,"A":1}}},
              {"id":"Bad-Id","version":1,"kind":"avatar_tint","parts":["body"],"models":["Meso"],"vector":{"PrimaryColor":{"R":1,"G":1,"B":1,"A":1}}},
              {"id":"twice","version":1,"kind":"weapon_finish","parts":["weapon"],"vector":{"PrimaryColor":{"R":1,"G":1,"B":1,"A":1}}},
              {"id":"twice","version":1,"kind":"weapon_finish","parts":["weapon"],"vector":{"PrimaryColor":{"R":0,"G":0,"B":0,"A":1}}},
              {"id":"meso-tint-hot","version":1,"kind":"avatar_tint","parts":["body"],"models":["Meso"],"vector":{"PrimaryColor":{"R":2,"G":0,"B":0,"A":1}}}]}
            """;
        File.WriteAllText(Path.Combine(folder, CosmeticsCatalog.CatalogFile), catalog);
        void Manifest(string sha) => File.WriteAllText(Path.Combine(folder, CosmeticsCatalog.ManifestFile), JsonSerializer.Serialize(new { version = 1, files = new[] { new { name = CosmeticsCatalog.CatalogFile, size = new FileInfo(Path.Combine(folder, CosmeticsCatalog.CatalogFile)).Length, sha256 = sha } } }));
        Manifest("00");
        Check(!CosmeticsCatalog.Load(folder, null).Available && CosmeticsCatalog.Load(folder, null).Problem == "manifest-mismatch", "A catalog that doesn't match its manifest is not used");
        Manifest(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, CosmeticsCatalog.CatalogFile)))));
        var loaded = CosmeticsCatalog.Load(folder, null);
        Check(loaded.Available && loaded.Pickable.Select(i => i.Id).OrderBy(x => x).SequenceEqual(["meso-tint-ember", "weapon-finish-sand"]), "Only valid, non-draft items with their paks are pickable; bad kinds, ids, ranges and duplicates are dropped");
        Check(loaded.Filter([new("meso-tint-ember", 1), new("unknown-item", 1), new("weapon-finish-sand", 1), new("accessory-halo", 1)]).Select(r => r.Id).SequenceEqual(["meso-tint-ember"]), "Shared looks resolve only to the same id and version in the viewer's own catalog");
        Check(CosmeticLooks.Format([new("meso-tint-ember", 1)], [("76561190000000001", [new CosmeticRef("weapon-finish-sand", 2)]), ("sim-bot", [new CosmeticRef("meso-tint-ember", 1)])])
            == "v=1\npeer=76561190000000001 items=weapon-finish-sand@2\nself=meso-tint-ember@1\n", "cosmetic-looks.txt has v=1, Steam peers only, and a self line");
        // Character preview request (AimModCore's ParsePreviewRequest reads it).
        var tint = loaded.Pickable.First(i => i.Id == "meso-tint-ember");
        var previewBody = CosmeticPreviewFormat.Body("Meso", "McCree", -35.5, [tint]);
        Check(previewBody is not null && previewBody.StartsWith("model=Meso\nskin=McCree\nyaw=-35.5\nvector=", StringComparison.Ordinal), "Preview request carries the look, the rotation and the item's parameters");
        Check(CosmeticPreviewFormat.Body("Endo", "Default", 0, []) == "model=Endo\nyaw=0\n", "A model's default skin is not sent");
        Check(CosmeticPreviewFormat.Body("../Meso", null, 0, []) is null && CosmeticPreviewFormat.Body("Meso", "C:/me.png", 0, []) is null, "Preview names are look names, never paths");
        Check(CosmeticPreviewFormat.Body("Meso", null, 999, [])!.Contains("yaw=180\n"), "Preview rotation is clamped");
        var bad = tint with { Vectors = new Dictionary<string, double[]> { ["Bad Name"] = [1, 0, 0, 1], ["Hot"] = [5, 0, 0, 1] } };
        Check(!CosmeticPreviewFormat.Body("Meso", null, 0, [bad])!.Contains("vector="), "Preview drops parameters with bad names or values");
        Check(CosmeticPreviewFormat.Request("model=Meso\nyaw=0\n", 3, 100) == "v=1\nexpires=105\nseq=3\nmodel=Meso\nyaw=0\n", "Preview request expires within seconds");
        Check(CosmeticPreviewFormat.Frame("v=1\nseq=4\nfile=preview-1.png\nwidth=384\nheight=384\n") == (4, "preview-1.png"), "Preview frame record parses");
        Check(CosmeticPreviewFormat.Frame("v=1\nseq=4\nfile=../secret.png\n") is null && CosmeticPreviewFormat.Frame(null) is null, "Preview frame only names AimMod's own PNGs");
        // Service: equip, view, the looks file with the session marker.
        long now = 8_000_000;
        var output = Path.Combine(root, "cos-output"); Directory.CreateDirectory(output);
        var service = new MultiplayerService(new OfflineTransport(), library, new NoGameControl(), () => new LocalRun(false, null, null, null, null, 0, 0, 0, null), () => [], () => null, output, simulation: true, () => now, autoTick: false) { CosmeticsFolder = folder };
        Check(service.Act("cosmetic-equip", J(new { id = "meso-tint-ember" })).Ok && !service.Act("cosmetic-equip", J(new { id = "accessory-halo" })).Ok && !service.Act("cosmetic-equip", J(new { id = "unknown-item" })).Ok, "Only pickable catalog items can be equipped");
        var view = JsonSerializer.SerializeToElement(service.CosmeticsView(), Protocol.Json);
        Check(view.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == "meso-tint-ember").GetProperty("equipped").GetBoolean() && view.GetProperty("show").GetString() == "all", "The Cosmetics page shows what's equipped; others' cosmetics default to all");
        var looksFile = Path.Combine(output, CosmeticLooks.FileName);
        Check(!File.Exists(looksFile), "No looks file outside an AimMod session");
        service.Act("create", J(new { mode = "practice", scenario = "Synthetic Plain" }));
        service.Act("sim", J(new { op = "add" }));
        for (var i = 0; i < 5; i++) { now += 100; service.Tick(); }
        var lobby = JsonSerializer.SerializeToElement(service.View(), Protocol.Json).GetProperty("lobby");
        Check(lobby.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("id").GetString() == service.SelfId).GetProperty("cosmetics")[0].GetProperty("id").GetString() == "meso-tint-ember", "My look is in the lobby snapshot (cosmetic.look)");
        Check(File.ReadAllText(looksFile) == "v=1\nself=meso-tint-ember@1\n", "The looks file is written with the session marker");
        Check(service.Act("cosmetic-view", J(new { show = "off" })).Ok && !service.Act("cosmetic-view", J(new { show = "everyone" })).Ok, "Show others' cosmetics: all, friends or off");
        service.Act("leave", default);
        Check(!File.Exists(looksFile) && !File.Exists(Path.Combine(output, SessionMarker.FileName)), "The looks file goes with the session marker");
        service.Dispose();
    }

    static void Developer(string root, ContentLibrary library)
    {
        long now = 7_000_000;
        var output = Path.Combine(root, "dev-output");
        Directory.CreateDirectory(output);
        var service = new MultiplayerService(new OfflineTransport(), library, new NoGameControl(), () => new LocalRun(false, null, null, null, null, 0, 0, 0, null), () => [], () => null, output, simulation: false, () => now, autoTick: false, seed: 3);
        var mode = new AimMod.InGame.Developer.DeveloperMode(output);
        LobbyResult Dev(object body) => AimMod.InGame.Developer.DeveloperEndpoints.Act(mode, service, JsonSerializer.SerializeToElement(body));
        Check(!mode.Enabled && !service.SimulationOn && !Dev(new { action = "lobby", members = 3 }).Ok, "Developer mode is off by default and its tools are refused");
        Check(Dev(new { action = "enable", on = true }).Ok && service.SimulationOn && new AimMod.InGame.Developer.DeveloperMode(output).Enabled, "Turning developer mode on starts the simulation and is saved");
        Check(Dev(new { action = "lobby", members = 5, mode = LobbyModes.Rounds }).Ok, "A simulated lobby of any size is created");
        var lobby = JsonSerializer.SerializeToElement(service.View(), Protocol.Json).GetProperty("lobby");
        Check(lobby.GetProperty("members").EnumerateArray().Count(m => m.GetProperty("simulated").GetBoolean()) == 5 && lobby.GetProperty("settings").GetProperty("maxPlayers").GetInt32() >= 6 && lobby.GetProperty("isHost").GetBoolean(), "Five simulated players join your lobby, with room for everyone");
        Check(lobby.GetProperty("settings").GetProperty("scenario").GetProperty("name").GetString() == "AimMod - Dust2 (CSGO) - CS Movement", "Developer lobbies default to an installed AimMod map");
        for (var i = 0; i < 40; i++) { now += 100; service.Tick(); }
        Check(Dev(new { action = "sim", op = "chat" }).Ok && Dev(new { action = "sim", op = "away" }).Ok && Dev(new { action = "sim", op = "suggest" }).Ok, "Simulated players chat, go away and suggest on demand");
        lobby = JsonSerializer.SerializeToElement(service.View(), Protocol.Json).GetProperty("lobby");
        Check(lobby.GetProperty("members").EnumerateArray().Any(m => m.GetProperty("away").GetBoolean()) && lobby.GetProperty("suggestions").GetArrayLength() == 1, "Away and suggestions show in the lobby");
        Check(Dev(new { action = "lobby", members = 2, simulatedHost = true }).Ok && !JsonSerializer.SerializeToElement(service.View(), Protocol.Json).GetProperty("lobby").GetProperty("isHost").GetBoolean(), "A simulated host's lobby can be joined");
        Dev(new { action = "leave" });
        foreach (var kind in MultiplayerService.DevNotices)
        {
            Check(Dev(new { action = "notice", kind }).Ok, "Notice " + kind + " can be triggered");
            now += 50; service.Tick();
            var n = JsonDocument.Parse(service.NoticeText()).RootElement;
            Check(n.GetProperty("active").GetBoolean() || n.GetProperty("badge").ValueKind == JsonValueKind.String, "Notice " + kind + " shows in game");
            Act(service, n);
            now += 21_000; service.Tick();
        }
        Check(!Dev(new { action = "notice", kind = "nope" }).Ok, "Unknown notices are refused");
        // Tools: content loopback into a scratch folder (and a forced hash failure), the delayed self stream, redaction.
        using (var tools = new AimMod.InGame.Developer.DeveloperTools(output, library, service))
        {
            LobbyResult Tool(object body) => AimMod.InGame.Developer.DeveloperEndpoints.Act(mode, service, JsonSerializer.SerializeToElement(body), tools);
            JsonElement ToolView() => JsonSerializer.SerializeToElement(tools.View(), Protocol.Json);
            bool Wait(Func<bool> done) { for (var i = 0; i < 80 && !done(); i++) Thread.Sleep(50); return done(); }
            const string port = "AimMod - Dust2 (CSGO) - CS Movement";
            Check(Tool(new { action = "content", scenario = port }).Ok && Wait(() => ToolView().GetProperty("content").GetProperty("state").GetString() == "done")
                && File.Exists(Path.Combine(output, "dev-loopback", "game", "Saved", "SaveGames", "Scenarios", port + ".sce")), "The content loopback copies the scenario through the transfer pipeline into a scratch folder");
            Check(Tool(new { action = "content", scenario = port, fail = true }).Ok && Wait(() => ToolView().GetProperty("content").GetProperty("state").GetString() == "error")
                && ToolView().GetProperty("content").GetProperty("code").GetString() == "hash", "A corrupted chunk is caught by the hash check");
            var now0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            File.WriteAllText(Path.Combine(output, "self-pose.tsv"), "AIMMOD_POSE_1\t1\nmeta\tSynthetic%20A\tsynthetic_map\t4\npose\t" + (now0 - 900) + "\t1\t2\t3\t-5\t90\t0\t100\npose\t" + (now0 - 800) + "\t1\t2\t3\t-5\t91\t0\t100\n");
            Check(Tool(new { action = "loopback", source = "self", delay = 0.5 }).Ok && Wait(() => File.Exists(Path.Combine(output, "spectate-pose.tsv"))), "Spectate yourself writes the loopback stream");
            var looped = LivePoseFrame.Parse(File.ReadAllText(Path.Combine(output, "spectate-pose.tsv")));
            Check(looped is { Scenario: "Synthetic A" } && looped.Poses[^1].UnixMs == now0 - 800 + 500, "Your own view comes back delayed, re-stamped as live, with its scenario");
            Check(Tool(new { action = "loopback", source = "off" }).Ok && !File.Exists(Path.Combine(output, "spectate-pose.tsv")), "Stopping the loopback removes its stream");
            Check(AimMod.InGame.Developer.DeveloperTools.Redact(@"peer 76561198000000001 token 0123456789abcdef0123456789abcdef at C:\Users\Someone\AppData") == @"peer <steam id> token <id> at C:\Users\<user>\AppData", "Logs shown on the page have ids and user names redacted");
            Check(!Tool(new { action = "import", path = Path.Combine(output, "nope.amreplay") }).Ok && !Tool(new { action = "avatar-path", replay = "missing" }).Ok, "Missing replays are refused");
        }
        Check(Dev(new { action = "enable", on = false }).Ok && !service.SimulationOn && !Dev(new { action = "notice", kind = "ready" }).Ok, "Turning developer mode off stops the simulation and the tools");
        service.Dispose();
        static void Act(MultiplayerService s, JsonElement n)
        {
            // Clear popups that wait for an answer, as the player would.
            if (n.TryGetProperty("actions", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
            {
                var last = list[list.GetArrayLength() - 1];
                s.Act(last.GetProperty("action").GetString()!, JsonSerializer.SerializeToElement(new { id = last.GetProperty("id").GetString() }));
            }
        }
    }

    static void DevAvatarChecks()
    {
        long now = 3_000_000;
        var net = new MemoryNetwork(); var t = new MemoryTransport(net, "dev-a"); net.Peers["dev-a"] = t;
        var service = new MultiplayerService(t, new ContentLibrary(null), new NoGameControl(), () => new LocalRun(false, null, null, null, null, 0, 0, 0, null), () => [], () => null, null, false, () => now, autoTick: false);
        service.SetSimulation(true);
        service.Act("avatar", J(new { avatar = "meso-tracer" }));
        Check(service.DevAvatar(true, "circle").Ok && service.DevAvatar(true, "circle").Ok && service.DevAvatar(true, "circle").Ok && t.DevAvatars.SequenceEqual(["on circle AimMod Meso Tracer"]), "Repeated spawn clicks send one dev.avatar, wearing the chosen look");
        service.Act("avatar", J(new { avatar = "meso-genji" }));
        Check(t.DevAvatars.Last() == "on circle AimMod Meso Genji", "Changing the look re-dresses the running test avatar");
        Check(service.DevAvatar(false, "circle").Ok && service.DevAvatar(false, "circle").Ok && t.DevAvatars.Count(x => x.StartsWith("off", StringComparison.Ordinal)) == 1, "Repeated despawn clicks send one off");
        now += 4000;
        Check(service.DevAvatar(false, "circle").Ok && t.DevAvatars.Count(x => x.StartsWith("off", StringComparison.Ordinal)) == 2, "A later request goes out again, in case the game lost track");
        service.Dispose();
    }

    static void Picks(string root, ContentLibrary library)
    {
        long now = 6_000_000;
        var output = Path.Combine(root, "picks-output");
        Directory.CreateDirectory(output);
        MultiplayerService Make() => new(new OfflineTransport(), library, new NoGameControl(), () => new LocalRun(false, null, null, null, null, 0, 0, 0, null), () => [], () => null, output, simulation: false, () => now, autoTick: false);
        var service = Make();
        JsonElement Picks() => JsonSerializer.SerializeToElement(service.View(), Protocol.Json).GetProperty("picks");
        const string port = "AimMod - Dust2 (CSGO) - CS Movement";
        Check(service.Act("favourite", J(new { scenario = port })).Ok && !service.Act("favourite", J(new { scenario = "Not In Library" })).Ok, "Favourites take scenarios from the library only");
        Check(service.Act("create", J(new { mode = "practice", scenario = "Synthetic Plain" })).Ok && service.Act("settings", J(new { settings = new { scenario = port } })).Ok, "The host picks scenarios");
        var picks = Picks();
        Check(picks.GetProperty("favourites")[0].GetString() == port && picks.GetProperty("recent")[0].GetString() == port, "Picked scenarios become recent, newest first");
        service.Dispose();
        service = Make();
        Check(Picks().GetProperty("favourites").GetArrayLength() == 1, "Favourites survive a restart");
        Check(service.Act("favourite", J(new { scenario = port, on = false })).Ok && Picks().GetProperty("favourites").GetArrayLength() == 0, "Favourites can be removed");
        service.Dispose();
        // Rivals: head-to-head over saved matches, by key, with the newest name.
        RecentPlayer P(string name, int place, bool self, string key) => new(name, place, 100 - place, 0, 0, self, key);
        var saved = new[]
        {
            new RecentMatch("m3", 3000, LobbyModes.Race, "Synthetic A", 1, 2, "Synthetic One", true, false, [P("Synthetic One", 1, true, "me0000000000"), P("Rival Renamed", 2, false, "rival0000000")]),
            new RecentMatch("m2", 2000, LobbyModes.Race, "Synthetic A", 2, 2, "Synthetic Rival", false, false, [P("Synthetic Rival", 1, false, "rival0000000"), P("Synthetic One", 2, true, "me0000000000")]),
            new RecentMatch("m1", 1000, LobbyModes.Duel, "Synthetic A", 1, 2, "Synthetic One", true, false, [P("Synthetic One", 1, true, "me0000000000"), P("Synthetic Rival", 2, false, "rival0000000")]),
            new RecentMatch("m0", 900, LobbyModes.Practice, "Synthetic A", null, 2, null, false, false, [P("Synthetic One", 1, true, "me0000000000"), P("Synthetic Rival", 2, false, "rival0000000")]),
            new RecentMatch("mx", 800, LobbyModes.Race, "Synthetic A", 1, 2, "Synthetic One", true, false, [P("Synthetic One", 1, true, "me0000000000"), P("Once Only", 2, false, "once00000000")]),
        };
        File.WriteAllText(Path.Combine(output, "multiplayer-matches.json"), JsonSerializer.Serialize(saved, Protocol.Json));
        service = Make();
        var rivals = JsonSerializer.SerializeToElement(service.HistoryView(), Protocol.Json).GetProperty("rivals");
        Check(rivals.GetArrayLength() == 1 && rivals[0].GetProperty("name").GetString() == "Rival Renamed" && rivals[0].GetProperty("played").GetInt32() == 3 && rivals[0].GetProperty("won").GetInt32() == 2 && rivals[0].GetProperty("lost").GetInt32() == 1,
            "Rivals count scored matches only, keep the newest name and need two meetings");
        Check(JsonSerializer.SerializeToElement(service.View(), Protocol.Json).GetProperty("recent").GetArrayLength() == 5, "Older history files still load");
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
        // Temporary and marked: own tag, marker description; cleanup removes only marked, generated names.
        Check(one.Contains("SearchTags=" + MatchScenario.Tag + "\n") && one.Contains("Description=" + MatchScenario.Marker + "Synthetic A.") && !BaseScenario.Contains("SearchTags"), "Match scenarios carry only the AimMod Match tag and the generated marker");
        var marked = MatchScenario.Prefix + "Old Base - Timed - ab93b242";
        WriteText(Path.Combine(folder, marked + ".sce"), "Name=" + marked + "\nDescription=" + MatchScenario.Marker + "Old Base. Played in freeplay; not a published scenario.\nSearchTags=KovaaK, Reflex\n\n[Map Data]\n");
        var lookalike = MatchScenario.Prefix + "Mine - Timed - 12345678";
        WriteText(Path.Combine(folder, lookalike + ".sce"), "Name=" + lookalike + "\nDescription=My own scenario\n");
        WriteText(Path.Combine(folder, "AimMod Match - notes.sce"), "Description=" + MatchScenario.Marker + "x\n");
        var keepName = MatchScenario.Prefix + "Synthetic A - CS - 11111112";
        WriteText(Path.Combine(folder, keepName + ".sce"), "Name=" + keepName + "\nDescription=" + MatchScenario.Marker + "Synthetic A.\n");
        Check(store.Clean(keepName) == 2 && !File.Exists(Path.Combine(folder, marked + ".sce")) && File.Exists(Path.Combine(folder, keepName + ".sce")), "Cleanup removes leftover match scenarios (including older builds') but keeps the current lobby's");
        Check(File.Exists(Path.Combine(folder, lookalike + ".sce")) && File.Exists(Path.Combine(folder, taken + ".sce")) && File.Exists(Path.Combine(folder, "AimMod Match - notes.sce")), "Files without the marker or the generated name pattern are never deleted");
        Check(store.Clean(null) == 1 && !File.Exists(Path.Combine(folder, keepName + ".sce")) && store.Files().Count == 0, "Leaving removes the last one too");
        Check(MatchScenario.SafeMode(MatchScenario.Name(cs), "challenge") == "freeplay" && MatchScenario.SafeMode("Synthetic A", "challenge") == "challenge", "Match scenarios never start as challenges");
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
        Check(!View(joiner).GetProperty("prefs").GetProperty("onboarded").GetBoolean() && joiner.Act("prefs", J(new { prefs = new { onboarded = true } })).Ok && View(joiner).GetProperty("prefs").GetProperty("onboarded").GetBoolean(), "The first-run tour is shown until it is finished once");
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
        public long? Start(string scenario, string mode, long? seed = null) { Calls.Add("start " + mode + " " + scenario + (seed is long s ? " seed " + s : "")); return lastStart = Calls.Count; }
        public long? Refresh() { Calls.Add("refresh"); return Calls.Count; }
        // A challenge still running in KovaaK's (core-scene.json); while true, loads answer challenge-active.
        public bool? ChallengeRunning { get; set; }
        bool refusedLoad;
        // Answers like AimModCore: the latest load is done, then the latest start.
        public GameCommandResult? Result => lastStart > lastLoad ? new GameCommandResult(lastStart, "done", "started", "")
            : lastLoad > 0 ? (refusedLoad ? new GameCommandResult(lastLoad, "error", "challenge-active", "") : new GameCommandResult(lastLoad, "done", "loaded", "")) : null;
        // The next load is refused as if a challenge started right after the check.
        public void RefuseNextLoad() => refusedLoad = true;
        public long? QuitRun() { if (!Capabilities.Contains("quit")) return null; Calls.Add("quit"); return lastLoad = Calls.Count; }
        public void Accept() => refusedLoad = false;
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
        // A friend starting AimMod: one in-game toast with Watch, never for friends online at start.
        service.Tick();
        Check(!JsonDocument.Parse(service.NoticeText()).RootElement.GetProperty("active").GetBoolean(), "Friends already online at start don't toast");
        service.Act("sim", J(new { op = "friend-online" })); now += 1500; service.Tick();
        var toast = JsonDocument.Parse(service.NoticeText()).RootElement;
        Check(toast.GetProperty("active").GetBoolean() && toast.GetProperty("title").GetString() == "Vesper is on AimMod" && toast.GetProperty("actions").EnumerateArray().Any(a => a.GetProperty("action").GetString() == "friend-watch" && a.GetProperty("id").GetString() == "sim-f4"), "A friend starting AimMod gets a toast with Watch");
        Check(service.Act("friend-dismiss", J(new { id = "sim-f4" })).Ok && !JsonDocument.Parse(service.NoticeText()).RootElement.GetProperty("active").GetBoolean(), "The toast can be dismissed");
        service.Act("sim", J(new { op = "friend-online" })); now += 1500; service.Tick(); service.Act("sim", J(new { op = "friend-online" })); now += 30_000; service.Tick();
        Check(!JsonDocument.Parse(service.NoticeText()).RootElement.GetProperty("active").GetBoolean(), "The same friend doesn't toast again within half an hour");
        service.Act("sim", J(new { op = "friend-online" }));
        Check(!service.Act("join", J(new { code = "bad" })).Ok, "Bad room codes are refused");
        WriteText(Path.Combine(output, SessionMarker.FileName), "v=1\nmode=match\nscenario=AimMod Match - Old - Timed - ab93b242\nexpires=9999999999\n");
        var markerService = new MultiplayerService(new OfflineTransport(), new ContentLibrary(game), new NoGameControl(), () => new LocalRun(false, null, null, null, null, 0, 0, 0, null), () => [], () => null, output, simulation: false, () => now, autoTick: false);
        Check(!File.Exists(Path.Combine(output, SessionMarker.FileName)), "A marker left by a crash is deleted at startup");
        markerService.Dispose();
        Check(service.Act("create", J(new { mode = "score-race", scenario = "Synthetic A" })).Ok && !service.Act("create", default).Ok, "One lobby at a time");
        service.Tick();
        Check(SessionMarker.Read(File.ReadAllText(Path.Combine(output, SessionMarker.FileName)), now / 1000) is { Mode: "lobby", Scenario: "" }, "In a lobby the marker says lobby, with no scenario");
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
        Check(!File.Exists(Path.Combine(output, SessionMarker.FileName)), "No marker while a normal scenario plays");
        Check(View().GetProperty("lobby").GetProperty("match").GetProperty("live").EnumerateArray().Count(l => l.GetProperty("status").GetString() == "playing") >= 2, "Live score frames arrive");
        Run(62_000);
        var match = View().GetProperty("lobby").GetProperty("match");
        Check(match.GetProperty("phase").GetString() == MatchPhases.Final && match.GetProperty("standings").GetArrayLength() == 3, "The simulated race reaches its final results");
        Check(View().GetProperty("recent").GetArrayLength() == 1 && File.Exists(Path.Combine(output, "multiplayer-matches.json")), "Finished matches are kept locally, apart from KovaaK's leaderboards");
        var history = JsonSerializer.SerializeToElement(service.HistoryView(), Protocol.Json);
        var kept = history.GetProperty("matches")[0];
        Check(kept.GetProperty("standings").EnumerateArray().All(p => p.GetProperty("key").GetString()!.Length == 12) && !history.ToString().Contains(service.SelfId, StringComparison.Ordinal) && kept.GetProperty("rounds").GetInt32() == 1,
            "History keeps opponents by a hashed key, never by their id");
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
        Run(200);
        Check(File.Exists(Path.Combine(game, "Saved", "SaveGames", "Scenarios", name + ".sce")), "The lobby's match scenario stays while the lobby needs it (play again, rematch)");
        var markerFile = Path.Combine(output, SessionMarker.FileName);
        var marker = SessionMarker.Read(File.ReadAllText(markerFile), now / 1000);
        Check(marker is { Mode: "match" } && marker.Scenario == name && !File.ReadAllBytes(markerFile).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "While the match scenario plays, the cosmetics marker says match with its exact name (UTF-8, no BOM)");
        service.Act("leave", default);
        Check(!File.Exists(markerFile), "Leaving deletes the cosmetics marker");
        var refreshes = control.Calls.Count(c => c == "refresh");
        Run(200);
        Check(!File.Exists(Path.Combine(game, "Saved", "SaveGames", "Scenarios", name + ".sce")) && control.Calls.Count(c => c == "refresh") == refreshes + 1, "Leaving removes the match scenario and refreshes KovaaK's list");
        // Tracking duel: the notice layer gets the duel HUD; this machine streams its self-pose feed.
        service.Act("leave", default);
        Check(service.Act("create", J(new { mode = "tracking-duel", scenario = "Synthetic A" })).Ok, "A tracking duel lobby opens");
        service.Act("sim", J(new { op = "add" }));
        Run(12_000);
        Check(service.Act("start", default).Ok, "The duel starts with a simulated opponent");
        Run(300);
        var countdown = JsonDocument.Parse(service.NoticeText()).RootElement;
        Check(countdown.GetProperty("duel").GetProperty("you").ValueKind == JsonValueKind.Null && countdown.GetProperty("duel").GetProperty("left").ValueKind == JsonValueKind.Null
            && countdown.GetProperty("body").GetString()!.Contains("dodge their aim", StringComparison.Ordinal), "Countdown: the toast says both players track and dodge");
        Check(File.Exists(Path.Combine(output, "self-pose.request")), "The duel asks AimModCore for the self-pose feed");
        static bool DuelLive(string notice) => JsonDocument.Parse(notice).RootElement.TryGetProperty("duel", out var d) && d.ValueKind == JsonValueKind.Object && d.GetProperty("phase").GetString() == MatchPhases.Live;
        for (var i = 0; i < 400 && !DuelLive(service.NoticeText()); i++) Run(100);
        Run(2000);
        var live = JsonDocument.Parse(service.NoticeText()).RootElement.GetProperty("duel");
        Check(live.GetProperty("phase").GetString() == MatchPhases.Live && live.GetProperty("left").GetInt32() is > 0 and <= 10 && live.GetProperty("you").ValueKind == JsonValueKind.Number
            && live.GetProperty("them").ValueKind == JsonValueKind.Number && live.GetProperty("rounds").GetInt32() == 3 && live.GetProperty("wins").GetInt32() == 0,
            "Live: the duel HUD has your score and theirs so far, round wins, seconds left and the round count");
        service.Act("end", default);
        // Deathmatch: the combat HUD and the avatar state for AimModSteam.
        service.Act("leave", default);
        Check(service.Act("create", J(new { mode = "deathmatch", scenario = "Synthetic A" })).Ok, "A deathmatch lobby opens");
        service.Act("sim", J(new { op = "add" }));
        Run(12_000);
        Check(service.Act("start", default).Ok, "The deathmatch starts with a simulated opponent");
        static bool CombatLive(string notice) => JsonDocument.Parse(notice).RootElement.TryGetProperty("combat", out var cb) && cb.ValueKind == JsonValueKind.Object && cb.GetProperty("phase").GetString() == MatchPhases.Live;
        for (var i = 0; i < 400 && !CombatLive(service.NoticeText()); i++) Run(100);
        Run(500);
        var hud = JsonDocument.Parse(service.NoticeText()).RootElement.GetProperty("combat");
        Check(hud.GetProperty("health").GetDouble() == 100 && hud.GetProperty("alive").GetBoolean() && hud.GetProperty("fragLimit").GetInt32() == 20 && hud.GetProperty("left").GetInt32() is > 290 and <= 300 && hud.GetProperty("protected").GetBoolean(),
            "Combat HUD: full health, spawn protection, frags against the limit and time left");
        var avatars = File.ReadAllText(Path.Combine(output, "avatar-state.tsv"));
        Check(avatars.StartsWith("AIMMOD_AVATARS_1\t", StringComparison.Ordinal) && avatars.Contains("\t1\tenemy\t100\t0\t0\n", StringComparison.Ordinal) && !avatars.Contains("peer\t" + service.SelfId, StringComparison.Ordinal),
            "avatar-state.tsv lists the other players' avatars (alive, enemy, health) for AimModSteam");
        service.Act("end", default);
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
        Check(invite.GetProperty("kind").GetString() == "incoming" && service.Notice() is { Kind: "invite", Invite: not null } n && n.Title.Contains("invited you to a score duel"), "An incoming invite shows a popup naming the mode and scenario");
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

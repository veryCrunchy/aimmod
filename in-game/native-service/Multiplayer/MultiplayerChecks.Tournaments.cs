using System.Net;
using System.Text;
using System.Text.Json;
using AimMod.InGame.Tournaments;

namespace AimMod.InGame.Multiplayer;

// Tournament play in the lobby service. Synthetic identities, a temporary
// game folder, the simulated Hub and a fake Hub transport only.
static partial class MultiplayerChecks
{
    sealed class TournamentFixture : HttpMessageHandler
    {
        public readonly List<(string Path, string? Auth, string Body)> Requests = [];
        public Func<string, (HttpStatusCode, string)> Answer = _ => (HttpStatusCode.OK, "{}");
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
            Requests.Add((request.RequestUri!.PathAndQuery, request.Headers.Authorization?.ToString(), body));
            var (status, text) = Answer(request.RequestUri.AbsolutePath);
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    static void Tournaments(string root)
    {
        TournamentRules();
        TournamentHubClient(root);
        TournamentSeedCommand(root);
        TournamentSimulation(root);
        TournamentBridge();
    }

    // The bridge's tournament lobbies (multiplayer.md): the host creates an invisible
    // lobby for one entrant and the match token; the opponent joins by id with it.
    static void TournamentBridge()
    {
        var name = "aimmod-steam-test-" + Guid.NewGuid().ToString("N")[..10];
        using var server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous, 1 << 17, 1 << 17);
        using var steam = new SteamTransport(name);
        Check(server.WaitForConnectionAsync().Wait(5000), "The transport connects to the bridge pipe (tournament)");
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
        Expect("hello");
        const string self = "76561190000000001", opponent = "76561190000000002", token = "synthetic-match-token_01";
        Write(new { v = 1, ev = "ready", contract = 1, wire = 1, bridge = "test", steam = true, appId = 824270, self = new { peer = self, name = "Synthetic Host", initials = "SH" }, relay = "Current", features = new[] { "lobby", "p2p", "ugc", "xfer" }, maxChunk = 32768, xferWindow = 4 });
        Check(Until(() => steam.Available), "The bridge is ready");
        Check(!((IMultiplayerTransport)steam).BeginTournamentJoin("109775240000000001", "short") && !((IMultiplayerTransport)steam).BeginTournamentJoin("not-a-lobby", token), "Malformed lobby ids and tokens are refused locally");
        ((IMultiplayerTransport)steam).PrepareTournament(token, opponent);
        long now = 1_000_000;
        var content = new FakeContent();
        var locked = new LobbySettings(Scenario: content.Scenario("Synthetic A"), MaxPlayers: 2, Tournament: new TournamentLock("t_synthetic", "W1-1", "Final", 0, 7, [self, opponent]));
        steam.Advertise(new LobbyCore(self, "Synthetic Host", locked, () => now).Snapshot());
        var create = Expect("lobby.create");
        Check(create.GetProperty("privacy").GetString() == "tournament" && create.GetProperty("token").GetString() == token && create.GetProperty("entrant").GetString() == opponent
            && !create.GetProperty("data").ToString().Contains(token), "The host creates a tournament lobby for the opponent and the match token, never in lobby data");
        Check(((IMultiplayerTransport)steam).BeginTournamentJoin("109775240000000001", token), "The opponent joins by id with the token");
        var join = Expect("lobby.join");
        Check(join.GetProperty("lobby").GetString() == "109775240000000001" && join.GetProperty("token").GetString() == token, "lobby.join carries the lobby id and the token");
    }

    static void TournamentRules()
    {
        var content = new FakeContent();
        var lockOn = new TournamentLock("t_synthetic", "W1-1", "Final", 0, 42, ["host", "p2"]);
        var locked = new LobbySettings(Scenario: content.Scenario("Synthetic A"), Spectators: true, Tournament: lockOn);
        var (next, result) = LobbyRules.Apply(locked, Patch(new { rounds = 3 }), 2, content);
        Check(next is null && result.Code == "tournament-locked", "Tournament lobbies refuse settings changes");
        Check(LobbyRules.Plausible(locked) && !LobbyRules.Plausible(locked with { Tournament = lockOn with { Seed = -1 } }) && !LobbyRules.Plausible(locked with { Tournament = lockOn with { MatchId = "" } }),
            "Tournament locks are checked like any other snapshot field");
        Check(LobbyRules.Normalize(locked with { TimeLimit = 30 }, 2).TimeLimit == 30 && LobbyRules.Normalize(locked with { Tournament = null, TimeLimit = 30 }, 2).TimeLimit is null,
            "A tournament may set the game length of a score race (its games run in freeplay)");
        Check(locked.PlayKey != (locked with { Tournament = lockOn with { Game = 1, Seed = 43 } }).PlayKey, "A new game or seed clears readiness");
        long now = 1_000_000;
        var core = new LobbyCore("host", "Host", locked, () => now);
        core.Join("p2", "Synthetic Two"); core.Join("p3", "Synthetic Three");
        Check(core.Members.First(m => m.Id == "p2").Role == MemberRoles.Player && core.Members.First(m => m.Id == "p3").Role == MemberRoles.Spectator,
            "Only the match's players play in a tournament lobby; anyone else watches");
        Check(!core.Apply("host", "settings", Patch(new { rounds = 2 }), content).Ok, "Even the host can't change a tournament lobby's settings");
        core.Apply("p2", "ready", J(new { ready = true }), content);
        Check(core.LockTournament(core.Settings with { Tournament = lockOn with { Game = 1, Seed = 43 } }).Ok && core.Settings.Tournament!.Seed == 43
            && !core.Members.First(m => m.Id == "p2").Ready && core.Snapshot().Chat.Last().Text.Contains("game 2"), "The tournament moves the lobby to the next game and its seed");
        Check(!core.LockTournament(core.Settings with { Tournament = null }).Ok, "Only tournament settings can be locked in");
    }

    static void TournamentHubClient(string root)
    {
        var folder = Path.Combine(root, "hub-tournament");
        Directory.CreateDirectory(folder);
        var fixture = new TournamentFixture();
        using (var unlinked = new Hub(folder, fixture))
        {
            var source = new HubTournamentSource(unlinked);
            Check(!source.Available, "Without a linked account there are no tournaments");
            var refused = false;
            try { source.Feed(CancellationToken.None).GetAwaiter().GetResult(); } catch (TournamentHubException) { refused = true; }
            Check(refused && fixture.Requests.Count == 0, "Nothing is sent to the Hub without an account");
        }
        AccountVault.Save(Path.Combine(folder, "account.bin"), new HubAccount("synthetic", "Synthetic", "synthetic-user", "synthetic-token"));
        using var hub = new Hub(folder, fixture);
        var client = new HubTournamentSource(hub);
        fixture.Answer = path => path switch
        {
            "/aimmod.tournament.v1.TournamentService/ListMyMatches" => (HttpStatusCode.OK, """
                {"matches":[{"tournamentId":"t_synthetic","tournamentName":"Synthetic Cup","host":true,"opponentSteamId":"76561190000000002","lobbyToken":"",
                "self":{"id":"e1","seed":1,"user":{"handle":"synthetic","displayName":"Synthetic One"}},"opponent":{"id":"e2","seed":2,"user":{"handle":"synthetic-two","displayName":"Synthetic Two"}},
                "ruleset":{"gameMode":"score-race","bestOf":3,"pool":[{"name":"Synthetic A","timeLimitSeconds":60}],"countdownSeconds":5},
                "match":{"id":"W1-1","label":"Final","side":"BRACKET_SIDE_WINNERS","round":1,"position":1,"state":"MATCH_STATE_LIVE","bestOf":3,"slotA":{"entrantId":"e1"},"slotB":{"entrantId":"e2"},
                  "currentGame":0,"games":[{"index":0,"scenario":"Synthetic A","seed":"4000000000","winner":-1}],"readyEntrantIds":["e1","e2"],"hostEntrantId":"e1"}}],
                "checkIn":[{"tournament":{"id":"t_other","name":"Synthetic Open"}}]}
                """),
            "/aimmod.tournament.v1.TournamentService/ReportGame" => (HttpStatusCode.OK, """{"match":{"id":"W1-1","state":"MATCH_STATE_LIVE","winsA":1,"currentGame":1}}"""),
            "/aimmod.tournament.v1.TournamentService/SubmitVeto" => (HttpStatusCode.PreconditionFailed, """{"code":"failed_precondition","message":"It's your opponent's turn."}"""),
            "/api/tournaments/v1/replays" => (HttpStatusCode.OK, """{"id":"r7","status":"verified"}"""),
            _ => (HttpStatusCode.NotFound, "{}"),
        };
        var feed = client.Feed(CancellationToken.None).GetAwaiter().GetResult();
        var call = fixture.Requests.Last();
        Check(call.Path == "/aimmod.tournament.v1.TournamentService/ListMyMatches" && call.Auth == "Bearer synthetic-token", "Tournament calls act as the linked account");
        var mine = feed.Matches.Single();
        Check(mine.Host && mine.Match.State == "live" && mine.Match.Current?.Seed == "4000000000" && mine.Ruleset.Pool[0].TimeLimit == 60 && mine.Opponent.Name == "Synthetic Two"
            && feed.CheckIn.Single().TournamentId == "t_other", "Matches, seeds, rulesets and check-ins are read from the Hub's JSON");
        var record = new GameResultRecord("m-synthetic", "score-race", "key", "hash", 1, 2, "k1", [new("k1", "e1", 1, 1200, 80, null, false), new("k2", "e2", 2, 1100, 75, null, false)], "k1", "4000000000");
        var after = client.ReportGame("t_synthetic", "W1-1", 0, record, ["r7"], true, "Synthetic A", CancellationToken.None).GetAwaiter().GetResult();
        using (var body = JsonDocument.Parse(fixture.Requests.Last().Body))
        {
            var r = body.RootElement;
            Check(r.GetProperty("result").GetProperty("format").GetInt32() == 1 && r.GetProperty("result").GetProperty("players")[1].GetProperty("entrantId").GetString() == "e2"
                && r.GetProperty("result").GetProperty("startedAt").GetString() == "1" && r.GetProperty("seed").GetString() == "4000000000" && r.GetProperty("hostValidated").GetBoolean(),
                "Game reports carry the shared result record with entrant ids and the seed");
        }
        Check(after.WinsA == 1 && after.CurrentGame == 1, "The reply updates the match");
        var message = "";
        try { client.Veto("t_synthetic", "W1-1", "Synthetic A", CancellationToken.None).GetAwaiter().GetResult(); } catch (TournamentHubException ex) { message = ex.Message + "|" + ex.Code; }
        Check(message == "It's your opponent's turn.|failed_precondition", "Hub refusals reach the player with the Hub's message");
        var id = client.UploadReplay("t_synthetic", "W1-1", 0, [1, 2, 3], CancellationToken.None).GetAwaiter().GetResult();
        Check(id == "r7" && fixture.Requests.Last().Path == "/api/tournaments/v1/replays?tournament=t_synthetic&match=W1-1&game=0" && fixture.Requests.Last().Auth == "Bearer synthetic-token",
            "Replays upload to the Hub's tournament replay endpoint");
    }

    static void TournamentSeedCommand(string root)
    {
        var output = Path.Combine(root, "seed-output");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "core-active.tsv"), "AIMMOD_CORE_1\tsynthetic\t" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() + "\tload,start\n");
        var control = new CoreGameControl(output);
        Check(control.Start("Synthetic A", "freeplay", 4000000000) is not null && File.ReadAllText(Path.Combine(output, "core-command.tsv")).Contains("seed\t4000000000\n"),
            "The game's shared seed is passed to AimModCore's start-scenario");
        Check(control.Start("Synthetic A", "freeplay") is not null && !File.ReadAllText(Path.Combine(output, "core-command.tsv")).Contains("seed\t"), "Ordinary rounds carry no seed");
    }

    static void TournamentSimulation(string root)
    {
        long now = 50_000_000;
        var game = Path.Combine(root, "game");
        var output = Path.Combine(root, "tournament-output");
        Directory.CreateDirectory(output);
        var control = new FakeGame("load", "start") { Root = game };
        var service = new MultiplayerService(new OfflineTransport(), new ContentLibrary(game), control, () => new LocalRun(false, null, null, null, null, 0, 0, 0, null), () => [],
            () => "Synthetic Player", output, simulation: true, () => now, autoTick: false, seed: 11);
        using var hub = new Hub(Path.Combine(root, "hub-unlinked"), new TournamentFixture());
        var tournaments = new TournamentService(new HubTournamentSource(hub), new MultiplayerTournamentLobby(service), () => true, () => ["Synthetic A"], () => "Synthetic Player", output, () => now, autoTick: false);
        service.Tournaments = tournaments;
        JsonElement Act(object body) => JsonSerializer.SerializeToElement(new { ok = tournaments.Act(JsonSerializer.SerializeToElement(body), CancellationToken.None).GetAwaiter().GetResult().Ok });
        JsonElement View() => JsonSerializer.SerializeToElement(tournaments.View(), Protocol.Json);
        Check(!View().GetProperty("linked").GetBoolean(), "Unlinked and not simulating: no tournaments");
        Check(Act(new { action = "simulate" }).GetProperty("ok").GetBoolean() && View().GetProperty("simulated").GetBoolean(), "Developer mode can simulate a tournament");
        var first = View().GetProperty("matches")[0];
        Check(first.GetProperty("host").GetBoolean() && first.GetProperty("match").GetProperty("state").GetString() == "ready" && View().GetProperty("open").GetProperty("matches").GetArrayLength() == 7,
            "The simulated event has this player's first match and its bracket");
        var notice = JsonDocument.Parse(service.NoticeText()).RootElement;
        Check(notice.GetProperty("title").GetString() == "Your tournament match is ready" && notice.GetProperty("actions").EnumerateArray().Any(a => a.GetProperty("action").GetString() == "tournament-ready"),
            "A ready match shows the in-game notice with a Ready button");
        Check(service.Act("tournament-dismiss", J(new { id = notice.GetProperty("id").GetString() })).Ok && !JsonDocument.Parse(service.NoticeText()).RootElement.GetProperty("active").GetBoolean(), "The notice can be put off");
        Check(Act(new { action = "ready", tournament = SimulatedTournamentHub.Id, match = "W1-1" }).GetProperty("ok").GetBoolean(), "Ready from the Tournaments page");
        var lobbyChecked = false; var seedPassed = false;
        for (var i = 0; i < 1200 && tournaments.Simulation!.Detail("", CancellationToken.None).Result.Matches.First(m => m.Id == "W1-1").State != "complete"; i++)
        {
            now += 500;
            service.Tick();
            tournaments.Tick(CancellationToken.None).GetAwaiter().GetResult();
            if (!lobbyChecked && service.TournamentLobby() is { } l)
            {
                var lv = JsonSerializer.SerializeToElement(service.View(), Protocol.Json).GetProperty("lobby");
                lobbyChecked = l.IsHost && l.MatchId == "W1-1" && lv.GetProperty("settings").GetProperty("tournament").GetProperty("seed").GetInt64() == l.Seed
                    && !service.Act("settings", Patch(new { rounds = 3 })).Ok;
            }
            seedPassed |= control.Calls.Any(c => c.StartsWith("start freeplay", StringComparison.Ordinal) && c.Contains(" seed "));
        }
        var done = tournaments.Simulation!.Detail("", CancellationToken.None).Result.Matches.First(m => m.Id == "W1-1");
        Check(lobbyChecked, "The host's client created the locked tournament lobby for the game");
        Check(seedPassed && control.Calls.All(c => !c.StartsWith("start challenge", StringComparison.Ordinal)), "Tournament games start in freeplay with the game's seed");
        Check(done.State == "complete" && done.Games.Count(g => g.HasScore) >= 2 && done.Games.Select(g => g.Seed).Distinct().Count() == done.Games.Count,
            "Games are played and reported until the series is decided; every game has its own seed");
        Check(tournaments.Simulation.LastLive is { MatchId: "W1-1", Players.Count: 2 }, "The host pushes live state for the organiser overview");
        Check(JsonSerializer.SerializeToElement(tournaments.ObsView(), Protocol.Json).GetProperty("tournament").GetProperty("matches").GetArrayLength() == 7, "The OBS overlay state has the bracket");
        tournaments.Act(JsonSerializer.SerializeToElement(new { action = "sim-stop" }), CancellationToken.None).GetAwaiter().GetResult();
        Check(!View().GetProperty("simulated").GetBoolean(), "The simulation ends on request");
        tournaments.Dispose(); service.Dispose();
    }
}

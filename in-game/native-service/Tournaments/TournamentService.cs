using System.Globalization;
using System.Text.Json;
using AimMod.InGame.Multiplayer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AimMod.InGame.Tournaments;

// The lobby operations a tournament needs (MultiplayerService implements them).
interface ITournamentLobby
{
    LobbyResult HostTournamentGame(TournamentGameSpec spec);
    LobbyResult JoinTournamentLobby(string token, string matchId);
    TournamentLobbyState? TournamentLobby();
    string? TournamentPlayedScenario();
    byte[]? TournamentReplay(string lobbyMatch);
    bool CanHostLobby { get; }
}

sealed class MultiplayerTournamentLobby(MultiplayerService service) : ITournamentLobby
{
    public LobbyResult HostTournamentGame(TournamentGameSpec spec) => service.HostTournamentGame(spec);
    public LobbyResult JoinTournamentLobby(string token, string matchId) => service.JoinTournamentLobby(token, matchId);
    public TournamentLobbyState? TournamentLobby() => service.TournamentLobby();
    public string? TournamentPlayedScenario() => service.TournamentPlayedScenario();
    public byte[]? TournamentReplay(string lobbyMatch) => service.TournamentReplay(lobbyMatch);
    public bool CanHostLobby => service.CanHostLobby;
}

// Tournaments in game: shows this player's matches and check-ins from AimMod
// Hub, raises the "your match is ready" notice, and runs the series through
// the lobby. The higher seed's client hosts: it creates the locked lobby for
// each game (scenario, length and the game's seed from the Hub), reports each
// finished game with its result record, and pushes live state for the
// organiser overview. The other client joins the host's lobby, uploads its own
// replays and confirms or disputes the result. Every rule (brackets, seeds,
// deadlines, verification) is the Hub's; nothing here decides a result.
sealed class TournamentService : IDisposable
{
    readonly object gate = new();
    readonly ITournamentHub hub;
    readonly ITournamentLobby lobby;
    readonly Func<long> clock;
    readonly Func<bool> developer;
    readonly Func<IReadOnlyList<string>> scenarios;
    readonly Func<string?> selfName;
    readonly string? output;
    readonly Timer? timer;
    SimulatedTournamentHub? sim;
    TournamentFeed feed = new([], []);
    IReadOnlyList<TournamentSummary> mine = [];
    TournamentDetail? detail;
    string? openId;
    string status = "";
    long nextFeed, nextDetail, nextLive, failures;
    bool ticking;
    readonly HashSet<string> dismissed = [], reported = [], uploaded = [];
    readonly HashSet<string> readyAsked = [];
    string? lastError;
    public int Revision { get; private set; }

    public TournamentService(ITournamentHub hub, ITournamentLobby lobby, Func<bool> developer, Func<IReadOnlyList<string>> scenarios, Func<string?> selfName,
        string? output, Func<long>? clock = null, bool autoTick = true)
    {
        this.hub = hub; this.lobby = lobby; this.developer = developer; this.scenarios = scenarios; this.selfName = selfName; this.output = output;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (autoTick) timer = new Timer(_ => _ = TickSafe(), null, 1000, 1000);
    }

    ITournamentHub Source => sim ?? (ITournamentHub)hub;
    public bool Simulating { get { lock (gate) return sim is not null; } }
    public SimulatedTournamentHub? Simulation { get { lock (gate) return sim; } }

    async Task TickSafe()
    {
        lock (gate) { if (ticking) return; ticking = true; }
        try { await Tick(CancellationToken.None); }
        catch (Exception ex) { Console.Error.WriteLine("Tournament tick failed: " + ex.GetType().Name); }
        finally { lock (gate) ticking = false; }
    }

    MyMatch? Active() => feed.Matches.FirstOrDefault(m => m.Match.State is "live" or "veto" or "awaiting_confirmation" or "disputed")
        ?? feed.Matches.FirstOrDefault(m => m.Match.State == "ready");

    public async Task Tick(CancellationToken token)
    {
        var now = clock();
        var source = Source;
        if (!source.Available) { lock (gate) { if (feed.Matches.Count > 0 || feed.CheckIn.Count > 0) { feed = new([], []); Revision++; } status = "Link your AimMod Hub account to play in tournaments."; } return; }
        bool pollFeed, pollDetail; string? detailId;
        lock (gate)
        {
            pollFeed = now >= nextFeed; pollDetail = openId is not null && now >= nextDetail; detailId = openId;
        }
        if (pollFeed)
        {
            try
            {
                var next = await source.Feed(token);
                var list = await source.List(token);
                lock (gate)
                {
                    feed = next; mine = list; failures = 0; status = ""; lastError = null;
                    var busy = Active() is { Match.State: not "ready" };
                    nextFeed = now + (sim is not null ? 1000 : busy ? 3000 : feed.Matches.Count > 0 || feed.CheckIn.Count > 0 ? 10_000 : 30_000);
                    Revision++;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TournamentHubException or TaskCanceledException)
            {
                lock (gate)
                {
                    failures = Math.Min(failures + 1, 6);
                    nextFeed = now + Math.Min(300_000, 5000L << (int)failures);
                    status = ex is TournamentHubException h ? h.Message : "AimMod Hub is unavailable. Tournaments refresh when it’s back.";
                    Revision++;
                }
                return;
            }
        }
        if (pollDetail && detailId is not null)
        {
            try { var d = await source.Detail(detailId, token); lock (gate) { detail = d; nextDetail = now + (sim is not null ? 1500 : 10_000); Revision++; } }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TournamentHubException or TaskCanceledException)
            { lock (gate) nextDetail = now + 30_000; }
        }
        await Drive(source, now, token);
    }

    // The series: host the game, report it, join, upload replays, push live state.
    async Task Drive(ITournamentHub source, long now, CancellationToken token)
    {
        MyMatch? active; lock (gate) active = Active();
        if (active is null) return;
        var m = active.Match;
        var state = lobby.TournamentLobby();
        var inLobby = state is { } s0 && s0.TournamentId == active.TournamentId && s0.MatchId == m.Id;
        try
        {
            if (m.State == "live" && m.Current is { } game)
            {
                if (active.Host)
                {
                    var finishedThisGame = inLobby && state!.Game == game.Index && state.Finished;
                    if (!finishedThisGame && (!inLobby || state!.Game != game.Index))
                    {
                        var pool = active.Ruleset.Pool.FirstOrDefault(p => p.Name.Equals(game.Scenario, StringComparison.OrdinalIgnoreCase));
                        var spec = new TournamentGameSpec(active.TournamentId, m.Id, m.Label, game.Index, game.Scenario, game.TimeLimit > 0 ? game.TimeLimit : pool?.TimeLimit ?? 0,
                            long.TryParse(game.Seed, NumberStyles.None, CultureInfo.InvariantCulture, out var seed) && seed is >= 0 and <= uint.MaxValue ? seed : 0,
                            active.Ruleset.Countdown, active.Ruleset.Spectators, active.OpponentSteamId, active.Opponent.Name, active.TournamentName);
                        var result = lobby.HostTournamentGame(spec);
                        lock (gate) { var text = result.Ok ? "" : result.Message ?? ""; if (text != status) { status = text; Revision++; } }
                    }
                    if (finishedThisGame) await Report(source, active, game, state!, token);
                }
                else if (!inLobby && active.LobbyToken.Length > 0)
                {
                    var result = lobby.JoinTournamentLobby(active.LobbyToken, m.Id);
                    lock (gate) if (!result.Ok && result.Message != status) { status = result.Message ?? ""; Revision++; }
                }
            }
            // Each player uploads their own replay of every finished game.
            if (inLobby && state!.Finished && state.LobbyMatch.Length > 0)
            {
                var key = m.Id + "#" + state.Game;
                bool fresh; lock (gate) fresh = uploaded.Add(key);
                if (fresh && lobby.TournamentReplay(state.LobbyMatch) is { Length: > 0 and <= 8 * 1024 * 1024 } file)
                    await source.UploadReplay(active.TournamentId, m.Id, state.Game, file, token);
            }
            if (inLobby && state!.IsHost && now >= nextLive && m.State is "live" or "veto")
            {
                lock (gate) nextLive = now + 3000;
                await source.ReportLive(active.TournamentId, Live(active, state), token);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TournamentHubException or TaskCanceledException)
        {
            lock (gate) { lastError = ex is TournamentHubException h ? h.Message : "Couldn’t reach AimMod Hub. Retrying."; Revision++; }
        }
    }

    static LiveMatchState Live(MyMatch active, TournamentLobbyState state)
    {
        string Entrant(TournamentLobbyPlayer p) => p.Self ? active.Self.Id : active.Opponent.Id;
        return new LiveMatchState(active.Match.Id, state.Game, state.Scenario, state.Phase, state.Players.Select(p =>
            new LivePlayerState(Entrant(p), p.Score ?? 0, p.Accuracy ?? 0, p.Remaining ?? 0, p.Ping ?? 0, p.Connection, p.Status, p.Ready)).ToArray(),
            state.Spectators, state.LobbyToken);
    }

    // Host validation: both players finished the game in the lobby, with real scores.
    async Task Report(ITournamentHub source, MyMatch active, TGame game, TournamentLobbyState state, CancellationToken token)
    {
        var key = active.Match.Id + "#" + game.Index;
        lock (gate) if (!reported.Add(key)) return;
        var self = state.Players.FirstOrDefault(p => p.Self);
        var other = state.Players.FirstOrDefault(p => !p.Self);
        if (self is null || other is null) { lock (gate) reported.Remove(key); return; }
        var validated = self.Status == "finished" && other.Status == "finished" && self.Score is double a && double.IsFinite(a) && other.Score is double b && double.IsFinite(b) && state.Seed.ToString(CultureInfo.InvariantCulture) == game.Seed;
        var players = new[] { self, other }.OrderByDescending(p => p.Score ?? double.MinValue).ToArray();
        var record = new GameResultRecord(state.LobbyMatch, "score-race", state.SettingsKey, state.ScenarioHash, state.StartedAt ?? 0, state.EndedAt ?? clock(), state.HostKey,
            players.Select((p, i) => new ResultPlayer(MultiplayerService.KeyOf(p.MemberId), p.Self ? active.Self.Id : active.Opponent.Id, i + 1, p.Score ?? 0, p.Accuracy ?? 0, null,
                p.Status is "dnf" or "left")).ToArray(),
            players[0].Score == players[1].Score ? null : MultiplayerService.KeyOf(players[0].MemberId), game.Seed);
        try
        {
            var match = await source.ReportGame(active.TournamentId, active.Match.Id, game.Index, record, [], validated, lobby.TournamentPlayedScenario() ?? game.Scenario, token);
            lock (gate) { Replace(active.TournamentId, match); nextFeed = 0; Revision++; }
        }
        catch
        {
            lock (gate) reported.Remove(key);
            throw;
        }
    }

    void Replace(string tournamentId, TMatch match)
    {
        feed = feed with { Matches = feed.Matches.Select(x => x.TournamentId == tournamentId && x.Match.Id == match.Id ? x with { Match = match } : x).ToArray() };
    }

    // ---- notices (shown outside the panel through the multiplayer notice layer) ----

    public GameNotice? Notice()
    {
        lock (gate)
        {
            foreach (var c in feed.CheckIn)
            {
                var id = "tci-" + c.TournamentId;
                if (!dismissed.Contains(id))
                    return new GameNotice(id, "ready", "Check in for " + Clean(c.Name), "Your tournament is about to start. Check in to keep your place.", null, null, "popup")
                    { Actions = [new("Check in", "tournament-checkin", c.TournamentId), new("Later", "tournament-dismiss", id)] };
            }
            if (Active() is not { } a) return null;
            var m = a.Match;
            var vs = "vs " + a.Opponent.Name + " · " + Clean(m.Label) + " · best of " + m.BestOf;
            if (m.State == "ready" && !m.Ready.Contains(a.Self.Id))
            {
                var id = "tmr-" + a.TournamentId + "-" + m.Id;
                if (!dismissed.Contains(id))
                    return new GameNotice(id, "invite", "Your tournament match is ready", vs, null, null, "popup")
                    { Actions = [new("Ready", "tournament-ready", a.TournamentId + "/" + m.Id), new("Later", "tournament-dismiss", id)] };
            }
            if (m.State == "ready" && m.Ready.Contains(a.Self.Id))
                return Quiet("tmw-" + m.Id, "Waiting for " + a.Opponent.Name, vs);
            if (m.State == "veto" && m.VetoTurn == a.Self.Id)
                return new GameNotice("tmv-" + m.Id + "-" + m.Veto.Count, "ready", "Your " + (m.VetoAction == "ban" ? "ban" : "pick"), "Choose in AimMod’s Tournaments page. " + vs, null, null, "popup")
                { Actions = [new("Open", "tournament-open", a.TournamentId)] };
            if (m.State == "awaiting_confirmation" && m.ReportedBy != a.Self.Id && m.ReportedBy.Length > 0)
            {
                var (mineWins, theirs) = m.A == a.Self.Id ? (m.WinsA, m.WinsB) : (m.WinsB, m.WinsA);
                return new GameNotice("tmc-" + m.Id, "ready", "Confirm the result: " + mineWins + "–" + theirs, (mineWins > theirs ? "You won " : "You lost ") + vs, null, null, "popup")
                { Actions = [new("Confirm", "tournament-confirm", a.TournamentId + "/" + m.Id), new("Dispute", "tournament-open", a.TournamentId)] };
            }
            return null;
        }
    }
    static GameNotice Quiet(string id, string title, string body) => new(id, "info", title, body, null, null, "none");
    static string Clean(string s) => LobbyRules.CleanName(s, "Tournament");

    // Buttons on the notice layer: the answer is queued, so the notice never waits on the Hub.
    public LobbyResult NoticeAction(string action, string? id)
    {
        if (action == "tournament-dismiss") { lock (gate) { if (id is not null) dismissed.Add(id); Revision++; } return LobbyResult.Success; }
        if (action == "tournament-open") { RequestPanel(); return LobbyResult.Success; }
        if (id is null) return LobbyResult.Fail("invalid", "Missing match.");
        var parts = id.Split('/', 2);
        _ = Task.Run(async () =>
        {
            try
            {
                switch (action)
                {
                    case "tournament-checkin": await Run(new { action = "check-in", tournament = parts[0] }); break;
                    case "tournament-ready" when parts.Length == 2: await Run(new { action = "ready", tournament = parts[0], match = parts[1] }); break;
                    case "tournament-confirm" when parts.Length == 2: await Run(new { action = "confirm", tournament = parts[0], match = parts[1] }); break;
                }
            }
            catch (Exception ex) { Console.Error.WriteLine("Tournament notice action failed: " + ex.GetType().Name); }
        });
        return LobbyResult.Success;
    }

    Task<LobbyResult> Run(object body) => Act(JsonSerializer.SerializeToElement(body), CancellationToken.None);

    void RequestPanel()
    {
        if (output is null) return;
        try { AtomicFile.WriteText(Path.Combine(output, "open-workspace.request"), "tournaments"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---- UI ---------------------------------------------------------------

    public async Task<LobbyResult> Act(JsonElement args, CancellationToken token)
    {
        string? Text(string key) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var action = Text("action") ?? "";
        var tid = Text("tournament") ?? ""; var mid = Text("match") ?? "";
        try
        {
            switch (action)
            {
                case "refresh": lock (gate) { nextFeed = 0; nextDetail = 0; } await Tick(token); return LobbyResult.Success;
                case "open":
                    if (tid.Length is 0 or > 80) return LobbyResult.Fail("invalid", "Choose a tournament.");
                    lock (gate) { openId = tid; detail = null; nextDetail = 0; }
                    await Tick(token); return LobbyResult.Success;
                case "close": lock (gate) { openId = null; detail = null; Revision++; } return LobbyResult.Success;
                case "check-in": await Source.CheckIn(tid, token); break;
                case "ready" or "unready":
                    var match = await Source.MarkReady(tid, mid, action == "ready", lobby.CanHostLobby, token);
                    lock (gate) { Replace(tid, match); readyAsked.Add(mid); }
                    break;
                case "veto":
                    var scenario = Text("scenario");
                    if (string.IsNullOrWhiteSpace(scenario) || scenario.Length > 128) return LobbyResult.Fail("invalid", "Choose a scenario.");
                    var vetoed = await Source.Veto(tid, mid, scenario, token);
                    lock (gate) Replace(tid, vetoed);
                    break;
                case "confirm":
                    var confirmed = await Source.Confirm(tid, mid, token);
                    lock (gate) Replace(tid, confirmed);
                    break;
                case "dispute":
                    var reason = (Text("reason") ?? "").Trim();
                    if (reason.Length < 5) return LobbyResult.Fail("invalid", "Say what went wrong.");
                    await Source.Dispute(tid, mid, reason.Length > 500 ? reason[..500] : reason, token);
                    break;
                case "simulate":
                    if (!developer()) return LobbyResult.Fail("dev-off", "Turn on developer mode in Settings first.");
                    lock (gate)
                    {
                        sim = new SimulatedTournamentHub(scenarios(), selfName() ?? "You", clock, checkIn: Text("op") == "check-in");
                        openId = SimulatedTournamentHub.Id; reported.Clear(); uploaded.Clear(); dismissed.Clear(); nextFeed = 0; nextDetail = 0;
                    }
                    break;
                case "sim-advance": lock (gate) sim?.AdvanceOthers(); break;
                case "sim-opponent-reports": lock (gate) sim?.OpponentReports(); break;
                case "sim-stop":
                    lock (gate) { sim = null; openId = null; detail = null; feed = new([], []); nextFeed = 0; Revision++; }
                    return LobbyResult.Success;
                default: return LobbyResult.Fail("invalid", "Unknown action.");
            }
        }
        catch (TournamentHubException ex) { return LobbyResult.Fail(ex.Code.Length > 0 ? ex.Code : "hub", ex.Message); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TaskCanceledException)
        { return LobbyResult.Fail("hub", "Couldn’t reach AimMod Hub. Try again in a moment."); }
        lock (gate) { nextFeed = 0; nextDetail = 0; }
        await Tick(token);
        return LobbyResult.Success;
    }

    public object View()
    {
        lock (gate)
        {
            var source = Source;
            var lobbyState = lobby.TournamentLobby();
            var active = Active();
            return new
            {
                v = 1,
                linked = source.Available,
                simulated = sim is not null,
                developer = developer(),
                status = lastError ?? status,
                canHost = lobby.CanHostLobby,
                checkIn = feed.CheckIn.Select(c => new { tournament = c.TournamentId, name = c.Name, closesAt = c.ClosesAt }),
                matches = feed.Matches.Select(m => new
                {
                    tournament = m.TournamentId, tournamentName = m.TournamentName, match = m.Match, self = m.Self, opponent = m.Opponent, host = m.Host,
                    ruleset = new { m.Ruleset.BestOf, pool = m.Ruleset.Pool.Select(p => p.Name), m.Ruleset.RequireReplays },
                    active = active is not null && active.Match.Id == m.Match.Id && active.TournamentId == m.TournamentId,
                }),
                mine,
                open = detail,
                lobby = lobbyState is null ? null : new
                {
                    lobbyState.TournamentId, lobbyState.MatchId, game = lobbyState.Game, lobbyState.Phase, lobbyState.IsHost, lobbyState.Scenario, lobbyState.Spectators,
                    players = lobbyState.Players.Select(p => new { p.Name, p.Self, p.Score, p.Accuracy, p.Remaining, p.Ping, p.Connection, p.Status, p.Ready }),
                },
            };
        }
    }

    // State for the OBS tournament overlay: the open (or active) tournament's
    // bracket and the live match. Read-only and without any account data.
    public object ObsView()
    {
        lock (gate)
        {
            var active = Active();
            var state = lobby.TournamentLobby();
            return new
            {
                v = 1,
                tournament = detail is null ? null : new { detail.Name, detail.Status, detail.Champion, entrants = detail.Entrants.Select(e => new { e.Id, e.Name, e.Seed }), matches = detail.Matches.Select(m => new { m.Id, m.Label, m.Side, m.Round, m.Position, m.State, m.A, m.B, m.WinsA, m.WinsB, m.Winner, m.BestOf }) },
                match = active is null ? null : new { active.Match.Id, active.Match.Label, active.Match.State, active.Match.BestOf, active.Match.WinsA, active.Match.WinsB, a = active.Match.A == active.Self.Id ? active.Self.Name : active.Opponent.Name, b = active.Match.B == active.Self.Id ? active.Self.Name : active.Opponent.Name },
                live = state is null ? null : new { state.Game, state.Phase, state.Scenario, players = state.Players.Select(p => new { p.Name, p.Score, p.Accuracy, p.Remaining, p.Ping, p.Connection, p.Status }) },
            };
        }
    }

    public object DevView() { lock (gate) return new { simulating = sim is not null, status = sim?.Status }; }

    public void MapEndpoints(IEndpointRouteBuilder routes, string prefix)
    {
        routes.MapGet(prefix + "/tournaments", () => Results.Json(View(), Protocol.Json));
        routes.MapPost(prefix + "/tournaments", async (HttpRequest request, CancellationToken token) =>
        {
            if (request.Headers["X-AimMod-UI"] != "1") return Results.StatusCode(403);
            if (!request.HasJsonContentType()) return Results.StatusCode(415);
            if (request.ContentLength is not long length || length is <= 0 or > 4096) return Results.StatusCode(413);
            try
            {
                var bytes = new byte[(int)length]; await request.Body.ReadExactlyAsync(bytes, token);
                using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
                var result = await Act(doc.RootElement.Clone(), token);
                return result.Ok ? Results.Json(View(), Protocol.Json) : Results.Json(new { error = result.Message, code = result.Code }, Protocol.Json, statusCode: 409);
            }
            catch (JsonException) { return Results.BadRequest(new { error = "Invalid request." }); }
            catch (EndOfStreamException) { return Results.BadRequest(new { error = "Incomplete request." }); }
        });
    }

    public void Dispose() => timer?.Dispose();
}

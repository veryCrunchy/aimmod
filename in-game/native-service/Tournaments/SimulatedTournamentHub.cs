namespace AimMod.InGame.Tournaments;

// Where tournaments come from: AimMod Hub (HubTournamentSource), or the
// developer simulation below. All tournament rules live on the Hub; the
// simulation only imitates its answers so the in-game flow can be tried solo.
interface ITournamentHub
{
    bool Available { get; }
    bool Simulated { get; }
    Task<TournamentFeed> Feed(CancellationToken token);
    Task<IReadOnlyList<TournamentSummary>> List(CancellationToken token);
    Task<TournamentDetail> Detail(string tournament, CancellationToken token);
    Task CheckIn(string tournamentId, CancellationToken token);
    Task<TMatch> MarkReady(string tournamentId, string matchId, bool ready, bool canHost, CancellationToken token);
    Task<TMatch> Veto(string tournamentId, string matchId, string scenario, CancellationToken token);
    Task<TMatch> ReportGame(string tournamentId, string matchId, int game, GameResultRecord result, IReadOnlyList<string> replays, bool hostValidated, string playedScenario, CancellationToken token);
    Task<TMatch> Confirm(string tournamentId, string matchId, CancellationToken token);
    Task Dispute(string tournamentId, string matchId, string reason, CancellationToken token);
    Task ReportLive(string tournamentId, LiveMatchState live, CancellationToken token);
    Task<string?> UploadReplay(string tournamentId, string matchId, int game, byte[] file, CancellationToken token);
}

// Developer mode: an 8-player single-elimination event with this player as the
// top seed and simulated opponents. Other matches finish when the developer
// advances the bracket; the simulated opponent readies, bans and confirms by itself.
sealed class SimulatedTournamentHub : ITournamentHub
{
    public const string Id = "t_simulated";
    sealed class SimMatch
    {
        public required string Id; public int Round, Position; public string A = "", B = "", Winner = "";
        public string State = "pending"; public int BestOf = 3; public HashSet<string> Ready = []; public string Host = "";
        public List<TGame> Games = []; public List<TVeto> Veto = []; public string ReportedBy = ""; public bool Disputed; public bool CheckedIn;
    }
    readonly object gate = new();
    readonly Random random;
    readonly List<TEntrant> entrants = [];
    readonly List<SimMatch> matches = [];
    readonly IReadOnlyList<TPool> pool;
    readonly bool veto;
    readonly Func<long> clock;
    long confirmAt;
    public string Self => "e1";
    public bool CheckInOpen { get; private set; }
    public LiveMatchState? LastLive { get; private set; }
    public List<string> Uploads { get; } = [];
    public string Status => matches.All(m => m.State == "complete") ? "completed" : CheckInOpen ? "check_in" : "in_progress";

    public SimulatedTournamentHub(IReadOnlyList<string> scenarios, string selfName, Func<long> clock, int seed = 7, bool checkIn = false)
    {
        this.clock = clock;
        random = new Random(seed);
        var names = scenarios.Where(s => s.Length > 0).Distinct().Take(5).ToList();
        if (names.Count == 0) names.Add("Synthetic Scenario");
        pool = names.Select(n => new TPool(n, 0)).ToArray();
        veto = pool.Count >= 5;
        string[] others = ["Nova", "Kestrel", "Ember", "Quill", "Atlas", "Juniper", "Orbit"];
        entrants.Add(new TEntrant("e1", Multiplayer.LobbyRules.CleanName(selfName, "You"), "you", 1));
        for (var i = 0; i < others.Length; i++) entrants.Add(new TEntrant("e" + (i + 2), others[i], others[i].ToLowerInvariant(), i + 2));
        int[] order = [1, 8, 4, 5, 2, 7, 3, 6];
        for (var i = 0; i < 4; i++) matches.Add(new SimMatch { Id = "W1-" + (i + 1), Round = 1, Position = i + 1, A = "e" + order[2 * i], B = "e" + order[2 * i + 1] });
        for (var i = 0; i < 2; i++) matches.Add(new SimMatch { Id = "W2-" + (i + 1), Round = 2, Position = i + 1 });
        matches.Add(new SimMatch { Id = "W3-1", Round = 3, Position = 1 });
        CheckInOpen = checkIn;
        Settle();
    }

    public bool Available => true;
    public bool Simulated => true;
    TEntrant Ent(string id) => entrants.First(e => e.Id == id);
    string Label(SimMatch m) => m.Round switch { 3 => "Final", 2 => "Semi-final", _ => "Round 1" };
    TRuleset Ruleset => new("score-race", 3, pool, 5, true, false);

    void Settle()
    {
        foreach (var m in matches.Where(m => m.Round > 1))
        {
            var feeders = matches.Where(f => f.Round == m.Round - 1 && (f.Position + 1) / 2 == m.Position).OrderBy(f => f.Position).ToArray();
            if (m.A == "" && feeders[0].Winner != "") m.A = feeders[0].Winner;
            if (m.B == "" && feeders[1].Winner != "") m.B = feeders[1].Winner;
        }
        foreach (var m in matches.Where(m => m.State == "pending" && m.A != "" && m.B != "")) m.State = "ready";
    }

    TMatch View(SimMatch m, bool insider = true)
    {
        var wins = Wins(m);
        var turn = ""; var action = "";
        if (m.State == "veto") { (turn, action) = VetoTurn(m); }
        return new TMatch(m.Id, Label(m), "winners", m.Round, m.Position, m.State, m.BestOf, wins[0], wins[1], m.A, m.B, false, false, m.Winner, m.Host,
            m.Ready.ToArray(), m.State == "live" && m.Games.Count > 0 && !m.Games[^1].HasScore ? m.Games.Count - 1 : -1,
            m.Games.Select(g => insider || m.State == "complete" ? g : g with { Seed = "" }).ToArray(), m.Veto.ToArray(), turn, action, "", m.ReportedBy,
            m.Disputed ? ["disputed"] : [], m.State == "complete" ? "played" : "");
    }

    static int[] Wins(SimMatch m) => [m.Games.Count(g => g.HasScore && g.Winner == 0), m.Games.Count(g => g.HasScore && g.Winner == 1)];

    (string, string) VetoTurn(SimMatch m)
    {
        // Ban (higher seed), ban (lower), pick (higher), pick (lower), then the decider.
        string[] actions = ["ban", "ban", "pick", "pick"];
        if (m.Veto.Count >= actions.Length) return ("", "");
        var higher = Ent(m.A).Seed < Ent(m.B).Seed ? m.A : m.B; var lower = higher == m.A ? m.B : m.A;
        return (m.Veto.Count % 2 == 0 ? higher : lower, actions[m.Veto.Count]);
    }

    void NewGame(SimMatch m)
    {
        string scenario;
        if (veto)
        {
            var order = m.Veto.Where(v => v.Action == "pick").Select(v => v.Scenario).Concat(m.Veto.Where(v => v.Action == "decider").Select(v => v.Scenario)).ToList();
            var decided = m.Games.Count(g => g.Winner >= 0);
            scenario = m.Games.Count > 0 && m.Games[^1].Winner < 0 ? m.Games[^1].Scenario : order[decided % order.Count];
        }
        else scenario = m.Games.Count > 0 && m.Games[^1].Winner < 0 ? m.Games[^1].Scenario : pool[m.Games.Count(g => g.Winner >= 0) % pool.Count].Name;
        m.Games.Add(new TGame(m.Games.Count, scenario, 0, ((uint)random.Next() ^ (uint)random.Next() << 1).ToString(System.Globalization.CultureInfo.InvariantCulture), false, 0, 0, -1, false));
    }

    SimMatch Find(string id) => matches.FirstOrDefault(m => m.Id == id) ?? throw new TournamentHubException("Unknown match.", "not_found");
    SimMatch? Mine() => matches.FirstOrDefault(m => (m.A == Self || m.B == Self) && m.State is not ("complete" or "pending"));

    public Task<TournamentFeed> Feed(CancellationToken token)
    {
        lock (gate)
        {
            Step();
            var list = new List<MyMatch>();
            if (!CheckInOpen && Mine() is { } m)
            {
                var opp = m.A == Self ? m.B : m.A;
                list.Add(new MyMatch(Id, "Simulated Cup", View(m), Ent(Self), Ent(opp), Ruleset, m.Host == "" ? Ent(Self).Seed < Ent(opp).Seed : m.Host == Self,
                    "sim-" + Ent(opp).Name.ToLowerInvariant(), "", false, "sim-token-" + m.Id));
            }
            var due = CheckInOpen && !matches[0].CheckedIn ? new[] { new CheckInDue(Id, "Simulated Cup", "") } : [];
            return Task.FromResult(new TournamentFeed(list, due));
        }
    }

    // The simulated opponent acts a moment after this player.
    void Step()
    {
        var m = Mine();
        if (m is null) return;
        var opp = m.A == Self ? m.B : m.A;
        if (m.State == "veto" && VetoTurn(m).Item1 == opp) Ban(m, opp, Left(m).OrderBy(_ => random.Next()).First());
        if (m.State == "awaiting_confirmation" && m.ReportedBy == Self && clock() >= confirmAt) Finish(m);
    }

    List<string> Left(SimMatch m) => pool.Select(p => p.Name).Where(n => m.Veto.All(v => v.Scenario != n)).ToList();

    void Ban(SimMatch m, string who, string scenario)
    {
        var (turn, action) = VetoTurn(m);
        if (turn != who) throw new TournamentHubException("It’s your opponent’s turn.", "failed_precondition");
        if (!Left(m).Contains(scenario)) throw new TournamentHubException("That scenario isn’t available.", "invalid_argument");
        m.Veto.Add(new TVeto(m.Veto.Count + 1, action, who, scenario));
        if (VetoTurn(m).Item1 == "")
        {
            m.Veto.Add(new TVeto(m.Veto.Count + 1, "decider", "", Left(m)[0]));
            m.State = "live"; NewGame(m);
        }
    }

    void Finish(SimMatch m)
    {
        var w = Wins(m);
        m.Winner = w[0] > w[1] ? m.A : m.B;
        m.State = "complete";
        Settle();
    }

    public Task<IReadOnlyList<TournamentSummary>> List(CancellationToken token)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<TournamentSummary>>([new TournamentSummary(Id, "simulated-cup", "Simulated Cup", Status, "single_elimination", 8, 8, "", true)]);
    }

    public Task<TournamentDetail> Detail(string tournament, CancellationToken token)
    {
        lock (gate)
        {
            var champion = matches[^1].Winner;
            return Task.FromResult(new TournamentDetail(Id, "simulated-cup", "Simulated Cup", Status, "single_elimination", champion, entrants.ToArray(),
                matches.Select(m => View(m, m.A == Self || m.B == Self)).ToArray(), CheckInOpen && !matches[0].CheckedIn, Self));
        }
    }

    public Task CheckIn(string tournamentId, CancellationToken token)
    {
        lock (gate) { matches[0].CheckedIn = true; CheckInOpen = false; }
        return Task.CompletedTask;
    }

    public Task<TMatch> MarkReady(string tournamentId, string matchId, bool ready, bool canHost, CancellationToken token)
    {
        lock (gate)
        {
            var m = Find(matchId);
            if (m.State != "ready") throw new TournamentHubException("This match isn’t open.", "failed_precondition");
            if (!ready) { m.Ready.Remove(Self); return Task.FromResult(View(m)); }
            m.Ready.Add(Self); m.Ready.Add(m.A == Self ? m.B : m.A);
            m.Host = Self;
            if (veto) m.State = "veto"; else { m.State = "live"; NewGame(m); }
            Step();
            return Task.FromResult(View(m));
        }
    }

    public Task<TMatch> Veto(string tournamentId, string matchId, string scenario, CancellationToken token)
    {
        lock (gate) { var m = Find(matchId); Ban(m, Self, scenario); Step(); return Task.FromResult(View(m)); }
    }

    public Task<TMatch> ReportGame(string tournamentId, string matchId, int game, GameResultRecord result, IReadOnlyList<string> replays, bool hostValidated, string playedScenario, CancellationToken token)
    {
        lock (gate)
        {
            var m = Find(matchId);
            if (m.State != "live" || m.Games.Count == 0 || m.Games[^1].HasScore || m.Games[^1].Index != game) throw new TournamentHubException("Report game " + m.Games.Count + ".", "invalid_argument");
            if (result.Players.Count != 2) throw new TournamentHubException("A tournament game result names its two players.", "invalid_argument");
            var a = result.Players.FirstOrDefault(p => p.Entrant == m.A); var b = result.Players.FirstOrDefault(p => p.Entrant == m.B);
            if (a is null || b is null) throw new TournamentHubException("The result names someone who isn’t in this match.", "invalid_argument");
            var g = m.Games[^1];
            m.Games[^1] = g with { HasScore = true, ScoreA = a.Score, ScoreB = b.Score, Winner = a.Score > b.Score ? 0 : b.Score > a.Score ? 1 : -1, HostValidated = hostValidated };
            var w = Wins(m);
            if (w[0] >= 2 || w[1] >= 2) { m.State = "awaiting_confirmation"; m.ReportedBy = Self; confirmAt = clock() + 3000; }
            else NewGame(m);
            return Task.FromResult(View(m));
        }
    }

    public Task<TMatch> Confirm(string tournamentId, string matchId, CancellationToken token)
    {
        lock (gate)
        {
            var m = Find(matchId);
            if (m.State != "awaiting_confirmation") throw new TournamentHubException("There’s no result to confirm.", "failed_precondition");
            if (m.ReportedBy == Self) throw new TournamentHubException("Your opponent confirms the result you reported.", "failed_precondition");
            Finish(m);
            return Task.FromResult(View(m));
        }
    }

    public Task Dispute(string tournamentId, string matchId, string reason, CancellationToken token)
    {
        lock (gate) { var m = Find(matchId); m.Disputed = true; m.State = "disputed"; }
        return Task.CompletedTask;
    }

    public Task ReportLive(string tournamentId, LiveMatchState live, CancellationToken token)
    {
        lock (gate) LastLive = live;
        return Task.CompletedTask;
    }

    public Task<string?> UploadReplay(string tournamentId, string matchId, int game, byte[] file, CancellationToken token)
    {
        lock (gate) { Uploads.Add(matchId + "#" + game); return Task.FromResult<string?>("r-sim-" + Uploads.Count); }
    }

    // Developer: finish every other playable match at random, so the bracket moves on.
    public void AdvanceOthers()
    {
        lock (gate)
        {
            foreach (var m in matches.Where(m => m.State == "ready" && m.A != Self && m.B != Self).ToArray())
            {
                m.Games.Add(new TGame(0, pool[0].Name, 0, "1", true, 1000 + random.Next(300), 1000 + random.Next(300), 0, true));
                var g = m.Games[0]; m.Games[0] = g with { Winner = g.ScoreA >= g.ScoreB ? 0 : 1 };
                m.Games.Add(m.Games[0] with { Index = 1 });
                Finish(m);
            }
            Settle();
        }
    }

    // Developer: the opponent reports the result, so this player gets the confirm notice.
    public void OpponentReports()
    {
        lock (gate)
        {
            if (Mine() is not { State: "live" } m) return;
            var opp = m.A == Self ? m.B : m.A;
            while (m.Games.Count > 0 && !m.Games[^1].HasScore)
            {
                var g = m.Games[^1];
                var oppWins = m.A == opp ? 0 : 1;
                m.Games[^1] = g with { HasScore = true, ScoreA = oppWins == 0 ? 1100 : 900, ScoreB = oppWins == 0 ? 900 : 1100, Winner = oppWins };
                var w = Wins(m);
                if (w[0] >= 2 || w[1] >= 2) { m.State = "awaiting_confirmation"; m.ReportedBy = opp; break; }
                NewGame(m);
            }
        }
    }
}

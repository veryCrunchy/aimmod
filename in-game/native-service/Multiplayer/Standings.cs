namespace AimMod.InGame.Multiplayer;

// One mode-aware standings model for every leaderboard surface: the in-game
// corner panel, the hold-to-show scoreboard and the OBS browser source.
// Kind tells the views which columns matter:
//   score    rank, score, gap to the leader (score race, free-for-all, practice)
//   duel     round wins and the current round's score (score duel)
//   tracking time on target % and round wins (tracking duel)
//   combat   frags, deaths, K/D, and health in vampiric (deathmatch, instagib, vampiric)
//   team     team totals, then players by team with frags, deaths and K/D (TDM, CS)
// No Steam ids: rows carry names and a self flag only.
sealed record BoardRow(string Name, bool Self, int Rank, double? Score, double? Gap, int? Wins, double? Percent,
    int? Frags, int? Deaths, double? Kd, double? Health, int Team, string Status, bool Bot = false);
sealed record BoardTeam(int Team, string Name, int Total, bool Self);
sealed record Board(string Mode, string Kind, string Title, string Phase, int Round, int? Rounds, int? FirstTo, int? Left, int? FragLimit,
    string Scenario, IReadOnlyList<BoardRow> Rows, IReadOnlyList<BoardTeam>? Teams);

static class Standings
{
    static string Title(string mode) => mode switch
    {
        LobbyModes.Race => "Score race", LobbyModes.Duel => "Score duel", LobbyModes.Rounds => "Free-for-all", LobbyModes.Practice => "Practice",
        LobbyModes.Tracking => "Tracking duel", LobbyModes.Deathmatch => "Deathmatch", LobbyModes.Vampiric => "Vampiric 1v1", LobbyModes.Instagib => "Instagib",
        LobbyModes.TeamDeathmatch => "Team deathmatch", LobbyModes.Cs => "CS competitive", _ => "Match",
    };
    static double? Kd(int frags, int deaths) => Math.Round(deaths == 0 ? frags : frags / (double)deaths, 2);

    // hostNow: the host's clock (match times are on it). combat: the live combat view, if the mode has one.
    public static Board? Build(LobbySnapshot lobby, MatchSnapshot m, string self, long hostNow, CombatView? combat)
    {
        string Name(string id) => LobbyRules.CleanName(lobby.Members.FirstOrDefault(x => x.Id == id)?.Name ?? m.Standings.FirstOrDefault(s => s.MemberId == id)?.Name, "Player");
        int Wins(string id) => m.Rounds.Count(r => r.WinnerId == id);
        bool Bot(string id) => lobby.Members.Any(x => x.Id == id && x.Bot is not null);
        int? left = m.Phase == MatchPhases.Live && m.StartsAt is { } start ? (int)Math.Max(0, Math.Ceiling((start + m.TimeLimit * 1000 - hostNow) / 1000.0)) : null;
        Board Make(string kind, IEnumerable<BoardRow> rows, IReadOnlyList<BoardTeam>? teams = null, int? fragLimit = null) =>
            new(m.Mode, kind, Title(m.Mode), m.Phase, m.Round, m.TotalRounds, m.FirstTo, left, fragLimit, m.Scenario, rows.ToArray(), teams);

        if (m.Cs is { } cs)
        {
            var teams = new[] { 1, 2 }.Select(t => new BoardTeam(t, (t == 1 ? cs.Team1Side : cs.Team1Side == CsRules.T ? CsRules.CT : CsRules.T) == CsRules.T ? "Terrorists" : "Counter-Terrorists",
                cs.Score.Length >= t ? cs.Score[t - 1] : 0, cs.Players.Any(p => p.Member == self && p.Team == t))).ToArray();
            var rows = cs.Players.OrderBy(p => p.Team).ThenByDescending(p => p.Kills).ThenBy(p => p.Deaths)
                .Select((p, i) => new BoardRow(Name(p.Member), p.Member == self, i + 1, null, null, null, null, p.Kills, p.Deaths, Kd(p.Kills, p.Deaths), p.Alive ? p.Health : 0, p.Team, p.Alive ? "alive" : "down", Bot(p.Member)));
            return Make("team", rows, teams);
        }
        if (combat is not null && LobbyModes.Combat(m.Mode))
        {
            var ordered = combat.Players.OrderByDescending(p => p.Frags).ThenBy(p => p.Deaths).ToList();
            var vampiric = m.Mode == LobbyModes.Vampiric;
            BoardRow Row(CombatPlayerView p, int rank) => new(Name(p.Member), p.Member == self, rank, null, null, null, null, p.Frags, p.Deaths, Kd(p.Frags, p.Deaths),
                vampiric || m.Mode == LobbyModes.TeamDeathmatch ? Math.Round(p.Alive ? p.Health : 0) : null, p.Team, p.Alive ? "alive" : "down", Bot(p.Member));
            if (m.Mode == LobbyModes.TeamDeathmatch)
            {
                var teams = new[] { 1, 2 }.Select(t => new BoardTeam(t, t == 1 ? "Mint" : "Rose", combat.TeamFrags is { Count: >= 2 } tf ? tf[t - 1] : combat.Players.Where(p => p.Team == t).Sum(p => p.Frags),
                    combat.Players.Any(p => p.Member == self && p.Team == t))).ToArray();
                return Make("team", ordered.OrderBy(p => p.Team).ThenByDescending(p => p.Frags).Select((p, i) => Row(p, i + 1)), teams, combat.FragLimit);
            }
            return Make("combat", ordered.Select((p, i) => Row(p, i + 1)), null, combat.FragLimit);
        }
        if (m.Mode == LobbyModes.Tracking)
        {
            var rows = m.Players.Select(id => (id, t: m.Tracking?.FirstOrDefault(x => x.Member == id)))
                .OrderByDescending(x => Wins(x.id)).ThenByDescending(x => x.t?.Percent ?? 0)
                .Select((x, i) => new BoardRow(Name(x.id), x.id == self, i + 1, null, null, Wins(x.id), x.t is null ? null : Math.Round(x.t.Percent, 1, MidpointRounding.AwayFromZero), null, null, null, null, 0, x.t?.Disputed == true ? "disputed" : "playing"));
            return Make("tracking", rows);
        }
        // Score modes: the live round's score; duels add round wins.
        var lines = m.Players.Select(id => (id, line: m.Live.FirstOrDefault(l => l.MemberId == id))).OrderByDescending(x => x.line?.Score ?? double.MinValue).ToList();
        var leader = lines.FirstOrDefault().line?.Score;
        if (m.Mode == LobbyModes.Duel)
            return Make("duel", lines.OrderByDescending(x => Wins(x.id)).ThenByDescending(x => x.line?.Score ?? 0)
                .Select((x, i) => new BoardRow(Name(x.id), x.id == self, i + 1, x.line?.Score, null, Wins(x.id), null, null, null, null, null, 0, x.line?.Status ?? "waiting")));
        return Make("score", lines.Select((x, i) => new BoardRow(Name(x.id), x.id == self, i + 1, x.line?.Score,
            i > 0 && leader is { } top && x.line?.Score is { } sc ? Math.Round(sc - top, 1) : null, null, null, null, null, null, null, 0, x.line?.Status ?? "waiting")));
    }

    // The corner panel: the top three and you, so it stays small.
    public static Board Compact(Board full)
    {
        var rows = full.Rows.Take(3).ToList();
        if (full.Rows.FirstOrDefault(r => r.Self) is { } me && !rows.Contains(me)) rows.Add(me);
        return full with { Rows = rows };
    }
}

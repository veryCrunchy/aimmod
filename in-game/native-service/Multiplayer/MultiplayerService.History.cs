namespace AimMod.InGame.Multiplayer;

// Round replays kept with a finished match: this machine's run and the others it received.
sealed record RecentReplay(int Round, string? Mine, IReadOnlyList<RecentReplayRun> Others);
sealed record RecentReplayRun(string Key, string Name, string Id);

// Match history and rivals (local only, separate from KovaaK's leaderboards).
// Opponents are told apart by a hash of their member id, so a renamed friend stays
// one rival and no Steam id is stored or shown.
sealed partial class MultiplayerService
{
    public const int RecentInView = 8;
    static string PlayerKey(string memberId) => ContentLibrary.TextHash("player:" + memberId)[..12];

    // Replays arrive after the results, so they are re-read from the swap while this session has them.
    IReadOnlyList<RecentReplay>? MatchReplays(string matchId, int rounds, IReadOnlyList<RecentPlayer> players)
    {
        var list = new List<RecentReplay>();
        for (var r = 1; r <= Math.Clamp(rounds, 1, 50); r++)
        {
            var others = swap.Received.Where(x => x.Match == matchId && x.Round == r && x.Kind == "round" && x.Owner != SelfId)
                .Select(x => { var key = PlayerKey(x.Owner); return new RecentReplayRun(key, players.FirstOrDefault(p => p.Key == key)?.Name ?? "Player", x.Id); }).ToArray();
            var mine = swap.MineFor(matchId, r);
            if (mine is not null || others.Length > 0) list.Add(new RecentReplay(r, mine, others));
        }
        return list.Count == 0 ? null : list;
    }

    void RefreshHistoryReplays()
    {
        var changed = false;
        for (var i = 0; i < recent.Count && i < RecentInView; i++)
        {
            var m = recent[i];
            if (MatchReplays(m.Id, m.Rounds, m.Standings) is not { } fresh) continue;
            var had = m.Replays?.Sum(r => (r.Mine is null ? 0 : 1) + r.Others.Count) ?? 0;
            if (fresh.Sum(r => (r.Mine is null ? 0 : 1) + r.Others.Count) <= had) continue;
            recent[i] = m with { Replays = fresh }; changed = true;
        }
        if (changed) SaveHistory();
    }

    public object HistoryView()
    {
        lock (gate)
        {
            RefreshHistoryReplays();
            return new { matches = recent, rivals = Rivals() };
        }
    }

    // Head-to-head against everyone played at least twice: who placed higher each match.
    IReadOnlyList<object> Rivals()
    {
        var table = new Dictionary<string, (string Name, int Played, int Won, int Lost, long Last, string Scenario)>();
        foreach (var m in recent)
        {
            if (!LobbyModes.Scored(m.Mode)) continue;
            var me = m.Standings.FirstOrDefault(p => p.Self);
            if (me is null) continue;
            foreach (var p in m.Standings)
            {
                if (p.Self || p.Key is null) continue;
                var row = table.TryGetValue(p.Key, out var seen) ? seen : (Name: p.Name, Played: 0, Won: 0, Lost: 0, Last: 0L, Scenario: m.Scenario);
                // History is newest first: the first sighting carries the current name.
                if (row.Played == 0) { row.Name = p.Name; row.Last = m.EndedAt; row.Scenario = m.Scenario; }
                row.Played++;
                if (me.Place < p.Place) row.Won++; else if (me.Place > p.Place) row.Lost++;
                table[p.Key] = row;
            }
        }
        return table.Where(t => t.Value.Played >= 2).OrderByDescending(t => t.Value.Played).ThenByDescending(t => t.Value.Last).Take(12)
            .Select(t => (object)new { key = t.Key, name = t.Value.Name, played = t.Value.Played, won = t.Value.Won, lost = t.Value.Lost, drawn = t.Value.Played - t.Value.Won - t.Value.Lost, lastAt = t.Value.Last, lastScenario = t.Value.Scenario })
            .ToArray();
    }
}

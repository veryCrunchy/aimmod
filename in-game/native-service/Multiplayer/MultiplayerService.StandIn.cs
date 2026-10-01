namespace AimMod.InGame.Multiplayer;

// Developer mode: simulated players have no game and no pose stream, so nothing would draw
// them. While this machine runs a simulated lobby's match, every simulated player (up to 16) is
// one of AimModSteam's synthetic peers ("1", "2", ...), walking between its own side's spawns:
// each stream maps back to its member, its drawn positions become the member's own track on
// the host (so hits on it validate like any player's), and avatar-state.tsv drives its team,
// health and deaths. A bridge without several walkers shows the first one only (peer 1).
// Bots (MultiplayerService.Bots.cs) are stand-ins too, on every machine: the host's walk on their
// own (its bot logic steers them), a client's follow the positions the host sends.
sealed partial class MultiplayerService
{
    // AimModSteam's first synthetic peer (Ghosts.cpp TestPeer).
    public const string StandInPeer = "1";
    public const int MaxStandIns = 16;
    readonly Dictionary<string, string> standIns = new(StringComparer.Ordinal); // member -> peer
    bool standInAuto; long standInTriedAt = long.MinValue / 2; string? standInSent;

    // The first simulated member shown, while a match of ours runs.
    internal string? StandInMember => standIns.FirstOrDefault(p => p.Value == StandInPeer).Key;
    internal IReadOnlyDictionary<string, string> StandIns => standIns;

    void UpdateStandIn()
    {
        standIns.Clear();
        var dev = Simulation is not null && core is not null;
        if (Current is { Match: { Phase: MatchPhases.Loading or MatchPhases.Countdown or MatchPhases.Live } match } lobby && match.Players.Contains(SelfId))
            foreach (var (id, i) in match.Players.Where(id => id != SelfId && lobby.Members.Any(m => m.Id == id && (IsBot(m) || (dev && m.Simulated)))).Take(MaxStandIns).Select((id, i) => (id, i)))
                standIns[id] = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (standIns.Count == 0)
        {
            if (standInAuto) { standInAuto = false; standInSent = null; transport.DevAvatar(false, "circle", null); }
            return;
        }
        // The developer menu's own test avatar wins.
        if (devAvatarState is { On: true } && !standInAuto) return;
        var walkers = standIns.Select(p =>
        {
            var (points, own) = WalkPointsFor(p.Key);
            return (Peer: p.Value, Look: AvatarProfiles.Find(Current!.Members.FirstOrDefault(m => m.Id == p.Key)?.Avatar ?? "")?.ProfileName, Spawns: (IReadOnlyList<double[]>)points, Own: own);
        }).ToArray();
        var key = string.Join("|", walkers.Select(w => w.Peer + ":" + w.Look + ":" + w.Own + ":" + string.Join(";", w.Spawns.Select(s => string.Join(",", s)))));
        if (key == standInSent || clock() - standInTriedAt < 2000) return;
        standInTriedAt = clock();
        // Several walkers when the bridge can, else the first one walks (or, a developer's simulated
        // player, circles you without spawns; a bot never circles).
        var first = walkers[0];
        var sent = transport.DevWalkers(walkers.Select(w => (w.Peer, w.Look, w.Spawns, w.Own)).ToArray())
            || (first.Spawns.Count > 0 ? transport.DevAvatar(true, "walk", first.Look, first.Spawns) : dev && transport.DevAvatar(true, "circle", first.Look));
        if (sent) { standInAuto = true; standInSent = key; }
    }

    // Where a stand-in walks, and how many of those points (the first ones) it may be placed on. A
    // simulated player walks between its spawns. The host's bots get waypoints across the map: their
    // own spawns first, then (CS) the bomb sites, the callouts and the other side's spawns; a client's
    // bots get none (they follow the host).
    (double[][] Points, int Own) WalkPointsFor(string member)
    {
        var own = SpawnsFor(member);
        if (Current?.Members.FirstOrDefault(m => m.Id == member) is not { Bot: not null }) return (own, own.Length);
        if (core is null) return ([], 0);
        own = own.Take(8).ToArray();
        IEnumerable<double[]> more;
        if (Current?.Match?.Cs is not null && csObjectives is { } map)
            more = map.BombSites.Concat(map.Callouts).Select(z => new[] { Math.Round((z.Min[0] + z.Max[0]) / 2, 1), Math.Round((z.Min[1] + z.Max[1]) / 2, 1), Math.Round(z.Min[2] + 60, 1) })
                .Concat(map.Spawns.Select(s => new[] { s.X, s.Y, s.Z }));
        else more = arenaSpawns.Select(s => new[] { s.X, s.Y, s.Z });
        var points = own.ToList();
        foreach (var p in more)
        {
            if (points.Count >= 32) break;
            if (points.Any(q => Math.Abs(q[0] - p[0]) < 50 && Math.Abs(q[1] - p[1]) < 50)) continue;
            points.Add(p);
        }
        return (points.ToArray(), own.Length);
    }

    // A simulated player's spawns: its CS side's, its TDM team's, else every arena spawn (world cm).
    double[][] SpawnsFor(string member)
    {
        var match = Current?.Match;
        if (match?.Cs is { } cs && csObjectives is { } objectives && cs.Players.FirstOrDefault(p => p.Member == member) is { } player)
            return objectives.SpawnsFor(player.Side).Take(32).Select(s => new[] { s.X, s.Y, s.Z }).ToArray();
        var team = match?.Combat?.Players.FirstOrDefault(p => p.Member == member)?.Team ?? 0;
        return arenaSpawns.Where(s => team == 0 || s.TeamMask == 0 || (s.TeamMask & team) != 0).Take(32).Select(s => new[] { s.X, s.Y, s.Z }).ToArray();
    }

    // The stand-ins' own tracks on the host: where this machine's game drew their avatars
    // (capsule centre), at standing eye height, so claims against them validate.
    void FeedStandIn(TrackBatch batch)
    {
        if (core is null || standIns.Count == 0) return;
        foreach (var group in batch.Seen.Where(v => v.Member is { } m && standIns.ContainsKey(m)).GroupBy(v => v.Member!))
        {
            var samples = group.Select(v => new TrackSample(v.T, v.X, v.Y, v.Z + TrackingRound.DefaultEyeAboveCentre, 0, 0)).ToArray();
            core.Track(group.Key, new TrackBatch(batch.MatchId, batch.Round, samples, []));
        }
    }

    // Streams that stand for members (the synthetic peers' avatars).
    IEnumerable<(string Stream, string Member)> StandInStreams() => standIns.Select(p => (StreamIds.For(p.Value), p.Key));

    // avatar-state.tsv names bridge peers: a stand-in is its synthetic peer.
    string AvatarPeer(string member) => standIns.TryGetValue(member, out var peer) ? peer : member;
}

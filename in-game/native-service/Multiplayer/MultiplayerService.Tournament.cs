using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

// What a tournament game needs from the lobby: the host's client creates (or
// moves on) a locked lobby for the game; the other player's client joins it.
// The tournament service (Tournaments/) talks to AimMod Hub; this side only
// runs the lobby.
// JoinToken: the Hub's per-match secret; OpponentPeer: the opponent's SteamID64.
sealed record TournamentGameSpec(string TournamentId, string MatchId, string Label, int Game, string Scenario, int TimeLimit, long Seed,
    int Countdown, bool Spectators, string OpponentPeer, string OpponentName, string? TournamentName = null, string? JoinToken = null);

// One player of the tournament lobby, for live state and the host overview.
sealed record TournamentLobbyPlayer(string MemberId, string Name, bool Self, double? Score, double? Accuracy, double? Remaining,
    int? Ping, string Connection, string Status, bool Ready, int Shots, int Hits);

// The lobby as the tournament sees it. Finished is set when the current game
// has final results (players' scores, in member order).
sealed record TournamentLobbyState(string TournamentId, string MatchId, int Game, long Seed, bool IsHost, string Phase, string LobbyMatch,
    string Scenario, IReadOnlyList<TournamentLobbyPlayer> Players, int Spectators, string? LobbyToken, long? StartedAt, long? EndedAt,
    string? SettingsKey, string? ScenarioHash, bool Finished, string HostKey);

sealed partial class MultiplayerService
{
    // Set by the workspace host; the developer page and the notice layer reach tournaments through it.
    public Tournaments.TournamentService? Tournaments { get; set; }

    internal static string KeyOf(string memberId) => PlayerKey(memberId);

    // Host: create the game's locked lobby, or move the existing one on to this game.
    public LobbyResult HostTournamentGame(TournamentGameSpec spec)
    {
        lock (gate)
        {
            if (library.Scenario(spec.Scenario) is not { } scenario)
                return LobbyResult.Fail("scenario-missing", "You don’t have “" + spec.Scenario + "”. Get it from the Workshop or the organiser to host this game.");
            var locked = core?.Settings.Tournament is { } t && t.TournamentId == spec.TournamentId && t.MatchId == spec.MatchId;
            if (Current is not null && !locked)
            {
                if (core is null || hostPeer is not null) Leave("left");
                else if (core.Snapshot().Match is { Phase: not MatchPhases.Final }) return LobbyResult.Fail("in-match", "Finish your current match first.");
                else Leave("left");
            }
            var players = new List<string> { SelfId };
            if (spec.OpponentPeer.Length > 0) players.Add(spec.OpponentPeer);
            var settings = new LobbySettings(Mode: LobbyModes.Race, Scenario: scenario, MaxPlayers: 2, Spectators: spec.Spectators, Rounds: 1,
                TimeLimit: spec.TimeLimit is >= 10 and <= 600 ? spec.TimeLimit : null, Privacy: LobbyPrivacy.Invite, Countdown: Math.Clamp(spec.Countdown, 3, 10),
                AutoStart: true, Voting: false,
                Tournament: new TournamentLock(spec.TournamentId, spec.MatchId, LobbyRules.CleanName(spec.Label, "Tournament match"), spec.Game, spec.Seed, players, spec.TournamentName));
            var simulatedOpponent = Simulation is not null && (spec.OpponentPeer.StartsWith("sim-", StringComparison.Ordinal) || !transport.Available);
            if (core is null && !simulatedOpponent && transport.Available)
            {
                if (!IMultiplayerTransport.ValidTournamentToken(spec.JoinToken)) return LobbyResult.Fail("token", "AimMod Hub didn’t send this match’s lobby token. Refresh and try again.");
                if (spec.OpponentPeer.Length is 0 or > 20 || !spec.OpponentPeer.All(char.IsAsciiDigit)) return LobbyResult.Fail("steam", LobbyRules.CleanName(spec.OpponentName, "Your opponent") + " hasn’t linked Steam on AimMod Hub, so they can’t join a lobby yet.");
                transport.PrepareTournament(spec.JoinToken, spec.OpponentPeer);
            }
            if (core is null)
            {
                selfName = LocalName();
                core = new LobbyCore(SelfId, selfName, settings with { Tournament = settings.Tournament }, clock);
                core.LockTournament(settings);
                Reset();
                ReportContent(force: true);
                transport.Advertise(core.Snapshot());
                notice = ("info", "Tournament lobby ready. " + LobbyRules.CleanName(spec.OpponentName, "Your opponent") + " joins automatically.", clock());
                InviteOpponent(spec);
                return LobbyResult.Success;
            }
            var result = core.LockTournament(settings);
            if (result.Ok) { ReportContent(force: true); InviteOpponent(spec); }
            return result;
        }
    }

    // No Steam invite: the opponent's client joins the invisible tournament lobby by
    // the id the Hub relays, with the match token (friends or not).
    // In the developer simulation a simulated opponent joins instead.
    void InviteOpponent(TournamentGameSpec spec)
    {
        if (core is null) return;
        var snapshot = core.Snapshot();
        if (snapshot.Members.Any(m => m.Id == spec.OpponentPeer)) return;
        if (Simulation is not null && (spec.OpponentPeer.StartsWith("sim-", StringComparison.Ordinal) || !transport.Available))
        {
            Simulation.Add(core, name: LobbyRules.CleanName(spec.OpponentName, "Opponent"));
            // The simulated opponent gets a generated id; it is the second tournament player.
            var bot = core.Members.LastOrDefault(m => m.Simulated && m.Id != SelfId);
            if (bot is not null && core.Settings.Tournament is { } t && !t.Players.Contains(bot.Id))
                core.LockTournament(core.Settings with { Tournament = t with { Players = [SelfId, bot.Id] } });
            if (bot is not null && core.Members.FirstOrDefault(m => m.Id == bot.Id) is { Role: MemberRoles.Spectator })
                core.Apply(bot.Id, "role", JsonSerializer.SerializeToElement(new { spectator = false }), library);
        }
    }

    // Opponent: join the host's tournament lobby by the id the Hub relayed, with the match token.
    public LobbyResult JoinTournamentLobby(string lobbyId, string matchId, string? joinToken = null)
    {
        lock (gate)
        {
            if (Current?.Settings.Tournament?.MatchId == matchId) return LobbyResult.Success;
            if (joinPendingSince is not null || (hostPeer is not null && mirror is null)) return LobbyResult.Success;
            if (Current is { Match: { Phase: not MatchPhases.Final } }) return LobbyResult.Fail("in-match", "Finish your current match first.");
            if (Current is not null) Leave("left");
            selfName = LocalName();
            if (joinToken is not null && transport.BeginTournamentJoin(lobbyId, joinToken)) { joinPendingSince = clock(); notice = null; return LobbyResult.Success; }
            if (!transport.Available && Simulation is not null) return JoinBy(lobbyId, invite: true);
            return LobbyResult.Fail("join", "Couldn’t join the tournament lobby. AimMod’s Steam connection may not be ready.");
        }
    }

    public LobbyResult LeaveTournamentLobby(string matchId)
    {
        lock (gate)
        {
            if (Current?.Settings.Tournament?.MatchId != matchId) return LobbyResult.Success;
            Leave("left");
            return LobbyResult.Success;
        }
    }

    public TournamentLobbyState? TournamentLobby()
    {
        lock (gate)
        {
            if (Current is not { Settings.Tournament: { } t } lobby) return null;
            var match = lobby.Match;
            var lines = match?.Live ?? [];
            var players = lobby.Members.Where(m => t.Players.Count == 0 ? m.Role == MemberRoles.Player : t.Players.Contains(m.Id)).Select(m =>
            {
                var line = lines.FirstOrDefault(l => l.MemberId == m.Id);
                var standing = match?.Phase == MatchPhases.Final ? match.Standings.FirstOrDefault(s => s.MemberId == m.Id) : null;
                double? score = standing?.Best ?? line?.Score;
                double? accuracy = line is { Shots: > 0 } ? 100.0 * line.Hits / line.Shots : null;
                return new TournamentLobbyPlayer(m.Id, m.Name, m.Id == SelfId, score, accuracy, line?.Remaining, m.Ping, m.Connection,
                    line?.Status ?? (m.Ready ? "ready" : "waiting"), m.Ready, line?.Shots ?? 0, line?.Hits ?? 0);
            }).ToArray();
            var finished = match is { Phase: MatchPhases.Final } && match.Players.Count == 2;
            return new TournamentLobbyState(t.TournamentId, t.MatchId, t.Game, t.Seed, lobby.HostId == SelfId, match?.Phase ?? "lobby", match?.Id ?? "",
                lobby.Settings.Scenario?.Name ?? "", players, lobby.Members.Count(m => m.Role == MemberRoles.Spectator), transport.LobbyToken,
                match?.StartsAt, match?.EndsAt, MatchScenario.Key(lobby.Settings), lobby.Settings.Scenario?.Hash, finished, PlayerKey(lobby.HostId));
        }
    }

    // The scenario this machine actually plays for the tournament game (generated when the length changes).
    public string? TournamentPlayedScenario()
    {
        lock (gate) return Current is { Settings: { Tournament: not null } s } ? (MatchScenario.Needed(s) ? MatchScenario.Name(s) : s.Scenario?.Name) : null;
    }

    // This machine's replay of a finished tournament game, when one was recorded.
    public byte[]? TournamentReplay(string lobbyMatch)
    {
        lock (gate) return swap.MineFor(lobbyMatch, 1) is { } id ? swap.ReadOwn(id) : null;
    }

    public bool CanHostLobby => transport.Available || Simulation is not null;
    public string SelfName { get { lock (gate) return LocalName(); } }
    public IReadOnlyList<string> LibraryScenarioNames() => library.Available ? library.Scenarios.Take(50).Select(s => s.Name).ToArray() : [];
}

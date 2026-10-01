using System.Security.Cryptography;
using System.Text;

namespace AimMod.InGame.Multiplayer;

// Lobby and match summary for the Discord presence, and Discord joins.
//
// Nothing here reaches Discord with a Steam id in it:
// - party.id is a hash of the AimMod lobby id (random, not a Steam id).
// - secrets.join is a one-way hash of the Steam lobby token. Discord delivers it
//   only to someone who used Join or Ask to Join. Their AimMod resolves it by
//   hashing the lobby tokens of their own Steam friends' joinable AimMod lobbies,
//   so a Discord join reaches the same people as Steam's own "Join Game", and
//   the lobby's privacy and the host's checks still apply.
static class MultiplayerDiscord
{
    const string JoinPrefix = "aimmod1:";
    public static string PartyId(string lobbyId) => "aimmod-" + Hash("aimmod-discord-party|" + lobbyId)[..24];
    public static string JoinSecret(string steamLobbyToken) => JoinPrefix + Hash("aimmod-discord-join|" + steamLobbyToken)[..40];
    static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    public static bool ValidSecret(string? secret) => secret is { Length: 48 } && secret.StartsWith(JoinPrefix, StringComparison.Ordinal) && secret[JoinPrefix.Length..].All(char.IsAsciiHexDigitLower);

    // The Steam lobby token of the friend lobby the secret names, if any.
    public static string? Resolve(string secret, IEnumerable<FriendEntry> friends)
    {
        if (!ValidSecret(secret)) return null;
        foreach (var friend in friends)
            if (friend.Joinable && friend.Code is { } token && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(JoinSecret(token)), Encoding.ASCII.GetBytes(secret)))
                return token;
        return null;
    }

    public static string ModeLabel(string mode) => mode switch
    {
        LobbyModes.Race => "Score race",
        LobbyModes.Duel => "Score duel",
        LobbyModes.Rounds => "Rounds",
        LobbyModes.Practice => "Practice",
        _ => "Match",
    };

    // joinToken: this machine's Steam lobby token, when the transport has one.
    public static DiscordLobbyInfo Summarize(LobbySnapshot lobby, string selfId, string? joinToken)
    {
        var s = lobby.Settings;
        var players = lobby.Members.Count(m => m.Role == MemberRoles.Player);
        var match = lobby.Match;
        var state = match is null ? "lobby" : match.Phase == MatchPhases.Final ? "results" : "match";
        double? lead = null; int? place = null; bool? won = null;
        if (match is not null)
        {
            var standing = match.Standings.FirstOrDefault(x => x.MemberId == selfId);
            place = standing?.Place;
            if (match.Phase == MatchPhases.Final) won = match.WinnerId is null ? null : match.WinnerId == selfId;
            if (match.Phase == MatchPhases.Live)
            {
                // Live: this round's score against the best other player.
                var mine = match.Live.FirstOrDefault(l => l.MemberId == selfId)?.Score;
                var others = match.Live.Where(l => l.MemberId != selfId && l.Status != LineStates.Left && l.Score is not null).Select(l => l.Score!.Value).ToArray();
                if (mine is double m && others.Length > 0) lead = m - others.Max();
            }
            else if (standing is not null && match.Standings.Count > 1 && LobbyModes.Scored(match.Mode))
            {
                // Between rounds: total against the best other total.
                var best = match.Standings.Where(x => x.MemberId != selfId).Select(x => x.Total).DefaultIfEmpty(0).Max();
                lead = standing.Total - best;
            }
        }
        var joinable = state != "match" || s.LateJoin;
        joinable &= players < s.MaxPlayers && s.Privacy != LobbyPrivacy.Invite && joinToken is not null;
        var scenario = match?.Scenario is { Length: > 0 } name ? name : s.Scenario?.Name;
        return new DiscordLobbyInfo(PartyId(lobby.Id), players, s.MaxPlayers, ModeLabel(s.Mode), scenario, state,
            match?.Round, match?.TotalRounds, match?.FirstTo, lead, place, won, joinable ? JoinSecret(joinToken!) : null);
    }
}

using System.Globalization;

namespace AimMod.InGame.Multiplayer;

// Spectating inside a lobby: follow one player, or follow whoever leads the round.
// Switching waits a few seconds between leaders so the camera doesn't flicker
// when two scores trade places.
sealed partial class MultiplayerService
{
    const long FollowSwitchMs = 8000;
    bool followLeader;
    // Pose stream ids the bridge reported per watched peer (spectate.started {stream}).
    readonly Dictionary<string, string> spectateStreams = new();
    string? spectateStartedFor;
    long followSwitchedAt = long.MinValue / 2;

    LobbyResult SpectateFollow(bool on)
    {
        if (Current is not { Match: { } } lobby) return LobbyResult.Fail("no-match", "Follow the leader works during a match.");
        followLeader = on;
        if (!on) return LobbyResult.Success;
        followSwitchedAt = long.MinValue / 2;
        if (Leader(lobby) is null && spectating is null) { followLeader = false; return LobbyResult.Fail("none", "There’s nobody to follow yet."); }
        FollowLeader(clock());
        return LobbyResult.Success;
    }

    // The highest live score among real players other than this machine.
    string? Leader(LobbySnapshot lobby) => lobby.Match is { } match ? LeaderOf(match, lobby.Members, SelfId) : null;
    internal static string? LeaderOf(MatchSnapshot match, IReadOnlyList<LobbyMember> members, string self) =>
        match.Live.Where(l => l.MemberId != self && l.Score is not null && l.Status is not ("left" or "dnf") && members.Any(m => m.Id == l.MemberId && !m.Simulated))
            .OrderByDescending(l => l.Score).Select(l => l.MemberId).FirstOrDefault();

    void FollowLeader(long now)
    {
        if (!followLeader) return;
        if (Current is not { Match: { Phase: MatchPhases.Live or MatchPhases.Round } } lobby) return;
        if (Leader(lobby) is not { } leader || leader == spectating || now - followSwitchedAt < FollowSwitchMs) return;
        if (!transport.StartSpectate(leader, 60)) return;
        spectating = leader; spectateStartedFor = null; followSwitchedAt = now; watchScore = null;
    }

    // "Watching Juniper · 12,400 · 18 s left" over the spectator view, from the lobby's live line.
    string? LobbySpectateBadge()
    {
        if (spectating is null || Current is not { Match: { Phase: MatchPhases.Live } match } lobby) return null;
        var name = lobby.Members.FirstOrDefault(m => m.Id == spectating)?.Name ?? "Player";
        var parts = new List<string> { (followLeader ? "Following the leader: " : "Watching ") + name };
        if (match.Live.FirstOrDefault(l => l.MemberId == spectating) is { } line)
        {
            if (line.Score is { } score) parts.Add(score.ToString("N0", CultureInfo.InvariantCulture));
            if (line.Shots > 0) parts.Add((line.Hits * 100.0 / line.Shots).ToString("0.#", CultureInfo.InvariantCulture) + "%");
            if (line.Remaining is { } left) parts.Add(Math.Ceiling(left) + " s left");
        }
        return string.Join(" · ", parts);
    }
}

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
        match.Live.Where(l => l.MemberId != self && l.Score is not null && l.Status is not ("left" or "dnf") && members.Any(m => m.Id == l.MemberId && !m.Simulated && m.Bot is null))
            .OrderByDescending(l => l.Score).Select(l => l.MemberId).FirstOrDefault();

    void FollowLeader(long now)
    {
        if (!followLeader) return;
        if (Current is not { Match: { Phase: MatchPhases.Live or MatchPhases.Round } } lobby) return;
        if (Leader(lobby) is not { } leader || leader == spectating || now - followSwitchedAt < FollowSwitchMs) return;
        if (!transport.StartSpectate(leader, 60)) return;
        spectating = leader; spectateStartedFor = null; followSwitchedAt = now; watchScore = null;
    }

    // ---- CS: spectating while dead -----------------------------------------------------------
    // Down in a CS round, the camera follows a living player until the round is over (the next
    // round respawns you): teammates first, like CS, anyone alive when no teammate is. Click or
    // Space watches the next player, right click the previous one; the HUD names who you watch.
    // Every player, bots included, is an avatar this game draws, so AimModSteam's camera follows
    // the avatar it draws (spectate-view.tsv names its peer): no pose stream from another machine
    // is needed, which also covers bots (stand-ins) and the bridge refusing a stream of this
    // machine's own stand-ins.
    public const string SpectateViewFile = "spectate-view.tsv";
    string? deadWatch; string? deadWatchFor; string? viewWritten; long viewWrittenAt;
    internal string? DeadWatch => deadWatch;

    // Who a dead player may watch, in match order: living teammates, else anyone alive.
    internal static IReadOnlyList<string> DeadWatchCandidates(CsView cs, IReadOnlyList<string> order, string self)
    {
        var side = cs.Players.FirstOrDefault(p => p.Member == self)?.Side;
        var alive = order.Where(id => id != self && cs.Players.Any(p => p.Member == id && p.Alive)).ToList();
        var mates = alive.Where(id => cs.Players.First(p => p.Member == id).Side == side).ToList();
        return mates.Count > 0 ? mates : alive;
    }

    // The player to watch: `current` while it can still be watched (step 0), else the next one
    // (step 1) or the previous one (step -1) in the list; the first one when `current` is gone.
    internal static string? NextDeadWatch(IReadOnlyList<string> candidates, string? current, int step)
    {
        if (candidates.Count == 0) return null;
        var i = current is null ? -1 : candidates.ToList().IndexOf(current);
        if (i < 0) return candidates[step < 0 ? candidates.Count - 1 : 0];
        return candidates[((i + step) % candidates.Count + candidates.Count) % candidates.Count];
    }

    // Down and the round still on (live, planted or its end): who is watched, and the camera file.
    void DeadSpectate(MatchSnapshot match, CsView cs, CsPlayerView me, bool keys)
    {
        var key = match.Id + "#" + cs.Round;
        var down = !me.Alive && match.Phase == MatchPhases.Live && cs.Phase is "live" or "planted" or "end";
        if (!down) { deadWatch = null; deadWatchFor = null; WriteSpectateView(null); return; }
        if (deadWatchFor != key)
        {
            // Just went down: the click that killed nobody (or the fire still held) doesn't skip ahead.
            deadWatchFor = key; deadWatch = null;
            csKeys.Pressed((char)0x01); csKeys.Pressed((char)0x02); csKeys.Pressed(' ');
        }
        var step = !keys ? 0 : csKeys.Pressed((char)0x01) || csKeys.Pressed(' ') ? 1 : csKeys.Pressed((char)0x02) ? -1 : 0;
        deadWatch = NextDeadWatch(DeadWatchCandidates(cs, match.Players, SelfId), deadWatch, step);
        WriteSpectateView(deadWatch is { } who ? AvatarPeer(who) : null);
    }

    // spectate-view.tsv: "AIMMOD_VIEW_1\t<unix ms>" then "view\t<peer>"; rewritten every second
    // while watching (AimModSteam ignores it after 3 s) and removed when the view is yours again.
    internal static string FormatSpectateView(string peer, long unixMs) => "AIMMOD_VIEW_1\t" + unixMs.ToString(CultureInfo.InvariantCulture) + "\nview\t" + peer + "\n";
    void WriteSpectateView(string? peer)
    {
        if (outputFolder is null) return;
        var now = clock();
        if (peer == viewWritten && (peer is null || now - viewWrittenAt < 1000)) return;
        viewWritten = peer; viewWrittenAt = now;
        var path = Path.Combine(outputFolder, SpectateViewFile);
        try
        {
            if (peer is null) { if (File.Exists(path)) File.Delete(path); }
            else AtomicFile.WriteText(path, FormatSpectateView(peer, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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

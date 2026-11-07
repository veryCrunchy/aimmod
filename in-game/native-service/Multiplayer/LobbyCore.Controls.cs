using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

sealed partial class LobbyCore
{
    public bool ControlledSlot(string id) => match?.Cs?.Controlled(id) == true;
    MatchVote? matchVote;
    long nextVoteAt;
    string? controlMatch;
    readonly Dictionary<string, long> activity = [];
    readonly Dictionary<string, TrackSample> humanPose = [];
    readonly Dictionary<string, long> humanTrackAt = [];
    readonly Dictionary<string, TrackSample> handbackAt = [];
    readonly Dictionary<string, int> afkRounds = [], afkCountedRound = [];

    public LobbyResult ApplyBot(string id, string action, JsonElement args, IContentResolver content)
    {
        if (Find(id) is not { } member || (member.Bot is null && match?.Cs?.Controlled(id) != true)) return LobbyResult.Fail("not-bot", "Not a bot.");
        return action is "buy" or "hold" or "drop" or "use" or "throw" ? CsAction(member, action, args, automated: true) : Apply(id, action, args, content);
    }

    int PlayingTeam(Member member) => match?.Cs?.View().Players.FirstOrDefault(p => p.Member == member.Id)?.Team
        ?? match?.Combat?.View().Players.FirstOrDefault(p => p.Member == member.Id)?.Team ?? member.Team;

    LobbyResult MatchControl(Member member, string command, string? target = null)
    {
        if (match is not { Phase: MatchPhases.Live } m) return LobbyResult.Fail("not-now", "No match is running.");
        var now = clock();
        while (member.ControlTimes.Count > 0 && now - member.ControlTimes.Peek() >= 5000) member.ControlTimes.Dequeue();
        if (member.ControlTimes.Count >= 5) return LobbyResult.Fail("slow", "Slow down a little.");
        member.ControlTimes.Enqueue(now);
        if (command is "pause" or "unpause")
        {
            if (!IsHost(member.Id)) return LobbyResult.Fail("not-host", "Only the host can pause.");
            if (m.Cs is null) return LobbyResult.Fail("mode", "Pause is available in CS.");
            m.Cs.SetPaused(command == "pause", now); System(command == "pause" ? "The host paused the match." : "The host resumed the match.");
            return LobbyResult.Success;
        }
        if (member.Bot is not null || member.Role != MemberRoles.Player || !m.Players.Contains(member.Id)) return LobbyResult.Fail("spectator", "Only players can do that.");
        if (command == "return") { HumanActivity(member.Id); return LobbyResult.Success; }
        if (command == "ready" && m.Cs is { Phase: "warmup" } warmup) { warmup.ReadyWarmup(member.Id, true); Changed(); return LobbyResult.Success; }
        if (command is "yes" or "no")
        {
            if (matchVote is null || now >= matchVote.EndsAt || !matchVote.Cast(member.Id, command == "yes")) return LobbyResult.Fail("vote", "No vote available.");
            System(member.Name + " voted " + command + "."); ResolveVote(now); return LobbyResult.Success;
        }
        var team = PlayingTeam(member);
        if (team is not (1 or 2) || m.Settings.Mode is not (LobbyModes.Cs or LobbyModes.TeamDeathmatch)) return LobbyResult.Fail("mode", "Team votes need a team match.");
        if (command == "timeout")
        {
            var reason = m.Cs?.QueueTimeout(team, now) ?? (m.Cs is null ? "mode" : null);
            if (reason is not null) return LobbyResult.Fail(reason, "No timeout available this half.");
            System(member.Name + " called a 30-second tactical timeout for the next freeze time."); return LobbyResult.Success;
        }
        if (command is not ("surrender" or "kickvote")) return LobbyResult.Fail("command", "Unknown match control.");
        if (command == "surrender" && (m.Cs is null || m.Cs.Phase == "warmup")) return LobbyResult.Fail("mode", "Surrender is available during CS rounds.");
        if (matchVote is not null || now < nextVoteAt) return LobbyResult.Fail("vote-busy", "Wait before starting another vote.");
        var victim = target is null ? null : members.FirstOrDefault(p => p.Id == target)
            ?? members.SingleOrDefault(p => p.Name.Equals(target, StringComparison.OrdinalIgnoreCase) && members.Count(x => x.Name.Equals(target, StringComparison.OrdinalIgnoreCase)) == 1);
        if (command == "kickvote" && (victim is null || victim.Id == HostId || victim.Id == member.Id || victim.Bot is not null || victim.Role != MemberRoles.Player || PlayingTeam(victim) != team))
            return LobbyResult.Fail("target", "Choose a human teammate other than the host.");
        var humans = members.Where(p => p.Bot is null && p.Role == MemberRoles.Player && m.Players.Contains(p.Id) && PlayingTeam(p) == team).Select(p => p.Id);
        matchVote = new MatchVote(command == "kickvote" ? "kick" : command, command == "kickvote" ? "Kick " + victim!.Name : "Surrender", team, victim?.Id, humans, now);
        matchVote.Cast(member.Id, true); nextVoteAt = now + 60_000;
        System(member.Name + " started a team vote: " + matchVote.Label + ". F1 yes / F2 no (" + matchVote.Required + " yes required).");
        ResolveVote(now); return LobbyResult.Success;
    }

    void ResolveVote(long now)
    {
        if (matchVote is not { } vote || !vote.Finished(now)) return;
        matchVote = null;
        System(vote.Label + (vote.Passed ? " vote passed." : " vote failed."));
        if (!vote.Passed) return;
        if (vote.Kind == "surrender") match?.Cs?.Surrender(vote.Team, now);
        else if (vote.Target is { } target && target != HostId && Find(target) is { } victim && PlayingTeam(victim) == vote.Team)
        { banned.Add(target); Leave(target, "vote-kicked"); }
    }

    void HumanActivity(string id)
    {
        activity[id] = clock();
        if (match?.Cs is { } cs && cs.Controlled(id))
        {
            if (cs.Combat.Position(id) is { } at) handbackAt[id] = at;
            cs.Control(id, false, clock()); System((Find(id)?.Name ?? "Player") + " returned to play.");
        }
    }

    public void RecordHumanShot(string id)
    {
        if (Find(id) is { Bot: null, Role: MemberRoles.Player } && match?.Cs?.Controlled(id) != true) HumanActivity(id);
    }

    bool HumanTrack(string id, TrackBatch batch)
    {
        // A spectator camera follows the replacement. Only the player's input can reclaim it.
        if (match?.Cs?.Controlled(id) == true) return false;
        var now = clock();
        if (batch.Samples.LastOrDefault(s => s.T >= now - 1000 && s.T <= now + 250) is not { } pose || !double.IsFinite(pose.X + pose.Y + pose.Z)) return false;
        if (humanTrackAt.TryGetValue(id, out var last) && pose.T <= last) return false;
        if (handbackAt.TryGetValue(id, out var anchor))
        {
            if (Math.Abs(pose.X - anchor.X) + Math.Abs(pose.Y - anchor.Y) > 200) return false;
            handbackAt.Remove(id); humanPose[id] = pose;
        }
        humanTrackAt[id] = pose.T;
        if (!humanPose.TryGetValue(id, out var before)) humanPose[id] = pose;
        else if (Math.Abs(pose.X - before.X) + Math.Abs(pose.Y - before.Y) + Math.Abs(pose.Z - before.Z) > 6)
        { HumanActivity(id); humanPose[id] = pose; }
        return match?.Cs?.Controlled(id) != true && !handbackAt.ContainsKey(id);
    }

    void TickControls(long now)
    {
        if (controlMatch != match?.Id)
        {
            controlMatch = match?.Id; matchVote = null; nextVoteAt = 0; activity.Clear(); humanPose.Clear(); humanTrackAt.Clear(); handbackAt.Clear(); afkRounds.Clear(); afkCountedRound.Clear();
        }
        if (match is not { Phase: MatchPhases.Live } m)
        {
            matchVote = null;
            foreach (var id in activity.Keys.ToArray()) activity[id] = now;
            return;
        }
        ResolveVote(now);
        foreach (var member in members.Where(p => p.Bot is null && p.Role == MemberRoles.Player && m.Players.Contains(p.Id)).ToArray())
        {
            var id = member.Id;
            if (!activity.ContainsKey(id)) activity[id] = now;
            if (m.Cs is { } cs)
            {
                if (cs.Paused || cs.Phase is not ("live" or "planted")) { activity[id] = now; continue; }
                if (!cs.Combat.Alive(id)) continue;
                if (!cs.Controlled(id) && now - activity[id] >= 60_000)
                { cs.Control(id, true, now); System(member.Name + " is AFK; a bot is playing their slot. Move to return."); }
                if (cs.Controlled(id) && afkCountedRound.GetValueOrDefault(id, -1) != cs.Round)
                {
                    afkCountedRound[id] = cs.Round; afkRounds[id] = afkRounds.GetValueOrDefault(id) + 1;
                    if (afkRounds[id] >= 3)
                    { cs.Spectate(id, now); member.Role = MemberRoles.Spectator; System(member.Name + " moved to spectators after three AFK rounds."); }
                }
            }
            else if (LobbyModes.TwoPlayers(m.Settings.Mode) && m.Settings.DuelAfkSeconds > 0 && now - activity[id] >= m.Settings.DuelAfkSeconds * 1000L)
            {
                m.WinnerId = m.Players.FirstOrDefault(p => p != id); m.Phase = MatchPhases.Final; m.Over = true;
                if (m.Live.TryGetValue(id, out var line)) line.Status = LineStates.Dnf;
                System(member.Name + " forfeited after inactivity."); MatchFinished?.Invoke(MatchView()!, Members); return;
            }
        }
    }
}

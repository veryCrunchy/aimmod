namespace AimMod.InGame.Multiplayer;

// Developer mode: simulated players have no game and no pose stream, so nothing would draw
// them. While this machine runs a simulated lobby's match, the first simulated player is
// shown as AimModSteam's test avatar (bridge peer 1, circling the local player): its stream
// maps back to that member, its drawn positions become the member's own track on the host
// (so hits on it validate like any player's), and avatar-state.tsv drives its team and deaths.
sealed partial class MultiplayerService
{
    // AimModSteam's synthetic test peer (Ghosts.cpp TestPeer).
    public const string StandInPeer = "1";
    string? standInMember; bool standInAuto; long standInTriedAt = long.MinValue / 2;

    // The simulated member the test avatar stands in for, while a match of ours runs.
    internal string? StandInMember => standInMember;

    void UpdateStandIn()
    {
        string? member = null;
        if (Simulation is not null && core is not null && Current is { Match: { Phase: MatchPhases.Loading or MatchPhases.Countdown or MatchPhases.Live } match } lobby && match.Players.Contains(SelfId))
            member = match.Players.FirstOrDefault(id => id != SelfId && lobby.Members.Any(m => m.Id == id && m.Simulated));
        standInMember = member;
        // Switch the test avatar on for the match (unless the developer menu already has it on), and off after.
        if (member is not null && !standInAuto && devAvatarState is not { On: true } && clock() - standInTriedAt >= 5000)
        {
            standInTriedAt = clock();
            var look = AvatarProfiles.Find(Current!.Members.First(m => m.Id == member).Avatar)?.ProfileName;
            // Walks between the arena's spawns (feet on the floor); circles you when the arena has none.
            var spawns = arenaSpawns.Select(p => new[] { p.X, p.Y, p.Z }).ToArray();
            if (spawns.Length > 0 ? transport.DevAvatar(true, "walk", look, spawns) : transport.DevAvatar(true, "circle", look)) standInAuto = true;
        }
        else if (member is null && standInAuto)
        {
            standInAuto = false;
            transport.DevAvatar(false, "circle", null);
        }
    }

    // The stand-in's own track on the host: where this machine's game drew its avatar
    // (capsule centre), at standing eye height, so claims against it validate.
    void FeedStandIn(TrackBatch batch)
    {
        if (core is null || standInMember is not { } member) return;
        var samples = batch.Seen.Where(v => v.Member == member)
            .Select(v => new TrackSample(v.T, v.X, v.Y, v.Z + TrackingRound.DefaultEyeAboveCentre, 0, 0)).ToArray();
        if (samples.Length > 0) core.Track(member, new TrackBatch(batch.MatchId, batch.Round, samples, []));
    }

    // avatar-state.tsv names bridge peers: the stand-in is peer 1.
    string AvatarPeer(string member) => member == standInMember ? StandInPeer : member;
}

using System.Globalization;

namespace AimMod.InGame.Multiplayer;

// round-state.tsv for every match (AimModCore applies it only in AimMod match scenarios, and
// locks KovaaK's restart while it is fresh for the scenario on screen):
//  - phase: frozen at the spawn until go-live (loading, countdown; CS keeps its own phases);
//  - spawn: the host's start and respawn positions, and a position restore after a restart;
//  - loadout: CS only.
// A restart that still gets through (KovaaK's run timer jumps back) never resets the match:
// combat scores are the host's anyway, a score run keeps the score it had, and the player is
// put back where they were.
sealed partial class MultiplayerService
{
    // Before go-live everyone is frozen at their spawn: movement and jump off, looking allowed.
    IEnumerable<string> PhaseLines(MatchSnapshot match)
    {
        if (match.Cs is not null) return CsPlayLines(match);
        var offset = HostOffset();
        if (match.Phase is MatchPhases.Loading or MatchPhases.Countdown)
            return ["phase\tfreeze\t1\t0\t" + Math.Max(0, (match.StartsAt ?? 0) - offset).ToString(CultureInfo.InvariantCulture)];
        if (match.Phase == MatchPhases.Live)
            return ["phase\tlive\t0\t0\t" + Math.Max(0, (match.EndsAt ?? 0) - offset).ToString(CultureInfo.InvariantCulture)];
        return [];
    }

    // The match scenario's exact name: AimModCore applies round state only there.
    string RoundScenario(MatchSnapshot match) =>
        plan is { } pl && pl.Key == PlanKey(match) ? pl.Scenario : Current is { } cur && MatchScenario.Needed(cur.Settings) ? MatchScenario.Name(cur.Settings) : match.Scenario;

    void WriteRoundState(MatchSnapshot match, CombatEvent? lastSpawn, IEnumerable<string> extra)
    {
        if (outputFolder is null) return;
        if (restoreSpawn is { } restore && (lastSpawn is null || restore.T >= lastSpawn.T)) lastSpawn = restore;
        var scenario = RoundScenario(match);
        var lines = extra.ToList();
        var now = clock();
        var round = PlayState.Round(0, scenario, lastSpawn, lines);
        // AimModCore drops a round state not rewritten for 5 s: rewrite on change and every second.
        if (round == lastRoundState && now - roundWrittenAt < 1000) return;
        lastRoundState = round; roundWrittenAt = now;
        try { AtomicFile.WriteText(Path.Combine(outputFolder, "round-state.tsv"), PlayState.Round(++roundSequence, scenario, lastSpawn, lines)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---- a restart that got through --------------------------------------
    // KovaaK's run timer for the match scenario jumping back while the match is live.
    public const double RestartJumpSeconds = 1.5;
    string? restartKey; double restartLastSeconds = -1; int restarts; string? restartedRound;
    CombatEvent? restoreSpawn; long restartNoticeUntil;
    readonly List<TrackSample> ownRecent = [];

    // This machine's own camera samples (host clock), kept 5 s for a position restore.
    void KeepOwnSamples(TrackBatch batch)
    {
        ownRecent.AddRange(batch.Samples);
        var cut = ownRecent.FindIndex(s => s.T >= (ownRecent.Count > 0 ? ownRecent[^1].T : 0) - 5000);
        if (cut > 0) ownRecent.RemoveRange(0, cut);
    }

    // True once per restart. Called every tick while this player plays a live match.
    bool DetectRestart(MatchSnapshot match)
    {
        var key = match.Id + "#" + match.Round;
        if (restartKey != key) { restartKey = key; restartLastSeconds = -1; restoreSpawn = null; ownRecent.Clear(); }
        if (match.Phase != MatchPhases.Live) { restartLastSeconds = -1; return false; }
        var live = liveRun();
        var scenario = RoundScenario(match);
        if (!live.Active || live.Seconds is not { } seconds || !(string.Equals(live.Scenario, scenario, StringComparison.OrdinalIgnoreCase) || string.Equals(live.Scenario, match.Scenario, StringComparison.OrdinalIgnoreCase))) return false;
        var jumped = restartLastSeconds >= 0 && seconds < restartLastSeconds - RestartJumpSeconds;
        restartLastSeconds = seconds;
        if (!jumped) return false;
        restarts++;
        restartedRound = key;
        restartNoticeUntil = clock() + 4000;
        // Back to where the player was just before the restart (capsule centre, facing the same way).
        var before = clock() + HostOffset() - (long)(seconds * 1000) - 250;
        if ((ownRecent.LastOrDefault(s => s.T <= before) ?? ownRecent.FirstOrDefault()) is { } at)
            restoreSpawn = new CombatEvent(3_000_000 + restarts, "respawn", at.T, SelfId, null, 0, false, 0, null,
                [Math.Round(at.X, 1), Math.Round(at.Y, 1), Math.Round(at.Z - TrackingRound.DefaultEyeAboveCentre, 1), Math.Round(at.Yaw, 1)]);
        return true;
    }

    // AimModCore's match-lock.tsv (AIMMOD_LOCK_1, presses, unix ms): a switched-off restart key was
    // pressed. The same notice says why nothing happened.
    long lockPresses = -1;
    void ReadLockPresses()
    {
        if (outputFolder is null) return;
        try
        {
            var file = new FileInfo(Path.Combine(outputFolder, "match-lock.tsv"));
            if (!file.Exists || file.Length > 256) return;
            var cells = File.ReadAllText(file.FullName).Trim().Split('\t');
            if (cells.Length != 3 || cells[0] != "AIMMOD_LOCK_1" || !long.TryParse(cells[1], NumberStyles.None, CultureInfo.InvariantCulture, out var presses)) return;
            var fresh = DateTime.UtcNow - file.LastWriteTimeUtc < TimeSpan.FromSeconds(5);
            if (lockPresses >= 0 && presses != lockPresses && fresh) restartNoticeUntil = clock() + 4000;
            lockPresses = presses;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    GameNotice? RestartNotice(long now) => now < restartNoticeUntil
        ? new GameNotice("restart-" + restarts, "info", "Restart is off during a match", "Your match carries on.", null, null, "click")
        : null;
}

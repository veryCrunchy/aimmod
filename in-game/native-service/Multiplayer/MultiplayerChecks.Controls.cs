namespace AimMod.InGame.Multiplayer;

static partial class MultiplayerChecks
{
    static void MatchControlChecks()
    {
        ControlPacketChecks();
        var heldKeys = new HashSet<int> { 0x70 };
        var reader = new CsKeyReader { ReadKey = heldKeys.Contains };
        Check(reader.PressedCode(0x70) && !reader.PressedCode(0x70) && !reader.Pressed('P'), "F1 is a function key with one edge, not the letter P");
        heldKeys.Clear(); reader.PressedCode(0x70); heldKeys.Add(0x71);
        Check(reader.PressedCode(0x71) && !reader.Pressed('Q'), "F2 is distinct from Q");
        heldKeys.Clear(); heldKeys.Add('W');
        Check(MatchControlKeys.ReturnHeld(GameBinds.Bound.Standard, heldKeys.Contains), "Default movement returns an AFK slot");
        var rebound = new GameBinds.Bound(new Dictionary<string, IReadOnlyList<string>> { ["MoveForward+"] = ["Up"], ["Fire"] = ["Ctrl+RightMouseButton"] });
        Check(!MatchControlKeys.ReturnHeld(rebound, heldKeys.Contains), "Rebound movement does not retain the old key");
        heldKeys.Clear(); heldKeys.Add(0x26);
        Check(MatchControlKeys.ReturnHeld(rebound, heldKeys.Contains), "Custom movement returns an AFK slot");
        heldKeys.Clear(); heldKeys.Add(0x02);
        Check(!MatchControlKeys.ReturnHeld(rebound, heldKeys.Contains), "Modified fire requires its modifier");
        heldKeys.Add(0x11);
        Check(MatchControlKeys.ReturnHeld(rebound, heldKeys.Contains), "Custom modified fire returns an AFK slot");

        var vote = new MatchVote("surrender", "Surrender", 1, null, ["a", "b", "c", "d", "e"], 1000);
        Check(vote.Required == 4 && !vote.Cast("outsider", true), "Surrender requires all but one of five humans and excludes outsiders");
        Check(vote.Cast("a", true) && !vote.Cast("a", false), "One ballot per human");
        vote.Cast("b", true); vote.Cast("c", true);
        Check(!vote.Passed, "Three of five cannot surrender");
        vote.Cast("d", true);
        Check(vote.Passed && vote.Finished(1001), "Four of five surrender immediately");
        var small = new MatchVote("surrender", "Surrender", 1, null, ["a", "b", "c"], 0);
        small.Cast("a", true); small.Cast("b", true);
        Check(!small.Passed && small.Required == 3, "Three human teammates must all agree to surrender");
        small.Cast("c", false);
        Check(small.Finished(1) && !small.Passed, "An impossible surrender fails early");
        var kick = new MatchVote("kick", "Kick player", 2, "c", ["a", "b", "c"], 0);
        kick.Cast("a", true);
        Check(!kick.Passed && kick.Required == 2, "Kick needs a strict majority of humans including the target");
        kick.Cast("b", true);
        Check(kick.Passed, "A human majority passes a kick vote");
        Check(new MatchVote("kick", "Kick player", 2, "c", ["a", "b", "c"], 0).Finished(20_000), "Unanswered votes expire after twenty seconds");

        const long start = 60_000_000;
        var cs = new CsMatch(["a", "b"], start, 6, false, null);
        cs.BeginWarmup(start);
        Check(cs.View().Phase == "warmup" && cs.Round == 0 && cs.View().Bomb.State == "none", "Warmup has no numbered round or bomb");
        Check(cs.Buy("a", "awp", start + 10) is null && cs.View().Players[0].Money == CsRules.MaxMoney, "Warmup purchases are free");
        cs.Combat.Track("a", new TrackBatch("m", 1, [new TrackSample(start + 20, 0, 0, 164, 0, 0)], []));
        cs.Combat.Track("b", new TrackBatch("m", 1, [new TrackSample(start + 20, 100, 0, 164, 0, 0)], []));
        cs.Combat.BotHit("a", "b", true, start + 30, CsRules.PrimarySlot, null);
        cs.Tick(start + 3000);
        Check(cs.Combat.Alive("b") && cs.Combat.View().Players.All(p => p.Frags == 0 && p.Deaths == 0) && cs.View().Players.All(p => p.Kills == 0 && p.Deaths == 0 && p.Adr == 0 && p.Mvps == 0), "Warmup deaths respawn without CS statistics");
        cs.ReadyWarmup("a", true); cs.Tick(start + 3100);
        Check(cs.Phase == "warmup", "Warmup waits for every human");
        cs.ReadyWarmup("b", true); cs.Tick(start + 3200);
        Check(cs.Phase == "freeze" && cs.Round == 1 && cs.View().Players.All(p => p.Money == CsRules.StartMoney && p.Primary is null), "All ready starts the round-one countdown with fresh economy");
        var timed = new CsMatch(["a", "b"], start, 6, false, null);
        timed.BeginWarmup(start); timed.Tick(start + 60_000);
        Check(timed.Phase == "freeze" && timed.Round == 1, "Warmup expires after sixty seconds");

        var freezeEnd = cs.PhaseEndsAt;
        cs.SetPaused(true, start + 4000); cs.Tick(start + 14_000);
        Check(cs.Paused && cs.PhaseEndsAt == freezeEnd + 10_000 && cs.Phase == "freeze", "Host pause preserves remaining freeze time");
        cs.SetPaused(false, start + 15_000);
        Check(!cs.Paused && cs.PhaseEndsAt == freezeEnd + 11_000, "Unpause accounts for time since the last tick");
        var live = cs.PhaseEndsAt; cs.Tick(live);
        Check(cs.QueueTimeout(1, live) is null && cs.QueueTimeout(1, live) == "timeout-used", "One tactical timeout per team per half");
        Check(cs.TimeoutUntil is null && cs.Phase == "live", "Timeout does not interrupt the live round");
        cs.Combat.Kill("a", live + 1); cs.Tick(live + 2); cs.Tick(live + 2 + CsRules.RoundEndMs);
        Check(cs.Phase == "freeze" && cs.TimeoutUntil == live + 2 + CsRules.RoundEndMs + 30_000, "Tactical timeout starts at the next freeze");
        Check(cs.PhaseEndsAt - cs.TimeoutUntil == CsRules.FreezeMs, "Normal freeze remains after the thirty-second timeout");

        var bombPause = new CsMatch(["a", "b"], start, 6, false, null);
        var plantAt = start + CsRules.FreezeMs;
        bombPause.Tick(plantAt);
        bombPause.Combat.Track("a", new TrackBatch("m", 1, [new TrackSample(plantAt, 0, 0, 164, 0, 0)], []));
        Check(bombPause.Use("a", true, plantAt) is null, "Pause fixture starts planting");
        bombPause.Tick(plantAt + CsRules.PlantMs);
        var explosion = bombPause.View().Bomb.ExplodesAt;
        bombPause.Grenades.Throw("a", GrenadeRules.He, [0, 0, 164], [200, 0, 100], plantAt + CsRules.PlantMs, null);
        var thrownAt = bombPause.Grenades.View()[0].At;
        bombPause.SetPaused(true, plantAt + CsRules.PlantMs);
        bombPause.Tick(plantAt + CsRules.PlantMs + 10_000);
        Check(bombPause.View().Bomb.ExplodesAt == explosion + 10_000 && bombPause.Grenades.View()[0].At == thrownAt + 10_000,
            "A live host pause preserves both the bomb fuse and an in-flight grenade");

        var halves = new CsMatch(["a", "b"], start, 6, false, null);
        halves.QueueTimeout(1, start);
        for (var round = 0; round < 6; round++)
        {
            var at = halves.PhaseEndsAt; halves.Tick(at);
            halves.Combat.Kill("a", at + 1); halves.Tick(at + 2);
            halves.Tick(at + 2 + CsRules.RoundEndMs);
        }
        Check(halves.Round == 7 && halves.QueueTimeout(1, halves.PhaseEndsAt - 1) is null, "Tactical timeout allowance renews at halftime");

        cs.Combat.Track("a", new TrackBatch("m", 1, [new TrackSample(live + 9000, 300, 400, 164, 0, 90)], []));
        cs.Control("a", true, live + 9000);
        Check(cs.Controlled("a") && cs.View().Players[0].Afk, "AFK control is visible in the match snapshot");
        cs.Combat.Track("a", new TrackBatch("m", 1, [new TrackSample(live + 9100, 500, 600, 164, 0, 90)], []));
        var serial = cs.View().Players[0].SpawnSerial;
        cs.Control("a", false, live + 9200);
        Check(!cs.Controlled("a") && cs.View().Spawns!["a"][0] == 500 && cs.View().Players[0].SpawnSerial > serial, "Handback teleports to the bot's current position");
        cs.Spectate("a", live + 9300);
        Check(cs.View().Players[0].Spectating && !cs.Combat.Alive("a"), "Repeated AFK can retire a slot to spectator");
        cs.Surrender(1, live + 9400);
        Check(cs.Over && cs.WinnerTeam == 2, "Surrender finishes the match for the opposing team");

        var awp = timed.View().Players[0] with { Primary = "awp", Side = CsRules.CT, Team = 2 };
        var retake = timed.View() with { Phase = "planted", PhaseEndsAt = start + 4000, Players = [awp, awp with { Member = "enemy", Team = 1, Side = CsRules.T }], Bomb = timed.View().Bomb with { State = "planted", Carrier = null, ExplodesAt = start + 4000 } };
        Check(BotStrategy.SaveAwp(retake, awp, start), "A bot saves its AWP when no defuse is possible");
        Check(!BotStrategy.SaveAwp(retake, awp with { Primary = "m4a1s" }, start), "AWP save rule does not apply to another rifle");
        Check(!BotStrategy.SaveAwp(retake with { PhaseEndsAt = start + 40_000, Bomb = retake.Bomb with { ExplodesAt = start + 40_000 } }, awp, start), "A winnable retake keeps the AWP bot in play");
        ControlAuthorityChecks();
    }

    static void ControlAuthorityChecks()
    {
        var content = new FakeContent();
        (LobbyCore Core, Func<long> Clock, Action<long> Advance) Setup(bool warmup = false)
        {
            var (core, clock, advance) = Lobby();
            core.Apply("host", "settings", Patch(new { mode = "cs", csWarmup = warmup, maxPlayers = 6, countdown = 3 }), content);
            foreach (var id in new[] { "a", "b", "c", "d", "e" }) core.Join(id, id);
            foreach (var id in new[] { "host", "a", "b" }) core.Apply("host", "team", J(new { member = id, team = 1 }), content);
            foreach (var id in new[] { "c", "d", "e" }) core.Apply("host", "team", J(new { member = id, team = 2 }), content);
            ReadyAll(core);
            Check(core.Apply("host", "start", default, content).Ok, "Control fixture starts a six-player CS match");
            advance(3000); core.Tick(); return (core, clock, advance);
        }
        var (core, clock, advance) = Setup();
        LobbyResult Control(string id, string command, string? target = null) => core.Apply(id, "match-control", J(new { command, member = target }), content);
        Check(Control("a", "pause").Code == "not-host", "Only the host may pause");
        Check(Control("host", "pause").Ok && core.Snapshot().Match!.Cs!.Paused, "Host pause reaches the snapshot");
        var frozen = core.Snapshot().Match!.Cs!.PhaseEndsAt;
        advance(30_000); core.Tick();
        Check(core.Snapshot().Match!.Cs!.PhaseEndsAt == frozen + 30_000, "Host authority keeps freeze time unchanged while paused");
        Control("host", "unpause");
        Check(Control("a", "kickvote", "host").Code == "target", "Host cannot be vote-kicked");
        Check(Control("a", "kickvote", "c").Code == "target", "Enemy cannot be vote-kicked");
        Check(Control("host", "kickvote", "b").Ok && core.Snapshot().Match!.Vote?.Required == 2, "A teammate kick vote uses the human majority");
        Check(!Control("c", "yes").Ok, "Opponents cannot cast team ballots");
        Check(Control("a", "yes").Ok && core.Members.All(p => p.Id != "b"), "Passed kick removes the teammate");
        Check(core.Snapshot().Chat.Any(c => c.Text.Contains("F1 yes / F2 no")) && core.Snapshot().Chat.Any(c => c.Text.Contains("vote passed")), "Votes and results appear in chat");

        var warm = Setup(true);
        Check(warm.Core.Snapshot().Match!.Cs!.Phase == "warmup", "Lobby warmup setting starts warmup before round one");
        foreach (var id in new[] { "host", "a", "b", "c", "d", "e" }) warm.Core.Apply(id, "match-control", J(new { command = "ready" }), content);
        warm.Core.Tick();
        Check(warm.Core.Snapshot().Match!.Cs is { Phase: "freeze", Round: 1 }, "Every human ready ends lobby warmup");

        var idle = Setup(); var m = idle.Core.Snapshot().Match!;
        void Track(string id, double x, bool automated = false) => idle.Core.Track(id, new TrackBatch(m.Id, m.Round, [new TrackSample(idle.Clock(), x, 0, 164, 0, 0)], []), automated);
        idle.Advance(CsRules.FreezeMs); idle.Core.Tick(); Track("host", 0);
        idle.Advance(59_999);
        idle.Core.Track("host", new TrackBatch(m.Id, m.Round, [new TrackSample(idle.Clock() + 30_000, 1000, 0, 164, 0, 0)], []));
        Check(idle.Core.CombatPositions()["host"].At.X == 0, "A future human pose cannot move the slot or reset inactivity");
        idle.Core.Tick();
        Check(!idle.Core.Snapshot().Match!.Cs!.Players[0].Afk, "AFK does not start before sixty live seconds");
        idle.Advance(1); idle.Core.Tick();
        Check(idle.Core.Snapshot().Match!.Cs!.Players[0].Afk, "Sixty live seconds without input starts bot takeover");
        Track("host", 500, true); Track("host", 0);
        Check(idle.Core.Snapshot().Match!.Cs!.Players[0].Afk && idle.Core.CombatPositions()["host"].At.X == 500, "Bot movement does not hand back and static human tracks cannot move its slot");
        Track("host", 20);
        Check(idle.Core.Snapshot().Match!.Cs!.Players[0].Afk, "The following camera cannot reclaim a controlled slot");
        idle.Core.Apply("host", "match-control", J(new { command = "return" }), content); Track("host", 20);
        Check(!idle.Core.Snapshot().Match!.Cs!.Players[0].Afk && idle.Core.CombatPositions()["host"].At.X == 500, "Movement input hands back without accepting the stale origin");
        Track("host", 500);
        Check(idle.Core.CombatPositions()["host"].At.X == 500, "The returned player acknowledges the bot position");
        Check(!idle.Core.BotShot("host", "c", false, 1, null).Ok, "A returned human no longer accepts bot fire");

        var repeated = Setup(); repeated.Advance(CsRules.FreezeMs); repeated.Core.Tick();
        repeated.Advance(60_000); repeated.Core.Tick();
        for (var round = 0; round < 2; round++)
        {
            repeated.Advance(repeated.Core.Snapshot().Match!.Cs!.PhaseEndsAt - repeated.Clock() + 1); repeated.Core.Tick();
            repeated.Advance(CsRules.RoundEndMs); repeated.Core.Tick();
            repeated.Advance(CsRules.FreezeMs); repeated.Core.Tick();
            repeated.Advance(1); repeated.Core.Tick();
        }
        Check(repeated.Core.Members.First(p => p.Id == "host").Role == MemberRoles.Spectator, "Three distinct AFK rounds move the human to spectators");
        Check(!repeated.Core.Apply("host", "match-control", J(new { command = "return" }), content).Ok, "A retired spectator cannot reclaim a playing slot");
        Check(new LobbySettings().CsWarmup && LobbyRules.Apply(new LobbySettings(), J(new { duelAfkSeconds = 20 }), 2, content).Settings is null, "Warmup defaults on and duel AFK grace is bounded");

        var duel = Lobby(new LobbySettings(Mode: LobbyModes.Duel, Scenario: content.Scenario("Synthetic A"), Countdown: 3, DuelAfkSeconds: 30));
        duel.Core.Join("other", "Other"); ReadyAll(duel.Core); duel.Core.Apply("host", "start", default, content);
        duel.Advance(3000); duel.Core.Tick(); duel.Core.Tick();
        var dm = duel.Core.Snapshot().Match!;
        duel.Core.Track("host", new TrackBatch(dm.Id, dm.Round, [new TrackSample(duel.Clock(), 0, 0, 164, 0, 0)], []));
        duel.Advance(29_000);
        duel.Core.Track("host", new TrackBatch(dm.Id, dm.Round, [new TrackSample(duel.Clock(), 20, 0, 164, 0, 0)], []));
        duel.Core.Tick(); duel.Advance(1000); duel.Core.Tick();
        Check(duel.Core.Snapshot().Match is { Phase: MatchPhases.Final, WinnerId: "host" }, "Duel movement resets activity and the inactive opponent forfeits after the configured grace");
    }
}

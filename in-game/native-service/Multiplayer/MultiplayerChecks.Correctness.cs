using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

static partial class MultiplayerChecks
{
    static void AnswerShotTraces(string folder, long now)
    {
        var request = Path.Combine(folder, MultiplayerService.GrenadeSimFile);
        if (!File.Exists(request)) return;
        var rows = File.ReadAllLines(request).Where(l => l.StartsWith("los\t", StringComparison.Ordinal)).Select(l => "los\t" + l.Split('\t')[1] + "\t1");
        File.WriteAllText(Path.Combine(folder, MultiplayerService.GrenadePathsFile), "AIMMOD_GRENADEPATHS_1\t" + now + "\n" + string.Join('\n', rows) + "\n");
    }
    static void CorrectnessChecks()
    {
        CsDeadlineChecks();
        Check(!PacketJson.Int64(J("wrong"), out _) && !PacketJson.Int32(J(false), out _), "Typed numeric readers reject wrong kinds");
        foreach (var spec in Protocol.Types)
        {
            var field = spec.Type switch
            {
                "hello" => "proto", "welcome" or "snapshot" => "snapshot", "chat" => "lines",
                "command" or "result" or "state-part" => "id", "reject" or "content.error" => "code",
                "ping" or "pong" => "t0", "bye" => "reason", "content.manifest" => "files",
                "content.get" or "content.chunk" => "offset", "content.done" => "transfer", "cosmetic.look" => "items", _ => "match"
            };
            var bad = Protocol.Create(spec.Type, "room", "peer", 1, 1000, spec.Type == "content.request" ? (object)"wrong" : new Dictionary<string, object> { [field] = false });
            var refused = false;
            try { PacketJson.Validate(bad); } catch (JsonException) { refused = true; }
            Check(refused, spec.Type + " rejects a body of the wrong kind");
        }
        foreach (var action in new[] { "chat", "throw" })
        {
            var bad = Protocol.Create("command", "room", "peer", 1, 1000, new { id = 1, action, args = new { text = 7, kind = false } });
            var refused = false;
            try { PacketJson.Validate(bad); } catch (JsonException) { refused = true; }
            Check(refused, action + " rejects incorrect field kinds");
        }
        var combat = new CombatMatch(LobbyModes.Deathmatch, ["a", "b"], 100, 0, 1000, 100000);
        combat.Track("a", new TrackBatch("m", 1, [new TrackSample(50000, 10, 0, 100, 0, 0)], []), 2000);
        Check(combat.Position("a") is null, "Future pose cannot poison history");
        combat.Track("a", new TrackBatch("m", 1, [new TrackSample(2000, 20, 0, 100, 0, 0)], []), 2000);
        Check(combat.Position("a")?.X == 20, "Valid pose follows rejected future pose");
        for (var i = 0; i < 600; i++) { combat.Revive("a", 3000 + i, 100); combat.Kill("a", 3000 + i); }
        Check(combat.EventsSince(0).Count == 512 && combat.LatestEvent == 600 && combat.EventsSince(599).Single().Id == 600, "Combat event retention is bounded with sequence lookup");
        var net = new MemoryNetwork();
        var transport = new MemoryTransport(net, "host"); net.Peers["host"] = transport;
        var peer = new MemoryTransport(net, "peer"); net.Peers["peer"] = peer;
        using (var service = new MultiplayerService(transport, new ContentLibrary(null), new NoGameControl(), () => new LocalRun(false, null, null, null, null, 0, 0, 0, null), () => [], () => null, null, false, () => 100000, autoTick: false))
        {
            void Enqueue(object body) => transport.Inbox.Enqueue(new TransportEvent("peer", TransportEvent.Message, Protocol.Encode(Protocol.Create("ping", "room", "peer", 1, 100000, body))));
            Enqueue(new { t0 = "wrong" }); Enqueue(new { t0 = 100000 }); service.Tick();
            Check(peer.Inbox.Any(e => Protocol.Decode(e.Frame!)?.T == "pong"), "Malformed event does not abort the following event in the tick");
            for (var i = 0; i < 7; i++) Enqueue(new { t0 = false });
            service.Tick();
            Check(transport.HostActions.Contains("kick peer"), "Repeated malformed packets kick the peer");
        }
        var (core, clock, _) = Lobby();
        var snapshot = core.Snapshot();
        var chat = Enumerable.Range(1, 60).Select(i => new ChatLine(i, "peer", "Player", new string('\u4e00', 500), clock(), false)).ToArray();
        var cs = GrenadeMatch(1000, ("host", 1, 0, 0, 0), ("peer", 2, 0, 0, 180));
        var scoreView = cs.View() with { Score = [7, 5] };
        var standings = new[] { new Standing("peer", "Player", 1, 7, 7, 7, 7, 12) };
        var match = new MatchSnapshot("m", MatchPhases.Live, LobbyModes.Cs, "", 60, 1, null, null, 1000, 61000, null, ["host", "peer"], [], [], standings, null, [], Cs: scoreView);
        core.Join("peer", "Player");
        var migration = core.Snapshot() with { Settings = snapshot.Settings with { Mode = LobbyModes.Cs }, Match = match };
        var restored = LobbyCore.Restore(migration, "peer", clock).Snapshot();
        Check(restored.Match is { Phase: MatchPhases.Final } && restored.Match.Standings.SequenceEqual(standings) && restored.Match.Cs!.Score.SequenceEqual([7, 5]), "CS migration preserves published standings and ends the match");
        Check(restored.Chat.Any(l => l.Text == "The host left. The match ended at 7–5."), "CS migration explains the final score");
        var large = migration with { Match = null, Suggestions = Enumerable.Range(0, 100).Select(i => new Suggestion(new string('界', 100), "peer", ["peer"])).ToArray() };
        using (var sender = new MultiplayerService(transport, new ContentLibrary(null), new NoGameControl(), () => new LocalRun(false, null, null, null, null, 0, 0, 0, null), () => [], () => null, null, false, clock, autoTick: false))
        {
            peer.Inbox.Clear();
            var send = typeof(MultiplayerService).GetMethod("SendSnapshot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Check((bool)send.Invoke(sender, ["peer", "welcome", large])!, "Large welcome serializes successfully");
            var frames = peer.Drain();
            Check(frames.Count > 1 && frames.All(e => e.Frame!.Length < 10000), "Every large welcome fragment stays well under the wire limit");
            using var receiver = new MultiplayerService(peer, new ContentLibrary(null), new NoGameControl(), () => new LocalRun(false, null, null, null, null, 0, 0, 0, null), () => [], () => null, null, false, clock, autoTick: false);
            typeof(MultiplayerService).GetField("hostPeer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(receiver, "host");
            var handle = typeof(MultiplayerService).GetMethod("HandleAsClient", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            foreach (var frame in frames) handle.Invoke(receiver, ["host", Protocol.Decode(frame.Frame!)!]);
            Check(J(receiver.View()).GetProperty("lobby").GetProperty("suggestions").GetArrayLength() == 100, "Welcome fragments reconstruct the full state");
        }
        var sync = new ClockSync(); sync.Add(1000, 6100, 1200);
        var rebased = SnapshotClock.Rebase(migration with { Now = 6200, ReadyCheck = 6000, AutoStartAt = 7000 }, sync.Offset);
        Check(rebased.Now == 1200 && rebased.ReadyCheck == 1000 && rebased.AutoStartAt == 2000 && rebased.Match!.StartsAt == -4000 && rebased.Match.EndsAt == 56000, "Migration rebases timestamps with measured clock offset");
        snapshot = snapshot with { Chat = chat };
        Check(Protocol.Encode(Protocol.Create("welcome", "room", "peer", 1, clock(), new { member = "peer", snapshot = snapshot with { Chat = Array.Empty<ChatLine>() } })).Length < 8192, "Welcome excludes worst-case chat history");
        Check(chat.All(line => Protocol.Encode(Protocol.Create("chat", "room", "peer", 1, clock(), new { lines = new[] { line } })).Length < 4096), "Unicode chat frames stay within budget");
    }

    static void CsDeadlineChecks()
    {
        const long start = 100000;
        CsMatch Ready()
        {
            var c = GrenadeMatch(start, ("t", 1, 0, 0, 0), ("ct", 2, 0, 0, 180));
            c.Tick(start + CsRules.FreezeMs);
            return c;
        }
        foreach (var delta in new[] { -1L, 0L, 1L })
        {
            var c = Ready(); var at = start + CsRules.FreezeMs + 100;
            Check(c.Use("t", true, at) is null, "Synthetic planter starts");
            c.Tick(at + CsRules.PlantMs + 50);
            var explosion = c.View().Bomb.ExplodesAt!.Value;
            Check(explosion == at + CsRules.PlantMs + CsRules.BombMs, "Bomb deadline uses scheduled plant completion");
            Check(c.Use("ct", true, explosion - CsRules.DefuseMs + delta) is null, "Synthetic defuser starts");
            c.Tick(explosion + 100);
            Check(c.View().LastReason == (delta < 0 ? "defuse" : "bomb"), "Defuse and explosion resolve by deadline, including ties");
        }
        var late = Ready(); var deadline = late.PhaseEndsAt;
        late.Use("t", true, deadline - CsRules.PlantMs + 1);
        late.Tick(deadline + 100);
        Check(late.View().LastReason == "time", "Late plant cannot defeat round timeout");
        var before = Ready(); var begun = start + CsRules.FreezeMs + 100;
        before.Use("t", true, begun); before.Tick(begun + CsRules.PlantMs + 50);
        before.Combat.AreaHit("ct", "t", 10000, before.Combat.Weapon, begun + CsRules.PlantMs - 1, null);
        Check(before.View().Bomb.State != "planted", "Delayed kill before completion cancels the published plant");
        var kill = Ready(); var plant = start + CsRules.FreezeMs + 100;
        kill.Use("t", true, plant);
        kill.Combat.BeforeDamage!(plant + CsRules.PlantMs + 1);
        kill.Combat.Kill("t", plant + CsRules.PlantMs + 1);
        Check(kill.View().Bomb.State == "planted", "Scheduled plant completes before a later kill");
    }
}

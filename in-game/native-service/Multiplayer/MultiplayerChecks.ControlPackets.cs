using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

static partial class MultiplayerChecks
{
    static void ControlPacketChecks()
    {
        foreach (var command in new[] { "surrender", "timeout", "pause", "unpause", "kickvote", "yes", "no", "ready", "return" })
        {
            PacketJson.Validate(Protocol.Create("command", "room", "peer", 1, 1000, new { id = 1, action = "match-control", args = new { command, member = "teammate" } }));
            Check(true, command + " passes typed control validation");
        }
        foreach (var args in new object[] { new { command = true }, new { command = 7 }, new { command = new[] { "ready" } }, new { }, new { command = "unknown" }, new { command = new string('x', 257) }, new { command = "kickvote" }, new { command = "kickvote", member = 3 } })
        {
            var refused = false;
            try { PacketJson.Validate(Protocol.Create("command", "room", "peer", 1, 1000, new { id = 1, action = "match-control", args })); }
            catch (JsonException) { refused = true; }
            Check(refused, "Malformed match controls are rejected before dispatch");
        }
        foreach (var settings in new object[] { new { csWarmup = "true" }, new { csWarmup = 1 }, new { duelAfkSeconds = false } })
        {
            var refused = false;
            try { PacketJson.Validate(Protocol.Create("command", "room", "peer", 1, 1000, new { id = 1, action = "settings", args = new { settings } })); }
            catch (JsonException) { refused = true; }
            Check(refused, "Warmup and inactivity settings receive typed validation");
        }

        var content = new FakeContent();
        var (core, clock, advance) = Lobby();
        core.Apply("host", "settings", Patch(new { mode = "cs", csWarmup = true, maxPlayers = 6, countdown = 3 }), content);
        foreach (var id in new[] { "peer", "b", "c", "d", "e" }) core.Join(id, id);
        ReadyAll(core); core.Apply("host", "start", default, content); advance(3000); core.Tick();
        var net = new MemoryNetwork();
        var host = new MemoryTransport(net, "host"); net.Peers["host"] = host;
        var peer = new MemoryTransport(net, "peer"); net.Peers["peer"] = peer;
        using var service = new MultiplayerService(host, new ContentLibrary(null), new NoGameControl(), () => new LocalRun(false, null, null, null, null, 0, 0, 0, null), () => [], () => null, null, false, clock, autoTick: false);
        typeof(MultiplayerService).GetField("core", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(service, core);
        long seq = 0;
        void Send(string action, object args) => host.Inbox.Enqueue(new TransportEvent("peer", TransportEvent.Message,
            Protocol.Encode(Protocol.Create("command", core.Id, "peer", ++seq, clock(), new { id = seq, action, args }))));
        JsonElement[] Results() => peer.Drain().Select(e => e.Frame is null ? null : Protocol.Decode(e.Frame)).Where(m => m?.T == "result").Select(m => m!.Body).ToArray();
        Send("match-control", new { command = true });
        Send("match-control", new { command = "ready" }); service.Tick();
        Check(Results().Single().GetProperty("ok").GetBoolean() && core.Snapshot().Match!.Cs!.Ready!.Contains("peer"), "Malformed control does not abort the following warmup ready packet");
        for (var i = 0; i < 5; i++) Send("match-control", new { command = "ready" });
        service.Tick();
        var limited = Results();
        Check(limited.Count(r => r.GetProperty("ok").GetBoolean()) == 4 && limited[^1].GetProperty("code").GetString() == "slow", "Peer controls share a five-per-five-second rate limit");
        advance(5000);
        Send("chat", new { text = "/ready" });
        for (var i = 0; i < 4; i++) Send("match-control", new { command = "ready" });
        Send("chat", new { text = "/ready" }); service.Tick();
        limited = Results();
        Check(limited.Length == 6 && limited[0].GetProperty("ok").GetBoolean() && limited[^1].GetProperty("code").GetString() == "slow", "Chat and button controls share the same budget and recover after the window");
        advance(5000);
        Send("match-control", new { command = "pause" }); service.Tick();
        Check(Results().Single().GetProperty("code").GetString() == "not-host" && !core.Snapshot().Match!.Cs!.Paused, "A well-typed peer pause still requires host authority");
        for (var i = 0; i < 7; i++) Send("match-control", new { command = false });
        service.Tick();
        Check(host.HostActions.Contains("kick peer") && core.Members.All(m => m.Id != "peer"), "Malformed controls use the packet strike and removal path");

        const long start = 100000;
        var cs = GrenadeMatch(start, ("t", 1, 0, 0, 0), ("ct", 2, 0, 0, 180));
        cs.Tick(start + CsRules.FreezeMs);
        var deadline = cs.PhaseEndsAt;
        var plant = start + CsRules.FreezeMs + 100;
        cs.Use("t", true, plant); cs.Tick(plant + CsRules.PlantMs + 50);
        cs.SetPaused(true, plant + CsRules.PlantMs + 100);
        cs.SetPaused(false, plant + CsRules.PlantMs + 10100);
        cs.Combat.AreaHit("ct", "t", 10000, cs.Combat.Weapon, plant + CsRules.PlantMs + 9999, null);
        Check(cs.Phase == "live" && cs.PhaseEndsAt == deadline + 10000 && cs.View().Bomb.State != "planted", "Pause shifts plant rollback ordering and the original round deadline together");
    }
}

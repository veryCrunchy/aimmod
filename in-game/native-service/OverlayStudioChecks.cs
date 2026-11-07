using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AimMod.InGame;

static class OverlayStudioChecks
{
    public static async Task Run()
    {
        var count = 0;
        void Check(bool ok, string name) { count++; if (!ok) throw new Exception("Overlay studio check failed: " + name); }
        var empty = OverlayDataCatalog.Read(new { enabled = true });
        foreach (var path in new[] { "multiplayer.lobby.players.0.ready", "multiplayer.cs.players.0.money", "multiplayer.cs.players.0.grenades", "multiplayer.cs.bomb.explodesAt",
            "multiplayer.killFeed.0.weapon", "multiplayer.killFeed.0.head", "replay.stats.score", "replay.speed", "mouse.speed", "mouse.trail.0.yaw", "kovaaks.cm360" })
            Check(empty.Any(f => f.Path == path && f.Type != "unknown"), "typed empty path " + path);
        Check(empty.Single(f => f.Path == "mouse.speed").Unit == "deg/s", "angular speed unit");
        var analysis = new OverlayAnalysisSource(); analysis.Publish(new { flick = new { speed = 14.2 }, tracking = new { error = 0.5 } });
        Check(JsonSerializer.Serialize(analysis.Read()).Contains("14.2"), "analysis extension snapshot");
        Check(OverlayDataCatalog.Read(new { enabled = true }).Any(f => f.Path == "mouse.analysis.flick.speed" && f.Type == "number"), "analysis producer registers stable types");
        var rows = OverlayDataCatalog.Read(new { enabled = true, multiplayer = new { cs = new { players = new[] { new { site = (string?)"A" }, new { site = (string?)null } } } } });
        Check(rows.Single(f => f.Path == "multiplayer.cs.players.1.site").Type == "string", "nullable later rows keep registered type");
        var folder = Path.Combine(Path.GetTempPath(), "overlay-studio-check-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var motion = new OverlayMotion(folder, () => 1000);
            motion.Merge([new LivePose(900, [0, 0, 0, 0, 359, 0]), new LivePose(1000, [0, 0, 0, 0, 1, 0])]);
            Check(Math.Abs(motion.CatalogView(null).Speed!.Value - 20) < 0.001, "mouse speed unwraps yaw");
            var scene = OverlayScenes.CleanScene(JsonNode.Parse("""{"id":"portrait","width":1080,"height":1920,"widgets":[{"id":"a","type":"stat","y":1700}]}"""))!;
            Check((double)scene["height"]! == 1920 && (double)scene["widgets"]![0]!["y"]! == 1700, "portrait scene persists positions");
            var c = new ObsSwitchConfig(true, MatchStart: "Match", MatchEnd: "Desk", RunStart: "Practice", RunEnd: "Desk", Replay: "Replay", ReplayEnd: "Desk");
            var idle = new ObsSwitchState(false, false, false);
            Check(ObsSceneSwitching.Transition(idle, idle, c) is null, "no repeated switch");
            Check(ObsSceneSwitching.Transition(idle, new(true, true, true), c) == "Replay", "replay priority");
            Check(ObsSceneSwitching.Transition(new(true, false, false), idle, c) == "Desk", "match end mapping");
            Check(ObsSceneSwitching.Transition(new(false, false, true), new(false, true, true), c) is null, "run changes do not interrupt replay");
            Check(ObsSceneSwitching.Transition(idle, new(false, true, false), c with { Enabled = false }) is null, "disabled never switches");
            await using (var settings = new ObsSceneSwitching(folder, () => idle))
            {
                settings.Save(c with { Password = "synthetic-password" });
                var view = JsonSerializer.Serialize(settings.View());
                Check(!view.Contains("synthetic-password") && !view.Contains("\"password\""), "password excluded from responses");
                settings.Save(c); Check(File.ReadAllText(Path.Combine(folder, "obs-scene-switching.json")).Contains("synthetic-password"), "omitted password retains saved secret");
                settings.Save(c with { Password = "" }); Check(!File.ReadAllText(Path.Combine(folder, "obs-scene-switching.json")).Contains("synthetic-password"), "explicit password removal");
            }
            await FakeObs(Check, folder);
        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine($"PASS {count} overlay studio checks");
    }
    static async Task FakeObs(Action<bool, string> check, string folder)
    {
        await using var app = LoopbackServer.Build(0); app.UseWebSockets();
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connections = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        app.Map("/", async context => {
            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            if (Interlocked.Increment(ref connections) == 1) { await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Unavailable", timeout.Token); return; }
            async Task Send(object data) { var bytes = JsonSerializer.SerializeToUtf8Bytes(data); var half = bytes.Length / 2; await ws.SendAsync(bytes.AsMemory(0, half), WebSocketMessageType.Text, false, timeout.Token); await ws.SendAsync(bytes.AsMemory(half), WebSocketMessageType.Text, true, timeout.Token); }
            async Task<JsonElement> Read() { var bytes = new byte[8192]; var r = await ws.ReceiveAsync(new ArraySegment<byte>(bytes), timeout.Token); using var doc = JsonDocument.Parse(bytes.AsMemory(0, r.Count)); return doc.RootElement.Clone(); }
            await Send(new { op = 0, d = new { rpcVersion = 1, authentication = new { salt = "sample-salt", challenge = "sample-challenge" } } });
            var auth = await Read();
            check(auth.GetProperty("op").GetInt32() == 1 && auth.GetProperty("d").GetProperty("eventSubscriptions").GetInt32() == 0, "v5 identify with no event subscription");
            check(auth.GetProperty("d").GetProperty("authentication").GetString() == ObsSceneSwitching.Authentication("synthetic-password", "sample-salt", "sample-challenge"), "v5 challenge authentication");
            await Send(new { op = 2, d = new { negotiatedRpcVersion = 1 } });
            var request = await Read();
            await Send(new { op = 7, d = new { requestId = request.GetProperty("d").GetProperty("requestId").GetString(), requestStatus = new { result = true, code = 100 } } });
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", timeout.Token);
            received.TrySetResult(request);
        });
        await app.StartAsync(timeout.Token);
        var port = new Uri(LoopbackServer.VerifiedAddress(app)).Port;
        await using (var switching = new ObsSceneSwitching(folder, () => new(false, true, false)))
        {
            switching.Save(new(true, port, "synthetic-password", RunStart: "Practice")); switching.Start();
            var result = await received.Task.WaitAsync(timeout.Token);
            check(connections == 2, "local connection retries after refusal");
            check(result.GetProperty("op").GetInt32() == 6 && result.GetProperty("d").GetProperty("requestType").GetString() == "SetCurrentProgramScene" && result.GetProperty("d").GetProperty("requestData").GetProperty("sceneName").GetString() == "Practice", "v5 scene request acknowledged");
        }
        await app.StopAsync(timeout.Token);
    }
}

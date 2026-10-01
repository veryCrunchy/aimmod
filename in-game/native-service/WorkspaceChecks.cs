using System.Net;
using System.Text;
using System.Text.Json;

namespace AimMod.InGame;

static class WorkspaceChecks
{
    sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Workspace checks must not contact Hub.");
    }

    public static async Task Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "aimmod-workspace-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        int checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("Workspace check failed: " + name);
            checks++;
        }
        try
        {
            using var hub = new Hub(folder, new NoNetwork(), openBrowser: _ => throw new InvalidOperationException("No browser in workspace checks."));
            await using var host = new WorkspaceHost(hub, folder);
            await host.Start(CancellationToken.None);
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
            var ui = new Uri(host.Url);
            var root = host.Url[..^3];
            using (var response = await client.GetAsync(host.Url))
            {
                var html = await response.Content.ReadAsStringAsync();
                Check(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentType?.MediaType == "text/html" && html.Contains("<html", StringComparison.OrdinalIgnoreCase), "capability serves embedded UI");
                Check(!html.Contains("src=\"replay.js\""), "workspace never loads sprite replay renderer");
                Check(response.Headers.CacheControl?.NoStore == true && response.Headers.TryGetValues("X-Content-Type-Options", out var values) && values.Contains("nosniff"), "response caching and content type policy");
            }
            using (var response = await client.GetAsync(new Uri(ui, "/invalid-capability/ui")))
                Check(response.StatusCode == HttpStatusCode.NotFound, "unknown capability rejected");
            using (var response = await client.GetAsync(root + "/replay.js"))
                Check(response.StatusCode == HttpStatusCode.NotFound, "sprite renderer is not served by in-game workspace");
            foreach (var asset in new[] { ("statistics.js", "application/javascript"), ("statistics.css", "text/css"), ("run-details.js", "application/javascript"), ("run-details.css", "text/css"), ("settings.js", "application/javascript"), ("mechanics.js", "application/javascript"), ("benchmarks.js", "application/javascript"), ("benchmarks.css", "text/css"), ("coaching.css", "text/css"), ("workspace-theme.css", "text/css"), ("overlay-editor.js", "application/javascript"), ("overlay-editor.css", "text/css"), ("overlay.js", "application/javascript"), ("overlay.css", "text/css") })
                using (var response = await client.GetAsync(root + "/" + asset.Item1))
                    Check(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentType?.MediaType == asset.Item2 && (await response.Content.ReadAsStringAsync()).Length > 100, "statistics asset embedded and served");
            using (var response = await client.GetAsync(root + "/history-import.js"))
                Check(response.StatusCode == HttpStatusCode.OK,"History import UI is served");
            using (var request = new HttpRequestMessage(HttpMethod.Post, root + "/history-import")) {
                request.Headers.Add("X-AimMod-UI", "1"); request.Content = new StringContent("{\"directory\":\"relative\"}", Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request); Check(response.StatusCode == HttpStatusCode.BadRequest,"Import requires explicit absolute directory");
            }
            using (var request = new HttpRequestMessage(HttpMethod.Post, root + "/history-import")) {
                request.Headers.Add("X-AimMod-UI", "1"); request.Content = new StringContent(JsonSerializer.Serialize(new { directory = @"\\synthetic-host\share" }), Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request); Check(response.StatusCode == HttpStatusCode.BadRequest,"Import refuses network share paths");
            }
            using (var request = new HttpRequestMessage(HttpMethod.Post, root + "/command")) {
                request.Headers.Add("X-AimMod-UI", "1"); request.Headers.Add("Origin", "https://attacker.example"); request.Content = new StringContent("history-next", Encoding.UTF8, "text/plain");
                using var response = await client.SendAsync(request); Check(response.StatusCode == HttpStatusCode.Forbidden,"Cross-origin command rejected even with capability and UI header");
            }
            Check(host.Url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal) && File.ReadAllText(Path.Combine(folder, "live-overlay-url.txt")).StartsWith(root, StringComparison.Ordinal), "workspace publishes loopback URLs");
            var csvFolder=Path.Combine(folder,"csv");Directory.CreateDirectory(csvFolder);
            File.WriteAllText(Path.Combine(csvFolder,"Synthetic - Challenge - 2026.01.01-12.01.00 Stats.csv"),"Score:,100\nChallenge Start:,12:00:00\nHit Count:,8\nMiss Count:,2\n");
            for(var attempt=0;attempt<2;attempt++)using (var request = new HttpRequestMessage(HttpMethod.Post, root + "/history-import")) {
                request.Headers.Add("X-AimMod-UI", "1"); request.Content = new StringContent(JsonSerializer.Serialize(new{directory=csvFolder}), Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request);using var summary=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check(response.StatusCode == HttpStatusCode.OK&&summary.RootElement.GetProperty("imported").GetInt32()==(attempt==0?1:0),"Import endpoint persists once and skips duplicate");
            }
            using (var response = await client.GetAsync(root + "/overlay-state")) {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check(!json.RootElement.GetProperty("live").GetProperty("active").GetBoolean(), "missing live telemetry stays inactive");
            }
            using (var response = await client.GetAsync(root + "/overlay-setup")) {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check(json.RootElement.GetProperty("obsAvailable").GetBoolean(), "dedicated OBS source starts with workspace");
                var url = json.RootElement.GetProperty("obsUrl").GetString()!;
                var obsRoot = url[..url.LastIndexOf('/')];
                using var state = JsonDocument.Parse(await client.GetStringAsync(obsRoot + "/overlay-state"));
                Check(!state.RootElement.GetProperty("live").GetProperty("active").GetBoolean() && !state.RootElement.GetProperty("live").TryGetProperty("scenario", out _), "disabled OBS suppresses performance data server side");
            }
            using (var request = new HttpRequestMessage(HttpMethod.Post, root + "/overlay-opponents")) {
                request.Headers.Add("X-AimMod-UI", "1"); request.Content = new StringContent("{\"key\":\"\"}", Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request);
                Check(response.StatusCode == HttpStatusCode.OK, "personal best can be selected without a leaderboard");
            }
            using (var request = new HttpRequestMessage(HttpMethod.Post, root + "/overlay-opponents")) {
                request.Headers.Add("X-AimMod-UI", "1"); request.Content = new StringContent("{\"key\":\"missing\"}", Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request);
                Check(response.StatusCode == HttpStatusCode.Conflict, "unknown opponent cannot be selected");
            }
            using (var response = await client.GetAsync(root + "/settings"))
                Check(response.StatusCode == HttpStatusCode.OK && (await response.Content.ReadAsStringAsync()).Contains("replayRecordingEnabled"), "settings available through capability");
            foreach (var route in new[] { "settings", "replay-library", "overlay-settings", "overlay-opponents", "history-import" }) {
                using var response = await client.PostAsync(root + "/" + route, new StringContent("{}", Encoding.UTF8, "application/json"));
                Check(response.StatusCode == HttpStatusCode.Forbidden, "mutations require non-simple UI header");
            }
            using (var request = new HttpRequestMessage(HttpMethod.Post, root + "/settings")) {
                request.Headers.Add("X-AimMod-UI", "1"); request.Content = new StringContent("{\"hubHistoryEnabled\":false}", Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request);
                Check(response.StatusCode == HttpStatusCode.OK && (await response.Content.ReadAsStringAsync()).Contains("\"hubHistoryEnabled\":false"), "settings patch persists through endpoint");
            }
            using (var request = new HttpRequestMessage(HttpMethod.Post, root + "/replay-library")) {
                request.Headers.Add("X-AimMod-UI", "1"); request.Content = new StringContent("{\"action\":\"delete\",\"id\":\"../private\"}", Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request);
                Check(response.StatusCode == HttpStatusCode.NotFound, "replay mutation traversal rejected");
            }
            using (var response = await client.GetAsync(root + "/run-details/synthetic?shotPage=-1"))
                Check(response.StatusCode == HttpStatusCode.BadRequest, "invalid shot page rejected");
            using (var response = await client.GetAsync(root + "/run-details/synthetic?shotPage=0"))
                Check(response.StatusCode == HttpStatusCode.NotFound, "absent local run telemetry not invented");
            host.Update("{\"synthetic\":true,\"revision\":7}");
            using (var response = await client.GetAsync(root + "/data"))
            {
                using var snapshot = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentType?.MediaType == "application/json" && snapshot.RootElement.GetProperty("revision").GetInt32() == 7, "snapshot GET exposes current data");
            }
            async Task<HttpStatusCode> Command(string body, bool header = true)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, root + "/command") { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
                if (header) request.Headers.Add("X-AimMod-UI", "1");
                using var response = await client.SendAsync(request);
                return response.StatusCode;
            }
            Check(await Command("unsupported-command") == HttpStatusCode.BadRequest, "invalid command rejected");
            Check(await Command("history-next", false) == HttpStatusCode.Forbidden, "custom header required");
            Check(await Command(new string('x', 1025)) == HttpStatusCode.Forbidden, "oversized body rejected");
            hub.MaxHistoryPage = 4;
            Check(await Command("history-next") == HttpStatusCode.NoContent && hub.HistoryPage == 0, "valid command queued before processing");
            await hub.Tick(CancellationToken.None);
            Check(hub.HistoryPage == 1, "queued command processed once");
            await hub.Tick(CancellationToken.None);
            Check(hub.HistoryPage == 1, "command not replayed");
            using (var request = new HttpRequestMessage(HttpMethod.Get, host.Url))
            {
                request.Headers.Host = "unexpected.example";
                using var response = await client.SendAsync(request);
                Check(response.StatusCode == HttpStatusCode.Forbidden, "unexpected Host rejected");
            }
            File.WriteAllText(Path.Combine(folder, "private-fixture.txt"), "synthetic-private-marker");
            foreach (var path in new[] { root + "/private-fixture.txt", root + "/../private-fixture.txt", root + "/%2e%2e/private-fixture.txt", root + "/ui/../../private-fixture.txt" })
            {
                using var response = await client.GetAsync(path);
                Check(response.StatusCode == HttpStatusCode.NotFound && !(await response.Content.ReadAsStringAsync()).Contains("synthetic-private-marker"), "filesystem and traversal paths unavailable");
            }
            Check(await Command("close") == HttpStatusCode.NoContent && File.ReadAllText(Path.Combine(folder, "close.request")) == "1", "close command creates owned request");

            Run Fixture(string id, string scenario, double score, double? accuracy = null) => new(id, scenario, score, accuracy, 60, 0, 0, "2026-01-01T00:00:00Z", null, null, null, null, false);
            var runs = new[] { Fixture("new", "Practice", 100), Fixture("other", "Different", 9000), Fixture("old", "Practice", 120, 92) };
            using (var data = JsonDocument.Parse(WorkspaceData.Build(runs, hub)))
            {
                var value = data.RootElement;
                Check(value.GetProperty("best").GetDouble() == 120 && value.GetProperty("graph").GetArrayLength() == 2, "scenario graph and PB remain isolated");
                Check(value.GetProperty("trend").ValueKind == JsonValueKind.Null && value.GetProperty("latest").GetProperty("Accuracy").ValueKind == JsonValueKind.Null, "insufficient baseline and unknown accuracy remain null");
                Check(value.GetProperty("statistics").GetProperty("Periods").GetArrayLength() == 4, "statistics periods integrated in workspace snapshot");
            }
            Check(await Command("scenario\tDifferent") == HttpStatusCode.NoContent, "scenario command accepted");
            await hub.Tick(CancellationToken.None);
            using (var data = JsonDocument.Parse(WorkspaceData.Build(runs, hub)))
                Check(data.RootElement.GetProperty("historyCount").GetInt32() == 1 && data.RootElement.GetProperty("latest").GetProperty("Id").GetString() == "other", "scenario selection filters history and selected run");
            Check(await Command("history-all") == HttpStatusCode.NoContent, "clear scenario filter");
            await hub.Tick(default);
            Check(await Command("search-history\tPRACT") == HttpStatusCode.NoContent, "search command accepted");
            await hub.Tick(default);
            using (var data = JsonDocument.Parse(WorkspaceData.Build(runs, hub)))
                Check(data.RootElement.GetProperty("historyCount").GetInt32() == 2, "search covers all matching runs case insensitively");
            using (var data = JsonDocument.Parse(WorkspaceData.Build([], hub)))
                Check(data.RootElement.GetProperty("latest").ValueKind == JsonValueKind.Null && data.RootElement.GetProperty("graph").GetArrayLength() == 0, "empty history safe");

            // A synthetic native replay exercises playback HTTP without touching game actors or scores.
            var journal = Path.Combine(folder, "completed.tsv");
            const string scoreMarker = "synthetic-score-journal-unchanged\n";
            File.WriteAllText(journal, scoreMarker);
            var journalTime = File.GetLastWriteTimeUtc(journal);
            var replayPath = Path.Combine(folder, "replays", "http-synthetic.amreplay");
            File.WriteAllText(replayPath, string.Join('\n',
                "{\"kind\":\"header\",\"version\":1,\"id\":\"http-synthetic\",\"scenario\":\"Synthetic\",\"recordedAt\":\"2026-01-01T00:00:00Z\",\"coordinates\":\"unreal-centimeters\",\"mapName\":\"Synthetic Map\",\"mapScale\":1}",
                "{\"kind\":\"frame\",\"t\":0,\"camera\":[0,0,0,0,0,0,90],\"actors\":[[1,100,0,0,10,10]]}",
                "{\"kind\":\"frame\",\"t\":10,\"camera\":[0,0,0,0,10,0,90],\"actors\":[[1,100,1,0,10,10]]}",
                "{\"kind\":\"end\",\"reason\":\"completed\",\"frames\":2,\"inputEvents\":0}") + "\n");
            async Task<HttpStatusCode> ReplayCommand(string json, bool header = true)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, root + "/native-replay") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                if (header) request.Headers.Add("X-AimMod-UI", "1");
                using var response = await client.SendAsync(request);
                return response.StatusCode;
            }
            using (var response = await client.GetAsync(root + "/native-replay"))
            {
                using var state = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check(response.StatusCode == HttpStatusCode.OK && !state.RootElement.GetProperty("rendererReady").GetBoolean(), "native renderer defaults unavailable");
            }
            Check(await ReplayCommand("{\"action\":\"load\",\"id\":\"http-synthetic\"}") == HttpStatusCode.Accepted, "native load without a ready renderer waits instead of failing");
            using (var response = await client.GetAsync(root + "/native-replay"))
            {
                using var state = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var start = state.RootElement.GetProperty("start");
                Check(start.GetProperty("pending").GetString() == "http-synthetic" && start.GetProperty("reason").GetString() == "game-unavailable"
                    && start.GetProperty("message").GetString()!.Length > 10 && !state.RootElement.GetProperty("playback").GetProperty("visible").GetBoolean(),
                    "pending start reports its reason and does not play");
            }
            Check(await ReplayCommand("{\"action\":\"cancel\"}") == HttpStatusCode.OK, "pending start can be cancelled");
            Check(await ReplayCommand("{\"action\":\"close\"}", false) == HttpStatusCode.Forbidden, "native command requires custom header");
            Check(await ReplayCommand(new string('x', 1025)) == HttpStatusCode.Forbidden, "native oversized body rejected");
            Check(await ReplayCommand("{") == HttpStatusCode.BadRequest, "native malformed JSON rejected");
            Check(await ReplayCommand("{\"action\":\"play\"}") == HttpStatusCode.BadRequest, "play without replay rejected");
            var rendererPath = Path.Combine(folder, "native-replay-renderer.json");
            File.WriteAllText(rendererPath, "{\"state\":\"ready\",\"mode\":\"main\"}");
            foreach (var id in new[] { "missing", "../http-synthetic", "http-synthetic.amreplay", "legacy-only" })
                Check(await ReplayCommand(JsonSerializer.Serialize(new { action = "load", id })) == HttpStatusCode.NotFound, "native load rejects missing or non-native identifier");
            Check(await ReplayCommand("{\"action\":\"load\",\"id\":\"http-synthetic\"}") == HttpStatusCode.OK, "native valid replay loads");
            Check(await ReplayCommand("{\"action\":\"play\"}") == HttpStatusCode.OK, "native play accepted");
            Check(await ReplayCommand("{\"action\":\"pause\"}") == HttpStatusCode.OK, "native pause accepted");
            Check(await ReplayCommand("{\"action\":\"seek\",\"value\":5}") == HttpStatusCode.OK, "native seek accepted");
            Check(await ReplayCommand("{\"action\":\"speed\",\"value\":1.5}") == HttpStatusCode.OK, "native supported speed accepted");
            Check(await ReplayCommand("{\"action\":\"speed\",\"value\":99}") == HttpStatusCode.BadRequest, "native unsupported speed rejected");
            Check(await ReplayCommand("{\"action\":\"layout\",\"area\":[0.1,0.2,0.5,0.5]}") == HttpStatusCode.OK, "native normalized layout accepted");
            Check(await ReplayCommand("{\"action\":\"layout\",\"area\":[0.9,0.2,0.5,0.5]}") == HttpStatusCode.BadRequest, "native out of bounds layout rejected");
            using (var response = await client.GetAsync(root + "/native-replay"))
            {
                using var state = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var replay = state.RootElement.GetProperty("playback");
                Check(replay.GetProperty("id").GetString() == "http-synthetic" && replay.GetProperty("time").GetDouble() == 5 && !replay.GetProperty("playing").GetBoolean(), "native status reflects selected paused seek");
            }
            Check(await ReplayCommand("{\"action\":\"close\"}") == HttpStatusCode.OK, "native close accepted");
            File.SetLastWriteTimeUtc(rendererPath, DateTime.UtcNow.AddSeconds(-10));
            Check(await ReplayCommand("{\"action\":\"load\",\"id\":\"http-synthetic\"}") == HttpStatusCode.Accepted, "stale renderer readiness waits instead of loading");
            Check(await ReplayCommand("{\"action\":\"cancel\"}") == HttpStatusCode.OK, "stale wait cancelled");
            // Publication gaps reuse only the original fresh acknowledgement.
            var ackPath = Path.Combine(folder, "ack-race.json");
            var ackNow = DateTime.UtcNow;
            void PublishAck(string json) { File.WriteAllText(ackPath, json); File.SetLastWriteTimeUtc(ackPath, ackNow); }
            const string ready3 = "{\"state\":\"ready\",\"mode\":\"main\",\"protocol\":3}";
            var ack = new RendererAcknowledgement(ackPath, () => ackNow);
            Check(!ack.Read().Ready, "missing acknowledgement never invents initial readiness");
            PublishAck(ready3);
            Check(ack.Read().Ready && ack.Read().Protocol == 3, "fresh acknowledgement enables matching protocol");
            File.Delete(ackPath);
            ackNow = ackNow.AddSeconds(2);
            Check(ack.Read().Ready && ack.Read().Protocol == 3, "publication gap retains readiness and negotiated protocol");
            ackNow = ackNow.AddSeconds(1.01);
            Check(!ack.Read().Ready && ack.Read().Protocol == 2, "repeated reads cannot extend expired acknowledgement");
            PublishAck(ready3); Check(ack.Read().Ready, "fresh publication recovers after expiry");
            using (var exclusive = new FileStream(ackPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(ack.Read().Ready && ack.Read().Protocol == 3, "sharing violation retains fresh acknowledgement");
            using (var pending = new FileStream(ackPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                File.Delete(ackPath);
                Check(ack.Read().Ready && ack.Read().Protocol == 3, "delete-pending acknowledgement (Lua remove+rename) is a transient gap");
            }
            PublishAck(ready3);
            PublishAck("{\"state\":\"error\",\"mode\":\"main\",\"detail\":\"map-mismatch\"}");
            Check(!ack.Read().Ready && ack.Read().Reason == "map-mismatch", "explicit scene error invalidates immediately");
            File.Delete(ackPath);
            Check(!ack.Read().Ready && ack.Read().Protocol == 2, "gap after error cannot revive prior readiness");
            PublishAck(ready3); Check(ack.Read().Ready, "ready acknowledgement recovers after error");
            PublishAck("{"); Check(!ack.Read().Ready, "malformed acknowledgement fails closed");
            PublishAck(ready3); Check(ack.Read().Ready, "ready acknowledgement recovers after malformed data");
            File.SetLastWriteTimeUtc(ackPath, ackNow.AddSeconds(-4));
            Check(!ack.Read().Ready, "existing stale acknowledgement immediately invalidates fresh cache");

            // Exercise the real 30 Hz pump for longer than the reported one-second
            // failure, with multiple remove/rename windows spanning several ticks.
            var pumpFolder = Path.Combine(folder, "ack-pump"); Directory.CreateDirectory(pumpFolder);
            var pumpAckPath = Path.Combine(pumpFolder, "native-replay-renderer.json");
            File.WriteAllText(pumpAckPath, ready3);
            var pumpAck = new RendererAcknowledgement(pumpAckPath);
            Check(pumpAck.Read().Ready, "pump has initial renderer acknowledgement");
            await using (var player = new NativeReplayPlayback(pumpFolder, () => pumpAck.Read().Ready, () => pumpAck.Read().Protocol)) {
                player.Load(new NativeReplay(1, "ack-pump", "Synthetic", "2026-01-01", "completed", 10,
                    [new(0, [0,0,0,0,0,0,90], []), new(10, [0,0,0,0,0,0,90], [])], []));
                player.Command("play"); player.Start();
                var started = System.Diagnostics.Stopwatch.StartNew();
                for (var cycle = 0; cycle < 4; cycle++) {
                    File.Delete(pumpAckPath);
                    await Task.Delay(180);
                    File.WriteAllText(pumpAckPath + ".next", ready3);
                    File.Move(pumpAckPath + ".next", pumpAckPath, true);
                    await Task.Delay(180);
                }
                using (var state = JsonDocument.Parse(JsonSerializer.Serialize(player.Status)))
                    Check(started.Elapsed.TotalSeconds > 1 && state.RootElement.GetProperty("playing").GetBoolean()
                        && state.RootElement.GetProperty("time").GetDouble() > 1, "actual pump plays beyond one second across repeated publication gaps");
                // Replace atomically: the pump reads this file 30 times a second.
                File.WriteAllText(pumpAckPath + ".next", "{\"state\":\"error\",\"mode\":\"main\"}"); File.Move(pumpAckPath + ".next", pumpAckPath, true);
                await Task.Delay(200);
                using (var state = JsonDocument.Parse(JsonSerializer.Serialize(player.Status)))
                    Check(!state.RootElement.GetProperty("visible").GetBoolean() && !state.RootElement.GetProperty("playing").GetBoolean(), "actual pump closes on explicit renderer error");
            }
            Check(File.ReadAllText(journal) == scoreMarker && File.GetLastWriteTimeUtc(journal) == journalTime, "playback never writes completed scores journal");
        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine($"{checks} workspace HTTP and data checks passed.");
    }
}

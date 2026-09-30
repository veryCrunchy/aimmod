using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AimMod.InGame;

// Regression checks for the local-server, persistence, Hub and playback
// hardening. Synthetic data only; nothing contacts Hub or the game.
static class HardeningChecks
{
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint db, int flags, nint vfs);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_close_v2(nint db);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_exec(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, nint callback, nint context, nint error);

    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    sealed class RateLimitedLeaderboard : HttpMessageHandler
    {
        public int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (++Count == 1)
            {
                var limited = new HttpResponseMessage((HttpStatusCode)429);
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
                return Task.FromResult(limited);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"records\":[],\"topScores\":[]}", Encoding.UTF8, "application/json") });
        }
    }

    public static async Task Run()
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception("Hardening check failed: " + name); count++; }
        var folder = Path.Combine(Path.GetTempPath(), "aimmod-hardening-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // Capability comparison.
            var capability = Encoding.ASCII.GetBytes("0123456789abcdef");
            Check(LoopbackServer.CapabilityMatches("/0123456789abcdef/ui", capability) && LoopbackServer.CapabilityMatches("/0123456789abcdef", capability), "capability segment accepted");
            Check(!LoopbackServer.CapabilityMatches("/0123456789abcdeg/ui", capability) && !LoopbackServer.CapabilityMatches("/0123456789abcdef0/ui", capability)
                && !LoopbackServer.CapabilityMatches("/0123456789abcde/ui", capability) && !LoopbackServer.CapabilityMatches("/", capability)
                && !LoopbackServer.CapabilityMatches("/ui/0123456789abcdef", capability), "wrong, prefixed and truncated capabilities rejected");

            // Environment configuration cannot add listeners or enable the
            // development exception page on the loopback servers.
            var variables = new Dictionary<string, string?> {
                ["ASPNETCORE_ENVIRONMENT"] = "Development", ["DOTNET_ENVIRONMENT"] = "Development",
                ["ASPNETCORE_URLS"] = "http://127.0.0.1:0", ["ASPNETCORE_Kestrel__Endpoints__Extra__Url"] = "http://127.0.0.1:0" };
            var previous = variables.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
            WebApplication app;
            try { foreach (var (key, value) in variables) Environment.SetEnvironmentVariable(key, value); app = LoopbackServer.Build(0); }
            finally { foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value); }
            await using (app)
            {
                const string token = "synthetic0capability0token";
                LoopbackServer.UseGuards(app, token);
                app.MapGet("/" + token + "/fail", (Func<string>)(() => throw new InvalidOperationException("synthetic-exception-marker")));
                app.MapGet("/" + token + "/ok", () => "ok");
                app.MapPost("/" + token + "/ok", () => "ok");
                await app.StartAsync();
                var address = LoopbackServer.VerifiedAddress(app);
                Check(app.Urls.Count == 1 && address.StartsWith("http://127.0.0.1:", StringComparison.Ordinal), "only the IPv4 loopback listener is bound");
                var port = new Uri(address).Port;
                using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
                async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> Send(HttpMethod method, string path, Action<HttpRequestMessage>? configure = null)
                {
                    using var request = new HttpRequestMessage(method, address + path);
                    configure?.Invoke(request);
                    var response = await client.SendAsync(request);
                    return (response.StatusCode, await response.Content.ReadAsStringAsync(), response);
                }
                var failed = await Send(HttpMethod.Get, "/" + token + "/fail");
                Check(failed.Status == HttpStatusCode.InternalServerError && !failed.Body.Contains("synthetic-exception-marker") && !failed.Body.Contains(folder), "unhandled errors never expose messages or paths");
                var ok = await Send(HttpMethod.Get, "/" + token + "/ok");
                Check(ok.Status == HttpStatusCode.OK && !ok.Response.Headers.Contains("Server") && ok.Response.Headers.TryGetValues("Referrer-Policy", out var referrer) && referrer.Single() == "no-referrer", "no server banner; capability URL never sent as referrer");
                Check((await Send(HttpMethod.Get, "/synthetic0capability0tokex/ok")).Status == HttpStatusCode.NotFound, "same-length wrong capability rejected");
                Check((await Send(HttpMethod.Post, "/" + token + "/ok", r => r.Headers.Add("Origin", "https://attacker.example"))).Status == HttpStatusCode.Forbidden, "cross-origin request rejected before routing");
                Check((await Send(HttpMethod.Get, "/" + token + "/ok", r => r.Headers.Add("Origin", "http://127.0.0.1:" + (port == 65535 ? 65534 : port + 1)))).Status == HttpStatusCode.Forbidden, "other loopback port origin rejected");
                Check((await Send(HttpMethod.Get, "/" + token + "/ok", r => r.Headers.Add("Origin", "http://localhost:" + port))).Status == HttpStatusCode.Forbidden, "localhost alias origin rejected");
                Check((await Send(HttpMethod.Post, "/" + token + "/ok", r => r.Headers.Add("Origin", "http://127.0.0.1:" + port))).Status == HttpStatusCode.OK, "same-origin request accepted");
                Check((await Send(HttpMethod.Post, "/" + token + "/ok", r => r.Headers.Add("Origin", "null"))).Status == HttpStatusCode.OK, "opaque origin accepted (cannot send UI header without preflight)");
                Check((await Send(HttpMethod.Get, "/" + token + "/ok", r => r.Headers.Add("Sec-Fetch-Site", "cross-site"))).Status == HttpStatusCode.Forbidden, "cross-site fetch metadata rejected");
                Check((await Send(HttpMethod.Get, "/" + token + "/ok", r => r.Headers.Host = "127.0.0.1:" + (port == 65535 ? 65534 : port + 1))).Status == HttpStatusCode.Forbidden, "Host naming another port rejected");
                Check((await Send(HttpMethod.Get, "/" + token + "/ok", r => r.Headers.Host = "localhost:" + port)).Status == HttpStatusCode.Forbidden, "Host alias rejected (DNS rebinding)");
                await app.StopAsync();
            }

            // Frame pacing: Windows rounds every wait up to its timer tick.
            // Absolute deadlines must still average the requested interval.
            long ticks = 0; var pacer = new FramePacer(() => ticks);
            var quantum = (long)(Stopwatch.Frequency * 0.015625);
            var frames = 300; var start = ticks;
            for (var i = 0; i < frames; i++)
            {
                var wait = (long)(pacer.Next(NativeReplayPlayback.ActiveInterval).TotalSeconds * Stopwatch.Frequency);
                ticks += Math.Max(1, (wait + quantum - 1) / quantum) * quantum; // Task.Delay granularity
            }
            var average = (double)(ticks - start) / frames / Stopwatch.Frequency;
            Check(Math.Abs(average - 1.0 / 30) < 0.0025, "replay frames average 30 Hz despite timer rounding");
            ticks += Stopwatch.Frequency; // one second stall
            Check(pacer.Next(NativeReplayPlayback.ActiveInterval) > TimeSpan.FromMilliseconds(30), "a stall restarts the schedule instead of bursting frames");

            // Graceful playback shutdown publishes a closed frame and retracts the heartbeat.
            var playbackFolder = Path.Combine(folder, "playback"); Directory.CreateDirectory(playbackFolder);
            var playback = new NativeReplayPlayback(playbackFolder, () => true, () => 5);
            playback.Load(new NativeReplay(1, "shutdown", "Synthetic", "2026-01-01", "completed", 1, [new(0, [0, 0, 0, 0, 0, 0, 90], []), new(1, [0, 0, 0, 0, 0, 0, 90], [])], []));
            playback.Start();
            var heartbeat = Path.Combine(playbackFolder, "native-replay-worker.txt");
            var frame = Path.Combine(playbackFolder, "replay-frame.tsv");
            for (var i = 0; i < 100 && !(File.Exists(heartbeat) && File.Exists(frame)); i++) await Task.Delay(20);
            Check(playback.Visible && File.Exists(heartbeat), "playback publishes heartbeat while running");
            await playback.DisposeAsync(); await playback.DisposeAsync();
            Check(!File.Exists(heartbeat) && File.ReadAllText(frame).Split('\n')[0].EndsWith("\t0", StringComparison.Ordinal), "shutdown retracts heartbeat and publishes closed frame");
            Check(!Directory.EnumerateFiles(playbackFolder, "*.tmp").Any(), "atomic writes leave no temporary files");

            // Live overlay: in-memory replay state and precomputed personal bests.
            Run Fixture(string id, string scenario, double score) => new(id, scenario, score, null, 60, 0, 0, "2026-01-01T00:00:00Z", null, null, null, null, false);
            var history = new[] { Fixture("a", "Synthetic", 10), Fixture("b", "synthetic", 30), Fixture("c", "Synthetic", 30), Fixture("d", "Other", 99), Fixture("e", "Synthetic", double.NaN) };
            var bests = LiveOverlayState.PersonalBests(history);
            Check(bests["SYNTHETIC"].Id == "b" && bests["Other"].Score == 99 && bests.Count == 2, "personal best lookup is case-insensitive, first tie wins, invalid scores ignored");
            var liveFolder = Path.Combine(folder, "live"); Directory.CreateDirectory(liveFolder);
            File.WriteAllText(Path.Combine(liveFolder, "live-overlay.json"), "{\"version\":1,\"active\":true,\"scenario\":\"Synthetic\",\"score\":10,\"seconds\":20}");
            Check(LiveOverlayState.Read(liveFolder, s => bests.GetValueOrDefault(s), true).Replay, "in-memory playback suppresses live overlay");
            var live = LiveOverlayState.Read(liveFolder, s => bests.GetValueOrDefault(s), false);
            Check(live.Active && live.PersonalBest == 30 && live.PersonalBest == LiveOverlayState.Read(liveFolder, history).PersonalBest, "precomputed personal best matches history scan");
            File.WriteAllText(Path.Combine(liveFolder, "replay-frame.tsv"), "AIMMOD_REPLAY_5\t1\t1\n");
            Check(LiveOverlayState.Read(liveFolder, history).Replay, "file-based replay detection still works standalone");
            Check(LiveOverlayState.Read(liveFolder, s => bests.GetValueOrDefault(s), false).Active, "worker state wins over a stale frame file");

            // Hub resilience.
            Check(Hub.Backoff(1) == TimeSpan.FromMinutes(2) && Hub.Backoff(2) == TimeSpan.FromMinutes(4) && Hub.Backoff(4) == TimeSpan.FromMinutes(16) && Hub.Backoff(9) == TimeSpan.FromMinutes(30), "Hub refresh backoff grows and is capped");
            var hubFolder = Path.Combine(folder, "hub"); Directory.CreateDirectory(hubFolder);
            AccountVault.Save(Path.Combine(hubFolder, "account.bin"), new HubAccount("synthetic", "Synthetic", "synthetic-id", "synthetic-token"));
            File.WriteAllText(Path.Combine(hubFolder, "hub-cache.json"), "{");
            using (var damaged = new Hub(hubFolder, new RateLimitedLeaderboard()))
                Check(damaged.ExternalId == "synthetic-id" && damaged.Runs.Count == 0, "damaged offline cache keeps the linked account");
            File.WriteAllBytes(Path.Combine(hubFolder, "account.bin"), []);
            using (var empty = new Hub(hubFolder, new RateLimitedLeaderboard()))
                Check(empty.ExternalId.Length == 0, "empty credential file treated as unlinked");
            var clock = new Clock(); var limited = new RateLimitedLeaderboard();
            Directory.CreateDirectory(Path.Combine(folder, "hub-limit"));
            using (var hub = new Hub(Path.Combine(folder, "hub-limit"), limited, clock))
            {
                async Task<bool> Unavailable() { try { await hub.Leaderboard("", default); return false; } catch (HttpRequestException) { return true; } }
                Check(await Unavailable() && limited.Count == 1, "429 reported as unavailable");
                clock.Now = clock.Now.AddMinutes(5);
                Check(await Unavailable() && limited.Count == 1, "leaderboard honors Retry-After without contacting Hub");
                clock.Now = clock.Now.AddMinutes(6);
                Check(!await Unavailable() && limited.Count == 2, "leaderboard resumes after Retry-After");
            }

            // Read-only history: NULL columns never become real zero values.
            var database = Path.Combine(folder, "history.sqlite3"); nint db = 0;
            try
            {
                if (sqlite3_open_v2(database, out db, 6, 0) != 0) throw new Exception("Synthetic database open failed");
                const string sql = """
                    CREATE TABLE sessions(id TEXT,scenario TEXT,score REAL,accuracy REAL,duration_secs REAL,kills REAL,damage_done REAL,timestamp TEXT,smoothness_json TEXT,has_replay INTEGER);
                    INSERT INTO sessions VALUES('known','Synthetic',10,NULL,60,1,1,'2026-01-02','',0);
                    INSERT INTO sessions VALUES(NULL,'Synthetic',10,50,60,1,1,'2026-01-01','',0);
                    INSERT INTO sessions VALUES('no-scenario',NULL,10,50,60,1,1,'2026-01-01','',0);
                    INSERT INTO sessions VALUES('no-score','Synthetic',NULL,50,60,1,1,'2026-01-01','',0);
                    """;
                if (sqlite3_exec(db, sql, 0, 0, 0) != 0) throw new Exception("Synthetic database seed failed");
            }
            finally { if (db != 0) sqlite3_close_v2(db); }
            var runs = History.Read(database);
            Check(runs.Count == 1 && runs[0].Id == "known" && runs[0].Accuracy is null, "unknown accuracy stays unknown; rows without identity, scenario or score skipped");
        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine($"{count} hardening checks passed.");
    }
}

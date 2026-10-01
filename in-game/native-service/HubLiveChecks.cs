using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AimMod.InGame;

// Live activity and run uploads against a fake AimMod Hub. Synthetic account
// and scenario names only; nothing leaves the process.
static class HubLiveChecks
{
    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    sealed record Sent(string Method, string Path, string? Auth, string? Connect, string Body);
    sealed class FakeHub : HttpMessageHandler
    {
        public readonly List<Sent> Requests = [];
        public HttpStatusCode Status = HttpStatusCode.OK;
        public TimeSpan? RetryAfter;
        public bool Offline;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(request.Method.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Connect-Protocol-Version", out var v) ? v.First() : null, body));
            if (Offline) throw new HttpRequestException("Offline fixture");
            var response = new HttpResponseMessage(Status) { Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json") };
            if (RetryAfter is TimeSpan wait) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
            return response;
        }
    }
    static void Check(bool value, string name) { if (!value) throw new Exception(name); }
    static int count;
    static void Pass(bool value, string name) { Check(value, name); count++; }

    static LiveOverlaySnapshot Idle(bool available = true) => new(available, false, false, null, null, null, null, null, null, null, null, null, null, null, null, null);
    static LiveOverlaySnapshot Playing(string scenario, double score, bool paused = false) =>
        new(true, true, paused, scenario, score, 30, 40, 30, 12, null, 75, null, null, null, null, null, ScorePerMinute: score * 2, RemainingSeconds: 30);
    static GameScene Scene(string scenario = "", bool inChallenge = false) => new(true, scenario, "SyntheticMap", 1, inChallenge, inChallenge, false, false);

    public static async Task Run()
    {
        count = 0;
        Builder();
        var folder = Path.Combine(Path.GetTempPath(), "aimmod-live-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await Publisher(folder);
            await Uploads(Path.Combine(folder, "uploads"));
            await Endpoints(Path.Combine(folder, "web"));
        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine($"{count} Hub live activity and upload checks passed.");
    }

    static void Builder()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var session = new DiscordSession(now.AddMinutes(-10), 3, null, false, null);
        HubLivePayload? Build(HubLiveSnapshot s) => HubLiveActivityBuilder.Build(s, session, now, "1.2.3");
        Pass(Build(new(false, null, Idle(false), false, null, null, null)) is null, "No heartbeat while KovaaK's is closed");
        var starting = Build(new(true, null, Idle(false), false, null, null, null))!;
        Pass(starting is { RuntimeLoaded: false, BridgeConnected: false, Activity: "menu", GameState: "Starting AimMod" }, "Game running without AimModCore reports the runtime as not loaded");
        var menu = Build(new(true, Scene(), Idle(), false, null, null, null))!;
        Pass(menu is { RuntimeLoaded: true, BridgeConnected: true, Activity: "menu", GameState: "In the menus", ScenarioName: null }, "Menus");
        var loaded = Build(new(true, Scene("Synthetic Track"), Idle(), false, null, null, null))!;
        Pass(loaded is { Activity: "scenario", ScenarioName: "Synthetic Track", Score: null }, "A loaded scenario outside a challenge");
        var playing = Build(new(true, Scene("Synthetic Track", true), Playing("Synthetic Track", 512.25), false, null, null, null))!;
        Pass(playing is { Activity: "challenge", GameState: "In a challenge", Paused: false, ScenarioName: "Synthetic Track", Score: 512.25, AccuracyPct: 75, Kills: 12, ElapsedSecs: 30, TimeRemainingSecs: 30, ScorePerMinute: 1024.5 }, "Challenge carries live score, accuracy, kills and timers");
        Pass(Build(new(true, Scene("Synthetic Track", true), Playing("Synthetic Track", 1, paused: true), false, null, null, null)) is { Paused: true, GameState: "Paused" }, "Paused challenge");
        var replay = Build(new(true, Scene("Synthetic Track"), Idle() with { Replay = true }, true, "Synthetic Track", null, null))!;
        Pass(replay is { Activity: "replay", ScenarioName: "Synthetic Track", BridgeConnected: true, Score: null }, "Watching a replay");
        var lobbyInfo = new DiscordLobbyInfo("party", 2, 4, "Score race", "Synthetic Track", "match", 2, 3, null, 5, 1, null, null);
        var match = Build(new(true, Scene(Multiplayer.MatchScenario.Prefix + "Synthetic Track - 0123abcd", true), Playing(Multiplayer.MatchScenario.Prefix + "Synthetic Track - 0123abcd", 300), false, null, lobbyInfo, true))!;
        Pass(match is { Activity: "match", GameState: "Score race · Round 2/3", ScenarioName: "Synthetic Track", Score: 300, SteamConnected: true }, "Multiplayer match uses the base scenario and round");
        Pass(!match.ToJson().Contains(Multiplayer.MatchScenario.Prefix), "Generated arena names are never sent");
        Pass(Build(new(true, Scene(), Idle(), false, null, lobbyInfo with { State = "lobby", Scenario = null }, false)) is { Activity: "lobby", GameState: "In a lobby · 2/4 · Score race", SteamConnected: false }, "Lobby");
        Pass(Build(new(true, Scene(), Idle(), false, null, lobbyInfo with { State = "results" }, null)) is { Activity: "results" }, "Match results");
        var stale = Build(new(true, Scene("Synthetic Track", true), Idle(false), false, null, null, null))!;
        Pass(stale is { RuntimeLoaded: true, BridgeConnected: false, GameState: "Reconnecting" }, "A stale live feed is reported as reconnecting");
        using var doc = JsonDocument.Parse(playing.ToJson());
        var root = doc.RootElement;
        Pass(root.GetProperty("gameStateCode").GetInt32() == 0 && root.GetProperty("gameState").GetString() == "In a challenge" && root.GetProperty("runtimeLoaded").GetBoolean()
            && root.GetProperty("bridgeConnected").GetBoolean() && root.GetProperty("kills").ValueKind == JsonValueKind.Number && root.GetProperty("accuracyPct").GetDouble() == 75
            && root.GetProperty("client").GetString() == "in-game" && root.GetProperty("clientVersion").GetString() == "1.2.3" && root.GetProperty("activity").GetString() == "challenge"
            && root.GetProperty("sessionRunCount").GetInt32() == 3 && root.GetProperty("sessionElapsedSecs").GetDouble() == 600, "Payload uses the Hub's field names");
        Pass(!root.TryGetProperty("scenarioType", out _) && !root.TryGetProperty("steamConnected", out _) && !root.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.Null), "Unknown values are omitted, never null");
    }

    static Hub Linked(string folder, FakeHub fake, Clock clock)
    {
        Directory.CreateDirectory(folder);
        AccountVault.Save(Path.Combine(folder, "account.bin"), new HubAccount("synthetic", "Synthetic", "synthetic-user", "synthetic-upload-token"));
        return new Hub(folder, fake, clock, _ => { }, () => false);
    }

    static async Task Publisher(string root)
    {
        var clock = new Clock();
        HubLiveSnapshot snapshot = new(true, Scene(), Idle(), false, null, null, null);
        // Not linked: nothing is sent.
        var unlinkedFolder = Path.Combine(root, "unlinked"); Directory.CreateDirectory(unlinkedFolder);
        var quiet = new FakeHub();
        using (var unlinked = new Hub(unlinkedFolder, quiet, clock, _ => { }, () => false))
        {
            await using var publisher = new HubLivePublisher(unlinked, new HubSharingSettings(unlinkedFolder), () => snapshot, () => clock.Now);
            await publisher.Step(default);
            Pass(quiet.Requests.Count == 0 && publisher.Status == "not-linked", "Nothing is sent without a linked account");
        }

        var folder = Path.Combine(root, "linked");
        var fake = new FakeHub();
        using var hub = Linked(folder, fake, clock);
        var sharing = new HubSharingSettings(folder);
        Pass(sharing.Current is { LiveActivity: true, RunUploads: true }, "Sharing defaults follow the companion app: on once linked");
        var publisher2 = new HubLivePublisher(hub, sharing, () => snapshot, () => clock.Now);
        await publisher2.Step(default);
        var first = fake.Requests.Single();
        Pass(first is { Method: "POST", Path: "/activity/live", Auth: "Bearer synthetic-upload-token" } && publisher2.Status == "live", "Heartbeat posts to /activity/live as the linked account");
        Pass(JsonDocument.Parse(first.Body).RootElement.GetProperty("client").GetString() == "in-game", "Heartbeat names the in-game client");
        clock.Now = clock.Now.AddSeconds(10); await publisher2.Step(default);
        Pass(fake.Requests.Count == 1, "An unchanged state is not resent before the heartbeat interval");
        clock.Now = clock.Now.AddSeconds(21); await publisher2.Step(default);
        Pass(fake.Requests.Count == 2, "Heartbeat every 30 s keeps the 90 s Hub entry alive");
        snapshot = new(true, Scene("Synthetic Track", true), Playing("Synthetic Track", 10), false, null, null, null);
        clock.Now = clock.Now.AddSeconds(1); await publisher2.Step(default);
        Pass(fake.Requests.Count == 2, "State changes respect the 2 s spacing");
        clock.Now = clock.Now.AddSeconds(1.5); await publisher2.Step(default);
        Pass(fake.Requests.Count == 3 && fake.Requests[^1].Body.Contains("\"activity\":\"challenge\""), "Starting a challenge is sent promptly");
        snapshot = snapshot with { Live = Playing("Synthetic Track", 20) };
        clock.Now = clock.Now.AddSeconds(3); await publisher2.Step(default);
        Pass(fake.Requests.Count == 3, "Score changes wait for the 5 s value spacing");
        clock.Now = clock.Now.AddSeconds(2.5); await publisher2.Step(default);
        Pass(fake.Requests.Count == 4 && fake.Requests[^1].Body.Contains("\"score\":20"), "Score changes are sent at most every 5 s");

        // Errors back off; Retry-After is honored.
        fake.Status = HttpStatusCode.InternalServerError;
        snapshot = snapshot with { Live = Playing("Synthetic Track", 30) };
        clock.Now = clock.Now.AddSeconds(6); await publisher2.Step(default);
        var failedAt = fake.Requests.Count;
        clock.Now = clock.Now.AddSeconds(4); await publisher2.Step(default);
        Pass(fake.Requests.Count == failedAt && publisher2.Status == "unavailable", "A failed heartbeat backs off");
        clock.Now = clock.Now.AddSeconds(2); await publisher2.Step(default);
        Pass(fake.Requests.Count == failedAt + 1, "Retry after the first 5 s backoff");
        clock.Now = clock.Now.AddSeconds(6); await publisher2.Step(default);
        Pass(fake.Requests.Count == failedAt + 1, "The second failure waits 10 s");
        fake.Status = (HttpStatusCode)429; fake.RetryAfter = TimeSpan.FromSeconds(60);
        clock.Now = clock.Now.AddSeconds(5); await publisher2.Step(default);
        var limitedAt = fake.Requests.Count;
        clock.Now = clock.Now.AddSeconds(45); await publisher2.Step(default);
        Pass(fake.Requests.Count == limitedAt, "Retry-After from the Hub is honored");
        fake.Status = HttpStatusCode.OK; fake.RetryAfter = null;
        clock.Now = clock.Now.AddSeconds(20); await publisher2.Step(default);
        Pass(fake.Requests.Count == limitedAt + 1 && publisher2.Status == "live", "Publishing resumes after the wait");
        fake.Offline = true; snapshot = snapshot with { Live = Playing("Synthetic Track", 40) };
        clock.Now = clock.Now.AddSeconds(6); await publisher2.Step(default);
        Pass(publisher2.Status == "unavailable", "A network failure is reported, not thrown");
        fake.Offline = false;
        clock.Now = clock.Now.AddSeconds(6); await publisher2.Step(default);
        Pass(publisher2.Status == "live", "Publishing recovers once the Hub is reachable");

        // Turning the setting off removes the entry at once, then sends nothing.
        sharing.ApplyJson(Encoding.UTF8.GetBytes("{\"hubLiveActivityEnabled\":false}"));
        clock.Now = clock.Now.AddSeconds(1); await publisher2.Step(default);
        Pass(fake.Requests[^1] is { Method: "DELETE", Path: "/activity/live", Auth: "Bearer synthetic-upload-token" } && publisher2.Status == "off", "Show me on Live activity off removes the heartbeat");
        var offAt = fake.Requests.Count;
        clock.Now = clock.Now.AddMinutes(5); await publisher2.Step(default);
        Pass(fake.Requests.Count == offAt, "Nothing is sent while live activity is off");
        Pass(new HubSharingSettings(folder).Current is { LiveActivity: false, RunUploads: true }, "The choice is saved");
        sharing.ApplyJson(Encoding.UTF8.GetBytes("{\"hubLiveActivityEnabled\":true}"));

        // KovaaK's closing removes the entry.
        await publisher2.Step(default);
        snapshot = new(false, null, Idle(false), false, null, null, null);
        clock.Now = clock.Now.AddSeconds(3); await publisher2.Step(default);
        Pass(fake.Requests[^1].Method == "DELETE" && publisher2.Status == "waiting", "Closing KovaaK's removes the heartbeat");
        var closedAt = fake.Requests.Count;
        clock.Now = clock.Now.AddMinutes(2); await publisher2.Step(default);
        Pass(fake.Requests.Count == closedAt, "Nothing is sent while KovaaK's is closed");

        // A refused token stops publishing until another account is linked.
        snapshot = new(true, Scene(), Idle(), false, null, null, null);
        fake.Status = HttpStatusCode.Unauthorized;
        await publisher2.Step(default);
        var refusedAt = fake.Requests.Count;
        clock.Now = clock.Now.AddMinutes(10); await publisher2.Step(default);
        Pass(fake.Requests.Count == refusedAt && publisher2.Status == "rejected", "A refused upload token stops publishing");
        await publisher2.DisposeAsync();

        // Unlinking removes the heartbeat while the credential still exists; disposing removes it too.
        fake.Status = HttpStatusCode.OK;
        var relinkFolder = Path.Combine(root, "relink");
        using var hub3 = Linked(relinkFolder, fake, clock);
        var publisher3 = new HubLivePublisher(hub3, new HubSharingSettings(relinkFolder), () => snapshot, () => clock.Now);
        hub3.Unlinking = publisher3.ClearNow;
        await publisher3.Step(default);
        Pass(fake.Requests[^1].Method == "POST", "Linked again: publishing");
        Check(hub3.Enqueue("unlink"), "unlink queued"); await hub3.Tick(default);
        Pass(fake.Requests[^1] is { Method: "DELETE", Auth: "Bearer synthetic-upload-token" } && !hub3.Linked, "Unlinking removes the live entry before forgetting the account");
        var unlinkedAt = fake.Requests.Count;
        await publisher3.Step(default);
        Pass(fake.Requests.Count == unlinkedAt && publisher3.Status == "not-linked", "Nothing is sent after unlinking");
        await publisher3.DisposeAsync();
        using var hub4 = Linked(Path.Combine(root, "dispose"), fake, clock);
        var publisher4 = new HubLivePublisher(hub4, new HubSharingSettings(Path.Combine(root, "dispose")), () => snapshot, () => clock.Now);
        await publisher4.Step(default); await publisher4.DisposeAsync();
        Pass(fake.Requests[^1].Method == "DELETE", "Stopping the service removes the heartbeat");

        // Damaged settings turn sharing off instead of on.
        File.WriteAllText(Path.Combine(folder, "hub-sharing.tsv"), "damaged");
        var damaged = new HubSharingSettings(folder);
        Pass(damaged.ReadFailed && damaged.Current is { LiveActivity: false, RunUploads: false }, "Damaged sharing settings share nothing");
        foreach (var invalid in new[] { "{}", "[]", "{\"hubLiveActivityEnabled\":1}", "{\"unexpected\":true}", "{\"hubRunUploadsEnabled\":true,\"hubRunUploadsEnabled\":false}" })
        {
            try { damaged.ApplyJson(Encoding.UTF8.GetBytes(invalid)); throw new Exception("Accepted invalid sharing settings: " + invalid); }
            catch (JsonException) { }
        }
        Pass(HubSharingSettings.Decode(HubSharingSettings.Encode(new(true, false))) == new HubSharingValue(true, false), "Sharing settings round-trip");
    }

    static async Task Endpoints(string web)
    {
        Directory.CreateDirectory(web);
        var fake = new FakeHub();
        using var hub = new Hub(web, fake, openBrowser: _ => throw new InvalidOperationException("No browser in checks."));
        var store = new HubSharingSettings(web);
        await using var workspace = new WorkspaceHost(hub, web, hubSharing: store, hubSharingStatus: () => new { linked = false, live = new { state = "not-linked" }, uploads = new { state = "not-linked" } });
        await workspace.Start(CancellationToken.None);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var root = workspace.Url[..^3];
        using (var response = await http.GetAsync(root + "/hub-sharing"))
        {
            var body = await response.Content.ReadAsStringAsync();
            Pass(response.IsSuccessStatusCode && body.Contains("\"hubLiveActivityEnabled\":true") && body.Contains("\"state\":\"not-linked\""), "Sharing settings and status are served through the capability");
        }
        using (var response = await http.PostAsync(root + "/hub-sharing", new StringContent("{\"hubLiveActivityEnabled\":false}", Encoding.UTF8, "application/json")))
            Pass(response.StatusCode == HttpStatusCode.Forbidden && store.Current.LiveActivity, "Sharing changes require the UI header");
        using (var request = new HttpRequestMessage(HttpMethod.Post, root + "/hub-sharing"))
        {
            request.Headers.Add("X-AimMod-UI", "1"); request.Content = new StringContent("{\"hubLiveActivityEnabled\":false}", Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request);
            Pass(response.IsSuccessStatusCode && !store.Current.LiveActivity, "Sharing change persists through the endpoint");
        }
        using (var response = await http.GetAsync(root + "/hub-sharing.js"))
            Pass(response.IsSuccessStatusCode && (await response.Content.ReadAsStringAsync()).Contains("AimModHubSharing"), "Sharing card script is embedded");
        Pass(fake.Requests.Count == 0, "Serving the workspace sends nothing to the Hub");
    }

    static Run Native(string id, string scenario, double score, DateTimeOffset at) =>
        new("native:" + id, scenario, score, 80, 60, 12, 340, at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), null, null, null, null, false);

    static async Task Uploads(string root)
    {
        var clock = new Clock();
        var fake = new FakeHub();
        var folder = Path.Combine(root, "service");
        var stats = Path.Combine(root, "stats"); Directory.CreateDirectory(stats);
        using var hub = Linked(folder, fake, clock);
        var sharing = new HubSharingSettings(folder);
        var none = new HashSet<string>();
        var uploads = new HubRunUploads(hub, sharing, folder, () => stats, () => clock.Now);
        Pass(HubRunUploads.CompanionSessionId("Synthetic Track Small", "2026.01.01-12.00.30") == "synthetic_track_small-2026.01.01-12.00.30", "Companion session id format");
        Pass(!HubRunUploads.Eligible(Native("a", Multiplayer.MatchScenario.Prefix + "Synthetic - 0123abcd", 10, clock.Now)) && !HubRunUploads.Eligible(Native("b", "AimMod Probe", 10, clock.Now)), "AimMod arenas are never uploaded");

        var before = Native("before", "Synthetic Track", 100, clock.Now.AddMinutes(-5));
        await uploads.Tick([before], none, none, default);
        clock.Now = clock.Now.AddMinutes(1); await uploads.Tick([before], none, none, default);
        Pass(fake.Requests.Count == 0, "Runs completed before linking are not uploaded");

        var completed = clock.Now.AddSeconds(-5);
        var run = Native("attempt-1", "Synthetic Track", 512.5, completed);
        await uploads.Tick([before, run], none, none, default);
        Pass(fake.Requests.Count == 0, "A fresh run waits for KovaaK's stats file");
        var stamp = completed.AddSeconds(1).ToLocalTime().ToString("yyyy.MM.dd-HH.mm.ss", CultureInfo.InvariantCulture);
        var csv = Path.Combine(stats, $"Synthetic Track - Challenge - {stamp} Stats.csv");
        var start = completed.AddSeconds(1).ToLocalTime().AddSeconds(-60).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        File.WriteAllText(csv, $"Kills:,12\nScore:,512.5\nHit Count:,30\nMiss Count:,10\nDamage Done:,340\nChallenge Start:,{start}.000\n");
        File.SetLastWriteTimeUtc(csv, completed.UtcDateTime);
        clock.Now = clock.Now.AddSeconds(30); await uploads.Tick([before, run], none, none, default);
        var ingest = fake.Requests.Single();
        Pass(ingest is { Method: "POST", Path: "/aimmod.hub.v1.HubService/IngestSession", Auth: "Bearer synthetic-upload-token", Connect: "1" }, "Run uploads use IngestSession as the linked account");
        using (var doc = JsonDocument.Parse(ingest.Body))
        {
            var r = doc.RootElement;
            Pass(r.GetProperty("sessionId").GetString() == HubRunUploads.CompanionSessionId("Synthetic Track", stamp), "Run with a stats file uses the companion app's session id");
            Pass(r.GetProperty("scenarioName").GetString() == "Synthetic Track" && r.GetProperty("score").GetDouble() == 512.5 && r.GetProperty("accuracy").GetDouble() == 80
                && r.GetProperty("durationMs").GetInt64() == 60000 && r.GetProperty("userExternalId").GetString() == "synthetic-user" && r.GetProperty("schemaVersion").GetInt32() == 11
                && r.GetProperty("appVersion").GetString()!.StartsWith("in-game/", StringComparison.Ordinal), "IngestSession fields match the Hub's request");
            var summary = r.GetProperty("summary");
            Pass(summary.GetProperty("csvAccuracy").GetProperty("numberValue").GetDouble() == 75 && summary.GetProperty("client").GetProperty("stringValue").GetString() == "in-game"
                && summary.GetProperty("kills").GetProperty("numberValue").GetDouble() == 12, "Summary values use the Hub's oneof encoding");
            Pass(DateTimeOffset.Parse(r.GetProperty("playedAtIso").GetString()!, CultureInfo.InvariantCulture) == HubHistory.Date(stamp), "Played-at time is the stats file's end time in UTC");
            Pass(!r.TryGetProperty("scenarioType", out _), "No guessed scenario type: the Hub classifies it");
        }
        clock.Now = clock.Now.AddMinutes(1); await uploads.Tick([before, run], none, none, default);
        Pass(fake.Requests.Count == 1, "An uploaded run is not sent again");
        using (var reread = new Hub(folder, fake, clock, _ => { }, () => false))
        {
            var again = new HubRunUploads(reread, sharing, folder, () => stats, () => clock.Now);
            await again.Tick([before, run], none, none, default);
            Pass(fake.Requests.Count == 1, "Upload state survives a restart");
        }

        // The companion app's own record, or a run already on Hub, is left alone.
        var second = Native("attempt-2", "Synthetic Track", 600, clock.Now.AddSeconds(-30));
        var secondStamp = clock.Now.AddSeconds(-29).ToLocalTime().ToString("yyyy.MM.dd-HH.mm.ss", CultureInfo.InvariantCulture);
        var secondCsv = Path.Combine(stats, $"Synthetic Track - Challenge - {secondStamp} Stats.csv");
        File.WriteAllText(secondCsv, $"Score:,600\nHit Count:,1\nMiss Count:,1\nChallenge Start:,{clock.Now.AddSeconds(-89).ToLocalTime():HH:mm:ss}\n");
        File.SetLastWriteTimeUtc(secondCsv, clock.Now.UtcDateTime);
        var companionId = HubRunUploads.CompanionSessionId("Synthetic Track", secondStamp);
        await uploads.Tick([run, second], new HashSet<string> { companionId }, none, default);
        Pass(fake.Requests.Count == 1, "A run the companion app recorded is left to the companion app");
        var third = Native("attempt-3", "Synthetic Track", 700, clock.Now.AddSeconds(-25));
        var thirdStamp = clock.Now.AddSeconds(-24).ToLocalTime().ToString("yyyy.MM.dd-HH.mm.ss", CultureInfo.InvariantCulture);
        var thirdCsv = Path.Combine(stats, $"Synthetic Track - Challenge - {thirdStamp} Stats.csv");
        File.WriteAllText(thirdCsv, $"Score:,700\nHit Count:,1\nMiss Count:,1\nChallenge Start:,{clock.Now.AddSeconds(-84).ToLocalTime():HH:mm:ss}\n");
        File.SetLastWriteTimeUtc(thirdCsv, clock.Now.UtcDateTime);
        var onHub = new HashSet<string> { HubHistory.PublicId("synthetic-user", HubRunUploads.CompanionSessionId("Synthetic Track", thirdStamp)) };
        clock.Now = clock.Now.AddSeconds(4); await uploads.Tick([run, second, third], none, onHub, default);
        Pass(fake.Requests.Count == 1, "A run already on Hub is not uploaded again");

        // Without a stats file the run is sent under its own id after a wait; arenas never.
        var arena = Native("arena", Multiplayer.MatchScenario.Prefix + "Synthetic Track - 0123abcd", 50, clock.Now.AddSeconds(-30));
        var lonely = Native("attempt-4", "Synthetic Flick", 42, clock.Now.AddSeconds(-30));
        clock.Now = clock.Now.AddSeconds(4); await uploads.Tick([arena, lonely], none, none, default);
        Pass(fake.Requests.Count == 1, "Without a stats file the upload waits");
        clock.Now = clock.Now.AddMinutes(3); await uploads.Tick([arena, lonely], none, none, default);
        Pass(fake.Requests.Count == 2 && JsonDocument.Parse(fake.Requests[^1].Body).RootElement.GetProperty("sessionId").GetString() == "aimmod-attempt-4", "No stats file: the run uploads under its own id");
        clock.Now = clock.Now.AddSeconds(4); await uploads.Tick([arena, lonely], none, none, default);
        Pass(fake.Requests.Count == 2, "AimMod arena runs are skipped");

        // Failures: retried with backoff; a refused run is dropped; a refused token stops uploads.
        fake.Status = HttpStatusCode.InternalServerError;
        var flaky = Native("attempt-5", "Synthetic Flick", 43, clock.Now.AddMinutes(-4));
        await uploads.Tick([flaky], none, none, default);
        var failedAt = fake.Requests.Count;
        clock.Now = clock.Now.AddSeconds(5); await uploads.Tick([flaky], none, none, default);
        Pass(fake.Requests.Count == failedAt && uploads.Status == "unavailable", "A failed upload backs off");
        fake.Status = HttpStatusCode.OK;
        clock.Now = clock.Now.AddSeconds(10); await uploads.Tick([flaky], none, none, default);
        Pass(fake.Requests.Count == failedAt + 1 && uploads.Status == "idle", "The upload is retried after the backoff");
        fake.Status = HttpStatusCode.BadRequest;
        var invalid = Native("attempt-6", "Synthetic Flick", 44, clock.Now.AddMinutes(-4));
        clock.Now = clock.Now.AddSeconds(4); await uploads.Tick([invalid], none, none, default);
        clock.Now = clock.Now.AddSeconds(4); await uploads.Tick([invalid], none, none, default);
        Pass(fake.Requests.Count == failedAt + 2, "A run the Hub refuses is not retried");
        fake.Status = HttpStatusCode.Unauthorized;
        var refused = Native("attempt-7", "Synthetic Flick", 45, clock.Now.AddMinutes(-4));
        clock.Now = clock.Now.AddSeconds(4); await uploads.Tick([refused], none, none, default);
        clock.Now = clock.Now.AddMinutes(10); await uploads.Tick([refused], none, none, default);
        Pass(fake.Requests.Count == failedAt + 3 && uploads.Status == "rejected", "A refused upload token stops uploads");

        // Turned off: nothing is uploaded.
        fake.Status = HttpStatusCode.OK;
        var offFolder = Path.Combine(root, "off");
        using var offHub = Linked(offFolder, fake, clock);
        var offSharing = new HubSharingSettings(offFolder);
        offSharing.ApplyJson(Encoding.UTF8.GetBytes("{\"hubRunUploadsEnabled\":false}"));
        var off = new HubRunUploads(offHub, offSharing, offFolder, () => null, () => clock.Now);
        var offRun = Native("attempt-8", "Synthetic Flick", 46, clock.Now.AddMinutes(1));
        clock.Now = clock.Now.AddMinutes(10); await off.Tick([offRun], none, none, default);
        Pass(fake.Requests.Count == failedAt + 3 && off.Status == "off", "Upload my runs off sends nothing");
    }
}

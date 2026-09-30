using System.Net;
using System.Text;
using System.Text.Json;

namespace AimMod.InGame;

static class HubChecks
{
    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    sealed class FixtureHandler : HttpMessageHandler
    {
        public readonly List<string> Paths = [];
        public bool Offline;
        public bool RateLimitHistoryOnce;
        public bool WrongIdentity;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline) throw new HttpRequestException("Offline fixture");
            var path = request.RequestUri!.AbsolutePath; Paths.Add(path);
            if (RateLimitHistoryOnce && path.EndsWith("GetPlayerScenarioHistory"))
            {
                RateLimitHistoryOnce = false;
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
                return Task.FromResult(limited);
            }
            var response = path switch
            {
                "/auth/device/start" => """{"deviceCode":"private-fixture","userCode":"TEST-CODE","verificationUriComplete":"https://aimmod.app/link-device?code=TEST-CODE","expiresIn":600,"interval":3}""",
                "/auth/device/poll" => """{"status":"approved","uploadToken":"synthetic-secret-token","user":{"profileHandle":"synthetic","displayName":"Synthetic","userExternalId":"synthetic-user"}}""",
                "/aimmod.hub.v1.HubService/GetProfile" => """{"userHandle":"synthetic","userExternalId":"synthetic-user","runCount":1,"scenarioCount":1,"topScenarios":[{"scenarioSlug":"scenario","runCount":1}],"benchmarks":[{"benchmarkName":"Synthetic benchmark","overallRank":{"rankName":"Gold"}}]}""",
                "/aimmod.hub.v1.HubService/GetPlayerScenarioHistory" => """{"runs":[{"runId":"remote-only","userHandle":"synthetic","scenarioName":"Scenario","score":42,"accuracy":80,"durationMs":"60000","playedAtIso":"2026-01-01T00:00:00Z"},{"runId":"foreign","userHandle":"someone-else","scenarioName":"Scenario","score":999,"durationMs":"60000","playedAtIso":"2026-01-01T00:00:00Z"}]}""",
                _ => throw new Exception("Unexpected Hub endpoint: " + path)
            };
            if (WrongIdentity && path.EndsWith("GetProfile")) response = response.Replace("synthetic-user", "different-user");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") });
        }
    }
    public static async Task Run()
    {
        static void Check(bool value, string name) { if (!value) throw new Exception(name); }
        Check(HubHistory.PublicId(" Synthetic-User ", "local-1") == "run_99c5e08af66d79285e36ac1ebe043b84", "Hub identity golden vector");
        Check(HubHistory.ScenarioSlug("  1wall_6targets...small's! ") == "1wall-6targets-small-s", "Match Hub scenario slug algorithm");
        var local = new Run("local-1", "Scenario", 10, 90, 60, 1, 1, "2026-01-01", 75, 1, .8, 1, true);
        var remote = local with { Id = HubHistory.PublicId("synthetic-user", local.Id), Replay = false, Smoothness = null };
        var merged = HubHistory.Merge([local], [remote, remote with { Id = "other-run" }], "synthetic-user");
        Check(merged.Length == 2 && merged.Single(r => r.Id == "local-1").Replay, "Local replay wins without collapsing distinct equal scores");
        using var malformed = JsonDocument.Parse("""{"userHandle":"synthetic","runId":"x","scenarioName":"A","score":"NaN","durationMs":"60000","playedAtIso":"2026-01-01"}""");
        Check(HubHistory.Parse(malformed.RootElement, "synthetic") is null, "Reject invalid remote score");
        Check(!Hub.SafeApprovalUrl("https://aimmod.app.evil.test/link-device") && !Hub.SafeApprovalUrl("file:///link-device") && !Hub.SafeApprovalUrl("https://aimmod.app:8443/link-device"), "Constrain browser handoff");
        var folder = Path.Combine(Path.GetTempPath(), "aimmod-native-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var fixture = new FixtureHandler(); var clock = new Clock();
            var opened = new List<string>();
            using (var hub = new Hub(folder, fixture, clock, opened.Add))
            {
                await hub.Tick(default); Check(fixture.Paths.Count == 0, "No implicit link requests");
                File.WriteAllText(Path.Combine(folder, "command.tsv"), "link"); await hub.Tick(default);
                Check(opened.SequenceEqual(["https://aimmod.app/link-device?code=TEST-CODE"]), "Link automatically opens complete approval URL");
                Check(!Views.Encode(hub.Rows()).Contains("private-fixture"), "Device secret excluded from game protocol");
                Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(folder, "pending-link.bin"))).Contains("private-fixture"), "Pending link encrypted at rest");
                await hub.Tick(default); Check(fixture.Paths.Count == 1, "Respect polling interval");
            }
            fixture = new FixtureHandler();
            using (var hub = new Hub(folder, fixture, clock, opened.Add))
            {
                await hub.Tick(default); Check(fixture.Paths.Count == 0 && opened.Count == 1, "Restart resumes pending link without reopening browser or early polling");
                clock.Now = clock.Now.AddSeconds(4); await hub.Tick(default);
                Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(folder, "account.bin"))).Contains("synthetic-secret-token"), "Credential encrypted at rest");
                await hub.Tick(default);
                fixture.RateLimitHistoryOnce = true; await hub.Tick(default);
                var requests = fixture.Paths.Count;
                clock.Now = clock.Now.AddMinutes(3); await hub.Tick(default);
                Check(fixture.Paths.Count == requests, "Honor Hub Retry-After without losing pending history");
                clock.Now = clock.Now.AddMinutes(3); await hub.Tick(default);
                Check(hub.Runs.Count == 1 && hub.Runs.Single().Score == 42 && hub.Runs.Single().Duration == 60, "Download previews with uint64 strings and reject foreign profile");
                Check(hub.Rows().Any(r => r.Page == "Benchmarks" && r.Body == "Gold"), "Read benchmark rank");
                Check(fixture.Paths.All(p => !p.Contains("Ingest") && !p.Contains("upload")), "History flow never uploads scores");
                Check(!File.Exists(Path.Combine(folder, "pending-link.bin")), "Approval removes pending device secret");
                clock.Now = clock.Now.AddMinutes(11); await hub.Tick(default); await hub.Tick(default);
                Check(fixture.Paths.Count(p => p.EndsWith("GetPlayerScenarioHistory")) == 2, "Unchanged scenario count avoids repeated full history requests");
                fixture.WrongIdentity = true; hub.Enqueue("refresh-hub"); await hub.Tick(default);
                Check(hub.ExternalId == "synthetic-user" && hub.Runs.Count == 1, "Reject changed profile identity without mixing cached history");
            }
            var historyAllowed = false; var privacyFixture = new FixtureHandler();
            using (var pausedHistory = new Hub(folder, privacyFixture, clock, historyEnabled: () => historyAllowed))
            {
                await pausedHistory.Tick(default);
                Check(privacyFixture.Paths.Count == 0 && pausedHistory.Runs.Count == 1, "Disabled Hub history sends no request and retains downloaded local cache");
                pausedHistory.Enqueue("refresh-hub"); await pausedHistory.Tick(default);
                Check(privacyFixture.Paths.Count == 0, "Manual refresh cannot bypass download privacy setting");
                historyAllowed = true; await pausedHistory.Tick(default);
                Check(privacyFixture.Paths.Any(p => p.EndsWith("GetProfile")), "Re-enabling history resumes remote refresh");
                var countBeforePause = privacyFixture.Paths.Count; historyAllowed = false;
                clock.Now = clock.Now.AddHours(1); await pausedHistory.Tick(default);
                Check(privacyFixture.Paths.Count == countBeforePause, "Automatic refresh stays stopped after privacy change");
            }
            using (var offline = new Hub(folder, new FixtureHandler { Offline = true }, clock))
            {
                Check(offline.Runs.Count == 1, "Restore account-scoped cache");
                await offline.Tick(default); Check(offline.Runs.Count == 1, "Network failure preserves offline history");
                File.WriteAllText(Path.Combine(folder, "command.tsv"), "unlink"); await offline.Tick(default);
                Check(offline.Runs.Count == 0 && !File.Exists(Path.Combine(folder, "account.bin")) && !File.Exists(Path.Combine(folder, "hub-cache.json")), "Unlink clears private account cache");
            }
            Console.WriteLine("26 Hub integration checks passed.");
        }
        finally { Directory.Delete(folder, true); }
    }
}

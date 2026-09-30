using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AimMod.Osu.Runtime;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class PublicBeatmapDiscoveryClientTests
{
    private const string payload = """
        {
          "providers": [{"provider":"PROVIDER_OSU_OFFICIAL","available":true}],
          "items": [{"provider":"PROVIDER_OSU_OFFICIAL","sourceId":"123","title":"Fixture",
            "artist":"Artist","creator":"Mapper","status":"ranked","playCount":12345,"favouriteCount":100,
            "coverUrl":"https://assets.example/cover.jpg","updatedAtIso":"2026-01-01T00:00:00Z",
            "difficulties":[
              {"beatmapId":"456","name":"Hard","ruleset":"RULESET_OSU","stars":4.5,"bpm":180,
                "lengthSeconds":120,"circleSize":4,"approachRate":9,"overallDifficulty":8,"drainRate":6},
              {"beatmapId":"457","name":"Expert","ruleset":"RULESET_OSU","stars":6.5},
              {"beatmapId":"458","name":"Taiko","ruleset":"RULESET_TAIKO","stars":4.5}]}],
          "nextPageTokens":[{"provider":"PROVIDER_OSU_OFFICIAL","pageToken":"next-page"}]
        }
        """;

    [Test]
    public async Task StableOnlySearchUsesPublicCatalogAndPreservesCalculationInputs()
    {
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            Assert.That(request.RequestUri!.AbsoluteUri, Is.EqualTo("https://hub.example/aimmod.osu.v1.OsuService/SearchBeatmapItems"));
            Assert.That(request.Headers.Authorization, Is.Null);
            Assert.That(request.Headers.GetValues("Connect-Protocol-Version"), Is.EqualTo(new[] { "1" }));
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            Assert.That(body["filters"]!["ruleset"]!.GetValue<string>(), Is.EqualTo("RULESET_OSU"));
            Assert.That(body["filters"]!["status"]!.GetValue<string>(), Is.EqualTo("ranked"));
            Assert.That(body["filters"]!["stars"]!["minimum"]!.GetValue<double>(), Is.EqualTo(4));
            Assert.That(body["sort"]!.GetValue<string>(), Is.EqualTo("favourites_desc"));
            Assert.That(body["pageTokens"]!.AsArray(), Is.Empty);
            return response(payload);
        }));
        using var client = create(http);
        var result = await client.SearchAsync(new(MinimumStars: 4, MaximumStars: 5,
            Category: OfficialBeatmapCategory.Ranked, Sort: OfficialBeatmapSort.Rating));
        var set = result.BeatmapSets.Single();
        var difficulty = set.Difficulties.Single();
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(OfficialBeatmapRequestStatus.Success));
            Assert.That(result.NextCursor, Is.EqualTo("hub:next-page"));
            Assert.That(set.BeatmapSetId, Is.EqualTo(123));
            Assert.That(set.PlayCount, Is.EqualTo(12345));
            Assert.That(difficulty, Is.EqualTo(new OfficialBeatmapDifficulty(456, "Hard", "osu", 4.5, 180, 120, 4, 9, 8, 6, 0, 0, null)));
        });
    }

    [TestCase(OfficialBeatmapRequestStatus.SignedOut)]
    [TestCase(OfficialBeatmapRequestStatus.TokenExpired)]
    [TestCase(OfficialBeatmapRequestStatus.SessionUnavailable)]
    [TestCase(OfficialBeatmapRequestStatus.SessionChanged)]
    [TestCase(OfficialBeatmapRequestStatus.Unauthorized)]
    public async Task UnusableLazerSessionFallsBackWithoutSendingItsCursor(OfficialBeatmapRequestStatus status)
    {
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            Assert.That(body["pageTokens"]!.AsArray(), Is.Empty);
            return response(payload);
        }));
        var official = new StubClient(status);
        using var client = create(http, () => official);
        Assert.That((await client.SearchAsync(new(Cursor: "official-cursor"))).Status, Is.EqualTo(OfficialBeatmapRequestStatus.Success));
        Assert.That(official.Calls, Is.EqualTo(1));
    }

    [TestCase(OfficialBeatmapRequestStatus.Success)]
    [TestCase(OfficialBeatmapRequestStatus.NetworkError)]
    [TestCase(OfficialBeatmapRequestStatus.RateLimited)]
    public async Task UsableSessionOrTransientFailureDoesNotMakeExtraPublicRequests(OfficialBeatmapRequestStatus status)
    {
        using var http = new HttpClient(new Handler((_, _) => throw new AssertionException("Unexpected fallback request.")));
        using var client = create(http, () => new StubClient(status));
        Assert.That((await client.SearchAsync(new())).Status, Is.EqualTo(status));
    }

    [Test]
    public async Task PublicPaginationRemainsPublicIfLazerSignsIn()
    {
        var official = new StubClient(OfficialBeatmapRequestStatus.Success);
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            Assert.That(body["pageTokens"]![0]!["pageToken"]!.GetValue<string>(), Is.EqualTo("next-page"));
            return response(payload);
        }));
        using var client = create(http, () => official);
        Assert.That((await client.SearchAsync(new(Cursor: "hub:next-page"))).BeatmapSets, Has.Count.EqualTo(1));
        Assert.That(official.Calls, Is.Zero);
    }

    [TestCase("{}", OfficialBeatmapRequestStatus.InvalidResponse)]
    [TestCase("{\"providers\":[{\"provider\":\"PROVIDER_OSU_OFFICIAL\",\"available\":false}]}", OfficialBeatmapRequestStatus.ServerError)]
    [TestCase("{\"providers\":[{\"provider\":\"PROVIDER_OSU_OFFICIAL\",\"available\":true}]}", OfficialBeatmapRequestStatus.Success)]
    public async Task UnavailableProviderIsNotMistakenForAnEmptySuccessfulCatalog(string json, OfficialBeatmapRequestStatus expected)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(response(json))));
        using var client = create(http);
        Assert.That((await client.SearchAsync(new())).Status, Is.EqualTo(expected));
    }

    [Test]
    public async Task RateLimitingAndCallerCancellationArePreserved()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests))));
        using var client = create(http);
        Assert.That((await client.SearchAsync(new())).Status, Is.EqualTo(OfficialBeatmapRequestStatus.RateLimited));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(async () => await client.SearchAsync(new(), cancelled.Token));
    }

    [Test]
    public async Task SetLookupValidatesIdentityAndUsesFallback()
    {
        var item = JsonNode.Parse(payload)!["items"]![0]!.DeepClone();
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(response(new JsonObject { ["item"] = item.DeepClone() }.ToJsonString()))));
        var official = new StubClient(OfficialBeatmapRequestStatus.TokenExpired);
        using var client = create(http, () => official);
        Assert.That((await client.GetSetAsync(123)).BeatmapSets.Single().BeatmapSetId, Is.EqualTo(123));
        Assert.That((await client.GetSetAsync(999)).Status, Is.EqualTo(OfficialBeatmapRequestStatus.InvalidResponse));
    }

    [Test]
    public async Task PublicPagesAreReusedByExistingDiskCache()
    {
        string directory = Path.Combine(Path.GetTempPath(), "aimmod-public-catalog-test-" + Guid.NewGuid().ToString("N"));
        int calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(response(payload)); }));
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var cached = new CachedOfficialBeatmapDiscoveryClient(create(http), Path.Combine(directory, "catalog.json"));
                Assert.That((await cached.SearchAsync(new())).BeatmapSets, Has.Count.EqualTo(1));
            }
            Assert.That(calls, Is.EqualTo(1));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static PublicBeatmapDiscoveryClient create(HttpClient http, Func<IOfficialBeatmapDiscoveryClient?>? authenticated = null) =>
        new(http, new Uri("https://hub.example"), authenticated ?? (() => null));
    private static HttpResponseMessage response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
    private sealed class StubClient(OfficialBeatmapRequestStatus status) : IOfficialBeatmapDiscoveryClient
    {
        public int Calls { get; private set; }
        public Task<OfficialBeatmapSearchResult> SearchAsync(OfficialBeatmapSearchQuery query, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(OfficialBeatmapSearchResult.Empty(status)); }
        public Task<OfficialBeatmapSearchResult> GetSetAsync(int beatmapSetId, CancellationToken cancellationToken = default) => Task.FromResult(OfficialBeatmapSearchResult.Empty(status));
        public Task<OfficialBeatmapDownloadResult> DownloadAsync(int beatmapSetId, string destinationDirectory, bool noVideo = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

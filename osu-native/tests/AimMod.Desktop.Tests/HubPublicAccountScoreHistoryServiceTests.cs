using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AimMod.Desktop.ScoreHistory;
using AimMod.Osu.Runtime;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class HubPublicAccountScoreHistoryServiceTests
{
    private const string payload = """
        {
          "profile": { "osuUserId": 42, "osuUsername": "Synthetic Player", "globalRank": 123,
            "performancePoints": 456.7, "countryCode": "NL", "playCount": 50, "playTimeSeconds": 1200 },
          "items": [ { "onlineScoreId": 9001, "osuUserId": 42, "beatmapId": 12, "beatmapSetId": 34,
            "source": "official", "ruleset": "osu", "title": "Fixture", "artist": "Artist", "difficulty": "Hard",
            "playedAt": "2026-01-01T00:00:00Z", "starRating": 4.2, "accuracy": 0.98, "performancePoints": 123.4,
            "totalScore": 123456, "maxCombo": 300, "countMiss": 2, "mods": ["HD", "DT"],
            "passed": true, "bpm": 180, "lengthMs": 123000, "officialReplayExists": true } ],
          "coverage": { "best": { "status": "available", "fetched": 100 },
            "recent": { "status": "page_limit", "hasMore": true }, "completeHistory": false }, "hasMore": true
        }
        """;

    [Test]
    public async Task StableScoringIdentitySurvivesPublicHistoryAndCoachingConversion()
    {
        var json = JsonNode.Parse(payload)!;
        json["items"]![0]!["ppCalculation"] = new JsonObject { ["lazer"] = false };
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(response(json.ToJsonString()))));
        var result = await create(client).FetchAccountAsync();
        var run = ScoreHistoryMerger.MergeAsLocalReplays([], result.Scores).Single();
        Assert.That(run.LegacyScore, Is.True);
        Assert.That(run.IsLocallyStored, Is.False);
        Assert.That(AimMod.Desktop.Practice.PracticeProgressTracker.Stable(run), Is.True);
    }

    [Test]
    public async Task MapsPublicProfileAndScoresWithoutClaimingLocalReplayOrCompleteHistory()
    {
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Assert.That(request.RequestUri!.AbsoluteUri, Is.EqualTo("https://hub.example/prefix/api/osu/v1/profile-scores/42?mode=osu&limit=100"));
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Get));
            Assert.That(request.Headers.Authorization, Is.Null);
            return Task.FromResult(response(payload));
        }));
        var service = create(client);
        var result = await service.FetchAccountAsync();
        var score = result.Scores.Single();
        Assert.Multiple(() =>
        {
            Assert.That(result.Profile!.UserId, Is.EqualTo(42));
            Assert.That(result.Profile.Statistics!.GlobalRank, Is.EqualTo(123));
            Assert.That(score.OnlineScoreId, Is.EqualTo(9001));
            Assert.That(score.PerformancePoints, Is.EqualTo(123.4));
            Assert.That(score.Accuracy, Is.EqualTo(0.98));
            Assert.That(score.LengthSeconds, Is.EqualTo(123));
            Assert.That(score.Mods, Is.EqualTo(new[] { "HD", "DT" }));
            Assert.That(score.Provenance, Is.EqualTo(ScoreHistoryProvenance.OnlinePublic));
            Assert.That(score.HasReplay, Is.False);
            Assert.That(score.IsLocal, Is.False);
            Assert.That(result.BestCoverage.IsSuccess, Is.True);
            Assert.That(result.RecentCoverage.IsSuccess, Is.True);
            Assert.That(result.BestCoverage.IsExhaustive, Is.False);
        });
        var beatmap = await service.FetchBeatmapAsync(12);
        Assert.That(beatmap.Scores, Has.Count.EqualTo(1));
        Assert.That(beatmap.Coverage.IsExhaustive, Is.False);
        Assert.That(beatmap.Coverage.IsFromCache, Is.True);
        Assert.That((await service.FetchBeatmapAsync(99)).Scores, Is.Empty);
    }

    [Test]
    public async Task ConcurrentRequestsShareAnInMemoryCache()
    {
        int calls = 0;
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(20, token);
            return response(payload);
        }));
        var service = create(client);
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => service.FetchAccountAsync()));
        Assert.That(calls, Is.EqualTo(1));
        Assert.That(results.Count(result => result.BestCoverage.IsFromCache), Is.EqualTo(4));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    [TestCase("bad/name")]
    [TestCase("bad\\name")]
    [TestCase("bad\0name")]
    public async Task MissingOrUnsafeUsernameDoesNotUseNetwork(string? username)
    {
        using var client = new HttpClient(new Handler((_, _) => throw new AssertionException("Unexpected network request.")));
        var service = new HubPublicAccountScoreHistoryService(client, new Uri("https://hub.example"), username);
        var result = await service.FetchAccountAsync();
        Assert.That(result.Profile, Is.Null);
        Assert.That(result.Scores, Is.Empty);
        Assert.That(result.BestCoverage.IsSuccess, Is.False);
    }

    [TestCase(HttpStatusCode.NotFound, OsuBestScoresFetchStatus.SessionUnavailable)]
    [TestCase(HttpStatusCode.BadGateway, OsuBestScoresFetchStatus.ServerError)]
    [TestCase(HttpStatusCode.TooManyRequests, OsuBestScoresFetchStatus.ServerError)]
    public async Task HttpFailuresAreNegativeCached(HttpStatusCode code, OsuBestScoresFetchStatus expected)
    {
        int calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(code));
        }));
        var service = create(client);
        Assert.That((await service.FetchAccountAsync()).BestCoverage.Status, Is.EqualTo(expected));
        Assert.That((await service.FetchAccountAsync()).BestCoverage.IsFromCache, Is.True);
        Assert.That(calls, Is.EqualTo(1));
    }

    [TestCase("not json")]
    [TestCase("null")]
    [TestCase("{}")]
    public async Task InvalidPayloadReturnsUnavailable(string content)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(response(content))));
        var result = await create(client).FetchAccountAsync();
        Assert.That(result.Profile, Is.Null);
        Assert.That(result.BestCoverage.Status, Is.EqualTo(OsuBestScoresFetchStatus.InvalidResponse));
    }

    [Test]
    public async Task RejectsResponseForDifferentUserId()
    {
        var content = JsonNode.Parse(payload)!;
        content["profile"]!["osuUserId"] = 99;
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(response(content.ToJsonString()))));
        var result = await create(client).FetchAccountAsync();
        Assert.That(result.Profile, Is.Null);
        Assert.That(result.Scores, Is.Empty);
    }

    [Test]
    public async Task KnownPresenceIdAcceptsCanonicalRenameWithoutUsernameLookup()
    {
        var content = JsonNode.Parse(payload)!;
        content["profile"]!["osuUsername"] = "Renamed Player";
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Get));
            Assert.That(request.RequestUri!.AbsolutePath, Does.EndWith("/profile-scores/42"));
            return Task.FromResult(response(content.ToJsonString()));
        }));
        var result = await create(client).FetchAccountAsync();
        Assert.That(result.Profile!.Username, Is.EqualTo("Renamed Player"));
        Assert.That(result.Profile.UserId, Is.EqualTo(42));
    }

    [TestCase("\"2\"", 2)]
    [TestCase("\"42\"", 42)]
    [TestCase("42", 42)]
    public async Task ResolvesUsernameThroughOfficialPublicRpcBeforeNumericScoresWithoutHubMembership(string encodedId, int userId)
    {
        int calls = 0;
        var scoresPayload = JsonNode.Parse(payload)!;
        scoresPayload["profile"]!["osuUserId"] = userId;
        scoresPayload["items"]![0]!["osuUserId"] = userId;
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            calls++;
            Assert.That(request.Headers.Authorization, Is.Null);
            if (request.Method == HttpMethod.Post)
            {
                Assert.That(request.RequestUri!.AbsolutePath, Is.EqualTo("/aimmod.osu.v1.OsuService/GetOfficialUserProfile"));
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
                Assert.That(body["identifier"]!.GetValue<string>(), Is.EqualTo("Old Name"));
                Assert.That(body["lookupKey"]!.GetValue<string>(), Is.EqualTo("OFFICIAL_USER_LOOKUP_KEY_USERNAME"));
                return response(new JsonObject
                {
                    ["profile"] = new JsonObject
                    {
                        ["userId"] = JsonNode.Parse(encodedId),
                        ["username"] = "Synthetic Player",
                    },
                }.ToJsonString());
            }
            Assert.That(request.RequestUri!.AbsolutePath, Is.EqualTo($"/api/osu/v1/profile-scores/{userId}"));
            return response(scoresPayload.ToJsonString());
        }));
        var service = new HubPublicAccountScoreHistoryService(client, new Uri("https://hub.example"), "Old Name");
        var result = await service.FetchAccountAsync();
        Assert.That(result.Profile!.Username, Is.EqualTo("Synthetic Player"));
        Assert.That(result.Profile.UserId, Is.EqualTo(userId));
        Assert.That(result.Scores, Has.Count.EqualTo(1));
        await service.FetchAccountAsync();
        Assert.That(calls, Is.EqualTo(2));
    }

    [Test]
    public async Task UnresolvableUsernameStopsBeforeScoresAndIsNegativeCached()
    {
        int calls = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            calls++;
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));
        var service = new HubPublicAccountScoreHistoryService(client, new Uri("https://hub.example"), "Unknown Player");
        Assert.That((await service.FetchAccountAsync()).Profile, Is.Null);
        Assert.That((await service.FetchAccountAsync()).BestCoverage.IsFromCache, Is.True);
        Assert.That(calls, Is.EqualTo(1));
    }

    [TestCase("osuUserId", "99")]
    [TestCase("ruleset", "\"mania\"")]
    [TestCase("source", "\"local\"")]
    [TestCase("onlineScoreId", "0")]
    [TestCase("accuracy", "1.2")]
    public async Task ExcludesWrongOwnerModeAndUnsubmittedOrMalformedScores(string field, string value)
    {
        var content = JsonNode.Parse(payload)!;
        content["items"]![0]![field] = JsonNode.Parse(value);
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(response(content.ToJsonString()))));
        Assert.That((await create(client).FetchAccountAsync()).Scores, Is.Empty);
    }

    [Test]
    public async Task TimeoutReturnsPromptlyAndIsNegativeCached()
    {
        int calls = 0;
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            calls++;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return response(payload);
        }));
        var service = create(client, TimeSpan.FromMilliseconds(30));
        var result = await service.FetchAccountAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(result.BestCoverage.Status, Is.EqualTo(OsuBestScoresFetchStatus.NetworkError));
        Assert.That((await service.FetchAccountAsync()).BestCoverage.IsFromCache, Is.True);
        Assert.That(calls, Is.EqualTo(1));
    }

    [Test]
    public void CallerCancellationIsPreserved()
    {
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return response(payload);
        }));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        Assert.ThrowsAsync<TaskCanceledException>(async () => await create(client).FetchAccountAsync(cancellation.Token));
    }

    [Test]
    public async Task OversizedResponseIsRejected()
    {
        using var client = new HttpClient(new Handler((_, _) =>
        {
            var result = response(payload);
            result.Content.Headers.ContentLength = 4 * 1024 * 1024 + 1;
            return Task.FromResult(result);
        }));
        Assert.That((await create(client).FetchAccountAsync()).BestCoverage.Status, Is.EqualTo(OsuBestScoresFetchStatus.InvalidResponse));
    }

    private static HubPublicAccountScoreHistoryService create(HttpClient client, TimeSpan? timeout = null) =>
        new(client, new Uri("https://hub.example/prefix/"), "Synthetic Player", timeout, userId: 42);

    private static HttpResponseMessage response(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json"),
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace AimMod.Osu.Runtime.Tests;

[TestFixture]
public sealed class OfficialOsuApiClientTests
{
    private const string access_token = "private-access-token";
    private string temporaryDirectory = null!;
    private string gameIniPath = null!;

    [SetUp]
    public void SetUp()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"aimmod-api-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        gameIniPath = Path.Combine(temporaryDirectory, "game.ini");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(temporaryDirectory))
            Directory.Delete(temporaryDirectory, true);
    }

    [Test]
    public async Task FetchesStandardProfileFromFixedOfficialEndpoint()
    {
        await writeSignedInSessionAsync("crunchy", access_token);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        var handler = new RecordingHandler(_ => jsonResponse(HttpStatusCode.OK, validProfileJson("crunchy")));
        using var client = new OfficialOsuApiClient(monitor, handler);

        OsuProfileFetchResult result = await client.FetchCurrentProfileAsync();

        Assert.Multiple(() =>
        {
            Assert.That(handler.CallCount, Is.EqualTo(1));
            Assert.That(handler.RequestUri, Is.EqualTo(new Uri("https://osu.ppy.sh/api/v2/me/osu")));
            Assert.That(handler.Method, Is.EqualTo(HttpMethod.Get));
            Assert.That(handler.AuthorizationScheme, Is.EqualTo("Bearer"));
            Assert.That(handler.AuthorizationParameter, Is.EqualTo(access_token));
            Assert.That(result.Status, Is.EqualTo(OsuProfileFetchStatus.Success));
            Assert.That(result.Profile?.UserId, Is.EqualTo(42));
            Assert.That(result.Profile?.Username, Is.EqualTo("crunchy"));
            Assert.That(result.Profile?.Statistics?.PerformancePoints, Is.EqualTo(1234.5));
            Assert.That(result.Profile?.Statistics?.GlobalRank, Is.EqualTo(5678));
            Assert.That(JsonSerializer.Serialize(result), Does.Not.Contain(access_token));
        });
    }

    [TestCase("Username = crunchy\nToken =\n", OsuProfileFetchStatus.SignedOut)]
    [TestCase("Username = crunchy\nToken = expired|1|refresh\n", OsuProfileFetchStatus.TokenExpired)]
    public async Task DoesNotSendARequestWithoutAValidLease(string gameIni, OsuProfileFetchStatus expectedStatus)
    {
        await File.WriteAllTextAsync(gameIniPath, gameIni);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("A request should not be sent."));
        using var client = new OfficialOsuApiClient(monitor, handler);

        OsuProfileFetchResult result = await client.FetchCurrentProfileAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(expectedStatus));
            Assert.That(handler.CallCount, Is.Zero);
        });
    }

    [Test]
    public void ProductionHandlerDoesNotFollowRedirects()
    {
        using HttpClientHandler handler = OfficialOsuApiClient.CreateProductionHandler();

        Assert.That(handler.AllowAutoRedirect, Is.False);
    }

    [Test]
    public async Task RejectsRedirectWithoutFollowingIt()
    {
        await writeSignedInSessionAsync("crunchy", access_token);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://attacker.invalid/profile") },
        });
        using var client = new OfficialOsuApiClient(monitor, handler);

        OsuProfileFetchResult result = await client.FetchCurrentProfileAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(OsuProfileFetchStatus.InvalidResponse));
            Assert.That(handler.CallCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task StableUnauthorizedResponseDoesNotExposeCredential()
    {
        await writeSignedInSessionAsync("crunchy", access_token);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var client = new OfficialOsuApiClient(monitor, handler);

        OsuProfileFetchResult result = await client.FetchCurrentProfileAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(OsuProfileFetchStatus.Unauthorized));
            Assert.That(JsonSerializer.Serialize(result), Does.Not.Contain(access_token));
            Assert.That(result.ToString(), Does.Not.Contain(access_token));
        });
    }

    [Test]
    public async Task DiscardsUnauthorizedResponseWhenAccountChangesDuringRequest()
    {
        await writeSignedInSessionAsync("first", access_token);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        var handler = new RecordingHandler(_ =>
        {
            File.WriteAllText(gameIniPath, sessionContents("second", "second-private-token"));
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
        using var client = new OfficialOsuApiClient(monitor, handler);

        OsuProfileFetchResult result = await client.FetchCurrentProfileAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(OsuProfileFetchStatus.SessionChanged));
            Assert.That(result.Profile, Is.Null);
            Assert.That(monitor.Current.Username, Is.EqualTo("second"));
            Assert.That(JsonSerializer.Serialize(result), Does.Not.Contain(access_token));
            Assert.That(JsonSerializer.Serialize(result), Does.Not.Contain("second-private-token"));
        });
    }

    [Test]
    public async Task RejectsProfileForDifferentAccount()
    {
        await writeSignedInSessionAsync("crunchy", access_token);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        var handler = new RecordingHandler(_ => jsonResponse(HttpStatusCode.OK, validProfileJson("someone-else")));
        using var client = new OfficialOsuApiClient(monitor, handler);

        OsuProfileFetchResult result = await client.FetchCurrentProfileAsync();

        Assert.That(result.Status, Is.EqualTo(OsuProfileFetchStatus.InvalidResponse));
    }

    [Test]
    public async Task BoundsMalformedAndOversizedResponses()
    {
        await writeSignedInSessionAsync("crunchy", access_token);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        var malformedHandler = new RecordingHandler(_ => jsonResponse(HttpStatusCode.OK, "not-json"));
        using var malformedClient = new OfficialOsuApiClient(monitor, malformedHandler);

        OsuProfileFetchResult malformed = await malformedClient.FetchCurrentProfileAsync();

        var oversizedHandler = new RecordingHandler(_ => jsonResponse(HttpStatusCode.OK, new string('x', 1024 * 1024 + 1)));
        using var oversizedClient = new OfficialOsuApiClient(monitor, oversizedHandler);
        OsuProfileFetchResult oversized = await oversizedClient.FetchCurrentProfileAsync();

        Assert.Multiple(() =>
        {
            Assert.That(malformed.Status, Is.EqualTo(OsuProfileFetchStatus.InvalidResponse));
            Assert.That(oversized.Status, Is.EqualTo(OsuProfileFetchStatus.InvalidResponse));
        });
    }

    [Test]
    public async Task ConvertsTransportFailureToSafePublicStatus()
    {
        await writeSignedInSessionAsync("crunchy", access_token);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        var handler = new RecordingHandler(_ => throw new HttpRequestException($"transport failed with {access_token}"));
        using var client = new OfficialOsuApiClient(monitor, handler);

        OsuProfileFetchResult result = await client.FetchCurrentProfileAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(OsuProfileFetchStatus.NetworkError));
            Assert.That(JsonSerializer.Serialize(result), Does.Not.Contain(access_token));
            Assert.That(result.ToString(), Does.Not.Contain(access_token));
        });
    }

    [Test]
    public async Task FetchesAndParsesAllStandardBestScorePages()
    {
        await writeSignedInSessionAsync("crunchy", access_token);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        var handler = new RecordingHandler(request =>
            request.RequestUri!.Query.Contains("offset=0", StringComparison.Ordinal)
                ? jsonResponse(HttpStatusCode.OK, scorePageJson(42, 100))
                : jsonResponse(HttpStatusCode.OK, scorePageJson(42, 1, firstId: 101)));
        using var client = new OfficialOsuApiClient(monitor, handler, Path.Combine(temporaryDirectory, "cache"), TimeProvider.System);

        OsuBestScoresFetchResult result = await client.FetchBestScoresAsync(profile());

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(OsuBestScoresFetchStatus.Success));
            Assert.That(result.IsFromCache, Is.False);
            Assert.That(result.Scores, Has.Count.EqualTo(101));
            Assert.That(handler.Requests.Select(request => request.Uri?.Query), Is.EqualTo(new[]
            {
                "?mode=osu&limit=100&offset=0",
                "?mode=osu&limit=100&offset=100",
            }));
            Assert.That(handler.Requests.All(request => request.ApiVersion == "20220705"), Is.True);
            Assert.That(handler.Requests.All(request => request.AuthorizationParameter == access_token), Is.True);
        });

        OsuBestScore score = result.Scores![0];
        Assert.Multiple(() =>
        {
            Assert.That(score.ScoreId, Is.EqualTo(1));
            Assert.That(score.UserId, Is.EqualTo(42));
            Assert.That(score.Username, Is.EqualTo("crunchy"));
            Assert.That(score.PerformancePoints, Is.EqualTo(321.45));
            Assert.That(score.Accuracy, Is.EqualTo(0.9876));
            Assert.That(score.TotalScore, Is.EqualTo(987654));
            Assert.That(score.MaximumCombo, Is.EqualTo(777));
            Assert.That(score.Statistics, Is.EqualTo(new OsuScoreStatistics(2, 600, 20, 3)));
            Assert.That(score.Mods, Is.EqualTo(new[] { "HD", "DT" }));
            Assert.That(score.EndedAt, Is.EqualTo(DateTimeOffset.Parse("2026-08-01T12:34:56Z")));
            Assert.That(score.CreatedAt, Is.EqualTo(DateTimeOffset.Parse("2026-08-01T12:34:55Z")));
            Assert.That(score.Beatmap.BeatmapId, Is.EqualTo(1234));
            Assert.That(score.Beatmap.Checksum, Is.EqualTo("abc123"));
            Assert.That(score.Beatmap.StarRating, Is.EqualTo(6.42));
            Assert.That(score.BeatmapSet.BeatmapSetId, Is.EqualTo(567));
            Assert.That(score.BeatmapSet.Title, Is.EqualTo("Test Song"));
            Assert.That(score.BeatmapSet.CoverUrl, Is.EqualTo(new Uri("https://assets.ppy.sh/cover.jpg")));
        });
    }

    [Test]
    public async Task ReusesFreshPersistentCacheWithoutPersistingCredentials()
    {
        await writeSignedInSessionAsync("crunchy", access_token);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        string cacheDirectory = Path.Combine(temporaryDirectory, "cache");
        var firstHandler = new RecordingHandler(_ => jsonResponse(HttpStatusCode.OK, scorePageJson(42, 1)));
        using (var firstClient = new OfficialOsuApiClient(monitor, firstHandler, cacheDirectory, TimeProvider.System))
        {
            Assert.That((await firstClient.FetchBestScoresAsync(profile())).Status, Is.EqualTo(OsuBestScoresFetchStatus.Success));
        }

        string cachePath = Directory.GetFiles(cacheDirectory, "*.json").Single();
        string cacheContents = await File.ReadAllTextAsync(cachePath);
        var secondHandler = new RecordingHandler(_ => throw new InvalidOperationException("Fresh cache should avoid HTTP."));
        using var secondClient = new OfficialOsuApiClient(monitor, secondHandler, cacheDirectory, TimeProvider.System);

        OsuBestScoresFetchResult cached = await secondClient.FetchBestScoresAsync(profile());

        Assert.Multiple(() =>
        {
            Assert.That(cached.Status, Is.EqualTo(OsuBestScoresFetchStatus.Success));
            Assert.That(cached.IsFromCache, Is.True);
            Assert.That(cached.Scores, Has.Count.EqualTo(1));
            Assert.That(secondHandler.CallCount, Is.Zero);
            Assert.That(cachePath, Does.Contain("20220705").And.Contain("user-42"));
            Assert.That(cacheContents, Does.Not.Contain(access_token));
            Assert.That(cacheContents, Does.Not.Contain("refresh-private"));
            Assert.That(Directory.GetFiles(cacheDirectory, "*.tmp"), Is.Empty);
        });
    }

    [Test]
    public async Task ExpiredCacheIsRefetchedInsteadOfUsedAsFallback()
    {
        await writeSignedInSessionAsync("crunchy", access_token);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        string cacheDirectory = Path.Combine(temporaryDirectory, "cache");
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        var firstHandler = new RecordingHandler(_ => jsonResponse(HttpStatusCode.OK, scorePageJson(42, 1)));
        using (var firstClient = new OfficialOsuApiClient(monitor, firstHandler, cacheDirectory, time))
            await firstClient.FetchBestScoresAsync(profile());

        time.Advance(TimeSpan.FromMinutes(16));
        var secondHandler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var secondClient = new OfficialOsuApiClient(monitor, secondHandler, cacheDirectory, time);

        OsuBestScoresFetchResult result = await secondClient.FetchBestScoresAsync(profile());

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(OsuBestScoresFetchStatus.ServerError));
            Assert.That(result.Scores, Is.Null);
            Assert.That(result.IsFromCache, Is.False);
            Assert.That(secondHandler.CallCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task DiscardsSecondPageWhenSignedInAccountChanges()
    {
        await writeSignedInSessionAsync("crunchy", access_token);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        int calls = 0;
        var handler = new RecordingHandler(_ =>
        {
            calls++;
            if (calls == 2)
                File.WriteAllText(gameIniPath, sessionContents("other-user", "other-private-token"));
            return jsonResponse(HttpStatusCode.OK, scorePageJson(42, calls == 1 ? 100 : 1));
        });
        using var client = new OfficialOsuApiClient(monitor, handler, Path.Combine(temporaryDirectory, "cache"), TimeProvider.System);

        OsuBestScoresFetchResult result = await client.FetchBestScoresAsync(profile());

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(OsuBestScoresFetchStatus.SessionChanged));
            Assert.That(result.Scores, Is.Null);
            Assert.That(handler.CallCount, Is.EqualTo(2));
            Assert.That(JsonSerializer.Serialize(result), Does.Not.Contain(access_token));
            Assert.That(JsonSerializer.Serialize(result), Does.Not.Contain("other-private-token"));
        });
    }

    [Test]
    public async Task RejectsBestScorePayloadForAnotherUser()
    {
        await writeSignedInSessionAsync("crunchy", access_token);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        var handler = new RecordingHandler(_ => jsonResponse(HttpStatusCode.OK, scorePageJson(99, 1)));
        using var client = new OfficialOsuApiClient(monitor, handler, Path.Combine(temporaryDirectory, "cache"), TimeProvider.System);

        OsuBestScoresFetchResult result = await client.FetchBestScoresAsync(profile());

        Assert.That(result.Status, Is.EqualTo(OsuBestScoresFetchStatus.InvalidResponse));
    }

    [Test]
    public async Task PreservesMissingOfficialPpWithoutInventingAValue()
    {
        await writeSignedInSessionAsync("crunchy", access_token);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        string payload = scorePageJson(42, 1).Replace("\"pp\": 321.45", "\"pp\": null", StringComparison.Ordinal);
        var handler = new RecordingHandler(_ => jsonResponse(HttpStatusCode.OK, payload));
        using var client = new OfficialOsuApiClient(monitor, handler, Path.Combine(temporaryDirectory, "cache"), TimeProvider.System);

        OsuBestScoresFetchResult result = await client.FetchBestScoresAsync(profile());

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(OsuBestScoresFetchStatus.Success));
            Assert.That(result.Scores, Has.Count.EqualTo(1));
            Assert.That(result.Scores![0].PerformancePoints, Is.Null);
        });
    }

    [Test]
    public async Task FreshCacheIsNotReturnedAfterAccountChanges()
    {
        await writeSignedInSessionAsync("crunchy", access_token);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        string cacheDirectory = Path.Combine(temporaryDirectory, "cache");
        var firstHandler = new RecordingHandler(_ => jsonResponse(HttpStatusCode.OK, scorePageJson(42, 1)));
        using (var firstClient = new OfficialOsuApiClient(monitor, firstHandler, cacheDirectory, TimeProvider.System))
            await firstClient.FetchBestScoresAsync(profile());

        await File.WriteAllTextAsync(gameIniPath, sessionContents("other-user", "other-private-token"));
        var secondHandler = new RecordingHandler(_ => throw new InvalidOperationException("Wrong-account cache must not issue HTTP."));
        using var secondClient = new OfficialOsuApiClient(monitor, secondHandler, cacheDirectory, TimeProvider.System);

        OsuBestScoresFetchResult result = await secondClient.FetchBestScoresAsync(profile());

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(OsuBestScoresFetchStatus.SessionChanged));
            Assert.That(result.Scores, Is.Null);
            Assert.That(secondHandler.CallCount, Is.Zero);
        });
    }

    private Task writeSignedInSessionAsync(string username, string token) => File.WriteAllTextAsync(gameIniPath, sessionContents(username, token));

    [Test]
    public async Task FetchesAllSubmittedScoresForExactBeatmap()
    {
        await writeSignedInSessionAsync("crunchy", access_token);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        var handler = new RecordingHandler(_ => jsonResponse(HttpStatusCode.OK,
            $$"""{ "scores": [{{bestScoreJson(7, 42)}}, {{bestScoreJson(8, 42)}}] }"""));
        using var client = new OfficialOsuApiClient(monitor, handler, Path.Combine(temporaryDirectory, "cache"), TimeProvider.System);

        OsuUserBeatmapScoresFetchResult result = await client.FetchUserBeatmapScoresAsync(profile(), 1234);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(OsuBestScoresFetchStatus.Success));
            Assert.That(result.Scores, Has.Count.EqualTo(2));
            Assert.That(result.Scores![0].ScoreId, Is.EqualTo(7));
            Assert.That(result.Scores[0].PerformancePoints, Is.EqualTo(321.45));
            Assert.That(result.Scores[0].Mods, Is.EqualTo(new[] { "HD", "DT" }));
            Assert.That(handler.RequestUri?.AbsolutePath, Is.EqualTo("/api/v2/beatmaps/1234/scores/users/42/all"));
            Assert.That(handler.RequestUri?.Query, Is.EqualTo("?ruleset=osu"));
        });
    }

    [Test]
    public async Task ReusesExactBeatmapScoreCacheByUserAndBeatmap()
    {
        await writeSignedInSessionAsync("crunchy", access_token);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        string cacheDirectory = Path.Combine(temporaryDirectory, "cache");
        var firstHandler = new RecordingHandler(_ => jsonResponse(HttpStatusCode.OK,
            $$"""{ "scores": [{{bestScoreJson(7, 42)}}] }"""));
        using (var first = new OfficialOsuApiClient(monitor, firstHandler, cacheDirectory, TimeProvider.System))
            Assert.That((await first.FetchUserBeatmapScoresAsync(profile(), 1234)).Status, Is.EqualTo(OsuBestScoresFetchStatus.Success));

        var secondHandler = new RecordingHandler(_ => throw new InvalidOperationException("Fresh exact-beatmap cache should avoid HTTP."));
        using var second = new OfficialOsuApiClient(monitor, secondHandler, cacheDirectory, TimeProvider.System);
        OsuUserBeatmapScoresFetchResult cached = await second.FetchUserBeatmapScoresAsync(profile(), 1234);

        Assert.Multiple(() =>
        {
            Assert.That(cached.Status, Is.EqualTo(OsuBestScoresFetchStatus.Success));
            Assert.That(cached.IsFromCache, Is.True);
            Assert.That(cached.Scores, Has.Count.EqualTo(1));
            Assert.That(secondHandler.CallCount, Is.Zero);
            Assert.That(Directory.GetFiles(cacheDirectory, "beatmap-scores-*-user-42-beatmap-1234.json"), Has.Length.EqualTo(1));
            Assert.That(Directory.GetFiles(cacheDirectory, "*.tmp"), Is.Empty);
        });
    }

    [Test]
    public async Task FetchesRecentScoresIncludingFailsAndCachesFeedSeparately()
    {
        await writeSignedInSessionAsync("crunchy", access_token);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        string cacheDirectory = Path.Combine(temporaryDirectory, "cache");
        var firstHandler = new RecordingHandler(_ => jsonResponse(HttpStatusCode.OK, scorePageJson(42, 2)));
        using (var first = new OfficialOsuApiClient(monitor, firstHandler, cacheDirectory, TimeProvider.System))
        {
            OsuBestScoresFetchResult result = await first.FetchRecentScoresAsync(profile());
            Assert.Multiple(() =>
            {
                Assert.That(result.Status, Is.EqualTo(OsuBestScoresFetchStatus.Success));
                Assert.That(result.Scores, Has.Count.EqualTo(2));
                Assert.That(firstHandler.RequestUri?.AbsolutePath, Is.EqualTo("/api/v2/users/42/scores/recent"));
                Assert.That(firstHandler.RequestUri?.Query, Does.Contain("include_fails=1").And.Contain("limit=100"));
            });
        }

        var secondHandler = new RecordingHandler(_ => throw new InvalidOperationException("Fresh recent-score cache should avoid HTTP."));
        using var second = new OfficialOsuApiClient(monitor, secondHandler, cacheDirectory, TimeProvider.System);
        OsuBestScoresFetchResult cached = await second.FetchRecentScoresAsync(profile());

        Assert.Multiple(() =>
        {
            Assert.That(cached.Status, Is.EqualTo(OsuBestScoresFetchStatus.Success));
            Assert.That(cached.IsFromCache, Is.True);
            Assert.That(secondHandler.CallCount, Is.Zero);
            Assert.That(Directory.GetFiles(cacheDirectory, "recent-scores-*-user-42.json"), Has.Length.EqualTo(1));
        });
    }

    private static string sessionContents(string username, string token) =>
        $"Username = {username}\nToken = {token}|{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}|refresh-private\n";

    private static HttpResponseMessage jsonResponse(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static string validProfileJson(string username) => $$"""
        {
          "id": 42,
          "username": "{{username}}",
          "country_code": "NL",
          "avatar_url": "https://a.ppy.sh/42",
          "statistics": {
            "global_rank": 5678,
            "country_rank": 123,
            "pp": 1234.5,
            "hit_accuracy": 98.76,
            "play_count": 321,
            "play_time": 654,
            "ranked_score": 123456789,
            "total_score": 987654321,
            "total_hits": 111222,
            "maximum_combo": 999
          }
        }
        """;

    private static OsuProfile profile() => new(42, "crunchy", "NL", null, null);

    private static string scorePageJson(int userId, int count, int firstId = 1) =>
        "[" + string.Join(",", Enumerable.Range(firstId, count).Select(id => bestScoreJson(id, userId))) + "]";

    private static string bestScoreJson(int id, int userId) => $$"""
        {
          "id": {{id}},
          "user_id": {{userId}},
          "pp": 321.45,
          "accuracy": 0.9876,
          "score": 987654,
          "max_combo": 777,
          "statistics": { "count_miss": 2, "count_300": 600, "count_100": 20, "count_50": 3 },
          "mods": ["HD", { "acronym": "DT", "settings": { "speed_change": 1.5 } }],
          "ended_at": "2026-08-01T12:34:56Z",
          "created_at": "2026-08-01T12:34:55Z",
          "beatmap": {
            "id": 1234, "checksum": "abc123", "version": "Insane", "difficulty_rating": 6.42,
            "max_combo": 900, "bpm": 180.0, "total_length": 125
          },
          "beatmapset": {
            "id": 567, "title": "Test Song", "title_unicode": "Test Song Unicode",
            "artist": "Test Artist", "artist_unicode": "Test Artist Unicode", "creator": "Mapper",
            "source": "Game", "status": "ranked", "covers": { "cover": "https://assets.ppy.sh/cover.jpg" }
          }
        }
        """;

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];
        public int CallCount { get; private set; }
        public Uri? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            RequestUri = request.RequestUri;
            Method = request.Method;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            Requests.Add(new RecordedRequest(
                request.RequestUri,
                request.Method,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                request.Headers.TryGetValues("x-api-version", out IEnumerable<string>? values) ? values.SingleOrDefault() : null));
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed record RecordedRequest(
        Uri? Uri,
        HttpMethod Method,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        string? ApiVersion);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}

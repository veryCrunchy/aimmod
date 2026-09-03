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

    private Task writeSignedInSessionAsync(string username, string token) => File.WriteAllTextAsync(gameIniPath, sessionContents(username, token));

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

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
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
            return Task.FromResult(responseFactory(request));
        }
    }
}

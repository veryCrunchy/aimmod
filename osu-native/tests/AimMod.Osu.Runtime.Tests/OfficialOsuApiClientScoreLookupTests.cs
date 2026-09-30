using System.Net;
using NUnit.Framework;

namespace AimMod.Osu.Runtime.Tests;

public sealed partial class OfficialOsuApiClientTests
{
    [TestCase(null, "https://osu.ppy.sh/api/v2/scores/42")]
    [TestCase("osu", "https://osu.ppy.sh/api/v2/scores/osu/42")]
    public async Task ScoreLookupUsesTheFixedEndpointAndReadOnlySession(string? ruleset, string endpoint)
    {
        await writeSignedInSessionAsync("PracticePlayer", access_token);
        string before = await File.ReadAllTextAsync(gameIniPath);
        await using var monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        var handler = new RecordingHandler(_ => jsonResponse(HttpStatusCode.OK, "{\"id\":42}"));
        using var client = new OfficialOsuApiClient(monitor, handler);
        var result = await client.FetchScoreAsync(new(42, ruleset));
        Assert.That(result.GetProperty("id").GetInt64(), Is.EqualTo(42));
        Assert.That(handler.RequestUri!.AbsoluteUri, Is.EqualTo(endpoint));
        Assert.That(handler.AuthorizationParameter, Is.EqualTo(access_token));
        Assert.That(await File.ReadAllTextAsync(gameIniPath), Is.EqualTo(before));
    }

    [Test]
    public async Task RateLimitedScoreLookupStartsACooldownInsteadOfRetrying()
    {
        await writeSignedInSessionAsync("PracticePlayer", access_token);
        await using var monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        var handler = new RecordingHandler(_ =>
        {
            HttpResponseMessage response = jsonResponse(HttpStatusCode.TooManyRequests, "{}");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            return response;
        });
        using var client = new OfficialOsuApiClient(monitor, handler);

        InvalidOperationException first = Assert.ThrowsAsync<InvalidOperationException>(async () => await client.FetchScoreAsync(new(42)))!;
        InvalidOperationException second = Assert.ThrowsAsync<InvalidOperationException>(async () => await client.FetchScoreAsync(new(43)))!;

        Assert.Multiple(() =>
        {
            Assert.That(first.Message, Does.Contain("limiting"));
            Assert.That(second.Message, Is.EqualTo(first.Message));
            Assert.That(handler.CallCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ScoreLookupRejectsANonObjectPayload()
    {
        await writeSignedInSessionAsync("PracticePlayer", access_token);
        await using var monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        using var client = new OfficialOsuApiClient(monitor, new RecordingHandler(_ => jsonResponse(HttpStatusCode.OK, "null")));

        Assert.ThrowsAsync<HttpRequestException>(async () => await client.FetchScoreAsync(new(42)));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("https://osu.ppy.sh.example/scores/1")]
    [TestCase("https://osu.ppy.sh/scores/catch/1")]
    public void ScoreAddressRejectsMissingAndForeignLinks(string? text) =>
        Assert.That(OsuScoreAddress.TryParse(text!, out _), Is.False);

    [TestCase(HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.NotFound)]
    public async Task FailedScoreLookupDoesNotSignThePlayerOut(HttpStatusCode status)
    {
        await writeSignedInSessionAsync("PracticePlayer", access_token);
        string before = await File.ReadAllTextAsync(gameIniPath);
        await using var monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        var handler = new RecordingHandler(_ => jsonResponse(status, "{}"));
        using var client = new OfficialOsuApiClient(monitor, handler);
        Assert.ThrowsAsync<InvalidOperationException>(async () => await client.FetchScoreAsync(new(42)));
        using var lease = monitor.TryLeaseAccessToken();
        Assert.That(lease, Is.Not.Null);
        Assert.That(await File.ReadAllTextAsync(gameIniPath), Is.EqualTo(before));
    }
}

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

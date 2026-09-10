using System.Net;
using System.Text;
using System.Text.Json;
using AimMod.Desktop.Creator;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class TwitchVodDiscoveryTests
{
    private static readonly DateTimeOffset now = DateTimeOffset.Parse("2026-09-11T18:00:00Z");
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = now; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Store : ITwitchCredentialStore
    {
        public TwitchCredential? Value = new("client", "access", "refresh", now.AddHours(1), new("7", "viewer"));
        public int Saves;
        public Task<TwitchCredential?> LoadAsync(CancellationToken token) => Task.FromResult(Value);
        public Task SaveAsync(TwitchCredential value, CancellationToken token) { token.ThrowIfCancellationRequested(); Value = value; Saves++; return Task.CompletedTask; }
        public Task ClearAsync(CancellationToken token) { Value = null; return Task.CompletedTask; }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler
    {
        public int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Count++; return Task.FromResult(action(request)); }
    }
    private static HttpResponseMessage json(object body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    private static object video(string id, DateTimeOffset at, string duration = "2h", string type = "archive", string owner = "123") => new
    { id, user_id = owner, title = "Synthetic stream", type, created_at = at, duration };
    private static object page(object[] data, string cursor = "") => new { data, pagination = new { cursor } };
    private static Handler authentication() => new(request =>
    {
        Assert.That(request.RequestUri!.AbsoluteUri, Is.EqualTo("https://id.twitch.tv/oauth2/validate"));
        Assert.That(request.Headers.Authorization!.Scheme, Is.EqualTo("OAuth"));
        return json(new { client_id = "client", user_id = "7", login = "viewer", expires_in = 3600 });
    });

    [Test]
    public async Task ListsAllAvailableArchivesAcrossPagesWithoutScoreDateCutoff()
    {
        var clock = new Clock();
        using var connection = new TwitchConnection("client", new Store(), authentication(), clock);
        var api = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("users"))
                return json(new { data = new[] { new { id = "123", login = "streamer" } } });
            return !request.RequestUri.Query.Contains("after=")
                ? json(page([video("1", now.AddDays(-1))], "next"))
                : json(page([video("2", now.AddDays(-60))]));
        });
        using var discovery = new TwitchVodDiscovery(connection, api, clock);
        var result = await discovery.ListArchivesAsync("streamer", default);
        Assert.That(result.Complete, Is.True);
        Assert.That(result.Matches.Select(v => v.Id), Is.EquivalentTo(new[] { "1", "2" }));
        Assert.That(result.PagesChecked, Is.EqualTo(2));
        var again = await discovery.ListArchivesAsync("streamer", default);
        Assert.That(again.UsedCachedMetadata, Is.True);
        Assert.That(api.Count, Is.EqualTo(3));
    }

    [Test]
    public async Task FindsOldScoreFromArchiveMetadataAndReusesPagesAcrossAttempts()
    {
        var clock = new Clock(); var auth = authentication();
        using var connection = new TwitchConnection("client", new Store(), auth, clock);
        var requests = new List<string>();
        var api = new Handler(request =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            Assert.That(request.RequestUri.Host, Is.EqualTo("api.twitch.tv"));
            Assert.That(request.Headers.GetValues("Client-Id").Single(), Is.EqualTo("client"));
            if (request.RequestUri.AbsolutePath.EndsWith("users"))
                return json(new { data = new[] { new { id = "123", login = "streamer" } } });
            Assert.That(request.RequestUri.Query, Does.Contain("type=archive"));
            if (!request.RequestUri.Query.Contains("after="))
                return json(page([video("1", now.AddDays(-1))], "next"));
            return json(page([video("2", now.AddDays(-10)), video("3", now.AddDays(-20))]));
        });
        using var discovery = new TwitchVodDiscovery(connection, api, clock);
        var result = await discovery.FindAsync("https://twitch.tv/streamer", now.AddDays(-10).AddMinutes(35), default);
        Assert.That(result.Matches.Select(v => v.Id), Is.EqualTo(new[] { "2" }));
        Assert.That(result.Complete, Is.True); Assert.That(result.PagesChecked, Is.EqualTo(2));
        Assert.That(result.Matches.Single().ForPlayer("PracticePlayer").VideoEndSeconds, Is.EqualTo(7200));
        var repeated = await discovery.FindAsync("streamer", now.AddDays(-10).AddMinutes(40), default);
        Assert.That(repeated.UsedCachedMetadata, Is.True);
        Assert.That(api.Count, Is.EqualTo(3)); Assert.That(auth.Count, Is.EqualTo(1));
        Assert.That(requests.All(r => r.StartsWith("https://api.twitch.tv/helix/", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public async Task LongOverlappingArchiveOnFollowingPageIsNotSkipped()
    {
        var at = now.AddDays(-1); var clock = new Clock();
        using var connection = new TwitchConnection("client", new Store(), authentication(), clock);
        var api = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("users")
            ? json(new { data = new[] { new { id = "123", login = "streamer" } } })
            : !request.RequestUri.Query.Contains("after=") ? json(page([video("1", at.AddHours(-1), "10m")], "older"))
            : json(page([video("2", at.AddHours(-4), "5h")])));
        using var discovery = new TwitchVodDiscovery(connection, api, clock);
        Assert.That((await discovery.FindAsync("streamer", at, default)).Matches.Single().Id, Is.EqualTo("2"));
    }

    [Test]
    public async Task MarksPageLimitAndRepeatedCursorAsPartial()
    {
        var clock = new Clock();
        using var connection = new TwitchConnection("client", new Store(), authentication(), clock);
        var api = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("users")
            ? json(new { data = new[] { new { id = "123", login = "streamer" } } })
            : json(page([video("1", now.AddDays(-1))], "same")));
        using var discovery = new TwitchVodDiscovery(connection, api, clock, 2);
        var result = await discovery.FindAsync("streamer", now.AddDays(-30), default);
        Assert.That(result.Complete, Is.False); Assert.That(result.Matches, Is.Empty);
        Assert.That(api.Count, Is.EqualTo(3));
    }

    [Test]
    public async Task RefreshesMetadataForAPlayNewerThanTheCache()
    {
        var clock = new Clock(); int videoRequests = 0;
        using var connection = new TwitchConnection("client", new Store(), authentication(), clock);
        var api = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("users")) return json(new { data = new[] { new { id = "123", login = "streamer" } } });
            videoRequests++; return json(page([video("1", now.AddHours(-1), videoRequests == 1 ? "1h" : "2h")]));
        });
        using var discovery = new TwitchVodDiscovery(connection, api, clock);
        await discovery.FindAsync("streamer", now.AddMinutes(-5), default);
        clock.Now = now.AddMinutes(5);
        Assert.That((await discovery.FindAsync("streamer", now.AddMinutes(2), default)).Matches.Single().Id, Is.EqualTo("1"));
        Assert.That(videoRequests, Is.EqualTo(2));
    }

    [TestCase("highlight", "123")][TestCase("archive", "other")]
    public void RejectsWrongChannelOrUploadedHighlightDates(string type, string owner)
    {
        var clock = new Clock();
        using var connection = new TwitchConnection("client", new Store(), authentication(), clock);
        var api = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("users")
            ? json(new { data = new[] { new { id = "123", login = "streamer" } } })
            : json(page([video("1", now.AddDays(-1), type: type, owner: owner)])));
        using var discovery = new TwitchVodDiscovery(connection, api, clock);
        Assert.ThrowsAsync<InvalidDataException>(async () => await discovery.FindAsync("streamer", now.AddDays(-1).AddMinutes(5), default));
    }

    [Test]
    public async Task RefreshesTokenOnceAndKeepsItSeparateFromFootageMetadata()
    {
        var clock = new Clock(); var store = new Store(); store.Value = store.Value! with { ExpiresAt = now.AddSeconds(-1) };
        int refreshes = 0;
        var auth = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("token"))
            {
                refreshes++;
                string body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert.That(body, Does.Contain("refresh_token=refresh")); Assert.That(body, Does.Not.Contain("client_secret"));
                return json(new { access_token = "rotated-access", refresh_token = "rotated-refresh", expires_in = 3600 });
            }
            Assert.That(request.Headers.Authorization!.Parameter, Is.EqualTo("rotated-access"));
            return json(new { client_id = "client", user_id = "7", login = "viewer", expires_in = 3600 });
        });
        using var connection = new TwitchConnection("client", store, auth, clock);
        var values = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => connection.AccessTokenAsync(default)));
        Assert.That(values.Distinct().Single(), Is.EqualTo("rotated-access")); Assert.That(refreshes, Is.EqualTo(1));
        Assert.That(store.Value!.RefreshToken, Is.EqualTo("rotated-refresh")); Assert.That(store.Saves, Is.EqualTo(1));
        Assert.That(store.Value.ToString(), Does.Not.Contain("rotated"));
        await connection.DisconnectAsync(default); Assert.That(store.Value, Is.Null);
    }

    [Test]
    public async Task DeviceConnectionValidatesTheVerificationAddress()
    {
        var clock = new Clock();
        var auth = new Handler(request => json(new { device_code = "private-code", user_code = "ABCD", expires_in = 300, interval = 5,
            verification_uri = "https://evil.example/activate" }));
        using var connection = new TwitchConnection("client", new Store { Value = null }, auth, clock);
        Assert.ThrowsAsync<InvalidDataException>(async () => await connection.BeginAsync(default));
        using var missing = new TwitchConnection("", new Store { Value = null }, authentication(), clock);
        Assert.ThrowsAsync<InvalidOperationException>(async () => await missing.BeginAsync(default));
        Assert.That(await connection.SavedAccountAsync(default), Is.Null);
    }

    [Test]
    public async Task DeviceConnectionWaitsForApprovalThenValidatesAndSavesTheAccount()
    {
        var store = new Store { Value = null }; int polls = 0;
        var auth = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("device"))
            {
                string form = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert.That(form, Does.Contain("scopes=")); Assert.That(form, Does.Not.Contain("client_secret"));
                return json(new { device_code = "private-device", user_code = "ABCD", verification_uri = "https://www.twitch.tv/activate", interval = 1, expires_in = 60 });
            }
            if (request.RequestUri.AbsolutePath.EndsWith("token"))
            {
                if (++polls == 1) return json(new { message = "authorization_pending" }, HttpStatusCode.BadRequest);
                return json(new { access_token = "linked-access", refresh_token = "linked-refresh", expires_in = 3600 });
            }
            Assert.That(request.Headers.Authorization!.Parameter, Is.EqualTo("linked-access"));
            return json(new { client_id = "client", user_id = "7", login = "viewer", expires_in = 3600 });
        });
        using var connection = new TwitchConnection("client", store, auth);
        var code = await connection.BeginAsync(default);
        var account = await connection.CompleteAsync(code, default);
        Assert.That(account, Is.EqualTo(new TwitchAccount("7", "viewer")));
        Assert.That(store.Saves, Is.EqualTo(1)); Assert.That(polls, Is.EqualTo(2));
        Assert.That(code.ToString(), Does.Not.Contain("private-device"));
    }

    [Test]
    public void CancelledDeviceConnectionDoesNotPersistCredentials()
    {
        var store = new Store { Value = null }; var auth = new Handler(_ => throw new InvalidOperationException("No request expected"));
        using var connection = new TwitchConnection("client", store, auth);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await connection.CompleteAsync(new("private", "CODE", new("https://www.twitch.tv/activate"), DateTimeOffset.UtcNow.AddMinutes(1), 5), cancel.Token));
        Assert.That(store.Saves, Is.Zero); Assert.That(auth.Count, Is.Zero);
    }

    [Test]
    public async Task CredentialStoreRoundTripsWithoutLeavingTemporaryFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), "aimmod-twitch-tests-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "connection.bin");
        try
        {
            var store = new TwitchCredentialStore(path);
            var value = new TwitchCredential("client", "synthetic-private-access", "synthetic-private-refresh", now.AddHours(1), new("7", "viewer"));
            await store.SaveAsync(value, default);
            Assert.That(await store.LoadAsync(default), Is.EqualTo(value));
            if (OperatingSystem.IsWindows()) Assert.That(Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)), Does.Not.Contain(value.AccessToken));
            else Assert.That(File.GetUnixFileMode(path), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
            Assert.That(Directory.GetFiles(directory).Length, Is.EqualTo(1));
            await store.ClearAsync(default); Assert.That(await store.LoadAsync(default), Is.Null);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestCase("1h2m3s", 3723)][TestCase("3m21s", 201)][TestCase("0s", 0)]
    public void ParsesDuration(string value, int expected)
    { Assert.That(TwitchVodDiscovery.TryDuration(value, out double seconds), Is.True); Assert.That(seconds, Is.EqualTo(expected)); }
    [TestCase("")][TestCase("999999h")][TestCase("-1h")][TestCase("01:00:00")]
    public void RejectsInvalidDuration(string value) => Assert.That(TwitchVodDiscovery.TryDuration(value, out _), Is.False);
    [TestCase("https://www.twitch.tv/streamer", "streamer")][TestCase("Example_Player", "example_player")]
    [TestCase("https://twitch.tv.evil.example/streamer", "")][TestCase("https://twitch.tv/videos/123", "")]
    public void NormalisesOnlyChannelLinks(string input, string expected) => Assert.That(TwitchVodDiscovery.NormaliseChannel(input), Is.EqualTo(expected));
}

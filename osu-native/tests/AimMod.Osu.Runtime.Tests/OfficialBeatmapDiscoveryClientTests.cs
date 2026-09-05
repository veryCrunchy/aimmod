using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace AimMod.Osu.Runtime.Tests;

[TestFixture]
public sealed class OfficialBeatmapDiscoveryClientTests
{
    private const string access_token = "beatmap-access-token";
    private string temporaryDirectory = null!;
    private string gameIniPath = null!;

    [SetUp]
    public void SetUp()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"aimmod-beatmap-api-test-{Guid.NewGuid():N}");
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
    public async Task SearchesOfficialStandardCatalogAndReturnsGroupedFilteredSets()
    {
        await writeSignedInSessionAsync("crunchy", access_token);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        var handler = new RecordingHandler(_ => jsonResponse(HttpStatusCode.OK, searchJson));
        using var client = new OfficialBeatmapDiscoveryClient(monitor, handler);

        OfficialBeatmapSearchResult result = await client.SearchAsync(new OfficialBeatmapSearchQuery(
            "Camellia & friends",
            MinimumStars: 4,
            MaximumStars: 6,
            Category: OfficialBeatmapCategory.Ranked,
            Limit: 12));

        Assert.Multiple(() =>
        {
            Assert.That(handler.Requests, Has.Count.EqualTo(1));
            Assert.That(handler.Requests[0].Uri?.AbsolutePath, Is.EqualTo("/api/v2/beatmapsets/search"));
            Assert.That(handler.Requests[0].Uri?.Query, Does.Contain("q=Camellia%20%26%20friends"));
            Assert.That(handler.Requests[0].Uri?.Query, Does.Contain("m=0"));
            Assert.That(handler.Requests[0].Uri?.Query, Does.Contain("s=ranked"));
            Assert.That(handler.Requests[0].Uri?.Query, Does.Contain("sort=relevance_desc"));
            Assert.That(handler.Requests[0].Authorization, Is.EqualTo(access_token));
            Assert.That(result.Status, Is.EqualTo(OfficialBeatmapRequestStatus.Success));
            Assert.That(result.ServerTotal, Is.EqualTo(321));
            Assert.That(result.IsTruncated, Is.True);
            Assert.That(result.BeatmapSets, Has.Count.EqualTo(1));
            Assert.That(result.BeatmapSets[0].Title, Is.EqualTo("Light it up"));
            Assert.That(result.BeatmapSets[0].CoverUrl, Is.EqualTo(new Uri("https://assets.ppy.sh/beatmaps/123/covers/cover@2x.jpg")));
            Assert.That(result.BeatmapSets[0].PreviewAudioUrl, Is.EqualTo(new Uri("https://b.ppy.sh/preview/123.mp3")));
            Assert.That(result.BeatmapSets[0].Difficulties.Select(difficulty => difficulty.StarRating), Is.EqualTo(new[] { 5.25 }));
            Assert.That(result.BeatmapSets[0].Difficulties[0].RulesetShortName, Is.EqualTo("osu"));
            Assert.That(JsonSerializer.Serialize(result), Does.Not.Contain(access_token));
        });
    }

    [TestCase(123, OfficialBeatmapRequestStatus.Success)]
    [TestCase(456, OfficialBeatmapRequestStatus.InvalidResponse)]
    public async Task ExactSetLookupChecksIdentityAndDoesNotDownload(int setId, OfficialBeatmapRequestStatus expected)
    {
        await writeSignedInSessionAsync("crunchy", access_token);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        using var document = JsonDocument.Parse(searchJson);
        string payload = document.RootElement.GetProperty("beatmapsets")[0].GetRawText();
        var handler = new RecordingHandler(_ => jsonResponse(HttpStatusCode.OK, payload));
        using var client = new OfficialBeatmapDiscoveryClient(monitor, handler);
        var result = await client.GetSetAsync(setId);
        Assert.That(result.Status, Is.EqualTo(expected));
        Assert.That(handler.Requests, Has.Count.EqualTo(1));
        Assert.That(handler.Requests[0].Uri?.AbsolutePath, Is.EqualTo($"/api/v2/beatmapsets/{setId}"));
        Assert.That(handler.Requests[0].Authorization, Is.EqualTo(access_token));
    }

    [Test]
    public async Task EmptyDiscoveryUsesOsuRankedOrdering()
    {
        await writeSignedInSessionAsync("crunchy", access_token);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        var handler = new RecordingHandler(_ => jsonResponse(HttpStatusCode.OK, searchJson));
        using var client = new OfficialBeatmapDiscoveryClient(monitor, handler);

        OfficialBeatmapSearchResult result = await client.SearchAsync(new OfficialBeatmapSearchQuery());

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(OfficialBeatmapRequestStatus.Success));
            Assert.That(handler.Requests[0].Uri?.Query, Does.Contain("sort=ranked_desc"));
        });
    }

    [TestCase("Username = crunchy\nToken =\n", OfficialBeatmapRequestStatus.SignedOut)]
    [TestCase("Username = crunchy\nToken = expired|1|refresh\n", OfficialBeatmapRequestStatus.TokenExpired)]
    public async Task SearchDoesNotUseNetworkWithoutAValidInheritedSession(string session, OfficialBeatmapRequestStatus expected)
    {
        await File.WriteAllTextAsync(gameIniPath, session);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("Network access was not expected."));
        using var client = new OfficialBeatmapDiscoveryClient(monitor, handler);

        OfficialBeatmapSearchResult result = await client.SearchAsync(new OfficialBeatmapSearchQuery("test"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(expected));
            Assert.That(result.BeatmapSets, Is.Empty);
            Assert.That(handler.Requests, Is.Empty);
        });
    }

    [Test]
    public async Task DownloadWritesValidatedOszWithoutExposingToken()
    {
        await writeSignedInSessionAsync("crunchy", access_token);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        byte[] archive = createOsz();
        var handler = new RecordingHandler(_ => binaryResponse(HttpStatusCode.OK, archive));
        using var client = new OfficialBeatmapDiscoveryClient(monitor, handler);

        OfficialBeatmapDownloadResult result = await client.DownloadAsync(123, temporaryDirectory, noVideo: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(OfficialBeatmapRequestStatus.Success));
            Assert.That(result.ArchivePath, Is.Not.Null.And.EndsWith(".osz"));
            Assert.That(result.ArchivePath, Does.StartWith(temporaryDirectory + Path.DirectorySeparatorChar));
            Assert.That(File.ReadAllBytes(result.ArchivePath!), Is.EqualTo(archive));
            Assert.That(result.ArchiveBytes, Is.EqualTo(archive.Length));
            Assert.That(handler.Requests[0].Uri, Is.EqualTo(new Uri("https://osu.ppy.sh/api/v2/beatmapsets/123/download?noVideo=1")));
            Assert.That(handler.Requests[0].Authorization, Is.EqualTo(access_token));
            Assert.That(JsonSerializer.Serialize(result), Does.Not.Contain(access_token));
        });
    }

    [Test]
    public async Task SearchCursorRoundTripsWithoutChangingOpaqueCharactersAndForwardsStarBounds()
    {
        await writeSignedInSessionAsync("crunchy", access_token);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        const string cursor = "opaque+/= token&next";
        string payload = searchJson.Insert(searchJson.IndexOf('{') + 1, "\"cursor_string\":\"" + cursor + "\",");
        var handler = new RecordingHandler(_ => jsonResponse(HttpStatusCode.OK, payload));
        using var client = new OfficialBeatmapDiscoveryClient(monitor, handler);
        var query = new OfficialBeatmapSearchQuery("farm", 0, 6.5, Sort: OfficialBeatmapSort.Plays, Limit: 50);
        OfficialBeatmapSearchResult first = await client.SearchAsync(query);
        await client.SearchAsync(query with { Cursor = first.NextCursor });
        Assert.Multiple(() =>
        {
            Assert.That(first.NextCursor, Is.EqualTo(cursor));
            Assert.That(handler.Requests[0].Uri!.Query, Does.Not.Contain("cursor_string"));
            Assert.That(handler.Requests[1].Uri!.Query, Does.Contain("cursor_string=" + Uri.EscapeDataString(cursor)));
            Assert.That(Uri.UnescapeDataString(handler.Requests[1].Uri!.Query), Does.Contain("stars>=0 stars<=6.5"));
            Assert.That(handler.Requests[1].Uri!.Query, Does.Contain("sort=plays_desc"));
        });
    }

    [Test]
    public async Task SearchReportsRateLimitWithoutRetrying()
    {
        await writeSignedInSessionAsync("crunchy", access_token);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        var handler = new RecordingHandler(_ => jsonResponse(HttpStatusCode.TooManyRequests, "{}"));
        using var client = new OfficialBeatmapDiscoveryClient(monitor, handler);
        OfficialBeatmapSearchResult response = await client.SearchAsync(new OfficialBeatmapSearchQuery());
        Assert.That(response.Status, Is.EqualTo(OfficialBeatmapRequestStatus.RateLimited));
        Assert.That(handler.Requests, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task DownloadsAndValidatesOneExactDifficultyByBeatmapId()
    {
        await writeSignedInSessionAsync("crunchy", access_token);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        const string beatmap = "osu file format v14\n\n[Metadata]\nBeatmapID:456\nBeatmapSetID:123\n";
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(beatmap, Encoding.UTF8, "text/plain"),
        });
        using var client = new OfficialBeatmapDiscoveryClient(monitor, handler);

        OfficialBeatmapDifficultyDownloadResult result = await client.DownloadDifficultyAsync(456, temporaryDirectory);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(OfficialBeatmapRequestStatus.Success));
            Assert.That(result.BeatmapId, Is.EqualTo(456));
            Assert.That(result.BeatmapPath, Is.Not.Null.And.EndsWith(".osu"));
            Assert.That(File.ReadAllText(result.BeatmapPath!), Does.Contain("BeatmapID:456"));
            Assert.That(handler.Requests.Single().Uri, Is.EqualTo(new Uri("https://osu.ppy.sh/osu/456")));
            Assert.That(handler.Requests.Single().Authorization, Is.Null);
        });
    }

    [Test]
    public async Task RejectsDifficultyWhoseMetadataDoesNotMatchRequestedBeatmap()
    {
        await writeSignedInSessionAsync("crunchy", access_token);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("osu file format v14\n[Metadata]\nBeatmapID:999\n", Encoding.UTF8, "text/plain"),
        });
        using var client = new OfficialBeatmapDiscoveryClient(monitor, handler);

        OfficialBeatmapDifficultyDownloadResult result = await client.DownloadDifficultyAsync(456, temporaryDirectory);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(OfficialBeatmapRequestStatus.InvalidResponse));
            Assert.That(result.BeatmapPath, Is.Null);
            Assert.That(Directory.EnumerateFiles(temporaryDirectory, "*.osu"), Is.Empty);
        });
    }

    [Test]
    public async Task DownloadFollowsOnlyTrustedHttpsRedirectAndDropsCredential()
    {
        await writeSignedInSessionAsync("crunchy", access_token);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        byte[] archive = createOsz();
        var handler = new RecordingHandler(request => request.RequestUri?.Host == "osu.ppy.sh"
            ? new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://dl.ppy.sh/beatmapsets/123.osz") },
            }
            : binaryResponse(HttpStatusCode.OK, archive));
        using var client = new OfficialBeatmapDiscoveryClient(monitor, handler);

        OfficialBeatmapDownloadResult result = await client.DownloadAsync(123, temporaryDirectory);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(OfficialBeatmapRequestStatus.Success));
            Assert.That(handler.Requests, Has.Count.EqualTo(2));
            Assert.That(handler.Requests[0].Authorization, Is.EqualTo(access_token));
            Assert.That(handler.Requests[1].Uri?.Host, Is.EqualTo("dl.ppy.sh"));
            Assert.That(handler.Requests[1].Authorization, Is.Null);
        });
    }

    [Test]
    public async Task DownloadRejectsUntrustedRedirectAndNonArchiveBody()
    {
        await writeSignedInSessionAsync("crunchy", access_token);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        var redirectHandler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://attacker.invalid/map.osz") },
        });
        using var redirectClient = new OfficialBeatmapDiscoveryClient(monitor, redirectHandler);

        OfficialBeatmapDownloadResult redirect = await redirectClient.DownloadAsync(123, temporaryDirectory);

        var htmlHandler = new RecordingHandler(_ => binaryResponse(HttpStatusCode.OK, Encoding.UTF8.GetBytes("<html>not a beatmap</html>")));
        using var htmlClient = new OfficialBeatmapDiscoveryClient(monitor, htmlHandler);
        OfficialBeatmapDownloadResult html = await htmlClient.DownloadAsync(123, temporaryDirectory);

        Assert.Multiple(() =>
        {
            Assert.That(redirect.Status, Is.EqualTo(OfficialBeatmapRequestStatus.InvalidResponse));
            Assert.That(html.Status, Is.EqualTo(OfficialBeatmapRequestStatus.InvalidResponse));
            Assert.That(Directory.EnumerateFiles(temporaryDirectory, "*.osz"), Is.Empty);
        });
    }

    [Test]
    public async Task SearchDiscardsResponseAfterAccountChanges()
    {
        await writeSignedInSessionAsync("first", access_token);
        await using LazerSessionMonitor monitor = await LazerSessionMonitor.CreateAsync(gameIniPath);
        var handler = new RecordingHandler(_ =>
        {
            File.WriteAllText(gameIniPath, sessionContents("second", "second-token"));
            return jsonResponse(HttpStatusCode.OK, searchJson);
        });
        using var client = new OfficialBeatmapDiscoveryClient(monitor, handler);

        OfficialBeatmapSearchResult result = await client.SearchAsync(new OfficialBeatmapSearchQuery("test"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(OfficialBeatmapRequestStatus.SessionChanged));
            Assert.That(result.BeatmapSets, Is.Empty);
            Assert.That(JsonSerializer.Serialize(result), Does.Not.Contain(access_token));
            Assert.That(JsonSerializer.Serialize(result), Does.Not.Contain("second-token"));
        });
    }

    private Task writeSignedInSessionAsync(string username, string token) => File.WriteAllTextAsync(gameIniPath, sessionContents(username, token));

    private static string sessionContents(string username, string token) =>
        $"Username = {username}\nToken = {token}|{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}|refresh-private\n";

    private static HttpResponseMessage jsonResponse(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage binaryResponse(HttpStatusCode statusCode, byte[] content) => new(statusCode)
    {
        Content = new ByteArrayContent(content),
    };

    private static byte[] createOsz()
    {
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry beatmap = archive.CreateEntry("map.osu");
            using StreamWriter writer = new(beatmap.Open());
            writer.Write("osu file format v14");
        }
        return bytes.ToArray();
    }

    private const string searchJson = """
        {
          "total": 321,
          "beatmapsets": [
            {
              "id": 123,
              "title": "Light it up",
              "title_unicode": "Light it up",
              "artist": "Camellia",
              "artist_unicode": "Camellia",
              "creator": "mapper",
              "source": "",
              "status": "ranked",
              "ranked_date": "2024-01-02T03:04:05Z",
              "last_updated": "2024-01-01T03:04:05Z",
              "play_count": 10000,
              "favourite_count": 500,
              "nsfw": false,
              "preview_url": "//b.ppy.sh/preview/123.mp3",
              "covers": {
                "cover": "https://assets.ppy.sh/beatmaps/123/covers/cover.jpg",
                "cover@2x": "https://assets.ppy.sh/beatmaps/123/covers/cover@2x.jpg",
                "card": "https://assets.ppy.sh/beatmaps/123/covers/card.jpg",
                "list": "https://assets.ppy.sh/beatmaps/123/covers/list.jpg"
              },
              "availability": { "download_disabled": false },
              "beatmaps": [
                {
                  "id": 1001, "version": "Normal", "mode_int": 0,
                  "difficulty_rating": 2.1, "bpm": 180, "total_length": 120,
                  "cs": 4, "ar": 7, "accuracy": 6, "drain": 5,
                  "playcount": 5000, "passcount": 4000, "max_combo": 300
                },
                {
                  "id": 1002, "version": "Insane", "mode_int": 0,
                  "difficulty_rating": 5.25, "bpm": 180, "total_length": 130,
                  "cs": 4, "ar": 9.3, "accuracy": 8.7, "drain": 6,
                  "playcount": 3000, "passcount": 1000, "max_combo": 600
                },
                {
                  "id": 1003, "version": "Mania", "mode_int": 3,
                  "difficulty_rating": 4.5, "bpm": 180, "total_length": 130,
                  "cs": 4, "ar": 9, "accuracy": 8, "drain": 6,
                  "playcount": 1000, "passcount": 500, "max_combo": 700
                }
              ]
            }
          ]
        }
        """;

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(request.RequestUri, request.Headers.Authorization?.Parameter));
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed record RecordedRequest(Uri? Uri, string? Authorization);
}

using System.Net;
using System.Text;
using System.Text.Json;
using AimMod.Desktop.Hub;
using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class OsuHubSyncTests
{
    private string temporaryDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"aimmod-hub-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(temporaryDirectory))
            Directory.Delete(temporaryDirectory, true);
    }

    [Test]
    public async Task ContractUsesDifficultyIdentityAndDefaultsToPrivate()
    {
        (LocalReplay replay, LocalBeatmapSet set, LocalBeatmapDifficulty difficulty) = createLocalData();

        OsuHubSyncRequest request = await OsuHubContractFactory.CreateAsync(new OsuHubSyncInput(
            replay,
            set,
            difficulty,
            new OsuHubProfile(42, "player"),
            createAnalysis(),
            UploadAnalysis: true));

        Assert.Multiple(() =>
        {
            Assert.That(request.SchemaVersion, Is.EqualTo(1));
            Assert.That(request.Visibility, Is.EqualTo("private"));
            Assert.That(request.BeatmapSet.SetKey, Is.EqualTo("online:123"));
            Assert.That(request.Difficulty.DifficultyKey, Is.EqualTo("online:456"));
            Assert.That(request.Score.ClientScoreId, Does.StartWith("lazer:"));
            Assert.That(request.Score.Count300, Is.EqualTo(400));
            Assert.That(request.Analysis?.Payload.Judgements, Has.Count.EqualTo(1));
            Assert.That(request.ContentHash, Has.Length.EqualTo(64));
        });
    }

    [Test]
    public async Task UploadSendsAuthenticatedIdempotentMetadataThenVerifiedReplayBytes()
    {
        string replayPath = Path.Combine(temporaryDirectory, "play.osr");
        await File.WriteAllBytesAsync(replayPath, Encoding.UTF8.GetBytes("replay-payload"));
        (LocalReplay replay, LocalBeatmapSet set, LocalBeatmapDifficulty difficulty) = createLocalData(replayPath);
        OsuHubSyncRequest request = await OsuHubContractFactory.CreateAsync(new OsuHubSyncInput(
            replay,
            set,
            difficulty,
            new OsuHubProfile(42, "player"),
            createAnalysis(),
            OsuHubVisibility.Unlisted,
            true,
            true));

        var handler = new RecordingHandler([
            jsonResponse(HttpStatusCode.Created, new OsuHubSyncResponse("osu_" + new string('a', 32), "unlisted", true, true)),
            jsonResponse(HttpStatusCode.Created, new { uploaded = true }),
        ]);
        var cache = new MemorySyncCache();
        var client = new OsuHubSyncClient(
            new HttpClient(handler),
            new MemoryCredentialStore(new HubCredential("secret", "player", DateTimeOffset.UtcNow)),
            cache,
            new Uri("https://hub.example/"));

        OsuHubUploadResult result = await client.UploadAsync(request, replayPath);

        Assert.Multiple(() =>
        {
            Assert.That(handler.Requests, Has.Count.EqualTo(2));
            Assert.That(handler.Requests[0].Authorization, Is.EqualTo("Bearer secret"));
            Assert.That(handler.Requests[0].IdempotencyKey, Is.EqualTo(request.ClientUploadId));
            Assert.That(handler.Requests[1].ContentHash, Is.EqualTo(request.Replay!.Sha256));
            Assert.That(handler.Requests[1].Body, Is.EqualTo(Encoding.UTF8.GetBytes("replay-payload")));
            Assert.That(result.ReplayUploaded, Is.True);
            Assert.That(result.ShareUri.AbsoluteUri, Is.EqualTo("https://hub.example/osu/replays/" + result.ShareId));
            Assert.That(cache.Entry?.ReplayUploaded, Is.True);
        });
    }

    [Test]
    public async Task CompletedUploadIsDeduplicatedLocally()
    {
        (LocalReplay replay, LocalBeatmapSet set, LocalBeatmapDifficulty difficulty) = createLocalData();
        OsuHubSyncRequest request = await OsuHubContractFactory.CreateAsync(new OsuHubSyncInput(
            replay, set, difficulty, new OsuHubProfile(42, "player"), null));
        var cache = new MemorySyncCache
        {
            Entry = new OsuHubSyncCacheEntry(request.ContentHash, request.Visibility, "osu_" + new string('b', 32), "", false, DateTimeOffset.UtcNow),
        };
        var handler = new RecordingHandler([]);
        var client = new OsuHubSyncClient(
            new HttpClient(handler),
            new MemoryCredentialStore(new HubCredential("secret", "player", DateTimeOffset.UtcNow)),
            cache,
            new Uri("https://hub.example"));

        OsuHubUploadResult result = await client.UploadAsync(request);

        Assert.Multiple(() =>
        {
            Assert.That(result.FromLocalCache, Is.True);
            Assert.That(handler.Requests, Is.Empty);
        });
    }

    [Test]
    public async Task DeviceLinkPersistsApprovedUploadToken()
    {
        var credentials = new MemoryCredentialStore();
        var handler = new RecordingHandler([
            jsonResponse(HttpStatusCode.Created, new
            {
                deviceCode = "device-code",
                userCode = "ABCD-1234",
                verificationUri = "https://hub.example/link-device",
                verificationUriComplete = "https://hub.example/link-device?user_code=ABCD-1234",
                expiresIn = 600,
                interval = 1,
            }),
            jsonResponse(HttpStatusCode.OK, new
            {
                status = "approved",
                user = new { username = "player", displayName = "Player" },
                uploadToken = "new-secret",
            }),
        ]);
        var client = new HubDeviceLinkClient(new HttpClient(handler), new Uri("https://hub.example"), credentials);

        HubDeviceLinkSession session = await client.BeginAsync("AimMod osu test");
        HubDeviceLinkPollResult result = await client.PollAsync(session.DeviceCode);

        Assert.Multiple(() =>
        {
            Assert.That(session.UserCode, Is.EqualTo("ABCD-1234"));
            Assert.That(result.Status, Is.EqualTo(HubDeviceLinkStatus.Approved));
            Assert.That(credentials.Load()?.UploadToken, Is.EqualTo("new-secret"));
        });
    }

    [Test]
    public async Task CredentialStoreNeverWritesPlaintextTokenWithProtector()
    {
        string path = Path.Combine(temporaryDirectory, "hub-credential.dat");
        var store = new FileHubCredentialStore(path, new ReversingProtector());
        var credential = new HubCredential("sensitive-token", "Player", DateTimeOffset.UtcNow);

        await store.SaveAsync(credential);

        Assert.Multiple(() =>
        {
            Assert.That(Encoding.UTF8.GetString(File.ReadAllBytes(path)), Does.Not.Contain("sensitive-token"));
            Assert.That(store.Load(), Is.EqualTo(credential));
        });
    }

    [Test]
    public async Task SharingPreferencesDefaultPrivateAndPersistExplicitChoices()
    {
        string path = Path.Combine(temporaryDirectory, "sharing-preferences.json");
        var store = new FileHubSharingPreferenceStore(path);

        Assert.That(store.Load(), Is.EqualTo(HubSharingPreferences.Default));

        var selected = new HubSharingPreferences(OsuHubVisibility.Unlisted, true, true);
        await store.SaveAsync(selected);

        Assert.That(new FileHubSharingPreferenceStore(path).Load(), Is.EqualTo(selected));
    }

    [Test]
    public async Task DurableQueueCompletesAndRestoresShareResult()
    {
        string path = Path.Combine(temporaryDirectory, "upload-queue.json");
        (LocalReplay replay, LocalBeatmapSet set, LocalBeatmapDifficulty difficulty) = createLocalData();
        OsuHubSyncRequest request = await OsuHubContractFactory.CreateAsync(new OsuHubSyncInput(
            replay, set, difficulty, new OsuHubProfile(42, "player"), null));
        var uploader = new FakeUploader();

        using (var queue = new OsuHubUploadQueue(path, uploader))
        {
            HubUploadQueueItem queued = await queue.EnqueueAsync(request, null, "Map [Insane]");
            Assert.That(SpinWait.SpinUntil(
                () => queue.Snapshot().Any(item => item.Id == queued.Id && item.Status == HubUploadQueueStatus.Completed),
                TimeSpan.FromSeconds(3)), Is.True);
        }

        using var restored = new OsuHubUploadQueue(path, uploader, startWorker: false);
        HubUploadQueueItem completed = restored.Snapshot().Single();
        Assert.Multiple(() =>
        {
            Assert.That(completed.Status, Is.EqualTo(HubUploadQueueStatus.Completed));
            Assert.That(completed.ShareUrl, Is.EqualTo("https://hub.example/osu/replays/share-id"));
            Assert.That(completed.AttemptCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task CancelledQueueItemCanBeRetriedWithoutRecreatingIt()
    {
        string path = Path.Combine(temporaryDirectory, "upload-queue.json");
        (LocalReplay replay, LocalBeatmapSet set, LocalBeatmapDifficulty difficulty) = createLocalData();
        OsuHubSyncRequest request = await OsuHubContractFactory.CreateAsync(new OsuHubSyncInput(
            replay, set, difficulty, new OsuHubProfile(42, "player"), null));
        using var queue = new OsuHubUploadQueue(path, new FakeUploader(), startWorker: false);
        HubUploadQueueItem queued = await queue.EnqueueAsync(request, null, "Map [Insane]");

        Assert.That(await queue.CancelAsync(queued.Id), Is.True);
        Assert.That(queue.Snapshot().Single().Status, Is.EqualTo(HubUploadQueueStatus.Cancelled));
        Assert.That(await queue.RetryAsync(queued.Id), Is.True);
        Assert.That(queue.Snapshot().Single().Status, Is.EqualTo(HubUploadQueueStatus.Queued));
    }

    [Test]
    public async Task ReplayShareServiceUsesExactDifficultyAndQueuesOnlyRequestedPayloads()
    {
        string path = Path.Combine(temporaryDirectory, "upload-queue.json");
        (LocalReplay replay, LocalBeatmapSet set, LocalBeatmapDifficulty difficulty) = createLocalData();
        using var queue = new OsuHubUploadQueue(path, new FakeUploader(), startWorker: false);
        var service = new OsuHubReplayShareService(
            new SingleMapLibrary(set),
            () => new OsuProfile(42, "player", "NL", null, null),
            new Dictionary<Guid, ReplayAnalysisResult> { [replay.ScoreId] = createAnalysis() },
            queue);

        HubUploadQueueItem item = await service.QueueAsync(new HubReplayShareSelection(
            replay,
            OsuHubVisibility.Private,
            UploadReplayFile: false,
            UploadAnalysis: true));

        Assert.Multiple(() =>
        {
            Assert.That(item.Request.Visibility, Is.EqualTo("private"));
            Assert.That(item.Request.Difficulty.OnlineId, Is.EqualTo(difficulty.OnlineId));
            Assert.That(item.Request.Analysis, Is.Not.Null);
            Assert.That(item.Request.Replay, Is.Null);
            Assert.That(item.Status, Is.EqualTo(HubUploadQueueStatus.Queued));
        });
    }

    private static (LocalReplay Replay, LocalBeatmapSet Set, LocalBeatmapDifficulty Difficulty) createLocalData(string replayPath = "")
    {
        Guid setId = Guid.NewGuid();
        Guid beatmapId = Guid.NewGuid();
        LocalBeatmapDifficulty difficulty = new(
            beatmapId, 456, "Insane", "osu", 5.2, 180, 90_000, 4, 9, 8, 6, 1, new string('c', 64));
        LocalBeatmapSet set = new(
            setId, 123, "Map", "Artist", "Mapper", "Source", DateTimeOffset.UtcNow.AddDays(-2),
            DateTimeOffset.UtcNow.AddMinutes(-1), [difficulty], 1, "cover.jpg");
        LocalReplay replay = new(
            Guid.NewGuid(), setId, beatmapId, "Map", "Artist", "Insane", "osu", "player",
            DateTimeOffset.UtcNow.AddMinutes(-1), 5.2, .98, 1_000_000, 500, 1, 180,
            ["HD"], !string.IsNullOrWhiteSpace(replayPath), new string('c', 64), "cover.jpg",
            new PpScoreStatistics(400, 10, 1, 1, 0, 0), "", 0, true, "map.osu", replayPath);
        return (replay, set, difficulty);
    }

    private static ReplayAnalysisResult createAnalysis() => new(
        ReplayAnalysisProtocol.EngineVersion,
        "gameplay-clock",
        true,
        ReplayAnalysisProtocol.WallClockTimeoutMs,
        [],
        [new ReplayObjectJudgement(1, null, "HitCircle", 1000, 1000, "Great", "Great", 1004, 4, 1, new ReplayPoint(256, 192), new ReplayPoint(255, 191), 1, 2)],
        new ReplayJudgementSummary(1, 0, 0, 0, 0, 0),
        new ReplayAnalysisContentIdentity(new string('c', 64), new string('d', 64)));

    private static HttpResponseMessage jsonResponse(HttpStatusCode status, object payload) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json"),
    };

    private sealed class RecordingHandler(IReadOnlyList<HttpResponseMessage> responses) : HttpMessageHandler
    {
        private int index;
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.RequestUri!,
                request.Headers.Authorization?.ToString() ?? "",
                request.Headers.TryGetValues("Idempotency-Key", out IEnumerable<string>? keys) ? keys.Single() : "",
                request.Headers.TryGetValues("X-Content-SHA256", out IEnumerable<string>? hashes) ? hashes.Single() : "",
                body));
            if (index >= responses.Count)
                throw new InvalidOperationException("Unexpected HTTP request.");
            return responses[index++];
        }
    }

    private sealed record RecordedRequest(Uri Uri, string Authorization, string IdempotencyKey, string ContentHash, byte[] Body);

    private sealed class MemoryCredentialStore(HubCredential? value = null) : IHubCredentialStore
    {
        private HubCredential? credential = value;
        public HubCredential? Load() => credential;
        public Task SaveAsync(HubCredential next, CancellationToken cancellationToken = default)
        {
            credential = next;
            return Task.CompletedTask;
        }
        public void Clear() => credential = null;
    }

    private sealed class MemorySyncCache : IOsuHubSyncCache
    {
        public OsuHubSyncCacheEntry? Entry { get; set; }
        public OsuHubSyncCacheEntry? Find(string contentHash) => string.Equals(Entry?.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase) ? Entry : null;
        public Task SaveAsync(OsuHubSyncCacheEntry entry, CancellationToken cancellationToken = default)
        {
            Entry = entry;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUploader : IOsuHubUploader
    {
        public Task<OsuHubUploadResult> UploadAsync(OsuHubSyncRequest request, string? replayPath = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OsuHubUploadResult(
                "share-id",
                new Uri("https://hub.example/osu/replays/share-id"),
                request.Visibility,
                true,
                request.Replay?.UploadFile == true,
                false));
    }

    private sealed class SingleMapLibrary(LocalBeatmapSet set) : ILocalLibrarySource
    {
        public ValueTask<LocalLibraryPage<LocalBeatmapSet>> SearchBeatmapSetsAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LocalLibraryPage<LocalBeatmapSet>([set], 1, 0, query.Limit));

        public ValueTask<LocalLibraryPage<LocalReplay>> SearchReplaysAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LocalLibraryPage<LocalReplay>([], 0, 0, query.Limit));

        public void Invalidate()
        {
        }
    }

    private sealed class ReversingProtector : IHubSecretProtector
    {
        public byte[] Protect(byte[] value) => value.Reverse().ToArray();
        public byte[] Unprotect(byte[] value) => value.Reverse().ToArray();
    }
}

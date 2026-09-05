using System.Net;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Reflection;
using AimMod.Desktop.Hub;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.PpTargets;
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

    [TestCase(OsuHubVisibility.Private)]
    [TestCase(OsuHubVisibility.Unlisted)]
    [TestCase(OsuHubVisibility.Public)]
    public async Task SharingPreferencesDefaultPublicAndPersistExplicitChoices(OsuHubVisibility visibility)
    {
        string path = Path.Combine(temporaryDirectory, "sharing-preferences.json");
        var store = new FileHubSharingPreferenceStore(path);

        Assert.That(store.Load(), Is.EqualTo(HubSharingPreferences.Default));
        Assert.That(store.Load().Visibility, Is.EqualTo(OsuHubVisibility.Public));
        Assert.That(store.Load().UploadReplayFile, Is.False);
        Assert.That(store.Load().UploadAnalysis, Is.False);

        var selected = new HubSharingPreferences(visibility, true, true);
        await store.SaveAsync(selected);

        Assert.That(new FileHubSharingPreferenceStore(path).Load(), Is.EqualTo(selected));
    }

    [Test]
    public void NewSharingPanelsDefaultPublicWithoutOptingIntoPayloadUploads()
    {
        using var settings = new NativeHubSettingsPanel(null, null, null, null, null, null);
        using var share = new NativeHubReplaySharePanel(null, null, null, null, null, null);
        var selected = (osu.Framework.Bindables.Bindable<OsuHubVisibility>)typeof(NativeHubReplaySharePanel)
            .GetField("visibility", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(share)!;
        Assert.That(settings.PreferencesForTesting, Is.EqualTo(new HubSharingPreferences(OsuHubVisibility.Public, false, false)));
        Assert.That(selected.Value, Is.EqualTo(OsuHubVisibility.Public));
        (LocalReplay replay, _, _) = createLocalData();
        share.SetReplay(replay, hasAnalysis: true);
        Assert.That(selected.Value, Is.EqualTo(OsuHubVisibility.Public));
    }

    [Test]
    public async Task PublicPreferenceDoesNotChangeAnExistingPrivateQueuedShare()
    {
        string queuePath = Path.Combine(temporaryDirectory, "private-queue.json");
        var preferences = new FileHubSharingPreferenceStore(Path.Combine(temporaryDirectory, "sharing-preferences.json"));
        (LocalReplay replay, LocalBeatmapSet set, LocalBeatmapDifficulty difficulty) = createLocalData();
        OsuHubSyncRequest request = await OsuHubContractFactory.CreateAsync(new OsuHubSyncInput(
            replay, set, difficulty, new OsuHubProfile(42, "player"), null, OsuHubVisibility.Private));
        using var queue = new OsuHubUploadQueue(queuePath, new FakeUploader(), startWorker: false);
        HubUploadQueueItem original = await queue.EnqueueAsync(request, null, "Private replay");
        byte[] before = await File.ReadAllBytesAsync(queuePath);
        await preferences.SaveAsync(new HubSharingPreferences(OsuHubVisibility.Public, false, true));
        using var share = new NativeHubReplaySharePanel(null, null, queue, preferences, null, null);
        share.SetReplay(replay, hasAnalysis: true);
        Assert.Multiple(() =>
        {
            Assert.That(queue.Snapshot(), Has.Count.EqualTo(1));
            Assert.That(queue.Snapshot().Single(), Is.EqualTo(original));
            Assert.That(queue.Snapshot().Single().Request.Visibility, Is.EqualTo("private"));
            Assert.That(queue.Snapshot().Single().AttemptCount, Is.Zero);
            Assert.That(File.ReadAllBytes(queuePath), Is.EqualTo(before));
            Assert.That(preferences.Load(), Is.EqualTo(new HubSharingPreferences(OsuHubVisibility.Public, false, true)));
        });
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
    public async Task ShareHydratesMissingPPUsingDeferredProviderWithoutChangingPlayIdentity()
    {
        (LocalReplay replay, LocalBeatmapSet set, LocalBeatmapDifficulty difficulty) = createLocalData();
        replay = replay with { PerformancePoints = null };
        using var queue = new OsuHubUploadQueue(Path.Combine(temporaryDirectory, "hydrated.json"), new FakeUploader(), startWorker: false);
        ILocalScorePpHydrationService? current = null;
        var service = new OsuHubReplayShareService(new SingleMapLibrary(set), () => new OsuProfile(42, "player", "", null, null),
            new Dictionary<Guid, ReplayAnalysisResult>(), queue, ppHydrator: () => current);
        var hydrator = new SharePpHydrator(run => run with { PerformancePoints = 234.5, TotalScore = 1 });
        current = hydrator;
        using var cancellation = new CancellationTokenSource();

        HubUploadQueueItem item = await service.QueueAsync(new HubReplayShareSelection(replay, OsuHubVisibility.Public, false, false), cancellation.Token);
        OsuHubSyncRequest original = await OsuHubContractFactory.CreateAsync(new OsuHubSyncInput(replay, set, difficulty, new OsuHubProfile(42, "player"), null, OsuHubVisibility.Public));

        Assert.Multiple(() =>
        {
            Assert.That(hydrator.Calls, Is.EqualTo(1));
            Assert.That(hydrator.Token, Is.EqualTo(cancellation.Token));
            Assert.That(item.Request.Score.PerformancePoints, Is.EqualTo(234.5));
            Assert.That(item.Request.Score.TotalScore, Is.EqualTo(replay.TotalScore));
            Assert.That(item.Request.ContentHash, Is.EqualTo(original.ContentHash));
            Assert.That(replay.PerformancePoints, Is.Null);
        });
    }

    [TestCase(0)]
    [TestCase(321.5)]
    public async Task SharePreservesKnownPPWithoutConsultingHydrator(double pp)
    {
        (LocalReplay replay, LocalBeatmapSet set, _) = createLocalData();
        using var queue = new OsuHubUploadQueue(Path.Combine(temporaryDirectory, "known.json"), new FakeUploader(), startWorker: false);
        var service = new OsuHubReplayShareService(new SingleMapLibrary(set), () => new OsuProfile(42, "player", "", null, null),
            new Dictionary<Guid, ReplayAnalysisResult>(), queue, ppHydrator: () => throw new AssertionException("Known PP must not be recalculated."));
        HubUploadQueueItem item = await service.QueueAsync(new HubReplayShareSelection(replay with { PerformancePoints = pp }, OsuHubVisibility.Public, false, false));
        Assert.That(item.Request.Score.PerformancePoints, Is.EqualTo(pp));
    }

    [TestCase("null")]
    [TestCase("negative")]
    [TestCase("nan")]
    [TestCase("infinity")]
    [TestCase("other-score")]
    [TestCase("other-origin")]
    [TestCase("no-service")]
    public async Task ShareNeverFabricatesPPFromUnavailableOrUnrelatedHydration(string scenario)
    {
        (LocalReplay replay, LocalBeatmapSet set, _) = createLocalData();
        var hydrator = new SharePpHydrator(run => scenario switch
        {
            "negative" => run with { PerformancePoints = -1 },
            "nan" => run with { PerformancePoints = double.NaN },
            "infinity" => run with { PerformancePoints = double.PositiveInfinity },
            "other-score" => run with { ScoreId = Guid.NewGuid(), PerformancePoints = 500 },
            "other-origin" => run with { Origin = (LocalLibraryOrigin)((int)run.Origin + 1), PerformancePoints = 500 },
            _ => run with { PerformancePoints = null },
        });
        using var queue = new OsuHubUploadQueue(Path.Combine(temporaryDirectory, "unknown.json"), new FakeUploader(), startWorker: false);
        var service = new OsuHubReplayShareService(new SingleMapLibrary(set), () => new OsuProfile(42, "player", "", null, null),
            new Dictionary<Guid, ReplayAnalysisResult>(), queue, ppHydrator: () => scenario == "no-service" ? null : hydrator);
        HubUploadQueueItem item = await service.QueueAsync(new HubReplayShareSelection(replay with { PerformancePoints = null }, OsuHubVisibility.Public, false, false));
        Assert.That(item.Request.Score.PerformancePoints, Is.Null);
    }

    private sealed class SharePpHydrator(Func<LocalReplay, LocalReplay> calculate) : ILocalScorePpHydrationService
    {
        public int Calls { get; private set; }
        public CancellationToken Token { get; private set; }

        public Task<LocalScorePpHydrationResult> HydrateAsync(IReadOnlyList<LocalReplay> runs,
            CancellationToken cancellationToken = default, IProgress<LocalScorePpHydrationProgress>? progress = null)
        {
            Calls++;
            Token = cancellationToken;
            Assert.That(runs, Has.Count.EqualTo(1));
            return Task.FromResult(new LocalScorePpHydrationResult([calculate(runs[0])], 0, 0, 1, 0));
        }
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

    [Test]
    public async Task AutomaticSharingPreferencesRequireOptInAndRotateGenerationOnlyOnEnable()
    {
        var store = new FileHubSharingPreferenceStore(Path.Combine(temporaryDirectory, "prefs.json"));
        Assert.That(store.Load().AutomaticSharingEnabled, Is.False);
        await store.SaveAsync(store.Load() with { AutomaticSharingEnabled = true, MinimumPp = 150, MinimumAccuracy = 97.5 });
        HubSharingPreferences enabled = store.Load();
        Assert.That(enabled.AutomaticSharingGeneration, Is.Not.EqualTo(Guid.Empty));
        await store.UpdateAsync(previous => previous with { Visibility = OsuHubVisibility.Unlisted });
        Assert.That(store.Load().AutomaticSharingGeneration, Is.EqualTo(enabled.AutomaticSharingGeneration));
        Assert.That(store.Load().MinimumAccuracy, Is.EqualTo(97.5));
        await store.SaveAsync(store.Load() with { AutomaticSharingEnabled = false });
        await store.UpdateAsync(previous => previous with { UploadAnalysis = true });
        Assert.That(store.Load().AutomaticSharingEnabled, Is.False);
        await store.SaveAsync(store.Load() with { AutomaticSharingEnabled = true });
        Assert.That(store.Load().AutomaticSharingGeneration, Is.Not.EqualTo(enabled.AutomaticSharingGeneration));
    }

    [Test]
    public async Task AutomaticSharingNeverBackfillsLaterHistoryPagesAndUsesInclusiveThresholds()
    {
        var preferences = new FileHubSharingPreferenceStore(Path.Combine(temporaryDirectory, "prefs.json"));
        (LocalReplay replay, LocalBeatmapSet set, _) = createLocalData();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var queue = new OsuHubUploadQueue(Path.Combine(temporaryDirectory, "queue.json"), new FakeUploader(), false);
        var share = new OsuHubReplayShareService(new SingleMapLibrary(set), () => new OsuProfile(42, "player", "", null, null),
            new Dictionary<Guid, ReplayAnalysisResult>(), queue);
        var automatic = new HubAutomaticShareService(Path.Combine(temporaryDirectory, "auto.json"), preferences, share,
            () => new HubAutomaticShareAccount("hub:player", 42, "player"), clock);
        await automatic.ObserveAsync([replay]);
        Assert.That(File.Exists(Path.Combine(temporaryDirectory, "auto.json")), Is.False, "OFF must not establish an upload baseline.");
        await preferences.SaveAsync(new HubSharingPreferences(AutomaticSharingEnabled: true, MinimumPp: 180, MinimumAccuracy: 98,
            UploadReplayFile: true, UploadAnalysis: true));
        await automatic.ObserveAsync([]); // A limited or empty initial page must still exclude ALL old history.
        clock.Now = clock.Now.AddMinutes(1);
        LocalReplay eligible = replay with { ScoreId = Guid.NewGuid(), PlayedAt = clock.Now, HasReplayFile = false, ReplayPath = "" };
        LocalReplay lowerPp = eligible with { ScoreId = Guid.NewGuid(), PlayedAt = clock.Now.AddSeconds(-1), PerformancePoints = 179.9 };
        LocalReplay lowerAccuracy = eligible with { ScoreId = Guid.NewGuid(), PlayedAt = clock.Now.AddSeconds(-2), Accuracy = .979 };
        LocalReplay missingPp = eligible with { ScoreId = Guid.NewGuid(), PlayedAt = clock.Now.AddSeconds(-3), PerformancePoints = null };
        LocalReplay foreign = eligible with { ScoreId = Guid.NewGuid(), PlayedAt = clock.Now.AddSeconds(-4), Player = "someone else" };
        await automatic.ObserveAsync([replay, eligible, lowerPp, lowerAccuracy, missingPp, foreign]);
        HubUploadQueueItem item = queue.Snapshot().Single();
        Assert.Multiple(() =>
        {
            Assert.That(item.Request.Visibility, Is.EqualTo("public"));
            Assert.That(item.Request.Replay, Is.Null, "Scores with no replay can share metadata when automatic replay attachment is optional.");
            Assert.That(item.Request.Analysis, Is.Null, "Optional analysis can be attached only when available.");
            Assert.That(item.AutomaticAccountScope, Is.Not.Empty);
            Assert.That(item.AutomaticGeneration, Is.EqualTo(preferences.Load().AutomaticSharingGeneration));
        });
        await preferences.UpdateAsync(previous => previous with { MinimumPp = 0, MinimumAccuracy = 0 });
        await automatic.ObserveAsync([lowerPp, lowerAccuracy]);
        Assert.That(queue.Snapshot(), Has.Count.EqualTo(1), "Lowering thresholds must not retrospectively share observed plays.");
        await automatic.ObserveAsync([missingPp with { PerformancePoints = 180 }]);
        Assert.That(queue.Snapshot(), Has.Count.EqualTo(2), "A new play awaiting PP calculation can qualify once measured.");
    }

    [Test]
    public async Task AutomaticSharingRestartsAndReEnablesEstablishFreshBaselines()
    {
        var preferences = new FileHubSharingPreferenceStore(Path.Combine(temporaryDirectory, "prefs.json"));
        await preferences.SaveAsync(new HubSharingPreferences(AutomaticSharingEnabled: true));
        (LocalReplay replay, LocalBeatmapSet set, _) = createLocalData();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var queue = new OsuHubUploadQueue(Path.Combine(temporaryDirectory, "queue.json"), new FakeUploader(), false);
        var share = new OsuHubReplayShareService(new SingleMapLibrary(set), () => new OsuProfile(42, "player", "", null, null),
            new Dictionary<Guid, ReplayAnalysisResult>(), queue);
        HubAutomaticShareService create() => new(Path.Combine(temporaryDirectory, "auto.json"), preferences, share,
            () => new HubAutomaticShareAccount("hub:player", 42, "player"), clock);
        var automatic = create();
        await automatic.ObserveAsync([]);
        clock.Now = clock.Now.AddMinutes(1);
        LocalReplay first = replay with { PlayedAt = clock.Now };
        await automatic.ObserveAsync([first]);
        Assert.That(queue.Snapshot(), Has.Count.EqualTo(1));
        clock.Now = clock.Now.AddMinutes(1);
        LocalReplay whileClosed = replay with { ScoreId = Guid.NewGuid(), PlayedAt = clock.Now };
        automatic = create();
        await automatic.ObserveAsync([first, whileClosed]);
        clock.Now = clock.Now.AddMinutes(1);
        await automatic.ObserveAsync([first, whileClosed]);
        Assert.That(queue.Snapshot(), Has.Count.EqualTo(1));
        await preferences.UpdateAsync(previous => previous with { AutomaticSharingEnabled = false });
        LocalReplay whileDisabled = replay with { ScoreId = Guid.NewGuid(), PlayedAt = clock.Now };
        await automatic.ObserveAsync([whileDisabled]);
        await preferences.UpdateAsync(previous => previous with { AutomaticSharingEnabled = true });
        await automatic.ObserveAsync([]);
        await automatic.ObserveAsync([whileDisabled]);
        Assert.That(queue.Snapshot(), Has.Count.EqualTo(1));
        clock.Now = clock.Now.AddMinutes(1);
        await automatic.ObserveAsync([replay with { ScoreId = Guid.NewGuid(), PlayedAt = clock.Now }]);
        Assert.That(queue.Snapshot(), Has.Count.EqualTo(2));
    }

    [Test]
    public async Task AutomaticQueueDeduplicationSurvivesTrimmingAndRestartAndIsAccountScoped()
    {
        string path = Path.Combine(temporaryDirectory, "queue.json");
        (LocalReplay replay, LocalBeatmapSet set, LocalBeatmapDifficulty difficulty) = createLocalData();
        OsuHubSyncRequest request = await OsuHubContractFactory.CreateAsync(new OsuHubSyncInput(
            replay, set, difficulty, new OsuHubProfile(42, "player"), null, OsuHubVisibility.Public));
        Guid generation = Guid.NewGuid();
        using (var queue = new OsuHubUploadQueue(path, new FakeUploader(), false))
        {
            HubUploadQueueItem original = (await queue.TryEnqueueAutomaticAsync(request, null, "Auto", "account-a:score", "account-a", generation))!;
            await queue.CancelAsync(original.Id);
            for (int index = 0; index < OsuHubUploadQueue.MaximumEntries; index++)
            {
                HubUploadQueueItem next = await queue.EnqueueAsync(request with
                {
                    Score = request.Score with { ClientScoreId = "manual:" + index },
                }, null, "Manual");
                await queue.CancelAsync(next.Id);
            }
            Assert.That(queue.Snapshot().Any(item => item.Id == original.Id), Is.False);
        }
        using var restored = new OsuHubUploadQueue(path, new FakeUploader(), false);
        Assert.That(await restored.TryEnqueueAutomaticAsync(request, null, "Auto", "account-a:score", "account-a", generation), Is.Null);
        Assert.That(await restored.TryEnqueueAutomaticAsync(request, null, "Auto", "account-b:score", "account-b", generation), Is.Not.Null);
    }

    [Test]
    public async Task AutomaticQueueWorkerWaitsForCurrentConsentAndMatchingAccount()
    {
        var preferences = new FileHubSharingPreferenceStore(Path.Combine(temporaryDirectory, "prefs.json"));
        await preferences.SaveAsync(new HubSharingPreferences(AutomaticSharingEnabled: true));
        (LocalReplay replay, LocalBeatmapSet set, _) = createLocalData();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        string path = Path.Combine(temporaryDirectory, "queue.json");
        HubAutomaticShareAccount? account = new("hub:player", 42, "player");
        using (var queue = new OsuHubUploadQueue(path, new FakeUploader(), false))
        {
            var share = new OsuHubReplayShareService(new SingleMapLibrary(set), () => new OsuProfile(42, "player", "", null, null),
                new Dictionary<Guid, ReplayAnalysisResult>(), queue);
            var automatic = new HubAutomaticShareService(Path.Combine(temporaryDirectory, "auto.json"), preferences, share, () => account, clock);
            await automatic.ObserveAsync([]);
            clock.Now = clock.Now.AddMinutes(1);
            await automatic.ObserveAsync([replay with { PlayedAt = clock.Now }]);
        }
        using var restored = new OsuHubUploadQueue(path, new FakeUploader());
        await Task.Delay(50);
        Assert.That(restored.Snapshot().Single().AttemptCount, Is.Zero, "No automatic queue entry may upload before consent/account guards are attached.");
        var restoredShare = new OsuHubReplayShareService(new SingleMapLibrary(set), () => new OsuProfile(42, "player", "", null, null),
            new Dictionary<Guid, ReplayAnalysisResult>(), restored);
        account = new("other-hub-account", 42, "player");
        var restoredAutomatic = new HubAutomaticShareService(Path.Combine(temporaryDirectory, "auto.json"), preferences, restoredShare, () => account, clock);
        await restoredAutomatic.ObserveAsync([]);
        await Task.Delay(50);
        Assert.That(restored.Snapshot().Single().AttemptCount, Is.Zero);
        account = new("hub:player", 42, "player");
        await restoredAutomatic.ObserveAsync([]);
        Assert.That(SpinWait.SpinUntil(() => restored.Snapshot().Single().Status == HubUploadQueueStatus.Completed, TimeSpan.FromSeconds(3)), Is.True);
    }

    [Test]
    public async Task LocalThenOnlineEnrichmentQueuesOneShareOnly()
    {
        var preferences = new FileHubSharingPreferenceStore(Path.Combine(temporaryDirectory, "prefs.json"));
        await preferences.SaveAsync(new HubSharingPreferences(AutomaticSharingEnabled: true));
        (LocalReplay replay, LocalBeatmapSet set, _) = createLocalData();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var queue = new OsuHubUploadQueue(Path.Combine(temporaryDirectory, "queue.json"), new FakeUploader(), false);
        var share = new OsuHubReplayShareService(new SingleMapLibrary(set), () => new OsuProfile(42, "player", "", null, null),
            new Dictionary<Guid, ReplayAnalysisResult>(), queue);
        var automatic = new HubAutomaticShareService(Path.Combine(temporaryDirectory, "auto.json"), preferences, share,
            () => new HubAutomaticShareAccount("hub:player", 42, "player"), clock);
        await automatic.ObserveAsync([]);
        clock.Now = clock.Now.AddMinutes(1);
        LocalReplay local = replay with { PlayedAt = clock.Now };
        await automatic.ObserveAsync([local]);
        LocalReplay enriched = local with { OnlineScoreId = 3456 };
        await automatic.ObserveAsync([enriched]);
        LocalReplay onlineOnly = enriched with { ScoreId = Guid.NewGuid(), IsLocallyStored = false, TotalScore = 999999 };
        await automatic.ObserveAsync([onlineOnly]);
        // Even an online refresh arriving before the local row receives its online ID is the same play.
        clock.Now = clock.Now.AddMinutes(1);
        LocalReplay second = local with { ScoreId = Guid.NewGuid(), PlayedAt = clock.Now };
        await automatic.ObserveAsync([second]);
        await automatic.ObserveAsync([second with { ScoreId = Guid.NewGuid(), OnlineScoreId = 7890, IsLocallyStored = false }]);
        Assert.That(queue.Snapshot(), Has.Count.EqualTo(2));
    }

    [Test]
    public async Task AutomaticUploadVerifiesAccountBeforeNetworkAndUsesAccountScopedCache()
    {
        (LocalReplay replay, LocalBeatmapSet set, LocalBeatmapDifficulty difficulty) = createLocalData();
        OsuHubSyncRequest request = await OsuHubContractFactory.CreateAsync(new OsuHubSyncInput(
            replay, set, difficulty, new OsuHubProfile(42, "player"), null, OsuHubVisibility.Public));
        var cache = new MemorySyncCache
        {
            Entry = new OsuHubSyncCacheEntry(request.ContentHash, "public", "wrong-account-result", "", false, DateTimeOffset.UtcNow),
        };
        var handler = new RecordingHandler([
            jsonResponse(HttpStatusCode.Created, new OsuHubSyncResponse("osu_" + new string('a', 32), "public", true, false)),
        ]);
        var credentials = new MemoryCredentialStore(new HubCredential("test-secret", "linked-player", DateTimeOffset.UtcNow));
        var client = new OsuHubSyncClient(new HttpClient(handler), credentials, cache, new Uri("https://hub.example/"));
        string scope = new HubAutomaticShareAccount("https://hub.example/|linked-player", 42, "player").StorageScope;
        Assert.ThrowsAsync<InvalidOperationException>(() => client.UploadAutomaticAsync(request, "other-account"));
        Assert.That(handler.Requests, Is.Empty);
        OsuHubUploadResult first = await client.UploadAutomaticAsync(request, scope);
        OsuHubUploadResult second = await client.UploadAutomaticAsync(request, scope);
        Assert.That(first.FromLocalCache, Is.False);
        Assert.That(second.FromLocalCache, Is.True);
        Assert.That(handler.Requests, Has.Count.EqualTo(1));
        Assert.That(cache.Entry!.ContentHash, Does.StartWith(scope + ":"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task BlankLazerReplayPathIsResolvedAndSpooledBeforeLeaseDisposal(bool automatic)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("exported lazer replay bytes");
        string temporaryReplay = Path.Combine(temporaryDirectory, "temporary-export.osr");
        await File.WriteAllBytesAsync(temporaryReplay, bytes);
        string spool = Path.Combine(temporaryDirectory, "hub-spool");
        var resolver = new LeasedReplayResolver(temporaryReplay, () =>
        {
            Assert.That(Directory.GetFiles(spool, "*.osr"), Has.Length.EqualTo(1));
            Assert.That(File.ReadAllBytes(Directory.GetFiles(spool, "*.osr").Single()), Is.EqualTo(bytes));
        });
        (LocalReplay replay, LocalBeatmapSet set, _) = createLocalData();
        replay = replay with { HasReplayFile = true, ReplayPath = "" };
        string queuePath = Path.Combine(temporaryDirectory, "queue.json");
        ILocalReplayOpenService? currentResolver = null;
        HubUploadQueueItem queued;
        using (var queue = new OsuHubUploadQueue(queuePath, new FakeUploader(), false))
        {
            var service = new OsuHubReplayShareService(new SingleMapLibrary(set), () => new OsuProfile(42, "player", "", null, null),
                new Dictionary<Guid, ReplayAnalysisResult>(), queue, () => currentResolver, spool);
            currentResolver = resolver; // The source can connect after Hub services are constructed.
            queued = automatic
                ? (await service.QueueAutomaticAsync(replay, new HubSharingPreferences(UploadReplayFile: true,
                    AutomaticSharingEnabled: true, AutomaticSharingGeneration: Guid.NewGuid()), 42, "scope", "dedup", () => true))!
                : await service.QueueAsync(new HubReplayShareSelection(replay, OsuHubVisibility.Public, true, false));
            Assert.That(resolver.Disposed, Is.True);
            Assert.That(File.Exists(temporaryReplay), Is.False);
            Assert.That(queued.ReplayPath, Does.StartWith(spool));
            Assert.That(File.ReadAllBytes(queued.ReplayPath), Is.EqualTo(bytes));
            Assert.That(queued.Request.Replay!.Sha256, Is.EqualTo(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
        }
        using var restored = new OsuHubUploadQueue(queuePath, new FakeUploader(), false);
        Assert.That(restored.Snapshot().Single().ReplayPath, Is.EqualTo(queued.ReplayPath));
        Assert.That(File.ReadAllBytes(restored.Snapshot().Single().ReplayPath), Is.EqualTo(bytes));
        Assert.That(await restored.CancelAsync(queued.Id), Is.True);
        Assert.That(await restored.RetryAsync(queued.Id), Is.True);
        Assert.That(File.Exists(queued.ReplayPath), Is.True);
    }

    [Test]
    public async Task StableReplayIsSpooledWithoutPlaybackOrBeatmapResolution()
    {
        string replayPath = Path.Combine(temporaryDirectory, "stable.osr");
        await File.WriteAllTextAsync(replayPath, "stable replay bytes");
        (LocalReplay replay, LocalBeatmapSet set, _) = createLocalData(replayPath);
        replay = replay with { Origin = LocalLibraryOrigin.Stable, BeatmapPath = "missing-map.osu" };
        using var queue = new OsuHubUploadQueue(Path.Combine(temporaryDirectory, "queue.json"), new FakeUploader(), false);
        var service = new OsuHubReplayShareService(new SingleMapLibrary(set), () => new OsuProfile(42, "player", "", null, null),
            new Dictionary<Guid, ReplayAnalysisResult>(), queue, () => throw new AssertionException("A stable file must not invoke playback."),
            Path.Combine(temporaryDirectory, "spool"));
        HubUploadQueueItem item = await service.QueueAsync(new HubReplayShareSelection(replay, OsuHubVisibility.Public, true, false));
        File.Delete(replayPath);
        Assert.That(File.ReadAllText(item.ReplayPath), Is.EqualTo("stable replay bytes"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TrulyUnavailableRequestedReplayFailsWithoutDowngradingOrQueueing(bool automatic)
    {
        (LocalReplay replay, LocalBeatmapSet set, _) = createLocalData();
        replay = replay with { HasReplayFile = true, ReplayPath = "" };
        var resolver = new LeasedReplayResolver(Path.Combine(temporaryDirectory, "missing.osr"));
        using var queue = new OsuHubUploadQueue(Path.Combine(temporaryDirectory, "queue.json"), new FakeUploader(), false);
        var service = new OsuHubReplayShareService(new SingleMapLibrary(set), () => new OsuProfile(42, "player", "", null, null),
            new Dictionary<Guid, ReplayAnalysisResult>(), queue, () => resolver, Path.Combine(temporaryDirectory, "spool"));
        Assert.ThrowsAsync<FileNotFoundException>(async () =>
        {
            if (automatic)
                await service.QueueAutomaticAsync(replay, new HubSharingPreferences(UploadReplayFile: true, AutomaticSharingEnabled: true,
                    AutomaticSharingGeneration: Guid.NewGuid()), 42, "scope", "key", () => true);
            else
                await service.QueueAsync(new HubReplayShareSelection(replay, OsuHubVisibility.Public, true, false));
        });
        Assert.That(queue.Snapshot(), Is.Empty);
        Assert.That(resolver.Disposed, Is.True);
    }

    [Test]
    public async Task LazerReplayOnlyResolverDoesNotRequestBeatmapAudioOrBackground()
    {
        (LocalReplay replay, _, _) = createLocalData();
        replay = replay with { HasReplayFile = true, ReplayPath = "", BeatmapHash = "" };
        var runtime = new ReplayOnlyAssetRuntime();
        ILocalReplayOpenService resolver = new CompositeLocalReplayOpenService(new ExternalLazerReplayOpenService(
            temporaryDirectory, new ExternalLazerAssetClient(runtime)));
        string staged;
        await using (IReplayFileLease lease = await resolver.OpenReplayFileAsync(replay))
        {
            staged = lease.ReplayPath;
            Assert.That(File.ReadAllText(staged), Is.EqualTo("replay only"));
            Assert.That(runtime.RequestedScoreIds, Is.EqualTo(new[] { replay.ScoreId }));
        }
        Assert.That(File.Exists(staged), Is.False);
    }

    [Test]
    public void ChangingReplayCancelsPreparationAndClearsOldShareActions()
    {
        using var panel = new NativeHubReplaySharePanel(null, null, null, null, null, null);
        (LocalReplay replay, _, _) = createLocalData();
        using var preparing = new CancellationTokenSource();
        CancellationToken token = preparing.Token;
        const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(NativeHubReplaySharePanel).GetField("preparing", fields)!.SetValue(panel, preparing);
        typeof(NativeHubReplaySharePanel).GetField("shareUrl", fields)!.SetValue(panel, "https://hub.example/old");
        typeof(NativeHubReplaySharePanel).GetField("queueItemId", fields)!.SetValue(panel, Guid.NewGuid());
        foreach (string name in new[] { "copyButton", "openButton", "cancelRetryButton" })
        {
            var button = (osu.Game.Graphics.UserInterface.OsuButton)typeof(NativeHubReplaySharePanel).GetField(name, fields)!.GetValue(panel)!;
            button.Alpha = 1;
            button.Enabled.Value = true;
        }
        panel.SetReplay(replay, false);
        Assert.That(token.IsCancellationRequested, Is.True);
        Assert.That(typeof(NativeHubReplaySharePanel).GetField("preparing", fields)!.GetValue(panel), Is.Null);
        Assert.That(typeof(NativeHubReplaySharePanel).GetField("shareUrl", fields)!.GetValue(panel), Is.EqualTo(""));
        Assert.That(typeof(NativeHubReplaySharePanel).GetField("queueItemId", fields)!.GetValue(panel), Is.Null);
        foreach (string name in new[] { "copyButton", "openButton", "cancelRetryButton" })
        {
            var button = (osu.Game.Graphics.UserInterface.OsuButton)typeof(NativeHubReplaySharePanel).GetField(name, fields)!.GetValue(panel)!;
            Assert.That(button.Alpha, Is.Zero);
            Assert.That(button.Enabled.Value, Is.False);
        }
    }

    private sealed class LeasedReplayResolver(string replayPath, Action? onDispose = null) : ILocalReplayOpenService
    {
        public bool Disposed { get; private set; }
        public Task<IPlayableReplayBundle> OpenAsync(LocalReplay replay, CancellationToken cancellationToken = default) =>
            Task.FromResult<IPlayableReplayBundle>(new Lease(replayPath, () =>
            {
                onDispose?.Invoke();
                Disposed = true;
                File.Delete(replayPath);
            }));

        private sealed record Lease(string ReplayPath, Action Dispose) : IPlayableReplayBundle
        {
            public string BeatmapPath => "";
            public ReplayOpenRequest OpenRequest => throw new AssertionException("Sharing must not start playback.");
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class ReplayOnlyAssetRuntime : IRuntimeRequestClient
    {
        public IReadOnlyList<Guid> RequestedScoreIds { get; private set; } = [];
        public Task<RuntimeResponse> SendAsync(RuntimeRequest request, CancellationToken cancellationToken = default)
        {
            ExternalLazerAssetResolveRequest input = request.Payload!.Value.Deserialize<ExternalLazerAssetResolveRequest>(RuntimeProtocol.JsonOptions)!;
            Assert.That(input.BeatmapHashes, Is.Empty);
            RequestedScoreIds = input.ScoreIds;
            byte[] bytes = Encoding.UTF8.GetBytes("replay only");
            string path = Path.Combine(input.StagingDirectory, "replay.osr");
            File.WriteAllBytes(path, bytes);
            var asset = new ExternalLazerResolvedAsset("Replay", input.ScoreIds.Single().ToString(), "replay.osr",
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), path, bytes.Length);
            var result = new ExternalLazerAssetResolveResult([asset], [], [], []);
            return Task.FromResult(new RuntimeResponse(request.Id, RuntimeProtocol.CurrentVersion, true,
                JsonSerializer.SerializeToElement(result, RuntimeProtocol.JsonOptions)));
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
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
        public Task<OsuHubUploadResult> UploadAutomaticAsync(OsuHubSyncRequest request, string accountScope,
            string? replayPath = null, CancellationToken cancellationToken = default) => UploadAsync(request, replayPath, cancellationToken);

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

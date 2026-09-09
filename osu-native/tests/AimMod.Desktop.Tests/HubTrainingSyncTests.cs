using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AimMod.Desktop.Hub;
using AimMod.Desktop.Trainers;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class HubTrainingSyncTests
{
    private string directory = null!;
    private string queue => Path.Combine(directory, "queue.json");
    private FileHubSharingPreferenceStore preferences = null!;
    private readonly Credentials credentials = new();
    private long? account;
    private Handler handler = null!;
    private HttpClient client = null!;
    private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
    private HubTrainingSyncService service() => new(queue, client, new Uri("https://training.example/"), credentials, preferences, () => account);
    [SetUp] public async Task SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), "aimmod-training-tests-" + Guid.NewGuid().ToString("N"));
        preferences = new(Path.Combine(directory, "preferences.json"));
        await preferences.SaveAsync(new(TrainingSyncEnabled: true, TrainingPublicSharing: false));
        credentials.Value = new("synthetic-token", "practice-player", DateTimeOffset.UtcNow);
        account = 123; handler = new(); client = new(handler);
    }
    [TearDown] public void TearDown() { client.Dispose(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    private static TrainerResult result() => new(Guid.NewGuid(), DateTimeOffset.UtcNow,
        new TrainerSettings(), 100, 98, 90, 0, 1, 0, 18, 2, Engine: "osu-moving-v2", Accuracy: 97, PlayedSeconds: 30, Demand: new(4, 90, 200, 8));
    private async Task waitQueued()
    {
        for (int i = 0; i < 100 && !File.Exists(queue); i++) await Task.Delay(10);
        Assert.That(File.Exists(queue), Is.True);
    }

    [Test] public async Task FreshPreferencesShareNewSessionsWithoutOpeningSettingsAndSurviveRestart()
    {
        string path = Path.Combine(directory, "fresh.json");
        preferences = new(path);
        var initial = preferences.Load();
        Assert.That(initial.TrainingSyncEnabled && initial.TrainingPublicSharing, Is.True);
        Assert.That(initial.TrainingSyncGeneration, Is.Not.EqualTo(Guid.Empty));
        service().BeginSession()!(result()); await waitQueued();
        preferences = new(path);
        Assert.That(preferences.Load().TrainingSyncGeneration, Is.EqualTo(initial.TrainingSyncGeneration));
        await service().FlushAsync();
        Assert.That(handler.Requests.Single(), Does.Contain("\"visibility\":\"public\""));
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public void ExistingExplicitPrivacyChoicesArePreserved(bool sync, bool publicly)
    {
        string path = Path.Combine(directory, "existing.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { version = 1, preferences = new { trainingSyncEnabled = sync, trainingPublicSharing = publicly } }, json));
        var saved = new FileHubSharingPreferenceStore(path).Load();
        Assert.That(saved.TrainingSyncEnabled, Is.EqualTo(sync));
        Assert.That(saved.TrainingPublicSharing, Is.EqualTo(publicly));
    }

    [TestCase("not json")]
    [TestCase("{\"version\":999,\"preferences\":{}}")]
    public void UnreadablePreferencesDoNotEnablePublicSync(string contents)
    {
        string path = Path.Combine(directory, "invalid.json"); File.WriteAllText(path, contents);
        var saved = new FileHubSharingPreferenceStore(path).Load();
        Assert.That(saved.TrainingSyncEnabled || saved.TrainingPublicSharing, Is.False);
        Assert.That(File.ReadAllText(path), Is.EqualTo(contents));
    }
    [Test] public void ContractExcludesPrivateInputAndSourceData()
    {
        TrainerResult r = result() with { Settings = new(Music: "song", SongIdentity: "private-source-file", SongTitle: "private-title", Keys: "A / S", OffsetMs: 37) };
        string payload = JsonSerializer.Serialize(HubTrainingSession.FromResult(r, false), json);
        Assert.Multiple(() => { Assert.That(payload, Does.Not.Contain("private-source-file")); Assert.That(payload, Does.Not.Contain("private-title")); Assert.That(payload, Does.Not.Contain("A / S")); Assert.That(payload, Does.Not.Contain("offsetMs")); Assert.That(payload, Does.Contain("\"visibility\":\"private\"")); });
        var before = HubTrainingSession.FromResult(r, false);
        var after = HubTrainingSession.FromResult(r with { Settings = r.Settings with { OffsetMs = 42 } }, false);
        Assert.That(after.Setup.ConfigurationHash, Is.Not.EqualTo(before.Setup.ConfigurationHash));
    }
    [Test] public void DefaultMovementKeepsExistingConfigurationHashAndCompactMovementSeparatesCohorts()
    {
        // Frozen wire shape from TrainerSettings before guided practice was introduced.
        const string previousJson = """
            {"Kind":0,"Bpm":120,"Seconds":30,"OffsetMs":0,"Keys":"Z / X","Music":"cues","Cue":"pulse","SongIdentity":"","SongStartSeconds":0,"SongTitle":"","AimStyle":0,"AimSpacing":100,"CircleSize":4,"PatternSeed":0,"Pattern":0,"NoteSpeed":0,"Sliders":0,"SliderBeats":1,"PathStyle":0,"RandomizePatterns":false,"ApproachRate":7,"ReactionDelay":0,"SkillLimits":null,"TempoDescription":"120 BPM"}
            """;
        string previousHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(previousJson))).ToLowerInvariant();
        var original = result();
        string currentHash = HubTrainingSession.FromResult(original, false).Setup.ConfigurationHash;
        Assert.That(currentHash, Is.EqualTo(previousHash));
        Assert.That(HubTrainingSession.FromResult(original with { Settings = original.Settings with { MovementScale = 1, PatternSeed = 42 } }, false).Setup.ConfigurationHash,
            Is.EqualTo(previousHash));
        string compactHash = HubTrainingSession.FromResult(original with { Settings = original.Settings with { MovementScale = .55 } }, false).Setup.ConfigurationHash;
        Assert.That(compactHash, Is.Not.EqualTo(previousHash));
        Assert.That(HubTrainingSession.FromResult(original with { Settings = original.Settings with { MovementScale = .7 } }, false).Setup.ConfigurationHash,
            Is.Not.EqualTo(compactHash));
    }
    [Test] public void GuidedBaselineAndLocalResultMetadataNeverEnterPublicPayload()
    {
        var planId = Guid.NewGuid();
        var baseline = new TrainerSettings(Music: "song", SongIdentity: "private-guided-source", SongTitle: "private-guided-title", Keys: "A / S", OffsetMs: 37);
        var guided = result() with { Settings = baseline with { MovementScale = .55 }, JudgementMisses = 2,
            GuidedRun = new(planId, TrainerGuidedFocus.MovementComparison, 1, "Compact movement", baseline) };
        string payload = JsonSerializer.Serialize(HubTrainingSession.FromResult(guided, true), json);
        foreach (string privateValue in new[] { "private-guided-source", "private-guided-title", "A / S", "offsetMs", "guidedRun", "baseline", "judgementMisses", "assisted", planId.ToString() })
            Assert.That(payload, Does.Not.Contain(privateValue));
        Assert.That(payload, Does.Contain("\"visibility\":\"public\""));
    }
    [Test] public async Task AssistedPracticeCannotBePublishedAsOrdinaryTraining()
    {
        var assisted = result() with { Assisted = true };
        Assert.Throws<ArgumentException>(() => HubTrainingSession.FromResult(assisted, true));
        service().BeginSession()!(assisted);
        await service().FlushAsync();
        Assert.That(handler.Requests, Is.Empty);
        Assert.That(File.Exists(queue), Is.False);
    }
    [Test] public async Task CompletedSessionSurvivesRestartAndSendsAuthenticatedPrivatePayload()
    {
        service().BeginSession()!(result()); await waitQueued();
        await service().FlushAsync();
        Assert.That(handler.Requests, Has.Count.EqualTo(1));
        Assert.That(handler.Requests[0], Does.Contain("\"visibility\":\"private\""));
        Assert.That(handler.Authorization, Is.EqualTo("Bearer synthetic-token"));
        Assert.That(File.ReadAllText(queue), Is.EqualTo("[]"));
    }
    [Test] public async Task FailedUploadRemainsDurableAndDoesNotHammerServer()
    {
        handler.Status = HttpStatusCode.ServiceUnavailable;
        var sync = service(); sync.BeginSession()!(result()); await waitQueued(); await sync.FlushAsync(); await sync.FlushAsync();
        Assert.That(handler.Requests, Has.Count.EqualTo(1));
        Assert.That(JsonSerializer.Deserialize<HubTrainingSyncService.Pending[]>(File.ReadAllText(queue), json), Has.Length.EqualTo(1));
        handler.Status = HttpStatusCode.OK;
        var item = JsonSerializer.Deserialize<HubTrainingSyncService.Pending[]>(File.ReadAllText(queue), json)![0];
        File.WriteAllText(queue, JsonSerializer.Serialize(new[] { item with { NextAttempt = DateTimeOffset.MinValue } }, json));
        await service().FlushAsync(); Assert.That(handler.Requests, Has.Count.EqualTo(2)); Assert.That(File.ReadAllText(queue), Is.EqualTo("[]"));
    }
    [Test] public async Task SwitchingOsuOrHubAccountDoesNotUploadPendingPractice()
    {
        var sync = service(); sync.BeginSession()!(result()); await waitQueued();
        account = 456; await sync.FlushAsync(); Assert.That(handler.Requests, Is.Empty);
        account = 123; credentials.Value = credentials.Value! with { AccountLabel = "another-player" };
        await sync.FlushAsync(); Assert.That(handler.Requests, Is.Empty);
    }
    [Test] public async Task SessionStartAccountIsPreservedAcrossSwitch()
    {
        var record = service().BeginSession()!; account = 456; record(result());
        await service().FlushAsync(); Assert.That(handler.Requests, Is.Empty); Assert.That(File.Exists(queue), Is.False);
    }
    [Test] public async Task DisablingAndReenablingCannotPublishOldPendingSessions()
    {
        service().BeginSession()!(result()); await waitQueued();
        await preferences.UpdateAsync(p => p with { TrainingSyncEnabled = false });
        Assert.That(service().BeginSession(), Is.Null);
        await preferences.UpdateAsync(p => p with { TrainingSyncEnabled = true });
        await service().FlushAsync(); Assert.That(handler.Requests, Is.Empty); Assert.That(File.ReadAllText(queue), Is.EqualTo("[]"));
    }
    [Test] public async Task InvalidSuccessAcknowledgementDoesNotDropResult()
    {
        handler.Body = "{}"; service().BeginSession()!(result()); await waitQueued(); await service().FlushAsync();
        Assert.That(File.ReadAllText(queue), Is.Not.EqualTo("[]"));
    }
    [Test] public async Task SlowNetworkCannotDelayPersistingTheNextCompletedSession()
    {
        handler.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = service(); sync.BeginSession()!(result()); await waitQueued();
        Task upload = sync.FlushAsync(); await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        sync.BeginSession()!(result());
        Assert.That(JsonSerializer.Deserialize<HubTrainingSyncService.Pending[]>(File.ReadAllText(queue), json), Has.Length.EqualTo(2));
        handler.Release.SetResult(); await upload;
        Assert.That(JsonSerializer.Deserialize<HubTrainingSyncService.Pending[]>(File.ReadAllText(queue), json), Has.Length.EqualTo(1));
    }
    private sealed class Handler : HttpMessageHandler
    {
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Body = "{\"accepted\":1}";
        public string? Authorization;
        public List<string> Requests { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Release;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString(); Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            Started.TrySetResult(); if (Release is not null) await Release.Task.WaitAsync(cancellationToken);
            return new(Status) { Content = new StringContent(Body) };
        }
    }
    private sealed class Credentials : IHubCredentialStore
    {
        public HubCredential? Value;
        public HubCredential? Load() => Value;
        public Task SaveAsync(HubCredential credential, CancellationToken cancellationToken = default) { Value = credential; return Task.CompletedTask; }
        public void Clear() => Value = null;
    }
}

using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using AimMod.Osu.Worker;
using NUnit.Framework;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Osu.Objects;

namespace AimMod.Osu.Worker.Tests;

[TestFixture]
public sealed class ReplayAnalysisBackendTests
{
    private string temporaryDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"aimmod-worker-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(temporaryDirectory))
            Directory.Delete(temporaryDirectory, true);
    }

    [Test]
    public void AdvertisesReplayAnalysisAndExternalLibraryCapabilities()
    {
        var backend = createBackend(new RecordingEngine());

        RuntimeHello hello = backend.Describe();

        Assert.That(hello.Capabilities, Is.EqualTo(new[]
        {
            RuntimeCapabilities.ReplayAnalysis,
            RuntimeCapabilities.PerformanceCalculation,
            RuntimeCapabilities.ExternalLibraryCatalog,
            RuntimeCapabilities.ExternalLibraryAssets,
            RuntimeCapabilities.SkinRead,
        }));
    }

    [Test]
    public void AnalysisTrackCoversTheWholeBeatmap()
    {
        var beatmap = new Beatmap
        {
            HitObjects = { new HitCircle { StartTime = 42_000 } },
        };

        using var track = new AnalysisWorkingBeatmap(beatmap, null).LoadTrack();

        Assert.That(track.Length, Is.GreaterThanOrEqualTo(52_000),
            "Replay playback must not stall on the one-second fallback track.");
    }

    [Test]
    public async Task CalculatesPpThroughTheAdvertisedCommand()
    {
        var calculator = new RecordingPpCalculator();
        var backend = new ReplayAnalysisBackend(
            new RecordingEngine(),
            calculator,
            new ReplayInputValidator(),
            TimeSpan.FromSeconds(1));
        string beatmapPath = Path.Combine(temporaryDirectory, "map.osu");
        File.WriteAllText(beatmapPath, "osu file format v14");
        var statistics = new PpScoreStatistics(950, 40, 9, 1, 300, 2);
        const string modsJson = "[{\"acronym\":\"DT\",\"settings\":{\"speed_change\":1.25}}]";
        var request = new PpWhatIfRequest(temporaryDirectory, beatmapPath, new[] { "DT" }, 0.975, 1, 900, statistics, modsJson);

        JsonElement? payload = await backend.ExecuteAsync(
            RuntimeCommands.CalculatePp,
            JsonSerializer.SerializeToElement(request, RuntimeProtocol.JsonOptions),
            CancellationToken.None);

        PpWhatIfResult? result = payload?.Deserialize<PpWhatIfResult>(RuntimeProtocol.JsonOptions);
        Assert.Multiple(() =>
        {
            Assert.That(calculator.LastInput?.BeatmapPath, Is.EqualTo(beatmapPath));
            Assert.That(calculator.LastInput?.Mods, Is.EqualTo(new[] { "DT" }));
            Assert.That(calculator.LastInput?.Accuracy, Is.EqualTo(0.975));
            Assert.That(calculator.LastInput?.MissCount, Is.EqualTo(1));
            Assert.That(calculator.LastInput?.Statistics, Is.EqualTo(statistics));
            Assert.That(calculator.LastInput?.ModsJson, Is.EqualTo(modsJson));
            Assert.That(result?.PerformancePoints, Is.EqualTo(321.5));
            Assert.That(result?.StarRating, Is.EqualTo(6.2));
        });
    }

    [Test]
    public void RejectsInvalidPpCalculationInputBeforeCalculator()
    {
        var calculator = new RecordingPpCalculator();
        var backend = new ReplayAnalysisBackend(
            new RecordingEngine(),
            calculator,
            new ReplayInputValidator(),
            TimeSpan.FromSeconds(1));
        string beatmapPath = Path.Combine(temporaryDirectory, "map.txt");
        File.WriteAllText(beatmapPath, "not an osu file");
        var request = new PpWhatIfRequest(temporaryDirectory, beatmapPath, Array.Empty<string>(), 1.1);

        RuntimeCommandException exception = Assert.ThrowsAsync<RuntimeCommandException>(async () =>
            await backend.ExecuteAsync(
                RuntimeCommands.CalculatePp,
                JsonSerializer.SerializeToElement(request, RuntimeProtocol.JsonOptions),
                CancellationToken.None))!;

        Assert.Multiple(() =>
        {
            Assert.That(exception.Code, Is.EqualTo("input_invalid"));
            Assert.That(calculator.LastInput, Is.Null);
        });
    }

    [TestCase(1_000, 0.975, 2)]
    [TestCase(131, 0.95, 1)]
    [TestCase(10_000, 0.9237, 17)]
    public void GeneratedPpStatisticsUseTheClosestAchievableAccuracy(int objectCount, double targetAccuracy, int misses)
    {
        PpGeneratedStatistics statistics = OfficialPpWhatIfCalculator.GenerateStatistics(objectCount, targetAccuracy, misses);

        Assert.Multiple(() =>
        {
            Assert.That(statistics.Great + statistics.Ok + statistics.Meh + statistics.Miss, Is.EqualTo(objectCount));
            Assert.That(statistics.Miss, Is.EqualTo(misses));
            Assert.That(statistics.Accuracy, Is.EqualTo(targetAccuracy).Within(1.0 / (objectCount * 6)));
        });
    }

    [Test]
    public void GeneratedPpStatisticsRespectAccuracyCeilingImposedByMisses()
    {
        PpGeneratedStatistics statistics = OfficialPpWhatIfCalculator.GenerateStatistics(100, 1, 10);

        Assert.Multiple(() =>
        {
            Assert.That(statistics.Miss, Is.EqualTo(10));
            Assert.That(statistics.Great, Is.EqualTo(90));
            Assert.That(statistics.Accuracy, Is.EqualTo(0.9));
        });
    }

    [Test]
    public async Task SearchesExternalLazerCatalogThroughTheAdvertisedCommand()
    {
        var catalog = new RecordingExternalLazerCatalogBackend();
        var backend = new ReplayAnalysisBackend(
            new RecordingEngine(),
            new ReplayInputValidator(),
            TimeSpan.FromSeconds(1),
            new RecordingExternalLazerAssetBackend(),
            catalog);
        var request = new ExternalLazerCatalogSearchRequest("/lazer", ExternalLazerCatalogEntryKind.BeatmapSets, SearchText: "artist");

        JsonElement? payload = await backend.ExecuteAsync(
            RuntimeCommands.SearchExternalLazerCatalog,
            JsonSerializer.SerializeToElement(request, RuntimeProtocol.JsonOptions),
            CancellationToken.None);

        ExternalLazerCatalogSearchResult? result = payload?.Deserialize<ExternalLazerCatalogSearchResult>(RuntimeProtocol.JsonOptions);
        Assert.Multiple(() =>
        {
            Assert.That(catalog.LastRequest, Is.EqualTo(request));
            Assert.That(result?.Kind, Is.EqualTo(ExternalLazerCatalogEntryKind.BeatmapSets));
            Assert.That(result?.BeatmapSets, Is.Empty);
        });
    }

    [Test]
    public async Task ResolvesExternalLazerAssetsThroughTheAdvertisedCommand()
    {
        var engine = new RecordingEngine();
        var externalLibrary = new RecordingExternalLazerAssetBackend();
        var backend = new ReplayAnalysisBackend(
            engine,
            new ReplayInputValidator(),
            TimeSpan.FromSeconds(1),
            externalLibrary);
        var scoreId = Guid.NewGuid();
        var request = new ExternalLazerAssetResolveRequest(
            "/lazer",
            "/stage",
            new[] { "beatmap-hash" },
            new[] { scoreId });

        JsonElement? payload = await backend.ExecuteAsync(
            RuntimeCommands.ResolveExternalLazerAssets,
            JsonSerializer.SerializeToElement(request, RuntimeProtocol.JsonOptions),
            CancellationToken.None);

        ExternalLazerAssetResolveResult? result = payload?.Deserialize<ExternalLazerAssetResolveResult>(RuntimeProtocol.JsonOptions);
        Assert.Multiple(() =>
        {
            Assert.That(externalLibrary.LastRequest?.LibraryRoot, Is.EqualTo(request.LibraryRoot));
            Assert.That(externalLibrary.LastRequest?.BeatmapHashes, Is.EqualTo(request.BeatmapHashes));
            Assert.That(externalLibrary.LastRequest?.ScoreIds, Is.EqualTo(request.ScoreIds));
            Assert.That(result?.Files, Is.Empty);
            Assert.That(result?.MissingBeatmaps, Is.EqualTo(request.BeatmapHashes));
            Assert.That(result?.MissingScores, Is.EqualTo(request.ScoreIds));
            Assert.That(engine.LastInput, Is.Null);
        });
    }

    [Test]
    public void RejectsNullExternalLibrarySelectionLists()
    {
        var backend = createBackend(new RecordingEngine());
        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            libraryRoot = "/lazer",
            beatmapHashes = (object?)null,
            scoreIds = Array.Empty<Guid>(),
        }, RuntimeProtocol.JsonOptions);

        RuntimeCommandException exception = Assert.ThrowsAsync<RuntimeCommandException>(async () =>
            await backend.ExecuteAsync(RuntimeCommands.ResolveExternalLazerAssets, payload, CancellationToken.None))!;

        Assert.That(exception.Code, Is.EqualTo("invalid_payload"));
    }

    [Test]
    public async Task RunsAnalysisForFilesInsideStagingDirectory()
    {
        (string beatmapPath, string replayPath) = createInputs();
        var engine = new RecordingEngine();
        var backend = createBackend(engine);

        JsonElement? payload = await backend.ExecuteAsync(
            RuntimeCommands.AnalyseReplay,
            JsonSerializer.SerializeToElement(new ReplayAnalysisRequest(temporaryDirectory, beatmapPath, replayPath), RuntimeProtocol.JsonOptions),
            CancellationToken.None);

        ReplayAnalysisResult? result = payload?.Deserialize<ReplayAnalysisResult>(RuntimeProtocol.JsonOptions);
        Assert.Multiple(() =>
        {
            Assert.That(engine.LastInput?.BeatmapPath, Is.EqualTo(beatmapPath));
            Assert.That(engine.LastInput?.ReplayPath, Is.EqualTo(replayPath));
            Assert.That(result?.HeadlessAudioMuted, Is.True);
            Assert.That(result?.Summary.Great, Is.EqualTo(1));
            Assert.That(result?.ContentIdentity?.BeatmapSha256, Is.EqualTo(sha256("beatmap")));
            Assert.That(result?.ContentIdentity?.ReplaySha256, Is.EqualTo(sha256("replay")));
        });
    }

    [Test]
    public void RejectsInputsChangedDuringAnalysis()
    {
        (string beatmapPath, string replayPath) = createInputs();
        var backend = createBackend(new MutatingEngine());

        RuntimeCommandException exception = Assert.ThrowsAsync<RuntimeCommandException>(async () =>
            await backend.ExecuteAsync(
                RuntimeCommands.AnalyseReplay,
                JsonSerializer.SerializeToElement(new ReplayAnalysisRequest(temporaryDirectory, beatmapPath, replayPath), RuntimeProtocol.JsonOptions),
                CancellationToken.None))!;

        Assert.That(exception.Code, Is.EqualTo("input_changed"));
    }

    [Test]
    public void RejectsAFileOutsideTheStagingDirectory()
    {
        (_, string replayPath) = createInputs();
        string outsidePath = Path.Combine(Path.GetTempPath(), $"aimmod-outside-{Guid.NewGuid():N}.osu");
        File.WriteAllText(outsidePath, "outside");

        try
        {
            var backend = createBackend(new RecordingEngine());

            RuntimeCommandException exception = Assert.ThrowsAsync<RuntimeCommandException>(async () =>
                await backend.ExecuteAsync(
                    RuntimeCommands.AnalyseReplay,
                    JsonSerializer.SerializeToElement(new ReplayAnalysisRequest(temporaryDirectory, outsidePath, replayPath), RuntimeProtocol.JsonOptions),
                    CancellationToken.None))!;

            Assert.That(exception.Code, Is.EqualTo("staged_path_invalid"));
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    [Test]
    public void RejectsEmptyInputsBeforeStartingTheEngine()
    {
        string beatmapPath = Path.Combine(temporaryDirectory, "map.osu");
        string replayPath = Path.Combine(temporaryDirectory, "play.osr");
        File.WriteAllText(beatmapPath, string.Empty);
        File.WriteAllText(replayPath, "replay");
        var engine = new RecordingEngine();
        var backend = createBackend(engine);

        RuntimeCommandException exception = Assert.ThrowsAsync<RuntimeCommandException>(async () =>
            await backend.ExecuteAsync(
                RuntimeCommands.AnalyseReplay,
                JsonSerializer.SerializeToElement(new ReplayAnalysisRequest(temporaryDirectory, beatmapPath, replayPath), RuntimeProtocol.JsonOptions),
                CancellationToken.None))!;

        Assert.Multiple(() =>
        {
            Assert.That(exception.Code, Is.EqualTo("input_empty"));
            Assert.That(engine.LastInput, Is.Null);
        });
    }

    [Test]
    public void RejectsSymbolicLinksInsideTheStagingDirectory()
    {
        (_, string replayPath) = createInputs();
        string sourcePath = Path.Combine(temporaryDirectory, "source.osu");
        string linkPath = Path.Combine(temporaryDirectory, "linked.osu");
        File.WriteAllText(sourcePath, "beatmap");
        try
        {
            File.CreateSymbolicLink(linkPath, sourcePath);
        }
        catch (Exception symlinkException) when (symlinkException is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Ignore($"This test requires permission to create symbolic links: {symlinkException.Message}");
        }
        var backend = createBackend(new RecordingEngine());

        RuntimeCommandException exception = Assert.ThrowsAsync<RuntimeCommandException>(async () =>
            await backend.ExecuteAsync(
                RuntimeCommands.AnalyseReplay,
                JsonSerializer.SerializeToElement(new ReplayAnalysisRequest(temporaryDirectory, linkPath, replayPath), RuntimeProtocol.JsonOptions),
                CancellationToken.None))!;

        Assert.That(exception.Code, Is.EqualTo("staged_path_invalid"));
    }

    [Test]
    public void AppliesAWallClockTimeout()
    {
        (string beatmapPath, string replayPath) = createInputs();
        var backend = new ReplayAnalysisBackend(new WaitingEngine(), new ReplayInputValidator(), TimeSpan.FromMilliseconds(20));

        RuntimeCommandException exception = Assert.ThrowsAsync<RuntimeCommandException>(async () =>
            await backend.ExecuteAsync(
                RuntimeCommands.AnalyseReplay,
                JsonSerializer.SerializeToElement(new ReplayAnalysisRequest(temporaryDirectory, beatmapPath, replayPath), RuntimeProtocol.JsonOptions),
                CancellationToken.None))!;

        Assert.That(exception.Code, Is.EqualTo("analysis_timeout"));
    }

    [Test]
    public void RejectsMissingPayload()
    {
        var backend = createBackend(new RecordingEngine());

        RuntimeCommandException exception = Assert.ThrowsAsync<RuntimeCommandException>(async () =>
            await backend.ExecuteAsync(RuntimeCommands.AnalyseReplay, null, CancellationToken.None))!;

        Assert.That(exception.Code, Is.EqualTo("invalid_payload"));
    }

    private ReplayAnalysisBackend createBackend(IReplayAnalysisEngine engine) =>
        new(engine, new ReplayInputValidator(), TimeSpan.FromSeconds(1));

    private (string BeatmapPath, string ReplayPath) createInputs()
    {
        string beatmapPath = Path.Combine(temporaryDirectory, "map.osu");
        string replayPath = Path.Combine(temporaryDirectory, "play.osr");
        File.WriteAllText(beatmapPath, "beatmap");
        File.WriteAllText(replayPath, "replay");
        return (beatmapPath, replayPath);
    }

    private static string sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class RecordingEngine : IReplayAnalysisEngine
    {
        public ValidatedReplayInput? LastInput { get; private set; }

        public ValueTask<ReplayAnalysisResult> AnalyseAsync(ValidatedReplayInput input, CancellationToken cancellationToken)
        {
            LastInput = input;
            return ValueTask.FromResult(new ReplayAnalysisResult(
                ReplayAnalysisProtocol.EngineVersion,
                "officialRulesetPlayback",
                true,
                ReplayAnalysisProtocol.WallClockTimeoutMs,
                Array.Empty<int>(),
                Array.Empty<ReplayObjectJudgement>(),
                new ReplayJudgementSummary(1, 0, 0, 0, 0, 0)));
        }
    }

    private sealed class WaitingEngine : IReplayAnalysisEngine
    {
        public async ValueTask<ReplayAnalysisResult> AnalyseAsync(ValidatedReplayInput input, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class RecordingPpCalculator : IPpWhatIfCalculator
    {
        public ValidatedPpInput? LastInput { get; private set; }

        public ValueTask<PpWhatIfResult> CalculateAsync(ValidatedPpInput input, CancellationToken cancellationToken)
        {
            LastInput = input;
            return ValueTask.FromResult(new PpWhatIfResult(
                PpCalculationProtocol.EngineVersion,
                20260903,
                6.2,
                900,
                500,
                490,
                9,
                0,
                1,
                0.975,
                321.5,
                120,
                130,
                80,
                0,
                0,
                1));
        }
    }

    private sealed class MutatingEngine : IReplayAnalysisEngine
    {
        public ValueTask<ReplayAnalysisResult> AnalyseAsync(ValidatedReplayInput input, CancellationToken cancellationToken)
        {
            File.AppendAllText(input.ReplayPath, "-changed");
            return ValueTask.FromResult(new ReplayAnalysisResult(
                ReplayAnalysisProtocol.EngineVersion,
                "officialRulesetPlayback",
                true,
                ReplayAnalysisProtocol.WallClockTimeoutMs,
                Array.Empty<int>(),
                Array.Empty<ReplayObjectJudgement>(),
                ReplayJudgementSummary.Empty));
        }
    }

    private sealed class RecordingExternalLazerAssetBackend : IExternalLazerAssetBackend
    {
        public ExternalLazerAssetResolveRequest? LastRequest { get; private set; }

        public ValueTask<ExternalLazerAssetResolveResult> ResolveAsync(
            ExternalLazerAssetResolveRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return ValueTask.FromResult(new ExternalLazerAssetResolveResult(
                Array.Empty<ExternalLazerResolvedAsset>(),
                Array.Empty<ExternalLazerMissingAsset>(),
                request.BeatmapHashes,
                request.ScoreIds));
        }
    }

    private sealed class RecordingExternalLazerCatalogBackend : IExternalLazerCatalogBackend
    {
        public ExternalLazerCatalogSearchRequest? LastRequest { get; private set; }

        public ValueTask<ExternalLazerCatalogSearchResult> SearchAsync(
            ExternalLazerCatalogSearchRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return ValueTask.FromResult(new ExternalLazerCatalogSearchResult(
                request.Kind,
                Array.Empty<ExternalLazerBeatmapSet>(),
                Array.Empty<ExternalLazerReplaySummary>(),
                0,
                request.Offset,
                request.Limit));
        }
    }
}

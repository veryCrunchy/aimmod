using System.Text.Json;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Osu.Runtime.Tests;

[TestFixture]
public sealed class ReplayAnalysisClientTests
{
    [Test]
    public async Task SendsReplayAnalysisCommandAndReturnsTypedResult()
    {
        ReplayAnalysisResult expected = createResult(12);
        var runtime = new RecordingRuntimeClient(request => new RuntimeResponse(
            request.Id,
            RuntimeProtocol.CurrentVersion,
            true,
            JsonSerializer.SerializeToElement(expected, RuntimeProtocol.JsonOptions)));
        var client = new ReplayAnalysisClient(runtime);
        var input = new ReplayAnalysisRequest("/stage", "/stage/map.osu", "/stage/play.osr");

        ReplayAnalysisResult actual = await client.AnalyseAsync(input);

        ReplayAnalysisRequest? sentInput = runtime.LastRequest?.Payload?.Deserialize<ReplayAnalysisRequest>(RuntimeProtocol.JsonOptions);
        Assert.Multiple(() =>
        {
            Assert.That(runtime.LastRequest?.Command, Is.EqualTo(RuntimeCommands.AnalyseReplay));
            Assert.That(sentInput, Is.EqualTo(input));
            Assert.That(actual.EngineVersion, Is.EqualTo(expected.EngineVersion));
            Assert.That(actual.TimeBasis, Is.EqualTo(expected.TimeBasis));
            Assert.That(actual.HeadlessAudioMuted, Is.True);
            Assert.That(actual.Pauses, Is.Empty);
            Assert.That(actual.Judgements, Is.Empty);
            Assert.That(actual.Summary, Is.EqualTo(expected.Summary));
            Assert.That(actual.ContentIdentity, Is.EqualTo(expected.ContentIdentity));
        });
    }

    [Test]
    public void PreservesBoundedWorkerErrors()
    {
        var runtime = new RecordingRuntimeClient(request => new RuntimeResponse(
            request.Id,
            RuntimeProtocol.CurrentVersion,
            false,
            Error: new RuntimeError("analysis_timeout", "Replay analysis timed out.")));
        var client = new ReplayAnalysisClient(runtime);

        ReplayAnalysisClientException exception = Assert.ThrowsAsync<ReplayAnalysisClientException>(async () =>
            await client.AnalyseAsync(new ReplayAnalysisRequest("/stage", "/stage/map.osu", "/stage/play.osr")))!;

        Assert.Multiple(() =>
        {
            Assert.That(exception.Code, Is.EqualTo("analysis_timeout"));
            Assert.That(exception.Message, Is.EqualTo("Replay analysis timed out."));
        });
    }

    [Test]
    public void RejectsInvalidSuccessPayload()
    {
        var runtime = new RecordingRuntimeClient(request => new RuntimeResponse(
            request.Id,
            RuntimeProtocol.CurrentVersion,
            true,
            JsonSerializer.SerializeToElement(new { unexpected = true }, RuntimeProtocol.JsonOptions)));
        var client = new ReplayAnalysisClient(runtime);

        ReplayAnalysisClientException exception = Assert.ThrowsAsync<ReplayAnalysisClientException>(async () =>
            await client.AnalyseAsync(new ReplayAnalysisRequest("/stage", "/stage/map.osu", "/stage/play.osr")))!;

        Assert.That(exception.Code, Is.EqualTo("invalid_worker_response"));
    }

    [Test]
    public async Task SendsPpWhatIfCommandAndReturnsTypedResult()
    {
        PpWhatIfResult expected = createPpResult(420.5);
        var runtime = new RecordingRuntimeClient(request => new RuntimeResponse(
            request.Id,
            RuntimeProtocol.CurrentVersion,
            true,
            JsonSerializer.SerializeToElement(expected, RuntimeProtocol.JsonOptions)));
        var client = new PpWhatIfClient(runtime);
        var input = new PpWhatIfRequest("/stage", "/stage/map.osu", new[] { "HD" }, 0.98, 1, 900);

        PpWhatIfResult actual = await client.CalculateAsync(input);

        PpWhatIfRequest? sentInput = runtime.LastRequest?.Payload?.Deserialize<PpWhatIfRequest>(RuntimeProtocol.JsonOptions);
        Assert.Multiple(() =>
        {
            Assert.That(runtime.LastRequest?.Command, Is.EqualTo(RuntimeCommands.CalculatePp));
            Assert.That(sentInput?.StagingDirectory, Is.EqualTo(input.StagingDirectory));
            Assert.That(sentInput?.BeatmapPath, Is.EqualTo(input.BeatmapPath));
            Assert.That(sentInput?.Mods, Is.EqualTo(input.Mods));
            Assert.That(sentInput?.Accuracy, Is.EqualTo(input.Accuracy));
            Assert.That(sentInput?.MissCount, Is.EqualTo(input.MissCount));
            Assert.That(sentInput?.MaxCombo, Is.EqualTo(input.MaxCombo));
            Assert.That(actual.PerformancePoints, Is.EqualTo(expected.PerformancePoints));
            Assert.That(actual.Aim, Is.EqualTo(expected.Aim));
        });
    }

    [Test]
    public void PpWhatIfClientPreservesWorkerErrors()
    {
        var runtime = new RecordingRuntimeClient(request => new RuntimeResponse(
            request.Id,
            RuntimeProtocol.CurrentVersion,
            false,
            Error: new RuntimeError("unsupported_mod", "PP calculation does not support that mod.")));
        var client = new PpWhatIfClient(runtime);

        PpWhatIfClientException exception = Assert.ThrowsAsync<PpWhatIfClientException>(async () =>
            await client.CalculateAsync(new PpWhatIfRequest("/stage", "/stage/map.osu", Array.Empty<string>(), 1)))!;

        Assert.Multiple(() =>
        {
            Assert.That(exception.Code, Is.EqualTo("unsupported_mod"));
            Assert.That(exception.Message, Is.EqualTo("PP calculation does not support that mod."));
        });
    }

    [Test]
    public void PpWhatIfClientRejectsInvalidSuccessPayload()
    {
        var runtime = new RecordingRuntimeClient(request => new RuntimeResponse(
            request.Id,
            RuntimeProtocol.CurrentVersion,
            true,
            JsonSerializer.SerializeToElement(new { performancePoints = -1 }, RuntimeProtocol.JsonOptions)));
        var client = new PpWhatIfClient(runtime);

        PpWhatIfClientException exception = Assert.ThrowsAsync<PpWhatIfClientException>(async () =>
            await client.CalculateAsync(new PpWhatIfRequest("/stage", "/stage/map.osu", Array.Empty<string>(), 1)))!;

        Assert.That(exception.Code, Is.EqualTo("invalid_worker_response"));
    }

    internal static ReplayAnalysisResult createResult(int great) => new(
        ReplayAnalysisProtocol.EngineVersion,
        "officialRulesetPlayback",
        true,
        ReplayAnalysisProtocol.WallClockTimeoutMs,
        Array.Empty<int>(),
        Array.Empty<ReplayObjectJudgement>(),
        new ReplayJudgementSummary(great, 0, 0, 0, 0, 0),
        new ReplayAnalysisContentIdentity(new string('a', 64), new string('b', 64)));

    private static PpWhatIfResult createPpResult(double pp) => new(
        PpCalculationProtocol.EngineVersion,
        20260903,
        6.1,
        900,
        500,
        490,
        9,
        0,
        1,
        0.98,
        pp,
        100,
        110,
        80,
        0,
        0,
        1);

    private sealed class RecordingRuntimeClient(Func<RuntimeRequest, RuntimeResponse> responseFactory) : IRuntimeRequestClient
    {
        public RuntimeRequest? LastRequest { get; private set; }

        public Task<RuntimeResponse> SendAsync(RuntimeRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(responseFactory(request));
        }
    }
}

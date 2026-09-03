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

    internal static ReplayAnalysisResult createResult(int great) => new(
        ReplayAnalysisProtocol.EngineVersion,
        "officialRulesetPlayback",
        true,
        ReplayAnalysisProtocol.WallClockTimeoutMs,
        Array.Empty<int>(),
        Array.Empty<ReplayObjectJudgement>(),
        new ReplayJudgementSummary(great, 0, 0, 0, 0, 0));

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

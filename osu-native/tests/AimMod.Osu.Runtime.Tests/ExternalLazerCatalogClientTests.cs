using System.Text.Json;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Osu.Runtime.Tests;

[TestFixture]
public sealed class ExternalLazerCatalogClientTests
{
    [Test]
    public async Task SendsCatalogCommandAndReturnsTypedPage()
    {
        var set = new ExternalLazerBeatmapSet(
            Guid.NewGuid(), 12, "Title", "Artist", "Mapper", "Source", DateTimeOffset.UtcNow, null,
            new[] { difficulty() }, 0);
        var expected = new ExternalLazerCatalogSearchResult(
            ExternalLazerCatalogEntryKind.BeatmapSets,
            new[] { set },
            Array.Empty<ExternalLazerReplaySummary>(),
            1,
            0,
            20);
        var runtime = new RecordingRuntimeClient(request => new RuntimeResponse(
            request.Id,
            RuntimeProtocol.CurrentVersion,
            true,
            JsonSerializer.SerializeToElement(expected, RuntimeProtocol.JsonOptions)));
        var client = new ExternalLazerCatalogClient(runtime);
        var input = new ExternalLazerCatalogSearchRequest(
            "/lazer", ExternalLazerCatalogEntryKind.BeatmapSets, SearchText: "artist", Limit: 20);

        ExternalLazerCatalogSearchResult actual = await client.SearchAsync(input);

        ExternalLazerCatalogSearchRequest? sent = runtime.LastRequest?.Payload?.Deserialize<ExternalLazerCatalogSearchRequest>(RuntimeProtocol.JsonOptions);
        Assert.Multiple(() =>
        {
            Assert.That(runtime.LastRequest?.Command, Is.EqualTo(RuntimeCommands.SearchExternalLazerCatalog));
            Assert.That(sent, Is.EqualTo(input));
            Assert.That(actual.Kind, Is.EqualTo(expected.Kind));
            Assert.That(actual.BeatmapSets, Has.Count.EqualTo(1));
            Assert.That(actual.BeatmapSets[0].SetId, Is.EqualTo(set.SetId));
            Assert.That(actual.BeatmapSets[0].Difficulties[0].BeatmapHash, Is.EqualTo(set.Difficulties[0].BeatmapHash));
            Assert.That(actual.Replays, Is.Empty);
            Assert.That(actual.Total, Is.EqualTo(expected.Total));
        });
    }

    [Test]
    public void PreservesWorkerErrors()
    {
        var runtime = new RecordingRuntimeClient(request => new RuntimeResponse(
            request.Id,
            RuntimeProtocol.CurrentVersion,
            false,
            Error: new RuntimeError("catalog_query_invalid", "The query is too large.")));
        var client = new ExternalLazerCatalogClient(runtime);

        ExternalLazerCatalogClientException exception = Assert.ThrowsAsync<ExternalLazerCatalogClientException>(async () =>
            await client.SearchAsync(request()))!;

        Assert.That(exception.Code, Is.EqualTo("catalog_query_invalid"));
    }

    [Test]
    public void RejectsAResultThatExceedsTheRequestedPage()
    {
        ExternalLazerBeatmapSet[] sets = Enumerable.Range(0, 2)
                                                    .Select(_ => new ExternalLazerBeatmapSet(
                                                        Guid.NewGuid(), -1, "Title", "Artist", "Mapper", "", DateTimeOffset.UtcNow, null,
                                                        new[] { difficulty() }, 0))
                                                    .ToArray();
        var runtime = new RecordingRuntimeClient(request => new RuntimeResponse(
            request.Id,
            RuntimeProtocol.CurrentVersion,
            true,
            JsonSerializer.SerializeToElement(new ExternalLazerCatalogSearchResult(
                ExternalLazerCatalogEntryKind.BeatmapSets, sets, Array.Empty<ExternalLazerReplaySummary>(), 2, 0, 1), RuntimeProtocol.JsonOptions)));
        var client = new ExternalLazerCatalogClient(runtime);

        ExternalLazerCatalogClientException exception = Assert.ThrowsAsync<ExternalLazerCatalogClientException>(async () =>
            await client.SearchAsync(request() with { Limit = 1 }))!;

        Assert.That(exception.Code, Is.EqualTo("invalid_worker_response"));
    }

    private static ExternalLazerCatalogSearchRequest request() =>
        new("/lazer", ExternalLazerCatalogEntryKind.BeatmapSets, Limit: 20);

    private static ExternalLazerBeatmapDifficulty difficulty() => new(
        Guid.NewGuid(), -1,
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
        "abcdef0123456789abcdef0123456789",
        "Hard", "osu", 4.2, 180, 120_000, 4, 9, 8, 6, 0);

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

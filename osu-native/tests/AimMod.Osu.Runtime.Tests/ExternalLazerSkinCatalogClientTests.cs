using System.Text.Json;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Osu.Runtime.Tests;

[TestFixture]
public sealed class ExternalLazerSkinCatalogClientTests
{
    [Test]
    public async Task SendsInstalledSkinCommandAndReturnsRealMetadataShape()
    {
        Guid skinId = Guid.NewGuid();
        var expected = new ExternalLazerSkinCatalogSearchResult(
            new[]
            {
                new ExternalLazerSkinSummary(
                    skinId,
                    "WhiteCat",
                    "CK",
                    new string('a', 64),
                    false,
                    420,
                    new string('b', 64),
                    "menu-background.jpg"),
            },
            1,
            0,
            20);
        var runtime = new RecordingRuntimeClient(request => new RuntimeResponse(
            request.Id,
            RuntimeProtocol.CurrentVersion,
            true,
            JsonSerializer.SerializeToElement(expected, RuntimeProtocol.JsonOptions)));
        var client = new ExternalLazerSkinCatalogClient(runtime);
        var input = new ExternalLazerSkinCatalogSearchRequest("/lazer", "white", Limit: 20);

        ExternalLazerSkinCatalogSearchResult result = await client.SearchAsync(input);

        Assert.Multiple(() =>
        {
            Assert.That(runtime.LastRequest?.Command, Is.EqualTo(RuntimeCommands.SearchExternalLazerSkins));
            Assert.That(result.Skins, Has.Count.EqualTo(1));
            Assert.That(result.Skins[0].SkinId, Is.EqualTo(skinId));
            Assert.That(result.Skins[0].PreviewLogicalName, Is.EqualTo("menu-background.jpg"));
        });
    }

    [Test]
    public void RejectsMismatchedPreviewMetadata()
    {
        var invalid = new ExternalLazerSkinCatalogSearchResult(
            new[] { new ExternalLazerSkinSummary(Guid.NewGuid(), "Skin", "", "", false, 1, new string('a', 64), "") },
            1,
            0,
            20);
        var runtime = new RecordingRuntimeClient(request => new RuntimeResponse(
            request.Id,
            RuntimeProtocol.CurrentVersion,
            true,
            JsonSerializer.SerializeToElement(invalid, RuntimeProtocol.JsonOptions)));

        ExternalLazerSkinClientException exception = Assert.ThrowsAsync<ExternalLazerSkinClientException>(async () =>
            await new ExternalLazerSkinCatalogClient(runtime).SearchAsync(new ExternalLazerSkinCatalogSearchRequest("/lazer", Limit: 20)))!;

        Assert.That(exception.Code, Is.EqualTo("invalid_worker_response"));
    }

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

using System.Text.Json;
using System.Security.Cryptography;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Osu.Runtime.Tests;

[TestFixture]
public sealed class ExternalLazerAssetClientTests
{
    [Test]
    public async Task PrivateStagingLeaseRemovesWorkerStagedFiles()
    {
        byte[] content = "staged asset"u8.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        string? stagedDirectory = null;
        var runtime = new RecordingRuntimeClient(request =>
        {
            ExternalLazerAssetResolveRequest input = request.Payload!.Value.Deserialize<ExternalLazerAssetResolveRequest>(RuntimeProtocol.JsonOptions)!;
            stagedDirectory = input.StagingDirectory;
            string path = Path.Combine(input.StagingDirectory, $"0000-beatmap-{hash}.osu");
            File.WriteAllBytes(path, content);
            var result = new ExternalLazerAssetResolveResult(
                new[] { new ExternalLazerResolvedAsset("Beatmap", hash, "map.osu", hash, path, content.Length) },
                Array.Empty<ExternalLazerMissingAsset>(),
                Array.Empty<string>(),
                Array.Empty<Guid>());
            return new RuntimeResponse(
                request.Id,
                RuntimeProtocol.CurrentVersion,
                true,
                JsonSerializer.SerializeToElement(result, RuntimeProtocol.JsonOptions));
        });
        var client = new ExternalLazerAssetClient(runtime);

        ExternalLazerAssetStagingLease lease = await client.ResolveToPrivateStagingAsync(
            Path.GetFullPath("lazer-library"),
            new[] { hash },
            Array.Empty<Guid>());

        Assert.That(Directory.Exists(stagedDirectory), Is.True);
        await lease.DisposeAsync();
        await lease.DisposeAsync();
        Assert.That(Directory.Exists(stagedDirectory), Is.False);
    }

    [Test]
    public async Task PrivateStagingAcceptsOneSelectedSkinAndRejectsForeignOwners()
    {
        byte[] content = "skin ini"u8.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        Guid skinId = Guid.NewGuid();
        var runtime = new RecordingRuntimeClient(request =>
        {
            ExternalLazerAssetResolveRequest input = request.Payload!.Value.Deserialize<ExternalLazerAssetResolveRequest>(RuntimeProtocol.JsonOptions)!;
            string path = Path.Combine(input.StagingDirectory, $"0000-skin-{hash}.ini");
            File.WriteAllBytes(path, content);
            var result = new ExternalLazerAssetResolveResult(
                new[] { new ExternalLazerResolvedAsset("Skin", skinId.ToString("D"), "skin.ini", hash, path, content.Length) },
                Array.Empty<ExternalLazerMissingAsset>(),
                Array.Empty<string>(),
                Array.Empty<Guid>(),
                Array.Empty<Guid>());
            return new RuntimeResponse(
                request.Id,
                RuntimeProtocol.CurrentVersion,
                true,
                JsonSerializer.SerializeToElement(result, RuntimeProtocol.JsonOptions));
        });
        var client = new ExternalLazerAssetClient(runtime);

        await using ExternalLazerAssetStagingLease lease = await client.ResolveToPrivateStagingAsync(
            Path.GetFullPath("lazer-library"),
            Array.Empty<string>(),
            Array.Empty<Guid>(),
            new[] { skinId });

        Assert.That(lease.Result.Files.Single().OwnerId, Is.EqualTo(skinId.ToString("D")));
    }

    [Test]
    public async Task AcceptsAnEmptyFileOnlyForSelectedSkins()
    {
        const string emptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        Guid skinId = Guid.NewGuid();
        string stagingDirectory = Path.GetFullPath("asset-staging");
        string stagedPath = Path.Combine(stagingDirectory, "0000-skin-empty.txt");
        var expected = new ExternalLazerAssetResolveResult(
            new[] { new ExternalLazerResolvedAsset("Skin", skinId.ToString("D"), "empty.txt", emptyHash, stagedPath, 0) },
            Array.Empty<ExternalLazerMissingAsset>(),
            Array.Empty<string>(),
            Array.Empty<Guid>(),
            Array.Empty<Guid>());
        var runtime = new RecordingRuntimeClient(request => new RuntimeResponse(
            request.Id,
            RuntimeProtocol.CurrentVersion,
            true,
            JsonSerializer.SerializeToElement(expected, RuntimeProtocol.JsonOptions)));
        var client = new ExternalLazerAssetClient(runtime);

        ExternalLazerAssetResolveResult result = await client.ResolveAsync(new ExternalLazerAssetResolveRequest(
            Path.GetFullPath("lazer-library"),
            stagingDirectory,
            Array.Empty<string>(),
            Array.Empty<Guid>(),
            new[] { skinId }));

        Assert.That(result.Files.Single().Length, Is.Zero);
    }

    [Test]
    public async Task CancellationWaitsForWorkerCleanupThenRemovesPrivateStaging()
    {
        byte[] content = "cancelled staged asset"u8.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var runtime = new DelayedRuntimeClient(hash, content);
        var client = new ExternalLazerAssetClient(runtime);
        using var cancellation = new CancellationTokenSource();

        Task<ExternalLazerAssetStagingLease> operation = client.ResolveToPrivateStagingAsync(
            Path.GetFullPath("lazer-library"),
            new[] { hash },
            Array.Empty<Guid>(),
            cancellation.Token);
        await runtime.Started.Task;
        cancellation.Cancel();

        Assert.That(operation.IsCompleted, Is.False);
        runtime.Release.SetResult();
        Assert.CatchAsync<OperationCanceledException>(async () => await operation);
        Assert.That(Directory.Exists(runtime.StagingDirectory), Is.False);
    }

    [Test]
    public async Task SendsResolveCommandAndReturnsTypedAssets()
    {
        string stagingDirectory = Path.GetFullPath("asset-staging");
        string sourcePath = Path.Combine(stagingDirectory, "map.osu");
        const string hash = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
        const string missingHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var scoreId = Guid.NewGuid();
        var expected = new ExternalLazerAssetResolveResult(
            new[]
            {
                new ExternalLazerResolvedAsset("Beatmap", hash, "map.osu", hash, sourcePath, 42),
            },
            Array.Empty<ExternalLazerMissingAsset>(),
            new[] { missingHash },
            new[] { scoreId });
        var runtime = new RecordingRuntimeClient(request => new RuntimeResponse(
            request.Id,
            RuntimeProtocol.CurrentVersion,
            true,
            JsonSerializer.SerializeToElement(expected, RuntimeProtocol.JsonOptions)));
        var client = new ExternalLazerAssetClient(runtime);
        var input = new ExternalLazerAssetResolveRequest(
            Path.GetFullPath("lazer-library"),
            stagingDirectory,
            new[] { hash, missingHash },
            new[] { scoreId });

        ExternalLazerAssetResolveResult actual = await client.ResolveAsync(input);

        ExternalLazerAssetResolveRequest? sentInput = runtime.LastRequest?.Payload?.Deserialize<ExternalLazerAssetResolveRequest>(RuntimeProtocol.JsonOptions);
        Assert.Multiple(() =>
        {
            Assert.That(runtime.LastRequest?.Command, Is.EqualTo(RuntimeCommands.ResolveExternalLazerAssets));
            Assert.That(sentInput?.LibraryRoot, Is.EqualTo(input.LibraryRoot));
            Assert.That(sentInput?.BeatmapHashes, Is.EqualTo(input.BeatmapHashes));
            Assert.That(sentInput?.ScoreIds, Is.EqualTo(input.ScoreIds));
            Assert.That(actual.Files, Is.EqualTo(expected.Files));
            Assert.That(actual.MissingBeatmaps, Is.EqualTo(expected.MissingBeatmaps));
            Assert.That(actual.MissingScores, Is.EqualTo(expected.MissingScores));
        });
    }

    [Test]
    public void PreservesWorkerErrors()
    {
        var runtime = new RecordingRuntimeClient(request => new RuntimeResponse(
            request.Id,
            RuntimeProtocol.CurrentVersion,
            false,
            Error: new RuntimeError("library_busy", "The lazer library is busy.")));
        var client = new ExternalLazerAssetClient(runtime);

        ExternalLazerAssetClientException exception = Assert.ThrowsAsync<ExternalLazerAssetClientException>(async () =>
            await client.ResolveAsync(createRequest()))!;

        Assert.Multiple(() =>
        {
            Assert.That(exception.Code, Is.EqualTo("library_busy"));
            Assert.That(exception.Message, Is.EqualTo("The lazer library is busy."));
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
        var client = new ExternalLazerAssetClient(runtime);

        ExternalLazerAssetClientException exception = Assert.ThrowsAsync<ExternalLazerAssetClientException>(async () =>
            await client.ResolveAsync(createRequest()))!;

        Assert.That(exception.Code, Is.EqualTo("invalid_worker_response"));
    }

    [Test]
    public void RejectsNullAssetInSuccessPayload()
    {
        var runtime = new RecordingRuntimeClient(request => new RuntimeResponse(
            request.Id,
            RuntimeProtocol.CurrentVersion,
            true,
            JsonSerializer.SerializeToElement(new
            {
                files = new object?[] { null },
                missingFiles = Array.Empty<object>(),
                missingBeatmaps = Array.Empty<string>(),
                missingScores = Array.Empty<Guid>(),
            }, RuntimeProtocol.JsonOptions)));
        var client = new ExternalLazerAssetClient(runtime);

        ExternalLazerAssetClientException exception = Assert.ThrowsAsync<ExternalLazerAssetClientException>(async () =>
            await client.ResolveAsync(createRequest()))!;

        Assert.That(exception.Code, Is.EqualTo("invalid_worker_response"));
    }

    private static ExternalLazerAssetResolveRequest createRequest() => new(
        Path.GetFullPath("lazer-library"),
        Path.GetFullPath("asset-staging"),
        Array.Empty<string>(),
        Array.Empty<Guid>());

    private sealed class RecordingRuntimeClient(Func<RuntimeRequest, RuntimeResponse> responseFactory) : IRuntimeRequestClient
    {
        public RuntimeRequest? LastRequest { get; private set; }

        public Task<RuntimeResponse> SendAsync(RuntimeRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class DelayedRuntimeClient(string hash, byte[] content) : IRuntimeRequestClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? StagingDirectory { get; private set; }

        public async Task<RuntimeResponse> SendAsync(RuntimeRequest request, CancellationToken cancellationToken = default)
        {
            Assert.That(cancellationToken.CanBeCanceled, Is.False);
            ExternalLazerAssetResolveRequest input = request.Payload!.Value.Deserialize<ExternalLazerAssetResolveRequest>(RuntimeProtocol.JsonOptions)!;
            StagingDirectory = input.StagingDirectory;
            Started.SetResult();
            await Release.Task;

            string path = Path.Combine(input.StagingDirectory, $"0000-beatmap-{hash}.osu");
            await File.WriteAllBytesAsync(path, content);
            var result = new ExternalLazerAssetResolveResult(
                new[] { new ExternalLazerResolvedAsset("Beatmap", hash, "map.osu", hash, path, content.Length) },
                Array.Empty<ExternalLazerMissingAsset>(),
                Array.Empty<string>(),
                Array.Empty<Guid>());
            return new RuntimeResponse(
                request.Id,
                RuntimeProtocol.CurrentVersion,
                true,
                JsonSerializer.SerializeToElement(result, RuntimeProtocol.JsonOptions));
        }
    }
}

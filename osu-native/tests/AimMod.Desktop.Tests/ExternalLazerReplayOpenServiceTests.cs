using System.Security.Cryptography;
using System.Text.Json;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using AimMod.Desktop.LocalLibrary;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class ExternalLazerReplayOpenServiceTests
{
    [Test]
    public async Task ReturnsAPlayablePrivateBundleAndReleasesWorkerStaging()
    {
        ExternalLazerReplaySummary replay = replaySummary();
        var runtime = new AssetRuntimeClient(replay, missingKind: null);
        var service = new ExternalLazerReplayOpenService(
            Path.GetFullPath("lazer-library"),
            new ExternalLazerAssetClient(runtime));

        ExternalLazerPlayableReplayBundle bundle = await service.OpenAsync(replay);

        Assert.Multiple(() =>
        {
            Assert.That(Directory.Exists(runtime.StagingDirectory), Is.False);
            Assert.That(bundle.OpenRequest.BeatmapPath, Is.EqualTo(bundle.BeatmapPath));
            Assert.That(bundle.OpenRequest.ReplayPath, Is.EqualTo(bundle.ReplayPath));
            Assert.That(Path.GetExtension(bundle.BeatmapPath), Is.EqualTo(".osu"));
            Assert.That(Path.GetExtension(bundle.ReplayPath), Is.EqualTo(".osr"));
            Assert.That(File.ReadAllText(bundle.BeatmapPath), Does.Contain("AudioFilename: audio/test.mp3"));
            Assert.That(File.ReadAllText(bundle.ReplayPath), Is.EqualTo("real replay"));
            Assert.That(File.ReadAllText(bundle.AudioPath), Is.EqualTo("real audio"));
            Assert.That(bundle.BackgroundPaths, Has.Count.EqualTo(1));
            Assert.That(File.ReadAllText(bundle.BackgroundPaths[0]), Is.EqualTo("real background"));
        });

        string bundleDirectory = bundle.DirectoryPath;
        await bundle.DisposeAsync();
        await bundle.DisposeAsync();
        Assert.That(Directory.Exists(bundleDirectory), Is.False);
    }

    [Test]
    public async Task OpensTheNativeReplayRowReturnedByTheLibraryScreen()
    {
        ExternalLazerReplaySummary catalogReplay = replaySummary();
        var runtime = new AssetRuntimeClient(catalogReplay, missingKind: null);
        var service = new ExternalLazerReplayOpenService(
            Path.GetFullPath("lazer-library"),
            new ExternalLazerAssetClient(runtime));
        var nativeReplay = new LocalReplay(
            catalogReplay.ScoreId,
            catalogReplay.SetId,
            catalogReplay.BeatmapId,
            catalogReplay.Title,
            catalogReplay.Artist,
            catalogReplay.Difficulty,
            catalogReplay.RulesetShortName,
            catalogReplay.Player,
            catalogReplay.PlayedAt,
            catalogReplay.StarRating,
            catalogReplay.Accuracy,
            catalogReplay.TotalScore,
            catalogReplay.MaxCombo,
            catalogReplay.MissCount,
            catalogReplay.PerformancePoints,
            catalogReplay.Mods,
            catalogReplay.HasReplayFile,
            catalogReplay.BeatmapHash);

        await using ExternalLazerPlayableReplayBundle bundle = await service.OpenAsync(nativeReplay);

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(bundle.BeatmapPath), Is.True);
            Assert.That(File.Exists(bundle.ReplayPath), Is.True);
            Assert.That(Directory.Exists(runtime.StagingDirectory), Is.False);
        });
    }

    [Test]
    public void ReportsTheExactMissingPlayableAssetAndCleansStaging()
    {
        ExternalLazerReplaySummary replay = replaySummary();
        var runtime = new AssetRuntimeClient(replay, "Audio");
        var service = new ExternalLazerReplayOpenService(
            Path.GetFullPath("lazer-library"),
            new ExternalLazerAssetClient(runtime));

        ExternalLazerReplayOpenException error = Assert.ThrowsAsync<ExternalLazerReplayOpenException>(async () =>
            await service.OpenAsync(replay))!;

        Assert.Multiple(() =>
        {
            Assert.That(error.Code, Is.EqualTo("audio_file_missing"));
            Assert.That(error.Message, Is.EqualTo("The selected beatmap audio is missing from lazer storage."));
            Assert.That(Directory.Exists(runtime.StagingDirectory), Is.False);
        });
    }

    [Test]
    public void MaterialisationFailureRemovesBothPrivateStagingDirectories()
    {
        ExternalLazerReplaySummary replay = replaySummary();
        var runtime = new AssetRuntimeClient(replay, missingKind: null, corruptLengthKind: "Beatmap");
        var service = new ExternalLazerReplayOpenService(
            Path.GetFullPath("lazer-library"),
            new ExternalLazerAssetClient(runtime));
        string[] bundlesBefore = Directory.GetDirectories(Path.GetTempPath(), "aimmod-replay-open-*");

        ExternalLazerReplayOpenException error = Assert.ThrowsAsync<ExternalLazerReplayOpenException>(async () =>
            await service.OpenAsync(replay))!;
        string[] bundlesAfter = Directory.GetDirectories(Path.GetTempPath(), "aimmod-replay-open-*");

        Assert.Multiple(() =>
        {
            Assert.That(error.Code, Is.EqualTo("staged_asset_changed"));
            Assert.That(Directory.Exists(runtime.StagingDirectory), Is.False);
            Assert.That(bundlesAfter.Except(bundlesBefore), Is.Empty);
        });
    }

    [Test]
    public async Task CancellationWaitsForWorkerThenRemovesItsStaging()
    {
        ExternalLazerReplaySummary replay = replaySummary();
        var runtime = new DelayedAssetRuntimeClient(replay);
        var service = new ExternalLazerReplayOpenService(
            Path.GetFullPath("lazer-library"),
            new ExternalLazerAssetClient(runtime));
        using var cancellation = new CancellationTokenSource();

        Task<ExternalLazerPlayableReplayBundle> opening = service.OpenAsync(replay, cancellation.Token);
        await runtime.Started.Task;
        cancellation.Cancel();

        Assert.That(opening.IsCompleted, Is.False);
        runtime.Release.SetResult();
        Assert.CatchAsync<OperationCanceledException>(async () => await opening);
        Assert.That(Directory.Exists(runtime.StagingDirectory), Is.False);
    }

    [Test]
    public void RejectsCatalogRowsWithoutReplayDataBeforeStartingAWorker()
    {
        ExternalLazerReplaySummary replay = replaySummary() with { HasReplayFile = false };
        int calls = 0;
        var service = new ExternalLazerReplayOpenService(
            Path.GetFullPath("lazer-library"),
            (_, _, _, _) =>
            {
                calls++;
                throw new AssertionException("Asset staging must not run.");
            });

        ExternalLazerReplayOpenException error = Assert.ThrowsAsync<ExternalLazerReplayOpenException>(async () =>
            await service.OpenAsync(replay))!;

        Assert.Multiple(() =>
        {
            Assert.That(error.Code, Is.EqualTo("replay_unavailable"));
            Assert.That(calls, Is.Zero);
        });
    }

    private static ExternalLazerReplaySummary replaySummary() => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        new string('a', 64),
        "Title",
        "Artist",
        "Insane",
        "osu",
        "Player",
        DateTimeOffset.UnixEpoch,
        5.2,
        0.98,
        1_000_000,
        500,
        1,
        200,
        Array.Empty<string>(),
        true);

    private sealed class AssetRuntimeClient(
        ExternalLazerReplaySummary replay,
        string? missingKind,
        string? corruptLengthKind = null) : IRuntimeRequestClient
    {
        public string? StagingDirectory { get; private set; }

        public Task<RuntimeResponse> SendAsync(RuntimeRequest request, CancellationToken cancellationToken = default)
        {
            ExternalLazerAssetResolveRequest input = request.Payload!.Value.Deserialize<ExternalLazerAssetResolveRequest>(RuntimeProtocol.JsonOptions)!;
            StagingDirectory = input.StagingDirectory;
            ExternalLazerAssetResolveResult result = createResult(input.StagingDirectory, replay, missingKind, corruptLengthKind);
            return Task.FromResult(success(request, result));
        }
    }

    private sealed class DelayedAssetRuntimeClient(ExternalLazerReplaySummary replay) : IRuntimeRequestClient
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
            return success(request, createResult(input.StagingDirectory, replay, missingKind: null));
        }
    }

    private static ExternalLazerAssetResolveResult createResult(
        string stagingDirectory,
        ExternalLazerReplaySummary replay,
        string? missingKind,
        string? corruptLengthKind = null)
    {
        (string Kind, string Owner, string LogicalName, string Contents)[] assets =
        {
            ("Beatmap", replay.BeatmapHash, "original.osu", "[General]\nAudioFilename: audio/test.mp3\n[Events]\n0,0,\"images/bg.jpg\",0,0"),
            ("Replay", replay.ScoreId.ToString(), "original.osr", "real replay"),
            ("Audio", replay.BeatmapHash, "audio/test.mp3", "real audio"),
            ("Background", replay.BeatmapHash, "images/bg.jpg", "real background"),
        };
        var files = new List<ExternalLazerResolvedAsset>();
        var missing = new List<ExternalLazerMissingAsset>();

        for (int i = 0; i < assets.Length; i++)
        {
            (string kind, string owner, string logicalName, string contents) = assets[i];
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(contents);
            string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

            if (kind == missingKind)
            {
                missing.Add(new ExternalLazerMissingAsset(kind, owner, logicalName, hash, "file_missing"));
                continue;
            }

            string stagedPath = Path.Combine(stagingDirectory, $"{i:D4}-{kind.ToLowerInvariant()}-{hash}{Path.GetExtension(logicalName)}");
            File.WriteAllBytes(stagedPath, bytes);
            long reportedLength = kind == corruptLengthKind ? bytes.Length + 1 : bytes.Length;
            files.Add(new ExternalLazerResolvedAsset(kind, owner, logicalName, hash, stagedPath, reportedLength));
        }

        return new ExternalLazerAssetResolveResult(
            files,
            missing,
            Array.Empty<string>(),
            Array.Empty<Guid>());
    }

    private static RuntimeResponse success(RuntimeRequest request, ExternalLazerAssetResolveResult result) => new(
        request.Id,
        RuntimeProtocol.CurrentVersion,
        true,
        JsonSerializer.SerializeToElement(result, RuntimeProtocol.JsonOptions));
}

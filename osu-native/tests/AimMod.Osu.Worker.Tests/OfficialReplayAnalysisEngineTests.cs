using NUnit.Framework;
using AimMod.Osu.Runtime.Contracts;
using osu.Framework.Timing;
using System.Diagnostics;

namespace AimMod.Osu.Worker.Tests;

[TestFixture]
public sealed class OfficialReplayAnalysisEngineTests
{
    [Test]
    public void HeadlessTrackUsesTheProvidedClockAndPreservesGameplayRate()
    {
        var clock = new ManualClock();
        var track = new HeadlessAnalysisTrack(10_000, clock);
        track.Frequency.Value = 1.5;

        clock.CurrentTime = 100;
        track.Start();
        clock.CurrentTime = 2_100;

        Assert.That(track.CurrentTime, Is.EqualTo(3_000).Within(0.001));

        track.Stop();
        clock.CurrentTime = 4_100;
        Assert.That(track.CurrentTime, Is.EqualTo(3_000).Within(0.001));
        Assert.That(track.Rate, Is.EqualTo(1.5));
    }

    [Test]
    public void CompletesImmediatelyWhenOfficialScoreProcessorCompletes()
    {
        var watchdog = new ReplayAnalysisCompletionWatchdog(120_000, 2_000);

        Assert.That(watchdog.ShouldComplete(10_000, true), Is.True);
    }

    public void UsesOfficialGameplayTimelineAsTerminalFallback()
    {
        var watchdog = new ReplayAnalysisCompletionWatchdog(120_000, 2_000);

        Assert.Multiple(() =>
        {
            Assert.That(watchdog.ShouldComplete(121_999.999, false), Is.False);
            Assert.That(watchdog.ShouldComplete(122_000, false), Is.False);
            Assert.That(watchdog.ShouldComplete(122_000, false), Is.True);
        });
    }

    [Test]
    public void EndsAtFinalReplayFrameWhenRecordingStopsBeforeBeatmap()
    {
        var watchdog = new ReplayAnalysisCompletionWatchdog(120_000, 2_000, 41_250);

        Assert.Multiple(() =>
        {
            Assert.That(watchdog.TerminalGameplayTime, Is.EqualTo(43_250));
            Assert.That(watchdog.ShouldComplete(43_250, false), Is.False);
            Assert.That(watchdog.ShouldComplete(43_250, false), Is.True);
        });
    }

    [Test]
    public void CompletesFailedOfficialPlaybackAfterChildrenSettle()
    {
        var watchdog = new ReplayAnalysisCompletionWatchdog(120_000, 2_000, 80_000);

        Assert.Multiple(() =>
        {
            Assert.That(watchdog.ShouldComplete(40_000, false, true), Is.False);
            Assert.That(watchdog.ShouldComplete(40_000, false, true), Is.True);
        });
    }

    [TestCase(double.NaN)]
    [TestCase(double.NegativeInfinity)]
    [TestCase(double.PositiveInfinity)]
    public void DoesNotCompleteForInvalidGameplayClock(double gameplayTime)
    {
        var watchdog = new ReplayAnalysisCompletionWatchdog(120_000, 2_000);

        Assert.That(watchdog.ShouldComplete(gameplayTime, false), Is.False);
    }

    [Test]
    public async Task AnalysesARealSavedLazerReplayWhenAvailable()
    {
        string? libraryRoot = Environment.GetEnvironmentVariable("AIMMOD_REAL_LAZER_ROOT");
        if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
            Assert.Ignore("Set AIMMOD_REAL_LAZER_ROOT to run the official engine against a saved lazer replay.");
        string searchText = Environment.GetEnvironmentVariable("AIMMOD_REAL_REPLAY_SEARCH") ?? string.Empty;

        ExternalLazerCatalogSearchResult catalog = await new ExternalLazerCatalogBackend().SearchAsync(
            new ExternalLazerCatalogSearchRequest(
                libraryRoot!,
                ExternalLazerCatalogEntryKind.Replays,
                SearchText: searchText,
                Sort: ExternalLazerCatalogSort.RecentlyPlayed,
                Limit: 100),
            CancellationToken.None);
        ExternalLazerReplaySummary replay = catalog.Replays.FirstOrDefault(candidate =>
                                                candidate.HasReplayFile
                                                && !string.IsNullOrWhiteSpace(candidate.BeatmapHash))
                                            ?? throw new AssertionException("The lazer library has no saved osu!standard replay with a beatmap.");

        string assetDirectory = Directory.CreateTempSubdirectory("aimmod-real-replay-assets-").FullName;
        string analysisDirectory = Directory.CreateTempSubdirectory("aimmod-real-replay-analysis-").FullName;
        try
        {
            ExternalLazerAssetResolveResult assets = await new ExternalLazerAssetBackend().ResolveAsync(
                new ExternalLazerAssetResolveRequest(
                    libraryRoot!,
                    assetDirectory,
                    new[] { replay.BeatmapHash },
                    new[] { replay.ScoreId }),
                CancellationToken.None);
            ExternalLazerResolvedAsset beatmap = assets.Files.Single(file =>
                file.Kind == "Beatmap" && string.Equals(file.OwnerId, replay.BeatmapHash, StringComparison.OrdinalIgnoreCase));
            ExternalLazerResolvedAsset replayFile = assets.Files.Single(file =>
                file.Kind == "Replay" && string.Equals(file.OwnerId, replay.ScoreId.ToString(), StringComparison.OrdinalIgnoreCase));

            string beatmapPath = Path.Combine(analysisDirectory, "map.osu");
            string replayPath = Path.Combine(analysisDirectory, "replay.osr");
            File.Copy(beatmap.StagedPath, beatmapPath);
            File.Copy(replayFile.StagedPath, replayPath);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            ReplayAnalysisResult result = await new OfficialReplayAnalysisEngine().AnalyseAsync(
                new ValidatedReplayInput(analysisDirectory, beatmapPath, replayPath),
                timeout.Token);

            Assert.Multiple(() =>
            {
                Assert.That(result.TimeBasis, Is.EqualTo("officialRulesetPlayback"));
                Assert.That(result.Judgements, Is.Not.Empty);
                Assert.That(result.Judgements.All(judgement => double.IsFinite(judgement.JudgementTimeMs)), Is.True);
            });
        }
        finally
        {
            Directory.Delete(assetDirectory, recursive: true);
            Directory.Delete(analysisDirectory, recursive: true);
        }
    }

    [Test]
    public async Task AcceleratesAndNormalizesAStagedReplayWhenAvailable()
    {
        string? stagingDirectory = Environment.GetEnvironmentVariable("AIMMOD_STAGED_REPLAY_DIR");
        if (string.IsNullOrWhiteSpace(stagingDirectory) || !Directory.Exists(stagingDirectory))
            Assert.Ignore("Set AIMMOD_STAGED_REPLAY_DIR to a directory containing beatmap.osu and replay.osr.");

        string beatmapPath = Path.Combine(stagingDirectory!, "beatmap.osu");
        string replayPath = Path.Combine(stagingDirectory, "replay.osr");
        Assert.That(File.Exists(beatmapPath), Is.True, $"Missing {beatmapPath}");
        Assert.That(File.Exists(replayPath), Is.True, $"Missing {replayPath}");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var stopwatch = Stopwatch.StartNew();
        ReplayAnalysisResult result = await new OfficialReplayAnalysisEngine().AnalyseAsync(
            new ValidatedReplayInput(stagingDirectory, beatmapPath, replayPath),
            timeout.Token);
        stopwatch.Stop();

        int summarizedJudgements = result.Summary.Great
                                   + result.Summary.Ok
                                   + result.Summary.Meh
                                   + result.Summary.Miss
                                   + result.Summary.SliderBreaks
                                   + result.Summary.Other;
        double[] reportedRates = result.Judgements
                                       .Where(judgement => judgement.GameplayRate.HasValue)
                                       .Select(judgement => judgement.GameplayRate!.Value)
                                       .ToArray();

        TestContext.Out.WriteLine(
            $"elapsed={stopwatch.Elapsed.TotalSeconds:F3}s judgements={result.Judgements.Count} "
            + $"summary={result.Summary} rates={string.Join(',', reportedRates.Distinct().Order())}");

        Assert.Multiple(() =>
        {
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(30)),
                "A staged replay should complete well below the old 120-second wall-clock limit.");
            Assert.That(result.TimeBasis, Is.EqualTo("officialRulesetPlayback"));
            Assert.That(result.Judgements, Is.Not.Empty);
            Assert.That(summarizedJudgements, Is.EqualTo(result.Judgements.Count));
            Assert.That(result.Judgements.All(judgement => double.IsFinite(judgement.JudgementTimeMs)), Is.True);
            Assert.That(reportedRates, Is.Not.Empty);
            Assert.That(reportedRates.All(rate => double.IsFinite(rate) && rate >= 0.5 && rate <= 2), Is.True,
                "Reported gameplay rates should describe the replay mods, not the internal 16x analysis clock.");
        });
    }
}

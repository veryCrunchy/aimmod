using System.Diagnostics;
using AimMod.Desktop;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime.Contracts;
using AimMod.Osu.Runtime;
using NUnit.Framework;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Game;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using osu.Game.Screens.Play;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class NativeReplayRouteTests
{
    [Test]
    public void PracticeFolderUsesTheWindowsShellWithoutAnExplorerCommandLine()
    {
        string path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AimMod practice map"));

        ProcessStartInfo startInfo = AimModGame.CreatePracticeFolderStartInfo(path);

        Assert.Multiple(() =>
        {
            Assert.That(startInfo.FileName, Is.EqualTo(path));
            Assert.That(startInfo.UseShellExecute, Is.True);
            Assert.That(startInfo.Verb, Is.EqualTo("open"));
            Assert.That(startInfo.ArgumentList, Is.Empty);
        });
    }

    private string temporaryDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"aimmod-native-replay-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(temporaryDirectory))
            Directory.Delete(temporaryDirectory, true);
    }

    [Test]
    public void NativeRouteUsesOfficialPlayerWithoutFullOsuClient()
    {
        Assert.Multiple(() =>
        {
            Assert.That(typeof(AimModGame).IsSubclassOf(typeof(OsuGameBase)), Is.True);
            Assert.That(typeof(NativeReplayPlayer).IsSubclassOf(typeof(ReplayPlayer)), Is.True);
            Assert.That(typeof(ImportedBeatmapReplayDecoder).IsSubclassOf(typeof(LegacyScoreDecoder)), Is.True);
            Assert.That(typeof(AimModGame).Assembly.GetReferencedAssemblies().Select(reference => reference.Name),
                Does.Not.Contain("osu.Desktop"));
        });
    }

    [Test]
    public void ReplayTransportRejectsCommandsUntilOfficialPlayerIsReady()
    {
        using var player = new NativeReplayPlayer(new Score(), () => { }, _ => { });

        Assert.Multiple(() =>
        {
            Assert.That(player.IsTransportReady.Value, Is.False);
            Assert.That(player.CurrentTime.Value, Is.Zero);
            Assert.That(player.Duration.Value, Is.Zero);
            Assert.That(player.IsPaused.Value, Is.True);
            Assert.That(player.PlaybackRate.Value, Is.EqualTo(1));
            Assert.That(player.TogglePause(), Is.False);
            Assert.That(player.SetPaused(false), Is.False);
            Assert.That(player.SeekTo(5000), Is.False);
            Assert.That(player.SeekTo(double.NaN), Is.False);
            Assert.That(player.SetPlaybackRate(1.5), Is.False);
            Assert.That(player.SetPlaybackRate(double.PositiveInfinity), Is.False);
        });
    }

    [Test]
    public void TimelineIsBoundedButNeverDropsLateExactMisses()
    {
        ReplayObjectJudgement[] ordinary = Enumerable.Range(0, 2000)
                                                     .Select(index => judgement(index, index * 10, "Great"))
                                                     .Append(judgement(2000, 25_000, "Miss"))
                                                     .ToArray();
        var result = new ReplayAnalysisResult(
            "test",
            "beatmap",
            true,
            1000,
            Array.Empty<int>(),
            ordinary,
            new ReplayJudgementSummary(2000, 0, 0, 1, 0, 0));

        IReadOnlyList<ReplayTimelineMark> marks = ReplayTimelineSampler.Sample(result, 100);

        Assert.Multiple(() =>
        {
            Assert.That(marks, Has.Count.LessThanOrEqualTo(100));
            Assert.That(marks.Any(mark => mark.Tone == ReplayTimelineTone.Miss && mark.TimeMilliseconds == 25_000), Is.True);
            Assert.That(marks.Select(mark => mark.TimeMilliseconds), Is.Ordered);
        });
    }

    [Test]
    public void NewReplayWorkspacePresentsUsefulEmptyAnalysisState()
    {
        var route = new NativeReplayRouteView();

        Assert.Multiple(() =>
        {
            Assert.That(text(route, "analysisTitle"), Is.EqualTo("No replay selected"));
            Assert.That(container(route, "notableRows").Count, Is.EqualTo(1));
            Assert.That(container(route, "mapPatternRows").Count, Is.EqualTo(1));
        });
    }

    [Test]
    public void SelectingReplayImmediatelyPresentsCompletedCachedAnalysis()
    {
        Guid scoreId = Guid.NewGuid();
        ReplayAnalysisResult result = analysis("Miss");
        var route = new NativeReplayRouteView(analyses: new Dictionary<Guid, ReplayAnalysisResult> { [scoreId] = result });

        route.SetReplaySummary(replay(scoreId));

        Assert.Multiple(() =>
        {
            Assert.That(text(route, "statusTitle"), Is.EqualTo("Title"));
            Assert.That(text(route, "statusDetail"), Does.Contain("Difficulty"));
            Assert.That(text(route, "summaryAccuracy"), Is.EqualTo("98.00%"));
            Assert.That(text(route, "summaryPerformance"), Is.EqualTo("100pp"));
            Assert.That(text(route, "analysisTitle"), Is.EqualTo("Exact replay analysis"));
            Assert.That(text(route, "analysisNextPlay"), Does.Not.Contain("will appear"));
            Assert.That(container(route, "notableRows").Count, Is.EqualTo(1));
        });
    }

    [Test]
    public void SuspendedTransportInvalidatesAlreadyScheduledActions()
    {
        var lifetime = new ReplayTransportLifetime();

        Assert.That(lifetime.TryCapture(out int scheduledGeneration), Is.True);
        Assert.That(lifetime.CanRun(scheduledGeneration), Is.True);

        lifetime.InvalidateScheduledActions();

        Assert.Multiple(() =>
        {
            Assert.That(lifetime.CanRun(scheduledGeneration), Is.False);
            Assert.That(lifetime.CanAcceptCommands, Is.True);
            Assert.That(lifetime.TryCapture(out int replacementGeneration), Is.True);
            Assert.That(lifetime.CanRun(replacementGeneration), Is.True);
        });
    }

    [Test]
    public void CompletedTransportRejectsQueuedAndFutureActions()
    {
        var lifetime = new ReplayTransportLifetime();
        Assert.That(lifetime.TryCapture(out int scheduledGeneration), Is.True);

        Assert.That(lifetime.TryComplete(), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(lifetime.CanRun(scheduledGeneration), Is.False);
            Assert.That(lifetime.CanAcceptCommands, Is.False);
            Assert.That(lifetime.TryCapture(out _), Is.False);
            Assert.That(lifetime.TryComplete(), Is.False);
        });
    }

    [Test]
    public void DisposedTransportRejectsQueuedAndFutureActions()
    {
        var lifetime = new ReplayTransportLifetime();
        Assert.That(lifetime.TryCapture(out int scheduledGeneration), Is.True);

        lifetime.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(lifetime.CanRun(scheduledGeneration), Is.False);
            Assert.That(lifetime.CanAcceptCommands, Is.False);
            Assert.That(lifetime.TryCapture(out _), Is.False);
        });
    }

    [Test]
    public void NewReplayAcceptsFreshAnalysisAfterPreviousHigherRevision()
    {
        var route = new NativeReplayRouteView();
        route.SetReplaySummary(replay(Guid.NewGuid()));
        route.ShowAnalysisState(new ReplayAnalysisState(12, ReplayAnalysisStatus.Completed, Result: analysis("Miss")));

        route.SetReplaySummary(replay(Guid.NewGuid()));
        route.ShowAnalysisState(new ReplayAnalysisState(1, ReplayAnalysisStatus.Completed, Result: analysis("SliderBreak")));

        Assert.Multiple(() =>
        {
            Assert.That(text(route, "analysisTitle"), Is.EqualTo("Exact replay analysis"));
            Assert.That(container(route, "notableRows").Count, Is.EqualTo(1));
            Assert.That(text(route, "analysisNextPlay"), Does.Not.Contain("will appear"));
        });
    }

    [Test]
    public void CompletedCleanReplayShowsExplicitEmptyNotableStateAndFocus()
    {
        var route = new NativeReplayRouteView();
        route.SetReplaySummary(replay(Guid.NewGuid()));
        route.ShowAnalysisState(new ReplayAnalysisState(2, ReplayAnalysisStatus.Completed, Result: analysis("Great")));

        Assert.Multiple(() =>
        {
            Assert.That(container(route, "notableRows").Count, Is.EqualTo(1));
            Assert.That(text(route, "analysisNextPlay"), Is.Not.Empty);
            Assert.That(text(route, "analysisNextPlay"), Does.Not.Contain("will appear"));
        });
    }

    [Test]
    public void FailedAndIdleAnalysisReplacePendingPlaceholdersWithExplicitStates()
    {
        var route = new NativeReplayRouteView();
        route.SetReplaySummary(replay(Guid.NewGuid()));
        route.ShowAnalysisState(new ReplayAnalysisState(1, ReplayAnalysisStatus.Failed,
            Error: new ReplayAnalysisFailure("test", "Exact analysis failed.")));

        Assert.Multiple(() =>
        {
            Assert.That(text(route, "analysisSummary"), Is.EqualTo("Exact analysis failed."));
            Assert.That(text(route, "analysisNextPlay"), Does.Contain("unavailable"));
            Assert.That(container(route, "notableRows").Count, Is.EqualTo(1));
        });

        route.SetReplaySummary(replay(Guid.NewGuid()));
        route.ShowAnalysisState(new ReplayAnalysisState(0, ReplayAnalysisStatus.Idle));

        Assert.Multiple(() =>
        {
            Assert.That(text(route, "analysisSummary"), Does.Contain("has not started"));
            Assert.That(text(route, "analysisNextPlay"), Does.Contain("Open this run"));
            Assert.That(container(route, "notableRows").Count, Is.EqualTo(1));
        });
    }

    [Test]
    public void ParsesPairedBeatmapAndReplayFiles()
    {
        string beatmap = createFile("set.osz");
        string replay = createFile("play.osr");

        AimModLaunchOptions result = AimModLaunchOptions.Parse(new[] { "--beatmap", beatmap, "--replay", replay });

        Assert.Multiple(() =>
        {
            Assert.That(result.Error, Is.Null);
            Assert.That(result.Replay?.BeatmapPath, Is.EqualTo(Path.GetFullPath(beatmap)));
            Assert.That(result.Replay?.ReplayPath, Is.EqualTo(Path.GetFullPath(replay)));
        });
    }

    [Test]
    public void RejectsAnIncompleteReplayRequest()
    {
        string replay = createFile("play.osr");

        AimModLaunchOptions result = AimModLaunchOptions.Parse(new[] { "--replay", replay });

        Assert.Multiple(() =>
        {
            Assert.That(result.Replay, Is.Null);
            Assert.That(result.Error, Is.EqualTo("Opening a replay needs both --beatmap and --replay."));
        });
    }

    [Test]
    public void RejectsUnsupportedFileTypesBeforeStartingTheGame()
    {
        string beatmap = createFile("set.zip");
        string replay = createFile("play.osr");

        AimModLaunchOptions result = AimModLaunchOptions.Parse(new[] { "--beatmap", beatmap, "--replay", replay });

        Assert.That(result.Error, Does.Contain(".osz"));
    }

    private string createFile(string name)
    {
        string path = Path.Combine(temporaryDirectory, name);
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    private static ReplayObjectJudgement judgement(int index, double time, string result) => new(
        index,
        null,
        "HitCircle",
        time,
        time,
        result,
        "Great",
        time,
        0,
        1,
        null,
        null,
        0,
        0);

    private static ReplayAnalysisResult analysis(string result) => new(
        ReplayAnalysisProtocol.EngineVersion,
        "gameplay-clock",
        true,
        ReplayAnalysisProtocol.WallClockTimeoutMs,
        Array.Empty<int>(),
        new[] { judgement(7, 12_345, result) },
        result == "Great"
            ? new ReplayJudgementSummary(1, 0, 0, 0, 0, 0)
            : new ReplayJudgementSummary(0, 0, 0, 1, 0, 0));

    private static LocalReplay replay(Guid scoreId) => new(
        scoreId,
        Guid.NewGuid(),
        Guid.NewGuid(),
        "Title",
        "Artist",
        "Difficulty",
        "osu",
        "Player",
        DateTimeOffset.UtcNow,
        5,
        0.98,
        1_000_000,
        500,
        1,
        100,
        Array.Empty<string>(),
        true,
        "beatmap-hash");

    private static string text(NativeReplayRouteView route, string fieldName)
    {
        object value = field(route, fieldName);
        return value switch
        {
            SpriteText spriteText => spriteText.Text.ToString(),
            _ when value.GetType().GetProperty("Text")?.GetValue(value) is string wrappedText => wrappedText,
            _ => throw new AssertionException($"{fieldName} is not a supported text drawable."),
        };
    }

    private static FillFlowContainer<osu.Framework.Graphics.Drawable> container(NativeReplayRouteView route, string fieldName) =>
        (FillFlowContainer<osu.Framework.Graphics.Drawable>)field(route, fieldName);

    private static object field(NativeReplayRouteView route, string fieldName) =>
        typeof(NativeReplayRouteView).GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(route)
        ?? throw new AssertionException($"Could not find {fieldName}.");
}

using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Replays;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Scoring;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class ReplayPlaybackControlsTests
{
    [Test]
    public void MomentsIncludeMissesAndSliderBreaksInTimeOrder()
    {
        IReadOnlyList<ReplayMoment> moments = ReplayMoment.From(analysis(
            judgement(4, 9_000, "LargeTickMiss"),
            judgement(1, 2_000, "Great"),
            judgement(2, 3_000, "Miss"),
            judgement(3, 500, "Miss")));

        Assert.Multiple(() =>
        {
            Assert.That(moments.Select(moment => moment.TimeMs), Is.EqualTo(new[] { 500d, 3_000, 9_000 }));
            Assert.That(moments[2].Severity, Is.EqualTo(ReplayMomentSeverity.SliderBreak));
            Assert.That(moments[0].SeekTimeMs, Is.Zero, "Pre-roll never seeks before the start.");
            Assert.That(moments[1].SeekTimeMs, Is.EqualTo(3_000 - ReplayTransportController.MomentPreRollMs));
            Assert.That(moments[1].ObjectLabel, Is.EqualTo("Object 3"));
        });
    }

    [Test]
    public void PreviousAndNextMistakeStepAwayFromTheCurrentPosition()
    {
        IReadOnlyList<ReplayMoment> moments = ReplayMoment.From(analysis(
            judgement(1, 5_000, "Miss"),
            judgement(2, 10_000, "Miss"),
            judgement(3, 20_000, "Miss")));

        Assert.Multiple(() =>
        {
            Assert.That(ReplayTransportController.FindAdjacentMoment(moments, 0, 1)?.TimeMs, Is.EqualTo(5_000));
            // Sitting on a moment's lead-in must move on, not re-select the same moment.
            Assert.That(ReplayTransportController.FindAdjacentMoment(moments, moments[0].SeekTimeMs, 1)?.TimeMs, Is.EqualTo(10_000));
            Assert.That(ReplayTransportController.FindAdjacentMoment(moments, moments[1].SeekTimeMs, -1)?.TimeMs, Is.EqualTo(5_000));
            Assert.That(ReplayTransportController.FindAdjacentMoment(moments, 30_000, 1), Is.Null);
            Assert.That(ReplayTransportController.FindAdjacentMoment(moments, 0, -1), Is.Null);
            Assert.That(ReplayTransportController.FindAdjacentMoment(moments, 12_000, 0), Is.Null);
        });
    }

    [Test]
    public void TransportRejectsCommandsWithoutAPlayerAndUsesAnalysedLength()
    {
        var transport = new ReplayTransportController();
        transport.SetAnalysis(analysis(judgement(1, 42_000, "Miss")));

        Assert.Multiple(() =>
        {
            Assert.That(transport.Duration, Is.EqualTo(42_000));
            Assert.That(transport.IsPaused, Is.True);
            Assert.That(transport.TogglePause(), Is.False);
            Assert.That(transport.SeekBy(5_000), Is.False);
            Assert.That(transport.StepFrame(1), Is.False);
            Assert.That(transport.JumpToMoment(1), Is.False);
            Assert.That(transport.SetPlaybackRate(0.5), Is.False);
        });
    }

    [Test]
    public void PlaybackRatesCoverQuarterToDoubleSpeed()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ReplayTransportController.PlaybackRates.First(), Is.EqualTo(0.25));
            Assert.That(ReplayTransportController.PlaybackRates.Last(), Is.EqualTo(2));
            Assert.That(ReplayTransportController.PlaybackRates, Is.Ordered);
            Assert.That(ReplayTransportController.FormatRate(0.75), Is.EqualTo("0.75x"));
            Assert.That(ReplayTransportController.FormatRate(1), Is.EqualTo("1x"));
        });
    }

    [Test]
    public void EmbeddedPlayerDisablesTheDuplicateLeaderboardAndShowsHudByDefault()
    {
        using var player = new NativeReplayPlayer(new Score(), () => { }, _ => { });
        Assert.Multiple(() =>
        {
            Assert.That(player.Configuration.ShowLeaderboard, Is.False);
            Assert.That(player.Configuration.ShowResults, Is.False);
            Assert.That(player.ShowGameplayHud.Value, Is.True);
            Assert.That(player.ReplayChromeRemoved, Is.False, "Chrome is removed once the official player has loaded.");
        });
    }

    [Test]
    public void MissMarkerTooltipNamesTimeObjectAndCause()
    {
        var moment = new ReplayMoment(3_500, ReplayMomentSeverity.Miss, "Object 6", "Likely overshoot");
        string tooltip = ReplayTimelineScrubberTooltip(moment);
        Assert.Multiple(() =>
        {
            Assert.That(tooltip, Does.Contain("Miss at 0:03.500"));
            Assert.That(tooltip, Does.Contain("Object 6"));
            Assert.That(tooltip, Does.Contain("Likely overshoot"));
        });
    }

    [Test]
    public void ScoreAndAnalysisDisagreementIsExplained()
    {
        LocalReplay replay = localReplay(misses: 0);
        Assert.Multiple(() =>
        {
            Assert.That(NativeReplayRouteView.DescribeScoreMismatch(replay, new ReplayJudgementSummary(10, 0, 0, 0, 0, 0)), Is.Null);
            string? mismatch = NativeReplayRouteView.DescribeScoreMismatch(replay, new ReplayJudgementSummary(150, 0, 0, 10, 0, 0));
            Assert.That(mismatch, Does.Contain("Score: 0 misses"));
            Assert.That(mismatch, Does.Contain("analysis found 10 misses"));
        });
    }

    [Test]
    public void JudgementBarKeepsCountsForEachJudgement()
    {
        var parts = ReplayJudgementBar.Parts(new ReplayJudgementSummary(129, 14, 7, 10, 2, 0));
        Assert.Multiple(() =>
        {
            Assert.That(parts.Select(part => part.Label), Is.EqualTo(new[] { "300", "100", "50", "miss", "slider break" }));
            Assert.That(parts.Select(part => part.Count), Is.EqualTo(new[] { 129, 14, 7, 10, 2 }));
        });
        Assert.That(ReplayJudgementBar.Parts(new ReplayJudgementSummary(1, 0, 0, 0, 0, 0)).Select(part => part.Label), Does.Not.Contain("slider break"));
    }

    [Test]
    public void DisplaySettingsPopoverIsUnmaskedAndDrawnAboveItsNeighbours()
    {
        using var route = new NativeReplayRouteView();
        route.OpenPlaybackSettingsForCapture();
        IReadOnlyList<Drawable> path = pathTo(route, route.DisplaySettings);

        Assert.Multiple(() =>
        {
            Assert.That(route.DisplaySettingsOpen, Is.True);
            assertUnmasked(path);
            // Before the popup layer lifts it (on load), sibling order alone already puts the branch in front.
            assertDrawnAboveSiblings(path);
        });
    }

    [Test]
    public void SpeedMenuAncestorsAreUnmaskedAndDrawnAboveTheViewportAndSidePanel()
    {
        using var route = new NativeReplayRouteView();
        IReadOnlyList<Drawable> path = pathTo(route, route.TransportBar.SpeedDropdown);

        Assert.Multiple(() =>
        {
            assertUnmasked(path);
            Assert.That(path, Does.Contain(route.PlaybackColumn));
            var body = (Container<Drawable>)path[path.ToList().IndexOf(route.PlaybackColumn) - 1];
            var playbackArea = (Container<Drawable>)path[path.ToList().IndexOf(route.TransportBar) - 1];
            Assert.That(isDrawnAbove(body, route.PlaybackColumn, route.SidePanel), Is.True, "Playback menus must draw above the side panel.");
            Assert.That(isDrawnAbove(playbackArea, route.TransportBar, route.Viewport), Is.True, "The speed menu must draw above the viewport.");
        });
    }

    [Test]
    public void LibraryFilterRowDrawsAboveTheReplayList()
    {
        using var route = new NativeReplayRouteView();
        foreach (Drawable filter in new[] { route.ModeFilter, route.ModFilter })
        {
            IReadOnlyList<Drawable> path = pathTo(route, filter);
            // The filter row is the GridContainer; the library content holds it beside the search box and list.
            int rowIndex = path.ToList().FindIndex(drawable => drawable is GridContainer);
            var row = path[rowIndex];
            var library = (Container<Drawable>)path[rowIndex - 1];
            Assert.Multiple(() =>
            {
                Assert.That(library.Children.Where(child => child != row).All(child => isDrawnAbove(library, row, child)), Is.True,
                    "The filter row must draw above the search box, status and list.");
                assertUnmasked(path.Skip(rowIndex - 1).ToArray());
            });
        }
    }

    [Test]
    public void ChoosingAnAttemptOpensTheSummaryTab()
    {
        using var route = new NativeReplayRouteView();
        Assert.That(route.SideTab.Value, Is.EqualTo(ReplaySidePanelTab.Library));
        route.SetReplaySummary(localReplay(misses: 2));
        Assert.Multiple(() =>
        {
            Assert.That(route.SideTab.Value, Is.EqualTo(ReplaySidePanelTab.Summary));
            Assert.That(route.NowPlaying.TitleText, Is.EqualTo("Title"));
        });
    }

    private static string ReplayTimelineScrubberTooltip(ReplayMoment moment) =>
        (string)typeof(ReplayTimelineScrubber).GetNestedType("MomentMarker", System.Reflection.BindingFlags.NonPublic)!
                                              .GetMethod("Describe", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                                              .Invoke(null, [moment])!;

    /// <summary>Top-down path from <paramref name="root"/> to <paramref name="target"/> through public children.</summary>
    private static IReadOnlyList<Drawable> pathTo(Drawable root, Drawable target)
    {
        if (root == target)
            return new[] { root };
        IEnumerable<Drawable> children = root switch
        {
            GridContainer grid => grid.Content.SelectMany(row => row).Where(cell => cell is not null),
            IContainerEnumerable<Drawable> container => container.Children,
            _ => Array.Empty<Drawable>(),
        };
        foreach (Drawable child in children)
        {
            IReadOnlyList<Drawable> path = pathTo(child, target);
            if (path.Count > 0)
                return new[] { root }.Concat(path).ToArray();
        }

        return Array.Empty<Drawable>();
    }

    /// <summary>Children are kept in draw order (depth, then insertion), so a later index draws in front.</summary>
    private static bool isDrawnAbove(Container<Drawable> parent, Drawable drawable, Drawable sibling) =>
        parent.Children.ToList().IndexOf(drawable) > parent.Children.ToList().IndexOf(sibling);

    private static void assertDrawnAboveSiblings(IReadOnlyList<Drawable> path)
    {
        for (int index = 1; index < path.Count; index++)
        {
            if (path[index - 1] is not Container<Drawable> container || !container.Children.Contains(path[index]))
                continue;
            foreach (Drawable sibling in container.Children.Where(sibling => sibling != path[index] && sibling.IsPresent))
                Assert.That(isDrawnAbove(container, path[index], sibling), Is.True, $"{path[index].GetType().Name} must draw above {sibling.GetType().Name}.");
        }
    }

    private static void assertUnmasked(IReadOnlyList<Drawable> path)
    {
        Assert.That(path, Is.Not.Empty, "The popup must be reachable from the route.");
        // The popup itself may mask its own content; only its ancestors would clip it.
        foreach (Drawable ancestor in path.Take(path.Count - 1))
            Assert.That(ancestor is CompositeDrawable { Masking: true }, Is.False, $"{ancestor.GetType().Name} would clip the open popup.");
    }

    private static ReplayObjectJudgement judgement(int index, double time, string result) => new(
        index, null, "HitCircle", time, time, result, "Great", time, 0, 1, null, null, 0, 0);

    private static ReplayAnalysisResult analysis(params ReplayObjectJudgement[] judgements) => new(
        ReplayAnalysisProtocol.EngineVersion, "gameplay-clock", true, ReplayAnalysisProtocol.WallClockTimeoutMs,
        Array.Empty<int>(), judgements,
        new ReplayJudgementSummary(judgements.Count(j => j.Result == "Great"), 0, 0, judgements.Count(j => j.Result == "Miss"), 0, 0));

    private static LocalReplay localReplay(int misses) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Title", "Artist", "Difficulty", "osu", "Player",
        DateTimeOffset.UtcNow, 5, 0.98, 1_000_000, 500, misses, 100, Array.Empty<string>(), true, "beatmap-hash");
}

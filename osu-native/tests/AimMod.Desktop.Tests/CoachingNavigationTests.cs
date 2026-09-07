using System.Reflection;
using AimMod.Desktop.Coaching;
using AimMod.Desktop.Practice;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public class CoachingNavigationTests
{
    private const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public void BeatmapGroupingKeepsPlayersAndDifferentDifficultiesSeparate()
    {
        var tracking = new PracticeTracking("Player", 42, 123, "hash", Guid.NewGuid(), "", false, [], []);
        var map = new SavedPracticeMap("one", "Song", "Hard", PracticeDrillType.Streams, DateTimeOffset.UtcNow, 0, 1000, 60000, 4, 100, Tracking: tracking);
        var key = NativeCoachingWorkspace.CoachingMapKey(map);
        Assert.That(NativeCoachingWorkspace.CoachingMapKey(map with { Id="two", PlaybackRate=.8 }), Is.EqualTo(key));
        Assert.That(NativeCoachingWorkspace.CoachingMapKey(map with { Tracking=tracking with { OnlineBeatmapId=124 } }), Is.Not.EqualTo(key));
        Assert.That(NativeCoachingWorkspace.CoachingMapKey(map with { Tracking=tracking with { Player="Other" } }), Is.Not.EqualTo(key));
        Assert.That(NativeCoachingWorkspace.CoachingMapKey(map with { Tracking=null }), Is.Not.EqualTo(NativeCoachingWorkspace.CoachingMapKey(map with { Id="two", Tracking=null })));
    }

    [Test]
    public void OnlyChosenPageIsVisibleAndRunSelectionOpensProgress()
    {
        var run = new LocalReplay(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Practice", "Artist", "Normal",
            "osu", "Practice Player", DateTimeOffset.UtcNow.AddMinutes(-5), 3, .95, 10000, 100, 0, null, [], true);
        using var view = new NativeCoachingWorkspace(new InMemoryLocalLibrarySource([], [run]),
            new Dictionary<Guid, ReplayAnalysisResult>(), _ => { });
        var type = typeof(NativeCoachingWorkspace);
        type.GetMethod("apply", flags)!.Invoke(view, new object[] { new[] { run } });
        var pages = (AimModScrollContainer[])type.GetField("coachingPages", flags)!.GetValue(view)!;
        Assert.That(pages.Select(p => p.Alpha), Is.EqualTo(new[] { 0f, 0f, 1f, 0f, 0f }));
        type.GetMethod("showCoachingPage", flags)!.Invoke(view, new object[] { 2 });
        Assert.That(pages.Select(p => p.Alpha), Is.EqualTo(new[] { 0f, 0f, 1f, 0f, 0f }));
        type.GetMethod("selectRun", flags)!.Invoke(view, new object[] { run.ScoreId });
        Assert.That(pages.Select(p => p.Alpha), Is.EqualTo(new[] { 0f, 1f, 0f, 0f, 0f }));
        var model = (NativeCoachingWorkspaceModel)type.GetField("workspace", flags)!.GetValue(view)!;
        Assert.That(model.SelectedRun?.ScoreId, Is.EqualTo(run.ScoreId));
    }

    [Test]
    public void SavedPracticeSetsAreReachableWithoutSelectingAPlay()
    {
        using var view = new NativeCoachingWorkspace(new InMemoryLocalLibrarySource([], []),
            new Dictionary<Guid, ReplayAnalysisResult>(), _ => { });
        var type = typeof(NativeCoachingWorkspace);
        type.GetMethod("showCoachingPage", flags)!.Invoke(view, new object[] { 3 });
        var pages = (AimModScrollContainer[])type.GetField("coachingPages", flags)!.GetValue(view)!;
        Assert.That(pages.Select(p => p.Alpha), Is.EqualTo(new[] { 0f, 0f, 0f, 1f, 0f }));
        Assert.That(type.GetField("coachingTargetScoreId", flags)!.GetValue(view), Is.Null);
    }

    [Test]
    public void SelectedOlderPlayKeepsItsMapAndBaseline()
    {
        var chosen = new LocalReplay(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Chosen map", "Artist", "Normal",
            "osu", "Practice Player", DateTimeOffset.UtcNow.AddMinutes(-15), 3, .95, 10000, 100, 0, null, [], true);
        var other = chosen with { ScoreId = Guid.NewGuid(), BeatmapId = Guid.NewGuid(), Title = "Newer unrelated map",
            PlayedAt = DateTimeOffset.UtcNow.AddMinutes(-1), Accuracy = .8 };
        using var view = new NativeCoachingWorkspace(new InMemoryLocalLibrarySource([], [chosen, other]),
            new Dictionary<Guid, ReplayAnalysisResult>(), _ => { });
        var type = typeof(NativeCoachingWorkspace);
        type.GetMethod("apply", flags)!.Invoke(view, new object[] { new[] { chosen, other } });
        type.GetMethod("chooseCoachingRun", flags)!.Invoke(view, new object[] { chosen.ScoreId });
        var model = (NativeCoachingWorkspaceModel)type.GetField("workspace", flags)!.GetValue(view)!;
        var plan = (CoachingTrainingPlan)type.GetMethod("chosenPlan", flags)!.Invoke(view, new object[] { model })!;
        Assert.Multiple(() => {
            Assert.That(plan.TargetScoreId, Is.EqualTo(chosen.ScoreId));
            Assert.That(plan.TargetTitle, Is.EqualTo(chosen.Title));
            Assert.That(plan.BaselineAccuracy, Is.EqualTo(.95));
            Assert.That(plan.BaselineCount, Is.EqualTo(1));
        });
        var pages = (AimModScrollContainer[])type.GetField("coachingPages", flags)!.GetValue(view)!;
        Assert.That(pages.Select(p => p.Alpha), Is.EqualTo(new[] { 0f, 0f, 0f, 0f, 1f }));
    }

    [Test]
    public void ReturningToPracticeDoesNotRestartTrackedSession()
    {
        using var view = new NativeCoachingWorkspace(new InMemoryLocalLibrarySource([], []),
            new Dictionary<Guid, ReplayAnalysisResult>(), _ => { });
        var plan = new CoachingTrainingPlan("Focus", "Why", "Cue", Guid.NewGuid(), "Practice", "Normal",
            "setup", "No mods", "Practice Player", 3, .95, 0, .952, 0, DateTimeOffset.UtcNow.AddMinutes(-10));
        var type = typeof(NativeCoachingWorkspace);
        type.GetField("activeTraining", flags)!.SetValue(view, plan);
        var model = NativeCoachingWorkspaceModel.Build([], new Dictionary<Guid, ReplayAnalysisResult>());
        type.GetMethod("startTraining", flags)!.Invoke(view, new object[] { plan with { StartedAt = null }, model });
        Assert.That(type.GetField("activeTraining", flags)!.GetValue(view), Is.SameAs(plan));
    }
}

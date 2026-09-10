using System.Reflection;
using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.ScoreHistory;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed partial class CoachingHistoryRefreshTests
{
    private const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public async Task RefreshShowsNewestStablePlayAndKeepsSubmittedSelectionDuringOnlineFailure()
    {
        var older = run();
        var source = new MutableSource { Runs = [older] };
        var online = new HistoryService();
        using var view = new TestWorkspace(source, online);
        await refresh(view);
        var submitted = view.PracticeSourceHistory.Single(r => !r.IsLocallyStored);
        invoke(view, "chooseCoachingRun", submitted.ScoreId);

        var newest = older with { ScoreId = Guid.NewGuid(), PlayedAt = DateTimeOffset.UtcNow.AddSeconds(-1), Title = "Newest stable play" };
        source.Runs = [newest, older];
        online.Fail = true;
        await refresh(view);
        Assert.That(view.PracticeSourceHistory.Select(r => r.ScoreId), Does.Contain(submitted.ScoreId));
        var model = (NativeCoachingWorkspaceModel)field(view, "workspace")!;
        Assert.That(model.History[0].ScoreId, Is.EqualTo(newest.ScoreId));
        Assert.That(((LocalReplay)field(view, "coachingMapRun")!).ScoreId, Is.EqualTo(submitted.ScoreId));
        Assert.That(buttons(view).Select(caption), Does.Contain("Prepare practice set"));
        var rows = (FillFlowContainer<Drawable>)field(view, "runList")!;
        Assert.That(descendants(rows).OfType<OsuSpriteText>().Select(t => t.Text.ToString()), Does.Contain(newest.Title));
    }

    [TestCase(LocalLibraryOrigin.Stable)]
    [TestCase(LocalLibraryOrigin.Lazer)]
    public void DisplayedPlayCanOpenPracticeEvenWhenHistoryChangedBeforeClick(LocalLibraryOrigin origin)
    {
        var selected = run() with { Origin = origin, HasReplayFile = false };
        using var view = new TestWorkspace(new MutableSource(), new HistoryService());
        invoke(view, "apply", (object)new[] { run() });
        invoke(view, "openCoachingRun", selected);
        Assert.That(field(view, "coachingMapRun"), Is.EqualTo(selected));
        Assert.That(buttons(view).Select(caption), Does.Contain("Prepare practice set"));
    }

    [Test]
    public void QueuedRefreshCannotApplyAfterCancellationOrAccountChange()
    {
        int account = 42;
        using var view = new TestWorkspace(new MutableSource(), new HistoryService(), () => account);
        using var cancellation = new CancellationTokenSource();
        bool cancelledApplied = false, otherAccountApplied = false;
        invoke(view, "scheduleHistoryUpdate", (Action)(() => cancelledApplied = true), 42, cancellation.Token);
        invoke(view, "scheduleHistoryUpdate", (Action)(() => otherAccountApplied = true), 42, CancellationToken.None);
        cancellation.Cancel();
        account = 43;
        view.Drain();
        Assert.That(cancelledApplied || otherAccountApplied, Is.False);
    }

    private static async Task refresh(TestWorkspace view)
    {
        invoke(view, "load");
        await ((Task)field(view, "historyLoadTask")!).WaitAsync(TimeSpan.FromSeconds(10));
        view.Drain();
    }
    private static LocalReplay run() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Stable fixture", "Artist", "Hard",
        "osu", "Synthetic Player", DateTimeOffset.UtcNow.AddMinutes(-5), 4, .95, 123456, 100, 1, null, [], false,
        Origin: LocalLibraryOrigin.Stable, OnlineBeatmapId: 123);
    private static object? invoke(NativeCoachingWorkspace view, string name, params object[] args) => typeof(NativeCoachingWorkspace).GetMethod(name, flags)!.Invoke(view, args);
    private static object? field(NativeCoachingWorkspace view, string name) => typeof(NativeCoachingWorkspace).GetField(name, flags)!.GetValue(view);
    private static IEnumerable<AimModButton> buttons(NativeCoachingWorkspace view) => descendants((Drawable)field(view, "mapDetailHost")!).OfType<AimModButton>();
    private static string caption(AimModButton button) => ((OsuSpriteText)typeof(AimModButton).GetField("caption", flags)!.GetValue(button)!).Text.ToString();
    private static IEnumerable<Drawable> descendants(Drawable drawable)
    {
        yield return drawable;
        if (drawable is CompositeDrawable composite)
            foreach (var child in (IEnumerable<Drawable>)typeof(CompositeDrawable).GetProperty("InternalChildren", flags)!.GetValue(composite)!)
                foreach (var descendant in descendants(child)) yield return descendant;
    }
    private sealed partial class TestWorkspace(ILocalLibrarySource source, IAccountScoreHistoryService online, Func<int>? account = null)
        : NativeCoachingWorkspace(source, new Dictionary<Guid, ReplayAnalysisResult>(), _ => { }, () => online, accountId: account)
    {
        public void Drain() => Scheduler.Update();
    }
    private sealed class MutableSource : ILocalLibrarySource
    {
        public LocalReplay[] Runs { get; set; } = [];
        public void Invalidate() { }
        public ValueTask<LocalLibraryPage<LocalReplay>> SearchReplaysAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default) =>
            new InMemoryLocalLibrarySource([], Runs).SearchReplaysAsync(query, cancellationToken);
        public ValueTask<LocalLibraryPage<LocalBeatmapSet>> SearchBeatmapSetsAsync(LocalLibraryQuery query, CancellationToken cancellationToken = default) =>
            new InMemoryLocalLibrarySource([], Runs).SearchBeatmapSetsAsync(query, cancellationToken);
    }
    private sealed class HistoryService : IAccountScoreHistoryService
    {
        public bool Fail { get; set; }
        public Task<OnlineAccountScoreHistoryResult> FetchAccountAsync(CancellationToken cancellationToken = default)
        {
            var coverage = new OnlineScoreCoverage(Fail ? OsuBestScoresFetchStatus.NetworkError : OsuBestScoresFetchStatus.Success, false, DateTimeOffset.UtcNow, "recent", 100, false);
            ScoreHistoryEntry[] scores = Fail ? [] : [new("osu:999", 999, 456, 789, null, null, "Submitted stable play", "Artist", "Hard",
                DateTimeOffset.UtcNow.AddMinutes(-2), 4, .96, 100, 234567, 100, 1, [], ScoreHistoryProvenance.OnlinePublic, false, true, LegacyScore: true)];
            return Task.FromResult(new OnlineAccountScoreHistoryResult(null, scores, coverage, coverage));
        }
        public Task<OnlineBeatmapScoreHistoryResult> FetchBeatmapAsync(int beatmapId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

using System.Reflection;
using AimMod.Desktop.Practice;
using AimMod.Desktop.Visuals;
using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime;
using NUnit.Framework;

namespace AimMod.Desktop.Tests.Practice;

[TestFixture]
public sealed partial class PracticeOperationTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task BreakdownEntryKeepsSelectedSectionInGenerationRequest(bool nextSection)
    {
        string root = Directory.CreateTempSubdirectory("practice-breakdown-operation-").FullName;
        var library = new PracticeMapLibrary(root);
        try
        {
            var replay = new LocalReplay(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Fixture", "Artist", "Difficulty", "osu", "Player",
                DateTimeOffset.UtcNow, 5, .95, 1_000_000, 500, 1, 100, [], true, new string('a', 64));
            var candidate = new PracticeMapCandidate(replay, [replay.ScoreId], 1, 1, 1);
            var earlier = new PracticeSourceSection(PracticeDrillType.Mixed, 0, 9, 0, 5000, 0, [], []);
            var selected = new PracticeSourceSection(PracticeDrillType.Mixed, 20, 29, 10000, 15000, 0, [], []);
            using var workspace = new BreakdownWorkspace(library, [new(PracticeDrillType.Mixed, earlier), new(PracticeDrillType.Mixed, selected)]);
            if (nextSection) workspace.OpenNextBreakdown(candidate, [new(0, 5000)]);
            else workspace.OpenBreakdown(candidate, 24);
            workspace.Drain();
            invoke(workspace, "create");
            Assert.That(workspace.Request, Is.Not.Null);
            Assert.That(workspace.Request!.CreateBreakdown, Is.True);
            Assert.That(workspace.Request.CreateSet, Is.False);
            Assert.That(workspace.Request.Options!.FirstObjectIndex, Is.EqualTo(20));
        }
        finally
        {
            // Disposal flushes settings asynchronously through the library's IO queue.
            // Remove the fixture only after those writes have released their file handles.
            await library.RunAsync(() => { Directory.Delete(root, true); return true; });
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TerminalStateRejectsQueuedAndLateProgress(bool cancelled)
    {
        string root = Directory.CreateTempSubdirectory("practice-operation-test-").FullName;
        try
        {
            using var workspace = new TestWorkspace(new PracticeMapLibrary(root));
            int ticket = (int)invoke(workspace, "startOperation", "Creating practice map")!;
            bool queuedRan = false, lateRan = false;
            invoke(workspace, "deliver", ticket, (Action)(() => queuedRan = true));
            if (cancelled) invoke(workspace, "cancelOperation");
            else invoke(workspace, "finishOperation", "Practice map saved");
            invoke(workspace, "deliver", ticket, (Action)(() => lateRan = true));
            workspace.Drain();
            Assert.That(queuedRan || lateRan, Is.False);
            var overlay = (AimModLoadingOverlay)typeof(NativePracticeWorkspace)
                .GetField("loadingOverlay", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(workspace)!;
            Assert.That(typeof(AimModLoadingOverlay).GetField("loading", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(overlay), Is.False);
            int next = (int)invoke(workspace, "startOperation", "Opening in osu!")!;
            bool nextRan = false;
            invoke(workspace, "deliver", next, (Action)(() => nextRan = true));
            workspace.Drain();
            Assert.That(nextRan, Is.True);
        }
        finally { Directory.Delete(root, true); }
    }

    private static object? invoke(NativePracticeWorkspace workspace, string name, params object[] args) =>
        typeof(NativePracticeWorkspace).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(workspace, args);

    private partial class TestWorkspace(PracticeMapLibrary library) : NativePracticeWorkspace(
        (_, _) => Task.FromResult<IReadOnlyList<PracticeSectionChoice>>([]),
        (_, _) => Task.FromResult(new PracticeMapGenerationResult(true, "Saved")),
        (_, _) => Task.FromResult(new LazerBeatmapInstallResult(LazerBeatmapInstallStatus.Sent)), library, () => { })
    {
        public void Drain() => Scheduler.Update();
    }

    private sealed partial class BreakdownWorkspace : NativePracticeWorkspace
    {
        private readonly List<PracticeMapGenerationRequest> requests;
        public PracticeMapGenerationRequest? Request => requests.LastOrDefault();
        public BreakdownWorkspace(PracticeMapLibrary library, IReadOnlyList<PracticeSectionChoice> sections)
            : this(library, sections, []) { }
        private BreakdownWorkspace(PracticeMapLibrary library, IReadOnlyList<PracticeSectionChoice> sections, List<PracticeMapGenerationRequest> requests)
            : base((_, _) => Task.FromResult(sections), (request, _) =>
                { requests.Add(request); return Task.FromResult(new PracticeMapGenerationResult(false, "Finished")); },
                (_, _) => Task.FromResult(new LazerBeatmapInstallResult(LazerBeatmapInstallStatus.Sent)), library, () => { }) => this.requests = requests;
        public void Drain() => Scheduler.Update();
    }
}

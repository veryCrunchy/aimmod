using System.Reflection;
using AimMod.Desktop.Practice;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime;
using NUnit.Framework;

namespace AimMod.Desktop.Tests.Practice;

[TestFixture]
public sealed partial class PracticeOperationTests
{
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
}

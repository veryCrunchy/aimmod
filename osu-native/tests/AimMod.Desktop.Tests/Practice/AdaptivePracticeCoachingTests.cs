using System.Reflection;
using AimMod.Desktop.Coaching;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Practice;
using AimMod.Desktop.Visuals;
using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using osu.Game.Graphics.Sprites;

namespace AimMod.Desktop.Tests.Practice;

public class AdaptivePracticeCoachingTests
{
    private const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;

    [Test]
    public void APlaysMapPageLeadsWithAdaptivePracticeAndKeepsPracticeSetsAvailable()
    {
        var run = new LocalReplay(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Practice", "Artist", "Normal",
            "osu", "Practice Player", DateTimeOffset.UtcNow.AddMinutes(-5), 3, .95, 10000, 100, 2, null, [], true, BeatmapHash: "abc");
        string root = Directory.CreateTempSubdirectory("aimmod-adaptive-coaching-").FullName;
        try
        {
            using var view = new NativeCoachingWorkspace(new InMemoryLocalLibrarySource([], [run]),
                new Dictionary<Guid, ReplayAnalysisResult>(), _ => { }) { SynchronousModelBuilds = true };
            (LocalReplay Run, int? First)? started = null;
            view.ConfigureAdaptivePractice(() => new AdaptivePracticeStore(Path.Combine(root, "adaptive.json")), (r, first) => started = (r, first));
            var type = typeof(NativeCoachingWorkspace);
            type.GetMethod("apply", flags)!.Invoke(view, new object[] { new[] { run }, ScoreMods.Choices([run]) });
            type.GetMethod("chooseCoachingRun", flags)!.Invoke(view, new object[] { run.ScoreId });

            var page = (FillFlowContainer<Drawable>)type.GetField("mapDetailHost", flags)!.GetValue(view)!;
            var buttons = page.ChildrenOfType<AimModButton>().ToArray();
            string caption(AimModButton button) => button.ChildrenOfType<OsuSpriteText>().First().Text.ToString();
            var captions = buttons.Select(caption).ToList();

            Assert.That(captions, Does.Contain("Start adaptive practice"));
            Assert.That(captions, Does.Contain("Prepare practice set"), "generated practice sets stay available");
            Assert.That(captions.IndexOf("Start adaptive practice"), Is.LessThan(captions.IndexOf("Prepare practice set")));

            buttons.First(b => caption(b) == "Start adaptive practice").TriggerClick();
            Assert.That(started?.Run, Is.SameAs(run));
            Assert.That(started?.First, Is.Null, "the map page starts from the first section that needs work");
        }
        finally { Directory.Delete(root, true); }
    }
}

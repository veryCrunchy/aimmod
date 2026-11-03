using System.Reflection;
using AimMod.Desktop.Onboarding;
using AimMod.Desktop.Practice;
using AimMod.Desktop.Trainers;
using AimMod.Osu.Runtime;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Input.Handlers.Tablet;
using osu.Game.Graphics.UserInterface;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Osu.Mods;

namespace AimMod.Desktop.Tests;

[TestFixture]
public class TrainerAutopilotTests
{
    [TestCase(TrainerKind.Steady)] [TestCase(TrainerKind.Alternating)] [TestCase(TrainerKind.Bursts)] [TestCase(TrainerKind.Rhythm)]
    public void TimingDrillsAddAutopilotWithoutChangingTheGeneratedDrill(TrainerKind kind)
    {
        var settings = new TrainerSettings(kind, Autopilot: true);
        Assert.That(TrainerAutopilot.GameplayMods(settings).Single(), Is.TypeOf<OsuModAutopilot>());
        Assert.That(TrainerAutopilot.GameplayMods(settings with { Autopilot = false }), Is.Empty);
        // Star targets and pattern generation use the difficulty mods only.
        Assert.That(TrainerReadingPatterns.Mods(settings), Is.Empty);
        Assert.That(TrainerBeatmap.Create(settings).HitObjects.Select(h => (h.StartTime, h.Position)),
            Is.EqualTo(TrainerBeatmap.Create(settings with { Autopilot = false }).HitObjects.Select(h => (h.StartTime, h.Position))));
        Assert.That(settings.ComparisonKey(), Is.Not.EqualTo((settings with { Autopilot = false }).ComparisonKey()));
    }

    [TestCase(TrainerKind.Aim)] [TestCase(TrainerKind.Reading)] [TestCase(TrainerKind.Spinner)] [TestCase(TrainerKind.Reaction)]
    public void AimReadingSpinnerAndReactionNeverUseAutopilot(TrainerKind kind)
    {
        var settings = new TrainerSettings(kind, Autopilot: true);
        Assert.That(TrainerAutopilot.IsActive(settings), Is.False);
        Assert.That(TrainerAutopilot.GameplayMods(settings).OfType<OsuModAutopilot>(), Is.Empty);
        Assert.That(settings.ComparisonKey(), Is.EqualTo((settings with { Autopilot = false }).ComparisonKey()));
    }

    [Test]
    public void AutopilotResultsAreAssistedAndStayOutOfSkillAndProfileData()
    {
        var settings = new TrainerSettings(TrainerKind.Steady, Autopilot: true);
        var map = TrainerBeatmap.Create(settings);
        foreach (var obj in map.HitObjects) obj.ApplyDefaults(map.ControlPointInfo, map.Difficulty);
        var judgements = map.HitObjects.SelectMany(flatten).Select(obj => new JudgementResult(obj, obj.CreateJudgement())
            { Type = obj.CreateJudgement().MaxResult }).ToArray();
        var result = NativeTrainerPlayer.CreateResult(settings, map, judgements, 99);
        Assert.That(result.Assisted, Is.True);
        Assert.That(TrainerPerformance.IsCompleted(result, DateTimeOffset.Now.AddMinutes(1)), Is.False);
        Assert.Throws<ArgumentException>(() => Hub.HubTrainingSession.FromResult(result, false));
        Assert.That(NativeTrainerPlayer.CreateResult(settings with { Autopilot = false }, map, judgements, 99).Assisted, Is.False);
    }

    private static IEnumerable<HitObject> flatten(HitObject obj) => new[] { obj }.Concat(obj.NestedHitObjects.SelectMany(flatten));

    [Test]
    public void ExternalTabletDriverKeepsTheBuiltInHandlerOffInGameplayAndRestoresIt()
    {
        var lazer = new TrainerInputSettings(Tablet: new(true, new(70, 45), new(80, 60), new(1), new(.5f), 0, 0));
        var tablet = new OpenTabletDriverHandler(); tablet.Enabled.Value = false;
        using (lazer.Apply([tablet], builtInTablet: false))
        {
            Assert.That(tablet.Enabled.Value, Is.False);
            Assert.That(tablet.AreaSize.Value, Is.Not.EqualTo(new osuTK.Vector2(70, 45)));
        }
        Assert.That(tablet.Enabled.Value, Is.False);
        using (lazer.Apply([tablet])) Assert.That(tablet.Enabled.Value, Is.True);
    }

    [Test]
    public void ChoosingAnExternalDriverTurnsTheBuiltInHandlerOffAndBackOn()
    {
        var tablet = new OpenTabletDriverHandler(); tablet.Enabled.Value = true;
        AimModGame.UseTabletDriver([tablet], external: false);
        Assert.That(tablet.Enabled.Value, Is.True);
        AimModGame.UseTabletDriver([tablet], external: true);
        Assert.That(tablet.Enabled.Value, Is.False);
        AimModGame.UseTabletDriver([tablet], external: false);
        Assert.That(tablet.Enabled.Value, Is.False, "Startup with the built-in choice leaves the saved handler state alone.");
        AimModGame.UseTabletDriver([tablet], external: false, enableBuiltIn: true);
        Assert.That(tablet.Enabled.Value, Is.True);
    }

    [Test]
    public void TabletDriverChoiceIsSavedFromGeneralSettings()
    {
        string root = Path.Combine(Path.GetTempPath(), "aimmod-tablet-" + Guid.NewGuid().ToString("N"));
        try
        {
            var destination = new OsuBeatmapDestinationService(new LazerBeatmapInstallService(Path.Combine(root, "handoff")),
                new FileOsuClientDestinationPreferenceStore(Path.Combine(root, "client.txt")), Path.Combine(root, "handoff"));
            var startup = new UserSetupStore(Path.Combine(root, "setup.json"));
            bool? applied = null;
            using var view = new OsuClientSettingsScreen(destination, null, null, null, null, null, null,
                new AutomaticPracticeStore(root), startup, externalTabletDriverChanged: external => applied = external);
            var pages = (List<Drawable>)typeof(OsuClientSettingsScreen).GetField("settingsPages", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;
            var general = ((Container)pages[0]).Children.OfType<FillFlowContainer>().Single().Children.OfType<OsuDropdown<string>>().ToArray();
            Assert.That(general.Select(c => c.Current.Value), Is.EquivalentTo(new[] { "Auto", OsuClientSettingsScreen.BuiltInTabletDriver }));
            // The destination menu opens downwards over the tablet driver control.
            Assert.That(general.Single(c => c.Current.Value == "Auto").Depth,
                Is.LessThan(general.Single(c => c.Current.Value == OsuClientSettingsScreen.BuiltInTabletDriver).Depth));
            general.Single(c => c.Current.Value == OsuClientSettingsScreen.BuiltInTabletDriver).Current.Value = OsuClientSettingsScreen.ExternalTabletDriver;
            Assert.That(startup.Load().ExternalTabletDriver, Is.True);
            Assert.That(applied, Is.True);
            Assert.That(startup.Load().StartupSound, Is.True);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

using AimMod.Desktop.Trainers;
using Newtonsoft.Json;
using NUnit.Framework;
using osu.Framework.Input.Handlers.Mouse;
using osu.Framework.Input.Handlers.Tablet;
using osuTK;

namespace AimMod.Desktop.Tests;

public class TrainerGameTests
{
    [TestCase(TrainerKind.Steady)]
    [TestCase(TrainerKind.Alternating)]
    [TestCase(TrainerKind.Bursts)]
    [TestCase(TrainerKind.Rhythm)]
    [TestCase(TrainerKind.Aim)]
    [TestCase(TrainerKind.Reading)]
    public void RealBeatmapHasMatchingAudioTimelineAndPlayableGeometry(TrainerKind kind)
    {
        var settings = new TrainerSettings(kind, 120, 15);
        var map = TrainerBeatmap.Create(settings);
        var timeline = new TrainerSession(settings);
        Assert.That(map.BeatmapInfo.Ruleset.ShortName, Is.EqualTo("osu"));
        Assert.That(map.HitObjects.Select(h => h.StartTime), Is.EqualTo(timeline.Notes.Select(n => n.TimeMs)));
        Assert.That(map.HitObjects.All(h => h.Position.X is >= 50 and <= 462 && h.Position.Y is >= 50 and <= 334), Is.True);
        Assert.That(map.HitObjects.Select(h => h.Position).Distinct().Count(), Is.GreaterThan(3));
        Assert.That(map.BeatmapInfo.OnlineID, Is.LessThanOrEqualTo(0));
    }

    [TestCase(TrainerKind.Steady, 60)]
    [TestCase(TrainerKind.Alternating, 240)]
    [TestCase(TrainerKind.Bursts, 180)]
    [TestCase(TrainerKind.Rhythm, 120)]
    public void MovingTappingPatternsStayReadableAcrossLoops(TrainerKind kind, int bpm)
    {
        var settings = new TrainerSettings(kind, bpm, 60);
        var map = TrainerBeatmap.Create(settings);
        var timeline = new TrainerSession(settings);
        for (int i = 1; i < map.HitObjects.Count; i++)
        {
            double spacing = Vector2.Distance(map.HitObjects[i - 1].Position, map.HitObjects[i].Position);
            bool betweenBursts = kind == TrainerKind.Bursts && timeline.Notes[i].Phrase != timeline.Notes[i - 1].Phrase;
            Assert.That(spacing, betweenBursts ? Is.InRange(60, 116) : Is.InRange(20, 49), $"Spacing at note {i}");
            if (kind == TrainerKind.Bursts) Assert.That(map.HitObjects[i].NewCombo, Is.EqualTo(betweenBursts));
        }
        Assert.That(map.HitObjects.Max(h => h.Position.X) - map.HitObjects.Min(h => h.Position.X), Is.GreaterThan(250));
        Assert.That(map.HitObjects.Max(h => h.Position.Y) - map.HitObjects.Min(h => h.Position.Y), Is.GreaterThan(150));
    }

    [Test]
    public void ImportsActualFrameworkSerializationAndRestoresHandlers()
    {
        var sourceMouse = new MouseHandler(); sourceMouse.Sensitivity.Value = 1.73; sourceMouse.UseRelativeMode.Value = true;
        var sourceTablet = new OpenTabletDriverHandler();
        sourceTablet.Enabled.Value = true;
        sourceTablet.AreaSize.Value = new Vector2(70, 45); sourceTablet.AreaOffset.Value = new(80, 60);
        sourceTablet.OutputAreaSize.Value = new(.8f, .7f); sourceTablet.OutputAreaOffset.Value = new(.4f, .6f);
        sourceTablet.Rotation.Value = 15; sourceTablet.PressureThreshold.Value = .2f;
        string withType(object o) => JsonConvert.SerializeObject(o, new osu.Framework.IO.Serialization.Vector2Converter()).Insert(1, $"\"$type\":\"{o.GetType().FullName}, osu.Framework\",");
        var input = TrainerInputSettings.Lazer("{\"InputHandlers\":[" + withType(sourceMouse) + "," + withType(sourceTablet) + "]}");
        Assert.That(input.Tablet!.AreaSize, Is.EqualTo(new Vector2(70, 45)));
        Assert.That(input.Tablet.OutputSize, Is.EqualTo(new Vector2(.8f, .7f)));
        var mouse = new MouseHandler(); mouse.UseRelativeMode.Value = false;
        var tablet = new OpenTabletDriverHandler(); tablet.Enabled.Value = false;
        using (input.Apply([mouse, tablet]))
        {
            Assert.That(mouse.Sensitivity.Value, Is.EqualTo(1.73)); Assert.That(mouse.UseRelativeMode.Value, Is.True);
            Assert.That(tablet.AreaOffset.Value, Is.EqualTo(new Vector2(80, 60)));
            Assert.That(tablet.Enabled.Value, Is.True); Assert.That(tablet.Rotation.Value, Is.EqualTo(15));
        }
        Assert.That(mouse.Sensitivity.Value, Is.EqualTo(1)); Assert.That(mouse.UseRelativeMode.Value, Is.False);
        Assert.That(tablet.Enabled.Value, Is.False); Assert.That(tablet.AreaSize.Value, Is.EqualTo(Vector2.Zero));
    }

    [Test]
    public void StableExternalTabletDoesNotActivateASecondDriver()
    {
        var settings = TrainerInputSettings.Stable("RawInput = 1\nMouseSensitivity = 1.5");
        Assert.That(settings.Sensitivity, Is.EqualTo(1.5)); Assert.That(settings.RelativeMouse, Is.True);
        var tablet = new OpenTabletDriverHandler(); tablet.Enabled.Value = true;
        using (settings.Apply([tablet])) Assert.That(tablet.Enabled.Value, Is.False);
        Assert.That(tablet.Enabled.Value, Is.True);
    }
}

using AimMod.Desktop.Trainers;
using AimMod.Osu.Runtime.Contracts;
using osu.Framework.Input.Bindings;
using osu.Game.Rulesets.Osu;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public class TrainerOsuSettingsTests
{
    [Test]
    public void StableImportsKeysExactOffsetAndMousePreference()
    {
        var imported = TrainerOsuSettingsReader.Stable("keyOsuLeft = A\nkeyOsuRight = S\nOffset = -37\nMouseDisableButtons = 1\nUsername = Private");
        Assert.That(imported, Is.EqualTo(new TrainerOsuSettings("A / S", -37, false, "osu!stable", new TrainerInputSettings())));
        Assert.DoesNotThrow(() => new TrainerSettings(Keys:imported.Keys, OffsetMs:imported.OffsetMs).Validate());
    }
    [Test]
    public void LazerUsesActionMappingNotRowOrderOrMouseDefaults()
    {
        static ExternalTrainerKeyBinding binding(OsuAction action, InputKey key) => new((int)action, new KeyBinding(key, action).KeyCombination.ToString());
        var imported = TrainerOsuSettingsReader.Lazer(new([
            binding(OsuAction.RightButton, InputKey.MouseRight), binding(OsuAction.Smoke, InputKey.C),
            binding(OsuAction.RightButton, InputKey.F), binding(OsuAction.LeftButton, InputKey.D)]), 23, false);
        Assert.That(imported.Keys, Is.EqualTo("D / F")); Assert.That(imported.OffsetMs, Is.EqualTo(23));
        Assert.That(imported.MouseButtons, Is.False);
        Assert.That(imported.Bindings, Has.Count.EqualTo(4));
        Assert.That(TrainerOsuSettingsReader.LazerMouseButtons("MouseDisableButtons = True"), Is.False);
    }

    [TestCase("OemComma", "Comma")]
    [TestCase("oem5", "BackSlash")]
    [TestCase("LControlKey", "ControlLeft")]
    [TestCase("RShiftKey", "ShiftRight")]
    [TestCase("D4", "Number4")]
    [TestCase("NumPad2", "Keypad2")]
    public void ImportsStableWindowsKeyNames(string stable, string expected)
    {
        var imported = TrainerOsuSettingsReader.Stable($"\uFEFFkeyOsuLeft = {stable}\nkeyOsuRight = X\nMouseDisableButtons = TRUE");
        Assert.That(imported.Keys, Is.EqualTo(expected + " / X"));
        Assert.That(imported.MouseButtons, Is.False);
        Assert.DoesNotThrow(() => new TrainerSettings(Keys: imported.Keys).Validate());
    }

    [Test]
    public void StableUsesActualMouseSpeedSettingBeforeLegacyAlias()
    {
        var imported = TrainerOsuSettingsReader.Stable("\uFEFFMouseSpeed = 1.73\nMouseSensitivity = 2\nRAWINPUT = TRUE");
        Assert.That(imported.Input!.Sensitivity, Is.EqualTo(1.73));
        Assert.That(imported.Input.RelativeMouse, Is.True);
    }
}

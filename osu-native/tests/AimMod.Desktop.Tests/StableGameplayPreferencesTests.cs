using AimMod.Desktop.Trainers;
using NUnit.Framework;
using osu.Framework.Platform;
using osu.Game.Configuration;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class StableGameplayPreferencesTests
{
    [Test]
    public void AppliesStableAppearanceAndRestoresAimModPreferences()
    {
        string root = Directory.CreateTempSubdirectory("aimmod-stable-appearance-").FullName;
        try
        {
            using var config = new OsuConfigManager(new NativeStorage(root));
            config.SetValue(OsuSetting.GameplayCursorSize, 1f);
            config.SetValue(OsuSetting.AutoCursorSize, false);
            config.SetValue(OsuSetting.BeatmapSkins, true);
            config.SetValue(OsuSetting.BeatmapHitsounds, true);
            using (TrainerGameplayPreferences.Stable("CursorSize = 1.6\nAutomaticCursorSizing = 1\nIgnoreBeatmapSkins = 1\nIgnoreBeatmapSamples = TRUE").Apply(config))
            {
                Assert.That(config.Get<float>(OsuSetting.GameplayCursorSize), Is.EqualTo(1.6f));
                Assert.That(config.Get<bool>(OsuSetting.AutoCursorSize), Is.True);
                Assert.That(config.Get<bool>(OsuSetting.BeatmapSkins), Is.False);
                Assert.That(config.Get<bool>(OsuSetting.BeatmapHitsounds), Is.False);
            }
            Assert.That(config.Get<float>(OsuSetting.GameplayCursorSize), Is.EqualTo(1f));
            Assert.That(config.Get<bool>(OsuSetting.AutoCursorSize), Is.False);
            Assert.That(config.Get<bool>(OsuSetting.BeatmapSkins), Is.True);
            Assert.That(config.Get<bool>(OsuSetting.BeatmapHitsounds), Is.True);
        }
        finally { Directory.Delete(root, true); }
    }
}

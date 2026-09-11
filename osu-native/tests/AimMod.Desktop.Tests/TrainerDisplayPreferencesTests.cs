using AimMod.Desktop.Trainers;
using NUnit.Framework;
using osu.Framework.Configuration;
using osu.Framework.Platform;
using osu.Game.Configuration;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Configuration;
using System.Drawing;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class TrainerDisplayPreferencesTests
{
    [TestCase(WindowMode.Windowed)]
    [TestCase(WindowMode.Borderless)]
    [TestCase(WindowMode.Fullscreen)]
    public void LazerDisplayAndVisualSettingsRestoreAfterPractice(WindowMode mode)
    {
        withConfig((framework, game) =>
        {
            framework.SetValue(FrameworkSetting.WindowedSize, new Size(900, 700));
            var previousMode = framework.Get<WindowMode>(FrameworkSetting.WindowMode);
            var previousFullscreen = framework.Get<Size>(FrameworkSetting.SizeFullscreen);
            var previousDim = game.Get<double>(OsuSetting.DimLevel);
            using (new TrainerGameplayPreferences("DimLevel=0.88\nBlurLevel=0.2\nScaling=Everything\nScalingSizeX=0.75\nScalingSizeY=0.8\nScalingPositionX=0.25\nUIScale=1.1\nShowStoryboard=False\nPreferNoVideo=True\nKeyOverlay=True").Apply(game))
            using (new TrainerDisplayPreferences($"WindowMode={mode}\nWindowedSize=1280x720\nSizeFullscreen=1600x900\nWindowedPositionX=0.2\nWindowedPositionY=0.8\nFrameSync=VSync").Apply(framework, game))
            {
                Assert.That(framework.Get<WindowMode>(FrameworkSetting.WindowMode), Is.EqualTo(mode));
                Assert.That(framework.Get<Size>(FrameworkSetting.WindowedSize), Is.EqualTo(new Size(1280, 720)));
                Assert.That(framework.Get<Size>(FrameworkSetting.SizeFullscreen), Is.EqualTo(new Size(1600, 900)));
                Assert.That(framework.Get<double>(FrameworkSetting.WindowedPositionX), Is.EqualTo(.2));
                Assert.That(game.Get<double>(OsuSetting.DimLevel), Is.EqualTo(.88));
                Assert.That(game.Get<double>(OsuSetting.BlurLevel), Is.EqualTo(.2));
                Assert.That(game.Get<ScalingMode>(OsuSetting.Scaling), Is.EqualTo(ScalingMode.Everything));
                Assert.That(game.Get<float>(OsuSetting.ScalingSizeX), Is.EqualTo(.75f));
                Assert.That(game.Get<bool>(OsuSetting.ShowStoryboard), Is.False);
                Assert.That(game.Get<bool>(OsuSetting.KeyOverlay), Is.True);
            }
            Assert.That(framework.Get<WindowMode>(FrameworkSetting.WindowMode), Is.EqualTo(previousMode));
            Assert.That(framework.Get<Size>(FrameworkSetting.WindowedSize), Is.EqualTo(new Size(900, 700)));
            Assert.That(framework.Get<Size>(FrameworkSetting.SizeFullscreen), Is.EqualTo(previousFullscreen));
            Assert.That(game.Get<double>(OsuSetting.DimLevel), Is.EqualTo(previousDim));
            Assert.That(game.Get<ScalingMode>(OsuSetting.Scaling), Is.EqualTo(ScalingMode.Off));
        });
    }

    [Test]
    public void StableDimAndLetterboxPreservePhysicalPlayArea()
    {
        const string contents = "Fullscreen=1\nLetterboxing=1\nWidthFullscreen=1280\nHeightFullscreen=720\nLetterboxPositionX=-100\nLetterboxPositionY=100\nDimLevel=85\nIHateHavingFun=1\nVideo=0\nShowStoryboard=0";
        withConfig((framework, game) =>
        {
            using var appearance = TrainerGameplayPreferences.Stable(contents).Apply(game);
            using var display = new TrainerDisplayPreferences(contents, stable: true).Apply(framework, game);
            Assert.That(framework.Get<WindowMode>(FrameworkSetting.WindowMode), Is.EqualTo(WindowMode.Borderless));
            Assert.That(game.Get<double>(OsuSetting.DimLevel), Is.EqualTo(.85));
            Assert.That(game.Get<bool>(OsuSetting.LightenDuringBreaks), Is.False);
            Assert.That(game.Get<bool>(OsuSetting.PreferNoVideo), Is.True);
            Assert.That(game.Get<ScalingMode>(OsuSetting.Scaling), Is.EqualTo(ScalingMode.Everything));
            Assert.That(game.Get<float>(OsuSetting.ScalingSizeX), Is.EqualTo(2f / 3).Within(.001));
            Assert.That(game.Get<float>(OsuSetting.ScalingPositionX), Is.Zero);
            Assert.That(game.Get<float>(OsuSetting.ScalingPositionY), Is.EqualTo(1));
        });
    }

    [Test]
    public void StableWindowedAndExclusiveFullscreenAreDistinct()
    {
        var desktop = new Size(1920, 1080);
        Assert.That(new TrainerDisplayPreferences("Fullscreen=0\nWidth=1280\nHeight=720", true).Mode(desktop), Is.EqualTo(WindowMode.Windowed));
        Assert.That(new TrainerDisplayPreferences("Fullscreen=0\nWidth=1920\nHeight=1080", true).Mode(desktop), Is.EqualTo(WindowMode.Borderless));
        Assert.That(new TrainerDisplayPreferences("Fullscreen=1\nLetterboxing=0", true).Mode(desktop), Is.EqualTo(WindowMode.Fullscreen));
    }

    [Test]
    public void InvalidAndMissingPreferencesLeaveDefaultsAlone()
    {
        withConfig((framework, game) =>
        {
            var size = framework.Get<Size>(FrameworkSetting.WindowedSize);
            var dim = game.Get<double>(OsuSetting.DimLevel);
            using var visual = new TrainerGameplayPreferences("DimLevel=NaN\nBlurLevel=Infinity\nScaling=12345\nKeyOverlay=perhaps").Apply(game);
            using var display = new TrainerDisplayPreferences("WindowedSize=-1x0\nSizeFullscreen=999999x999999\nWindowMode=555\nWindowedPositionX=NaN").Apply(framework, game);
            Assert.That(framework.Get<Size>(FrameworkSetting.WindowedSize), Is.EqualTo(size));
            Assert.That(framework.Get<WindowMode>(FrameworkSetting.WindowMode), Is.EqualTo(WindowMode.Windowed));
            Assert.That(game.Get<double>(OsuSetting.DimLevel), Is.EqualTo(dim));
            Assert.That(game.Get<ScalingMode>(OsuSetting.Scaling), Is.EqualTo(ScalingMode.Off));
        });
    }

    [Test]
    public void SliderAnimationPreferencesRestoreOnExit()
    {
        using var config = new OsuRulesetConfigManager(null, new OsuRuleset().RulesetInfo);
        using (TrainerRulesetPreferences.Stable("SnakingSliders=0\nCursorRipple=1").Apply(config))
        {
            Assert.That(config.Get<bool>(OsuRulesetSetting.SnakingInSliders), Is.False);
            Assert.That(config.Get<bool>(OsuRulesetSetting.SnakingOutSliders), Is.False);
            Assert.That(config.Get<bool>(OsuRulesetSetting.ShowCursorRipples), Is.True);
        }
        Assert.That(config.Get<bool>(OsuRulesetSetting.SnakingInSliders), Is.True);
        Assert.That(config.Get<bool>(OsuRulesetSetting.ShowCursorRipples), Is.False);
    }

    private static void withConfig(Action<FrameworkConfigManager, OsuConfigManager> check)
    {
        string root = Directory.CreateTempSubdirectory("aimmod-practice-display-").FullName;
        try
        {
            using var framework = new FrameworkConfigManager(new NativeStorage(root));
            using var game = new OsuConfigManager(new NativeStorage(root));
            check(framework, game);
        }
        finally { Directory.Delete(root, true); }
    }
}

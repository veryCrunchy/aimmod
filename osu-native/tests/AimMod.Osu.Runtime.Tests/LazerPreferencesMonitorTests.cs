using NUnit.Framework;

namespace AimMod.Osu.Runtime.Tests;

[TestFixture]
public sealed class LazerPreferencesMonitorTests
{
    private string temporaryDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"aimmod-preferences-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(temporaryDirectory))
            Directory.Delete(temporaryDirectory, true);
    }

    [Test]
    public async Task ReadsOnlyPlaybackAndPresentationPreferences()
    {
        Guid skinId = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "game.ini"), $"""
            Username = private-user
            Token = must-not-be-read
            Skin = {skinId}
            AudioOffset = 21.0
            BeatmapSkins = False
            BeatmapColours = True
            BeatmapHitsounds = False
            PositionalHitsoundsLevel = 0.2
            """);
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "framework.ini"), """
            VolumeUniversal = 0.43
            VolumeMusic = 0.6
            VolumeEffect = 0.61
            """);

        await using LazerPreferencesMonitor monitor = await LazerPreferencesMonitor.CreateAsync(temporaryDirectory);
        LazerPreferencesState state = monitor.Current;

        Assert.Multiple(() =>
        {
            Assert.That(state.SkinId, Is.EqualTo(skinId));
            Assert.That(state.AudioOffset, Is.EqualTo(21));
            Assert.That(state.BeatmapSkins, Is.False);
            Assert.That(state.BeatmapColours, Is.True);
            Assert.That(state.BeatmapHitsounds, Is.False);
            Assert.That(state.PositionalHitsoundsLevel, Is.EqualTo(0.2f).Within(0.001f));
            Assert.That(state.VolumeUniversal, Is.EqualTo(0.43));
            Assert.That(state.VolumeMusic, Is.EqualTo(0.6));
            Assert.That(state.VolumeEffect, Is.EqualTo(0.61));
            Assert.That(state.Revision, Is.EqualTo(1));
            Assert.That(state.ToString(), Does.Not.Contain("private-user"));
            Assert.That(state.ToString(), Does.Not.Contain("must-not-be-read"));
        });
    }

    [Test]
    public async Task RejectsOutOfRangeValuesWithoutRejectingValidOnes()
    {
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "game.ini"), """
            AudioOffset = 900
            BeatmapSkins = True
            PositionalHitsoundsLevel = NaN
            """);
        await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "framework.ini"), """
            VolumeUniversal = -1
            VolumeMusic = 0.75
            VolumeEffect = Infinity
            """);

        await using LazerPreferencesMonitor monitor = await LazerPreferencesMonitor.CreateAsync(temporaryDirectory);

        Assert.Multiple(() =>
        {
            Assert.That(monitor.Current.AudioOffset, Is.Null);
            Assert.That(monitor.Current.BeatmapSkins, Is.True);
            Assert.That(monitor.Current.PositionalHitsoundsLevel, Is.Null);
            Assert.That(monitor.Current.VolumeUniversal, Is.Null);
            Assert.That(monitor.Current.VolumeMusic, Is.EqualTo(0.75));
            Assert.That(monitor.Current.VolumeEffect, Is.Null);
        });
    }

    [Test]
    public async Task RefreshPublishesOnlyWhenAllowlistedValuesChange()
    {
        string gameIniPath = Path.Combine(temporaryDirectory, "game.ini");
        await File.WriteAllTextAsync(gameIniPath, "BeatmapColours = True\nUsername = first\n");
        await using LazerPreferencesMonitor monitor = await LazerPreferencesMonitor.CreateAsync(temporaryDirectory);
        int changes = 0;
        monitor.StateChanged += _ => changes++;

        await File.WriteAllTextAsync(gameIniPath, "BeatmapColours = True\nUsername = second\n");
        await monitor.RefreshAsync();
        Assert.That(changes, Is.EqualTo(0));

        await File.WriteAllTextAsync(gameIniPath, "BeatmapColours = False\nUsername = second\n");
        await monitor.RefreshAsync();

        Assert.Multiple(() =>
        {
            Assert.That(changes, Is.EqualTo(1));
            Assert.That(monitor.Current.BeatmapColours, Is.False);
            Assert.That(monitor.Current.Revision, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task MissingFilesStayUnavailable()
    {
        await using LazerPreferencesMonitor monitor = await LazerPreferencesMonitor.CreateAsync(temporaryDirectory);

        Assert.Multiple(() =>
        {
            Assert.That(monitor.Current, Is.EqualTo(LazerPreferencesState.Unavailable));
            Assert.That(monitor.Current.HasValues, Is.False);
        });
    }
}

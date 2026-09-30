using AimMod.Desktop.Creator;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class CreatorSettingsTests
{
    [Test]
    public void CreatorToolsAreOffByDefaultAndExplicitPreferenceSurvivesRestart()
    {
        string directory = Path.Combine(Path.GetTempPath(), "aimmod-creator-settings-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            Assert.That(new CreatorSettingsStore(path).Load(), Is.False);
            new CreatorSettingsStore(path).Save(true);
            Assert.That(new CreatorSettingsStore(path).Load(), Is.True);
            new CreatorSettingsStore(path).Save(false);
            Assert.That(new CreatorSettingsStore(path).Load(), Is.False);
            File.WriteAllText(path, "broken");
            Assert.That(new CreatorSettingsStore(path).Load(), Is.False);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestCase(42, "7", true)]
    [TestCase(43, "7", false)]
    [TestCase(42, "8", false)]
    [TestCase(null, "7", false)]
    [TestCase(42, null, false)]
    public void AutomaticImportRequiresBothLinkedAccountIds(int? osuId, string? twitchId, bool linked)
    {
        var own = new FootageChannel("PracticePlayer", "viewer", 42, "7");
        var library = FootageLibrary.Empty with { Channels = [new("AnotherPlayer", "other"), own] };
        Assert.That(FootageIndex.FindOwnChannel(library, osuId, twitchId), linked ? Is.EqualTo(own) : Is.Null);
    }
}

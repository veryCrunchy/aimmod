using AimMod.Desktop.Creator;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.ScoreHistory;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class CreatorTestInstanceTests
{
    [Test]
    public void PublicScoreRetainsDateAndModernIdentityWithoutClaimingLocalReplay()
    {
        var date = DateTimeOffset.Parse("2026-08-01T14:15:16Z");
        var entry = new ScoreHistoryEntry("osu:456", 456, 11, 12, null, null, "Song", "Artist", "Hard", date,
            4, 0.98, 123, 900000, 600, 1, ["HD"], ScoreHistoryProvenance.OnlinePublic, true, true, LegacyScore: true);
        var score = CreatorTestInstance.ToReplay(entry, "PracticePlayer");
        Assert.Multiple(() =>
        {
            Assert.That(score.PlayedAt, Is.EqualTo(date));
            Assert.That(score.OnlineScoreId, Is.EqualTo(456));
            Assert.That(score.Origin, Is.EqualTo(LocalLibraryOrigin.Online));
            Assert.That(score.LegacyScore, Is.False);
            Assert.That(score.HasReplayFile, Is.False);
            Assert.That(score.IsLocallyStored, Is.False);
            Assert.That(score.ReplayPath, Is.Empty);
            Assert.That(FootageIndex.ScoreKey(score), Is.EqualTo("osu:solo:osu:456"));
            Assert.That(NativeFootageWorkspace.ScoreUri(score)!.AbsoluteUri, Is.EqualTo("https://osu.ppy.sh/scores/456"));
            Assert.That(NativeFootageWorkspace.ScoreUri(score with { LegacyScore = true })!.AbsoluteUri, Is.EqualTo("https://osu.ppy.sh/scores/osu/456"));
            Assert.That(NativeFootageWorkspace.ScoreUri(score with { OnlineScoreId = 0 }), Is.Null);
            Assert.That(CreatorTestInstance.ToReplay(entry, "PracticePlayer").ScoreId, Is.EqualTo(score.ScoreId));
        });
    }

    [Test]
    public void CreatorLaunchDoesNotRegisterOrUpdateNormalInstallation()
        => Assert.That(Program.ShouldRunVelopackBootstrap(["--creator-test", "instance.json"]), Is.False);
}

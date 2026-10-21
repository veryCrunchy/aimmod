using AimMod.Desktop.PpTargets;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

public sealed class PpTargetSkillsetTests
{
    private static readonly PpPatternFeatures streamMap = new() { PointCount = 800, StreamFraction = .42, BurstFraction = .1, JumpFraction = .12, SharpTurnFraction = .1 };
    private static readonly PpPatternFeatures jumpMap = new() { PointCount = 600, StreamFraction = .02, BurstFraction = .05, JumpFraction = .66, SharpTurnFraction = .45 };

    [Test]
    public void MapsMatchWhenEnoughOfThemIsThePattern()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PpTargetSkillsets.Matches(streamMap, PpTargetSkillset.Streams), Is.True);
            Assert.That(PpTargetSkillsets.Matches(jumpMap, PpTargetSkillset.Streams), Is.False);
            Assert.That(PpTargetSkillsets.Matches(jumpMap, PpTargetSkillset.Jumps), Is.True);
            Assert.That(PpTargetSkillsets.Matches(jumpMap, PpTargetSkillset.SharpAngles), Is.True);
            Assert.That(PpTargetSkillsets.Matches(streamMap, PpTargetSkillset.Bursts), Is.False);
            Assert.That(PpTargetSkillsets.Matches(null, PpTargetSkillset.Any), Is.True, "Any keeps maps that are not measured yet");
            Assert.That(PpTargetSkillsets.Matches(null, PpTargetSkillset.Streams), Is.False, "an unmeasured map is never called a stream map");
            Assert.That(PpTargetSkillsets.Matches(new PpPatternFeatures { PointCount = 500 }, PpTargetSkillset.Streams), Is.False,
                "without a circle size streams cannot be measured");
        });
    }

    [Test]
    public void RowsShowTheChosenShareOrTheMostProminentPattern()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PpTargetSkillsets.Tag(streamMap, PpTargetSkillset.Streams), Is.EqualTo("42% streams"));
            Assert.That(PpTargetSkillsets.Tag(jumpMap, PpTargetSkillset.Streams), Is.EqualTo("2% streams"));
            Assert.That(PpTargetSkillsets.Tag(streamMap, PpTargetSkillset.Any), Is.EqualTo("42% streams"));
            // 66% jumps is 1.65× its threshold, 45% sharp angles 1.5×.
            Assert.That(PpTargetSkillsets.Tag(jumpMap, PpTargetSkillset.Any), Is.EqualTo("66% jumps"));
            Assert.That(PpTargetSkillsets.Tag(new PpPatternFeatures { PointCount = 400, StreamFraction = .05, JumpFraction = .1 }, PpTargetSkillset.Any), Is.Null);
            Assert.That(PpTargetSkillsets.Label(PpTargetSkillset.SharpAngles), Is.EqualTo("Sharp angles"));
        });
    }
}

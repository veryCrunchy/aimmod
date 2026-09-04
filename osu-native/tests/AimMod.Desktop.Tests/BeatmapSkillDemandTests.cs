using AimMod.Desktop.LocalLibrary;
using NUnit.Framework;

namespace AimMod.Desktop.Tests;

[TestFixture]
public class BeatmapSkillDemandTests
{
    [Test]
    public void HardFastMapProducesHigherAimAndSpeedDemand()
    {
        BeatmapSkillDemand easier = BeatmapSkillDemand.From(difficulty(3.2, 140, 90_000, 4, 8, 7));
        BeatmapSkillDemand harder = BeatmapSkillDemand.From(difficulty(7.1, 230, 90_000, 5.5f, 9.8f, 9.2f));

        Assert.Multiple(() =>
        {
            Assert.That(harder.Aim, Is.GreaterThan(easier.Aim));
            Assert.That(harder.Speed, Is.GreaterThan(easier.Speed));
            Assert.That(harder.Reading, Is.GreaterThan(easier.Reading));
            Assert.That(harder.Precision, Is.GreaterThan(easier.Precision));
        });
    }

    [Test]
    public void LongerMapProducesHigherStaminaDemand()
    {
        BeatmapSkillDemand shortMap = BeatmapSkillDemand.From(difficulty(5.5, 190, 60_000, 4, 9, 8.5f));
        BeatmapSkillDemand longMap = BeatmapSkillDemand.From(difficulty(5.5, 190, 360_000, 4, 9, 8.5f));

        Assert.That(longMap.Stamina, Is.GreaterThan(shortMap.Stamina));
    }

    [Test]
    public void ValuesStayWithinRadarRange()
    {
        BeatmapSkillDemand demand = BeatmapSkillDemand.From(difficulty(50, 2_000, 9_000_000, 20, 20, 20));

        Assert.That(new[] { demand.Aim, demand.Speed, demand.Stamina, demand.Reading, demand.Precision },
            Has.All.InRange(0, 1));
    }

    private static LocalBeatmapDifficulty difficulty(double stars, double bpm, double length, float cs, float ar, float od) =>
        new(Guid.NewGuid(), 1, "Test", "osu", stars, bpm, length, cs, ar, od, 6, 0);
}

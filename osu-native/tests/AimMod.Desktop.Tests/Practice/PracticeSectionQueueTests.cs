using AimMod.Desktop.Practice;
using NUnit.Framework;

namespace AimMod.Desktop.Tests.Practice;

public class PracticeSectionQueueTests
{
    private static PracticeSectionChoice choice(int first, double start, double end, PracticeDrillType type = PracticeDrillType.Mixed)
        => new(type, new(type, first, first + 9, start, end, 0, [], []));

    [Test]
    public void NextChoicesExcludePreparedSectionsAndDuplicatePatternViews()
    {
        var next = choice(20, 20000, 26000);
        var remaining = PracticeSectionQueue.Remaining([
            choice(40, 40000, 46000), choice(0, 10000, 16000), next,
            choice(20, 20000, 26000, PracticeDrillType.LongJumps), choice(1, 11000, 17000)
        ], [new(10000, 16000)]);
        Assert.That(remaining.Select(s => s.Section.FirstObjectIndex), Is.EqualTo(new[] { 20, 40 }));
        Assert.That(remaining.First(), Is.SameAs(next));
    }

    [Test]
    public void ACompletedQueueStaysEmptyAndAdjacentSectionsRemainAvailable()
    {
        Assert.That(PracticeSectionQueue.Remaining([choice(0, 10000, 16000)], [new(10000, 16000)]), Is.Empty);
        Assert.That(PracticeSectionQueue.Remaining([choice(10, 16000, 22000)], [new(10000, 16000)]), Has.Count.EqualTo(1));
    }
}

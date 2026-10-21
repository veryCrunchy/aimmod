using AimMod.Desktop.Practice;
using NUnit.Framework;

namespace AimMod.Desktop.Tests.Practice;

/// <summary>Guardrails on the drill loop: parking, spaced reviews, a chosen order and a lead-in on the bar.</summary>
public class AdaptivePracticeLoopTests
{
    private static readonly DateTimeOffset now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static readonly AdaptiveSection[] sections = Enumerable.Range(0, 4)
        .Select(i => new AdaptiveSection(i, 1_000 + i * 10_000, 10_000 + i * 10_000, i * 20, 20)).ToArray();

    private static AdaptivePracticeProgress progress() => AdaptivePracticeCoach.Create("map", "Title", "Hard", 1, sections.Length, now);

    /// <summary>Fails a section at alternating places, so no spot drill forms.</summary>
    private static AdaptivePracticeProgress failTimes(AdaptivePracticeProgress p, int section, int times)
    {
        for (int i = 0; i < times; i++)
            p = AdaptivePracticeCoach.Fail(p, sections, section, sections[section].StartMs + (i % 2 == 0 ? 1_000 : 6_000), now);
        return p;
    }

    private static AdaptivePracticeProgress solid(AdaptivePracticeProgress p, params int[] which)
    {
        foreach (int i in which) p = AdaptivePracticeCoach.Skip(p, new AdaptiveDrill(AdaptiveDrillKind.Section, i, i, 0, 0, 1), now);
        return p;
    }

    private static AdaptiveResult clean() => new(0, 20, 0, 1, [], 20);
    private static AdaptiveResult shaky() => new(1, 20, 0, .95, [5_000], 20);

    [Test]
    public void ASectionIsParkedAfterTheLoopCapAndComesBackLast()
    {
        var p = failTimes(progress(), 0, AdaptivePracticeCoach.LoopCap - 1);
        Assert.That(AdaptivePracticeCoach.Next(p, sections).Focus, Is.Zero, "below the cap the section keeps its turn");

        p = failTimes(p, 0, 1);
        Assert.Multiple(() =>
        {
            Assert.That(p.Sections[0].Parked);
            Assert.That(AdaptivePracticeCoach.Next(p, sections).Focus, Is.EqualTo(1), "the next section comes first");
        });

        p = solid(p, 1, 2, 3);
        Assert.That(AdaptivePracticeCoach.Next(p, sections).Focus, Is.Zero, "once the rest is done the parked section returns");
        p = failTimes(p, 0, 1);
        Assert.Multiple(() =>
        {
            Assert.That(p.Sections[0].Parked, Is.False);
            Assert.That(p.Sections[0].Loops, Is.EqualTo(1), "it starts a fresh count");
        });
    }

    [Test]
    public void TheLastSectionLeftIsNeverParked()
    {
        var p = failTimes(solid(progress(), 1, 2, 3), 0, AdaptivePracticeCoach.LoopCap * 2);
        Assert.That(p.Sections[0].Parked, Is.False);
    }

    [Test]
    public void ANewSessionClearsParking()
    {
        var p = failTimes(progress(), 0, AdaptivePracticeCoach.LoopCap);
        var resumed = AdaptivePracticeCoach.Resume(p, "map", "Title", "Hard", 1, sections.Length, now.AddHours(1));
        Assert.Multiple(() =>
        {
            Assert.That(resumed.Sections[0].Parked, Is.False);
            Assert.That(AdaptivePracticeCoach.Next(resumed, sections).Focus, Is.Zero);
        });
    }

    [Test]
    public void ASectionThatTookWorkIsReviewedOnASpacedSchedule()
    {
        var p = failTimes(progress(), 0, 2);
        for (int i = 0; i < 40 && !p.Sections[0].ClearedAtFullSpeed; i++)
            p = AdaptivePracticeCoach.Complete(p, sections, AdaptivePracticeCoach.Next(p, sections), clean(), now);
        p = solid(p, 1, 2, 3) with { ConnectTo = null };
        Assert.That(p.Sections[0].ReviewDays, Is.EqualTo(AdaptivePracticeCoach.FirstReviewDays));

        var sameDay = AdaptivePracticeCoach.Resume(p, "map", "Title", "Hard", 1, sections.Length, now.AddHours(6));
        Assert.That(AdaptivePracticeCoach.Next(sameDay, sections).Kind, Is.EqualTo(AdaptiveDrillKind.FullRun), "not due yet");

        var later = AdaptivePracticeCoach.Resume(p, "map", "Title", "Hard", 1, sections.Length, now.AddDays(1));
        var review = AdaptivePracticeCoach.Next(later, sections);
        Assert.That(review, Is.EqualTo(new AdaptiveDrill(AdaptiveDrillKind.Review, 0, 0, sections[0].StartMs, sections[0].EndMs, 1)));

        var held = AdaptivePracticeCoach.Complete(later, sections, review, clean(), now.AddDays(1));
        Assert.Multiple(() =>
        {
            Assert.That(held.Sections[0].ReviewDays, Is.EqualTo(2), "a clean review doubles the wait");
            Assert.That(held.Sections[0].ClearedAtFullSpeed);
            Assert.That(AdaptivePracticeCoach.Next(held, sections).Kind, Is.EqualTo(AdaptiveDrillKind.FullRun));
        });

        var wobbly = AdaptivePracticeCoach.Complete(later, sections, review, shaky(), now.AddDays(1));
        Assert.That(wobbly.Sections[0].ReviewDays, Is.EqualTo(AdaptivePracticeCoach.FirstReviewDays), "a shaky review comes back soon");

        var lost = AdaptivePracticeCoach.Complete(later, sections, review, new AdaptiveResult(6, 20, 0, .5, [3_000], 20), now.AddDays(1));
        Assert.That(lost.Sections[0].ClearedAtFullSpeed, Is.False, "a struggled review sends the section back to drills");
    }

    [Test]
    public void AnEasySectionIsNotReviewed()
    {
        var p = AdaptivePracticeCoach.Complete(progress(), sections, AdaptivePracticeCoach.Next(progress(), sections), clean(), now);
        var later = AdaptivePracticeCoach.Resume(p, "map", "Title", "Hard", 1, sections.Length, now.AddDays(30));
        Assert.Multiple(() =>
        {
            Assert.That(p.Sections[0].ClearedAtFullSpeed);
            Assert.That(later.Sections[0].Review, Is.False);
        });
    }

    [Test]
    public void FocusSectionsComeFirstThenTheMapInOrder()
    {
        var p = progress() with { Focus = [2, 0] };
        Assert.That(AdaptivePracticeCoach.Next(p, sections).Focus, Is.EqualTo(2));
        p = solid(p, 2);
        Assert.That(AdaptivePracticeCoach.Next(p, sections).Focus, Is.Zero);
        p = solid(p, 0);
        Assert.That(AdaptivePracticeCoach.Next(p, sections).Focus, Is.EqualTo(1));
    }

    [Test]
    public void TheLeadInStartsOnABarLine()
    {
        AdaptiveTiming[] common = [new(0, 500, 4)];
        AdaptiveTiming[] slow = [new(0, 3_000, 4)];
        Assert.Multiple(() =>
        {
            Assert.That(AdaptiveAttemptPlan.LeadIn(10_300, common), Is.EqualTo(8_000), "the last bar at least two seconds before");
            Assert.That(AdaptiveAttemptPlan.LeadIn(10_000, common), Is.EqualTo(8_000));
            Assert.That(AdaptiveAttemptPlan.LeadIn(10_000, slow), Is.EqualTo(6_000), "very slow bars snap to a beat");
            Assert.That(AdaptiveAttemptPlan.LeadIn(10_000, []), Is.EqualTo(7_000), "no timing keeps three seconds");
            Assert.That(AdaptiveAttemptPlan.LeadIn(10_300, [new(9_000, 400, 4), new(0, 500, 4)]), Is.EqualTo(8_000),
                "the timing point in effect at the lead-in is used");
        });
    }
}

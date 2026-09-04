using AimMod.Desktop.Visuals;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Game.Graphics;
using osu.Game.Rulesets.Scoring;

namespace AimMod.Desktop.Tests;

[TestFixture]
public sealed class ReplayJudgementTimelineTests
{
    private static readonly OsuColour osuColours = new();

    [TestCase(ReplayTimelineTone.Great, HitResult.Great)]
    [TestCase(ReplayTimelineTone.Ok, HitResult.Ok)]
    [TestCase(ReplayTimelineTone.Meh, HitResult.Meh)]
    [TestCase(ReplayTimelineTone.Miss, HitResult.Miss)]
    [TestCase(ReplayTimelineTone.SliderBreak, HitResult.Miss)]
    public void UsesOsuJudgementColours(ReplayTimelineTone tone, HitResult hitResult)
    {
        Colour4 actual = ReplayJudgementTimeline.ColourFor(tone);
        Colour4 expected = osuColours.ForHitResult(hitResult);

        Assert.Multiple(() =>
        {
            Assert.That(actual.R, Is.EqualTo(expected.R).Within(0.000001f));
            Assert.That(actual.G, Is.EqualTo(expected.G).Within(0.000001f));
            Assert.That(actual.B, Is.EqualTo(expected.B).Within(0.000001f));
            Assert.That(actual.A, Is.EqualTo(expected.A).Within(0.000001f));
        });
    }
}

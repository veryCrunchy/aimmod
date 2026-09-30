using AimMod.Osu.Runtime.Contracts;
using NUnit.Framework;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Replays;
using osuTK;

namespace AimMod.Osu.Worker.Tests;

public sealed class ReplayMissAnalyzerTests
{
    private static readonly Vector2 target = new(100, 100);

    [Test]
    public void FindsAnEarlyClickBeforeTheCursorReachesTheTarget()
    {
        ReplayMissAnalysis result = analyse(
            frame(-150, 0, 100),
            frame(-100, 20, 100, pressed: true),
            frame(-80, 35, 100),
            frame(0, 100, 100),
            frame(150, 140, 100));

        Assert.Multiple(() =>
        {
            Assert.That(result.Reason, Is.EqualTo(ReplayMissReason.EarlyClick));
            Assert.That(result.PressTimeOffsetMs, Is.EqualTo(-100).Within(0.01));
            Assert.That(result.EnteredTargetAfterObject || result.ClosestTimeOffsetMs < 0, Is.True);
        });
    }

    [Test]
    public void FindsALateClickAfterLeavingTheTarget()
    {
        ReplayMissAnalysis result = analyse(
            frame(-150, 30, 100),
            frame(-40, 100, 100),
            frame(0, 115, 100),
            frame(80, 180, 100, pressed: true),
            frame(150, 220, 100));

        Assert.Multiple(() =>
        {
            Assert.That(result.Reason, Is.EqualTo(ReplayMissReason.LateClick));
            Assert.That(result.LeftTargetBeforePress, Is.True);
            Assert.That(result.PressTimeOffsetMs, Is.EqualTo(80).Within(0.01));
        });
    }

    [Test]
    public void DistinguishesApproachingUndershootFromLeavingOvershoot()
    {
        ReplayMissAnalysis undershoot = analyse(
            frame(-150, 0, 100),
            frame(-20, 35, 100),
            frame(0, 45, 100, pressed: true),
            frame(20, 55, 100),
            frame(150, 80, 100));
        ReplayMissAnalysis overshoot = analyse(
            frame(-150, 80, 100),
            frame(-20, 135, 100),
            frame(0, 155, 100, pressed: true),
            frame(20, 175, 100),
            frame(150, 220, 100));

        Assert.Multiple(() =>
        {
            Assert.That(undershoot.Reason, Is.EqualTo(ReplayMissReason.Undershoot));
            Assert.That(undershoot.RadialVelocityAtObject, Is.LessThan(0));
            Assert.That(overshoot.Reason, Is.EqualTo(ReplayMissReason.Overshoot));
            Assert.That(overshoot.RadialVelocityAtObject, Is.GreaterThan(0));
        });
    }

    [Test]
    public void DetectsCursorOnTargetWithoutAClick()
    {
        ReplayMissAnalysis result = analyse(
            frame(-150, 40, 100),
            frame(-20, 100, 100),
            frame(20, 105, 100),
            frame(150, 170, 100));

        Assert.That(result.Reason, Is.EqualTo(ReplayMissReason.OnTargetNoClick));
    }

    [Test]
    public void MatchesTheReferenceImplementationOnRandomReplays()
    {
        var random = new Random(1234);
        for (int iteration = 0; iteration < 300; iteration++)
        {
            var frames = new List<OsuReplayFrame>();
            double time = -2000;
            bool held = false;
            while (time < 2000)
            {
                time += random.Next(1, iteration % 3 == 0 ? 90 : 20);
                if (random.Next(6) == 0)
                    held = !held;
                var position = new Vector2(random.Next(0, 512), random.Next(0, 384));
                frames.Add(held
                    ? new OsuReplayFrame(time, position, random.Next(2) == 0 ? OsuAction.LeftButton : OsuAction.RightButton)
                    : new OsuReplayFrame(time, position));
            }

            double objectTime = random.Next(-500, 500);
            var objectPosition = new Vector2(random.Next(0, 512), random.Next(0, 384));
            double radius = 20 + random.Next(40);
            double window = 50 + random.Next(150);

            Assert.That(
                ReplayMissAnalyzer.Analyse(frames, objectPosition, objectTime, radius, window),
                Is.EqualTo(ReferenceReplayMissAnalyzer.Analyse(frames, objectPosition, objectTime, radius, window)),
                $"iteration {iteration}");
        }
    }

    [Test]
    public void HandlesSingleFrameAndWindowsOutsideTheReplay()
    {
        OsuReplayFrame[] frames = [frame(0, 100, 100)];

        Assert.Multiple(() =>
        {
            Assert.That(
                ReplayMissAnalyzer.Analyse(frames, target, 5000, 30, 150),
                Is.EqualTo(ReferenceReplayMissAnalyzer.Analyse(frames, target, 5000, 30, 150)));
            Assert.That(
                ReplayMissAnalyzer.Analyse(frames, target, -5000, 30, 150),
                Is.EqualTo(ReferenceReplayMissAnalyzer.Analyse(frames, target, -5000, 30, 150)));
        });
    }

    private static ReplayMissAnalysis analyse(params OsuReplayFrame[] frames) =>
        ReplayMissAnalyzer.Analyse(frames, target, 0, 30, 150)
        ?? throw new AssertionException("Expected miss evidence.");

    private static OsuReplayFrame frame(double time, float x, float y, bool pressed = false) =>
        pressed
            ? new OsuReplayFrame(time, new Vector2(x, y), OsuAction.LeftButton)
            : new OsuReplayFrame(time, new Vector2(x, y));
}

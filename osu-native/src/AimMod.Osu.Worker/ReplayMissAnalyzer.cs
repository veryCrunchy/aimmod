using AimMod.Osu.Runtime.Contracts;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Replays;
using osuTK;

namespace AimMod.Osu.Worker;

internal static class ReplayMissAnalyzer
{
    private const double sample_interval = 4;

    public static ReplayMissAnalysis? Analyse(
        IReadOnlyList<OsuReplayFrame> frames,
        Vector2 target,
        double objectTime,
        double hitRadius,
        double hitWindow)
    {
        if (frames.Count == 0
            || !double.IsFinite(objectTime)
            || !double.IsFinite(hitRadius)
            || hitRadius <= 0
            || !double.IsFinite(hitWindow)
            || hitWindow <= 0)
            return null;

        double windowStart = objectTime - hitWindow;
        double windowEnd = objectTime + hitWindow;
        var samples = new List<CursorSample>();
        for (double time = windowStart; time <= windowEnd; time += sample_interval)
            samples.Add(sample(frames, time, target));
        if (samples.Count == 0)
            return null;

        CursorSample closest = samples.MinBy(value => value.Distance)!;
        CursorSample atObject = sample(frames, objectTime, target);
        CursorSample beforeObject = sample(frames, objectTime - 12, target);
        CursorSample afterObject = sample(frames, objectTime + 12, target);
        double radialVelocity = (afterObject.Distance - beforeObject.Distance) / 24;

        ReplayPress? press = findNearestPress(frames, objectTime, windowStart, windowEnd, target);
        bool enteredBefore = samples.Any(value => value.Time <= objectTime && value.Distance <= hitRadius);
        bool enteredAfter = samples.Any(value => value.Time > objectTime && value.Distance <= hitRadius);
        bool leftBeforePress = press is not null
                               && samples.Any(value => value.Time < press.Time && value.Distance <= hitRadius)
                               && press.Distance > hitRadius;
        CursorSample[] inside = samples.Where(value => value.Distance <= hitRadius).ToArray();
        double? firstEntry = inside.Length == 0 ? null : inside[0].Time - objectTime;
        double? lastExit = inside.Length == 0 ? null : inside[^1].Time - objectTime;
        double maximumFrameGap = maximumGap(frames, windowStart, windowEnd);
        bool keyHeldAtObject = isPressed(frameAtOrBefore(frames, objectTime));

        ReplayMissReason reason = classify(
            press,
            hitRadius,
            closest,
            atObject,
            enteredBefore,
            enteredAfter,
            leftBeforePress,
            radialVelocity,
            objectTime);
        double confidence = confidenceFor(reason, press, enteredBefore, enteredAfter, maximumFrameGap);

        return new ReplayMissAnalysis(
            reason,
            hitRadius,
            closest.Distance,
            closest.Time - objectTime,
            point(closest.Position),
            press?.Time - objectTime,
            press?.Distance,
            press is null ? null : point(press.Position),
            atObject.Distance,
            enteredBefore,
            enteredAfter,
            leftBeforePress,
            radialVelocity,
            ClassifierVersion: 1,
            Confidence: confidence,
            FirstTargetEntryOffsetMs: firstEntry,
            LastTargetExitOffsetMs: lastExit,
            MaximumFrameGapMs: maximumFrameGap,
            KeyHeldAtObject: keyHeldAtObject);
    }

    private static ReplayMissReason classify(
        ReplayPress? press,
        double radius,
        CursorSample closest,
        CursorSample atObject,
        bool enteredBefore,
        bool enteredAfter,
        bool leftBeforePress,
        double radialVelocity,
        double objectTime)
    {
        if (press is not null)
        {
            if (press.Time < objectTime && press.Distance > radius && (enteredAfter || closest.Time > press.Time && closest.Distance <= radius))
                return ReplayMissReason.EarlyClick;
            if (press.Time > objectTime && press.Distance > radius && (leftBeforePress || enteredBefore))
                return ReplayMissReason.LateClick;
            if (press.Distance > radius)
                return radialVelocity < -0.02 ? ReplayMissReason.Undershoot
                    : radialVelocity > 0.02 ? ReplayMissReason.Overshoot
                    : ReplayMissReason.AimDeviation;
            return ReplayMissReason.Unknown;
        }

        if (enteredBefore || enteredAfter || atObject.Distance <= radius)
            return ReplayMissReason.OnTargetNoClick;
        if (radialVelocity < -0.02)
            return ReplayMissReason.Undershoot;
        if (radialVelocity > 0.02)
            return ReplayMissReason.Overshoot;
        return ReplayMissReason.AimDeviation;
    }

    private static ReplayPress? findNearestPress(
        IReadOnlyList<OsuReplayFrame> frames,
        double objectTime,
        double windowStart,
        double windowEnd,
        Vector2 target)
    {
        bool wasPressed = false;
        var presses = new List<ReplayPress>();
        foreach (OsuReplayFrame frame in frames)
        {
            bool pressed = isPressed(frame);
            if (frame.Time >= windowStart && frame.Time <= windowEnd && pressed && !wasPressed)
                presses.Add(new ReplayPress(frame.Time, frame.Position, Vector2.Distance(frame.Position, target)));
            wasPressed = pressed;
            if (frame.Time > windowEnd)
                break;
        }

        return presses.OrderBy(value => Math.Abs(value.Time - objectTime)).FirstOrDefault();
    }

    private static bool isPressed(OsuReplayFrame frame) =>
        frame.Actions.Contains(OsuAction.LeftButton) || frame.Actions.Contains(OsuAction.RightButton);

    private static OsuReplayFrame frameAtOrBefore(IReadOnlyList<OsuReplayFrame> frames, double time)
    {
        int upper = lowerBound(frames, time);
        if (upper < frames.Count && frames[upper].Time <= time)
            return frames[upper];
        return upper <= 0 ? frames[0] : frames[Math.Min(upper - 1, frames.Count - 1)];
    }

    private static double maximumGap(IReadOnlyList<OsuReplayFrame> frames, double start, double end)
    {
        OsuReplayFrame[] local = frames.Where(frame => frame.Time >= start && frame.Time <= end).ToArray();
        if (local.Length < 2)
            return end - start;
        double maximum = 0;
        for (int index = 1; index < local.Length; index++)
            maximum = Math.Max(maximum, local[index].Time - local[index - 1].Time);
        return maximum;
    }

    private static double confidenceFor(
        ReplayMissReason reason,
        ReplayPress? press,
        bool enteredBefore,
        bool enteredAfter,
        double maximumFrameGap)
    {
        double confidence = reason switch
        {
            ReplayMissReason.EarlyClick when press is not null && enteredAfter => 0.9,
            ReplayMissReason.LateClick when press is not null && enteredBefore => 0.9,
            ReplayMissReason.OnTargetNoClick => 0.85,
            ReplayMissReason.Undershoot or ReplayMissReason.Overshoot => 0.68,
            ReplayMissReason.AimDeviation => 0.58,
            _ => 0.35,
        };
        if (maximumFrameGap > 50)
            confidence *= 0.6;
        return Math.Clamp(confidence, 0, 1);
    }

    private static CursorSample sample(IReadOnlyList<OsuReplayFrame> frames, double time, Vector2 target)
    {
        int upper = lowerBound(frames, time);
        if (upper <= 0)
            return cursorSample(frames[0].Position, time, target);
        if (upper >= frames.Count)
            return cursorSample(frames[^1].Position, time, target);

        OsuReplayFrame previous = frames[upper - 1];
        OsuReplayFrame next = frames[upper];
        double span = next.Time - previous.Time;
        float progress = span <= 0 ? 0 : (float)Math.Clamp((time - previous.Time) / span, 0, 1);
        return cursorSample(Vector2.Lerp(previous.Position, next.Position, progress), time, target);
    }

    private static int lowerBound(IReadOnlyList<OsuReplayFrame> frames, double time)
    {
        int low = 0;
        int high = frames.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (frames[middle].Time < time)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    private static CursorSample cursorSample(Vector2 position, double time, Vector2 target) =>
        new(time, position, Vector2.Distance(position, target));

    private static ReplayPoint point(Vector2 position) => new(position.X, position.Y);

    private sealed record CursorSample(double Time, Vector2 Position, double Distance);
    private sealed record ReplayPress(double Time, Vector2 Position, double Distance);
}

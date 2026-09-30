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
        if (windowStart > windowEnd)
            return null;

        ReplayPress? press = findNearestPress(frames, objectTime, windowStart, windowEnd, target);
        bool hasClosest = false;
        CursorSample closest = default;
        bool enteredBefore = false;
        bool enteredAfter = false;
        bool insideBeforePress = false;
        double firstInsideTime = 0;
        double lastInsideTime = 0;
        bool anyInside = false;
        for (double time = windowStart; time <= windowEnd; time += sample_interval)
        {
            CursorSample value = sample(frames, time, target);
            if (!hasClosest || value.Distance < closest.Distance)
            {
                closest = value;
                hasClosest = true;
            }

            if (value.Distance > hitRadius)
                continue;

            if (value.Time <= objectTime)
                enteredBefore = true;
            else
                enteredAfter = true;
            if (press is { } pressed && value.Time < pressed.Time)
                insideBeforePress = true;
            if (!anyInside)
            {
                firstInsideTime = value.Time;
                anyInside = true;
            }

            lastInsideTime = value.Time;
        }

        if (!hasClosest)
            return null;

        CursorSample atObject = sample(frames, objectTime, target);
        CursorSample beforeObject = sample(frames, objectTime - 12, target);
        CursorSample afterObject = sample(frames, objectTime + 12, target);
        double radialVelocity = (afterObject.Distance - beforeObject.Distance) / 24;

        bool leftBeforePress = press is { } leftPress && insideBeforePress && leftPress.Distance > hitRadius;
        double? firstEntry = anyInside ? firstInsideTime - objectTime : null;
        double? lastExit = anyInside ? lastInsideTime - objectTime : null;
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
            press is { } reported ? point(reported.Position) : null,
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
        if (press is { } click)
        {
            if (click.Time < objectTime && click.Distance > radius && (enteredAfter || closest.Time > click.Time && closest.Distance <= radius))
                return ReplayMissReason.EarlyClick;
            if (click.Time > objectTime && click.Distance > radius && (leftBeforePress || enteredBefore))
                return ReplayMissReason.LateClick;
            if (click.Distance > radius)
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
        int start = lowerBound(frames, windowStart);
        bool wasPressed = start > 0 && isPressed(frames[start - 1]);
        ReplayPress? nearest = null;
        double nearestOffset = double.PositiveInfinity;
        for (int index = start; index < frames.Count; index++)
        {
            OsuReplayFrame frame = frames[index];
            if (frame.Time > windowEnd)
                break;

            bool pressed = isPressed(frame);
            if (pressed && !wasPressed)
            {
                double offset = Math.Abs(frame.Time - objectTime);
                if (offset < nearestOffset)
                {
                    nearest = new ReplayPress(frame.Time, frame.Position, Vector2.Distance(frame.Position, target));
                    nearestOffset = offset;
                }
            }

            wasPressed = pressed;
        }

        return nearest;
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
        int index = lowerBound(frames, start);
        int count = 0;
        double previous = 0;
        double maximum = 0;
        for (; index < frames.Count && frames[index].Time <= end; index++)
        {
            double time = frames[index].Time;
            if (count > 0)
                maximum = Math.Max(maximum, time - previous);
            previous = time;
            count++;
        }

        return count < 2 ? end - start : maximum;
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

    private readonly record struct CursorSample(double Time, Vector2 Position, double Distance);
    private readonly record struct ReplayPress(double Time, Vector2 Position, double Distance);
}

using osu.Game.Beatmaps;
using osu.Game.Audio;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Objects.Types;
using osuTK;

namespace AimMod.Desktop.Trainers;

public static class TrainerSpinners
{
    public static double End(OsuHitObject obj) => obj is IHasDuration duration ? duration.EndTime : obj.StartTime;

    public static void Apply(TrainerSettings settings, Beatmap<OsuHitObject> map)
    {
        if (map.HitObjects.Count == 0 || settings.Kind != TrainerKind.Spinner && settings.Spinners == TrainerSpinnerFrequency.None) return;
        foreach (var obj in map.HitObjects) obj.ApplyDefaults(map.ControlPointInfo, map.Difficulty);
        var original = map.HitObjects.ToArray();
        double end = original.Max(End);
        double gap = settings.Kind == TrainerKind.Reading ? Math.Max(1000, TrainerReadingPatterns.VisibilityMs(settings)) : 1000;
        double next = original[0].StartTime + (settings.Kind == TrainerKind.Spinner ? 0 : 8000);
        double duration = settings.SpinnerSeconds * 1000;
        var replacements = new List<OsuHitObject>();
        if (settings.Kind == TrainerKind.Spinner) map.HitObjects.Clear();
        while (next + duration <= end)
        {
            // Snap to an existing musical onset, including imported tempo changes.
            double start = original.FirstOrDefault(o => o.StartTime >= next)?.StartTime ?? end;
            if (start + duration > end) break;
            if (settings.Kind != TrainerKind.Spinner)
            {
                // Never cut a slider already being held. Move the insertion after its tail.
                var held = map.HitObjects.LastOrDefault(o => o.StartTime < start && End(o) > start - gap);
                if (held is Slider) { next = End(held) + gap; continue; }
                map.HitObjects.RemoveAll(o => End(o) > start - gap && o.StartTime < start + duration + gap);
            }
            replacements.Add(new Spinner { StartTime = start, Duration = duration, Position = new Vector2(256, 192),
                NewCombo = true, Samples = [new HitSampleInfo(HitSampleInfo.HIT_NORMAL)] });
            next = start + duration + (settings.Kind == TrainerKind.Spinner ? 1500 : 12000);
        }
        map.HitObjects.AddRange(replacements);
        map.HitObjects.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
    }
}

public sealed record SpinnerPracticeSummary(int Attempts, double MeanRpm, double? SpeedVariationPercent,
    double HeldPercent, int DirectionChanges, double? MeanRadius = null);

/// <summary>Observes the native spinner. It never changes cursor input or scoring.</summary>
public sealed class SpinnerPracticeMetrics
{
    private double totalMs, heldMs, weightedRpm;
    private double windowMs, windowRpm, sampledMs, sampledRpm, sampledSquareRpm;
    private int attempts, directionChanges, lastDirection;
    private double reverseTravel;
    private double radiusTotal, radiusMs;
    public void Begin() { attempts++; lastDirection = 0; reverseTravel = 0; windowMs = windowRpm = 0; }
    public void Sample(double elapsedMs, double rpm, bool held, double angleDelta, double? radius = null)
    {
        if (!double.IsFinite(elapsedMs) || elapsedMs <= 0 || elapsedMs > 100 || !double.IsFinite(rpm) || rpm < 0 || !double.IsFinite(angleDelta)) return;
        totalMs += elapsedMs;
        if (!held) { lastDirection = 0; reverseTravel = 0; windowMs = windowRpm = 0; return; }
        heldMs += elapsedMs;
        if (radius is >= 0 && double.IsFinite(radius.Value)) { radiusTotal += radius.Value * elapsedMs; radiusMs += elapsedMs; }
        weightedRpm += rpm * elapsedMs;
        // Compare 300 ms windows, not per-frame rates or the HUD's startup smoothing.
        windowMs += elapsedMs; windowRpm += rpm * elapsedMs;
        if (windowMs >= 300)
        {
            double rate = windowRpm / windowMs;
            sampledMs += windowMs; sampledRpm += windowRpm; sampledSquareRpm += rate * rate * windowMs;
            windowMs = windowRpm = 0;
        }
        int direction = Math.Sign(angleDelta);
        if (Math.Abs(angleDelta) < .05) return;
        if (lastDirection == 0) lastDirection = direction;
        if (direction == lastDirection) reverseTravel = 0;
        else if ((reverseTravel += Math.Abs(angleDelta)) >= 30)
        { directionChanges++; lastDirection = direction; reverseTravel = 0; }
    }
    public SpinnerPracticeSummary Result()
    {
        double mean = heldMs > 0 ? weightedRpm / heldMs : 0;
        double steadyMean = sampledMs > 0 ? sampledRpm / sampledMs : 0;
        double? variation = sampledMs >= 600 && steadyMean > 0
            ? Math.Sqrt(Math.Max(0, sampledSquareRpm / sampledMs - steadyMean * steadyMean)) / steadyMean * 100 : null;
        return new(attempts, mean, variation, totalMs > 0 ? heldMs / totalMs * 100 : 0, directionChanges,
            radiusMs > 0 ? radiusTotal / radiusMs : null);
    }
}

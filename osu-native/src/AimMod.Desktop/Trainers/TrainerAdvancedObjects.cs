using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Types;
using osu.Game.Rulesets.Osu.Objects;
using osuTK;

namespace AimMod.Desktop.Trainers;

public enum TrainerSliderShape { Straight, Arc, SCurve, Angular, Mixed }
public enum TrainerSpinnerPattern { Steady, BuildUp, MixedLengths }

public static class TrainerAdvancedObjects
{
    public static SliderPath Slider(TrainerSettings settings, Vector2 position, int index, double length)
    {
        var shape = settings.SliderShape == TrainerSliderShape.Mixed
            ? (TrainerSliderShape)(Math.Abs((long)settings.PatternSeed + index) % 4) : settings.SliderShape;
        Vector2 direction = new(position.X < 256 ? 1 : -1, position.Y < 192 ? .35f : -.35f);
        direction.Normalize();
        Vector2 side = new(-direction.Y, direction.X);
        float span = (float)length;
        Vector2 safe(Vector2 relative) => Vector2.Clamp(position + relative, new(32, 32), new(480, 352)) - position;
        Vector2 end = safe(direction * span * (shape == TrainerSliderShape.Straight ? 1 : .75f));
        PathControlPoint point(Vector2 value) => new(safe(value));
        PathControlPoint[] points = shape switch {
            TrainerSliderShape.Arc => [new(Vector2.Zero, PathType.BEZIER), point(end * .5f + side * span * .4f), new(end)],
            TrainerSliderShape.SCurve => [new(Vector2.Zero, PathType.BEZIER), point(end * .25f + side * span * .55f), point(end * .75f - side * span * .55f), new(end)],
            TrainerSliderShape.Angular => [new(Vector2.Zero, PathType.LINEAR), point(end * .45f + side * span * .3f), new(end)],
            _ => [new(Vector2.Zero, PathType.LINEAR), new(end)],
        };
        // Use the actual fitted path length. Extending a clipped curve can leave the playfield.
        var path = new SliderPath(points);
        if (path.Distance > length)
            path = new SliderPath(points.Select(p => new PathControlPoint(p.Position * (float)(length / path.Distance), p.Type)).ToArray());
        return path;
    }

    public static double SpinSeconds(TrainerSettings settings, int index) => settings.SpinnerPattern switch {
        TrainerSpinnerPattern.BuildUp => Math.Min(settings.SpinnerSeconds, 2 + index / 2 * 2),
        TrainerSpinnerPattern.MixedLengths => Math.Min(settings.SpinnerSeconds, new[] { 2, 4, 2, 6 }[index % 4]),
        _ => settings.SpinnerSeconds,
    };
}

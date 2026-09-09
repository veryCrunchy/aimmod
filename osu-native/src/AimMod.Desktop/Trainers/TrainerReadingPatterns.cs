using osuTK;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.ControlPoints;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Mods;

namespace AimMod.Desktop.Trainers;

public sealed record ReadingWindowResult(double StartSeconds, int Circles, int Misses)
{
    public double HitPercent => Circles == 0 ? 0 : 100.0 * (Circles - Misses) / Circles;
}

public static class TrainerReadingPatterns
{
    public static Mod[] Mods(TrainerSettings settings) => settings.Kind == TrainerKind.Reading && settings.ReadingHidden ? [new OsuModHidden()] : [];
    public static double VisibilityMs(TrainerSettings settings)
    {
        var circle = new HitCircle();
        circle.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty { ApproachRate = settings.ApproachRate, CircleSize = settings.CircleSize });
        return circle.TimePreempt + 250;
    }

    public static IReadOnlyList<TrainerNote> ConstrainTiming(TrainerSettings settings, IReadOnlyList<TrainerNote> source)
    {
        if (settings.Kind != TrainerKind.Reading) return source;
        // Include the judgement tail so missed circles do not immediately overlap a new phrase.
        double visibility = VisibilityMs(settings);
        int visibleLimit = 3 + settings.ReadingComplexity;
        double minGap = Math.Max(250, visibility / visibleLimit + 1);
        double recovery = Math.Max(600, visibility);
        var result = new List<TrainerNote>();
        foreach (var note in source)
        {
            if (result.Count > 0)
            {
                double gap = note.TimeMs - result[^1].TimeMs;
                bool nextPhrase = result.Count % settings.ReadingGroupSize == 0;
                if (gap < (nextPhrase ? recovery : minGap) - .001) continue;
            }
            result.Add(note with { Phrase = result.Count / settings.ReadingGroupSize });
        }
        return result;
    }

    public static Vector2[] Create(TrainerSettings settings, IReadOnlyList<TrainerNote> notes)
    {
        var result = new Vector2[notes.Count];
        var random = new Random(settings.PatternSeed);
        int length = settings.ReadingGroupSize;
        double visibility = VisibilityMs(settings);
        float separation = settings.CircleSize <= 4 ? 76 : 64;
        float sample(float min, float max) => min + (float)random.NextDouble() * (max - min);
        for (int start = 0; start < notes.Count; start += length)
        {
            int count = Math.Min(length, notes.Count - start);
            var pattern = notes[start].Pattern;
            float angle = sample(-MathF.PI, MathF.PI), bend = sample(35, 105), stretch = sample(.7f, 1.25f);
            float phase = sample(-.5f, .5f), radius = sample(55, 115), sweep = sample(1.3f, 1.7f);
            int corners = random.Next(3, 7), stackLength = 2;
            bool mirrored = random.Next(2) == 0;
            Vector2 centre = new(sample(220, 292), sample(156, 228));
            var local = new Vector2[count];
            float direction = sample(-MathF.PI, MathF.PI);
            Vector2 walk = Vector2.Zero;
            for (int j = 0; j < count; j++)
            {
                float t = j / (float)Math.Max(1, count - 1);
                float arc = t * MathF.PI * sweep + phase;
                local[j] = pattern switch
                {
                    TrainerPattern.Crossings => new((j % 2 == 0 ? -1 : 1) * sample(75, 135), -100 + t * 200),
                    TrainerPattern.Reversals => new(-120 + (j < count / 2 ? j : count - 1 - j) * 240f / Math.Max(1, count / 2 - 1), j < count / 2 ? -bend / 2 : bend / 2),
                    TrainerPattern.Overlaps => new((28 + t * 35) * MathF.Cos(arc * 2), (28 + t * 35) * MathF.Sin(arc * 2)),
                    TrainerPattern.ReadingPolygons => new(radius * MathF.Cos(j * MathF.Tau / corners), radius * MathF.Sin(j * MathF.Tau / corners)),
                    TrainerPattern.ReadingWeave => new(115 * MathF.Sin(t * MathF.Tau + phase), bend * MathF.Sin(t * MathF.Tau * 2 + phase)),
                    TrainerPattern.ReadingStacks => new(-105 + (j / stackLength) * 210f / Math.Max(1, (count - 1) / stackLength) + j % stackLength * 18, (j / stackLength % 2 == 0 ? -45 : 45) + j % stackLength * 18),
                    TrainerPattern.ReadingSpacedStreams => new(-125 + t * 250, bend * MathF.Sin(arc)),
                    _ => new(-125 + t * 250, bend * MathF.Sin(arc)),
                };
                if (pattern == TrainerPattern.Scattered)
                {
                    direction += sample(.8f, 2.4f) * (random.Next(2) == 0 ? -1 : 1);
                    walk += new Vector2(MathF.Cos(direction), MathF.Sin(direction)) * sample(55, 130);
                    local[j] = walk;
                }
                // Change spacing within a phrase, not just its rotation between phrases.
                if (pattern is not (TrainerPattern.Overlaps or TrainerPattern.ReadingStacks))
                    local[j] *= sample(.8f, 1.15f);
                local[j].X *= mirrored ? -stretch : stretch;
                local[j] = new(local[j].X * MathF.Cos(angle) - local[j].Y * MathF.Sin(angle),
                    local[j].X * MathF.Sin(angle) + local[j].Y * MathF.Cos(angle));
            }
            // Fit a whole phrase as one shape. Per-note clamping creates accidental edge stacks.
            Vector2 min = new(local.Min(p => p.X), local.Min(p => p.Y)), max = new(local.Max(p => p.X), local.Max(p => p.Y));
            var midpoint = (min + max) / 2;
            float scale = Math.Min(settings.AimSpacing / 100f, Math.Min(300 / Math.Max(1, max.X - min.X), 184 / Math.Max(1, max.Y - min.Y)));
            for (int j = 0; j < count; j++)
            {
                var position = centre + (local[j] - midpoint) * scale;
                if (start + j > 0 && pattern is not (TrainerPattern.Overlaps or TrainerPattern.ReadingStacks)
                    && Vector2.Distance(position, result[start + j - 1]) < 42)
                    position = result[start + j - 1] + new Vector2(result[start + j - 1].X < 256 ? 48 : -48, 0);
                position = Vector2.Clamp(position, new(64, 64), new(448, 320));
                int index = start + j;
                // Only explicit overlap drills permit a pair of close targets. No three-note piles.
                bool permitsPair = pattern is TrainerPattern.Overlaps or TrainerPattern.ReadingStacks && j % 2 == 1;
                bool clear(Vector2 candidate)
                {
                    for (int previous = index - 1; previous >= 0 && notes[index].TimeMs - notes[previous].TimeMs < visibility; previous--)
                    {
                        if (permitsPair && previous == index - 1) continue;
                        if (Vector2.Distance(candidate, result[previous]) < separation) return false;
                    }
                    return true;
                }
                if (!clear(position))
                {
                    Vector2 desired = position;
                    position = Enumerable.Range(0, 20).Select(cell => new Vector2(76 + cell % 5 * 90, 72 + cell / 5 * 80))
                        .Where(clear).OrderBy(candidate => Vector2.DistanceSquared(candidate, desired)).First();
                }
                result[index] = position;
            }
        }
        return result;
    }
}

using osuTK;

namespace AimMod.Desktop.Trainers;

public enum TrainerAimStyle { Balanced, WideJumps, Flow, SmallCorrections, DirectionChanges }

public static class TrainerAimPatterns
{
    public static Vector2[] Create(TrainerSettings settings, int count)
    {
        var random = new Random(settings.PatternSeed);
        var result = new Vector2[count];
        Vector2 previous = new(210 + random.Next(90), 150 + random.Next(80));
        double heading = random.NextDouble() * Math.PI * 2;
        for (int i = 0; i < count; i++)
        {
            if (i == 0) { result[i] = previous; continue; }
            double distance = (settings.AimStyle switch
            {
                TrainerAimStyle.WideJumps => 240 + random.NextDouble() * 100,
                TrainerAimStyle.Flow => 70 + random.NextDouble() * 30,
                TrainerAimStyle.SmallCorrections => 35 + random.NextDouble() * 40,
                TrainerAimStyle.DirectionChanges => 150 + random.NextDouble() * 80,
                _ => 140 + random.NextDouble() * 120,
            }) * settings.AimSpacing / 100.0;
            Vector2 best = previous; double bestScore = double.MaxValue;
            for (int attempt = 0; attempt < 32; attempt++)
            {
                double angle = settings.AimStyle switch
                {
                    TrainerAimStyle.Flow => heading + (random.NextDouble() - .5) * (.6 + attempt * .1),
                    TrainerAimStyle.DirectionChanges => heading + Math.PI + (random.NextDouble() - .5) * 1.5,
                    _ => random.NextDouble() * Math.PI * 2,
                };
                Vector2 candidate = new(reflect(previous.X + Math.Cos(angle) * distance, 64, 448),
                    reflect(previous.Y + Math.Sin(angle) * distance, 64, 320));
                double score = Math.Abs(Vector2.Distance(previous, candidate) - distance);
                if (score < bestScore) { best = candidate; bestScore = score; }
                if (score < 2) break;
            }
            heading = Math.Atan2(best.Y - previous.Y, best.X - previous.X);
            result[i] = previous = best;
        }
        return result;
    }
    private static float reflect(double value, double min, double max)
    {
        double range = max - min, wrapped = ((value - min) % (range * 2) + range * 2) % (range * 2);
        return (float)(min + (wrapped > range ? 2 * range - wrapped : wrapped));
    }
}

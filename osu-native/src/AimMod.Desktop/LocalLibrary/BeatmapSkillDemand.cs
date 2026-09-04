namespace AimMod.Desktop.LocalLibrary;

internal sealed record BeatmapSkillDemand(double Aim, double Speed, double Stamina, double Reading, double Precision)
{
    public static BeatmapSkillDemand From(LocalBeatmapDifficulty difficulty)
    {
        ArgumentNullException.ThrowIfNull(difficulty);

        double stars = normalise(difficulty.StarRating, 1.5, 9);
        double bpm = normalise(difficulty.Bpm, 100, 280);
        double duration = normalise(difficulty.LengthMilliseconds, 45_000, 420_000);
        double circleSize = normalise(difficulty.CircleSize, 3, 7);
        double approachRate = normalise(difficulty.ApproachRate, 6, 10.5);
        double overallDifficulty = normalise(difficulty.OverallDifficulty, 5, 10.5);

        return new BeatmapSkillDemand(
            weighted(stars, 0.62, circleSize, 0.23, approachRate, 0.15),
            weighted(bpm, 0.52, overallDifficulty, 0.28, stars, 0.20),
            weighted(duration, 0.48, bpm, 0.32, stars, 0.20),
            weighted(approachRate, 0.48, stars, 0.34, overallDifficulty, 0.18),
            weighted(circleSize, 0.42, overallDifficulty, 0.33, stars, 0.25));
    }

    private static double normalise(double value, double minimum, double maximum)
    {
        if (!double.IsFinite(value))
            return 0;
        return Math.Clamp((value - minimum) / (maximum - minimum), 0, 1);
    }

    private static double weighted(double first, double firstWeight, double second, double secondWeight, double third, double thirdWeight) =>
        Math.Clamp(first * firstWeight + second * secondWeight + third * thirdWeight, 0, 1);
}

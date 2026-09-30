namespace AimMod.Desktop.Trainers;

/// <summary>Training-specific defaults, not an equation converting stars into osu! settings.</summary>
public sealed record TrainerDifficultyParameters(int ApproachRate, int CircleSize, double OverallDifficulty)
{
    public static TrainerDifficultyParameters For(TrainerSettings settings, TrainerSkillLimits limits)
    {
        double difficulty = settings.MinimumStars is {} lo && settings.MaximumStars is {} hi ? (lo + hi) / 2
            : Math.Clamp(Math.Max(limits.MaxNps / 2, limits.MaxJumpDistance / 100), 1, 8);
        var (arBase, arSlope, odBase, odSlope) = settings.Kind switch
        {
            TrainerKind.Aim => (5d, .7, 3d, .5),
            TrainerKind.Reading => (4d, .6, 3d, .4),
            TrainerKind.Alternating => (5d, .65, 3.5, .5),
            TrainerKind.Bursts => (5d, .7, 3.5, .55),
            TrainerKind.Rhythm => (5d, .55, 4d, .5),
            _ => (5.5, .55, 4d, .55),
        };
        int ar = (int)Math.Clamp(Math.Round(arBase + difficulty * arSlope), 4, settings.Kind == TrainerKind.Reading ? 8 : 9);
        // Prevent dense tapping from filling the screen with approach circles. Reading
        // retains its longer preview and controls clutter through its phrase/visibility limits.
        double step = TrainerPatterns.Step(settings with { AdaptiveDifficulty = false, RandomizePatterns = false });
        double nps = Math.Min(limits.MaxNps, settings.Bpm / (60 * step));
        if (settings.Kind is not (TrainerKind.Reading or TrainerKind.Spinner or TrainerKind.Reaction))
            ar = Math.Max(ar, nps >= 9 ? 9 : nps >= 6 ? 8 : nps >= 4 ? 7 : 4);
        int cs = difficulty < 2 ? 3 : settings.Kind == TrainerKind.Aim && difficulty >= 6 ? 5 : 4;
        double od = Math.Clamp(Math.Round((odBase + difficulty * odSlope) * 4) / 4, 3, 8.5);
        return new(ar, cs, od);
    }

    public static bool SameTarget(TrainerSettings first, TrainerSettings second) =>
        rounded(first.MinimumStars) == rounded(second.MinimumStars) && rounded(first.MaximumStars) == rounded(second.MaximumStars)
        // Older builds sometimes recorded an easy map under a much harder target.
        && (first.MeasuredStars is not {} actual || second.MinimumStars is not {} lo || second.MaximumStars is not {} hi
            || actual >= lo - TrainerStarTarget.Tolerance && actual <= hi + TrainerStarTarget.Tolerance);

    private static double? rounded(double? value) => value is {} number ? Math.Round(number, 1) : null;
}

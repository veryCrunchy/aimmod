namespace AimMod.Desktop.Trainers;

public static class TrainerAdaptiveDifficulty
{
    public static TrainerSettings Apply(TrainerSettings settings, IEnumerable<TrainerResult> history,
        IEnumerable<TrainerSkillEvidence> evidence, DateTimeOffset now)
    {
        if (!settings.AdaptiveDifficulty) return TrainerSkillProfile.Apply(settings,
            TrainerSkillProfile.Build(settings.Kind, history, evidence, now));
        var runs = history.Where(r => r.Settings.Kind == settings.Kind && r.Settings.AdaptiveDifficulty
            && !r.Assisted && r.WarmupRun is null && r.CompletedAt >= now.AddDays(-30) && r.CompletedAt <= now)
            .DistinctBy(r => r.Id).OrderByDescending(r => r.CompletedAt).Take(12).ToArray();
        if (settings.Kind == TrainerKind.Reaction)
        {
            runs = runs.Where(r => r.Engine == "reaction-v2" && r.Settings.ReactionMode == settings.ReactionMode
                && r.Settings.ReactionDelay == settings.ReactionDelay && r.Notes >= 5 && r.Reaction is not null
                && r.Hits >= 0 && r.Hits <= r.Notes && r.Extras >= 0
                && r.PlayedSeconds >= r.Settings.Seconds * .8).ToArray();
            int window = runs.FirstOrDefault()?.Settings.ReactionWindowMs ?? settings.ReactionWindowMs;
            var matching = runs.Take(3).Where(r => r.Settings.ReactionWindowMs == window).ToArray();
            bool good = matching.Length == 3 && matching.All(r => r.Hits >= r.Notes * .95 && r.Extras == 0
                && r.Reaction!.Slow90Ms is > 0 && r.Reaction.Slow90Ms < window * .7);
            bool hard = matching.Count(r => r.Hits < r.Notes * .8 || r.Extras > r.Notes * .1) >= 2;
            return settings with { ReactionWindowMs = step([600, 1000, 1200, 1500, 2000], window, good ? -1 : hard ? 1 : 0), SkillLimits = null };
        }
        if (settings.Kind == TrainerKind.Spinner)
        {
            runs = runs.Where(r => TrainerPerformance.IsCompleted(r, now) && r.SpinnerPractice is { Attempts: > 0 }).ToArray();
            int length = runs.FirstOrDefault()?.Settings.SpinnerSeconds ?? settings.SpinnerSeconds;
            var matching = runs.Take(3).Where(r => r.Settings.SpinnerSeconds == length).ToArray();
            bool good = matching.Length == 3 && matching.All(r => r.Accuracy >= 95 && r.Misses == 0
                && r.SpinnerPractice is { HeldPercent: >= 95, SpeedVariationPercent: >= 0 and <= 20 });
            bool hard = matching.Count(r => r.Accuracy < 80 || r.Misses > 0 || r.SpinnerPractice!.HeldPercent < 80) >= 2;
            return settings with { SpinnerSeconds = step([2, 4, 6], length, good ? 1 : hard ? -1 : 0), SkillLimits = null };
        }
        var completed = runs.Where(r => TrainerPerformance.IsCompleted(r, now)).ToArray();
        double od = completed.FirstOrDefault()?.Settings.OverallDifficulty ?? settings.OverallDifficulty;
        var atDifficulty = completed.Take(3).Where(r => r.Settings.OverallDifficulty == od
            && r.Settings.Pattern == settings.Pattern && r.Settings.GuidedCues == settings.GuidedCues).ToArray();
        double reference = TrainerPerformance.Reference(completed.Skip(3).Count() >= 3 ? completed.Skip(3) : completed);
        bool steady = atDifficulty.Length == 3 && atDifficulty.Sum(r => r.Notes) >= 24 && atDifficulty.All(r => TrainerPerformance.Steady(r, reference))
            && atDifficulty.Max(r => r.Accuracy) - atDifficulty.Min(r => r.Accuracy) <= 3;
        bool struggling = atDifficulty.Count(r => TrainerPerformance.Struggling(r, reference)) >= 2;
        // Small timing-window steps add challenge even when musical subdivisions keep note density unchanged.
        od = Math.Clamp(od + (steady ? .25 : struggling ? -.5 : 0), 2, 8);
        return TrainerSkillProfile.Apply(settings with { OverallDifficulty = od }, TrainerSkillProfile.Build(settings.Kind, history, evidence, now));
    }

    private static int step(int[] choices, int current, int direction)
    {
        int at = Array.IndexOf(choices, current);
        return at < 0 ? current : choices[Math.Clamp(at + direction, 0, choices.Length - 1)];
    }

    public static string Describe(TrainerSettings settings) => settings.Kind switch
    {
        TrainerKind.Reaction => $"Next run: {settings.ReactionWindowMs} ms to respond. Steady, correct responses shorten the window; repeated mistakes give you more time.",
        TrainerKind.Spinner => $"Next run: {settings.SpinnerSeconds}-second spins. Smooth, completed spins build duration; repeated difficulty shortens them.",
        _ when settings.SkillLimits is {} limits => $"Next run: up to {limits.MaxNps:0.#} taps/s, {limits.MaxJumpDistance:0} px jumps, AR {Math.Min(settings.ApproachRate, limits.MaxApproachRate)}, OD {settings.OverallDifficulty:0.##}. Steady plays gradually tighten timing and raise limits; difficult runs ease them.",
        _ => "Your selected difficulty stays fixed.",
    };
}

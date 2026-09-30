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
            double spinOd = runs.FirstOrDefault()?.Settings.OverallDifficulty ?? 5;
            return settings with { SpinnerSeconds = step([2, 4, 6], length, good ? 1 : hard ? -1 : 0),
                OverallDifficulty = Math.Clamp(spinOd + (good ? .25 : hard ? -.5 : 0), 3, 7), SkillLimits = null };
        }
        var limits = TrainerSkillProfile.Build(settings.Kind, history, evidence, now, TrainerStarTarget.StartingLimits(settings));
        var parameters = TrainerDifficultyParameters.For(settings, limits);
        var completed = runs.Where(r => TrainerPerformance.IsCompleted(r, now) && TrainerDifficultyParameters.SameTarget(r.Settings, settings)).ToArray();
        double od = completed.FirstOrDefault()?.Settings.OverallDifficulty ?? parameters.OverallDifficulty;
        var atDifficulty = completed.Take(3).Where(r => r.Settings.OverallDifficulty == od
            && r.Settings.Pattern == settings.Pattern && r.Settings.GuidedCues == settings.GuidedCues).ToArray();
        double reference = TrainerPerformance.Reference(completed.Skip(3).Count() >= 3 ? completed.Skip(3) : completed);
        bool steady = atDifficulty.Length == 3 && atDifficulty.Sum(r => r.Notes) >= 24 && atDifficulty.All(r => TrainerPerformance.Steady(r, reference))
            && atDifficulty.Max(r => r.Accuracy) - atDifficulty.Min(r => r.Accuracy) <= 3;
        bool struggling = atDifficulty.Count(r => TrainerPerformance.Struggling(r, reference)) >= 2;
        // Small timing-window steps add challenge even when musical subdivisions keep note density unchanged.
        od = Math.Clamp(od + (steady ? .25 : struggling ? -.5 : 0), 2, 8);
        bool sustained = settings.Kind is TrainerKind.Alternating or TrainerKind.Bursts;
        int tempo = TrainerMusicCatalog.IsSong(settings.Music)
            ? TrainerMusicCatalog.Tempos.OrderBy(b => Math.Abs(b - Math.Clamp(limits.MaxNps * (sustained ? 30 : 60), 90, limits.EvidenceCount == 0 && settings.MinimumStars is null ? 120 : 210))).First()
            : settings.Bpm;
        parameters = TrainerDifficultyParameters.For(settings with { Bpm = tempo, NoteSpeed = TrainerNoteSpeed.Default }, limits);
        limits = limits with { MaxApproachRate = Math.Max(limits.MaxApproachRate, parameters.ApproachRate) };
        // The chosen skill/preset remains the purpose. Automatic settings supply its load and object mix.
        var pattern = TrainerSkillProfile.Allows(settings.Pattern, limits) ? settings.Pattern : TrainerPattern.Standard;
        if (settings.Kind == TrainerKind.Reading) pattern = TrainerPattern.ReadingMix;
        return TrainerSkillProfile.Apply(settings with {
            Bpm = tempo, OverallDifficulty = od, Pattern = pattern, NoteSpeed = TrainerNoteSpeed.Default, RandomizePatterns = false,
            ApproachRate = parameters.ApproachRate, CircleSize = parameters.CircleSize,
            AimSpacing = limits.MaxJumpDistance < 100 ? 70 : limits.MaxJumpDistance < 150 ? 85 : limits.MaxJumpDistance < 220 ? 100 : 120,
            Sliders = sustained && !(settings.Kind == TrainerKind.Alternating && settings.Sliders != TrainerSliderStyle.None)
                ? TrainerSliderStyle.None : TrainerSliderStyle.Mixed,
            SliderBeats = limits.Complexity == 0 ? 1 : 2,
            SliderShape = limits.Complexity == 0 ? TrainerSliderShape.Arc : TrainerSliderShape.Mixed,
            SpinnerPattern = limits.Complexity == 0 ? TrainerSpinnerPattern.Steady : TrainerSpinnerPattern.MixedLengths,
            Spinners = settings.Kind == TrainerKind.Reading && limits.Complexity >= 1 && TrainerMusicCatalog.IsSong(settings.Music)
                ? TrainerSpinnerFrequency.Occasional : TrainerSpinnerFrequency.None,
            SpinnerSeconds = 2, ReadingComplexity = limits.Complexity,
            ReadingGroupSize = limits.Complexity == 0 ? 4 : limits.Complexity == 1 ? 6 : 8,
            PathStyle = limits.Complexity == 0 ? TrainerPathStyle.Arc : TrainerPathStyle.FigureEight,
        }, limits);
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
        _ when settings.SkillLimits is {} limits => $"{settings.Bpm} BPM · up to {limits.MaxNps:0.#} taps/s · {limits.MaxJumpDistance:0} px jumps · AR {settings.ApproachRate} · CS {settings.CircleSize} · OD {settings.OverallDifficulty:0.##}. "
            + (settings.Sliders == TrainerSliderStyle.None ? "Circles keep the focus on tapping. " : $"Up to {settings.SliderBeats}-beat sliders follow held notes. ")
            + (settings.Spinners != TrainerSpinnerFrequency.None ? "Short spins in musical breaks. " : "")
            + (limits.EvidenceCount == 0 ? settings.MinimumStars is not null ? "Starting from your selected star range; completed runs guide the next session." : "Starting gently until you have a few completed runs." : $"Matched to {limits.EvidenceCount} recent plays; repeated struggles ease the next run."),
        _ => "Your selected difficulty stays fixed.",
    };
}

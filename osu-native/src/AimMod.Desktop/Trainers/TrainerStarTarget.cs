using osu.Game.Beatmaps;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Objects;

namespace AimMod.Desktop.Trainers;

public sealed record TrainerStarPlan(TrainerSettings Settings, Beatmap<OsuHitObject> Map, double Stars, bool InRange);

public static class TrainerStarTarget
{
    // The range handles use tenths of a star. Accept half a display step, while
    // retaining the full calculated rating on results rather than relabelling it.
    public const double Tolerance = .05;
    // A player's explicit goal supplies the cold-start envelope, not an estimated osu! rating.
    // Replay evidence, clean runs and repeated struggles still replace or reduce these limits.
    public static TrainerSkillLimits? StartingLimits(TrainerSettings settings)
    {
        if (!settings.AdaptiveDifficulty || settings.MinimumStars is not {} stars) return null;
        return new TrainerSkillLimits(MaxNps: Math.Clamp(2 + stars * 1.6, 1, 16),
            MaxJumpDistance: Math.Clamp(40 + stars * 55, 40, 350),
            MaxAimVelocity: Math.Clamp(150 + stars * 220, 100, 1800),
            MaxChain: stars < 3 ? 8 : 32, MaxBurst: stars < 3 ? 3 : stars < 5 ? 7 : 9,
            Complexity: stars < 2 ? 0 : stars < 4 ? 1 : 2,
            MaxApproachRate: (int)Math.Clamp(Math.Round(5 + stars * .6), 3, 9));
    }

    public static TrainerStarPlan RequireTarget(TrainerStarPlan plan)
    {
        if (plan.Settings.MinimumStars is not {} lo || plan.Settings.MaximumStars is not {} hi
            || plan.Settings.Kind is TrainerKind.Spinner or TrainerKind.Reaction || plan.InRange) return plan;
        string adjustment = plan.Settings.AdaptiveDifficulty
            ? "Try another song or drill, adjust the range, or turn off the star target to use Adaptive without a star goal."
            : "Try another song or drill, change the note speed, or turn off the star target.";
        throw new InvalidOperationException($"Practice has not started. This setup could not reach {lo:0.0}–{hi:0.0} stars. Closest result: {plan.Stars:0.00} stars. {adjustment}");
    }

    public static double Measure(Beatmap<OsuHitObject> map, TrainerSettings settings)
    {
        var working = new FlatWorkingBeatmap(map);
        return new OsuRuleset().CreateDifficultyCalculator(working).Calculate(TrainerReadingPatterns.Mods(settings)).StarRating;
    }

    // Called on the preparation worker, never when dragging a UI control.
    public static TrainerStarPlan Fit(TrainerSettings settings, Func<TrainerSettings, Beatmap<OsuHitObject>>? create = null)
    {
        settings.Validate();
        create ??= TrainerBeatmap.Create;
        if (settings.MinimumStars is null || settings.Kind is TrainerKind.Spinner or TrainerKind.Reaction)
            return new(settings with { MeasuredStars = null }, create(settings), 0, false);
        double lo = settings.MinimumStars ?? 0, hi = settings.MaximumStars ?? double.MaxValue;
        double distance(double stars) => stars < lo - Tolerance ? lo - Tolerance - stars : stars > hi + Tolerance ? stars - hi - Tolerance : 0;
        TrainerStarPlan make(TrainerSettings candidate)
        {
            if (candidate.AdaptiveDifficulty && candidate.SkillLimits is {} limits)
            {
                var parameters = TrainerDifficultyParameters.For(candidate, limits);
                candidate = candidate with { ApproachRate = Math.Min(limits.MaxApproachRate, parameters.ApproachRate), CircleSize = parameters.CircleSize };
            }
            var map = create(candidate);
            double stars = Measure(map, candidate);
            return new(candidate with { MeasuredStars = stars }, map, stars, distance(stars) == 0);
        }
        var best = make(settings);
        if (settings.MinimumStars is null || best.InRange || settings.Kind is TrainerKind.Spinner or TrainerKind.Reaction) return best;
        // Search every spacing/rate pair before other tempos. Manual timing and object choices stay fixed.
        var rates = settings.AdaptiveDifficulty || settings.NoteSpeed == TrainerNoteSpeed.Default ? new[] { settings.NoteSpeed, TrainerNoteSpeed.OnePerBeat, TrainerNoteSpeed.TwoPerBeat, TrainerNoteSpeed.FourPerBeat }.Distinct()
            : [settings.NoteSpeed];
        var tempos = settings.AdaptiveDifficulty && TrainerMusicCatalog.IsSong(settings.Music)
            ? new[] { settings.Bpm }.Concat(TrainerMusicCatalog.Tempos.OrderBy(b => Math.Abs(b-settings.Bpm))).Distinct()
            : [settings.Bpm];
        var sliders = settings.AdaptiveDifficulty && settings.Sliders != TrainerSliderStyle.SlidersOnly
            ? new[] { settings.Sliders, TrainerSliderStyle.None }.Distinct() : [settings.Sliders];
        var paths = settings.AdaptiveDifficulty && settings.Kind == TrainerKind.Steady
            ? new[] { settings.PathStyle, TrainerPathStyle.FigureEight, TrainerPathStyle.Zigzag, TrainerPathStyle.Arc }.Distinct()
            : [settings.PathStyle];
        int attempts = 0;
        foreach (int tempo in tempos)
        foreach (var path in paths)
        foreach (var slider in sliders)
        foreach (var rate in rates)
        foreach (int spacing in new[] { 70, 85, 100, 120, 140 }.OrderBy(s => Math.Abs(s-settings.AimSpacing)))
        {
            if (tempo == settings.Bpm && path == settings.PathStyle && slider == settings.Sliders && rate == settings.NoteSpeed && spacing == settings.AimSpacing) continue;
            if (++attempts > 600) return best;
            var candidate = make(settings with { Bpm = tempo, PathStyle = path, Sliders = slider, AimSpacing = spacing, NoteSpeed = rate });
            if (distance(candidate.Stars) < distance(best.Stars)) best = candidate;
            if (best.InRange) return best;
        }
        return best;
    }

    public static string Describe(TrainerSettings settings) => settings.MeasuredStars is {} actual
        ? $"Generated difficulty: {actual:0.00} stars" + (settings.MinimumStars is {} lo && settings.MaximumStars is {} hi
            ? $" · target {lo:0.0}–{hi:0.0}" + (actual < lo - Tolerance || actual > hi + Tolerance ? ". Outside the selected range." : ".") : ".")
        : "";
}

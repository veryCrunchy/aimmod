namespace AimMod.Desktop.Trainers;

public enum TrainerGuidedFocus { MovementComparison, Spacing, Endurance, GroupLength }

/// <summary>A frozen setup for a controlled series. Step changes only on explicit progression or comparison completion.</summary>
public sealed record TrainerGuidedPlan(Guid Id, TrainerGuidedFocus Focus, TrainerSettings Baseline,
    TrainerSettings Current, int Step = 0, bool Finished = false);

public sealed record TrainerGuidedRun(Guid PlanId, TrainerGuidedFocus Focus, int Step, string Condition,
    TrainerSettings Baseline);

public sealed record TrainerMovementComparison(int OriginalRuns, int CompactRuns, double? OriginalSpreadMs,
    double? CompactSpreadMs, double? OriginalAccuracy, double? CompactAccuracy, string Observation);

public sealed record TrainerProgressionAdvice(int MatchingRuns, TrainerSettings? Next, string Explanation);

public static class TrainerGuidedPractice
{
    public const int MinimumRuns = 3;
    public static TrainerGuidedPlan Create(TrainerSettings settings, TrainerGuidedFocus focus)
    {
        if (!Enum.IsDefined(focus) || settings.Kind == TrainerKind.Reaction)
            throw new ArgumentException("Choose an osu! exercise for guided practice.");
        if (focus == TrainerGuidedFocus.GroupLength && settings.Kind is not (TrainerKind.Bursts or TrainerKind.Alternating))
            throw new ArgumentException("Group length is available for bursts and streams.");
        if (focus == TrainerGuidedFocus.GroupLength && settings.RandomizePatterns)
            throw new ArgumentException("Choose a fixed pattern before building group length.");
        // Freeze the selected demand and seed, including skill limits. Stripping the limits from a
        // randomized drill would silently increase its note rate and chain length.
        settings = TrainerSkillProfile.Apply(settings, settings.SkillLimits ?? new());
        settings = settings with { MovementScale = 1,
            PatternSeed = settings.PatternSeed == 0 ? Random.Shared.Next(1, int.MaxValue) : settings.PatternSeed };
        if (focus == TrainerGuidedFocus.GroupLength)
        {
            if (settings.Kind == TrainerKind.Bursts && settings.Pattern == TrainerPattern.MixedBursts)
                settings = settings with { Pattern = TrainerPattern.Standard };
            if (settings.Kind == TrainerKind.Alternating && settings.Pattern == TrainerPattern.BuildUp)
                settings = settings with { Pattern = TrainerPattern.PartialStreams };
        }
        settings.Validate();
        return new(Guid.NewGuid(), focus, settings, settings);
    }

    public static TrainerSettings SettingsFor(TrainerGuidedPlan plan) => plan.Focus == TrainerGuidedFocus.MovementComparison
        ? plan.Baseline with { MovementScale = plan.Step % 2 == 0 ? 1 : .55 }
        : plan.Current;

    public static string Condition(TrainerGuidedPlan plan) => plan.Focus == TrainerGuidedFocus.MovementComparison
        ? plan.Step % 2 == 0 ? "Original movement" : "Compact movement"
        : plan.Focus switch
        {
            TrainerGuidedFocus.Spacing => $"Spacing {plan.Current.AimSpacing}%",
            TrainerGuidedFocus.Endurance => $"{plan.Current.Seconds} seconds",
            _ => TrainerPatterns.Choices(plan.Current.Kind).First(p => p.Value == plan.Current.Pattern).Key,
        };

    public static TrainerGuidedRun Stamp(TrainerGuidedPlan plan) =>
        new(plan.Id, plan.Focus, plan.Step, Condition(plan), plan.Baseline);

    public static bool IsUsable(TrainerResult r) => !r.Assisted && r.UsesOsuJudgements && r.Notes >= 12
        && r.CompletedAt <= DateTimeOffset.UtcNow
        && r.Hits >= 0 && r.Hits <= r.Notes && r.Accuracy is >= 0 and <= 100
        && r.SpreadMs is >= 0 && double.IsFinite(r.SpreadMs.Value)
        && r.PlayedSeconds >= r.Settings.Seconds * .9;

    public static TrainerMovementComparison CompareMovement(Guid planId, IEnumerable<TrainerResult> results)
    {
        var candidates = results.DistinctBy(r => r.Id).Where(r => IsUsable(r)
            && r.GuidedRun is { Focus: TrainerGuidedFocus.MovementComparison } g && g.PlanId == planId).ToArray();
        var baseline = candidates.FirstOrDefault()?.GuidedRun?.Baseline;
        // A tag alone is not evidence. Verify the actual settings and original seed too.
        var matching = candidates.Where(r => r.GuidedRun!.Baseline == baseline
            && r.Settings with { MovementScale = 1 } == baseline).ToArray();
        var original = matching.Where(r => r.Settings.MovementScale == 1).ToArray();
        var compact = matching.Where(r => r.Settings.MovementScale == .55).ToArray();
        double? spread(TrainerResult[] a) => a.Length == 0 ? null : median(a.Select(r => r.SpreadMs!.Value));
        double? accuracy(TrainerResult[] a) => a.Length == 0 ? null : median(a.Select(r => r.Accuracy!.Value));
        string observation = original.Length < MinimumRuns || compact.Length < MinimumRuns
            ? $"Complete {MinimumRuns} original and {MinimumRuns} compact runs before comparing. Skipped and stopped runs do not count."
            : "Compare the timing spread and accuracy below. These runs show how added movement affected this exercise; they do not identify a cause on their own.";
        if (original.Length >= MinimumRuns && compact.Length >= MinimumRuns
            && spread(original) > spread(compact) + 5 && accuracy(original) < accuracy(compact))
            observation = "Timing was less consistent with the original movement in these runs. Practise adding movement gradually, then check a familiar map. This does not prove that aim is the cause.";
        return new(original.Length, compact.Length, spread(original), spread(compact), accuracy(original), accuracy(compact), observation);
    }

    public static TrainerProgressionAdvice Advise(TrainerGuidedPlan plan, IEnumerable<TrainerResult> results)
    {
        var current = SettingsFor(plan);
        var matching = results.DistinctBy(r => r.Id).Where(r => IsUsable(r)
            && r.CompletedAt >= DateTimeOffset.UtcNow.AddDays(-30)
            && r.GuidedRun?.PlanId == plan.Id && r.GuidedRun.Step == plan.Step
            && r.Settings == current).OrderByDescending(r => r.CompletedAt).Take(MinimumRuns).ToArray();
        if (plan.Focus == TrainerGuidedFocus.MovementComparison)
            return new(matching.Length, null, "Keep the same setup for all six runs.");
        if (matching.Length < MinimumRuns)
            return new(matching.Length, null, $"Repeat this setup: {matching.Length}/{MinimumRuns} completed runs. Keep the song, tempo and controls the same.");
        // Conservative training heuristic, not a universal skill threshold. Never advance after one lucky score.
        bool clean = matching.All(r => r.Accuracy >= 95 && r.Hits >= r.Notes * .95 && r.SpreadMs <= 25);
        bool struggling = matching.Count(r => r.Accuracy < 90 || r.Hits < r.Notes * .9) >= 2;
        if (!clean && !struggling)
            return new(matching.Length, null, "Results still vary at this setup. Repeat it or take a break before adding more demand.");
        int direction = clean ? 1 : -1;
        var next = ChangeDemand(current, plan.Focus, direction);
        string change = plan.Focus == TrainerGuidedFocus.Spacing ? "spacing"
            : plan.Focus == TrainerGuidedFocus.Endurance ? "session length" : "group length";
        return new(matching.Length, next == current ? null : next, next == current
            ? "You have reached the end of this progression. Try these skills on a familiar map."
            : clean ? $"Three steady runs. You can increase {change} one step while keeping the rest of the setup fixed."
            : $"Two of the last three runs were difficult. Try less {change} with the same tempo and controls.");
    }

    public static TrainerSettings ChangeDemand(TrainerSettings current, TrainerGuidedFocus focus, int direction)
    {
        int next(int[] choices, int value) { int at = Array.IndexOf(choices, value); return at < 0 ? value : choices[Math.Clamp(at + Math.Sign(direction), 0, choices.Length - 1)]; }
        return focus switch
        {
            TrainerGuidedFocus.Spacing => current with { AimSpacing = next([70, 85, 100, 120, 140], current.AimSpacing) },
            TrainerGuidedFocus.Endurance => current with { Seconds = next([15, 30, 60, 120, 180], current.Seconds) },
            TrainerGuidedFocus.GroupLength when current.Kind == TrainerKind.Bursts => current with
            { Pattern = (TrainerPattern)next([(int)TrainerPattern.Standard, (int)TrainerPattern.FiveNotes, (int)TrainerPattern.SevenNotes, (int)TrainerPattern.NineNotes], (int)current.Pattern) },
            TrainerGuidedFocus.GroupLength when current.Kind == TrainerKind.Alternating => current with
            { Pattern = (TrainerPattern)next([(int)TrainerPattern.PartialStreams, (int)TrainerPattern.LongStreams, (int)TrainerPattern.Standard], (int)current.Pattern) },
            _ => current,
        };
    }

    private static double median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
    }
}

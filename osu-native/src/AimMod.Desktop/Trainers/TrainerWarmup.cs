namespace AimMod.Desktop.Trainers;

public sealed record TrainerWarmupRun(Guid SessionId, int Step);
public sealed record TrainerWarmupStep(string Title, string Hint, TrainerSettings Settings, double AccuracyReference);

/// <summary>A short preparation session, bounded by demonstrated demand rather than a progression test.</summary>
public sealed class TrainerWarmup
{
    public Guid Id { get; } = Guid.NewGuid();
    public IReadOnlyList<TrainerWarmupStep> Steps { get; }
    public List<TrainerResult> Results { get; } = [];
    public int Step => Results.Count;
    public bool Finished => Step == Steps.Count;
    public double Pace { get; private set; } = 1;
    public string Advice { get; private set; } = "Start gently. Take a moment between drills whenever you need it.";

    private TrainerWarmup(IReadOnlyList<TrainerWarmupStep> steps) => Steps = steps;

    public static TrainerWarmup Create(int minutes, TrainerSettings controls, IEnumerable<TrainerResult> history,
        IEnumerable<TrainerSkillEvidence> evidence, DateTimeOffset now)
    {
        if (minutes is not (2 or 4 or 8)) throw new ArgumentOutOfRangeException(nameof(minutes));
        var runs = history.Where(r => r.WarmupRun is null && !r.Assisted && r.UsesOsuJudgements
            && r.CompletedAt >= now.AddDays(-30) && r.CompletedAt <= now && r.Notes >= 12
            && r.Hits >= 0 && r.Hits <= r.Notes && r.Misses >= 0 && r.Misses <= r.Notes * .05
            && r.Accuracy is >= 80 and <= 100 && r.PlayedSeconds >= r.Settings.Seconds * .9)
            .DistinctBy(r => r.Id).OrderByDescending(r => r.CompletedAt).Take(200).ToArray();
        var replayEvidence = evidence.ToArray();
        TrainerKind[] kinds = [TrainerKind.Steady, TrainerKind.Aim, TrainerKind.Bursts, TrainerKind.Rhythm];
        string[] titles = ["Find your timing", "Loosen up your aim", "Wake up your fingers", "Put it together"];
        string[] hints = ["Keep the taps light and follow the song.", "Land comfortably before each tap.",
            "Keep each short group relaxed, then release.", "Keep your timing through the rhythm changes."];
        var steps = new List<TrainerWarmupStep>();
        var usedSongs = new HashSet<string>();
        for (int i = 0; i < kinds.Length; i++)
        {
            var kind = kinds[i];
            var matching = runs.Where(r => r.Settings.Kind == kind).Take(12).ToArray();
            var limits = TrainerSkillProfile.Build(kind, runs, replayEvidence, now);
            double accuracy = matching.Length >= 3 ? matching.Select(r => r.Accuracy!.Value).Order().ElementAt((matching.Length - 1) / 2) : 90;
            // Lower-accuracy players still have demonstrated practice demand. Do not require 95% to personalise a warmup.
            var demand = matching.Select(r => r.Demand).OfType<TrainerDemand>().Where(d => double.IsFinite(d.PeakNps)
                && d.PeakNps is >= 1 and <= 16 && double.IsFinite(d.JumpDistance) && d.JumpDistance is >= 40 and <= 350
                && double.IsFinite(d.AimVelocity) && d.AimVelocity is >= 100 and <= 1800).ToArray();
            if (demand.Length >= 3)
                limits = limits with { MaxNps = lower(demand.Select(d => d.PeakNps)),
                    MaxJumpDistance = lower(demand.Select(d => d.JumpDistance)), MaxAimVelocity = lower(demand.Select(d => d.AimVelocity)) };
            double fraction = .70 + i * .10;
            limits = limits with { MaxNps = Math.Max(1, limits.MaxNps * fraction),
                MaxJumpDistance = Math.Max(40, limits.MaxJumpDistance * fraction),
                MaxAimVelocity = Math.Max(100, limits.MaxAimVelocity * fraction), MaxBurst = 3, Complexity = 0 };
            // Music stays on its native tempo. Note density and movement carry the gradual increase.
            int bpm = TrainerMusicCatalog.Tempos.LastOrDefault(t => t <= limits.MaxNps * 30, 90);
            var songs = TrainerMusicCatalog.Songs.Keys.Where(s => !usedSongs.Contains(s)).ToArray();
            string song = songs[Random.Shared.Next(songs.Length)]; usedSongs.Add(song);
            var selected = new TrainerSettings(kind, bpm, minutes * 15, controls.OffsetMs, controls.Keys, song,
                RandomizePatterns: true, SkillLimits: limits, AimSpacing: 85, PatternSeed: Random.Shared.Next(1, int.MaxValue),
                ApproachRate: Math.Min(7, limits.MaxApproachRate), Sliders: kind == TrainerKind.Aim ? TrainerSliderStyle.Mixed : TrainerSliderStyle.None,
                GuidedCues: true);
            selected = TrainerSkillProfile.Apply(selected, limits);
            selected.Validate();
            steps.Add(new(titles[i], hints[i], selected, accuracy));
        }
        return new(steps);
    }

    private static double lower(IEnumerable<double> values) { var ordered = values.Order().ToArray(); return ordered[(ordered.Length - 1) / 4]; }

    public TrainerSettings CurrentSettings()
    {
        if (Finished) throw new InvalidOperationException("Warmup is complete.");
        var settings = Steps[Step].Settings;
        var limits = settings.SkillLimits!;
        return settings with { SkillLimits = limits with { MaxNps = Math.Max(1, limits.MaxNps * Pace),
            MaxJumpDistance = Math.Max(40, limits.MaxJumpDistance * Pace), MaxAimVelocity = Math.Max(100, limits.MaxAimVelocity * Pace) } };
    }

    public bool Record(TrainerResult result)
    {
        if (Finished || Results.Any(r => r.Id == result.Id) || result.Settings != CurrentSettings()
            || result.Assisted || !result.UsesOsuJudgements || result.Notes < 12 || result.Hits < 0 || result.Hits > result.Notes
            || result.Accuracy is not (>= 0 and <= 100) || result.Misses < 0 || result.CompletedAt > DateTimeOffset.UtcNow
            // The result clock excludes trailing musical rests. Stopped drills return null from the player.
            || result.PlayedSeconds is not {} seconds || !double.IsFinite(seconds) || seconds < result.Settings.Seconds * .8) return false;
        bool ease = result.Accuracy < Math.Max(75, Steps[Step].AccuracyReference - 4) || result.Misses > result.Notes * .05;
        if (ease) Pace = Math.Max(.6, Pace * .85);
        // A good warmup never raises demand beyond the original comfortable ceiling.
        Advice = ease ? "That was a rougher run. The next drill will ease off. Take a breather first if you need one."
            : "Keep the same relaxed feel in the next drill. Continue when you are ready.";
        Results.Add(result with { WarmupRun = new(Id, Step) });
        if (Finished) Advice = ease ? "Warmup complete. Start with a comfortable map and see how it feels."
            : "Warmup complete. Try a familiar map before going for a score.";
        return true;
    }

    public void Ease() { Pace = Math.Max(.6, Pace * .85); Advice = "The next drill is set to a gentler pace."; }

    public void SetControls(string keys, int offset)
    {
        if (Finished) return;
        var steps = (List<TrainerWarmupStep>)Steps;
        steps[Step] = steps[Step] with { Settings = steps[Step].Settings with { Keys = keys, OffsetMs = offset } };
    }
}

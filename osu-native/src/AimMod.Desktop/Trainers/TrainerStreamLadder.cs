namespace AimMod.Desktop.Trainers;

/// <summary>Marks one step of a stream ladder in trainer history.</summary>
/// <param name="SpreadLimitPercent">The ladder's spread limit as a share of the gap between notes. Zero on steps saved before limits were relative.</param>
public sealed record TrainerLadderRun(Guid SessionId, int Rung, int StartBpm, double SpreadLimitPercent, bool Clean);

/// <summary>
/// One ladder read back from history. <see cref="Complete"/> is false when the player finished before the ladder found
/// its answer, so it stays out of the trend.
/// </summary>
public sealed record TrainerLadderOutcome(Guid SessionId, DateTimeOffset CompletedAt, int StartBpm, double SpreadLimitPercent,
    int? HighestCleanBpm, double? SpreadAtHighestMs, double? SpreadAtStartMs, int Rungs, bool Complete, string Keys, int OffsetMs);

/// <summary>Where a new ladder's edge tempo and spread limit come from.</summary>
public sealed record TrainerLadderDefaults(int EdgeBpm, double SpreadLimitPercent, string Source);

/// <summary>
/// A fixed stream benchmark: full streams for 15 seconds a step. A clean first step climbs 10 BPM at a time until a
/// step is not clean; a first step that is not clean steps down 10 BPM at a time until one is. Either way the answer is
/// the highest clean tempo. Clean means no misses and a timing spread within a share of the gap between notes, so the
/// same evenness is asked of slow and fast streams. Adaptive difficulty and the layout never change, so ladders on
/// different days measure the same thing.
/// </summary>
public sealed class TrainerStreamLadder
{
    public const int RungSeconds = 15, BpmStep = 10, StartBelowEdge = 20, MinimumBpm = 60, MaximumBpm = 240, PatternSeed = 1;
    public const double DefaultSpreadLimitPercent = 15, MinimumSpreadLimitPercent = 8, MaximumSpreadLimitPercent = 30;

    public Guid Id { get; } = Guid.NewGuid();
    public int StartBpm { get; }
    public double SpreadLimitPercent { get; }
    public string Keys { get; private set; }
    public int OffsetMs { get; private set; }
    public List<TrainerResult> Results { get; } = [];
    public bool Finished { get; private set; }
    public bool ReachedTop { get; private set; }
    /// <summary>True once the first step was not clean and the ladder is looking for a slower clean step.</summary>
    public bool SteppingDown { get; private set; }
    public int? HighestCleanBpm { get; private set; }
    public string StopReason { get; private set; } = "";
    public int CurrentBpm { get; private set; }

    public TrainerStreamLadder(int edgeBpm, double spreadLimitPercent, string keys, int offsetMs)
    {
        if (edgeBpm is < MinimumBpm or > MaximumBpm) throw new ArgumentOutOfRangeException(nameof(edgeBpm));
        if (!double.IsFinite(spreadLimitPercent) || spreadLimitPercent is < MinimumSpreadLimitPercent or > MaximumSpreadLimitPercent)
            throw new ArgumentOutOfRangeException(nameof(spreadLimitPercent));
        StartBpm = CurrentBpm = StartFor(edgeBpm); SpreadLimitPercent = spreadLimitPercent; Keys = keys; OffsetMs = offsetMs;
        CurrentSettings().Validate();
    }

    public static int StartFor(int edgeBpm) => Math.Clamp(edgeBpm - StartBelowEdge, MinimumBpm, MaximumBpm);

    /// <summary>Milliseconds between stream notes, four to a beat.</summary>
    public static double NoteGapMs(int bpm) => 15000.0 / bpm;

    /// <summary>The spread a step at this tempo may have and still be clean.</summary>
    public static double SpreadLimitMs(int bpm, double percent) => NoteGapMs(bpm) * percent / 100;

    /// <summary>The exact drill for a step. Everything except tempo, keys and offset is fixed.</summary>
    public static TrainerSettings Rung(int bpm, string keys, int offsetMs) => new(Kind: TrainerKind.Alternating, Bpm: bpm, Seconds: RungSeconds,
        OffsetMs: offsetMs, Keys: keys, Music: "cues", Cue: "pulse", PatternSeed: PatternSeed, Pattern: TrainerPattern.Standard);

    public TrainerSettings CurrentSettings()
    {
        if (Finished) throw new InvalidOperationException("The ladder is complete.");
        return Rung(CurrentBpm, Keys, OffsetMs);
    }

    /// <summary>Keys and offset follow the player's osu! controls until the first step is played.</summary>
    public void SetControls(string keys, int offsetMs)
    {
        if (Results.Count > 0 || Finished) return;
        Rung(CurrentBpm, keys, offsetMs).Validate();
        Keys = keys; OffsetMs = offsetMs;
    }

    public static bool IsClean(TrainerResult result, double spreadLimitPercent) =>
        result.Misses == 0 && result.SpreadMs is { } spread && spread <= SpreadLimitMs(result.Settings.Bpm, spreadLimitPercent) + 1e-9;

    public bool Record(TrainerResult result)
    {
        if (Finished || Results.Any(r => r.Id == result.Id) || result.Settings != CurrentSettings()
            || !TrainerPerformance.IsCompleted(result, DateTimeOffset.UtcNow)) return false;
        int bpm = CurrentBpm;
        bool clean = IsClean(result, SpreadLimitPercent);
        bool first = Results.Count == 0;
        Results.Add(result with { LadderRun = new(Id, Results.Count, StartBpm, SpreadLimitPercent, clean) });
        if (clean)
        {
            HighestCleanBpm = bpm;
            if (SteppingDown) { Finished = true; StopReason = $"Clean at {bpm} BPM after stepping down from {StartBpm} BPM."; }
            else if (bpm + BpmStep > MaximumBpm) { Finished = ReachedTop = true; StopReason = $"Cleared {MaximumBpm} BPM, the top of the ladder."; }
            else CurrentBpm = bpm + BpmStep;
            return true;
        }
        if (first) SteppingDown = true;
        if (SteppingDown && bpm - BpmStep >= MinimumBpm) { CurrentBpm = bpm - BpmStep; return true; }
        Finished = true;
        StopReason = SteppingDown ? $"No clean step down to {MinimumBpm} BPM. {Describe(result, SpreadLimitPercent)}." : Describe(result, SpreadLimitPercent);
        return true;
    }

    public static string Describe(TrainerResult result, double spreadLimitPercent) =>
        result.Misses > 0 ? $"{result.Misses} {(result.Misses == 1 ? "miss" : "misses")} at {result.Settings.Bpm} BPM"
        : result.SpreadMs is { } spread
            ? $"Timing spread {spread:0.0} ms at {result.Settings.Bpm} BPM, above the {SpreadLimitMs(result.Settings.Bpm, spreadLimitPercent):0.0} ms limit"
        : $"No timed hits at {result.Settings.Bpm} BPM";

    /// <summary>Every ladder in history, newest first. Steps saved before limits were relative to the note gap are left out.</summary>
    public static IReadOnlyList<TrainerLadderOutcome> Outcomes(IEnumerable<TrainerResult> history) => history
        .Where(r => r.LadderRun is { SpreadLimitPercent: > 0 } && !r.Assisted).DistinctBy(r => r.Id)
        .GroupBy(r => r.LadderRun!.SessionId)
        .Select(group =>
        {
            var rungs = group.OrderBy(r => r.LadderRun!.Rung).ToArray();
            var first = rungs[0];
            var cleared = rungs.Where(r => r.LadderRun!.Clean).MaxBy(r => r.Settings.Bpm);
            var last = rungs[^1];
            int bpm = last.Settings.Bpm;
            bool complete = last.LadderRun!.Clean
                ? bpm + BpmStep > MaximumBpm || bpm < first.Settings.Bpm
                : cleared is not null || bpm - BpmStep < MinimumBpm;
            return new TrainerLadderOutcome(group.Key, rungs.Max(r => r.CompletedAt), first.LadderRun!.StartBpm, first.LadderRun.SpreadLimitPercent,
                cleared?.Settings.Bpm, cleared?.SpreadMs, first.SpreadMs, rungs.Length, complete, last.Settings.Keys, last.Settings.OffsetMs);
        })
        .OrderByDescending(o => o.CompletedAt).ToArray();

    /// <summary>
    /// A new ladder repeats the last one's start and limit so results stay comparable. Without one, the limit comes
    /// from how even recent clean stream runs were, and the edge from their tempo.
    /// </summary>
    public static TrainerLadderDefaults Defaults(IEnumerable<TrainerResult> history, TrainerSettings controls, DateTimeOffset now)
    {
        var runs = history.ToArray();
        if (Outcomes(runs).FirstOrDefault() is { } last)
            return new(Math.Clamp(last.StartBpm + StartBelowEdge, MinimumBpm, MaximumBpm), last.SpreadLimitPercent, "Same start and limit as your last ladder");
        var streams = runs.Where(r => r.Settings.Kind == TrainerKind.Alternating && r.LadderRun is null && r.WarmupRun is null
                && r.Settings.Music == "cues" && TrainerPerformance.IsCompleted(r, now) && r.CompletedAt >= now.AddDays(-30)
                && r.Misses <= r.Notes * .02 && r.SpreadMs is > 0)
            .OrderByDescending(r => r.CompletedAt).Take(12).ToArray();
        int fallbackEdge = Math.Clamp(controls.Kind == TrainerKind.Alternating && controls.Music == "cues" ? controls.Bpm : 150, 80, MaximumBpm);
        if (streams.Length < 3) return new(fallbackEdge, DefaultSpreadLimitPercent, "Starting values. Adjust them to suit you.");
        double limit = Math.Clamp(Math.Round(TrainerPerformance.Median(streams.Select(r => r.SpreadMs!.Value / NoteGapMs(r.Settings.Bpm) * 100)) * 1.25),
            MinimumSpreadLimitPercent, MaximumSpreadLimitPercent);
        int edge = Math.Clamp((int)Math.Round(TrainerPerformance.Median(streams.Select(r => (double)r.Settings.Bpm)) / BpmStep) * BpmStep, 80, MaximumBpm);
        return new(edge, limit, "From your recent stream runs");
    }
}

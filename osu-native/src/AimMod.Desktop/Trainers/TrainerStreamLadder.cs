namespace AimMod.Desktop.Trainers;

/// <summary>Marks one step of a stream ladder in trainer history.</summary>
public sealed record TrainerLadderRun(Guid SessionId, int Rung, int StartBpm, double SpreadLimitMs, bool Clean);

/// <summary>
/// One ladder read back from history. <see cref="Complete"/> is false when the player finished early, so its highest
/// clean tempo is only a lower bound and stays out of the trend.
/// </summary>
public sealed record TrainerLadderOutcome(Guid SessionId, DateTimeOffset CompletedAt, int StartBpm, double SpreadLimitMs,
    int? HighestCleanBpm, double? SpreadAtHighestMs, double? SpreadAtStartMs, int Rungs, bool Complete, string Keys, int OffsetMs);

/// <summary>Where a new ladder's edge tempo and spread limit come from.</summary>
public sealed record TrainerLadderDefaults(int EdgeBpm, double SpreadLimitMs, string Source);

/// <summary>
/// A fixed stream benchmark: full streams for 15 seconds a step, 10 BPM faster after each clean step, ending at the
/// first step with a miss or a timing spread above the limit. Adaptive difficulty and the layout never change, so
/// ladders on different days measure the same thing.
/// </summary>
public sealed class TrainerStreamLadder
{
    public const int RungSeconds = 15, BpmStep = 10, StartBelowEdge = 20, MinimumBpm = 60, MaximumBpm = 240, PatternSeed = 1;
    public const double DefaultSpreadLimitMs = 20, MinimumSpreadLimitMs = 8, MaximumSpreadLimitMs = 40;

    public Guid Id { get; } = Guid.NewGuid();
    public int StartBpm { get; }
    public double SpreadLimitMs { get; }
    public string Keys { get; private set; }
    public int OffsetMs { get; private set; }
    public List<TrainerResult> Results { get; } = [];
    public bool Finished { get; private set; }
    public bool ReachedTop { get; private set; }
    public int? HighestCleanBpm { get; private set; }
    public string StopReason { get; private set; } = "";
    public int CurrentBpm => StartBpm + BpmStep * Results.Count;

    public TrainerStreamLadder(int edgeBpm, double spreadLimitMs, string keys, int offsetMs)
    {
        if (edgeBpm is < MinimumBpm or > MaximumBpm) throw new ArgumentOutOfRangeException(nameof(edgeBpm));
        if (!double.IsFinite(spreadLimitMs) || spreadLimitMs is < MinimumSpreadLimitMs or > MaximumSpreadLimitMs)
            throw new ArgumentOutOfRangeException(nameof(spreadLimitMs));
        StartBpm = StartFor(edgeBpm); SpreadLimitMs = spreadLimitMs; Keys = keys; OffsetMs = offsetMs;
        CurrentSettings().Validate();
    }

    public static int StartFor(int edgeBpm) => Math.Clamp(edgeBpm - StartBelowEdge, MinimumBpm, MaximumBpm);

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

    public static bool IsClean(TrainerResult result, double spreadLimitMs) =>
        result.Misses == 0 && result.SpreadMs is { } spread && spread <= spreadLimitMs + 1e-9;

    public bool Record(TrainerResult result)
    {
        if (Finished || Results.Any(r => r.Id == result.Id) || result.Settings != CurrentSettings()
            || !TrainerPerformance.IsCompleted(result, DateTimeOffset.UtcNow)) return false;
        int bpm = CurrentBpm;
        bool clean = IsClean(result, SpreadLimitMs);
        Results.Add(result with { LadderRun = new(Id, Results.Count, StartBpm, SpreadLimitMs, clean) });
        if (clean)
        {
            HighestCleanBpm = bpm;
            if (bpm + BpmStep > MaximumBpm) { Finished = ReachedTop = true; StopReason = $"Cleared {MaximumBpm} BPM, the top of the ladder."; }
            return true;
        }
        Finished = true;
        StopReason = Describe(result, SpreadLimitMs);
        return true;
    }

    public static string Describe(TrainerResult result, double spreadLimitMs) =>
        result.Misses > 0 ? $"{result.Misses} {(result.Misses == 1 ? "miss" : "misses")} at {result.Settings.Bpm} BPM"
        : result.SpreadMs is { } spread ? $"Timing spread {spread:0.0} ms at {result.Settings.Bpm} BPM, above the {spreadLimitMs:0} ms limit"
        : $"No timed hits at {result.Settings.Bpm} BPM";

    /// <summary>Every ladder in history, newest first.</summary>
    public static IReadOnlyList<TrainerLadderOutcome> Outcomes(IEnumerable<TrainerResult> history) => history
        .Where(r => r.LadderRun is not null && !r.Assisted).DistinctBy(r => r.Id)
        .GroupBy(r => r.LadderRun!.SessionId)
        .Select(group =>
        {
            var rungs = group.OrderBy(r => r.LadderRun!.Rung).ToArray();
            var first = rungs[0].LadderRun!;
            var cleared = rungs.Where(r => r.LadderRun!.Clean).MaxBy(r => r.Settings.Bpm);
            var last = rungs[^1];
            bool complete = !last.LadderRun!.Clean || last.Settings.Bpm + BpmStep > MaximumBpm;
            return new TrainerLadderOutcome(group.Key, rungs.Max(r => r.CompletedAt), first.StartBpm, first.SpreadLimitMs,
                cleared?.Settings.Bpm, cleared?.SpreadMs, rungs[0].SpreadMs, rungs.Length, complete, last.Settings.Keys, last.Settings.OffsetMs);
        })
        .OrderByDescending(o => o.CompletedAt).ToArray();

    /// <summary>
    /// A new ladder repeats the last one's start and limit so results stay comparable. Without one, the limit comes
    /// from recent clean stream runs and the edge from their tempo.
    /// </summary>
    public static TrainerLadderDefaults Defaults(IEnumerable<TrainerResult> history, TrainerSettings controls, DateTimeOffset now)
    {
        var runs = history.ToArray();
        if (Outcomes(runs).FirstOrDefault() is { } last)
            return new(Math.Clamp(last.StartBpm + StartBelowEdge, MinimumBpm, MaximumBpm), last.SpreadLimitMs, "Same start and limit as your last ladder");
        var streams = runs.Where(r => r.Settings.Kind == TrainerKind.Alternating && r.LadderRun is null && r.WarmupRun is null
                && TrainerPerformance.IsCompleted(r, now) && r.CompletedAt >= now.AddDays(-30)
                && r.Misses <= r.Notes * .02 && r.SpreadMs is > 0)
            .OrderByDescending(r => r.CompletedAt).Take(12).ToArray();
        int fallbackEdge = Math.Clamp(controls.Kind == TrainerKind.Alternating && controls.Music == "cues" ? controls.Bpm : 150, 80, MaximumBpm);
        if (streams.Length < 3) return new(fallbackEdge, DefaultSpreadLimitMs, "Starting values. Adjust them to suit you.");
        double limit = Math.Clamp(Math.Round(TrainerPerformance.Median(streams.Select(r => r.SpreadMs!.Value)) * 1.25), MinimumSpreadLimitMs, MaximumSpreadLimitMs);
        var cued = streams.Where(r => r.Settings.Music == "cues").ToArray();
        int edge = cued.Length == 0 ? fallbackEdge
            : Math.Clamp((int)Math.Round(TrainerPerformance.Median(cued.Select(r => (double)r.Settings.Bpm)) / BpmStep) * BpmStep, 80, MaximumBpm);
        return new(edge, limit, "From your recent stream runs");
    }
}

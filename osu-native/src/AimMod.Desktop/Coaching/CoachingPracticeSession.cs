using System.Text.Json;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.Practice;

namespace AimMod.Desktop.Coaching;

public enum CoachingPracticeStage { Baseline, Isolate, Combine, Original, Transfer, Retention }

public sealed record CoachingPracticeStageProgress(CoachingPracticeStage Stage, string Label, string Detail,
    int Completed, int Required, bool Skipped)
{
    public bool IsComplete => Completed >= Required;
}

public sealed record CoachingPracticeCohort(string Label, string GroupId, PracticeBreakdownVariant Variant,
    string Setup, bool Assisted, int Attempts, int Completed, int Failed, double? MedianAccuracy, double? MedianMisses);

public sealed record CoachingPracticeComparison(string Label, string Detail, int FirstCount, int SecondCount,
    double? AccuracyDifference, double? MissDifference);

public sealed record CoachingPracticeReview(IReadOnlyList<CoachingPracticeStageProgress> Stages,
    CoachingPracticeStage? NextStage, string NextAction, IReadOnlyList<CoachingPracticeComparison> Comparisons,
    IReadOnlyList<CoachingPracticeCohort> Cohorts);

public sealed record CoachingPracticeCheck(CoachingPracticeStage Stage, DateTimeOffset StartedAt,
    PracticeTracking Target, string Title, string Difficulty, PracticeDrillType Skill, IReadOnlyList<PracticeAttempt> Attempts);

public sealed record CoachingPracticeSession(string PracticeSetId, DateTimeOffset StartedAt,
    IReadOnlyList<CoachingPracticeStage> Skipped, IReadOnlyList<CoachingPracticeCheck> Checks);

/// <summary>
/// Describes observed practice, not a causal diagnosis. Full-map checks and section results stay separate;
/// a skipped step never becomes a successful result. Three repeats are a reporting minimum, not a skill threshold.
/// </summary>
public static class CoachingPracticeSessionPlanner
{
    public static readonly TimeSpan SessionBreak = TimeSpan.FromMinutes(30);
    public static CoachingPracticeSession Start(PracticeSetProgress set, DateTimeOffset now) => new(set.Map.Id, now, [], []);

    public static CoachingPracticeSession Skip(CoachingPracticeSession session, CoachingPracticeStage stage) =>
        session with { Skipped = session.Skipped.Append(stage).Distinct().ToArray() };

    public static CoachingPracticeSession Restore(CoachingPracticeSession session, CoachingPracticeStage stage) =>
        session with { Skipped = session.Skipped.Where(s => s != stage).ToArray() };

    /// <summary>Explicitly binds a check to one selected map, player, scoring system and complete mod configuration.</summary>
    public static CoachingPracticeSession StartCheck(CoachingPracticeSession session, PracticeSetProgress set,
        CoachingPracticeStage stage, LocalReplay target, IEnumerable<LocalReplay> history, DateTimeOffset now,
        IEnumerable<DateTimeOffset>? otherPracticeTimes = null)
    {
        if (session.PracticeSetId != set.Map.Id || set.Map.Tracking is not { } source)
            throw new ArgumentException("Select a tracked practice set first.");
        if (stage is not (CoachingPracticeStage.Transfer or CoachingPracticeStage.Retention))
            throw new ArgumentOutOfRangeException(nameof(stage));
        if (!valid(target, now) || !PracticeProgressTracker.SamePlayer(target, source.Player))
            throw new ArgumentException("Choose an unassisted osu! play by the same player.");
        if (target.BeatmapHash.Length == 0 && target.OnlineBeatmapId <= 0 && target.BeatmapId == Guid.Empty)
            throw new ArgumentException("This play has no map identity. Choose another play.");
        bool sameMap = (source.OnlineBeatmapId > 0 && target.OnlineBeatmapId == source.OnlineBeatmapId)
            || (source.SourceHash.Length > 0 && target.BeatmapHash.Equals(source.SourceHash, StringComparison.OrdinalIgnoreCase))
            || (source.SourceBeatmapId != Guid.Empty && target.BeatmapId == source.SourceBeatmapId);
        var practice = clean(set.Progress.Attempts, now).Where(a => !a.Original).ToArray();
        var runs = history.Where(r => valid(r, now) && PracticeProgressTracker.SamePlayer(r, source.Player)).ToArray();
        if (stage == CoachingPracticeStage.Transfer && sameMap)
            throw new ArgumentException("Choose a different map with the same kind of pattern for the transfer check.");
        if (stage == CoachingPracticeStage.Retention)
        {
            if (!sameMap || !PracticeProgressTracker.SameSource(target, source))
                throw new ArgumentException("Use the original map with its original mods and scoring setup.");
            DateTimeOffset? lastActivity = runs.Select(r => (DateTimeOffset?)r.PlayedAt)
                .Concat(set.Progress.Attempts.Select(a => (DateTimeOffset?)a.PlayedAt))
                .Concat((otherPracticeTimes ?? []).Where(t => t <= now).Select(t => (DateTimeOffset?)t)).Max();
            if (practice.Length == 0 || lastActivity is null || now - lastActivity < SessionBreak)
                throw new ArgumentException("Start this check next session, before warming up or repeating the drill. Leave at least 30 minutes between sessions.");
        }
        var tracking = new PracticeTracking(source.Player, source.AccountId, target.OnlineBeatmapId,
            target.BeatmapHash, target.BeatmapId, ScoreMods.Configuration(target), PracticeProgressTracker.Stable(target), [], []);
        // A baseline taken after this practice began cannot establish transfer from that practice.
        var baseline = PracticeProgressTracker.Baseline(runs, tracking, set.Map.CreatedAt);
        tracking = tracking with { Baseline = baseline };
        var check = new CoachingPracticeCheck(stage, now, tracking, target.Title, target.Difficulty, set.Map.Scenario, []);
        return session with { Checks = session.Checks.Where(c => c.Stage != stage).Append(check).ToArray(),
            Skipped = session.Skipped.Where(s => s != stage).ToArray() };
    }

    public static CoachingPracticeSession Reconcile(CoachingPracticeSession session, PracticeSetProgress set,
        IEnumerable<LocalReplay> history, int accountId, DateTimeOffset now, IEnumerable<DateTimeOffset>? otherPracticeTimes = null)
    {
        if (session.PracticeSetId != set.Map.Id || set.Map.Tracking is not { } source
            || source.AccountId > 0 && source.AccountId != accountId) return session;
        var runs = history.Where(r => valid(r, now)).ToArray();
        var firstPractice = clean(set.Progress.Attempts, now).Where(a => !a.Original).OrderBy(a => a.PlayedAt).FirstOrDefault();
        var checks = session.Checks.Select(check =>
        {
            if (check.Skill != set.Map.Scenario || check.Target.AccountId > 0 && check.Target.AccountId != accountId) return check;
            // Next-session evidence must precede any resumed drill. The explicit start alone never completes a check.
            DateTimeOffset cutoff = check.Stage == CoachingPracticeStage.Retention
                ? set.Progress.Attempts.Where(a => !a.Original && a.PlayedAt > check.StartedAt)
                    .Select(a => a.PlayedAt)
                    .Concat(runs.Where(r => r.PlayedAt > check.StartedAt && PracticeProgressTracker.SamePlayer(r, source.Player)
                        && !PracticeProgressTracker.SameSource(r, check.Target)).Select(r => r.PlayedAt))
                    .Concat((otherPracticeTimes ?? []).Where(t => t > check.StartedAt && t <= now))
                    .DefaultIfEmpty(DateTimeOffset.MaxValue).Min()
                : DateTimeOffset.MaxValue;
            var fresh = runs.Where(r => r.PlayedAt > check.StartedAt && r.PlayedAt < cutoff
                && (check.Stage != CoachingPracticeStage.Transfer || firstPractice is not null && r.PlayedAt > firstPractice.PlayedAt)
                && PracticeProgressTracker.SamePlayer(r, check.Target.Player) && PracticeProgressTracker.SameSource(r, check.Target))
                .Select(r => PracticeProgressTracker.Snapshot(r, check.Difficulty, true));
            return check with { Attempts = clean(check.Attempts.Concat(fresh), now)
                .Where(a => a.PlayedAt > check.StartedAt && a.PlayedAt < cutoff).ToArray() };
        }).ToArray();
        return session with { Checks = checks };
    }

    public static CoachingPracticeReview Build(PracticeSetProgress set, DateTimeOffset now, CoachingPracticeSession? session = null, string? breakdownGroupId = null)
    {
        if (session?.PracticeSetId != set.Map.Id) session = null;
        var attempts = clean(set.Progress.Attempts, now).ToArray();
        var practice = attempts.Where(a => !a.Original).ToArray();
        DateTimeOffset firstPractice = practice.Select(a => a.PlayedAt).DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
        var baseline = clean((set.Map.Tracking?.Baseline ?? []).Concat(attempts.Where(a => a.Original && a.PlayedAt < firstPractice)), now)
            .Where(a => !a.Assisted && a.Passed).TakeLast(5).ToArray();
        var original = attempts.Where(a => a.Original && !a.Assisted && a.PlayedAt > firstPractice).ToArray();
        var groups = practice.GroupBy(a => (a.BreakdownGroupId, a.BreakdownVariant, a.Setup, a.Assisted,
            a.Difficulty, a.SourceStartMs, a.SourceEndMs)).ToArray();
        var cohorts = groups.Select(g => new CoachingPracticeCohort(g.Key.Difficulty, g.Key.BreakdownGroupId,
            g.Key.BreakdownVariant, g.Key.Setup, g.Key.Assisted, g.Count(), g.Count(a => a.Passed), g.Count(a => !a.Passed),
            median(g.Where(a => a.Passed).Select(a => a.Accuracy)), median(g.Where(a => a.Passed).Select(a => (double)a.Misses)))).ToArray();
        var comparisons = new List<CoachingPracticeComparison>();
        foreach (var compact in groups.Where(g => g.Key.BreakdownVariant == PracticeBreakdownVariant.ReducedMovement && !g.Key.Assisted && g.Key.BreakdownGroupId.Length > 0))
        {
            var full = groups.FirstOrDefault(g => g.Key.BreakdownVariant == PracticeBreakdownVariant.Original && !g.Key.Assisted
                && g.Key.BreakdownGroupId == compact.Key.BreakdownGroupId && g.Key.Setup == compact.Key.Setup
                && g.Key.SourceStartMs == compact.Key.SourceStartMs && g.Key.SourceEndMs == compact.Key.SourceEndMs);
            comparisons.Add(compare("Movement and timing", compact, full?.AsEnumerable() ?? [],
                "Reduced movement", "Original section", "A difference is a reason to test coordination. Reading, familiarity and attempt order can also affect the result."));
        }
        if (comparisons.Count == 0)
            comparisons.Add(new("Movement and timing", "Play the reduced-movement and original-section difficulties three times each with matching mods. Assisted aim results stay separate.", 0, 0, null, null));
        comparisons.Add(compare("Original map", baseline, original, "Before practice", "After practice",
            "These are full-map results. They do not measure only the practised section or prove what caused a change."));
        var checks = session?.Checks ?? [];
        foreach (var check in checks)
            comparisons.Add(compare(check.Stage == CoachingPracticeStage.Transfer ? "Different-map check" : "Next-session check",
                check.Target.Baseline, clean(check.Attempts, now), "Before practice", check.Title,
                check.Target.Baseline.Count == 0
                    ? "There is no earlier baseline for this map. This records performance on the check; improvement is still unknown."
                    : "Compare only this map and setup. A better result on one check does not establish broader skill improvement."));

        // Follow one source section. Work done on unrelated sections must not finish this section's stages.
        string selectedGroup = breakdownGroupId ?? practice.Where(a => a.BreakdownGroupId.Length > 0)
            .OrderByDescending(a => a.PlayedAt).Select(a => a.BreakdownGroupId).FirstOrDefault()
            ?? set.Map.Tracking?.Difficulties.FirstOrDefault(d => d.BreakdownGroupId.Length > 0)?.BreakdownGroupId ?? "";
        // Count repeats within one source section and setup, never add unrelated conditions together.
        int repetitions(PracticeBreakdownVariant variant, bool assisted = false) => cohorts
            .Where(c => c.GroupId == selectedGroup && c.Variant == variant && c.Assisted == assisted).Select(c => c.Completed).DefaultIfEmpty(0).Max();
        int isolated = repetitions(PracticeBreakdownVariant.ReducedMovement);
        int combined = repetitions(PracticeBreakdownVariant.CombinedEasier);
        bool hasBreakdown = set.Map.Tracking?.Difficulties.Any(d => d.BreakdownGroupId.Length > 0) == true;
        CoachingPracticeStageProgress stage(CoachingPracticeStage id, string label, string detail, int completed, int required) =>
            new(id, label, detail, completed, required, session?.Skipped.Contains(id) == true);
        var stages = new[]
        {
            stage(CoachingPracticeStage.Baseline, "Check your starting point", practice.Length > 0 && baseline.Length < 3
                ? "Practice has already started, so an earlier starting point cannot be filled in now. Continue the exercises; new full-map plays will count as retests."
                : "Complete three original-map plays with the same setup. Keep unfinished attempts in the record.", baseline.Length, 3),
            stage(CoachingPracticeStage.Isolate, "Separate the workload", hasBreakdown
                ? "Repeat reduced movement three times. Try aim focus with Relax separately; its accuracy is not a tapping result."
                : "Generate a section breakdown to practise its rhythm and movement separately.", isolated, 3),
            stage(CoachingPracticeStage.Combine, "Put it together", "Repeat the easier combined section twice. Keep the same setup while you compare attempts.", combined, 2),
            stage(CoachingPracticeStage.Original, "Return to your map", "Complete three original-map retests after practice. Compare them with your full-map baseline.", original.Count(a => a.Passed), 3),
            stage(CoachingPracticeStage.Transfer, "Try another map", checks.Any(c => c.Stage == CoachingPracticeStage.Transfer)
                ? "Play the selected comparison map with its saved setup. Its results stay separate from your original map."
                : "Select a different map with the same kind of pattern. Keep its results separate from the practised map.",
                checks.Where(c => c.Stage == CoachingPracticeStage.Transfer).SelectMany(c => clean(c.Attempts, now)).Count(a => a.Passed), 3),
            stage(CoachingPracticeStage.Retention, "Check next session", "Return after a break and start this check before repeating the drill. Use the original map and setup.",
                checks.Where(c => c.Stage == CoachingPracticeStage.Retention).SelectMany(c => clean(c.Attempts, now)).Count(a => a.Passed), 1)
        };
        // Later plays cannot repair a missing pre-practice baseline. Keep it incomplete,
        // but do not send the player back to a step that can no longer collect evidence.
        var next = stages.FirstOrDefault(s => s.Stage < CoachingPracticeStage.Transfer && !s.IsComplete && !s.Skipped
            && (s.Stage != CoachingPracticeStage.Baseline || practice.Length == 0));
        return new(stages, next?.Stage, next?.Detail ?? "Review the separate results for the drill, original map and checks. Repeat a check later to see if the result holds.", comparisons, cohorts);
    }

    private static CoachingPracticeComparison compare(string label, IEnumerable<PracticeAttempt> first, IEnumerable<PracticeAttempt> second,
        string firstLabel, string secondLabel, string caveat)
    {
        var a = first.Where(x => x.Passed && !x.Assisted).TakeLast(5).ToArray();
        var b = second.Where(x => x.Passed && !x.Assisted).TakeLast(5).ToArray();
        var common = a.Select(x => x.Setup).Intersect(b.Select(x => x.Setup), StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        if (a.Length > 0 && b.Length > 0 && common.Count == 0)
            return new(label, $"{firstLabel}: {a.Length} completed / {secondLabel}: {b.Length} completed. These plays use different settings and cannot be compared. {caveat}", a.Length, b.Length, null, null);
        if (common.Count > 0)
        {
            string selectedSetup = b.Last(x => common.Contains(x.Setup)).Setup;
            a = a.Where(x => x.Setup == selectedSetup).ToArray();
            b = b.Where(x => x.Setup == selectedSetup).ToArray();
        }
        if (a.Length < 3 || b.Length < 3)
            return new(label, $"{firstLabel}: {a.Length} completed / {secondLabel}: {b.Length} completed. Three matching completed plays per condition are needed for a comparison. {caveat}", a.Length, b.Length, null, null);
        double aa = median(a.Select(x => x.Accuracy))!.Value, ba = median(b.Select(x => x.Accuracy))!.Value;
        double am = median(a.Select(x => (double)x.Misses))!.Value, bm = median(b.Select(x => (double)x.Misses))!.Value;
        return new(label, $"{firstLabel}: {aa:P2}, {am:0.#} misses ({a.Length} plays). {secondLabel}: {ba:P2}, {bm:0.#} misses ({b.Length} plays). {caveat}",
            a.Length, b.Length, ba - aa, bm - am);
    }

    private static bool valid(LocalReplay r, DateTimeOffset now) => r.RulesetShortName == "osu" && ScoreMods.IsManualPlay(r)
        && r.Player.Length > 0 && double.IsFinite(r.Accuracy) && r.Accuracy is >= 0 and <= 1 && r.MissCount >= 0 && r.PlayedAt <= now;

    private static IEnumerable<PracticeAttempt> clean(IEnumerable<PracticeAttempt> attempts, DateTimeOffset now) => attempts
        .Where(a => double.IsFinite(a.Accuracy) && a.Accuracy is >= 0 and <= 1 && a.Misses >= 0 && a.PlayedAt <= now)
        .GroupBy(a => a.OnlineScoreId > 0 ? "online:" + a.OnlineScoreId : "local:" + a.ScoreId)
        .Select(g => g.First()).OrderBy(a => a.PlayedAt);

    private static double? median(IEnumerable<double> values)
    {
        var items = values.Order().ToArray();
        return items.Length == 0 ? null : (items[(items.Length - 1) / 2] + items[items.Length / 2]) / 2;
    }
}

public sealed class CoachingPracticeSessionStore(string path)
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string file = Path.GetFullPath(path);
    private object Gate => gates.GetOrAdd(file, _ => new());

    public CoachingPracticeSession? Load(string practiceSetId)
    {
        lock (Gate) return read().FirstOrDefault(s => s.PracticeSetId == practiceSetId);
    }

    public void Save(CoachingPracticeSession session)
    {
        lock (Gate)
        {
            var sessions = read().Where(s => s.PracticeSetId != session.PracticeSetId).Append(session)
                .OrderByDescending(s => s.StartedAt).Take(100).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(sessions));
            File.Move(file + ".tmp", file, true);
        }
    }

    private IReadOnlyList<CoachingPracticeSession> read()
    {
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length > 4_000_000) return [];
            return (JsonSerializer.Deserialize<CoachingPracticeSession[]>(File.ReadAllText(file)) ?? [])
                .Where(s => s is not null && !string.IsNullOrWhiteSpace(s.PracticeSetId) && s.Skipped is not null && s.Checks is not null
                    && s.Skipped.All(Enum.IsDefined) && s.Checks.All(c => c is not null && c.Target is not null
                        && c.Target.Baseline is not null && c.Attempts is not null && c.Stage is CoachingPracticeStage.Transfer or CoachingPracticeStage.Retention)).ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }
}

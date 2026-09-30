using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime.Contracts;

namespace AimMod.Desktop.Coaching;

public enum CoachingIssueFamily { Aim, Timing, NoPress, Tapping }

public enum CoachingIssueKind
{
    Overshoot,
    Undershoot,
    OffTarget,
    EarlyPress,
    LatePress,
    NoPress,
    Rushing,
    Dragging,
    UnevenTaps,
    EarlyStart,
    LateStart,
}

/// <summary>The most frequent replay observation on a difficulty, with where it usually happens.</summary>
/// <remarks>
/// <see cref="TimeMs"/> to <see cref="EndMs"/> is the section where this issue clusters, <see cref="FirstObjectIndex"/> its first
/// object, and <see cref="PerPlay"/> the misses per analysed play in that section (or the share of plays, for tapping drift).
/// </remarks>
public sealed record CoachingIssue(CoachingIssueKind Kind, int Evidence, int Plays, double TimeMs, double EndMs = 0,
    int FirstObjectIndex = -1, double PerPlay = 0);

/// <summary>One beatmap difficulty summarised across every eligible attempt in scope.</summary>
public sealed record CoachingMapCandidate(
    string Key,
    LocalReplay Latest,
    LocalReplay Target,
    int Attempts,
    int AnalysedAttempts,
    double BestAccuracy,
    double TypicalAccuracy,
    double TypicalMisses,
    int BestMisses,
    double? BestPp,
    double? TypicalPp,
    CoachingIssue? TopIssue,
    double Concentration,
    double Potential)
{
    public double AccuracyGap => Math.Max(0, BestAccuracy - TypicalAccuracy);
}

public sealed record CoachingSkillLine(CoachingIssueFamily Family, double Share, double PerPlay, double? Change);

/// <summary>Where analysed misses come from, with the change between the older and newer half of analysed plays.</summary>
public sealed record CoachingSkillSummary(
    int AnalysedPlays,
    IReadOnlyList<CoachingSkillLine> Lines,
    double? MeanOffsetMs,
    double? SpreadMs,
    double? DriftShare,
    double? DriftChange)
{
    public static CoachingSkillSummary Empty { get; } = new(0, [], null, null, null, null);
}

public sealed record CoachingMapRanking(IReadOnlyList<CoachingMapCandidate> Maps, CoachingSkillSummary Skills)
{
    public static CoachingMapRanking Empty { get; } = new([], CoachingSkillSummary.Empty);
}

/// <summary>Replay evidence helpers shared by the map ranking, the timeline and the section cards.</summary>
public static class CoachingIssues
{
    public static CoachingIssueFamily Family(CoachingIssueKind kind) => kind switch
    {
        CoachingIssueKind.Overshoot or CoachingIssueKind.Undershoot or CoachingIssueKind.OffTarget => CoachingIssueFamily.Aim,
        CoachingIssueKind.EarlyPress or CoachingIssueKind.LatePress => CoachingIssueFamily.Timing,
        CoachingIssueKind.NoPress => CoachingIssueFamily.NoPress,
        _ => CoachingIssueFamily.Tapping,
    };

    public static string Label(CoachingIssueKind kind) => kind switch
    {
        CoachingIssueKind.Overshoot => "Overshooting jumps",
        CoachingIssueKind.Undershoot => "Undershooting jumps",
        CoachingIssueKind.OffTarget => "Off-target presses",
        CoachingIssueKind.EarlyPress => "Early presses",
        CoachingIssueKind.LatePress => "Late presses",
        CoachingIssueKind.NoPress => "Missed presses",
        CoachingIssueKind.Rushing => "Rushing streams",
        CoachingIssueKind.Dragging => "Dragging streams",
        CoachingIssueKind.EarlyStart => "Early stream starts",
        CoachingIssueKind.LateStart => "Late stream starts",
        _ => "Uneven tapping",
    };

    public static string FamilyLabel(CoachingIssueFamily family) => family switch
    {
        CoachingIssueFamily.Aim => "Aim",
        CoachingIssueFamily.Timing => "Press timing",
        CoachingIssueFamily.NoPress => "Missed presses",
        _ => "Stream control",
    };

    /// <summary>Same evidence threshold as the replay observations: confident, top-level misses only.</summary>
    public static bool IsEvidenceMiss(ReplayObjectJudgement j) => ReplayJudgementClassifier.IsMiss(j) && j.ObjectIndex is >= 0
        && string.IsNullOrEmpty(j.NestedPath) && double.IsFinite(j.StartTimeMs) && j.StartTimeMs >= 0
        && j.MissAnalysis is { Confidence: >= .7, HitRadius: > 0 } m && double.IsFinite(m.HitRadius);

    public static CoachingIssueKind? FromMiss(ReplayObjectJudgement j)
    {
        if (!IsEvidenceMiss(j)) return null;
        var m = j.MissAnalysis!;
        if (m.DistanceAtPress is { } distance && double.IsFinite(distance) && distance > m.HitRadius)
            return m.Reason switch
            {
                ReplayMissReason.Overshoot => CoachingIssueKind.Overshoot,
                ReplayMissReason.Undershoot => CoachingIssueKind.Undershoot,
                _ => CoachingIssueKind.OffTarget,
            };
        if (m.Reason is ReplayMissReason.EarlyClick or ReplayMissReason.LateClick && m.PressTimeOffsetMs is { } offset && double.IsFinite(offset))
            return m.Reason == ReplayMissReason.EarlyClick ? CoachingIssueKind.EarlyPress : CoachingIssueKind.LatePress;
        return m.Reason == ReplayMissReason.OnTargetNoClick ? CoachingIssueKind.NoPress : null;
    }

    public static CoachingIssueKind FromLesson(TappingLesson lesson) => lesson.Pattern switch
    {
        _ when lesson.Pattern.StartsWith("Rushing", StringComparison.Ordinal) => CoachingIssueKind.Rushing,
        _ when lesson.Pattern.StartsWith("Falling behind", StringComparison.Ordinal) => CoachingIssueKind.Dragging,
        _ when lesson.Pattern.StartsWith("Even taps, early", StringComparison.Ordinal) => CoachingIssueKind.EarlyStart,
        _ when lesson.Pattern.StartsWith("Even taps, late", StringComparison.Ordinal) => CoachingIssueKind.LateStart,
        _ => CoachingIssueKind.UnevenTaps,
    };

    internal static bool Current(ReplayAnalysisResult? analysis) => analysis is not null && analysis.EngineVersion == ReplayAnalysisProtocol.EngineVersion;

    /// <summary>Groups attempts on the same file and player, independent of mods.</summary>
    public static string DifficultyKey(LocalReplay run)
    {
        string map = !string.IsNullOrEmpty(run.BeatmapHash) ? "h:" + run.BeatmapHash.ToLowerInvariant()
            : run.OnlineBeatmapId > 0 ? "o:" + run.OnlineBeatmapId
            : "b:" + run.BeatmapId;
        return (run.Player ?? string.Empty).ToLowerInvariant() + "|" + map;
    }

    internal static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.Where(double.IsFinite).Order().ToArray();
        if (sorted.Length == 0) return double.NaN;
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    internal const double SectionWindowMs = 5_000;

    /// <summary>Where one issue clusters across analysed plays, for the start step, the timeline band and the row label.</summary>
    internal static CoachingIssue Locate(CoachingIssueKind kind, int evidence, int plays, IReadOnlyList<(double Time, int Index, double End)> events, int analysedPlays)
    {
        var (start, _) = Densest(events.Select(e => e.Time).ToArray(), SectionWindowMs);
        var inside = events.Where(e => e.Time >= start && e.Time <= start + SectionWindowMs).ToArray();
        var first = inside.MinBy(e => e.Time);
        double end = Math.Max(inside.Max(e => e.End), inside.Max(e => e.Time));
        bool tapping = Family(kind) == CoachingIssueFamily.Tapping;
        double perPlay = analysedPlays == 0 ? 0 : tapping
            ? (double)Math.Min(plays, analysedPlays) / analysedPlays
            : (double)inside.Length / analysedPlays;
        return new CoachingIssue(kind, evidence, plays, start, end, first.Index, perPlay);
    }

    /// <summary>Start of the densest window of <paramref name="windowMs"/>, and its share of all times.</summary>
    internal static (double StartMs, double Share) Densest(IReadOnlyList<double> times, double windowMs)
    {
        if (times.Count == 0) return (0, 0);
        double[] sorted = times.Order().ToArray();
        int best = 0, bestStart = 0;
        for (int start = 0, end = 0; start < sorted.Length; start++)
        {
            while (end < sorted.Length && sorted[end] - sorted[start] <= windowMs) end++;
            if (end - start > best) { best = end - start; bestStart = start; }
        }
        return (sorted[bestStart], (double)best / sorted.Length);
    }
}

public static class CoachingMapRanker
{
    private const int analysed_runs_per_map = 40;

    /// <summary>
    /// Ranks difficulties by how much a player can realistically gain: the gap between best and typical accuracy,
    /// misses that repeat in one section, pp left on the table, recency and enough attempts to trust the numbers.
    /// </summary>
    public static CoachingMapRanking Rank(IReadOnlyList<LocalReplay> eligible, IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eligible);
        ArgumentNullException.ThrowIfNull(analyses);
        var maps = new List<CoachingMapCandidate>();
        foreach (var group in eligible.GroupBy(CoachingIssues.DifficultyKey))
        {
            cancellationToken.ThrowIfCancellationRequested();
            maps.Add(candidate(group.Key, group.OrderByDescending(r => r.PlayedAt).ToArray(), analyses, now));
        }
        return new CoachingMapRanking(maps.OrderByDescending(m => m.Potential).ThenByDescending(m => m.Latest.PlayedAt).ToArray(),
            skills(eligible, analyses));
    }

    private static CoachingMapCandidate candidate(string key, LocalReplay[] runs, IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses, DateTimeOffset now)
    {
        double best = runs.Max(r => r.Accuracy);
        double typical = CoachingIssues.Median(runs.Select(r => r.Accuracy));
        double typicalMisses = CoachingIssues.Median(runs.Select(r => (double)Math.Max(0, r.MissCount)));
        double[] pp = runs.Select(r => r.PerformancePoints ?? double.NaN).Where(v => double.IsFinite(v) && v > 0).ToArray();
        LocalReplay[] analysed = runs.Where(r => CoachingIssues.Current(analyses.GetValueOrDefault(r.ScoreId))).Take(analysed_runs_per_map).ToArray();

        var tally = new Dictionary<CoachingIssueKind, (int Evidence, HashSet<Guid> Plays, List<(double Time, int Index, double End)> Events)>();
        var missTimes = new List<double>();
        void add(CoachingIssueKind kind, Guid play, double time, int index, double end, int weight)
        {
            if (!tally.TryGetValue(kind, out var entry)) tally[kind] = entry = (0, [], []);
            entry.Plays.Add(play); entry.Events.Add((time, index, end));
            tally[kind] = entry with { Evidence = entry.Evidence + weight };
        }
        foreach (LocalReplay run in analysed)
        {
            var analysis = analyses[run.ScoreId];
            foreach (var judgement in analysis.Judgements)
                if (CoachingIssues.FromMiss(judgement) is { } kind)
                {
                    add(kind, run.ScoreId, judgement.StartTimeMs, judgement.ObjectIndex!.Value, judgement.StartTimeMs, 1);
                    missTimes.Add(judgement.StartTimeMs);
                }
            if (TappingCoaching.Build(analysis) is { } lesson)
                add(CoachingIssues.FromLesson(lesson), run.ScoreId, lesson.StartTimeMs, lesson.FirstObjectIndex, lesson.EndTimeMs, 2);
        }

        CoachingIssue? top = tally.OrderByDescending(t => t.Value.Evidence).ThenByDescending(t => t.Value.Plays.Count)
            .Select(t => CoachingIssues.Locate(t.Key, t.Value.Evidence, t.Value.Plays.Count, t.Value.Events, analysed.Length))
            .FirstOrDefault();
        double concentration = missTimes.Count < 3 ? 0 : CoachingIssues.Densest(missTimes, 8_000).Share;

        LocalReplay target = analysed.FirstOrDefault(r => r.HasReplayFile) ?? runs.FirstOrDefault(r => r.HasReplayFile) ?? runs[0];
        double gapPoints = Math.Clamp((best - typical) * 100, 0, 8);
        double missLoad = Math.Min(double.IsFinite(typicalMisses) ? typicalMisses : 0, 12) / 12 * 4;
        double focus = concentration * 3 * Math.Min(1, analysed.Length / 3.0);
        double ppGap = pp.Length < 2 ? 0 : Math.Min((pp.Max() - CoachingIssues.Median(pp)) / 15, 3);
        double days = Math.Max(0, (now - runs[0].PlayedAt).TotalDays);
        double recency = 0.35 + 0.65 * Math.Exp(-days / 21);
        double confidence = Math.Sqrt(Math.Min(1, runs.Length / 6.0));
        double potential = (gapPoints + missLoad + focus + ppGap) * recency * confidence;

        return new CoachingMapCandidate(key, runs[0], target, runs.Length, analysed.Length, best, typical,
            double.IsFinite(typicalMisses) ? typicalMisses : 0, runs.Min(r => Math.Max(0, r.MissCount)),
            pp.Length == 0 ? null : pp.Max(), pp.Length == 0 ? null : CoachingIssues.Median(pp), top, concentration, potential);
    }

    private static CoachingSkillSummary skills(IReadOnlyList<LocalReplay> eligible, IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses)
    {
        LocalReplay[] analysed = eligible.Where(r => CoachingIssues.Current(analyses.GetValueOrDefault(r.ScoreId)))
            .OrderBy(r => r.PlayedAt).ToArray();
        if (analysed.Length == 0) return CoachingSkillSummary.Empty;

        var families = Enum.GetValues<CoachingIssueFamily>().Where(f => f != CoachingIssueFamily.Tapping).ToArray();
        var older = new Dictionary<CoachingIssueFamily, int>();
        var newer = new Dictionary<CoachingIssueFamily, int>();
        int half = analysed.Length / 2, driftOld = 0, driftNew = 0;
        double offsetSum = 0, offsetSquares = 0;
        int offsetCount = 0;
        for (int i = 0; i < analysed.Length; i++)
        {
            var analysis = analyses[analysed[i].ScoreId];
            var bucket = i < half ? older : newer;
            foreach (var judgement in analysis.Judgements)
            {
                if (CoachingIssues.FromMiss(judgement) is { } kind)
                {
                    var family = CoachingIssues.Family(kind);
                    bucket[family] = bucket.GetValueOrDefault(family) + 1;
                }
                else if (i >= analysed.Length - 60 && !ReplayJudgementClassifier.IsMiss(judgement) && double.IsFinite(judgement.TimeOffsetMs)
                         && string.IsNullOrEmpty(judgement.NestedPath) && judgement.ObjectType == "HitCircle")
                {
                    offsetSum += judgement.TimeOffsetMs;
                    offsetSquares += judgement.TimeOffsetMs * judgement.TimeOffsetMs;
                    offsetCount++;
                }
            }
            if (TappingCoaching.Build(analysis) is { } lesson && CoachingIssues.FromLesson(lesson) is CoachingIssueKind.Rushing or CoachingIssueKind.Dragging or CoachingIssueKind.UnevenTaps)
            {
                if (i < half) driftOld++;
                else driftNew++;
            }
        }

        int oldPlays = half, newPlays = analysed.Length - half;
        int total = families.Sum(f => older.GetValueOrDefault(f) + newer.GetValueOrDefault(f));
        double? change(double oldRate, double newRate) => oldPlays >= 5 && newPlays >= 5 && oldRate > 0 ? (newRate - oldRate) / oldRate : null;
        var lines = families.Select(f =>
            {
                int count = older.GetValueOrDefault(f) + newer.GetValueOrDefault(f);
                double oldRate = oldPlays == 0 ? 0 : (double)older.GetValueOrDefault(f) / oldPlays;
                double newRate = newPlays == 0 ? 0 : (double)newer.GetValueOrDefault(f) / newPlays;
                return new CoachingSkillLine(f, total == 0 ? 0 : (double)count / total, (double)count / analysed.Length, change(oldRate, newRate));
            })
            .Where(l => l.PerPlay > 0).OrderByDescending(l => l.Share).ToArray();
        double mean = offsetCount == 0 ? double.NaN : offsetSum / offsetCount;
        double spread = offsetCount < 2 ? double.NaN : Math.Sqrt(Math.Max(0, offsetSquares / offsetCount - mean * mean));
        double driftShare = (double)(driftOld + driftNew) / analysed.Length;
        return new CoachingSkillSummary(analysed.Length, lines,
            double.IsFinite(mean) ? mean : null, double.IsFinite(spread) ? spread : null, driftShare,
            change(oldPlays == 0 ? 0 : (double)driftOld / oldPlays, newPlays == 0 ? 0 : (double)driftNew / newPlays));
    }
}

public sealed record CoachingTimelineMarker(double TimeMs, CoachingIssueKind Kind);

public sealed record CoachingMapTimeline(double LengthMs, int AnalysedPlays, IReadOnlyList<CoachingTimelineMarker> Markers)
{
    public static CoachingMapTimeline Build(LocalReplay target, IEnumerable<LocalReplay> history, IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses)
    {
        string key = CoachingIssues.DifficultyKey(target);
        LocalReplay[] runs = history.Append(target).Where(r => CoachingIssues.DifficultyKey(r) == key && CoachingRunKeys.IsManualPlay(r)
                && CoachingIssues.Current(analyses.GetValueOrDefault(r.ScoreId)))
            .DistinctBy(r => r.ScoreId).OrderByDescending(r => r.PlayedAt).Take(40).ToArray();
        var markers = new List<CoachingTimelineMarker>();
        double length = 0;
        foreach (var run in runs)
        {
            var analysis = analyses[run.ScoreId];
            foreach (var judgement in analysis.Judgements)
            {
                if (double.IsFinite(judgement.EndTimeMs)) length = Math.Max(length, judgement.EndTimeMs);
                if (CoachingIssues.FromMiss(judgement) is { } kind) markers.Add(new(judgement.StartTimeMs, kind));
            }
            if (TappingCoaching.Build(analysis) is { } lesson)
                markers.Add(new(lesson.StartTimeMs, CoachingIssues.FromLesson(lesson)));
        }
        return new CoachingMapTimeline(length, runs.Length, markers);
    }
}

/// <summary>A replay observation with the measurements its compact card draws.</summary>
public sealed record CoachingSection(
    CoachingReplayObservation Observation,
    CoachingIssueKind Kind,
    double TimeMs,
    int FirstObjectIndex,
    int Count,
    string Summary,
    IReadOnlyList<double> TapOffsetsMs,
    IReadOnlyList<(float X, float Y)> PressPoints,
    IReadOnlyList<double> PressOffsetsMs,
    double EndMs = 0);

public static class CoachingSections
{
    /// <summary>Sections of the target play; those matching the map's main issue come first so the page agrees with itself.</summary>
    public static IReadOnlyList<CoachingSection> Build(LocalReplay target, IReadOnlyList<CoachingReplayObservation> observations,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses, CoachingIssue? mainIssue) =>
        Build(target, observations, analyses)
            .OrderByDescending(s => mainIssue is { } main && CoachingIssues.Family(s.Kind) == CoachingIssues.Family(main.Kind))
            .ThenByDescending(s => mainIssue is { } main && s.TimeMs <= main.EndMs + 1_000 && s.EndMs >= main.TimeMs - 1_000)
            .ToArray();

    public static IReadOnlyList<CoachingSection> Build(LocalReplay target, IReadOnlyList<CoachingReplayObservation> observations,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses)
    {
        if (!analyses.TryGetValue(target.ScoreId, out var analysis) || !CoachingIssues.Current(analysis)) return [];
        var sections = new List<CoachingSection>();
        var lesson = TappingCoaching.Build(analysis);
        var misses = analysis.Judgements.Where(CoachingIssues.IsEvidenceMiss).ToArray();
        foreach (var observation in observations)
        {
            if (lesson is not null && observation.FirstObjectIndex == lesson.FirstObjectIndex && observation.Label == lesson.Pattern)
            {
                double[] offsets = analysis.Judgements.Where(j => j.ObjectIndex >= lesson.FirstObjectIndex && j.ObjectIndex < lesson.FirstObjectIndex + 8
                        && string.IsNullOrEmpty(j.NestedPath)).OrderBy(j => j.ObjectIndex).Select(j => j.TimeOffsetMs).ToArray();
                var kind = CoachingIssues.FromLesson(lesson);
                sections.Add(new(observation, kind, lesson.StartTimeMs, lesson.FirstObjectIndex, 8,
                    tappingSummary(kind, lesson, offsets), offsets, [], [], lesson.EndTimeMs));
                continue;
            }

            var first = misses.FirstOrDefault(j => j.ObjectIndex == observation.FirstObjectIndex);
            if (first is null || CoachingIssues.FromMiss(first) is not { } firstKind) continue;
            var family = CoachingIssues.Family(firstKind);
            var group = misses.Where(j => CoachingIssues.FromMiss(j) is { } k && CoachingIssues.Family(k) == family).ToArray();
            // Practise and watch where these misses cluster, not wherever the first one happened.
            var (start, _) = CoachingIssues.Densest(group.Select(j => j.StartTimeMs).ToArray(), CoachingIssues.SectionWindowMs);
            var anchor = group.Where(j => j.StartTimeMs >= start).MinBy(j => j.StartTimeMs) ?? first;
            double end = group.Where(j => j.StartTimeMs >= start && j.StartTimeMs <= start + CoachingIssues.SectionWindowMs).Max(j => j.StartTimeMs);
            var dominant = group.GroupBy(j => CoachingIssues.FromMiss(j)!.Value).MaxBy(g => g.Count())!.Key;
            var points = group.Where(j => j.ObjectPosition is not null && j.MissAnalysis!.CursorAtPress is not null)
                .Select(j => ((float)((j.MissAnalysis!.CursorAtPress!.X - j.ObjectPosition!.X) / j.MissAnalysis.HitRadius),
                    (float)((j.MissAnalysis.CursorAtPress.Y - j.ObjectPosition.Y) / j.MissAnalysis.HitRadius))).Take(24).ToArray();
            var pressOffsets = group.Select(j => j.MissAnalysis!.PressTimeOffsetMs ?? double.NaN).Where(double.IsFinite).Take(24).ToArray();
            sections.Add(new(observation, dominant, anchor.StartTimeMs, anchor.ObjectIndex!.Value, group.Length,
                missSummary(dominant, group.Length, pressOffsets), [], points, pressOffsets, Math.Max(end, anchor.StartTimeMs + 1_000)));
        }
        return sections;
    }

    private static string tappingSummary(CoachingIssueKind kind, TappingLesson lesson, double[] offsets) => kind switch
    {
        CoachingIssueKind.Rushing => $"Speeds up {Math.Abs(lesson.DriftMs):0} ms across 8 notes",
        CoachingIssueKind.Dragging => $"Falls {Math.Abs(lesson.DriftMs):0} ms behind across 8 notes",
        CoachingIssueKind.EarlyStart => $"All 8 taps about {Math.Abs(lesson.MeanOffsetMs):0} ms early",
        CoachingIssueKind.LateStart => $"All 8 taps about {Math.Abs(lesson.MeanOffsetMs):0} ms late",
        _ => $"Tap gaps vary by {spread(offsets):0} ms",
    };

    private static double spread(double[] values) => values.Length < 2 ? 0 : ReplayJudgementClassifier.StandardDeviation(values);

    private static string missSummary(CoachingIssueKind kind, int count, double[] pressOffsets)
    {
        string misses = $"{count} {(count == 1 ? "miss" : "misses")}";
        double median = CoachingIssues.Median(pressOffsets);
        return kind switch
        {
            CoachingIssueKind.Overshoot => $"{misses} · cursor went past the circle",
            CoachingIssueKind.Undershoot => $"{misses} · cursor stopped short",
            CoachingIssueKind.OffTarget => $"{misses} · pressed off the circle",
            CoachingIssueKind.EarlyPress => double.IsFinite(median) ? $"{misses} · pressed {Math.Abs(median):0} ms early" : $"{misses} · pressed early",
            CoachingIssueKind.LatePress => double.IsFinite(median) ? $"{misses} · pressed {Math.Abs(median):0} ms late" : $"{misses} · pressed late",
            _ => $"{misses} · on the circle without a press",
        };
    }
}

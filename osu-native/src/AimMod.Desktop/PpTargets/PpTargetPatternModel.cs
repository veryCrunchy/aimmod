using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AimMod.Desktop.LocalLibrary;
using AimMod.Osu.Runtime.Contracts;
using osu.Game.Rulesets.Objects.Legacy;
using osu.Game.Rulesets.Osu.Objects;

namespace AimMod.Desktop.PpTargets;

public sealed record PpPatternPoint(double TimeMs, double X, double Y, bool BreakBefore = false);
public sealed record PpPatternContext(double? HitRadius, double? ClockRate);

// Distances use osu's normalised radius of 50. Times are real playing time after clock-rate adjustment.
// These are descriptive head-geometry features, not osu difficulty/strain or slider-path calculations.
public sealed record PpPatternFeatures
{
    public int PointCount { get; init; }
    public int TransitionCount { get; init; }
    public int InvalidPointCount { get; init; }
    public double? HitRadius { get; init; }
    public double? ClockRate { get; init; }
    public double? MeanSpacing { get; init; }
    public double? PeakSpacing { get; init; }
    public double? JumpDistance { get; init; }
    public double? JumpFraction { get; init; }
    public double? NotesPerSecond { get; init; }
    public double? PeakNotesPerSecond { get; init; }
    public double? NormalizedSpeed { get; init; }
    public double? BurstFraction { get; init; }
    public double? StreamFraction { get; init; }
    public double? MeanDirectionChangeDegrees { get; init; }
    public double? SharpTurnFraction { get; init; }
    public double? DurationSeconds { get; init; }
}

public sealed record PpPatternOutcome(int ObjectCount, double Accuracy, double MissRate,
    IReadOnlyDictionary<ReplayMissReason, int> MissReasons);

public sealed record PpPatternEvidence(Guid ScoreId, string MapKey, string ModsKey, DateTimeOffset PlayedAt,
    PpPatternFeatures Features, double Weight, IReadOnlyDictionary<string, PpPatternOutcome> Outcomes);

public sealed record PpPatternProfile(string Identity, DateTimeOffset ReferenceTime, int RecencyDays,
    IReadOnlyList<PpPatternEvidence> Evidence);

public sealed record PpPatternFit(string Pattern, double? Fit, double? ExpectedAccuracy,
    double Confidence, int DistinctMaps, double? ExpectedMissRate = null);

public sealed record PpPatternPrediction(double? Fit, double? ExpectedAccuracy, double EvidenceConfidence,
    IReadOnlyList<string> Strengths, IReadOnlyList<string> Risks, IReadOnlyList<PpPatternFit> PatternFits,
    IReadOnlyList<string>? CoverageNotes = null, double? ExpectedMissRate = null);

public static class PpTargetPatternModel
{
    public const string Version = "geometry-v2";
    private const double normalized_radius = 50;
    private const double jump_spacing = 150;
    private const double tapping_spacing = 100;
    private const double fast_interval_ms = 200;
    private const int minimum_pattern_objects = 8;

    public static PpPatternFeatures ExtractFeatures(IEnumerable<PpPatternPoint> points, double? hitRadius, double? clockRate)
    {
        ArgumentNullException.ThrowIfNull(points);
        return measure(points.ToArray(), hitRadius, clockRate).Features;
    }

    public static PpPatternProfile BuildProfile(IEnumerable<LocalReplay> replays,
        IReadOnlyDictionary<Guid, ReplayAnalysisResult> analyses,
        IReadOnlyDictionary<Guid, PpPatternContext>? contexts = null,
        DateTimeOffset? now = null, int recencyDays = 30,
        IEnumerable<LocalBeatmapSet>? localSets = null)
    {
        ArgumentNullException.ThrowIfNull(replays);
        ArgumentNullException.ThrowIfNull(analyses);
        if (recencyDays <= 0 || recencyDays > 3650) throw new ArgumentOutOfRangeException(nameof(recencyDays));
        DateTimeOffset reference = now ?? DateTimeOffset.UtcNow;
        DateTimeOffset referenceDay = new(reference.UtcDateTime.Date, TimeSpan.Zero);
        var difficulties = (localSets ?? []).SelectMany(s => s.Difficulties)
            .GroupBy(d => d.BeatmapId).ToDictionary(g => g.Key, g => g.First());
        var evidence = new List<PpPatternEvidence>();

        foreach (LocalReplay replay in replays.Where(r => r.RulesetShortName.Equals("osu", StringComparison.OrdinalIgnoreCase)
                     && r.PlayedAt <= reference && r.PlayedAt >= referenceDay.AddDays(-recencyDays))
                     .GroupBy(r => r.ScoreId).Select(g => g.OrderByDescending(r => r.PlayedAt).First()).OrderBy(r => r.ScoreId))
        {
            if (!analyses.TryGetValue(replay.ScoreId, out ReplayAnalysisResult? analysis)) continue;
            // A slider's aggregate result can reflect its tail. Use its genuine head instead, exactly once.
            ReplayObjectJudgement[] heads = analysis.Judgements.Where(isHead)
                .GroupBy(j => j.ObjectIndex!.Value)
                .Select(g => g.OrderByDescending(j => j.ObjectType == "SliderHeadCircle")
                    .ThenByDescending(j => j.JudgementTimeMs).First())
                .OrderBy(j => j.StartTimeMs).ThenBy(j => j.ObjectIndex).ToArray();
            if (heads.Length < 3) continue;

            // Uniform per-judgement gameplay rate is measured evidence. Variable/absent rate stays unknown.
            double? rate = contextRate(contexts, replay.ScoreId, heads);
            double? radius = contexts is not null && contexts.TryGetValue(replay.ScoreId, out var supplied) ? positive(supplied.HitRadius) : null;
            string mods = modsKey(replay.Mods);
            if (radius is null && difficulties.TryGetValue(replay.BeatmapId, out LocalBeatmapDifficulty? difficulty))
                radius = localRadius(difficulty.CircleSize, mods, replay.ModsJson);

            PpPatternPoint[] points = heads.Select((j, i) => new PpPatternPoint(j.StartTimeMs, j.ObjectPosition!.X, j.ObjectPosition.Y,
                i > 0 && j.ObjectIndex != heads[i - 1].ObjectIndex + 1)).ToArray();
            var measured = measure(points, radius, rate);
            var outcomes = new Dictionary<string, PpPatternOutcome>();
            foreach (var (pattern, indices) in measured.Patterns)
            {
                var judged = indices.Select(i => heads[i]).Where(j => judgementAccuracy(j) is not null).ToArray();
                if (judged.Length == 0) continue;
                var reasons = judged.Where(j => j.Result == "Miss")
                    .GroupBy(j => j.MissAnalysis?.Reason ?? ReplayMissReason.Unknown)
                    .OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count());
                outcomes[pattern] = new PpPatternOutcome(judged.Length, judged.Average(j => judgementAccuracy(j)!.Value),
                    judged.Count(j => j.Result == "Miss") / (double)judged.Length, reasons);
            }
            if (!outcomes.TryGetValue("Overall", out var overall) || overall.ObjectCount < 3) continue;
            double age = (referenceDay.UtcDateTime - replay.PlayedAt.UtcDateTime.Date).TotalDays;
            double decay = Math.Pow(0.5, age / Math.Max(1, recencyDays / 2d));
            string mapKey = !string.IsNullOrWhiteSpace(replay.BeatmapHash) ? replay.BeatmapHash.Trim().ToLowerInvariant()
                : replay.BeatmapId != Guid.Empty ? replay.BeatmapId.ToString("N") : $"unknown:{replay.ScoreId:N}";
            evidence.Add(new PpPatternEvidence(replay.ScoreId, mapKey, mods, replay.PlayedAt, measured.Features, decay, outcomes));
        }

        // One map/setup contributes at most its freshest play's weight, even after hundreds of retries.
        var balanced = evidence.GroupBy(e => (e.MapKey, e.ModsKey)).SelectMany(g =>
        {
            double total = g.Sum(e => e.Weight), newest = g.Max(e => e.Weight);
            return g.Select(e => e with { Weight = e.Weight / total * newest });
        }).OrderBy(e => e.ScoreId).ToArray();
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { Version, recencyDays, Evidence = balanced })))).ToLowerInvariant();
        return new PpPatternProfile(identity, reference, recencyDays, balanced);
    }

    public static PpPatternPrediction Predict(PpPatternFeatures candidate, PpPatternProfile profile, IReadOnlyList<string>? mods = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(profile);
        var strengths = new List<string>();
        var risks = new List<string>();
        var fits = new List<PpPatternFit>();
        if (candidate.PointCount < 8 || candidate.TransitionCount < 6 || candidate.InvalidPointCount > 0)
            return new(null, null, 0, [], ["Too few valid candidate objects to measure pattern fit."], []);
        if (candidate.HitRadius is null || candidate.ClockRate is null)
            return new(null, null, 0, [], ["Circle radius or clock rate is unmeasured; normalized skill fit is unavailable."], []);

        string key = modsKey(mods ?? []);
        var compatible = profile.Evidence.Where(e => e.ModsKey == key && e.Features.HitRadius is not null
            && e.Features.ClockRate is { } rate && Math.Abs(rate - candidate.ClockRate.Value) < 0.01).ToArray();
        var demanded = new List<string> { "Overall" };
        if (candidate.JumpFraction >= 0.1) demanded.Add("Jumps");
        if (candidate.BurstFraction >= 0.1) demanded.Add("Bursts");
        if (candidate.StreamFraction >= 0.1) demanded.Add("Streams");
        if (candidate.NotesPerSecond >= 5) demanded.Add("Speed");
        if (candidate.SharpTurnFraction >= 0.1) demanded.Add("Direction changes");

        foreach (string pattern in demanded)
        {
            var relevant = compatible.Where(e => e.Outcomes.TryGetValue(pattern, out var o) && o.ObjectCount >= minimum_pattern_objects)
                .Select(e => (Evidence: e, Similarity: similarity(candidate, e.Features, pattern)))
                .Where(e => e.Similarity >= 0.55).ToArray();
            // Repeated attempts are not independent geometry coverage; unknown-map records cannot prove it either.
            int maps = relevant.Select(e => e.Evidence.MapKey).Where(k => !k.StartsWith("unknown:", StringComparison.Ordinal)).Distinct().Count();
            if (maps < 2)
            {
                fits.Add(new(pattern, null, null, 0, maps));
                risks.Add($"{pattern}: unmeasured fit; only {maps} comparable map(s) in the recent mod setup.");
                continue;
            }
            double weight = relevant.Sum(e => e.Evidence.Weight * e.Similarity);
            double accuracy = relevant.Sum(e => e.Evidence.Weight * e.Similarity * e.Evidence.Outcomes[pattern].Accuracy) / weight;
            double misses = relevant.Sum(e => e.Evidence.Weight * e.Similarity * e.Evidence.Outcomes[pattern].MissRate) / weight;
            double fit = Math.Clamp((accuracy - 0.7) / 0.3, 0, 1) * (1 - misses);
            double confidence = Math.Min(0.85, maps / 6d) * relevant.Average(e => e.Similarity)
                * Math.Min(1, weight / maps);
            fits.Add(new(pattern, fit, accuracy, confidence, maps, misses));
            if (pattern != "Overall" && accuracy >= 0.97 && misses < 0.01)
                strengths.Add($"{pattern}: {accuracy:P1} head accuracy across {maps} comparable maps.");
            if (accuracy < 0.95 || misses >= 0.02)
            {
                var reason = relevant.SelectMany(e => e.Evidence.Outcomes[pattern].MissReasons
                        .Select(r => (Reason: r.Key, Weight: r.Value / (double)e.Evidence.Outcomes[pattern].ObjectCount * e.Evidence.Weight * e.Similarity)))
                    .GroupBy(r => r.Reason).OrderByDescending(g => g.Sum(r => r.Weight)).ThenBy(g => g.Key).FirstOrDefault();
                risks.Add($"{pattern}: {accuracy:P1} head accuracy, {misses:P1} misses" + (reason is null ? "." : $"; {reasonLabel(reason.Key)} observed."));
            }
        }
        double coverage = fits.Count(f => f.Fit is not null) / (double)fits.Count;
        double evidenceConfidence = fits.Average(f => f.Confidence) * coverage;
        string[] coverageNotes = ["Slider tracking and full-map completion are unmeasured; accuracy estimates cover judged heads."];
        if (fits.Any(f => f.Fit is null)) return new(null, null, evidenceConfidence, strengths, risks, fits, coverageNotes);
        // Use the weakest demanded pattern rather than allowing easy sections to hide a bottleneck.
        double bottleneck = fits.Min(f => f.Fit!.Value);
        double expectedAccuracy = Math.Min(fits[0].ExpectedAccuracy!.Value, fits.Min(f => f.ExpectedAccuracy!.Value) + 0.015);
        double expectedMissRate = fits.Max(f => f.ExpectedMissRate!.Value);
        return new(bottleneck, expectedAccuracy, evidenceConfidence, strengths, risks, fits, coverageNotes, expectedMissRate);
    }

    private static (PpPatternFeatures Features, Dictionary<string, HashSet<int>> Patterns) measure(PpPatternPoint[] input, double? radius, double? rate)
    {
        radius = positive(radius); rate = positive(rate);
        int invalid = input.Count(p => !double.IsFinite(p.TimeMs) || !double.IsFinite(p.X) || !double.IsFinite(p.Y));
        var patterns = new Dictionary<string, HashSet<int>> { ["Overall"] = [], ["Jumps"] = [], ["Bursts"] = [], ["Streams"] = [], ["Speed"] = [], ["Direction changes"] = [] };
        if (invalid > 0) return (new() { InvalidPointCount = invalid, HitRadius = radius, ClockRate = rate }, patterns);
        PpPatternPoint[] points = input.OrderBy(p => p.TimeMs).ToArray();
        for (int i = 0; i < points.Length; i++) patterns["Overall"].Add(i);
        var spacing = new List<double>(); var intervals = new List<double>(); var speeds = new List<double>(); var angles = new List<double>();
        var fast = new bool[Math.Max(0, points.Length - 1)];
        int transitions = 0;
        for (int i = 1; i < points.Length; i++)
        {
            double rawDelta = points[i].TimeMs - points[i - 1].TimeMs;
            if (rawDelta <= 0 || rawDelta > 2000 || points[i].BreakBefore) continue;
            transitions++;
            double distance = Math.Sqrt(Math.Pow(points[i].X - points[i - 1].X, 2) + Math.Pow(points[i].Y - points[i - 1].Y, 2));
            double? normalized = radius is { } r ? distance * normalized_radius / r : null;
            double? delta = rate is { } speed ? rawDelta / speed : null;
            if (normalized is { } d)
            {
                spacing.Add(d);
                if (d >= jump_spacing) patterns["Jumps"].Add(i);
            }
            if (delta is { } ms)
            {
                intervals.Add(ms);
                if (ms <= fast_interval_ms) patterns["Speed"].Add(i);
                if (normalized is { } norm) speeds.Add(norm / Math.Max(25, ms) * 1000);
            }
            fast[i - 1] = delta <= fast_interval_ms && normalized <= tapping_spacing;
            if (i >= 2 && !points[i - 1].BreakBefore
                && points[i - 1].TimeMs - points[i - 2].TimeMs is > 0 and <= 2000)
            {
                double ax = points[i - 1].X - points[i - 2].X, ay = points[i - 1].Y - points[i - 2].Y;
                double bx = points[i].X - points[i - 1].X, by = points[i].Y - points[i - 1].Y;
                double magnitude = Math.Sqrt((ax * ax + ay * ay) * (bx * bx + by * by));
                if (magnitude <= 0) continue;
                double angle = Math.Acos(Math.Clamp((ax * bx + ay * by) / magnitude, -1, 1)) * 180 / Math.PI;
                angles.Add(angle);
                if (angle >= 60) patterns["Direction changes"].Add(i);
            }
        }
        for (int start = 0; start < fast.Length;)
        {
            if (!fast[start]) { start++; continue; }
            int end = start;
            while (end < fast.Length && fast[end]) end++;
            int length = end - start + 1;
            if (length >= 3)
                for (int index = start; index <= end; index++) patterns[length >= 8 ? "Streams" : "Bursts"].Add(index);
            start = end;
        }
        var features = new PpPatternFeatures
        {
            PointCount = points.Length, TransitionCount = transitions, HitRadius = radius, ClockRate = rate,
            MeanSpacing = mean(spacing), JumpDistance = spacing.Count == 0 ? null : spacing.Where(d => d >= jump_spacing).DefaultIfEmpty(0).Average(),
            PeakSpacing = spacing.Count == 0 ? null : spacing.Order().ElementAt((int)Math.Ceiling((spacing.Count - 1) * 0.9)),
            JumpFraction = spacing.Count == 0 ? null : patterns["Jumps"].Count / (double)spacing.Count,
            NotesPerSecond = intervals.Count == 0 ? null : 1000 / Math.Max(25, median(intervals)),
            PeakNotesPerSecond = intervals.Count == 0 ? null : 1000 / Math.Max(25, intervals.Order().ElementAt((int)((intervals.Count - 1) * 0.1))),
            NormalizedSpeed = mean(speeds),
            BurstFraction = radius is null || rate is null || transitions == 0 ? null : patterns["Bursts"].Count / (double)points.Length,
            StreamFraction = radius is null || rate is null || transitions == 0 ? null : patterns["Streams"].Count / (double)points.Length,
            MeanDirectionChangeDegrees = mean(angles), SharpTurnFraction = angles.Count == 0 ? null : patterns["Direction changes"].Count / (double)angles.Count,
            DurationSeconds = points.Length < 2 || rate is null ? null : (points[^1].TimeMs - points[0].TimeMs) / rate / 1000,
        };
        return (features, patterns);
    }

    private static bool isHead(ReplayObjectJudgement j) => j.ObjectIndex is >= 0 && j.ObjectPosition is { } p
        && double.IsFinite(j.StartTimeMs) && float.IsFinite(p.X) && float.IsFinite(p.Y)
        && ((j.ObjectType == "HitCircle" && string.IsNullOrEmpty(j.NestedPath)) || j.ObjectType == "SliderHeadCircle");

    private static double? judgementAccuracy(ReplayObjectJudgement j) => j.MaximumResult != "Great" ? null : j.Result switch
    { "Great" => 1, "Ok" => 1d / 3, "Meh" => 1d / 6, "Miss" => 0, _ => null };

    private static double? contextRate(IReadOnlyDictionary<Guid, PpPatternContext>? contexts, Guid id, ReplayObjectJudgement[] heads)
    {
        if (contexts is not null && contexts.TryGetValue(id, out var context) && positive(context.ClockRate) is { } supplied) return supplied;
        double[] rates = heads.Select(j => positive(j.GameplayRate)).Where(r => r is not null).Select(r => r!.Value).ToArray();
        return rates.Length == heads.Length && rates.Max() - rates.Min() < 0.00001 ? rates[0] : null;
    }

    private static double? localRadius(float cs, string mods, string modsJson)
    {
        if (!float.IsFinite(cs) || cs < 0 || cs > 10) return null;
        // Custom settings can change CS. Require explicit measured context rather than guessing their effects.
        if (hasCustomCircleSize(modsJson)) return null;
        string[] setup = mods.Split('+', StringSplitOptions.RemoveEmptyEntries);
        if (setup.Any(m => m is not ("HD" or "HR" or "EZ" or "DT" or "HT" or "FL" or "NF" or "SO" or "CL"))) return null;
        if (setup.Contains("HR")) cs = Math.Min(10, cs * 1.3f);
        if (setup.Contains("EZ")) cs *= 0.5f;
        return OsuHitObject.OBJECT_RADIUS * LegacyRulesetExtensions.CalculateScaleFromCircleSize(cs, true);
    }

    private static string modsKey(IEnumerable<string> mods) => string.Join('+', mods.Select(m => m.Trim().Replace(" ", "").ToUpperInvariant() switch
    {
        "NOMOD" or "NM" or "" => "", "HIDDEN" => "HD", "HARDROCK" => "HR", "EASY" => "EZ",
        "DOUBLETIME" or "NIGHTCORE" or "NC" => "DT", "HALFTIME" or "DAYCORE" or "DC" => "HT",
        "FLASHLIGHT" => "FL", "NOFAIL" => "NF", "SPUNOUT" => "SO", "CLASSIC" => "CL", var acronym => acronym,
    }).Where(m => m.Length > 0).Distinct().Order(StringComparer.Ordinal));

    private static double similarity(PpPatternFeatures a, PpPatternFeatures b, string pattern)
    {
        var differences = new List<double>();
        void compare(double? x, double? y, double scale) { if (x is { } xx && y is { } yy) differences.Add(Math.Min(3, Math.Abs(xx - yy) / scale)); }
        compare(a.NotesPerSecond, b.NotesPerSecond, 3);
        compare(a.MeanSpacing, b.MeanSpacing, 130);
        compare(a.PeakSpacing, b.PeakSpacing, 180);
        compare(a.PeakNotesPerSecond, b.PeakNotesPerSecond, 3);
        compare(a.NormalizedSpeed, b.NormalizedSpeed, 800);
        if (pattern is "Overall" or "Jumps") compare(a.JumpDistance, b.JumpDistance, 180);
        if (pattern is "Overall" or "Jumps") compare(a.JumpFraction, b.JumpFraction, 0.4);
        if (pattern is "Overall" or "Streams" or "Bursts")
        { compare(a.StreamFraction, b.StreamFraction, 0.4); compare(a.BurstFraction, b.BurstFraction, 0.4); }
        if (pattern is "Overall" or "Direction changes") compare(a.SharpTurnFraction, b.SharpTurnFraction, 0.5);
        if (pattern == "Overall" && a.DurationSeconds is { } ad && b.DurationSeconds is { } bd)
            differences.Add(Math.Min(3, Math.Abs(Math.Log((ad + 1) / (bd + 1))) / 2));
        return differences.Count < 2 ? 0 : Math.Exp(-differences.Average());
    }

    private static double? positive(double? value) => value is > 0 && double.IsFinite(value.Value) ? value : null;

    private static bool hasCustomCircleSize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return inspect(document.RootElement);
        }
        catch (JsonException) { return true; }

        static bool inspect(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array) return element.EnumerateArray().Any(inspect);
            if (element.ValueKind != JsonValueKind.Object) return false;
            foreach (JsonProperty property in element.EnumerateObject())
            {
                string name = property.Name.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
                if (name is "circlesize" or "cs" or "circlesizemultiplier"
                    && property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                    && !(property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() == "")) return true;
                if (inspect(property.Value)) return true;
            }
            return false;
        }
    }
    private static double? mean(List<double> values) => values.Count == 0 ? null : values.Average();
    private static double median(List<double> values) { double[] sorted = values.Order().ToArray(); return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2; }
    private static string reasonLabel(ReplayMissReason reason) => reason switch
    { ReplayMissReason.EarlyClick => "early clicks", ReplayMissReason.LateClick => "late clicks", ReplayMissReason.OnTargetNoClick => "on-target misses without clicks", ReplayMissReason.AimDeviation => "aim deviations", ReplayMissReason.Overshoot => "overshoots", ReplayMissReason.Undershoot => "undershoots", _ => "unclassified misses" };
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AimMod.Desktop.LocalLibrary;
using AimMod.Desktop.ScoreHistory;
using AimMod.Osu.Runtime;

namespace AimMod.Desktop.PpTargets;

public enum PpOutcomeSource
{
    Local,
    OnlineRecent,
    OnlineBest,
}

// One play's own score statistics. Unlike replay head judgements these include slider
// accuracy, the real miss count and the recorded combo of the submitted score.
public sealed record PpOutcomeObservation(
    Guid? ScoreId, string MapKey, int BeatmapId, string? BeatmapHash, DateTimeOffset PlayedAt,
    double Stars, double? Bpm, int? LengthSeconds, double? OverallDifficulty, double? ApproachRate,
    IReadOnlyList<string> Mods, string ModsJson, string Setup, bool LegacyScore, bool Passed, PpOutcomeSource Source,
    double Accuracy, int Misses, int? Combo, int? ObjectCount, int? MapMaxCombo, double? Pp);

public sealed record PpOutcomeProfile(string Identity, DateTimeOffset AsOf, IReadOnlyList<PpOutcomeObservation> Observations);

public sealed record PpOutcomeTarget(double Stars, double? Bpm, int LengthSeconds, double? OverallDifficulty, double? ApproachRate,
    int ObjectCount, int MaximumCombo, string Setup, bool LegacyScore, string? MapKey = null, PpPatternFeatures? Features = null);

public sealed record PpOutcomePrior(double MissMean, double HitAccuracy);

/// <summary>
/// Per-attempt outcome of a passed play: misses follow a negative binomial (Poisson when not
/// overdispersed), hit accuracy is normal and combo is the even-spread segment scaled by the
/// player's observed combo efficiency.
/// </summary>
public sealed record PpOutcomeDistribution(
    double MissMean, double MissShape, double HitAccuracy, double AccuracySpread,
    double FullComboEfficiency, double ComboEfficiency, double EffectiveSamples, int Maps, int Passes,
    bool CrossMode, bool UsesBestScores, double MissStarSlope, double AccuracyStarSlope)
{
    public const double PoissonShape = 1e9;

    public double MissProbability(int misses) => PpTargetOutcomeModel.MissProbabilities(this, misses)[misses];

    public double ScenarioAccuracy(int misses, int objectCount) => objectCount <= 0 ? 0
        : Math.Clamp(HitAccuracy * (1 - Math.Clamp(misses, 0, objectCount) / (double)objectCount), 0, 1);

    public int ScenarioCombo(int misses, int objectCount, int maximumCombo)
    {
        if (maximumCombo <= 0) return 0;
        double fraction = misses == 0 ? FullComboEfficiency
            : PpTargetOutcomeModel.EvenSpreadComboFraction(misses, objectCount) * ComboEfficiency;
        return Math.Clamp((int)Math.Round(maximumCombo * Math.Clamp(fraction, 0, 1)), 0, maximumCombo);
    }
}

public sealed record PpScenario(int Misses, double Accuracy, int Combo, double Pp);

public sealed record PpOutcomeAtom(double Pp, double Probability);

public sealed record PpCalibrationSample(string Setup, bool LegacyScore, double Stars, DateTimeOffset PlayedAt, double Ratio);

public sealed record PpOutcomeEstimate(PpOutcomeDistribution Distribution, IReadOnlyList<PpScenario> Scenarios,
    IReadOnlyList<PpOutcomeAtom> Atoms, double AccuracySlope, double CalibrationFactor, int CalibrationSamples);

public static class PpTargetOutcomeModel
{
    public const string Version = "outcome-v1";
    private const int maximum_observations = 4_000;
    private const int history_days = 120;
    private const double prior_dispersion = 1.8;
    private const double prior_accuracy_spread = .012;
    internal const double PriorMissStarSlope = .9;
    internal const double PriorAccuracyStarSlope = -.012;
    private static readonly int[] scenario_grid = [0, 1, 2, 3, 5, 8, 13, 21, 34, 55, 89];
    private static readonly (double Z, double P)[] accuracy_nodes =
        [(-2.856970, .011257), (-1.355626, .222076), (0, .533333), (1.355626, .222076), (2.856970, .011257)];

    public static PpOutcomeProfile Build(IEnumerable<ScoreHistoryEntry> history, IEnumerable<LocalReplay>? local = null,
        IEnumerable<LocalBeatmapSet>? sets = null, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        DateTimeOffset reference = now ?? DateTimeOffset.UtcNow;
        var runs = (local ?? []).GroupBy(r => r.ScoreId).ToDictionary(g => g.Key, g => g.First());
        var difficulties = (sets ?? []).SelectMany(s => s.Difficulties).ToArray();
        var byLocal = difficulties.GroupBy(d => d.BeatmapId).ToDictionary(g => g.Key, g => g.First());
        var byOnline = difficulties.Where(d => d.OnlineId > 0).GroupBy(d => d.OnlineId).ToDictionary(g => g.Key, g => g.First());
        var observations = new List<PpOutcomeObservation>();
        foreach (ScoreHistoryEntry entry in history)
        {
            if (entry.PlayedAt > reference || entry.PlayedAt < reference.AddDays(-history_days)
                || !double.IsFinite(entry.StarRating) || entry.StarRating <= 0
                || !double.IsFinite(entry.Accuracy) || entry.Accuracy is < 0 or > 1 || entry.MissCount < 0)
                continue;
            IReadOnlyList<string> mods = PpTargetMods.Normalise(entry.Mods);
            if (mods.Any(m => m is "RX" or "AP" or "AT" or "CN")) continue;
            PpOutcomeSource? source = entry.IsLocal ? PpOutcomeSource.Local
                : entry.Provenance.HasFlag(ScoreHistoryProvenance.OnlineRecent) ? PpOutcomeSource.OnlineRecent
                : entry.Provenance.HasFlag(ScoreHistoryProvenance.OnlineBest) ? PpOutcomeSource.OnlineBest : null;
            if (source is null) continue;
            LocalReplay? run = entry.LocalScoreId is { } id ? runs.GetValueOrDefault(id) : null;
            // Stable stores completed plays only; its imports carry no explicit completion flag.
            bool passed = entry.Passed ?? (source == PpOutcomeSource.OnlineBest || run is { Passed: true });
            LocalBeatmapDifficulty? map = entry.LocalBeatmapId is { } localId ? byLocal.GetValueOrDefault(localId) : null;
            map ??= entry.OnlineBeatmapId > 0 ? byOnline.GetValueOrDefault(entry.OnlineBeatmapId) : null;
            int? objects = run?.HitStatistics is { } statistics && statistics.Great + statistics.Ok + statistics.Meh + statistics.Miss is > 0 and var total
                ? total : null;
            string mapKey = entry.OnlineBeatmapId > 0 ? $"online:{entry.OnlineBeatmapId}"
                : entry.LocalBeatmapId is { } key && key != Guid.Empty ? $"local:{key:N}" : $"score:{entry.Identity}";
            string? hash = run?.BeatmapHash is { Length: 32 or 64 } runHash ? runHash.ToLowerInvariant()
                : map?.BeatmapHash is { Length: 32 or 64 } mapHash ? mapHash.ToLowerInvariant() : null;
            observations.Add(new PpOutcomeObservation(entry.LocalScoreId, mapKey, entry.OnlineBeatmapId > 0 ? entry.OnlineBeatmapId : map?.OnlineId ?? 0,
                hash, entry.PlayedAt, entry.StarRating,
                entry.Bpm is > 0 && double.IsFinite(entry.Bpm.Value) ? entry.Bpm : positive(map?.Bpm),
                entry.LengthSeconds is > 0 ? entry.LengthSeconds : map is { LengthMilliseconds: > 0 } ? (int)(map.LengthMilliseconds / 1000) : null,
                PpTargetDifficulty.OverallDifficulty(map?.OverallDifficulty, mods, PpTargetDifficulty.ClockRate(mods, entry.ModsJson)),
                PpTargetDifficulty.ApproachRate(map?.ApproachRate, mods, PpTargetDifficulty.ClockRate(mods, entry.ModsJson)),
                mods, entry.ModsJson ?? "", Setup(mods, entry.ModsJson), entry.LegacyScore || run?.Origin == LocalLibraryOrigin.Stable,
                passed, source.Value, entry.Accuracy, entry.MissCount, entry.MaximumCombo > 0 ? entry.MaximumCombo : null, objects, null,
                entry.PerformancePoints is >= 0 && double.IsFinite(entry.PerformancePoints.Value) ? entry.PerformancePoints : null));
        }
        return create(reference, observations.OrderByDescending(o => o.PlayedAt).ThenBy(o => o.MapKey, StringComparer.Ordinal)
            .ThenBy(o => o.ScoreId).Take(maximum_observations).ToArray());
    }

    /// <summary>Fills map metadata that score history lacks from catalog difficulties the scan already knows.</summary>
    public static PpOutcomeProfile WithMaps(PpOutcomeProfile profile, IReadOnlyDictionary<int, OfficialBeatmapDifficulty> maps)
    {
        ArgumentNullException.ThrowIfNull(profile);
        bool changed = false;
        var observations = profile.Observations.Select(o =>
        {
            if (o.BeatmapId <= 0 || !maps.TryGetValue(o.BeatmapId, out var map)) return o;
            var enriched = o with
            {
                MapMaxCombo = o.MapMaxCombo ?? (map.MaximumCombo is > 0 ? map.MaximumCombo : null),
                OverallDifficulty = o.OverallDifficulty ?? PpTargetDifficulty.OverallDifficulty(map.OverallDifficulty, o.Mods, PpTargetDifficulty.ClockRate(o.Mods, o.ModsJson)),
                ApproachRate = o.ApproachRate ?? PpTargetDifficulty.ApproachRate(map.ApproachRate, o.Mods, PpTargetDifficulty.ClockRate(o.Mods, o.ModsJson)),
                Bpm = o.Bpm ?? positive(map.Bpm),
                LengthSeconds = o.LengthSeconds ?? (map.TotalLengthSeconds > 0 ? map.TotalLengthSeconds : null),
            };
            changed |= enriched != o;
            return enriched;
        }).ToArray();
        return changed ? create(profile.AsOf, observations) : profile;
    }

    public static PpOutcomeProfile Before(PpOutcomeProfile profile, DateTimeOffset time) =>
        create(time, profile.Observations.Where(o => o.PlayedAt < time).ToArray());

    public static string Setup(IEnumerable<string> mods, string? json) =>
        ScoreMods.Configuration(PpTargetMods.Normalise(mods), json ?? "", PpTargetMods.NormaliseForSkill);

    public static PpOutcomePrior Prior(PpPatternPrediction? prediction, double fallbackAccuracy, double attainability, int objectCount, double headAccuracyOffset = 0)
    {
        double misses = prediction?.ExpectedMissRate is { } rate && double.IsFinite(rate) && rate is >= 0 and <= 1
            ? rate * Math.Max(0, objectCount)
            : Math.Clamp(attainability, 0, 1) switch { >= .85 => .4, >= .6 => 1, >= .35 => 2, _ => 3 };
        double accuracy = prediction?.ExpectedAccuracy is { } head && double.IsFinite(head) && head is >= 0 and <= 1
            ? Math.Clamp(head + headAccuracyOffset, 0, 1)
            : double.IsFinite(fallbackAccuracy) ? Math.Clamp(fallbackAccuracy, 0, 1) : .95;
        return new PpOutcomePrior(misses, accuracy);
    }

    /// <summary>Replay head accuracy understates score accuracy on some maps; the mean paired gap corrects the pattern prior.</summary>
    public static double HeadAccuracyOffset(PpOutcomeProfile? profile, PpPatternProfile? patterns)
    {
        if (profile is null || patterns is null) return 0;
        var heads = patterns.Evidence.Where(e => e.Passed && e.Outcomes.ContainsKey("Overall"))
            .GroupBy(e => e.ScoreId).ToDictionary(g => g.Key, g => g.First().Outcomes["Overall"]);
        double sum = 0, count = 0;
        foreach (var o in profile.Observations)
        {
            if (!o.Passed || o.ScoreId is not { } id || !heads.TryGetValue(id, out var head) || head.ObjectCount < 20) continue;
            sum += hitAccuracy(o, o.ObjectCount ?? head.ObjectCount) - head.Accuracy;
            count++;
        }
        return Math.Clamp(sum / (count + 3), -.05, .05);
    }

    public static PpOutcomeDistribution Fit(PpOutcomeProfile? profile, PpOutcomeTarget target, PpOutcomePrior prior,
        PpPatternProfile? patterns = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(prior);
        int objects = Math.Max(1, target.ObjectCount);
        var features = patterns is null || target.Features is null ? null
            : patterns.Evidence.GroupBy(e => e.ScoreId).ToDictionary(g => g.Key, g => g.First().Features);
        var candidates = new List<Sample>();
        foreach (var o in profile?.Observations ?? [])
        {
            if (!o.Passed || o.Setup != target.Setup || o.PlayedAt > profile!.AsOf || !double.IsFinite(o.Stars)
                || Math.Abs(o.Stars - target.Stars) > 1.5 || !double.IsFinite(o.Accuracy)) continue;
            double baseWeight = recency(profile.AsOf, o.PlayedAt, 14) * contextWeight(o, target);
            if (features is not null && o.ScoreId is { } id && features.TryGetValue(id, out var geometry))
                baseWeight *= .4 + .6 * PpTargetPatternModel.Similarity(target.Features!, geometry, "Overall");
            if (baseWeight < 1e-4) continue;
            double exposure = o.ObjectCount is > 0 ? o.ObjectCount.Value
                : o.LengthSeconds is > 0 && target.LengthSeconds > 0 ? o.LengthSeconds.Value * objects / (double)target.LengthSeconds : objects;
            candidates.Add(new Sample(o, baseWeight, o.Misses * objects / Math.Max(1, exposure), hitAccuracy(o, exposure)));
        }
        double ess(IEnumerable<Sample> items)
        {
            double sum = 0, squares = 0;
            foreach (var s in items) { double w = s.Weight * starKernel(s.Observation.Stars - target.Stars); sum += w; squares += w * w; }
            return squares <= 0 ? 0 : sum * sum / squares;
        }
        var primary = candidates.Where(s => s.Observation.LegacyScore == target.LegacyScore && s.Observation.Source != PpOutcomeSource.OnlineBest).ToList();
        bool crossMode = false, bests = false;
        var used = new List<Sample>(primary);
        if (ess(primary) < 6)
        {
            double shift = modeShift(candidates, target.LegacyScore);
            var other = candidates.Where(s => s.Observation.LegacyScore != target.LegacyScore && s.Observation.Source != PpOutcomeSource.OnlineBest)
                .Select(s => s with { Weight = s.Weight * .4, HitAccuracy = Math.Clamp(s.HitAccuracy + shift, 0, 1) }).ToArray();
            crossMode = other.Length > 0;
            used.AddRange(other);
        }
        if (ess(used) < 4)
        {
            // Best scores are selected successes; they only fill a thin history, at low weight.
            var best = candidates.Where(s => s.Observation.Source == PpOutcomeSource.OnlineBest)
                .Select(s => s.Observation.LegacyScore == target.LegacyScore ? s with { Weight = s.Weight * .3 } : s with { Weight = s.Weight * .12 }).ToArray();
            bests = best.Length > 0;
            used.AddRange(best);
        }

        double missSlope = slope(used, target.Stars, s => Math.Log((s.Misses + .5) / objects), PriorMissStarSlope, 0, 2.5);
        double accuracySlope = slope(used, target.Stars, s => s.HitAccuracy, PriorAccuracyStarSlope, -.05, 0);
        var adjusted = balance(used.Select(s =>
        {
            double delta = target.Stars - s.Observation.Stars;
            return s with
            {
                Weight = s.Weight * starKernel(-delta),
                Misses = s.Misses * Math.Exp(missSlope * delta),
                HitAccuracy = Math.Clamp(s.HitAccuracy + accuracySlope * delta, 0, 1),
            };
        })).Where(s => s.Weight > 1e-6).ToArray();
        // Players improve: a shrunk monthly trend moves recent-weighted evidence to today.
        if (profile is not null)
        {
            double months(Sample s) => -(profile.AsOf - s.Observation.PlayedAt).TotalDays / 30;
            double missTrend = ShrunkSlope(adjusted.Select(s => (s.Weight, months(s), Math.Log((s.Misses + .5) / objects))), 0, -1, .5);
            double accuracyTrend = ShrunkSlope(adjusted.Select(s => (s.Weight, months(s), s.HitAccuracy)), 0, -.02, .03);
            adjusted = adjusted.Select(s => s with
            {
                Misses = s.Misses * Math.Exp(-missTrend * months(s)),
                HitAccuracy = Math.Clamp(s.HitAccuracy - accuracyTrend * months(s), 0, 1),
            }).ToArray();
        }

        double weight = adjusted.Sum(s => s.Weight);
        double squared = adjusted.Sum(s => s.Weight * s.Weight);
        double effective = squared > 0 ? weight * weight / squared : 0;
        double missMean = weight > 0 ? adjusted.Sum(s => s.Weight * s.Misses) / weight : 0;
        double missVariance = weight > 0 ? adjusted.Sum(s => s.Weight * Math.Pow(s.Misses - missMean, 2)) / weight * (effective > 1 ? effective / (effective - 1) : 1) : 0;
        double mean = (weight * missMean + Math.Max(0, prior.MissMean)) / (weight + 1);
        double observedDispersion = missMean > 1e-6 ? Math.Max(1, missVariance / missMean) : 1;
        double dispersion = (effective * observedDispersion + 3 * prior_dispersion) / (effective + 3);
        double shape = dispersion > 1.02 ? Math.Max(.05, mean / (dispersion - 1)) : PpOutcomeDistribution.PoissonShape;

        double accuracy = (adjusted.Sum(s => s.Weight * s.HitAccuracy) + .5 * prior.HitAccuracy) / (weight + .5);
        double accuracyVariance = weight > 0 ? adjusted.Sum(s => s.Weight * Math.Pow(s.HitAccuracy - accuracy, 2)) / weight : 0;
        double spread = Math.Sqrt((effective * accuracyVariance + 3 * prior_accuracy_spread * prior_accuracy_spread) / (effective + 3));

        double efficiency(bool fullCombo)
        {
            double sum = 0, total = 0;
            foreach (var s in adjusted)
            {
                if (s.Observation.Combo is not { } combo || s.Observation.MapMaxCombo is not > 0 || (s.Observation.Misses == 0) != fullCombo) continue;
                double fraction = combo / (double)s.Observation.MapMaxCombo.Value;
                double expected = fullCombo ? 1 : EvenSpreadComboFraction(s.Observation.Misses, s.Observation.ObjectCount ?? s.Observation.MapMaxCombo.Value);
                if (expected <= 0) continue;
                sum += s.Weight * Math.Clamp(fraction / expected, .05, 1.5);
                total += s.Weight;
            }
            return (sum + 1.5) / (total + 1.5);
        }

        return new PpOutcomeDistribution(mean, shape, Math.Clamp(accuracy, 0, 1), Math.Clamp(spread, .003, .08),
            Math.Clamp(efficiency(true), .05, 1), Math.Clamp(efficiency(false), .05, 1.5), effective,
            adjusted.Select(s => s.Observation.MapKey).Distinct().Count(), adjusted.Length, crossMode, bests, missSlope, accuracySlope);
    }

    /// <summary>Recorded PP relative to the official calculator at the same score statistics, shrunk towards one.</summary>
    public static (double Factor, int Samples) Calibration(IEnumerable<PpCalibrationSample>? samples, string setup, bool legacyScore, double stars, DateTimeOffset asOf)
    {
        double sum = 0, weight = 0;
        int count = 0;
        foreach (var sample in samples ?? [])
        {
            if (sample.Setup != setup || !double.IsFinite(sample.Ratio) || sample.Ratio <= 0 || Math.Abs(sample.Stars - stars) > 2) continue;
            double w = Math.Exp(-Math.Pow(sample.Stars - stars, 2)) * recency(asOf, sample.PlayedAt, 30) * (sample.LegacyScore == legacyScore ? 1 : .5);
            if (w < .02) continue;
            sum += w * Math.Log(sample.Ratio);
            weight += w;
            count++;
        }
        return (Math.Clamp(Math.Exp(sum / (weight + 2)), .75, 1.3), count);
    }

    public static IReadOnlyList<int> ScenarioMisses(PpOutcomeDistribution distribution, int objectCount)
    {
        double[] probabilities = MissProbabilities(distribution, Math.Min(objectCount, 400));
        double cumulative = 0;
        int lower = -1, upper = probabilities.Length - 1;
        for (int i = 0; i < probabilities.Length; i++)
        {
            cumulative += probabilities[i];
            if (lower < 0 && cumulative >= .005) lower = i;
            if (cumulative >= .995) { upper = i; break; }
        }
        // Official calculations where the probability mass is, bracketed by grid points; the no-miss score always.
        int from = scenario_grid.LastOrDefault(m => m <= Math.Max(0, lower));
        int to = scenario_grid.FirstOrDefault(m => m >= Math.Max(3, upper), scenario_grid[^1]);
        return scenario_grid.Where(m => m == 0 || m >= from && m <= to).Where(m => m <= Math.Max(0, objectCount)).Take(8).ToArray();
    }

    public static double[] MissProbabilities(PpOutcomeDistribution distribution, int maximum)
    {
        maximum = Math.Max(0, maximum);
        var values = new double[maximum + 1];
        double mean = Math.Max(1e-9, distribution.MissMean), shape = distribution.MissShape;
        bool poisson = shape >= PpOutcomeDistribution.PoissonShape / 10;
        double ratio = poisson ? 0 : mean / (shape + mean);
        values[0] = poisson ? Math.Exp(-mean) : Math.Exp(shape * Math.Log(shape / (shape + mean)));
        for (int m = 1; m <= maximum; m++)
            values[m] = values[m - 1] * (poisson ? mean / m : (m - 1 + shape) / m * ratio);
        return values;
    }

    public static double EvenSpreadComboFraction(int misses, int objectCount)
    {
        int count = Math.Max(0, objectCount);
        if (count == 0) return 0;
        misses = Math.Clamp(misses, 0, count);
        if (misses == 0) return 1;
        int segments = misses + 1;
        double harmonic = 0;
        for (int i = 1; i <= segments; i++) harmonic += 1d / i;
        double successful = count - misses;
        return Math.Min(successful, successful * harmonic / segments) / count;
    }

    /// <summary>Discrete pass-conditional PP distribution from official scenario results.</summary>
    public static IReadOnlyList<PpOutcomeAtom> Integrate(PpOutcomeDistribution distribution, IReadOnlyList<PpScenario> scenarios,
        int objectCount, double accuracySlope, double ceilingPp, double factor = 1, int maximumAtoms = 48)
    {
        var points = scenarios.Where(s => double.IsFinite(s.Pp) && s.Pp >= 0).OrderBy(s => s.Misses).DistinctBy(s => s.Misses).ToArray();
        if (points.Length == 0) return [];
        int maximum = Math.Min(Math.Max(0, objectCount), 400);
        double[] probabilities = MissProbabilities(distribution, maximum);
        double total = 0;
        int last = maximum;
        for (int m = 0; m <= maximum; m++)
            if ((total += probabilities[m]) >= .9995) { last = m; break; }
        double slope = double.IsFinite(accuracySlope) ? Math.Max(0, accuracySlope) : 0;
        var atoms = new List<PpOutcomeAtom>((last + 1) * accuracy_nodes.Length);
        for (int m = 0; m <= last; m++)
        {
            double pp = interpolate(points, m);
            double exposure = objectCount > 0 ? 1 - m / (double)objectCount : 0;
            foreach (var (z, p) in accuracy_nodes)
            {
                double hit = Math.Clamp(distribution.HitAccuracy + z * distribution.AccuracySpread, 0, 1);
                double scale = Math.Max(.05, 1 + slope * (hit - distribution.HitAccuracy) * exposure);
                atoms.Add(new PpOutcomeAtom(Math.Clamp(pp * scale * factor, 0, Math.Max(0, ceilingPp)), probabilities[m] * p));
            }
        }
        return compress(atoms, maximumAtoms);
    }

    public static double Mean(IReadOnlyList<PpOutcomeAtom> atoms)
    {
        double weight = atoms.Sum(a => a.Probability);
        return weight <= 0 ? 0 : atoms.Sum(a => a.Pp * a.Probability) / weight;
    }

    public static double Quantile(IReadOnlyList<PpOutcomeAtom> atoms, double fraction)
    {
        double weight = atoms.Sum(a => a.Probability), threshold = Math.Clamp(fraction, 0, 1) * weight, cumulative = 0;
        foreach (var atom in atoms.OrderBy(a => a.Pp))
            if ((cumulative += atom.Probability) >= threshold - 1e-12) return atom.Pp;
        return atoms.Count == 0 ? 0 : atoms.Max(a => a.Pp);
    }

    /// <summary>
    /// Highest PP whose chance of being reached by the best of <paramref name="tries"/> attempts is at least
    /// <paramref name="reach"/>; failed attempts earn nothing. Null when even a pass is unlikely enough.
    /// </summary>
    public static (double Pp, double Probability)? BestOf(IReadOnlyList<PpOutcomeAtom> atoms, double passChance, int tries, double reach)
    {
        if (atoms.Count == 0 || !double.IsFinite(passChance) || passChance <= 0 || tries < 1) return null;
        double p = Math.Clamp(passChance, 0, 1), weight = atoms.Sum(a => a.Probability);
        var ordered = atoms.OrderBy(a => a.Pp).ToArray();
        (double, double)? result = null;
        double below = 0;
        foreach (var atom in ordered)
        {
            double chance = 1 - Math.Pow(1 - p + p * below / weight, tries);
            if (chance < reach - 1e-12) break;
            result = (atom.Pp, chance);
            below += atom.Probability;
        }
        return result;
    }

    private static PpOutcomeProfile create(DateTimeOffset asOf, PpOutcomeObservation[] observations) =>
        new(identity(asOf, observations), asOf, observations);

    private static string identity(DateTimeOffset asOf, PpOutcomeObservation[] observations)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] number = new byte[8];
        void text(string? value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(number, value?.Length ?? -1);
            hash.AppendData(number[..4]);
            if (value is not null) hash.AppendData(Encoding.UTF8.GetBytes(value));
        }
        void real(double? value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(number, value is { } v ? BitConverter.DoubleToInt64Bits(v) : long.MinValue);
            hash.AppendData(number);
        }
        text(Version);
        real(asOf.UtcDateTime.Date.Ticks);
        foreach (var o in observations)
        {
            text(o.ScoreId?.ToString("N")); text(o.MapKey); real(o.BeatmapId); text(o.BeatmapHash); real(o.PlayedAt.UtcTicks);
            real(o.Stars); real(o.Bpm); real(o.LengthSeconds); real(o.OverallDifficulty); real(o.ApproachRate);
            text(string.Join(',', o.Mods)); text(o.ModsJson); text(o.Setup); real(o.LegacyScore ? 1 : 0); real(o.Passed ? 1 : 0);
            real((int)o.Source); real(o.Accuracy); real(o.Misses); real(o.Combo); real(o.ObjectCount); real(o.MapMaxCombo); real(o.Pp);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private sealed record Sample(PpOutcomeObservation Observation, double Weight, double Misses, double HitAccuracy);

    private static double hitAccuracy(PpOutcomeObservation o, double exposure)
    {
        double objects = Math.Max(o.Misses + 1, o.ObjectCount ?? exposure);
        return Math.Clamp(o.Accuracy * objects / (objects - o.Misses), 0, 1);
    }

    private static double recency(DateTimeOffset asOf, DateTimeOffset played, double halfLifeDays) =>
        Math.Pow(.5, Math.Max(0, (asOf.UtcDateTime.Date - played.UtcDateTime.Date).TotalDays) / halfLifeDays);

    private static double starKernel(double delta) => Math.Exp(-Math.Pow(delta / .75, 2));

    private static double contextWeight(PpOutcomeObservation o, PpOutcomeTarget t)
    {
        double w = o.OverallDifficulty is { } od && t.OverallDifficulty is { } tod ? Math.Exp(-Math.Pow((od - tod) / 2, 2)) : .85;
        w *= o.ApproachRate is { } ar && t.ApproachRate is { } tar ? Math.Exp(-Math.Pow((ar - tar) / 1.5, 2)) : .85;
        w *= o.Bpm is > 0 && t.Bpm is > 0 ? Math.Exp(-Math.Pow(Math.Log(o.Bpm.Value / t.Bpm.Value) / .3, 2)) : .85;
        w *= o.LengthSeconds is > 0 && t.LengthSeconds > 0 ? Math.Exp(-Math.Pow(Math.Log(o.LengthSeconds.Value / (double)t.LengthSeconds) / .8, 2)) : .85;
        return w;
    }

    private static double slope(IEnumerable<Sample> samples, double stars, Func<Sample, double> value, double prior, double minimum, double maximum) =>
        ShrunkSlope(samples.Select(s => (s.Weight, s.Observation.Stars - stars, value(s))), prior, minimum, maximum);

    // Monotone weighted least squares on the star difference, shrunk towards a prior slope when sparse.
    internal static double ShrunkSlope(IEnumerable<(double Weight, double X, double Y)> samples, double prior, double minimum, double maximum)
    {
        var items = samples.Where(i => i.Weight > 0 && double.IsFinite(i.X) && double.IsFinite(i.Y)).ToArray();
        double w = 0, x = 0, y = 0;
        foreach (var item in items) { w += item.Weight; x += item.Weight * item.X; y += item.Weight * item.Y; }
        if (w <= 0) return Math.Clamp(prior, minimum, maximum);
        x /= w; y /= w;
        double sxx = 0, sxy = 0;
        foreach (var item in items) { sxx += item.Weight * (item.X - x) * (item.X - x); sxy += item.Weight * (item.X - x) * (item.Y - y); }
        double estimate = sxx > 1e-9 ? sxy / sxx : prior;
        return Math.Clamp((sxx * estimate + 3 * prior) / (sxx + 3), minimum, maximum);
    }

    private static double modeShift(List<Sample> samples, bool legacyScore)
    {
        var same = samples.Where(s => s.Observation.LegacyScore == legacyScore).GroupBy(s => s.Observation.MapKey)
            .ToDictionary(g => g.Key, g => g.Average(s => s.HitAccuracy));
        double sum = 0; int count = 0;
        foreach (var group in samples.Where(s => s.Observation.LegacyScore != legacyScore).GroupBy(s => s.Observation.MapKey))
            if (same.TryGetValue(group.Key, out double accuracy)) { sum += accuracy - group.Average(s => s.HitAccuracy); count++; }
        return Math.Clamp(sum / (count + 3), -.03, .03);
    }

    // Many retries of one map describe its variability, but cannot outweigh several other maps.
    private static IEnumerable<Sample> balance(IEnumerable<Sample> samples) => samples.GroupBy(s => s.Observation.MapKey).SelectMany(g =>
    {
        double total = g.Sum(s => s.Weight), cap = 3 * g.Max(s => s.Weight);
        double scale = total > cap ? cap / total : 1;
        return g.Select(s => s with { Weight = s.Weight * scale });
    });

    private static double interpolate(PpScenario[] points, int misses)
    {
        if (points.Length == 1 || misses <= points[0].Misses) return points[0].Pp;
        for (int i = 1; i < points.Length; i++)
            if (misses <= points[i].Misses) return logLinear(points[i - 1], points[i], misses);
        return Math.Min(points[^1].Pp, logLinear(points[^2], points[^1], misses));
    }

    private static double logLinear(PpScenario a, PpScenario b, int misses)
    {
        double left = Math.Log(Math.Max(1e-6, a.Pp)), right = Math.Log(Math.Max(1e-6, b.Pp));
        return Math.Exp(left + (right - left) * (misses - a.Misses) / Math.Max(1, b.Misses - a.Misses));
    }

    private static IReadOnlyList<PpOutcomeAtom> compress(List<PpOutcomeAtom> atoms, int maximum)
    {
        double total = atoms.Sum(a => a.Probability);
        if (total <= 0) return [];
        var ordered = atoms.Where(a => a.Probability > 0).OrderBy(a => a.Pp).ToArray();
        var result = new List<PpOutcomeAtom>(maximum);
        double bucket = total / Math.Max(1, maximum), weight = 0, sum = 0;
        foreach (var atom in ordered)
        {
            weight += atom.Probability;
            sum += atom.Pp * atom.Probability;
            if (weight >= bucket - 1e-15)
            {
                result.Add(new PpOutcomeAtom(sum / weight, weight / total));
                weight = sum = 0;
            }
        }
        if (weight > 0) result.Add(new PpOutcomeAtom(sum / weight, weight / total));
        return result;
    }

    private static double? positive(double? value) => value is > 0 && double.IsFinite(value.Value) ? value : null;
}

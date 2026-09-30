namespace AimMod.Desktop.PpTargets;

/// <summary>Score accuracy of a passed attempt (misses included), as a fraction; Low/High are the 25th/75th percentiles.</summary>
public sealed record PpForecastAccuracy(double Expected, double Median, double Low, double High);

/// <summary>
/// Misses of a passed attempt. Low/High are the 25th/75th percentiles; <see cref="Buckets"/> holds the chance of
/// exactly 0, 1, 2, 3 and 4 misses followed by 5 or more.
/// </summary>
public sealed record PpForecastMisses(double Expected, int Median, int Low, int High, double FullComboChance,
    IReadOnlyList<double> Buckets, int MostLikely)
{
    public const int BucketCount = 6;
}

public sealed record PpForecastCombo(int Typical, int Maximum, double ExpectedFraction);

/// <summary>Predicted hit timing in real-time ms. A negative offset is early. The unstable rate is ten times the offset deviation.</summary>
public sealed record PpForecastTiming(double MeanOffsetMs, double UnstableRate, int Plays, int Maps);

/// <summary>PP if the attempt passes: percentiles, the mean (first try PP) and the 100% full-combo ceiling.</summary>
public sealed record PpForecastPpDistribution(double P10, double P25, double Median, double P75, double P90, double Mean, double FullComboCeiling);

/// <summary>
/// The predicted performance behind a PP forecast. The typical play (median misses at the fitted hit accuracy and
/// the combo those misses usually leave) is what the official calculator scores as <see cref="CalculatorPpAtTypicalStats"/>;
/// calibration and the retry trend turn it into <see cref="PpAtTypicalStats"/>.
/// </summary>
public sealed record PpTargetForecastBreakdown(
    int ObjectCount,
    PpForecastAccuracy Accuracy,
    PpForecastMisses Misses,
    PpForecastCombo Combo,
    PpForecastTiming? Timing,
    PpForecastPpDistribution Pp,
    double TypicalAccuracy,
    int TypicalMisses,
    int TypicalCombo,
    double PpAtTypicalStats,
    double CalculatorPpAtTypicalStats,
    double CalibrationFactor,
    double LearningAdjustment);

public static class PpTargetForecastBreakdownModel
{
    private const int maximum_misses = 400;

    public static PpTargetForecastBreakdown? Create(PpTargetEstimate? estimate, double learningAdjustment = 1)
    {
        if (estimate?.Outcome is not { Atoms.Count: > 0, Scenarios.Count: > 0 } outcome) return null;
        var distribution = outcome.Distribution;
        double ceiling = double.IsFinite(estimate.RealisticMaximumPp) && estimate.RealisticMaximumPp > 0 ? estimate.RealisticMaximumPp : double.MaxValue;
        double next = double.IsFinite(learningAdjustment) && learningAdjustment > 0 ? learningAdjustment : 1;
        int objects = ObjectCount(outcome);
        int maximumCombo = MaximumCombo(outcome, objects);

        double[] probabilities = MissDistribution(distribution, objects);
        var misses = Misses(distribution, probabilities);
        var accuracy = Accuracy(distribution, probabilities, objects);

        double comboFraction = 0;
        for (int m = 0; m < probabilities.Length; m++)
            comboFraction += probabilities[m] * (maximumCombo > 0 ? distribution.ScenarioCombo(m, objects, maximumCombo) / (double)maximumCombo : 0);
        int typicalMisses = misses.Median;
        int typicalCombo = distribution.ScenarioCombo(typicalMisses, objects, maximumCombo);

        double pp(double fraction) => finite(Math.Min(ceiling, PpTargetOutcomeModel.Quantile(outcome.Atoms, fraction) * next));
        var ppDistribution = new PpForecastPpDistribution(pp(.1), pp(.25), pp(.5), pp(.75), pp(.9),
            finite(Math.Min(ceiling, PpTargetOutcomeModel.Mean(outcome.Atoms) * next)), ceiling == double.MaxValue ? 0 : ceiling);
        double calculator = finite(PpTargetOutcomeModel.ScenarioPp(outcome.Scenarios, typicalMisses));
        double factor = double.IsFinite(outcome.CalibrationFactor) && outcome.CalibrationFactor > 0 ? outcome.CalibrationFactor : 1;

        return new PpTargetForecastBreakdown(objects, accuracy, misses,
            new PpForecastCombo(typicalCombo, maximumCombo, finite(Math.Clamp(comboFraction, 0, 1))),
            Timing(estimate.PatternPrediction), ppDistribution,
            finite(distribution.ScenarioAccuracy(typicalMisses, objects)), typicalMisses, typicalCombo,
            finite(Math.Min(ceiling, calculator * factor * next)), calculator, factor, next);
    }

    public static PpForecastTiming? Timing(PpPatternPrediction? prediction) =>
        prediction?.Timing is { HitErrorMeanMs: { } mean, UnstableRate: { } ur } fit && double.IsFinite(mean) && double.IsFinite(ur)
            ? new PpForecastTiming(mean, ur, fit.TimingPlays, fit.DistinctMaps) : null;

    /// <summary>Miss-count probabilities of a pass, normalised and truncated once 99.95% of the mass is covered.</summary>
    public static double[] MissDistribution(PpOutcomeDistribution distribution, int objectCount)
    {
        double[] raw = PpTargetOutcomeModel.MissProbabilities(distribution, Math.Clamp(objectCount, 0, maximum_misses));
        double total = raw.Where(double.IsFinite).Sum();
        if (total <= 0) return [1];
        int last = raw.Length - 1;
        double cumulative = 0;
        for (int m = 0; m < raw.Length; m++)
            if ((cumulative += raw[m]) >= .9995 * total) { last = m; break; }
        var values = new double[last + 1];
        double kept = raw.Take(last + 1).Sum();
        for (int m = 0; m <= last; m++) values[m] = double.IsFinite(raw[m]) ? raw[m] / kept : 0;
        return values;
    }

    public static PpForecastMisses Misses(PpOutcomeDistribution distribution, double[] probabilities)
    {
        var buckets = new double[PpForecastMisses.BucketCount];
        for (int m = 0; m < probabilities.Length; m++) buckets[Math.Min(m, buckets.Length - 1)] += probabilities[m];
        int mostLikely = 0;
        for (int i = 1; i < buckets.Length; i++) if (buckets[i] > buckets[mostLikely] + 1e-12) mostLikely = i;
        int quantile(double q)
        {
            double cumulative = 0;
            for (int m = 0; m < probabilities.Length; m++)
                if ((cumulative += probabilities[m]) >= q - 1e-12) return m;
            return probabilities.Length - 1;
        }
        double expected = 0;
        for (int m = 0; m < probabilities.Length; m++) expected += m * probabilities[m];
        return new PpForecastMisses(finite(expected), quantile(.5), quantile(.25), quantile(.75), finite(probabilities[0]), buckets, mostLikely);
    }

    /// <summary>
    /// Score accuracy mixes the normal hit accuracy with the miss count: a play with <c>m</c> misses scores
    /// <c>hit × (1 - m / objects)</c>. Percentiles solve the mixture's distribution function by bisection.
    /// </summary>
    public static PpForecastAccuracy Accuracy(PpOutcomeDistribution distribution, double[] probabilities, int objectCount)
    {
        int objects = Math.Max(1, objectCount);
        double hit = Math.Clamp(distribution.HitAccuracy, 0, 1), spread = Math.Max(1e-6, distribution.AccuracySpread);
        double cdf(double accuracy)
        {
            double sum = 0;
            for (int m = 0; m < probabilities.Length; m++)
            {
                double exposure = 1 - Math.Min(m, objects) / (double)objects;
                // Hit accuracy is clamped to one, so every accuracy at or above the exposure is certain.
                sum += probabilities[m] * (exposure <= 0 || accuracy >= exposure ? 1 : NormalCdf((accuracy / exposure - hit) / spread));
            }
            return sum;
        }
        double quantile(double q)
        {
            double low = 0, high = 1;
            for (int i = 0; i < 48; i++)
            {
                double middle = (low + high) / 2;
                if (cdf(middle) < q) low = middle; else high = middle;
            }
            return (low + high) / 2;
        }
        double expectedMisses = 0;
        for (int m = 0; m < probabilities.Length; m++) expectedMisses += Math.Min(m, objects) * probabilities[m];
        return new PpForecastAccuracy(finite(Math.Clamp(hit * (1 - expectedMisses / objects), 0, 1)),
            finite(quantile(.5)), finite(quantile(.25)), finite(quantile(.75)));
    }

    /// <summary>Standard normal distribution function (Abramowitz and Stegun 7.1.26, |error| &lt; 1.5e-7).</summary>
    public static double NormalCdf(double z)
    {
        if (!double.IsFinite(z)) return z > 0 ? 1 : 0;
        double x = Math.Abs(z) / Math.Sqrt(2);
        double t = 1 / (1 + .3275911 * x);
        double erf = 1 - ((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t - .284496736) * t + .254829592) * t * Math.Exp(-x * x);
        return z >= 0 ? (1 + erf) / 2 : (1 - erf) / 2;
    }

    /// <summary>Object count of the calculated map; older cached estimates infer it from a missed scenario's accuracy.</summary>
    public static int ObjectCount(PpOutcomeEstimate outcome)
    {
        if (outcome.ObjectCount > 0) return outcome.ObjectCount;
        double hit = outcome.Distribution.HitAccuracy;
        foreach (var scenario in outcome.Scenarios.Where(s => s.Misses > 0).OrderBy(s => s.Misses))
        {
            double ratio = hit > 0 ? scenario.Accuracy / hit : 1;
            if (ratio is > 0 and < 1 && double.IsFinite(ratio))
                return Math.Clamp((int)Math.Round(scenario.Misses / (1 - ratio)), scenario.Misses + 1, 50_000);
        }
        return outcome.MaximumCombo > 0 ? outcome.MaximumCombo : 1_000;
    }

    public static int MaximumCombo(PpOutcomeEstimate outcome, int objectCount)
    {
        if (outcome.MaximumCombo > 0) return outcome.MaximumCombo;
        if (outcome.Scenarios.FirstOrDefault(s => s.Misses == 0) is { Combo: > 0 } clean && outcome.Distribution.FullComboEfficiency > 0)
            return Math.Max(clean.Combo, (int)Math.Round(clean.Combo / Math.Min(1, outcome.Distribution.FullComboEfficiency)));
        return Math.Max(0, objectCount);
    }

    private static double finite(double value) => double.IsFinite(value) ? value : 0;
}

namespace AimMod.Desktop.PpTargets;

/// <param name="PpIfPass">Expected PP of the next attempt given that it passes (not weighted by pass chance).</param>
/// <param name="ExpectedEarnedPp">Pass chance times PP if passed; a ranking value, since failed attempts earn nothing.</param>
/// <param name="TargetPp">Highest PP the best of <paramref name="Tries"/> attempts reaches with <see cref="PpTargetForecastModel.TargetReach"/> probability.</param>
public sealed record PpTargetForecast(double PpIfPass, PpTargetRange PpIfPassRange, double? PassChance, double? ExpectedEarnedPp,
    double? TargetPp, int Tries, double? ReachProbability, int PreviousTries, PpTargetConfidence Confidence,
    double CalibrationFactor, int CalibrationSamples, double EffectiveSamples, int EvidenceMaps, bool CrossMode, bool UsesBestScores,
    int LearningSessions, double LearningAdjustment, PpTargetForecastBreakdown? Breakdown = null);

public static class PpTargetForecastModel
{
    public const double TargetReach = .6;
    public const int MinimumTries = 3;
    public const int MaximumTries = 5;

    /// <summary>Enough attempts for a 90% chance of at least one pass, within a short focused session.</summary>
    public static int Tries(double? passChance)
    {
        if (passChance is not { } p || !double.IsFinite(p) || p <= 0) return MaximumTries;
        if (p >= .999) return MinimumTries;
        return Math.Clamp((int)Math.Ceiling(Math.Log(.1) / Math.Log(1 - p)), MinimumTries, MaximumTries);
    }

    public static PpTargetForecast? Forecast(PpTargetEstimate? estimate, PpTargetPassEstimate? pass, PpTargetLearningForecast? learning)
    {
        if (estimate?.Outcome is not { Atoms.Count: > 0 } outcome || !double.IsFinite(estimate.RealisticMaximumPp) || estimate.RealisticMaximumPp <= 0)
            return null;
        double ceiling = estimate.RealisticMaximumPp;
        double? chance = pass is { Probability: var p } && double.IsFinite(p) ? Math.Clamp(p, 0, 1) : null;
        int tries = Tries(chance);
        double next = learning?.Ratio(0) ?? 1;
        double session = learning?.MeanRatio(tries) ?? 1;
        double ppIfPass = Math.Min(ceiling, PpTargetOutcomeModel.Mean(outcome.Atoms) * next);
        var range = new PpTargetRange(Math.Min(ceiling, PpTargetOutcomeModel.Quantile(outcome.Atoms, .2) * next),
            Math.Min(ceiling, PpTargetOutcomeModel.Quantile(outcome.Atoms, .8) * next));
        var scaled = session == 1 ? outcome.Atoms
            : outcome.Atoms.Select(a => a with { Pp = Math.Min(ceiling, a.Pp * session) }).ToArray();
        var reach = chance is { } c ? PpTargetOutcomeModel.BestOf(scaled, c, tries, TargetReach) : null;
        var distribution = outcome.Distribution;
        var confidence = distribution.EffectiveSamples >= 12 && distribution.Maps >= 5 ? PpTargetConfidence.High
            : distribution.EffectiveSamples >= 4 && distribution.Maps >= 2 ? PpTargetConfidence.Medium : PpTargetConfidence.Low;
        confidence = (PpTargetConfidence)Math.Min((int)confidence, (int)(pass?.Confidence ?? PpTargetConfidence.Low));
        return new PpTargetForecast(ppIfPass, range, chance, chance is { } earned ? ppIfPass * earned : null,
            reach?.Pp, tries, reach?.Probability, learning?.PreviousTries ?? 0, confidence,
            outcome.CalibrationFactor, outcome.CalibrationSamples, distribution.EffectiveSamples, distribution.Maps,
            distribution.CrossMode || pass?.CrossMode == true, distribution.UsesBestScores, learning?.Sessions ?? 0, next,
            PpTargetForecastBreakdownModel.Create(estimate, next));
    }
}

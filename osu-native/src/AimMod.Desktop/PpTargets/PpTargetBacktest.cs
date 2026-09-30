using System.Globalization;
using System.Text;

namespace AimMod.Desktop.PpTargets;

/// <summary>PP of one map, mod setup and scoring mode at given score statistics: the calculator, or a stand-in for it.</summary>
public interface IPpScenarioCurve
{
    int ObjectCount { get; }
    int MaximumCombo { get; }
    double Pp(double accuracy, int misses, int combo);
}

public sealed record PpBacktestOptions(double SessionGapHours = 2, int MinimumPriorPlays = 3);

public sealed record PpBacktestSession(DateTimeOffset Start, string Band, double PpIfPass, double LegacyPp, double? PassChance, double? LegacyPassRate,
    double? TargetPp, int Tries, double SessionBest, IReadOnlyList<double> PassPp, int Failures, IReadOnlyList<double> CurvePp);

/// <param name="Bias">Mean error relative to the mean actual value.</param>
public sealed record PpBacktestMetrics(int Count, double MeanError, double MeanAbsoluteError, double Bias)
{
    public static PpBacktestMetrics Of(IEnumerable<(double Predicted, double Actual)> pairs)
    {
        var items = pairs.Where(p => double.IsFinite(p.Predicted) && double.IsFinite(p.Actual)).ToArray();
        if (items.Length == 0) return new(0, 0, 0, 0);
        double actual = items.Average(p => p.Actual);
        return new(items.Length, items.Average(p => p.Predicted - p.Actual), items.Average(p => Math.Abs(p.Predicted - p.Actual)),
            actual > 0 ? items.Average(p => p.Predicted - p.Actual) / actual : 0);
    }
}

/// <param name="Earned">Pass chance times PP if pass against every attempt's actual PP, failures earning zero.</param>
public sealed record PpBacktestGroup(string Name, int Sessions, PpBacktestMetrics PpIfPass, PpBacktestMetrics LegacyPlugIn,
    PpBacktestMetrics Earned, PpBacktestMetrics LegacyEarned, int TargetSessions, double? Coverage, double? ReachRate, double? LegacyCoverage,
    PpBacktestMetrics CalculatorCheck);

public sealed record PpBacktestReport(IReadOnlyList<PpBacktestGroup> Groups, IReadOnlyList<PpBacktestSession> Sessions);

/// <summary>
/// Chronological held-out evaluation. Each practice session (consecutive attempts of one map, setup
/// and scoring mode) is predicted from plays strictly before it and compared with what happened.
/// The legacy baseline is the former single plug-in: average accuracy and miss rate over all attempts,
/// failed ones included, with evenly spread misses.
/// </summary>
public static class PpTargetBacktest
{
    private static readonly string[] bands = ["All", "<4.5*", "4.5-5.5*", "5.5-6.5*", "6.5*+"];

    public static PpBacktestReport Run(IReadOnlyList<PpOutcomeObservation> history,
        Func<PpOutcomeObservation, IPpScenarioCurve?> curves, PpBacktestOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(curves);
        options ??= new PpBacktestOptions();
        var ordered = history.OrderBy(o => o.PlayedAt).ThenBy(o => o.MapKey, StringComparer.Ordinal).ToArray();
        var all = new PpOutcomeProfile("backtest", ordered.Length == 0 ? DateTimeOffset.UnixEpoch : ordered[^1].PlayedAt, ordered);
        var sessions = new List<PpBacktestSession>();
        // No Fail turns every attempt into a pass; targets are never suggested with it.
        foreach (var session in split(ordered.Where(o => o.Source != PpOutcomeSource.OnlineBest && !o.Mods.Contains("NF")), TimeSpan.FromHours(options.SessionGapHours))
                     .OrderBy(s => s[0].PlayedAt).ThenBy(s => s[0].MapKey, StringComparer.Ordinal))
        {
            PpOutcomeObservation first = session[0];
            var before = PpTargetOutcomeModel.Before(all, first.PlayedAt);
            if (before.Observations.Count(o => o.Setup == first.Setup && o.Source != PpOutcomeSource.OnlineBest) < options.MinimumPriorPlays) continue;
            if (curves(first) is not { ObjectCount: > 0, MaximumCombo: > 0 } curve) continue;
            if (Predict(before, first, curve) is not { } prediction) continue;
            var attempts = session.Take(prediction.Tries).ToArray();
            sessions.Add(new PpBacktestSession(first.PlayedAt, band(first.Stars), prediction.PpIfPass, prediction.LegacyPp, prediction.PassChance,
                prediction.LegacyPassRate, prediction.TargetPp, prediction.Tries,
                attempts.Select(a => a.Passed ? a.Pp ?? 0 : 0).DefaultIfEmpty(0).Max(),
                session.Where(a => a.Passed && a.Pp is > 0).Select(a => a.Pp!.Value).ToArray(), session.Count(a => !a.Passed),
                session.Where(a => a.Passed && a.Pp is > 0).Select(a => curve.Pp(a.Accuracy, (int)Math.Round(a.Misses * curve.ObjectCount / (double)Math.Max(1, a.ObjectCount ?? curve.ObjectCount)), a.Combo ?? curve.MaximumCombo)).ToArray()));
        }
        return new PpBacktestReport(bands.Select(name => group(name, name == "All" ? sessions : sessions.Where(s => s.Band == name).ToArray())).ToArray(), sessions);
    }

    public sealed record Prediction(double PpIfPass, double? PassChance, double? TargetPp, int Tries, double LegacyPp, double? LegacyPassRate);

    public static Prediction? Predict(PpOutcomeProfile before, PpOutcomeObservation play, IPpScenarioCurve curve)
    {
        int objects = curve.ObjectCount;
        var prior = before.Observations.Where(o => o.Setup == play.Setup && o.Passed).ToArray();
        double fallback = prior.Length == 0 ? .95 : prior.Average(o => o.Accuracy);
        var target = new PpOutcomeTarget(play.Stars, play.Bpm, play.LengthSeconds ?? 0, play.OverallDifficulty, play.ApproachRate,
            objects, curve.MaximumCombo, play.Setup, play.LegacyScore, play.MapKey);
        var distribution = PpTargetOutcomeModel.Fit(before, target, PpTargetOutcomeModel.Prior(null, fallback, .5, objects));
        var scenarios = PpTargetOutcomeModel.ScenarioMisses(distribution, objects).Select(m =>
        {
            double accuracy = Math.Clamp(distribution.ScenarioAccuracy(m, objects), 0, 1 - m / (double)objects);
            int combo = distribution.ScenarioCombo(m, objects, curve.MaximumCombo);
            return new PpScenario(m, accuracy, combo, curve.Pp(accuracy, m, combo));
        }).ToArray();
        double step = scenarios[0].Accuracy + .01 <= 1 ? .01 : -.01;
        double slope = scenarios[0].Pp > 0 ? (curve.Pp(scenarios[0].Accuracy + step, 0, scenarios[0].Combo) / scenarios[0].Pp - 1) / step : 0;
        double ceiling = curve.Pp(1, 0, curve.MaximumCombo);
        var atoms = PpTargetOutcomeModel.Integrate(distribution, scenarios, objects, slope, ceiling);
        double? pass = passChance(before, play);
        int tries = PpTargetForecastModel.Tries(pass);
        var reach = pass is { } p ? PpTargetOutcomeModel.BestOf(atoms, p, tries, PpTargetForecastModel.TargetReach) : null;
        var legacy = legacyPlugIn(before, play, curve);
        return new Prediction(PpTargetOutcomeModel.Mean(atoms), pass, reach?.Pp, tries, legacy.Pp, legacy.PassRate);
    }

    public static string Format(PpBacktestReport report)
    {
        var text = new StringBuilder();
        string f(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
        string p(double? value) => value is { } v ? v.ToString("P0", CultureInfo.InvariantCulture) : "-";
        text.AppendLine($"PP target backtest: {report.Sessions.Count} held-out sessions, reach level {PpTargetForecastModel.TargetReach.ToString("P0", CultureInfo.InvariantCulture)}.");
        text.AppendLine("Passes: PP-if-pass vs recorded PP. Earned: pass chance x PP-if-pass vs every attempt (fails = 0). ME = mean error, MAE = mean absolute error, bias = ME / mean actual.");
        text.AppendLine("Calculator check: the PP curve at each pass's actual statistics vs its recorded PP (exact when the real calculator is used).");
        text.AppendLine("band        sessions  passes | new ME / MAE / bias      | legacy ME / MAE / bias   | earned bias new / legacy | target n  best<=target  best>=target  legacy best<=plug-in | calculator bias");
        foreach (var g in report.Groups)
            text.AppendLine(string.Join("  ", g.Name.PadRight(10), g.Sessions.ToString(CultureInfo.InvariantCulture).PadLeft(8), g.PpIfPass.Count.ToString(CultureInfo.InvariantCulture).PadLeft(6), "|",
                $"{f(g.PpIfPass.MeanError)} / {f(g.PpIfPass.MeanAbsoluteError)} / {p(g.PpIfPass.Bias)}".PadLeft(22), "|",
                $"{f(g.LegacyPlugIn.MeanError)} / {f(g.LegacyPlugIn.MeanAbsoluteError)} / {p(g.LegacyPlugIn.Bias)}".PadLeft(22), "|",
                $"{p(g.Earned.Bias)} / {p(g.LegacyEarned.Bias)}".PadLeft(22), "|",
                g.TargetSessions.ToString(CultureInfo.InvariantCulture).PadLeft(8), p(g.Coverage).PadLeft(12), p(g.ReachRate).PadLeft(12), p(g.LegacyCoverage).PadLeft(12), "|",
                p(g.CalculatorCheck.Bias).PadLeft(8)));
        return text.ToString();
    }

    private static PpBacktestGroup group(string name, IReadOnlyList<PpBacktestSession> sessions)
    {
        var withTarget = sessions.Where(s => s.TargetPp is not null).ToArray();
        return new PpBacktestGroup(name, sessions.Count,
            PpBacktestMetrics.Of(sessions.SelectMany(s => s.PassPp.Select(pp => (s.PpIfPass, pp)))),
            PpBacktestMetrics.Of(sessions.SelectMany(s => s.PassPp.Select(pp => (s.LegacyPp, pp)))),
            PpBacktestMetrics.Of(sessions.Where(s => s.PassChance is not null).SelectMany(s => attempts(s, s.PassChance!.Value * s.PpIfPass))),
            PpBacktestMetrics.Of(sessions.Where(s => s.LegacyPassRate is not null).SelectMany(s => attempts(s, s.LegacyPassRate!.Value * s.LegacyPp))),
            withTarget.Length,
            withTarget.Length == 0 ? null : withTarget.Count(s => s.SessionBest <= s.TargetPp!.Value) / (double)withTarget.Length,
            withTarget.Length == 0 ? null : withTarget.Count(s => s.SessionBest >= s.TargetPp!.Value) / (double)withTarget.Length,
            sessions.Count == 0 ? null : sessions.Count(s => s.SessionBest <= s.LegacyPp) / (double)sessions.Count,
            PpBacktestMetrics.Of(sessions.SelectMany(s => s.CurvePp.Zip(s.PassPp))));
    }

    private static IEnumerable<(double, double)> attempts(PpBacktestSession session, double predicted) =>
        session.PassPp.Select(pp => (predicted, pp)).Concat(Enumerable.Repeat((predicted, 0d), session.Failures));

    private static string band(double stars) => stars switch { < 4.5 => "<4.5*", < 5.5 => "4.5-5.5*", < 6.5 => "5.5-6.5*", _ => "6.5*+" };

    private static IEnumerable<IReadOnlyList<PpOutcomeObservation>> split(IEnumerable<PpOutcomeObservation> plays, TimeSpan gap)
    {
        foreach (var group in plays.GroupBy(o => (o.MapKey, o.Setup, o.LegacyScore)))
        {
            var current = new List<PpOutcomeObservation>();
            foreach (var play in group.OrderBy(o => o.PlayedAt))
            {
                if (current.Count > 0 && play.PlayedAt - current[^1].PlayedAt > gap)
                {
                    yield return current;
                    current = [];
                }
                current.Add(play);
            }
            if (current.Count > 0) yield return current;
        }
    }

    // The pass-frequency model sees the same local attempts it would in the app.
    private static double? passChance(PpOutcomeProfile before, PpOutcomeObservation play)
    {
        var attempts = before.Observations.Where(o => o.Source != PpOutcomeSource.OnlineBest)
            .Select(o => new PpTargetPassSample(o.BeatmapId, o.PlayedAt, o.Stars, o.Bpm, o.LengthSeconds, string.Join(',', PpTargetMods.Normalise(o.Mods)),
                o.Passed, o.BeatmapId > 0 ? null : new Guid(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(o.MapKey))), o.Accuracy, o.ModsJson, o.ScoreId, o.Pp, o.Source == PpOutcomeSource.Local, o.LegacyScore))
            .ToArray();
        var profile = new PpTargetOpportunityProfile(play.PlayedAt, [], attempts);
        var estimate = PpTargetOpportunityModel.EstimatePass(profile, play.Stars, play.Bpm ?? 0, play.LengthSeconds ?? 0, play.Mods, play.BeatmapId, play.ModsJson, play.LegacyScore);
        if (estimate is not null) return estimate.Probability;
        var comparable = attempts.Where(a => Math.Abs(a.Stars - play.Stars) <= 1).ToArray();
        return comparable.Length < 3 ? null : (comparable.Count(a => a.Passed) + 1d) / (comparable.Length + 2);
    }

    private static (double Pp, double? PassRate) legacyPlugIn(PpOutcomeProfile before, PpOutcomeObservation play, IPpScenarioCurve curve)
    {
        double weight = 0, accuracy = 0, missRate = 0, passes = 0;
        foreach (var o in before.Observations)
        {
            if (o.Setup != play.Setup || o.LegacyScore != play.LegacyScore || o.Source == PpOutcomeSource.OnlineBest || Math.Abs(o.Stars - play.Stars) > 1) continue;
            double w = Math.Pow(.5, (play.PlayedAt - o.PlayedAt).TotalDays / 15) * Math.Exp(-Math.Pow((o.Stars - play.Stars) / .75, 2));
            int judged = Math.Max(1, o.ObjectCount ?? curve.ObjectCount);
            weight += w;
            accuracy += w * o.Accuracy;
            missRate += w * Math.Min(1, o.Misses / (double)judged);
            passes += o.Passed ? w : 0;
        }
        if (weight <= 0) return (curve.Pp(.95, 1, curve.MaximumCombo / 2), null);
        (int misses, int combo) = PpTargetExactCalculationService.ExpectedScoreShape(.5, curve.MaximumCombo, curve.ObjectCount, missRate / weight);
        double feasible = PpTargetExactCalculationService.FeasibleAccuracy(accuracy / weight, misses, curve.ObjectCount);
        return (curve.Pp(feasible, misses, combo), passes / weight);
    }
}

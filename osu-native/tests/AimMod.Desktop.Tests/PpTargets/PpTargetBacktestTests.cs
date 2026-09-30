using AimMod.Desktop.PpTargets;
using NUnit.Framework;

namespace AimMod.Desktop.Tests.PpTargets;

[TestFixture]
public sealed class PpTargetBacktestTests
{
    private static readonly DateTimeOffset start = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    // Steeply convex in misses, as official PP is: the plug-in average of misses underestimates the average PP.
    private sealed class SyntheticCurve(double fullCombo, int objects, int maximumCombo) : IPpScenarioCurve
    {
        public int ObjectCount { get; } = objects;
        public int MaximumCombo { get; } = maximumCombo;
        public double Pp(double accuracy, int misses, int combo) =>
            fullCombo * Math.Exp(7 * (Math.Clamp(accuracy, 0, 1) - 1)) * Math.Pow(.9, misses) * Math.Pow(Math.Clamp(combo / (double)MaximumCombo, 0, 1), .3);
    }

    private sealed record Map(int Id, double Stars, int Objects, SyntheticCurve Curve);

    /// <summary>
    /// A player whose passes average roughly 120pp: 70% of attempts pass with a few overdispersed misses;
    /// failures stop part-way with many misses. The generating distribution is known exactly.
    /// </summary>
    private static (List<PpOutcomeObservation> History, Dictionary<string, SyntheticCurve> Curves) player(int seed, int days = 70)
    {
        var random = new Random(seed);
        var maps = Enumerable.Range(1, 30).Select(id =>
        {
            double stars = Math.Round(4.7 + random.NextDouble(), 2);
            int objects = random.Next(600, 1000);
            return new Map(id, stars, objects, new SyntheticCurve(165 + 30 * (stars - 5.2), objects, (int)(objects * 1.35)));
        }).ToArray();
        var history = new List<PpOutcomeObservation>();
        for (int day = 0; day < days; day++)
            for (int session = 0; session < 2; session++)
            {
                Map map = maps[random.Next(maps.Length)];
                DateTimeOffset time = start.AddDays(day).AddHours(session * 5);
                int attempts = random.Next(3, 7);
                for (int attempt = 0; attempt < attempts; attempt++, time = time.AddMinutes(5))
                    history.Add(play(random, map, time));
            }
        return (history, maps.ToDictionary(m => $"online:{m.Id}", m => m.Curve));
    }

    private static PpOutcomeObservation play(Random random, Map map, DateTimeOffset time)
    {
        double difficulty = map.Stars - 5.2;
        bool passed = random.NextDouble() < .7 - .1 * difficulty;
        int objects = map.Objects;
        if (!passed)
        {
            int judged = (int)(objects * (.2 + .7 * random.NextDouble()));
            int failMisses = 3 + poisson(random, 4);
            double failAccuracy = Math.Clamp(.95 + .01 * normal(random), 0, 1) * (1 - failMisses / (double)judged);
            return observation(map, time, false, failAccuracy, failMisses, random.Next(20, 200), judged, null);
        }
        double mean = 1.2 * objects / 800 * Math.Exp(.9 * difficulty);
        int misses = poisson(random, gamma(random, 1.5) * mean / 1.5);
        double hit = Math.Clamp(.975 - .012 * difficulty + .007 * normal(random), .85, 1);
        double accuracy = hit * (1 - misses / (double)objects);
        int maxCombo = map.Curve.MaximumCombo;
        int combo = misses == 0
            ? random.NextDouble() < .7 ? maxCombo : (int)(maxCombo * (.8 + .2 * random.NextDouble()))
            : (int)Math.Clamp(maxCombo * PpTargetOutcomeModel.EvenSpreadComboFraction(misses, objects) * (.7 + .6 * random.NextDouble()), 1, maxCombo);
        return observation(map, time, true, accuracy, misses, combo, objects, map.Curve.Pp(accuracy, misses, combo));
    }

    private static PpOutcomeObservation observation(Map map, DateTimeOffset time, bool passed, double accuracy, int misses, int combo, int objects, double? pp) =>
        new(Guid.NewGuid(), $"online:{map.Id}", map.Id, null, time, map.Stars, 180, map.Objects / 3, 8, 9.3, [], "", "", false, passed,
            PpOutcomeSource.Local, accuracy, misses, combo, objects, map.Curve.MaximumCombo, pp);

    private static double normal(Random random) => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());

    private static int poisson(Random random, double mean)
    {
        double limit = Math.Exp(-mean), product = random.NextDouble();
        int count = 0;
        for (; product > limit; count++) product *= random.NextDouble();
        return count;
    }

    private static double gamma(Random random, double shape)
    {
        if (shape < 1) return gamma(random, shape + 1) * Math.Pow(random.NextDouble(), 1 / shape);
        double d = shape - 1 / 3d, c = 1 / Math.Sqrt(9 * d);
        while (true)
        {
            double x = normal(random), v = Math.Pow(1 + c * x, 3);
            if (v > 0 && Math.Log(1 - random.NextDouble()) < .5 * x * x + d - d * v + d * Math.Log(v)) return d * v;
        }
    }

    [TestCase(11)]
    [TestCase(12)]
    public void NewModelIsRoughlyUnbiasedWhileThePlugInUnderestimates(int seed)
    {
        var (history, curves) = player(seed);
        PpBacktestReport report = PpTargetBacktest.Run(history, play => curves.GetValueOrDefault(play.MapKey));
        TestContext.Out.WriteLine(PpTargetBacktest.Format(report));
        PpBacktestGroup all = report.Groups.Single(g => g.Name == "All");
        double actual = report.Sessions.SelectMany(s => s.PassPp).Average();
        Assert.Multiple(() =>
        {
            Assert.That(all.Sessions, Is.GreaterThan(100));
            Assert.That(actual, Is.InRange(105, 135), "Synthetic passes average roughly 120pp.");
            Assert.That(Math.Abs(all.PpIfPass.Bias), Is.LessThan(.04), "PP if pass is roughly unbiased.");
            Assert.That(all.LegacyPlugIn.Bias, Is.LessThan(-.2), "The former plug-in underestimates.");
            Assert.That(all.PpIfPass.MeanAbsoluteError, Is.LessThan(all.LegacyPlugIn.MeanAbsoluteError / 2));
            Assert.That(Math.Abs(all.Earned.Bias), Is.LessThan(.1), "Pass chance times PP if pass matches PP per attempt.");
            Assert.That(all.LegacyEarned.Bias, Is.LessThan(-.2));
            Assert.That(all.TargetSessions, Is.GreaterThan(80));
            Assert.That(all.ReachRate, Is.InRange(.45, .75), "Targets are reached about 60% of the time.");
        });
    }

    [Test]
    public void EveryPredictionUsesOnlyEarlierPlays()
    {
        var (history, curves) = player(21, 30);
        var baseline = PpTargetBacktest.Run(history, play => curves.GetValueOrDefault(play.MapKey));
        var cutoff = history.Max(p => p.PlayedAt).AddDays(-3);
        // A dramatic change to the final days cannot alter predictions for any earlier session.
        var changed = history.Select(p => p.PlayedAt >= cutoff ? p with { Misses = 40, Accuracy = .5 } : p).ToArray();
        var rerun = PpTargetBacktest.Run(changed, play => curves.GetValueOrDefault(play.MapKey));
        double[] earlier(PpBacktestReport report) => report.Sessions.Where(s => s.Start < cutoff).Select(s => s.PpIfPass).ToArray();
        Assert.That(earlier(baseline), Is.Not.Empty);
        Assert.That(earlier(rerun), Is.EqualTo(earlier(baseline)));
        Assert.That(rerun.Sessions.Where(s => s.Start > cutoff).Select(s => s.PpIfPass),
            Is.Not.EqualTo(baseline.Sessions.Where(s => s.Start > cutoff).Select(s => s.PpIfPass)));
    }

    [Test]
    public void ReportPrintsOnlyAggregateMetrics()
    {
        var (history, curves) = player(31, 30);
        string text = PpTargetBacktest.Format(PpTargetBacktest.Run(history, play => curves.GetValueOrDefault(play.MapKey)));
        Assert.That(text, Does.Contain("held-out sessions").And.Contain("All"));
        Assert.That(text, Does.Not.Contain("online:"));
    }
}

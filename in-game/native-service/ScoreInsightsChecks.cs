using System.Diagnostics;
using System.Text.Json;

namespace AimMod.InGame;

static class ScoreInsightsChecks
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; }
        Run Make(int i, double score, string scenario = "Synthetic") => new("sample-" + i, scenario, score, 80, 60, 0, 0,
            new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero).AddDays(i / 8).AddMinutes(i % 8 * 2).ToString("O"), null, null, null, null, false);
        ScoreInsights.Entry[] Entries(Run[] runs) => runs.Select(r => new ScoreInsights.Entry(r, DateTimeOffset.Parse(r.Timestamp))).ToArray();
        var flat = Enumerable.Range(0, 64).Select(i => Make(i, 100)).ToArray();
        var plateau = ScoreInsights.Analyze("Synthetic", Entries(flat));
        Check(plateau.Plateau.Detected == true && plateau.Plateau.Runs == 54, "Flat rolling typical plateau");
        Check(plateau.Floor.Value == 100 && plateau.Learning.PointsPer10Runs == 0, "Constant percentiles and robust slope");
        var tiny = ScoreInsights.Analyze("Synthetic", Entries(flat.Take(3).ToArray()));
        Check(tiny.Typical.Value is null && tiny.Learning.PointsPer10Runs is null && tiny.Plateau.Detected is null, "Tiny histories stay unknown");
        var rising = ScoreInsights.Analyze("Synthetic", Entries(Enumerable.Range(0, 64).Select(i => Make(i, 100 + i)).ToArray()));
        Check(rising.Learning.PointsPer10Runs == 10 && rising.Typical.ChangePoints == 30 && rising.LaterScoreChangePoints == 15, "Known progress and window changes");
        var warm = Enumerable.Range(0, 160).Select(i => Make(i, i % 8 < 2 ? 80 : 100)).ToArray();
        var practice = PracticeStructure.Analyze(Entries(warm));
        Check(practice.Single(t => t.Key == "warmupTax").Value == 20, "Known normalized opening tax");
        var scaled = warm.Select(r => r with { Id = r.Id + "-large", Scenario = "Synthetic large", Score = r.Score * 1000 }).ToArray();
        Check(PracticeStructure.Analyze(Entries(scaled)).Single(t => t.Key == "warmupTax").Value == 20, "Practice normalization ignores score scale");
        Check(PracticeStructure.Analyze(Entries(flat.Take(3).ToArray())).All(t => t.Value is null && t.Confidence == "low"), "Tiny practice histories stay unknown");
        var switches = Enumerable.Range(0, 160).Select(i => Make(i, i % 4 == 0 ? 80 : 100, i % 8 < 4 ? "Synthetic A" : "Synthetic B")).ToArray();
        Check(PracticeStructure.Analyze(Entries(switches)).Single(t => t.Key == "switchCost").Value == 20, "Known switch cost excludes session opening");
        var returning = switches.Select((r, i) => r with { Timestamp = DateTimeOffset.Parse(r.Timestamp).AddDays(i / 8).ToString("O") }).ToArray();
        Check(PracticeStructure.Analyze(Entries(returning)).Single(t => t.Key == "retention1Day").Value == 80, "First same-scenario return after two days retains 80 percent");
        var fading = Enumerable.Range(0, 160).Select(i => Make(i, i % 8 < 4 ? 100 : 80)).ToArray();
        Check(PracticeStructure.Analyze(Entries(fading)).Single(t => t.Key == "lateBlockFade").Value < -20, "Known continuous block fade");
        var many = Enumerable.Range(0, 50000).Select(i => Make(i, 100 + i % 7, "Synthetic " + i % 20)).ToArray();
        var timer = Stopwatch.StartNew(); var first = ScoreInsights.Build(many, "Synthetic 0"); timer.Stop();
        var cached = Stopwatch.StartNew(); var second = ScoreInsights.Build(many.ToArray(), "Synthetic 0"); cached.Stop();
        Check(JsonSerializer.Serialize(first) == JsonSerializer.Serialize(second), "Cached snapshot stable");
        var edited = many.ToArray(); edited[0] = edited[0] with { Score = 10000 };
        Check(JsonSerializer.Serialize(ScoreInsights.Build(edited, "Synthetic 0")) != JsonSerializer.Serialize(first), "Cache invalidates record corrections");
        Check(JsonSerializer.Serialize(ScoreInsights.Build([], "Unknown")).Contains("Not enough runs"), "Empty snapshot serializes");
        var legacy = flat.Select(r => r with { Timestamp = "2026.01.01-12.00.00" }).ToArray();
        Check(JsonSerializer.Serialize(ScoreInsights.Build(legacy, "Synthetic")).Contains("\"Samples\":30"), "Legacy local timestamp format remains included");
        Console.WriteLine($"{checks} score insights checks passed; 50k cold {timer.ElapsedMilliseconds}ms, cached {cached.ElapsedMilliseconds}ms.");
    }
}

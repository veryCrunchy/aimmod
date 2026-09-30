using System.Text.Json;
using System.Runtime.InteropServices;

namespace AimMod.InGame;
static class StatsChecks
{
    [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)]string path,out nint db,int flags,nint vfs);
    [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_close_v2(nint db);
    [DllImport("winsqlite3",CallingConvention=CallingConvention.Cdecl)]static extern int sqlite3_exec(nint db,[MarshalAs(UnmanagedType.LPUTF8Str)]string sql,nint callback,nint context,nint error);
    public static void Run()
    {
        var checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
        var now = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
        Run Sample(string id, string scenario, double score, DateTimeOffset stamp) => new(id, scenario, score, null, 60, 0, 0, stamp.ToString("O"), null, null, null, null, false);
        var fixture = new List<Run>();
        for (int i = 0; i < 3; i++)
        {
            fixture.Add(Sample("old-" + i, "Small", 100, now.AddDays(-10).AddMinutes(i)));
            fixture.Add(Sample("new-" + i, "Small", 120, now.AddDays(-1).AddMinutes(i)));
            fixture.Add(Sample("large-" + i, "Large", 100000, now.AddDays(-1).AddMinutes(i)));
        }
        var report = StatsAnalysis.Build(fixture, "Small", now, TimeZoneInfo.Utc);
        var week = report.Periods.Single(p => p.Key == "7");
        Check(week.Runs == 6 && week.Selected.Runs == 3, "Scenario isolation does not mix incomparable scores");
        Check(Math.Abs(week.Selected.ScoreChange.ChangePercent!.Value - 20) < .00001, "Compare matching scenario across equal periods");
        Check(week.Selected.Median == 120 && week.Selected.StandardDeviation == 0, "Stable score distribution");
        Check(week.Selected.Accuracy is null && week.Selected.Smoothness is null && week.Selected.AccuracySamples == 0, "Hub previews retain unknown movement and accuracy");
        Check(week.Selected.Distribution.Length == 1 && week.Selected.Distribution[0].Count == 3, "Equal score histogram includes all runs");
        Check(week.Selected.Points.All(p => p.RollingAverage is null), "Desktop rolling average requires five samples");
        Check(week.Blocks.Length == 1 && week.Blocks[0].Runs == 6 && week.Blocks[0].Scenarios.Length == 2, "Practice grouping counts runs and unique scenarios");
        Check(report.Periods.Single(p => p.Key == "all").Selected.ScoreChange.ChangePercent is null, "All-time has no invented previous period");
        var boundary = StatsAnalysis.Build([Sample("edge", "A", 1, now.AddDays(-7)), Sample("outside", "A", 2, now.AddDays(-7).AddTicks(-1))], "A", now, TimeZoneInfo.Utc).Periods[0];
        Check(boundary.Selected.Runs == 1 && boundary.Selected.ScoreChange.PreviousSamples == 1, "Period boundaries are inclusive start and exclusive previous end");
        Check(boundary.Selected.ScoreChange.ChangePercent is null, "Small sample comparisons withheld");
        var split = StatsAnalysis.Build([Sample("a", "A", 1, now.AddHours(-2)), Sample("b", "A", 2, now.AddHours(-2).AddMinutes(30)), Sample("c", "A", 3, now.AddHours(-1).AddMinutes(1))], "A", now, TimeZoneInfo.Utc).Periods[0];
        Check(split.Blocks.Length == 2 && split.Blocks[1].Runs == 2, "Blocks split only after more than thirty minutes");
        var zone = TimeZoneInfo.CreateCustomTimeZone("Fixture plus two", TimeSpan.FromHours(2), "Fixture", "Fixture");
        var dated = StatsAnalysis.Build([Sample("midnight", "A", 1, new DateTimeOffset(2026, 9, 5, 23, 0, 0, TimeSpan.Zero))], "A", now, zone);
        Check(dated.Periods[0].Calendar[0].Date == "2026-09-06", "Practice calendar uses requested local time zone");
        var large = Enumerable.Range(0, 1200).Select(i => Sample("n" + i, "A", i == 555 ? 99999 : i % 17, now.AddMinutes(i - 1300))).ToArray();
        var detail = StatsAnalysis.Build(large, "A", now, TimeZoneInfo.Utc).Periods[0].Selected;
        Check(detail.Points.Length <= 400 && detail.Points.Any(p => p.Score == 99999) && detail.Points[0].Id == "n0" && detail.Points[^1].Id == "n1199", "Bounded downsample preserves extrema and endpoints");
        Check(detail.Distribution.Sum(b => b.Count) == 1200, "Histogram does not lose maximum boundary score");
        var sparse=StatsAnalysis.Build(large,"A",now,TimeZoneInfo.Utc,new Dictionary<string,HistoricalMeasurement>{["n553"]=new("n553",Overshoot:.2)}).Periods[0].Selected;
        Check(sparse.Points.Length<=400&&sparse.Points.Any(p=>p.Id=="n553"&&p.Measurements?.Overshoot==.2),"Downsampling retains sparse measured metrics within chart bound");
        var malformed = StatsAnalysis.Build([Sample("invalid", "A", double.NaN, now), Sample("future", "A", 1, now.AddDays(1)), Sample("date", "A", 1, now) with { Timestamp = "invalid" }], "A", now, TimeZoneInfo.Utc);
        Check(malformed.AvailableRuns == 0 && malformed.InvalidRuns == 3, "Reject invalid and future records");
        Check(JsonSerializer.Serialize(malformed).Length > 0, "Empty statistics serializes without nonfinite values");
        var zero = StatsAnalysis.Build([Sample("one", "A", 0, now.AddDays(-1)), Sample("two", "A", 0, now.AddDays(-2))], "A", now, TimeZoneInfo.Utc).Periods[0].Selected;
        Check(zero.VariationPercent is null && zero.Median == 0, "Zero scores remain valid without division by zero");
        var enriched=StatsAnalysis.Build([Sample("known","A",1,now.AddDays(-1)),Sample("missing","A",2,now.AddDays(-2)),Sample("other","B",9,now)],"A",now,TimeZoneInfo.Utc,
            new Dictionary<string,HistoricalMeasurement> { ["known"]=new("known",Overshoot:0,AverageTimeToKillMs:800,AverageFireToHitMs:45),["other"]=new("other",Overshoot:1,AverageTimeToKillMs:9000) }).Periods[0].Selected;
        Check(enriched.Points.Single(p=>p.Id=="missing").Measurements is null,"Missing supplementary values remain unknown");
        Check(enriched.Measurements.Single(m=>m.Key=="Overshoot") is {Average:0,Samples:1},"Exact run join preserves measured zero and excludes other scenarios");
        Check(enriched.Measurements.Single(m=>m.Key=="AverageTimeToKillMs").Average==800 && enriched.Measurements.Single(m=>m.Key=="AverageFireToHitMs").Average==45,"Target completion time remains distinct from fire-to-hit delay");
        Check(enriched.Points.Single(p=>p.Id=="known").Measurements?.AverageTimeToKillMs==800,"Per-run measurement series is available for trends");
        var fixturePath=Path.Combine(Path.GetTempPath(),"aimmod-measurements-"+Guid.NewGuid().ToString("N")+".sqlite3");nint db=0;
        try {
            Check(sqlite3_open_v2(fixturePath,out db,6,0)==0,"Create synthetic measurements database");
            Check(sqlite3_exec(db,"CREATE TABLE sessions(id TEXT,timestamp TEXT);INSERT INTO sessions VALUES('known','2026-01-01'),('foreign','2026-01-02');CREATE TABLE session_stats_panels(session_id TEXT,avg_kps REAL,avg_ttk_ms REAL,best_ttk_ms REAL,ttk_std_ms REAL,accuracy_trend REAL);INSERT INTO session_stats_panels VALUES('known',0,800,NULL,-1,NULL),('foreign',100,9000,1,1,1);",0,0,0)==0,"Populate optional scalar measurements");
            sqlite3_close_v2(db);db=0;
            var before=File.ReadAllBytes(fixturePath);
            var read=HistoricalMeasurements.Read(fixturePath,new HashSet<string>{"known"});
            Check(read.Count==1 && read["known"].KillsPerSecond==0 && read["known"].AverageTimeToKillMs==800,"Supplementary reader joins exact identity and retains zero");
            Check(read["known"].Overshoot is null && read["known"].AverageFireToHitMs is null && read["known"].TimeToKillSpreadMs is null,"Missing optional tables and invalid measurements stay unknown");
            Check(before.SequenceEqual(File.ReadAllBytes(fixturePath)),"Supplementary reading leaves historical database untouched");
        } finally {if(db!=0)sqlite3_close_v2(db);File.Delete(fixturePath);}
        Console.WriteLine($"{checks} statistics analysis checks passed.");
    }
}

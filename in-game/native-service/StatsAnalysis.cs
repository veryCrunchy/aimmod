using System.Globalization;

namespace AimMod.InGame;

record StatComparison(double? Current, double? Previous, double? ChangePercent, int CurrentSamples, int PreviousSamples);
record ScorePoint(string Id, string Date, double Score, double? Accuracy, double? RollingAverage, double PersonalBest,
    int RunNumber, double? TrendLine, double? Smoothness, double? Jitter, double? Efficiency, double? Correction, HistoricalMeasurement? Measurements = null);
record MeasurementSummary(string Key, double? Average, int Samples);
record ScoreBin(double From, double To, int Count);
record PracticeDay(string Date, int Runs, double Minutes, int Scenarios);
record PracticeBlock(string Start, string End, int Runs, double Minutes, string[] Scenarios);
record ScenarioStatistics(string Name, int Runs, double Hours, double? Best, double? Median, double? Average,
    double? StandardDeviation, double? VariationPercent, double? Accuracy, int AccuracySamples,
    double? Smoothness, int SmoothnessSamples, StatComparison ScoreChange, ScorePoint[] Points, ScoreBin[] Distribution, MeasurementSummary[] Measurements);
record StatsPeriod(string Key, int? Days, int Runs, double Hours, int ActiveDays, int Scenarios,
    StatComparison PracticeChange, ScenarioStatistics Selected, ScenarioStatistics[] ScenarioTable, PracticeDay[] Calendar, PracticeBlock[] Blocks) { public ScenarioStatistics? Warmup { get; init; } public ScenarioStatistics? Settled { get; init; } }
record StatsReport(string TimeZone, string SelectedScenario, int AvailableRuns, int InvalidRuns, string? EarliestRun, string? LatestRun, StatsPeriod[] Periods);

// Calculated once per history revision by the worker, never on the game thread.
// Raw scores are summarized strictly within one scenario. Missing measurements
// stay null; imported Hub previews do not acquire invented movement metrics.
static class StatsAnalysis
{
    static readonly System.Reflection.PropertyInfo[] MeasurementFields=typeof(HistoricalMeasurement).GetProperties().Where(p=>p.PropertyType==typeof(double?)).ToArray();
    record Entry(Run Run, DateTimeOffset Stamp, DateOnly Day, HistoricalMeasurement? Measurements);
    public static StatsReport Build(IEnumerable<Run> source, string selectedScenario, DateTimeOffset? now = null, TimeZoneInfo? zone = null, IReadOnlyDictionary<string,HistoricalMeasurement>? measurements = null)
    {
        zone ??= TimeZoneInfo.Local;
        var current = now ?? DateTimeOffset.UtcNow;
        var entries = new List<Entry>(); var invalid = 0;
        foreach (var run in source)
        {
            var stamp = HubHistory.Date(run.Timestamp);
            if (string.IsNullOrWhiteSpace(run.Scenario) || !double.IsFinite(run.Score) || Math.Abs(run.Score) > 1e12 || !double.IsFinite(run.Duration)
                || run.Duration <= 0 || run.Duration > 1e9 || stamp == DateTimeOffset.MinValue || stamp > current.AddMinutes(5)) { invalid++; continue; }
            entries.Add(new(run, stamp, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(stamp, zone).DateTime), measurements?.GetValueOrDefault(run.Id)));
        }
        var ordered = entries.OrderBy(e => e.Stamp).ThenBy(e => e.Run.Id, StringComparer.Ordinal).ToArray();
        if (selectedScenario.Length == 0) selectedScenario = ordered.LastOrDefault()?.Run.Scenario ?? "";
        var periods = new List<StatsPeriod>();
        var warmupIds = WarmupAnalysis.Classify(ordered.Select(e=>e.Run));
        var histories = ordered.GroupBy(e => e.Run.Scenario, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (int? days in new int?[] { 7, 30, 90, null })
        {
            var start = days.HasValue ? current.AddDays(-days.Value) : DateTimeOffset.MinValue;
            var previousStart = days.HasValue ? start.AddDays(-days.Value) : start;
            var slice = ordered.Where(e => e.Stamp >= start && e.Stamp <= current).ToArray();
            var previous = days.HasValue ? ordered.Where(e => e.Stamp >= previousStart && e.Stamp < start).ToArray() : [];
            var byScenario = slice.GroupBy(e => e.Run.Scenario, StringComparer.OrdinalIgnoreCase).ToArray();
            var previousGroups = previous.GroupBy(e => e.Run.Scenario, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
            var table = byScenario.Select(g => Analyze(g.Key, g.ToArray(), previousGroups.GetValueOrDefault(g.Key) ?? [], histories[g.Key], false)).OrderByDescending(s => s.Runs).ThenBy(s => s.Name).ToArray();
            var selected = Analyze(selectedScenario, slice.Where(e => e.Run.Scenario.Equals(selectedScenario, StringComparison.OrdinalIgnoreCase)).ToArray(),
                previousGroups.GetValueOrDefault(selectedScenario) ?? [], histories.GetValueOrDefault(selectedScenario) ?? [], true);
            ScenarioStatistics Filtered(bool warm) => Analyze(selectedScenario,
                slice.Where(e=>e.Run.Scenario.Equals(selectedScenario,StringComparison.OrdinalIgnoreCase)&&warmupIds.Contains(e.Run.Id)==warm).ToArray(),
                (previousGroups.GetValueOrDefault(selectedScenario)??[]).Where(e=>warmupIds.Contains(e.Run.Id)==warm).ToArray(),
                (histories.GetValueOrDefault(selectedScenario)??[]).Where(e=>warmupIds.Contains(e.Run.Id)==warm).ToArray(),true);
            var calendar = slice.GroupBy(e => e.Day).Select(g => new PracticeDay(g.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), g.Count(), g.Sum(e => e.Run.Duration) / 60, g.Select(e => e.Run.Scenario).Distinct(StringComparer.OrdinalIgnoreCase).Count())).ToArray();
            periods.Add(new(days?.ToString(CultureInfo.InvariantCulture) ?? "all", days, slice.Length, slice.Sum(e => e.Run.Duration) / 3600,
                calendar.Length, byScenario.Length, Compare(slice.Sum(e => e.Run.Duration) / 3600, previous.Sum(e => e.Run.Duration) / 3600, slice.Length, previous.Length),
                selected, table, calendar.TakeLast(366).ToArray(), Blocks(slice).TakeLast(100).Reverse().ToArray()) { Warmup=Filtered(true), Settled=Filtered(false) });
        }
        return new(zone.Id, selectedScenario, ordered.Length, invalid, ordered.FirstOrDefault()?.Run.Timestamp, ordered.LastOrDefault()?.Run.Timestamp, periods.ToArray());
    }
    static double? Finite(double? value) => value.HasValue && double.IsFinite(value.Value) ? value : null;
    static StatComparison Compare(double? current, double? previous, int count, int previousCount) =>
        new(current, previousCount > 0 ? previous : null, Finite(count >= 3 && previousCount >= 3 && previous is > 0 && current.HasValue ? (current.Value / previous.Value - 1) * 100 : null), count, previousCount);
    static double? Mean(IEnumerable<double?> values)
    {
        var finite = values.Where(v => v.HasValue && double.IsFinite(v.Value)).Select(v => v!.Value).ToArray();
        return finite.Length > 0 ? finite.Average() : null;
    }
    static ScenarioStatistics Analyze(string name, Entry[] entries, Entry[] previous, Entry[] all, bool chart)
    {
        var values = entries.Select(e => e.Run.Score).Order().ToArray();
        double? mean = values.Length > 0 ? values.Average() : null;
        double? median = values.Length == 0 ? null : values.Length % 2 == 1 ? values[values.Length / 2] : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2;
        double? deviation = values.Length >= 2 ? Math.Sqrt(values.Sum(v => Math.Pow(v - mean!.Value, 2)) / values.Length) : null;
        var accuracy = entries.Select(e => e.Run.Accuracy is >= 0 and <= 100 ? e.Run.Accuracy : null).ToArray();
        var movement = entries.Select(e => e.Run.Smoothness is >= 0 and <= 100 ? e.Run.Smoothness : null).ToArray();
        var points = new List<ScorePoint>();
        if (chart && entries.Length > 0)
        {
            var first = entries[0].Stamp;
            var best = all.Where(e => e.Stamp < first).Select(e => e.Run.Score).DefaultIfEmpty(double.NegativeInfinity).Max();
            var rolling = new Queue<double>();
            // Match the desktop overview's per-run least-squares trend and
            // five-run moving mean, retaining unrounded values for rendering.
            var center = (entries.Length + 1) / 2.0;
            var denominator = entries.Select((e, i) => Math.Pow(i + 1 - center, 2)).Sum();
            var slope = denominator > 0 ? entries.Select((e, i) => (i + 1 - center) * (e.Run.Score - mean!.Value)).Sum() / denominator : 0;
            var index = 0;
            foreach (var entry in entries)
            {
                index++; rolling.Enqueue(entry.Run.Score); if (rolling.Count > 5) rolling.Dequeue();
                best = Math.Max(best, entry.Run.Score);
                points.Add(new(entry.Run.Id, entry.Run.Timestamp, entry.Run.Score, entry.Run.Accuracy is >= 0 and <= 100 ? entry.Run.Accuracy : null,
                    rolling.Count == 5 ? rolling.Average() : null, best, index, entries.Length >= 4 ? mean + slope * (index - center) : null,
                    entry.Run.Smoothness is >= 0 and <= 100 ? entry.Run.Smoothness : null,
                    entry.Run.Jitter is >= 0 and <= 1 ? entry.Run.Jitter * 100 : null,
                    entry.Run.Efficiency is >= 0 and <= 1 ? entry.Run.Efficiency * 100 : null,
                    entry.Run.Correction is >= 0 and <= 1 ? entry.Run.Correction * 100 : null,entry.Measurements));
            }
        }
        var bins = new List<ScoreBin>();
        if (chart && values.Length > 0)
        {
            var lo = values[0]; var hi = values[^1];
            if (lo == hi) bins.Add(new(lo, hi, values.Length));
            else
            {
                var count = Math.Min(12, Math.Max(3, (int)Math.Ceiling(Math.Sqrt(values.Length)))); var counts = new int[count]; var width = (hi - lo) / count;
                foreach (var v in values) counts[Math.Min(count - 1, (int)((v - lo) / width))]++;
                for (int i = 0; i < count; i++) bins.Add(new(lo + i * width, lo + (i + 1) * width, counts[i]));
            }
        }
        return new(name, values.Length, entries.Sum(e => e.Run.Duration) / 3600, values.Length > 0 ? values[^1] : null, median, mean,
            deviation, Finite(mean is > 0 && deviation.HasValue ? deviation / mean * 100 : null), Mean(accuracy), accuracy.Count(a => a.HasValue), Mean(movement), movement.Count(a => a.HasValue),
            Compare(mean, previous.Length > 0 ? previous.Average(e => e.Run.Score) : null, values.Length, previous.Length), Downsample(points, 400), bins.ToArray(),Summarize(entries.Select(e=>e.Measurements)));
    }
    internal static MeasurementSummary[] Summarize(IEnumerable<HistoricalMeasurement?> source)
    {
        var measurements=source.Where(m=>m is not null).ToArray();
        return MeasurementFields.Select(p=> {
            var values=measurements.Select(m=>(double?)p.GetValue(m)).Where(v=>v.HasValue&&double.IsFinite(v.Value)).Select(v=>v!.Value).ToArray();
            return new MeasurementSummary(p.Name,values.Length>0?values.Average():null,values.Length);
        }).ToArray();
    }
    static ScorePoint[] Downsample(List<ScorePoint> points, int limit)
    {
        if (points.Count <= limit) return points.ToArray();
        // Min/max buckets preserve visible peaks and troughs, including PBs.
        var result = new SortedDictionary<int, ScorePoint> { [0] = points[0], [points.Count - 1] = points[^1] };
        // Retain sparse supplementary measurements even when their score is
        // not a bucket extreme, so selecting an available metric cannot show
        // an empty chart simply because downsampling discarded its samples.
        foreach(var field in MeasurementFields) {
            var measured=Enumerable.Range(0,points.Count).Where(i=>points[i].Measurements is not null && field.GetValue(points[i].Measurements) is double v && double.IsFinite(v)).ToArray();
            if(measured.Length==0)continue;
            var lo=measured.MinBy(i=>(double)field.GetValue(points[i].Measurements)!);
            var hi=measured.MaxBy(i=>(double)field.GetValue(points[i].Measurements)!);
            result[lo]=points[lo];result[hi]=points[hi];
        }
        var size = (int)Math.Ceiling(points.Count / ((limit - result.Count) / 2.0));
        for (int start = 0; start < points.Count; start += size)
        {
            var indices = Enumerable.Range(start, Math.Min(size, points.Count - start)).ToArray();
            var lo = indices.MinBy(i => points[i].Score); var hi = indices.MaxBy(i => points[i].Score);
            result[lo] = points[lo]; result[hi] = points[hi];
        }
        return result.Values.ToArray();
    }
    static IEnumerable<PracticeBlock> Blocks(Entry[] entries)
    {
        var block = new List<Entry>();
        PracticeBlock Complete() => new(block[0].Stamp.ToString("O"), block[^1].Stamp.ToString("O"), block.Count,
            block.Sum(e => e.Run.Duration) / 60, block.Select(e => e.Run.Scenario).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        foreach (var entry in entries)
        {
            if (block.Count > 0 && entry.Stamp - block[^1].Stamp > TimeSpan.FromMinutes(30)) { yield return Complete(); block.Clear(); }
            block.Add(entry);
        }
        if (block.Count > 0) yield return Complete();
    }
}

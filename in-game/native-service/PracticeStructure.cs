namespace AimMod.InGame;

static class PracticeStructure
{
    internal static PracticeInsight[] Analyze(ScoreInsights.Entry[] entries)
    {
        var baselines = entries.GroupBy(e => e.Run.Scenario.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 10).ToDictionary(g => g.Key, g => ScoreInsights.Quantile(g.TakeLast(100).Select(e => e.Run.Score), .5), StringComparer.OrdinalIgnoreCase);
        var warm = new List<double>(); var settled = new List<double>(); var switched = new List<double>(); var repeated = new List<double>();
        var fade = new List<double>(); var minutes = new List<double>(); var retention = new[] { new List<double>(), new List<double>(), new List<double>() };
        var pbNext = new List<double>(); var badNext = new List<double>();
        var hours = new Dictionary<int, List<(double Value, DateTime Day)>>();
        var bests = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var lastScenario = new Dictionary<string, (DateTimeOffset Time, double Value)> (StringComparer.OrdinalIgnoreCase);
        var sessions = new List<List<(ScoreInsights.Entry E, double Value)>>();
        // Keep invalid-for-normalization runs in the chronology so they still break blocks.
        List<(ScoreInsights.Entry E, double Value)>? session = null;
        ScoreInsights.Entry? prior = null;
        foreach (var e in entries)
        {
            if (prior is null || (e.Time - prior.Time).TotalMinutes - prior.Run.Duration / 60 > 30)
            { session = []; sessions.Add(session); }
            var key = e.Run.Scenario.Trim();
            var value = baselines.TryGetValue(key, out var baseline) && baseline > 0 ? e.Run.Score / baseline * 100 : double.NaN;
            session!.Add((e, value)); prior = e;
            if (!double.IsFinite(value)) continue;
            if (lastScenario.TryGetValue(key, out var last))
            {
                var days = (e.Time - last.Time).TotalDays;
                if (days >= 1 && last.Value > 0) retention[days >= 7 ? 2 : days >= 3 ? 1 : 0].Add(value / last.Value * 100);
            }
            lastScenario[key] = (e.Time, value);
        }
        foreach (var s in sessions)
        {
            var warmupEligible = s.Count >= 4 && s.All(r => double.IsFinite(r.Value));
            var block = new List<double>();
            void EndBlock() { if (block.Count >= 6) fade.Add(block.TakeLast(2).Average() - block.Skip(1).Take(2).Average()); block.Clear(); }
            for (int i = 0; i < s.Count; i++)
            {
                var row = s[i]; var key = row.E.Run.Scenario.Trim();
                if (i > 0 && !key.Equals(s[i - 1].E.Run.Scenario.Trim(), StringComparison.OrdinalIgnoreCase)) EndBlock();
                if (!double.IsFinite(row.Value)) { EndBlock(); continue; }
                block.Add(row.Value);
                if (warmupEligible)
                { if (i < 2) warm.Add(row.Value); else settled.Add(row.Value); }
                if (i > 0) (key.Equals(s[i - 1].E.Run.Scenario.Trim(), StringComparison.OrdinalIgnoreCase) ? repeated : switched).Add(row.Value);
                var elapsed = (row.E.Time - s[0].E.Time).TotalMinutes;
                if (elapsed >= 30 && s.Take(2).All(r => double.IsFinite(r.Value))) minutes.Add(row.Value - s.Take(2).Average(r => r.Value));
                if (i + 1 < s.Count && key.Equals(s[i + 1].E.Run.Scenario.Trim(), StringComparison.OrdinalIgnoreCase) && double.IsFinite(s[i + 1].Value))
                {
                    if (bests.TryGetValue(key, out var best) && row.E.Run.Score > best) pbNext.Add(s[i + 1].Value - row.Value);
                    if (row.Value < 90) badNext.Add(s[i + 1].Value - row.Value);
                }
                bests[key] = Math.Max(bests.GetValueOrDefault(key, double.NegativeInfinity), row.E.Run.Score);
                var hour = row.E.Time.ToLocalTime().Hour / 4 * 4;
                if (!hours.ContainsKey(hour)) hours[hour] = [];
                hours[hour].Add((row.Value, row.E.Time.LocalDateTime.Date));
            }
            EndBlock();
        }
        PracticeInsight Tile(string key, string label, List<double> a, string action, List<double>? b = null, bool tax = false, string unit = "percentage points")
        {
            var ok = a.Count >= 8 && (b is null || b.Count >= 8);
            return new(key, label, ok ? b is null ? a.Average() : tax ? b.Average() - a.Average() : a.Average() - b.Average() : null,
                unit, a.Count, b?.Count ?? 0, ok ? "supported" : "low", action);
        }
        var tiles = new List<PracticeInsight> {
            Tile("warmupTax", "Opening runs", warm, "Use two easy opening runs before judging today's pace.", settled, true),
            Tile("switchCost", "After switching", switched, "Try a short block on one scenario before switching again.", repeated, true),
            Tile("lateBlockFade", "Late in a block", fade, "Take a short break when your later runs start slipping."),
            Tile("sessionFade", "After 30 minutes", minutes, "Pause after half an hour and check your next run against your opening pace."),
            Tile("retention1Day", "After 1 to 3 days away", retention[0], "Start with a familiar scenario when returning after a break.", unit: "% retained"),
            Tile("retention3Days", "After 3 to 7 days away", retention[1], "Give yourself a short return block before raising the difficulty.", unit: "% retained"),
            Tile("retention7Days", "After 7+ days away", retention[2], "Rebuild your usual pace with a few comfortable runs.", unit: "% retained"),
            Tile("afterBest", "After a personal best", pbNext, "Keep the same pace for the next run after a personal best."),
            Tile("afterBad", "After a low score", badNext, "Reset your grip and breathing before the next run.") };
        var supported = hours.Where(h => h.Value.Count >= 20 && h.Value.Select(v => v.Day).Distinct().Count() >= 5).OrderByDescending(h => h.Value.Average(v => v.Value)).ToArray();
        var timeOk = supported.Length >= 2;
        tiles.Add(new("bestTimeOfDay", "Best time of day", timeOk ? supported[0].Key : null, "local hour", timeOk ? supported[0].Value.Count : hours.Values.Select(v => v.Count).DefaultIfEmpty().Max(),
            timeOk ? supported[1].Value.Count : 0, timeOk ? "supported" : "low", timeOk ? $"Try a focused block between {supported[0].Key:00}:00 and {supported[0].Key + 4:00}:00." : "Practice at a few different times to find a useful pattern."));
        return tiles.ToArray();
    }
}

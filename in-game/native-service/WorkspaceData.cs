using System.Text.Json;

namespace AimMod.InGame;
static class WorkspaceData
{
    public static string Build(Run[] runs, Hub hub, RunDetails? details = null, IReadOnlyDictionary<string,HistoricalMeasurement>? measurements = null)
    {
        var selected = hub.SelectedScenario.Length > 0 ? hub.SelectedScenario : runs.FirstOrDefault()?.Scenario ?? "";
        var selection = runs.Where(r => r.Scenario.Equals(selected, StringComparison.OrdinalIgnoreCase)).ToArray();
        var history = runs.Where(hub.MatchesHistory).ToArray();
        var page = Math.Clamp(hub.HistoryPage, 0, Math.Max(0, (history.Length - 1) / 50));
        var views = Views.Build(runs).Concat(hub.Rows()).ToArray();
        var recent = selection.Take(5).ToArray(); var previous = selection.Skip(5).Take(5).ToArray();
        var baseline = previous.Length > 0 ? previous.Average(r => r.Score) : 0;
        var runCoaching = selection.Length > 0 ? Coaching.Analyze(selection[0], details ?? new RunDetails(selection[0].Id, null, [])) : null;
        var coaching = (runCoaching?.Tips.Select(t => new Row("Coaching", t.Title, t.Detail)) ?? [])
            .Concat(views.Where(r => r.Page == "Coaching" && r.Heading == selected));
        return JsonSerializer.Serialize(new {
            revision = hub.Revision, selectedScenario = selected, scenarioFiltered = hub.SelectedScenario.Length > 0,
            statistics = StatsAnalysis.Build(runs, selected,measurements:measurements),
            totalRuns = runs.Length, hours = runs.Sum(r => r.Duration) / 3600,
            scenarios = runs.GroupBy(r => r.Scenario, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key).Select(g => new { name = g.Key, count = g.Count(), best = g.Max(r => r.Score) }),
            latest = selection.FirstOrDefault(), best = selection.Length > 0 ? selection.Max(r => r.Score) : (double?)null,
            average = recent.Length > 0 ? recent.Average(r => r.Score) : (double?)null,
            trend = recent.Length == 5 && previous.Length == 5 && baseline > 0 ? (recent.Average(r => r.Score) / baseline - 1) * 100 : (double?)null,
            graph = selection.Take(80).Reverse().Select(r => new { score = r.Score, accuracy = r.Accuracy, date = r.Timestamp }),
            history = history.Skip(page * 50).Take(50), historyCount = history.Length, historyPage = page, historyPages = Math.Max(1, (history.Length + 49) / 50),
            coaching, moments = runCoaching?.Moments, runDetails = details,
            mechanics = selection.Where(r => r.Smoothness.HasValue).Take(20),
            mechanicsMeasurements = StatsAnalysis.Summarize(selection.Select(r=>measurements?.GetValueOrDefault(r.Id))),
            coachingHistory = runs.Take(1000).Select(r=> {
                var m=measurements?.GetValueOrDefault(r.Id);
                return new { id=r.Id,normalizedScenario=r.Scenario.Trim().ToLowerInvariant(),timestampMs=HubHistory.Date(r.Timestamp).ToUnixTimeMilliseconds(),
                    duration_secs=r.Duration,score=r.Score,accuracy=r.Accuracy,kills=r.Kills,damage_done=r.Damage,
                    stats_panel=new {avg_kps=m?.KillsPerSecond,avg_ttk_ms=m?.AverageTimeToKillMs,accuracy_pct=r.Accuracy},
                    smoothness=new {composite=r.Smoothness,jitter=r.Jitter,path_efficiency=r.Efficiency,correction_ratio=r.Correction},
                    shot_timing=new {avg_fire_to_hit_ms=m?.AverageFireToHitMs,avg_shots_to_hit=m?.AverageShotsToHit,corrective_shot_ratio=m?.CorrectiveShotRatio} };
            }),
            benchmarks = views.Where(r => r.Page == "Benchmarks"), account = hub.AccountInfo
        });
    }
}

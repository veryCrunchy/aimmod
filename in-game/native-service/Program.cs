using System.Globalization;
using System.Text;
using System.Text.Json;
using AimMod.InGame;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
if (args.Contains("--self-test")) { Checks.Run(); HistoryCompletenessChecks.Run(); CsvHistoryChecks.Run(); await HubChecks.Run(); HubPaginationChecks.Run(); await HubLeaderboardChecks.Run(); Coaching.SelfTest(); CoachingFeedbackChecks.Run(); StatsChecks.Run(); WarmupChecks.Run(); RunInspectionChecks.Run(); NativeSettingsChecks.Run(); LiveOverlayChecks.Run(); LiveOverlayFeedChecks.Run(); OverlaySettingsChecks.Run(); await ObsOverlayChecks.Run(); BenchmarkChecks.Run(); ReplayLibraryChecks.Run(); await WorkspaceChecks.Run(); ReplayChecks.Run(); ReplayKeyboardChecks.Run(); await NativeReplayPlaybackChecks.Run(); await HardeningChecks.Run(); CoreFormatChecks.Run(); return; }
if (args.Length == 5 && args[0] == "--compare-spawns") { Environment.ExitCode = ReplayCompare.Spawns(args[1], args[2], args[3], args[4]); return; }
if (args.Length == 4 && args[0] == "--compare-replays") { Environment.ExitCode = ReplayCompare.Run(args[1], args[2], args[3]); return; }
var output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AimMod", "KovaaksNative");
var database = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "com.verycrunchy.kovaaks", "stats.sqlite3");
var exitWithGame = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--output" && i + 1 < args.Length) output = Path.GetFullPath(args[++i]);
    else if (args[i] == "--history" && i + 1 < args.Length) database = Path.GetFullPath(args[++i]);
    else if (args[i] == "--exit-with-game") exitWithGame = true;
}
Directory.CreateDirectory(output);
using var singleton = new Mutex(true, "Local\\AimMod.KovaaksNative.History", out var ownsMutex);
if (!ownsMutex) { Console.Error.WriteLine("Another AimMod worker is already running for this user."); return; }
using var cancellation = new CancellationTokenSource();
using var stopped = new ManualResetEventSlim(false);
// Ctrl+C, console window close, logoff and shutdown all request the same
// graceful stop: the listeners close, playback publishes a closed frame and
// the published URLs/heartbeat are retracted. Close/shutdown handlers wait
// briefly because Windows terminates the process when they return.
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
void RequestStop(System.Runtime.InteropServices.PosixSignalContext context)
{
    context.Cancel = true;
    try { cancellation.Cancel(); stopped.Wait(TimeSpan.FromSeconds(4)); } catch (ObjectDisposedException) { }
}
using var hangup = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGHUP, RequestStop);
using var terminate = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, RequestStop);
var gameWatch = exitWithGame ? new GameProcessWatch() : null;
string? fingerprint = null;
string? localFingerprint = null;
IReadOnlyList<Run> localRuns = [];
IReadOnlyDictionary<string,HistoricalMeasurement> measurements = new Dictionary<string,HistoricalMeasurement>();
RunDetails? details = null;
string? detailsFingerprint = null;
var settings = new NativeSettings(output);
using var hub = new Hub(output, historyEnabled: () => settings.Current.HubHistoryEnabled);
var csvHistory = new CsvHistory(output);
var failures = 0;
try
{
await using var workspace = new WorkspaceHost(hub, output, database, settings, csvHistory);
await workspace.Start(cancellation.Token);
var workspaceUrlPath = Path.Combine(output, "workspace-url.txt");
AtomicFile.WriteText(workspaceUrlPath, workspace.Url);
try
{
    while (!cancellation.IsCancellationRequested)
    {
        try
        {
            await hub.Tick(cancellation.Token);
            var journal = Path.Combine(output, "completed.tsv");
            var files = string.Join('|', new[] { database, database + "-wal", journal, Path.Combine(output, "imported-history.json") }.Select(p => File.Exists(p) ? $"{new FileInfo(p).Length}:{File.GetLastWriteTimeUtc(p).Ticks}" : "missing"));
            var next = hub.Revision + "|" + files;
            if (next != fingerprint)
            {
                if (files != localFingerprint)
                {
                    localRuns = History.Read(database).Concat(NativeRuns.Read(journal)).ToArray();
                    try { measurements = HistoricalMeasurements.Read(database,localRuns.Select(r=>r.Id).ToHashSet(StringComparer.Ordinal)); }
                    catch (IOException) { measurements = new Dictionary<string,HistoricalMeasurement>(); Console.Error.WriteLine("Historical measurements temporarily unavailable."); }
                    localFingerprint = files;
                }
                var runs = HubHistory.Merge(localRuns.Concat(csvHistory.Runs), hub.Runs, hub.ExternalId);
                hub.MaxHistoryPage = Math.Max(0, (runs.Count(hub.MatchesHistory) - 1) / 50);
                var selectedRun = runs.FirstOrDefault(r => hub.SelectedScenario.Length == 0 || r.Scenario.Equals(hub.SelectedScenario, StringComparison.OrdinalIgnoreCase));
                var nextDetails = selectedRun?.Id + "|" + files;
                if (nextDetails != detailsFingerprint)
                { details = selectedRun is null ? null : RunMetrics.Read(database, selectedRun.Id); detailsFingerprint = nextDetails; }
                workspace.UpdateHistory(runs);
                workspace.Update(WorkspaceData.Build(runs, hub, details,measurements));
                var content = Views.Encode(Views.Build(runs, hub.HistoryPage, hub.SelectedScenario).Concat(hub.Rows()));
                AtomicFile.WriteText(Path.Combine(output, "views.tsv"), content);
                var scenario = hub.SelectedScenario.Length > 0 ? hub.SelectedScenario : runs.FirstOrDefault()?.Scenario;
                var scenarioRuns = runs.Where(r => r.Scenario.Equals(scenario,StringComparison.OrdinalIgnoreCase)).ToArray();
                var points = scenarioRuns.Take(80).Reverse().ToArray();
                var graph = JsonSerializer.Serialize(new { scenarioName=scenario,
                    data=points.Select(r=>r.Score), labels=points.Select(r=>r.Timestamp),
                    personalBest=scenarioRuns.Length>0 ? scenarioRuns.Max(r=>r.Score) : 0,
                    sessionBest=0, primaryColor="27E4A1FF" });
                AtomicFile.WriteText(Path.Combine(output,"graph.json"),graph);
                fingerprint = next;
                Console.WriteLine($"Refreshed {runs.Length} history records.");
            }
            failures = 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine($"History refresh failed: {ex.Message}"); }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellation.IsCancellationRequested)
        {
            // Unexpected data must not terminate the worker the game depends on.
            // Back off so a persistent fault does not spin, and never log payloads.
            failures = Math.Min(failures + 1, 30);
            Console.Error.WriteLine($"History refresh failed unexpectedly ({ex.GetType().Name}).");
        }
        if (args.Contains("--once")) break;
        if (gameWatch?.Exited() == true) { Console.WriteLine("KovaaK's exited; stopping."); break; }
        await Task.Delay(TimeSpan.FromSeconds(1 + failures), cancellation.Token);
    }
}
catch (OperationCanceledException) { }
finally { AtomicFile.DeleteIfContent(workspaceUrlPath, workspace.Url); }
}
finally { stopped.Set(); }

namespace AimMod.InGame
{
    record Row(string Page, string Heading, string Body);
    // Optional (--exit-with-game): stop after KovaaK's has been observed and then
    // closes, so a worker started alongside the game does not outlive it. The
    // worker never starts, stops or attaches to the game process.
    sealed class GameProcessWatch(Func<bool>? running = null)
    {
        const string GameProcess = "FPSAimTrainer-Win64-Shipping";
        readonly Func<bool> isRunning = running ?? Running;
        bool seen;
        long nextCheck;
        static bool Running()
        {
            var processes = System.Diagnostics.Process.GetProcessesByName(GameProcess);
            try { return processes.Length > 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        public bool Exited(bool force = false)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!force && now < nextCheck) return false;
            nextCheck = now + 5 * System.Diagnostics.Stopwatch.Frequency;
            if (isRunning()) { seen = true; return false; }
            return seen;
        }
    }
    static class Views
    {
        public static string Escape(string text) => text.Replace("%", "%25").Replace("\t", "%09").Replace("\r", "%0D").Replace("\n", "%0A");
        public static string Encode(IEnumerable<Row> rows) => "AIMMOD_VIEWS_1\n" + string.Join('\n', rows.Select(r => string.Join('\t', Escape(r.Page), Escape(r.Heading), Escape(r.Body)))) + "\n";
        static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
        static string A(double? value) => value is double v ? N(v) + "%" : "--";
        public static List<Row> Build(IReadOnlyList<Run> runs, int historyPage = 0, string scenario = "")
        {
            var rows = new List<Row>();
            if (runs.Count == 0)
            {
                foreach (var page in new[] { "Overview", "History", "Coaching", "Mechanics" })
                    rows.Add(new(page, "Your practice starts here", "Complete a run to build your history."));
                return rows;
            }
            var groups = runs.GroupBy(r => r.Scenario, StringComparer.OrdinalIgnoreCase).ToArray();
            rows.Add(new("Overview", "Your practice", $"{runs.Count:N0} recent runs   |   {groups.Length} scenarios   |   {runs.Sum(r => r.Duration) / 3600:0.0} hours"));
            var latest = runs[0];
            if (latest.Accuracy is >=0 and <=100) rows.Add(new("Progress", "Latest accuracy", (latest.Accuracy.Value/100).ToString("R",CultureInfo.InvariantCulture)));
            var latestBest=runs.Where(r=>r.Scenario.Equals(latest.Scenario,StringComparison.OrdinalIgnoreCase)).Max(r=>r.Score);
            if (latestBest>0 && latest.Score>=0) rows.Add(new("Progress", "Latest score / best in history", (latest.Score/latestBest).ToString("R",CultureInfo.InvariantCulture)));
            if (latest.Smoothness is >=0 and <=100) rows.Add(new("Progress", "Movement control", (latest.Smoothness.Value/100).ToString("R",CultureInfo.InvariantCulture)));
            rows.Add(new("Overview", latest.Scenario, $"Latest score  {N(latest.Score)}   |   Accuracy  {A(latest.Accuracy)}   |   {N(latest.Duration)}s"));
            foreach (var group in groups.Take(24))
            {
                var items = group.ToArray();
                var best = items.Max(r => r.Score);
                var recent = items.Take(5).Average(r => r.Score);
                rows.Add(new("Overview", group.Key, $"Best in history  {N(best)}   |   Last five average  {N(recent)}   |   {items.Length} runs"));
                if (items.Length >= 10)
                {
                    var previous = items.Skip(5).Take(5).Average(r => r.Score);
                    if (previous > 0)
                    {
                        var delta = (recent / previous - 1) * 100;
                        rows.Add(new("Coaching", group.Key, $"Last five vs previous five: {delta:+0.0;-0.0;0.0}%. " +
                            (delta < -5 ? "Take a short break, then return with one focus for the next block." : delta > 5 ? "Keep the same focus for another short block to check that the improvement holds." : "Keep one focus per block and compare your next five runs.")));
                    }
                }
            }
            var history = runs.Where(r => scenario.Length == 0 || r.Scenario.Equals(scenario, StringComparison.OrdinalIgnoreCase)).ToArray();
            var pageIndex = Math.Clamp(historyPage, 0, Math.Max(0, (history.Length - 1) / 50));
            rows.Add(new("History", "Run history", $"{history.Length:N0} runs · Page {pageIndex + 1} of {Math.Max(1, (history.Length + 49) / 50)}"));
            foreach (var run in history.Skip(pageIndex * 50).Take(50))
                rows.Add(new("History", run.Scenario, $"{run.Timestamp}   |   Score {N(run.Score)}   |   Accuracy {A(run.Accuracy)}   |   {N(run.Duration)}s"));
            // Run-level thresholds ported from src/coaching/engine.ts buildRunCoachingTips.
            // Do not infer shot conversion, target response, or mechanics from score alone.
            if (latest.Accuracy is double accuracy)
            {
                if (accuracy < 75) rows.Insert(0, new("Coaching", "Focus for next run", "Reduce speed slightly and confirm each shot before pushing tempo."));
                else if (accuracy >= 92) rows.Insert(0, new("Coaching", "Strong accuracy", "Increase pace in small steps while keeping shot conversion steady."));
                if (accuracy >= 88 && latest.Kills > 0 && latest.Kills / latest.Duration < 0.75)
                    rows.Add(new("Coaching", "Build pace", "Add short target-switch blocks and plan your next target during confirms."));
            }
            if (!rows.Any(r => r.Page == "Coaching")) rows.Add(new("Coaching", "Build a baseline", "Repeat a scenario for a short block so you can compare your pace and consistency."));
            var measured = runs.Where(r => r.Smoothness.HasValue).Take(20).ToArray();
            if (measured.Length == 0) rows.Add(new("Mechanics", "No movement samples in recent history", "Choose a recorded run with movement data to review your control."));
            foreach (var run in measured)
                rows.Add(new("Mechanics", run.Scenario, $"Control {N(run.Smoothness!.Value)}/100   |   Jitter {(run.Jitter is double j ? N(j) : "--")}   |   Path efficiency {(run.Efficiency is double e ? A(e * 100) : "--")}"));
            return rows;
        }
    }
    static class Checks
    {
        public static void Run()
        {
            static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
            Assert(Views.Escape("a%\tb\nc") == "a%25%09b%0Ac", "Protocol escaping");
            Assert(Views.Build([]).All(r => !r.Body.Contains("0%")), "Empty data must stay unknown");
            Run Make(string scenario, double score) => new("synthetic", scenario, score, null, 60, 0, 0, "2026-01-01", null, null, null, null, false);
            var mixed = Enumerable.Range(0, 10).Select(i => Make(i < 5 ? "A" : "B", i < 5 ? 100 : 10000)).ToArray();
            Assert(!Views.Build(mixed).Any(r => r.Body.Contains("vs previous")), "Never compare scores across scenarios");
            var stable = Enumerable.Range(0, 10).Select(_ => Make("A", 100)).ToArray();
            Assert(Views.Build(stable).Any(r => r.Body.Contains("0.0%")), "Stable block comparison");
            Assert(!Views.Build(stable).Any(r => r.Heading == "Strong accuracy"), "Unknown accuracy must not generate advice");
            Assert(NativeRuns.Parse("run\tid\tScenario\tNaN\t90\t60\t1\t1\t2026-01-01") is null, "Reject non-finite score");
            Assert(NativeRuns.Parse("run\tid\tScenario\t10\t90\t\t1\t1\t2026-01-01") is null, "Reject unknown duration");
            Assert(NativeRuns.Decode("A%2509%09B") == "A%09\tB", "Decode once only");
            var running = false;
            var watch = new GameProcessWatch(() => running);
            Assert(!watch.Exited(true), "Worker keeps running before the game is observed");
            running = true; Assert(!watch.Exited(true), "Running game keeps worker alive");
            running = false; Assert(watch.Exited(true), "Observed game exit stops the worker");
            Console.WriteLine("11 native history checks passed.");
        }
    }
}


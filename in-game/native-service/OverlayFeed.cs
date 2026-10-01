using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace AimMod.InGame;

/// <summary>Session, recent runs and per-scenario numbers for overlay widgets, computed once per history revision.</summary>
sealed class OverlayHistory
{
    internal sealed record SessionRun(string Scenario, double Score, double? Accuracy, long At, double Ratio, bool Pb, double Duration);
    internal sealed record ScenarioStats(string Name, double Best, int Attempts, double? Average, double Last, long LastAt);
    internal sealed record Summary(SessionRun[] Session, SessionRun[] Recent, IReadOnlyDictionary<string, ScenarioStats> Scenarios, string? Latest);
    public static readonly TimeSpan SessionGap = TimeSpan.FromMinutes(60);
    Summary summary = new([], [], new Dictionary<string, ScenarioStats>(), null);
    public Summary Current => Volatile.Read(ref summary);

    public void Update(IReadOnlyList<Run> runs) => Volatile.Write(ref summary, Build(runs));

    internal static Summary Build(IReadOnlyList<Run> runs)
    {
        var dated = runs.Where(r => !string.IsNullOrEmpty(r.Scenario) && double.IsFinite(r.Score) && r.Duration > 0)
            .Select(r => (Run: r, At: HubHistory.Date(r.Timestamp))).Where(x => x.At != DateTimeOffset.MinValue)
            .OrderBy(x => x.At).ToArray();
        // Every run against the best of the same scenario before it.
        var best = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var rated = new SessionRun[dated.Length];
        for (int i = 0; i < dated.Length; i++)
        {
            var (r, at) = dated[i];
            var had = best.TryGetValue(r.Scenario, out var prior) && prior > 0;
            rated[i] = new(r.Scenario, r.Score, r.Accuracy is double a && double.IsFinite(a) ? a : null, at.ToUnixTimeMilliseconds(), had ? r.Score / prior : 1, had && r.Score > prior, r.Duration);
            if (!best.TryGetValue(r.Scenario, out var b) || r.Score > b) best[r.Scenario] = r.Score;
        }
        // The session: the latest runs with no break longer than the gap between them.
        var start = rated.Length;
        for (int i = rated.Length - 1; i >= 0; i--)
        {
            if (i < rated.Length - 1 && rated[i + 1].At - (rated[i].At + (long)(rated[i].Duration * 1000)) > SessionGap.TotalMilliseconds) break;
            start = i;
        }
        var session = rated.Skip(start).TakeLast(200).ToArray();
        var recent = rated.Reverse().Take(10).ToArray();
        var scenarios = new Dictionary<string, ScenarioStats>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in dated.GroupBy(x => x.Run.Scenario, StringComparer.OrdinalIgnoreCase))
        {
            var list = group.ToArray(); var last = list[^1];
            var last10 = list.TakeLast(10).Select(x => x.Run.Score).ToArray();
            scenarios[group.Key] = new(last.Run.Scenario, list.Max(x => x.Run.Score), list.Length, last10.Length > 0 ? last10.Average() : null, last.Run.Score, last.At.ToUnixTimeMilliseconds());
        }
        return new(session, recent, scenarios, dated.Length > 0 ? dated[^1].Run.Scenario : null);
    }

    public object Session(long nowMs)
    {
        var s = Current.Session;
        if (s.Length == 0 || nowMs - (s[^1].At + (long)(s[^1].Duration * 1000)) > SessionGap.TotalMilliseconds) return new { runs = 0, seconds = 0, pbs = 0, graph = Array.Empty<object>() };
        var acc = s.Where(r => r.Accuracy.HasValue).Select(r => r.Accuracy!.Value).ToArray();
        return new
        {
            runs = s.Length, seconds = s.Sum(r => r.Duration), pbs = s.Count(r => r.Pb), accuracy = acc.Length > 0 ? acc.Average() : (double?)null,
            scenarios = s.Select(r => r.Scenario).Distinct(StringComparer.OrdinalIgnoreCase).Count(), bestRatio = s.Max(r => r.Ratio), startedAt = s[0].At,
            graph = s.TakeLast(80).Select(r => new { scenario = r.Scenario, score = r.Score, ratio = Math.Round(r.Ratio, 4), pb = r.Pb }),
        };
    }
    public object Recent() => Current.Recent.Select(r => new { scenario = r.Scenario, score = r.Score, accuracy = r.Accuracy, at = r.At, pb = r.Pb, delta = r.Pb || r.Ratio == 1 ? (double?)null : Math.Round((r.Ratio - 1) * 100, 2) });
    public object? Scenario(string? live)
    {
        var c = Current; var name = !string.IsNullOrEmpty(live) ? live : c.Latest;
        if (name is null) return null;
        return c.Scenarios.TryGetValue(name, out var s) ? new { name = s.Name, best = s.Best, attempts = s.Attempts, average = s.Average, last = s.Last, lastAt = s.LastAt } : new { name, best = (double?)null, attempts = 0, average = (double?)null, last = (double?)null, lastAt = (long?)null };
    }
}

/// <summary>
/// Mouse path and input display samples, only while an overlay page asks for them.
/// The path comes from AimModCore's self-pose.tsv (requested like the developer
/// tools do); key states are read for a fixed set of movement keys and mouse
/// buttons, only while KovaaK's is the foreground window. Nothing is recorded,
/// logged or sent anywhere else, and nothing is written to the game.
/// </summary>
sealed class OverlayMotion(string output, Func<long>? clock = null)
{
    readonly object gate = new();
    readonly List<(long Ms, double Yaw, double Pitch)> path = [];
    long? requested;
    long lastPose;
    Process? game;
    long Now() => clock?.Invoke() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public object Read(bool wantPath, bool wantInput)
    {
        lock (gate)
        {
            object? trail = null, input = null;
            if (wantPath) trail = Path();
            if (wantInput) input = Input();
            return new { path = trail, input };
        }
    }

    object Path()
    {
        var now = Now();
        if (requested is not long last || now - last >= 2000 || now < last)
        {
            requested = now;
            try { File.WriteAllText(System.IO.Path.Combine(output, "self-pose.request"), now.ToString(CultureInfo.InvariantCulture)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (LivePoseFrame.Read(System.IO.Path.Combine(output, "self-pose.tsv"), TimeSpan.FromSeconds(1.5)) is { Stream.Length: 0 } frame) Merge(frame.Poses);
        return Window(now);
    }
    internal void Merge(IReadOnlyList<LivePose> poses)
    {
        foreach (var p in poses)
        {
            // A clock that jumped back by more than a second (a new game session) starts over.
            if (p.UnixMs < lastPose - 1000) { path.Clear(); lastPose = 0; }
            if (p.UnixMs <= lastPose) continue;
            lastPose = p.UnixMs; path.Add((p.UnixMs, p.Camera[4], p.Camera[3]));
        }
        while (path.Count > 0 && path[^1].Ms - path[0].Ms > 5000) path.RemoveAt(0);
        if (path.Count > 600) path.RemoveRange(0, path.Count - 600);
    }
    internal double[][] Window(long now)
    {
        if (path.Count == 0 || now - path[^1].Ms > 1500) return [];
        var origin = path[^1].Ms - 5000;
        return path.Select(p => new[] { p.Ms - origin, Math.Round(p.Yaw, 3), Math.Round(p.Pitch, 3) }).ToArray();
    }

    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    // W A S D, Space, Shift, Ctrl, left and right mouse button: nothing else is ever read.
    internal static readonly (string Name, int Key)[] Keys = [("w", 0x57), ("a", 0x41), ("s", 0x53), ("d", 0x44), ("space", 0x20), ("shift", 0x10), ("ctrl", 0x11), ("lmb", 0x01), ("rmb", 0x02)];
    object Input()
    {
        if (!OperatingSystem.IsWindows() || !GameForeground()) return new { available = false };
        var state = new Dictionary<string, object> { ["available"] = true };
        foreach (var (name, key) in Keys) state[name] = (GetAsyncKeyState(key) & 0x8000) != 0;
        return state;
    }
    bool GameForeground()
    {
        try
        {
            var window = GetForegroundWindow();
            if (window == 0 || GetWindowThreadProcessId(window, out var pid) == 0) return false;
            if (game is null || game.Id != pid || game.HasExited)
            {
                game?.Dispose(); game = null;
                var candidate = Process.GetProcessById(checked((int)pid));
                try { if (!ReplayKeyboard.IsGameExecutable(candidate.MainModule?.FileName)) return false; game = candidate; }
                finally { if (game != candidate) candidate.Dispose(); }
            }
            game.Refresh();
            return !game.HasExited && game.MainWindowHandle == window;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException or OverflowException) { game?.Dispose(); game = null; return false; }
    }
}

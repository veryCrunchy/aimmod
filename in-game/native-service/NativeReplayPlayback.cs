using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AimMod.InGame;

// Playback changes only private replay state and its frame file. It has no
// reference to scoring, game controllers, live actors or Hub upload clients.
sealed class NativeReplayPlayback : IAsyncDisposable
{
    readonly object gate = new();
    readonly string path;
    readonly string commandPath;
    readonly string heartbeatPath;
    string? lastCommand;
    readonly Func<bool>? rendererReady;
    readonly Func<int>? rendererProtocol;
    readonly CancellationTokenSource cancellation = new();
    Task? pump;
    NativeReplay? replay;
    // Optional second run on the same timeline (ghost), or a live view.
    NativeReplay? compare;
    LivePoseFeed? live;
    bool playing, visible;
    double position, speed = 1;
    long anchor = Stopwatch.GetTimestamp(), revision, keyboardSession, keyboardRevision;
    double[] rect = [.13, .22, .84, .58];
    public NativeReplayPlayback(string output, Func<bool>? rendererReady = null, Func<int>? rendererProtocol = null) {
        path = Path.Combine(output, "replay-frame.tsv"); commandPath = Path.Combine(output, "native-replay-command.tsv"); this.rendererReady = rendererReady;
        this.rendererProtocol = rendererProtocol;
        heartbeatPath = Path.Combine(output, "native-replay-worker.txt");
        try { if (File.Exists(commandPath) && new FileInfo(commandPath).Length <= 1024) lastCommand = File.ReadAllText(commandPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    double Time => replay is null ? 0 : live is not null ? 0 : Math.Clamp(position + (playing ? Stopwatch.GetElapsedTime(anchor).TotalSeconds * speed : 0), 0, replay.Frames[^1].T);
    public bool Visible { get { lock (gate) return visible && replay is not null; } }
    public object Status { get { lock (gate) return new { id = replay?.Id, time = Time, duration = replay?.Frames.LastOrDefault()?.T ?? 0, playing, speed, visible,
        mode = live is not null ? "live" : compare is not null ? "compare" : "replay", compareId = compare?.Id }; } }
    internal (long Session, string? Id, long Revision) KeyboardState { get { lock (gate) return visible && replay is not null ? (keyboardSession, replay.Id, keyboardRevision) : (0, null, 0); } }
    internal bool KeyboardCommand(long session, string action, double? value = null) {
        lock (gate) return session != 0 && session == keyboardSession && visible && replay is not null && Command(action, value);
    }
    public void Load(NativeReplay value, NativeReplay? compareWith = null)
    {
        if (value.Frames.Count < 2) throw new ArgumentException("Replay has no frames.");
        if (compareWith is not null && (compareWith.Frames.Count < 2 || compareWith.Scenario != value.Scenario)) throw new ArgumentException("Comparison replay must be of the same scenario.");
        lock (gate) { keyboardSession++; keyboardRevision = revision + 1; replay = value; compare = compareWith; live = null; position = 0; speed = 1; playing = false; visible = true; anchor = Stopwatch.GetTimestamp(); revision++; }
    }
    /// <summary>Follows a live view (spectating) from `feed` in the given world.</summary>
    public void Spectate(LivePoseFeed feed, string scenario, string mapName, double mapScale, string label)
    {
        var camera = new double[] { 0, 0, 0, 0, 0, 0, 90 };
        var placeholder = new NativeReplay(2, "live-" + label, scenario, DateTimeOffset.UtcNow.ToString("O"), "live", 86400,
            [new(0, camera, []), new(86400, camera, [])], [], mapName, mapScale);
        lock (gate) { keyboardSession++; keyboardRevision = revision + 1; replay = placeholder; compare = null; live = feed; position = 0; speed = 1; playing = true; visible = true; anchor = Stopwatch.GetTimestamp(); revision++; }
    }
    public bool Command(string action, double? value = null, double[]? area = null)
    {
        lock (gate)
        {
            if (action == "close") { visible = false; playing = false; replay = null; compare = null; live = null; revision++; return true; }
            if (replay is null || live is not null) return false;
            var now = Time;
            switch (action)
            {
                case "play": position = now >= replay.Frames[^1].T ? 0 : now; playing = true; break;
                case "pause": position = now; playing = false; break;
                case "toggle": position = now >= replay.Frames[^1].T ? 0 : now; playing = !playing; break;
                case "seek-relative": if (value is not double delta || !double.IsFinite(delta) || Math.Abs(delta) > 60) return false; position = Math.Clamp(now + delta, 0, replay.Frames[^1].T); break;
                case "speed-next": position = now; speed = speed switch { .25 => .5, .5 => 1, 1 => 1.5, 1.5 => 2, _ => .25 }; break;
                case "seek": if (value is not double seek || !double.IsFinite(seek)) return false; position = Math.Clamp(seek, 0, replay.Frames[^1].T); break;
                case "speed": if (value is not (.25 or .5 or 1 or 1.5 or 2)) return false; position = now; speed = value.Value; break;
                case "layout":
                    if (area is not { Length: 4 } || area.Any(n => !double.IsFinite(n) || n < 0 || n > 1) || area[2] <= 0 || area[3] <= 0 || area[0] + area[2] > 1.001 || area[1] + area[3] > 1.001) return false;
                    position = now; rect = area.ToArray(); break;
                default: return false;
            }
            anchor = Stopwatch.GetTimestamp(); revision++; return true;
        }
    }
    public static ReplayFrame Sample(NativeReplay value, double time)
    {
        var frames = value.Frames; int lo = 0, hi = frames.Count;
        while (lo < hi) { var mid = (lo + hi) / 2; if (frames[mid].T <= time) lo = mid + 1; else hi = mid; }
        var index = Math.Max(0, lo - 1); var a = frames[index]; var b = frames[Math.Min(index + 1, frames.Count - 1)];
        var dt = b.T - a.T; var amount = dt is > 0 and <= .25 ? Math.Clamp((time - a.T) / dt, 0, 1) : 0;
        double Mix(double x, double y) => x + (y - x) * amount;
        double Angle(double x, double y) => x + (((y - x + 540) % 360 + 360) % 360 - 180) * amount;
        var camera = a.Camera.Select((n, i) => i is >= 3 and <= 5 ? Angle(n, b.Camera[i]) : Mix(n, b.Camera[i])).ToArray();
        // Format 2: exact motion at this instant (rotation from the input
        // stream, tracks from their keyframes), not a blend of 60 Hz frames.
        if (value.Motion is { } motion) { var pose = motion.Pose(time); return new(time, pose.Camera, pose.Actors, a.Stats, pose.Health, pose.Appearance); }
        var next = b.Actors.ToDictionary(actor => actor[0]);
        var actors = a.Actors.Select(actor => !next.TryGetValue(actor[0], out var other) ? actor.ToArray() : actor.Select((n, i) => i == 0 ? n : Mix(n, other[i])).ToArray()).ToArray();
        var nextAppearance = b.Appearance?.ToDictionary(value => value.Id);
        var appearance = a.Appearance?.Select(value => nextAppearance is not null && nextAppearance.TryGetValue(value.Id, out var other) && other.Profile == value.Profile
            ? value with { Rotation = value.Rotation.Select((rotation, axis) => Angle(rotation, other.Rotation[axis])).ToArray() } : value).ToArray();
        return new(time, camera, actors, a.Stats, a.Health, appearance);
    }
    internal string Snapshot()
    {
        lock (gate)
        {
            var time = Time;
            // Wall-clock instant of `time`: the native presenter extrapolates from it exactly.
            var clockStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (replay is not null && playing && time >= replay.Frames[^1].T) { position = time; playing = false; revision++; }
            // A live view ends when its stream stops (2 s).
            if (live is not null && visible && !live.Update()) { visible = false; playing = false; replay = null; live = null; revision++; }
            static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);
            // Extensions require explicit renderer support. Production supplies a
            // capability reader; standalone playback defaults to the current protocol.
            var protocol = rendererProtocol?.Invoke() ?? 5;
            var extended = protocol >= 3;
            var text = new StringBuilder(protocol >= 6 ? "AIMMOD_REPLAY_6\t" : protocol >= 5 ? "AIMMOD_REPLAY_5\t" : protocol >= 4 ? "AIMMOD_REPLAY_4\t" : extended ? "AIMMOD_REPLAY_3\t" : "AIMMOD_REPLAY_2\t").Append(revision).Append('\t').Append(visible && replay is not null ? '1' : '0');
            text.Append('\n');
            if (visible && replay is not null && live is not null)
            {
                // Live view: the newest poses of the watched player, shown a
                // little behind the newest one; the presenter interpolates.
                var (liveTime, window) = live.Window();
                if (window.Count == 0) return text.ToString();
                text.Append("meta\t").Append(Uri.EscapeDataString(replay.Id)).Append('\t').Append(Uri.EscapeDataString(replay.Scenario)).Append('\t').Append(Uri.EscapeDataString(replay.MapName ?? "")).Append('\t').Append(N(replay.MapScale ?? 0)).Append('\n');
                text.Append("time\t").Append(N(liveTime)).Append('\t').Append(N(liveTime + 3600)).Append("\t1\t1\n");
                text.Append("camera"); foreach (var n in window[0].Camera) text.Append('\t').Append(N(n)); text.Append('\n');
                if (extended) text.Append("transport\t").Append(revision).Append('\n');
                foreach (var target in live.Targets) { text.Append("actor"); foreach (var n in target) text.Append('\t').Append(N(n)); text.Append('\n'); }
                if (protocol >= 6) {
                    text.Append("clock\t").Append(clockStamp.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(N(liveTime)).Append('\n');
                    foreach (var (t, camera) in window) { text.Append("motion\t").Append(N(t)); foreach (var n in camera) text.Append('\t').Append(N(n)); text.Append('\n'); }
                }
            }
            else if (visible && replay is not null)
            {
                var frame = Sample(replay, time);
                text.Append("meta\t").Append(Uri.EscapeDataString(replay.Id)).Append('\t').Append(Uri.EscapeDataString(replay.Scenario)).Append('\t').Append(Uri.EscapeDataString(replay.MapName ?? "")).Append('\t').Append(N(replay.MapScale ?? 0)).Append('\n');
                text.Append("time\t").Append(N(time)).Append('\t').Append(N(replay.Frames[^1].T)).Append('\t').Append(playing ? '1' : '0').Append('\t').Append(N(speed)).Append('\n');
                text.Append("camera"); foreach (var n in frame.Camera) text.Append('\t').Append(N(n)); text.Append('\n');
                if (extended) text.Append("transport\t").Append(revision).Append('\n');
                if (extended && frame.Stats is { } stats) {
                    text.Append("stats");
                    foreach (var n in new[] { stats.Score, stats.Shots, stats.Hits, stats.Kills, stats.Damage, stats.Seconds }) text.Append('\t').Append(n is double v ? N(v) : "");
                    text.Append('\n');
                    if (playing && stats.HitTime is double ht && stats.HitDelta is double hd && stats.Hits is double hits)
                        text.Append("hit\t").Append(N(ht)).Append('\t').Append(N(hd)).Append('\t').Append(N(hits)).Append('\t').Append(stats.HitTarget is double target ? N(target) : "").Append('\n');
                }
                foreach (var actor in frame.Actors) { text.Append("actor"); foreach (var n in actor) text.Append('\t').Append(N(n)); text.Append('\n'); }
                if (protocol >= 4 && frame.Health is not null)
                    foreach (var health in frame.Health) text.Append("health\t").Append(N(health.Id)).Append('\t').Append(N(health.Percent)).Append('\n');
                if (protocol >= 6 && playing) text.Append("clock\t").Append(clockStamp.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(N(time)).Append('\n');
                if (protocol >= 6 && playing) {
                    // Render-rate motion: the renderer advances its own clock
                    // every engine frame and interpolates these samples, so view
                    // motion is not limited to this file's publication rate.
                    var end = replay.Frames[^1].T;
                    for (int i = 0; i < MotionSamples; i++) {
                        var at = Math.Min(time + i * MotionStep * speed, end);
                        var pose = Sample(replay, at);
                        text.Append("motion\t").Append(N(at));
                        foreach (var n in pose.Camera) text.Append('\t').Append(N(n));
                        text.Append('\n');
                        if (at >= end) break;
                    }
                    var ahead = Sample(replay, Math.Min(time + VelocityStep, end));
                    var span = Math.Min(time + VelocityStep, end) - time;
                    if (span > 0)
                        foreach (var actor in frame.Actors) {
                            var next = ahead.Actors.FirstOrDefault(a => a[0] == actor[0]);
                            if (next is null) continue;
                            text.Append("velocity\t").Append(N(actor[0]));
                            for (int k = 1; k <= 3; k++) text.Append('\t').Append(N((next[k] - actor[k]) / span));
                            text.Append('\n');
                        }
                }
                if (protocol >= 6 && compare is not null && time <= compare.Frames[^1].T) {
                    // The comparison run on the same timeline: its camera now
                    // and, while playing, a motion window for render-rate posing.
                    var ghost = Sample(compare, time);
                    text.Append("ghost"); foreach (var n in ghost.Camera) text.Append('\t').Append(N(n)); text.Append('\n');
                    if (playing) {
                        var end = compare.Frames[^1].T;
                        for (int i = 0; i < MotionSamples; i++) {
                            var at = Math.Min(time + i * MotionStep * speed, end);
                            text.Append("ghostmotion\t").Append(N(at));
                            foreach (var n in Sample(compare, at).Camera) text.Append('\t').Append(N(n));
                            text.Append('\n');
                            if (at >= end) break;
                        }
                    }
                }
                if (protocol >= 5) {
                    if (replay.Frames[^1].Stats?.Score is double finalScore) text.Append("result\t").Append(N(finalScore)).Append('\n');
                    foreach (var appearance in frame.Appearance ?? []) {
                        text.Append("appearance\t").Append(N(appearance.Id)).Append('\t').Append(Uri.EscapeDataString(appearance.Profile));
                        foreach (var rotation in appearance.Rotation) text.Append('\t').Append(N(rotation));
                        text.Append('\n');
                    }
                }
            }
            return text.ToString();
        }
    }
    public void Start()
    {
        pump = Task.Run(async () => {
            string? last = null;
            long lastHeartbeat = 0;
            var pacer = new FramePacer();
            try {
                while (!cancellation.IsCancellationRequested) {
                    try {
                    var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    if (stamp != lastHeartbeat) {
                        try { File.WriteAllText(heartbeatPath + ".next", stamp.ToString(CultureInfo.InvariantCulture)); File.Move(heartbeatPath + ".next", heartbeatPath, true); lastHeartbeat = stamp; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    }
                    ReadCommand();
                    bool active;
                    lock (gate) active = visible;
                    if (active && rendererReady is not null && !rendererReady()) {
                        lock (gate) { if (visible) {
                            Console.Error.WriteLine("Replay stopped: renderer acknowledgement unavailable or not ready.");
                            Command("close");
                        } }
                    }
                    var data = Snapshot();
                    if (data != last) {
                        try { File.WriteAllText(path + ".next", data, Utf8); File.Move(path + ".next", path, true); last = data; }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Retry a shared-file race on the next frame. */ }
                    }
                    await Task.Delay(pacer.Next(active ? ActiveInterval : IdleInterval), cancellation.Token);
                    } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
                      catch (Exception ex) {
                        // Keep transport available after a malformed frame or an
                        // unexpected callback failure. Fail closed and allow a new load.
                        Console.Error.WriteLine($"Replay pump recovered from {ex.GetType().Name}: {ex.Message}");
                        Command("close");
                        await Task.Delay(100, cancellation.Token);
                    }
                }
            } catch (OperationCanceledException) { }
        });
    }
    internal void ReadCommand()
    {
        try {
            var file = new FileInfo(commandPath);
            if (!file.Exists || file.Length is < 1 or > 1024) return;
            var text = File.ReadAllText(commandPath);
            if (text == lastCommand || !text.EndsWith('\n')) return;
            lastCommand = text;
            var cells = text.TrimEnd('\r', '\n').Split('\t');
            if (cells.Length is < 2 or > 3 || cells[0].Length is < 1 or > 100 || cells[0].Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')) return;
            if (cells[1] is not ("toggle" or "seek" or "seek-relative" or "speed-next" or "close")) return;
            double? value = cells.Length == 3 && double.TryParse(cells[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            Command(cells[1], value);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        cancellation.Cancel(); if (pump is not null) await pump;
        Command("close");
        // Publish a closed frame atomically, and remove the heartbeat so the game
        // sees the worker as unavailable immediately rather than after 3 s.
        try { AtomicFile.WriteText(path, Snapshot()); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (pump is not null) try { File.Delete(heartbeatPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        cancellation.Dispose();
    }
    bool disposed;
    static readonly UTF8Encoding Utf8 = new(false);
    // Motion window: 0.4 s of playback at 120 Hz (the renderer interpolates between).
    internal const int MotionSamples = 48;
    internal const double MotionStep = 1.0 / 120, VelocityStep = 0.02;
    internal static readonly TimeSpan ActiveInterval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 30), IdleInterval = TimeSpan.FromMilliseconds(100);
}

// Deadline-based pacing. Task.Delay rounds each wait up to the system timer
// tick (typically 15.6 ms), so fixed 33 ms delays run at roughly 21 Hz. Waiting
// until an absolute deadline keeps the average publication rate at the target
// without raising the global timer resolution. A late frame never triggers a
// burst: when more than one interval behind, the schedule restarts from now.
sealed class FramePacer(Func<long>? clock = null)
{
    readonly Func<long> now = clock ?? Stopwatch.GetTimestamp;
    long deadline;
    internal TimeSpan Next(TimeSpan interval)
    {
        var current = now();
        var step = (long)(interval.TotalSeconds * Stopwatch.Frequency);
        deadline = deadline == 0 || current - deadline > step ? current + step : deadline + step;
        var wait = deadline - current;
        return wait <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency);
    }
}

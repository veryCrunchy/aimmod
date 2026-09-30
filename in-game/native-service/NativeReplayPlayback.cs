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
    double Time => replay is null ? 0 : Math.Clamp(position + (playing ? Stopwatch.GetElapsedTime(anchor).TotalSeconds * speed : 0), 0, replay.Frames[^1].T);
    public object Status { get { lock (gate) return new { id = replay?.Id, time = Time, duration = replay?.Frames.LastOrDefault()?.T ?? 0, playing, speed, visible }; } }
    internal (long Session, string? Id, long Revision) KeyboardState { get { lock (gate) return visible && replay is not null ? (keyboardSession, replay.Id, keyboardRevision) : (0, null, 0); } }
    internal bool KeyboardCommand(long session, string action, double? value = null) {
        lock (gate) return session != 0 && session == keyboardSession && visible && replay is not null && Command(action, value);
    }
    public void Load(NativeReplay value)
    {
        if (value.Frames.Count < 2) throw new ArgumentException("Replay has no frames.");
        lock (gate) { keyboardSession++; keyboardRevision = revision + 1; replay = value; position = 0; speed = 1; playing = false; visible = true; anchor = Stopwatch.GetTimestamp(); revision++; }
    }
    public bool Command(string action, double? value = null, double[]? area = null)
    {
        lock (gate)
        {
            if (action == "close") { visible = false; playing = false; replay = null; revision++; return true; }
            if (replay is null) return false;
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
            if (replay is not null && playing && time >= replay.Frames[^1].T) { position = time; playing = false; revision++; }
            static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);
            // Extensions require explicit renderer support. Production supplies a
            // capability reader; standalone playback defaults to the current protocol.
            var protocol = rendererProtocol?.Invoke() ?? 5;
            var extended = protocol >= 3;
            var text = new StringBuilder(protocol >= 5 ? "AIMMOD_REPLAY_5\t" : protocol >= 4 ? "AIMMOD_REPLAY_4\t" : extended ? "AIMMOD_REPLAY_3\t" : "AIMMOD_REPLAY_2\t").Append(revision).Append('\t').Append(visible && replay is not null ? '1' : '0');
            text.Append('\n');
            if (visible && replay is not null)
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
                        try { File.WriteAllText(path + ".next", data, new UTF8Encoding(false)); File.Move(path + ".next", path, true); last = data; }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Retry a shared-file race on the next frame. */ }
                    }
                    await Task.Delay(active ? 33 : 100, cancellation.Token);
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
        cancellation.Cancel(); if (pump is not null) await pump;
        Command("close");
        try { File.WriteAllText(path, Snapshot()); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        cancellation.Dispose();
    }
}

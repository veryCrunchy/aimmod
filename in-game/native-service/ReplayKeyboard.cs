using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AimMod.InGame;

// Reads only replay shortcut key states while the actual game window owns focus.
// No keyboard hook, injection, text capture or entry into the game Lua VM.
sealed class ReplayKeyboard(NativeReplayPlayback playback, string output) : IAsyncDisposable
{
    readonly CancellationTokenSource stop = new();
    readonly ReplayKeyboardEdges edges = new();
    readonly string acknowledgement = Path.Combine(output, "native-replay-renderer.json");
    Task? pump;
    Process? game;
    long ackChecked;
    bool acknowledged;
    long ackSession;
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);

    internal static bool IsGameExecutable(string? path) => path?.Replace('\\', '/').EndsWith("/FPSAimTrainer/Binaries/Win64/FPSAimTrainer-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase) == true;
    bool GameForeground()
    {
        var window = GetForegroundWindow();
        if (window == 0 || GetWindowThreadProcessId(window, out var pid) == 0) return false;
        if (game is null || game.Id != pid || game.HasExited) {
            game?.Dispose(); game = null;
            var candidate = Process.GetProcessById(checked((int)pid));
            try { if (!IsGameExecutable(candidate.MainModule?.FileName)) return false; game = candidate; }
            finally { if (game != candidate) candidate.Dispose(); }
        }
        game.Refresh();
        return !game.HasExited && game.MainWindowHandle == window;
    }
    internal static bool IsAcknowledged(string text, DateTime stamp, DateTime now, string id, long revision)
    {
        if (text.Length > 4096 || now - stamp > TimeSpan.FromSeconds(3) || stamp - now > TimeSpan.FromSeconds(1)) return false;
        try {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            return root.GetProperty("state").GetString() == "ready" && root.GetProperty("mode").GetString() == "main"
                && root.GetProperty("active").GetBoolean() && root.GetProperty("replayId").GetString() == id
                && root.GetProperty("revision").TryGetInt64(out var observed) && observed >= revision;
        } catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
    }
    bool Acknowledged((long Session, string? Id, long Revision) state)
    {
        if (state.Session != ackSession || Stopwatch.GetElapsedTime(ackChecked).TotalMilliseconds >= 100) {
            ackSession = state.Session; ackChecked = Stopwatch.GetTimestamp(); acknowledged = false;
            try {
                var file = new FileInfo(acknowledgement);
                if (file.Exists && file.Length <= 4096 && state.Id is { } id)
                    acknowledged = IsAcknowledged(File.ReadAllText(acknowledgement), file.LastWriteTimeUtc, DateTime.UtcNow, id, state.Revision);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return acknowledged;
    }
    public void Start()
    {
        if (pump is not null || !OperatingSystem.IsWindows()) return;
        pump = Task.Run(async () => {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(16));
            try {
                while (await timer.WaitForNextTickAsync(stop.Token)) {
                    try {
                        var state = playback.KeyboardState;
                        var eligible = state.Session != 0 && Acknowledged(state) && GameForeground();
                        edges.Poll(state.Session, eligible, key => (GetAsyncKeyState(key) & 0x8000) != 0,
                            (action, value) => {
                                // Recheck foreground and load identity immediately before dispatch.
                                if (GameForeground()) playback.KeyboardCommand(state.Session, action, value);
                            });
                    } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException or OverflowException) {
                        edges.Reset(); game?.Dispose(); game = null;
                    }
                }
            } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); if (pump is not null) await pump;
        edges.Reset(); game?.Dispose(); game = null; stop.Dispose();
    }
}

sealed class ReplayKeyboardEdges
{
    static readonly int[] Keys = [0x20, 0x25, 0x27, 0x1B];
    readonly bool[] previous = new bool[4];
    long session;
    bool primed;
    internal void Reset() { primed = false; session = 0; Array.Clear(previous); }
    internal void Poll(long current, bool eligible, Func<int, bool> down, Action<string, double?> send)
    {
        if (!eligible || current == 0) { Reset(); return; } // Never read keys outside the focused replay.
        var next = Keys.Select(down).ToArray();
        bool modifier = down(0x11) || down(0x12); // Ctrl/Alt combinations belong to the OS/UI.
        bool shift = down(0x10);
        var ready = primed && session == current && !modifier;
        session = current; primed = true;
        var pressed = next.Select((value, i) => value && !previous[i]).ToArray();
        next.CopyTo(previous, 0);
        if (!ready) return; // Held keys on entry/focus regain are only baselined.
        if (pressed[3]) { send("close", null); Reset(); return; }
        if (pressed[0]) send("toggle", null);
        if (pressed[1] != pressed[2]) send("seek-relative", (pressed[2] ? 1 : -1) * (shift ? 1 : 5));
    }
}

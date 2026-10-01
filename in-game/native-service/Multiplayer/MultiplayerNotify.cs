using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

// What the player should know even with the AimMod panel closed: an invite,
// the host asking everyone to ready up, or a countdown. AimModNativeUI shows
// it in an always-on, non-interactive Gameface layer (Notify.lua + notify page)
// and plays the game's own UI sound. Kind: invite, ready, countdown or info.
// Invite is set for an incoming invite the popup can join or dismiss.
sealed record GameNotice(string Id, string Kind, string Title, string Body, string? Key, int? Countdown, string Sound)
{
    public string? Invite { get; init; }
}

// Per-player multiplayer preferences in multiplayer-settings.json (local only).
// AutoReady: ready up on joining, when the content arrives, and after each match.
sealed record MultiplayerPrefs(string Hotkey = "F7", bool ReadyOnJoin = false, bool ReadyOnContent = true, bool ReadyAfterMatch = false,
    bool QuietDuringRanked = true, bool Sounds = true, double Volume = 0.8)
{
    public static MultiplayerPrefs Load(string? path)
    {
        try
        {
            if (path is null || !File.Exists(path) || new FileInfo(path).Length > 2048) return new();
            return Apply(new(), JsonDocument.Parse(File.ReadAllText(path)).RootElement) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    // Known keys only, each checked; anything else is ignored rather than trusted.
    public static MultiplayerPrefs? Apply(MultiplayerPrefs p, JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        bool? Flag(string k) => e.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
        if (e.TryGetProperty("hotkey", out var h) && h.ValueKind == JsonValueKind.String) p = p with { Hotkey = MultiplayerHotkey.Parse(h.GetString()).Name };
        if (Flag("readyOnJoin") is { } a) p = p with { ReadyOnJoin = a };
        if (Flag("readyOnContent") is { } b) p = p with { ReadyOnContent = b };
        if (Flag("readyAfterMatch") is { } c) p = p with { ReadyAfterMatch = c };
        if (Flag("quietDuringRanked") is { } d) p = p with { QuietDuringRanked = d };
        if (Flag("sounds") is { } s) p = p with { Sounds = s };
        if (e.TryGetProperty("volume", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var vol) && double.IsFinite(vol)) p = p with { Volume = Math.Round(Math.Clamp(vol, 0, 1), 2) };
        return p;
    }
    public void Save(string path) => AtomicFile.WriteText(path, JsonSerializer.Serialize(this, Protocol.Json));
}

// The global multiplayer hotkey (default F7). Keys are read only while the game
// window has focus and only while this machine is in a lobby or has an invite,
// the same way the replay shortcuts are read; nothing is hooked or injected.
sealed class MultiplayerHotkey : IDisposable
{
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    readonly CancellationTokenSource stop = new();
    readonly Func<bool> armed;
    readonly Action pressed;
    readonly string settingsPath;
    Task? pump;
    Process? game;
    int key = 0x76; // VK_F7
    long settingsAt;
    public string KeyName { get; private set; } = "F7";

    public MultiplayerHotkey(string output, Func<bool> armed, Action pressed)
    {
        this.armed = armed; this.pressed = pressed;
        settingsPath = Path.Combine(output, "multiplayer-settings.json");
        ReadSettings();
    }

    // {"hotkey":"F7"}: F1 to F12. Anything else keeps F7.
    internal static (int Code, string Name) Parse(string? name) =>
        name is { Length: 2 or 3 } && name[0] is 'F' or 'f' && int.TryParse(name[1..], out var n) && n is >= 1 and <= 12 ? (0x6F + n, "F" + n) : (0x76, "F7");
    void ReadSettings()
    {
        try
        {
            if (!File.Exists(settingsPath) || new FileInfo(settingsPath).Length > 1024) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            var value = doc.RootElement.TryGetProperty("hotkey", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
            (key, KeyName) = Parse(value);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }

    bool GameForeground()
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

    public void Start()
    {
        if (pump is not null || !OperatingSystem.IsWindows()) return;
        pump = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(30));
            var wasDown = true; // a key held when arming is only baselined
            try
            {
                while (await timer.WaitForNextTickAsync(stop.Token))
                {
                    try
                    {
                        if (Environment.TickCount64 - settingsAt > 5000) { settingsAt = Environment.TickCount64; ReadSettings(); }
                        if (!armed() || !GameForeground()) { wasDown = true; continue; }
                        // Ctrl, Alt and Shift combinations belong to the game and the OS.
                        var modifier = (GetAsyncKeyState(0x11) & 0x8000) != 0 || (GetAsyncKeyState(0x12) & 0x8000) != 0 || (GetAsyncKeyState(0x10) & 0x8000) != 0;
                        var down = (GetAsyncKeyState(key) & 0x8000) != 0;
                        if (down && !wasDown && !modifier) pressed();
                        wasDown = down;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException or OverflowException) { game?.Dispose(); game = null; wasDown = true; }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
    }

    public void Dispose()
    {
        stop.Cancel();
        try { pump?.Wait(1000); } catch (AggregateException) { }
        game?.Dispose(); stop.Dispose();
    }
}

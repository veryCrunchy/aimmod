using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

// What the player should know even with the AimMod panel closed: an invite,
// the host asking everyone to ready up, or a countdown. AimModNativeUI shows
// it in an always-on, non-interactive Gameface layer (Notify.lua + notify page)
// and plays the game's own UI sound. Kind: invite, ready, countdown or info.
// Invite is set for an incoming invite the popup can join or dismiss.
// Actions are clickable buttons (the layer takes clicks only then); Badge is a small
// always-visible line such as "2 watching: Catfish, X".
sealed record NoticeAction(string Label, string Action, string Id);
sealed record GameNotice(string Id, string Kind, string Title, string Body, string? Key, int? Countdown, string Sound)
{
    public string? Invite { get; init; }
    // The small label above the title; the notice layer picks one by kind when it's empty.
    public string? Eyebrow { get; init; }
    public IReadOnlyList<NoticeAction>? Actions { get; init; }
    // Who the notice is about (invites, watch requests, friends): the notice layer shows their
    // Steam picture, or their initials until it arrives. Peer is never sent to the UI as such.
    public string? Peer { get; init; }
    public string? PeerName { get; init; }
    // A short extra line under the body, such as a keybind that differs from KovaaK's default.
    public string? Note { get; init; }
}

// Per-player multiplayer preferences in multiplayer-settings.json (local only).
// AutoReady: ready up on joining, when the content arrives, and after each match.
// RoundVolume: the bomb and round sounds of CS matches (BombAudio), 0 silences them.
// GunVolume: other players' gunfire and footsteps in the shooting modes (GunAudio.cs), 0 silences them.
sealed record MultiplayerPrefs(string Hotkey = "F7", bool ReadyOnJoin = false, bool ReadyOnContent = true, bool ReadyAfterMatch = false,
    bool QuietDuringRanked = true, bool Sounds = true, double Volume = 0.8, string Avatar = AvatarProfiles.Default, bool HideScenario = false,
    string SpectatePrivacy = "friends", bool ShowWatchers = true, bool Onboarded = false, bool FriendToasts = true, bool LeaveRun = true, bool ShowBoard = true, string ScoreboardKey = "Tab",
    double RoundVolume = 0.7, double GunVolume = 0.7)
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
        if (Flag("hideScenario") is { } hide) p = p with { HideScenario = hide };
        if (Flag("showWatchers") is { } sw) p = p with { ShowWatchers = sw };
        if (Flag("onboarded") is { } ob) p = p with { Onboarded = ob };
        if (Flag("friendToasts") is { } ft) p = p with { FriendToasts = ft };
        if (Flag("leaveRun") is { } lr) p = p with { LeaveRun = lr };
        if (Flag("showBoard") is { } sb) p = p with { ShowBoard = sb };
        if (e.TryGetProperty("scoreboardKey", out var sk) && sk.ValueKind == JsonValueKind.String && MultiplayerHotkey.ParseHold(sk.GetString()).Name == sk.GetString()) p = p with { ScoreboardKey = sk.GetString()! };
        if (e.TryGetProperty("spectatePrivacy", out var sp) && sp.GetString() is "friends" or "ask" or "off") p = p with { SpectatePrivacy = sp.GetString()! };
        if (e.TryGetProperty("avatar", out var av) && av.ValueKind == JsonValueKind.String && AvatarProfiles.Find(av.GetString()) is { } look) p = p with { Avatar = look.Id };
        if (e.TryGetProperty("volume", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var vol) && double.IsFinite(vol)) p = p with { Volume = Math.Round(Math.Clamp(vol, 0, 1), 2) };
        if (e.TryGetProperty("roundVolume", out var rv) && rv.ValueKind == JsonValueKind.Number && rv.TryGetDouble(out var round) && double.IsFinite(round)) p = p with { RoundVolume = Math.Round(Math.Clamp(round, 0, 1), 2) };
        if (e.TryGetProperty("gunVolume", out var gv) && gv.ValueKind == JsonValueKind.Number && gv.TryGetDouble(out var gun) && double.IsFinite(gun)) p = p with { GunVolume = Math.Round(Math.Clamp(gun, 0, 1), 2) };
        return p;
    }
    public void Save(string path) => AtomicFile.WriteText(path, JsonSerializer.Serialize(this, Protocol.Json));
}

// AimMod's keys and the clashes they would have. The clip key is AimModCore's
// (clip-settings.tsv: AIMMOD_CLIPS_1 / key / before / after); this only edits its key line.
static class KeyBinds
{
    public static readonly string[] ClipKeys = ["F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12", "Insert", "Home", "End", "PageUp", "PageDown", "Pause", "ScrollLock"];

    public static string ReadClipKey(string? output)
    {
        try
        {
            var path = output is null ? null : Path.Combine(output, "clip-settings.tsv");
            if (path is null || !File.Exists(path) || new FileInfo(path).Length > 1024) return "F8";
            var key = File.ReadAllLines(path).Select(l => l.Split('\t')).FirstOrDefault(c => c.Length == 2 && c[0] == "key")?[1];
            return ClipKeys.Contains(key) ? key! : "F8";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "F8"; }
    }
    public static void WriteClipKey(string output, string key)
    {
        if (!ClipKeys.Contains(key)) return;
        var path = Path.Combine(output, "clip-settings.tsv");
        string before = "8", after = "2";
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length <= 1024)
                foreach (var c in File.ReadAllLines(path).Select(l => l.Split('\t')).Where(c => c.Length == 2))
                {
                    if (c[0] == "before" && int.TryParse(c[1], out var b) && b is >= 0 and <= 60) before = c[1];
                    if (c[0] == "after" && int.TryParse(c[1], out var a) && a is >= 0 and <= 60) after = c[1];
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        AtomicFile.WriteText(path, "AIMMOD_CLIPS_1\nkey\t" + key + "\nbefore\t" + before + "\nafter\t" + after + "\n");
    }

    // Keys the game binds, from UE's Input.ini (written once the player changes a bind, so it
    // can be missing; the defaults then apply, see GameBinds).
    static (DateTime At, string? Root, IReadOnlySet<string> Keys) cached = (DateTime.MinValue, null, new HashSet<string>());
    public static IReadOnlySet<string> GameKeys(string? root)
    {
        if (DateTime.UtcNow - cached.At < TimeSpan.FromSeconds(30) && cached.Root == root) return cached.Keys;
        var read = ReadGameKeys(root); cached = (DateTime.UtcNow, root, read); return read;
    }
    static IReadOnlySet<string> ReadGameKeys(string? root)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in GameBinds.InputFiles(root))
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 1 << 20) continue;
                foreach (var line in File.ReadLines(path))
                {
                    var key = System.Text.RegularExpressions.Regex.Match(line, @"[(,]Key=([A-Za-z0-9_]+)");
                    if (!key.Success || !(line.Contains("ActionMappings=", StringComparison.Ordinal) || line.Contains("AxisMappings=", StringComparison.Ordinal))) continue;
                    // AimMod's keys only act with no modifier held, so only bare binds clash; record modified ones as such.
                    var modifiers = new[] { "bShift", "bCtrl", "bAlt", "bCmd" }.Where(m => line.Contains(m + "=True", StringComparison.Ordinal)).Select(m => m[1..]).ToArray();
                    keys.Add(modifiers.Length == 0 ? key.Groups[1].Value : string.Join('+', modifiers) + "+" + key.Groups[1].Value);
                }
                break; // the first config found is the one the game uses
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return keys;
    }

    public static IReadOnlyList<string> Conflicts(string hotkey, string clipKey, IReadOnlySet<string> game)
    {
        var list = new List<string>();
        if (hotkey == clipKey) list.Add("The lobby key and the clip key are both " + hotkey + ".");
        if (game.Contains(hotkey)) list.Add("KovaaK’s already uses " + hotkey + "; pick another lobby key.");
        if (game.Contains(clipKey)) list.Add("KovaaK’s already uses " + clipKey + "; pick another clip key.");
        return list;
    }
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
    // The hold-to-show scoreboard key (Tab by default), only while a match is on.
    public Func<bool>? BoardArmed { get; set; }
    public Action<bool>? BoardHeld { get; set; }
    int holdKey = 0x09; bool holdDown;
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
    internal static (int Code, string Name) ParseHold(string? name) => name switch { "CapsLock" => (0x14, "CapsLock"), "Tilde" => (0xC0, "Tilde"), _ => (0x09, "Tab") };
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
            holdKey = ParseHold(doc.RootElement.TryGetProperty("scoreboardKey", out var sk) && sk.ValueKind == JsonValueKind.String ? sk.GetString() : null).Code;
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
                        // The scoreboard shows while its key is held, in a match, with the game in front.
                        var hold = BoardHeld is not null && BoardArmed?.Invoke() == true && GameForeground() && (GetAsyncKeyState(holdKey) & 0x8000) != 0;
                        if (hold != holdDown) { holdDown = hold; BoardHeld?.Invoke(hold); }
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

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

// CS mode on this machine (game-modes.md 6.6): the map's objective metadata for the
// host, the round and loadout lines AimModCore applies, the CS HUD, and the B (buy)
// and E (use: plant, defuse) keys. Keys are read only while the game window has focus
// and a CS round runs, like the lobby hotkey; nothing is hooked or injected.
sealed partial class MultiplayerService
{
    MapObjectives? csObjectives;
    bool buyOpen, useHeld; string? csRoundKey;
    readonly CsKeyReader csKeys = new();
    public const string CsBuyKey = "B", CsUseKey = "E";

    // aimmod_<map>_<game>.aimmod.json next to the arena's map (KovaaK's maps folder), or in
    // AimMod's own maps folder.
    MapObjectives? LoadObjectives(string scenarioText)
    {
        var map = scenarioText.Replace("\r\n", "\n").Split('\n').FirstOrDefault(l => l.StartsWith("MapName=", StringComparison.Ordinal))?[8..].Trim();
        if (string.IsNullOrEmpty(map)) return null;
        var file = MapObjectives.FileFor(map);
        foreach (var folder in new[] { library.Root is { } r ? Path.Combine(r, "maps") : null, outputFolder is { } o ? Path.Combine(o, "maps") : null })
        {
            if (folder is null) continue;
            var path = Path.Combine(folder, file);
            try { if (File.Exists(path) && new FileInfo(path).Length < 4 << 20 && MapObjectives.Parse(File.ReadAllText(path)) is { } parsed) return parsed; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    long HostOffset() => core is null && hostPeer is not null ? clocks.GetValueOrDefault(hostPeer)?.Offset ?? 0 : 0;

    // play-state.tsv lines for CS (contract in game-modes.md 6.6.1):
    //   round\t<phase>\t<frozen 0/1>\t<buy open 0/1>\t<phase ends, local unix ms>
    //   loadout\t<primary profile or ->\t<pistol profile or ->\t<armour>\t<helmet 0/1>\t<kit 0/1>
    IEnumerable<string> CsPlayLines(MatchSnapshot match)
    {
        if (match.Cs is not { } cs || cs.Players.FirstOrDefault(p => p.Member == SelfId) is not { } me) yield break;
        var hostNow = clock() + HostOffset();
        var buyWindow = cs.Phase == "freeze" || (cs.Phase == "live" && cs.LiveAt is { } live && hostNow < live + CsRules.BuyMs);
        yield return "round\t" + cs.Phase + "\t" + (cs.Phase == "freeze" ? 1 : 0) + "\t" + (buyWindow ? 1 : 0) + "\t" + (cs.PhaseEndsAt - HostOffset());
        string Profile(string? id) => CsRules.Find(id)?.Combat.Name ?? "-";
        yield return "loadout\t" + Profile(me.Primary) + "\t" + Profile(me.Secondary) + "\t" + Math.Round(me.Armor).ToString(CultureInfo.InvariantCulture) + "\t" + (me.Helmet ? 1 : 0) + "\t" + (me.Kit ? 1 : 0);
    }

    // The buy menu: what this player's side can buy now, numbered for the digit keys.
    IReadOnlyList<(string Item, string Label, int Price)> BuyMenu(string side, CsPlayerView me)
    {
        var list = CsRules.Weapons.Where(w => w.Side == "any" || w.Side == side).Select(w => (w.Id, w.Label, w.Price)).ToList();
        list.Add(("kevlar", "Kevlar", CsRules.KevlarPrice));
        list.Add(("kevlar-helmet", "Kevlar + helmet", me.Armor >= CsRules.MaxArmor && !me.Helmet ? CsRules.HelmetUpgradePrice : CsRules.KevlarHelmetPrice));
        if (side == CsRules.CT) list.Add(("defuse-kit", "Defuse kit", CsRules.KitPrice));
        return list.Take(9).ToList();
    }

    // B toggles the buy menu (buy window only), digits buy from it, E held plants or defuses.
    void CsInput(MatchSnapshot match)
    {
        if (match.Cs is not { } cs || cs.Players.FirstOrDefault(p => p.Member == SelfId) is not { } me) return;
        var key = match.Id + "#" + cs.Round;
        if (key != csRoundKey) { csRoundKey = key; buyOpen = false; }
        var hostNow = clock() + HostOffset();
        var buyWindow = cs.Phase == "freeze" || (cs.Phase == "live" && cs.LiveAt is { } live && hostNow < live + CsRules.BuyMs);
        if (!buyWindow) buyOpen = false;
        if (!csKeys.Foreground()) { if (useHeld) { useHeld = false; Command("use", JsonSerializer.SerializeToElement(new { held = false })); } return; }
        if (csKeys.Pressed('B') && buyWindow && me.Alive) buyOpen = !buyOpen;
        if (buyOpen)
        {
            var menu = BuyMenu(me.Side, me);
            for (var i = 0; i < menu.Count; i++)
                if (csKeys.Pressed((char)('1' + i))) Command("buy", JsonSerializer.SerializeToElement(new { item = menu[i].Item }));
        }
        var held = csKeys.Down('E') && me.Alive && cs.Phase is "live" or "planted";
        if (held != useHeld) { useHeld = held; Command("use", JsonSerializer.SerializeToElement(new { held })); }
    }

    // CS HUD for the notice layer (top edge): health, armour, money, the round clock, score,
    // bomb state and progress, the buy menu while it's open, and the keys with any clash.
    internal sealed record CsBuyItem(int Key, string Label, int Price, bool Affordable);
    internal sealed record CsHudView(string Phase, int? Left, int Round, int[] Score, string Side, int Money, bool Alive, double Health, double Armor, bool Helmet, bool Kit,
        string Bomb, string? Site, int? BombIn, double? PlantProgress, double? DefuseProgress, bool BuyOpen, IReadOnlyList<CsBuyItem>? Buy, string? LastRound,
        string BuyKey, string UseKey, IReadOnlyList<string> KeyClashes);
    internal CsHudView? CsHud()
    {
        if (Current is not { Match: { Phase: MatchPhases.Countdown or MatchPhases.Live, Cs: { } cs } m } || cs.Players.FirstOrDefault(p => p.Member == SelfId) is not { } me) return null;
        var hostNow = clock() + HostOffset();
        var team = me.Team;
        int? Secs(long? at) => at is { } t ? (int)Math.Max(0, Math.Ceiling((t - hostNow) / 1000.0)) : null;
        double? Progress(long? doneAt, long total) => doneAt is { } d ? Math.Round(Math.Clamp(1 - (d - hostNow) / (double)total, 0, 1), 2) : null;
        var b = cs.Bomb;
        var menu = buyOpen ? BuyMenu(me.Side, me).Select((x, i) => new CsBuyItem(i + 1, x.Label, x.Price, x.Price <= me.Money)).ToArray() : null;
        string? last = cs.LastWinner is { } w ? (w == team ? "Round won" : "Round lost") + " · " + cs.LastReason : null;
        return new CsHudView(cs.Phase, Secs(cs.PhaseEndsAt), cs.Round, [cs.Score[team - 1], cs.Score[2 - team]], me.Side, me.Money, me.Alive, me.Health, me.Armor, me.Helmet, me.Kit,
            b.State, b.Site, b.State == "planted" ? Secs(b.ExplodesAt) : null, Progress(b.PlantDoneAt, CsRules.PlantMs),
            Progress(b.DefuseDoneAt, b.Defuser is { } d && cs.Players.FirstOrDefault(p => p.Member == d)?.Kit == true ? CsRules.KitDefuseMs : CsRules.DefuseMs),
            buyOpen, menu, cs.Phase == "end" ? last : null, CsBuyKey, CsUseKey, CsKeyClashes(KeyBinds.GameKeys(library.Root)));
    }

    // KovaaK's own binds (Input.ini) that use the CS keys.
    public static IReadOnlyList<string> CsKeyClashes(IReadOnlySet<string> gameKeys)
    {
        var list = new List<string>();
        if (gameKeys.Contains(CsBuyKey)) list.Add("KovaaK’s also uses " + CsBuyKey + " (buy).");
        if (gameKeys.Contains(CsUseKey)) list.Add("KovaaK’s also uses " + CsUseKey + " (use, plant, defuse).");
        return list;
    }
}

// Letter and digit keys while the game window is in front (GetAsyncKeyState), with press edges.
sealed class CsKeyReader
{
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    readonly HashSet<char> wasDown = [];
    public bool Foreground()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var window = GetForegroundWindow();
            if (window == 0 || GetWindowThreadProcessId(window, out var pid) == 0) return false;
            using var process = Process.GetProcessById(checked((int)pid));
            return ReplayKeyboard.IsGameExecutable(process.MainModule?.FileName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException or OverflowException) { return false; }
    }
    public bool Down(char key) => OperatingSystem.IsWindows() && (GetAsyncKeyState(char.ToUpperInvariant(key)) & 0x8000) != 0;
    public bool Pressed(char key)
    {
        var down = Down(key);
        var edge = down && !wasDown.Contains(key);
        if (down) wasDown.Add(key); else wasDown.Remove(key);
        return edge;
    }
}

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
    public const string CsBuyKey = "B", CsUseKey = "E", CsDropKey = "G";
    // Why the last use or drop was refused, shown on the HUD for a moment.
    string? csRefusal; long csRefusalUntil;
    void CsCommand(string action, object args)
    {
        var result = Command(action, JsonSerializer.SerializeToElement(args));
        if (!result.Ok && result.Message is { } why && !(action == "use" && args.ToString()!.Contains("False", StringComparison.Ordinal)))
        { csRefusal = why; csRefusalUntil = clock() + 2500; }
    }

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

    // lobby.eligibility for the map selector: scenario name -> { ok, reason?, players }, for modes
    // that need map data of their own (CS); null for the others (every scenario fits). Rebuilt when
    // the library changes.
    public sealed record ScenarioFit(bool Ok, string? Reason, string? Players);
    (int Count, string Mode, long At, IReadOnlyDictionary<string, ScenarioFit>? Map)? eligibilityCache;
    IReadOnlyDictionary<string, ScenarioFit>? Eligibility(string mode)
    {
        if (mode != LobbyModes.Cs) return null;
        var scenarios = library.Scenarios;
        if (eligibilityCache is { } c && c.Mode == mode && c.Count == scenarios.Count && clock() - c.At < 30_000) return c.Map;
        var map = new Dictionary<string, ScenarioFit>(StringComparer.Ordinal);
        foreach (var s in scenarios)
            map.TryAdd(s.Name, library.CsMapProblem(s.Map) is { } problem ? new ScenarioFit(false, problem, "6, 8 or 10 players") : new ScenarioFit(true, null, "6, 8 or 10 players"));
        eligibilityCache = (scenarios.Count, mode, clock(), map);
        return map;
    }

    long HostOffset() => core is null && hostPeer is not null ? clocks.GetValueOrDefault(hostPeer)?.Offset ?? 0 : 0;

    // round-state.tsv lines for CS (contract in game-modes.md 6.6.1 and 6.6.2):
    //   phase\t<phase>\t<frozen 0/1>\t<buy open 0/1>\t<phase ends, local unix ms>
    //   loadout\t<primary or ->\t<pistol or ->\t<armour>\t<helmet 0/1>\t<kit 0/1>\t<knife or ->\t<bomb or ->
    //     (weapon profile names for slots 0-3; the bomb only for its carrier)
    //   bomb\t<dropped|planted|defused>\t<x>\t<y>\t<z>\t<explodes at, local unix ms, 0>\t<defusing 0/1>
    //     (only while the bomb lies in the world: where AimModCore draws it)
    IEnumerable<string> CsPlayLines(MatchSnapshot match)
    {
        if (match.Cs is not { } cs || cs.Players.FirstOrDefault(p => p.Member == SelfId) is not { } me) yield break;
        var hostNow = clock() + HostOffset();
        var buyWindow = cs.Phase == "freeze" || (cs.Phase == "live" && cs.LiveAt is { } live && hostNow < live + CsRules.BuyMs);
        yield return "phase\t" + cs.Phase + "\t" + (cs.Phase == "freeze" ? 1 : 0) + "\t" + (buyWindow ? 1 : 0) + "\t" + (cs.PhaseEndsAt - HostOffset());
        yield return CsLoadoutLine(me, cs.Bomb.Carrier == SelfId);
        if (CsBombLine(cs.Bomb, HostOffset()) is { } bomb) yield return bomb;
    }

    internal static string CsLoadoutLine(CsPlayerView me, bool carrier)
    {
        string Profile(string? id) => CsRules.Find(id)?.Combat.Name ?? "-";
        return "loadout\t" + Profile(me.Primary) + "\t" + Profile(me.Secondary) + "\t" + Math.Round(me.Armor).ToString(CultureInfo.InvariantCulture) + "\t" + (me.Helmet ? 1 : 0) + "\t" + (me.Kit ? 1 : 0)
            + "\t" + (me.Alive ? CsRules.Knife.Combat.Name : "-") + "\t" + (carrier && me.Alive ? CsRules.Bomb.Combat.Name : "-");
    }

    internal static string? CsBombLine(CsBombView b, long hostToLocal)
    {
        if (b.State is not ("dropped" or "planted" or "defused") || b.Position is not { Length: 3 } at) return null;
        static string N(double v) => Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);
        var explodes = b.State == "planted" && b.ExplodesAt is { } e ? Math.Max(0, e - hostToLocal) : 0;
        return "bomb\t" + b.State + "\t" + N(at[0]) + "\t" + N(at[1]) + "\t" + N(at[2]) + "\t" + explodes.ToString(CultureInfo.InvariantCulture) + "\t" + (b.Defuser is not null ? 1 : 0);
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
    long csEscapedAt = long.MinValue;
    internal const int SwallowMenuMs = 1500;
    internal bool CsSwallowMenu(long now) => now - csEscapedAt is >= 0 and < SwallowMenuMs;
    // Test hook: Escape closed the buy menu (the key reader only sees the real keyboard).
    internal void CsEscapeForTest() { buyOpen = false; csEscapedAt = clock(); }
    void CsInput(MatchSnapshot match)
    {
        if (match.Cs is not { } cs || cs.Players.FirstOrDefault(p => p.Member == SelfId) is not { } me) return;
        CsSounds(match, cs);
        var key = match.Id + "#" + cs.Round;
        if (key != csRoundKey) { csRoundKey = key; buyOpen = false; }
        var hostNow = clock() + HostOffset();
        var buyWindow = cs.Phase == "freeze" || (cs.Phase == "live" && cs.LiveAt is { } live && hostNow < live + CsRules.BuyMs);
        if (!buyWindow || me.InBuyZone == false || !me.Alive) buyOpen = false;
        var foreground = csKeys.Foreground();
        DeadSpectate(match, cs, me, foreground);
        if (!foreground) { if (useHeld) { useHeld = false; Command("use", JsonSerializer.SerializeToElement(new { held = false })); } return; }
        if (csKeys.Pressed('B') && buyWindow && me.Alive && me.InBuyZone != false) buyOpen = !buyOpen;
        // Escape closes the buy menu. KovaaK's also opens its pause menu on Escape; AimModNativeUI
        // closes that again while the notice file says so (swallowMenu, for a moment after).
        if (csKeys.Pressed((char)0x1B) && buyOpen) { buyOpen = false; csEscapedAt = clock(); }
        if (buyOpen)
        {
            var menu = BuyMenu(me.Side, me);
            for (var i = 0; i < menu.Count; i++)
                if (csKeys.Pressed((char)('1' + i))) Command("buy", JsonSerializer.SerializeToElement(new { item = menu[i].Item }));
        }
        // Planting: E, or fire with the bomb in your hands (CS); defusing: E.
        var holdingBomb = poseTracker?.Weapon == CsRules.BombSlot && cs.Bomb.Carrier == SelfId;
        var held = (csKeys.Down('E') || (holdingBomb && !buyOpen && csKeys.Down((char)0x01))) && me.Alive && cs.Phase is "live" or "planted";
        if (held != useHeld) { useHeld = held; CsCommand("use", new { held }); }
        if (csKeys.Pressed('G') && me.Alive && !buyOpen) CsCommand("drop", new { });
        CsHold(match, cs, me);
    }

    // Bomb and round sounds (BombAudio): the cues for what changed, and the planted bomb's beep
    // from where it lies relative to this player's camera.
    readonly BombAudio roundAudio = new();
    readonly RoundSoundPlan roundSounds = new();
    internal BombAudio RoundAudio => roundAudio;
    void CsSounds(MatchSnapshot match, CsView cs)
    {
        (double X, double Y, double Yaw)? listener = ownRecent.Count > 0 ? (ownRecent[^1].X, ownRecent[^1].Y, ownRecent[^1].Yaw) : null;
        var volume = prefs.RoundVolume;
        foreach (var cue in roundSounds.Update(match.Id, cs, SelfId)) roundAudio.Play(cue, listener);
        var bomb = RoundSoundPlan.Beeping(cs) is { } b ? (b.At, b.ExplodesAt - HostOffset()) : ((double[], long)?)null;
        roundAudio.Update(volume, bomb, listener);
    }

    // The knife's own sounds for this player's slashes and stabs (KovaaK's gunshot is off for it): a
    // swish for a miss, a thud when the ray meets a drawn player within reach.
    void KnifeSounds(IEnumerable<ShotFeed.Shot> shots, Func<int, long, TrackSeen?> targetAt, long offset)
    {
        foreach (var s in shots)
        {
            if (s.Weapon is not (CsRules.KnifeSlot or CsRules.StabSlot)) continue;
            var stab = s.Weapon == CsRules.StabSlot;
            roundAudio.Play(new RoundCue(KnifeSound(s, stab, s.Target == 0 ? null : targetAt(s.Target, s.UnixMs + offset))), null);
        }
    }
    internal static string KnifeSound(ShotFeed.Shot s, bool stab, TrackSeen? target)
    {
        var reach = (stab ? CsRules.StabRangeCm : CsRules.KnifeRangeCm) + CombatWeapon.RangeToleranceCm;
        var hit = target is { } t && Math.Sqrt((t.X - s.X) * (t.X - s.X) + (t.Y - s.Y) * (t.Y - s.Y)) - t.Radius <= reach;
        return hit ? (stab ? "knife-stab" : "knife-hit") : stab ? "knife-stab-swish" : "knife-swish";
    }

    // Which weapon slot this player holds, for the item the others see in their hands: sent when
    // it changes (and again each round, which starts the host's view from scratch).
    string? csHoldSent;
    void CsHold(MatchSnapshot match, CsView cs, CsPlayerView me)
    {
        if (poseTracker?.Weapon is not { } slot || !me.Alive) return;
        var key = match.Id + "#" + cs.Round + "#" + slot;
        if (key == csHoldSent) return;
        csHoldSent = key;
        Command("hold", JsonSerializer.SerializeToElement(new { slot }));
    }

    // CS HUD for the notice layer, kept clear of the crosshair: the score strip and clocks at
    // the top, money, health, armour and kit bottom left, the plant/defuse bar low in the middle,
    // the kill feed top right, round-end and halftime banners, and the clickable buy menu on
    // the left while it's open (B in buy time; number keys still buy).
    internal sealed record CsBuyItem(int? Key, string Id, string Label, string Category, int Price, bool Owned, bool Affordable, string? Disabled, string? Profile);
    internal sealed record CsFeedLine(long Id, string Killer, string Victim, string Weapon, bool Head, string? You, int KillerTeam, bool TeamKill = false);
    internal sealed record CsBanner(string Title, string Reason, bool Won, int Team);
    internal sealed record CsHudView(string Phase, int? Left, int Round, int Rounds, int TScore, int CtScore, string Side, int Team,
        int Money, int? MoneyDelta, bool Alive, double Health, double Armor, bool Helmet, bool Kit,
        string Bomb, string? Site, int? BombIn, double? PlantProgress, double? DefuseProgress, string? UseHint,
        bool BuyOpen, bool BuyWindow, int? BuyLeft, IReadOnlyList<CsBuyItem>? Buy, CsBanner? Banner, string? Notice, IReadOnlyList<CsFeedLine> Feed,
        string? Primary, string? Secondary, string BuyKey, string UseKey, IReadOnlyList<string> KeyClashes,
        string? InSite = null, string? Callout = null, IReadOnlyList<CsMarker>? Sites = null,
        bool HasBomb = false, string? BombCarrier = null, string? Refused = null, string DropKey = CsDropKey, int TAlive = 0, int CtAlive = 0,
        IReadOnlyList<CsHurt>? Hurt = null, string? HitMarker = null, string? Watching = null, string? WatchHint = null);
    // Where damage came from, around the crosshair: bearing in degrees from where you look (negative
    // left), the damage, and how old the hit is (ms) so the marker fades.
    internal sealed record CsHurt(long Id, int Bearing, int Damage, int Age);
    // A bomb site on the HUD compass: its bearing from where you look (degrees, negative left) and distance.
    internal sealed record CsMarker(string Name, int Bearing, int Meters);
    static readonly string[] BuyCategories = ["pistol", "smg", "rifle", "heavy", "gear"];
    internal CsHudView? CsHud()
    {
        if (Current is not { Match: { Phase: MatchPhases.Countdown or MatchPhases.Live, Cs: { } cs } m } lobby || cs.Players.FirstOrDefault(p => p.Member == SelfId) is not { } me) return null;
        var hostNow = clock() + HostOffset();
        int? Secs(long? at) => at is { } t ? (int)Math.Max(0, Math.Ceiling((t - hostNow) / 1000.0)) : null;
        double? Progress(long? doneAt, long total) => doneAt is { } d ? Math.Round(Math.Clamp(1 - (d - hostNow) / (double)total, 0, 1), 2) : null;
        string Name(string? id) => id == SelfId ? "You" : LobbyRules.CleanName(lobby.Members.FirstOrDefault(x => x.Id == id)?.Name ?? m.Standings.FirstOrDefault(x => x.MemberId == id)?.Name, "Player");
        var b = cs.Bomb;
        var tTeam = cs.Team1Side == CsRules.T ? 1 : 2;
        var buyWindow = cs.Phase == "freeze" || (cs.Phase == "live" && cs.LiveAt is { } live && hostNow < live + CsRules.BuyMs);
        int? buyLeft = cs.Phase == "freeze" ? Secs(cs.PhaseEndsAt) + (int)(CsRules.BuyMs / 1000) : cs.LiveAt is { } l2 && buyWindow ? Secs(l2 + CsRules.BuyMs) : null;
        // The buy menu: every item with why it can't be bought now; the number keys follow the old list.
        CsBuyItem[]? menu = null;
        if (buyOpen)
        {
            var keys = BuyMenu(me.Side, me).Select((x, i) => (x.Item, Key: i + 1)).ToDictionary(x => x.Item, x => x.Key);
            string? Why(string side, int price, bool owned)
            {
                if (owned) return "Already yours";
                if (!me.Alive) return "You’re down until the next round";
                if (!buyWindow) return "Buy time is over";
                if (side != "any" && side != me.Side) return side == CsRules.T ? "Terrorists only" : "Counter-Terrorists only";
                if (me.InBuyZone == false) return "Go back to your buy zone";
                if (price > me.Money) return "$" + (price - me.Money).ToString("N0", CultureInfo.InvariantCulture) + " short";
                return null;
            }
            var list = CsRules.Weapons.Select(w =>
            {
                var owned = (w.Slot == 0 ? me.Primary : me.Secondary) == w.Id;
                return new CsBuyItem(keys.TryGetValue(w.Id, out var k) ? k : null, w.Id, w.Label, w.Class == "sniper" ? "rifle" : w.Class, w.Price, owned, w.Price <= me.Money, Why(w.Side, w.Price, owned), w.Combat.Name);
            }).ToList();
            var helmetPrice = me.Armor >= CsRules.MaxArmor && !me.Helmet ? CsRules.HelmetUpgradePrice : CsRules.KevlarHelmetPrice;
            list.Add(new(keys.GetValueOrDefault("kevlar"), "kevlar", "Kevlar", "gear", CsRules.KevlarPrice, me.Armor >= CsRules.MaxArmor, CsRules.KevlarPrice <= me.Money, Why("any", CsRules.KevlarPrice, me.Armor >= CsRules.MaxArmor), null));
            list.Add(new(keys.GetValueOrDefault("kevlar-helmet"), "kevlar-helmet", "Kevlar + helmet", "gear", helmetPrice, me.Helmet && me.Armor >= CsRules.MaxArmor, helmetPrice <= me.Money, Why("any", helmetPrice, me.Helmet && me.Armor >= CsRules.MaxArmor), null));
            list.Add(new(keys.GetValueOrDefault("defuse-kit"), "defuse-kit", "Defuse kit", "gear", CsRules.KitPrice, me.Kit, CsRules.KitPrice <= me.Money, Why(CsRules.CT, CsRules.KitPrice, me.Kit), null));
            menu = list.Select(x => x.Key == 0 ? x with { Key = null } : x).OrderBy(x => Array.IndexOf(BuyCategories, x.Category)).ToArray();
        }
        // Money from the last round's result (win reward or loss bonus), shown until the next round goes live.
        int? delta = null;
        if (cs.Phase is "end" or "freeze" && cs.Events.LastOrDefault(e => e.Kind == "round-end") is { } roundEnd)
            delta = cs.Events.Where(e => e.Kind == "money" && e.Member == SelfId && e.T == roundEnd.T && e.Text is "round-win" or "round-loss").Sum(e => e.Amount);
        CsBanner? banner = null;
        if (cs.Phase == "end" && cs.LastWinner is { } w)
        {
            var reason = cs.LastReason switch { "bomb" => "The bomb exploded", "defuse" => "The bomb was defused", "time" => "Time ran out", _ => "All enemies eliminated" };
            banner = new CsBanner((w == tTeam ? "Terrorists" : "Counter-Terrorists") + " win", reason, w == me.Team, w);
        }
        string? notice = cs.Events.LastOrDefault(e => e.Kind is "halftime" or "overtime-half") is { } half && hostNow - half.T < 8000
            ? (half.Kind == "halftime" ? "Halftime · Switching sides" : "Overtime · Switching sides") : null;
        var feed = cs.Events.Where(e => e.Kind == "kill" && hostNow - e.T < 7000).TakeLast(5).Select(e =>
        {
            var parts = (e.Text ?? "").Split('\t');
            var killer = parts.Length > 0 ? parts[0] : null;
            var weapon = parts.Length > 1 ? CsRules.FindAny(parts[1])?.Label ?? "" : "";
            return new CsFeedLine(e.Id, Name(killer), Name(e.Member), weapon, parts.Length > 2 && parts[2] == "1", killer == SelfId ? "killer" : e.Member == SelfId ? "victim" : null,
                cs.Players.FirstOrDefault(p => p.Member == killer)?.Team ?? 0, parts.Length > 3 && parts[3] == "TK");
        }).ToArray();
        string? hint = !me.Alive ? null
            : me.Side == CsRules.T && b.Carrier == SelfId && cs.Phase == "live" ? (me.Site is { } inSite ? "Hold " + CsUseKey + " to plant at " + inSite : "You have the bomb: plant it at a site (" + CsDropKey + " drops it)")
            : me.Side == CsRules.T && b.State == "dropped" ? "The bomb is down: walk over it to pick it up"
            : me.Side == CsRules.CT && b.State == "planted" ? "Hold " + CsUseKey + " at the bomb to defuse" : null;
        var refused = clock() < csRefusalUntil ? csRefusal : null;
        var planting = b.Planter == SelfId ? Progress(b.PlantDoneAt, CsRules.PlantMs) : null;
        var defusing = b.Defuser == SelfId ? Progress(b.DefuseDoneAt, me.Kit ? CsRules.KitDefuseMs : CsRules.DefuseMs) : null;
        return new CsHudView(cs.Phase, Secs(cs.PhaseEndsAt), cs.Round, cs.HalfRounds * 2, cs.Score[tTeam - 1], cs.Score[2 - tTeam], me.Side, me.Team,
            me.Money, delta, me.Alive, me.Health, me.Armor, me.Helmet, me.Kit,
            b.State, b.Site, b.State == "planted" ? Secs(b.ExplodesAt) : null, planting, defusing, hint,
            buyOpen, buyWindow && me.Alive, buyLeft, menu, banner, notice, feed,
            CsRules.Find(me.Primary)?.Label, CsRules.Find(me.Secondary)?.Label, CsBuyKey, CsUseKey, CsKeyClashes(KeyBinds.GameKeys(library.Root)),
            me.Alive ? me.Site : null, me.Alive ? me.Callout : null, SiteMarkers(cs, me.Side == CsRules.T || b.State == "planted" ? b.Position : null),
            b.Carrier == SelfId, me.Side == CsRules.T && b.Carrier is { } bc ? Name(bc) : null, refused, CsDropKey,
            cs.Players.Count(p => p.Side == CsRules.T && p.Alive), cs.Players.Count(p => p.Side == CsRules.CT && p.Alive),
            HurtMarkers(m, hostNow), HitMarker(m, hostNow),
            !me.Alive && deadWatch is { } watched ? Name(watched) : null,
            !me.Alive && deadWatch is not null ? (DeadWatchCandidates(cs, m.Players, SelfId).Count > 1 ? "Click or Space: next player · Right click: previous" : "The only player left") : null);
    }

    // The last hits you took (1.5 s), as bearings from where you look: the hit came from the
    // opposite of its ray (the event's dir points from the shooter to you).
    IReadOnlyList<CsHurt>? HurtMarkers(MatchSnapshot match, long hostNow)
    {
        if (LiveCombat(match) is not { } combat || ownRecent.Count == 0) return null;
        var yaw = ownRecent[^1].Yaw;
        var list = combat.Events.Where(e => e.Kind == "damage" && e.Member == SelfId && e.Dir is { Length: 3 } && hostNow - e.T is >= 0 and < 1500).TakeLast(4)
            .Select(e =>
            {
                var bearing = Math.Atan2(-e.Dir![1], -e.Dir[0]) * 180 / Math.PI - yaw;
                bearing = ((bearing % 360) + 540) % 360 - 180;
                return new CsHurt(e.Id, (int)Math.Round(bearing), (int)Math.Round(e.Amount), (int)(hostNow - e.T));
            }).ToArray();
        return list.Length > 0 ? list : null;
    }

    // Your own hit landing (400 ms): "hit", "head" or "kill", for the crosshair hit marker.
    string? HitMarker(MatchSnapshot match, long hostNow)
    {
        if (LiveCombat(match) is not { } combat) return null;
        var mine = combat.Events.LastOrDefault(e => e.Attacker == SelfId && e.Kind is "damage" or "death" && hostNow - e.T is >= 0 and < 400);
        return mine is null ? null : mine.Kind == "death" ? "kill" : mine.Head ? "head" : "hit";
    }

    // The sites relative to this player's own last camera sample (no pose feed: no markers).
    IReadOnlyList<CsMarker>? SiteMarkers(CsView cs, double[]? bomb)
    {
        if (cs.Sites is not { Count: > 0 } sites || ownRecent.Count == 0) return null;
        var me = ownRecent[^1];
        // A dropped bomb (for Terrorists) or the planted bomb (for everyone) is a marker too.
        var points = sites.ToList();
        if (bomb is { Length: 3 }) points.Add(new CsSiteView("Bomb", bomb[0], bomb[1], bomb[2]));
        return points.Select(s =>
        {
            var bearing = Math.Atan2(s.Y - me.Y, s.X - me.X) * 180 / Math.PI - me.Yaw;
            bearing = ((bearing % 360) + 540) % 360 - 180;
            return new CsMarker(s.Name, (int)Math.Round(bearing), (int)Math.Round(Math.Sqrt((s.X - me.X) * (s.X - me.X) + (s.Y - me.Y) * (s.Y - me.Y)) / 100));
        }).ToArray();
    }

    // From the clickable buy menu (notice layer): open or close it, or buy one item.
    LobbyResult CsAction(string action, string? item)
    {
        if (Current is not { Match: { Cs: { } cs } m } || cs.Players.FirstOrDefault(p => p.Member == SelfId) is not { } me) return LobbyResult.Fail("no-match", "No CS round is running.");
        var hostNow = clock() + HostOffset();
        var buyWindow = cs.Phase == "freeze" || (cs.Phase == "live" && cs.LiveAt is { } live && hostNow < live + CsRules.BuyMs);
        // Same round key as the B key, so the next input pass doesn't treat this as a new round and close it.
        csRoundKey = m.Id + "#" + cs.Round;
        if (action == "cs-buy-menu") { buyOpen = !buyOpen && buyWindow && me.Alive && me.InBuyZone != false; return LobbyResult.Success; }
        if (item is null || (CsRules.Find(item) is null && !CsRules.Equipment.Contains(item))) return LobbyResult.Fail("invalid", "Unknown item.");
        return Command("buy", JsonSerializer.SerializeToElement(new { item }));
    }

    // KovaaK's own binds (Input.ini) that use the CS keys.
    public static IReadOnlyList<string> CsKeyClashes(IReadOnlySet<string> gameKeys)
    {
        var list = new List<string>();
        if (gameKeys.Contains(CsBuyKey)) list.Add("KovaaK’s also uses " + CsBuyKey + " (buy).");
        if (gameKeys.Contains(CsUseKey)) list.Add("KovaaK’s also uses " + CsUseKey + " (use, plant, defuse).");
        if (gameKeys.Contains(CsDropKey)) list.Add("KovaaK’s also uses " + CsDropKey + " (drop the bomb).");
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

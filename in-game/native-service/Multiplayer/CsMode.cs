using System.Globalization;
using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

// Phase 3: CS-style competitive (in-game/docs/game-modes.md 6.6). Two teams of
// 3, 4 or 5, T and CT, rounds with freeze and buy time, the CS2 economy, buyable
// armour with the CS2 damage rules, bomb plant and defuse on the map-port
// objective metadata, halves with a side switch, and overtime. The host owns all
// of it; hits are the same host-validated claims as the combat modes
// (CombatMatch), with the shooter's bought weapon and the victim's armour.

// A buyable weapon. Slot 0 primary, 1 secondary (pistol). ArmorPenetration: the share
// of damage that reaches health through armour (CS2 "armor penetration").
sealed record CsWeapon(string Id, string Label, int Price, string Side, int Slot, string Class, int KillReward, double ArmorPenetration, CombatWeapon Combat);

static class CsRules
{
    // CS2 competitive timers (ms).
    public const long FreezeMs = 15_000, BuyMs = 20_000, RoundMs = 115_000, BombMs = 40_000, PlantMs = 3_200, DefuseMs = 10_000, KitDefuseMs = 5_000, RoundEndMs = 7_000;
    // CS2 economy.
    public const int StartMoney = 800, MaxMoney = 16_000, OvertimeMoney = 12_500, OvertimeHalf = 3;
    public const int WinElimination = 3_250, WinBomb = 3_500, WinDefuse = 3_500, WinTime = 3_250;
    public const int LossBase = 1_400, LossStep = 500, LossSteps = 4; // 1400, 1900, 2400, 2900, 3400
    public const int PlantReward = 300, DefuseReward = 300, PlantedLossBonus = 800;
    public const int KevlarPrice = 650, KevlarHelmetPrice = 1_000, HelmetUpgradePrice = 350, KitPrice = 400;
    public const double ArmorBonus = 0.5; // armour lost per point of damage it absorbs
    public const double PlantRadiusCm = 0, DefuseRadiusCm = 100, BombPickupCm = 80, PlantMoveCm = 40;
    public const double MaxHealth = 100, MaxArmor = 100;
    public const string T = "T", CT = "CT";

    static CombatWeapon W(string label, double damage, double tbs, bool auto) => new("AimMod CS " + label, damage, 4, tbs, auto);
    // CS2 reference weapons: price, side, slot, class, kill reward, armour penetration, damage, fire interval.
    public static readonly CsWeapon[] Weapons =
    [
        new("glock", "Glock-18", 200, T, 1, "pistol", 300, 0.47, W("Glock-18", 30, 0.15, false)),
        new("usp", "USP-S", 200, CT, 1, "pistol", 300, 0.505, W("USP-S", 35, 0.17, false)),
        new("deagle", "Desert Eagle", 700, "any", 1, "pistol", 300, 0.932, W("Desert Eagle", 53, 0.225, false)),
        new("mac10", "MAC-10", 1050, T, 0, "smg", 600, 0.575, W("MAC-10", 29, 0.075, true)),
        new("mp9", "MP9", 1250, CT, 0, "smg", 600, 0.6, W("MP9", 26, 0.07, true)),
        new("ak47", "AK-47", 2700, T, 0, "rifle", 300, 0.775, W("AK-47", 36, 0.1, true)),
        new("m4a1s", "M4A1-S", 2900, CT, 0, "rifle", 300, 0.7, W("M4A1-S", 38, 0.1, true)),
        new("awp", "AWP", 4750, "any", 0, "sniper", 100, 0.975, W("AWP", 115, 1.46, false)),
    ];
    public static CsWeapon? Find(string? id) => Weapons.FirstOrDefault(w => w.Id == id);
    public static CsWeapon? ByProfile(string name) => Weapons.FirstOrDefault(w => w.Combat.Name == name);
    public static CsWeapon DefaultPistol(string side) => side == T ? Weapons[0] : Weapons[1];
    public static readonly string[] Equipment = ["kevlar", "kevlar-helmet", "defuse-kit"];

    // CS2 armour: a share of the damage (the weapon's armour penetration) reaches health, the
    // armour absorbs the rest at 0.5 armour per point; once armour runs out the rest is health
    // damage. A headshot is only reduced with a helmet.
    public static (double Health, double Armor) Armor(double damage, bool head, double armor, bool helmet, double penetration)
    {
        if (armor <= 0 || (head && !helmet)) return (damage, 0);
        var toHealth = damage * penetration;
        var cost = (damage - toHealth) * ArmorBonus;
        if (cost > armor) { toHealth = damage - armor / ArmorBonus; cost = armor; }
        return (toHealth, cost);
    }

    // Loss bonus for a loss after `streak` consecutive losses (0 for the first).
    public static int LossBonus(int streak) => LossBase + LossStep * Math.Clamp(streak, 0, LossSteps);
}

// Map-port objective metadata (aimmod_<map>_<game>.aimmod.json, "aimmod.map-objectives"
// version 1): zones (bomb sites, buy zones) as AABBs, team spawns, in map units times
// map_scale for centimetres (world units).
sealed record ObjectiveZone(string Type, string Team, string Name, double[] Min, double[] Max)
{
    // Inside, with the player's eye 64 cm above the centre and a vertical allowance.
    public bool Contains(double x, double y, double eyeZ, double margin = 0) =>
        x >= Min[0] - margin && x <= Max[0] + margin && y >= Min[1] - margin && y <= Max[1] + margin && eyeZ >= Min[2] - 100 && eyeZ - 64 <= Max[2] + 150;
}
sealed record ObjectiveSpawn(string Team, double X, double Y, double Z, double Yaw);
// CsProblem: why the map can't host CS competitive (null: it can). Only the AimMod CS map spec
// (the file's "cs" block, "aimmod.cs-map" version 1: T and CT spawns, bomb sites A and B, T and
// CT buy zones, optional callouts) makes a map eligible; the zones alone don't.
sealed record MapObjectives(IReadOnlyList<ObjectiveZone> Zones, IReadOnlyList<ObjectiveSpawn> Spawns, string? CsProblem = MapObjectives.NoCsData)
{
    public const string NoCsData = "No CS map data";
    public const int MinCsSpawns = 5;
    public IReadOnlyList<ObjectiveZone> Callouts => Zones.Where(z => z.Type == "callout").ToArray();
    // The same rules as the map port (tools/map-port/mapport/csmap.py problems()).
    public static string? CsProblemOf(IReadOnlyList<ObjectiveZone> zones, IReadOnlyList<ObjectiveSpawn> spawns)
    {
        var sites = zones.Where(z => z.Type == "bomb_site").Select(z => z.Name).ToList();
        if (sites.Count == 0) return "No bomb sites";
        if (!sites.Contains("A") || !sites.Contains("B")) return "Needs bomb sites A and B";
        foreach (var (side, team) in new[] { ("T", "terrorist"), ("CT", "counter_terrorist") })
            if (spawns.Count(s => s.Team == team) < MinCsSpawns) return "Fewer than " + MinCsSpawns + " " + side + " spawns";
        foreach (var (side, team) in new[] { ("T", "terrorist"), ("CT", "counter_terrorist") })
            if (!zones.Any(z => z.Type == "buy_zone" && z.Team == team)) return "No " + side + " buy zone";
        return null;
    }
    public IReadOnlyList<ObjectiveZone> BombSites => Zones.Where(z => z.Type == "bomb_site").ToArray();
    public IReadOnlyList<ObjectiveZone> BuyZones(string side) => Zones.Where(z => z.Type == "buy_zone" && (z.Team == "any" || z.Team == (side == CsRules.T ? "terrorist" : "counter_terrorist"))).ToArray();
    public IReadOnlyList<ObjectiveSpawn> SpawnsFor(string side) => Spawns.Where(s => s.Team == "any" || s.Team == (side == CsRules.T ? "terrorist" : "counter_terrorist")).ToArray();

    public static string FileFor(string mapFile) => Path.GetFileNameWithoutExtension(mapFile) + ".aimmod.json";

    public static MapObjectives? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.GetProperty("format").GetString() != "aimmod.map-objectives" || root.GetProperty("version").GetInt32() != 1) return null;
            var scale = root.TryGetProperty("map_scale", out var ms) && ms.TryGetDouble(out var sv) && sv is > 0 and < 100 ? sv : 1;
            static double[]? V(JsonElement e) => e.ValueKind == JsonValueKind.Array && e.GetArrayLength() == 3 && e.EnumerateArray().All(x => x.ValueKind == JsonValueKind.Number) ? e.EnumerateArray().Select(x => x.GetDouble()).ToArray() : null;
            var zones = new List<ObjectiveZone>(); var spawns = new List<ObjectiveSpawn>();
            if (root.TryGetProperty("zones", out var zs) && zs.ValueKind == JsonValueKind.Array)
                foreach (var z in zs.EnumerateArray().Take(64))
                {
                    if (!z.TryGetProperty("aabb", out var box) || V(box.GetProperty("min")) is not { } lo || V(box.GetProperty("max")) is not { } hi) continue;
                    zones.Add(new ObjectiveZone(z.GetProperty("type").GetString() ?? "zone", z.TryGetProperty("team", out var t) ? t.GetString() ?? "any" : "any",
                        z.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", lo.Select(v => v * scale).ToArray(), hi.Select(v => v * scale).ToArray()));
                }
            if (root.TryGetProperty("spawns", out var ss) && ss.ValueKind == JsonValueKind.Array)
                foreach (var sp in ss.EnumerateArray().Take(128))
                    if (V(sp.GetProperty("origin")) is { } o)
                        spawns.Add(new ObjectiveSpawn(sp.TryGetProperty("team", out var t) ? t.GetString() ?? "any" : "any", o[0] * scale, o[1] * scale, o[2] * scale,
                            sp.TryGetProperty("yaw", out var y) && y.TryGetDouble(out var yaw) ? yaw : 0));
            // The CS map spec replaces the raw zones and spawns for CS.
            if (root.TryGetProperty("cs", out var cs) && cs.ValueKind == JsonValueKind.Object && cs.TryGetProperty("format", out var cf) && cf.GetString() == "aimmod.cs-map"
                && cs.TryGetProperty("version", out var cv) && cv.TryGetInt32(out var version) && version == 1)
            {
                var csZones = new List<ObjectiveZone>(); var csSpawns = new List<ObjectiveSpawn>();
                ObjectiveZone? Box(JsonElement b, string type, string team, string name) =>
                    b.ValueKind == JsonValueKind.Object && b.TryGetProperty("min", out var lo2) && b.TryGetProperty("max", out var hi2) && V(lo2) is { } a && V(hi2) is { } c
                        ? new ObjectiveZone(type, team, name, a.Select(v => v * scale).ToArray(), c.Select(v => v * scale).ToArray()) : null;
                if (cs.TryGetProperty("bomb_sites", out var sites) && sites.ValueKind == JsonValueKind.Array)
                    foreach (var site in sites.EnumerateArray().Take(8))
                        if (Box(site, "bomb_site", "any", site.TryGetProperty("name", out var sn) ? sn.GetString() ?? "" : "") is { } z) csZones.Add(z);
                foreach (var (side, team) in new[] { ("T", "terrorist"), ("CT", "counter_terrorist") })
                {
                    if (cs.TryGetProperty("buy_zones", out var buys) && buys.ValueKind == JsonValueKind.Object && buys.TryGetProperty(side, out var list) && list.ValueKind == JsonValueKind.Array)
                        foreach (var box in list.EnumerateArray().Take(16))
                            if (Box(box, "buy_zone", team, "") is { } z) csZones.Add(z);
                    if (cs.TryGetProperty("spawns", out var sp) && sp.ValueKind == JsonValueKind.Object && sp.TryGetProperty(side, out var points) && points.ValueKind == JsonValueKind.Array)
                        foreach (var point in points.EnumerateArray().Take(64))
                            if (point.ValueKind == JsonValueKind.Array && point.GetArrayLength() == 4 && point.EnumerateArray().All(x => x.ValueKind == JsonValueKind.Number))
                            {
                                var v = point.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                                csSpawns.Add(new ObjectiveSpawn(team, v[0] * scale, v[1] * scale, v[2] * scale, v[3]));
                            }
                }
                if (cs.TryGetProperty("callouts", out var calls) && calls.ValueKind == JsonValueKind.Array)
                    foreach (var call in calls.EnumerateArray().Take(64))
                        if (call.TryGetProperty("name", out var cn) && cn.GetString() is { Length: > 0 and <= 32 } callName && Box(call, "callout", "any", callName) is { } z) csZones.Add(z);
                return new MapObjectives(csZones, csSpawns, CsProblemOf(csZones, csSpawns));
            }
            // Unnamed bomb sites are A, B, ... in file order.
            var letter = 'A';
            zones = zones.Select(z => z.Type == "bomb_site" && z.Name.Length == 0 ? z with { Name = (letter++).ToString() } : z).ToList();
            return new MapObjectives(zones, spawns);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }
}

// What clients mirror and the HUD shows.
// InBuyZone: inside one of the side's buy zones now (null when the map has none, so buying works anywhere).
sealed record CsPlayerView(string Member, int Team, string Side, int Money, bool Alive, double Health, double Armor, bool Helmet, bool Kit, string? Primary, string? Secondary, int Kills, int Deaths,
    bool? InBuyZone = null, string? Site = null, string? Callout = null);
// A bomb site's centre (world units), for the HUD's site markers.
sealed record CsSiteView(string Name, double X, double Y, double Z);
sealed record CsBombView(string State, string? Carrier, string? Site, double[]? Position, long? ExplodesAt, string? Planter, long? PlantDoneAt, string? Defuser, long? DefuseDoneAt);
sealed record CsEvent(long Id, string Kind, long T, string? Member, string? Text, int Amount = 0);
sealed record CsView(int Round, string Phase, long PhaseEndsAt, long? LiveAt, int[] Score, string Team1Side, int HalfRounds, bool Overtime, IReadOnlyList<CsPlayerView> Players,
    CsBombView Bomb, int? LastWinner, string? LastReason, int? WinnerTeam, IReadOnlyList<CsEvent> Events, IReadOnlyDictionary<string, double[]>? Spawns,
    IReadOnlyList<CsSiteView>? Sites = null);

sealed class CsMatch
{
    sealed class P
    {
        public required string Id; public int Team; public int Money = CsRules.StartMoney;
        public CsWeapon? Primary, Secondary; public double Armor; public bool Helmet, Kit; public int Kills, Deaths;
    }
    readonly Dictionary<string, P> players = new();
    readonly MapObjectives? map;
    readonly List<CsEvent> events = [];
    long eventId;
    readonly int[] score = [0, 0];
    readonly int[] lossStreak = [0, 0];
    bool swapped; int roundsPlayed;
    public CombatMatch Combat { get; }
    public int HalfRounds { get; }
    public bool Overtime { get; }
    public int Round { get; private set; }
    public string Phase { get; private set; } = "freeze";
    public long PhaseEndsAt { get; private set; }
    public long? LiveAt { get; private set; }
    public int? WinnerTeam { get; private set; }
    public bool Over => Phase == "over";
    int? lastWinner; string? lastReason;
    // Bomb.
    string bombState = "carried"; string? carrier, site, planter, defuser; double[]? bombAt; long? explodesAt, plantDoneAt, defuseDoneAt; double[]? plantFrom;
    // Who dropped the bomb on purpose, and until when they can't pick it straight back up.
    string? dropper; long dropperBlockedUntil;
    public const long DropBlockMs = 1500;
    public const double DropAheadCm = 70;
    bool plantedThisRound;
    readonly Dictionary<string, double[]> roundSpawns = new();

    // teams: the lobby's team picks (1 = starts as T, 2 = starts as CT); anyone missing alternates.
    public CsMatch(IReadOnlyList<string> ids, long start, int halfRounds, bool overtime, MapObjectives? objectives, IReadOnlyDictionary<string, int>? teams = null)
    {
        HalfRounds = Math.Clamp(halfRounds, 6, 15); Overtime = overtime; map = objectives;
        teams = teams is not null && ids.All(id => teams.TryGetValue(id, out var t) && t is 1 or 2) ? teams : CombatRules.Teams(ids);
        foreach (var id in ids) players[id] = new P { Id = id, Team = teams[id] };
        // Hit validation over the whole match; teams are the friendly-fire groups.
        Combat = new CombatMatch(LobbyModes.Cs, ids, int.MaxValue, 0, start, start + 6 * 3_600_000L, teams)
        {
            Respawns = false,
            WeaponFor = (id, slot) => players.TryGetValue(id, out var p) ? (slot == 0 ? p.Primary : slot == 1 ? p.Secondary : null)?.Combat : null,
            DamageModel = Damage,
            OnKill = Killed,
        };
        Round = 0;
        StartRound(start);
    }

    public string SideOf(int team) => (team == 1) != swapped ? CsRules.T : CsRules.CT;
    public string SideOf(string id) => players.TryGetValue(id, out var p) ? SideOf(p.Team) : "";
    IEnumerable<P> Side(string side) => players.Values.Where(p => SideOf(p.Team) == side);
    void Event(string kind, long t, string? member, string? text, int amount = 0) { events.Add(new CsEvent(++eventId, kind, t, member, text, amount)); if (events.Count > 64) events.RemoveAt(0); }
    void Pay(P p, int amount, long t, string why) { var before = p.Money; p.Money = Math.Clamp(p.Money + amount, 0, CsRules.MaxMoney); if (p.Money != before) Event("money", t, p.Id, why, p.Money - before); }

    double Damage(string victim, double raw, bool head, CombatWeapon weapon)
    {
        if (!players.TryGetValue(victim, out var v)) return raw;
        var cs = CsRules.ByProfile(weapon.Name);
        var (health, armor) = CsRules.Armor(raw, head, v.Armor, v.Helmet, cs?.ArmorPenetration ?? 1);
        v.Armor = Math.Max(0, v.Armor - armor);
        return health;
    }

    void Killed(string victim, string killer, CombatWeapon weapon, bool head)
    {
        var now = Combat.Position(victim)?.T ?? 0;
        if (players.TryGetValue(killer, out var k))
        {
            k.Kills++;
            var cs = CsRules.ByProfile(weapon.Name);
            if (players.TryGetValue(victim, out var v0) && v0.Team != k.Team) Pay(k, cs?.KillReward ?? 300, now, "kill");
        }
        if (players.TryGetValue(victim, out var v)) { v.Deaths++; v.Primary = null; v.Secondary = null; v.Armor = 0; v.Helmet = false; v.Kit = false; }
        // Text: killer, the weapon's id and whether it was a headshot (tab-separated).
        Event("kill", now, victim, killer + "\t" + (CsRules.ByProfile(weapon.Name)?.Id ?? "") + "\t" + (head ? "1" : "0"));
        if (carrier == victim) DropBomb(victim);
        if (planter == victim) { planter = null; plantDoneAt = null; }
        if (defuser == victim) { defuser = null; defuseDoneAt = null; }
    }

    void DropBomb(string from)
    {
        carrier = null; bombState = "dropped";
        if (Combat.Position(from) is { } at) bombAt = [at.X, at.Y, at.Z - 64];
    }

    void StartRound(long now)
    {
        Round++;
        Phase = "freeze"; PhaseEndsAt = now + CsRules.FreezeMs; LiveAt = null;
        roundSpawns.Clear();
        var index = new Dictionary<string, int>();
        foreach (var p in players.Values)
        {
            Combat.Revive(p.Id, now, CsRules.MaxHealth);
            Combat.SetTeam(p.Id, p.Team);
            var side = SideOf(p.Team);
            p.Secondary ??= CsRules.DefaultPistol(side);
            // Spawn: the side's spawns from the map metadata, one per player in turn.
            var spawns = map?.SpawnsFor(side) ?? [];
            if (spawns.Count > 0)
            {
                var i = index.GetValueOrDefault(side); index[side] = i + 1;
                var s = spawns[(i + Round) % spawns.Count];
                roundSpawns[p.Id] = [s.X, s.Y, s.Z, s.Yaw];
            }
        }
        // The bomb: the last carrier keeps it if they're still a Terrorist, else a random Terrorist.
        var ts = Side(CsRules.T).ToList();
        carrier = carrier is { } last && ts.Any(t => t.Id == last) ? last
            : ts.Count > 0 ? ts[new Random(unchecked((int)(Combat.Start ^ Round * 7919L))).Next(ts.Count)].Id : null;
        dropper = null; dropperBlockedUntil = 0;
        bombState = carrier is null ? "none" : "carried"; site = null; planter = null; defuser = null; bombAt = null; explodesAt = null; plantDoneAt = null; defuseDoneAt = null; plantedThisRound = false;
        Event("round", now, null, "Round " + Round);
    }

    // Buying: in freeze or buy time, alive, in your side's buy zone (when the map has
    // them), with the money, and the item allowed for your side.
    public string? Buy(string id, string item, long now)
    {
        if (!players.TryGetValue(id, out var p)) return "not-playing";
        if (!(Phase == "freeze" || (Phase == "live" && LiveAt is { } live && now < live + CsRules.BuyMs))) return "buy-time";
        if (!Combat.Alive(id)) return "dead";
        var side = SideOf(p.Team);
        var zones = map?.BuyZones(side) ?? [];
        if (zones.Count > 0 && (Combat.Position(id) is not { } at || !zones.Any(z => z.Contains(at.X, at.Y, at.Z, 50)))) return "buy-zone";
        int price; Action grant;
        if (CsRules.Find(item) is { } w)
        {
            if (w.Side != "any" && w.Side != side) return "side";
            if ((w.Slot == 0 ? p.Primary : p.Secondary)?.Id == w.Id) return "owned";
            price = w.Price;
            grant = () => { if (w.Slot == 0) p.Primary = w; else p.Secondary = w; };
        }
        else if (item == "kevlar")
        {
            if (p.Armor >= CsRules.MaxArmor) return "owned";
            price = CsRules.KevlarPrice; grant = () => p.Armor = CsRules.MaxArmor;
        }
        else if (item == "kevlar-helmet")
        {
            if (p.Helmet && p.Armor >= CsRules.MaxArmor) return "owned";
            price = p.Armor >= CsRules.MaxArmor ? CsRules.HelmetUpgradePrice : CsRules.KevlarHelmetPrice;
            grant = () => { p.Armor = CsRules.MaxArmor; p.Helmet = true; };
        }
        else if (item == "defuse-kit")
        {
            if (side != CsRules.CT) return "side";
            if (p.Kit) return "owned";
            price = CsRules.KitPrice; grant = () => p.Kit = true;
        }
        else return "unknown-item";
        if (p.Money < price) return "money";
        p.Money -= price; grant();
        Event("buy", now, id, item, -price);
        return null;
    }

    // The drop key (G): the carrier puts the bomb down in front of them, for a teammate to pick up.
    public string? Drop(string id, long now)
    {
        if (!players.TryGetValue(id, out _) || !Combat.Alive(id)) return "dead";
        if (carrier != id) return "no-bomb";
        if (Phase is not ("freeze" or "live")) return "not-now";
        if (Combat.Position(id) is not { } at) return "no-track";
        var yaw = at.Yaw * Math.PI / 180;
        carrier = null; bombState = "dropped";
        bombAt = [Math.Round(at.X + Math.Cos(yaw) * DropAheadCm, 1), Math.Round(at.Y + Math.Sin(yaw) * DropAheadCm, 1), Math.Round(at.Z - 64, 1)];
        dropper = id; dropperBlockedUntil = now + DropBlockMs;
        if (planter == id) { planter = null; plantDoneAt = null; }
        Event("bomb-dropped", now, id, null);
        return null;
    }

    // The use key (E): held to plant (carrier in a bomb site) or to defuse (CT at the bomb).
    public string? Use(string id, bool held, long now)
    {
        if (!players.TryGetValue(id, out var p) || !Combat.Alive(id)) return "dead";
        if (!held)
        {
            if (planter == id) { planter = null; plantDoneAt = null; }
            if (defuser == id) { defuser = null; defuseDoneAt = null; }
            return null;
        }
        var side = SideOf(p.Team);
        if (Combat.Position(id) is not { } at) return "no-track";
        if (side == CsRules.T && Phase == "live" && carrier != id && bombState is "carried" or "dropped") return "no-bomb";
        if (side == CsRules.T && Phase == "freeze") return "freeze";
        if (side == CsRules.T && Phase == "live" && carrier == id)
        {
            var inSite = map?.BombSites.FirstOrDefault(z => z.Contains(at.X, at.Y, at.Z));
            if (map is not null && inSite is null) return "not-in-site";
            if (Combat.Speed(id) > 30) return "moving";
            planter = id; plantDoneAt = now + CsRules.PlantMs; plantFrom = [at.X, at.Y]; site = inSite?.Name ?? "A";
            Event("planting", now, id, site);
            return null;
        }
        if (side == CsRules.CT && Phase == "planted" && bombAt is { } bomb)
        {
            if (Math.Sqrt((at.X - bomb[0]) * (at.X - bomb[0]) + (at.Y - bomb[1]) * (at.Y - bomb[1])) > CsRules.DefuseRadiusCm || Math.Abs(at.Z - 64 - bomb[2]) > 150) return "not-at-bomb";
            if (defuser is not null && defuser != id) return "busy";
            defuser = id; defuseDoneAt = now + (p.Kit ? CsRules.KitDefuseMs : CsRules.DefuseMs);
            Event("defusing", now, id, p.Kit ? "kit" : null);
            return null;
        }
        return "nothing-to-use";
    }

    // A dropped bomb is picked up by an alive Terrorist walking over it (not by the one who just
    // dropped it, for a moment); Counter-Terrorists walk over it.
    void PickUp(long now)
    {
        if (bombState != "dropped" || bombAt is not { } drop) return;
        foreach (var t in Side(CsRules.T))
        {
            if (t.Id == dropper && now < dropperBlockedUntil) continue;
            if (Combat.Alive(t.Id) && Combat.Position(t.Id) is { } at && Math.Sqrt((at.X - drop[0]) * (at.X - drop[0]) + (at.Y - drop[1]) * (at.Y - drop[1])) <= CsRules.BombPickupCm
                && Math.Abs(at.Z - 64 - drop[2]) <= 120)
            { carrier = t.Id; bombState = "carried"; bombAt = null; dropper = null; Event("bomb-picked", now, t.Id, null); return; }
        }
    }

    public void Leave(string id, long now) { Combat.Kill(id, now); if (carrier == id) DropBomb(id); if (planter == id) planter = null; if (defuser == id) defuser = null; }

    public void Tick(long now)
    {
        if (Over) return;
        if (Phase == "freeze" && now >= PhaseEndsAt) { Phase = "live"; LiveAt = now; PhaseEndsAt = now + CsRules.RoundMs; Event("live", now, null, null); }
        if (Phase == "freeze") PickUp(now);
        if (Phase is "live" or "planted")
        {
            // Plant and defuse progress: the holder must stay alive and in place.
            if (planter is { } pl && plantDoneAt is { } pd)
            {
                var at = Combat.Position(pl);
                if (!Combat.Alive(pl) || at is null || plantFrom is not { } from || Math.Sqrt((at.X - from[0]) * (at.X - from[0]) + (at.Y - from[1]) * (at.Y - from[1])) > CsRules.PlantMoveCm) { planter = null; plantDoneAt = null; }
                else if (now >= pd)
                {
                    Phase = "planted"; bombState = "planted"; carrier = null; bombAt = [at.X, at.Y, at.Z - 64]; explodesAt = now + CsRules.BombMs; PhaseEndsAt = explodesAt.Value; plantedThisRound = true;
                    if (players.TryGetValue(pl, out var pp)) Pay(pp, CsRules.PlantReward, now, "plant");
                    Event("planted", now, pl, site);
                    planter = null; plantDoneAt = null;
                }
            }
            if (defuser is { } df && defuseDoneAt is { } dd)
            {
                var at = Combat.Position(df);
                if (!Combat.Alive(df) || at is null || bombAt is not { } b || Math.Sqrt((at.X - b[0]) * (at.X - b[0]) + (at.Y - b[1]) * (at.Y - b[1])) > CsRules.DefuseRadiusCm) { defuser = null; defuseDoneAt = null; }
                else if (now >= dd)
                {
                    if (players.TryGetValue(df, out var dp)) Pay(dp, CsRules.DefuseReward, now, "defuse");
                    bombState = "defused"; Event("defused", now, df, site);
                    EndRound(now, TeamOf(CsRules.CT), "defuse");
                    return;
                }
            }
            PickUp(now);
            var tAlive = Side(CsRules.T).Count(p => Combat.Alive(p.Id)); var ctAlive = Side(CsRules.CT).Count(p => Combat.Alive(p.Id));
            if (Phase == "planted")
            {
                if (now >= explodesAt) { bombState = "exploded"; Event("exploded", now, null, site); EndRound(now, TeamOf(CsRules.T), "bomb"); }
                else if (ctAlive == 0) EndRound(now, TeamOf(CsRules.T), "elimination");
            }
            else
            {
                if (tAlive == 0 && ctAlive > 0) EndRound(now, TeamOf(CsRules.CT), "elimination");
                else if (ctAlive == 0 && tAlive > 0) EndRound(now, TeamOf(CsRules.T), "elimination");
                else if (tAlive == 0 && ctAlive == 0) EndRound(now, TeamOf(CsRules.CT), "elimination");
                else if (now >= PhaseEndsAt) EndRound(now, TeamOf(CsRules.CT), "time");
            }
        }
        else if (Phase == "end" && now >= PhaseEndsAt) NextRound(now);
    }

    int TeamOf(string side) => SideOf(1) == side ? 1 : 2;

    // Round rewards (CS2): the winners by reason, the losers their loss bonus (none for
    // Terrorists who survive a lost time-out; +800 each if they planted).
    void EndRound(long now, int winner, string reason)
    {
        Phase = "end"; PhaseEndsAt = now + CsRules.RoundEndMs; lastWinner = winner; lastReason = reason;
        score[winner - 1]++; roundsPlayed++;
        var loser = 3 - winner;
        var win = reason switch { "bomb" => CsRules.WinBomb, "defuse" => CsRules.WinDefuse, "time" => CsRules.WinTime, _ => CsRules.WinElimination };
        var lossAmount = CsRules.LossBonus(lossStreak[loser - 1]);
        foreach (var p in players.Values)
        {
            if (p.Team == winner) Pay(p, win, now, "round-win");
            else
            {
                var side = SideOf(p.Team);
                var survivedTimeout = side == CsRules.T && reason == "time" && Combat.Alive(p.Id);
                Pay(p, survivedTimeout ? 0 : lossAmount + (side == CsRules.T && plantedThisRound ? CsRules.PlantedLossBonus : 0), now, "round-loss");
            }
        }
        lossStreak[loser - 1] = Math.Min(CsRules.LossSteps, lossStreak[loser - 1] + 1);
        lossStreak[winner - 1] = Math.Max(0, lossStreak[winner - 1] - 1);
        Event("round-end", now, null, reason, winner);
        planter = null; defuser = null; plantDoneAt = null; defuseDoneAt = null;
    }

    // Halves: sides switch after HalfRounds (money and loss bonus reset, weapons gone). First to
    // HalfRounds + 1 wins; a tie at HalfRounds each goes to overtime (MR3 halves, $12,500), or a draw.
    void NextRound(long now)
    {
        var target = HalfRounds + 1;
        if (roundsPlayed > 2 * HalfRounds)
        {
            var ot = roundsPlayed - 2 * HalfRounds;           // rounds played in overtime
            var block = (ot - 1) / (2 * CsRules.OvertimeHalf);  // current overtime block
            target = HalfRounds + 1 + CsRules.OvertimeHalf * (block + 1);
        }
        else if (roundsPlayed == 2 * HalfRounds) target = HalfRounds + 1;
        if (score[0] >= target || score[1] >= target) { Finish(now, score[0] > score[1] ? 1 : 2); return; }
        if (roundsPlayed == 2 * HalfRounds && score[0] == score[1] && !Overtime) { Finish(now, null); return; }
        var switchNow = roundsPlayed == HalfRounds
            || (roundsPlayed >= 2 * HalfRounds && (roundsPlayed - 2 * HalfRounds) % CsRules.OvertimeHalf == 0);
        if (switchNow)
        {
            swapped = !swapped;
            var money = roundsPlayed >= 2 * HalfRounds ? CsRules.OvertimeMoney : CsRules.StartMoney;
            foreach (var p in players.Values) { p.Money = money; p.Primary = null; p.Secondary = null; p.Armor = 0; p.Helmet = false; p.Kit = false; }
            lossStreak[0] = 0; lossStreak[1] = 0;
            Event(roundsPlayed == HalfRounds ? "halftime" : "overtime-half", now, null, null);
        }
        StartRound(now);
    }

    bool? InBuyZone(P p)
    {
        var zones = map?.BuyZones(SideOf(p.Team)) ?? [];
        if (zones.Count == 0) return null;
        return Combat.Position(p.Id) is { } at && zones.Any(z => z.Contains(at.X, at.Y, at.Z, 50));
    }

    // The bomb site (letter) and callout the player stands in now.
    string? SiteOf(P p) => map is not null && Combat.Position(p.Id) is { } at ? map.BombSites.FirstOrDefault(z => z.Contains(at.X, at.Y, at.Z))?.Name : null;
    string? CalloutOf(P p) => map is not null && Combat.Position(p.Id) is { } at ? map.Callouts.FirstOrDefault(z => z.Contains(at.X, at.Y, at.Z))?.Name : null;

    void Finish(long now, int? winner) { Phase = "over"; WinnerTeam = winner; PhaseEndsAt = now; Event("match-end", now, null, null, winner ?? 0); }

    public CsView View() => new(Round, Phase, PhaseEndsAt, LiveAt, [score[0], score[1]], SideOf(1), HalfRounds, Overtime,
        players.Values.Select(p => new CsPlayerView(p.Id, p.Team, SideOf(p.Team), p.Money, Combat.Alive(p.Id), Math.Round(Combat.Health(p.Id), 1), Math.Round(p.Armor, 1), p.Helmet, p.Kit,
            p.Primary?.Id, p.Secondary?.Id, p.Kills, p.Deaths, InBuyZone(p), SiteOf(p), CalloutOf(p))).ToArray(),
        new CsBombView(bombState, carrier, site, bombAt, explodesAt, planter, plantDoneAt, defuser, defuseDoneAt), lastWinner, lastReason, WinnerTeam, events.TakeLast(16).ToArray(),
        roundSpawns.Count > 0 ? new Dictionary<string, double[]>(roundSpawns) : null,
        map?.BombSites.Select(z => new CsSiteView(z.Name, Math.Round((z.Min[0] + z.Max[0]) / 2), Math.Round((z.Min[1] + z.Max[1]) / 2), Math.Round((z.Min[2] + z.Max[2]) / 2))).ToArray());
}

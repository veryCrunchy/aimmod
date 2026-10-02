using System.Globalization;

namespace AimMod.InGame.Multiplayer;

// Where CS bots stand and what they watch when they hold ground: a site before the plant (the
// defenders), the planted bomb (the attackers), the retake (the defenders again). No hand-placed
// spots: AimModSteam works each area out on the bots' nav grid (SiteSpots.hpp) and writes it back
// (bot-spots.tsv): the ways the other side comes in (entrances, from where it comes from), grid
// points that see the bomb or an entrance (line traces from a standing eye), how walled-in each
// is, and a lurker's spot by each way. This file reads that, gives each bot a role and a spot of
// its own (BotHolds), moves its head like a player holding an angle (BotScan), plans the retake
// (BotRetake) and notices a bot left staring at a wall (BotIdle). Without an area yet (an older
// AimModSteam, the grid still growing) it falls back to spots spread round the centre.

// What the brain asks AimModSteam to work out ("area"/"from" rows in bot-orders.tsv).
sealed record AreaRequest(string Key, double[] Centre, double Rmin, double Rmax, double Entry, IReadOnlyList<double[]> Sources);
// What came back: the ways in (the point `entry` along each way, its outer point twice that out, a
// lurker's spot), and the spots (floor points) with what each sees.
sealed record SpotEntrance(int Index, double[] At, double[] Outer, double PathCm, int Source, double[]? Lurk);
sealed record HoldSpot(double[] At, bool SeesBomb, int Mask, int Cover, double WalkCm)
{
    public bool Sees(int entrance) => entrance >= 0 && (Mask >> entrance & 1) != 0;
    public int Exposure => System.Numerics.BitOperations.PopCount((uint)Mask);
}
sealed record SpotArea(string Key, string State, IReadOnlyList<SpotEntrance> Entrances, IReadOnlyList<HoldSpot> Spots)
{
    public bool Done => State == "done";
}

static class BotAreas
{
    public const string FileName = "bot-spots.tsv";

    // How far out an area reaches, from its site's box (a bigger site, longer angles): spots from
    // rmin to rmax from the centre, entrances `entry` along the ways in.
    public static (double Rmin, double Rmax, double Entry) Radii(ObjectiveZone? site)
    {
        var half = site is null ? 700 : Math.Sqrt(Math.Pow(site.Max[0] - site.Min[0], 2) + Math.Pow(site.Max[1] - site.Min[1], 2)) / 2;
        return (Math.Clamp(half * 0.2, 250, 700), Math.Clamp(half * 3.2, 2400, 5600), Math.Clamp(half * 1.8, 1200, 3600));
    }
    public static ObjectiveZone? SiteZone(MapObjectives? map, double x, double y) =>
        map?.BombSites.OrderBy(z => Math.Pow((z.Min[0] + z.Max[0]) / 2 - x, 2) + Math.Pow((z.Min[1] + z.Max[1]) / 2 - y, 2)).FirstOrDefault();
    // An area key the bridge accepts: letters, digits, '.', '_' and '-', at most 32.
    public static string Key(string text)
    {
        var s = new string(text.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-').ToArray());
        return s.Length == 0 ? "area" : s.Length > 32 ? s[..32] : s;
    }

    public static string Format(IEnumerable<AreaRequest> requests)
    {
        static string F(double v) => Math.Round(v).ToString("0", CultureInfo.InvariantCulture);
        var sb = new System.Text.StringBuilder();
        foreach (var r in requests.Take(4))
        {
            sb.Append("area\t").Append(r.Key).Append('\t').Append(F(r.Centre[0])).Append('\t').Append(F(r.Centre[1])).Append('\t').Append(F(r.Centre[2]))
                .Append('\t').Append(F(r.Rmin)).Append('\t').Append(F(r.Rmax)).Append('\t').Append(F(r.Entry)).Append('\n');
            foreach (var s in r.Sources.Take(4)) sb.Append("from\t").Append(r.Key).Append('\t').Append(F(s[0])).Append('\t').Append(F(s[1])).Append('\t').Append(F(s[2])).Append('\n');
        }
        return sb.ToString();
    }

    // bot-spots.tsv (SiteSpots.hpp FormatSpots); null when it isn't one.
    public static Dictionary<string, SpotArea>? Parse(string text)
    {
        var lines = text.Replace("\r", "").Split('\n');
        if (lines.Length == 0 || !lines[0].StartsWith("AIMMOD_SPOTS_1\t", StringComparison.Ordinal)) return null;
        static double? N(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) && Math.Abs(v) < 1e7 ? v : null;
        static double[]? P(string[] p, int i) => p.Length >= i + 3 && N(p[i]) is { } x && N(p[i + 1]) is { } y && N(p[i + 2]) is { } z ? [x, y, z] : null;
        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        var entrances = new Dictionary<string, List<SpotEntrance>>(StringComparer.Ordinal);
        var lurks = new Dictionary<(string, int), double[]>();
        var spots = new Dictionary<string, List<HoldSpot>>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1).Take(2048))
        {
            var p = line.Split('\t');
            if (p.Length < 2 || p[1].Length is 0 or > 32) continue;
            var key = p[1];
            switch (p[0])
            {
                case "area" when p.Length >= 3 && p[2] is "growing" or "done" or "nogrid":
                    states[key] = p[2];
                    break;
                case "entrance" when p.Length >= 11 && int.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < 8 && P(p, 3) is { } at && P(p, 6) is { } outer && N(p[9]) is { } len
                                     && int.TryParse(p[10], NumberStyles.None, CultureInfo.InvariantCulture, out var source):
                    (entrances.TryGetValue(key, out var el) ? el : entrances[key] = []).Add(new SpotEntrance(i, at, outer, len, source, null));
                    break;
                case "lurk" when p.Length >= 6 && int.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out var li) && P(p, 3) is { } lat:
                    lurks[(key, li)] = lat;
                    break;
                case "spot" when p.Length >= 9 && P(p, 2) is { } sat && p[5] is "0" or "1" && int.TryParse(p[6], NumberStyles.None, CultureInfo.InvariantCulture, out var mask)
                                 && int.TryParse(p[7], NumberStyles.None, CultureInfo.InvariantCulture, out var cover) && N(p[8]) is { } walk:
                    var sl = spots.TryGetValue(key, out var have) ? have : spots[key] = [];
                    if (sl.Count < 64) sl.Add(new HoldSpot(sat, p[5] == "1", mask, Math.Clamp(cover, 0, 4), walk));
                    break;
            }
        }
        return states.ToDictionary(kv => kv.Key, kv => new SpotArea(kv.Key, kv.Value,
            (entrances.GetValueOrDefault(kv.Key) ?? []).OrderBy(e => e.Index).Select(e => e with { Lurk = lurks.GetValueOrDefault((kv.Key, e.Index)) }).ToArray(),
            spots.GetValueOrDefault(kv.Key) ?? []), StringComparer.Ordinal);
    }

    internal static double Dist2(double[] a, double[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]));
}

// A bot's place in a hold: its role, its spot and a second one close by to shift to, the point it
// mostly watches (an entrance, the bomb) and the others it checks, and what its spot is like.
sealed record HoldSlot(string Member, string Role, double[] Spot, double[]? Alt, double[] Primary, IReadOnlyList<double[]> Watch, int Entrance, bool SeesBomb, int Cover, bool FromArea);
sealed record HoldBot(string Member, double[] At, string Skill);
// Centre: the bomb or the site; Bomb: a post-plant (someone watches the bomb itself); Passive: the
// clock is on the holders' side (safe, far angles); Threat: the entrance the enemy was last heard or
// seen at (it gets a crossfire); Lurk: one bot waits out by a way in; Gap: how far apart holders
// stand (one grenade must not get two); EyeAbove: a standing eye above the floor; Banned: spots a
// bot stood stuck at (the idle watchdog); Toward: the enemy's main way, without an area.
sealed record HoldContext(double[] Centre, bool Bomb, bool Passive, int? Threat, bool Lurk, double Gap, double EyeAbove, double Rmax, double Entry,
    IReadOnlyCollection<double[]> Banned, double[]? Toward, string Label);

static class BotHolds
{
    public const double BannedCm = 160, ShiftMinCm = 150, ShiftMaxCm = 650;
    // How far apart two holders keep: out of one molotov's spread and most of an HE's blast.
    public static readonly double GrenadeGapCm = Math.Max(GrenadeRules.FireRadiusCm * 2, GrenadeRules.HeRadiusCm * 0.6);

    // Roles for n holders, most important first (kind, entrance).
    public static List<(string Kind, int Entrance)> Roles(int n, IReadOnlyList<int> entrances, HoldContext c, bool lurkSpot)
    {
        var roles = new List<(string, int)>();
        if (n <= 0) return roles;
        int E(int i) => entrances.Count == 0 ? -1 : entrances[Math.Min(i, entrances.Count - 1)];
        var main = c.Threat ?? E(0);
        if (c.Bomb)
        {
            if (n == 1) { roles.Add((c.Passive ? "passive" : "bomb+entrance", main)); return roles; }
            roles.Add((c.Passive ? "passive" : "entrance", main));
            roles.Add(("bomb", -1));
            if (n >= 3) roles.Add(c.Passive ? ("passive", -1) : entrances.Count > 1 && c.Threat is null ? ("entrance", E(1)) : ("crossfire", main));
            if (n >= 4) roles.Add(c.Lurk && lurkSpot && !c.Passive ? ("lurk", E(0)) : c.Passive ? ("passive", -1) : ("crossfire", entrances.Count > 1 && c.Threat is null ? E(1) : main));
            if (n >= 5) roles.Add(c.Passive ? ("passive", -1) : entrances.Count > 2 ? ("entrance", E(2)) : ("crossfire", entrances.Count > 1 ? E(1) : main));
            while (roles.Count < n) roles.Add(("bomb", -1));
            return roles;
        }
        // A site before the plant: the ways in, a crossfire on the main one, then the rest.
        roles.Add(("entrance", main));
        if (n >= 2) roles.Add(entrances.Count > 1 && c.Threat is null ? ("entrance", E(1)) : ("crossfire", main));
        if (n >= 3) roles.Add(entrances.Count > 2 && c.Threat is null ? ("entrance", E(2)) : ("crossfire", entrances.Count > 1 ? E(1) : main));
        while (roles.Count < n) roles.Add(("crossfire", E(roles.Count % Math.Max(1, entrances.Count))));
        return roles;
    }

    static string RoleName(string kind, int e) => (kind, e < 0) switch
    {
        ("entrance", false) => "entrance " + e,
        ("crossfire", false) => "crossfire " + e,
        ("bomb+entrance", false) => "bomb and entrance " + e,
        ("entrance", true) => "angle",
        ("crossfire", true) => "second angle",
        ("bomb+entrance", true) => "bomb",
        _ => kind,
    };

    // Each bot's slot. Deterministic for the same inputs (every re-plan with nothing new agrees).
    public static IReadOnlyList<HoldSlot> Plan(SpotArea? area, IReadOnlyList<HoldBot> bots, HoldContext c)
    {
        if (bots.Count == 0) return [];
        var fromArea = area is { Done: true, Spots.Count: > 0 };
        var entrances = fromArea ? area!.Entrances.OrderBy(e => e.Source).ThenBy(e => e.PathCm).Select(e => e.Index).ToList() : [];
        var threat = c.Threat is { } t && entrances.Contains(t) ? c.Threat : null;
        c = c with { Threat = threat };
        var lurkSpot = fromArea && area!.Entrances.FirstOrDefault(e => entrances.Count > 0 && e.Index == entrances[0])?.Lurk is { } l && !Banned(l, c);
        var roles = Roles(bots.Count, entrances, c, lurkSpot);
        var skill = bots.Any(b => b.Skill == BotSkills.Hard) ? BotSkills.Hard : bots.All(b => b.Skill == BotSkills.Easy) ? BotSkills.Easy : BotSkills.Normal;
        var chosen = new List<(string Kind, int E, double[] At, HoldSpot? Spot)>();
        if (fromArea)
            foreach (var (kind, e) in roles)
            {
                if (kind == "lurk")
                {
                    var lurk = area!.Entrances.First(x => x.Index == e).Lurk!;
                    chosen.Add((kind, e, lurk, null));
                    continue;
                }
                HoldSpot? best = null;
                for (var gap = c.Gap; best is null && gap >= 200; gap *= 0.75)
                {
                    var bestScore = double.NegativeInfinity;
                    foreach (var s in area!.Spots)
                    {
                        if (Banned(s.At, c) || chosen.Any(x => BotAreas.Dist2(x.At, s.At) < gap)) continue;
                        var score = Score(s, kind, e, area, c, chosen, skill);
                        if (score > bestScore) { bestScore = score; best = s; }
                    }
                    if (double.IsNegativeInfinity(bestScore)) best = null;
                }
                // Nothing fits this role: any spot that sees the bomb or a way in, apart from the others.
                best ??= area!.Spots.Where(s => !Banned(s.At, c) && !chosen.Any(x => BotAreas.Dist2(x.At, s.At) < 200)).OrderByDescending(s => s.Cover + (s.SeesBomb ? 2 : 0)).FirstOrDefault();
                if (best is not null) chosen.Add((kind, e, best.At, best));
            }
        // No area (or too few spots): spread round the centre, the main way in front.
        var toward = c.Toward is { } tw && BotAreas.Dist2(tw, c.Centre) > 1 ? tw : [c.Centre[0] + 1, c.Centre[1], c.Centre[2]];
        var dir = Math.Atan2(toward[1] - c.Centre[1], toward[0] - c.Centre[0]);
        double[] offsets = [0, -55, 55, -110, 110, 180, -160, 160];
        for (var i = chosen.Count; i < bots.Count; i++)
        {
            var (kind, e) = i < roles.Count ? roles[i] : ("bomb", -1);
            var a = dir + offsets[i % offsets.Length] * Math.PI / 180;
            // Off the bomb (a post-plant); on a site, the first on it and the others a few steps aside.
            var r = c.Bomb ? Math.Max(600, Math.Min(c.Rmax * 0.4, 1500)) : 250.0 * i;
            chosen.Add((kind == "lurk" ? "bomb" : kind, e, [Math.Round(c.Centre[0] + Math.Cos(a) * r), Math.Round(c.Centre[1] + Math.Sin(a) * r), c.Centre[2]], null));
        }
        // Bots onto spots: the least walking in all (any order for up to six, else nearest first).
        var order = Match(bots, chosen.Select(x => x.At).ToList());
        var slots = new List<HoldSlot>();
        var centreLook = new[] { c.Centre[0], c.Centre[1], c.Centre[2] };
        var mainWay = new[] { c.Centre[0] + Math.Cos(dir) * c.Entry, c.Centre[1] + Math.Sin(dir) * c.Entry, c.Centre[2] + c.EyeAbove * 0.5 };
        for (var i = 0; i < bots.Count; i++)
        {
            var pick = chosen[order[i]];
            double[] EntranceLook(int e) => area!.Entrances.First(x => x.Index == e) is var en ? [en.At[0], en.At[1], en.At[2] + c.EyeAbove * 0.9] : centreLook;
            double[] primary;
            var watch = new List<double[]>();
            if (pick.Kind == "lurk" && fromArea)
            {
                var en = area!.Entrances.First(x => x.Index == pick.E);
                primary = [en.Outer[0], en.Outer[1], en.Outer[2] + c.EyeAbove * 0.9];
                watch.Add(primary);
                watch.Add(EntranceLook(pick.E));
            }
            else if (pick.Spot is { } s)
            {
                // Mostly: its way in, or the bomb; then the other ways in it sees, and the bomb.
                var seen = area!.Entrances.Select(x => x.Index).Where(s.Sees).ToList();
                var watched = pick.Kind is "bomb" or "passive" ? -1 : s.Sees(pick.E) ? pick.E : s.SeesBomb && c.Bomb ? -1 : seen.Count > 0 ? seen[0] : -1;
                primary = watched >= 0 ? EntranceLook(watched) : centreLook;
                watch.Add(primary);
                foreach (var e in seen.Where(e => e != watched)) watch.Add(EntranceLook(e));
                if (watched >= 0 && (s.SeesBomb || !c.Bomb)) watch.Add(centreLook);
            }
            else
            {
                primary = c.Bomb && pick.Kind is "bomb" or "passive" ? centreLook : mainWay;
                watch.Add(primary);
                if (c.Bomb) watch.Add(primary == centreLook ? mainWay : centreLook);
                else
                    // A site: the main way and the angles either side of it, from where it stands.
                    foreach (var side in new[] { -50.0, 50.0 })
                    {
                        var a = dir + side * Math.PI / 180;
                        watch.Add([Math.Round(pick.At[0] + Math.Cos(a) * c.Entry), Math.Round(pick.At[1] + Math.Sin(a) * c.Entry), c.Centre[2] + c.EyeAbove * 0.5]);
                    }
            }
            // A second spot close by to shift to now and then: it must see what this one watches most.
            double[]? alt = null;
            if (pick.Spot is { } sp && fromArea)
                alt = area!.Spots.Where(o => o != sp && !Banned(o.At, c) && BotAreas.Dist2(o.At, sp.At) is >= ShiftMinCm and <= ShiftMaxCm
                        && (pick.Kind is "bomb" or "passive" ? o.SeesBomb : pick.E < 0 || o.Sees(pick.E)) && chosen.All(x => x.At == sp.At || BotAreas.Dist2(x.At, o.At) >= 300))
                    .OrderByDescending(o => o.Cover).ThenBy(o => BotAreas.Dist2(o.At, sp.At)).Select(o => o.At).FirstOrDefault();
            slots.Add(new HoldSlot(bots[i].Member, RoleName(pick.Kind, pick.E), pick.At, alt, primary, watch, pick.E, pick.Spot?.SeesBomb ?? false, pick.Spot?.Cover ?? 0, pick.Spot is not null || pick.Kind == "lurk" && fromArea));
        }
        return slots;
    }

    static bool Banned(double[] at, HoldContext c) => c.Banned.Any(b => BotAreas.Dist2(b, at) < BannedCm);

    // How well a spot suits a role (higher is better; -infinity: not at all).
    static double Score(HoldSpot s, string kind, int e, SpotArea area, HoldContext c, List<(string Kind, int E, double[] At, HoldSpot? Spot)> chosen, string skill)
    {
        var dc = BotAreas.Dist2(s.At, c.Centre);
        var noise = skill == BotSkills.Easy ? (BotStrategy.Hash(s.At[0].ToString(CultureInfo.InvariantCulture) + "," + s.At[1].ToString(CultureInfo.InvariantCulture)) % 1000 / 1000.0) * 2.5 : 0;
        double NearestEntrance() => area.Entrances.Count == 0 ? c.Entry : area.Entrances.Min(x => BotAreas.Dist2(x.At, s.At));
        switch (kind)
        {
            case "entrance":
            case "crossfire":
            case "bomb+entrance":
            {
                if (!s.Sees(e)) return kind == "bomb+entrance" && s.SeesBomb ? 0.5 + s.Cover * 0.5 + noise : double.NegativeInfinity;
                var en = area.Entrances.First(x => x.Index == e);
                var de = BotAreas.Dist2(s.At, en.At);
                var score = 3 - Math.Abs(de - c.Entry * 0.8) / c.Entry * 2 + s.Cover * 0.6 - Math.Max(0, s.Exposure - 2) * 0.4 + noise;
                if (c.Bomb && s.SeesBomb) score += kind == "bomb+entrance" ? 3 : 1.5;
                if (de < c.Entry * 0.25) score -= 3; // right on top of the way in: swung on first
                if (kind == "crossfire")
                {
                    // A second angle on the same way in, from well off the first one's line.
                    var partner = chosen.FirstOrDefault(x => x.E == e && x.Kind is "entrance" or "bomb+entrance");
                    if (partner.At is not null)
                    {
                        var a1 = Math.Atan2(partner.At[1] - en.At[1], partner.At[0] - en.At[0]);
                        var a2 = Math.Atan2(s.At[1] - en.At[1], s.At[0] - en.At[0]);
                        var angle = Math.Abs(BotAim.Wrap((a1 - a2) * 180 / Math.PI));
                        var need = skill == BotSkills.Hard ? 35 : skill == BotSkills.Normal ? 22 : 0;
                        if (angle < need) return double.NegativeInfinity;
                        score += Math.Min(angle, 90) / 45;
                    }
                }
                return score;
            }
            case "bomb":
            {
                if (!s.SeesBomb && c.Bomb) return double.NegativeInfinity;
                var want = c.Rmax * 0.45;
                return 3 + s.Cover * 0.8 - s.Exposure * 0.3 - Math.Abs(dc - want) / Math.Max(1, c.Rmax) * 2 + noise;
            }
            case "passive":
            {
                if (!s.SeesBomb && c.Bomb) return double.NegativeInfinity;
                // Safe: walled in, well back from the ways in, not seen from many of them.
                return s.Cover * 1.0 + Math.Min(NearestEntrance() / c.Entry, 2) * 1.5 - s.Exposure * 0.5 + noise;
            }
        }
        return double.NegativeInfinity;
    }

    // Bots onto spots with the least walking in all (exhaustive for up to six, else nearest first).
    static int[] Match(IReadOnlyList<HoldBot> bots, IReadOnlyList<double[]> spots)
    {
        var n = bots.Count;
        var best = Enumerable.Range(0, n).ToArray();
        if (n <= 6)
        {
            var bestCost = double.MaxValue;
            foreach (var perm in Permutations(Enumerable.Range(0, n).ToArray(), 0))
            {
                var cost = 0.0;
                for (var i = 0; i < n; i++) cost += BotAreas.Dist2(bots[i].At, spots[perm[i]]);
                if (cost < bestCost - 1e-6) { bestCost = cost; best = (int[])perm.Clone(); }
            }
            return best;
        }
        var free = Enumerable.Range(0, spots.Count).ToList();
        for (var i = 0; i < n; i++)
        {
            var j = free.OrderBy(k => BotAreas.Dist2(bots[i].At, spots[k])).First();
            best[i] = j; free.Remove(j);
        }
        return best;
    }
    static IEnumerable<int[]> Permutations(int[] a, int k)
    {
        if (k >= a.Length) { yield return a; yield break; }
        for (var i = k; i < a.Length; i++)
        {
            (a[k], a[i]) = (a[i], a[k]);
            foreach (var p in Permutations(a, k + 1)) yield return p;
            (a[k], a[i]) = (a[i], a[k]);
        }
    }

    public static string Describe(HoldSlot s) => FormattableString.Invariant($"{s.Role} at ({s.Spot[0]:0}, {s.Spot[1]:0}){(s.SeesBomb ? ", sees the bomb" : "")}{(s.FromArea ? $", cover {s.Cover}" : ", no area yet")}");
}

// Holding an angle like a player: mostly on the angle it holds, now and then a quick check of the
// others it sees, the crosshair never dead still (a degree or few of drift), an occasional jiggle
// peek and a small shift to a spot close by. The walker eases every turn, so it is all smooth.
sealed class ScanState
{
    public string? Key; public int Index; public long Until, DriftUntil, NextJiggle, JiggleUntil, NextShift; public double Drift; public bool Shifted;
}
static class BotScan
{
    public static (long Lo, long Hi) Dwell(string skill, bool primary) => (skill, primary) switch
    {
        (BotSkills.Easy, true) => (3000, 6000), (BotSkills.Easy, false) => (1500, 3000),
        (BotSkills.Hard, true) => (2200, 4200), (BotSkills.Hard, false) => (700, 1300),
        (_, true) => (2000, 4000), (_, false) => (900, 1700),
    };
    public static double DriftDegrees(string skill) => skill switch { BotSkills.Easy => 5, BotSkills.Hard => 2.5, _ => 3.5 };

    // The point to look at now.
    public static double[] Next(ScanState s, IReadOnlyList<double[]> watch, double[] eye, string skill, long now, Random rng, string key)
    {
        if (watch.Count == 0) return [eye[0] + 600, eye[1], eye[2]];
        long Pick((long Lo, long Hi) r) => r.Lo + (long)(rng.NextDouble() * (r.Hi - r.Lo));
        if (s.Key != key) { s.Key = key; s.Index = 0; s.Until = now + Pick(Dwell(skill, true)); s.NextJiggle = now + 4000 + rng.Next(4000); s.NextShift = now + 12_000 + rng.Next(8000); s.Shifted = false; }
        if (now >= s.Until)
        {
            var share = skill == BotSkills.Hard ? 0.6 : skill == BotSkills.Easy ? 0.45 : 0.5;
            var next = watch.Count == 1 || s.Index != 0 || rng.NextDouble() < share ? 0 : 1 + rng.Next(watch.Count - 1);
            s.Index = next;
            s.Until = now + Pick(Dwell(skill, next == 0));
        }
        if (now >= s.DriftUntil) { s.Drift = (rng.NextDouble() * 2 - 1) * DriftDegrees(skill); s.DriftUntil = now + 700 + rng.Next(800); }
        var p = watch[Math.Min(s.Index, watch.Count - 1)];
        // Drift: the point turned a little about the eye.
        var dx = p[0] - eye[0]; var dy = p[1] - eye[1]; var a = s.Drift * Math.PI / 180;
        return [Math.Round(eye[0] + dx * Math.Cos(a) - dy * Math.Sin(a), 1), Math.Round(eye[1] + dx * Math.Sin(a) + dy * Math.Cos(a), 1), Math.Round(p[2], 1)];
    }
    // An occasional jiggle peek of the angle it holds (Normal and Hard, not when playing safe).
    public static bool Jiggle(ScanState s, string skill, long now, Random rng, bool passive)
    {
        if (passive || skill == BotSkills.Easy) return false;
        if (now >= s.NextJiggle)
        {
            s.JiggleUntil = now + 1200 + rng.Next(800);
            s.NextJiggle = now + (skill == BotSkills.Hard ? 6000 + rng.Next(5000) : 10_000 + rng.Next(8000));
        }
        return now < s.JiggleUntil;
    }
    // Whether it stands at its second spot now: a small shift every so often (not Easy, not when safe).
    public static bool Shift(ScanState s, string skill, long now, Random rng, bool passive)
    {
        if (passive || skill == BotSkills.Easy) return s.Shifted = false;
        if (now >= s.NextShift) { s.Shifted = !s.Shifted; s.NextShift = now + (skill == BotSkills.Hard ? 12_000 + rng.Next(8000) : 16_000 + rng.Next(12_000)); }
        return s.Shifted;
    }
}

// The retake (defenders after the plant): gather out of sight by one way in, go in together (a
// flash over the site first), the next one close behind to trade, one defuses while the others
// cover where Terrorists would hold; no retake when the clock can't allow a defuse.
static class BotRetake
{
    public const double GatherCm = 900, SpeedCm = 700;
    public static long TradeGapMs(string skill) => skill switch { BotSkills.Easy => 900, BotSkills.Hard => 450, _ => 650 };
    // Whether a defuse can still be made from `distance` away: the walk, the defuse, a second spare.
    public static bool InTime(long now, long explodesAt, double distance, bool kit) =>
        explodesAt - now > (kit ? CsRules.KitDefuseMs : CsRules.DefuseMs) + distance / SpeedCm * 1000 + 1000;
    // The way in to gather at: the one the defenders are nearest to on the whole (out of sight at its
    // outer point), avoiding one Terrorists were just seen at when there is another.
    public static SpotEntrance? Entrance(SpotArea? area, IReadOnlyList<double[]> cts, IReadOnlyList<double[]> enemies, double entry)
    {
        if (area is not { Done: true, Entrances.Count: > 0 } || cts.Count == 0) return null;
        var ways = area.Entrances.ToList();
        var clean = ways.Where(e => !enemies.Any(x => BotAreas.Dist2(x, e.At) < entry * 0.7)).ToList();
        return (clean.Count > 0 ? clean : ways).OrderBy(e => cts.Average(c => BotAreas.Dist2(c, e.Outer))).First();
    }
    // Where Terrorists would hold: the spots that see the bomb, walled in, away from the way in.
    public static IReadOnlyList<double[]> Likely(SpotArea? area, SpotEntrance? way, double eyeAbove, int count = 3) =>
        area?.Spots.Where(s => s.SeesBomb).OrderByDescending(s => s.Cover + (way is null ? 0 : Math.Min(BotAreas.Dist2(s.At, way.At) / 1000, 2)))
            .Take(count).Select(s => new[] { s.At[0], s.At[1], s.At[2] + eyeAbove * 0.9 }).ToArray() ?? [];
    // A spot to cover the defuse from: one that sees the bomb, near the way in (the defenders' side).
    public static double[]? CoverSpot(SpotArea? area, SpotEntrance? way, double[] bomb, IReadOnlyCollection<double[]> taken) =>
        area?.Spots.Where(s => s.SeesBomb && taken.All(t => BotAreas.Dist2(t, s.At) > 400))
            .OrderBy(s => (way is null ? 0 : BotAreas.Dist2(s.At, way.At)) + BotAreas.Dist2(s.At, bomb) * 0.3).Select(s => s.At).FirstOrDefault();
}

// The idle watchdog: a bot that has stood still, looking the same way, at a wall right in its face
// (its game's trace straight ahead stops within WallCm) for IdleMs re-plans: the spot is banned for
// the round, it looks round the open ways for a moment, and its hold is planned again.
sealed class IdleWatch
{
    public double X = double.NaN, Y, Yaw; public long Since; public bool Fired; public int Count;
}
static class BotIdle
{
    public const double WallCm = 220, StillCm = 35, StillDegrees = 8;
    public const long IdleMs = 2500, LookAroundMs = 3000;
    // Updates the watch from the bot's report; true once, when it has been still at a wall too long.
    public static bool Check(IdleWatch w, BotSight s, long now)
    {
        if (double.IsNaN(w.X) || Math.Sqrt((s.X - w.X) * (s.X - w.X) + (s.Y - w.Y) * (s.Y - w.Y)) > StillCm || Math.Abs(BotAim.Wrap(s.Yaw - w.Yaw)) > StillDegrees || s.Look is not { } look || look >= WallCm)
        {
            w.X = s.X; w.Y = s.Y; w.Yaw = s.Yaw; w.Since = now; w.Fired = false;
            return false;
        }
        if (w.Fired || now - w.Since < IdleMs) return false;
        w.Fired = true; w.Count++;
        return true;
    }
    public static void Reset(IdleWatch w) { w.X = double.NaN; w.Fired = false; }
}

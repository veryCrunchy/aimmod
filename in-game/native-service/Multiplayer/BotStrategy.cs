namespace AimMod.InGame.Multiplayer;

// CS bot strategy (BotBrain): one plan per side per round, like a team calling it in freeze time,
// and what the side knows (who saw an enemy where, the bomb). Pure: the brain hands in the round,
// the map's sites and spawns and its bots, and reads each bot's job back.
//
// Terrorists pick a site and a style:
//  - rush: everyone straight to the site, the carrier in the middle of the pack;
//  - default: spread out to take map control part of the way to both sites, then at the call
//    (20 to 35 s in) group up and take the chosen site together;
//  - split: half go straight in, half by way of the other site's approach, meeting on the site.
// With four or more on a default one lurks: quietly out towards the other site, catching the
// rotation. A site the side lost two players at (their deaths called out) before the call is
// contested: they go to the other one. After the plant they spread over spots that watch the bomb
// and the ways back in (BotHolds, BotPositions.cs).
// Counter-Terrorists split over the sites by a read of the round (BotSplits): 2-1-2 (one forward,
// the rotator) or 3-2 / 3-1 heavy on the site the Terrorists hit in earlier rounds, never one alone
// where the read says two; the split is rebalanced as defenders die. A sighting, or a teammate's
// death called out, near a site pulls others over (rotation): everyone but an anchor for a full
// execute (three or more), the rotator and enough to make two there for less. With the bomb planted
// they retake (BotRetake).
static class BotStyles
{
    public const string Rush = "rush", Default = "default", Split = "split", Eco = "eco";
}

// A bot's job this round. Goal: where it heads (null: hold where it is); Stop: walk only that
// fraction of the way (map control, a default spot); Via: by way of a point first (a split);
// Face: what it watches from its spot.
sealed record BotJob(string Role, double[]? Goal, double Stop = 1, double[]? Via = null, double ViaStop = 1, double[]? Face = null);

// A raised site: which, how many enemies there as far as the side knows, and from what (seen or a death).
sealed record CtAlert(int Site, int Count, string From);

// A side's plan for the round.
sealed record TeamPlan(string Side, int Round, string Style, int Site, long ExecuteAt, IReadOnlyList<string> Order);

// What a side knows (TeamKnowledge) lives in BotMemory.cs.

static class BotStrategy
{
    public const long RushCallMs = 0, DefaultCallMinMs = 20_000, DefaultCallMaxMs = 35_000;
    public const double SiteAlertCm = 2600; // a sighting this close to a site pulls the defence there

    // The round's Terrorist plan: deterministic from the round and match (every machine agrees).
    public static TeamPlan PlanT(string matchKey, int round, IReadOnlyList<string> bots, int sites, long liveAt, BuyKind buy)
    {
        var rng = new Random(Hash(matchKey) ^ (round * 7919));
        var site = sites > 0 ? rng.Next(sites) : 0;
        var roll = rng.NextDouble();
        // Eco rounds rush (nothing to lose, surprise); full buys play slower more often.
        var style = buy == BuyKind.Eco ? BotStyles.Rush
            : roll < (buy == BuyKind.Full ? 0.25 : 0.4) ? BotStyles.Rush
            : bots.Count >= 4 && roll < 0.65 ? BotStyles.Split
            : BotStyles.Default;
        var call = style == BotStyles.Default ? liveAt + rng.Next((int)DefaultCallMinMs, (int)DefaultCallMaxMs) : liveAt + RushCallMs;
        return new TeamPlan(CsRules.T, round, style, site, call, bots);
    }

    public static TeamPlan PlanCT(string matchKey, int round, IReadOnlyList<string> bots, int sites, long liveAt) =>
        new(CsRules.CT, round, "hold", sites > 0 ? new Random(Hash(matchKey) ^ (round * 104729)).Next(sites) : 0, liveAt, bots);

    // The lurker on a default (four or more): the last of the order that isn't carrying the bomb.
    public static string? Lurker(TeamPlan plan, string? carrier) =>
        plan.Style == BotStyles.Default && plan.Order.Count >= 4 ? plan.Order.LastOrDefault(b => b != carrier) : null;

    // A Terrorist's job: where it goes and how far, from the plan and the round so far.
    public static BotJob TJob(TeamPlan plan, string bot, bool carrier, long now, IReadOnlyList<CsSiteView> sites, double[]? ctSpawn, string? carrierId = null)
    {
        if (sites.Count == 0) return new BotJob("roam", null);
        var site = sites[plan.Site % sites.Count];
        var other = sites[(plan.Site + 1) % sites.Count];
        double[] At(CsSiteView s) => [s.X, s.Y, s.Z];
        var index = Math.Max(0, plan.Order.ToList().IndexOf(bot));
        var executing = now >= plan.ExecuteAt;
        // The lurker: out towards the other site, waiting quietly in its approach for the rotation.
        if (!carrier && sites.Count > 1 && Lurker(plan, carrierId) == bot) return new BotJob("lurk", At(other), Stop: 0.7, Face: At(other));
        switch (plan.Style)
        {
            case BotStyles.Default when !executing:
                // Map control: part of the way towards a site, alternating between the two; the
                // carrier waits back with the site's group.
                var towards = carrier ? site : index % 2 == 0 ? site : other;
                return new BotJob(carrier ? "carrier" : "control", At(towards), Stop: carrier ? 0.45 : 0.55 + 0.1 * (index % 3), Face: At(towards));
            case BotStyles.Split when !carrier && index % 2 == 1 && sites.Count > 1:
                // The second half come in by way of the other site's approach.
                return new BotJob("split", At(site), Via: At(other), ViaStop: 0.6);
            default:
                return new BotJob(carrier ? "carrier" : "entry", At(site));
        }
    }

    // A Counter-Terrorist's job: its site from the split (-1: the forward rotator), unless the
    // defence rotates to an alert. `split` is every living defender bot's site.
    public static BotJob CtJob(TeamPlan plan, string bot, IReadOnlyList<CsSiteView> sites, double[]? tSpawn, CtAlert? alert, IReadOnlyDictionary<string, int>? split = null, double[]? at = null)
    {
        if (sites.Count == 0) return new BotJob("roam", null);
        double[] At(CsSiteView s) => [s.X, s.Y, s.Z];
        split ??= BotSplits.Assign(BotSplits.Shape(plan.Order.Count, BotSplits.Read([], sites.Count, plan.Site), false), plan.Order.Select(b => (b, (double[]?)null)).ToList(), null, sites);
        var mine = split.TryGetValue(bot, out var assigned) ? assigned : plan.Site % sites.Count;
        if (alert is { } a)
        {
            var site = sites[a.Site % sites.Count];
            var target = At(site);
            var execute = a.Count >= 3;
            // A full execute: whoever isn't in the site already sets up short of it to retake (no
            // running in one by one), the split's new fill included.
            var inSite = at is not null && Math.Sqrt((at[0] - site.X) * (at[0] - site.X) + (at[1] - site.Y) * (at[1] - site.Y)) < SiteAlertCm * 0.6;
            if (mine == a.Site) return execute && at is not null && !inSite ? new BotJob("rotate (retake setup)", target, Stop: 0.7, Face: target) : new BotJob("anchor", target, Face: tSpawn);
            var there = split.Count(kv => kv.Value == a.Site);
            // Who goes: the forward one first, then the other sites' anchors in the side's order, never
            // the last one on a site (with three or more defenders); a full execute takes everyone else,
            // less as many as make one more there than the enemies called (two at least).
            var order = plan.Order.ToList();
            var movers = split.Where(kv => kv.Value != a.Site).OrderBy(kv => kv.Value == -1 ? 0 : 1).ThenBy(kv => Math.Max(0, order.IndexOf(kv.Key))).Select(kv => kv.Key).ToList();
            var keep = split.Count <= 2 ? [] : movers.Where(m => split[m] >= 0).GroupBy(m => split[m]).Select(g => g.Last()).ToHashSet();
            var candidates = movers.Where(m => !keep.Contains(m)).ToList();
            var need = execute ? candidates.Count : Math.Max(0, Math.Max(2, a.Count + 1) - there);
            if (candidates.Take(need).Contains(bot))
                return execute ? new BotJob("rotate (retake setup)", target, Stop: 0.7, Face: target) : new BotJob("rotate", target, Face: tSpawn);
        }
        if (mine < 0 && tSpawn is not null) return new BotJob("rotator", tSpawn, Stop: 0.35, Face: tSpawn);
        return new BotJob("anchor", At(sites[Math.Max(0, mine) % sites.Count]), Face: tSpawn);
    }

    // What raises a site: enemies seen or heard near it, or a teammate's death called out there; how
    // many (the most of either).
    public static CtAlert? Alert(IEnumerable<double[]> enemies, IEnumerable<TeamKnowledge.DeathCall> deaths, IReadOnlyList<CsSiteView> sites)
    {
        var counts = new int[sites.Count];
        int? Near(double[] p)
        {
            int? best = null; var bestD = double.MaxValue;
            for (var i = 0; i < sites.Count; i++)
            {
                var d = Math.Sqrt((p[0] - sites[i].X) * (p[0] - sites[i].X) + (p[1] - sites[i].Y) * (p[1] - sites[i].Y));
                if (d < SiteAlertCm && d < bestD) { bestD = d; best = i; }
            }
            return best;
        }
        foreach (var e in enemies) if (Near(e) is { } i) counts[i]++;
        // Deaths: the most one of them saw, or every killer named over them; two deaths there mean two at least.
        var fromDeaths = new int[sites.Count];
        var killers = Enumerable.Range(0, sites.Count).Select(_ => new HashSet<string>(StringComparer.Ordinal)).ToArray();
        var deathsAt = new int[sites.Count];
        foreach (var d in deaths)
            if (Near(d.At) is { } i) { fromDeaths[i] = Math.Max(fromDeaths[i], d.Count); killers[i].UnionWith(d.Killers); deathsAt[i]++; }
        for (var i = 0; i < sites.Count; i++) fromDeaths[i] = Math.Max(fromDeaths[i], Math.Max(killers[i].Count, deathsAt[i] >= 2 ? 2 : 0));
        CtAlert? alert = null;
        for (var i = 0; i < sites.Count; i++)
        {
            var c = Math.Max(counts[i], fromDeaths[i]);
            if (c > 0 && (alert is null || c > alert.Count)) alert = new CtAlert(i, c, fromDeaths[i] >= counts[i] && fromDeaths[i] > 0 ? "death" : "seen");
        }
        return alert;
    }

    // The site a sighting points at: an enemy within SiteAlertCm of a site (nearest site), or none.
    public static int? AlertSite(IEnumerable<double[]> enemies, IReadOnlyList<CsSiteView> sites)
    {
        int? best = null;
        var bestD = double.MaxValue;
        foreach (var e in enemies)
            for (var i = 0; i < sites.Count; i++)
            {
                var d = Math.Sqrt((e[0] - sites[i].X) * (e[0] - sites[i].X) + (e[1] - sites[i].Y) * (e[1] - sites[i].Y));
                if (d < SiteAlertCm && d < bestD) { bestD = d; best = i; }
            }
        return best;
    }

    public static int Hash(string s)
    {
        unchecked
        {
            var h = (int)2166136261;
            foreach (var c in s) h = (h ^ c) * 16777619;
            return h & 0x7fffffff;
        }
    }
}

// How Counter-Terrorists split over the sites this round. The read: where the Terrorists hit in the
// last rounds (planted, or where they were raised), most recent counting most (two recent hits on
// a site make it heavy, one doesn't), with the round's favoured site breaking ties. The shape: with five, 2-1-2 (one forward) or 3-2 heavy on a strong
// read; four 2-2 or 3-1; three 2-1 (two where the read points); two 1-1 (both on a very strong
// read); one where the read points. Their own eco (or a strong read) stacks. Re-applied to the
// living defenders every step, each keeping its site where it can, the others to the nearest site
// short of its count.
static class BotSplits
{
    public const double Heavy = 0.63, VeryHeavy = 0.8;

    public static IReadOnlyList<double> Read(IReadOnlyList<int> history, int sites, int favoured)
    {
        if (sites <= 0) return [];
        // The favoured site only breaks ties; a hit counts 0.6, older ones less (0.7 a round back).
        var w = Enumerable.Repeat(1.0, sites).ToArray();
        w[favoured % sites] += 0.01;
        var k = 1.0;
        for (var i = history.Count - 1; i >= 0 && i >= history.Count - 5; i--, k *= 0.7)
            if (history[i] >= 0 && history[i] < sites) w[history[i]] += 0.6 * k;
        var sum = w.Sum();
        return w.Select(x => x / sum).ToArray();
    }

    // Counts per site and how many play forward, and the shape's name ("2-1-2").
    public sealed record Split(int[] Counts, int Forward, string Name);
    public static Split Shape(int n, IReadOnlyList<double> read, bool stack)
    {
        var sites = read.Count;
        if (sites == 0 || n <= 0) return new([], 0, "none");
        var top = Enumerable.Range(0, sites).OrderByDescending(i => read[i]).First();
        var heavy = stack || read[top] >= Heavy;
        var counts = new int[sites];
        var forward = n >= 5 && !heavy ? 1 : 0;
        var m = n - forward;
        if (sites == 1) counts[0] = m;
        else if (m == 1) counts[top] = 1;
        else if (m == 2 && !(read[top] >= VeryHeavy)) { counts[top] = 1; counts[(top + 1) % sites] = 1; }
        else
        {
            // Everyone on a site gets company where it can; the heavy site the most.
            var rest = Enumerable.Range(0, sites).Where(i => i != top).OrderByDescending(i => read[i]).ToList();
            var share = heavy ? (m + 2) / 2 : (m + 1) / 2;
            if (m == 2) share = 2;
            counts[top] = Math.Min(m, share);
            var left = m - counts[top];
            for (var i = 0; left > 0; i = (i + 1) % rest.Count) { counts[rest[i]]++; left--; }
        }
        var name = sites == 2 ? (forward > 0 ? counts[0] + "-" + forward + "-" + counts[1] : counts[0] + "-" + counts[1]) : string.Join("-", counts) + (forward > 0 ? " +" + forward : "");
        return new(counts, forward, name);
    }

    // Every living defender's site (-1: forward): each keeps its last one while its site has room,
    // the nearest to a short site moves there; the forward one is whoever was forward, else the last.
    public static Dictionary<string, int> Assign(Split split, IReadOnlyList<(string Member, double[]? At)> bots, IReadOnlyDictionary<string, int>? previous, IReadOnlyList<CsSiteView> sites)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var room = split.Counts.ToArray();
        var forward = split.Forward;
        double D((string Member, double[]? At) b, int site) => b.At is null || site < 0 || site >= sites.Count ? 0 : Math.Sqrt(Math.Pow(b.At[0] - sites[site].X, 2) + Math.Pow(b.At[1] - sites[site].Y, 2));
        foreach (var b in bots)
            if (previous?.TryGetValue(b.Member, out var was) == true)
            {
                if (was == -1 && forward > 0) { result[b.Member] = -1; forward--; }
                else if (was >= 0 && was < room.Length && room[was] > 0) { result[b.Member] = was; room[was]--; }
            }
        var free = bots.Where(b => !result.ContainsKey(b.Member)).ToList();
        while (free.Count > 0)
        {
            var open = Enumerable.Range(0, room.Length).Where(i => room[i] > 0).ToList();
            if (open.Count == 0) break;
            var (bot, site) = free.SelectMany(b => open.Select(i => (b, i))).OrderBy(x => D(x.b, x.i)).ThenBy(x => bots.ToList().IndexOf(x.b)).First();
            result[bot.Member] = site; room[site]--; free.Remove(bot);
        }
        foreach (var b in free) result[b.Member] = forward-- > 0 ? -1 : split.Counts.Length == 0 ? 0 : Array.IndexOf(split.Counts, split.Counts.Max());
        return result;
    }
}

// CS buying, per side: how the team buys this round from its money, like CS players do.
//  full: rifles and armour; half: SMGs and armour; force: whatever is best (last round of a half,
//  match point, or the loss bonus at its top); eco: save (pistol round: armour only).
enum BuyKind { Eco, Half, Force, Full, Pistol }

static class BotEconomy
{
    public const int FullBuyMoney = 3_700, HalfBuyMoney = 2_000;

    public static BuyKind TeamBuy(CsView cs, string side)
    {
        if (cs.Round == 1 || cs.Round == cs.HalfRounds + 1) return BuyKind.Pistol;
        var team = cs.Players.Where(p => p.Side == side).ToList();
        if (team.Count == 0) return BuyKind.Eco;
        var average = team.Average(p => p.Money);
        if (average >= FullBuyMoney) return BuyKind.Full;
        var tTeam = cs.Team1Side == CsRules.T ? 1 : 2;
        var us = side == CsRules.T ? tTeam : 3 - tTeam;
        var ourScore = cs.Score[us - 1];
        var theirScore = cs.Score[2 - us];
        var lastOfHalf = cs.Round == cs.HalfRounds;
        var matchPoint = theirScore == cs.HalfRounds || ourScore == cs.HalfRounds;
        if (lastOfHalf || matchPoint) return BuyKind.Force;
        return average >= HalfBuyMoney ? BuyKind.Half : BuyKind.Eco;
    }

    // What one bot buys for its team's call (rifle and armour; an SMG on a half buy; armour on the
    // pistol round; a kit for Counter-Terrorists with money left; nothing on an eco).
    public static IReadOnlyList<string> BuyList(BuyKind kind, CsPlayerView me, BotSkill skill)
    {
        var money = me.Money;
        var list = new List<string>();
        var rifle = me.Side == CsRules.T ? CsRules.Find("ak47")! : CsRules.Find("m4a1s")!;
        var smg = me.Side == CsRules.T ? CsRules.Find("mac10")! : CsRules.Find("mp9")!;
        void Armour(bool helmet)
        {
            if (helmet && (me.Armor < CsRules.MaxArmor || !me.Helmet) && money >= CsRules.KevlarHelmetPrice) { list.Add("kevlar-helmet"); money -= CsRules.KevlarHelmetPrice; }
            else if (me.Armor < CsRules.MaxArmor && money >= CsRules.KevlarPrice) { list.Add("kevlar"); money -= CsRules.KevlarPrice; }
        }
        switch (kind)
        {
            case BuyKind.Pistol:
                Armour(false);
                break;
            case BuyKind.Full:
            case BuyKind.Force:
                if (me.Primary is null && money >= rifle.Price) { list.Add(rifle.Id); money -= rifle.Price; }
                else if (me.Primary is null && money >= smg.Price) { list.Add(smg.Id); money -= smg.Price; }
                Armour(true);
                break;
            case BuyKind.Half:
                if (me.Primary is null && money >= smg.Price + CsRules.KevlarPrice) { list.Add(smg.Id); money -= smg.Price; }
                Armour(false);
                break;
            case BuyKind.Eco:
                break;
        }
        if (kind is BuyKind.Full or BuyKind.Force && me.Side == CsRules.CT && !me.Kit && skill.Id != BotSkills.Easy && money >= CsRules.KitPrice) list.Add("defuse-kit");
        return list;
    }
}

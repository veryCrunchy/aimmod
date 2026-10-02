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
// After the plant they hold around the bomb, facing the way the Counter-Terrorists come back.
// Counter-Terrorists split over the sites (anchors), one plays forward towards the Terrorist spawn
// (the rotator); a sighting near a site or the bomb going down there pulls the others over
// (rotation); with the bomb planted they gather short of it, then retake together and the nearest
// defuses while the others cover.
static class BotStyles
{
    public const string Rush = "rush", Default = "default", Split = "split", Eco = "eco";
}

// A bot's job this round. Goal: where it heads (null: hold where it is); Stop: walk only that
// fraction of the way (map control, a default spot); Via: by way of a point first (a split);
// Face: what it watches from its spot.
sealed record BotJob(string Role, double[]? Goal, double Stop = 1, double[]? Via = null, double ViaStop = 1, double[]? Face = null);

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

    // A Terrorist's job: where it goes and how far, from the plan and the round so far.
    public static BotJob TJob(TeamPlan plan, string bot, bool carrier, long now, IReadOnlyList<CsSiteView> sites, double[]? ctSpawn)
    {
        if (sites.Count == 0) return new BotJob("roam", null);
        var site = sites[plan.Site % sites.Count];
        var other = sites[(plan.Site + 1) % sites.Count];
        double[] At(CsSiteView s) => [s.X, s.Y, s.Z];
        var index = Math.Max(0, plan.Order.ToList().IndexOf(bot));
        var executing = now >= plan.ExecuteAt;
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

    // A Counter-Terrorist's job: its site (or the forward spot), unless the defence rotates.
    public static BotJob CtJob(TeamPlan plan, string bot, IReadOnlyList<CsSiteView> sites, double[]? tSpawn, int? alertSite)
    {
        if (sites.Count == 0) return new BotJob("roam", null);
        double[] At(CsSiteView s) => [s.X, s.Y, s.Z];
        var index = Math.Max(0, plan.Order.ToList().IndexOf(bot));
        var n = plan.Order.Count;
        // Rotation: everyone but one anchor on the other site goes to the alert.
        if (alertSite is { } alert)
        {
            var otherAnchor = Enumerable.Range(0, n).FirstOrDefault(i => SiteOf(i, n, sites.Count, plan.Site) != alert, -1);
            if (index != otherAnchor || n <= 2) return new BotJob("rotate", At(sites[alert % sites.Count]), Face: tSpawn);
        }
        // Five or more: the last one plays forward, a third of the way towards the Terrorist spawn.
        if (n >= 5 && index == n - 1 && tSpawn is not null) return new BotJob("rotator", tSpawn, Stop: 0.35, Face: tSpawn);
        var mine = SiteOf(index, n >= 5 ? n - 1 : n, sites.Count, plan.Site);
        return new BotJob("anchor", At(sites[mine]), Face: tSpawn);
    }

    // Anchors spread over the sites, the round's favoured site getting the odd one.
    static int SiteOf(int index, int n, int sites, int favoured) => sites == 0 ? 0 : (favoured + index) % sites;

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

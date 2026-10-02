namespace AimMod.InGame.Multiplayer;

// Host-run bots for the shooting modes (CS, deathmatch, team deathmatch). The host's game draws
// each bot as one of AimModSteam's walking avatars; this logic decides where it goes, what it
// buys, when it plants or defuses and when it shoots. A bot only shoots a player its own game
// reports in sight (a line trace from the bot's eye), after a reaction time, and whether a shot
// lands is a roll on its difficulty's aim (distance, target movement and spray make it worse).
// Its hits go through the same host rules as a player's (CombatMatch.BotHit). Bots never post
// runs, never reach the Hub and can't join tournament lobbies.
static class BotSkills
{
    public const string Easy = "easy", Normal = "normal", Hard = "hard";
    public static readonly string[] All = [Easy, Normal, Hard];
    public static string? Parse(string? s) => s is Easy or Normal or Hard ? s : null;
    public static string Label(string? s) => s switch { Easy => "Easy", Hard => "Hard", _ => "Normal" };
    public static BotSkill For(string? s) => s switch { Easy => EasySkill, Hard => HardSkill, _ => NormalSkill };
    static readonly BotSkill EasySkill = new(Easy, 650, 0.28, 0.08, 3, 650, 0.08, 2500, 1.5);
    static readonly BotSkill NormalSkill = new(Normal, 380, 0.45, 0.18, 5, 420, 0.06, 4000, 1.2);
    static readonly BotSkill HardSkill = new(Hard, 220, 0.62, 0.32, 7, 260, 0.04, 6000, 1.0);
}

// ReactionMs: from first sight to the first shot. Hit: the chance a shot lands on a still target up
// close; Head: the share of landed shots that are headshots. Burst shots, then PauseMs. SprayDecay:
// hit chance lost per shot within a burst. RangeCm: no shots beyond. FireRateScale: slower than the
// weapon allows (easy bots tap).
sealed record BotSkill(string Id, int ReactionMs, double Hit, double Head, int Burst, int PauseMs, double SprayDecay, double RangeCm, double FireRateScale);

// A player as the bots see them: eye position (world cm), team (0: free for all), alive, speed (cm/s).
// Tag: the player's number in the match (what sight targets are reported by).
sealed record BotPlayer(string Member, int Tag, double X, double Y, double Z, int Team, bool Alive, double Speed);
// What a bot's game reported: its avatar's capsule centre and yaw, and which tags it can see.
sealed record BotSight(long At, double X, double Y, double Z, double Yaw, IReadOnlySet<int> Visible);
sealed record BotWorld(long Now, string Mode, IReadOnlyList<(string Member, string Skill)> Bots, IReadOnlyList<BotPlayer> Players,
    IReadOnlyDictionary<string, BotSight> Sight, CsView? Cs, MapObjectives? Map, IReadOnlyList<CombatEvent> Events);

// Mode: roam (between waypoints), goal (walk to Goal and stay), hold (stand still). Face: look at
// that point. Sight: the targets to trace (tag, eye). Place: stand at PlaceAt once per token (a CS
// round's spawn, a respawn).
sealed record BotOrder(string Member, string Mode, double[]? Goal, double[]? Face, IReadOnlyList<(int Tag, double[] Eye)> Sight, string? PlaceToken, double[]? PlaceAt);
sealed record BotShotDecision(string Bot, string Victim, bool Head, int Slot, double[] Dir);
sealed record BotAction(string Bot, string Action, Dictionary<string, object> Args);
sealed record BotStep(IReadOnlyList<BotOrder> Orders, IReadOnlyList<BotShotDecision> Shots, IReadOnlyList<BotAction> Actions);

sealed class BotBrain(int seed = 0)
{
    public const double EyeAboveCentre = 64;
    public const int MaxSight = 6;
    public const long SightFreshMs = 700, ChaseMs = 4000;
    sealed class State
    {
        public string? Target; public long ReactUntil, NextShot; public int BurstShot;
        public double[]? LastSeen; public long LastSeenAt = long.MinValue / 2;
        public int BoughtRound = -1; public long BuyAt; public int BuyTries;
        public bool Using; public long UseSentAt = long.MinValue / 2; public int Held = -1;
        public long HoldSince = long.MinValue / 2;
    }
    readonly Random random = seed == 0 ? new Random() : new Random(seed);
    readonly Dictionary<string, State> states = new();
    string? matchKey;

    public void Reset(string key) { if (matchKey != key) { matchKey = key; states.Clear(); } }

    public BotStep Step(BotWorld w)
    {
        var orders = new List<BotOrder>(); var shots = new List<BotShotDecision>(); var actions = new List<BotAction>();
        foreach (var id in states.Keys.Where(id => w.Bots.All(b => b.Member != id)).ToArray()) states.Remove(id);
        var botIndex = 0;
        foreach (var (member, skillId) in w.Bots)
        {
            var st = states.TryGetValue(member, out var have) ? have : states[member] = new State();
            var skill = BotSkills.For(skillId);
            var index = botIndex++;
            var self = w.Players.FirstOrDefault(p => p.Member == member);
            var sight = w.Sight.TryGetValue(member, out var s) && w.Now - s.At <= SightFreshMs ? s : null;
            var eye = sight is not null ? new[] { sight.X, sight.Y, sight.Z + EyeAboveCentre } : self is not null ? new[] { self.X, self.Y, self.Z } : null;
            var (placeToken, placeAt) = Place(w, member);
            var csSelf = w.Cs?.Players.FirstOrDefault(p => p.Member == member);
            if (self is null || !self.Alive || eye is null)
            {
                // Down: it stays where it fell (hidden) until it respawns or the next round places it.
                st.Target = null; st.Using = false;
                orders.Add(new BotOrder(member, self is { Alive: false } ? "hold" : "roam", null, null, [], placeToken, placeAt));
                continue;
            }
            var team = self.Team;
            var enemies = w.Players.Where(p => p.Member != member && p.Alive && (team == 0 || p.Team != team))
                .Select(p => (P: p, D: Distance(eye, p))).OrderBy(x => x.D).ToList();
            var sightList = enemies.Where(x => x.D <= skill.RangeCm * 1.25).Take(MaxSight).Select(x => (x.P.Tag, new[] { Math.Round(x.P.X, 1), Math.Round(x.P.Y, 1), Math.Round(x.P.Z, 1) })).ToArray();

            // CS: buy in freeze time, hold the best weapon.
            var slot = 0;
            if (w.Cs is { } cs && csSelf is not null)
            {
                slot = csSelf.Primary is not null ? CsRules.PrimarySlot : CsRules.PistolSlot;
                if (st.Held != slot) { st.Held = slot; actions.Add(new BotAction(member, "hold", new() { ["slot"] = slot })); }
                if (cs.Phase == "freeze" || (cs.Phase == "live" && cs.LiveAt is { } live && w.Now < live + 3000))
                {
                    if (st.BoughtRound != cs.Round) { st.BoughtRound = cs.Round; st.BuyAt = w.Now + random.Next(800, 2500); st.BuyTries = 0; }
                    if (w.Now >= st.BuyAt && st.BuyTries < 6)
                    {
                        st.BuyTries++; st.BuyAt = w.Now + 600;
                        foreach (var item in BuyList(cs, csSelf, skill)) actions.Add(new BotAction(member, "buy", new() { ["item"] = item }));
                    }
                }
                if (cs.Phase == "freeze")
                {
                    orders.Add(new BotOrder(member, "hold", null, null, sightList, placeToken, placeAt));
                    continue;
                }
                if (cs.Phase is not ("live" or "planted"))
                {
                    if (st.Using) { st.Using = false; actions.Add(new BotAction(member, "use", new() { ["held"] = false })); }
                    orders.Add(new BotOrder(member, "hold", null, null, [], placeToken, placeAt));
                    continue;
                }
            }

            // Who's in sight (the game's trace says so, for a recent request).
            var visible = enemies.Where(x => x.D <= skill.RangeCm && sight is not null && sight.Visible.Contains(x.P.Tag)).ToList();
            var current = visible.FirstOrDefault(x => x.P.Member == st.Target);
            var pick = current.P is not null ? current : visible.FirstOrDefault();
            if (pick.P is not null && pick.P.Member != st.Target)
            {
                st.Target = pick.P.Member;
                st.ReactUntil = w.Now + (long)(skill.ReactionMs * (0.8 + random.NextDouble() * 0.4));
                st.BurstShot = 0; st.NextShot = Math.Max(st.NextShot, st.ReactUntil);
            }
            if (pick.P is null) st.Target = null;
            else { st.LastSeen = [pick.P.X, pick.P.Y, pick.P.Z - EyeAboveCentre]; st.LastSeenAt = w.Now; }

            // The objective (CS) decides whether a bot keeps planting or defusing through a fight.
            // Its place among its own side's bots (CTs split over the sites by it; counting every bot
            // sent all of one side's bots to the same site when the sides alternate).
            var sideIndex = w.Cs is { } c0 && csSelf is not null ? w.Bots.TakeWhile(b => b.Member != member).Count(b => c0.Players.FirstOrDefault(p => p.Member == b.Member)?.Side == csSelf.Side) : index;
            var objective = w.Cs is { } cs2 && csSelf is not null ? Objective(w, cs2, csSelf, member, sideIndex, eye, st) : null;
            var busy = objective is { Use: true } && w.Cs is { } cs3 && (
                (cs3.Bomb.Planter == member && cs3.Bomb.PlantDoneAt is { } pd && pd - w.Now < 1000) ||
                (cs3.Bomb.Defuser == member && cs3.Bomb.DefuseDoneAt is { } dd && (dd - w.Now < 1500 || (skill.Id == BotSkills.Hard && dd - w.Now < 3000))));

            if (pick.P is not null && !busy)
            {
                if (st.Using) { st.Using = false; actions.Add(new BotAction(member, "use", new() { ["held"] = false })); }
                var target = pick.P;
                orders.Add(new BotOrder(member, "hold", null, [target.X, target.Y, target.Z - 8], sightList, placeToken, placeAt));
                if (w.Now >= st.ReactUntil)
                {
                    var interval = WeaponInterval(w, csSelf) * skill.FireRateScale;
                    for (var n = 0; n < 3 && st.NextShot <= w.Now; n++)
                    {
                        var p = HitChance(skill, pick.D, target.Speed, st.BurstShot);
                        if (random.NextDouble() < p)
                        {
                            var d = Math.Max(1, pick.D);
                            shots.Add(new BotShotDecision(member, target.Member, random.NextDouble() < skill.Head, slot, [(target.X - eye[0]) / d, (target.Y - eye[1]) / d, (target.Z - eye[2]) / d]));
                        }
                        st.BurstShot++;
                        st.NextShot = Math.Max(st.NextShot, w.Now - 50) + (long)(interval * 1000);
                        if (st.BurstShot >= skill.Burst) { st.BurstShot = 0; st.NextShot += (long)(skill.PauseMs * (0.8 + random.NextDouble() * 0.4)); }
                    }
                }
                continue;
            }

            if (objective is { } o)
            {
                if (o.Use)
                {
                    // Stand still a moment first (planting needs a still body), then hold the use key;
                    // pressed again only if the plant or defuse didn't start (never restarts one).
                    if (st.HoldSince == long.MinValue / 2) st.HoldSince = w.Now;
                    var active = w.Cs is { } c && (c.Bomb.Planter == member || c.Bomb.Defuser == member);
                    if (!active && w.Now - st.HoldSince >= 400 && (!st.Using || w.Now - st.UseSentAt > 1000))
                    { st.Using = true; st.UseSentAt = w.Now; actions.Add(new BotAction(member, "use", new() { ["held"] = true })); }
                    orders.Add(new BotOrder(member, "hold", null, null, sightList, placeToken, placeAt));
                    continue;
                }
                st.HoldSince = long.MinValue / 2;
                if (st.Using) { st.Using = false; actions.Add(new BotAction(member, "use", new() { ["held"] = false })); }
                // Near its spot: hold the angle the enemy comes from (their spawn's way).
                orders.Add(new BotOrder(member, "goal", o.Goal, o.Goal is { } at && csSelf is not null ? HoldAngle(w, csSelf.Side, eye, at) : null, sightList, placeToken, placeAt));
                continue;
            }
            st.HoldSince = long.MinValue / 2;
            // Lost sight: go where the enemy was last seen for a while, else roam.
            if (st.LastSeen is { } last && w.Now - st.LastSeenAt < ChaseMs)
                orders.Add(new BotOrder(member, "goal", last, null, sightList, placeToken, placeAt));
            else
                orders.Add(new BotOrder(member, "roam", null, null, sightList, placeToken, placeAt));
        }
        return new BotStep(orders, shots, actions);
    }

    sealed record Plan(double[]? Goal, bool Use);

    public const double HoldAngleCm = 600;
    // Within HoldAngleCm of its spot, a bot looks toward the other side's spawn (the way the enemy
    // comes) at eye height; farther away it looks where it walks (null).
    internal static double[]? HoldAngle(BotWorld w, string side, double[] eye, double[] spot)
    {
        if (Math.Sqrt((eye[0] - spot[0]) * (eye[0] - spot[0]) + (eye[1] - spot[1]) * (eye[1] - spot[1])) > HoldAngleCm) return null;
        var enemy = w.Map?.SpawnsFor(side == CsRules.T ? CsRules.CT : CsRules.T) ?? [];
        if (enemy.Count == 0) return null;
        return [Math.Round(enemy.Average(s => s.X), 1), Math.Round(enemy.Average(s => s.Y), 1), Math.Round(eye[2], 1)];
    }

    // CS: Terrorists take the round's site (the carrier plants there, the bomb is picked up when
    // dropped, then they guard it); Counter-Terrorists split between the sites and defuse.
    Plan? Objective(BotWorld w, CsView cs, CsPlayerView me, string member, int index, double[] eye, State st)
    {
        var sites = cs.Sites ?? [];
        if (sites.Count == 0) return null;
        var bomb = cs.Bomb;
        if (me.Side == CsRules.T)
        {
            var site = sites[(cs.Round + (matchKey?.Length ?? 0)) % sites.Count];
            if (bomb.State == "planted" && bomb.Position is { Length: 3 } planted) return new Plan([planted[0], planted[1], planted[2]], false);
            if (bomb.State == "dropped" && bomb.Position is { Length: 3 } dropped)
            {
                // The nearest Terrorist bot fetches it.
                var nearest = cs.Players.Where(p => p.Side == CsRules.T && p.Alive && w.Bots.Any(b => b.Member == p.Member))
                    .Select(p => (p.Member, D: w.Players.FirstOrDefault(x => x.Member == p.Member) is { } q ? Math.Sqrt((q.X - dropped[0]) * (q.X - dropped[0]) + (q.Y - dropped[1]) * (q.Y - dropped[1])) : double.MaxValue))
                    .OrderBy(x => x.D).FirstOrDefault();
                if (nearest.Member == member) return new Plan([dropped[0], dropped[1], dropped[2]], false);
            }
            if (bomb.Carrier == member && cs.Phase == "live")
            {
                var zone = w.Map?.BombSites.FirstOrDefault(z => z.Contains(eye[0], eye[1], eye[2]));
                if (zone is not null) return new Plan(null, true);
                if (bomb.Planter == member) return new Plan(null, true);
            }
            return new Plan([site.X, site.Y, site.Z], false);
        }
        if (bomb.State == "planted" && bomb.Position is { Length: 3 } b)
        {
            var near = Math.Sqrt((eye[0] - b[0]) * (eye[0] - b[0]) + (eye[1] - b[1]) * (eye[1] - b[1])) <= CsRules.DefuseRadiusCm * 0.7;
            if (near || bomb.Defuser == member) return new Plan(null, true);
            return new Plan([b[0], b[1], b[2]], false); // to the bomb (or covering whoever defuses)
        }
        var mine = sites[index % sites.Count];
        return new Plan([mine.X, mine.Y, mine.Z], false);
    }

    // CS2-like buys: rifle and armour when it can, an SMG and armour on a half buy, armour on the pistol
    // round, a kit for Counter-Terrorists with money left. Nothing on an eco.
    static IEnumerable<string> BuyList(CsView cs, CsPlayerView me, BotSkill skill)
    {
        var money = me.Money;
        var list = new List<string>();
        var rifle = me.Side == CsRules.T ? CsRules.Find("ak47")! : CsRules.Find("m4a1s")!;
        var smg = me.Side == CsRules.T ? CsRules.Find("mac10")! : CsRules.Find("mp9")!;
        var pistolRound = cs.Round == 1 || cs.Round == cs.HalfRounds + 1;
        if (me.Primary is null && !pistolRound)
        {
            if (money >= rifle.Price + CsRules.KevlarPrice) { list.Add(rifle.Id); money -= rifle.Price; }
            else if (money >= smg.Price + CsRules.KevlarPrice && money < rifle.Price + CsRules.KevlarPrice && money >= 2000) { list.Add(smg.Id); money -= smg.Price; }
        }
        if (me.Armor < CsRules.MaxArmor || !me.Helmet)
        {
            if (!pistolRound && money >= CsRules.KevlarHelmetPrice && (me.Primary is not null || list.Count > 0)) { list.Add("kevlar-helmet"); money -= CsRules.KevlarHelmetPrice; }
            else if (me.Armor < CsRules.MaxArmor && money >= CsRules.KevlarPrice && (pistolRound || list.Count > 0 || me.Primary is not null)) { list.Add("kevlar"); money -= CsRules.KevlarPrice; }
        }
        if (me.Side == CsRules.CT && !me.Kit && skill.Id != BotSkills.Easy && money >= CsRules.KitPrice && !pistolRound) list.Add("defuse-kit");
        return list;
    }

    static double WeaponInterval(BotWorld w, CsPlayerView? me)
    {
        if (me is not null) return (CsRules.Find(me.Primary) ?? CsRules.Find(me.Secondary) ?? CsRules.DefaultPistol(me.Side)).Combat.TimeBetweenShots;
        return CombatRules.Weapon(w.Mode).TimeBetweenShots;
    }

    // Aim: the skill's base chance, worse with distance, a moving target and deeper into a spray.
    public static double HitChance(BotSkill skill, double distance, double targetSpeed, int burstShot)
    {
        var range = Math.Clamp(1.15 - distance / skill.RangeCm * 0.8, 0.25, 1.1);
        var moving = targetSpeed > 150 ? 0.75 : 1;
        var spray = Math.Max(0.4, 1 - skill.SprayDecay * burstShot);
        return Math.Clamp(skill.Hit * range * moving * spray, 0, 0.95);
    }

    // Where the bot stands at a round start (CS) or after a respawn (the host's spawn for it).
    static (string?, double[]?) Place(BotWorld w, string member)
    {
        if (w.Cs is { Spawns: { } spawns } cs && spawns.TryGetValue(member, out var s) && s.Length >= 3) return ("cs" + cs.Round, [s[0], s[1], s[2], s.Length > 3 ? s[3] : 0]);
        var e = w.Events.LastOrDefault(x => x.Kind == "respawn" && x.Member == member && x.Spawn is { Length: >= 3 });
        return e is null ? (null, null) : ("r" + e.Id, [e.Spawn![0], e.Spawn[1], e.Spawn[2], e.Spawn.Length > 3 ? e.Spawn[3] : 0]);
    }

    static double Distance(double[] eye, BotPlayer p) => Math.Sqrt((p.X - eye[0]) * (p.X - eye[0]) + (p.Y - eye[1]) * (p.Y - eye[1]) + (p.Z - eye[2]) * (p.Z - eye[2]));
}

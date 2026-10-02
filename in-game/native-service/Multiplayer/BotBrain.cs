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
// Floor: the floor under it (the bridge traced it), when reported.
sealed record BotSight(long At, double X, double Y, double Z, double Yaw, IReadOnlySet<int> Visible, double? Floor = null);
sealed record BotWorld(long Now, string Mode, IReadOnlyList<(string Member, string Skill)> Bots, IReadOnlyList<BotPlayer> Players,
    IReadOnlyDictionary<string, BotSight> Sight, CsView? Cs, MapObjectives? Map, IReadOnlyList<CombatEvent> Events);

// Mode: roam (between waypoints), goal (walk to Goal and stay), hold (stand still). Face: look at
// that point. Sight: the targets to trace (tag, eye). Place: stand at PlaceAt once per token (a CS
// round's spawn, a respawn). Stop: walk only that fraction of the way to Goal; Via (x, y, z,
// fraction): a detour first; Fight: strafe that hard while holding in a fight; Turn: degrees per
// second it turns (its skill).
sealed record BotOrder(string Member, string Mode, double[]? Goal, double[]? Face, IReadOnlyList<(int Tag, double[] Eye)> Sight, string? PlaceToken, double[]? PlaceAt,
    double Stop = 1, double[]? Via = null, double Fight = 0, double Turn = 0, string? Role = null);
sealed record BotShotDecision(string Bot, string Victim, bool Head, int Slot, double[] Dir);
sealed record BotAction(string Bot, string Action, Dictionary<string, object> Args);
sealed record BotStep(IReadOnlyList<BotOrder> Orders, IReadOnlyList<BotShotDecision> Shots, IReadOnlyList<BotAction> Actions);

// The bots' decisions, one step for every bot (several times a second). The pieces it uses:
//  - BotStrategy: the side's plan for the round and each bot's job in it (CS);
//  - BotEconomy: how the side buys;
//  - BotAim: turning onto a target and when a shot can land;
//  - TeamKnowledge: what the side saw and heard, shared between its bots.
// Perception is honest: a bot only shoots a player its own game's trace says it sees, and only
// hears footsteps and shots within earshot.
sealed class BotBrain(int seed = 0)
{
    public const double EyeAboveCentre = 64;
    public const int MaxSight = 6;
    public const long SightFreshMs = 700, ChaseMs = 4000;
    // Earshot: running footsteps, and gunfire (cm); a run is faster than this (cm/s).
    public const double HearStepsCm = 2200, HearShotsCm = 4500, RunSpeed = 330;
    public const double LowHealth = 30;
    sealed class State
    {
        public string? Target; public long ReactUntil, NextShot; public int BurstShot;
        public double[]? LastSeen; public long LastSeenAt = long.MinValue / 2;
        public int BoughtRound = -1; public long BuyAt; public int BuyTries;
        public bool Using; public long UseSentAt = long.MinValue / 2; public int Held = -1;
        public long HoldSince = long.MinValue / 2;
        public readonly BotAimState Aim = new();
    }
    readonly Random random = seed == 0 ? new Random() : new Random(seed);
    readonly Dictionary<string, State> states = new();
    readonly Dictionary<string, TeamKnowledge> knowledge = new(StringComparer.Ordinal); // per team (side or TDM team)
    readonly Dictionary<string, TeamPlan> plans = new(StringComparer.Ordinal);         // per side, this round
    string? matchKey;
    long? plantedAt;

    public void Reset(string key) { if (matchKey != key) { matchKey = key; states.Clear(); knowledge.Clear(); plans.Clear(); plantedAt = null; } }
    internal TeamPlan? PlanFor(string side) => plans.GetValueOrDefault(side);
    internal TeamKnowledge KnowledgeOf(string team) => knowledge.TryGetValue(team, out var k) ? k : knowledge[team] = new TeamKnowledge();

    public BotStep Step(BotWorld w)
    {
        var orders = new List<BotOrder>(); var shots = new List<BotShotDecision>(); var actions = new List<BotAction>();
        foreach (var id in states.Keys.Where(id => w.Bots.All(b => b.Member != id)).ToArray()) states.Remove(id);
        string TeamKey(BotPlayer p) => w.Cs?.Players.FirstOrDefault(c => c.Member == p.Member)?.Side ?? (p.Team == 0 ? p.Member : "team" + p.Team);
        Perceive(w, TeamKey);
        if (w.Cs is { } csPlan) PlanRound(w, csPlan);
        var botIndex = 0;
        foreach (var (member, skillId) in w.Bots)
        {
            var st = states.TryGetValue(member, out var have) ? have : states[member] = new State();
            var skill = BotSkills.For(skillId);
            var index = botIndex++;
            var self = w.Players.FirstOrDefault(p => p.Member == member);
            var sight = w.Sight.TryGetValue(member, out var s) && w.Now - s.At <= SightFreshMs ? s : null;
            var eye = self is not null ? new[] { sight?.X ?? self.X, sight?.Y ?? self.Y, self.Z } : sight is not null ? new[] { sight.X, sight.Y, sight.Z + EyeAboveCentre } : null;
            var (placeToken, placeAt) = Place(w, member);
            var csSelf = w.Cs?.Players.FirstOrDefault(p => p.Member == member);
            var turn = BotAim.TurnRate(skill);
            BotOrder Order(string mode, double[]? goal, double[]? face, IReadOnlyList<(int Tag, double[] Eye)> sights, double stop = 1, double[]? via = null, double fight = 0, string? role = null) =>
                new(member, mode, goal, face, sights, placeToken, placeAt, stop, via, fight, turn, role);
            if (self is null || !self.Alive || eye is null)
            {
                // Down: it stays where it fell (hidden) until it respawns or the next round places it.
                st.Target = null; st.Using = false;
                orders.Add(Order(self is { Alive: false } ? "hold" : "roam", null, null, []));
                continue;
            }
            var team = self.Team;
            var teamKey = TeamKey(self);
            var known = KnowledgeOf(teamKey);
            var enemies = w.Players.Where(p => p.Member != member && p.Alive && (team == 0 || p.Team != team) && (w.Cs is null || TeamKey(p) != teamKey))
                .Select(p => (P: p, D: Distance(eye, p))).OrderBy(x => x.D).ToList();
            var sightList = enemies.Where(x => x.D <= skill.RangeCm * 1.25).Take(MaxSight).Select(x => (x.P.Tag, new[] { Math.Round(x.P.X, 1), Math.Round(x.P.Y, 1), Math.Round(x.P.Z, 1) })).ToArray();
            var dt = st.Aim.LastStep == 0 ? 0 : (w.Now - st.Aim.LastStep) / 1000.0;
            st.Aim.LastStep = w.Now;
            if (!st.Aim.Known && sight is not null) { st.Aim.Yaw = sight.Yaw; st.Aim.Known = true; }

            // CS: buy in freeze time by the side's call, hold the best weapon.
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
                        foreach (var item in BotEconomy.BuyList(BotEconomy.TeamBuy(cs, csSelf.Side), csSelf, skill)) actions.Add(new BotAction(member, "buy", new() { ["item"] = item }));
                    }
                }
                if (cs.Phase == "freeze")
                {
                    orders.Add(Order("hold", null, null, sightList, role: "freeze"));
                    continue;
                }
                if (cs.Phase is not ("live" or "planted"))
                {
                    if (st.Using) { st.Using = false; actions.Add(new BotAction(member, "use", new() { ["held"] = false })); }
                    orders.Add(Order("hold", null, null, []));
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
            // Where it was: its track as is (a camera, whatever its hull); the walker goes by the floor under it.
            else { st.LastSeen = [pick.P.X, pick.P.Y, pick.P.Z]; st.LastSeenAt = w.Now; }

            // The objective (CS) decides whether a bot keeps planting or defusing through a fight.
            var job = w.Cs is { } cs2 && csSelf is not null ? CsJob(w, cs2, csSelf, member, eye) : null;
            var busy = job is { Use: true } && w.Cs is { } cs3 && (
                (cs3.Bomb.Planter == member && cs3.Bomb.PlantDoneAt is { } pd && pd - w.Now < 1000) ||
                (cs3.Bomb.Defuser == member && cs3.Bomb.DefuseDoneAt is { } dd && (dd - w.Now < 1500 || (skill.Id == BotSkills.Hard && dd - w.Now < 3000))));

            // Aim: onto the target, else where it is told to look, else where its body faces.
            if (pick.P is not null && !busy)
            {
                var target = pick.P;
                var (yaw, pitch) = BotAim.Angles(eye, [target.X, target.Y, target.Z - 8]);
                var error = BotAim.Turn(st.Aim, yaw, pitch, turn, dt);
                if (st.Using) { st.Using = false; actions.Add(new BotAction(member, "use", new() { ["held"] = false })); }
                orders.Add(Order("hold", null, [target.X, target.Y, target.Z - 8], sightList, fight: BotAim.Strafe(skill), role: "fight"));
                if (w.Now >= st.ReactUntil && BotAim.Ready(st.Aim, error, pick.D, w.Now, skill))
                {
                    var interval = WeaponInterval(w, csSelf) * skill.FireRateScale;
                    for (var n = 0; n < 3 && st.NextShot <= w.Now; n++)
                    {
                        var p = HitChance(skill, pick.D, target.Speed, st.BurstShot) * BotAim.OnTarget(error, pick.D);
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
            st.Aim.SettledSince = long.MinValue / 2;
            if (sight is not null) BotAim.Turn(st.Aim, sight.Yaw, 0, turn, dt);

            // Something heard or seen by the side, close by: watch that way (a corner, a sound).
            var watch = known.Fresh(w.Now).Where(k => Dist2(eye, k.At) < HearShotsCm).OrderBy(k => Dist2(eye, k.At)).Select(k => (double[]?)new[] { k.At[0], k.At[1], k.At[2] }).FirstOrDefault();

            if (job is { } o)
            {
                if (o.Use)
                {
                    // Stand still a moment first (planting needs a still body), then hold the use key;
                    // pressed again only if the plant or defuse didn't start (never restarts one).
                    if (st.HoldSince == long.MinValue / 2) st.HoldSince = w.Now;
                    var active = w.Cs is { } c && (c.Bomb.Planter == member || c.Bomb.Defuser == member);
                    if (!active && w.Now - st.HoldSince >= 400 && (!st.Using || w.Now - st.UseSentAt > 1000))
                    { st.Using = true; st.UseSentAt = w.Now; actions.Add(new BotAction(member, "use", new() { ["held"] = true })); }
                    orders.Add(Order("hold", null, null, sightList, role: o.Role));
                    continue;
                }
                st.HoldSince = long.MinValue / 2;
                if (st.Using) { st.Using = false; actions.Add(new BotAction(member, "use", new() { ["held"] = false })); }
                // Low on health, not the one with the bomb: back to the nearest teammate, watching the way.
                if (csSelf is { Health: < LowHealth } && w.Cs?.Bomb.Carrier != member && NearestMate(w, self, teamKey, TeamKey, eye) is { } mate)
                {
                    orders.Add(Order("goal", mate, watch, sightList, stop: 0.85, role: "fall back"));
                    continue;
                }
                if (o.Goal is null) { orders.Add(Order("hold", null, watch ?? o.Face, sightList, role: o.Role)); continue; }
                // Near its spot: hold the angle it is given (or the enemy spawn's way); a fresh sound wins.
                var near = Dist2(eye, o.Goal) < HoldAngleCm;
                var face = watch ?? (near ? o.Face ?? (csSelf is not null ? HoldAngle(w, csSelf.Side, eye, o.Goal) : null) : null);
                orders.Add(Order("goal", o.Goal, face, sightList, o.Stop, o.Via, role: o.Role));
                continue;
            }
            st.HoldSince = long.MinValue / 2;
            // Lost sight: go where the enemy was last seen (or heard) for a while, else roam.
            if (st.LastSeen is { } last && w.Now - st.LastSeenAt < ChaseMs)
                orders.Add(Order("goal", last, null, sightList, role: "chase"));
            else if (watch is not null)
                orders.Add(Order("goal", watch, watch, sightList, stop: 0.8, role: "hunt"));
            else
                orders.Add(Order("roam", null, null, sightList, role: "roam"));
        }
        return new BotStep(orders, shots, actions);
    }

    // What each side's bots learn this step: an enemy one of them sees, running footsteps and
    // gunfire within earshot of one of them.
    void Perceive(BotWorld w, Func<BotPlayer, string> teamKey)
    {
        var shooters = w.Events.Where(e => e.Kind == "damage" && e.Attacker is not null && w.Now - e.T is >= 0 and < 600).Select(e => e.Attacker!).ToHashSet();
        foreach (var (member, _) in w.Bots)
        {
            if (w.Players.FirstOrDefault(p => p.Member == member) is not { Alive: true } me) continue;
            var key = teamKey(me);
            var known = KnowledgeOf(key);
            var sight = w.Sight.TryGetValue(member, out var s) && w.Now - s.At <= SightFreshMs ? s : null;
            foreach (var p in w.Players)
            {
                if (p.Member == member || !p.Alive || teamKey(p) == key) continue;
                var d = Math.Sqrt((p.X - me.X) * (p.X - me.X) + (p.Y - me.Y) * (p.Y - me.Y));
                if (sight is not null && sight.Visible.Contains(p.Tag)) known.Saw(p.Member, [p.X, p.Y, p.Z], w.Now);
                else if ((p.Speed > RunSpeed && d < HearStepsCm) || (shooters.Contains(p.Member) && d < HearShotsCm)) known.Saw(p.Member, [p.X, p.Y, p.Z], w.Now, heard: true);
            }
        }
    }

    // The sides' plans, made once per round when it goes live.
    void PlanRound(BotWorld w, CsView cs)
    {
        if (cs.Phase is not ("live" or "planted")) { if (cs.Phase == "freeze") { plans.Clear(); plantedAt = null; foreach (var k in knowledge.Values) k.Clear(); } return; }
        var key = matchKey ?? "";
        foreach (var side in new[] { CsRules.T, CsRules.CT })
        {
            if (plans.TryGetValue(side, out var plan) && plan.Round == cs.Round) continue;
            var bots = w.Bots.Where(b => cs.Players.Any(p => p.Member == b.Member && p.Side == side)).Select(b => b.Member).ToList();
            var sites = cs.Sites?.Count ?? 0;
            plans[side] = side == CsRules.T ? BotStrategy.PlanT(key, cs.Round, bots, sites, cs.LiveAt ?? w.Now, BotEconomy.TeamBuy(cs, side))
                : BotStrategy.PlanCT(key, cs.Round, bots, sites, cs.LiveAt ?? w.Now);
        }
        if (cs.Bomb.State == "planted") plantedAt ??= w.Now;
    }

    public const double HoldAngleCm = 600, PostPlantCm = 1300, RetakeGatherCm = 2200;
    public const long RetakeWaitMs = 6000;
    sealed record Job(double[]? Goal, bool Use, string Role, double Stop = 1, double[]? Via = null, double[]? Face = null);

    // Within HoldAngleCm of its spot, a bot looks toward the other side's spawn (the way the enemy
    // comes) at eye height; farther away it looks where it walks (null).
    internal static double[]? HoldAngle(BotWorld w, string side, double[] eye, double[] spot)
    {
        if (Math.Sqrt((eye[0] - spot[0]) * (eye[0] - spot[0]) + (eye[1] - spot[1]) * (eye[1] - spot[1])) > HoldAngleCm) return null;
        return SpawnOf(w, side == CsRules.T ? CsRules.CT : CsRules.T, eye[2]);
    }
    static double[]? SpawnOf(BotWorld w, string side, double z)
    {
        var spawns = w.Map?.SpawnsFor(side) ?? [];
        return spawns.Count == 0 ? null : [Math.Round(spawns.Average(s => s.X), 1), Math.Round(spawns.Average(s => s.Y), 1), Math.Round(z, 1)];
    }

    // A CS bot's job now: its plan's job, and the bomb's calls (plant, fetch, post-plant, retake, defuse).
    Job? CsJob(BotWorld w, CsView cs, CsPlayerView me, string member, double[] eye)
    {
        var sites = cs.Sites ?? [];
        if (sites.Count == 0) return null;
        var bomb = cs.Bomb;
        var enemySpawn = SpawnOf(w, me.Side == CsRules.T ? CsRules.CT : CsRules.T, eye[2]);
        if (me.Side == CsRules.T)
        {
            if (bomb.State == "planted" && bomb.Position is { Length: 3 } planted)
            {
                // Post-plant: around the bomb, watching the way the defence comes back.
                if (Dist2(eye, planted) < PostPlantCm) return new Job(null, false, "post-plant", Face: enemySpawn);
                return new Job([planted[0], planted[1], planted[2]], false, "post-plant", Stop: 0.85, Face: enemySpawn);
            }
            if (bomb.State == "dropped" && bomb.Position is { Length: 3 } dropped)
            {
                // The nearest Terrorist bot fetches it.
                var nearest = cs.Players.Where(p => p.Side == CsRules.T && p.Alive && w.Bots.Any(b => b.Member == p.Member))
                    .Select(p => (p.Member, D: w.Players.FirstOrDefault(x => x.Member == p.Member) is { } q ? Math.Sqrt((q.X - dropped[0]) * (q.X - dropped[0]) + (q.Y - dropped[1]) * (q.Y - dropped[1])) : double.MaxValue))
                    .OrderBy(x => x.D).FirstOrDefault();
                if (nearest.Member == member) return new Job([dropped[0], dropped[1], dropped[2]], false, "fetch");
            }
            if (bomb.Carrier == member && cs.Phase == "live")
            {
                var zone = w.Map?.BombSites.FirstOrDefault(z => z.Contains(eye[0], eye[1], eye[2]));
                if (zone is not null || bomb.Planter == member) return new Job(null, true, "plant");
            }
            var plan = plans.GetValueOrDefault(CsRules.T) ?? BotStrategy.PlanT(matchKey ?? "", cs.Round, [member], sites.Count, cs.LiveAt ?? w.Now, BuyKind.Full);
            var job = BotStrategy.TJob(plan, member, bomb.Carrier == member, w.Now, sites, enemySpawn);
            return job.Goal is null ? null : new Job(job.Goal, false, job.Role, job.Stop, job.Via is { } v ? [v[0], v[1], v[2], job.ViaStop] : null, job.Face);
        }
        if (bomb.State == "planted" && bomb.Position is { Length: 3 } b)
        {
            // Retake: gather short of the bomb, then go in together; the nearest defuses, the others cover.
            var near = Math.Sqrt((eye[0] - b[0]) * (eye[0] - b[0]) + (eye[1] - b[1]) * (eye[1] - b[1])) <= CsRules.DefuseRadiusCm * 0.7;
            if (near || bomb.Defuser == member) return new Job(null, true, "defuse");
            var cts = cs.Players.Where(p => p.Side == CsRules.CT && p.Alive).Select(p => w.Players.FirstOrDefault(x => x.Member == p.Member)).Where(p => p is not null).Select(p => p!).ToList();
            var gathered = cts.Count(p => Dist2([p.X, p.Y, p.Z], b) < RetakeGatherCm);
            var go = gathered >= Math.Min(2, cts.Count) || w.Now - (plantedAt ?? w.Now) > RetakeWaitMs;
            if (!go) return new Job([b[0], b[1], b[2]], false, "retake (gathering)", Stop: 0.7, Face: [b[0], b[1], eye[2]]);
            var defuser = cts.OrderBy(p => Dist2([p.X, p.Y, p.Z], b)).FirstOrDefault(p => w.Bots.Any(x => x.Member == p.Member));
            if (defuser?.Member == member) return new Job([b[0], b[1], b[2]], false, "retake (defuse)");
            return new Job([b[0], b[1], b[2]], false, "retake (cover)", Stop: 0.9, Face: enemySpawn);
        }
        var ctPlan = plans.GetValueOrDefault(CsRules.CT) ?? BotStrategy.PlanCT(matchKey ?? "", cs.Round, [member], sites.Count, cs.LiveAt ?? w.Now);
        // Rotation: where the side saw (or heard) Terrorists near a site, or the carrier.
        var alert = BotStrategy.AlertSite(KnowledgeOf(CsRules.CT).Fresh(w.Now).Select(k => k.At), sites);
        var ctJob = BotStrategy.CtJob(ctPlan, member, sites, enemySpawn, alert);
        return ctJob.Goal is null ? null : new Job(ctJob.Goal, false, ctJob.Role, ctJob.Stop, null, ctJob.Face);
    }

    static double[]? NearestMate(BotWorld w, BotPlayer self, string teamKey, Func<BotPlayer, string> keyOf, double[] eye) =>
        w.Players.Where(p => p.Member != self.Member && p.Alive && keyOf(p) == teamKey).OrderBy(p => Dist2(eye, [p.X, p.Y, p.Z])).Select(p => (double[]?)new[] { p.X, p.Y, p.Z }).FirstOrDefault();

    static double Dist2(double[] a, double[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]));

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

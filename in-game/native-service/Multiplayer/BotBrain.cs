namespace AimMod.InGame.Multiplayer;

// Host-run bots for the shooting modes (CS, deathmatch, team deathmatch). The host's game draws
// each bot as one of AimModSteam's walking avatars; this logic decides where it goes, what it
// buys, when it plants or defuses and when it shoots. A bot only shoots a player its own game
// reports in sight (a line trace from the bot's eye), after a reaction time, and whether a shot
// lands is a roll on its difficulty's aim (distance, target movement, its own movement, spray and
// blindness make it worse). Its hits go through the same host rules as a player's
// (CombatMatch.BotHit). Bots never post runs, never reach the Hub and can't join tournament lobbies.
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

// A player as the bots see them: eye position (world cm), team (0: free for all), alive, speed (cm/s),
// crouched (bots: their walker says so). Tag: the player's number in the match (what sight targets
// are reported by).
sealed record BotPlayer(string Member, int Tag, double X, double Y, double Z, int Team, bool Alive, double Speed, bool Crouched = false);
// What a bot's game reported: its avatar's capsule centre and yaw, and which tags it can see.
// Floor: the floor under it (the bridge traced it), when reported; Speed and Crouch: how its walker
// moves it (newer bridges: the "vel" row).
sealed record BotSight(long At, double X, double Y, double Z, double Yaw, IReadOnlySet<int> Visible, double? Floor = null, double? Speed = null, bool Crouch = false);
// Shots: every shot anyone fired lately (ShotLog), what the bots hear.
sealed record BotWorld(long Now, string Mode, IReadOnlyList<(string Member, string Skill)> Bots, IReadOnlyList<BotPlayer> Players,
    IReadOnlyDictionary<string, BotSight> Sight, CsView? Cs, MapObjectives? Map, IReadOnlyList<CombatEvent> Events, IReadOnlyList<GunShot>? Shots = null);

// Mode: roam (between waypoints), goal (walk to Goal and stay), hold (stand still). Face: look at
// that point. Sight: the targets to trace (tag, eye). Place: stand at PlaceAt once per token (a CS
// round's spawn, a respawn). Stop: walk only that fraction of the way to Goal; Via (x, y, z,
// fraction): a detour first; Fight: strafe that hard while holding in a fight (FightStyle: counter or
// ad); Turn: degrees per second it turns, Accel and Overshoot how (its skill). Gait (run, walk),
// Stance (stand, crouch) and PreAim: how it walks; Peek (jiggle, wide, crouch) at PeekAt: how it
// peeks the angle it holds; Avoid: circles to route around (x, y, z, radius, cost).
sealed record BotOrder(string Member, string Mode, double[]? Goal, double[]? Face, IReadOnlyList<(int Tag, double[] Eye)> Sight, string? PlaceToken, double[]? PlaceAt,
    double Stop = 1, double[]? Via = null, double Fight = 0, double Turn = 0, string? Role = null,
    string Gait = "run", string Stance = "stand", bool PreAim = false, string? Peek = null, double[]? PeekAt = null, string? FightStyle = null,
    double Accel = 0, double Overshoot = 0, IReadOnlyList<double[]>? Avoid = null);
sealed record BotShotDecision(string Bot, string Victim, bool Head, int Slot, double[] Dir);
sealed record BotAction(string Bot, string Action, Dictionary<string, object> Args);
// Every trigger pull, hit or miss (Shots are only the hits): when it fired (host clock), with which
// slot, from its eye, at whom. Everyone hears these (GunAudio.cs).
sealed record BotFire(string Bot, int Slot, long T, double[] From, string Target);
// A grenade the brain wants thrown now (BotGrenades carries it out if the bot has one): a flash to
// push a smoke or pop round a corner, a smoke on a choke. Timed: there when it goes off (a flash).
sealed record BotUtilityRequest(string Bot, string Kind, double[] Target, bool Timed, string Why);
// What the developer menu and the bot logs show: what each bot heard, which sources it took for
// decoys, its smoke call, how blind it is, the flash it looks away from and how it moves.
sealed record BotDebugInfo(string Bot, string Role, IReadOnlyList<string> Heard, IReadOnlyList<string> Decoys, string? Smoke, double Blind, string? Flash, string Move);
sealed record BotStep(IReadOnlyList<BotOrder> Orders, IReadOnlyList<BotShotDecision> Shots, IReadOnlyList<BotAction> Actions, IReadOnlyList<BotFire>? Fired = null,
    IReadOnlyList<BotUtilityRequest>? Utility = null, IReadOnlyList<BotDebugInfo>? Debug = null);

// The bots' decisions, one step for every bot (several times a second). The pieces it uses:
//  - BotSenses: sight after smoke, hearing (steps, gunfire, decoys), decoy judgement;
//  - TeamKnowledge (BotMemory): what the side saw and heard, shared between its bots, fading;
//  - BotTactics: smokes, flashes, blindness and how it moves;
//  - BotStrategy: the side's plan for the round and each bot's job in it (CS);
//  - BotEconomy: how the side buys;
//  - BotAim: an eased turn onto a target and when a shot can land.
// Perception is honest: a bot only shoots a player its own game's trace says it sees, and only
// hears footsteps and shots within earshot.
sealed class BotBrain(int seed = 0)
{
    public const double EyeAboveCentre = 64;
    public const int MaxSight = 6;
    public const long SightFreshMs = 700, ChaseMs = 4000;
    // Earshot for older callers: running footsteps and gunfire (cm, BotHearing has the real ranges);
    // a run outside CS is faster than this (cm/s).
    public const double HearStepsCm = 2200, HearShotsCm = 4500, RunSpeed = 330;
    public const double LowHealth = 30;
    public const long HeardReactMs = 2500;
    sealed class State
    {
        public string? Target; public long ReactUntil, NextShot; public int BurstShot;
        public double[]? LastSeen; public long LastSeenAt = long.MinValue / 2;
        public int BoughtRound = -1; public long BuyAt; public int BuyTries;
        public bool Using; public long UseSentAt = long.MinValue / 2; public int Held = -1;
        public long HoldSince = long.MinValue / 2;
        public readonly BotAimState Aim = new();
        public readonly List<BotHeard> Heard = [];
        public HashSet<int> Traced = [];
        public string? SmokeNote, FlashNote; public long SmokePushFlashAt = long.MinValue / 2; public long SmokeFlashFor = -1;
        public long PopFlashRound = -1; public long ChokeSmokeRound = -1;
        public double Blind;
    }
    readonly Random random = seed == 0 ? new Random() : new Random(seed);
    readonly Dictionary<string, State> states = new();
    readonly Dictionary<string, TeamKnowledge> knowledge = new(StringComparer.Ordinal); // per team (side or TDM team)
    readonly Dictionary<string, TeamPlan> plans = new(StringComparer.Ordinal);         // per side, this round
    string? matchKey;
    long? plantedAt;
    long heardUntil = long.MinValue / 2, lastShotId;
    int soundIds;
    // Lines for the service log: decoy verdicts and smoke calls as they change.
    public readonly List<string> Notes = [];

    public void Reset(string key) { if (matchKey != key) { matchKey = key; states.Clear(); knowledge.Clear(); plans.Clear(); plantedAt = null; heardUntil = long.MinValue / 2; lastShotId = 0; } }
    internal TeamPlan? PlanFor(string side) => plans.GetValueOrDefault(side);
    internal TeamKnowledge KnowledgeOf(string team) => knowledge.TryGetValue(team, out var k) ? k : knowledge[team] = new TeamKnowledge();

    public BotStep Step(BotWorld w)
    {
        var orders = new List<BotOrder>(); var shots = new List<BotShotDecision>(); var actions = new List<BotAction>(); var fired = new List<BotFire>();
        var utility = new List<BotUtilityRequest>(); var debug = new List<BotDebugInfo>();
        if (Notes.Count > 200) Notes.RemoveRange(0, Notes.Count - 200);
        foreach (var id in states.Keys.Where(id => w.Bots.All(b => b.Member != id)).ToArray()) states.Remove(id);
        foreach (var (member, _) in w.Bots) if (!states.ContainsKey(member)) states[member] = new State();
        string TeamKey(BotPlayer p) => w.Cs?.Players.FirstOrDefault(c => c.Member == p.Member)?.Side ?? (p.Team == 0 ? p.Member : "team" + p.Team);
        string? SkillOf(string member) => w.Bots.FirstOrDefault(b => b.Member == member).Skill;
        Perceive(w, TeamKey);
        if (w.Cs is { } csPlan) PlanRound(w, csPlan);
        var grenades = w.Cs?.Grenades;
        var smokes = grenades?.Where(g => g.State == "smoke").ToArray() ?? [];
        foreach (var (member, skillId) in w.Bots)
        {
            var st = states[member];
            var skill = BotSkills.For(skillId);
            var self = w.Players.FirstOrDefault(p => p.Member == member);
            var sight = w.Sight.TryGetValue(member, out var s) && w.Now - s.At <= SightFreshMs ? s : null;
            var eye = self is not null ? new[] { sight?.X ?? self.X, sight?.Y ?? self.Y, self.Z } : sight is not null ? new[] { sight.X, sight.Y, sight.Z + EyeAboveCentre } : null;
            var (placeToken, placeAt) = Place(w, member);
            var csSelf = w.Cs?.Players.FirstOrDefault(p => p.Member == member);
            var profile = BotAim.Profile(skill);
            var coin = BotStrategy.Hash(member + "#" + (w.Cs?.Round ?? 0)) % 1000 / 1000.0;
            // How it moves, its peek, what it routes around: set as the step goes, carried by every order.
            var move = new BotTactics.Movement("run", "stand", skill.Id != BotSkills.Easy, null);
            double[]? peekAt = null;
            IReadOnlyList<double[]> avoid = [];
            BotOrder Order(string mode, double[]? goal, double[]? face, IReadOnlyList<(int Tag, double[] Eye)> sights, double stop = 1, double[]? via = null, double fight = 0, string? role = null) =>
                new(member, mode, goal, face, sights, placeToken, placeAt, stop, via, fight, profile.Rate, role,
                    move.Gait, move.Stance, move.PreAim, mode == "hold" && peekAt is not null ? move.Peek : null, mode == "hold" ? peekAt : null,
                    fight > 0 ? BotAim.FightStyle(skill) : null, profile.Accel, profile.Overshoot, avoid.Count > 0 ? avoid : null);
            void Note(string role) => debug.Add(new BotDebugInfo(member, role, st.Heard.Where(h => w.Now - h.T < 6000).TakeLast(3).Select(h => Describe(h, eye)).ToArray(),
                DecoysOf(member), st.SmokeNote, Math.Round(st.Blind, 2), st.FlashNote, move.Gait + "/" + move.Stance + (move.Peek is { } pk && peekAt is not null ? ", " + pk + " peek" : "") + (avoid.Count > 0 ? ", avoiding " + avoid.Count : "")));
            if (self is null || !self.Alive || eye is null)
            {
                // Down: it stays where it fell (hidden) until it respawns or the next round places it.
                st.Target = null; st.Using = false; st.Heard.Clear(); st.Blind = 0; st.SmokeNote = st.FlashNote = null;
                orders.Add(Order(self is { Alive: false } ? "hold" : "roam", null, null, []));
                continue;
            }
            var team = self.Team;
            var teamKey = TeamKey(self);
            var known = KnowledgeOf(teamKey);
            bool Rejected(string key) => key.StartsWith("sound#", StringComparison.Ordinal) && int.TryParse(key.AsSpan(6), out var sid)
                && known.Sounds.FirstOrDefault(x => x.Id == sid) is { } src && BotEars.Rejects(src, member, skill.Id);
            var enemies = w.Players.Where(p => p.Member != member && p.Alive && (team == 0 || p.Team != team) && (w.Cs is null || TeamKey(p) != teamKey))
                .Select(p => (P: p, D: Distance(eye, p))).OrderBy(x => x.D).ToList();
            var sightList = enemies.Where(x => x.D <= skill.RangeCm * 1.25).Take(MaxSight).Select(x => (x.P.Tag, new[] { Math.Round(x.P.X, 1), Math.Round(x.P.Y, 1), Math.Round(x.P.Z, 1) })).ToArray();
            st.Traced = sightList.Select(x => x.Tag).ToHashSet();
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

            // Blindness: the host's flash on it. White: it sees nothing (its game's traces still say
            // who is in the open, so it can spray); half blind: only up close, slower and worse.
            var blind = BotTactics.Blindness(csSelf?.Flash, w.Now);
            if (blind > 0.05 && st.Blind <= 0.05) Notes.Add(member + " is blinded (" + blind.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ")");
            st.Blind = blind;
            var inOpen = enemies.Where(x => x.D <= skill.RangeCm && sight is not null && sight.Visible.Contains(x.P.Tag)).ToList();
            var visible = blind >= BotTactics.BlindOut ? [] : blind >= BotTactics.BlindHalf ? inOpen.Where(x => x.D <= BotTactics.HalfBlindSightCm).ToList() : inOpen;
            var current = visible.FirstOrDefault(x => x.P.Member == st.Target);
            var pick = current.P is not null ? current : visible.FirstOrDefault();
            if (pick.P is not null && pick.P.Member != st.Target)
            {
                st.Target = pick.P.Member;
                st.ReactUntil = w.Now + (long)(skill.ReactionMs * (0.8 + random.NextDouble() * 0.4) * (blind >= BotTactics.BlindHalf ? 1.8 : 1));
                st.BurstShot = 0; st.NextShot = Math.Max(st.NextShot, st.ReactUntil);
            }
            if (pick.P is null && blind < BotTactics.BlindOut) st.Target = null;
            // Where it was: its track as is (a camera, whatever its hull); the walker goes by the floor under it.
            else if (pick.P is not null) { st.LastSeen = [pick.P.X, pick.P.Y, pick.P.Z]; st.LastSeenAt = w.Now; }

            // The objective (CS) decides whether a bot keeps planting or defusing through a fight.
            var job = w.Cs is { } cs2 && csSelf is not null ? CsJob(w, cs2, csSelf, member, eye, skill.Id) : null;
            var busy = job is { Use: true } && w.Cs is { } cs3 && (
                (cs3.Bomb.Planter == member && cs3.Bomb.PlantDoneAt is { } pd && pd - w.Now < 1000) ||
                (cs3.Bomb.Defuser == member && cs3.Bomb.DefuseDoneAt is { } dd && (dd - w.Now < 1500 || (skill.Id == BotSkills.Hard && dd - w.Now < 3000))));

            // Flashes it knows are about to pop near it: look away as they go off.
            var yawNow = sight?.Yaw ?? st.Aim.Yaw;
            double[]? lookAway = null;
            st.FlashNote = null;
            foreach (var threat in BotTactics.FlashThreats(grenades, member, skill.Id, m => w.Players.FirstOrDefault(p => p.Member == m) is { } mp && TeamKey(mp) == teamKey && m != member || m == member, SkillOf,
                         yawNow, eye, w.Now, id => BotStrategy.Hash(member + "#flash#" + id) % 1000 / 1000.0, smokes))
                if (w.Now >= threat.PopAt - BotTactics.LookAwayLead(skill.Id) && w.Now <= threat.PopAt + BotTactics.LookAwayAfterMs)
                {
                    lookAway = BotTactics.AwayFrom(threat.Pop, eye);
                    st.FlashNote = "looking away (" + threat.Why + ")";
                    break;
                }
                else st.FlashNote ??= "flash coming (" + threat.Why + ")";

            // The nearest enemy it knows about, and what it last heard (and still believes).
            var fresh = known.Fresh(w.Now, Rejected).ToList();
            double? contact = fresh.Count == 0 ? null : fresh.Min(k => Dist2(eye, k.At));
            if (pick.P is not null) contact = Math.Min(contact ?? double.MaxValue, pick.D);
            var heard = st.Heard.LastOrDefault(h => w.Now - h.T <= HeardReactMs && !Rejected(h.Key));
            var watch = heard?.At is { } ha ? new[] { ha[0], ha[1], eye[2] }
                : fresh.Where(k => Dist2(eye, k.At) < HearShotsCm).OrderBy(k => Dist2(eye, k.At)).Select(k => (double[]?)new[] { k.At[0], k.At[1], k.At[2] }).FirstOrDefault();
            // Fire on the floor: everyone walks round it.
            avoid = BotTactics.Avoid(grenades, null, skill.Id, w.Now);

            // Aim: onto the target, else where it is told to look, else where its body faces.
            if (pick.P is not null && !busy && blind < BotTactics.BlindOut)
            {
                var target = pick.P;
                var head = new[] { target.X, target.Y, target.Z - 8 };
                var (yaw, pitch) = BotAim.Angles(eye, head);
                var error = BotAim.Turn(st.Aim, lookAway is null ? yaw : BotAim.Angles(eye, lookAway).Yaw, pitch, profile, dt);
                if (st.Using) { st.Using = false; actions.Add(new BotAction(member, "use", new() { ["held"] = false })); }
                move = move with { Gait = "run", Stance = skill.Id == BotSkills.Hard && pick.D > 2500 && coin < 0.3 ? "crouch" : "stand", Peek = null };
                orders.Add(Order("hold", null, lookAway ?? head, sightList, fight: BotAim.Strafe(skill), role: lookAway is null ? "fight" : "fight (looking away)"));
                // Counter-strafe: harder bots wait for the stop before the next shot; moving costs aim.
                if (lookAway is null && w.Now >= st.ReactUntil && BotAim.Ready(st.Aim, error, pick.D, w.Now, skill) && !BotAim.WaitsToStop(skill, sight?.Speed))
                    Fire(w, st, skill, member, slot, eye, target, pick.D, error, BotAim.Moving(sight?.Speed) * BotTactics.HalfBlindAim(blind), fired, shots, csSelf);
                Note(lookAway is null ? "fight" : "fight (looking away)");
                continue;
            }
            st.Aim.SettledSince = long.MinValue / 2;

            // White: spray where the enemy was (it hears them; its game traces them in the open), back
            // off towards its own side and turn away.
            if (blind >= BotTactics.BlindOut && !busy)
            {
                var sprayAt = inOpen.FirstOrDefault(x => x.P.Member == st.Target);
                if (sprayAt.P is not null && BotTactics.SprayShare(skill.Id) > 0 && w.Now - st.LastSeenAt < 2500 && w.Now >= st.ReactUntil)
                {
                    var (yaw, pitch) = BotAim.Angles(eye, [sprayAt.P.X, sprayAt.P.Y, sprayAt.P.Z - 8]);
                    var error = BotAim.Turn(st.Aim, yaw, pitch, profile, dt);
                    Fire(w, st, skill, member, slot, eye, sprayAt.P, sprayAt.D, error, BotTactics.SprayShare(skill.Id), fired, shots, csSelf);
                    orders.Add(Order("hold", null, [sprayAt.P.X, sprayAt.P.Y, sprayAt.P.Z - 8], sightList, fight: BotAim.Strafe(skill) * 0.5, role: "blind (spraying)"));
                    Note("blind (spraying)");
                    continue;
                }
                var from = st.LastSeen ?? watch;
                var away = from is not null ? BotTactics.AwayFrom(from, eye) : sight is not null ? new[] { eye[0] - Math.Cos(sight.Yaw * Math.PI / 180) * 600, eye[1] - Math.Sin(sight.Yaw * Math.PI / 180) * 600, eye[2] } : null;
                var back = away is not null ? new[] { eye[0] + (away[0] - eye[0]) * 0.6, eye[1] + (away[1] - eye[1]) * 0.6, eye[2] } : null;
                if (sight is not null) BotAim.Turn(st.Aim, sight.Yaw, 0, profile, dt);
                if (skill.Id == BotSkills.Easy) orders.Add(Order("hold", null, away, sightList, role: "blind"));
                else orders.Add(Order(back is null ? "hold" : "goal", back, away, sightList, role: "blind (backing off)"));
                Note(skill.Id == BotSkills.Easy ? "blind" : "blind (backing off)");
                continue;
            }
            if (sight is not null) BotAim.Turn(st.Aim, sight.Yaw, 0, profile, dt);

            // Where it heads now and what it watches (the job's, else a sound, else its chase).
            double[]? goal = null; double[]? face = null; double stop = 1; double[]? via = null; string role; var mode = "goal";
            var hurry = false; var holding = false;
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
                    move = move with { Gait = "run", Stance = "stand", Peek = null };
                    orders.Add(Order("hold", null, lookAway, sightList, role: o.Role));
                    Note(o.Role);
                    continue;
                }
                st.HoldSince = long.MinValue / 2;
                if (st.Using) { st.Using = false; actions.Add(new BotAction(member, "use", new() { ["held"] = false })); }
                role = o.Role;
                hurry = o.Role is "rotate" or "fetch" or "retake (defuse)" or "entry" or "split" || o.Role.StartsWith("retake", StringComparison.Ordinal) && o.Role != "retake (gathering)";
                // Low on health, not the one with the bomb: back to the nearest teammate, watching the way.
                if (csSelf is { Health: < LowHealth } && w.Cs?.Bomb.Carrier != member && NearestMate(w, self, teamKey, TeamKey, eye) is { } mate)
                {
                    goal = mate; face = watch; stop = 0.85; role = "fall back";
                }
                else if (o.Goal is null) { mode = "hold"; face = watch ?? o.Face; holding = true; }
                else
                {
                    // Near its spot: hold the angle it is given (or the enemy spawn's way); a fresh sound wins.
                    var near = Dist2(eye, o.Goal) < HoldAngleCm;
                    holding = near && o.Stop >= 0.999 && o.Via is null && !hurry;
                    goal = o.Goal; stop = o.Stop; via = o.Via;
                    face = watch ?? (near ? o.Face ?? (csSelf is not null ? HoldAngle(w, csSelf.Side, eye, o.Goal) : null) : null);
                }
            }
            else
            {
                st.HoldSince = long.MinValue / 2;
                // Lost sight: go where the enemy was last seen (or heard) for a while, else roam.
                if (st.LastSeen is { } last && w.Now - st.LastSeenAt < ChaseMs) { goal = last; role = "chase"; }
                else if (watch is not null) { goal = watch; face = watch; stop = 0.8; role = heard is not null ? "investigate" : "hunt"; }
                else { mode = "roam"; role = "roam"; }
            }

            // A smoke in the way (or around it): the call for this smoke.
            st.SmokeNote = null;
            if (w.Cs is { } cs4 && csSelf is not null && smokes.Length > 0 && !busy)
            {
                var insideOf = smokes.Where(x => x.Pos is not null && BotVision.Inside([x], eye, w.Now) > 0.3).FirstOrDefault();
                var inWay = BotTactics.InTheWay(smokes, eye, goal ?? face, w.Now) ?? (holding ? BotTactics.InTheWay(smokes, eye, face, w.Now) : null);
                var smoke = insideOf ?? inWay;
                if (smoke is not null)
                {
                    var left = (smoke.Ends ?? w.Now) - w.Now;
                    var pressure = csSelf.Side == CsRules.CT ? cs4.Bomb.State == "planted" && cs4.Bomb.ExplodesAt is { } ex && ex - w.Now < 20_000
                        : cs4.Bomb.State != "planted" && cs4.Phase == "live" && cs4.PhaseEndsAt - w.Now < 25_000;
                    var defending = csSelf.Side == CsRules.CT ? cs4.Bomb.State != "planted" : cs4.Bomb.State == "planted";
                    var hasFlash = csSelf.Grenades?.Contains(GrenadeRules.Flash) == true;
                    var mates = w.Players.Any(p => p.Member != member && p.Alive && TeamKey(p) == teamKey && Dist2(eye, [p.X, p.Y]) < 1500);
                    var choice = BotTactics.Decide(new SmokeSituation(insideOf is not null, inWay is not null || insideOf is not null && goal is not null && BotTactics.InTheWay([insideOf], eye, goal, w.Now) is not null,
                        left, hasFlash, pressure, defending, mates), skill.Id, BotStrategy.Hash(member + "#smoke" + smoke.Id) % 1000 / 1000.0);
                    var slotIndex = w.Bots.ToList().FindIndex(b => b.Member == member);
                    switch (choice)
                    {
                        case SmokeChoice.Leave:
                            goal = BotTactics.WayOut(smoke, eye); stop = 1; via = null; mode = "goal"; role = "leaving the smoke"; holding = false;
                            break;
                        case SmokeChoice.HoldEdge or SmokeChoice.WideSwing:
                        {
                            var (spot, edge) = BotTactics.EdgeHold(smoke, eye, slotIndex);
                            var thin = BotVision.Density(smoke, w.Now) < 0.75 || BotVision.Growth(smoke, w.Now) < 0.5;
                            if (choice == SmokeChoice.WideSwing && thin)
                            {
                                // It thins: swing out wide on its edge, then on.
                                mode = "hold"; goal = null; face = edge; peekAt = edge; move = move with { Peek = "wide" }; role = "wide swing (smoke fading)"; holding = true;
                            }
                            else
                            {
                                var there = Dist2(eye, spot) < 250;
                                mode = there ? "hold" : "goal"; goal = there ? null : spot; stop = 1; via = null; face = edge; holding = true;
                                role = choice == SmokeChoice.WideSwing ? "holding the smoke's edge (swing as it fades)" : "holding the smoke's edge";
                            }
                            break;
                        }
                        case SmokeChoice.Reroute:
                            avoid = BotTactics.Avoid(grenades, smoke, skill.Id, w.Now);
                            role += " (around the smoke)";
                            break;
                        case SmokeChoice.FlashPush:
                            if (st.SmokeFlashFor != smoke.Id && hasFlash)
                            {
                                st.SmokeFlashFor = smoke.Id; st.SmokePushFlashAt = w.Now + GrenadeRules.FlashFuseMs;
                                utility.Add(new BotUtilityRequest(member, GrenadeRules.Flash, BotTactics.FlashOver(smoke, eye), true, "flash through the smoke"));
                            }
                            if (w.Now < st.SmokePushFlashAt + 100)
                            {
                                // Until it pops: wait where it is, looking away from it.
                                mode = "hold"; goal = null; face = lookAway ?? BotTactics.AwayFrom(BotTactics.FlashOver(smoke, eye), eye); role = "flashing the smoke";
                            }
                            else role += " (pushing the smoke behind a flash)";
                            break;
                        case SmokeChoice.Push:
                            role += " (through the smoke)";
                            break;
                    }
                    var note = choice + (smoke.Pos is { } sp ? FormattableString.Invariant($" (smoke {Dist2(eye, sp) / 100:0} m, {left / 1000.0:0.#} s left)") : "");
                    if (choice != SmokeChoice.None)
                    {
                        LogOnce(member, "smoke", choice + "#" + smoke.Id, member + ": smoke call " + note);
                        st.SmokeNote = note;
                    }
                }
            }

            // Utility of its own: CTs smoke the choke the enemy comes through; hard bots pop-flash an
            // angle they are about to take.
            if (w.Cs is { } cs5 && csSelf is not null && cs5.Phase == "live" && csSelf.Grenades is { Count: > 0 } carried)
            {
                var near = fresh.Where(k => !k.Heard || TeamKnowledge.Confidence(k.T, k.Heard, w.Now) > 0.3).OrderBy(k => Dist2(eye, k.At)).FirstOrDefault();
                if (csSelf.Side == CsRules.CT && carried.Contains(GrenadeRules.Smoke) && skill.Id != BotSkills.Easy && st.ChokeSmokeRound != cs5.Round && near.At is { } en
                    && Dist2(eye, en) is > 900 and < 2600 && role is "anchor" or "rotate" or "holding the smoke's edge")
                {
                    st.ChokeSmokeRound = cs5.Round;
                    var spot = new[] { en[0] + (eye[0] - en[0]) * 0.3, en[1] + (eye[1] - en[1]) * 0.3, en[2] - GrenadeRules.EyeHeightCm };
                    utility.Add(new BotUtilityRequest(member, GrenadeRules.Smoke, spot, false, "smoke the choke"));
                    LogOnce(member, "choke", cs5.Round.ToString(System.Globalization.CultureInfo.InvariantCulture), member + ": smokes the choke the enemy comes through");
                }
                else if (skill.Id == BotSkills.Hard && carried.Contains(GrenadeRules.Flash) && st.PopFlashRound != cs5.Round && near.At is { } at2 && pick.P is null
                    && Dist2(eye, at2) is > 700 and < 2000 && (role is "entry" or "split" or "chase" or "investigate" || holding && watch is not null))
                {
                    st.PopFlashRound = cs5.Round;
                    var d = Math.Max(1, Dist2(eye, at2));
                    var k2 = Math.Min(700, d * 0.5) / d;
                    utility.Add(new BotUtilityRequest(member, GrenadeRules.Flash, [eye[0] + (at2[0] - eye[0]) * k2, eye[1] + (at2[1] - eye[1]) * k2, eye[2] + 220], true, "pop-flash"));
                }
            }

            // How it moves: silent near the enemy, crouched on a long hold, peeking a sound's angle.
            var peekIt = holding && watch is not null && pick.P is null && heard is not null;
            var decided = BotTactics.Move(skill.Id, contact, hurry, holding, peekIt, coin);
            move = move.Peek == "wide" ? decided with { Peek = "wide", Gait = "run" } : decided;
            if (peekIt && peekAt is null) peekAt = watch;
            if (holding && mode == "goal" && goal is not null && Dist2(eye, goal) < 200 && peekAt is not null) { mode = "hold"; goal = null; }
            face = lookAway ?? face;
            orders.Add(mode switch
            {
                "roam" => Order("roam", null, face, sightList, role: role),
                "hold" => Order("hold", null, face, sightList, role: role),
                _ => Order("goal", goal, face, sightList, stop, via, role: role),
            });
            Note(role);
        }
        return new BotStep(orders, shots, actions, fired, utility, debug);
    }

    // One step of shots at a target (up to three trigger pulls if the weapon allows): each a fire
    // decision everyone hears, a hit by chance (skill, distance, the target's speed, the spray, how
    // far off the crosshair is, and `share` for moving or blind).
    void Fire(BotWorld w, State st, BotSkill skill, string member, int slot, double[] eye, BotPlayer target, double distance, double error, double share, List<BotFire> fired, List<BotShotDecision> shots, CsPlayerView? csSelf)
    {
        var interval = WeaponInterval(w, csSelf) * skill.FireRateScale;
        for (var n = 0; n < 3 && st.NextShot <= w.Now; n++)
        {
            fired.Add(new BotFire(member, slot, Math.Max(st.NextShot, w.Now - 50), [eye[0], eye[1], eye[2]], target.Member));
            var p = HitChance(skill, distance, target.Speed, st.BurstShot) * BotAim.OnTarget(error, distance) * share;
            if (random.NextDouble() < p)
            {
                var d = Math.Max(1, distance);
                shots.Add(new BotShotDecision(member, target.Member, random.NextDouble() < skill.Head, slot, [(target.X - eye[0]) / d, (target.Y - eye[1]) / d, (target.Z - eye[2]) / d]));
            }
            st.BurstShot++;
            st.NextShot = Math.Max(st.NextShot, w.Now - 50) + (long)(interval * 1000);
            if (st.BurstShot >= skill.Burst) { st.BurstShot = 0; st.NextShot += (long)(skill.PauseMs * (0.8 + random.NextDouble() * 0.4)); }
        }
    }

    // Log lines once per change (decoy verdicts, smoke calls), for the service log.
    readonly Dictionary<string, string> logged = new(StringComparer.Ordinal);
    void LogOnce(string member, string what, string value, string line)
    {
        var key = member + "#" + what;
        if (logged.TryGetValue(key, out var had) && had == value) return;
        logged[key] = value;
        Notes.Add(line);
    }

    static string Describe(BotHeard h, double[]? eye) =>
        FormattableString.Invariant($"{h.Kind}{(h.Weapon is { } wpn ? " (" + wpn + ")" : "")} {h.Distance / 100:0} m{(h.Occluded ? " through a wall" : "")}");
    IReadOnlyList<string> DecoysOf(string member)
    {
        var list = new List<string>();
        foreach (var k in knowledge.Values)
            foreach (var s in k.Sounds)
                if (s.Verdicts.TryGetValue(member, out var v)) list.Add("#" + s.Id + " " + s.Weapon + ": " + v + (s.CalledBy is { } by && by != member ? " (called by " + by + ")" : ""));
        return list.TakeLast(3).ToArray();
    }

    // What each side's bots learn this step: an enemy one of them sees; running footsteps, gunfire
    // and a decoy's fake gunfire within earshot of one of them (where it seemed to come from); the
    // grenades they see thrown or hear land; and each bot's verdict on every source of gunfire.
    void Perceive(BotWorld w, Func<BotPlayer, string> teamKey)
    {
        var since = heardUntil == long.MinValue / 2 ? w.Now - 200 : heardUntil;
        heardUntil = w.Now;
        // This step's gunfire: everyone's real shots (new since the last step), and decoys' bursts.
        var sounds = new List<(string? Member, double[] At, long T, string Weapon)>();
        foreach (var shot in w.Shots ?? [])
        {
            if (shot.Id <= lastShotId) continue;
            if (GunSounds.ClassOf(shot.Weapon) is { } cls && cls != "knife") sounds.Add((shot.Member, shot.From, shot.T, cls));
        }
        if (w.Shots is { Count: > 0 } all) lastShotId = Math.Max(lastShotId, all[^1].Id);
        foreach (var g in w.Cs?.Grenades ?? [])
        {
            if (g.State != "decoy" || g.Pos is not { Length: 3 } at) continue;
            var cls = g.Weapon == "heavy" ? "shotgun" : g.Weapon ?? "pistol";
            foreach (var off in GrenadeRules.DecoyShots(g.Id, g.Weapon))
                if (g.At + off > since && g.At + off <= w.Now) sounds.Add((null, [at[0], at[1], at[2] + 40], g.At + off, cls));
        }
        // Who owns a decoy (its side doesn't fall for its own): the grenade's owner.
        string? OwnerSide(double[] at) => w.Cs?.Grenades?.Where(g => g.State == "decoy" && g.Pos is { } p && Math.Abs(p[0] - at[0]) < 1 && Math.Abs(p[1] - at[1]) < 1)
            .Select(g => w.Players.FirstOrDefault(p => p.Member == g.Owner)).Where(p => p is not null).Select(p => teamKey(p!)).FirstOrDefault();
        var enemiesAlive = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (member, skill) in w.Bots)
        {
            if (w.Players.FirstOrDefault(p => p.Member == member) is not { Alive: true } me) continue;
            var st = states[member];
            var key = teamKey(me);
            var known = KnowledgeOf(key);
            var sight = w.Sight.TryGetValue(member, out var s) && w.Now - s.At <= SightFreshMs ? s : null;
            var blind = BotTactics.Blindness(w.Cs?.Players.FirstOrDefault(p => p.Member == member)?.Flash, w.Now);
            var eye = new[] { me.X, me.Y, me.Z };
            foreach (var p in w.Players)
            {
                if (p.Member == member || !p.Alive || teamKey(p) == key) continue;
                var d = Math.Sqrt((p.X - me.X) * (p.X - me.X) + (p.Y - me.Y) * (p.Y - me.Y));
                if (sight is not null && sight.Visible.Contains(p.Tag) && blind < BotTactics.BlindOut) { known.Saw(p.Member, [p.X, p.Y, p.Z], w.Now); continue; }
                var gait = BotHearing.Gait(p.Speed, p.Crouched, w.Cs is not null);
                if (d < BotHearing.StepRange(gait, skill))
                {
                    var at = BotHearing.Place([p.X, p.Y, p.Z], d, false, skill, random);
                    known.Saw(p.Member, at, w.Now, heard: true);
                    Hear(st, new BotHeard("steps", at, w.Now, d, false, null, p.Member));
                }
            }
            // Gunfire: earshot by difficulty, shorter through a wall (its game's trace to the shooter said
            // so, or an untraced shooter round a corner).
            foreach (var snd in sounds)
            {
                var shooter = snd.Member is null ? null : w.Players.FirstOrDefault(p => p.Member == snd.Member);
                if (shooter is not null && (teamKey(shooter) == key || shooter.Member == member)) continue;
                if (snd.Member is null && OwnerSide(snd.At) == key) continue;
                var d = Dist2(eye, snd.At);
                var traced = shooter is not null && st.Traced.Contains(shooter.Tag);
                var occluded = traced ? sight is null || !sight.Visible.Contains(shooter!.Tag) : d > 1200;
                if (d > BotHearing.ShotRange(occluded, skill)) continue;
                var source = BotEars.Source(known, snd.Weapon, snd.At, snd.T, ref soundIds);
                source.Add(snd.T, snd.At);
                source.HeardBy.Add(member);
                var heardAt = BotHearing.Place(snd.At, d, occluded, skill, random);
                var skey = TeamKnowledge.SoundKey(source.Id);
                if (!BotEars.Rejects(source, member, skill)) known.Saw(skey, heardAt, snd.T, heard: true);
                Hear(st, new BotHeard("gunfire", heardAt, snd.T, d, occluded, snd.Weapon, skey));
            }
            // Grenades it sees thrown (in view) or hears land: where they come to rest.
            foreach (var g in w.Cs?.Grenades ?? [])
            {
                if (g.State != "flying" || known.Grenades.ContainsKey(g.Id)) continue;
                if (w.Players.FirstOrDefault(p => p.Member == g.Owner) is { } owner && teamKey(owner) == key) continue;
                var keys = GrenadePhysics.Unflat(g.Keys);
                if (keys.Count < 2) continue;
                var rest = new[] { keys[^1].X, keys[^1].Y, keys[^1].Z }; var restT = g.At + (long)keys[^1].T;
                var now = GrenadePhysics.At(keys, w.Now - g.At);
                var seen = sight is not null && blind < BotTactics.BlindOut && BotVision.InView(sight.Yaw, eye, now, 60, w.Cs?.Grenades, w.Now, 2600);
                var landed = w.Now >= restT && Dist2(eye, rest) < 1500;
                if (seen || landed) known.Grenades[g.Id] = (rest, restT);
            }
        }
        // Each bot's verdict on the sources it heard, from the side's shared cues.
        foreach (var (team, known) in knowledge)
        {
            known.Prune(w.Now);
            if (known.Sounds.Count == 0) continue;
            var enemySide = team == CsRules.T ? CsRules.CT : team == CsRules.CT ? CsRules.T : null;
            var alive = w.Players.Count(p => p.Alive && teamKey(p) != team);
            var ctx = new BotEars.Context(w.Events, w.Players, alive, BotEars.Plausible(w.Cs, enemySide), w.Now);
            foreach (var source in known.Sounds.Where(x => w.Now - x.LastT < 3000))
            {
                var (score, cues) = BotEars.Cues(source, known, ctx);
                source.Cues = cues;
                foreach (var bot in source.HeardBy)
                {
                    if (w.Bots.FirstOrDefault(b => b.Member == bot) is not { Member: not null } b) continue;
                    if (BotEars.Judge(source, bot, b.Skill, score, cues))
                        Notes.Add(bot + ": gunfire #" + source.Id + " (" + source.Weapon + ", " + source.Bursts.Count + " bursts) " + source.Verdicts[bot]);
                }
            }
        }
    }
    static void Hear(State st, BotHeard h)
    {
        // One line per source a step: the latest.
        st.Heard.RemoveAll(x => x.Key == h.Key && h.T - x.T < 400);
        st.Heard.Add(h);
        if (st.Heard.Count > 12) st.Heard.RemoveAt(0);
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
    Job? CsJob(BotWorld w, CsView cs, CsPlayerView me, string member, double[] eye, string skill)
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
        // Rotation: where the side saw (or heard, and believes) Terrorists near a site, or the carrier.
        var known = KnowledgeOf(CsRules.CT);
        var alert = BotStrategy.AlertSite(known.Fresh(w.Now, k => k.StartsWith("sound#", StringComparison.Ordinal) && int.TryParse(k.AsSpan(6), out var id)
            && known.Sounds.FirstOrDefault(x => x.Id == id) is { } src && BotEars.Rejects(src, member, skill)).Select(k => k.At), sites);
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

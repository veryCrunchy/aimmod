using System.Text.Json;
using Aim = AimMod.InGame.Multiplayer.BotAim;

namespace AimMod.InGame.Multiplayer;

// The bot AI overhaul: team plans (BotStrategy), buying (BotEconomy), aim (BotAim), what a side
// sees and hears, rotations, retakes and post-plant play, the orders the bridge gets, bots in the
// score modes (BotScorer), bot tracks and a dropped bomb, and bots in an online lobby (host and
// client).
static partial class MultiplayerChecks
{
    static void BotAi(string root)
    {
        BotStrategyChecks();
        BotEconomyChecks();
        BotAimChecks();
        BotTeamPlay();
        BotBombDrop();
        BotOrdersFile(root);
        BotScoreModes(root);
        BotsOnline(root);
        BotSenseChecks();
        BotHoldChecks();
        BotCalloutChecks();
    }

    static MapObjectives BotMap()
    {
        ObjectiveZone Box(string type, string team, string name, double x, double y) => new(type, team, name, [x - 300, y - 300, 0], [x + 300, y + 300, 300]);
        var zones = new[] { Box("bomb_site", "any", "A", 3000, 0), Box("bomb_site", "any", "B", -3000, 0), Box("buy_zone", "terrorist", "", 0, -3000), Box("buy_zone", "counter_terrorist", "", 0, 3000) };
        var spawns = Enumerable.Range(0, 5).Select(i => new ObjectiveSpawn("terrorist", i * 100, -3000, 40, 90))
            .Concat(Enumerable.Range(0, 5).Select(i => new ObjectiveSpawn("counter_terrorist", i * 100, 3000, 40, -90))).ToArray();
        return new MapObjectives(zones, spawns, MapObjectives.CsProblemOf(zones, spawns));
    }

    static void BotStrategyChecks()
    {
        string[] ts = ["t1", "t2", "t3", "t4", "t5"];
        var a = BotStrategy.PlanT("m-1", 3, ts, 2, 1000, BuyKind.Full);
        var b = BotStrategy.PlanT("m-1", 3, ts, 2, 1000, BuyKind.Full);
        Check(a == b || (a.Style == b.Style && a.Site == b.Site && a.ExecuteAt == b.ExecuteAt), "A round's Terrorist plan is the same every time (every machine agrees)");
        var styles = Enumerable.Range(1, 40).Select(r => BotStrategy.PlanT("m-1", r, ts, 2, 0, BuyKind.Full)).ToList();
        Check(styles.Select(p => p.Style).Distinct().Count() == 3 && styles.Select(p => p.Site).Distinct().Count() == 2, "Terrorists vary their play: rush, default and split, on both sites");
        Check(Enumerable.Range(1, 20).All(r => BotStrategy.PlanT("m-1", r, ts, 2, 0, BuyKind.Eco).Style == BotStyles.Rush), "On an eco they rush");
        var sites = new[] { new CsSiteView("A", 3000, 0, 60), new CsSiteView("B", -3000, 0, 60) };
        var def = new TeamPlan(CsRules.T, 2, BotStyles.Default, 0, 30_000, ts);
        var early = ts.Select(t => BotStrategy.TJob(def, t, t == "t1", 10_000, sites, [0, 3000, 100])).ToList();
        Check(early.All(j => j.Stop < 1) && early.Select(j => j.Goal![0]).Distinct().Count() == 2, "Default: before the call they take map control part of the way to both sites");
        var called = ts.Select(t => BotStrategy.TJob(def, t, t == "t1", 31_000, sites, null)).ToList();
        Check(called.Count(j => j.Stop == 1 && j.Goal![0] == 3000) == 4 && called[4] is { Role: "lurk", Stop: < 1 } lurk && lurk.Goal![0] == -3000,
            "At the call four go to the chosen site; the fifth lurks out towards the other one");
        var split = new TeamPlan(CsRules.T, 2, BotStyles.Split, 1, 0, ts);
        var splitJobs = ts.Select(t => BotStrategy.TJob(split, t, t == "t1", 1000, sites, null)).ToList();
        Check(splitJobs.All(j => j.Goal![0] == -3000) && splitJobs.Count(j => j.Via is { } v && v[0] == 3000) == 2 && splitJobs[0].Via is null, "Split: half come by way of the other site; the carrier goes straight in");
        // Counter-Terrorists: both sites held, one forward; a sighting near a site pulls the others over.
        string[] cts = ["c1", "c2", "c3", "c4", "c5"];
        var ct = new TeamPlan(CsRules.CT, 2, "hold", 0, 0, cts);
        var hold = cts.Select(c => BotStrategy.CtJob(ct, c, sites, [0, -3000, 100], null)).ToList();
        Check(hold.Count(j => j.Role == "anchor" && j.Goal![0] == 3000) == 2 && hold.Count(j => j.Role == "anchor" && j.Goal![0] == -3000) == 2 && hold.Count(j => j.Role == "rotator" && j.Stop < 1) == 1,
            "Counter-Terrorists anchor both sites (2-1-2) and one plays forward");
        var alert = BotStrategy.AlertSite([[-2900, 200, 0]], sites);
        Check(alert == 1 && BotStrategy.AlertSite([[0, 0, 0]], sites) is null, "A sighting close to a site raises that site");
        var full = BotStrategy.Alert([[-2900, 200, 0], [-2800, 100, 0], [-3100, -100, 0]], [], sites);
        Check(full is { Site: 1, Count: 3, From: "seen" }, "Three Terrorists seen near a site raise it as a full execute");
        var rotated = cts.Select(c => BotStrategy.CtJob(ct, c, sites, [0, -3000, 100], full)).ToList();
        Check(rotated.Count(j => j.Role == "rotate (retake setup)" && j.Goal![0] == -3000) == 2 && rotated.Count(j => j.Goal![0] == -3000) == 4 && rotated.Count(j => j.Goal![0] == 3000) == 1,
            "Rotation on a full execute: the forward one and all but one anchor of the other site go over, setting up to retake");
    }

    static void BotEconomyChecks()
    {
        var map = BotMap();
        var cs = new CsMatch(["t1", "t2", "c1", "c2"], 30_000_000, 12, true, map, new Dictionary<string, int> { ["t1"] = 1, ["t2"] = 1, ["c1"] = 2, ["c2"] = 2 });
        var v = cs.View();
        CsView Money(int round, int money) => v with { Round = round, Players = v.Players.Select(p => p with { Money = money }).ToArray() };
        Check(BotEconomy.TeamBuy(Money(1, 800), CsRules.T) == BuyKind.Pistol && BotEconomy.TeamBuy(Money(13, 800), CsRules.T) == BuyKind.Pistol, "Pistol rounds start both halves");
        Check(BotEconomy.TeamBuy(Money(4, 5000), CsRules.T) == BuyKind.Full && BotEconomy.TeamBuy(Money(4, 2500), CsRules.T) == BuyKind.Half && BotEconomy.TeamBuy(Money(4, 1200), CsRules.T) == BuyKind.Eco,
            "Full buy with the money for rifles and armour, half buy with less, eco with little");
        Check(BotEconomy.TeamBuy(Money(12, 1200), CsRules.CT) == BuyKind.Force, "The last round of a half is a force buy");
        var me = Money(4, 5000).Players.First(p => p.Member == "c1");
        Check(BotEconomy.BuyList(BuyKind.Full, me, BotSkills.For(BotSkills.Hard)).SequenceEqual(["m4a1s", "kevlar-helmet", "defuse-kit"]), "A full buy: rifle, armour and helmet, a kit for a Counter-Terrorist");
        Check(BotEconomy.BuyList(BuyKind.Eco, me, BotSkills.For(BotSkills.Hard)).Count == 0 && BotEconomy.BuyList(BuyKind.Half, me with { Money = 2500 }, BotSkills.For(BotSkills.Normal)).SequenceEqual(["mp9", "kevlar"]),
            "An eco saves; a half buy gets an SMG and armour");
    }

    static void BotAimChecks()
    {
        var hard = BotSkills.For(BotSkills.Hard); var easy = BotSkills.For(BotSkills.Easy);
        Check(Aim.TurnRate(hard) > Aim.TurnRate(easy) && Aim.SettleMs(hard) < Aim.SettleMs(easy) && Aim.Strafe(hard) > Aim.Strafe(easy), "Harder bots turn faster, settle sooner and strafe harder");
        var s = new BotAimState { Yaw = 0, Pitch = 0 };
        var profile = new BotTurnProfile(360, 100_000, 0);
        var left = Aim.Turn(s, 179, 0, profile, 0.1);
        Check(s.Yaw <= 36.01 && left >= 142.9, "Aim turns no faster than the turn rate");
        for (var i = 0; i < 10; i++) left = Aim.Turn(s, 179, 0, profile, 0.1);
        Check(left < 0.1 && Math.Abs(s.Yaw - 179) < 0.1, "and gets there");
        Check(Aim.OnTarget(0, 1000) == 1 && Aim.OnTarget(Aim.TargetAngle(1000) * 2, 1000) is > 0 and < 1 && Aim.OnTarget(20, 1000) == 0, "A shot's chance falls off as the crosshair leaves the body");
        var settle = new BotAimState();
        Check(!Aim.Ready(settle, 10, 1000, 0, hard), "Off target: no shot");
        Check(!Aim.Ready(settle, 0, 1000, 1000, hard) && Aim.Ready(settle, 0, 1000, 1000 + Aim.SettleMs(hard), hard), "On target it settles a moment before the first shot");
    }

    // A side's eyes and ears, rotations, post-plant and the retake.
    static void BotTeamPlay()
    {
        var map = BotMap();
        const long t0 = 40_000_000;
        var cs = new CsMatch(["t1", "c1", "c2"], t0, 12, true, map, new Dictionary<string, int> { ["t1"] = 1, ["c1"] = 2, ["c2"] = 2 });
        cs.Tick(t0 + CsRules.FreezeMs + 1);
        var brain = new BotBrain(seed: 11);
        brain.Reset("cs#team");
        var t = t0 + CsRules.FreezeMs + 500;
        BotWorld World(BotPlayer[] players, Dictionary<string, BotSight> sight, CsView? view = null) =>
            new(t, LobbyModes.Cs, [("c1", BotSkills.Normal), ("c2", BotSkills.Normal)], players, sight, view ?? cs.View(), map, []);
        // A Terrorist runs past c1, out of its sight but within earshot (a CS run: 1100 cm/s ported).
        var players = new[] { new BotPlayer("t1", 0, -2900, 900, 164, 1, true, 1000), new BotPlayer("c1", 1, -2000, 1500, 164, 2, true, 0), new BotPlayer("c2", 2, 3000, 200, 164, 2, true, 0) };
        var sight = new Dictionary<string, BotSight> { ["c1"] = new(t, -2000, 1500, 100, 0, new HashSet<int>()), ["c2"] = new(t, 3000, 200, 100, 0, new HashSet<int>()) };
        var heard = brain.Step(World(players, sight));
        Check(brain.KnowledgeOf(CsRules.CT).Fresh(t).Any(k => k.Enemy == "t1" && k.Heard), "A bot hears running footsteps within earshot, and its side knows");
        var c2 = heard.Orders.First(o => o.Member == "c2");
        Check(c2.Goal is { } g && Math.Abs(g[0] + 3000) < 1600 && Math.Abs(g[1]) < 1600 && c2.Role!.StartsWith("rotate", StringComparison.Ordinal), "The anchor on the other site rotates to where the Terrorist was heard (" + c2.Role + ")");
        var c1 = heard.Orders.First(o => o.Member == "c1");
        Check(c1.Face is { } f && Math.Abs(f[0] + 2900) < 250 && Math.Abs(f[1] - 900) < 250, "The bot that heard it watches that way (about where it was: ears are not exact)");
        Check(heard.Orders.All(o => o.Turn == Aim.TurnRate(BotSkills.For(BotSkills.Normal))), "Every order carries the bot's turn rate");
        // Planted at B: the defenders gather short of it, then retake; the nearest defuses, the other covers.
        var planted = cs.View() with { Phase = "planted", Bomb = cs.View().Bomb with { State = "planted", Position = [-3000, 0, 40], Carrier = null } };
        var far = new[] { players[0] with { Alive = false }, players[1] with { X = 0, Y = 3000 }, players[2] with { X = 500, Y = 3000 } };
        t += 100;
        var gather = brain.Step(World(far, sight, planted));
        Check(gather.Orders.All(o => o.Role == "retake (gathering)" && o.Stop < 1), "With the bomb down, the defenders gather short of it first");
        t += BotBrain.RetakeWaitMs + 100;
        var retake = brain.Step(World(far, sight, planted));
        Check(retake.Orders.Count(o => o.Role == "retake (defuse)") == 1 && retake.Orders.Count(o => o.Role == "retake (cover)") == 1, "Then they go in: one defuses, the other covers");
        // The Terrorists after the plant: around the bomb, facing the way the defence comes back.
        var tBrain = new BotBrain(seed: 12);
        tBrain.Reset("cs#post");
        var tWorld = new BotWorld(t, LobbyModes.Cs, [("t1", BotSkills.Normal)], [new BotPlayer("t1", 0, -2800, 300, 164, 1, true, 0)],
            new Dictionary<string, BotSight> { ["t1"] = new(t, -2800, 300, 100, 0, new HashSet<int>()) }, planted, map, []);
        var post = tBrain.Step(tWorld).Orders.Single();
        Check(post.Role!.StartsWith("post-plant", StringComparison.Ordinal) && post.Goal is { } pg && Math.Sqrt((pg[0] + 3000) * (pg[0] + 3000) + pg[1] * pg[1]) > 400,
            "Post-plant without an area yet: the Terrorist takes a spot off the bomb, not on top of it");
        // Low on health and nothing in sight: back to a teammate.
        var hurt = cs.View() with { Players = cs.View().Players.Select(p => p.Member == "c1" ? p with { Health = 12 } : p).ToArray() };
        var fallBack = new BotBrain(seed: 13);
        fallBack.Reset("cs#hurt");
        var back = fallBack.Step(World(players, sight, hurt)).Orders.First(o => o.Member == "c1");
        Check(back.Role == "fall back" && back.Goal is { } mate && mate[0] == 3000, "A bot low on health falls back to its nearest teammate");
    }

    static void BotBombDrop()
    {
        var map = BotMap();
        const long t0 = 50_000_000;
        var cs = new CsMatch(["t1", "c1"], t0, 12, true, map, new Dictionary<string, int> { ["t1"] = 1, ["c1"] = 2 });
        cs.Tick(t0 + CsRules.FreezeMs + 1);
        var carrier = cs.View().Bomb.Carrier!;
        // No track at all (a bot its game never reported): it drops at its spawn, not nowhere.
        cs.Leave(carrier, t0 + CsRules.FreezeMs + 100);
        cs.Tick(t0 + CsRules.FreezeMs + 200);
        var spawn = cs.View().Spawns![carrier];
        Check(cs.View().Bomb is { State: "dropped", Position: { } at } && at[0] == spawn[0] && Math.Abs(at[2] - spawn[2]) < 0.01, "A carrier with no track drops the bomb at their spawn point (a hull's centre)");
        // With a track, the bomb lands at the carrier's capsule centre by its own hull (a CS body: the
        // camera 167 cm above the centre), not a fixed 64 cm under the camera.
        var cs2 = new CsMatch(["t1", "c1"], t0, 12, true, map, new Dictionary<string, int> { ["t1"] = 1, ["c1"] = 2 });
        var live = t0 + CsRules.FreezeMs + 1;
        cs2.Tick(live);
        var carrier2 = cs2.View().Bomb.Carrier!;
        cs2.Combat.Track(carrier2, new TrackBatch("m", 1, [new TrackSample(live + 50, 100, -3000, 312, 0, 0)], [], [167, 60, 145]));
        cs2.Leave(carrier2, live + 60);
        Check(cs2.View().Bomb is { State: "dropped", Position: { } drop } && Math.Abs(drop[2] - (312 - 167)) < 0.01, "A dropped bomb lies at the carrier's own hull centre");
        // A bot remembers where it last saw an enemy as that enemy's track (whatever its hull).
        var memory = new BotBrain(seed: 21);
        memory.Reset("m#mem");
        var seenAt = new BotPlayer("you", 1, 800, 0, 312, 2, true, 0);
        BotWorld W(long now, bool visible) => new(now, LobbyModes.TeamDeathmatch, [("bot", BotSkills.Hard)], [new("bot", 0, 0, 0, 312, 1, true, 0), seenAt],
            new Dictionary<string, BotSight> { ["bot"] = new(now, 0, 0, 145, 0, visible ? new HashSet<int> { 1 } : new HashSet<int>(), 0) }, null, null, []);
        memory.Step(W(1000, true));
        var chase = memory.Step(W(1100, false)).Orders.Single();
        Check(chase.Mode == "goal" && chase.Goal is { } cg && cg[2] == 312, "A bot's last-seen memory keeps the enemy's track height, no fixed eye offset");
        // A bot's track follows a player's convention: floor plus a standing player's camera height.
        Check(Math.Abs(MultiplayerService.DefaultBotEyeHeight - (TrackingRound.DefaultHalfHeight + TrackingRound.DefaultEyeAboveCentre)) < 0.01, "A bot's camera height defaults to a standing body's");
        var sight = MultiplayerService.ParseBotSight("AIMMOD_BOTSIGHT_1\t1000\nbot\t1\t10\t20\t145\t90\t0\nbot\t2\t5\t5\t100\t0\n", 1000);
        Check(sight is { } s && s["1"].Floor == 0 && s["2"].Floor is null, "bot-sight.tsv carries the floor under each bot (older bridges don't)");
    }

    // What the bridge is told: stop short, detours, fight strafing, turn rates and the debug overlay.
    static void BotOrdersFile(string root)
    {
        long now = 41_000_000;
        var game = Path.Combine(root, "game");
        var output = Path.Combine(root, "bot-ai-output");
        Directory.CreateDirectory(output);
        var service = new MultiplayerService(new OfflineTransport(), new ContentLibrary(game), new FakeGame("load", "start") { Root = game }, () => new LocalRun(false, null, null, null, null, 0, 0, 0, null), () => [], () => null, output, simulation: false, () => now, autoTick: false, seed: 29);
        void Run(int ms) { for (var t = 0; t < ms; t += 100) { now += 100; service.Tick(); } }
        service.Act("create", J(new { mode = "deathmatch", scenario = "Synthetic A" }));
        service.Act("add-bot", J(new { skill = "hard" }));
        Run(3000);
        service.Act("start", default);
        var file = Path.Combine(output, MultiplayerService.BotOrdersFile);
        for (var i = 0; i < 300 && !File.Exists(file); i++) Run(100);
        // Into the live match (a track before the countdown ends is no part of it).
        for (var i = 0; i < 200 && JsonSerializer.SerializeToElement(service.View(), Protocol.Json).GetProperty("lobby").GetProperty("match").GetProperty("phase").GetString() != MatchPhases.Live; i++) Run(100);
        service.SetBotDebug(true);
        Run(1200);
        var text = File.Exists(file) ? File.ReadAllText(file) : "";
        Check(text.Contains("\ndebug\t1\n", StringComparison.Ordinal) && text.Contains("\nturn\t1\t720\n", StringComparison.Ordinal), "bot-orders.tsv carries the debug switch and the bot's turn rate (hard: 720 degrees a second)");
        Check(text.Contains("\naim\t1\t5200\t0.05\n", StringComparison.Ordinal) && text.Contains("\nmove\t1\trun\tstand\t1\n", StringComparison.Ordinal),
            "and how it turns (eased: 5200 degrees a second squared, 5 % overshoot) and walks (a hard bot pre-aims corners)");
        // Its game reports the bot standing on a floor: its track (for hits, the bomb) is that floor plus a camera height.
        File.WriteAllText(Path.Combine(output, MultiplayerService.BotSightFile), "AIMMOD_BOTSIGHT_1\t" + now + "\nbot\t1\t300\t400\t145\t0\t0\n");
        Run(300);
        var eye = service.BotTrackForTest(service.StandIns.Keys.Single());
        Check(eye is { } e && Math.Abs(e.Z - MultiplayerService.DefaultBotEyeHeight) < 0.01 && e.X == 300, "A bot's track is its floor plus a standing camera height, from its game's report");
    }

    // Score modes: a bot plays the scenario on the host and finishes with a believable score.
    static void BotScoreModes(string root)
    {
        Check(BotScorer.At("bot-a", BotSkills.Hard, "r", 6000, 60, 60).Score > BotScorer.At("bot-a", BotSkills.Easy, "r", 6000, 60, 60).Score, "A harder bot scores more");
        var run = Enumerable.Range(0, 61).Select(s => BotScorer.At("bot-a", BotSkills.Normal, "r", 6000, 60, s).Score).ToList();
        Check(run.Zip(run.Skip(1)).All(p => p.Second >= p.First - 1) && run[^1] is > 6000 * 0.7 and < 6000 * 1.0, "A bot's score climbs through the run to a believable total");
        Check(BotScorer.At("bot-a", BotSkills.Normal, "r", 6000, 60, 60).Score != BotScorer.At("bot-b", BotSkills.Normal, "r", 6000, 60, 60).Score, "Two bots of the same difficulty don't tie");
        long now = 42_000_000;
        var game = Path.Combine(root, "game");
        var output = Path.Combine(root, "bot-score-output");
        Directory.CreateDirectory(output);
        var service = new MultiplayerService(new OfflineTransport(), new ContentLibrary(game), new FakeGame("load", "start") { Root = game }, () => new LocalRun(false, null, null, null, null, 0, 0, 0, null), () => [], () => null, output, simulation: false, () => now, autoTick: false, seed: 31);
        void Run(int ms) { for (var t = 0; t < ms; t += 100) { now += 100; service.Tick(); } }
        service.Act("create", J(new { mode = "score-race", scenario = "Synthetic A" }));
        Check(service.Act("add-bot", J(new { skill = "normal" })).Ok, "A bot joins a score race");
        Run(3000);
        Check(service.Act("start", default).Ok, "A score race with a bot starts");
        JsonElement Lobby() => JsonSerializer.SerializeToElement(service.View(), Protocol.Json).GetProperty("lobby");
        string BotId() => Lobby().GetProperty("members").EnumerateArray().First(m => m.TryGetProperty("bot", out var b) && b.ValueKind == JsonValueKind.String).GetProperty("id").GetString()!;
        var bot = BotId();
        double? Score() => Lobby().GetProperty("match") is { ValueKind: JsonValueKind.Object } m && m.GetProperty("live").EnumerateArray().FirstOrDefault(l => l.GetProperty("memberId").GetString() == bot) is { ValueKind: JsonValueKind.Object } line && line.GetProperty("score").ValueKind == JsonValueKind.Number ? line.GetProperty("score").GetDouble() : null;
        for (var i = 0; i < 400 && Score() is not > 0; i++) Run(100);
        Check(Score() is > 0, "The bot's score shows in the race while it plays");
        Check(service.StandIns.Count == 0, "In a score mode a bot is no avatar in your game");
    }

    // Online: the host runs its bots; a client draws them where the host has them, its hits on a
    // bot are applied by the host, and when the host leaves the next host runs the bots on.
    static void BotsOnline(string root)
    {
        long now = 43_000_000;
        var net = new MemoryNetwork();
        var game = Path.Combine(root, "game");
        var all = new List<MultiplayerService>();
        MultiplayerService Make(string id, out string output)
        {
            var t = new MemoryTransport(net, id); net.Peers[id] = t;
            output = Path.Combine(root, "bots-online", id); Directory.CreateDirectory(output);
            var s = new MultiplayerService(t, new ContentLibrary(game), new FakeGame("load", "start") { Root = game }, () => new LocalRun(false, null, null, null, null, 0, 0, 0, null), () => [], () => null, output, false, () => now, autoTick: false);
            all.Add(s); return s;
        }
        void Pump(int rounds = 10) { for (var i = 0; i < rounds; i++) { now += 100; foreach (var s in all) s.Tick(); } }
        var host = Make("76561190000000301", out var hostOut);
        var client = Make("76561190000000302", out var clientOut);
        host.Act("create", J(new { mode = "deathmatch", scenario = "Synthetic A" }));
        client.Act("join", J(new { code = net.Codes.Single().Key }));
        Pump();
        host.Act("add-bot", J(new { skill = "normal" }));
        Pump(30);
        client.Act("ready", J(new { ready = true }));
        Pump(30);
        Check(host.Act("start", default).Ok, "An online deathmatch with a bot starts " + host.Act("start", default).Message);
        JsonElement Lobby(MultiplayerService s) => JsonSerializer.SerializeToElement(s.View(), Protocol.Json).GetProperty("lobby");
        string Phase(MultiplayerService s) => Lobby(s).GetProperty("match") is { ValueKind: JsonValueKind.Object } m ? m.GetProperty("phase").GetString()! : "";
        for (var i = 0; i < 300 && (Phase(host) != MatchPhases.Live || Phase(client) != MatchPhases.Live); i++) Pump(1);
        Check(Phase(host) == MatchPhases.Live && Phase(client) == MatchPhases.Live, "The match goes live on both machines");
        var botId = Lobby(client).GetProperty("members").EnumerateArray().First(m => m.TryGetProperty("bot", out var b) && b.ValueKind == JsonValueKind.String).GetProperty("id").GetString()!;
        var hostPeer = host.StandIns.GetValueOrDefault(botId); var clientPeer = client.StandIns.GetValueOrDefault(botId);
        Check(hostPeer is not null && hostPeer == clientPeer, "Host and client know the bot by the same stand-in");
        // The host's game walks the bot: the client is told where, and draws it there.
        for (var i = 0; i < 12; i++)
        {
            File.WriteAllText(Path.Combine(hostOut, MultiplayerService.BotSightFile), "AIMMOD_BOTSIGHT_1\t" + now + "\nbot\t" + hostPeer + "\t300\t" + (400 + i) + "\t145\t0\t0\n");
            Pump(2);
        }
        var clientOrders = File.Exists(Path.Combine(clientOut, MultiplayerService.BotOrdersFile)) ? File.ReadAllText(Path.Combine(clientOut, MultiplayerService.BotOrdersFile)) : "";
        Check(clientOrders.Contains("\npose\t" + hostPeer + "\t300\t", StringComparison.Ordinal) && !clientOrders.Contains("\nbot\t" + hostPeer + "\t", StringComparison.Ordinal),
            "The client draws the host's bot where the host's game has it (no bot logic of its own)");
        // The client hits the bot: the claim goes to the host, which applies it against the bot's track.
        var health = host.BotHealthForTest(botId);
        Check(client.ClaimForTest(botId) && Pump2(() => Pump(3)) && host.BotHealthForTest(botId) < health, "A client's hit on a bot is applied by the host");
        // Gunfire: the host's bot shots reach the client at once and play there; a client's own shots go
        // to the host, which passes them on (and never plays a player their own shots).
        var heardBefore = client.RoundAudio.Played;
        host.BotFireForTest(botId);
        Pump(2);
        Check(client.ShotsForTest.Recent.Any(s => s.Member == botId && s.Weapon == "rifle" && s.From is [300, 400, 160]) && client.RoundAudio.Played > heardBefore,
            "The host's bot shot reaches the client and plays there");
        var hostHeard = host.RoundAudio.Played;
        client.FiredForTest(now);
        Pump(2);
        Check(host.ShotsForTest.Recent.Any(s => s.Member == "76561190000000302" && s.From is [10, 20, 160]) && client.ShotsForTest.Recent.Any(s => s.Member == "76561190000000302") && host.RoundAudio.Played > hostHeard,
            "A client's own shot goes to the host, which plays it and passes it on");
        // The host leaves: the client takes over and runs the bot itself.
        host.Act("leave", default);
        all.Remove(host);
        for (var i = 0; i < 30 && !Lobby(client).GetProperty("isHost").GetBoolean(); i++) Pump(2);
        Pump(20);
        var after = File.Exists(Path.Combine(clientOut, MultiplayerService.BotOrdersFile)) ? File.ReadAllText(Path.Combine(clientOut, MultiplayerService.BotOrdersFile)) : "";
        Check(Lobby(client).GetProperty("isHost").GetBoolean() && Lobby(client).GetProperty("members").EnumerateArray().Any(m => m.GetProperty("id").GetString() == botId) && after.Contains("\nbot\t" + clientPeer + "\t", StringComparison.Ordinal),
            "When the host leaves, the new host keeps the bot and runs it");
    }
    static bool Pump2(Action a) { a(); return true; }
}

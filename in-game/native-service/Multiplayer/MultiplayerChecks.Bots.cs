using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

// Bots: the host adds them, they're always ready, load at once, never leave by timeout, are
// dropped outside the shooting modes, aim by difficulty after a reaction time, shoot only what
// their game reports in sight, buy, plant and defuse, and are marked as bots everywhere.
static partial class MultiplayerChecks
{
    static void Bots(string root)
    {
        BotLobby();
        BotAim();
        BotCs();
        BotLooks();
        DeadSpectate();
        BotService(root);
        BotAi(root);
    }

    // Each bot wears its own look, picked from its id: stable for the match, varied in a lobby.
    static void BotLooks()
    {
        Check(AvatarProfiles.ForBot("bot-1a2b3c4d").Id == AvatarProfiles.ForBot("bot-1a2b3c4d").Id, "A bot's look follows from its id");
        var ids = Enumerable.Range(0, 40).Select(i => "bot-" + i.ToString("x8", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Check(ids.Select(id => AvatarProfiles.ForBot(id).Id).Distinct().Count() >= 4, "Bot looks spread over the humanoid avatars");
        Check(AvatarProfiles.ForBot("bot-x", AvatarProfiles.All.Skip(1).Select(a => a.Id)).Id == AvatarProfiles.All[0].Id
            && AvatarProfiles.All.Any(a => a.Id == AvatarProfiles.ForBot("bot-x", AvatarProfiles.All.Select(x => x.Id)).Id), "A look other bots wear is skipped while another is free");
        long now = 21_000_000;
        var content = new FakeContent();
        var core = new LobbyCore("host", "Host", new LobbySettings(Mode: LobbyModes.Deathmatch, Scenario: content.Scenario("Synthetic A")), () => now, code: "ABCDEF");
        core.Apply("host", "add-bot", J(new { skill = "normal", fill = true }), content);
        var bots = core.Snapshot().Members.Where(m => m.Bot is not null).ToArray();
        Check(bots.Length >= 3 && bots.Select(b => b.Avatar).Distinct().Count() == Math.Min(bots.Length, AvatarProfiles.All.Length) && bots.All(b => AvatarProfiles.Find(b.Avatar) is not null),
            "Fill with bots: every bot looks different while looks last");
        var restored = LobbyCore.Restore(core.Snapshot(), "host", () => now);
        Check(bots.All(b => restored.Snapshot().Members.First(m => m.Id == b.Id).Avatar == b.Avatar), "A bot keeps its look through a host handover");
        // A downed bot stays put (hidden) instead of walking off invisibly.
        var brain = new BotBrain(seed: 4);
        brain.Reset("m#1");
        var down = brain.Step(new BotWorld(1_000_000, LobbyModes.Deathmatch, [("bot", BotSkills.Normal)], [new("bot", 0, 0, 0, 164, 0, false, 0)], new Dictionary<string, BotSight>(), null, null, []));
        Check(down.Orders[0].Mode == "hold", "A downed bot holds still");
    }

    // CS: down until the round is over, the camera follows living teammates (then anyone alive).
    static void DeadSpectate()
    {
        ObjectiveZone Box(string type, string team, string name, double x, double y) => new(type, team, name, [x - 300, y - 300, 0], [x + 300, y + 300, 300]);
        var zones = new[] { Box("bomb_site", "any", "A", 3000, 0), Box("bomb_site", "any", "B", -3000, 0), Box("buy_zone", "terrorist", "", 0, -3000), Box("buy_zone", "counter_terrorist", "", 0, 3000) };
        var spawns = Enumerable.Range(0, 5).Select(i => new ObjectiveSpawn("terrorist", i * 100, -3000, 40, 90))
            .Concat(Enumerable.Range(0, 5).Select(i => new ObjectiveSpawn("counter_terrorist", i * 100, 3000, 40, -90))).ToArray();
        var map = new MapObjectives(zones, spawns, MapObjectives.CsProblemOf(zones, spawns));
        string[] order = ["me", "mate1", "foe1", "mate2", "foe2"];
        var cs = new CsMatch(order, 30_000_000, 12, true, map, new Dictionary<string, int> { ["me"] = 1, ["mate1"] = 1, ["mate2"] = 1, ["foe1"] = 2, ["foe2"] = 2 });
        var view = cs.View();
        CsView With(params string[] dead) => view with { Players = view.Players.Select(p => dead.Contains(p.Member) ? p with { Alive = false } : p).ToArray() };
        var mates = MultiplayerService.DeadWatchCandidates(With("me"), order, "me");
        Check(mates.SequenceEqual(["mate1", "mate2"]), "A downed player watches living teammates, in match order");
        Check(MultiplayerService.DeadWatchCandidates(With("me", "mate1", "mate2"), order, "me").SequenceEqual(["foe1", "foe2"]), "With no teammate alive, anyone alive");
        Check(MultiplayerService.DeadWatchCandidates(With("me", "mate1", "mate2", "foe1", "foe2"), order, "me").Count == 0, "Nobody alive: nothing to watch");
        Check(MultiplayerService.NextDeadWatch(mates, null, 0) == "mate1" && MultiplayerService.NextDeadWatch(mates, "mate1", 0) == "mate1", "The first teammate, then the same one");
        Check(MultiplayerService.NextDeadWatch(mates, "mate1", 1) == "mate2" && MultiplayerService.NextDeadWatch(mates, "mate2", 1) == "mate1" && MultiplayerService.NextDeadWatch(mates, "mate1", -1) == "mate2",
            "Click or Space for the next player, right click for the previous one (wrapping)");
        var left = MultiplayerService.DeadWatchCandidates(With("me", "mate1"), order, "me");
        Check(MultiplayerService.NextDeadWatch(left, "mate1", 0) == "mate2", "The watched teammate goes down: the camera moves to the next one");
        var file = MultiplayerService.FormatSpectateView("3", 1_790_891_335_000);
        Check(file == "AIMMOD_VIEW_1\t1790891335000\nview\t3\n", "spectate-view.tsv names the avatar to follow (AimModSteam bridge::view)");
    }

    static void BotLobby()
    {
        long now = 20_000_000;
        var content = new FakeContent();
        var core = new LobbyCore("host", "Host", new LobbySettings(Mode: LobbyModes.Deathmatch, Scenario: content.Scenario("Synthetic A")), () => now, code: "ABCDEF");
        core.Join("guest", "Guest");
        Check(!core.Apply("guest", "add-bot", J(new { skill = "hard" }), content).Ok, "Only the host adds bots");
        Check(core.Apply("host", "add-bot", J(new { skill = "hard" }), content).Ok, "The host adds a bot");
        var bot = core.Snapshot().Members.First(m => m.Bot is not null);
        Check(bot.Bot == BotSkills.Hard && bot.Name.StartsWith("BOT ", StringComparison.Ordinal) && bot.Ready && bot.Scenario == ContentStates.Ok && bot.Map == ContentStates.Ok && !bot.Simulated,
            "A bot is named BOT ..., has its difficulty, the content and is ready");
        Check(core.Apply("host", "bot-skill", J(new { member = bot.Id, skill = "easy" }), content).Ok && core.Snapshot().Members.First(m => m.Id == bot.Id).Bot == BotSkills.Easy, "The host changes a bot's difficulty");
        Check(core.Apply("host", "add-bot", J(new { skill = "normal", fill = true }), content).Ok
            && core.Snapshot().Members.Count(m => m.Role == MemberRoles.Player) == core.Settings.MaxPlayers, "Fill with bots takes every free slot");
        Check(!core.Apply("host", "add-bot", default, content).Ok, "No bot joins a full lobby");
        // A changed play key resets readiness, but not a bot's.
        core.Apply("host", "settings", J(new { settings = new { fragLimit = 15 } }), content);
        Check(core.Snapshot().Members.Where(m => m.Bot is not null).All(m => m.Ready), "Bots stay ready when the host changes settings");
        // Score modes keep them (they play the scenario); the tracking duel has none.
        core.Apply("host", "settings", J(new { settings = new { mode = LobbyModes.Rounds } }), content);
        Check(core.Snapshot().Members.Any(m => m.Bot is not null), "Bots stay for a score mode");
        Check(!LobbyCore.BotsAllowed(new LobbySettings(Mode: LobbyModes.Tracking)) && LobbyModes.All.Where(m => m != LobbyModes.Tracking).All(m => LobbyCore.BotsAllowed(new LobbySettings(Mode: m))),
            "Bots play every mode but the tracking duel");
        foreach (var b in core.Snapshot().Members.Where(m => m.Bot is not null).ToArray()) core.Apply("host", "remove-bot", J(new { member = b.Id }), content);
        core.Apply("host", "settings", J(new { settings = new { mode = LobbyModes.Deathmatch } }), content);
        foreach (var who in new[] { "host", "guest" }) core.Apply(who, "content", J(new { scenario = ContentStates.Ok, map = ContentStates.Ok }), content);
        core.Apply("guest", "ready", J(new { ready = true }), content);
        core.Apply("host", "add-bot", J(new { skill = "hard" }), content);
        Check(LobbyRules.StartBlockers(core.Snapshot()).Count == 0, "A bot never blocks the start");
        var botId = core.Snapshot().Members.First(m => m.Bot is not null).Id;
        core.RequireLoading = true;
        Check(core.Apply("host", "start", default, content).Ok && core.Snapshot().Match?.Loaded?.Contains(botId) == true, "A bot loads at once");
        core.Apply("host", "loaded", default, content); core.Apply("guest", "loaded", default, content);
        for (var i = 0; i < 400 && core.Snapshot().Match?.Phase != MatchPhases.Live; i++) { now += 100; core.Tick(); }
        Check(core.Snapshot().Match?.Phase == MatchPhases.Live, "The match with a bot goes live");
        // Its hits go through the match's rules (no camera ray, the same damage and fire rate).
        var start = now;
        now += CombatRules.SpawnProtectionMs + 100; core.Tick();
        var applied = core.BotShot(botId, "guest", false, 0, [1, 0, 0]); Check(applied.Ok, "A bot's hit is applied " + applied.Code);
        var health = core.Snapshot().Match!.Combat!.Players.First(p => p.Member == "guest").Health;
        Check(health < 100, "A bot's hit does damage");
        Check(!core.BotShot(botId, "guest", false, 0, null).Ok, "A bot fires no faster than its weapon");
        Check(!core.BotShot("guest", "host", false, 0, null).Ok, "Only bots shoot this way");
        Check(core.Snapshot().Match!.Combat!.Events.Last(e => e.Kind == "damage").Dir is { Length: 3 }, "A bot's hit says where it came from (damage direction)");
        // A host handing over keeps its bots connected.
        var restored = LobbyCore.Restore(core.Snapshot(), "guest", () => now);
        Check(restored.Snapshot().Members.First(m => m.Id == botId) is { Bot: BotSkills.Hard, Connection: Connections.Connected }, "A new host keeps the bots, connected");
        _ = start;
    }

    static void BotAim()
    {
        var easy = BotSkills.For(BotSkills.Easy); var normal = BotSkills.For(BotSkills.Normal); var hard = BotSkills.For(BotSkills.Hard);
        Check(BotBrain.HitChance(easy, 800, 0, 0) < BotBrain.HitChance(normal, 800, 0, 0) && BotBrain.HitChance(normal, 800, 0, 0) < BotBrain.HitChance(hard, 800, 0, 0),
            "Harder bots aim better");
        Check(BotBrain.HitChance(hard, 3000, 0, 0) < BotBrain.HitChance(hard, 500, 0, 0) && BotBrain.HitChance(hard, 800, 300, 0) < BotBrain.HitChance(hard, 800, 0, 0)
            && BotBrain.HitChance(hard, 800, 0, 6) < BotBrain.HitChance(hard, 800, 0, 0), "Distance, a moving target and spraying make a bot miss more");
        Check(easy.ReactionMs > normal.ReactionMs && normal.ReactionMs > hard.ReactionMs, "Harder bots react faster");

        var brain = new BotBrain(seed: 5);
        brain.Reset("m#1");
        long t = 1_000_000;
        var players = new List<BotPlayer> { new("bot", 0, 0, 0, 164, 1, true, 0), new("you", 1, 800, 0, 164, 2, true, 0) };
        BotWorld World(bool visible) => new(t, LobbyModes.TeamDeathmatch, [("bot", BotSkills.Hard)], players,
            new Dictionary<string, BotSight> { ["bot"] = new(t, 0, 0, 100, 0, visible ? new HashSet<int> { 1 } : new HashSet<int>()) }, null, null, []);
        var hidden = brain.Step(World(false));
        Check(hidden.Shots.Count == 0 && hidden.Orders[0].Mode == "roam" && hidden.Orders[0].Sight.Any(s => s.Tag == 1), "A bot shoots nobody out of sight, but asks its game to trace to them");
        var first = brain.Step(World(true));
        Check(first.Shots.Count == 0 && first.Orders[0].Mode == "hold" && first.Orders[0].Face is { } face && face[0] == 800, "A bot that sees an enemy stops and aims, but waits its reaction time");
        var shots = 0;
        for (var i = 0; i < 30; i++) { t += 100; shots += brain.Step(World(true)).Shots.Count; }
        Check(shots >= 3, "After its reaction time a hard bot lands hits");
        // Teammates are never targets.
        players[1] = players[1] with { Team = 1 };
        var calm = 0;
        for (var i = 0; i < 20; i++) { t += 100; calm += brain.Step(World(true)).Shots.Count; }
        Check(calm == 0, "A bot never shoots its teammates");
        // Lost from sight: it goes where the enemy was last seen.
        players[1] = players[1] with { Team = 2 };
        brain.Step(World(true)); t += 100;
        var chase = brain.Step(World(false));
        Check(chase.Orders[0].Mode == "goal" && chase.Orders[0].Goal is { } g && g[0] == 800, "A bot that loses sight of an enemy goes where it last saw them");
        var sight = MultiplayerService.ParseBotSight("AIMMOD_BOTSIGHT_1\t" + t + "\nbot\t1\t10\t20\t30\t90\nseen\t1\t4\t1\nseen\t1\t5\t0\n", t + 50);
        Check(sight is { } s && s["1"].X == 10 && s["1"].Visible.Contains(4) && !s["1"].Visible.Contains(5), "Reads bot-sight.tsv");
        Check(MultiplayerService.ParseBotSight("AIMMOD_BOTSIGHT_1\t" + t + "\n", t + 60_000) is null, "A stale bot-sight.tsv says nothing");
    }

    static void BotCs()
    {
        ObjectiveZone Box(string type, string team, string name, double x, double y) => new(type, team, name, [x - 300, y - 300, 0], [x + 300, y + 300, 300]);
        var zones = new[] { Box("bomb_site", "any", "A", 3000, 0), Box("bomb_site", "any", "B", -3000, 0), Box("buy_zone", "terrorist", "", 0, -3000), Box("buy_zone", "counter_terrorist", "", 0, 3000) };
        var spawns = Enumerable.Range(0, 5).Select(i => new ObjectiveSpawn("terrorist", i * 100, -3000, 40, 90))
            .Concat(Enumerable.Range(0, 5).Select(i => new ObjectiveSpawn("counter_terrorist", i * 100, 3000, 40, -90))).ToArray();
        var map = new MapObjectives(zones, spawns, MapObjectives.CsProblemOf(zones, spawns));
        const long t0 = 30_000_000;
        var cs = new CsMatch(["tbot", "ctbot"], t0, 12, true, map, new Dictionary<string, int> { ["tbot"] = 1, ["ctbot"] = 2 });
        var brain = new BotBrain(seed: 9);
        brain.Reset("cs#1");
        long t = t0 + 3000;
        BotWorld World(double tx, double ty, double cx, double cy) => new(t, LobbyModes.Cs, [("tbot", BotSkills.Normal), ("ctbot", BotSkills.Normal)],
            [new("tbot", 0, tx, ty, 164, 1, true, 0), new("ctbot", 1, cx, cy, 164, 2, true, 0)],
            new Dictionary<string, BotSight> { ["tbot"] = new(t, tx, ty, 100, 0, new HashSet<int>()), ["ctbot"] = new(t, cx, cy, 100, 0, new HashSet<int>()) }, cs.View(), map, []);
        // Freeze time: they stand at their round's spawn and buy (pistol round: armour only).
        var freeze = brain.Step(World(0, -3000, 0, 3000));
        Check(freeze.Orders.All(o => o.Mode == "hold" && o.PlaceToken == "cs1" && o.PlaceAt is { Length: 4 }), "In freeze time bots stand at their round's spawn");
        t += 3000;
        var buys = brain.Step(World(0, -3000, 0, 3000)).Actions.Where(a => a.Action == "buy").Select(a => (a.Bot, Item: (string)a.Args["item"])).ToList();
        Check(buys.Contains(("tbot", "kevlar")) && buys.Contains(("ctbot", "kevlar")) && buys.All(b => b.Item == "kevlar"), "On the pistol round bots buy armour");
        // Live: the carrier walks to a site; inside it, it stands still, then holds the use key.
        cs.Tick(t0 + CsRules.FreezeMs + 1);
        t = t0 + CsRules.FreezeMs + 100;
        var live = brain.Step(World(0, -3000, 0, 3000));
        var tOrder = live.Orders.First(o => o.Member == "tbot");
        Check(cs.View().Bomb.Carrier == "tbot" && tOrder.Mode == "goal" && tOrder.Goal is { } site && Math.Abs(Math.Abs(site[0]) - 3000) < 1, "The bomb carrier walks to a bomb site");
        var siteX = tOrder.Goal![0];
        var ctGoal = live.Orders.First(o => o.Member == "ctbot").Goal;
        Check(ctGoal is { } cg && Math.Abs(Math.Abs(cg[0]) - 3000) < 1, "A Counter-Terrorist bot goes to guard a site");
        t += 100; brain.Step(World(siteX, 0, 0, 3000));
        t += 500;
        var plant = brain.Step(World(siteX, 0, 0, 3000));
        Check(plant.Orders.First(o => o.Member == "tbot").Mode == "hold" && plant.Actions.Any(a => a.Bot == "tbot" && a.Action == "use" && (bool)a.Args["held"]),
            "In the site the carrier stands still and plants");
        // CT bots split over the sites even when the bots' sides alternate (the live test sent every
        // bot to site A), and near its spot a bot holds the angle toward the enemy spawn.
        var mixed = new CsMatch(["ct1", "t1", "ct2", "t2"], t0, 12, true, map, new Dictionary<string, int> { ["ct1"] = 2, ["t1"] = 1, ["ct2"] = 2, ["t2"] = 1 });
        mixed.Tick(t0 + CsRules.FreezeMs + 1);
        var brain3 = new BotBrain(seed: 2);
        brain3.Reset("cs#3");
        var tm = t0 + CsRules.FreezeMs + 100;
        BotPlayer Pl(string id, int tag, double x, double y, int team) => new(id, tag, x, y, 164, team, true, 0);
        var who = new[] { Pl("ct1", 0, 0, 3000, 2), Pl("t1", 1, 0, -3000, 1), Pl("ct2", 2, 100, 3000, 2), Pl("t2", 3, 100, -3000, 1) };
        var split = brain3.Step(new BotWorld(tm, LobbyModes.Cs, [("ct1", BotSkills.Normal), ("t1", BotSkills.Normal), ("ct2", BotSkills.Normal), ("t2", BotSkills.Normal)], who,
            who.ToDictionary(p => p.Member, p => new BotSight(tm, p.X, p.Y, 100, 0, new HashSet<int>())), mixed.View(), map, []));
        var g1 = split.Orders.First(o => o.Member == "ct1").Goal; var g2 = split.Orders.First(o => o.Member == "ct2").Goal;
        Check(g1 is not null && g2 is not null && Math.Sign(g1[0]) != Math.Sign(g2[0]), "Counter-Terrorist bots split over both sites");
        var hold = BotBrain.HoldAngle(new BotWorld(tm, LobbyModes.Cs, [], [], new Dictionary<string, BotSight>(), mixed.View(), map, []), CsRules.CT, [3000, 0, 164], [3000, 0, 60]);
        Check(hold is { } h && Math.Abs(h[1] + 3000) < 1 && BotBrain.HoldAngle(new BotWorld(tm, LobbyModes.Cs, [], [], new Dictionary<string, BotSight>(), mixed.View(), map, []), CsRules.CT, [0, 3000, 164], [3000, 0, 60]) is null,
            "At its site a CT bot faces the Terrorist spawn; on the way it looks where it walks");
        // Economy: with money a bot buys a rifle and armour (round 3, $4,000).
        var view = cs.View();
        var rich = view with { Round = 3, Players = view.Players.Select(p => p with { Money = 4000 }).ToArray() };
        var brain2 = new BotBrain(seed: 3);
        brain2.Reset("cs#2");
        t = t0 + 100;
        var w2 = new BotWorld(t, LobbyModes.Cs, [("tbot", BotSkills.Hard)], [new("tbot", 0, 0, -3000, 164, 1, true, 0)], new Dictionary<string, BotSight>(), rich with { Phase = "freeze" }, map, []);
        brain2.Step(w2);
        var bought = brain2.Step(w2 with { Now = t + 3000 }).Actions.Where(a => a.Action == "buy").Select(a => (string)a.Args["item"]).ToList();
        Check(bought.Contains("ak47") && bought.Any(b => b.StartsWith("kevlar", StringComparison.Ordinal)), "With $4,000 a Terrorist bot buys an AK-47 and armour");
    }

    static void BotService(string root)
    {
        long now = 40_000_000;
        var game = Path.Combine(root, "game");
        var output = Path.Combine(root, "bots-output");
        Directory.CreateDirectory(output);
        var control = new FakeGame("load", "start") { Root = game };
        var service = new MultiplayerService(new OfflineTransport(), new ContentLibrary(game), control, () => new LocalRun(false, null, null, null, null, 0, 0, 0, null), () => [], () => null, output, simulation: false, () => now, autoTick: false, seed: 23);
        void Run(int ms) { for (var t = 0; t < ms; t += 100) { now += 100; service.Tick(); } }
        service.Act("create", J(new { mode = "deathmatch", scenario = "Synthetic A" }));
        Check(service.Act("add-bot", J(new { skill = "normal" })).Ok, "The host adds a bot from the lobby panel");
        Run(3000);
        Check(service.Act("start", default).Ok, "A deathmatch against a bot starts");
        for (var i = 0; i < 300 && !File.Exists(Path.Combine(output, MultiplayerService.BotOrdersFile)); i++) Run(100);
        Run(1000);
        var orders = File.Exists(Path.Combine(output, MultiplayerService.BotOrdersFile)) ? File.ReadAllText(Path.Combine(output, MultiplayerService.BotOrdersFile)) : "";
        Check(service.StandIns.Count == 1 && orders.StartsWith("AIMMOD_BOTS_1\t", StringComparison.Ordinal) && orders.Contains("bot\t1\t", StringComparison.Ordinal),
            "A bot is a stand-in on the host's game, steered by bot-orders.tsv");
        var notice = service.NoticeText();
        _ = notice;
        // avatar-state.tsv names the bot by its stand-in peer, which AimModSteam reads (its rows used
        // to void the whole file: no deaths, teams or weapons on any avatar).
        var state = File.Exists(Path.Combine(output, "avatar-state.tsv")) ? File.ReadAllText(Path.Combine(output, "avatar-state.tsv")) : "";
        Check(state.Contains("\npeer\t1\t1\t", StringComparison.Ordinal), "avatar-state.tsv has the bot's row by its stand-in peer");
    }
}

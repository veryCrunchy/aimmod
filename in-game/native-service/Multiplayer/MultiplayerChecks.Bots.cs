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
        BotService(root);
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
        // Score modes have no bots: switching drops them.
        core.Apply("host", "settings", J(new { settings = new { mode = LobbyModes.Rounds } }), content);
        Check(core.Snapshot().Members.All(m => m.Bot is null), "Bots leave when the mode isn't a shooting mode");
        Check(!core.Apply("host", "add-bot", default, content).Ok, "No bots in score modes");
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
    }
}

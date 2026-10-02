using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

// In-match chat (MatchChat.cs, MultiplayerService.Chat.cs): team and all routing, CS dead chat,
// callout locations, the rate limits, the bot chat contract and the chat input's state machine.
static partial class MultiplayerChecks
{
    static void Chat(string root)
    {
        ChatLocations();
        ChatInputMachine();
        ChatBotLimits();
        ChatRouting();
        ChatService(root);
    }

    static void ChatLocations()
    {
        var spec = MapObjectives.Parse(CsSpec())!; // map_scale 4: site A 1000..1200 x -2600..-2400, Long A around it, spawns at y 2400 / -2400
        Check(MatchChat.Location(spec, 1100, -2500, 104) == "Long A", "A callout zone names the place (the map's own callouts first)");
        var noCallouts = spec with { Zones = spec.Zones.Where(z => z.Type != "callout").ToArray() };
        Check(MatchChat.Location(noCallouts, 1100, -2500, 104) == "A Site" && MatchChat.Location(noCallouts, 1500, -2500, 104) == "A Site"
            && MatchChat.Location(noCallouts, -1100, -2500, 104) == "B Site", "Without a callout: the bomb site you stand in or next to");
        Check(MatchChat.Location(noCallouts, 80, 2400, 104) == "T Spawn" && MatchChat.Location(noCallouts, 80, -1700, 104) == "CT Spawn", "Else the side's spawn you are near");
        Check(MatchChat.Location(noCallouts, 5000, 0, 104) is null && MatchChat.Location(null, 0, 0, 0) is null && MatchChat.Location(spec, double.NaN, 0, 0) is null,
            "Nowhere named: no location (and none without map data)");
    }

    static void ChatInputMachine()
    {
        var input = new ChatInput();
        long t = 1_000;
        Check(!input.Open && input.Begin(MatchChat.Team, t) && input.Open && input.Scope == MatchChat.Team && input.Session == 1 && !input.Begin(MatchChat.All, t),
            "U opens team chat; a second key while open changes nothing");
        input.Step(true, false, false, t += 100);
        Check(input.Open, "Open while typing");
        input.Step(true, false, true, t += 100);
        Check(!input.Open && input.Closed == "enter", "Enter closes it (the game gets its keys back at once)");
        Check(input.Accept(1, t + 1500) == MatchChat.Team && input.Closed == "sent" && input.Accept(1, t + 1600) is null,
            "The page's line for that input still goes out just after Enter, once");
        input.Begin(MatchChat.All, t += 100);
        Check(input.Accept(1, t) is null && input.Open, "A line for an older input is refused");
        input.Step(true, true, false, t += 100);
        Check(!input.Open && input.Closed == "escape" && input.Accept(2, t) is null, "Escape cancels: nothing is sent");
        input.Begin(MatchChat.All, t += 100);
        input.Step(false, false, false, t += 100);
        Check(input.Open, "A blip out of focus keeps it");
        input.Step(true, false, false, t += 100);
        input.Step(false, false, false, t += 100);
        input.Step(false, false, false, t += ChatInput.FocusLossMs);
        Check(!input.Open && input.Closed == "focus", "Alt-tab (half a second away) closes it, so the game never stays in the chat");
        input.Begin(MatchChat.All, t += 100);
        input.Step(true, false, false, t + ChatInput.IdleMs + 1);
        Check(!input.Open && input.Closed == "idle", "Left open and untouched: it closes by itself");
        input.Begin(MatchChat.All, t += 100);
        input.Cancel(input.Session - 1, t);
        Check(input.Open, "The page cancels only its own input");
        input.Cancel(input.Session, t);
        Check(!input.Open && input.Closed == "cancel", "The page's Escape closes it");
        input.Begin(MatchChat.All, t += 100);
        Check(input.Accept(input.Session, t) == MatchChat.All && !input.Open, "A line sent while open closes the input");
        input.Begin(MatchChat.All, t += 100);
        input.Step(true, false, true, t += 10);
        Check(input.Accept(input.Session, t + ChatInput.SendGraceMs + 1) is null, "After the grace the closed input takes nothing");
    }

    static void ChatBotLimits()
    {
        var limit = new BotChatLimiter();
        long t = 5_000_000;
        Check(limit.Allow("b1", 1, true, "Enemy spotted", t), "A bot's first callout goes out");
        Check(!limit.Allow("b1", 1, true, "Rotating B", t + 1000) && !limit.Ready("b1", t + 2999) && limit.Ready("b1", t + 3000), "One line per bot every 3 s");
        Check(!limit.Allow("b2", 1, true, "enemy  SPOTTED", t + 1000), "A teammate bot doesn't repeat the same callout within 5 s");
        Check(limit.Allow("b3", 2, true, "Enemy spotted", t + 1000), "The other team's bots have their own callouts");
        Check(limit.Allow("b2", 1, true, "Enemy spotted", t + MatchChat.BotRepeatMs), "After 5 s it may be said again");
        Check(limit.Allow("b4", 1, false, "Enemy spotted", t + 1000), "All chat and team chat are separate");
    }

    // A CS lobby on the host: the host and five bots, three a side, the map spec of CsMaps.
    static (LobbyCore Core, Func<long> Clock, Action<long> Advance, MatchSnapshot Match) ChatCsLobby()
    {
        var content = new FakeContent();
        var (core, clock, advance) = Lobby();
        core.Apply("host", "settings", Patch(new { mode = "cs", maxPlayers = 6, countdown = 3 }), content);
        core.Apply("host", "add-bot", J(new { skill = "normal", fill = true }), content);
        ReadyAll(core);
        core.SetCsObjectives(MapObjectives.Parse(CsSpec()));
        Check(core.Apply("host", "start", default, content).Ok, "A CS match with bots starts (chat)");
        advance(3000); core.Tick();
        return (core, clock, advance, core.Snapshot().Match!);
    }

    static void ChatRouting()
    {
        var content = new FakeContent();
        var (core, clock, advance, m) = ChatCsLobby();
        var cs = m.Cs!;
        int TeamOf(string id) => cs.Players.First(p => p.Member == id).Team;
        var mate = cs.Players.First(p => p.Member != "host" && p.Team == TeamOf("host")).Member;
        var foe = cs.Players.First(p => p.Team != TeamOf("host")).Member;
        IReadOnlyList<ChatLine> Seen(string who) => MatchChat.For(core.Snapshot(), who).Chat;
        // Team chat with where the host stands: inside Long A.
        core.Track("host", new TrackBatch(m.Id, m.Round, [new TrackSample(clock(), 1100, -2500, 104, 0, 0), new TrackSample(clock() + 100, 1100, -2500, 104, 0, 0)], []));
        Check(core.Apply("host", "chat", J(new { text = "Hold this position", scope = "team" }), content).Ok, "Team chat is a chat command with a scope");
        var line = core.Snapshot().Chat.Last();
        Check(line is { Scope: MatchChat.Team, Place: "Long A", Dead: false, Radio: false } && line.Team == TeamOf("host") && line.Match == m.Id, "A team line carries the team, the match and the caller's callout");
        Check(Seen(mate).Any(l => l.Id == line.Id) && Seen("host").Any(l => l.Id == line.Id) && !Seen(foe).Any(l => l.Id == line.Id), "Team chat reaches the team only");
        Check(!Seen("someone-else").Any(l => l.Id == line.Id), "Lobby members outside the match don't get team chat");
        core.Apply("host", "chat", J(new { text = "gl hf", scope = "all" }), content);
        var all = core.Snapshot().Chat.Last();
        Check(all is { Scope: MatchChat.All, Place: null } && Seen(foe).Any(l => l.Id == all.Id) && Seen(mate).Any(l => l.Id == all.Id), "All chat reaches everyone, with no location");
        core.Apply(foe, "chat", J(new { text = "Enemy spotted", scope = "team", radio = true }), content);
        var radio = core.Snapshot().Chat.Last();
        Check(radio is { Radio: true, Scope: MatchChat.Team } && radio.Team == TeamOf(foe) && !Seen("host").Any(l => l.Id == radio.Id), "A radio callout is team chat too");
        core.Apply("host", "chat", J(new { text = "lobby line" }), content);
        Check(core.Snapshot().Chat.Last() is { Scope: null, Match: null } && Seen(foe).Last().Text == "lobby line", "Without a scope it stays a lobby line for everyone");
        // Clients get only their lines: the snapshot a peer receives is filtered by the host.
        var forFoe = MatchChat.For(core.Snapshot(), foe);
        Check(forFoe.Chat.Count < core.Snapshot().Chat.Count && MatchChat.For(forFoe, foe).Chat.Count == forFoe.Chat.Count, "The per-member snapshot drops what they may not see (and filtering again changes nothing)");

        // Dead chat (CS): a downed player's lines reach only the dead and spectators, until the match ends.
        advance(CsRules.FreezeMs + 100); core.Tick();
        var shooter = cs.Players.First(p => p.Team != TeamOf("host") && core.Snapshot().Members.Any(x => x.Id == p.Member && x.Bot is not null)).Member;
        for (var i = 0; i < 40 && core.Snapshot().Match!.Cs!.Players.First(p => p.Member == "host").Alive; i++) { advance(400); core.Tick(); core.BotShot(shooter, "host", true, 1, [1, 0, 0]); }
        Check(!core.Snapshot().Match!.Cs!.Players.First(p => p.Member == "host").Alive, "The host is down (chat)");
        core.Apply("host", "chat", J(new { text = "he's one shot", scope = "all" }), content);
        var dead = core.Snapshot().Chat.Last();
        Check(dead is { Dead: true, Scope: MatchChat.All }, "A downed player's line is marked dead");
        Check(!Seen(mate).Any(l => l.Id == dead.Id) && !Seen(foe).Any(l => l.Id == dead.Id) && Seen("host").Any(l => l.Id == dead.Id) && Seen("someone-else").Any(l => l.Id == dead.Id),
            "Living players don't see dead chat; the sender and spectators do");
        core.Apply("host", "chat", J(new { text = "dead team line", scope = "team" }), content);
        var deadTeam = core.Snapshot().Chat.Last();
        var s = core.Snapshot();
        var mateDown = s with { Match = s.Match! with { Cs = s.Match.Cs! with { Players = s.Match.Cs.Players.Select(p => p.Member == mate ? p with { Alive = false } : p).ToArray() } } };
        Check(MatchChat.For(mateDown, mate).Chat.Any(l => l.Id == dead.Id), "Another dead player sees it");
        Check(deadTeam.Place is null && !MatchChat.For(mateDown, foe).Chat.Any(l => l.Id == deadTeam.Id) && MatchChat.For(mateDown, mate).Chat.Any(l => l.Id == deadTeam.Id),
            "Dead team chat: the dead of that team only, and no location");
        var deadTalk = s with { Settings = s.Settings with { DeadTalk = true } };
        Check(MatchChat.For(deadTalk, foe).Chat.Any(l => l.Id == dead.Id), "deadTalk: dead players' chat reaches everyone");
        Check(LobbyRules.Apply(s.Settings, J(new { deadTalk = true }), 6, content).Settings!.DeadTalk && !LobbyRules.Apply(s.Settings, J(new { deadTalk = 1 }), 6, content).Result.Ok,
            "deadTalk is a lobby setting (on or off)");
        var over = s with { Match = s.Match! with { Phase = MatchPhases.Final } };
        Check(MatchChat.For(over, foe).Chat.Any(l => l.Id == dead.Id) && !MatchChat.For(over, foe).Chat.Any(l => l.Id == line.Id), "After the match dead chat is everyone's; team chat stays the team's");

        // The lobby's own limit holds in a match: five lines in five seconds.
        var limited = Enumerable.Range(0, 6).Select(i => core.Apply(mate, "chat", J(new { text = "spam " + i, scope = "all" }), content)).ToArray();
        Check(limited.Take(5).All(r => r.Ok) && limited[5].Code == "slow", "Chat is rate limited in a match too");

        // Modes without teams: team chat is all chat.
        var (dm, _, dmAdvance) = Lobby();
        dm.Apply("host", "settings", Patch(new { mode = "deathmatch" }), content);
        dm.Join("p2", "P2"); ReadyAll(dm);
        dm.Apply("host", "start", default, content); dmAdvance(10_000); dm.Tick();
        dm.Apply("p2", "chat", J(new { text = "team?", scope = "team" }), content);
        Check(dm.Snapshot().Chat.Last() is { Scope: MatchChat.All, Team: 0, Match: not null } && MatchChat.For(dm.Snapshot(), "host").Chat.Last().Text == "team?", "Without teams a team line goes to all");
    }

    static void ChatService(string root)
    {
        long now = 50_000_000;
        var game = Path.Combine(root, "game");
        var output = Path.Combine(root, "chat-output");
        Directory.CreateDirectory(output);
        var control = new FakeGame("load", "start") { Root = game };
        var service = new MultiplayerService(new OfflineTransport(), new ContentLibrary(game), control, () => new LocalRun(false, null, null, null, null, 0, 0, 0, null), () => [], () => null, output, simulation: false, () => now, autoTick: false, seed: 29);
        void Run(int ms) { for (var t = 0; t < ms; t += 100) { now += 100; service.Tick(); } }
        JsonElement Notice() => JsonDocument.Parse(service.NoticeText()).RootElement;
        service.Act("create", J(new { mode = "team-deathmatch", scenario = "Synthetic A" }));
        service.Act("settings", Patch(new { maxPlayers = 6 }));
        service.Act("add-bot", J(new { skill = "normal", fill = true }));
        Run(3000);
        Check(service.Act("start", default).Ok, "A team deathmatch against bots starts (chat)");
        for (var i = 0; i < 100 && JsonSerializer.SerializeToElement(service.View(), Protocol.Json).GetProperty("lobby").GetProperty("match").GetProperty("phase").GetString() != MatchPhases.Live; i++) Run(200);
        var lobby = JsonSerializer.SerializeToElement(service.View(), Protocol.Json).GetProperty("lobby");
        var members = lobby.GetProperty("members").EnumerateArray().ToArray();
        var self = lobby.GetProperty("self").GetString()!;
        var combat = lobby.GetProperty("match").GetProperty("combat").GetProperty("players").EnumerateArray().ToDictionary(p => p.GetProperty("member").GetString()!, p => p.GetProperty("team").GetInt32());
        var myTeam = combat[self];
        var bots = members.Where(x => x.TryGetProperty("bot", out var b) && b.ValueKind == JsonValueKind.String).Select(x => x.GetProperty("id").GetString()!).ToArray();
        var mateBots = bots.Where(b => combat[b] == myTeam).ToArray();
        var foeBot = bots.First(b => combat[b] != myTeam);
        Check(mateBots.Length >= 2, "Two bots on the host's team (chat)");
        string[] Feed() => Notice().TryGetProperty("chat", out var c) ? c.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("text").GetString()!).ToArray() : [];

        // The bot chat contract: BotSay(member, teamOnly, text).
        Check(service.BotSay is not null, "The service sets BotSay for the bot AI");
        service.BotSay!(mateBots[0], true, "Smoking A main");
        var said = Notice().GetProperty("chat").GetProperty("lines").EnumerateArray().Last();
        Check(said.GetProperty("text").GetString() == "Smoking A main" && said.GetProperty("scope").GetString() == MatchChat.Team && said.GetProperty("name").GetString()!.StartsWith("BOT ", StringComparison.Ordinal)
            && said.GetProperty("team").GetInt32() == myTeam, "A bot's team callout shows on its team's feed, by name and team");
        service.BotSay(foeBot, true, "Rotating B");
        Check(!Feed().Contains("Rotating B"), "The other team's bot callout never reaches this player");
        service.BotSay(mateBots[0], true, "2 enemies B site");
        Check(!Feed().Contains("2 enemies B site"), "A bot says at most one line every 3 s");
        service.BotSay(mateBots[1], true, "smoking a main");
        Check(Feed().Count(t => t.Equals("Smoking A main", StringComparison.OrdinalIgnoreCase)) == 1, "The same callout isn't repeated by the team within 5 s");
        service.BotSay(mateBots[1], false, "gg");
        Check(Feed().Contains("gg") && Notice().GetProperty("chat").GetProperty("lines").EnumerateArray().Last().GetProperty("scope").GetString() == MatchChat.All, "teamOnly false: all chat");
        service.BotSay(self, true, "not a bot"); service.BotSay("nobody", true, "who");
        Check(!Feed().Contains("not a bot") && !Feed().Contains("who"), "Only the lobby's bots speak through BotSay");
        Run(3100);
        service.BotSay(mateBots[0], true, "Planting A");
        Check(Feed().Contains("Planting A"), "After 3 s the bot speaks again");

        // The input: open (the notice tells AimModCore to hold the keyboard), the line goes out, input is given back.
        service.ChatState.Begin(MatchChat.Team, now);
        var open = Notice();
        Check(open.GetProperty("typing").GetBoolean() && open.GetProperty("interactive").GetBoolean() && !open.GetProperty("cursor").GetBoolean()
            && open.GetProperty("chat").GetProperty("open").GetString() == MatchChat.Team && open.GetProperty("chat").GetProperty("teams").GetBoolean(),
            "Open: the notice says typing (the layer takes the keyboard), team chat");
        Check(service.ChatCapturing && !service.BoardArmed, "While typing the keys are the chat's: no CS keys, no scoreboard");
        var session = open.GetProperty("chat").GetProperty("session").GetInt64();
        Check(!service.Act("chat-send", J(new { session = session + 7, text = "stale" })).Ok, "A line for another input is refused");
        Check(service.Act("chat-send", J(new { session, text = "  Rotate\u0007 B  " })).Ok && !service.ChatState.Open, "Enter: the page sends the line and the input closes");
        var mine = Notice().GetProperty("chat").GetProperty("lines").EnumerateArray().Last();
        Check(mine.GetProperty("text").GetString() == "Rotate B" && mine.GetProperty("you").GetBoolean() && mine.GetProperty("scope").GetString() == MatchChat.Team, "The line is cleaned and goes out as team chat");
        Check(Notice().GetProperty("typing").ValueKind == JsonValueKind.Null, "Closed: no typing, the game has its keys again");
        // The file AimModCore and Notify.lua read gets the chat's state only.
        Run(200);
        var file = File.ReadAllText(Path.Combine(output, "multiplayer-notify.json"));
        Check(file.Contains("\"chat\":{\"open\":null,\"session\":", StringComparison.Ordinal) && !file.Contains("Rotate B", StringComparison.Ordinal), "The notice file carries the chat's state, not its lines");
        service.ChatState.Begin(MatchChat.All, now);
        Run(100);
        Check(File.ReadAllText(Path.Combine(output, "multiplayer-notify.json")).Contains("\"typing\":true", StringComparison.Ordinal), "The notice file says typing while the input is open");
        Check(service.Act("chat-close", J(new { session = service.ChatState.Session })).Ok && !service.ChatState.Open && service.CsSwallowMenu(now), "The page's Escape closes it (and KovaaK's pause menu it opened closes again)");
        service.ChatState.Begin(MatchChat.All, now);
        Run((int)ChatInput.FocusLossMs + 200);
        Check(!service.ChatState.Open && service.ChatState.Closed == "focus", "The game window not in front: the input closes by itself");
        // Radio menus: clickable callouts as team chat.
        Check(service.Act("chat-radio", J(new { group = 1, item = 1 })).Ok && Feed().Last() == "Need backup", "A radio callout goes out as team chat");
        Check(!service.Act("chat-radio", J(new { group = 9, item = 0 })).Ok, "Unknown callouts are refused");
        // KovaaK's binds on the chat keys: information only.
        Check(MultiplayerService.ChatKeyClashes(new HashSet<string> { "Y", "C" }, true).SequenceEqual(["KovaaK’s also uses Y (all chat).", "KovaaK’s also uses C (radio responses)."])
            && MultiplayerService.ChatKeyClashes(new HashSet<string> { "U", "C" }, false).Count == 0, "Y, U and the radio keys are checked against KovaaK's binds (team keys only with teams)");
        service.Dispose();
    }
}

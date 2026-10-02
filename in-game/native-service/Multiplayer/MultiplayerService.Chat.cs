using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

// In-match chat on this machine (game-modes.md 6.11, rules in MatchChat.cs): Y opens all chat, U
// team chat, Z/X/C the radio menus (team modes). The keys are read like the CS keys (only with the
// game window in front, nothing hooked by the service). While the input or a radio menu is open
// the notice says so (typing / cursor) and AimModCore holds input for the notice layer the way it
// does for the buy menu: UI-only focused on the layer's Gameface widget, fire blocked, given back
// on close, on alt-tab and when KovaaK's menu takes over. The page types the line from the keys
// AimModCore relays and sends it here ("chat-send"); the service closes the input on Enter, Escape
// and focus loss by itself too, so the player always gets the game back.
sealed partial class MultiplayerService
{
    public const string ChatAllKey = "Y", ChatTeamKey = "U";
    public static readonly string[] RadioKeys = ["Z", "X", "C"];
    // Lines on the HUD feed, and how long a radio menu stays open untouched.
    public const int ChatFeedLines = 8;
    public const long RadioOpenMs = 8000;
    readonly ChatInput chatInput = new();
    readonly CsKeyReader chatKeys = new();
    readonly BotChatLimiter botChat = new();
    int? radioGroup; long radioOpenedAt;
    (string Match, long Since)? chatSeen;
    internal ChatInput ChatState => chatInput;
    internal int? RadioGroup => radioGroup;
    // The chat input or a radio menu has the keys: the CS keys, the scoreboard key and the rest wait.
    internal bool ChatCapturing => chatInput.Open || radioGroup is not null;

    // The bot chat contract (game-modes.md 6.11.3): BotSay(member, teamOnly, text) posts a line for a
    // bot of this lobby, as team chat (with the bot's location, like a player's) or all chat. Set by
    // the service itself; the bot AI calls it. Host only, rate limited (BotChatLimiter: one line per
    // bot every 3 s, the same callout once per team in 5 s); anything else is dropped silently.
    public Action<string, bool, string>? BotSay { get; set; }

    // Is this machine's player in a match where chat is on: countdown, play and between rounds.
    MatchSnapshot? ChatMatch(LobbySnapshot? lobby) =>
        lobby?.Match is { Phase: MatchPhases.Countdown or MatchPhases.Live or MatchPhases.Round } m && m.Players.Contains(SelfId) ? m : null;

    // Every tick: the keys.
    void StepChat()
    {
        var now = clock();
        var lobby = Current;
        var match = ChatMatch(lobby);
        // Read every key every tick so a press is an edge only once, whatever is open.
        var all = chatKeys.Pressed('Y'); var team = chatKeys.Pressed('U');
        var escape = chatKeys.Pressed((char)0x1B); var enter = chatKeys.Pressed((char)0x0D);
        var radio = new[] { chatKeys.Pressed('Z'), chatKeys.Pressed('X'), chatKeys.Pressed('C') };
        var digits = Enumerable.Range(1, 9).Select(i => chatKeys.Pressed((char)('0' + i))).ToArray();
        if (match is null || lobby is null) { chatInput.Close("match", now); radioGroup = null; return; }
        if (chatSeen?.Match != match.Id) chatSeen = (match.Id, now + HostOffset());
        var foreground = chatKeys.Foreground();
        var modifier = chatKeys.Down((char)0x11) || chatKeys.Down((char)0x12); // Ctrl or Alt: the game's and the OS's
        if (chatInput.Open)
        {
            chatInput.Step(foreground, escape, enter, now);
            // Escape over the input: KovaaK's pause menu it opened closes again (swallowMenu).
            if (!chatInput.Open && chatInput.Closed == "escape") csEscapedAt = now;
            return;
        }
        var teams = MatchChat.Teams(match);
        var down = MatchChat.ViewerOf(lobby, SelfId).Dead;
        if (radioGroup is { } open)
        {
            // Y or U over a radio menu: it closes and the chat input opens instead.
            if (all || team) radioGroup = null;
            else
            {
                var picked = Array.IndexOf(digits, true);
                var other = Array.IndexOf(radio, true);
                if (!foreground || escape || now - radioOpenedAt > RadioOpenMs || down) { if (escape) csEscapedAt = now; radioGroup = null; }
                else if (picked >= 0) { if (MatchChat.RadioText(open, picked) is { } text) SayRadio(text); radioGroup = null; }
                else if (other >= 0) { radioGroup = other == open ? null : other; radioOpenedAt = now; }
                return;
            }
        }
        if (!foreground || modifier) return;
        if (all || team)
        {
            chatInput.Begin(team && teams ? MatchChat.Team : MatchChat.All, now);
            radioGroup = null; buyOpen = false;
            return;
        }
        if (teams && !down && Array.IndexOf(radio, true) is var g and >= 0) { radioGroup = g; radioOpenedAt = now; buyOpen = false; }
    }

    LobbyResult SayRadio(string text) => Command("chat", JsonSerializer.SerializeToElement(new { text, scope = MatchChat.Team, radio = true }));

    // From the notify page: the typed line, Escape it saw, or a radio item clicked.
    LobbyResult ChatAction(string action, JsonElement args)
    {
        var now = clock();
        long session = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("session", out var s) && s.TryGetInt64(out var sv) ? sv : -1;
        switch (action)
        {
            case "chat-send":
                var text = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? LobbyRules.CleanChat(t.GetString()) : null;
                var scope = chatInput.Accept(session, now);
                if (scope is null) return LobbyResult.Fail("chat-closed", "The chat input is closed.");
                if (text is null) return LobbyResult.Success; // Enter on an empty line just closes it
                if (ChatMatch(Current) is null) return LobbyResult.Fail("no-match", "Chat in a match only.");
                return Command("chat", JsonSerializer.SerializeToElement(new { text, scope }));
            case "chat-close":
                if (chatInput.Open && chatInput.Session == session) { chatInput.Cancel(session, now); csEscapedAt = now; }
                return LobbyResult.Success;
            case "chat-radio":
                int Int(string k) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(k, out var v) && v.TryGetInt32(out var n) ? n : -1;
                var group = radioGroup ?? Int("group");
                radioGroup = null;
                if (MatchChat.RadioText(group, Int("item")) is not { } call) return LobbyResult.Fail("invalid", "Unknown callout.");
                if (ChatMatch(Current) is not { } m || !MatchChat.Teams(m)) return LobbyResult.Fail("no-match", "Radio callouts are for team modes.");
                return SayRadio(call);
            default:
                return LobbyResult.Fail("invalid", "Unknown chat action.");
        }
    }

    // BotSay's implementation (host only).
    void SayAsBot(string member, bool teamOnly, string text)
    {
        lock (gate)
        {
            var now = clock();
            if (core is null || LobbyRules.CleanChat(text) is not { } clean || !botChat.Ready(member, now)) return;
            var lobby = core.Snapshot();
            if (lobby.Members.FirstOrDefault(x => x.Id == member) is not { Bot: not null } || ChatMatchFor(lobby, member) is not { } m) return;
            var team = teamOnly && MatchChat.Teams(m) ? MatchChat.ViewerOf(lobby, member).Team : 0;
            var toTeam = team is 1 or 2;
            // Someone on the team (a person or a bot) just said the same: don't repeat it.
            if (lobby.Chat.Any(l => l.Match == m.Id && now - l.At < MatchChat.BotRepeatMs && (l.Scope == MatchChat.Team) == toTeam && (!toTeam || l.Team == team)
                && string.Equals(l.Text, clean, StringComparison.OrdinalIgnoreCase))) return;
            if (!botChat.Allow(member, team, toTeam, clean, now)) return;
            core.Apply(member, "chat", JsonSerializer.SerializeToElement(new { text = clean, scope = toTeam ? MatchChat.Team : MatchChat.All }), library);
        }
    }
    static MatchSnapshot? ChatMatchFor(LobbySnapshot lobby, string member) =>
        lobby.Match is { Phase: MatchPhases.Countdown or MatchPhases.Live or MatchPhases.Round } m && m.Players.Contains(member) ? m : null;

    // The chat for the notice layer: the feed (the lines this player may see from this match, newest
    // last, with their age so the page fades them), the input while open, the radio menu, and
    // KovaaK's binds on the chat keys (information only).
    internal sealed record ChatLineView(long Id, string Scope, string Name, int Team, bool Dead, string? Place, string Text, bool Radio, bool You, int Age);
    internal sealed record ChatRadioView(int Group, string Key, string Title, IReadOnlyList<string> Items);
    internal sealed record ChatHudView(string? Open, long Session, bool Teams, string AllKey, string TeamKey, IReadOnlyList<string> RadioKeys, bool Cs,
        IReadOnlyList<ChatLineView> Lines, ChatRadioView? Radio, IReadOnlyList<string> Clashes);
    internal ChatHudView? ChatHud()
    {
        var whole = Current;
        if (ChatMatch(whole) is not { } m || whole is null) return null;
        var lobby = MatchChat.For(whole, SelfId);
        var hostNow = clock() + HostOffset();
        var since = chatSeen is { } seen && seen.Match == m.Id ? seen.Since : hostNow;
        var lines = lobby.Chat.Where(l => l.Match == m.Id || (l.Scope is null && l.At >= since)).TakeLast(ChatFeedLines).Select(l =>
            new ChatLineView(l.Id, l.System ? "system" : l.Scope ?? MatchChat.All, l.System ? "" : LobbyRules.CleanName(l.Name, "Player"), l.Team, l.Dead, l.Place, l.Text, l.Radio,
                l.From == SelfId, (int)Math.Clamp(hostNow - l.At, 0, int.MaxValue))).ToArray();
        var teams = MatchChat.Teams(m);
        var radio = radioGroup is { } g ? new ChatRadioView(g, RadioKeys[g], MatchChat.Radio[g].Title, MatchChat.Radio[g].Items) : null;
        var clashes = chatInput.Open || hostNow - since < 15_000 ? ChatKeyClashes(KeyBinds.GameKeys(library.Root), teams) : [];
        if (lines.Length == 0 && !chatInput.Open && radio is null && clashes.Count == 0) return null;
        return new ChatHudView(chatInput.Scope, chatInput.Session, teams, ChatAllKey, ChatTeamKey, teams ? RadioKeys : [], m.Cs is not null, lines, radio, clashes);
    }

    // KovaaK's own binds (Input.ini) on the chat keys. The keys still open chat; this only says so.
    public static IReadOnlyList<string> ChatKeyClashes(IReadOnlySet<string> gameKeys, bool teams)
    {
        var list = new List<string>();
        if (gameKeys.Contains(ChatAllKey)) list.Add("KovaaK’s also uses " + ChatAllKey + " (all chat).");
        if (teams && gameKeys.Contains(ChatTeamKey)) list.Add("KovaaK’s also uses " + ChatTeamKey + " (team chat).");
        if (teams) for (var i = 0; i < RadioKeys.Length; i++) if (gameKeys.Contains(RadioKeys[i])) list.Add("KovaaK’s also uses " + RadioKeys[i] + " (radio " + MatchChat.Radio[i].Title.ToLowerInvariant() + ").");
        return list;
    }
}

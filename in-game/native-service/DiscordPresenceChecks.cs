using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AimMod.InGame.Multiplayer;

namespace AimMod.InGame;

static class DiscordPresenceChecks
{
    sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Discord checks must not contact Hub.");
    }
    // A private stand-in for the Discord client on a unique pipe name.
    sealed class FakeDiscord : IAsyncDisposable
    {
        public readonly string Prefix = "aimmod-check-" + Guid.NewGuid().ToString("N") + "-";
        public readonly List<JsonObject?> Activities = [];
        public readonly List<int> Pids = [];
        public readonly List<string> Commands = [];
        static readonly SemaphoreSlim writing = new(1, 1);
        // Sends a DISPATCH event to the connected client, as Discord does.
        public async Task Push(string evt, string data)
        {
            var s = current ?? throw new InvalidOperationException("No client connected.");
            await Send(s, DiscordFrames.Frame, "{\"cmd\":\"DISPATCH\",\"evt\":\"" + evt + "\",\"data\":" + data + "}");
        }
        public string? ClientId;
        public int Connections, Closes, Pongs;
        public bool RejectSteamButtons, PingFirst, RejectHandshake;
        readonly CancellationTokenSource stop = new();
        NamedPipeServerStream? current;
        readonly Task server;
        readonly TaskCompletionSource listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FakeDiscord() { server = Task.Run(Serve); listening.Task.Wait(TimeSpan.FromSeconds(5)); }
        public void Drop() { try { current?.Dispose(); } catch (IOException) { } }
        async Task Serve()
        {
            while (!stop.IsCancellationRequested)
            {
                // Unbuffered on purpose: a write completes only once the other end reads, the
                // worst case for a client that reads and writes on the same pipe.
                var pipe = new NamedPipeServerStream(Prefix + "0", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                listening.TrySetResult();
                try
                {
                    await pipe.WaitForConnectionAsync(stop.Token);
                    current = pipe;
                    await Session(pipe);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidDataException) { }
                finally { current = null; await pipe.DisposeAsync(); }
            }
        }
        static async Task Send(Stream s, int op, string json) { await writing.WaitAsync(); try { await s.WriteAsync(DiscordFrames.Encode(op, json)); await s.FlushAsync(); } finally { writing.Release(); } }
        async Task Session(Stream s)
        {
            var hello = await DiscordFrames.Read(s, stop.Token);
            if (hello is not { Opcode: DiscordFrames.Handshake }) return;
            lock (Activities) { ClientId = JsonNode.Parse(hello.Value.Json)?["client_id"]?.GetValue<string>(); Connections++; }
            if (RejectHandshake) { await Send(s, DiscordFrames.Close, "{\"code\":4000,\"message\":\"Invalid Client ID\"}"); return; }
            await Send(s, DiscordFrames.Frame, "{\"cmd\":\"DISPATCH\",\"evt\":\"READY\",\"data\":{\"v\":1}}");
            if (PingFirst) await Send(s, DiscordFrames.Ping, "{\"n\":1}");
            while (true)
            {
                var frame = await DiscordFrames.Read(s, stop.Token);
                if (frame is null) return;
                if (frame.Value.Opcode == DiscordFrames.Pong) { lock (Activities) Pongs++; continue; }
                if (frame.Value.Opcode == DiscordFrames.Close) { lock (Activities) Closes++; return; }
                var message = JsonNode.Parse(frame.Value.Json)!.AsObject();
                var nonce = message["nonce"]!.GetValue<string>();
                if (message["cmd"]?.GetValue<string>() is { } cmd && cmd != "SET_ACTIVITY")
                {
                    lock (Activities) Commands.Add(cmd + (message["evt"] is { } e ? " " + e.GetValue<string>() : "") + (message["args"]?["user_id"] is { } u ? " " + u.GetValue<string>() : ""));
                    await Send(s, DiscordFrames.Frame, "{\"cmd\":\"" + cmd + "\",\"data\":{},\"evt\":null,\"nonce\":\"" + nonce + "\"}");
                    continue;
                }
                var activity = message["args"]!["activity"] as JsonObject;
                var rejected = RejectSteamButtons && activity?["buttons"]?.AsArray().Any(b => b!["url"]!.GetValue<string>().StartsWith("steam:")) == true;
                if (!rejected) lock (Activities) { Activities.Add(activity?.DeepClone().AsObject()); Pids.Add(message["args"]!["pid"]!.GetValue<int>()); }
                await Send(s, DiscordFrames.Frame, rejected
                    ? "{\"cmd\":\"SET_ACTIVITY\",\"evt\":\"ERROR\",\"data\":{\"code\":4000,\"message\":\"invalid url\"},\"nonce\":\"" + nonce + "\"}"
                    : "{\"cmd\":\"SET_ACTIVITY\",\"data\":{},\"evt\":null,\"nonce\":\"" + nonce + "\"}");
            }
        }
        public async ValueTask DisposeAsync() { stop.Cancel(); Drop(); try { await server.WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { } }
    }

    static LiveOverlaySnapshot Live(bool active = true, bool paused = false, string? scenario = "Synthetic Track", double? score = 812.5, double? accuracy = 94.25, double? best = 1020, double? delta = 56, double? remaining = 42, double? seconds = 18) =>
        new(true, active, paused, scenario, score, seconds, 40, 38, 5, null, accuracy, best, best, 60, delta is null ? null : (best ?? 0) + delta, delta, RemainingSeconds: remaining);
    static readonly LiveOverlaySnapshot Menu = new(true, false, false, null, null, null, null, null, null, null, null, null, null, null, null, null);
    static Run Journal(string id, string scenario, double score, string at) => new("native:" + id, scenario, score, 90, 60, 3, 0, at, null, null, null, null, false);

    public static async Task Run()
    {
        var count = 0;
        void Check(bool value, string name) { count++; if (!value) throw new Exception(name); }
        var now = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var started = now.AddMinutes(-30);
        var defaults = new DiscordSettingsValue();
        var emptySession = new DiscordSession(started, 0, null, false, null);

        // Framing.
        var frame = DiscordFrames.Encode(DiscordFrames.Frame, "{\"a\":1}");
        Check(BitConverter.ToInt32(frame, 0) == 1 && BitConverter.ToInt32(frame, 4) == 7 && frame.Length == 15, "Frame header is little-endian opcode and length");
        var read = await DiscordFrames.Read(new MemoryStream(frame), CancellationToken.None);
        Check(read is { Opcode: 1, Json: "{\"a\":1}" }, "Frame round trip");
        Check(await DiscordFrames.Read(new MemoryStream(frame[..10]), CancellationToken.None) is null, "Truncated frame is end of stream");
        var huge = new byte[8]; BitConverter.GetBytes(1).CopyTo(huge, 0); BitConverter.GetBytes(DiscordFrames.MaxPayload + 1).CopyTo(huge, 4);
        var tooLarge = false; try { await DiscordFrames.Read(new MemoryStream(huge), CancellationToken.None); } catch (InvalidDataException) { tooLarge = true; }
        Check(tooLarge, "Oversized frame is never buffered");

        // Rate limit and back-off.
        var limit = new DiscordRateLimit(); var t0 = now.UtcDateTime;
        for (var i = 0; i < 5; i++) Check(limit.TryTake(t0.AddSeconds(i)), "Five updates fit in the window");
        Check(!limit.TryTake(t0.AddSeconds(19)), "Sixth update inside 20 seconds is refused");
        Check(limit.TryTake(t0.AddSeconds(20)), "Window slides after 20 seconds");
        Check(DiscordBackoff.Delay(1) == TimeSpan.FromSeconds(2) && DiscordBackoff.Delay(3) == TimeSpan.FromSeconds(8) && DiscordBackoff.Delay(20) == TimeSpan.FromSeconds(30), "Reconnect back-off doubles to 30 seconds");

        // Content.
        var playing = DiscordActivityBuilder.Build(new(Live(), false, emptySession, "synthetic-player", now), defaults);
        Check(playing.Phase == "playing" && playing.Details == "Synthetic Track", "Playing shows the scenario");
        Check(playing.State == "Score 812.5 · 94.3% acc · Pace +56 vs PB", "Playing shows score, accuracy and PB pace");
        Check(playing.End == now.ToUnixTimeSeconds() + 42 && playing.Start is null, "Remaining time counts down");
        Check(playing.Buttons.Count == 2 && playing.Buttons[0].Url == "steam://run/824270//?action=jump-to-scenario&name=Synthetic%20Track&mode=challenge", "Keeps KovaaK's play-this-scenario link");
        Check(playing.Buttons[1] == new DiscordButton("AimMod Hub profile", "https://aimmod.app/profiles/synthetic-player"), "Hub profile button for a linked account");
        Check(playing.Buttons.All(b => b.Label.Length <= 32 && b.Url.Length <= 512), "Buttons stay within Discord limits");
        var json = playing.ToJson();
        Check(json["timestamps"]!["end"]!.GetValue<long>() == playing.End && json["assets"]!["large_image"]!.GetValue<string>().StartsWith("https://"), "Activity JSON carries timer and assets");
        Check(json["assets"]!["small_image"] is null && json["assets"]!["small_text"] is null, "No small image: the application has no such asset");
        Check(playing.LargeText == "5 kills · 38/40 hits · On pace for 1,076", "Run counters on the image hover text");
        Check(json["assets"]!["large_text"]!.GetValue<string>() == playing.LargeText, "Hover text is sent");
        Check(new[] { playing.Details, playing.State, playing.LargeText }.All(t => t.Length is >= 2 and <= 128), "Text fields stay within Discord limits");
        Check(playing.ToJson(scenarioButton: false)["buttons"]!.AsArray().Count == 1, "Game link can be dropped when Discord rejects it");
        var elapsed = DiscordActivityBuilder.Build(new(Live(remaining: null), false, emptySession, null, now), defaults);
        Check(elapsed.Start == now.ToUnixTimeSeconds() - 18 && elapsed.End is null, "Without remaining time the elapsed time counts up");
        Check(elapsed.Buttons.Count == 1, "No Hub button without a linked account");
        var hidden = DiscordActivityBuilder.Build(new(Live(), false, emptySession, "synthetic-player", now), new DiscordSettingsValue(true, false, false, false));
        Check(hidden.State == "In a challenge" && hidden.Buttons.Count == 1, "Score, PB and Hub button can each be hidden");
        var pbOnly = DiscordActivityBuilder.Build(new(Live(delta: null), false, emptySession, null, now), new DiscordSettingsValue(true, false, true, false));
        Check(pbOnly.State == "PB 1,020", "PB without a projection shows the PB");
        var paused = DiscordActivityBuilder.Build(new(Live(paused: true), false, emptySession, null, now), defaults);
        Check(paused.Phase == "paused" && paused.State == "Paused · Score 812.5 · 94.3% acc · PB 1,020" && paused.Start is null && paused.End is null, "Paused stops the timer");
        var replay = DiscordActivityBuilder.Build(new(Menu with { Replay = true }, true, emptySession, "synthetic-player", now), defaults);
        Check(replay.Phase == "replay" && replay.Details == "Watching a replay" && replay.State == "In AimMod" && replay.Buttons.Single().Label == "AimMod Hub profile", "Replay viewing");
        var replayNamed = DiscordActivityBuilder.Build(new(Menu, true, emptySession, null, now, ReplayScenario: "Synthetic Track"), defaults);
        Check(replayNamed.Details == "Synthetic Track" && replayNamed.State == "Watching a replay", "Replay names its scenario");
        // AimMod panel pages.
        var stats = DiscordActivityBuilder.Build(new(Menu, false, emptySession, null, now, Page: "trends"), defaults);
        Check(stats.Phase == "workspace:trends" && stats.Details == "In AimMod · Statistics" && stats.State == "Ready to train" && stats.Start == started.ToUnixTimeSeconds(), "Panel page with the session timer");
        Check(DiscordActivityBuilder.Build(new(Menu, false, emptySession, null, now, Page: "run-details"), defaults).Details == "Reviewing a run", "Run analysis page");
        Check(DiscordActivityBuilder.Build(new(Menu, false, emptySession, null, now, Page: "replays"), defaults).Details == "Browsing replays", "Replay library page");
        Check(DiscordActivityBuilder.Build(new(Menu, false, emptySession, null, now, Page: "coaching"), defaults).Details == "In AimMod · Coaching", "Coaching page");
        Check(DiscordActivityBuilder.Build(new(Menu, false, emptySession, null, now, Page: "future-page"), defaults).Details == "In AimMod", "Unknown page stays generic");
        var pausedPanel = DiscordActivityBuilder.Build(new(Live(paused: true), false, emptySession, null, now, Page: "coaching"), defaults);
        Check(pausedPanel.State == "Paused · Synthetic Track · Score 812.5 · 94.3% acc · PB 1,020" && pausedPanel.Buttons.Count == 1, "Panel over a paused run keeps the run");
        Check(DiscordActivityBuilder.Build(new(Live(), false, emptySession, null, now, Page: "coaching"), defaults).Phase == "playing", "A running challenge wins over a stale page");
        var view = new DiscordWorkspaceView();
        view.Report("history", true, now);
        Check(view.Current(now.AddSeconds(9)) == "history" && view.Current(now.AddSeconds(11)) is null, "Page report expires after 10 s");
        view.Report("history", false, now); Check(view.Current(now) is null, "Hidden panel has no page");
        Check(DiscordWorkspaceView.ValidPage("run-details") && !DiscordWorkspaceView.ValidPage("Run") && !DiscordWorkspaceView.ValidPage("a/b") && !DiscordWorkspaceView.ValidPage(new string('a', 40)), "Page keys are validated");
        var redacted = DiscordDiagnostics.Redact("{\"cmd\":\"DISPATCH\",\"evt\":\"READY\",\"data\":{\"v\":1,\"user\":{\"id\":\"1\",\"username\":\"synthetic\"}}}");
        Check(redacted.Contains("\"user\":\"[redacted]\"") && !redacted.Contains("synthetic"), "Diagnostics redact the Discord user");
        var menu = DiscordActivityBuilder.Build(new(Menu, false, emptySession, null, now), defaults);
        Check(menu.Phase == "menu" && menu.Start == started.ToUnixTimeSeconds() && menu.Buttons.Count == 0, "Menu shows the session timer");
        var control = DiscordActivityBuilder.Build(new(Live(scenario: "Bad\nName\u0001 " + new string('x', 300)), false, emptySession, null, now), defaults);
        Check(control.Details.Length <= 128 && !control.Details.Any(char.IsControl) && control.Details.StartsWith("Bad Name"), "Text is cleaned and bounded");
        Check(DiscordActivityBuilder.Build(new(Live(scenario: "X"), false, emptySession, null, now), defaults).Details == "In a challenge", "Too-short text uses a fallback");
        Check(DiscordActivityBuilder.HubProfile("a b/c") == "https://aimmod.app/profiles/a%20b%2Fc", "Hub handle is escaped");

        // Session summary: journal runs since the session start; PB across history.
        var history = new[]
        {
            Journal("old", "Synthetic Track", 1000, "2026-02-01T10:00:00Z"),
            new Run("hub:1", "Synthetic Track", 1020, 90, 60, 3, 0, "2026-02-02T10:00:00Z", null, null, null, null, false),
            Journal("a", "Synthetic Track", 900, "2026-03-01T11:40:00Z"),
            Journal("b", "Synthetic Track", 1100, "2026-03-01T11:50:00Z"),
            Journal("c", "Other", 50, "2026-03-01T11:00:00Z"),
        };
        var summary = DiscordSession.Summarize(history, started);
        Check(summary.Runs == 2 && summary.Last?.Id == "native:b", "Session counts only journal runs since the start");
        Check(summary.LastIsPersonalBest && summary.PersonalBest == 1100, "Last run beating every earlier score is a PB");
        var notBest = DiscordSession.Summarize(history.Take(3).ToArray(), started);
        Check(notBest.Runs == 1 && !notBest.LastIsPersonalBest && notBest.PersonalBest == 1020, "PB includes Hub history");
        Check(!DiscordSession.Summarize([Journal("first", "New", 10, "2026-03-01T11:55:00Z")], started).LastIsPersonalBest, "A first-ever run is not announced as a PB");
        var results = DiscordActivityBuilder.Build(new(Menu, false, summary, null, now), defaults);
        Check(results.Phase == "results" && results.Details == "Synthetic Track" && results.State == "Last 1,100 · New PB! · 2 runs this session", "After a run: last score, PB flag and session count");
        var resultsPlain = DiscordActivityBuilder.Build(new(Menu, false, notBest, null, now), new DiscordSettingsValue(true, true, true, true));
        Check(resultsPlain.State == "Last 900 · PB 1,020 · 1 run this session", "Result without a PB shows the standing PB");
        Check(DiscordActivityBuilder.Build(new(Menu, false, summary, null, now), new DiscordSettingsValue(true, false, false, true)).State == "2 runs this session", "Hidden score and PB leave the run count");

        // Update policy.
        var later = DiscordActivityBuilder.Build(new(Live(score: 900), false, emptySession, "synthetic-player", now.AddSeconds(1)), defaults);
        Check(!later.Structural(playing) && !later.SameContent(playing), "Score change alone is not structural");
        Check(DiscordActivityBuilder.Build(new(Live(), false, emptySession, "synthetic-player", now.AddSeconds(2)), defaults) with { State = playing.State } is var drift && drift.SameContent(playing), "Timer drift within three seconds is not a change");
        Check(paused.Structural(playing), "Pause is a structural change");

        // Multiplayer: lobby, match and results.
        var lobbyInfo = new DiscordLobbyInfo("aimmod-0123456789abcdef01234567", 2, 4, "Score race", "Synthetic Track", "lobby", null, null, null, null, null, null, "aimmod1:" + new string('a', 40));
        var inLobby = DiscordActivityBuilder.Build(new(Live(active: false), false, emptySession, "synthetic-player", now, Lobby: lobbyInfo), defaults);
        Check(inLobby.Details == "In lobby · 2/4 · Score race" && inLobby.State == "Synthetic Track" && inLobby.Party == new DiscordParty(lobbyInfo.PartyId, 2, 4), "Lobby shows size, mode and scenario with a party");
        var lobbyJson = inLobby.ToJson();
        Check(lobbyJson["party"]!["size"]!.AsArray().Select(n => n!.GetValue<int>()).SequenceEqual([2, 4]) && lobbyJson["secrets"]!["join"]!.GetValue<string>() == lobbyInfo.JoinSecret, "Party size and join secret are sent");
        Check(lobbyJson["buttons"] is null, "No buttons alongside a join secret");
        var noJoin = DiscordActivityBuilder.Build(new(Live(active: false), false, emptySession, "synthetic-player", now, Lobby: lobbyInfo), defaults with { ShowJoin = false }).ToJson();
        Check(noJoin["secrets"] is null && noJoin["buttons"]!.AsArray().Count == 1 && noJoin["party"] is not null, "Join can be turned off; the Hub button returns");
        Check(DiscordActivityBuilder.Build(new(Live(), false, emptySession, null, now, Lobby: lobbyInfo), defaults with { ShowLobby = false }) is { Phase: "playing", Party: null, JoinSecret: null }, "Lobby details can be hidden");
        var leading = DiscordActivityBuilder.Build(new(Live(), false, emptySession, null, now, Lobby: lobbyInfo with { State = "match", Round = 2, TotalRounds = 4, Lead = 1200, JoinSecret = null }), defaults);
        Check(leading.Phase == "match:2" && leading.Details == "Score race · Synthetic Track" && leading.State == "Round 2/4 · Leading by 1,200" && leading.End == now.ToUnixTimeSeconds() + 42, "Match: round, lead, mode and scenario with the round timer");
        Check(DiscordActivityBuilder.Build(new(Live(), false, emptySession, null, now, Lobby: lobbyInfo with { State = "match", Round = 2, TotalRounds = 4, Lead = -300 }), defaults).State == "Round 2/4 · Trailing by 300", "Trailing");
        Check(DiscordActivityBuilder.Build(new(Live(), false, emptySession, null, now, Lobby: lobbyInfo with { State = "match", Round = 1, TotalRounds = 1, Lead = 0 }), defaults).State == "Round 1/1 · Tied", "Tied");
        Check(DiscordActivityBuilder.Build(new(Live(), false, emptySession, null, now, Lobby: lobbyInfo with { Mode = "Duel", State = "match", Round = 3, FirstTo = 3 }), defaults).State == "Round 3 · First to 3 · Score 812.5", "Duel rounds without a lead show the score");
        Check(DiscordActivityBuilder.Build(new(Live(), false, emptySession, null, now, Lobby: lobbyInfo with { State = "match", Round = 3, TotalRounds = 4 }), defaults).Structural(leading), "A new round is sent promptly");
        Check(DiscordActivityBuilder.Build(new(Menu, false, emptySession, null, now, Lobby: lobbyInfo with { State = "results", Won = true }), defaults).State == "Won the match", "Match won");
        Check(DiscordActivityBuilder.Build(new(Menu, false, emptySession, null, now, Lobby: lobbyInfo with { State = "results", Won = false, Place = 2 }), defaults).State == "Finished 2nd of 2", "Match placing");
        Check(DiscordActivityBuilder.Build(new(Menu, true, emptySession, null, now, Lobby: lobbyInfo), defaults).Phase == "replay", "Watching a replay outranks the lobby");
        // Summary from a lobby lobbySnap, and secret resolution.
        const string steamLobby = "109775240000000001", hostPeer = "76561198000000001", guest = "76561198000000002";
        LobbyMember Member(string id, string name) => new(id, name, MemberRoles.Player, true, 20, ContentStates.Ok, ContentStates.Ok, ContentStates.None, Connections.Connected, "relay", 0, false);
        var settingsMp = new LobbySettings(Scenario: new ScenarioChoice("Synthetic Track", "0123456789abcdef", "Map", "0123456789abcdef", 60), MaxPlayers: 4);
        var lobbySnap = new LobbySnapshot(1, "l-00112233aabbccdd", "ABC234", 1, hostPeer, settingsMp, [Member(hostPeer, "Host"), Member(guest, "Guest")], null, [], 0);
        var summarized = MultiplayerDiscord.Summarize(lobbySnap, guest, steamLobby);
        Check(summarized is { Players: 2, MaxPlayers: 4, Mode: "Score race", State: "lobby", Scenario: "Synthetic Track" } && summarized.JoinSecret is not null, "Lobby summary");
        var exposed = string.Join("|", summarized.PartyId, summarized.JoinSecret);
        Check(!exposed.Contains(steamLobby) && !exposed.Contains(hostPeer) && !exposed.Contains(guest) && !exposed.Contains("00112233aabbccdd") && !exposed.Contains("ABC234"), "Party id and secret carry no Steam id, lobby id or room code");
        Check(summarized.PartyId == MultiplayerDiscord.Summarize(lobbySnap, hostPeer, steamLobby).PartyId && summarized.PartyId.Length <= 128, "Party id is stable for every member");
        Check(MultiplayerDiscord.Summarize(lobbySnap with { Settings = settingsMp with { Privacy = LobbyPrivacy.Invite } }, guest, steamLobby).JoinSecret is null, "Invite-only lobbies offer no join");
        Check(MultiplayerDiscord.Summarize(lobbySnap with { Settings = settingsMp with { MaxPlayers = 2 } }, guest, steamLobby).JoinSecret is null, "Full lobbies offer no join");
        Check(MultiplayerDiscord.Summarize(lobbySnap, guest, null).JoinSecret is null, "No Steam lobby, no join");
        var friends = new[] { new FriendEntry("f1", "Friend", "aimmod-lobby", null, "109775240000000999", true), new FriendEntry("f2", "Friend 2", "aimmod-lobby", null, steamLobby, true) };
        Check(MultiplayerDiscord.Resolve(summarized.JoinSecret!, friends) == steamLobby, "Join secret resolves to the friend's Steam lobby");
        Check(MultiplayerDiscord.Resolve(summarized.JoinSecret!, [friends[0], friends[1] with { Joinable = false }]) is null, "Only joinable friend lobbies resolve");
        Check(MultiplayerDiscord.Resolve("aimmod1:zz", friends) is null && MultiplayerDiscord.Resolve(steamLobby, friends) is null, "Malformed secrets never resolve");
        var live2 = new MatchSnapshot("m-1", MatchPhases.Live, LobbyModes.Race, "Synthetic Track", 60, 2, 3, null, null, null, null, [hostPeer, guest],
            [new ScoreLine(hostPeer, 1000, 30, 30, 10, 9, 3, LineStates.Playing, false), new ScoreLine(guest, 2200, 30, 30, 10, 9, 3, LineStates.Playing, false)], [], [], null, []);
        var inMatch = MultiplayerDiscord.Summarize(lobbySnap with { Match = live2 }, guest, steamLobby);
        Check(inMatch is { State: "match", Round: 2, TotalRounds: 3, Lead: 1200 } && inMatch.JoinSecret is null, "Live round lead against the best other player; no join mid-match");
        Check(MultiplayerDiscord.Summarize(lobbySnap with { Match = live2 }, hostPeer, steamLobby).Lead == -1200, "The other side trails");
        var final = live2 with { Phase = MatchPhases.Final, WinnerId = guest, Standings = [new Standing(guest, "Guest", 1, 2, 6, 2200, 4000, 3), new Standing(hostPeer, "Host", 2, 1, 3, 1500, 3000, 3)] };
        Check(MultiplayerDiscord.Summarize(lobbySnap with { Match = final }, guest, steamLobby) is { State: "results", Won: true, Place: 1 }, "Final standing");

        // Settings store.
        var folder = Path.Combine(Path.GetTempPath(), "aimmod-discord-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new DiscordSettings(folder);
            DiscordSettingsValue Apply(string text) => store.ApplyJson(Encoding.UTF8.GetBytes(text));
            Check(store.Current == new DiscordSettingsValue(), "Presence is on with every detail by default");
            Check(DiscordSettings.Decode("AIMMOD_DISCORD_1\ndiscordPresenceEnabled\t1\ndiscordShowScore\t0\ndiscordShowPersonalBest\t1\ndiscordShowHubButton\t1\n") == new DiscordSettingsValue(true, false, true, true, true, true), "Version 1 settings still load, with lobby and join on");
            Check(DiscordSettings.Encode(new DiscordSettingsValue(ShowJoin: false)).StartsWith("AIMMOD_DISCORD_2\n") && DiscordSettings.Decode(DiscordSettings.Encode(new DiscordSettingsValue(ShowJoin: false))).ShowJoin == false, "Version 2 round trip");
            Check(DiscordSettings.Decode(File.ReadAllText(Path.Combine(folder, "discord-settings.tsv"))) == store.Current, "Saved file matches the API");
            Check(!File.Exists(Path.Combine(folder, "native-settings.tsv")), "Native settings file format is left untouched");
            Check(Apply("{\"discordShowScore\":false}") == new DiscordSettingsValue(true, false, true, true), "Patch changes one option");
            Check(Apply("{\"discordShowLobby\":false,\"discordShowJoin\":false}") == new DiscordSettingsValue(true, false, true, true, false, false) && Apply("{\"discordShowLobby\":true,\"discordShowJoin\":true}").ShowJoin, "Lobby and join options patch");
            Check(new DiscordSettings(folder).Current == store.Current, "Options survive restart");
            foreach (var invalid in new[] { "{}", "[]", "{\"discordShowScore\":1}", "{\"discordPresenceEnabled\":true,\"discordPresenceEnabled\":false}", "{\"replayRecordingEnabled\":false}", "{\"DiscordShowScore\":true}" })
            {
                var rejected = false; try { Apply(invalid); } catch (JsonException) { rejected = true; }
                Check(rejected && store.Current == new DiscordSettingsValue(true, false, true, true), "Invalid patch never changes options");
            }
            File.WriteAllText(Path.Combine(folder, "discord-settings.tsv"), "damaged");
            store = new DiscordSettings(folder);
            Check(store.ReadFailed && !store.Current.Enabled, "Damaged options turn presence off");
            Check(JsonSerializer.Serialize(store.Current).Contains("\"discordPresenceEnabled\":false"), "Stable browser property names");

            // Handoff files.
            Check(DiscordHandoff.ParseGame("AIMMOD_DISCORD_GAME_1\treleased\t" + now.ToUnixTimeSeconds() + "\n", now) == DiscordHandoff.GameState.Released, "Released acknowledgement");
            Check(DiscordHandoff.ParseGame("AIMMOD_DISCORD_GAME_1\toff\t" + now.ToUnixTimeSeconds() + "\n", now) == DiscordHandoff.GameState.Off, "Game presence turned off by the player");
            Check(DiscordHandoff.ParseGame("AIMMOD_DISCORD_GAME_1\treleased\t" + (now.ToUnixTimeSeconds() - 4) + "\n", now) == DiscordHandoff.GameState.Unknown, "Stale acknowledgement is not held");
            Check(DiscordHandoff.ParseGame("AIMMOD_DISCORD_GAME_1\treleased\t" + now.ToUnixTimeSeconds(), now) == DiscordHandoff.GameState.Unknown, "Torn acknowledgement is not held");
            Check(DiscordHandoff.ParseGame("AIMMOD_DISCORD_GAME_1\tbogus\t" + now.ToUnixTimeSeconds() + "\n", now) == DiscordHandoff.GameState.Unknown, "Unknown state is not held");
            Check(DiscordHandoff.RequestText(now) == "AIMMOD_DISCORD_TAKEOVER_1\t" + now.ToUnixTimeSeconds() + "\n", "Request line format");
        }
        finally { Directory.Delete(folder, true); }

        // Client against a stand-in Discord.
        await using (var fake = new FakeDiscord { PingFirst = true })
        {
            await using var client = new DiscordIpcClient(DiscordPresenceHost.ClientId, new DiscordNamedPipe(fake.Prefix, 2), TimeSpan.FromSeconds(3));
            Check(await client.Connect(CancellationToken.None), "Handshake completes on READY");
            Check(fake.ClientId == DiscordPresenceHost.ClientId, "Handshake sends the AimMod application id");
            var acknowledged = await client.SetActivity(4242, playing.ToJson(), CancellationToken.None);
            Check(acknowledged == DiscordSendResult.Ok, "SET_ACTIVITY acknowledged by nonce (" + acknowledged + ": " + client.LastError + ")");
            Check(await client.SetActivity(4242, null, CancellationToken.None) == DiscordSendResult.Ok && fake.Activities[^1] is null, "Null activity clears the presence");
            // The pong went out before this second command (same write queue), so the fake has read it by now.
            Check(fake.Pongs == 1, "Ping answered with pong");
            fake.Drop();
            await Task.Delay(200);
            Check(!client.Connected, "Dropped pipe is noticed");
            Check(await client.Connect(CancellationToken.None) && fake.Connections == 2, "Reconnect after Discord restarts");
            await client.Disconnect();
            await Task.Delay(100);
            Check(fake.Closes == 1, "Disconnect sends CLOSE");
        }
        await using (var fake = new FakeDiscord { RejectHandshake = true })
        {
            await using var client = new DiscordIpcClient(DiscordPresenceHost.ClientId, new DiscordNamedPipe(fake.Prefix, 1), TimeSpan.FromSeconds(3));
            Check(!await client.Connect(CancellationToken.None) && !client.Connected, "Rejected handshake is a failed connect");
            Check(client.LastError?.Contains("4000 Invalid Client ID") == true, "Handshake rejection reason is kept");
        }
        {
            await using var client = new DiscordIpcClient(DiscordPresenceHost.ClientId, new DiscordNamedPipe("aimmod-absent-" + Guid.NewGuid().ToString("N") + "-", 10));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Check(!await client.Connect(CancellationToken.None) && watch.Elapsed < TimeSpan.FromSeconds(2), "Missing Discord fails fast");
        }

        // Host: publishes only while the game acknowledges the handoff.
        var output = Path.Combine(Path.GetTempPath(), "aimmod-discord-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        try
        {
            await using var fake = new FakeDiscord { RejectSteamButtons = true };
            var settings = new DiscordSettings(output);
            var clock = now;
            var snapshot = Live();
            var logged = new List<string>();
            await using var host = new DiscordPresenceHost(output, settings, () => snapshot, () => false, () => "synthetic-player",
                new DiscordNamedPipe(fake.Prefix, 1), () => clock, () => 4242, TimeSpan.FromHours(1), log: line => { lock (logged) logged.Add(line); });
            void Ack(string state) => File.WriteAllText(Path.Combine(output, DiscordHandoff.GameFile), $"AIMMOD_DISCORD_GAME_1\t{state}\t{clock.ToUnixTimeSeconds()}\n");
            var request = Path.Combine(output, DiscordHandoff.RequestFile);
            await host.Step(CancellationToken.None);
            Check(File.Exists(request) && host.Status == "waiting" && fake.Connections == 0, "Requests the handoff and waits for the game");
            Ack("game"); await host.Step(CancellationToken.None);
            Check(fake.Connections == 0, "Never publishes while KovaaK's presence is on");
            Ack("off"); await host.Step(CancellationToken.None);
            Check(host.Status == "game-off" && fake.Connections == 0, "Respects KovaaK's presence being turned off");
            Ack("released"); await host.Step(CancellationToken.None);
            Check(fake.Connections == 1 && host.Status == "showing" && fake.Activities.Count == 0, "Connects once the game has released its presence");
            await host.Step(CancellationToken.None);
            Check(fake.Activities.Count == 1 && fake.Activities[0]!["buttons"]!.AsArray().Count == 1 && fake.Pids[0] == 4242, "Rejected game link is dropped and the rest is shown");
            snapshot = Live(score: 850); clock = clock.AddSeconds(2); Ack("released"); await host.Step(CancellationToken.None);
            Check(fake.Activities.Count == 1, "Score-only change waits for the refresh interval");
            clock = clock.AddSeconds(15); Ack("released"); await host.Step(CancellationToken.None);
            Check(fake.Activities.Count == 2 && fake.Activities[1]!["state"]!.GetValue<string>().StartsWith("Score 850"), "Score refresh after the interval");
            snapshot = Live(paused: true); clock = clock.AddSeconds(1); Ack("released"); await host.Step(CancellationToken.None);
            Check(fake.Activities.Count == 3 && fake.Activities[2]!["timestamps"] is null, "Pause is sent promptly");
            File.Delete(Path.Combine(output, DiscordHandoff.GameFile)); clock = clock.AddSeconds(1); await host.Step(CancellationToken.None);
            Check(fake.Closes == 0 && host.Shown is not null, "A briefly missing acknowledgement does not flicker the presence");
            clock = clock.AddSeconds(1); Ack("game"); await host.Step(CancellationToken.None);
            await Task.Delay(100);
            Check(host.Shown is null, "Presence cleared when the game takes its presence back");
            Check(fake.Closes == 1 && File.Exists(request), "Connection closed while the request stays open");
            settings.ApplyJson(Encoding.UTF8.GetBytes("{\"discordPresenceEnabled\":false}"));
            clock = clock.AddSeconds(1); await host.Step(CancellationToken.None);
            Check(!File.Exists(request) && host.Status == "off", "Turning presence off withdraws the request");
            Check(logged.Contains("connected: handshake READY (user redacted)"), "Log records the handshake");
            Check(logged.Any(l => l.StartsWith("SET_ACTIVITY playing") && l.Contains("rejected (4000 invalid url); dropping the play-this-scenario button")), "Log records the rejected button with Discord's reason");
            Check(logged.Any(l => l.StartsWith("SET_ACTIVITY playing") && l.EndsWith(": ok")), "Log records accepted activities");
            Check(logged.Any(l => l.StartsWith("disconnected from Discord (game reports Game)")) && logged.Contains("handoff request withdrawn"), "Log records hand-back");
        }
        finally { Directory.Delete(output, true); }

        // Host: lobby presence, Discord joins and ask-to-join requests.
        var mpOutput = Path.Combine(Path.GetTempPath(), "aimmod-discord-mp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mpOutput);
        try
        {
            await using var fake = new FakeDiscord();
            var mpClock = now; var joins = new List<string>(); var lines = new List<string>();
            var current = new DiscordLobbyInfo("aimmod-0123456789abcdef01234567", 2, 4, "Score race", "Synthetic Track", "lobby", null, null, null, null, null, null, "aimmod1:" + new string('b', 40));
            await using var host = new DiscordPresenceHost(mpOutput, new DiscordSettings(mpOutput), () => Menu, () => false, () => null,
                new DiscordNamedPipe(fake.Prefix, 1), () => mpClock, () => 4242, TimeSpan.FromHours(1), log: l => { lock (lines) lines.Add(l); },
                lobby: () => current, join: secret => { joins.Add(secret); return (true, ""); });
            File.WriteAllText(Path.Combine(mpOutput, DiscordHandoff.GameFile), $"AIMMOD_DISCORD_GAME_1\treleased\t{mpClock.ToUnixTimeSeconds()}\n");
            await host.Step(CancellationToken.None);
            Check(fake.Commands.Contains("SUBSCRIBE ACTIVITY_JOIN") && fake.Commands.Contains("SUBSCRIBE ACTIVITY_JOIN_REQUEST"), "Subscribes to Discord join events");
            Check(fake.Activities.Count == 1 && fake.Activities[0]!["secrets"]!["join"]!.GetValue<string>() == current.JoinSecret && fake.Activities[0]!["party"] is not null, "Lobby presence published with party and join secret");
            await fake.Push("ACTIVITY_JOIN", "{\"secret\":\"aimmod1:" + new string('c', 40) + "\"}");
            await fake.Push("ACTIVITY_JOIN_REQUEST", "{\"user\":{\"id\":\"123456789012345678\",\"username\":\"synthetic\"}}");
            await Task.Delay(200);
            mpClock = mpClock.AddSeconds(1);
            File.WriteAllText(Path.Combine(mpOutput, DiscordHandoff.GameFile), $"AIMMOD_DISCORD_GAME_1\treleased\t{mpClock.ToUnixTimeSeconds()}\n");
            await host.Step(CancellationToken.None);
            Check(joins.SequenceEqual(["aimmod1:" + new string('c', 40)]), "ACTIVITY_JOIN hands the secret to the lobby service");
            Check(fake.Commands.Contains("SEND_ACTIVITY_JOIN_INVITE 123456789012345678"), "Ask to Join is accepted while the lobby is joinable");
            current = current with { JoinSecret = null };
            await fake.Push("ACTIVITY_JOIN_REQUEST", "{\"user\":{\"id\":\"123456789012345678\"}}");
            await Task.Delay(200);
            mpClock = mpClock.AddSeconds(1);
            File.WriteAllText(Path.Combine(mpOutput, DiscordHandoff.GameFile), $"AIMMOD_DISCORD_GAME_1\treleased\t{mpClock.ToUnixTimeSeconds()}\n");
            await host.Step(CancellationToken.None);
            Check(fake.Commands.Contains("CLOSE_ACTIVITY_REQUEST 123456789012345678"), "Ask to Join is declined when the lobby is not joinable");
            lock (lines) Check(!lines.Any(l => l.Contains("123456789012345678") || l.Contains("synthetic")) && lines.Any(l => l.StartsWith("ask-to-join request (user redacted)")), "Join requests are logged without the Discord user");
        }
        finally { Directory.Delete(mpOutput, true); }

        // Workspace endpoints.
        var web = Path.Combine(Path.GetTempPath(), "aimmod-discord-web-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(web);
        try
        {
            using var hub = new Hub(web, new NoNetwork(), openBrowser: _ => throw new InvalidOperationException("No browser in checks."));
            var store = new DiscordSettings(web);
            await using (var workspace = new WorkspaceHost(hub, web, discordSettings: store, discordStatus: () => new { state = "waiting" }))
            {
                await workspace.Start(CancellationToken.None);
                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
                var root = workspace.Url[..^3];
                using (var response = await http.GetAsync(root + "/discord-settings"))
                {
                    var body = await response.Content.ReadAsStringAsync();
                    Check(response.IsSuccessStatusCode && body.Contains("\"discordShowHubButton\":true") && body.Contains("\"state\":\"waiting\""), "Settings and status served through the capability");
                }
                using (var response = await http.PostAsync(root + "/discord-settings", new StringContent("{\"discordShowScore\":false}", Encoding.UTF8, "application/json")))
                    Check(response.StatusCode == System.Net.HttpStatusCode.Forbidden && store.Current.ShowScore, "Changes require the UI header");
                using (var request = new HttpRequestMessage(HttpMethod.Post, root + "/discord-settings"))
                {
                    request.Headers.Add("X-AimMod-UI", "1"); request.Content = new StringContent("{\"discordShowScore\":false}", Encoding.UTF8, "application/json");
                    using var response = await http.SendAsync(request);
                    Check(response.IsSuccessStatusCode && !store.Current.ShowScore, "Change persists through the endpoint");
                }
                async Task<System.Net.HttpStatusCode> View(string body, bool header = true)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, root + "/workspace-view");
                    if (header) request.Headers.Add("X-AimMod-UI", "1");
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using var response = await http.SendAsync(request); return response.StatusCode;
                }
                Check(await View("{\"page\":\"coaching\",\"visible\":true}", header: false) == System.Net.HttpStatusCode.Forbidden && workspace.View.Current(DateTimeOffset.UtcNow) is null, "Page report requires the UI header");
                Check(await View("{\"page\":\"../x\",\"visible\":true}") == System.Net.HttpStatusCode.BadRequest, "Invalid page rejected");
                Check(await View("{\"page\":\"coaching\",\"visible\":true}") == System.Net.HttpStatusCode.OK && workspace.View.Current(DateTimeOffset.UtcNow) == "coaching", "Page report reaches the presence");
                using (var response = await http.GetAsync(root + "/discord-settings.js"))
                    Check(response.IsSuccessStatusCode && (await response.Content.ReadAsStringAsync()).Contains("AimModDiscordSettings"), "Settings card script is embedded");
            }
        }
        finally { Directory.Delete(web, true); }
        Console.WriteLine($"{count} Discord presence checks passed.");
    }
}

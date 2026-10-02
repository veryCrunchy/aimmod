using System.Globalization;
using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

// Bots in a real lobby. The host runs them: its game draws each bot as one of AimModSteam's
// walking avatars (a stand-in peer, MultiplayerService.StandIn.cs) and traces what each can see;
// BotBrain decides orders, buys and shots; the hits go through the match's rules.
//  - bot-orders.tsv (service -> AimModSteam): per bot peer, roam / walk to a goal / hold, the point to
//    face, where to stand at a round start, the sight targets to trace, or (on a client) the pose the
//    host reported for it.
//  - bot-sight.tsv (AimModSteam -> service): each bot avatar's position and which targets it sees.
//  - "bots" (host -> all): the bots' positions 10 times a second, so every player's game draws them.
sealed partial class MultiplayerService
{
    public const string BotOrdersFile = "bot-orders.tsv", BotSightFile = "bot-sight.tsv";
    readonly BotBrain botBrain = new();
    readonly Dictionary<string, BotSight> botSight = new(StringComparer.Ordinal); // member -> latest sight
    readonly Dictionary<string, (long At, double[] Pose)> botPoses = new(StringComparer.Ordinal); // client: member -> host pose
    long botSightStamp = -1, botOrdersSeq, botPosesSentAt, botSightLoggedAt = long.MinValue / 2;
    string? lastBotOrders; bool botOrdersWritten;
    internal BotStep? LastBotStep { get; private set; }
    // Bot debug (developer menu): the bridge draws each bot's path and goal, and logs every second.
    public bool BotDebug { get; set; }
    public LobbyResult SetBotDebug(bool on) { lock (gate) { BotDebug = on; lastBotOrders = null; return LobbyResult.Success; } }

    static bool IsBot(LobbyMember m) => m.Bot is not null;
    // Members that have no machine of their own (simulated players, bots): no network, no camera.
    static bool Offline(LobbyMember m) => m.Simulated || m.Bot is not null;

    // Called every tick: the host runs its bots; a client draws the host's.
    void StepBots()
    {
        if (outputFolder is null) return;
        var lobby = Current;
        if (lobby?.Match is not { Phase: MatchPhases.Countdown or MatchPhases.Live } match || !match.Players.Any(id => lobby.Members.Any(m => m.Id == id && IsBot(m))))
        {
            if (botOrdersWritten) { botOrdersWritten = false; lastBotOrders = null; WriteBotOrders(""); }
            botSight.Clear(); botPoses.Clear(); LastBotStep = null;
            return;
        }
        ReadBotSight();
        if (core is null) { WritePuppetOrders(match); return; }
        var now = clock();
        FeedBotTracks(match, now);
        botBrain.Reset(match.Id + "#" + match.Round);
        var positions = core.CombatPositions();
        var view = match.Combat;
        var players = match.Players.Select((id, tag) =>
        {
            var alive = view?.Players.FirstOrDefault(p => p.Member == id)?.Alive ?? false;
            var team = view?.Players.FirstOrDefault(p => p.Member == id)?.Team ?? 0;
            // A bot's own position: its avatar as its game reports it, else its track.
            if (botSight.TryGetValue(id, out var s) && now - s.At <= BotBrain.SightFreshMs)
                return new BotPlayer(id, tag, s.X, s.Y, BotEye(s), team, alive, positions.TryGetValue(id, out var ps) ? ps.Speed : 0);
            return positions.TryGetValue(id, out var p) ? new BotPlayer(id, tag, p.At.X, p.At.Y, p.At.Z, team, alive, p.Speed) : null;
        }).Where(p => p is not null).Select(p => p!).ToList();
        var bots = lobby.Members.Where(m => IsBot(m) && match.Players.Contains(m.Id)).Select(m => (m.Id, m.Bot!)).ToList();
        var step = botBrain.Step(new BotWorld(now, match.Mode, bots, players, botSight, match.Cs, csObjectives, view?.Events ?? []));
        LastBotStep = step;
        foreach (var a in step.Actions) core.Apply(a.Bot, a.Action, JsonSerializer.SerializeToElement(a.Args, Protocol.Json), library);
        foreach (var shot in step.Shots) core.BotShot(shot.Bot, shot.Victim, shot.Head, shot.Slot, shot.Dir);
        if (step.Shots.Count > 0) PushCombat();
        WriteBotOrders(FormatBotOrders(step.Orders));
        // Everyone else's game draws the bots where this machine's game has them.
        if (now - botPosesSentAt >= 100)
        {
            botPosesSentAt = now;
            var rows = botSight.Where(kv => now - kv.Value.At <= 1000 && bots.Any(b => b.Id == kv.Key))
                .Select(kv => new object[] { kv.Key, R(kv.Value.X), R(kv.Value.Y), R(kv.Value.Z), R(kv.Value.Yaw) }).ToArray();
            if (rows.Length > 0) foreach (var peer in RemotePeers(lobby)) Send(peer, "bots", new { match = match.Id, round = match.Round, b = rows });
        }
        if (now - botSightLoggedAt > 30_000 && bots.Count > 0)
        {
            botSightLoggedAt = now;
            Console.Error.WriteLine("Bots: " + bots.Count + " in the match, " + botSight.Count(kv => now - kv.Value.At <= 1000) + " reported by the game"
                + (botSight.Count == 0 ? " (no " + BotSightFile + " from AimModSteam: bots walk but can't see, so they don't shoot)" : "")
                + "; orders: " + string.Join(", ", step.Orders.Select(o => (StandIns.GetValueOrDefault(o.Member) ?? "?") + " " + o.Mode
                    + (o.Goal is { Length: >= 2 } g ? " to " + R(g[0]).ToString(CultureInfo.InvariantCulture) + "," + R(g[1]).ToString(CultureInfo.InvariantCulture) : "")
                    + (o.Face is not null ? " facing" : "") + (botSight.TryGetValue(o.Member, out var at) ? " at " + R(at.X).ToString(CultureInfo.InvariantCulture) + "," + R(at.Y).ToString(CultureInfo.InvariantCulture) : ""))));
        }
    }
    static double R(double v) => Math.Round(v, 1);

    // Test hooks (checks only): a bot's track and health on the host, and a client's shot at a bot
    // the way its game would send it (its own camera track, then the hit on the ray to the bot).
    internal TrackSample? BotTrackForTest(string member) { lock (gate) return core?.CombatPositions().TryGetValue(member, out var p) == true ? p.At : null; }
    internal double BotHealthForTest(string member) { lock (gate) return Current?.Match?.Combat?.Players.FirstOrDefault(p => p.Member == member)?.Health ?? -1; }
    internal bool ClaimForTest(string bot)
    {
        lock (gate)
        {
            if (hostPeer is null || Current?.Match is not { } match || !botPoses.TryGetValue(bot, out var pose)) return false;
            var t = clock() + HostOffset();
            // Standing 6 m south of the bot, at its camera height (floor 0 in the check), looking north at it.
            var eye = DefaultBotEyeHeight;
            var samples = Enumerable.Range(0, 5).Select(i => new TrackSample(t - 200 + i * 50, pose.Pose[0], pose.Pose[1] - 600, eye, 0, 90)).ToArray();
            Send(hostPeer, "track", new TrackBatch(match.Id, match.Round, samples, []).Body());
            // Through the same outbox as every claim (resent until the host's hit-ack), decided by the host.
            SendClaim(new HitClaim(match.Id, match.Round, 1, t, pose.Pose[0], pose.Pose[1] - 600, eye, 0, 90, false, null, null, null, null, null));
            return true;
        }
    }

    // Score modes: the host plays its bots' runs (BotScorer), a score frame every 250 ms and the
    // finish when the time is up, through the same host rules as a player's frames.
    readonly Dictionary<string, (string Round, long FrameAt, bool Done)> scoreBots = new(StringComparer.Ordinal);
    void StepScoreBots()
    {
        if (core is null || Current is not { Match: { Phase: MatchPhases.Live } match } lobby || LobbyModes.Shooting(match.Mode) || match.Mode == LobbyModes.Tracking) return;
        var now = clock();
        var key = match.Id + "#" + match.Round;
        double? reference = null;
        foreach (var m in lobby.Members.Where(m => IsBot(m) && match.Players.Contains(m.Id)))
        {
            var line = match.Live.FirstOrDefault(l => l.MemberId == m.Id);
            if (line is null || line.Status is LineStates.Finished or LineStates.Left or LineStates.Dnf) continue;
            var st = scoreBots.TryGetValue(m.Id, out var have) && have.Round == key ? have : (key, 0L, false);
            if (st.Item3 || now < st.Item2) continue;
            var elapsed = Math.Max(0, (now - (match.StartsAt ?? now)) / 1000.0);
            reference ??= BotScorer.Reference(completedRuns().Where(r => r.Scenario.Equals(match.Scenario, StringComparison.OrdinalIgnoreCase)).Select(r => r.Score), match.TimeLimit);
            var (score, shots, hits) = BotScorer.At(m.Id, m.Bot!, key, reference.Value, match.TimeLimit, elapsed);
            if (elapsed >= match.TimeLimit)
            {
                core.Finish(m.Id, new RunFinish(match.Id, match.Round, score, match.TimeLimit, shots, hits, hits, null));
                st.Item3 = true;
            }
            else core.Score(m.Id, new ScoreFrame(match.Id, match.Round, Math.Round(elapsed, 2), score, shots, hits, hits, Math.Round(match.TimeLimit - elapsed, 2)));
            st.Item2 = now + 250;
            scoreBots[m.Id] = st;
        }
    }

    // A bot's track uses a player's convention: a player's track is their camera, so a bot's is its
    // floor plus the camera height of a standing player (this machine's own, once seen; CS ports
    // otherwise). A dropped bomb (CsMatch.DropBomb) and the hit checks then mean the same for both.
    public const double DefaultBotEyeHeight = TrackingRound.DefaultHalfHeight + TrackingRound.DefaultEyeAboveCentre;
    double BotEyeHeight() => poseTracker?.EyeHeight ?? (Current?.Match?.Cs is not null ? CsRules.SpawnEyeAbove + 160 : DefaultBotEyeHeight);
    internal double BotEye(BotSight s) => s.Floor is { } floor ? floor + BotEyeHeight() : s.Z + BotBrain.EyeAboveCentre;

    // The host's bots are its own simulation: their tracks come from where its game walks them
    // (bot-sight.tsv), every new report, so hits on them (from any player) validate against that.
    readonly Dictionary<string, long> botTrackFed = new(StringComparer.Ordinal);
    void FeedBotTracks(MatchSnapshot match, long now)
    {
        if (core is null) return;
        foreach (var (member, s) in botSight)
        {
            if (!match.Players.Contains(member) || now - s.At > BotBrain.SightFreshMs || botTrackFed.GetValueOrDefault(member) == s.At) continue;
            botTrackFed[member] = s.At;
            core.Track(member, new TrackBatch(match.Id, match.Round, [new TrackSample(now, s.X, s.Y, BotEye(s), 0, s.Yaw)], []));
        }
    }

    // Client: the host's bot positions.
    void ReceiveBots(JsonElement body)
    {
        try
        {
            if (Current?.Match is not { } match || body.GetProperty("match").GetString() != match.Id) return;
            var now = clock();
            foreach (var row in body.GetProperty("b").EnumerateArray().Take(16))
            {
                if (row.GetArrayLength() != 5 || row[0].GetString() is not { Length: > 0 and <= 64 } member) continue;
                var pose = new[] { row[1].GetDouble(), row[2].GetDouble(), row[3].GetDouble(), row[4].GetDouble() };
                if (pose.All(v => double.IsFinite(v) && Math.Abs(v) < 1e7)) botPoses[member] = (now, pose);
            }
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException) { }
    }

    void WritePuppetOrders(MatchSnapshot match)
    {
        var now = clock();
        var rows = new List<string>();
        foreach (var (member, peer) in StandIns)
            if (botPoses.TryGetValue(member, out var p) && now - p.At <= 1500)
                rows.Add("pose\t" + peer + "\t" + string.Join("\t", p.Pose.Select(v => v.ToString("0.#", CultureInfo.InvariantCulture))));
        WriteBotOrders(rows.Count == 0 ? "" : string.Join("\n", rows) + "\n");
    }

    string FormatBotOrders(IReadOnlyList<BotOrder> orders)
    {
        static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
        var sb = new System.Text.StringBuilder();
        if (BotDebug) sb.Append("debug\t1\n");
        foreach (var o in orders)
        {
            if (!StandIns.TryGetValue(o.Member, out var peer)) continue;
            sb.Append("bot\t").Append(peer).Append('\t').Append(o.Mode);
            if (o.Goal is { Length: >= 3 } g)
            {
                sb.Append('\t').Append(F(g[0])).Append('\t').Append(F(g[1])).Append('\t').Append(F(g[2]));
                if (o.Stop is > 0 and < 1) sb.Append('\t').Append(o.Stop.ToString("0.##", CultureInfo.InvariantCulture));
            }
            sb.Append('\n');
            if (o.Via is { Length: >= 4 } via) sb.Append("via\t").Append(peer).Append('\t').Append(F(via[0])).Append('\t').Append(F(via[1])).Append('\t').Append(F(via[2])).Append('\t').Append(via[3].ToString("0.##", CultureInfo.InvariantCulture)).Append('\n');
            if (o.Fight > 0) sb.Append("fight\t").Append(peer).Append('\t').Append(o.Fight.ToString("0.##", CultureInfo.InvariantCulture)).Append('\n');
            if (o.Turn > 0) sb.Append("turn\t").Append(peer).Append('\t').Append(F(o.Turn)).Append('\n');
            if (o.Face is { Length: >= 3 } f) sb.Append("face\t").Append(peer).Append('\t').Append(F(f[0])).Append('\t').Append(F(f[1])).Append('\t').Append(F(f[2])).Append('\n');
            if (o.PlaceToken is { } token && o.PlaceAt is { Length: >= 3 } at)
                sb.Append("place\t").Append(peer).Append('\t').Append(token).Append('\t').Append(F(at[0])).Append('\t').Append(F(at[1])).Append('\t').Append(F(at[2])).Append('\t').Append(F(at.Length > 3 ? at[3] : 0)).Append('\n');
            foreach (var (tag, eye) in o.Sight) sb.Append("sight\t").Append(peer).Append('\t').Append(tag).Append('\t').Append(F(eye[0])).Append('\t').Append(F(eye[1])).Append('\t').Append(F(eye[2])).Append('\n');
        }
        return sb.ToString();
    }

    // Rewritten on change and every second (AimModSteam ignores orders older than 3 s).
    long botOrdersWrittenAt;
    void WriteBotOrders(string body)
    {
        if (outputFolder is null) return;
        var now = clock();
        if (body == lastBotOrders && now - botOrdersWrittenAt < 1000) return;
        lastBotOrders = body; botOrdersWrittenAt = now; botOrdersWritten = body.Length > 0;
        try { AtomicFile.WriteText(Path.Combine(outputFolder, BotOrdersFile), "AIMMOD_BOTS_1\t" + ++botOrdersSeq + "\n" + body); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // bot-sight.tsv: "AIMMOD_BOTSIGHT_1\t<unix ms>", then "bot\t<peer>\tx\ty\tz\tyaw" (capsule centre)
    // and "seen\t<peer>\t<tag>\t0|1" rows.
    void ReadBotSight()
    {
        var path = Path.Combine(outputFolder!, BotSightFile);
        try
        {
            if (!File.Exists(path)) return;
            var info = new FileInfo(path);
            if (info.Length > 64 * 1024) return;
            var stamp = info.LastWriteTimeUtc.Ticks;
            if (stamp == botSightStamp) return;
            botSightStamp = stamp;
            var parsed = ParseBotSight(File.ReadAllText(path), clock());
            if (parsed is null) return;
            var byPeer = StandIns.ToDictionary(kv => kv.Value, kv => kv.Key);
            foreach (var (peer, sight) in parsed) if (byPeer.TryGetValue(peer, out var member)) botSight[member] = sight;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal static Dictionary<string, BotSight>? ParseBotSight(string text, long now)
    {
        var lines = text.Replace("\r", "").Split('\n');
        if (lines.Length == 0 || !lines[0].StartsWith("AIMMOD_BOTSIGHT_1\t", StringComparison.Ordinal)) return null;
        if (!long.TryParse(lines[0][18..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var at)) return null;
        // A stale file (the game stopped writing it) says nothing.
        if (Math.Abs(now - at) > 5000) return null;
        var pos = new Dictionary<string, double[]>(); var seen = new Dictionary<string, HashSet<int>>();
        static double? Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) && Math.Abs(v) < 1e7 ? v : null;
        foreach (var line in lines.Skip(1).Take(256))
        {
            var p = line.Split('\t');
            // bot\t<peer>\tx\ty\tz\tyaw[\tfloor]: the floor column is newer (a bridge without it gives 6).
            if (p.Length is 6 or 7 && p[0] == "bot" && p[1].Length is > 0 and <= 2 && Num(p[2]) is { } x && Num(p[3]) is { } y && Num(p[4]) is { } z && Num(p[5]) is { } yaw)
                pos[p[1]] = p.Length == 7 && Num(p[6]) is { } fl && fl <= z ? [x, y, z, yaw, fl] : [x, y, z, yaw];
            else if (p.Length == 4 && p[0] == "seen" && int.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out var tag) && tag < 64 && p[3] is "0" or "1")
            {
                if (!seen.TryGetValue(p[1], out var set)) seen[p[1]] = set = [];
                if (p[3] == "1") set.Add(tag);
            }
        }
        return pos.ToDictionary(kv => kv.Key, kv => new BotSight(at, kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3], seen.TryGetValue(kv.Key, out var s) ? s : new HashSet<int>(), kv.Value.Length > 4 ? kv.Value[4] : null));
    }
}

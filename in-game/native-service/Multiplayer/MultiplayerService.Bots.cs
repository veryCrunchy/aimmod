using System.Globalization;
using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

// Bots in a real lobby. The host runs them: its game draws each bot as one of AimModSteam's
// walking avatars (a stand-in peer, MultiplayerService.StandIn.cs) and traces what each can see;
// BotBrain decides orders, buys and shots; the hits go through the match's rules.
//  - bot-orders.tsv (service -> AimModSteam): per bot peer, roam / walk to a goal / hold, the point to
//    face, where to stand at a round start, the sight targets to trace, or (on a client) the pose the
//    host reported for it.
//  - bot-sight.tsv (AimModSteam -> service): each bot avatar's position, which targets it sees and how
//    far it sees straight ahead.
//  - bot-spots.tsv (AimModSteam -> service): the holding spots it worked out on its nav grid for the
//    areas the orders ask for (a site, the planted bomb; BotPositions.cs).
//  - "bots" (host -> all): the bots' positions 10 times a second, so every player's game draws them.
sealed partial class MultiplayerService
{
    public const string BotOrdersFile = "bot-orders.tsv", BotSightFile = "bot-sight.tsv";
    readonly BotBrain botBrain = new();
    readonly Dictionary<string, BotSight> botSight = new(StringComparer.Ordinal); // member -> latest sight
    readonly Dictionary<string, (long At, double[] Pose)> botPoses = new(StringComparer.Ordinal); // client: member -> host pose
    Dictionary<string, SpotArea> botAreas = new(StringComparer.Ordinal); long botSpotsStamp = -1;
    long botSightStamp = -1, botOrdersSeq, botPosesSentAt, botSightLoggedAt = long.MinValue / 2;
    string? lastBotOrders; bool botOrdersWritten;
    internal BotStep? LastBotStep { get; private set; }
    // Bot callouts in chat (BotCallouts.cs): posts a line as that bot (member, team only, text); the
    // chat adds the place. Null: no callouts.
    public Action<string, bool, string>? BotSay { get; set; }
    readonly BotCallouts botCallouts = new();
    readonly List<BotEvent> botThrowEvents = [];
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
        ReadBotSpots();
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
                return new BotPlayer(id, tag, s.X, s.Y, BotEye(s), team, alive, s.Speed ?? (positions.TryGetValue(id, out var ps) ? ps.Speed : 0), s.Crouch);
            return positions.TryGetValue(id, out var p) ? new BotPlayer(id, tag, p.At.X, p.At.Y, p.At.Z, team, alive, p.Speed) : null;
        }).Where(p => p is not null).Select(p => p!).ToList();
        var bots = lobby.Members.Where(m => IsBot(m) && match.Players.Contains(m.Id)).Select(m => (m.Id, m.Bot!)).ToList();
        // Smoke takes sight away (BotVision: through the cloud as it grows, both ways), a flash is the
        // brain's (BotTactics); they hear everyone's shots (the shot log); bot grenades go after the
        // brain's own step, its calls first.
        shotLog.Reset(ShotKey(match));
        var world = new BotWorld(now, match.Mode, bots, players, BotVision.Sight(match.Cs, botSight, players, now), match.Cs, csObjectives, view?.Events ?? [], shotLog.Recent.ToArray(), botAreas);
        var step = botBrain.Step(world);
        StepBotUtility(world, step.Utility ?? []);
        LastBotStep = step;
        LogBots(step, now);
        SayBots(match, world, step, now);
        foreach (var a in step.Actions) core.Apply(a.Bot, a.Action, JsonSerializer.SerializeToElement(a.Args, Protocol.Json), library);
        RecordBotFire(match, step, now); // before the hits: a killing shot still sounds
        foreach (var shot in step.Shots) core.BotShot(shot.Bot, shot.Victim, shot.Head, shot.Slot, shot.Dir);
        if (step.Shots.Count > 0) PushCombat();
        PushShots();
        WriteBotOrders(FormatBotOrders(step.Orders) + BotAreas.Format(step.Areas ?? []));
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
                + "; orders: " + string.Join(", ", step.Orders.Select(o => (StandIns.GetValueOrDefault(o.Member) ?? "?") + " " + o.Mode + (o.Role is { } role ? " (" + role + ")" : "")
                    + (o.Goal is { Length: >= 2 } g ? " to " + R(g[0]).ToString(CultureInfo.InvariantCulture) + "," + R(g[1]).ToString(CultureInfo.InvariantCulture) : "")
                    + (o.Face is not null ? " facing" : "") + (botSight.TryGetValue(o.Member, out var at) ? " at " + R(at.X).ToString(CultureInfo.InvariantCulture) + "," + R(at.Y).ToString(CultureInfo.InvariantCulture) : ""))));
        }
    }
    static double R(double v) => Math.Round(v, 1);

    // Bot grenades: the policy (MultiplayerService.Grenades.cs BotGrenadePolicy, else BotGrenades)
    // with the brain's own calls this step.
    void StepBotUtility(BotWorld world, IReadOnlyList<BotUtilityRequest> requests)
    {
        if (core is null || world.Cs is null) return;
        var step = (BotGrenadePolicy ?? botGrenades).Step(world, requests);
        foreach (var (bot, item) in step.Buys) core.Apply(bot, "buy", JsonSerializer.SerializeToElement(new { item }, Protocol.Json), library);
        foreach (var t in step.Throws)
            if (core.BotThrow(t.Bot, t.Kind, t.From, t.Velocity) is { Ok: true })
            {
                if (requests.FirstOrDefault(r => r.Bot == t.Bot && r.Kind == t.Kind) is { } why) Console.Error.WriteLine("[bots] " + BotName(t.Bot) + " throws a " + t.Kind + ": " + why.Why);
                // Its callout: what it throws and where (fire on a planted bomb is "on the bomb").
                var onBomb = world.Cs!.Bomb is { State: "planted", Position: { Length: 3 } b } && Math.Sqrt(Math.Pow(t.Target[0] - b[0], 2) + Math.Pow(t.Target[1] - b[1], 2)) < 400;
                botThrowEvents.Add(new BotEvent(t.Bot, "utility", 0, t.Target, onBomb ? "the bomb" : BotCallouts.Place(world.Map, t.Target), t.Kind, world.Now));
            }
    }

    // Callouts: the brain's events and the throws, through the callouts' chattiness, rate limit and
    // de-duplication, to chat as each bot (BotSay; none without it).
    void SayBots(MatchSnapshot match, BotWorld world, BotStep step, long now)
    {
        var thrown = botThrowEvents.ToArray();
        botThrowEvents.Clear();
        if (BotSay is not { } say || match.Cs is not { } cs) return;
        string Team(string m) => cs.Players.FirstOrDefault(p => p.Member == m)?.Side ?? "?";
        string? Skill(string m) => world.Bots.FirstOrDefault(b => b.Member == m).Skill;
        foreach (var line in botCallouts.Step(now, (step.Events ?? []).Concat(thrown), Team, Skill))
        {
            say(line.Bot, line.TeamOnly, line.Text);
            if (BotDebug) Console.Error.WriteLine("[bots] " + BotName(line.Bot) + (line.TeamOnly ? " (team): " : " (all): ") + line.Text);
        }
    }

    // The bot log: what the brain noticed (decoy verdicts, smoke calls, flashes) as it happens, and
    // with bot debug on, every bot's senses once a second.
    long botDebugLoggedAt = long.MinValue / 2;
    string BotName(string member) => Current?.Members.FirstOrDefault(m => m.Id == member)?.Name ?? member;
    void LogBots(BotStep step, long now)
    {
        foreach (var line in botBrain.Notes.Take(32)) Console.Error.WriteLine("[bots] " + NameIn(line));
        botBrain.Notes.Clear();
        if (!BotDebug || now - botDebugLoggedAt < 1000 || step.Debug is not { Count: > 0 } debug) return;
        botDebugLoggedAt = now;
        foreach (var d in debug) Console.Error.WriteLine("[bots] " + BotDebugLine(d with { Bot = BotName(d.Bot) }));
    }
    // A note names bots by member id first: their lobby names instead.
    string NameIn(string line)
    {
        var space = line.IndexOf(' ');
        var colon = line.IndexOf(':');
        var end = space < 0 ? colon : colon < 0 ? space : Math.Min(space, colon);
        return end > 0 ? BotName(line[..end]) + line[end..] : line;
    }
    internal static string BotDebugLine(BotDebugInfo d) =>
        d.Bot + ": " + d.Role + " | " + d.Move
        + (d.Heard.Count > 0 ? " | heard " + string.Join(", ", d.Heard) : "")
        + (d.Decoys.Count > 0 ? " | sources " + string.Join("; ", d.Decoys) : "")
        + (d.Smoke is { } sm ? " | smoke: " + sm : "")
        + (d.Blind > 0.05 ? " | blind " + d.Blind.ToString("0.00", CultureInfo.InvariantCulture) : "")
        + (d.Flash is { } fl ? " | " + fl : "")
        + (d.Spot is { } sp ? " | " + sp : "")
        + (d.Look is { } lk ? " | looking at " + lk : "");
    // The developer menu's bot list (bot debug): the same, one entry per bot.
    internal object? BotDebugView() => LastBotStep?.Debug?.Select(d => new
    {
        name = BotName(d.Bot), role = d.Role, move = d.Move, heard = d.Heard, sources = d.Decoys, smoke = d.Smoke, blind = Math.Round(d.Blind, 2), flash = d.Flash, spot = d.Spot, look = d.Look,
    }).ToArray();

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
    // The hull a bot's track stands for (TrackBatch.Hull): its avatar as this machine's game draws it
    // (the capsule the game hit-tests) when seen, and the camera height above that capsule's centre,
    // so the host turns the track's camera Z back into the drawn centre and head (CS: about 167 cm).
    internal double[] BotBody(string member, BotSight s)
    {
        var drawn = poseTracker?.LastSeen.Values.FirstOrDefault(v => v.Member == member);
        var centre = drawn?.Z ?? s.Z;
        return [BotEye(s) - centre, drawn?.Radius ?? TrackingRound.DefaultRadius, drawn?.HalfHeight ?? (s.Floor is { } floor && s.Z - floor is > 20 and < 400 ? s.Z - floor : TrackingRound.DefaultHalfHeight)];
    }

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
            core.Track(member, new TrackBatch(match.Id, match.Round, [new TrackSample(now, s.X, s.Y, BotEye(s), 0, s.Yaw)], [], BotBody(member, s)));
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
        var cs = Current?.Match?.Cs;
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
            if (o.Gait != "run" || o.Stance != "stand" || o.PreAim) sb.Append("move\t").Append(peer).Append('\t').Append(o.Gait).Append('\t').Append(o.Stance).Append('\t').Append(o.PreAim ? 1 : 0).Append('\n');
            if (o.Peek is { } peek && o.PeekAt is { Length: >= 3 } pa) sb.Append("peek\t").Append(peer).Append('\t').Append(peek).Append('\t').Append(F(pa[0])).Append('\t').Append(F(pa[1])).Append('\t').Append(F(pa[2])).Append('\n');
            if (o.Accel > 0) sb.Append("aim\t").Append(peer).Append('\t').Append(F(o.Accel)).Append('\t').Append(o.Overshoot.ToString("0.##", CultureInfo.InvariantCulture)).Append('\n');
            foreach (var a in (o.Avoid ?? []).Take(8))
                if (a.Length >= 5) sb.Append("avoid\t").Append(peer).Append('\t').Append(F(a[0])).Append('\t').Append(F(a[1])).Append('\t').Append(F(a[2])).Append('\t').Append(F(a[3])).Append('\t').Append(F(a[4])).Append('\n');
            if (o.Via is { Length: >= 4 } via) sb.Append("via\t").Append(peer).Append('\t').Append(F(via[0])).Append('\t').Append(F(via[1])).Append('\t').Append(F(via[2])).Append('\t').Append(via[3].ToString("0.##", CultureInfo.InvariantCulture)).Append('\n');
            if (o.Fight > 0) sb.Append("fight\t").Append(peer).Append('\t').Append(o.Fight.ToString("0.##", CultureInfo.InvariantCulture)).Append(o.FightStyle is { } style ? "\t" + style : "").Append('\n');
            if (o.Turn > 0) sb.Append("turn\t").Append(peer).Append('\t').Append(F(o.Turn)).Append('\n');
            // CS: the bot walks at the speed of the weapon in its hand, as a player does (CsFeel).
            if (cs?.Players.FirstOrDefault(p => p.Member == o.Member) is { Alive: true } held)
                sb.Append("speed\t").Append(peer).Append('\t').Append(CsFeel.SpeedShare(CsFeel.ById(held.Holding)).ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
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

    // bot-spots.tsv: the holding spots AimModSteam worked out (BotAreas.Parse), read when it changes.
    void ReadBotSpots()
    {
        var path = Path.Combine(outputFolder!, BotAreas.FileName);
        try
        {
            if (!File.Exists(path)) return;
            var info = new FileInfo(path);
            if (info.Length > 256 * 1024 || info.LastWriteTimeUtc.Ticks == botSpotsStamp) return;
            botSpotsStamp = info.LastWriteTimeUtc.Ticks;
            if (BotAreas.Parse(File.ReadAllText(path)) is { } areas) botAreas = areas;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // bot-sight.tsv: "AIMMOD_BOTSIGHT_1\t<unix ms>", then "bot\t<peer>\tx\ty\tz\tyaw" (capsule centre),
    // "vel\t<peer>\t<speed cm/s>\t<crouch 0|1>" (newer bridges), "look\t<peer>\t<cm clear ahead>" and
    // "seen\t<peer>\t<tag>\t0|1" rows.
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
        var pos = new Dictionary<string, double[]>(); var seen = new Dictionary<string, HashSet<int>>(); var vel = new Dictionary<string, (double Speed, bool Crouch)>();
        var look = new Dictionary<string, double>();
        static double? Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) && Math.Abs(v) < 1e7 ? v : null;
        foreach (var line in lines.Skip(1).Take(256))
        {
            var p = line.Split('\t');
            // bot\t<peer>\tx\ty\tz\tyaw[\tfloor]: the floor column is newer (a bridge without it gives 6).
            if (p.Length is 6 or 7 && p[0] == "bot" && p[1].Length is > 0 and <= 2 && Num(p[2]) is { } x && Num(p[3]) is { } y && Num(p[4]) is { } z && Num(p[5]) is { } yaw)
                pos[p[1]] = p.Length == 7 && Num(p[6]) is { } fl && fl <= z ? [x, y, z, yaw, fl] : [x, y, z, yaw];
            else if (p.Length == 4 && p[0] == "vel" && p[1].Length is > 0 and <= 2 && Num(p[2]) is { } speed && speed is >= 0 and < 20_000 && p[3] is "0" or "1")
                vel[p[1]] = (speed, p[3] == "1");
            else if (p.Length == 3 && p[0] == "look" && p[1].Length is > 0 and <= 2 && Num(p[2]) is { } ahead && ahead is >= 0 and < 100_000)
                look[p[1]] = ahead;
            else if (p.Length == 4 && p[0] == "seen" && int.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out var tag) && tag < 64 && p[3] is "0" or "1")
            {
                if (!seen.TryGetValue(p[1], out var set)) seen[p[1]] = set = [];
                if (p[3] == "1") set.Add(tag);
            }
        }
        return pos.ToDictionary(kv => kv.Key, kv => new BotSight(at, kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3], seen.TryGetValue(kv.Key, out var s) ? s : new HashSet<int>(), kv.Value.Length > 4 ? kv.Value[4] : null,
            vel.TryGetValue(kv.Key, out var v) ? v.Speed : null, vel.TryGetValue(kv.Key, out var c) && c.Crouch, look.TryGetValue(kv.Key, out var l) ? l : null));
    }
}

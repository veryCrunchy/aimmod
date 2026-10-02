using System.Globalization;

namespace AimMod.InGame.Multiplayer;

// Hit registration (game-modes.md 6.2.2): lag-compensated validation against timestamped tracks,
// the shared head zone, frame-jitter fire rate, trades, claims waiting for their track, repeats
// answered once, and the shot feed never losing a shot. Synthetic tracks only.
static partial class MultiplayerChecks
{
    static void HitRegistration(string root)
    {
        BotHulls();
        const long t0 = 9_000_000;
        // a at the origin looking along +x; b 10 m away, its eye 64 cm above its capsule centre (z 100).
        CombatMatch Arena(string mode = LobbyModes.Deathmatch, Func<long, double>? bY = null, long until = t0 + 9_000, double aYaw = 0)
        {
            var c = new CombatMatch(mode, ["a", "b"], CombatRules.DefaultFragLimit(mode), 0, t0, t0 + 60_000);
            var a = new List<TrackSample>(); var b = new List<TrackSample>();
            for (long t = t0 - 500; t < until; t += 17) { a.Add(new(t, 0, 0, 164, 0, aYaw)); b.Add(new(t, 1000, bY?.Invoke(t) ?? 0, 164, 0, 180)); }
            for (var i = 0; i < a.Count; i += 60) { c.Track("a", new TrackBatch("m", 1, a.Skip(i).Take(60).ToList(), [])); c.Track("b", new TrackBatch("m", 1, b.Skip(i).Take(60).ToList(), [])); }
            return c;
        }
        long seq = 0;
        HitClaim Shot(long t, double yaw = 0, double pitch = 0, double tx = 1000, double ty = 0, double tz = 100, bool head = false, bool gameHit = false, double? damage = null, double radius = 45, double half = 115) =>
            new("m", 1, ++seq, t, 0, 0, 164, pitch, yaw, head, tx, ty, tz, radius, half, 0, 1000 + seq, gameHit, damage, 1);
        double Yaw(double y) => Math.Atan2(y, 1000) * 180 / Math.PI;
        double Pitch(double z) => Math.Atan2(z - 164, 1000) * 180 / Math.PI;
        var at = t0 + 2000;

        // Rewind: b strafes at 250 cm/s; a's game draws it 350 ms late (a remote bot's relay delay).
        static double Strafe(long t) => (t - t0) * 0.25;
        var drawnBack = Strafe(at - 350);
        var lagged = Arena(bY: Strafe, aYaw: Yaw(drawnBack));
        Check(lagged.Claim("a", Shot(at, yaw: Yaw(drawnBack), ty: drawnBack), at + 40, 40) == "target-mismatch" && lagged.LastDetail!.Contains("rewind cap 200 ms"),
            "Without evidence of the shooter's view delay, a target drawn 350 ms back is past the 200 ms rewind");
        var measured = Arena(bY: Strafe, aYaw: Yaw(drawnBack));
        // a's own game reports where it drew b (tagged rows): 350 ms behind b's track.
        var drawn = new List<TrackSeen>();
        for (long t = at - 1500; t < at; t += 33) drawn.Add(new TrackSeen(t, 7, 1000, Strafe(t - 350), 100, 45, 115, "b"));
        measured.Track("a", new TrackBatch("m", 1, [], drawn));
        Check(measured.Claim("a", Shot(at, yaw: Yaw(drawnBack), ty: drawnBack), at + 40, 40) is null && measured.LastDetail!.StartsWith("drawn hull, 35", StringComparison.Ordinal),
            "With the shooter's drawn rows the host measures its view delay and rewinds b that far (favour the shooter)");
        Check(measured.Claim("a", Shot(at + 300, yaw: Yaw(Strafe(at - 400)), ty: Strafe(at - 400)), at + 340, 40) == "target-mismatch",
            "but never past the measured delay plus 100 ms (and never past 500 ms)");
        // The host's own rewound test: a drawn target a little off (60 cm) whose ray goes through b's rewound hull.
        var rewound = Arena();
        Check(rewound.Claim("a", Shot(at, ty: 60), at + 30, 40) is null && rewound.LastDetail!.StartsWith("rewound", StringComparison.Ordinal),
            "A drawn target off the track (but near it) falls back to the host's rewound ray test");

        // A shot timed between two 60 Hz samples of a fast flick: inside the camera's turn, not its average.
        var flick = new CombatMatch(LobbyModes.Deathmatch, ["a", "b"], 20, 0, t0, t0 + 60_000);
        var sweep = new List<TrackSample>(); var still = new List<TrackSample>();
        long flickAt = -1;
        for (long t = t0 - 500; t < t0 + 4000; t += 17)
        {
            if (flickAt < 0 && t >= at) flickAt = t;
            sweep.Add(new(t, 0, 0, 164, 0, flickAt >= 0 && t > flickAt ? 20 : -20)); still.Add(new(t, 1000, 0, 164, 0, 180));
        }
        for (var i = 0; i < sweep.Count; i += 60) { flick.Track("a", new TrackBatch("m", 1, sweep.Skip(i).Take(60).ToList(), [])); flick.Track("b", new TrackBatch("m", 1, still.Skip(i).Take(60).ToList(), [])); }
        Check(flick.Claim("a", Shot(flickAt + 4), flickAt + 40, 40) is null, "A flick shot whose aim lies inside the camera's turn between samples is accepted");
        Check(flick.Claim("a", Shot(flickAt + 200, yaw: 30), flickAt + 240, 40) == "aim" && flick.LastDetail!.StartsWith("yaw 10", StringComparison.Ordinal), "An aim outside the turn is refused, with how far off");

        // The head zone: the same rule as AimModCore (60 % of the half height above the centre: z 169).
        var zones = Arena();
        Check(zones.Claim("a", Shot(at, pitch: Pitch(175)), at + 10, 40) is null && zones.View().Events[^1] is { Head: true, Amount: 40 }, "Above the zone line: a headshot, double damage");
        Check(zones.Claim("a", Shot(at + 150, pitch: Pitch(162)), at + 160, 40) is null && zones.View().Events[^1] is { Head: false, Amount: 20 }, "Below it: a body hit");
        Check(zones.Claim("a", Shot(at + 300, pitch: Pitch(167), head: true), at + 310, 40) is null && zones.View().Events[^1].Head, "AimModCore's head flag holds within 4 cm of the line");
        var gameHead = Arena();
        Check(gameHead.Claim("a", Shot(at, pitch: Pitch(140), gameHit: true, damage: 40), at + 10, 40) is null && gameHead.View().Events[^1] is { Head: true, Amount: 40 },
            "The game's own headshot (its damage shows the multiplier) counts in the upper body");
        Check(gameHead.Claim("a", Shot(at + 150, pitch: Pitch(140), gameHit: true, damage: 20), at + 160, 40) is null && !gameHead.View().Events[^1].Head, "and its body damage stays a body hit");

        // A game hit just beside the drawn capsule (the visible mesh is wider): accepted; a ray-only one isn't.
        var beside = Arena();
        Check(beside.Claim("a", Shot(at, yaw: Yaw(52)), at + 10, 40) == "ray-miss" && beside.LastDetail!.Contains("outside the drawn hull"), "A ray 7 cm beside the hull misses");
        Check(beside.Claim("a", Shot(at + 150, yaw: Yaw(52), gameHit: true), at + 160, 40) is null, "but a hit the game counted there lands");

        // Fire rate with frame jitter: 85 ms apart is a 100 ms weapon timed by frames; sustained it isn't.
        var jitter = Arena();
        var landed = Enumerable.Range(0, 5).Select(k => jitter.Claim("a", Shot(at + k * 85), at + k * 85 + 5, 40)).ToList();
        Check(landed.All(r => r is null), "Hits 85 ms apart (frame jitter on a 100 ms weapon) all land");
        Check(jitter.Claim("a", Shot(at + 5 * 85), at + 5 * 85 + 5, 40) == "fire-rate" && jitter.LastDetail!.Contains("for 5 shot(s)"), "Kept up over five shots it is faster than the weapon: refused");
        var sameFrame = Arena();
        Check(sameFrame.Claim("a", Shot(at), at + 5, 40) is null && sameFrame.Claim("a", Shot(at), at + 6, 40) == "fire-rate", "Two hits at one instant are one too many");

        // Trades: a shot fired before the host killed the shooter still lands.
        var trade = Arena(LobbyModes.Instagib);
        HitClaim Back(long t) => new("m", 1, ++seq, t, 1000, 0, 164, 0, 180, false, 0, 0, 100, 45, 115);
        Check(trade.Claim("a", Shot(at), at + 5, 40) is null && !trade.Alive("b"), "b dies at the host");
        Check(trade.Claim("b", Back(at - 30), at + 60, 40) is null && !trade.Alive("a"), "b's shot fired 30 ms before its death still lands (a trade)");
        var late = Arena(LobbyModes.Instagib);
        late.Claim("a", Shot(at), at + 5, 40);
        Check(late.Claim("b", Back(at + 30), at + 60, 40) == "shooter-dead" && late.LastDetail!.Contains("30 ms after"), "A shot fired after the death doesn't");

        // A claim ahead of the shooter's own camera samples waits for them; one whose samples never come is refused.
        var waiting = Arena(until: at - 100);
        Check(waiting.Claim("a", Shot(at), at + 20, 40) == "pending" && waiting.PendingClaims == 1 && waiting.View().Players.First(p => p.Member == "b").Health == 100, "A claim past the shooter's track waits");
        waiting.Track("a", new TrackBatch("m", 1, Enumerable.Range(0, 20).Select(k => new TrackSample(at - 90 + k * 17, 0, 0, 164, 0, 0)).ToList(), []));
        waiting.Track("b", new TrackBatch("m", 1, Enumerable.Range(0, 20).Select(k => new TrackSample(at - 90 + k * 17, 1000, 0, 164, 0, 180)).ToList(), []));
        waiting.ProcessPending(at + 120);
        var decided = waiting.TakeVerdicts();
        Check(decided.Single() is { Reason: null, WaitedMs: 100, Damage: 20 } && waiting.PendingClaims == 0, "and is decided once they arrive");
        Check(waiting.Claim("a", Shot(at + 1000), at + 1010, 40) == "pending", "A claim with no track to come waits too");
        waiting.ProcessPending(at + 1010 + CombatRules.DeferMs - 1);
        Check(waiting.PendingClaims == 1, "for at most DeferMs");
        waiting.ProcessPending(at + 1010 + CombatRules.DeferMs);
        Check(waiting.TakeVerdicts().Single() is { Reason: "no-shooter-track" } v && v.Detail!.Contains("before the shot"), "then it is refused as no-shooter-track");

        // Repeats (a resend after a lost acknowledgement) get the first decision and do nothing again.
        var repeat = Arena();
        var first = Shot(at);
        Check(repeat.Claim("a", first, at + 5, 40) is null && repeat.Claim("a", first, at + 600, 40) == "repeated" && repeat.View().Players.First(p => p.Member == "b").Health == 80,
            "A repeated claim never applies twice");
        var answers = repeat.TakeVerdicts();
        Check(answers.Count == 2 && answers[1] is { Duplicate: true, Reason: null, Damage: 20 } && repeat.ClaimStats("a") is { Claims: 1, Accepted: 1, Duplicates: 1 },
            "and is answered with the first decision");
        var counted = Arena();
        counted.Claim("a", Shot(at, yaw: 20), at + 5, 40); counted.Claim("a", Shot(at + 200, ty: 300), at + 205, 40); counted.Claim("a", Shot(at + 400), at + 405, 40);
        Check(counted.ClaimStats("a") is { Claims: 3, Accepted: 1 } cs && cs.Reasons["aim"] == 1 && cs.Reasons["target-mismatch"] == 1, "Refusals are counted by reason");

        // The client's outbox: resent until answered, then given up past the claim window.
        var outbox = new ClaimOutbox();
        outbox.Reset("m#1");
        outbox.Add(Shot(at), 0); outbox.Add(Shot(at + 100), 0);
        Check(outbox.Due(ClaimOutbox.ResendMs - 1).Count == 0 && outbox.Due(ClaimOutbox.ResendMs).Count == 2, "Unanswered claims are sent again");
        Check(outbox.Confirm(seq - 1, null) && !outbox.Confirm(seq - 1, null) && outbox.Accepted == 1, "An answer closes a claim once");
        outbox.Due(ClaimOutbox.GiveUpMs + 1);
        Check(outbox.Open == 0 && outbox.Unanswered == 1, "A claim nobody answers is given up after the window");
        // The lobby answers claims it can't take (between CS rounds, another match) too.
        var (core, clock, advance) = Lobby();
        core.Join("p2", "Two");
        core.Apply("host", "settings", Patch(new { mode = "deathmatch", countdown = 3 }), new FakeContent());
        ReadyAll(core); core.Apply("host", "start", default, new FakeContent()); advance(3000); core.Tick();
        core.Claim("p2", new HitClaim("other-match", 1, 5, clock(), 0, 0, 164, 0, 0, false, null, null, null, null, null));
        Check(core.TakeClaimVerdicts().Single() is { Shooter: "p2", Seq: 5, Reason: "stale" }, "A claim for another match is answered as stale");

        // The shot feed: a burst far beyond the old 32-shot window is taken whole, a gap is counted,
        // stale shots aren't claimed, and the request file acknowledges the last shot taken.
        var folder = Path.Combine(root, "hits");
        Directory.CreateDirectory(folder);
        var feed = new ShotFeed(folder);
        var now = 1_790_000_010_000L;
        string Row(long ms, long n, bool hit) => string.Join('\t', "shot", ms.ToString(CultureInfo.InvariantCulture), n.ToString(CultureInfo.InvariantCulture), "0", "0", "164", "1", "0", "0", "0", "7", "0", hit ? "1" : "0",
            "1000", "0", "100", "34", "96", hit ? "36" : "-1", "1") + "\n";
        var burst = "AIMMOD_SHOTS_1\t1\t1790000000000\n" + string.Concat(Enumerable.Range(1, 120).Select(n => Row(now - 900 + n, n, n % 3 != 0)));
        var taken = feed.Take(ShotFeed.Parse(burst), "m", 1, 0, (_, _) => null, now);
        Check(taken.Count == 80 && feed.Stats is { Shots: 120, GameHits: 80, RayOnly: 40, Lost: 0 } && taken[0] is { Seq: 1, Shot: 1, GameHit: true, GameDamage: 36, TargetRadius: 34, TargetHalfHeight: 96 },
            "A 120-shot burst is taken whole: every game hit claimed with its drawn capsule, ray-only shots after the game's counter spoke are not");
        var next = "AIMMOD_SHOTS_1\t2\t1790000000000\n" + Row(now - 100, 119, true) + Row(now - 90, 120, true) + Row(now - 50, 124, true) + Row(now - 5000, 125, true);
        var more = feed.Take(ShotFeed.Parse(next), "m", 1, 0, (_, _) => null, now);
        Check(more.Count == 1 && more[0] is { Shot: 124, Seq: 81 } && feed.Stats.Lost == 3 && feed.Stats.Stale == 1, "Only new shots are claimed; a gap counts as lost and an old shot as stale");
        feed.Request(now);
        var request = File.ReadAllText(Path.Combine(folder, "self-shots.request")).Split('\t');
        Check(request.Length == 3 && request[1] == "125" && request[2] == "1790000000000", "The request acknowledges the last shot taken, for AimModCore's session");
        Check(ShotFeed.Parse("AIMMOD_SHOTS_1\t1\t5\n" + string.Concat(Enumerable.Range(1, ShotFeed.MaxShots + 1).Select(n => Row(now, n, true)))) is null
            && ShotFeed.Parse("AIMMOD_SHOTS_1\t1\t5\n" + string.Concat(Enumerable.Range(1, ShotFeed.MaxShots).Select(n => Row(now, n, true)))) is { Shots.Count: ShotFeed.MaxShots },
            "AimModCore's whole unacknowledged log (256 shots) fits the feed");

        // Drawn positions of the publications between two polls (seen rows) reach the track.
        var tracker = new SelfPoseTracker(folder);
        var frame = LivePoseFrame.Parse("AIMMOD_POSE_1\t4\nmeta\tA\tm\t1\npose\t2000\t0\t0\t164\t0\t0\t0\t90\n"
            + "seen\t1934\t7\t100\t0\t100\t45\t115\nseen\t1967\t7\t110\t0\t100\t45\t115\ntarget\t7\t120\t0\t100\t45\t115\n");
        tracker.Take(frame, 0);
        Check(frame!.Seen.Count == 2 && tracker.SeenAt(7, 1950) is { X: > 104 and < 106 } && tracker.Drain("m", 1).Single().Seen.Count == 3, "Seen rows fill the drawn history between polls");
    }

    // End to end on the host: AimModCore's files in, the host's decisions out. The host shoots its
    // simulated opponent (a stand-in whose track is where the host's game draws it) with a stream of
    // game hits; AimModCore keeps every shot until the service acknowledges it.
    static void HitPipeline(string root)
    {
        long now = 9_700_000;
        var game = Path.Combine(root, "game");
        var output = Path.Combine(root, "hits-output");
        Directory.CreateDirectory(output);
        var control = new FakeGame("load", "start") { Root = game };
        var service = new MultiplayerService(new OfflineTransport(), new ContentLibrary(game), control, () => new LocalRun(false, null, null, null, null, 0, 0, 0, null), () => [], () => null, output, simulation: true, () => now, autoTick: false, seed: 31);
        service.Act("create", J(new { mode = "deathmatch", scenario = "Synthetic A" }));
        service.Act("sim", J(new { op = "add" }));
        void Run(int ms, Action? each = null) { for (var t = 0; t < ms; t += 100) { now += 100; each?.Invoke(); service.Tick(); } }
        Run(12_000);
        Check(service.Act("start", default).Ok, "A deathmatch against one simulated player starts");
        string? Phase() => System.Text.Json.JsonSerializer.SerializeToElement(service.View(), Protocol.Json).GetProperty("lobby").GetProperty("match") is { ValueKind: System.Text.Json.JsonValueKind.Object } m ? m.GetProperty("phase").GetString() : null;
        for (var i = 0; i < 300 && Phase() != MatchPhases.Live; i++) Run(100);
        // AimModCore: the camera at the origin looking along +x, the stand-in drawn 10 m ahead.
        long poseSeq = 0, shotSeq = 0, published = 0; var shots = new List<string>(); var acked = 0L;
        var stream = StreamIds.For(MultiplayerService.StandInPeer);
        void Core()
        {
            var pose = new System.Text.StringBuilder("AIMMOD_POSE_1\t" + ++poseSeq + "\nmeta\tA\tm\t1\n");
            for (var k = 15; k >= 0; k--) pose.Append("pose\t" + (now - k * 16).ToString(CultureInfo.InvariantCulture) + "\t0\t0\t164\t0\t0\t0\t90\n");
            pose.Append("target\t7\t1000\t0\t100\t45\t115\ntag\t7\t" + stream + "\n");
            File.WriteAllText(Path.Combine(output, "self-pose.tsv"), pose.ToString());
            // The service's acknowledgement drops what it took, as AimModCore's ShotLog does.
            var request = Path.Combine(output, "self-shots.request");
            if (File.Exists(request) && File.ReadAllText(request).Split('\t') is { Length: 3 } r && r[2] == "4242") acked = long.Parse(r[1], CultureInfo.InvariantCulture);
            shots.RemoveAll(s => long.Parse(s.Split('\t')[2], CultureInfo.InvariantCulture) <= acked);
        }
        void Fire()
        {
            shots.Add(string.Join('\t', "shot", (now - 5).ToString(CultureInfo.InvariantCulture), (++shotSeq).ToString(CultureInfo.InvariantCulture), "0", "0", "164", "1", "0", "0", "0", "7", "0", "1", "1000", "0", "100", "45", "115", "20", "1"));
            File.WriteAllText(Path.Combine(output, "self-shots.tsv"), "AIMMOD_SHOTS_1\t" + ++published + "\t4242\n" + string.Join("\n", shots) + "\n");
        }
        Run(2000, Core); // past spawn protection
        Run(3000, () => { Core(); Fire(); });
        Run(600, Core);
        var stats = service.HostClaimStats;
        var me = stats.TryGetValue(service.SelfId, out var mine) ? mine : default;
        Check(service.ShotStats is { Shots: 30, GameHits: 30, Claims: 30, Lost: 0 } && me.Claims == 30 && me.Pending == 0, "All 30 shots reach the host as claims and every one is decided");
        Check(me.Accepted >= 5 && me.Accepted + me.Reasons.Values.Sum() == 30 && me.Reasons.Keys.All(k => k is "victim-dead" or "spawn-protected"),
            "The game hits on the drawn stand-in land; the rest are refused only while it is down or protected: " + string.Join(", ", me.Reasons.Select(kv => kv.Key + " " + kv.Value)));
        Check(acked == 30 && shots.Count == 0, "The service acknowledged every shot, so AimModCore's log is empty again");
    }

    // Bot tracks follow the player convention: the floor plus a standing camera height. In CS (the
    // scaled ports) that camera is about 167 cm above the bot's capsule centre, elsewhere 64 cm. Every
    // path must turn the track back into the drawn hull: the drawn target's height check, the rewound
    // ray test, the hull size cap and the head zone.
    static void BotHulls()
    {
        const long t0 = 9_900_000;
        foreach (var (mode, eye, radius, half) in new[] { (LobbyModes.Cs, CsRules.SpawnEyeAbove + 160.0, 60.0, 145.0), (LobbyModes.Deathmatch, MultiplayerService.DefaultBotEyeHeight, 45.0, 115.0) })
        {
            var weapon = mode == LobbyModes.Cs ? CsRules.Find("ak47")!.Combat : CombatRules.Rifle;
            // The bot stands on the floor (z 0) 10 m ahead: its capsule centre is at its half height.
            var centre = half; var above = eye - centre; var at = t0 + 3000;
            double PitchTo(double z) => Math.Atan2(z - eye, 1000) * 180 / Math.PI;
            // The shooter's own camera looks where it shoots.
            CombatMatch Arena(double aimZ = 0)
            {
                var c = new CombatMatch(mode, ["shooter", "bot"], 20, 0, t0, t0 + 60_000);
                if (mode == LobbyModes.Cs) c.WeaponFor = (_, _) => weapon;
                var a = new List<TrackSample>(); var b = new List<TrackSample>();
                for (long t = t0; t < t0 + 6000; t += 17) { a.Add(new(t, 0, 0, eye, aimZ == 0 ? 0 : PitchTo(aimZ), 0)); b.Add(new(t, 1000, 0, eye, 0, 180)); }
                c.Track("shooter", new TrackBatch("m", 1, a, [], [above, radius, half]));
                c.Track("bot", new TrackBatch("m", 1, b, [], [above, radius, half]));
                return c;
            }
            HitClaim Shot(double z, bool drawn) => new("m", 1, 1, at, 0, 0, eye, PitchTo(z), 0, false, drawn ? 1000 : null, drawn ? 0 : null, drawn ? centre : null, drawn ? radius : null, drawn ? half : null);
            var headZ = centre + half * 0.8; var bodyZ = centre + half * 0.2; var overZ = centre + half + 10;
            foreach (var drawn in new[] { true, false })
            {
                var path = drawn ? "drawn" : "rewound";
                var head = Arena(headZ);
                Check(head.Claim("shooter", Shot(headZ, drawn), at + 20, 40) is null && head.View().Events.Last(e => e.Kind == "damage") is { Head: true } h && h.Amount == Math.Min(100, weapon.Damage * weapon.HeadMultiplier)
                    && head.LastDetail!.StartsWith(path, StringComparison.Ordinal), mode + ": a head shot on a bot is accepted on the " + path + " path, as a headshot (" + head.LastDetail + ")");
                var body = Arena(bodyZ);
                Check(body.Claim("shooter", Shot(bodyZ, drawn), at + 20, 40) is null && body.View().Events.Last(e => e.Kind == "damage") is { Head: false } bd && bd.Amount == weapon.Damage,
                    mode + ": a body shot on a bot is accepted on the " + path + " path as a body hit (" + body.LastDetail + ")");
                var over = Arena(overZ);
                Check(over.Claim("shooter", Shot(overZ, drawn), at + 20, 40) is not null and not "pending" && over.View().Players.First(p => p.Member == "bot").Health == 100,
                    mode + ": a shot 10 cm over the bot's head is refused on the " + path + " path (" + over.LastDetail + ")");
            }
            Check(Arena().Body("bot") is { } hull && Math.Abs(hull.EyeAbove - above) < 0.01 && hull.Half == half, mode + ": the bot's track carries its hull (camera " + Math.Round(above) + " cm above the centre)");
        }
    }
}
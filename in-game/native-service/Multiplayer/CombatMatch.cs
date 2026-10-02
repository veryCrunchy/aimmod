using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AimMod.InGame.Multiplayer;

// Phase 2 combat modes (in-game/docs/game-modes.md 6.2, 6.4, 6.5): deathmatch,
// vampiric 1v1 and instagib. The host owns health, deaths, frags and respawns.
// Clients only claim hits; the host validates every claim against the
// shooter's and the victim's own pose tracks with a 200 ms rewind cap
// (the same model as the tracking duel) and computes the damage itself.

// The weapon each mode plays with. The generated arena ships exactly this
// profile, so the host knows the fire rate and damage without trusting a client.
// Range: the reach in cm (a knife), 0 for hitscan across the map.
// FollowUpDamage: what a hit does within FollowUpMs of the same shooter's last hit with this weapon
// (the CS knife: 40, then 25 for slashes in quick succession); 0 for none.
sealed record CombatWeapon(string Name, double Damage, double HeadMultiplier, double TimeBetweenShots, bool FullyAuto, double KnockbackVertical = 0, double Range = 0,
    double FollowUpDamage = 0, long FollowUpMs = 0)
{
    // Reach the host allows past Range: the 200 ms rewind at a run.
    public const double RangeToleranceCm = 60;
    public double RayLength => Range > 0 ? Range + RangeToleranceCm : TrackingRound.RayLengthCm;
}

static class CombatRules
{
    public static readonly CombatWeapon Rifle = new("AimMod Combat Rifle", 20, 2, 0.1, true);
    public static readonly CombatWeapon Railgun = new("AimMod Railgun", 1000, 1, 1.2, false, 0);
    public static CombatWeapon Weapon(string mode) => mode == LobbyModes.Instagib ? Railgun : Rifle;
    public const double MaxHealth = 100, VampiricOverheal = 0, VampiricDecayPerSecond = 2, VampiricHealthOnKill = 25;
    public static long RespawnMs(string mode) => mode == LobbyModes.Instagib ? 1000 : 2000;
    public const long SpawnProtectionMs = 1500;
    // Hit registration (game-modes.md 6.2.2):
    //  - ClaimWindowMs: how old a claim may be when the host decides it; FutureMs: how far ahead of the
    //    host clock (clock-offset error) it may be.
    //  - DeferMs: a claim the shooter's own track doesn't cover yet waits this long for it.
    //  - MaxRewindMs: the furthest back a drawn target may be matched (the measured view delay of
    //    that shooter for that victim plus 100 ms, never under TrackingRound.RewindCapMs).
    //  - FrameSlackMs: fire-rate allowance for shots timed by the frame they were counted in.
    //  - TradeWindowMs: a shooter the host killed may still land shots fired before that death.
    //  - Ray tolerances: rounding (4 cm), and for a shot the game itself counted as a hit, the
    //    visible mesh beyond the capsule (15 cm).
    //  - FallbackMatchCm: a drawn target this far from the victim's rewound track still lets the
    //    host's own rewound ray test decide.
    public const long ClaimWindowMs = 1500, FutureMs = 200, DeferMs = 300, MaxRewindMs = 500, FrameSlackMs = 20, TradeWindowMs = 300;
    public const double RayToleranceCm = 4, GameHitToleranceCm = 15, FallbackMatchCm = 150, AimToleranceDeg = 3, OriginToleranceCm = 32;
    // The head zone (AimModCore's IsHeadHit): at least 60 % of the half height above the hull's centre.
    public const double HeadZoneFraction = 0.6;
    public static int DefaultFragLimit(string mode) => mode switch { LobbyModes.Vampiric => 10, LobbyModes.Instagib => 25, LobbyModes.TeamDeathmatch => 50, _ => 20 };
    // Team deathmatch: teams by join order, alternating (1, 2, 1, 2 ...), so sizes differ by at most one.
    public static Dictionary<string, int> Teams(IEnumerable<string> players) => players.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i % 2 + 1);
    public const int DefaultMatchSeconds = 300;
}

// A hit the shooter's game registered: host-clock time, the shot's camera ray,
// whether AimModCore's ray put it in the head zone, and where the game drew the target it hit.
// Seq: the shooter's claim number for this match (the host dedupes and acknowledges by it);
// Shot: AimModCore's shot number (diagnostics); GameHit: the game's own hit counter counted it;
// GameDamage: the damage per hit the game counted (its headshot multiplier shows in it);
// Source: how AimModCore found the target (1 ray on the capsule, 2 just beside it, 3 named by the game).
// Spread, SpreadShot (CS): the bullet's inaccuracy (rad) and the shot whose seed drew its offset; the host
// turns the camera ray (Pitch, Yaw) by that offset itself (CsFeel) and validates the hit on that ray.
sealed record HitClaim(string MatchId, int Round, long Seq, long T, double X, double Y, double Z, double Pitch, double Yaw, bool Head,
    double? TargetX, double? TargetY, double? TargetZ, double? TargetRadius, double? TargetHalfHeight, int Slot = 0,
    long Shot = 0, bool GameHit = false, double? GameDamage = null, int Source = 0, double? Spread = null, long? SpreadShot = null)
{
    public object Body() => new
    {
        match = MatchId, round = Round, seq = Seq, t = T, o = new[] { R(X), R(Y), R(Z) }, r = new[] { R(Pitch), R(Yaw) }, head = Head, w = Slot,
        target = TargetX is null ? null : new[] { R(TargetX.Value), R(TargetY!.Value), R(TargetZ!.Value), R(TargetRadius!.Value), R(TargetHalfHeight!.Value) },
        shot = Shot, gh = GameHit, gd = GameDamage is { } d ? R(d) : (double?)null, src = Source,
        sp = Spread is { } spread ? new[] { Math.Round(spread * 1000, 4), SpreadShot ?? Shot } : null,
    };
    static double R(double v) => Math.Round(v, 2);

    public static HitClaim? Read(JsonElement b)
    {
        try
        {
            var match = b.GetProperty("match").GetString(); var round = b.GetProperty("round").GetInt32();
            var seq = b.GetProperty("seq").GetInt64(); var t = b.GetProperty("t").GetInt64();
            if (match is not { Length: > 0 and <= 40 } || round is < 1 or > 100 || seq < 0) return null;
            static double[]? Nums(JsonElement e, int n)
            {
                if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != n) return null;
                var r = new double[n]; var i = 0;
                foreach (var x in e.EnumerateArray()) { if (x.ValueKind != JsonValueKind.Number || !x.TryGetDouble(out r[i]) || !double.IsFinite(r[i]) || Math.Abs(r[i]) > 1e7) return null; i++; }
                return r;
            }
            var o = Nums(b.GetProperty("o"), 3); var r = Nums(b.GetProperty("r"), 2);
            if (o is null || r is null || Math.Abs(r[0]) > 90.5 || Math.Abs(r[1]) > 720) return null;
            var head = b.TryGetProperty("head", out var h) && h.ValueKind == JsonValueKind.True;
            double[]? target = null;
            if (b.TryGetProperty("target", out var tg) && tg.ValueKind != JsonValueKind.Null)
            {
                target = Nums(tg, 5);
                if (target is null || target[3] is <= 0 or > 1000 || target[4] < target[3] || target[4] > 2000) return null;
            }
            var slot = b.TryGetProperty("w", out var w) && w.TryGetInt32(out var sv) ? sv : 0;
            if (slot is < 0 or > 7) return null;
            var shot = b.TryGetProperty("shot", out var sh) && sh.TryGetInt64(out var shv) && shv >= 0 ? shv : 0;
            var gameHit = b.TryGetProperty("gh", out var gh) && gh.ValueKind == JsonValueKind.True;
            double? gameDamage = b.TryGetProperty("gd", out var gd) && gd.ValueKind == JsonValueKind.Number && gd.TryGetDouble(out var gdv) && double.IsFinite(gdv) && gdv is >= 0 and <= 100_000 ? gdv : null;
            var source = b.TryGetProperty("src", out var src) && src.TryGetInt32(out var srcv) && srcv is >= 0 and <= 3 ? srcv : 0;
            double? spread = null; long? spreadShot = null;
            if (b.TryGetProperty("sp", out var sp) && sp.ValueKind != JsonValueKind.Null)
            {
                if (Nums(sp, 2) is not { } spv || spv[0] is < 0 or > 1000 || spv[1] < 0 || spv[1] != Math.Truncate(spv[1])) return null;
                spread = spv[0] / 1000; spreadShot = (long)spv[1];
            }
            return new HitClaim(match, round, seq, t, o[0], o[1], o[2], r[0], r[1], head, target?[0], target?[1], target?[2], target?[3], target?[4], slot, shot, gameHit, gameDamage, source, spread, spreadShot);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }
}

// What clients mirror: each player's health, life and score, and the recent events.
// Team: 0 in free-for-all modes, 1 or 2 in team deathmatch. Spawn on a respawn event: [x, y, z, yaw]
// the host chose (null: the game's own spawn). TeamFrags: team 1 and team 2 totals.
sealed record CombatPlayerView(string Member, double Health, bool Alive, int Frags, int Deaths, long? RespawnAt, long? ProtectedUntil, int Claims, int Rejected, int Team = 0);
sealed record CombatEvent(long Id, string Kind, long T, string Member, string? Attacker, double Amount, bool Head, double Health, double? AttackerHealth, double[]? Spawn = null, double[]? Dir = null);
sealed record CombatView(int FragLimit, IReadOnlyList<CombatPlayerView> Players, IReadOnlyList<CombatEvent> Events, IReadOnlyList<int>? TeamFrags = null, int? WinnerTeam = null);
// A spawn point of the arena (world units). TeamMask: bit 1 = team 1, bit 2 = team 2.
sealed record SpawnPoint(double X, double Y, double Z, double Yaw, int TeamMask);

// The host's decision on one claim, for the shooter's confirmation ("hit-ack") and the logs.
// Reason null: accepted (Damage, Head and Kill say what it did). Duplicate: a claim already decided
// (a resend); Reason is then the first decision's.
sealed record ClaimVerdict(string Shooter, long Seq, long Shot, long T, string? Reason, string? Detail, string? Victim = null, double Damage = 0, bool Head = false,
    bool Kill = false, bool Duplicate = false, long WaitedMs = 0);

sealed class CombatMatch
{
    sealed class Player
    {
        public required string Id;
        public double Health = CombatRules.MaxHealth; public bool Alive = true; public int Frags, Deaths, Claims, Rejected, Team;
        public long? RespawnAt; public long ProtectedUntil; public long LastShot = long.MinValue; public long DecayAt;
        public long? DiedAt; // host time of the shot that killed this player (trades)
        public string? LastWeapon; // the weapon of the last accepted hit (follow-up damage)
        public readonly List<TrackSample> Track = [];
        // Accepted hits (time, the weapon's minimum interval in ms) for the fire-rate check.
        public readonly List<(long T, double Interval)> Hits = [];
        // Claims already decided (resends are answered, never applied twice).
        public readonly Dictionary<long, ClaimVerdict> Decided = new();
        public readonly Queue<long> DecidedOrder = new();
        // What this player's game drew of each other player (member -> rows), for its view delay.
        public readonly Dictionary<string, List<TrackSeen>> Drawn = new();
        public readonly Dictionary<string, (long At, double? Lag)> Lag = new();
        public readonly Dictionary<string, int> Reasons = new();
        public int Accepted, Pending, Duplicates;
        // The body behind the track: how far the camera sits above the capsule centre, and the capsule
        // (reported with the track; the defaults are AimMod's standing avatar).
        public double EyeAbove = TrackingRound.DefaultEyeAboveCentre; public double? BodyRadius, BodyHalf;
    }
    readonly Dictionary<string, Player> players = new();
    readonly List<CombatEvent> events = [];
    readonly List<(string From, HitClaim Claim, long Received, int? Rtt)> pending = [];
    readonly List<ClaimVerdict> verdicts = [];
    long eventId;
    public string Mode { get; }
    public int FragLimit { get; }
    public long Start { get; }
    public long End { get; }
    public CombatWeapon Weapon { get; }
    // Round-based modes (CS) set these: no respawns, a weapon per shooter and slot (null: that
    // slot is empty), a damage model (armour), and a hook on every kill.
    public bool Respawns { get; set; } = true;
    public Func<string, int, CombatWeapon?>? WeaponFor { get; set; }
    // CS: the weapon's feel (spread, movement inaccuracy) for a shooter's slot; null: no spread (the camera ray).
    public Func<string, int, CsFeelSpec?>? SpreadFor { get; set; }
    public Func<string, double, bool, CombatWeapon, double>? DamageModel { get; set; }
    public Action<string, string, CombatWeapon, bool>? OnKill { get; set; }
    // Team damage as a share of the normal damage (0: off). CS2 bullets: 33 %.
    public double TeamDamage { get; set; }
    public const double CsTeamDamage = 0.33;
    public bool Alive(string id) => players.TryGetValue(id, out var p) && p.Alive;
    public double Health(string id) => players.TryGetValue(id, out var p) ? p.Health : 0;
    // The latest camera sample of a player (round modes: zone and bomb checks).
    public TrackSample? Position(string id) => players.TryGetValue(id, out var p) && p.Track.Count > 0 ? p.Track[^1] : null;
    // Horizontal speed over the last 200 ms (cm/s), from the player's own track.
    public double Speed(string id)
    {
        if (!players.TryGetValue(id, out var p) || p.Track.Count < 2) return 0;
        var last = p.Track[^1]; var before = At(p.Track, last.T - 200) ?? p.Track[0];
        var dt = Math.Max(1, last.T - before.T) / 1000.0;
        return Math.Sqrt((last.X - before.X) * (last.X - before.X) + (last.Y - before.Y) * (last.Y - before.Y)) / dt;
    }
    // Velocity over the last `spanMs` (cm/s: x, y, z) from the player's own track: what a grenade
    // thrown on the run or in a jump carries. Zero without two samples close enough.
    public double[] Velocity(string id, long spanMs = 100)
    {
        if (!players.TryGetValue(id, out var p) || p.Track.Count < 2) return [0, 0, 0];
        var last = p.Track[^1]; var before = At(p.Track, last.T - spanMs) ?? p.Track[^2];
        var dt = (last.T - before.T) / 1000.0;
        if (dt < 0.02 || dt > 0.5) return [0, 0, 0];
        return [(last.X - before.X) / dt, (last.Y - before.Y) / dt, (last.Z - before.Z) / dt];
    }
    // How the shooter moved at a shot, from its own track: horizontal speed (u/s, CsFeel.UnitCm) and
    // whether it was airborne. Benefit of the doubt: the slower of two windows around the shot, and
    // airborne only when both windows rise or fall fast.
    (double Speed, bool Air) MotionAt(Player p, long t)
    {
        (double H, double V)? Window(long from, long to)
        {
            if (At(p.Track, from) is not { } a || At(p.Track, to) is not { } b || to - from < 20) return null;
            var dt = (to - from) / 1000.0;
            return (Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y)) / dt, Math.Abs(b.Z - a.Z) / dt);
        }
        var w1 = Window(t - 100, t); var w2 = Window(t - 50, t + 50);
        if (w1 is null && w2 is null) return (0, false);
        var h = Math.Min(w1?.H ?? double.MaxValue, w2?.H ?? double.MaxValue);
        var air = (w1?.V ?? 0) > CsFeel.AirSpeedCm && (w2?.V ?? w1?.V ?? 0) > CsFeel.AirSpeedCm;
        return (h / CsFeel.UnitCm, air);
    }
    public (double Speed, bool Air) MotionAt(string id, long t) => players.TryGetValue(id, out var p) ? MotionAt(p, t) : (0, false);

    // Round start: everyone alive at full health, spawn-protected for a moment, scores kept.
    public void Revive(string id, long now, double health, long protectMs = 0)
    {
        if (!players.TryGetValue(id, out var p)) return;
        p.Alive = true; p.Health = health; p.RespawnAt = null; p.ProtectedUntil = now + protectMs; p.LastShot = long.MinValue; p.DiedAt = null; p.Hits.Clear();
    }
    public void SetTeam(string id, int team) { if (players.TryGetValue(id, out var p)) p.Team = team; }
    public void Kill(string id, long now) { if (players.TryGetValue(id, out var p) && p.Alive) { p.Alive = false; p.Health = 0; p.RespawnAt = null; p.DiedAt = now; Emit("death", now, id, null, 0, false, 0, null); } }
    public double Lifesteal { get; }
    // The arena's spawn points (parsed from the generated scenario's map); empty: the game's own spawns.
    public IReadOnlyList<SpawnPoint> Spawns { get; set; } = [];
    public bool Teams { get; }
    public CombatMatch(string mode, IEnumerable<string> ids, int fragLimit, double lifestealPercent, long start, long end, IReadOnlyDictionary<string, int>? teams = null)
    {
        Mode = mode; FragLimit = fragLimit; Start = start; End = end; Weapon = CombatRules.Weapon(mode);
        Lifesteal = mode == LobbyModes.Vampiric ? Math.Clamp(lifestealPercent, 0, 200) / 100.0 : 0;
        Teams = mode == LobbyModes.TeamDeathmatch;
        var list = ids.ToList();
        var assigned = Teams ? teams ?? CombatRules.Teams(list) : null;
        foreach (var id in list) players[id] = new Player { Id = id, ProtectedUntil = start + CombatRules.SpawnProtectionMs, DecayAt = start, Team = assigned?.GetValueOrDefault(id, 1) ?? 0 };
    }

    public IReadOnlyList<CombatEvent> EventsSince(long id) => events.Where(e => e.Id > id).ToArray();
    public long LatestEvent => events.Count > 0 ? events[^1].Id : 0;
    int TeamFrags(int team) => players.Values.Where(p => p.Team == team).Sum(p => p.Frags);
    public int? WinnerTeam => !Teams ? null : TeamFrags(1) >= FragLimit ? 1 : TeamFrags(2) >= FragLimit ? 2 : null;
    // The player (FFA) or the top player of the team (TDM) that reached the frag limit.
    public string? Leader => Teams
        ? WinnerTeam is { } team ? players.Values.Where(p => p.Team == team).OrderByDescending(p => p.Frags).First().Id : null
        : players.Values.OrderByDescending(p => p.Frags).ThenBy(p => p.Deaths).FirstOrDefault(p => p.Frags >= FragLimit)?.Id;
    public CombatView View() => new(FragLimit, players.Values.Select(p => new CombatPlayerView(p.Id, Math.Round(p.Health, 1), p.Alive, p.Frags, p.Deaths, p.RespawnAt,
        p.ProtectedUntil, p.Claims, p.Rejected, p.Team)).ToArray(), events.TakeLast(16).ToArray(), Teams ? [TeamFrags(1), TeamFrags(2)] : null, WinnerTeam);
    public int TeamOf(string id) => players.TryGetValue(id, out var p) ? p.Team : 0;
    public (int Frags, int Deaths, int Claims, int Rejected)? Score(string id) => players.TryGetValue(id, out var p) ? (p.Frags, p.Deaths, p.Claims, p.Rejected) : null;

    // Every player's own camera track (same batches as the tracking duel), and where its game drew
    // the others (tagged drawn targets), from which the host measures that player's view delay.
    public void Track(string from, TrackBatch batch)
    {
        if (!players.TryGetValue(from, out var p)) return;
        if (batch.Hull is [var eye, var radius, var half] && eye is >= 20 and <= 400 && radius is >= 10 and <= 200 && half >= radius && half <= 400)
        { p.EyeAbove = eye; p.BodyRadius = radius; p.BodyHalf = half; }
        foreach (var s in batch.Samples)
        {
            if (s.T < Start - 1000 || s.T > End + 1000) continue;
            if (p.Track.Count > 0 && s.T <= p.Track[^1].T) continue;
            p.Track.Add(s);
        }
        // Only the last 10 s are needed for rewinds and checks (claims are at most 1.5 s old).
        if (p.Track.Count > 0)
        {
            var cut = p.Track.FindIndex(x => x.T >= p.Track[^1].T - 10_000);
            if (cut > 0) p.Track.RemoveRange(0, cut);
        }
        foreach (var v in batch.Seen)
        {
            if (v.Member is not { } member || member == from || !players.ContainsKey(member)) continue;
            if (!p.Drawn.TryGetValue(member, out var rows)) p.Drawn[member] = rows = [];
            if (rows.Count > 0 && v.T <= rows[^1].T) continue;
            rows.Add(v);
            var cut = rows.FindIndex(x => x.T >= v.T - 3000);
            if (cut > 0) rows.RemoveRange(0, cut);
        }
    }

    static TrackSample? At(List<TrackSample> track, double t)
    {
        if (track.Count == 0 || t < track[0].T - TrackingRound.GapMs || t > track[^1].T + TrackingRound.GapMs) return null;
        int lo = 0, hi = track.Count - 1, i = -1;
        while (lo <= hi) { var mid = (lo + hi) / 2; if (track[mid].T <= t) { i = mid; lo = mid + 1; } else hi = mid - 1; }
        if (i < 0) return track[0];
        if (i == track.Count - 1) return track[i];
        var a = track[i]; var b = track[i + 1];
        if (b.T - a.T > 250) return t - a.T <= TrackingRound.GapMs ? a : null;
        var u = (t - a.T) / (b.T - a.T);
        return new TrackSample((long)t, a.X + (b.X - a.X) * u, a.Y + (b.Y - a.Y) * u, a.Z + (b.Z - a.Z) * u, a.Pitch + (b.Pitch - a.Pitch) * u, a.Yaw + ((b.Yaw - a.Yaw + 540) % 360 - 180) * u);
    }

    // The two samples around t (the same one twice at the ends), null without a usable one.
    static (TrackSample A, TrackSample B)? Around(List<TrackSample> track, long t)
    {
        if (track.Count == 0 || t < track[0].T - TrackingRound.GapMs || t > track[^1].T + TrackingRound.GapMs) return null;
        var i = track.FindLastIndex(s => s.T <= t);
        if (i < 0) return (track[0], track[0]);
        if (i == track.Count - 1) return (track[i], track[i]);
        var a = track[i]; var b = track[i + 1];
        if (b.T - a.T > 250) return t - a.T <= TrackingRound.GapMs ? (a, a) : null;
        return (a, b);
    }
    static double AngleDiff(double a, double b) => ((a - b) % 360 + 540) % 360 - 180;
    // How far an angle lies outside the arc a..b (degrees, 0 inside): the camera turned between samples.
    static double OutsideArc(double c, double a, double b)
    {
        var span = AngleDiff(b, a); var off = AngleDiff(c, a);
        var lo = Math.Min(0, span); var hi = Math.Max(0, span);
        return off < lo ? lo - off : off > hi ? off - hi : 0;
    }

    void Emit(string kind, long t, string member, string? attacker, double amount, bool head, double health, double? attackerHealth, double[]? spawn = null, double[]? dir = null) =>
        events.Add(new CombatEvent(++eventId, kind, t, member, attacker, Math.Round(amount, 1), head, Math.Round(health, 1), attackerHealth is { } a ? Math.Round(a, 1) : null, spawn, dir));

    // The spawn for a respawning player: allowed for its team, farthest from the
    // nearest living opponent (ties: the first listed). Null when the arena has none.
    public SpawnPoint? ChooseSpawn(string id)
    {
        if (!players.TryGetValue(id, out var me)) return null;
        var allowed = Spawns.Where(s => me.Team == 0 || (s.TeamMask & me.Team) != 0 || s.TeamMask == 0).ToList();
        if (allowed.Count == 0) allowed = Spawns.ToList();
        if (allowed.Count == 0) return null;
        var enemies = players.Values.Where(p => p != me && p.Alive && (me.Team == 0 || p.Team != me.Team) && p.Track.Count > 0).Select(p => p.Track[^1]).ToList();
        if (enemies.Count == 0) return allowed[0];
        return allowed.OrderByDescending(s => enemies.Min(e => Math.Sqrt((e.X - s.X) * (e.X - s.X) + (e.Y - s.Y) * (e.Y - s.Y) + (e.Z - s.Z) * (e.Z - s.Z)))).First();
    }

    // ---- hit claims ---------------------------------------------------------------------------
    // Decided claims since the last call (accepted, rejected and resends), oldest first.
    public IReadOnlyList<ClaimVerdict> TakeVerdicts() { var list = verdicts.ToArray(); verdicts.Clear(); return list; }
    public int PendingClaims => pending.Count;
    // The last decision's detail (what the check measured), for tests and logs.
    public string? LastDetail { get; private set; }
    // Per shooter: claims, accepted, rejected by reason, waiting, resends.
    public (int Claims, int Accepted, int Pending, int Duplicates, IReadOnlyDictionary<string, int> Reasons)? ClaimStats(string id) =>
        players.TryGetValue(id, out var p) ? (p.Claims, p.Accepted, p.Pending, p.Duplicates, new Dictionary<string, int>(p.Reasons)) : null;

    // Why a claim was refused (null: accepted, "pending": waiting for the shooter's own track to
    // reach the shot; decided by ProcessPending). The host decides who was hit, where and how hard;
    // the claim says when the shooter fired, along which ray, and what its game drew there.
    public string? Claim(string from, HitClaim c, long now, int? shooterRtt)
    {
        if (!players.TryGetValue(from, out var shooter)) return Decide(null, from, c, now, now, "not-playing", null);
        if (shooter.Decided.TryGetValue(c.Seq, out var before))
        {
            shooter.Duplicates++;
            verdicts.Add(before with { Duplicate = true });
            LastDetail = "already decided: " + (before.Reason ?? "accepted");
            return "repeated";
        }
        if (pending.Any(x => x.From == from && x.Claim.Seq == c.Seq)) { shooter.Duplicates++; return "pending"; }
        shooter.Claims++;
        // The shooter's own track doesn't reach the shot yet (its camera samples travel separately):
        // wait for it, at most DeferMs.
        if ((shooter.Track.Count == 0 || shooter.Track[^1].T < c.T) && c.T <= now + CombatRules.FutureMs && c.T >= now - CombatRules.ClaimWindowMs)
        {
            pending.Add((from, c, now, shooterRtt));
            shooter.Pending++;
            return "pending";
        }
        return Evaluate(shooter, c, now, now, shooterRtt);
    }

    // Claims waiting for their shooter's track: decided once it covers the shot, or after DeferMs.
    public void ProcessPending(long now)
    {
        for (var i = 0; i < pending.Count;)
        {
            var (from, c, received, rtt) = pending[i];
            if (!players.TryGetValue(from, out var shooter)) { pending.RemoveAt(i); continue; }
            var covered = shooter.Track.Count > 0 && shooter.Track[^1].T >= c.T;
            if (!covered && now - received < CombatRules.DeferMs) { i++; continue; }
            pending.RemoveAt(i);
            shooter.Pending = Math.Max(0, shooter.Pending - 1);
            Evaluate(shooter, c, now, received, rtt);
        }
    }

    string? Decide(Player? shooter, string from, HitClaim c, long now, long received, string? reason, string? detail, string? victim = null, double damage = 0, bool head = false, bool kill = false)
    {
        LastDetail = detail;
        var verdict = new ClaimVerdict(from, c.Seq, c.Shot, c.T, reason, detail, victim, Math.Round(damage, 1), head, kill, false, Math.Max(0, now - received));
        verdicts.Add(verdict);
        if (shooter is null) return reason;
        if (reason is null) shooter.Accepted++;
        else { shooter.Rejected++; shooter.Reasons[reason] = shooter.Reasons.GetValueOrDefault(reason) + 1; }
        shooter.Decided[c.Seq] = verdict; shooter.DecidedOrder.Enqueue(c.Seq);
        while (shooter.DecidedOrder.Count > 512) shooter.Decided.Remove(shooter.DecidedOrder.Dequeue());
        return reason;
    }

    // The fire rate, robust to shots timed by the frame they were counted in: every run of k
    // intervals between accepted hits (and this one) must span k minimum intervals, less one
    // frame of slack. Interval: 90 % of the weapon's time between shots (the smaller of the two
    // weapons for a pair).
    static string? FireRate(Player shooter, double interval, long t)
    {
        var times = shooter.Hits.Where(h => Math.Abs(h.T - t) < 3000).Append((T: t, Interval: interval)).OrderBy(h => h.T).ToList();
        var at = times.FindIndex(h => h.T == t && h.Interval == interval);
        for (var i = 0; i < times.Count; i++)
            for (var j = i + 1; j < times.Count; j++)
            {
                if (at < i || at > j) continue;
                var needed = (j - i) * times.Skip(i).Take(j - i + 1).Min(h => h.Interval) - CombatRules.FrameSlackMs;
                if (times[j].T - times[i].T < needed) return (times[j].T - times[i].T).ToString(CultureInfo.InvariantCulture) + " ms for " + (j - i) + " shot(s), at least " + Math.Round(needed) + " ms";
            }
        return null;
    }
    static void Accepted(Player shooter, double interval, long t)
    {
        shooter.Hits.Add((t, interval));
        shooter.Hits.RemoveAll(h => h.T < t - 3000);
        while (shooter.Hits.Count > 16) shooter.Hits.RemoveAt(0);
    }

    // How far behind the shooter's game draws `victim`: the median lag (0..MaxRewindMs) at which
    // its recent drawn rows of that victim match the victim's own track. Null without enough rows.
    double? ViewDelay(Player shooter, Player victim, long now)
    {
        if (shooter.Lag.TryGetValue(victim.Id, out var cached) && now - cached.At < 500) return cached.Lag;
        double? result = null;
        if (shooter.Drawn.TryGetValue(victim.Id, out var rows) && rows.Count > 0 && victim.Track.Count > 0)
        {
            var lags = new List<double>();
            foreach (var v in rows.Where(r => r.T >= rows[^1].T - 2000).TakeLast(40))
            {
                double best = double.MaxValue; long bestLag = -1;
                for (long lag = 0; lag <= CombatRules.MaxRewindMs; lag += 10)
                    if (At(victim.Track, v.T - lag) is { } d)
                    {
                        var dist = Math.Sqrt((d.X - v.X) * (d.X - v.X) + (d.Y - v.Y) * (d.Y - v.Y));
                        if (dist < best - 0.5) { best = dist; bestLag = lag; }
                    }
                if (bestLag >= 0 && best <= TrackingRound.MatchToleranceCm) lags.Add(bestLag);
            }
            if (lags.Count >= 3) { lags.Sort(); result = lags[lags.Count / 2]; }
        }
        shooter.Lag[victim.Id] = (now, result);
        return result;
    }
    // The rewind allowed for a drawn target of this victim: the measured delay plus 100 ms, at least
    // the 200 ms cap, at most MaxRewindMs.
    long RewindCap(Player shooter, Player victim, long now) =>
        (long)Math.Clamp(Math.Max(TrackingRound.RewindCapMs, (ViewDelay(shooter, victim, now) ?? 0) + 100), 0, CombatRules.MaxRewindMs);

    // A drawn hull sits at the victim's own height (its camera minus its eye height, within 50 cm),
    // and is at most the victim's body (or AimMod's avatar, the larger) plus 8 cm: a claim can't
    // move or enlarge the target onto the ray. CS bodies on the scaled maps are taller than the avatar.
    static bool Plausible(Player p, double eyeAboveCentre) => Math.Abs(eyeAboveCentre - p.EyeAbove) <= TrackingRound.HeightToleranceCm;
    static double CapRadius(Player p, double claimed) => Math.Min(claimed, Math.Max(TrackingRound.DefaultRadius, p.BodyRadius ?? 0) + TrackingRound.HullToleranceCm);
    static double CapHalf(Player p, double claimed) => Math.Min(claimed, Math.Max(TrackingRound.DefaultHalfHeight, p.BodyHalf ?? 0) + TrackingRound.HullToleranceCm);
    // The body a player's track stands for (tests and the logs).
    public (double EyeAbove, double? Radius, double? Half)? Body(string id) => players.TryGetValue(id, out var p) ? (p.EyeAbove, p.BodyRadius, p.BodyHalf) : null;

    sealed record Candidate(Player Victim, double X, double Y, double Z, double Radius, double Half, string How, long Lag, double Along);

    string? Evaluate(Player shooter, HitClaim c, long now, long received, int? shooterRtt)
    {
        string? Reject(string why, string? detail = null) => Decide(shooter, shooter.Id, c, now, received, why, detail);
        static string F(double v) => Math.Round(v, 1).ToString(CultureInfo.InvariantCulture);
        if (c.T < Start || c.T > End || c.T > now + CombatRules.FutureMs || c.T < now - CombatRules.ClaimWindowMs)
            return Reject("time", "shot " + (c.T - now).ToString(CultureInfo.InvariantCulture) + " ms from the host clock (window -" + CombatRules.ClaimWindowMs + "..+" + CombatRules.FutureMs + ")");
        // A shooter the host killed may still land what it fired before that death (a trade).
        if (!shooter.Alive && !(shooter.DiedAt is { } died && c.T <= died && now - died <= CombatRules.TradeWindowMs))
            return Reject("shooter-dead", shooter.DiedAt is { } d0 ? "shot " + (c.T - d0).ToString(CultureInfo.InvariantCulture) + " ms after the host's death" : null);
        var weapon = WeaponFor is null ? Weapon : WeaponFor(shooter.Id, c.Slot);
        if (weapon is null) return Reject("weapon", "nothing in slot " + c.Slot);
        var interval = weapon.TimeBetweenShots * 1000 * 0.9;
        if (FireRate(shooter, interval, c.T) is { } fast) return Reject("fire-rate", fast);
        // The ray must start where the shooter's own track had its camera and look the way it did
        // between the samples around the shot (the camera turns between 60 Hz samples).
        if (Around(shooter.Track, c.T) is not { } eyes || At(shooter.Track, c.T) is not { } eye)
            return Reject("no-shooter-track", shooter.Track.Count == 0 ? "no camera samples" : "camera samples end " + (c.T - shooter.Track[^1].T).ToString(CultureInfo.InvariantCulture) + " ms before the shot");
        var offset = Math.Min(Dist(c.X, c.Y, c.Z, eye.X, eye.Y, eye.Z), Math.Min(Dist(c.X, c.Y, c.Z, eyes.A.X, eyes.A.Y, eyes.A.Z), Dist(c.X, c.Y, c.Z, eyes.B.X, eyes.B.Y, eyes.B.Z)));
        if (offset > CombatRules.OriginToleranceCm) return Reject("origin", F(offset) + " cm from the shooter's camera");
        var yawOff = OutsideArc(c.Yaw, eyes.A.Yaw, eyes.B.Yaw);
        var pitchOff = c.Pitch < Math.Min(eyes.A.Pitch, eyes.B.Pitch) ? Math.Min(eyes.A.Pitch, eyes.B.Pitch) - c.Pitch : Math.Max(0, c.Pitch - Math.Max(eyes.A.Pitch, eyes.B.Pitch));
        if (yawOff > CombatRules.AimToleranceDeg || pitchOff > CombatRules.AimToleranceDeg)
            return Reject("aim", "yaw " + F(yawOff) + "°, pitch " + F(pitchOff) + "° outside the camera's turn between samples (allowed " + CombatRules.AimToleranceDeg + "°)");
        // CS: the bullet's own ray, rebuilt from the claim's seed and inaccuracy. The inaccuracy must be
        // one the shooter's movement allows (a running rifle can't claim a standing shot).
        var (dx, dy, dz) = TrackGeometry.Direction(c.Pitch, c.Yaw);
        if (SpreadFor?.Invoke(shooter.Id, c.Slot) is { Spreads: true } feel)
        {
            var (speed, air) = MotionAt(shooter, c.T);
            var least = CsFeel.MinimumCone(feel, speed, air);
            var spreadShot = c.SpreadShot ?? c.Shot;
            var inaccuracy = c.Spread ?? least;
            if (spreadShot > c.Shot || c.Shot - spreadShot > CsFeel.MaxSeedLag)
                return Reject("spread", "seed of shot #" + spreadShot.ToString(CultureInfo.InvariantCulture) + " for shot #" + c.Shot.ToString(CultureInfo.InvariantCulture));
            if (inaccuracy > CsFeel.MaxInaccuracy || inaccuracy < least * CsFeel.HostShare - CsFeel.HostSlack)
                return Reject("spread", F(inaccuracy * 1000) + " mrad claimed; moving at " + F(speed) + " u/s" + (air ? " in the air" : "") + " needs at least " + F(least * 1000 * CsFeel.HostShare) + " mrad");
            (dx, dy, dz) = CsFeel.Direction(c.Pitch, c.Yaw, CsFeel.Offset(CsFeel.Salt(c.MatchId), (ulong)spreadShot, inaccuracy, feel.Spread));
        }
        var tolerance = c.GameHit ? CombatRules.GameHitToleranceCm : CombatRules.RayToleranceCm;
        bool OnRay(double length, double cx, double cy, double cz, double r, double h) =>
            TrackGeometry.HitsCapsule(c.X, c.Y, c.Z, dx, dy, dz, length, cx, cy, cz, r + tolerance, h + tolerance);

        // Who was hit. First: the opponent whose own track matches the drawn target at most its
        // view delay (+100 ms) in the past (favour the shooter: the hull it saw). Else: the host's
        // own rewound ray test against each opponent's track, rewound by that delay (or the
        // estimate from the ping), if the drawn target (when given) lies near it.
        var candidates = new List<Candidate>();
        string? nearest = null; double nearestDistance = double.MaxValue;
        foreach (var p in players.Values)
        {
            if (p == shooter) continue;
            var teammate = shooter.Team != 0 && p.Team == shooter.Team;
            if (teammate && c.TargetX is null) continue; // only a drawn teammate can be hit (and only with team damage on)
            var cap = RewindCap(shooter, p, now);
            if (c.TargetX is { } tx)
            {
                // The drawn hull must also sit at the victim's own height, and its size is the avatar's
                // (at most the hull plus 8 cm), so a claim can't enlarge or move the target onto the ray.
                // The rewind where the victim's track comes closest to the drawn hull.
                (double Dist, long Lag)? best = null;
                for (long lag = 0; lag <= cap; lag += 5)
                {
                    if (At(p.Track, c.T - lag) is not { } d) continue;
                    var dist = Math.Sqrt((d.X - tx) * (d.X - tx) + (d.Y - c.TargetY!.Value) * (d.Y - c.TargetY.Value));
                    if (dist < nearestDistance) { nearestDistance = dist; nearest = "nearest track " + F(dist) + " cm away at " + lag + " ms (rewind cap " + cap + " ms" + (Plausible(p, d.Z - c.TargetZ!.Value) ? "" : ", height off by " + F(d.Z - c.TargetZ.Value - p.EyeAbove) + " cm") + ")"; }
                    if (dist > TrackingRound.MatchToleranceCm || !Plausible(p, d.Z - c.TargetZ!.Value)) continue;
                    if (best is null || dist < best.Value.Dist - 0.5) best = (dist, lag);
                }
                if (best is { } b)
                {
                    candidates.Add(new(p, tx, c.TargetY!.Value, c.TargetZ!.Value, CapRadius(p, c.TargetRadius!.Value), CapHalf(p, c.TargetHalfHeight!.Value), "drawn", b.Lag, Dist(c.X, c.Y, c.Z, tx, c.TargetY.Value, c.TargetZ.Value)));
                    continue;
                }
            }
            var rewind = (long)Math.Clamp(ViewDelay(shooter, p, now) ?? 100 + (shooterRtt ?? 0) / 2.0, 0, cap);
            if (At(p.Track, c.T - rewind) is not { } at) continue;
            var radius = c.TargetRadius is { } tr ? CapRadius(p, tr) : p.BodyRadius ?? TrackingRound.DefaultRadius;
            var half = c.TargetHalfHeight is { } th ? CapHalf(p, th) : p.BodyHalf ?? TrackingRound.DefaultHalfHeight;
            var centreZ = at.Z - p.EyeAbove; // the track is the camera: back to the capsule centre
            if (c.TargetX is { } dxv && Math.Sqrt((at.X - dxv) * (at.X - dxv) + (at.Y - c.TargetY!.Value) * (at.Y - c.TargetY.Value)) > CombatRules.FallbackMatchCm) continue;
            if (!OnRay(TrackingRound.RayLengthCm, at.X, at.Y, centreZ, radius, half)) continue;
            candidates.Add(new(p, at.X, at.Y, centreZ, radius, half, "rewound", rewind, Dist(c.X, c.Y, c.Z, at.X, at.Y, centreZ)));
        }
        // The drawn match first, the living over the dead, then the nearest along the ray.
        var hit = candidates.OrderBy(x => x.Victim.Alive ? 0 : 1).ThenBy(x => x.How == "drawn" ? 0 : 1).ThenBy(x => x.Along).FirstOrDefault();
        if (hit is null) return Reject(c.TargetX is null ? "miss" : "target-mismatch", c.TargetX is null ? "the ray meets no rewound opponent" : nearest ?? "no opponent track near the shot");
        var victim = hit.Victim;
        if (!victim.Alive) return Reject("victim-dead", victim.DiedAt is { } vd ? "died " + (c.T - vd).ToString(CultureInfo.InvariantCulture) + " ms before the shot" : null);
        var teamHit = shooter.Team != 0 && victim.Team == shooter.Team;
        if (teamHit && TeamDamage <= 0) return Reject("teammate");
        if (!OnRay(TrackingRound.RayLengthCm, hit.X, hit.Y, hit.Z, hit.Radius, hit.Half))
        {
            var gap = TrackGeometry.Pass(c.X, c.Y, c.Z, dx, dy, dz, hit.X, hit.Y, hit.Z, hit.Radius, hit.Half).Gap;
            return Reject("ray-miss", "the ray passes " + F(gap) + " cm outside the drawn hull (allowed " + tolerance + " cm)");
        }
        // A knife reaches only so far.
        if (weapon.Range > 0 && !OnRay(weapon.RayLength, hit.X, hit.Y, hit.Z, hit.Radius, hit.Half)) return Reject("range", F(hit.Along) + " cm, reach " + F(weapon.RayLength));
        if (c.T < victim.ProtectedUntil) return Reject("spawn-protected", (victim.ProtectedUntil - c.T).ToString(CultureInfo.InvariantCulture) + " ms of protection left");
        var followUp = weapon.FollowUpDamage > 0 && shooter.LastWeapon == weapon.Name && shooter.LastShot != long.MinValue && c.T - shooter.LastShot >= 0 && c.T - shooter.LastShot <= weapon.FollowUpMs;
        shooter.LastShot = Math.Max(shooter.LastShot, c.T);
        shooter.LastWeapon = weapon.Name;
        Accepted(shooter, interval, c.T);
        shooter.ProtectedUntil = Math.Min(shooter.ProtectedUntil, c.T); // firing ends your own spawn protection
        var head = IsHead(c, weapon, hit.X, hit.Y, hit.Z, hit.Radius, hit.Half, dx, dy, dz);
        var healthBefore = victim.Health;
        Hit(shooter, victim, weapon, head, teamHit, c.T, now, [Math.Round(dx, 4), Math.Round(dy, 4), Math.Round(dz, 4)], followUp);
        return Decide(shooter, shooter.Id, c, now, received, null, hit.How + " hull, " + hit.Lag + " ms back" + (head ? ", head" : ""), victim.Id, healthBefore - victim.Health, head, !victim.Alive);
    }
    static double Dist(double ax, double ay, double az, double bx, double by, double bz) => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by) + (az - bz) * (az - bz));

    // The head zone, the same rule as AimModCore's: where the ray enters the hull (or, for a game hit
    // just beside it, passes nearest) is at least 60 % of the half height above the centre. AimModCore's
    // own head flag allows 4 cm for rounding, and the game's own damage (its headshot multiplier) counts
    // anywhere in the upper body, where KovaaK's head hitbox may reach lower than AimMod's zone.
    static bool IsHead(HitClaim c, CombatWeapon weapon, double cx, double cy, double cz, double radius, double half, double dx, double dy, double dz)
    {
        if (weapon.HeadMultiplier <= 1) return false;
        var z = TrackGeometry.Pass(c.X, c.Y, c.Z, dx, dy, dz, cx, cy, cz, radius, half).Z;
        var zone = cz + half * CombatRules.HeadZoneFraction;
        if (z >= zone - (c.Head ? CombatRules.RayToleranceCm : 0)) return true;
        var gameHead = c.GameHit && c.GameDamage is { } gd && gd >= weapon.Damage * (1 + (weapon.HeadMultiplier - 1) * 0.5);
        return gameHead && z >= cz + half * 0.2;
    }

    // A host-run bot's hit (the bot logic decided it lands, after a sight trace): the same rules
    // as a claim for who can be hit and how hard, without a camera ray to check.
    public string? BotHit(string from, string target, bool head, long now, int slot, double[]? dir)
    {
        if (!players.TryGetValue(from, out var shooter) || !players.TryGetValue(target, out var victim) || shooter == victim) return "not-playing";
        if (now < Start || now > End) return "time";
        if (!shooter.Alive) return "shooter-dead";
        if (!victim.Alive) return "dead";
        var weapon = WeaponFor is null ? Weapon : WeaponFor(from, slot);
        if (weapon is null) return "weapon";
        var interval = weapon.TimeBetweenShots * 1000 * 0.9;
        if (FireRate(shooter, interval, now) is not null) return "fire-rate";
        var teamHit = shooter.Team != 0 && victim.Team == shooter.Team;
        if (teamHit && TeamDamage <= 0) return "teammate";
        if (now < victim.ProtectedUntil) return "spawn-protected";
        shooter.Claims++; shooter.Accepted++;
        shooter.LastShot = now;
        Accepted(shooter, interval, now);
        shooter.ProtectedUntil = Math.Min(shooter.ProtectedUntil, now);
        Hit(shooter, victim, weapon, head, teamHit, now, now, dir is { Length: 3 } ? dir.Select(v => Math.Round(v, 4)).ToArray() : null);
        return null;
    }

    // Area damage the host computed itself (CS grenades: an HE blast, fire): `damage` before armour
    // (DamageModel) and team share. The thrower can hurt themselves; a kill of your own or a
    // teammate's takes a frag away. Not a shot: fire rate and spawn protection don't apply.
    public string? AreaHit(string from, string target, double damage, CombatWeapon weapon, long now, double[]? dir)
    {
        if (!players.TryGetValue(from, out var shooter) || !players.TryGetValue(target, out var victim)) return "not-playing";
        if (!victim.Alive) return "dead";
        if (damage <= 0 || !double.IsFinite(damage)) return "none";
        var own = shooter == victim;
        var teamHit = !own && shooter.Team != 0 && victim.Team == shooter.Team;
        if (teamHit && TeamDamage <= 0) return "teammate";
        var dealt = Math.Min(victim.Health, (DamageModel is null ? damage : DamageModel(victim.Id, damage, false, weapon)) * (teamHit ? TeamDamage : 1));
        victim.Health -= dealt;
        Emit("damage", now, victim.Id, shooter.Id, dealt, false, victim.Health, null, null, dir is { Length: 3 } ? dir.Select(v => Math.Round(v, 4)).ToArray() : null);
        if (victim.Health <= 0.0001)
        {
            victim.Health = 0; victim.Alive = false; victim.Deaths++; victim.DiedAt = now; victim.RespawnAt = Respawns ? now + CombatRules.RespawnMs(Mode) : null;
            if (own || teamHit) shooter.Frags = Math.Max(0, shooter.Frags - 1); else shooter.Frags++;
            OnKill?.Invoke(victim.Id, shooter.Id, weapon, false);
            Emit("death", now, victim.Id, shooter.Id, dealt, false, 0, shooter.Health);
        }
        return null;
    }

    void Hit(Player shooter, Player victim, CombatWeapon weapon, bool head, bool teamHit, long t, long now, double[]? dir, bool followUp = false)
    {
        var raw = (followUp ? weapon.FollowUpDamage : weapon.Damage) * (head ? weapon.HeadMultiplier : 1);
        var damage = Math.Min(victim.Health, (DamageModel is null ? raw : DamageModel(victim.Id, raw, head, weapon)) * (teamHit ? TeamDamage : 1));
        victim.Health -= damage;
        double? healed = null;
        if (Lifesteal > 0)
        {
            var cap = CombatRules.MaxHealth + CombatRules.VampiricOverheal;
            shooter.Health = Math.Min(cap, shooter.Health + damage * Lifesteal);
            healed = shooter.Health;
        }
        Emit("damage", t, victim.Id, shooter.Id, damage, head, victim.Health, healed, null, dir);
        if (victim.Health <= 0.0001)
        {
            victim.Health = 0; victim.Alive = false; victim.Deaths++; victim.DiedAt = t; victim.RespawnAt = Respawns ? now + CombatRules.RespawnMs(Mode) : null;
            // A team kill scores nothing (CS takes a kill away); the kill hook decides the rest.
            if (teamHit) shooter.Frags = Math.Max(0, shooter.Frags - 1); else shooter.Frags++;
            OnKill?.Invoke(victim.Id, shooter.Id, weapon, head);
            if (Mode == LobbyModes.Vampiric) shooter.Health = Math.Min(CombatRules.MaxHealth, shooter.Health + CombatRules.VampiricHealthOnKill);
            Emit("death", t, victim.Id, shooter.Id, damage, head, 0, shooter.Health);
        }
    }

    // The start positions: every player gets a spawn of its own (its team's where the map says),
    // as a respawn event, so each client teleports there during the countdown, frozen until go-live.
    public void PlaceAll(long now)
    {
        if (Spawns.Count == 0) return;
        var used = new HashSet<SpawnPoint>();
        foreach (var p in players.Values)
        {
            var allowed = Spawns.Where(s => p.Team == 0 || s.TeamMask == 0 || (s.TeamMask & p.Team) != 0).ToList();
            if (allowed.Count == 0) allowed = Spawns.ToList();
            var spawn = allowed.FirstOrDefault(s => !used.Contains(s)) ?? allowed[used.Count % allowed.Count];
            used.Add(spawn);
            Emit("respawn", now, p.Id, null, 0, false, p.Health, null, [spawn.X, spawn.Y, spawn.Z, spawn.Yaw]);
        }
    }

    // Respawns, vampiric decay (never below 1 hp: decay doesn't kill) and the claims that waited.
    public void Tick(long now)
    {
        ProcessPending(now);
        foreach (var p in players.Values)
        {
            if (!p.Alive && p.RespawnAt is { } at && now >= at)
            {
                p.Alive = true; p.Health = CombatRules.MaxHealth; p.RespawnAt = null; p.ProtectedUntil = now + CombatRules.SpawnProtectionMs; p.DecayAt = now; p.DiedAt = null;
                var spawn = ChooseSpawn(p.Id);
                Emit("respawn", now, p.Id, null, 0, false, p.Health, null, spawn is null ? null : [spawn.X, spawn.Y, spawn.Z, spawn.Yaw]);
            }
            if (Mode == LobbyModes.Vampiric && p.Alive && now > p.DecayAt)
            {
                p.Health = Math.Max(Math.Min(p.Health, 1), p.Health - CombatRules.VampiricDecayPerSecond * (now - p.DecayAt) / 1000.0);
                p.DecayAt = now;
            }
        }
    }

    public void Leave(string id) { if (players.TryGetValue(id, out var p)) { p.Alive = false; p.RespawnAt = null; } }

    // Host migration: carry over the last published scores and lives.
    public void Restore(CombatView view)
    {
        foreach (var v in view.Players)
            if (players.TryGetValue(v.Member, out var p))
            {
                p.Frags = Math.Max(0, v.Frags); p.Deaths = Math.Max(0, v.Deaths); p.Claims = Math.Max(0, v.Claims); p.Rejected = Math.Clamp(v.Rejected, 0, p.Claims);
                p.Health = Math.Clamp(v.Health, 0, CombatRules.MaxHealth); p.Alive = v.Alive; p.RespawnAt = v.Alive ? null : v.RespawnAt;
            }
        eventId = view.Events.Count > 0 ? view.Events.Max(e => e.Id) : 0;
    }
}

// Client side: newer pushed events applied on top of the last snapshot, so health,
// deaths and frags show the moment the host decides them.
static class CombatOverlay
{
    public static CombatView Apply(CombatView view, IEnumerable<CombatEvent> pushed)
    {
        var latest = view.Events.Count > 0 ? view.Events.Max(e => e.Id) : 0;
        var newer = pushed.Where(e => e.Id > latest).OrderBy(e => e.Id).ToArray();
        if (newer.Length == 0) return view;
        var players = view.Players.ToDictionary(p => p.Member);
        foreach (var e in newer)
        {
            if (players.TryGetValue(e.Member, out var p))
                players[e.Member] = e.Kind switch
                {
                    "damage" => p with { Health = e.Health },
                    "death" => p with { Health = 0, Alive = false, Deaths = p.Deaths + 1 },
                    "respawn" => p with { Health = e.Health, Alive = true, RespawnAt = null },
                    _ => p,
                };
            if (e.Attacker is { } a && players.TryGetValue(a, out var shooter))
            {
                if (e.AttackerHealth is { } h) shooter = shooter with { Health = h };
                if (e.Kind == "death") shooter = shooter with { Frags = shooter.Frags + 1 };
                players[a] = shooter;
            }
        }
        var list = view.Players.Select(p => players[p.Member]).ToArray();
        var teams = view.TeamFrags is null ? null : new[] { list.Where(p => p.Team == 1).Sum(p => p.Frags), list.Where(p => p.Team == 2).Sum(p => p.Frags) };
        return view with { Players = list, Events = view.Events.Concat(newer).TakeLast(16).ToArray(), TeamFrags = teams };
    }
}

// Client side: the local player's shots from AimModCore's self-shots.tsv
// (AIMMOD_SHOTS_1, contract in game-modes.md 6.2.1), turned into hit claims on
// the host clock. Misses are not claimed; the host only needs hits. The feed
// acknowledges what it took (self-shots.request), so AimModCore keeps every shot
// until then: a slow or stalled poll never loses one.
sealed class ShotFeed(string outputFolder)
{
    readonly string path = Path.Combine(outputFolder, "self-shots.tsv");
    readonly string requestPath = Path.Combine(outputFolder, "self-shots.request");
    long lastSequence = -1, lastShot = -1, requestedAt, ackedShot = -1, claimSeq; string? session;
    public const int MaxShots = 256;
    // Shots older than this (local clock) when first read are history, never claimed.
    public const long StaleMs = CombatRules.ClaimWindowMs + 500;
    // A new match: claims are numbered from 1 again. The shot sequence carries on (it is
    // AimModCore's, per session), so shots already taken are never claimed twice.
    public void Reset() { claimSeq = 0; Stats = new(); }
    // AimModCore publishes shots only while self-shots.request is fresh (5 s); the request also
    // carries the last shot taken (and its session), at most 10 times a second when it moves.
    public void Request(long nowMs)
    {
        var moved = lastShot != ackedShot && session is not null;
        if (nowMs - requestedAt < (moved ? 100 : 2000)) return;
        requestedAt = nowMs;
        try
        {
            var text = nowMs.ToString(CultureInfo.InvariantCulture);
            if (session is { Length: > 0 } s && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out _) && lastShot >= 0)
                text += "\t" + lastShot.ToString(CultureInfo.InvariantCulture) + "\t" + s;
            File.WriteAllText(requestPath, text);
            ackedShot = lastShot;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // GameHit: the game's own hit counter counted it. Capsule: the drawn target AimModCore's ray
    // met (centre x, y, z, radius, half height) at the shot. GameDamage: the game's damage per hit
    // that frame. Source: 1 the ray met the capsule, 2 passed just beside it, 3 the game named it.
    // Spread (CS): the bullet's inaccuracy (rad), the shot whose seed drew its offset, and whether the
    // game's own trace followed it (else only AimModCore's ray did, and that ray decides the hit).
    public sealed record Shot(long UnixMs, long Seq, double X, double Y, double Z, double Pitch, double Yaw, int Weapon, int Target, bool Head,
        bool GameHit = false, double[]? Capsule = null, double? GameDamage = null, int Source = 0, double? Spread = null, long SpreadShot = 0, bool SpreadApplied = false);
    // Every shot new in the last Take, hits and misses (the knife's sounds).
    public List<Shot> Fresh { get; } = [];

    // What the feed saw, for the hit diagnostics.
    public sealed class Counters
    {
        public int Shots, GameHits, Claims, GameHitNoTarget, RayOnly, Stale, Lost;
    }
    public Counters Stats { get; private set; } = new();
    public bool GameHitsKnown { get; private set; }

    // AimModCore's self-shots.tsv (native-mod/DESIGN.md "Match play"):
    //   AIMMOD_SHOTS_1\t<publish seq>\t<session>
    //   shot\t<unix ms>\t<shot seq>\t<ox>\t<oy>\t<oz>\t<dx>\t<dy>\t<dz>\t<slot>\t<target>\t<headshot 0/1>\t<gameHit 0/1>
    //       [\t<cx>\t<cy>\t<cz>\t<radius>\t<half height>\t<game damage per hit, -1 unknown>\t<source 0-3>
    //       [\t<inaccuracy mrad, 0 none>\t<seed shot>\t<game trace followed 0/1>]]
    //   tag\t<target id>\t<stream id>
    // The ray direction becomes pitch and yaw. The earlier pitch/yaw row shape still reads.
    public static (long Sequence, string Session, IReadOnlyList<Shot> Shots)? Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 1 || text.Length > 4 * 65536) return null;
        var head = lines[0].Split('\t');
        if (head.Length is not (2 or 3) || head[0] != "AIMMOD_SHOTS_1" || !long.TryParse(head[1], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)) return null;
        var session = head.Length == 3 ? head[2] : "";
        if (session.Length > 64 || session.Any(char.IsControl)) return null;
        var shots = new List<Shot>();
        static bool Num(string s, out double v) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v) && Math.Abs(v) < 1e9;
        foreach (var line in lines.Skip(1))
        {
            var c = line.Split('\t');
            if (c[0] == "tag") continue; // avatar tags: the claim carries the drawn target itself
            if (c.Length is not (11 or 13 or 20 or 23) || c[0] != "shot" || shots.Count >= MaxShots) return null;
            if (!long.TryParse(c[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ms) || !long.TryParse(c[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seq)) return null;
            var n = c.Length >= 13 ? 6 : 5;
            var v = new double[n];
            for (var i = 0; i < n; i++) if (!Num(c[i + 3], out v[i])) return null;
            double pitch, yaw;
            if (n == 6)
            {
                var len = Math.Sqrt(v[3] * v[3] + v[4] * v[4] + v[5] * v[5]);
                if (len < 0.5 || len > 1.5) return null;
                pitch = Math.Asin(Math.Clamp(v[5] / len, -1, 1)) * 180 / Math.PI; yaw = Math.Atan2(v[4], v[3]) * 180 / Math.PI;
            }
            else { pitch = v[3]; yaw = v[4]; }
            var at = 3 + n;
            if (!int.TryParse(c[at], NumberStyles.None, CultureInfo.InvariantCulture, out var weapon) || weapon > 7 || !int.TryParse(c[at + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var target) || c[at + 2] is not ("0" or "1")) return null;
            if (n == 6 && c[at + 3] is not ("0" or "1")) return null;
            if (shots.Count > 0 && seq <= shots[^1].Seq) return null;
            double[]? capsule = null; double? damage = null; var source = 0;
            double? spread = null; long spreadShot = 0; var applied = false;
            if (c.Length == 23)
            {
                if (!Num(c[20], out var mrad) || mrad is < 0 or > 1000 || !long.TryParse(c[21], NumberStyles.None, CultureInfo.InvariantCulture, out spreadShot) || c[22] is not ("0" or "1")) return null;
                if (mrad > 0) { spread = mrad / 1000; applied = c[22] == "1"; }
            }
            if (c.Length >= 20)
            {
                var k = new double[7];
                for (var i = 0; i < 7; i++) if (!Num(c[13 + i], out k[i])) return null;
                if (k[3] > 0 && k[4] >= k[3] && k[3] <= 1000 && k[4] <= 2000 && target != 0) capsule = [k[0], k[1], k[2], k[3], k[4]];
                if (k[5] >= 0) damage = k[5];
                if (k[6] is < 0 or > 3 || k[6] != Math.Truncate(k[6])) return null;
                source = (int)k[6];
            }
            shots.Add(new Shot(ms, seq, v[0], v[1], v[2], pitch, yaw, weapon, target, c[at + 2] == "1", n == 6 && c[at + 3] == "1", capsule, damage, source, spread, spread is null ? 0 : spreadShot, applied));
        }
        return (sequence, session, shots);
    }

    // New hits since the last call. targets: the drawn targets by id (latest self-pose frame).
    public IReadOnlyList<HitClaim> Poll(string matchId, int round, long offsetMs, IReadOnlyDictionary<int, TrackSeen> targets) =>
        Poll(matchId, round, offsetMs, (id, _) => targets.TryGetValue(id, out var t) ? t : null);

    public IReadOnlyList<HitClaim> Take((long Sequence, string Session, IReadOnlyList<Shot> Shots)? parsed, string matchId, int round, long offsetMs, IReadOnlyDictionary<int, TrackSeen> targets) =>
        Take(parsed, matchId, round, offsetMs, (id, _) => targets.TryGetValue(id, out var t) ? t : null);

    // targetAt: where a drawn target was at a host time (SelfPoseTracker.SeenAt), for shots whose
    // row doesn't carry the drawn capsule itself.
    // nowMs: the local clock, for StaleMs.
    public IReadOnlyList<HitClaim> Poll(string matchId, int round, long offsetMs, Func<int, long, TrackSeen?> targetAt, long? nowMs = null)
    {
        string text;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 4 * 65536 || DateTime.UtcNow - file.LastWriteTimeUtc > TimeSpan.FromSeconds(3)) return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        return Take(Parse(text), matchId, round, offsetMs, targetAt, nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    // nowMs (local clock): shots already older than StaleMs when first read are not claimed.
    public IReadOnlyList<HitClaim> Take((long Sequence, string Session, IReadOnlyList<Shot> Shots)? parsed, string matchId, int round, long offsetMs, Func<int, long, TrackSeen?> targetAt, long? nowMs = null)
    {
        Fresh.Clear();
        if (parsed is not { } p) return [];
        // A new AimModCore session restarts its shot sequence.
        if (p.Session != session) { session = p.Session; lastSequence = -1; lastShot = -1; ackedShot = -1; GameHitsKnown = false; }
        if (p.Sequence == lastSequence) return [];
        lastSequence = p.Sequence;
        var claims = new List<HitClaim>();
        foreach (var s in p.Shots)
        {
            if (s.Seq <= lastShot) continue;
            // AimModCore numbers shots one by one: a gap is shots it dropped before we read them.
            if (lastShot >= 0 && s.Seq > lastShot + 1) Stats.Lost += (int)Math.Min(int.MaxValue, s.Seq - lastShot - 1);
            lastShot = s.Seq;
            if (nowMs is { } now && now - s.UnixMs > StaleMs) { Stats.Stale++; continue; }
            Fresh.Add(s);
            Stats.Shots++;
            if (s.GameHit) { Stats.GameHits++; GameHitsKnown = true; }
            // A hit is what the game counted (the hitmarker the shooter saw), on a drawn target. Before
            // the game's counter has said anything (or with an AimModCore without it) the ray decides.
            if (s.Target == 0) { if (s.GameHit) Stats.GameHitNoTarget++; continue; }
            // A CS bullet whose spread the game's trace didn't follow: AimModCore's own ray (with its world
            // trace) decides, the game's counter spoke for the crosshair.
            var ownRay = s.Spread is > 0 && !s.SpreadApplied;
            if (!s.GameHit && GameHitsKnown && !ownRay) { Stats.RayOnly++; continue; }
            var t = s.Capsule is { } k ? new TrackSeen(s.UnixMs + offsetMs, s.Target, k[0], k[1], k[2], k[3], k[4]) : targetAt(s.Target, s.UnixMs + offsetMs);
            claims.Add(new HitClaim(matchId, round, ++claimSeq, s.UnixMs + offsetMs, s.X, s.Y, s.Z, s.Pitch, s.Yaw, s.Head, t?.X, t?.Y, t?.Z, t?.Radius, t?.HalfHeight, s.Weapon,
                s.Seq, s.GameHit, s.GameDamage, s.Source, s.Spread, s.Spread is null ? null : s.SpreadShot));
            Stats.Claims++;
        }
        return claims;
    }
}

// Client side: claims sent to the host and not yet confirmed (hit-ack). A claim without an
// answer is sent again (the host answers a repeat with its first decision, never applying it
// twice) until it is older than the host's claim window.
sealed class ClaimOutbox
{
    public const long ResendMs = 400, GiveUpMs = CombatRules.ClaimWindowMs + 1000;
    readonly Dictionary<long, (HitClaim Claim, long SentAt, long FirstAt, int Tries)> open = new();
    string? key;
    public int Sent, Resent, Confirmed, Accepted, Unanswered;
    public readonly Dictionary<string, int> Rejected = new();
    public int Open => open.Count;
    public void Reset(string matchKey)
    {
        if (key == matchKey) return;
        key = matchKey; open.Clear(); Sent = Resent = Confirmed = Accepted = Unanswered = 0; Rejected.Clear();
    }
    public void Add(HitClaim claim, long now) { open[claim.Seq] = (claim, now, now, 1); Sent++; }
    // The claims due again; those past the window are given up (counted).
    public IReadOnlyList<HitClaim> Due(long now)
    {
        var due = new List<HitClaim>();
        foreach (var (seq, o) in open.ToArray())
        {
            if (now - o.FirstAt > GiveUpMs) { open.Remove(seq); Unanswered++; continue; }
            if (now - o.SentAt < ResendMs * o.Tries) continue;
            open[seq] = (o.Claim, now, o.FirstAt, o.Tries + 1);
            due.Add(o.Claim); Resent++;
        }
        return due;
    }
    // The host's answer: true when it was still open (the first answer for that claim).
    public bool Confirm(long seq, string? reason)
    {
        if (!open.Remove(seq)) return false;
        Confirmed++;
        if (reason is null) Accepted++; else Rejected[reason] = Rejected.GetValueOrDefault(reason) + 1;
        return true;
    }
}

// Client side: the state AimModCore applies to the local player (play-state.tsv,
// AIMMOD_PLAY_1, contract in game-modes.md 6.2.1). Absolute and idempotent: the
// host's health for this player, alive or not, when it respawns, spawn
// protection, and the last damage taken (for the native hit effect).
static class PlayState
{
    static string N(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    // AimModCore's play-state.tsv (native-mod/DESIGN.md "Match play"): AIMMOD_PLAYSTATE_1, the
    // match scenario's exact name, health, alive, respawnAt (local unix ms), protected and the last
    // hit (attacker member id, damage, headshot, unit direction). Unknown rows reject the file, so
    // nothing else goes here. hostToLocal: subtract from host-clock times.
    public static string Format(long sequence, string scenario, CombatPlayerView self, CombatEvent? lastHit, long hostNow, long hostToLocal)
    {
        var text = new StringBuilder();
        text.Append("AIMMOD_PLAYSTATE_1\t").Append(sequence).Append('\n');
        text.Append("match\t").Append(scenario).Append('\n');
        text.Append("health\t").Append(N(Math.Clamp(self.Health, 0, CombatRules.MaxHealth))).Append('\t').Append(N(CombatRules.MaxHealth)).Append('\n');
        text.Append("alive\t").Append(self.Alive ? 1 : 0).Append('\n');
        text.Append("respawnAt\t").Append(self.RespawnAt is { } r && !self.Alive ? Math.Max(0, r - hostToLocal) : 0).Append('\n');
        text.Append("protected\t").Append(self.Alive && self.ProtectedUntil is { } pu && pu > hostNow ? 1 : 0).Append('\n');
        if (lastHit is { Attacker: { } attacker } && LobbyRules.IsStreamSafe(attacker))
        {
            var d = lastHit.Dir is { Length: 3 } dir ? dir : [1.0, 0, 0];
            text.Append("hit\t").Append(lastHit.Id).Append('\t').Append(attacker).Append('\t').Append(N(lastHit.Amount)).Append('\t').Append(lastHit.Head ? 1 : 0)
                .Append('\t').Append(d[0].ToString("0.####", CultureInfo.InvariantCulture)).Append('\t').Append(d[1].ToString("0.####", CultureInfo.InvariantCulture)).Append('\t').Append(d[2].ToString("0.####", CultureInfo.InvariantCulture)).Append('\n');
        }
        return text.ToString();
    }

    // round-state.tsv (new, for AimModCore; game-modes.md 6.2.1): where the host respawned this
    // player and, in CS, the round phase and loadout:
    //   AIMMOD_ROUND_1\t<seq>
    //   match\t<scenario>
    //   spawn\t<id>\t<x>\t<y>\t<z>\t<yaw>                 (teleport once per id)
    //   phase\t<freeze|live|planted|end|over>\t<frozen 0/1>\t<buy window 0/1>\t<phase ends, local unix ms>
    //   loadout\t<primary profile or ->\t<pistol profile or ->\t<armour>\t<helmet 0/1>\t<kit 0/1>
    public static string Round(long sequence, string scenario, CombatEvent? lastSpawn, IEnumerable<string>? extra = null)
    {
        var text = new StringBuilder();
        text.Append("AIMMOD_ROUND_1\t").Append(sequence).Append('\n');
        text.Append("match\t").Append(scenario).Append('\n');
        if (lastSpawn?.Spawn is { Length: 4 } at)
            text.Append("spawn\t").Append(lastSpawn.Id).Append('\t').Append(N(at[0])).Append('\t').Append(N(at[1])).Append('\t').Append(N(at[2])).Append('\t').Append(N(at[3])).Append('\n');
        foreach (var line in extra ?? []) text.Append(line).Append('\n');
        return text.ToString();
    }
}

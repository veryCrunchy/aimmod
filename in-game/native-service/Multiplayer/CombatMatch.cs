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
sealed record CombatWeapon(string Name, double Damage, double HeadMultiplier, double TimeBetweenShots, bool FullyAuto, double KnockbackVertical = 0, double Range = 0)
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
    public const long SpawnProtectionMs = 1500, ClaimWindowMs = 1000;
    public static int DefaultFragLimit(string mode) => mode switch { LobbyModes.Vampiric => 10, LobbyModes.Instagib => 25, LobbyModes.TeamDeathmatch => 50, _ => 20 };
    // Team deathmatch: teams by join order, alternating (1, 2, 1, 2 ...), so sizes differ by at most one.
    public static Dictionary<string, int> Teams(IEnumerable<string> players) => players.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i % 2 + 1);
    public const int DefaultMatchSeconds = 300;
}

// A hit the shooter's game registered: host-clock time, the shot's camera ray,
// whether the game counted a headshot, and where the game drew the target it hit.
sealed record HitClaim(string MatchId, int Round, long Seq, long T, double X, double Y, double Z, double Pitch, double Yaw, bool Head,
    double? TargetX, double? TargetY, double? TargetZ, double? TargetRadius, double? TargetHalfHeight, int Slot = 0)
{
    public object Body() => new
    {
        match = MatchId, round = Round, seq = Seq, t = T, o = new[] { R(X), R(Y), R(Z) }, r = new[] { R(Pitch), R(Yaw) }, head = Head, w = Slot,
        target = TargetX is null ? null : new[] { R(TargetX.Value), R(TargetY!.Value), R(TargetZ!.Value), R(TargetRadius!.Value), R(TargetHalfHeight!.Value) },
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
            return new HitClaim(match, round, seq, t, o[0], o[1], o[2], r[0], r[1], head, target?[0], target?[1], target?[2], target?[3], target?[4], slot);
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

sealed class CombatMatch
{
    sealed class Player
    {
        public required string Id;
        public double Health = CombatRules.MaxHealth; public bool Alive = true; public int Frags, Deaths, Claims, Rejected, Team;
        public long? RespawnAt; public long ProtectedUntil; public long LastShot = long.MinValue, LastSeq = -1; public long DecayAt;
        public readonly List<TrackSample> Track = [];
    }
    readonly Dictionary<string, Player> players = new();
    readonly List<CombatEvent> events = [];
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
    public Func<string, double, bool, CombatWeapon, double>? DamageModel { get; set; }
    public Action<string, string, CombatWeapon, bool>? OnKill { get; set; }
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
    // Round start: everyone alive at full health, spawn-protected for a moment, scores kept.
    public void Revive(string id, long now, double health, long protectMs = 0)
    {
        if (!players.TryGetValue(id, out var p)) return;
        p.Alive = true; p.Health = health; p.RespawnAt = null; p.ProtectedUntil = now + protectMs; p.LastShot = long.MinValue;
    }
    public void SetTeam(string id, int team) { if (players.TryGetValue(id, out var p)) p.Team = team; }
    public void Kill(string id, long now) { if (players.TryGetValue(id, out var p) && p.Alive) { p.Alive = false; p.Health = 0; p.RespawnAt = null; Emit("death", now, id, null, 0, false, 0, null); } }
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

    // Every player's own camera track (same batches as the tracking duel).
    public void Track(string from, TrackBatch batch)
    {
        if (!players.TryGetValue(from, out var p)) return;
        foreach (var s in batch.Samples)
        {
            if (s.T < Start - 1000 || s.T > End + 1000) continue;
            if (p.Track.Count > 0 && s.T <= p.Track[^1].T) continue;
            p.Track.Add(s);
        }
        // Only the last 10 s are needed for rewinds and checks (claims are at most 1 s old).
        var cut = p.Track.FindIndex(x => x.T >= p.Track[^1].T - 10_000);
        if (cut > 0) p.Track.RemoveRange(0, cut);
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

    // Why a claim was refused (null: accepted). The host decides who was hit, where
    // and how hard; the claim only says when the shooter fired and along which ray.
    public string? Claim(string from, HitClaim c, long now, int? shooterRtt)
    {
        if (!players.TryGetValue(from, out var shooter)) return "not-playing";
        shooter.Claims++;
        string? Reject(string why) { shooter.Rejected++; return why; }
        if (c.Seq <= shooter.LastSeq) return Reject("repeated");
        shooter.LastSeq = c.Seq;
        if (c.T < Start || c.T > End || c.T > now + 100 || c.T < now - CombatRules.ClaimWindowMs) return Reject("time");
        if (!shooter.Alive) return Reject("shooter-dead");
        var weapon = WeaponFor is null ? Weapon : WeaponFor(from, c.Slot);
        if (weapon is null) return Reject("weapon");
        if (shooter.LastShot != long.MinValue && c.T - shooter.LastShot < weapon.TimeBetweenShots * 1000 * 0.9) return Reject("fire-rate");
        // The ray must start where the shooter's own track had its camera, looking the same way.
        if (At(shooter.Track, c.T) is not { } eye) return Reject("no-shooter-track");
        var dx0 = c.X - eye.X; var dy0 = c.Y - eye.Y; var dz0 = c.Z - eye.Z;
        if (dx0 * dx0 + dy0 * dy0 + dz0 * dz0 > 32 * 32) return Reject("origin");
        var yawError = Math.Abs(((c.Yaw - eye.Yaw) % 360 + 540) % 360 - 180);
        if (Math.Abs(c.Pitch - eye.Pitch) > 3 || yawError > 3) return Reject("aim");
        var (dx, dy, dz) = TrackGeometry.Direction(c.Pitch, c.Yaw);
        // Who was hit: the alive opponent whose own track matches the drawn target (favour the
        // shooter, at most 200 ms in the past), else whoever the ray hits after a capped rewind.
        Player? victim = null; double cx = 0, cy = 0, cz = 0, radius = TrackingRound.DefaultRadius, half = TrackingRound.DefaultHalfHeight;
        foreach (var p in players.Values)
        {
            if (p == shooter || !p.Alive) continue;
            var teammate = shooter.Team != 0 && p.Team == shooter.Team;
            if (teammate && c.TargetX is null) continue; // friendly fire is off
            if (c.TargetX is { } tx)
            {
                // The drawn hull must also sit at the victim's own height, and its size is the avatar's
                // (at most the hull plus 8 cm), so a claim can't enlarge or move the target onto the ray.
                for (long lag = 0; lag <= TrackingRound.RewindCapMs && victim is null; lag += 5)
                    if (At(p.Track, c.T - lag) is { } d && Math.Sqrt((d.X - tx) * (d.X - tx) + (d.Y - c.TargetY!.Value) * (d.Y - c.TargetY.Value)) <= TrackingRound.MatchToleranceCm
                        && TrackingRound.PlausibleHeight(d.Z - c.TargetZ!.Value))
                    { victim = p; cx = tx; cy = c.TargetY.Value; cz = c.TargetZ.Value; radius = TrackingRound.HullRadius(c.TargetRadius!.Value); half = TrackingRound.HullHalfHeight(c.TargetHalfHeight!.Value); }
            }
            else
            {
                var lag = Math.Clamp(100 + (shooterRtt ?? 0) / 2.0, 0, TrackingRound.RewindCapMs);
                if (At(p.Track, c.T - lag) is { } d && TrackGeometry.HitsCapsule(c.X, c.Y, c.Z, dx, dy, dz, TrackingRound.RayLengthCm, d.X, d.Y, d.Z - TrackingRound.DefaultEyeAboveCentre, radius, half))
                { victim = p; cx = d.X; cy = d.Y; cz = d.Z - TrackingRound.DefaultEyeAboveCentre; }
            }
            if (victim is not null) break;
        }
        if (victim is null) return Reject(c.TargetX is null ? "miss" : "target-mismatch");
        if (shooter.Team != 0 && victim.Team == shooter.Team) return Reject("teammate");
        if (!TrackGeometry.HitsCapsule(c.X, c.Y, c.Z, dx, dy, dz, TrackingRound.RayLengthCm, cx, cy, cz, radius, half)) return Reject("ray-miss");
        // A knife reaches only so far.
        if (weapon.Range > 0 && !TrackGeometry.HitsCapsule(c.X, c.Y, c.Z, dx, dy, dz, weapon.RayLength, cx, cy, cz, radius, half)) return Reject("range");
        if (c.T < victim.ProtectedUntil) return Reject("spawn-protected");
        shooter.LastShot = c.T;
        shooter.ProtectedUntil = Math.Min(shooter.ProtectedUntil, c.T); // firing ends your own spawn protection
        // Headshot: the ray passes through the top sphere of the hull (radius 25 cm).
        var headR = Math.Min(25, radius);
        var head = TrackGeometry.HitsCapsule(c.X, c.Y, c.Z, dx, dy, dz, TrackingRound.RayLengthCm, cx, cy, cz + half - headR, headR, headR);
        var raw = weapon.Damage * (head ? weapon.HeadMultiplier : 1);
        var damage = Math.Min(victim.Health, DamageModel is null ? raw : DamageModel(victim.Id, raw, head, weapon));
        victim.Health -= damage;
        double? healed = null;
        if (Lifesteal > 0)
        {
            var cap = CombatRules.MaxHealth + CombatRules.VampiricOverheal;
            shooter.Health = Math.Min(cap, shooter.Health + damage * Lifesteal);
            healed = shooter.Health;
        }
        Emit("damage", c.T, victim.Id, shooter.Id, damage, head, victim.Health, healed, null, [Math.Round(dx, 4), Math.Round(dy, 4), Math.Round(dz, 4)]);
        if (victim.Health <= 0.0001)
        {
            victim.Health = 0; victim.Alive = false; victim.Deaths++; victim.RespawnAt = Respawns ? now + CombatRules.RespawnMs(Mode) : null;
            shooter.Frags++;
            OnKill?.Invoke(victim.Id, shooter.Id, weapon, head);
            if (Mode == LobbyModes.Vampiric) shooter.Health = Math.Min(CombatRules.MaxHealth, shooter.Health + CombatRules.VampiricHealthOnKill);
            Emit("death", c.T, victim.Id, shooter.Id, damage, head, 0, shooter.Health);
        }
        return null;
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

    // Respawns, and vampiric decay (never below 1 hp: decay doesn't kill).
    public void Tick(long now)
    {
        foreach (var p in players.Values)
        {
            if (!p.Alive && p.RespawnAt is { } at && now >= at)
            {
                p.Alive = true; p.Health = CombatRules.MaxHealth; p.RespawnAt = null; p.ProtectedUntil = now + CombatRules.SpawnProtectionMs; p.DecayAt = now;
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
// the host clock. Misses are not claimed; the host only needs hits.
sealed class ShotFeed(string outputFolder)
{
    readonly string path = Path.Combine(outputFolder, "self-shots.tsv");
    readonly string requestPath = Path.Combine(outputFolder, "self-shots.request");
    long lastSequence = -1, lastShot = -1, requestedAt; string? session;
    public void Reset() { lastSequence = -1; lastShot = -1; session = null; }
    // AimModCore publishes shots only while self-shots.request is fresh (5 s).
    public void Request(long nowMs)
    {
        if (nowMs - requestedAt < 2000) return;
        requestedAt = nowMs;
        try { File.WriteAllText(requestPath, nowMs.ToString(CultureInfo.InvariantCulture)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public sealed record Shot(long UnixMs, long Seq, double X, double Y, double Z, double Pitch, double Yaw, int Weapon, int Target, bool Head);

    // AimModCore's self-shots.tsv (native-mod/DESIGN.md "Match play"):
    //   AIMMOD_SHOTS_1\t<publish seq>\t<session>
    //   shot\t<unix ms>\t<shot seq>\t<ox>\t<oy>\t<oz>\t<dx>\t<dy>\t<dz>\t<slot>\t<target>\t<headshot 0/1>\t<gameHit 0/1>
    //   tag\t<target id>\t<stream id>
    // The ray direction becomes pitch and yaw. The earlier pitch/yaw row shape still reads.
    public static (long Sequence, string Session, IReadOnlyList<Shot> Shots)? Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 1 || text.Length > 65536) return null;
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
            if (c.Length is not (11 or 13) || c[0] != "shot" || shots.Count >= 64) return null;
            if (!long.TryParse(c[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ms) || !long.TryParse(c[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seq)) return null;
            var n = c.Length == 13 ? 6 : 5;
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
            shots.Add(new Shot(ms, seq, v[0], v[1], v[2], pitch, yaw, weapon, target, c[at + 2] == "1"));
        }
        return (sequence, session, shots);
    }

    // New hits since the last call. targets: the drawn targets by id (latest self-pose frame).
    public IReadOnlyList<HitClaim> Poll(string matchId, int round, long offsetMs, IReadOnlyDictionary<int, TrackSeen> targets)
    {
        string text;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 65536 || DateTime.UtcNow - file.LastWriteTimeUtc > TimeSpan.FromSeconds(3)) return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        return Take(Parse(text), matchId, round, offsetMs, targets);
    }

    public IReadOnlyList<HitClaim> Take((long Sequence, string Session, IReadOnlyList<Shot> Shots)? parsed, string matchId, int round, long offsetMs, IReadOnlyDictionary<int, TrackSeen> targets) =>
        Take(parsed, matchId, round, offsetMs, (id, _) => targets.TryGetValue(id, out var t) ? t : null);

    // targetAt: where a drawn target was at a host time (SelfPoseTracker.SeenAt), so the claim pairs
    // the shot's ray with the hull as drawn when it was fired.
    public IReadOnlyList<HitClaim> Poll(string matchId, int round, long offsetMs, Func<int, long, TrackSeen?> targetAt)
    {
        string text;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 65536 || DateTime.UtcNow - file.LastWriteTimeUtc > TimeSpan.FromSeconds(3)) return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        return Take(Parse(text), matchId, round, offsetMs, targetAt);
    }

    public IReadOnlyList<HitClaim> Take((long Sequence, string Session, IReadOnlyList<Shot> Shots)? parsed, string matchId, int round, long offsetMs, Func<int, long, TrackSeen?> targetAt)
    {
        if (parsed is not { } p) return [];
        // A new AimModCore session restarts its shot sequence.
        if (p.Session != session) { session = p.Session; lastSequence = -1; lastShot = -1; }
        if (p.Sequence == lastSequence) return [];
        lastSequence = p.Sequence;
        var claims = new List<HitClaim>();
        foreach (var s in p.Shots)
        {
            if (s.Seq <= lastShot) continue;
            lastShot = s.Seq;
            if (s.Target == 0) continue;
            var t = targetAt(s.Target, s.UnixMs + offsetMs);
            claims.Add(new HitClaim(matchId, round, s.Seq, s.UnixMs + offsetMs, s.X, s.Y, s.Z, s.Pitch, s.Yaw, s.Head, t?.X, t?.Y, t?.Z, t?.Radius, t?.HalfHeight, s.Weapon));
        }
        return claims;
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

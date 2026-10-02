using System.Globalization;
using System.Text;

namespace AimMod.InGame.Multiplayer;

// CS grenades (in-game/docs/game-modes.md 6.6.5): HE, flashbang, smoke, decoy, molotov (T) and
// incendiary (CT). The host owns everything: what each player carries, every throw, where each
// grenade goes and when it goes off, and what it does (damage, blindness, smoke, fire). Paths
// bounce off the map by line traces in the host's own game (AimModSteam simulates them, the same
// physics as GrenadePhysics here), and every peer draws the path the host broadcasts, so all see
// the same bounces, pops and clouds.

// A grenade for sale: id, label, short tag (name tags, HUD), price, side ("any", T, CT), how many
// one player may carry, the profile its kills are credited to and the share of its damage that
// reaches health through armour.
sealed record CsGrenade(string Id, string Label, string Short, int Price, string Side, int Max, CombatWeapon Combat, double ArmorPenetration);

// A flash on one player: when it popped (host ms), how long the screen stays white, how long it
// takes to clear, and how hard it hit (0..1).
sealed record CsFlashView(long At, int HoldMs, int FadeMs, double Amount);

// A grenade in the CS view (clients mirror it, AimModCore and the HUD draw it):
//  - flying: At = thrown (host ms), Keys = its path (GrenadePhysics.Flat), Ends = when it goes off (null until known);
//  - smoke, fire, decoy: At = start, Ends = end, Pos = where it is (fire: Radius);
//  - blast: an HE explosion, a flash pop, a molotov that burst in the air or fizzled in smoke, a decoy's last pop.
sealed record CsGrenadeView(long Id, string Kind, string Owner, string State, long At, long? Ends = null, double[]? Pos = null, double[]? Keys = null, string? Weapon = null, double Radius = 0);

static class GrenadeRules
{
    public const string He = "he", Flash = "flash", Smoke = "smoke", Decoy = "decoy", Molotov = "molotov", Incendiary = "incendiary";
    // CS2: four grenades in all, one of each but two flashbangs.
    public const int MaxCarried = 4;
    // HE (CS2 98 damage, 350 units at 4.4 cm): falls off with distance, half reaches health through armour.
    public const double HeDamage = 98, HeRadiusCm = 350 * GrenadePhysics.Unit, HeArmorPenetration = 0.5;
    public const long HeFuseMs = 1500, FlashFuseMs = 1500, FireFuseMs = 2000;
    // Flash: full effect within 6 m, none beyond 30 m; at most about 5 s of blindness.
    public const double FlashNearCm = 600, FlashFarCm = 3000;
    public const int FlashHoldMaxMs = 2000, FlashFadeMaxMs = 3200;
    // Smoke: a cloud about 12 m across and 6 m tall for 18 s; it grows in over 1.5 s and thins over the last 2 s.
    public const double SmokeRadiusCm = 620, SmokeHalfHeightCm = 300;
    public const long SmokeMs = 18_000, SmokeGrowMs = 1500, SmokeFadeMs = 2000;
    // Fire: a 2.5 m pool for 7 s, 40 damage a second (in 0.25 s ticks), armour doesn't help; a smoke puts it out.
    public const double FireRadiusCm = 250, FireDps = 40;
    public const long FireMs = 7_000, FireTickMs = 250;
    // Decoy: fake gunfire for 15 s.
    public const long DecoyMs = 15_000;
    // The player's eye above their feet (KovaaK's body: capsule half height 115 + eye 64).
    public const double EyeHeightCm = 180;
    public const int KillReward = 300;

    public static readonly CsGrenade[] All =
    [
        new(He, "HE Grenade", "HE", 300, "any", 1, new CombatWeapon("AimMod CS HE Grenade", HeDamage, 1, 0, false), HeArmorPenetration),
        new(Flash, "Flashbang", "FL", 200, "any", 2, new CombatWeapon("AimMod CS Flashbang", 0, 1, 0, false), 1),
        new(Smoke, "Smoke Grenade", "SM", 300, "any", 1, new CombatWeapon("AimMod CS Smoke Grenade", 0, 1, 0, false), 1),
        new(Molotov, "Molotov", "MO", 400, CsRules.T, 1, new CombatWeapon("AimMod CS Molotov", FireDps, 1, 0, false), 1),
        new(Incendiary, "Incendiary Grenade", "IN", 600, CsRules.CT, 1, new CombatWeapon("AimMod CS Incendiary Grenade", FireDps, 1, 0, false), 1),
        new(Decoy, "Decoy Grenade", "DC", 50, "any", 1, new CombatWeapon("AimMod CS Decoy Grenade", 0, 1, 0, false), 1),
    ];
    public static CsGrenade? Find(string? id) => All.FirstOrDefault(g => g.Id == id);
    public static bool IsFire(string? kind) => kind is Molotov or Incendiary;
    // The kill feed and armour know grenades as CS items in the grenade slot.
    public static readonly CsWeapon[] Weapons = All.Select(g => new CsWeapon(g.Id, g.Label, g.Price, g.Side, CsRules.GrenadeSlot, "grenade", KillReward, g.ArmorPenetration, g.Combat,
        new CsLook(CsRules.BlankModel, "-", 0, 0, 0, 0))).ToArray();

    // Why `kind` can't join what a player carries (null: it can): four in all, the kind's own limit,
    // and one fire grenade (molotov or incendiary).
    public static string? CarryProblem(IReadOnlyCollection<string> have, string kind)
    {
        if (Find(kind) is not { } g) return "unknown-item";
        if (have.Count(k => k == kind) >= g.Max) return "owned";
        if (IsFire(kind) && have.Any(IsFire)) return "owned";
        if (have.Count >= MaxCarried) return "carry-limit";
        return null;
    }

    // The slot's order (key 4 cycles through it): HE, flash, smoke, fire, decoy.
    public static int Order(string kind) => Array.FindIndex(All, g => g.Id == kind);
    public static IReadOnlyList<string> Sorted(IEnumerable<string> kinds) => kinds.Where(k => Find(k) is not null).OrderBy(Order).ToArray();
    // Key 4 with a grenade already in hand: the next kind carried after `current` (wrapping).
    public static string? Next(IReadOnlyList<string> carried, string? current)
    {
        var kinds = Sorted(carried).Distinct().ToList();
        if (kinds.Count == 0) return null;
        var at = current is null ? -1 : kinds.IndexOf(current);
        return kinds[(at + 1) % kinds.Count];
    }

    // HE damage at a distance (cm) from the blast: a bell falloff (CS2), nothing past the radius.
    public static double HeDamageAt(double distanceCm)
    {
        if (distanceCm >= HeRadiusCm) return 0;
        var sigma = HeRadiusCm / 3;
        return HeDamage * Math.Exp(-distanceCm * distanceCm / (2 * sigma * sigma));
    }

    // How hard a flash hits (0..1): how directly the player looks at the pop (the dot of the view and
    // the direction to it), times its distance. Behind you it still whites out a little.
    public static double FlashAmount(double viewDot, double distanceCm)
    {
        var angle = viewDot >= 0.6 ? 1 : viewDot >= 0 ? 0.35 + 0.65 * viewDot / 0.6 : viewDot >= -0.6 ? 0.1 + 0.25 * (viewDot + 0.6) / 0.6 : 0.1;
        var near = distanceCm <= FlashNearCm ? 1 : Math.Max(0, 1 - (distanceCm - FlashNearCm) / (FlashFarCm - FlashNearCm));
        return Math.Round(Math.Clamp(angle * near, 0, 1), 3);
    }
    public static CsFlashView? FlashFor(long at, double amount) =>
        amount < 0.05 ? null : new CsFlashView(at, (int)Math.Round(FlashHoldMaxMs * amount * amount), (int)Math.Round(FlashFadeMaxMs * amount), amount);
    // The white over the screen `now`: full (by amount) while held, then clearing.
    public static double FlashAlpha(CsFlashView? f, long now)
    {
        if (f is null || now < f.At) return 0;
        var peak = Math.Min(1, 0.35 + f.Amount);
        var age = now - f.At;
        if (age < f.HoldMs) return Math.Round(peak, 2);
        var fade = 1 - (age - f.HoldMs) / (double)Math.Max(1, f.FadeMs);
        return fade <= 0 ? 0 : Math.Round(peak * fade * fade, 2);
    }

    // A smoke's size now (0..1): growing in, full, thinning out.
    public static double SmokeScale(CsGrenadeView s, long now)
    {
        if (s.State != "smoke" || s.Ends is not { } ends || now < s.At || now >= ends) return 0;
        return Math.Clamp(Math.Min((now - s.At) / (double)SmokeGrowMs, (ends - now) / (double)SmokeFadeMs), 0, 1);
    }
    // The cloud's centre: it sits on the ground where the grenade lay.
    public static double[] SmokeCentre(double[] at) => [at[0], at[1], at[2] + SmokeHalfHeightCm - 40];
    // A point in the cloud's own space (the cloud is the unit sphere).
    static (double X, double Y, double Z) Local(double[] centre, double x, double y, double z) =>
        ((x - centre[0]) / SmokeRadiusCm, (y - centre[1]) / SmokeRadiusCm, (z - centre[2]) / SmokeHalfHeightCm);

    // Whether a smoke hides b from a (eyes, bot sight, name tags, flashes): the line passes through the
    // thick of the cloud (within 85 % of its size now).
    public static bool SmokeBlocks(IEnumerable<CsGrenadeView>? grenades, double[] a, double[] b, long now)
    {
        if (grenades is null) return false;
        foreach (var s in grenades)
        {
            var scale = SmokeScale(s, now);
            if (scale < 0.3 || s.Pos is not { Length: 3 } at) continue;
            var c = SmokeCentre(at);
            var p = Local(c, a[0], a[1], a[2]); var q = Local(c, b[0], b[1], b[2]);
            if (SegmentDistance(p, q) < 0.85 * scale) return true;
        }
        return false;
    }
    // How deep in a smoke a point is (0 outside, 1 in the thick of it): the HUD's grey veil.
    public static double SmokeInside(IEnumerable<CsGrenadeView>? grenades, double[] p, long now)
    {
        var most = 0.0;
        foreach (var s in grenades ?? [])
        {
            var scale = SmokeScale(s, now);
            if (scale <= 0 || s.Pos is not { Length: 3 } at) continue;
            var l = Local(SmokeCentre(at), p[0], p[1], p[2]);
            var r = Math.Sqrt(l.X * l.X + l.Y * l.Y + l.Z * l.Z);
            most = Math.Max(most, Math.Clamp((scale - r) / 0.3, 0, 1));
        }
        return Math.Round(most, 2);
    }
    static double SegmentDistance((double X, double Y, double Z) p, (double X, double Y, double Z) q)
    {
        var dx = q.X - p.X; var dy = q.Y - p.Y; var dz = q.Z - p.Z;
        var len = dx * dx + dy * dy + dz * dz;
        var t = len <= 1e-12 ? 0 : Math.Clamp(-(p.X * dx + p.Y * dy + p.Z * dz) / len, 0, 1);
        var x = p.X + dx * t; var y = p.Y + dy * t; var z = p.Z + dz * t;
        return Math.Sqrt(x * x + y * y + z * z);
    }
    // A fire goes out when a smoke covers it (the cloud's foot reaches the fire's middle).
    public static bool Extinguishes(double[] smoke, double[] fire) =>
        Math.Sqrt((smoke[0] - fire[0]) * (smoke[0] - fire[0]) + (smoke[1] - fire[1]) * (smoke[1] - fire[1])) <= SmokeRadiusCm + FireRadiusCm * 0.5 && Math.Abs(smoke[2] - fire[2]) <= 300;
    // Whether a player's feet are in a fire.
    public static bool InFire(double[] fire, double radius, double eyeX, double eyeY, double eyeZ)
    {
        var feet = eyeZ - EyeHeightCm;
        return Math.Sqrt((eyeX - fire[0]) * (eyeX - fire[0]) + (eyeY - fire[1]) * (eyeY - fire[1])) <= radius && feet >= fire[2] - 80 && feet <= fire[2] + 120;
    }

    // The decoy's fake gunfire: bursts of the owner's weapon, the same on every machine (seeded by
    // its id). Offsets in ms from the decoy's start.
    public static IReadOnlyList<long> DecoyShots(long id, string? weaponClass, long durationMs = DecoyMs)
    {
        var (gap, burst, pause) = weaponClass switch
        {
            "rifle" => (100, 4, 900), "smg" => (75, 6, 800), "sniper" => (1500, 1, 1400), "heavy" => (250, 2, 1200), _ => (180, 3, 1000),
        };
        var random = new Random(unchecked((int)(id * 2654435761L)));
        var list = new List<long>();
        long t = 300;
        while (t < durationMs - 200 && list.Count < 200)
        {
            var shots = Math.Max(1, burst + random.Next(-1, 2));
            for (var i = 0; i < shots && t < durationMs - 200; i++, t += gap) list.Add(t);
            t += pause + random.Next(0, 900);
        }
        return list;
    }
}

// Grenade flight (shared with AimModSteam's GrenadePhysics.hpp: the same steps in the same order,
// so a path bounces the same on any machine): gravity, bounces off what a line trace hits (45 % of
// the speed kept), sliding to a stop on the floor. The path is a short list of keys; between keys
// it is exact (a parabola in flight, an even slow-down when sliding), so a key list draws the same
// path everywhere.
static class GrenadePhysics
{
    public const double Unit = 4.4; // cm per CS unit on the ports
    public const double Gravity = 800 * 0.4 * Unit; // CS grenades: 0.4 of sv_gravity 800
    public const double Elasticity = 0.45, SlideNormal = 120, StopSpeed = 90, SlideDecel = 700, FloorNz = 0.7, Lift = 2, SlideProbe = 24, SlideWallLift = 6;
    public const double Step = 1.0 / 64, MaxSeconds = 8;
    public const int MaxKeys = 24;
    public const int Flight = 0, Slide = 1, Rest = 2;
    public const int NoImpact = 0, WallImpact = 1, FloorImpact = 2;
    // CS throw: 675 units/s at full strength (left mouse), 0.3 of it underhand (right), between with both.
    public const double ThrowSpeed = 750 * 0.9 * Unit;

    // T: ms after the throw. Motion: Flight, Slide or Rest. Impact: what it just hit (sounds, molotov).
    public readonly record struct Key(double T, double X, double Y, double Z, double Vx, double Vy, double Vz, int Motion, int Impact);
    // A line trace from a to b: the hit point and the surface normal, or null.
    public delegate (double[] Point, double[] Normal)? Trace(double[] a, double[] b);

    // The throw velocity for a view (Unreal pitch up positive, yaw degrees) and strength (1 full,
    // 0.5 both buttons, 0 underhand): aimed a little up as CS does (10 degrees at level).
    public static double[] ThrowVelocity(double pitch, double yaw, double strength)
    {
        strength = Math.Clamp(strength, 0, 1);
        var p = Math.Clamp(pitch, -89, 89);
        p += (90 - Math.Abs(p)) * 10 / 90;
        var speed = ThrowSpeed * (0.3 + 0.7 * strength);
        var pr = p * Math.PI / 180; var yr = yaw * Math.PI / 180;
        return [Math.Cos(pr) * Math.Cos(yr) * speed, Math.Cos(pr) * Math.Sin(yr) * speed, Math.Sin(pr) * speed];
    }

    public static double[] Pos(Key k, double tau)
    {
        if (k.Motion == Rest || tau <= 0) return [k.X, k.Y, k.Z];
        if (k.Motion == Slide)
        {
            var s0 = Math.Sqrt(k.Vx * k.Vx + k.Vy * k.Vy);
            if (s0 <= 1e-9) return [k.X, k.Y, k.Z];
            var tc = Math.Min(tau, s0 / SlideDecel);
            var d = s0 * tc - SlideDecel * tc * tc / 2;
            return [k.X + k.Vx / s0 * d, k.Y + k.Vy / s0 * d, k.Z];
        }
        return [k.X + k.Vx * tau, k.Y + k.Vy * tau, k.Z + k.Vz * tau - Gravity * tau * tau / 2];
    }
    static double[] Vel(Key k, double tau) => [k.Vx, k.Vy, k.Vz - Gravity * tau];

    // Where the grenade is `tMs` after the throw.
    public static double[] At(IReadOnlyList<Key> keys, double tMs)
    {
        if (keys.Count == 0) return [0, 0, 0];
        var k = keys[0];
        foreach (var key in keys) { if (key.T <= tMs) k = key; else break; }
        return Pos(k, (tMs - k.T) / 1000);
    }

    static double Len(double x, double y, double z) => Math.Sqrt(x * x + y * y + z * z);

    public static List<Key> Simulate(double[] origin, double[] velocity, Trace trace)
    {
        var keys = new List<Key> { new(0, origin[0], origin[1], origin[2], velocity[0], velocity[1], velocity[2], Flight, NoImpact) };
        var cur = keys[0];
        while (cur.Motion != Rest)
        {
            if (keys.Count >= MaxKeys)
            {
                keys.Add(new Key(cur.T, cur.X, cur.Y, cur.Z, 0, 0, 0, Rest, NoImpact));
                break;
            }
            Key? next = null;
            var prevTau = 0.0;
            var prev = Pos(cur, 0);
            for (var i = 1; next is null; i++)
            {
                var tau = i * Step;
                if (cur.T / 1000 + tau > MaxSeconds) { var p0 = Pos(cur, prevTau); next = new Key(cur.T + prevTau * 1000, p0[0], p0[1], p0[2], 0, 0, 0, Rest, NoImpact); break; }
                if (cur.Motion == Flight)
                {
                    var p = Pos(cur, tau);
                    if (trace(prev, p) is { } hit && Normal(hit.Normal) is { } n)
                    {
                        var seg = Len(p[0] - prev[0], p[1] - prev[1], p[2] - prev[2]);
                        var f = seg > 1e-9 ? Math.Clamp(Len(hit.Point[0] - prev[0], hit.Point[1] - prev[1], hit.Point[2] - prev[2]) / seg, 0, 1) : 0;
                        var th = prevTau + f * Step;
                        var v = Vel(cur, th);
                        var vn = v[0] * n[0] + v[1] * n[1] + v[2] * n[2];
                        if (vn < 0)
                        {
                            var hp = new[] { hit.Point[0] + n[0] * Lift, hit.Point[1] + n[1] * Lift, hit.Point[2] + n[2] * Lift };
                            var r = new[] { (v[0] - 2 * vn * n[0]) * Elasticity, (v[1] - 2 * vn * n[1]) * Elasticity, (v[2] - 2 * vn * n[2]) * Elasticity };
                            var t = cur.T + th * 1000;
                            if (n[2] > FloorNz)
                            {
                                if (-vn * Elasticity < SlideNormal)
                                {
                                    var speed = Math.Sqrt(r[0] * r[0] + r[1] * r[1]);
                                    next = speed < StopSpeed ? new Key(t, hp[0], hp[1], hp[2], 0, 0, 0, Rest, FloorImpact) : new Key(t, hp[0], hp[1], hp[2], r[0], r[1], 0, Slide, FloorImpact);
                                }
                                else next = new Key(t, hp[0], hp[1], hp[2], r[0], r[1], r[2], Flight, FloorImpact);
                            }
                            else next = new Key(t, hp[0], hp[1], hp[2], r[0], r[1], r[2], Flight, WallImpact);
                            break;
                        }
                    }
                    prev = p; prevTau = tau;
                }
                else
                {
                    var s0 = Math.Sqrt(cur.Vx * cur.Vx + cur.Vy * cur.Vy);
                    var stop = s0 / SlideDecel;
                    var tc = Math.Min(tau, stop);
                    var p = Pos(cur, tc);
                    var from = new[] { prev[0], prev[1], prev[2] + SlideWallLift }; var to = new[] { p[0], p[1], p[2] + SlideWallLift };
                    if (Len(to[0] - from[0], to[1] - from[1], 0) > 1e-9 && trace(from, to) is { } wall && Normal([wall.Normal[0], wall.Normal[1], 0]) is { } n)
                    {
                        var seg = Len(to[0] - from[0], to[1] - from[1], 0);
                        var f = Math.Clamp(Len(wall.Point[0] - from[0], wall.Point[1] - from[1], 0) / seg, 0, 1);
                        var th = prevTau + f * (tc - prevTau);
                        var speed = s0 - SlideDecel * th;
                        var vx = cur.Vx / s0 * speed; var vy = cur.Vy / s0 * speed;
                        var vn = vx * n[0] + vy * n[1];
                        var rx = (vx - 2 * vn * n[0]) * Elasticity; var ry = (vy - 2 * vn * n[1]) * Elasticity;
                        var hp = Pos(cur, th);
                        hp[0] += n[0] * Lift; hp[1] += n[1] * Lift;
                        var t = cur.T + th * 1000;
                        next = Math.Sqrt(rx * rx + ry * ry) < StopSpeed ? new Key(t, hp[0], hp[1], hp[2], 0, 0, 0, Rest, WallImpact) : new Key(t, hp[0], hp[1], hp[2], rx, ry, 0, Slide, WallImpact);
                        break;
                    }
                    if (trace(p, [p[0], p[1], p[2] - SlideProbe]) is null)
                    {
                        var speed = s0 - SlideDecel * tc;
                        next = new Key(cur.T + tc * 1000, p[0], p[1], p[2], cur.Vx / s0 * speed, cur.Vy / s0 * speed, 0, Flight, NoImpact);
                        break;
                    }
                    if (tau >= stop) { next = new Key(cur.T + stop * 1000, p[0], p[1], p[2], 0, 0, 0, Rest, NoImpact); break; }
                    prev = p; prevTau = tau;
                }
            }
            keys.Add(next!.Value);
            cur = next.Value;
        }
        return keys;
    }

    static double[]? Normal(double[] n)
    {
        var l = Len(n[0], n[1], n[2]);
        return l < 1e-6 || !double.IsFinite(l) ? null : [n[0] / l, n[1] / l, n[2] / l];
    }

    // A level floor at height z (the fallback with no traces from the game): it is hit from above.
    public static Trace Floor(double z) => (a, b) =>
    {
        if (a[2] < z || b[2] >= z || Math.Abs(a[2] - b[2]) < 1e-12) return null;
        var f = (a[2] - z) / (a[2] - b[2]);
        return ([a[0] + (b[0] - a[0]) * f, a[1] + (b[1] - a[1]) * f, z], [0, 0, 1]);
    };

    // The key list on the wire: 9 numbers a key (t, x, y, z, vx, vy, vz, motion, impact), 1 decimal.
    public static double[] Flat(IReadOnlyList<Key> keys) =>
        keys.SelectMany(k => new[] { R(k.T), R(k.X), R(k.Y), R(k.Z), R(k.Vx), R(k.Vy), R(k.Vz), k.Motion, k.Impact }).ToArray();
    public static IReadOnlyList<Key> Unflat(double[]? flat)
    {
        var list = new List<Key>();
        if (flat is null) return list;
        for (var i = 0; i + 8 < flat.Length && list.Count < MaxKeys + 1; i += 9)
            list.Add(new Key(flat[i], flat[i + 1], flat[i + 2], flat[i + 3], flat[i + 4], flat[i + 5], flat[i + 6], (int)flat[i + 7], (int)flat[i + 8]));
        return list;
    }
    static double R(double v) => Math.Round(v, 1);

    // When and where a grenade of `kind` goes off on this path (ms after the throw), and whether it
    // does its work (a molotov that never reached the floor in time bursts in the air):
    //  - HE and flash: 1.5 s after the throw, wherever they are;
    //  - smoke and decoy: once they lie still (a smoke at least 1 s in);
    //  - molotov and incendiary: on the first landing on a floor within 2 s, else in the air at 2 s.
    public static (double T, double[] At, bool Works) Detonation(string kind, IReadOnlyList<Key> keys)
    {
        var rest = keys[^1];
        switch (kind)
        {
            case GrenadeRules.He: return (GrenadeRules.HeFuseMs, At(keys, GrenadeRules.HeFuseMs), true);
            case GrenadeRules.Flash: return (GrenadeRules.FlashFuseMs, At(keys, GrenadeRules.FlashFuseMs), true);
            case GrenadeRules.Smoke: return (Math.Max(rest.T, 1000) + 250, [rest.X, rest.Y, rest.Z], true);
            case GrenadeRules.Decoy: return (Math.Max(rest.T, 800) + 200, [rest.X, rest.Y, rest.Z], true);
        }
        foreach (var k in keys.Skip(1))
        {
            if (k.T > GrenadeRules.FireFuseMs) break;
            if (k.Impact == FloorImpact) return (k.T, [k.X, k.Y, k.Z], true);
        }
        return (GrenadeRules.FireFuseMs, At(keys, GrenadeRules.FireFuseMs), false);
    }
}

// What the host asks its game to trace for grenades, and the answers (MultiplayerService.Grenades.cs).
sealed record GrenadePathRequest(long Id, string Kind, double[] Origin, double[] Velocity);
sealed record GrenadeLosRequest(int Tag, double[] From, double[] To);

// The host's grenades in one CS match: throws, paths, detonations and effects. CsMatch owns it and
// hands it the players and the damage and flash rules.
sealed class CsGrenadeField
{
    // How long a path from the game may take before the level-floor fallback flies it, and how long a
    // blast waits for its line-of-sight answers (then: clear).
    public const long PathWaitMs = 350, LosWaitMs = 300;
    sealed class Thrown
    {
        public long Id; public required string Kind, Owner; public string? Weapon; public long At; public required double[] Origin, Velocity;
        public IReadOnlyList<GrenadePhysics.Key> Keys = []; public bool Final; public long? GoesOff; public double[]? Where; public bool Works; public bool Done;
    }
    sealed class Effect { public long Id; public required string Kind, Owner, State; public required double[] At; public long Starts, Ends, NextTick; public string? Weapon; }
    sealed class Blast
    {
        public long Id; public required string Kind, Owner; public required double[] At; public long T;
        public List<(string Victim, int Tag, double[] Point, bool? Clear)> Checks = []; public long Deadline; public bool Applied;
    }
    readonly List<Thrown> thrown = [];
    readonly List<Effect> effects = [];
    readonly List<Blast> blasts = [];
    long nextId; int nextTag;
    public int Revision { get; private set; }
    void Changed() => Revision++;

    // Set by CsMatch: the players (id, team, latest camera, alive), area damage and flashes.
    public required Func<IEnumerable<(string Id, int Team, TrackSample? At, bool Alive)>> Players;
    public required Action<string, string, double, CsGrenade, long, double[]?> Damage;
    public required Action<string, CsFlashView> Flashed;
    // A level floor under a throw when the game can't trace (eye height below the thrower).
    public Func<double[], GrenadePhysics.Trace> Fallback = o => GrenadePhysics.Floor(o[2] - GrenadeRules.EyeHeightCm);

    public long Throw(string owner, string kind, double[] origin, double[] velocity, long now, string? weaponClass)
    {
        var g = new Thrown { Id = ++nextId, Kind = kind, Owner = owner, Weapon = weaponClass, At = now, Origin = origin, Velocity = velocity };
        g.Keys = [new GrenadePhysics.Key(0, origin[0], origin[1], origin[2], velocity[0], velocity[1], velocity[2], GrenadePhysics.Flight, GrenadePhysics.NoImpact)];
        thrown.Add(g);
        Changed();
        return g.Id;
    }

    // The game's path for a throw (AimModSteam's traces).
    public bool SetPath(long id, IReadOnlyList<GrenadePhysics.Key> keys)
    {
        if (thrown.FirstOrDefault(t => t.Id == id) is not { Final: false } g || keys.Count < 2 || keys[^1].Motion != GrenadePhysics.Rest) return false;
        Settle(g, keys);
        return true;
    }
    void Settle(Thrown g, IReadOnlyList<GrenadePhysics.Key> keys)
    {
        g.Keys = keys; g.Final = true;
        var (t, at, works) = GrenadePhysics.Detonation(g.Kind, keys);
        g.GoesOff = g.At + (long)Math.Round(t); g.Where = at; g.Works = works;
        Changed();
    }

    public IReadOnlyList<GrenadePathRequest> PathRequests => thrown.Where(t => !t.Final && !t.Done).Select(t => new GrenadePathRequest(t.Id, t.Kind, t.Origin, t.Velocity)).ToArray();
    public IReadOnlyList<GrenadeLosRequest> LosRequests => blasts.Where(b => !b.Applied).SelectMany(b => b.Checks.Where(c => c.Clear is null).Select(c => new GrenadeLosRequest(c.Tag, b.At, c.Point))).ToArray();
    public void AnswerLos(int tag, bool clear)
    {
        foreach (var b in blasts)
            for (var i = 0; i < b.Checks.Count; i++)
                if (b.Checks[i].Tag == tag && b.Checks[i].Clear is null) b.Checks[i] = b.Checks[i] with { Clear = clear };
    }

    // Round over: nothing carries into the next one.
    public void Clear() { if (thrown.Count + effects.Count + blasts.Count == 0) return; thrown.Clear(); effects.Clear(); blasts.Clear(); Changed(); }

    public void Tick(long now)
    {
        foreach (var g in thrown.Where(t => !t.Done).ToArray())
        {
            if (!g.Final && now - g.At >= PathWaitMs) Settle(g, GrenadePhysics.Simulate(g.Origin, g.Velocity, Fallback(g.Origin)));
            if (g.GoesOff is { } at && now >= at) GoOff(g, at);
        }
        thrown.RemoveAll(t => t.Done && now - t.At > 30_000);
        foreach (var b in blasts.Where(b => !b.Applied))
            if (now >= b.Deadline || b.Checks.All(c => c.Clear is not null)) Apply(b, now);
        var players = Players().ToList();
        foreach (var e in effects.ToArray())
        {
            if (now >= e.Ends) { effects.Remove(e); if (e.Kind == GrenadeRules.Decoy) AddBlast(e.Kind, e.Owner, e.At, e.Ends); Changed(); continue; }
            if (e.State != "fire" || now < e.NextTick) continue;
            for (; e.NextTick <= now; e.NextTick += GrenadeRules.FireTickMs)
                foreach (var p in players)
                    if (p.Alive && p.At is { } eye && GrenadeRules.InFire(e.At, GrenadeRules.FireRadiusCm, eye.X, eye.Y, eye.Z) && GrenadeRules.Find(e.Kind) is { } kind)
                        Damage(e.Owner, p.Id, GrenadeRules.FireDps * GrenadeRules.FireTickMs / 1000.0, kind, now, null);
        }
        if (blasts.RemoveAll(b => b.Applied && now - b.T > 1500) > 0) Changed();
    }

    Blast AddBlast(string kind, string owner, double[] at, long t)
    {
        var b = new Blast { Id = ++nextId, Kind = kind, Owner = owner, At = at, T = t, Deadline = t + LosWaitMs, Applied = true };
        blasts.Add(b);
        Changed();
        return b;
    }

    void GoOff(Thrown g, long at)
    {
        g.Done = true;
        Changed();
        var where = g.Where!;
        switch (g.Kind)
        {
            case GrenadeRules.He or GrenadeRules.Flash:
            {
                // The blast: line-of-sight traces to everyone it could reach, answered by the game (or
                // clear after a moment), then the damage or the blindness.
                var blast = AddBlast(g.Kind, g.Owner, [where[0], where[1], where[2] + 10], at);
                blast.Applied = false;
                var reach = g.Kind == GrenadeRules.He ? GrenadeRules.HeRadiusCm : GrenadeRules.FlashFarCm;
                foreach (var p in Players())
                    if (p.Alive && p.At is { } eye && Distance(where, [eye.X, eye.Y, eye.Z]) < reach + 200)
                    {
                        blast.Checks.Add((p.Id, ++nextTag, [eye.X, eye.Y, eye.Z], null));
                        if (g.Kind == GrenadeRules.He) blast.Checks.Add((p.Id, ++nextTag, [eye.X, eye.Y, eye.Z - 64], null));
                    }
                break;
            }
            case GrenadeRules.Smoke:
                effects.Add(new Effect { Id = g.Id, Kind = g.Kind, Owner = g.Owner, State = "smoke", At = where, Starts = at, Ends = at + GrenadeRules.SmokeMs });
                // A smoke puts out the fires it covers.
                foreach (var fire in effects.Where(e => e.State == "fire" && GrenadeRules.Extinguishes(where, e.At)).ToArray())
                {
                    effects.Remove(fire);
                    AddBlast("extinguished", fire.Owner, fire.At, at);
                }
                break;
            case GrenadeRules.Decoy:
                effects.Add(new Effect { Id = g.Id, Kind = g.Kind, Owner = g.Owner, State = "decoy", At = where, Starts = at, Ends = at + GrenadeRules.DecoyMs, Weapon = g.Weapon });
                break;
            default:
                // Fire on the ground, unless it burst in the air or lands in a smoke.
                if (!g.Works) { AddBlast(g.Kind, g.Owner, where, at); break; }
                if (effects.Any(e => e.State == "smoke" && GrenadeRules.Extinguishes(e.At, where))) { AddBlast("extinguished", g.Owner, where, at); break; }
                effects.Add(new Effect { Id = g.Id, Kind = g.Kind, Owner = g.Owner, State = "fire", At = where, Starts = at, Ends = at + GrenadeRules.FireMs, NextTick = at });
                break;
        }
    }

    void Apply(Blast b, long now)
    {
        b.Applied = true;
        Changed();
        var grenade = GrenadeRules.Find(b.Kind)!;
        var smokes = View().Where(v => v.State == "smoke").ToArray();
        foreach (var victim in b.Checks.Select(c => c.Victim).Distinct())
        {
            var checks = b.Checks.Where(c => c.Victim == victim).ToList();
            if (!checks.Any(c => c.Clear != false)) continue; // every line to them is blocked
            var p = Players().FirstOrDefault(x => x.Id == victim);
            if (!p.Alive || p.At is not { } eye) continue;
            if (b.Kind == GrenadeRules.He)
            {
                var centre = new[] { eye.X, eye.Y, eye.Z - 64 };
                var damage = GrenadeRules.HeDamageAt(Distance(b.At, centre));
                if (damage < 1) continue;
                var d = Distance(b.At, centre);
                Damage(b.Owner, victim, damage, grenade, now, d > 1 ? [(centre[0] - b.At[0]) / d, (centre[1] - b.At[1]) / d, (centre[2] - b.At[2]) / d] : null);
            }
            else
            {
                var at = new[] { eye.X, eye.Y, eye.Z };
                if (GrenadeRules.SmokeBlocks(smokes, b.At, at, now)) continue;
                var dist = Distance(b.At, at);
                var (fx, fy, fz) = TrackGeometry.Direction(eye.Pitch, eye.Yaw);
                var dot = dist < 1 ? 1 : ((b.At[0] - at[0]) * fx + (b.At[1] - at[1]) * fy + (b.At[2] - at[2]) * fz) / dist;
                if (GrenadeRules.FlashFor(b.T, GrenadeRules.FlashAmount(dot, dist)) is { } flash) Flashed(victim, flash);
            }
        }
    }

    static double Distance(double[] a, double[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));

    public IReadOnlyList<CsGrenadeView> View()
    {
        var list = new List<CsGrenadeView>();
        foreach (var g in thrown.Where(t => !t.Done))
            list.Add(new CsGrenadeView(g.Id, g.Kind, g.Owner, "flying", g.At, g.GoesOff, null, GrenadePhysics.Flat(g.Keys), g.Weapon));
        foreach (var e in effects)
            list.Add(new CsGrenadeView(e.Id, e.Kind, e.Owner, e.State, e.Starts, e.Ends, R(e.At), null, e.Weapon, e.State == "fire" ? GrenadeRules.FireRadiusCm : e.State == "smoke" ? GrenadeRules.SmokeRadiusCm : 0));
        foreach (var b in blasts)
            list.Add(new CsGrenadeView(b.Id, b.Kind, b.Owner, "blast", b.T, null, R(b.At)));
        return list;
    }
    static double[] R(double[] v) => v.Select(x => Math.Round(x, 1)).ToArray();
}

// How a bot aims a grenade (GrenadeAim, used by BotGrenades and open to the bot brain): the
// velocity that brings it from `from` to `to`, by the throw strengths a player has.
static class GrenadeAim
{
    // At `to` after `seconds` (HE and flash: they go off in the air there), if it's within a full throw.
    public static double[] Timed(double[] from, double[] to, double seconds)
    {
        var v = new[] { (to[0] - from[0]) / seconds, (to[1] - from[1]) / seconds, (to[2] - from[2] + GrenadePhysics.Gravity * seconds * seconds / 2) / seconds };
        var speed = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
        return speed <= GrenadePhysics.ThrowSpeed ? v : v.Select(x => x * GrenadePhysics.ThrowSpeed / speed).ToArray();
    }
    // Landing on `to` (smoke, molotov, decoy): the low arc of the weakest throw that reaches it, else 45 degrees at full.
    public static double[] Lob(double[] from, double[] to)
    {
        var dx = to[0] - from[0]; var dy = to[1] - from[1]; var dz = to[2] - from[2];
        var d = Math.Max(1, Math.Sqrt(dx * dx + dy * dy)); var g = GrenadePhysics.Gravity;
        foreach (var strength in new[] { 0.0, 0.5, 1.0 })
        {
            var s = GrenadePhysics.ThrowSpeed * (0.3 + 0.7 * strength);
            var root = s * s * s * s - g * (g * d * d + 2 * dz * s * s);
            if (root < 0) continue;
            var angle = Math.Atan((s * s - Math.Sqrt(root)) / (g * d));
            return [Math.Cos(angle) * s * dx / d, Math.Cos(angle) * s * dy / d, Math.Sin(angle) * s];
        }
        var full = GrenadePhysics.ThrowSpeed * Math.Sqrt(0.5);
        return [full * dx / d, full * dy / d, full];
    }
}

// The grenade files between the host's service and its game (AimModSteam, GrenadePhysics.hpp):
//   grenade-sim.tsv (service -> AimModSteam), while something waits for an answer:
//     AIMMOD_GRENADESIM_1\t<seq>
//     throw\t<id>\t<kind>\t<x>\t<y>\t<z>\t<vx>\t<vy>\t<vz>      fly it with line traces
//     los\t<tag>\t<ax>\t<ay>\t<az>\t<bx>\t<by>\t<bz>          is the line clear?
//   grenade-paths.tsv (AimModSteam -> service):
//     AIMMOD_GRENADEPATHS_1\t<unix ms>
//     path\t<id>\t<keys>\t<t x y z vx vy vz motion impact> x keys
//     los\t<tag>\t<0|1>
static class GrenadeFiles
{
    static string F(double v) => Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);
    public static string Sim(long sequence, IEnumerable<GrenadePathRequest> paths, IEnumerable<GrenadeLosRequest> los)
    {
        var sb = new StringBuilder("AIMMOD_GRENADESIM_1\t").Append(sequence).Append('\n');
        foreach (var p in paths.Take(32))
            sb.Append("throw\t").Append(p.Id).Append('\t').Append(p.Kind).Append('\t').Append(string.Join('\t', p.Origin.Concat(p.Velocity).Select(F))).Append('\n');
        foreach (var l in los.Take(128))
            sb.Append("los\t").Append(l.Tag).Append('\t').Append(string.Join('\t', l.From.Concat(l.To).Select(F))).Append('\n');
        return sb.ToString();
    }

    public static (Dictionary<long, IReadOnlyList<GrenadePhysics.Key>> Paths, Dictionary<int, bool> Los)? Paths(string text, long now)
    {
        var lines = text.Replace("\r", "").Split('\n');
        if (lines.Length == 0 || !lines[0].StartsWith("AIMMOD_GRENADEPATHS_1\t", StringComparison.Ordinal)) return null;
        if (!long.TryParse(lines[0][22..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var at) || Math.Abs(now - at) > 10_000) return null;
        var paths = new Dictionary<long, IReadOnlyList<GrenadePhysics.Key>>(); var los = new Dictionary<int, bool>();
        static double? Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) && Math.Abs(v) < 1e7 ? v : null;
        foreach (var line in lines.Skip(1).Take(512))
        {
            var p = line.Split('\t');
            if (p.Length == 3 && p[0] == "los" && int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var tag) && p[2] is "0" or "1") los[tag] = p[2] == "1";
            else if (p.Length >= 3 && p[0] == "path" && long.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                && int.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 2 and <= GrenadePhysics.MaxKeys + 1 && p.Length == 3 + n * 9)
            {
                var keys = new List<GrenadePhysics.Key>();
                for (var k = 0; k < n; k++)
                {
                    var v = new double[9];
                    var ok = true;
                    for (var i = 0; i < 9 && ok; i++) { if (Num(p[3 + k * 9 + i]) is { } x) v[i] = x; else ok = false; }
                    if (!ok || v[7] is not (0 or 1 or 2) || v[8] is not (0 or 1 or 2) || (keys.Count > 0 && v[0] < keys[^1].T)) { keys.Clear(); break; }
                    keys.Add(new GrenadePhysics.Key(v[0], v[1], v[2], v[3], v[4], v[5], v[6], (int)v[7], (int)v[8]));
                }
                if (keys.Count == n) paths[id] = keys;
            }
        }
        return (paths, los);
    }
}

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
//  - smoke, decoy: At = start, Ends = end, Pos = where it lies;
//  - fire: At = when it caught, Ends = when its last flame dies, Pos = where it caught, Radius = how far
//    its flames reach from there, Flames = the flames on the floor (4 numbers each: x, y, z of the
//    floor, ms after At it starts; each burns GrenadeRules.FireMs);
//  - blast: an HE explosion, a flash pop, a molotov that burst in the air or fizzled in smoke, a decoy's last pop;
//  - dropped: a grenade a player dropped when they went down (Pos on the floor), there to pick up.
// Weapon: a decoy's sound class (pistol, smg, rifle, sniper, heavy); Gun: the owner's weapon id it
// mimics (its fire rate).
sealed record CsGrenadeView(long Id, string Kind, string Owner, string State, long At, long? Ends = null, double[]? Pos = null, double[]? Keys = null, string? Weapon = null, double Radius = 0,
    string? Gun = null, double[]? Flames = null);

static class GrenadeRules
{
    public const string He = "he", Flash = "flash", Smoke = "smoke", Decoy = "decoy", Molotov = "molotov", Incendiary = "incendiary";
    // CS2: four grenades in all, one of each but two flashbangs.
    public const int MaxCarried = 4;
    // HE (CS2 98 damage, 350 units at 4.4 cm): falls off with distance, half reaches health through armour.
    public const double HeDamage = 98, HeRadiusCm = 350 * GrenadePhysics.Unit, HeArmorPenetration = 0.5;
    // CS: HE and flash go off at the grenade's first 0.2 s think after 1.5 s (1.6 s); a fire grenade
    // bursts in the air at 2 s; a smoke and a decoy at the first think once they lie still.
    public const long HeFuseMs = 1600, FlashFuseMs = 1600, FireFuseMs = 2000, ThinkMs = 200;
    // Flash (CS-like): full effect within 400 units (17.6 m), none beyond 1500 (66 m); looking at it
    // close up: 2.5 s of full white, then 2.8 s clearing (about 5 s in all); less by angle and distance.
    public const double FlashNearCm = 400 * GrenadePhysics.Unit, FlashFarCm = 1500 * GrenadePhysics.Unit;
    public const int FlashHoldMaxMs = 2500, FlashFadeMaxMs = 2800;
    // Smoke: a cloud about 12 m across and 6 m tall for 18 s; it spreads out over 1 s and thins over the last 2 s.
    public const double SmokeRadiusCm = 620, SmokeHalfHeightCm = 300;
    public const long SmokeMs = 18_000, SmokeGrowMs = 1000, SmokeFadeMs = 2000;
    // Fire (CS's inferno): up to 16 flames 42 units apart, spreading over the floor from where it
    // caught (within 150 units, never through a wall, down steps and slopes more easily than up),
    // one ring of flames every 0.16 s; each flame covers 1.1 m and burns 7 s; 40 damage a second
    // (0.25 s ticks) to anyone standing in one, armour doesn't help; a smoke puts out what it covers.
    public const double FlameRadiusCm = 110, FlameSpacingCm = 42 * GrenadePhysics.Unit, FireRangeCm = 150 * GrenadePhysics.Unit, FireStepUpCm = 50, FireStepDownCm = 200;
    public const int MaxFlames = 16, MaxFireRings = 4;
    public const long FlameSpreadMs = 160;
    // How far a fire reaches on open floor (two rings of flames): what a bot can assume a fire covers.
    public const double FireRadiusCm = 2 * FlameSpacingCm + FlameRadiusCm;
    public const double FireDps = 40;
    public const long FireMs = 7_000, FireTickMs = 250;
    // Decoy: fake gunfire for 15 s.
    public const long DecoyMs = 15_000;
    // A dropped grenade is picked up by walking over it (within this of its spot).
    public const double PickupCm = 90;
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
    // What falls from a player who goes down (CS): the grenade in their hand, else their most
    // valuable one (null: none).
    public static string? DropOnDeath(IReadOnlyList<string> carried, string? inHand) =>
        inHand is not null && carried.Contains(inHand) ? inHand : carried.Where(k => Find(k) is not null).OrderByDescending(k => Find(k)!.Price).ThenBy(Order).FirstOrDefault();

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
    // How white the screen gets: fully white from about 0.6 (looking near the pop, or from the side
    // close up), a light veil for a glance or a flash behind you.
    public static double FlashPeak(CsFlashView f) => Math.Round(Math.Min(1, 1.6 * f.Amount), 2);
    public static double FlashAlpha(CsFlashView? f, long now)
    {
        if (f is null || now < f.At) return 0;
        var peak = FlashPeak(f);
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
    // A smoke covers a spot on the floor (a flame, a fire grenade landing): within its footprint, from
    // a little below where it lay to its top.
    public static bool Extinguishes(double[] smoke, double[] fire) =>
        Math.Sqrt((smoke[0] - fire[0]) * (smoke[0] - fire[0]) + (smoke[1] - fire[1]) * (smoke[1] - fire[1])) <= SmokeRadiusCm + FlameRadiusCm * 0.5
        && fire[2] >= smoke[2] - 150 && fire[2] <= smoke[2] + 2 * SmokeHalfHeightCm;
    // Whether a player's feet are in a fire's flame (a disc of `radius` on the floor at `fire`).
    public static bool InFire(double[] fire, double radius, double eyeX, double eyeY, double eyeZ)
    {
        var feet = eyeZ - EyeHeightCm;
        return Math.Sqrt((eyeX - fire[0]) * (eyeX - fire[0]) + (eyeY - fire[1]) * (eyeY - fire[1])) <= radius && feet >= fire[2] - 80 && feet <= fire[2] + 120;
    }
    // The flames of a fire burning at `now` (x, y, z of each): those started and not yet out.
    public static IEnumerable<double[]> Burning(CsGrenadeView fire, long now)
    {
        if (fire.State != "fire") yield break;
        if (fire.Flames is not { Length: >= 4 } f)
        {
            if (fire.Pos is { Length: 3 } p && now >= fire.At && (fire.Ends is not { } e || now < e)) yield return p;
            yield break;
        }
        for (var i = 0; i + 3 < f.Length; i += 4)
        {
            var start = fire.At + (long)f[i + 3];
            if (now >= start && now < start + FireMs) yield return [f[i], f[i + 1], f[i + 2]];
        }
    }

    // The decoy's fake gunfire (CS): bursts of the owner's gun at its own fire rate (an automatic
    // 3-7 shots, a semi-automatic 1-3 taps, a sniper one shot) with pauses of 0.6-2.6 s between, the
    // same on every machine (seeded by its id). Offsets in ms from the decoy's start. `gun`: the
    // owner's weapon id (its interval and whether it's automatic); without it the class decides.
    public static IReadOnlyList<long> DecoyShots(long id, string? weaponClass, long durationMs = DecoyMs, string? gun = null)
    {
        var (gap, auto) = weaponClass switch
        {
            "rifle" => (100.0, true), "smg" => (75.0, true), "sniper" => (1500.0, false), "heavy" => (250.0, false), _ => (180.0, false),
        };
        if (CsRules.Find(gun) is { } w) { gap = Math.Max(60, w.Combat.TimeBetweenShots * 1000); auto = w.Combat.FullyAuto; }
        var single = weaponClass == "sniper" || gap >= 900;
        if (!auto) gap = Math.Max(gap, 220);
        var random = new Random(unchecked((int)(id * 2654435761L)));
        var list = new List<long>();
        double t = 400;
        while (t < durationMs - 300 && list.Count < 200)
        {
            var shots = single ? 1 : auto ? random.Next(3, 8) : random.Next(1, 4);
            for (var i = 0; i < shots && t < durationMs - 300; i++)
            {
                list.Add((long)Math.Round(t));
                t += gap + (auto ? 0 : random.Next(0, 90));
            }
            t += 600 + random.Next(0, 2000);
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
    // CS: a throw carries 1.25 times the thrower's own velocity (running and jumping throws go
    // further), at most this much of it (cm/s).
    public const double InheritShare = 1.25, MaxInheritCm = 1500;

    // T: ms after the throw. Motion: Flight, Slide or Rest. Impact: what it just hit (sounds, molotov).
    public readonly record struct Key(double T, double X, double Y, double Z, double Vx, double Vy, double Vz, int Motion, int Impact);
    // A line trace from a to b: the hit point and the surface normal, or null.
    public delegate (double[] Point, double[] Normal)? Trace(double[] a, double[] b);

    // The throw velocity for a view (Unreal pitch up positive, yaw degrees) and strength (1 full,
    // 0.5 both buttons, 0 underhand): aimed a little up as CS does (10 degrees at level), plus 1.25
    // times the thrower's velocity (`inherit`, cm/s; at most MaxInheritCm of it).
    public static double[] ThrowVelocity(double pitch, double yaw, double strength, double[]? inherit = null)
    {
        strength = Math.Clamp(strength, 0, 1);
        var p = Math.Clamp(pitch, -89, 89);
        p += (90 - Math.Abs(p)) * 10 / 90;
        var speed = ThrowSpeed * (0.3 + 0.7 * strength);
        var pr = p * Math.PI / 180; var yr = yaw * Math.PI / 180;
        var v = new[] { Math.Cos(pr) * Math.Cos(yr) * speed, Math.Cos(pr) * Math.Sin(yr) * speed, Math.Sin(pr) * speed };
        if (inherit is { Length: 3 } && inherit.All(double.IsFinite))
        {
            var len = Math.Sqrt(inherit.Sum(x => x * x));
            var k = len > MaxInheritCm ? MaxInheritCm / len : 1;
            for (var i = 0; i < 3; i++) v[i] += inherit[i] * k * InheritShare;
        }
        return v;
    }
    // Where the grenade leaves the hand (CS): at the eye for a full throw, 12 units lower underhand.
    public static double[] ThrowOrigin(double[] eye, double strength) => [eye[0], eye[1], eye[2] + (Math.Clamp(strength, 0, 1) * 12 - 12) * Unit];

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

    // The first 0.2 s think after `t` (ms): when a grenade that came to rest at `t` notices.
    public static double NextThink(double t) => (Math.Floor(t / GrenadeRules.ThinkMs) + 1) * GrenadeRules.ThinkMs;

    // When and where a grenade of `kind` goes off on this path (ms after the throw), and whether it
    // does its work (a molotov that never reached the floor in time bursts in the air):
    //  - HE and flash: 1.6 s after the throw, wherever they are;
    //  - smoke and decoy: at the first think once they lie still;
    //  - molotov and incendiary: on the first landing on a floor within 2 s, else in the air at 2 s.
    public static (double T, double[] At, bool Works) Detonation(string kind, IReadOnlyList<Key> keys)
    {
        var rest = keys[^1];
        switch (kind)
        {
            case GrenadeRules.He: return (GrenadeRules.HeFuseMs, At(keys, GrenadeRules.HeFuseMs), true);
            case GrenadeRules.Flash: return (GrenadeRules.FlashFuseMs, At(keys, GrenadeRules.FlashFuseMs), true);
            case GrenadeRules.Smoke or GrenadeRules.Decoy: return (NextThink(rest.T), [rest.X, rest.Y, rest.Z], true);
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
// The floor under (X, Y) between two heights: a fire's flame spreading, a dropped grenade landing.
sealed record GrenadeFloorRequest(int Tag, double X, double Y, double Top, double Bottom);

// The host's grenades in one CS match: throws, paths, detonations and effects, dropped grenades.
// CsMatch owns it and hands it the players and the damage and flash rules.
sealed class CsGrenadeField
{
    // How long a path from the game may take before the level-floor fallback flies it, and how long a
    // blast (or a spreading flame, or a dropped grenade) waits for its trace answers (then: clear, level).
    public const long PathWaitMs = 350, LosWaitMs = 300;
    sealed class Thrown
    {
        public long Id; public required string Kind, Owner; public string? Weapon, Gun; public long At; public required double[] Origin, Velocity, Eye;
        public IReadOnlyList<GrenadePhysics.Key> Keys = []; public bool Final; public long? GoesOff; public double[]? Where; public bool Works; public bool Done;
    }
    sealed class Effect { public long Id; public required string Kind, Owner, State; public required double[] At; public long Starts, Ends, NextTick; public string? Weapon, Gun; public Fire? Fire; }
    // A fire's flames on the floor and the spots it is still finding out about (floor, wall).
    sealed class Flame { public required double[] At; public long Starts; public int Ring; }
    sealed class Probe { public int Ring; public required double[] From; public double X, Y; public int FloorTag, LosTag; public double? Floor; public bool FloorAnswered; public bool? Clear; public long Asked; }
    sealed class Fire { public readonly List<Flame> Flames = []; public readonly List<Probe> Probes = []; public int Ring; public Random Random = new(1); }
    sealed class Blast
    {
        public long Id; public required string Kind, Owner; public required double[] At; public long T;
        public List<(string Victim, int Tag, double[] Point, bool? Clear)> Checks = []; public long Deadline; public bool Applied;
    }
    sealed class Item { public long Id; public required string Kind, Owner; public required double[] At; public long Since; public int FloorTag; public bool Settled; }
    readonly List<Thrown> thrown = [];
    readonly List<Effect> effects = [];
    readonly List<Blast> blasts = [];
    readonly List<Item> items = [];
    readonly Dictionary<int, double?> floors = new();
    long nextId; int nextTag;
    public int Revision { get; private set; }
    void Changed() => Revision++;

    // Set by CsMatch: the players (id, team, latest camera, alive), area damage and flashes.
    public required Func<IEnumerable<(string Id, int Team, TrackSample? At, bool Alive)>> Players;
    public required Action<string, string, double, CsGrenade, long, double[]?> Damage;
    public required Action<string, CsFlashView> Flashed;
    // The host's log of what each flash did (who it blinded and why not); set by the service.
    public Action<string>? Trace;
    // A level floor under a throw when the game can't trace (eye height below the thrower).
    public Func<double[], GrenadePhysics.Trace> Fallback = o => GrenadePhysics.Floor(o[2] - GrenadeRules.EyeHeightCm);

    // eye: the thrower's eye (the fallback's floor is eye height under it; an underhand throw starts lower).
    public long Throw(string owner, string kind, double[] origin, double[] velocity, long now, string? weaponClass, string? gun = null, double[]? eye = null)
    {
        var g = new Thrown { Id = ++nextId, Kind = kind, Owner = owner, Weapon = weaponClass, Gun = gun, At = now, Origin = origin, Velocity = velocity, Eye = eye ?? origin };
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
    public IReadOnlyList<GrenadeLosRequest> LosRequests =>
        blasts.Where(b => !b.Applied).SelectMany(b => b.Checks.Where(c => c.Clear is null).Select(c => new GrenadeLosRequest(c.Tag, b.At, c.Point)))
            .Concat(effects.Where(e => e.Fire is not null).SelectMany(e => e.Fire!.Probes.Where(p => p.Clear is null)
                .Select(p => new GrenadeLosRequest(p.LosTag, [p.From[0], p.From[1], p.From[2] + 30], [p.X, p.Y, p.From[2] + 30])))).ToArray();
    public IReadOnlyList<GrenadeFloorRequest> FloorRequests =>
        effects.Where(e => e.Fire is not null).SelectMany(e => e.Fire!.Probes.Where(p => !p.FloorAnswered)
                .Select(p => new GrenadeFloorRequest(p.FloorTag, p.X, p.Y, p.From[2] + GrenadeRules.FireStepUpCm + 30, p.From[2] - GrenadeRules.FireStepDownCm)))
            .Concat(items.Where(i => !i.Settled).Select(i => new GrenadeFloorRequest(i.FloorTag, i.At[0], i.At[1], i.At[2] + GrenadeRules.EyeHeightCm - 40, i.At[2] - 400))).ToArray();
    public void AnswerLos(int tag, bool clear)
    {
        foreach (var b in blasts)
            for (var i = 0; i < b.Checks.Count; i++)
                if (b.Checks[i].Tag == tag && b.Checks[i].Clear is null) b.Checks[i] = b.Checks[i] with { Clear = clear };
        foreach (var e in effects)
            if (e.Fire is { } f)
                foreach (var p in f.Probes)
                    if (p.LosTag == tag && p.Clear is null) p.Clear = clear;
    }
    // The floor's height under a floor request (null: nothing there).
    public void AnswerFloor(int tag, double? z)
    {
        foreach (var e in effects)
            if (e.Fire is { } f)
                foreach (var p in f.Probes)
                    if (p.FloorTag == tag && !p.FloorAnswered) { p.Floor = z; p.FloorAnswered = true; }
        foreach (var i in items)
            if (i.FloorTag == tag && !i.Settled)
            {
                if (z is { } floor) i.At = [i.At[0], i.At[1], floor + GrenadePhysics.Lift];
                i.Settled = true;
                Changed();
            }
    }

    // Round over: nothing carries into the next one.
    public void Clear() { if (thrown.Count + effects.Count + blasts.Count + items.Count == 0) return; thrown.Clear(); effects.Clear(); blasts.Clear(); items.Clear(); Changed(); }

    // ---- dropped grenades -----------------------------------------------------------------------
    // A grenade that falls from a player going down: on the floor under their eye (the game's floor
    // once it answers, else eye height below).
    public long DropItem(string owner, string kind, double[] eye, long now)
    {
        var item = new Item { Id = ++nextId, Kind = kind, Owner = owner, At = [eye[0], eye[1], eye[2] - GrenadeRules.EyeHeightCm + GrenadePhysics.Lift], Since = now, FloorTag = ++nextTag };
        items.Add(item);
        Changed();
        return item.Id;
    }
    public IReadOnlyList<(long Id, string Kind, double[] At)> Items => items.Select(i => (i.Id, i.Kind, i.At)).ToArray();
    public bool TakeItem(long id) { if (items.RemoveAll(i => i.Id == id) == 0) return false; Changed(); return true; }

    public void Tick(long now)
    {
        foreach (var g in thrown.Where(t => !t.Done).ToArray())
        {
            if (!g.Final && now - g.At >= PathWaitMs) Settle(g, GrenadePhysics.Simulate(g.Origin, g.Velocity, Fallback(g.Eye)));
            if (g.GoesOff is { } at && now >= at) GoOff(g, at);
        }
        thrown.RemoveAll(t => t.Done && now - t.At > 30_000);
        foreach (var b in blasts.Where(b => !b.Applied))
            if (now >= b.Deadline || b.Checks.All(c => c.Clear is not null)) Apply(b, now);
        // A dropped grenade waits for its floor, then lies there (level under the eye without an answer).
        foreach (var i in items.Where(i => !i.Settled && now - i.Since >= LosWaitMs)) { i.Settled = true; Changed(); }
        var players = Players().ToList();
        foreach (var e in effects.ToArray())
        {
            if (e.Fire is { } fire) Spread(e, fire, now);
            if (now >= e.Ends) { effects.Remove(e); if (e.Kind == GrenadeRules.Decoy) AddBlast(e.Kind, e.Owner, e.At, e.Ends); Changed(); continue; }
            if (e.State != "fire" || now < e.NextTick) continue;
            for (; e.NextTick <= now; e.NextTick += GrenadeRules.FireTickMs)
                foreach (var p in players)
                    if (p.Alive && p.At is { } eye && InFlames(e, e.NextTick, eye) && GrenadeRules.Find(e.Kind) is { } kind)
                        Damage(e.Owner, p.Id, GrenadeRules.FireDps * GrenadeRules.FireTickMs / 1000.0, kind, now, null);
        }
        if (blasts.RemoveAll(b => b.Applied && now - b.T > 1500) > 0) Changed();
    }

    static bool InFlames(Effect e, long t, TrackSample eye) =>
        e.Fire is { } f ? f.Flames.Any(fl => t >= fl.Starts && t < fl.Starts + GrenadeRules.FireMs && GrenadeRules.InFire(fl.At, GrenadeRules.FlameRadiusCm, eye.X, eye.Y, eye.Z))
            : GrenadeRules.InFire(e.At, GrenadeRules.FlameRadiusCm, eye.X, eye.Y, eye.Z);

    // A fire spreads ring by ring: around each flame of the last ring, six spots a flame apart (in a
    // turned order of its own); each needs a floor within a step up or down of the flame it comes from
    // and a clear line from it (no wall), and no smoke over it; at most 16 flames within range.
    void Spread(Effect e, Fire fire, long now)
    {
        // Answers in (or the wait over: a level floor, a clear line): take the spots that have a floor.
        if (fire.Probes.Count > 0)
        {
            var due = fire.Probes.All(p => p.FloorAnswered && p.Clear is not null) || now - fire.Probes[0].Asked >= LosWaitMs;
            if (!due) return;
            var smokes = effects.Where(x => x.State == "smoke").Select(x => x.At).ToArray();
            foreach (var p in fire.Probes)
            {
                double? floor = p.FloorAnswered ? p.Floor : LevelFloor(p);
                if (floor is not { } z || p.Clear == false || fire.Flames.Count >= GrenadeRules.MaxFlames) continue;
                var at = new[] { p.X, p.Y, z };
                if (z - p.From[2] > GrenadeRules.FireStepUpCm || p.From[2] - z > GrenadeRules.FireStepDownCm || smokes.Any(s => GrenadeRules.Extinguishes(s, at))) continue;
                if (fire.Flames.Any(f => Flat(f.At, at) < GrenadeRules.FlameSpacingCm * 0.7 && Math.Abs(f.At[2] - z) < 120)) continue;
                fire.Flames.Add(new Flame { At = at, Starts = Math.Max(now, e.Starts + p.Ring * GrenadeRules.FlameSpreadMs), Ring = p.Ring });
            }
            fire.Probes.Clear();
            e.Ends = fire.Flames.Max(f => f.Starts) + GrenadeRules.FireMs;
            Changed();
        }
        if (fire.Ring >= GrenadeRules.MaxFireRings || fire.Flames.Count >= GrenadeRules.MaxFlames) return;
        var ring = fire.Ring + 1;
        var from = fire.Flames.Where(f => f.Ring == fire.Ring).ToList();
        if (from.Count == 0) { fire.Ring = GrenadeRules.MaxFireRings; return; }
        var turn = fire.Random.NextDouble() * Math.PI / 3;
        var spots = new List<Probe>();
        foreach (var f in from.OrderBy(_ => fire.Random.Next()))
            for (var k = 0; k < 6; k++)
            {
                var a = turn + k * Math.PI / 3;
                var x = f.At[0] + Math.Cos(a) * GrenadeRules.FlameSpacingCm; var y = f.At[1] + Math.Sin(a) * GrenadeRules.FlameSpacingCm;
                if (Flat(e.At, [x, y]) > GrenadeRules.FireRangeCm) continue;
                if (fire.Flames.Any(o => Flat(o.At, [x, y]) < GrenadeRules.FlameSpacingCm * 0.7) || spots.Any(o => Flat([o.X, o.Y], [x, y]) < GrenadeRules.FlameSpacingCm * 0.7)) continue;
                spots.Add(new Probe { Ring = ring, From = f.At, X = x, Y = y, FloorTag = ++nextTag, LosTag = ++nextTag, Asked = now });
            }
        fire.Ring = ring;
        // Never more spots than flames still allowed (nearest the middle first).
        fire.Probes.AddRange(spots.OrderBy(s => Flat(e.At, [s.X, s.Y])).Take(GrenadeRules.MaxFlames - fire.Flames.Count));
        if (fire.Probes.Count == 0) fire.Ring = GrenadeRules.MaxFireRings;
    }
    double? LevelFloor(Probe p)
    {
        var trace = Fallback([p.X, p.Y, p.From[2] + GrenadeRules.EyeHeightCm]);
        return trace([p.X, p.Y, p.From[2] + GrenadeRules.FireStepUpCm + 30], [p.X, p.Y, p.From[2] - GrenadeRules.FireStepDownCm]) is { } hit ? hit.Point[2] : null;
    }
    static double Flat(double[] a, double[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]));

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
                // The blast: line-of-sight traces to everyone it could reach (the thrower too), answered by
                // the game (or clear after a moment), then the damage or the blindness.
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
                // A smoke puts out the flames it covers (the rest of a fire burns on).
                foreach (var fire in effects.Where(e => e.State == "fire").ToArray())
                {
                    var flames = fire.Fire?.Flames ?? [];
                    var out_ = flames.Where(f => GrenadeRules.Extinguishes(where, f.At)).ToList();
                    if (fire.Fire is null ? !GrenadeRules.Extinguishes(where, fire.At) : out_.Count == 0) continue;
                    var spot = out_.Count > 0 ? new[] { out_.Average(f => f.At[0]), out_.Average(f => f.At[1]), out_.Average(f => f.At[2]) } : fire.At;
                    flames.RemoveAll(out_.Contains);
                    fire.Fire?.Probes.RemoveAll(p => GrenadeRules.Extinguishes(where, [p.X, p.Y, p.From[2]]));
                    if (flames.Count == 0) effects.Remove(fire);
                    else fire.Ends = flames.Max(f => f.Starts) + GrenadeRules.FireMs;
                    AddBlast("extinguished", fire.Owner, spot, at);
                }
                break;
            case GrenadeRules.Decoy:
                effects.Add(new Effect { Id = g.Id, Kind = g.Kind, Owner = g.Owner, State = "decoy", At = where, Starts = at, Ends = at + GrenadeRules.DecoyMs, Weapon = g.Weapon, Gun = g.Gun });
                break;
            default:
            {
                // Fire on the ground, unless it burst in the air or lands in a smoke.
                if (!g.Works) { AddBlast(g.Kind, g.Owner, where, at); break; }
                if (effects.Any(e => e.State == "smoke" && GrenadeRules.Extinguishes(e.At, where))) { AddBlast("extinguished", g.Owner, where, at); break; }
                var floor = new[] { where[0], where[1], where[2] - GrenadePhysics.Lift };
                var fire = new Fire { Random = new Random(unchecked((int)(g.Id * 2654435761L))) };
                fire.Flames.Add(new Flame { At = floor, Starts = at, Ring = 0 });
                effects.Add(new Effect { Id = g.Id, Kind = g.Kind, Owner = g.Owner, State = "fire", At = floor, Starts = at, Ends = at + GrenadeRules.FireMs, NextTick = at, Fire = fire });
                break;
            }
        }
    }

    void Apply(Blast b, long now)
    {
        b.Applied = true;
        Changed();
        var grenade = GrenadeRules.Find(b.Kind)!;
        var smokes = View().Where(v => v.State == "smoke").ToArray();
        // For the log (flashes): what happened to everyone in reach.
        var outcome = b.Kind == GrenadeRules.Flash ? new List<string>() : null;
        foreach (var victim in b.Checks.Select(c => c.Victim).Distinct())
        {
            var checks = b.Checks.Where(c => c.Victim == victim).ToList();
            var answered = checks.Count(c => c.Clear is not null);
            if (!checks.Any(c => c.Clear != false)) { outcome?.Add(victim + " behind a wall"); continue; } // every line to them is blocked
            var p = Players().FirstOrDefault(x => x.Id == victim);
            if (!p.Alive || p.At is not { } eye) { outcome?.Add(victim + " down"); continue; }
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
                if (GrenadeRules.SmokeBlocks(smokes, b.At, at, now)) { outcome?.Add(victim + " behind a smoke"); continue; }
                var dist = Distance(b.At, at);
                var (fx, fy, fz) = TrackGeometry.Direction(eye.Pitch, eye.Yaw);
                var dot = dist < 1 ? 1 : ((b.At[0] - at[0]) * fx + (b.At[1] - at[1]) * fy + (b.At[2] - at[2]) * fz) / dist;
                var amount = GrenadeRules.FlashAmount(dot, dist);
                var los = answered == checks.Count ? "sight from the game" : "no sight answer in " + LosWaitMs + " ms, counted clear";
                if (GrenadeRules.FlashFor(b.T, amount) is { } flash)
                {
                    Flashed(victim, flash);
                    outcome?.Add(FormattableString.Invariant($"{victim} blinded {amount:0.00} ({dist / 100:0} m, facing {dot:0.00}; {flash.HoldMs} ms white + {flash.FadeMs} ms; {los})"));
                }
                else outcome?.Add(FormattableString.Invariant($"{victim} untouched ({dist / 100:0} m, facing {dot:0.00})"));
            }
        }
        if (outcome is not null) Trace?.Invoke("flash #" + b.Id + " by " + b.Owner + " " + (now - b.T) + " ms after the pop: " + (outcome.Count == 0 ? "nobody in reach" : string.Join("; ", outcome)));
    }

    static double Distance(double[] a, double[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));

    public IReadOnlyList<CsGrenadeView> View()
    {
        var list = new List<CsGrenadeView>();
        foreach (var g in thrown.Where(t => !t.Done))
            list.Add(new CsGrenadeView(g.Id, g.Kind, g.Owner, "flying", g.At, g.GoesOff, null, GrenadePhysics.Flat(g.Keys), g.Weapon, Gun: g.Gun));
        foreach (var e in effects)
        {
            if (e.Fire is { } fire)
            {
                var reach = fire.Flames.Max(f => Flat(e.At, f.At)) + GrenadeRules.FlameRadiusCm;
                var flames = fire.Flames.SelectMany(f => new[] { Math.Round(f.At[0], 1), Math.Round(f.At[1], 1), Math.Round(f.At[2], 1), f.Starts - e.Starts }).ToArray();
                list.Add(new CsGrenadeView(e.Id, e.Kind, e.Owner, e.State, e.Starts, e.Ends, R(e.At), null, null, Math.Round(reach, 1), Flames: flames));
            }
            else list.Add(new CsGrenadeView(e.Id, e.Kind, e.Owner, e.State, e.Starts, e.Ends, R(e.At), null, e.Weapon, e.State == "smoke" ? GrenadeRules.SmokeRadiusCm : 0, e.Gun));
        }
        foreach (var b in blasts)
            list.Add(new CsGrenadeView(b.Id, b.Kind, b.Owner, "blast", b.T, null, R(b.At)));
        foreach (var i in items)
            list.Add(new CsGrenadeView(i.Id, i.Kind, i.Owner, "dropped", i.Since, null, R(i.At)));
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

// What grenades mean for the bots, read-only over the CS view (game-modes.md 6.6.5, "For the bot
// logic"): how blind a player is and until when, the smoke clouds over time, the fires' flames, the
// grenade sounds in a time window (a decoy's shots tagged as a decoy: to players they are gunfire),
// and the grenades lying on the floor. All times are host ms.
static class GrenadeIntel
{
    // A smoke cloud: its centre, its horizontal radius and half height at full size, and its size now (0..1).
    public sealed record SmokeCloud(long Id, string Owner, double[] Centre, double Radius, double HalfHeight, double Scale, long Starts, long Ends);
    // A fire: the flames burning now (x, y, z of the floor; each FlameRadius round), when it caught and
    // when its last flame dies.
    public sealed record FireArea(long Id, string Owner, string Kind, IReadOnlyList<double[]> Flames, double FlameRadius, long Starts, long Ends);
    // A grenade sound: "gunfire" (Decoy: true, a decoy mimicking Weapon), "he", "flash", "bounce",
    // "smoke", "fire", "decoy-pop", "extinguished"; where and when (host ms), and whose grenade.
    public sealed record Sound(long T, string Kind, double[] At, string Owner, bool Decoy = false, string? Weapon = null, long Grenade = 0);

    // How white a player's screen is at `t` (0..1).
    public static double Blind(CsView cs, string member, long t) => GrenadeRules.FlashAlpha(cs.Players.FirstOrDefault(p => p.Member == member)?.Flash, t);
    // Until when a player stays at least `threshold` white (null: not blinded that much). Bots in the
    // service see nothing from 0.6 (MultiplayerService.GrenadeSight).
    public static long? BlindUntil(CsView cs, string member, double threshold = 0.6)
    {
        if (cs.Players.FirstOrDefault(p => p.Member == member)?.Flash is not { } f) return null;
        var peak = GrenadeRules.FlashPeak(f);
        if (peak < threshold) return null;
        // peak * (1 - u)^2 = threshold during the fade.
        var u = 1 - Math.Sqrt(threshold / peak);
        return f.At + f.HoldMs + (long)Math.Round(u * f.FadeMs);
    }
    public static IReadOnlyList<SmokeCloud> Smokes(CsView cs, long t) =>
        (cs.Grenades ?? []).Where(g => g.State == "smoke" && g.Pos is { Length: 3 } && g.Ends is { } e && t < e)
            .Select(g => new SmokeCloud(g.Id, g.Owner, GrenadeRules.SmokeCentre(g.Pos!), GrenadeRules.SmokeRadiusCm, GrenadeRules.SmokeHalfHeightCm, GrenadeRules.SmokeScale(g, t), g.At, g.Ends!.Value)).ToArray();
    public static bool SmokeBlocks(CsView cs, double[] a, double[] b, long t) => GrenadeRules.SmokeBlocks(cs.Grenades, a, b, t);
    public static IReadOnlyList<FireArea> Fires(CsView cs, long t) =>
        (cs.Grenades ?? []).Where(g => g.State == "fire" && (g.Ends is not { } e || t < e))
            .Select(g => new FireArea(g.Id, g.Owner, g.Kind, GrenadeRules.Burning(g, t).ToArray(), GrenadeRules.FlameRadiusCm, g.At, g.Ends ?? g.At + GrenadeRules.FireMs)).ToArray();
    // Whether feet at `feet` stand in a burning flame at `t`.
    public static bool InFire(CsView cs, double[] feet, long t) =>
        Fires(cs, t).Any(f => f.Flames.Any(fl => GrenadeRules.InFire(fl, f.FlameRadius, feet[0], feet[1], feet[2] + GrenadeRules.EyeHeightCm)));
    public static IReadOnlyList<CsGrenadeView> Dropped(CsView cs) => (cs.Grenades ?? []).Where(g => g.State == "dropped").ToArray();

    // The sounds grenades make in [from, to): the decoys' shots (as players hear them: gunfire), bounces,
    // blasts, smokes and fires catching.
    public static IReadOnlyList<Sound> Sounds(CsView cs, long from, long to)
    {
        var list = new List<Sound>();
        foreach (var g in cs.Grenades ?? [])
        {
            switch (g.State)
            {
                case "decoy" when g.Pos is { Length: 3 } at:
                    foreach (var s in GrenadeRules.DecoyShots(g.Id, g.Weapon, (g.Ends ?? g.At + GrenadeRules.DecoyMs) - g.At, g.Gun))
                        if (g.At + s >= from && g.At + s < to) list.Add(new Sound(g.At + s, "gunfire", at, g.Owner, true, g.Gun ?? g.Weapon, g.Id));
                    break;
                case "flying":
                    foreach (var k in GrenadePhysics.Unflat(g.Keys))
                        if (k.Impact != GrenadePhysics.NoImpact && g.At + (long)k.T >= from && g.At + (long)k.T < to && (g.Ends is not { } ends || g.At + (long)k.T <= ends))
                            list.Add(new Sound(g.At + (long)k.T, "bounce", [k.X, k.Y, k.Z], g.Owner, Grenade: g.Id));
                    break;
                case "blast" when g.Pos is { Length: 3 } at && g.At >= from && g.At < to:
                    list.Add(new Sound(g.At, g.Kind switch { GrenadeRules.He => "he", GrenadeRules.Flash => "flash", GrenadeRules.Decoy => "decoy-pop", "extinguished" => "extinguished", _ => "fire" }, at, g.Owner, Grenade: g.Id));
                    break;
                case "smoke" or "fire" when g.Pos is { Length: 3 } at && g.At >= from && g.At < to:
                    list.Add(new Sound(g.At, g.State, at, g.Owner, Grenade: g.Id));
                    break;
            }
        }
        return list.OrderBy(s => s.T).ToArray();
    }
}

// The grenade files between the host's service and its game (AimModSteam, GrenadePhysics.hpp):
//   grenade-sim.tsv (service -> AimModSteam), while something waits for an answer:
//     AIMMOD_GRENADESIM_1\t<seq>
//     throw\t<id>\t<kind>\t<x>\t<y>\t<z>\t<vx>\t<vy>\t<vz>      fly it with line traces
//     los\t<tag>\t<ax>\t<ay>\t<az>\t<bx>\t<by>\t<bz>          is the line clear?
//     floor\t<tag>\t<x>\t<y>\t<top z>\t<bottom z>             the floor's height under x, y
//   grenade-paths.tsv (AimModSteam -> service):
//     AIMMOD_GRENADEPATHS_1\t<unix ms>
//     path\t<id>\t<keys>\t<t x y z vx vy vz motion impact> x keys
//     los\t<tag>\t<0|1>
//     floor\t<tag>\t<z|->                                     (-: no floor in between)
static class GrenadeFiles
{
    static string F(double v) => Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);
    public static string Sim(long sequence, IEnumerable<GrenadePathRequest> paths, IEnumerable<GrenadeLosRequest> los, IEnumerable<GrenadeFloorRequest>? floors = null)
    {
        var sb = new StringBuilder("AIMMOD_GRENADESIM_1\t").Append(sequence).Append('\n');
        foreach (var p in paths.Take(32))
            sb.Append("throw\t").Append(p.Id).Append('\t').Append(p.Kind).Append('\t').Append(string.Join('\t', p.Origin.Concat(p.Velocity).Select(F))).Append('\n');
        foreach (var l in los.Take(128))
            sb.Append("los\t").Append(l.Tag).Append('\t').Append(string.Join('\t', l.From.Concat(l.To).Select(F))).Append('\n');
        foreach (var f in (floors ?? []).Take(64))
            sb.Append("floor\t").Append(f.Tag).Append('\t').Append(F(f.X)).Append('\t').Append(F(f.Y)).Append('\t').Append(F(f.Top)).Append('\t').Append(F(f.Bottom)).Append('\n');
        return sb.ToString();
    }

    public static (Dictionary<long, IReadOnlyList<GrenadePhysics.Key>> Paths, Dictionary<int, bool> Los, Dictionary<int, double?> Floors)? Paths(string text, long now)
    {
        var lines = text.Replace("\r", "").Split('\n');
        if (lines.Length == 0 || !lines[0].StartsWith("AIMMOD_GRENADEPATHS_1\t", StringComparison.Ordinal)) return null;
        if (!long.TryParse(lines[0][22..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var at) || Math.Abs(now - at) > 10_000) return null;
        var paths = new Dictionary<long, IReadOnlyList<GrenadePhysics.Key>>(); var los = new Dictionary<int, bool>(); var floors = new Dictionary<int, double?>();
        static double? Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) && Math.Abs(v) < 1e7 ? v : null;
        foreach (var line in lines.Skip(1).Take(512))
        {
            var p = line.Split('\t');
            if (p.Length == 3 && p[0] == "los" && int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var tag) && p[2] is "0" or "1") los[tag] = p[2] == "1";
            else if (p.Length == 3 && p[0] == "floor" && int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ftag) && (p[2] == "-" || Num(p[2]) is not null)) floors[ftag] = p[2] == "-" ? null : Num(p[2]);
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
        return (paths, los, floors);
    }
}

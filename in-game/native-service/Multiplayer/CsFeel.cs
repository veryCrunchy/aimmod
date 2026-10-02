using System.Text;

namespace AimMod.InGame.Multiplayer;

// CS weapon feel (in-game/docs/game-modes.md 6.6.6): the host's copy of AimModCore's CsFeel
// (native-mod/core/include/aimmod/CsFeel.hpp). The numbers and the spread formula must stay the
// same on both sides: AimModCore turns every bullet by an offset drawn from the shot's seed (the
// match salt and the shot number), and the host rebuilds that ray from the claim to validate the
// hit on it. Inaccuracy in radians (CS:GO's numbers / 1000), speeds in CS units (u/s).
sealed record CsFeelSpec(string Id, string Profile, string Class, double MaxSpeed, double ScopedSpeed, double Spread, double Stand, double Crouch, double Move, double Jump,
    double Land, double Fire, double RecoverStand, double RecoverCrouch, double ScopedStand, double ScopedCrouch, int ZoomLevels)
{
    public bool Spreads => Stand > 0 || Spread > 0;
}

static class CsFeel
{
    // The CS map ports run 250 u/s at 1100 cm/s.
    public const double UnitCm = 4.4, KnifeSpeed = 250, AccurateShare = 0.34, FullMoveShare = 0.95;
    // A claim's inaccuracy is accepted down to this share of the least cone the shooter's own track
    // allows (the track is the camera at 60 Hz, a little behind the movement the client measured),
    // less a small allowance; and never beyond MaxInaccuracy.
    public const double HostShare = 0.5, HostSlack = 0.001, MaxInaccuracy = 0.6;
    // The seed's shot may be a few shots before the claimed one (shots counted together in one frame
    // share the armed offset).
    public const long MaxSeedLag = 8;
    // Airborne by the track: rising or falling faster than this (cm/s).
    public const double AirSpeedCm = 400;

    public static readonly CsFeelSpec[] All =
    [
        new("glock", "AimMod CS Glock-18", "pistol", 240, 0, 0.0020, 0.0070, 0.0045, 0.014, 0.16, 0.04, 0.045, 0.33, 0.25, 0, 0, 0),
        new("usp", "AimMod CS USP-S", "pistol", 240, 0, 0.0015, 0.0050, 0.0035, 0.012, 0.15, 0.04, 0.050, 0.35, 0.27, 0, 0, 0),
        new("deagle", "AimMod CS Desert Eagle", "pistol", 230, 0, 0.0020, 0.0090, 0.0060, 0.060, 0.26, 0.06, 0.060, 0.80, 0.60, 0, 0, 0),
        new("mac10", "AimMod CS MAC-10", "smg", 240, 0, 0.0030, 0.0150, 0.0100, 0.030, 0.16, 0.04, 0.008, 0.35, 0.25, 0, 0, 0),
        new("mp9", "AimMod CS MP9", "smg", 240, 0, 0.0025, 0.0130, 0.0090, 0.026, 0.16, 0.04, 0.007, 0.32, 0.24, 0, 0, 0),
        new("ak47", "AimMod CS AK-47", "rifle", 215, 0, 0.0006, 0.0064, 0.0048, 0.146, 0.30, 0.06, 0.0078, 0.37, 0.26, 0, 0, 0),
        new("m4a1s", "AimMod CS M4A1-S", "rifle", 225, 0, 0.0005, 0.0050, 0.0037, 0.120, 0.30, 0.06, 0.0070, 0.35, 0.25, 0, 0, 0),
        new("awp", "AimMod CS AWP", "sniper", 200, 100, 0.0002, 0.0800, 0.0600, 0.180, 0.45, 0.08, 0.110, 0.25, 0.20, 0.0020, 0.0015, 2),
        new("knife", "AimMod CS Knife", "knife", 250, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0),
        new("grenade", "AimMod CS Grenade", "grenade", 245, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0),
        new("c4", "AimMod CS C4", "bomb", 250, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0),
    ];
    public static CsFeelSpec? ById(string? id) => All.FirstOrDefault(w => w.Id == id);
    public static CsFeelSpec? ByProfile(string? name) => All.FirstOrDefault(w => w.Profile == name);

    public static double MoveShare(double speed, double maxSpeed) =>
        maxSpeed > 0 && double.IsFinite(speed) ? Math.Clamp((speed / maxSpeed - AccurateShare) / (FullMoveShare - AccurateShare), 0, 1) : 0;

    // The cone while moving like that (no penalty, no static spread); scopeBlend 0 unscoped .. 1 scoped.
    public static double ConeNow(CsFeelSpec w, double speed, bool air, bool crouch, double scopeBlend)
    {
        var blend = w.ZoomLevels > 0 ? Math.Clamp(scopeBlend, 0, 1) : 0;
        double unscoped = crouch ? w.Crouch : w.Stand, scoped = crouch ? w.ScopedCrouch : w.ScopedStand;
        var maxNow = blend >= 0.5 && w.ScopedSpeed > 0 ? w.ScopedSpeed : w.MaxSpeed;
        return unscoped + (scoped - unscoped) * blend + w.Move * MoveShare(speed, maxNow) + (air ? w.Jump : 0);
    }

    // The least cone a shot can honestly have moving at `speed`: the best stance, the slowest max speed, no penalty.
    public static double MinimumCone(CsFeelSpec w, double speed, bool air)
    {
        var b = Math.Min(w.Stand, w.Crouch);
        if (w.ZoomLevels > 0) b = Math.Min(b, Math.Min(w.ScopedStand, w.ScopedCrouch));
        return b + w.Move * MoveShare(speed, w.MaxSpeed) + (air ? w.Jump : 0);
    }

    // Max speed with it in hand as a share of the knife's (scoped: the scoped speed). Null: the knife's.
    public static double SpeedShare(CsFeelSpec? w, bool scoped = false) => w is null ? 1 : (scoped && w.ScopedSpeed > 0 ? w.ScopedSpeed : w.MaxSpeed) / KnifeSpeed;

    // FNV-1a 64 of the match id.
    public static ulong Salt(string matchId)
    {
        var h = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(matchId)) { h ^= b; h *= 1099511628211UL; }
        return h;
    }

    static ulong SplitMix(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        var z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public static double[] Randoms(ulong salt, ulong shot)
    {
        var state = salt ^ unchecked(shot * 0xD1B54A32D192ED03UL);
        var u = new double[4];
        for (var i = 0; i < 4; i++) u[i] = (SplitMix(ref state) >> 11) * (1.0 / 9007199254740992.0);
        return u;
    }

    // The bullet's offset in the camera's right and up directions (tangent units).
    public static (double Right, double Up) Offset(ulong salt, ulong shot, double inaccuracy, double spread)
    {
        var u = Randoms(salt, shot);
        double r1 = Math.Max(0, inaccuracy) * u[0], a1 = 2 * Math.PI * u[1], r2 = Math.Max(0, spread) * u[2], a2 = 2 * Math.PI * u[3];
        return (Math.Cos(a1) * r1 + Math.Cos(a2) * r2, Math.Sin(a1) * r1 + Math.Sin(a2) * r2);
    }

    // The bullet's unit direction: forward + right * offset.Right + up * offset.Up (UE axes, degrees).
    public static (double X, double Y, double Z) Direction(double pitch, double yaw, (double Right, double Up) o)
    {
        double p = pitch * Math.PI / 180, y = yaw * Math.PI / 180, cp = Math.Cos(p), sp = Math.Sin(p), cy = Math.Cos(y), sy = Math.Sin(y);
        double x = cp * cy - o.Right * sy - o.Up * sp * cy, yy = cp * sy + o.Right * cy - o.Up * sp * sy, z = sp + o.Up * cp;
        var len = Math.Sqrt(x * x + yy * yy + z * z);
        return (x / len, yy / len, z / len);
    }

    // The crosshair gap the HUD draws (a share of half the screen's width).
    public static double CrosshairGap(double cone, double fovDegrees) => fovDegrees is > 1 and < 179 ? Math.Tan(Math.Clamp(cone, 0, 1.2)) / Math.Tan(fovDegrees * Math.PI / 360) : 0;

    // The round-state line AimModCore reads: the salt, the dynamic crosshair, the zoom and the zoomed sensitivity.
    public static string Line(string matchId, bool crosshair, string zoom, double adsSensitivity) =>
        "feel\t" + Salt(matchId).ToString(System.Globalization.CultureInfo.InvariantCulture) + "\t" + (crosshair ? 1 : 0) + "\t" + zoom + "\t" + adsSensitivity.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

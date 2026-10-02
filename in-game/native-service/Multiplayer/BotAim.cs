namespace AimMod.InGame.Multiplayer;

// Bot aim (BotBrain): where a bot's crosshair points and how it gets onto a target, like a person:
// turning no faster than its skill allows, a moment to settle on target before it fires, and an
// error that grows with distance, the target's speed and the spray. A shot lands by chance from
// the base hit rate (BotBrain.HitChance) times how close the crosshair is to the target's body.
sealed class BotAimState
{
    public double Yaw, Pitch;          // where it looks now (degrees)
    public bool Known;                 // set from its body's yaw at least once
    public long SettledSince = long.MinValue / 2; // on target (within tolerance) since then
    public long LastStep;
}

static class BotAim
{
    // Degrees per second; the walker turns the body at the same rate ("turn" order).
    public static double TurnRate(BotSkill s) => s.Id switch { BotSkills.Easy => 260, BotSkills.Hard => 720, _ => 450 };
    // How long the crosshair stays on target before the first shot (ms), on top of the reaction time.
    public static long SettleMs(BotSkill s) => s.Id switch { BotSkills.Easy => 220, BotSkills.Hard => 60, _ => 130 };
    // How hard it strafes in a fight (0..1, the walker's "fight").
    public static double Strafe(BotSkill s) => s.Id switch { BotSkills.Easy => 0.25, BotSkills.Hard => 1.0, _ => 0.6 };

    public static double Wrap(double a)
    {
        a %= 360;
        if (a > 180) a -= 360;
        if (a < -180) a += 360;
        return a;
    }

    // The angles from an eye to a point.
    public static (double Yaw, double Pitch) Angles(double[] eye, double[] at)
    {
        var dx = at[0] - eye[0]; var dy = at[1] - eye[1]; var dz = at[2] - eye[2];
        return (Math.Atan2(dy, dx) * 180 / Math.PI, Math.Atan2(dz, Math.Sqrt(dx * dx + dy * dy)) * 180 / Math.PI);
    }

    // Turns toward (yaw, pitch) for dt seconds at most `rate` degrees per second; returns the angle left.
    public static double Turn(BotAimState s, double yaw, double pitch, double rate, double dt)
    {
        var step = rate * Math.Clamp(dt, 0, 0.25);
        var dYaw = Wrap(yaw - s.Yaw); var dPitch = pitch - s.Pitch;
        var left = Math.Sqrt(dYaw * dYaw + dPitch * dPitch);
        if (left <= step || left < 1e-9) { s.Yaw = yaw; s.Pitch = pitch; return 0; }
        var k = step / left;
        s.Yaw = Wrap(s.Yaw + dYaw * k); s.Pitch += dPitch * k;
        return left - step;
    }

    // The target's half-width as an angle from this distance (a body about 45 cm wide).
    public static double TargetAngle(double distance) => Math.Atan2(45, Math.Max(1, distance)) * 180 / Math.PI;

    // How much of a shot's chance survives the crosshair being `error` degrees off: all of it on the
    // body, falling to nothing by three body-widths off.
    public static double OnTarget(double error, double distance)
    {
        var body = TargetAngle(distance);
        if (error <= body) return 1;
        return Math.Clamp(1 - (error - body) / (body * 2), 0, 1);
    }

    // Fires only once on target (within a body-width and a half) and settled there.
    public static bool Ready(BotAimState s, double error, double distance, long now, BotSkill skill)
    {
        if (error > TargetAngle(distance) * 1.5) { s.SettledSince = long.MinValue / 2; return false; }
        if (s.SettledSince == long.MinValue / 2) s.SettledSince = now;
        return now - s.SettledSince >= SettleMs(skill);
    }
}

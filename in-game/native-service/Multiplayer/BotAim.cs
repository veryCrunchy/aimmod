namespace AimMod.InGame.Multiplayer;

// Bot aim (BotBrain): where a bot's crosshair points and how it gets onto a target, like a person:
// an eased turn that speeds up and slows down (never faster than its skill's turn rate, never
// changing speed faster than its angular acceleration), a little overshoot that settles, a moment
// on target before it fires, and an error that grows with distance, the target's speed and the
// spray. A shot lands by chance from the base hit rate (BotBrain.HitChance) times how close the
// crosshair is to the target's body. AimModSteam turns the bot's body with the same numbers
// ("turn" and "aim" orders), so what the host sees (the yaw a flash is judged by) is the same turn.
sealed class BotAimState
{
    public double Yaw, Pitch;          // where it looks now (degrees)
    public double YawVel, PitchVel;    // how fast it turns now (degrees per second)
    public bool Known;                 // set from its body's yaw at least once
    public long SettledSince = long.MinValue / 2; // on target (within tolerance) since then
    public long LastStep;
}

// How a skill turns: top speed (degrees per second), angular acceleration (degrees per second
// squared) and overshoot (the share of extra speed it carries into the target, then settles back).
sealed record BotTurnProfile(double Rate, double Accel, double Overshoot);

static class BotAim
{
    // Degrees per second; the walker turns the body at the same rate ("turn" order).
    public static double TurnRate(BotSkill s) => s.Id switch { BotSkills.Easy => 260, BotSkills.Hard => 720, _ => 450 };
    public static BotTurnProfile Profile(BotSkill s) => s.Id switch
    {
        BotSkills.Easy => new(TurnRate(s), 1100, 0.18),
        BotSkills.Hard => new(TurnRate(s), 5200, 0.05),
        _ => new(TurnRate(s), 2600, 0.1),
    };
    // How long the crosshair stays on target before the first shot (ms), on top of the reaction time.
    public static long SettleMs(BotSkill s) => s.Id switch { BotSkills.Easy => 220, BotSkills.Hard => 60, _ => 130 };
    // How hard it strafes in a fight (0..1, the walker's "fight").
    public static double Strafe(BotSkill s) => s.Id switch { BotSkills.Easy => 0.25, BotSkills.Hard => 1.0, _ => 0.6 };
    // How it strafes: harder bots counter-strafe to a stop before they shoot; easy bots spam A-D and shoot moving.
    public static string FightStyle(BotSkill s) => s.Id == BotSkills.Easy ? "ad" : "counter";

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

    // One axis of an eased turn over dt: the speed it wants is the most it can still brake from
    // (sqrt(2 a |error|)), a little more by the overshoot, at most the rate; the speed moves towards
    // that by at most accel * dt. Small substeps, so a long service tick turns the same as many short ones.
    public static void Ease(ref double angle, ref double vel, double target, double dt, BotTurnProfile p, bool wrap)
    {
        dt = Math.Clamp(dt, 0, 0.25);
        if (dt <= 0 || !double.IsFinite(target)) return;
        var steps = Math.Max(1, (int)Math.Ceiling(dt / (1 / 120.0)));
        var h = dt / steps;
        for (var i = 0; i < steps; i++)
        {
            var err = wrap ? Wrap(target - angle) : target - angle;
            // (Never more than the error left in one substep: no dither around the target.)
            var want = Math.Sign(err) * Math.Min(Math.Min(p.Rate, Math.Sqrt(2 * p.Accel * Math.Abs(err)) * (1 + p.Overshoot)), Math.Abs(err) / h);
            // Settled: on target and slow, it stops (no endless dither).
            if (Math.Abs(err) < 0.05 && Math.Abs(vel) <= p.Accel * h) { vel = 0; angle = wrap ? Wrap(target) : target; continue; }
            vel += Math.Clamp(want - vel, -p.Accel * h, p.Accel * h);
            vel = Math.Clamp(vel, -p.Rate, p.Rate);
            angle += vel * h;
            if (wrap) angle = Wrap(angle);
        }
    }

    // Turns toward (yaw, pitch) for dt seconds; returns the angle left.
    public static double Turn(BotAimState s, double yaw, double pitch, BotTurnProfile p, double dt)
    {
        Ease(ref s.Yaw, ref s.YawVel, yaw, dt, p, true);
        Ease(ref s.Pitch, ref s.PitchVel, pitch, dt, p, false);
        var dYaw = Wrap(yaw - s.Yaw); var dPitch = pitch - s.Pitch;
        return Math.Sqrt(dYaw * dYaw + dPitch * dPitch);
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

    // Moving while shooting (CS's moving inaccuracy): the share of a shot's chance left at the bot's
    // own speed (cm/s). Still or crouched: all of it; a run: a fifth.
    public const double StillSpeed = 130, RunningSpeed = 700;
    public static double Moving(double? speed)
    {
        if (speed is not { } v || v <= StillSpeed) return 1;
        return Math.Clamp(1 - 0.8 * (v - StillSpeed) / (RunningSpeed - StillSpeed), 0.2, 1);
    }
    // A CS gun (CsFeel, the same cones the players' bullets get): the share of its shots that land on a
    // body `distance` away, from the cone its own movement and stance give it (an AWP standing still
    // is scoped). Other weapons, or no spread: Moving(speed).
    public static double MoveAccuracy(CsFeelSpec? weapon, double? speed, bool crouch, double distance)
    {
        if (weapon is not { Spreads: true } w) return Moving(speed);
        var v = (speed ?? 0) / CsFeel.UnitCm;
        var cone = CsFeel.ConeNow(w, v, false, crouch, w.ZoomLevels > 0 && v < 20 ? 1 : 0) + w.Spread;
        var body = Math.Atan2(45, Math.Max(1, distance));
        return cone <= 0 ? 1 : Math.Clamp(body / cone, 0.05, 1);
    }
    // Whether a bot waits for its counter-strafe to stop it before the next shot: harder bots do;
    // an easy bot fires on the move. Unknown speed (an older AimModSteam): never waits.
    public static bool WaitsToStop(BotSkill s, double? speed) => s.Id != BotSkills.Easy && speed is { } v && v > StillSpeed * 1.6;
}

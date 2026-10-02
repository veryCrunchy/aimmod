namespace AimMod.InGame.Multiplayer;

// Bot tactics (BotBrain): the situational calls a CS player makes, pure so each is checked alone.
//  - Smokes: wait one out holding its edge, go another way, flash and push through it, or swing
//    wide as it thins; push through when the clock says so; never stand in one for nothing.
//  - Flashes: blinded by any flash (the host's per-player amount, theirs and the team's too), a
//    bot aims badly, sprays where the enemy was or backs off, and turns away; harder bots look
//    away from their own and their teammates' flashes as they pop (bots call their flashes) and
//    from an enemy flash they see coming; easy bots don't, and blind themselves now and then.
//  - Movement: shift-walk near the enemy (silent), crouch to hold a long angle, jiggle-peek an
//    angle a sound came from, swing wide as a smoke fades; harder bots counter-strafe before they
//    shoot and pre-aim the corners they clear.
//  - Fire on the floor is walked around by everyone; a smoke only when the bot decides to.
enum SmokeChoice { None, Leave, HoldEdge, Reroute, FlashPush, WideSwing, Push }

// What a bot faces with a smoke: inside it, or the cloud between it and where it goes (or what it
// watches); how long the smoke has left; whether it has a flash; whether the clock forces it; its
// side and whether it holds (defends) or takes ground.
sealed record SmokeSituation(bool Inside, bool Blocking, long LeftMs, bool HasFlash, bool Pressure, bool Defending, bool Mates);

static class BotTactics
{
    // ---- smokes ------------------------------------------------------------------------------------
    public const long FadeSwingMs = 4000, LongSmokeMs = 8000;
    public const double SmokeNearCm = 2600;

    // The call for one smoke. `roll` (0..1) is the bot's own coin for this smoke, so the same bot
    // sticks with its call.
    public static SmokeChoice Decide(SmokeSituation s, string skill, double roll)
    {
        if (s.Inside)
            // Never in a smoke for nothing: on through it when pressed or when its way lies beyond, else out of it.
            return s.Pressure || (!s.Defending && s.Blocking) ? SmokeChoice.Push : SmokeChoice.Leave;
        if (!s.Blocking) return SmokeChoice.None;
        if (s.Pressure) return s.HasFlash && skill != BotSkills.Easy ? SmokeChoice.FlashPush : SmokeChoice.Push;
        switch (skill)
        {
            case BotSkills.Easy:
                // Easy bots: walk into it or wait about, nothing clever.
                return roll < 0.5 ? SmokeChoice.Push : SmokeChoice.HoldEdge;
            case BotSkills.Hard:
                if (s.Defending) return s.LeftMs <= FadeSwingMs ? SmokeChoice.WideSwing : SmokeChoice.HoldEdge;
                if (s.LeftMs <= FadeSwingMs + 1500) return SmokeChoice.WideSwing;
                if (s.HasFlash && s.Mates) return SmokeChoice.FlashPush;
                return roll < 0.6 ? SmokeChoice.Reroute : SmokeChoice.HoldEdge;
            default:
                if (s.LeftMs <= FadeSwingMs) return roll < 0.5 ? SmokeChoice.WideSwing : SmokeChoice.HoldEdge;
                if (s.Defending) return SmokeChoice.HoldEdge;
                return s.LeftMs > LongSmokeMs && roll < 0.5 ? SmokeChoice.Reroute : SmokeChoice.HoldEdge;
        }
    }

    // The smoke in the way: a cloud between the eye and a point it heads for (or watches), close
    // enough to matter. Straight lines, the way a player judges it.
    public static CsGrenadeView? InTheWay(IEnumerable<CsGrenadeView>? grenades, double[] eye, double[]? to, long now)
    {
        if (to is null) return null;
        CsGrenadeView? best = null; var bestD = double.MaxValue;
        foreach (var s in grenades ?? [])
        {
            if (s.State != "smoke" || s.Pos is not { Length: 3 } at || BotVision.Growth(s, now) < 0.5) continue;
            var d = Dist2(eye, at);
            if (d > SmokeNearCm || d >= bestD) continue;
            var target = new[] { to[0], to[1], eye[2] };
            if (BotVision.Smoke(s, eye, target, now) >= BotVision.BlockChord * 0.8) { best = s; bestD = d; }
        }
        return best;
    }

    // Holding a smoke's edge: a spot back from the cloud on the bot's side (side by side with its
    // mates: `slot` spreads them), and the edge where someone would come out.
    public static (double[] Spot, double[] Edge) EdgeHold(CsGrenadeView smoke, double[] eye, int slot)
    {
        var c = smoke.Pos!;
        var dx = eye[0] - c[0]; var dy = eye[1] - c[1]; var d = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
        var ux = dx / d; var uy = dy / d;
        var back = GrenadeRules.SmokeRadiusCm + 650;
        var side = (slot % 3 - 1) * 260.0;
        var spot = new[] { c[0] + ux * Math.Max(back, Math.Min(d, back + 400)) - uy * side, c[1] + uy * Math.Max(back, Math.Min(d, back + 400)) + ux * side, eye[2] };
        var lateral = (slot % 2 == 0 ? 1 : -1) * GrenadeRules.SmokeRadiusCm * 0.7;
        var edge = new[] { c[0] + ux * GrenadeRules.SmokeRadiusCm * 0.8 - uy * lateral, c[1] + uy * GrenadeRules.SmokeRadiusCm * 0.8 + ux * lateral, eye[2] };
        return (spot, edge);
    }
    // Out of a smoke: the nearest way out, on the bot's own side of the cloud.
    public static double[] WayOut(CsGrenadeView smoke, double[] eye)
    {
        var c = smoke.Pos!;
        var dx = eye[0] - c[0]; var dy = eye[1] - c[1]; var d = Math.Sqrt(dx * dx + dy * dy);
        if (d < 1) { dx = 1; dy = 0; d = 1; }
        var r = GrenadeRules.SmokeRadiusCm * 1.3;
        return [c[0] + dx / d * r, c[1] + dy / d * r, eye[2]];
    }
    // A flash for a push through a smoke: over the cloud, popping on its far side at head height.
    public static double[] FlashOver(CsGrenadeView smoke, double[] eye)
    {
        var c = smoke.Pos!;
        var dx = c[0] - eye[0]; var dy = c[1] - eye[1]; var d = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
        var beyond = GrenadeRules.SmokeRadiusCm + 350;
        return [c[0] + dx / d * beyond, c[1] + dy / d * beyond, c[2] + GrenadeRules.SmokeHalfHeightCm * 1.4];
    }

    // ---- flashes -----------------------------------------------------------------------------------
    // How blind a bot is now (the host's flash on it): 0 clear .. 1 white.
    public static double Blindness(CsFlashView? flash, long now) => GrenadeRules.FlashAlpha(flash, now);
    public const double BlindOut = 0.6, BlindHalf = 0.25, HalfBlindSightCm = 1200;
    // While blind it still sprays at a target its game traces as in the open, at this share of its chance.
    public static double SprayShare(string skill) => skill switch { BotSkills.Easy => 0, BotSkills.Hard => 0.18, _ => 0.1 };
    // Half blind: a slower reaction and worse aim.
    public static double HalfBlindAim(double blind) => blind >= BlindHalf ? Math.Clamp(1 - blind, 0.3, 1) : 1;

    // A flash in flight a bot knows about: when and where it pops, whose it is.
    public sealed record Threat(long Id, double[] Pop, long PopAt, string Owner, string Why);
    // The flashes in flight a bot knows will pop near it: its own (harder bots, and Normal), its
    // teammates' when called (bots call theirs; Easy bots neither call nor listen), and an enemy's it
    // sees coming (Hard, mostly; Normal sometimes).
    public static IReadOnlyList<Threat> FlashThreats(IEnumerable<CsGrenadeView>? grenades, string member, string skill, Func<string, bool> mate, Func<string, string?> skillOf,
        double yaw, double[] eye, long now, Func<long, double> enemyRoll, IEnumerable<CsGrenadeView>? smokes)
    {
        var list = new List<Threat>();
        if (skill == BotSkills.Easy) return list;
        foreach (var g in grenades ?? [])
        {
            if (g.State != "flying" || g.Kind != GrenadeRules.Flash || g.Ends is not { } pop || pop < now - 100) continue;
            var keys = GrenadePhysics.Unflat(g.Keys);
            if (keys.Count == 0) continue;
            var at = GrenadePhysics.At(keys, pop - g.At);
            if (Dist3(at, eye) > GrenadeRules.FlashFarCm * 0.8) continue;
            string? why = null;
            if (g.Owner == member) why = "own flash";
            else if (mate(g.Owner) && skillOf(g.Owner) is BotSkills.Normal or BotSkills.Hard) why = "called by " + g.Owner;
            else if (mate(g.Owner) && skill == BotSkills.Hard && Dist3([keys[0].X, keys[0].Y, keys[0].Z], eye) < 1500) why = "teammate's flash";
            else if (!mate(g.Owner) && enemyRoll(g.Id) < (skill == BotSkills.Hard ? 0.7 : 0.3))
            {
                // Seen coming: where it is now is in view and in the open.
                var p = GrenadePhysics.At(keys, now - g.At);
                if (BotVision.InView(yaw, eye, p, 55, smokes, now, 2600)) why = "saw it coming";
            }
            if (why is not null) list.Add(new Threat(g.Id, at, pop, g.Owner, why));
        }
        return list;
    }
    // When to start looking away before a pop (ms), and how long after it to keep looking away.
    public static long LookAwayLead(string skill) => skill == BotSkills.Hard ? 520 : 380;
    public const long LookAwayAfterMs = 350;
    // The point to look at instead: straight away from the pop, at eye height.
    public static double[] AwayFrom(double[] pop, double[] eye)
    {
        var dx = eye[0] - pop[0]; var dy = eye[1] - pop[1]; var d = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
        return [eye[0] + dx / d * 600, eye[1] + dy / d * 600, eye[2]];
    }
    // Facing a pop this much or more (the dot of the view and the way to it) would hurt.
    public static double Facing(double yaw, double[] eye, double[] pop)
    {
        var dx = pop[0] - eye[0]; var dy = pop[1] - eye[1]; var d = Math.Sqrt(dx * dx + dy * dy);
        if (d < 1) return 1;
        var r = yaw * Math.PI / 180;
        return (dx * Math.Cos(r) + dy * Math.Sin(r)) / d;
    }
    // An easy bot's throw: off by a good share of the way (the reason it flashes itself and its mates).
    public static double[] Sloppy(double[] eye, double[] target, Random random, double share = 0.45)
    {
        var dx = target[0] - eye[0]; var dy = target[1] - eye[1];
        var k = 1 - share * random.NextDouble();
        return [eye[0] + dx * k + (random.NextDouble() - 0.5) * 300, eye[1] + dy * k + (random.NextDouble() - 0.5) * 300, target[2] - random.NextDouble() * 150];
    }

    // ---- movement ----------------------------------------------------------------------------------
    // How a bot moves now. Gait: run or walk (silent); Stance: stand or crouch; Peek: how it peeks the
    // angle it holds (null: it just holds).
    public sealed record Movement(string Gait, string Stance, bool PreAim, string? Peek);
    public const double SilentNearCm = 2400, QuietNearCm = 1500;
    // `contact`: distance to the nearest enemy the bot knows about (null: none); `hurry`: a rush,
    // a rotation, a retake or a fetch; `holding`: holding a spot; `heardNotSeen`: holding towards a
    // sound it can't see; `coin`: the bot's own steady coin (0..1) for this round.
    public static Movement Move(string skill, double? contact, bool hurry, bool holding, bool heardNotSeen, double coin)
    {
        var preAim = skill != BotSkills.Easy;
        if (skill == BotSkills.Easy) return new("run", holding && coin < 0.15 ? "crouch" : "stand", false, null);
        var near = skill == BotSkills.Hard ? SilentNearCm : QuietNearCm;
        var walk = !hurry && contact is { } c && c < near && (skill == BotSkills.Hard || coin < 0.6);
        string? peek = null;
        if (holding && heardNotSeen) peek = skill == BotSkills.Hard ? (coin < 0.75 ? "jiggle" : "crouch") : coin < 0.4 ? "jiggle" : null;
        var crouch = holding && peek is null && contact is null && coin < (skill == BotSkills.Hard ? 0.3 : 0.15);
        return new(walk ? "walk" : "run", crouch ? "crouch" : "stand", preAim, peek);
    }

    // Fires to walk around (everyone) and smokes to route around (when the bot reroutes): circles
    // for the walker's nav ("avoid": x, y, z, radius, cost per grid step).
    public const double FireAvoidCost = 1000, SmokeAvoidCost = 1000;
    public static IReadOnlyList<double[]> Avoid(IEnumerable<CsGrenadeView>? grenades, CsGrenadeView? rerouteSmoke, string skill, long now)
    {
        var list = new List<double[]>();
        foreach (var g in grenades ?? [])
        {
            if (g.Pos is not { Length: 3 } p) continue;
            if (g.State == "fire" && g.Ends is { } end && end - now > 600)
                list.Add([p[0], p[1], p[2], Math.Round(Math.Max(g.Radius, GrenadeRules.FireRadiusCm) + 90), skill == BotSkills.Easy ? 300 : FireAvoidCost]);
        }
        if (rerouteSmoke?.Pos is { Length: 3 } s) list.Add([s[0], s[1], s[2], Math.Round(GrenadeRules.SmokeRadiusCm * 1.1), SmokeAvoidCost]);
        return list.Take(8).ToArray();
    }

    static double Dist2(double[] a, double[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]));
    static double Dist3(double[] a, double[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));
}

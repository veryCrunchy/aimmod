namespace AimMod.InGame.Multiplayer;

// Bot senses (BotBrain): what a bot can see and hear, honestly.
//  - Sight: its own game's line traces (bot-sight.tsv), then smoke (BotVision: a line through the
//    cloud as it grows and thins, both ways) and the host's flash on it (BotTactics.Blindness).
//  - Hearing (BotHearing): running footsteps (walking and crouching are silent, as in CS), real
//    gunfire from the shared shots (ShotLog: every shot anyone fired, hit or miss) and a decoy's
//    fake gunfire, within earshot by difficulty, shorter through a wall, and placed with an error
//    that grows with distance.
//  - Decoys (BotEars): a heard source of gunfire is judged by what a player could notice: a grenade
//    seen or heard landing right where the shooting starts, a "shooter" that never moves, the same
//    bursts over and over, no hits anywhere near it, a gun the other side can't have this round, or
//    every enemy known to be somewhere else. Easy bots fall for decoys; Normal bots see through them
//    after a while; Hard bots quickly, and tell the side.

// A sound a bot heard: a footstep or a shot, where it seemed to come from, when, how far, whether a
// wall was in the way, the gun class of a shot, and its key in the side's knowledge.
sealed record BotHeard(string Kind, double[] At, long T, double Distance, bool Occluded, string? Weapon, string Key);

static class BotHearing
{
    // A human's earshot: running footsteps (GunSounds.StepRangeCm) and gunfire; through a wall less.
    public const double StepRunCm = GunSounds.StepRangeCm, ShotCm = 4500, OccludedShare = 0.6;
    // How much of that a bot hears, and how far off its sense of where is (share of the distance).
    public static double Range(string skill) => skill switch { BotSkills.Easy => 0.7, BotSkills.Hard => 1.0, _ => 0.88 };
    public static double Error(string skill) => skill switch { BotSkills.Easy => 0.18, BotSkills.Hard => 0.05, _ => 0.1 };
    // How someone moves, as heard: still, crouched, walking (shift) or running. CS speeds by default
    // (the walk threshold sits between a shift-walk and a run); other arenas run slower.
    public static string Gait(double speed, bool crouched, bool cs)
    {
        if (speed < FootstepPlan.MinSpeed) return "still";
        if (crouched) return "crouch";
        return speed < (cs ? FootstepPlan.RunSpeed : BotBrain.RunSpeed) ? "walk" : "run";
    }
    // How far a bot hears footsteps of that gait: only a run makes noise.
    public static double StepRange(string gait, string skill) => gait == "run" ? StepRunCm * Range(skill) : 0;
    public static double ShotRange(bool occluded, string skill) => ShotCm * Range(skill) * (occluded ? OccludedShare : 1);
    // Where it seems to come from: off by up to Error x distance (more through a wall), at the source's height.
    public static double[] Place(double[] at, double distance, bool occluded, string skill, Random random)
    {
        var err = distance * Error(skill) * (occluded ? 1.6 : 1);
        var a = random.NextDouble() * Math.PI * 2; var r = err * Math.Sqrt(random.NextDouble());
        return [Math.Round(at[0] + Math.Cos(a) * r, 1), Math.Round(at[1] + Math.Sin(a) * r, 1), Math.Round(at[2], 1)];
    }
}

static class BotVision
{
    // A smoke cloud now: how much of its full size it has grown to (it billows out fast, then slows)
    // and how thick it still is (it thins over its last seconds).
    public static double Growth(CsGrenadeView s, long now)
    {
        if (s.State != "smoke" || s.Ends is not { } ends || now < s.At || now >= ends) return 0;
        var u = Math.Clamp((now - s.At) / (double)GrenadeRules.SmokeGrowMs, 0, 1);
        return 1 - (1 - u) * (1 - u);
    }
    public static double Density(CsGrenadeView s, long now) =>
        s.Ends is not { } ends || now >= ends ? 0 : Math.Clamp((ends - now) / (double)GrenadeRules.SmokeFadeMs, 0, 1);

    // How much smoke the line a-b passes through: the length of its chord through the cloud (an
    // ellipsoid the size of the cloud now, in cloud radii: 2 straight through the middle), times the
    // thickness. The same both ways.
    public static double Smoke(CsGrenadeView s, double[] a, double[] b, long now)
    {
        var grown = Growth(s, now);
        if (grown <= 0.02 || s.Pos is not { Length: 3 } at) return 0;
        var c = GrenadeRules.SmokeCentre(at);
        double Lx(double v, int i) => (v - c[i]) / (i == 2 ? GrenadeRules.SmokeHalfHeightCm : GrenadeRules.SmokeRadiusCm);
        double px = Lx(a[0], 0), py = Lx(a[1], 1), pz = Lx(a[2], 2);
        double dx = Lx(b[0], 0) - px, dy = Lx(b[1], 1) - py, dz = Lx(b[2], 2) - pz;
        var qa = dx * dx + dy * dy + dz * dz;
        if (qa < 1e-12) return 0;
        var qb = 2 * (px * dx + py * dy + pz * dz);
        var qc = px * px + py * py + pz * pz - grown * grown;
        var disc = qb * qb - 4 * qa * qc;
        if (disc <= 0) return 0;
        var root = Math.Sqrt(disc);
        var t1 = Math.Clamp((-qb - root) / (2 * qa), 0, 1); var t2 = Math.Clamp((-qb + root) / (2 * qa), 0, 1);
        return Math.Max(0, t2 - t1) * Math.Sqrt(qa) * Density(s, now);
    }
    // Hidden: half a cloud radius of smoke or more in the way (two bodies inside the same smoke see
    // each other only up close).
    public const double BlockChord = 0.5;
    public static bool SmokeBlocks(IEnumerable<CsGrenadeView>? grenades, double[] a, double[] b, long now) =>
        grenades is not null && grenades.Any(s => s.State == "smoke" && Smoke(s, a, b, now) >= BlockChord);
    // How deep in a smoke a point is (0 outside, 1 at its heart).
    public static double Inside(IEnumerable<CsGrenadeView>? grenades, double[] p, long now)
    {
        var most = 0.0;
        foreach (var s in grenades ?? [])
        {
            var grown = Growth(s, now);
            if (grown <= 0 || s.Pos is not { Length: 3 } at) continue;
            var c = GrenadeRules.SmokeCentre(at);
            var x = (p[0] - c[0]) / GrenadeRules.SmokeRadiusCm; var y = (p[1] - c[1]) / GrenadeRules.SmokeRadiusCm; var z = (p[2] - c[2]) / GrenadeRules.SmokeHalfHeightCm;
            var r = Math.Sqrt(x * x + y * y + z * z);
            most = Math.Max(most, Math.Clamp((grown - r) / 0.3, 0, 1) * Density(s, now));
        }
        return Math.Round(most, 2);
    }

    // Bots' sight after smoke: a target behind (or in) a cloud is not seen. A flash is the brain's
    // (BotTactics.Blindness), so it can still spray at a target its game traces as in the open.
    public static Dictionary<string, BotSight> Sight(CsView? cs, IReadOnlyDictionary<string, BotSight> sight, IReadOnlyList<BotPlayer> players, long now)
    {
        var result = new Dictionary<string, BotSight>(StringComparer.Ordinal);
        var smokes = cs?.Grenades?.Where(g => g.State == "smoke").ToArray() ?? [];
        foreach (var (member, s) in sight)
        {
            if (smokes.Length == 0) { result[member] = s; continue; }
            var eye = players.FirstOrDefault(p => p.Member == member) is { } self ? new[] { self.X, self.Y, self.Z } : new[] { s.X, s.Y, s.Z + BotBrain.EyeAboveCentre };
            var visible = s.Visible.Where(tag => players.FirstOrDefault(p => p.Tag == tag) is not { } target || !SmokeBlocks(smokes, eye, [target.X, target.Y, target.Z], now)).ToHashSet();
            result[member] = visible.Count == s.Visible.Count ? s : s with { Visible = visible };
        }
        return result;
    }

    // Whether a point is in a bot's view (within halfFov of where it looks, and not behind smoke).
    public static bool InView(double yaw, double[] eye, double[] p, double halfFov, IEnumerable<CsGrenadeView>? grenades, long now, double range)
    {
        var dx = p[0] - eye[0]; var dy = p[1] - eye[1];
        var d = Math.Sqrt(dx * dx + dy * dy);
        if (d > range) return false;
        if (d > 1 && Math.Abs(BotAim.Wrap(Math.Atan2(dy, dx) * 180 / Math.PI - yaw)) > halfFov) return false;
        return !SmokeBlocks(grenades, eye, p, now);
    }
}

// The side's ears for gunfire: shots grouped by where they come from (SoundSource), and each bot's
// verdict on whether a source is a decoy, from the cues a player could notice.
static class BotEars
{
    public const double JoinCm = 320, StillCm = 120, ImpactCm = 500, LandedCm = 300, ElsewhereCm = 1500;
    public const long JoinMs = 2500, ForgetMs = 8000;

    // The source a shot belongs to (one nearby that fired lately with the same class of gun), or a new one.
    public static SoundSource Source(TeamKnowledge team, string weapon, double[] at, long t, ref int nextId)
    {
        foreach (var s in team.Sounds)
            if (s.Weapon == weapon && t - s.LastT <= JoinMs && t >= s.FirstT - 200 && Dist2(s.Last, at) <= JoinCm) return s;
        team.Sounds.RemoveAll(s => t - s.LastT > ForgetMs);
        var created = new SoundSource { Id = ++nextId, Weapon = weapon };
        team.Sounds.Add(created);
        return created;
    }

    // What makes a source look like a decoy (reasons) and how strongly (score).
    public sealed record Context(IReadOnlyList<CombatEvent> Events, IReadOnlyList<BotPlayer> Players, int EnemiesAlive, IReadOnlySet<string>? Plausible, long Now);
    public static (int Score, IReadOnlyList<string> Cues) Cues(SoundSource s, TeamKnowledge team, Context c)
    {
        var cues = new List<string>(); var score = 0;
        if (s.Shots.Count == 0) return (0, cues);
        // A grenade the side saw or heard come to rest right where the shooting started.
        if (team.Grenades.Values.Any(g => Dist2(g.At, s.First) <= LandedCm && s.FirstT >= g.RestT - 500 && s.FirstT <= g.RestT + 6000)) { cues.Add("a grenade landed there"); score += 3; }
        // Never moves, over several bursts; the same bursts again and again.
        if (s.Bursts.Count >= 3 && s.Spread <= StillCm) { cues.Add("never moves"); score += 1; if (s.Bursts.Count >= 4) { cues.Add("same bursts"); score += 1; } }
        // Bursts and bursts, and nobody near it hurt anyone or got hurt.
        if (s.Bursts.Count >= 3 && s.LastT - s.FirstT >= 2000)
        {
            var impacts = c.Events.Any(e => e.Kind == "damage" && e.T >= s.FirstT - 200 && e.T <= s.LastT + 300 &&
                (c.Players.FirstOrDefault(p => p.Member == e.Attacker) is { } a && Dist2([a.X, a.Y], s.Last) <= ImpactCm || c.Players.FirstOrDefault(p => p.Member == e.Member) is { } v && Dist2([v.X, v.Y], s.Last) <= ImpactCm));
            if (!impacts) { cues.Add("no hits"); score += 1; }
        }
        // A gun the other side can't have this round (their economy).
        if (c.Plausible is { } ok && !ok.Contains(s.Weapon)) { cues.Add("wrong gun for their buy"); score += 1; }
        // Every enemy alive was just seen somewhere else.
        if (c.EnemiesAlive > 0)
        {
            var seen = team.Enemies.Where(kv => !kv.Value.Heard && c.Now - kv.Value.T <= 2000).ToList();
            if (seen.Count >= c.EnemiesAlive && seen.All(kv => Dist2(kv.Value.At, s.Last) >= ElsewhereCm)) { cues.Add("everyone is elsewhere"); score += 3; }
        }
        return (score, cues);
    }

    // How sure it must be before calling a decoy: never (easy), a few cues (normal), two (hard).
    public static int Threshold(string skill) => skill switch { BotSkills.Easy => int.MaxValue, BotSkills.Hard => 2, _ => 3 };
    // Whether a bot takes a source for a decoy: its own verdict, or a call from the side (not easy bots).
    public static bool Rejects(SoundSource s, string bot, string skill) =>
        s.Verdicts.TryGetValue(bot, out var v) && v.StartsWith("decoy", StringComparison.Ordinal) || skill != BotSkills.Easy && s.CalledBy is not null;
    public static bool Judge(SoundSource s, string bot, string skill, int score, IReadOnlyList<string> cues)
    {
        var decoy = score >= Threshold(skill);
        var verdict = decoy ? "decoy (" + string.Join(", ", cues) + ")" : "believed";
        var changed = !s.Verdicts.TryGetValue(bot, out var had) || had != verdict;
        s.Verdicts[bot] = verdict;
        if (decoy && skill != BotSkills.Easy) s.CalledBy ??= bot;
        return changed;
    }

    // The guns the other side has this round (CS: what its living players bought, as a side reads the
    // other's economy), or null when unknown.
    public static IReadOnlySet<string>? Plausible(CsView? cs, string? enemySide)
    {
        if (cs is null || enemySide is null) return null;
        var set = new HashSet<string> { "knife" };
        foreach (var p in cs.Players.Where(p => p.Side == enemySide && p.Alive))
        {
            if (GunSounds.ClassOf(p.Primary) is { } a) set.Add(a);
            set.Add(GunSounds.ClassOf(p.Secondary ?? CsRules.DefaultPistol(enemySide).Id) ?? "pistol");
        }
        return set;
    }

    static double Dist2(double[] a, double[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]));
}

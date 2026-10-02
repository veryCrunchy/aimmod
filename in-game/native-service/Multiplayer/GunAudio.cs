namespace AimMod.InGame.Multiplayer;

// Other players' gunfire and footsteps (game-modes.md 6.6.3). KovaaK's plays only the local
// player's own gun, so without these a bot or a remote player fires and runs in silence. Every
// sound is AimMod's own, synthesised like the bomb's (BombSounds: sine tones, noise, envelopes;
// nothing recorded or taken from a game); `--write-bomb-sounds <folder>` writes these too.
//  - A shot by weapon class: pistol, SMG, rifle, sniper (AWP), shotgun, and the knife's swish.
//  - The same shot muffled (low-passed, quieter) when a wall is known to stand between you.
//  - Footsteps: a few short scuffs, played at a running player's stride.
static class GunSounds
{
    const int Rate = BombSounds.Rate;
    public static readonly string[] Classes = ["pistol", "smg", "rifle", "sniper", "shotgun", "knife"];
    public const int StepVariants = 4;

    // A shot: a sharp crack, a low thump that drops in pitch, and a short noisy tail.
    public static float[] Shot(string weaponClass)
    {
        var (len, crack, crackDecay, thump, thumpDecay, tailCut, tailDecay, tailGain, peak, seed) = weaponClass switch
        {
            "smg" => (0.3, 8200.0, 120.0, 165.0, 34.0, 2400.0, 13.0, 0.35, 0.55, 211u),
            "rifle" => (0.48, 6400.0, 80.0, 112.0, 24.0, 1900.0, 7.5, 0.5, 0.7, 223u),
            "sniper" => (0.95, 5200.0, 48.0, 74.0, 13.0, 1400.0, 3.6, 0.65, 0.85, 227u),
            "shotgun" => (0.62, 3900.0, 42.0, 88.0, 16.0, 1500.0, 5.0, 0.6, 0.8, 229u),
            _ => (0.36, 7200.0, 95.0, 150.0, 30.0, 2200.0, 10.0, 0.4, 0.6, 233u), // pistol
        };
        var s = BombSounds.Buffer(len); double phase = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var t = i / (double)Rate;
            phase += 2 * Math.PI * thump * (1 + 1.6 * Math.Exp(-55 * t)) / Rate;
            s[i] = (float)(0.85 * Math.Sin(phase) * Math.Exp(-thumpDecay * t) * Math.Min(1, t / 0.0008));
        }
        BombSounds.Burst(s, 0, Math.Min(len, 0.12), 1.1, crack, seed, crackDecay);
        BombSounds.Burst(s, 0.004, len - 0.004, tailGain, tailCut, seed + 1, tailDecay);
        if (weaponClass == "sniper") BombSounds.Burst(s, 0, 0.03, 0.6, 9000, seed + 2, 220); // the supersonic snap
        return BombSounds.Normalize(s, peak);
    }

    // Through a wall: the highs gone (two one-pole low-passes at 650 Hz) and quieter.
    public static float[] Muffle(float[] dry, double peak)
    {
        var s = (float[])dry.Clone();
        var a = 1 - Math.Exp(-2 * Math.PI * 650 / Rate);
        for (var pass = 0; pass < 2; pass++) { double low = 0; for (var i = 0; i < s.Length; i++) { low += a * (s[i] - low); s[i] = (float)low; } }
        return BombSounds.Normalize(s, peak);
    }

    // A footstep: a soft heel thud and a gritty scuff, a little different each time.
    public static float[] Step(int variant)
    {
        var s = BombSounds.Buffer(0.14); double phase = 0;
        var freq = 85 + 12 * variant;
        for (var i = 0; i < s.Length; i++)
        {
            var t = i / (double)Rate;
            phase += 2 * Math.PI * freq * (1 + Math.Exp(-40 * t)) / Rate;
            s[i] = (float)(0.7 * Math.Sin(phase) * Math.Exp(-38 * t) * Math.Min(1, t / 0.002));
        }
        BombSounds.Burst(s, 0.006, 0.09, 0.55, 1700 + 250 * variant, 241u + (uint)variant, 55);
        BombSounds.Burst(s, 0.03, 0.05, 0.25, 4200, 251u + (uint)variant, 90);
        return BombSounds.Normalize(s, 0.38);
    }

    public static readonly IReadOnlyDictionary<string, Func<float[]>> All = BuildAll();
    static Dictionary<string, Func<float[]>> BuildAll()
    {
        var all = new Dictionary<string, Func<float[]>>();
        foreach (var c in Classes)
        {
            var cls = c;
            Func<float[]> dry = cls == "knife" ? () => BombSounds.Swish(0.2, 900, 3800, 263, 0.45) : () => Shot(cls);
            all["gun-" + cls] = dry;
            all["gun-" + cls + "-muffled"] = () => Muffle(dry(), cls == "knife" ? 0.2 : 0.45);
        }
        for (var v = 0; v < StepVariants; v++) { var k = v; all["step-" + k] = () => Step(k); }
        return all;
    }

    // The sound class of a weapon: a CS item's class (CsRules), the combat modes' rifle and railgun.
    public static string? ClassOf(string? weaponId) => weaponId switch
    {
        null => null,
        "railgun" => "sniper",
        "rifle" => "rifle",
        _ => CsRules.FindAny(weaponId) switch
        {
            { Class: "pistol" } => "pistol", { Class: "smg" } => "smg", { Class: "rifle" } => "rifle", { Class: "sniper" } => "sniper",
            { Class: "heavy" or "shotgun" } => "shotgun", { Class: "knife" } => "knife", _ => null,
        },
    };

    // Distance falls off slower for gunfire (heard across a map) than for footsteps (the bomb's
    // curve at half the distance), and footsteps fade out by StepRangeCm.
    public const double ShotDistanceScale = 0.5, StepRangeCm = 2200, StepFadeCm = 1500;
}

// One shot someone fired (host clock): who, what (CsWeapon id, or "rifle"/"railgun" in the
// combat modes), from where (their eye, world cm), and the players a wall is known to hide the
// shooter from (a bot's own line traces; empty when nothing is known).
sealed record GunShot(long Id, string Member, long T, string Weapon, double[] From, IReadOnlyList<string>? Hidden = null);

// The host's record of the match's shots: the shooter's own stream says when each shot fired (the
// shot feed of their game, or for a bot the host's own decision), and the host passes them on to
// everyone. A shooter can't fire faster than its weapon allows (MinShare of the interval), and
// times must be recent.
sealed class ShotLog
{
    public const int Keep = 96, MaxPerMessage = 24;
    public const long MaxAgeMs = 1500, MaxAheadMs = 500;
    public const double MinShare = 0.6;
    readonly List<GunShot> shots = [];
    readonly Dictionary<string, long> lastAt = new(StringComparer.Ordinal);
    string? key; long nextId;
    public IReadOnlyList<GunShot> Recent => shots;
    public long LatestId => shots.Count > 0 ? shots[^1].Id : 0;
    public IEnumerable<GunShot> Since(long id) => shots.Where(s => s.Id > id);

    public void Reset(string matchKey) { if (key == matchKey) return; key = matchKey; shots.Clear(); lastAt.Clear(); }
    public string? Key => key;

    // Host: a shot to record, or null when refused (too old, too far ahead, faster than the weapon).
    public GunShot? Record(string member, long t, string weapon, double intervalSeconds, double[] from, IReadOnlyList<string>? hidden, long now)
    {
        if (t < now - MaxAgeMs || t > now + MaxAheadMs || from is not { Length: 3 } || from.Any(v => !double.IsFinite(v) || Math.Abs(v) > 1e7)) return null;
        if (lastAt.TryGetValue(member, out var last) && t - last < intervalSeconds * 1000 * MinShare) return null;
        lastAt[member] = t;
        var shot = new GunShot(++nextId, member, t, weapon, [Math.Round(from[0], 1), Math.Round(from[1], 1), Math.Round(from[2], 1)], hidden is { Count: > 0 } ? hidden.Take(16).ToArray() : null);
        Add(shot);
        return shot;
    }

    // Client: shots the host passed on (already checked there); repeats are ignored.
    public void Receive(IEnumerable<GunShot> received)
    {
        foreach (var s in received.Take(MaxPerMessage))
            if (s.Id > 0 && shots.All(x => x.Id != s.Id) && s.From is { Length: 3 } && s.From.All(double.IsFinite) && s.Member.Length is > 0 and <= 64 && s.Weapon.Length <= 32) Add(s);
        shots.Sort((a, b) => a.Id.CompareTo(b.Id));
    }
    void Add(GunShot s) { shots.Add(s); if (shots.Count > Keep) shots.RemoveRange(0, shots.Count - Keep); }
}

// A sound to play at a moment (local unix ms), positional like RoundCue.
sealed record TimedCue(RoundCue Cue, long At);

// What other players' shots sound like from one player's view. Each shot plays once, at its own
// time (so a burst keeps its fire rate however the shots arrive): the shot's host time on this
// machine's clock plus a small per-shooter delay that absorbs how late that shooter's shots arrive
// (their network path); a shot later than LateMs is dropped rather than played out of time.
sealed class GunSoundPlan
{
    public const long LateMs = 600, MaxDelayMs = 350;
    readonly HashSet<long> played = [];
    readonly Dictionary<string, double> delay = new(StringComparer.Ordinal);
    string? key;

    public IReadOnlyList<TimedCue> Update(string matchKey, IEnumerable<GunShot> shots, string self, long now, long hostOffset)
    {
        var cues = new List<TimedCue>();
        if (key != matchKey) { key = matchKey; played.Clear(); delay.Clear(); }
        var fresh = shots.Where(s => s.Member != self && !played.Contains(s.Id)).ToList();
        // Each shooter's lateness, once per update (so the shots that came together keep their spacing):
        // up at once, down slowly.
        foreach (var g in fresh.GroupBy(s => s.Member))
        {
            var late = g.Max(s => now - (s.T - hostOffset));
            if (late > LateMs * 2) continue; // a stale batch says nothing about the path
            var d = delay.TryGetValue(g.Key, out var have) ? have : 0;
            delay[g.Key] = late > d ? Math.Min(MaxDelayMs, late) : d * 0.95 + Math.Max(0, late) * 0.05;
        }
        foreach (var s in fresh)
        {
            played.Add(s.Id);
            var local = s.T - hostOffset;
            var d = delay.GetValueOrDefault(s.Member);
            if (now - local > LateMs) continue;
            if (GunSounds.ClassOf(s.Weapon) is not { } cls) continue;
            var muffled = s.Hidden?.Contains(self) == true;
            var gain = cls == "knife" ? 0.7 : 0.9;
            cues.Add(new TimedCue(new RoundCue("gun-" + cls + (muffled ? "-muffled" : ""), s.From, muffled ? gain * 0.8 : gain), Math.Max(now, local + (long)d)));
        }
        if (played.Count > 4096) played.Clear();
        return cues;
    }
    public double DelayFor(string member) => delay.GetValueOrDefault(member);
}

// Footsteps of the other players, from where this machine's game draws them (host clock): a step
// every stride while they move on the ground, full at a run, quieter walking or crouched (CS walks
// and crouches quietly), none past StepRangeCm. Pure; BombAudio plays the cues.
sealed class FootstepPlan
{
    public const double MinSpeed = 140, RunSpeed = 650, StrideCm = 330, JumpSpeed = 260;
    public const double WalkGain = 0.3, CrouchGain = 0.2, MateGain = 0.6;
    sealed class Body { public long T; public double X, Y, Z, Half, MaxHalf, Speed, Climb; public long NextStep; public int Variant; public long SpeedT; public double SpeedX, SpeedY, SpeedZ; }
    readonly Dictionary<string, Body> bodies = new(StringComparer.Ordinal);
    string? key;

    // avatars: member, host time, capsule centre and half height; mate: a teammate (quieter).
    public IReadOnlyList<TimedCue> Update(string matchKey, IEnumerable<(string Member, long T, double X, double Y, double Z, double Half, bool Alive, bool Mate)> avatars,
        (double X, double Y, double Yaw)? listener, long hostNow, long hostOffset)
    {
        var cues = new List<TimedCue>();
        if (key != matchKey) { key = matchKey; bodies.Clear(); }
        foreach (var a in avatars)
        {
            if (!bodies.TryGetValue(a.Member, out var b))
            {
                bodies[a.Member] = new Body { T = a.T, X = a.X, Y = a.Y, Z = a.Z, Half = a.Half, MaxHalf = a.Half, SpeedT = a.T, SpeedX = a.X, SpeedY = a.Y, SpeedZ = a.Z, Variant = a.Member.Sum(c => c) % GunSounds.StepVariants };
                continue;
            }
            if (a.T < b.T) continue;
            b.T = a.T; b.X = a.X; b.Y = a.Y; b.Z = a.Z; b.Half = a.Half; b.MaxHalf = Math.Max(b.MaxHalf, a.Half);
            // Speed over at least 150 ms of drawn positions (frames arrive unevenly); not seen moving for a second: still.
            var dt = (a.T - b.SpeedT) / 1000.0;
            if (dt >= 0.15)
            {
                b.Speed = dt > 1 ? 0 : Math.Sqrt((a.X - b.SpeedX) * (a.X - b.SpeedX) + (a.Y - b.SpeedY) * (a.Y - b.SpeedY)) / dt;
                b.Climb = dt > 1 ? 0 : Math.Abs(a.Z - b.SpeedZ) / dt;
                b.SpeedT = a.T; b.SpeedX = a.X; b.SpeedY = a.Y; b.SpeedZ = a.Z;
            }
            var speed = b.Speed;
            if (!a.Alive || speed < MinSpeed || b.Climb > JumpSpeed || hostNow - a.T > 500) { b.NextStep = 0; continue; }
            var interval = (long)(Math.Clamp(StrideCm / speed, 0.26, 0.7) * 1000);
            if (b.NextStep == 0) { b.NextStep = hostNow + interval / 2; continue; }
            if (hostNow < b.NextStep) continue;
            b.NextStep = Math.Max(b.NextStep + interval, hostNow + interval / 2);
            var crouched = a.Half < b.MaxHalf * 0.82;
            var gain = (crouched ? CrouchGain : speed < RunSpeed ? WalkGain : 1) * (a.Mate ? MateGain : 1);
            if (listener is { } l)
            {
                var d = Math.Sqrt((a.X - l.X) * (a.X - l.X) + (a.Y - l.Y) * (a.Y - l.Y));
                if (d > GunSounds.StepRangeCm) continue;
                if (d > GunSounds.StepFadeCm) gain *= 1 - (d - GunSounds.StepFadeCm) / (GunSounds.StepRangeCm - GunSounds.StepFadeCm);
            }
            b.Variant = (b.Variant + 1) % GunSounds.StepVariants;
            cues.Add(new TimedCue(new RoundCue("step-" + b.Variant, [a.X, a.Y, a.Z - a.Half], gain), hostNow - hostOffset));
        }
        return cues;
    }
}
